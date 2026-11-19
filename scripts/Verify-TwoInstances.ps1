param([string]$OutputDirectory, [switch]$UseBroadcast, [switch]$WithLanUpdate, [string]$SecondExecutable, [switch]$ExpectLegacyPeer)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('.codex-temp\two-instances-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
    throw 'Thư mục kết quả đã có dữ liệu. Hãy chọn thư mục mới để không ghi đè cluster/database cũ.'
}
$exe = Join-Path $repo 'src\LanChat.WinForms\bin\Debug\net48\LanChat.WinForms.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Debug trước khi kiểm tra.' }
if ($SecondExecutable -and -not (Test-Path -LiteralPath $SecondExecutable)) { throw 'Không tìm thấy EXE kiểm tra tương thích.' }
if ($SecondExecutable -and $WithLanUpdate) { throw 'Kiểm tra tương thích bản cũ và kiểm tra chia sẻ LAN phải chạy riêng.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if ($WithLanUpdate) {
    # Metadata-valid fixture only: compilation and reading are allowed; never execute it.
    $fixtureSource = Join-Path $OutputDirectory 'update-fixture.cs'
    $fixtureExe = Join-Path $OutputDirectory 'LanChat-test-setup.exe'
    @'
using System.Reflection;
[assembly: AssemblyProduct("LanChat            ")]
[assembly: AssemblyTitle("LanChat Setup      ")]
[assembly: AssemblyVersion("9.12.3.0")]
[assembly: AssemblyFileVersion("9.12.3.0")]
[assembly: AssemblyInformationalVersion("9.12.3")]
class Fixture { static void Main() { throw new System.InvalidOperationException("Do not execute test fixtures."); } }
'@ | Set-Content -LiteralPath $fixtureSource -Encoding utf8
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:winexe "/out:$fixtureExe" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw 'Không tạo được bộ cài giả lập.' }
}
Add-Type -AssemblyName System.Security
$secret = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($secret)
$protected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($secret, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
$cluster = [Guid]::NewGuid().ToString()
$bootstrap = [Guid]::NewGuid().ToString()
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new("CN=LanChat verification $cluster", $key, [Security.Cryptography.HashAlgorithmName]::SHA256)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(1))
$pfx = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
$protectedPfx = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($pfx, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
$certificate.Dispose(); $key.Dispose()
function Get-FreePort([bool]$Udp) {
    if ($Udp) { $socket = [Net.Sockets.UdpClient]::new(0); $port = $socket.Client.LocalEndPoint.Port; $socket.Dispose(); return $port }
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop(); return $port
}
$tcpA = Get-FreePort $false; $tcpB = Get-FreePort $false
$udpA = Get-FreePort $true; $udpB = Get-FreePort $true
while ($tcpA -eq $tcpB) { $tcpB = Get-FreePort $false }
while ($udpA -eq $udpB) { $udpB = Get-FreePort $true }
if ($UseBroadcast) { $udpB = $udpA }
$processes = @()
foreach ($entry in @(@{Name='A'; Tcp=$tcpA; Udp=$udpA; Target=$udpB}, @{Name='B'; Tcp=$tcpB; Udp=$udpB; Target=$udpA})) {
    $data = Join-Path $OutputDirectory $entry.Name
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    @{ClusterId=$cluster; ProtectedSecret=$protected; ServicePort=$entry.Tcp; DiscoveryPort=$entry.Udp; BootstrapNodeId=$bootstrap; ProtectedTlsPfx=$protectedPfx; InitialNodeId=$(if ($entry.Name -eq 'A') { $bootstrap } else { $null })} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'cluster.json') -Encoding utf8
    $report = Join-Path $OutputDirectory ($entry.Name + '.json')
    $arguments = @('--data-dir', ('"' + $data + '"'), '--port', $entry.Tcp, '--discovery-port', $entry.Udp,
        '--display-name', ('"Máy ' + $entry.Name + '"'), '--verify-ui', ('"' + $report + '"'))
    if (-not $UseBroadcast) { $arguments += @('--discovery-target', ('127.0.0.1:' + $entry.Target)) }
    if ($WithLanUpdate) { $arguments += @('--verify-update-package', ('"' + $fixtureExe + '"')) }
    if ($ExpectLegacyPeer -and $entry.Name -eq 'A') { $arguments += '--verify-legacy-peer' }
    $launch = if ($entry.Name -eq 'B' -and $SecondExecutable) { $SecondExecutable } else { $exe }
    $processes += Start-Process -FilePath $launch -ArgumentList $arguments -PassThru -WindowStyle Hidden
}
foreach ($process in $processes) {
    if (-not $process.WaitForExit(120000)) { throw "Instance $($process.Id) chưa thoát; kiểm tra cửa sổ lỗi. Script không tự tắt tiến trình." }
    if ($process.ExitCode -ne 0) { throw "Instance thất bại: exit $($process.ExitCode). Xem report trong $OutputDirectory" }
}
$reports = @('A', 'B') | ForEach-Object { Get-Content -LiteralPath (Join-Path $OutputDirectory ($_ + '.json')) -Raw | ConvertFrom-Json }
if ($reports.Where({-not $_.Success}).Count -gt 0) { throw 'Kiểm tra giao diện thất bại.' }
if ($reports[0].NodeId -eq $reports[1].NodeId -or $reports[0].UserId -eq $reports[1].UserId) { throw 'Hai instance phải có định danh khác nhau.' }
$idsA = ($reports[0].Messages.Id | Sort-Object) -join ','
$idsB = ($reports[1].Messages.Id | Sort-Object) -join ','
if ($idsA -ne $idsB) { throw 'Hai instance chưa có cùng tin nhắn.' }
if ($reports[0].GroupId -ne $reports[1].GroupId -or $reports.Where({$_.VerifiedFiles -ne 6 -or $_.VerifiedOpenRequests -ne 3 -or $_.VerifiedGalleries -ne 4 -or $_.InlineImagesLoaded -lt 2 -or $_.VerifiedSourceChanges -ne 3}).Count -gt 0) { throw 'Kiểm tra nhóm/tệp/ảnh hoặc sửa/xóa nguồn sau gửi chưa đạt.' }
if ($reports.Where({-not $_.VerifiedReplies -or -not $_.VerifiedClipboard}).Count -gt 0) { throw 'Kiểm tra điều hướng trích dẫn hoặc gửi ảnh từ bảng nhớ tạm chưa đạt.' }
if ($reports.Where({-not $_.VerifiedGroupMembers}).Count -gt 0) { throw 'Kiểm tra thêm và bớt thành viên từ thông tin hội thoại chưa đạt.' }
if ($reports.Where({$_.VerifiedSavedAttachments -ne 2}).Count -gt 0) { throw 'Kiểm tra lưu và mở lại bản chỉnh sửa của tệp/ảnh chưa đạt.' }
if ($WithLanUpdate -and $reports.Where({-not $_.VerifiedLanUpdate}).Count -gt 0) { throw 'Kiểm tra cập nhật LAN giữa hai tiến trình chưa đạt.' }
if ($ExpectLegacyPeer -and -not $reports[0].VerifiedLegacyUpdateUnsupported) { throw 'Chưa kiểm tra thông báo máy cũ không hỗ trợ cập nhật LAN.' }
Write-Output "PASS: hai tiến trình WinForms, chat trực tiếp và nhóm, tệp và ảnh, trích dẫn, ảnh từ bảng nhớ tạm, quản lý thành viên từ hội thoại. Kết quả: $OutputDirectory"

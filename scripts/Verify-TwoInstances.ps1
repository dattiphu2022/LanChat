param([string]$OutputDirectory, [switch]$UseBroadcast)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('.codex-temp\two-instances-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
    throw 'Thư mục kết quả đã có dữ liệu. Hãy chọn thư mục mới để không ghi đè cluster/database cũ.'
}
$exe = Join-Path $repo 'src\LanChat.WinForms\bin\Debug\net9.0-windows\LanChat.WinForms.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Debug trước khi kiểm tra.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
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
    $processes += Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
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
if ($reports[0].GroupId -ne $reports[1].GroupId -or $reports.Where({$_.VerifiedFiles -ne 6 -or $_.VerifiedOpenRequests -ne 3 -or $_.VerifiedGalleries -ne 4 -or $_.InlineImagesLoaded -lt 2}).Count -gt 0) { throw 'Kiểm tra nhóm/tệp/ảnh chưa đạt.' }
Write-Output "PASS: hai tiến trình WinForms, UDP discovery, TLS, sửa/trả lời/cảm xúc, kéo thả nhiều tệp, tải SHA256, ảnh inline, album/chú thích/zoom/chuyển nhanh, chat nhóm. Kết quả: $OutputDirectory"

param([string]$OutputDirectory, [switch]$NoRestore, [switch]$PublicRelease, [switch]$DevelopmentSign)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$buildId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $OutputDirectory ('LanChat-Win11-x64-' + $buildId)
$zip = Join-Path $OutputDirectory 'LanChat-Win11-x64.zip'

$publishArgs = @('publish', (Join-Path $repo 'src/LanChat.WinForms/LanChat.WinForms.csproj'), '-m:1', '-c', 'Release', '-f', 'net48', '-o', $stage)
if ($NoRestore) { $publishArgs += '--no-restore' }
if ($DevelopmentSign) { $publishArgs += '-p:LanChatDevelopmentSigning=true' }
if ($PublicRelease) {
    $contracts = Get-Content -LiteralPath (Join-Path $repo 'src/LanChat.Application/Contracts.cs') -Raw
    $versionMatch = [regex]::Match($contracts, 'const\s+string\s+CurrentApplicationVersion\s*=\s*"(?<version>\d+\.\d+\.\d+)";')
    if (-not $versionMatch.Success) { throw 'CurrentApplicationVersion phải có dạng X.Y.Z.' }
    $appVersion = $versionMatch.Groups['version'].Value
    $publicBuild = Join-Path $OutputDirectory ('public-build-' + $buildId)
    $publishArgs += @('-p:BuildInstaller=false', '-p:LanChatPublicRelease=true', "-p:LanChatPublicBuildDirectory=$publicBuild", "-p:Version=$appVersion")
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish thất bại ($LASTEXITCODE)." }
foreach ($name in @('LanChat.WinForms.exe', 'LanChat.WinForms.exe.config', 'Microsoft.Data.Sqlite.dll', 'e_sqlite3.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $name) -PathType Leaf)) { throw "Thiếu tệp $name." }
}
if ($PublicRelease) {
    $publishedExe = Join-Path $stage 'LanChat.WinForms.exe'
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($publishedExe))
    $provisioningFiles = Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object Name -in @('default-network.json', 'DefaultNetwork.local.json')
    if ($assembly.GetManifestResourceNames() -contains 'LanChat.DefaultNetwork' -or $provisioningFiles) {
        throw 'Bản publish công khai còn chứa tài nguyên hoặc tệp cấp mã mạng.'
    }
    $compiler = (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source
    if (-not $compiler) {
        $compiler = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $compiler) { throw 'Không tìm thấy ISCC.exe của Inno Setup 6.' }
    $setupScript = Join-Path $repo 'installer/LanChat.iss'
    $installerDirectory = Join-Path $OutputDirectory 'installer'
    $setupName = 'LanChat-Setup-Windows-x64'
    $signArguments = @()
    if ($DevelopmentSign) {
        $signer = Join-Path $repo 'tools/LanChat.Signing/bin/Release/net48/LanChat.Signing.exe'
        $local = Join-Path $repo 'CodeSigning.local.props'
        if (-not (Test-Path -LiteralPath $local)) { throw 'Configure and back up the development certificate first.' }
        [xml]$signConfiguration = Get-Content -LiteralPath $local -Raw
        $thumbprint = [string]$signConfiguration.Project.PropertyGroup.LanChatSigningThumbprint
        if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Missing development signing thumbprint.' }
        $command = '$q' + $signer + '$q sign --thumbprint ' + $thumbprint + ' $f'
        $signArguments = @('/DLanChatDevelopmentSigning', ('/SLanChatDevelopment=' + $command))
    }
    & $compiler "/DPublishDir=$stage" "/DAppVersion=$appVersion" "/DSetupOutputName=$setupName" "/O$installerDirectory" @signArguments $setupScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup thất bại ($LASTEXITCODE)." }
    if ($DevelopmentSign) {
        & $signer verify --thumbprint $thumbprint (Join-Path $installerDirectory "$setupName.exe")
        if ($LASTEXITCODE -ne 0) { throw 'Installer signature verification failed; do not publish.' }
    }
    Write-Output (Join-Path $installerDirectory "$setupName.exe")
    return
}
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $stage
$files = Get-ChildItem -LiteralPath $stage -File | Where-Object Extension -ne '.pdb'
Compress-Archive -LiteralPath $files.FullName -DestinationPath $zip -CompressionLevel Optimal -Force
Write-Output $zip

param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo 'artifacts\inno-payload'
$project = Join-Path $repo 'src\LanChat.WinForms\LanChat.WinForms.csproj'
$publishArgs = @('publish', $project, '-c', 'Release', '-f', 'net48', '-m:1', '-o', $stage)
if ($NoRestore) { $publishArgs += '--no-restore' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
Write-Output (Join-Path $repo 'artifacts/installer/LanChat-Setup-Windows-x64.exe')

param([string]$OutputDirectory, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $OutputDirectory ('LanChat-Win11-x64-' + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $OutputDirectory 'LanChat-Win11-x64.zip'

$publishArgs = @('publish', (Join-Path $repo 'src/LanChat.WinForms/LanChat.WinForms.csproj'), '-m:1', '-c', 'Release', '-f', 'net48', '-o', $stage)
if ($NoRestore) { $publishArgs += '--no-restore' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish thất bại ($LASTEXITCODE)." }
foreach ($name in @('LanChat.WinForms.exe', 'LanChat.WinForms.exe.config', 'Microsoft.Data.Sqlite.dll', 'e_sqlite3.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $name) -PathType Leaf)) { throw "Thiếu tệp $name." }
}
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $stage
$files = Get-ChildItem -LiteralPath $stage -File | Where-Object Extension -ne '.pdb'
Compress-Archive -LiteralPath $files.FullName -DestinationPath $zip -CompressionLevel Optimal -Force
Write-Output $zip

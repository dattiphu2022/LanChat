#Requires -Version 7.0
using namespace System.Security.Cryptography
using namespace System.Security.Cryptography.X509Certificates

param(
    [string]$CreatorMachine = [Environment]::MachineName,
    [switch]$Rotate
)

$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot '../src/LanChat.WinForms/Assets/DefaultNetwork.local.json'
if (Test-Path -LiteralPath $destination) {
    if (!$Rotate) { throw 'A default invitation already exists. Use -Rotate to replace it for the next build.' }
}
if ([string]::IsNullOrWhiteSpace($CreatorMachine)) { throw 'Specify the computer name of the initial node.' }

$cluster = [Guid]::NewGuid()
$secret = [byte[]]::new(32)
[RandomNumberGenerator]::Fill($secret)
$key = [ECDsa]::Create([ECCurve+NamedCurves]::nistP256)
try {
    $request = [CertificateRequest]::new("CN=LanChat-$($cluster.ToString('N'))", $key, [HashAlgorithmName]::SHA256)
    $request.CertificateExtensions.Add([X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add([X509KeyUsageExtension]::new([X509KeyUsageFlags]::DigitalSignature, $true))
    $now = [DateTimeOffset]::UtcNow
    $certificate = $request.CreateSelfSigned($now.AddDays(-1), $now.AddYears(10))
    try {
        $invite = @{
            ClusterId = $cluster
            Secret = [Convert]::ToBase64String($secret)
            Version = 2
            BootstrapNodeId = [Guid]::NewGuid()
            TlsCertificatePfx = [Convert]::ToBase64String($certificate.Export([X509ContentType]::Pfx))
        } | ConvertTo-Json -Compress
        $provision = @{
            invitation = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($invite))
            creatorMachine = $CreatorMachine.Trim()
        } | ConvertTo-Json -Compress
        Set-Content -LiteralPath $destination -Value $provision -Encoding utf8NoBOM
    }
    finally { $certificate.Dispose() }
}
finally { $key.Dispose() }

Write-Output 'Default invitation prepared for the next build.'

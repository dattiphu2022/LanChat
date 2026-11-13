#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)]
    [string]$AppPath
)

$ErrorActionPreference = 'Stop'
$exe = Get-Item -LiteralPath $AppPath
if ($exe.PSIsContainer -or $exe.Name -ne 'LanChat.WinForms.exe') {
    throw 'Specify the installed LanChat.WinForms.exe.'
}

foreach ($protocol in @('UDP', 'TCP')) {
    $name = "LanChat LAN ($protocol)"
    $port = if ($protocol -eq 'UDP') { 54545 } else { 'Any' }
    $existing = Get-NetFirewallRule -DisplayName $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue |
        Where-Object { ($_ | Get-NetFirewallApplicationFilter).Program -eq $exe.FullName }
    if (!$existing) {
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Enabled True `
            -Profile Domain,Private -RemoteAddress LocalSubnet -Program $exe.FullName `
            -Protocol $protocol -LocalPort $port | Out-Null
    }
}

# Adds an inbound Windows Firewall rule allowing TCP 8888 for Griffin Stream Server.
# Must be run as Administrator (the installer launches it with a UAC prompt).
$ErrorActionPreference = 'Stop'
$ruleName = 'Griffin Stream Server (TCP 8888)'
try {
    if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
        Write-Host "Firewall rule already exists."
    } else {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort 8888 -Profile Any | Out-Null
        Write-Host "Firewall rule added for TCP 8888."
    }
} catch {
    Write-Warning "Failed to add firewall rule: $($_.Exception.Message)"
    exit 1
}

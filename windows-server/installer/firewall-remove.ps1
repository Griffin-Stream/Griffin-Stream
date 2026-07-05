# Removes the inbound Windows Firewall rule for Griffin Stream Server.
# Must be run as Administrator (the uninstaller launches it with a UAC prompt).
$ErrorActionPreference = 'SilentlyContinue'
$ruleName = 'Griffin Stream Server (TCP 8888)'
if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName $ruleName
    Write-Host "Firewall rule removed."
} else {
    Write-Host "No firewall rule to remove."
}

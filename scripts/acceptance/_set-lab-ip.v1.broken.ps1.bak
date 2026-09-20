# LanRemote M2.1 - two-machine acceptance: add a private lab IPv4 address
#
# WHY THIS IS NEEDED
#   LanRemote only uses RFC1918 private IPv4 (10/8, 172.16-172.31, 192.168/16).
#   172.100.x.x looks private but is NOT: the 172 block only covers 172.16-172.31.
#   Both test machines sit on 172.100.166.x, so discovery correctly refuses them
#   and reports "no eligible private IPv4 NIC". This script ADDS a private
#   address ALONGSIDE the existing one - it never removes the original.
#
# USAGE (run in an ADMIN PowerShell on EACH machine, with a different -Role)
#   Machine A:  powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Role A
#   Machine B:  powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Role B
#
#   Machine A gets 192.168.1.10/24, machine B gets 192.168.1.20/24.
#
# TO UNDO (any time)
#   powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Undo
#
# This file is intentionally stored as UTF-8 WITH BOM so that Windows
# PowerShell 5.1 decodes the Chinese strings correctly.

param(
    [ValidateSet('A', 'B')]
    [string]$Role,

    [string]$InterfaceAlias = '',

    [switch]$Undo
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Pick the adapter that carries the existing 172.100.x.x / any IPv4 address.
# ---------------------------------------------------------------------------
function Resolve-Adapter {
    param([string]$Requested)

    if ($Requested -ne '') { return $Requested }

    $candidates = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.PrefixOrigin -ne 'WellKnown' }

    if (-not $candidates) {
        throw "No IPv4 adapter found. Pass -InterfaceAlias explicitly."
    }

    # Prefer the adapter that has the wired 172.100.x.x address.
    $wired = $candidates | Where-Object { $_.IPAddress -like '172.100.*' } | Select-Object -First 1
    if ($wired) { return $wired.InterfaceAlias }

    return ($candidates | Select-Object -First 1).InterfaceAlias
}

$bindings = @{
    'A' = @{ Address = '192.168.1.10'; Prefix = 24; Label = 'LanRemote Lab A' }
    'B' = @{ Address = '192.168.1.20'; Prefix = 24; Label = 'LanRemote Lab B' }
}

$alias = Resolve-Adapter -Requested $InterfaceAlias

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host " LanRemote lab IPv4 setup" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host ("  adapter : " + $alias)
Write-Host ""

# ---------------------------------------------------------------------------
# Undo
# ---------------------------------------------------------------------------
if ($Undo) {
    $labAddresses = $bindings.Values | ForEach-Object { $_.Address }

    foreach ($address in $labAddresses) {
        $existing = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $address -ErrorAction SilentlyContinue
        if ($existing) {
            Remove-NetIPAddress -IPAddress $address -Confirm:$false
            Write-Host ("  removed " + $address) -ForegroundColor Green
        } else {
            Write-Host ("  not present " + $address) -ForegroundColor Gray
        }
    }

    Write-Host ""
    Write-Host " Done. Remaining IPv4 addresses:" -ForegroundColor Cyan
    Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias $alias |
        Format-Table IPAddress, PrefixLength, PrefixOrigin -AutoSize
    exit 0
}

if (-not $Role) {
    Write-Host " Missing -Role A|B (or use -Undo)." -ForegroundColor Red
    exit 1
}

$target = $bindings[$Role]
Write-Host ("  role    : " + $Role + "  ->  " + $target.Address + "/" + $target.Prefix)
Write-Host ""

# ---------------------------------------------------------------------------
# Idempotent add
# ---------------------------------------------------------------------------
$existing = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $target.Address -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host ("  " + $target.Address + " already present - nothing to do.") -ForegroundColor Yellow
} else {
    Write-Host ("  adding " + $target.Address + "/" + $target.Prefix + " ...")
    New-NetIPAddress `
        -InterfaceAlias $alias `
        -IPAddress $target.Address `
        -PrefixLength $target.Prefix `
        -SkipAsSource $false | Out-Null
    Write-Host "  added." -ForegroundColor Green
}

# A private network must be profiled Private, otherwise inbound UDP is blocked.
Write-Host ""
Write-Host "  setting network profile to Private (inbound UDP 45872) ..."
try {
    Set-NetConnectionProfile -InterfaceAlias $alias -NetworkCategory Private
    Write-Host "  profile = Private" -ForegroundColor Green
} catch {
    Write-Host ("  could not set profile: " + $_.Exception.Message) -ForegroundColor Yellow
    Write-Host "  set it manually in Settings > Network & Internet > Ethernet." -ForegroundColor Yellow
}

# Dedicated inbound rule for the discovery port.
Write-Host ""
Write-Host "  ensuring inbound UDP 45872 firewall rule ..."
$ruleName = 'LanRemote Discovery UDP 45872'
$rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host "  rule already exists." -ForegroundColor Green
} else {
    New-NetFirewallRule `
        -DisplayName $ruleName `
        -Direction Inbound `
        -Protocol UDP `
        -LocalPort 45872 `
        -Action Allow `
        -Profile Any | Out-Null
    Write-Host "  rule created." -ForegroundColor Green
}

Write-Host ""
Write-Host "--- resulting IPv4 addresses on $alias --------------------" -ForegroundColor Cyan
Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias $alias |
    Format-Table IPAddress, PrefixLength, PrefixOrigin -AutoSize

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host (" READY: this machine is LanRemote Lab " + $Role + ".") -ForegroundColor Green
if ($Role -eq 'A') {
    Write-Host " Now run set-lab-ip.ps1 -Role B on the OTHER machine." -ForegroundColor Green
} else {
    Write-Host " Both machines configured. Start LanRemote on both." -ForegroundColor Green
}
Write-Host ""
Write-Host " To undo afterwards:  .\set-lab-ip.ps1 -Undo" -ForegroundColor Gray
Write-Host "=========================================================" -ForegroundColor Cyan

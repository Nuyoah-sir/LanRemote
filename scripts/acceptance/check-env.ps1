# LanRemote M2.1 - two-machine acceptance: environment pre-check
#
# Run this BEFORE starting LanRemote on each machine:
#   powershell -ExecutionPolicy Bypass -File check-env.ps1
#
# This file is intentionally stored as UTF-8 WITH BOM so that Windows
# PowerShell 5.1 decodes the Chinese strings correctly.

$ErrorActionPreference = 'SilentlyContinue'

function Test-PrivateIPv4([string]$ip) {
    $p = $ip.Split('.')
    if ($p.Count -ne 4) { return $false }
    $a = 0; $b = 0
    if (-not [int]::TryParse($p[0], [ref]$a)) { return $false }
    if (-not [int]::TryParse($p[1], [ref]$b)) { return $false }
    if ($a -eq 10) { return $true }
    if ($a -eq 172 -and $b -ge 16 -and $b -le 31) { return $true }
    if ($a -eq 192 -and $b -eq 168) { return $true }
    return $false
}

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host " LanRemote environment pre-check" -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "[1] Machine"
Write-Host ("    Computer : " + $env:COMPUTERNAME)
Write-Host ("    User     : " + $env:USERDOMAIN + "\" + $env:USERNAME)
Write-Host ("    Arch     : " + $env:PROCESSOR_ARCHITECTURE)
Write-Host ""

Write-Host "[2] IPv4 addresses (need at least one RFC1918 on an Up adapter)"
$ok = $false
$rows = @()
foreach ($addr in Get-NetIPAddress -AddressFamily IPv4) {
    if ($addr.IPAddress -eq '127.0.0.1') { continue }
    $ifc    = Get-NetAdapter -InterfaceIndex $addr.InterfaceIndex
    $status = if ($ifc) { $ifc.Status } else { 'Unknown' }
    $priv   = Test-PrivateIPv4 $addr.IPAddress
    $mark   = if ($priv) { 'RFC1918' } else { 'NOT-private' }
    if ($priv -and $status -eq 'Up') { $ok = $true }
    Write-Host ("    {0,-16} /{1,-3} {2,-10} {3,-8} {4}" -f $addr.IPAddress, $addr.PrefixLength, $addr.InterfaceAlias, $status, $mark)
}
Write-Host ""
if ($ok) {
    Write-Host "    RESULT: OK - at least one private IPv4 is Up." -ForegroundColor Green
} else {
    Write-Host "    RESULT: FAIL - no RFC1918 private IPv4 on an Up adapter." -ForegroundColor Red
    Write-Host "             LanRemote will log: no eligible private IPv4 NIC." -ForegroundColor Red
}
Write-Host ""

Write-Host "[3] UDP 45872 (discovery port) already bound?"
$udp = Get-NetUDPEndpoint -LocalPort 45872
if ($udp) {
    foreach ($e in $udp) {
        $proc = Get-Process -Id $e.OwningProcess
        Write-Host ("    IN USE  pid=" + $e.OwningProcess + "  " + $proc.ProcessName) -ForegroundColor Yellow
    }
    Write-Host "    If this is an old LanRemote, close it before the test." -ForegroundColor Yellow
} else {
    Write-Host "    free - OK" -ForegroundColor Green
}
Write-Host ""

Write-Host "[4] Network profile (firewall implications)"
foreach ($p in Get-NetConnectionProfile) {
    $cat = $p.NetworkCategory
    $col = if ($cat -eq 'Public') { 'Red' } else { 'Green' }
    Write-Host ("    {0,-24} {1}" -f $p.InterfaceAlias, $cat) -ForegroundColor $col
}
Write-Host "    'Public' means inbound UDP 45872 is very likely blocked." -ForegroundColor Yellow
Write-Host "    Set the LAN to Private, or add an inbound allow rule (see doc)." -ForegroundColor Yellow
Write-Host ""

Write-Host "[5] Running LanRemote instances"
$procs = Get-Process -Name 'LanRemote.App'
if ($procs) {
    foreach ($pr in $procs) { Write-Host ("    running pid=" + $pr.Id) -ForegroundColor Yellow }
} else {
    Write-Host "    none - OK" -ForegroundColor Green
}
Write-Host ""

Write-Host "[6] Data directory"
$dir = Join-Path $env:LOCALAPPDATA 'LanRemote'
Write-Host ("    " + $dir)
if (Test-Path $dir) {
    foreach ($f in @('secrets.bin', 'config.json')) {
        $p = Join-Path $dir $f
        if (Test-Path $p) { Write-Host ("      found : " + $f) } else { Write-Host ("      absent: " + $f) }
    }
} else {
    Write-Host "      not created yet (first launch will create it)"
}
Write-Host ("    logs   : " + (Join-Path $dir 'logs'))
Write-Host ""

Write-Host "=========================================================" -ForegroundColor Cyan
if ($ok) { Write-Host " PRE-CHECK PASSED - this machine can run the test." -ForegroundColor Green }
else     { Write-Host " PRE-CHECK FAILED - fix the network first." -ForegroundColor Red }
Write-Host "=========================================================" -ForegroundColor Cyan

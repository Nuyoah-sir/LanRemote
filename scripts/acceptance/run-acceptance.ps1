# LanRemote M3 - two-machine acceptance driver (runs on the CONTROLLING machine)
#
# WHAT THIS DOES
#   Runs the three mandatory M3 scenarios against the host machine, one after
#   another, and prints a PASS/FAIL table plus an overall verdict.
#
#   The three mandatory scenarios are:
#     success      - real TLS + pinning + channel_hello must be accepted
#     pin-mismatch - a wrong certificate pin must make the handshake fail
#     timeout      - a peer that never says hello must be cut by the deadline
#
#   The fourth scenario (cross-subnet) is NOT run here on purpose: it needs the
#   two machines to sit in different subnets, which the lab setup used for M3
#   deliberately does not create. See START-HERE.md section 5.
#
# PREREQUISITES
#   1. Both machines ran  set-lab-ip.ps1  and are on 192.168.1.0/24.
#   2. On the OTHER machine, the host role is already running:
#        LanRemote.Acceptance.exe host --seconds 600
#   3. You know that machine's device code (printed by its `host` or `info`).
#
# USAGE
#   powershell -ExecutionPolicy Bypass -File run-acceptance.ps1 -PeerDeviceCode XXXX-XXXX
#
# Stored as UTF-8 WITH BOM so Windows PowerShell 5.1 decodes the Chinese text.

param(
    [Parameter(Mandatory = $true)]
    [string]$PeerDeviceCode,

    [string]$Exe = '',

    [string]$LogDir = ''
)

$ErrorActionPreference = 'Continue'

if ($Exe -eq '') {
    $Exe = Join-Path $PSScriptRoot 'LanRemote.Acceptance.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "ERROR: acceptance tool not found at $Exe" -ForegroundColor Red
    Write-Host "       pass -Exe <path to LanRemote.Acceptance.exe>" -ForegroundColor Yellow
    exit 2
}

if ($LogDir -eq '') {
    $LogDir = Join-Path $env:TEMP 'lanremote-m3-acceptance'
}
if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

function Say-Rule {
    Write-Host '=====================================================================' -ForegroundColor Cyan
}

Say-Rule
Write-Host (' LanRemote M3 two-machine acceptance   ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -ForegroundColor Cyan
Write-Host (' peer device code : ' + $PeerDeviceCode) -ForegroundColor Cyan
Write-Host (' logs             : ' + $LogDir) -ForegroundColor Cyan
Say-Rule
Write-Host ''

# ---------------------------------------------------------------------------
# step 0: environment self-check. A FAIL here means the rest is meaningless.
# ---------------------------------------------------------------------------
Write-Host '--- step 0: info (environment self-check) ---------------------------' -ForegroundColor Cyan
$infoLog = Join-Path $LogDir '00-info.txt'
& $Exe info 2>&1 | Tee-Object -FilePath $infoLog
$infoExit = $LASTEXITCODE
Write-Host ''

if ($infoExit -ne 0) {
    Write-Host ' ABORT: this machine has no eligible RFC1918 NIC.' -ForegroundColor Red
    Write-Host '        Run  .\set-lab-ip.ps1 -Role A  (administrator) and try again.' -ForegroundColor Yellow
    exit 2
}

# ---------------------------------------------------------------------------
# the three mandatory scenarios
# ---------------------------------------------------------------------------
$scenarios = @(
    @{ Name = 'success';      Expected = 0; Title = 'TLS + pin + channel_hello accepted' },
    @{ Name = 'pin-mismatch'; Expected = 0; Title = 'wrong certificate pin rejected' },
    @{ Name = 'timeout';      Expected = 0; Title = 'silent peer cut by pre-auth deadline' }
)

$results = @()
$verdict = 0

foreach ($scenario in $scenarios) {
    $name = $scenario.Name
    Write-Host ("--- scenario: $name  ($($scenario.Title))") -ForegroundColor Cyan

    $out = & $Exe client --scenario $name --device-code $PeerDeviceCode 2>&1
    $code = $LASTEXITCODE

    $logFile = Join-Path $LogDir ("client-$name.txt")
    $out | Set-Content -LiteralPath $logFile -Encoding UTF8
    $out | ForEach-Object { Write-Host "    $_" }

    $resultLine = ($out | Where-Object { $_ -match '\[RESULT\]' } | Select-Object -First 1)
    if (-not $resultLine) { $resultLine = '(no RESULT line)' }

    $ok = ($code -eq $scenario.Expected)
    if (-not $ok) { $verdict = 1 }

    Write-Host ("    => $(if ($ok) { 'PASS' } else { 'FAIL' })  exit=$code") -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    Write-Host ''

    $results += [pscustomobject]@{
        Scenario = $name
        Expected = $scenario.Expected
        Actual   = $code
        Verdict  = $(if ($ok) { 'PASS' } else { 'FAIL' })
        Result   = $resultLine.Trim()
    }
}

# ---------------------------------------------------------------------------
# summary
# ---------------------------------------------------------------------------
Say-Rule
Write-Host ' SUMMARY' -ForegroundColor Cyan
$results | Format-Table Scenario, Expected, Actual, Verdict -AutoSize | Out-String | Write-Host

foreach ($row in $results) {
    Write-Host ("  " + $row.Verdict + "  " + $row.Scenario + "  ::  " + $row.Result)
}

Write-Host ''
Write-Host ' REMINDER: copy the HOST console output too - it is the other half of the' -ForegroundColor Yellow
Write-Host ' evidence. What each scenario must show there:' -ForegroundColor Yellow
Write-Host ''
Write-Host '   success       [HOST][RESULT] conn#1 ... outcome=PreAuthenticated rejection=-' -ForegroundColor Yellow
Write-Host '   timeout       [HOST][RESULT] conn#1 ... outcome=Rejected rejection=pre-auth-timeout' -ForegroundColor Yellow
Write-Host '   pin-mismatch  NOTHING per connection. That is expected: the TLS' -ForegroundColor Yellow
Write-Host '                 handshake dies inside TransportHost, before the session' -ForegroundColor Yellow
Write-Host '                 handler ever runs - and TransportHost has no logger.' -ForegroundColor Yellow
Write-Host ''
Write-Host ' Host summary line must read  accepted=2 preAuthenticated=1 rejected=1' -ForegroundColor Yellow
Say-Rule

if ($verdict -eq 0) {
    Write-Host ' OVERALL: PASS (3/3 mandatory scenarios)' -ForegroundColor Green
} else {
    Write-Host ' OVERALL: FAIL - see the per-scenario logs in ' + $LogDir -ForegroundColor Red
}
Say-Rule

exit $verdict

# LanRemote M3 - two-machine acceptance driver
#
# USAGE
#   Double-click  START.cmd          <- recommended, keeps the window open
#   or           powershell -ExecutionPolicy Bypass -File .\run-acceptance.ps1
#   or non-interactively:
#                run-acceptance.ps1 -Role host
#                run-acceptance.ps1 -Role client -PeerDeviceCode XXXX-XXXX
#
# WHAT IT DOES
#   1. runs `info` (environment self-check) - aborts if this machine has no
#      eligible RFC1918 NIC, because everything after that would be meaningless
#   2. asks which role this machine plays: HOST (controlled) or CLIENT (controller)
#   3. HOST   -> starts the transport host and waits, printing one RESULT line
#                per connection
#      CLIENT -> asks for the peer device code, then runs the three mandatory
#                scenarios and prints a PASS/FAIL table
#
# The three mandatory scenarios
#   success      - real TLS + pinning + channel_hello must be accepted
#   pin-mismatch - a wrong certificate pin must make the handshake fail
#   timeout      - a peer that never says hello must be cut by the deadline
#
# The fourth scenario (cross-subnet) is deliberately NOT run: it needs the two
# machines to sit in DIFFERENT subnets, which the lab setup used here
# deliberately does not create. See START-HERE.md section 5.
#
# PREREQUISITES
#   Both machines ran set-lab-ip.ps1 (admin) and are on 192.168.1.0/24.
#   On the HOST machine this tool must already be running before the CLIENT
#   starts - otherwise the client reports peer-port-unreachable (exit 2), which
#   is "could not test", NOT "the test failed".
#
# Stored as UTF-8 WITH BOM so Windows PowerShell 5.1 renders the Chinese text.

param(
    [ValidateSet('', 'host', 'client')]
    [string]$Role = '',

    [string]$PeerDeviceCode = '',

    [string]$Exe = '',

    [string]$LogDir = '',

    [int]$HostSeconds = 600
)

$ErrorActionPreference = 'Continue'

function Say-Rule {
    Write-Host '=====================================================================' -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# locate the acceptance tool
# ---------------------------------------------------------------------------
if ($Exe -eq '') {
    $Exe = Join-Path $PSScriptRoot 'LanRemote.Acceptance.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "ERROR: acceptance tool not found at $Exe" -ForegroundColor Red
    Write-Host '       pass -Exe <path to LanRemote.Acceptance.exe>' -ForegroundColor Yellow
    exit 2
}

if ($LogDir -eq '') {
    $LogDir = Join-Path $env:TEMP 'lanremote-m3-acceptance'
}
if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

Say-Rule
Write-Host (' LanRemote M3 two-machine acceptance   ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -ForegroundColor Cyan
Write-Host (' tool  : ' + $Exe) -ForegroundColor Cyan
Write-Host (' logs  : ' + $LogDir) -ForegroundColor Cyan
Say-Rule
Write-Host ''

# ---------------------------------------------------------------------------
# step 0: environment self-check
# ---------------------------------------------------------------------------
Write-Host '--- step 0: info (environment self-check) ---------------------------' -ForegroundColor Cyan
$infoLog = Join-Path $LogDir '00-info.txt'
& $Exe info 2>&1 | Tee-Object -FilePath $infoLog
$infoExit = $LASTEXITCODE
Write-Host ''

if ($infoExit -ne 0) {
    Write-Host ' ABORT: this machine has no eligible RFC1918 NIC.' -ForegroundColor Red
    Write-Host '        Run (administrator):  .\set-lab-ip.ps1 -Role A   or   -Role B' -ForegroundColor Yellow
    exit 2
}

# ---------------------------------------------------------------------------
# step 1: which role?
# ---------------------------------------------------------------------------
if ($Role -eq '') {
    Write-Host ''
    Say-Rule
    Write-Host ' Which machine is this?' -ForegroundColor Cyan
    Write-Host '   1 = HOST    (the controlled machine - starts listening)' -ForegroundColor Cyan
    Write-Host '   2 = CLIENT  (the controlling machine - runs the scenarios)' -ForegroundColor Cyan
    Say-Rule
    $answer = (Read-Host 'Enter 1 or 2').Trim()
    if ($answer -eq '1') { $Role = 'host' }
    elseif ($answer -eq '2') { $Role = 'client' }
    else {
        Write-Host " '$answer' is not 1 or 2 - rerun with -Role host or -Role client." -ForegroundColor Red
        exit 2
    }
    Write-Host ''
}

# ---------------------------------------------------------------------------
# HOST branch
# ---------------------------------------------------------------------------
if ($Role -eq 'host') {
    Write-Host ('--- HOST: listening for ' + $HostSeconds + ' seconds (Ctrl+C to stop) ---') -ForegroundColor Cyan
    Write-Host ' LEAVE THIS WINDOW OPEN and go run the CLIENT role on the other machine.' -ForegroundColor Yellow
    Write-Host ''
    & $Exe host --seconds $HostSeconds 2>&1 | Tee-Object -FilePath (Join-Path $LogDir 'host.txt')
    $hostExit = $LASTEXITCODE
    Write-Host ''
    Say-Rule
    Write-Host (' HOST exit code: ' + $hostExit) -ForegroundColor $(if ($hostExit -eq 0) { 'Green' } else { 'Red' })
    Write-Host ' Copy this whole window back - the [HOST][RESULT] lines are the evidence.' -ForegroundColor Yellow
    Say-Rule
    exit $hostExit
}

# ---------------------------------------------------------------------------
# CLIENT branch
# ---------------------------------------------------------------------------
if ($PeerDeviceCode -eq '') {
    Write-Host ''
    Say-Rule
    Write-Host ' Peer device code' -ForegroundColor Cyan
    Write-Host '   Printed by the HOST machine (this tool or its `info` output),' -ForegroundColor Cyan
    Write-Host '   looks like  M5WC-14GX . It is NOT a secret.' -ForegroundColor Cyan
    Say-Rule
    $PeerDeviceCode = (Read-Host 'Enter the HOST device code').Trim()
    Write-Host ''
}

if ($PeerDeviceCode -eq '') {
    Write-Host ' ABORT: no device code given. Pass -PeerDeviceCode XXXX-XXXX' -ForegroundColor Red
    exit 2
}

Write-Host (' peer device code : ' + $PeerDeviceCode) -ForegroundColor Cyan
Write-Host ''

$scenarios = @(
    @{ Name = 'success';      Title = 'TLS + pin + channel_hello accepted' },
    @{ Name = 'pin-mismatch'; Title = 'wrong certificate pin rejected' },
    @{ Name = 'timeout';      Title = 'silent peer cut by pre-auth deadline' }
)

$results = @()
$verdict = 0

foreach ($scenario in $scenarios) {
    $name = $scenario.Name
    Write-Host ("--- scenario: $name  ($($scenario.Title))") -ForegroundColor Cyan

    $out = & $Exe client --scenario $name --device-code $PeerDeviceCode 2>&1
    $code = $LASTEXITCODE

    $out | Set-Content -LiteralPath (Join-Path $LogDir ("client-$name.txt")) -Encoding UTF8
    $out | ForEach-Object { Write-Host "    $_" }

    $resultLine = ($out | Where-Object { $_ -match '\[RESULT\]' } | Select-Object -First 1)
    if (-not $resultLine) { $resultLine = '(no RESULT line)' }

    $ok = ($code -eq 0)
    if (-not $ok) { $verdict = 1 }

    Write-Host ("    => $(if ($ok) { 'PASS' } else { 'FAIL' })  exit=$code") -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    Write-Host ''

    $results += [pscustomobject]@{
        Scenario = $name
        Expected = 0
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
    Write-Host ('  ' + $row.Verdict + '  ' + $row.Scenario + '  ::  ' + $row.Result)
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
    Write-Host (' OVERALL: FAIL - see the per-scenario logs in ' + $LogDir) -ForegroundColor Red
}
Say-Rule

exit $verdict

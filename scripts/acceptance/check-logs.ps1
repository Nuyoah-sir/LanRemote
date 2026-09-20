# LanRemote M2.1 - two-machine acceptance: log inspection
#
# Run this AFTER the acceptance steps, on each machine:
#   powershell -ExecutionPolicy Bypass -File check-logs.ps1
#
# It answers two questions:
#   1. Did discovery actually do anything? (counts of key log events)
#   2. Did any Access Key leak into the log?  <- acceptance step 20
#
# This file is intentionally stored as UTF-8 WITH BOM so that Windows
# PowerShell 5.1 decodes the Chinese strings correctly.
#
# SAFETY: if a key-looking token is ever found, it is printed MASKED.
# This script must never echo a real access key to the console.

$ErrorActionPreference = 'SilentlyContinue'

$dir = Join-Path $env:LOCALAPPDATA 'LanRemote\logs'
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host " LanRemote log inspection" -ForegroundColor Cyan
Write-Host (" dir: " + $dir) -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $dir)) {
    Write-Host "Log directory does not exist - app has never run." -ForegroundColor Red
    exit 1
}

$files = Get-ChildItem -Path $dir -Filter 'lanremote-*.log' | Sort-Object LastWriteTime
if (-not $files) {
    Write-Host "No log files found." -ForegroundColor Red
    exit 1
}

$lines = @()
foreach ($f in $files) {
    Write-Host ("  " + $f.Name + "  " + $f.Length + " bytes")
    $lines += Get-Content -LiteralPath $f.FullName -Encoding UTF8
}
Write-Host ""
Write-Host ("total lines: " + $lines.Count)
Write-Host ""

Write-Host "--- key events -------------------------------------------" -ForegroundColor Cyan

function Count-Lines([string]$label, [string]$needle, [string]$hint) {
    $hits = @($lines | Where-Object { $_.Contains($needle) })
    $col = 'Gray'
    if ($hits.Count -gt 0) { $col = 'Green' }
    Write-Host ("  {0,-26} {1,5}   {2}" -f $label, $hits.Count, $hint) -ForegroundColor $col
    return $hits.Count
}

$noNic   = Count-Lines 'no eligible NIC'     '没有找到任何合格的私有 IPv4 网卡' 'must be 0'
$started = Count-Lines 'discovery started'   '局域网发现启动'                   'should be >= 1'
$found   = Count-Lines 'device discovered'   '发现设备'                         'should be >= 1'
$offline = Count-Lines 'device went offline' '设备离线'                         'after peer exit'
$probe   = Count-Lines 'probe sent'          '已发送 probe'                     'after refresh'
$dropped = Count-Lines 'packets dropped'     '丢弃发现报文'                     'informative'
Write-Host ""

Write-Host "--- local identity ---------------------------------------" -ForegroundColor Cyan
$identityLines = @($lines | Where-Object { $_.Contains('本机身份就绪') })
if ($identityLines.Count -gt 0) {
    Write-Host ("  " + $identityLines[$identityLines.Count - 1])
} else {
    Write-Host "  (identity line not found)"
}
Write-Host ""

Write-Host "--- Access Key leak scan (acceptance step 20) ------------" -ForegroundColor Cyan
# Crockford Base32 excludes I / L / O / U. A 128-bit key encodes to 26 chars.
# The device code is only 8 chars, so a 26-char run can never be a device code.
$keyPattern = '[0-9A-HJKMNP-TV-Z]{26}'
Write-Host ("  scanner : " + $keyPattern)

$leak = 0
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $keyPattern) {
        $leak++
        $tail = $lines[$i].Substring([Math]::Max(0, $lines[$i].Length - 12))
        Write-Host ("    line " + ($i + 1) + " : ..." + $tail) -ForegroundColor Red
    }
}
Write-Host ("  matches : " + $leak)
Write-Host ""

Write-Host "=========================================================" -ForegroundColor Cyan
if ($noNic -gt 0) {
    Write-Host " VERDICT: FAIL - this machine has no private IPv4 NIC." -ForegroundColor Red
} elseif ($leak -gt 0) {
    Write-Host (" VERDICT: FAIL - possible access key in log (" + $leak + " hit(s))." -ForegroundColor Red
    Write-Host "          Report as a BLOCKER and do not enter M3." -ForegroundColor Red
} elseif ($started -eq 0) {
    Write-Host " VERDICT: INCONCLUSIVE - discovery never started." -ForegroundColor Yellow
} else {
    Write-Host " VERDICT: PASS - no access key found in logs." -ForegroundColor Green
}
Write-Host "=========================================================" -ForegroundColor Cyan

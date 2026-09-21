# LanRemote M2.1 - two-machine acceptance: give this NIC a private lab IPv4
#
# WHY THIS IS NEEDED
#   LanRemote only uses RFC1918 private IPv4 (10/8, 172.16-172.31, 192.168/16).
#   172.100.x.x LOOKS private but is NOT: the 172 block only covers 172.16-172.31.
#   Both lab machines sit on 172.100.166.x, so discovery correctly refuses them
#   and logs "no eligible private IPv4 NIC". This script puts a real private
#   address on the SAME wire so both machines can talk RFC1918 to each other.
#
# HOW IT WORKS (v2 - read this before trusting any other doc)
#   Windows IPv4 is "DHCP OR static" per interface. It is NOT possible to keep a
#   DHCP lease and append an extra static address: both New-NetIPAddress and
#   "netsh interface ipv4 add address" silently flip the interface to
#   Dhcp=Disabled and drop the lease. (Measured on this machine, see
#   docs/TWO_MACHINE_ACCEPTANCE.md section 1.2.)
#
#   So v2 does the honest thing:
#     1. read the CURRENT IPv4 config (address/mask/gateway/DNS/Dhcp flag)
#     2. save it to %TEMP%\lanremote-lab-ip-state.json
#     3. switch the interface to STATIC with exactly that config  -> no outage
#     4. append 192.168.1.10 (role A) or 192.168.1.20 (role B), /24, no gateway
#     5. set the profile to Private and open INBOUND UDP 45872 + TCP 45873
#        (discovery AND control - opening only UDP makes M3 fail silently)
#   Result: the NIC keeps 172.100.166.x AND gains 192.168.1.x at the same time.
#
#   -Undo removes the lab address, deletes those two firewall rules and puts the
#   interface back to DHCP (or back to the original static config, if that is how
#   it was). It self-verifies and, if only an APIPA 169.254.x.x address is left,
#   prints the manual fix commands.
#
# USAGE (ADMIN PowerShell, run once per machine with a different -Role)
#   Machine A:  powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Role A
#   Machine B:  powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Role B
#
# TO UNDO (any time, on any machine)
#   powershell -ExecutionPolicy Bypass -File set-lab-ip.ps1 -Undo
#
# Stored as UTF-8 WITH BOM so Windows PowerShell 5.1 decodes the Chinese text.

param(
    [ValidateSet('A', 'B')]
    [string]$Role,

    [string]$InterfaceAlias = '',

    [switch]$Undo
)

$ErrorActionPreference = 'Stop'

$stateFile = Join-Path $env:TEMP 'lanremote-lab-ip-state.json'
$logFile   = Join-Path $env:TEMP 'lanremote-lab-ip.log'

$labBindings = @{
    'A' = @{ Address = '192.168.1.10'; Prefix = 24 }
    'B' = @{ Address = '192.168.1.20'; Prefix = 24 }
}
$labAddresses = @('192.168.1.10', '192.168.1.20')

# ---------------------------------------------------------------------------
# output helpers - console AND %TEMP%\lanremote-lab-ip.log
# ---------------------------------------------------------------------------
function Say {
    param([string]$Text = '', [string]$Color = 'Gray')
    if ($Text -ne '') { Write-Host $Text -ForegroundColor $Color }
    Add-Content -LiteralPath $logFile -Value $Text -Encoding UTF8
}

function Say-Rule {
    Say '=========================================================' 'Cyan'
}

# ---------------------------------------------------------------------------
# netsh wrapper: prefer the interface index (survives non-ASCII adapter names),
# fall back to the alias.
#
# IMPORTANT (measured): netsh exits NON-ZERO for purely informational messages.
#   "netsh interface ipv4 set address ... source=dhcp" on an interface that is
#   already DHCP prints "已在此接口上启用 DHCP。" and returns 1. Treating that as
#   failure is what broke the first -Undo. So this function NEVER throws on the
#   exit code - it returns whatever netsh printed and the CALLER verifies the
#   resulting state with Get-NetIP* cmdlets, which is the only trustworthy check.
# ---------------------------------------------------------------------------
function Invoke-Netsh {
    param(
        [string]$Alias,
        [int]$Index,
        [string[]]$Arguments
    )

    $byIndex = @('interface', 'ipv4') + $Arguments + @("name=$Index")
    $byAlias = @('interface', 'ipv4') + $Arguments + @("name=$Alias")

    # netsh prints localized GBK text; decode it correctly so the log is readable.
    $prevEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding(936)
        $out = & netsh.exe $byIndex 2>&1
        if ($LASTEXITCODE -eq 0) { return ($out | Out-String).Trim() }
        $first = ($out | Out-String).Trim()

        $out2 = & netsh.exe $byAlias 2>&1
        if ($LASTEXITCODE -eq 0) { return ($out2 | Out-String).Trim() }
        $second = ($out2 | Out-String).Trim()
    } finally {
        [Console]::OutputEncoding = $prevEncoding
    }

    return ($first + ' | alias: ' + $second)
}

function Convert-PrefixToMask {
    param([int]$Prefix)
    if ($Prefix -le 0 -or $Prefix -gt 32) { throw "bad prefix length $Prefix" }
    $bits = [uint32]0
    for ($i = 0; $i -lt $Prefix; $i++) { $bits = ($bits -shl 1) -bor 1 }
    $bits = $bits -shl (32 - $Prefix)
    $b0 = [byte](($bits -shr 24) -band 0xFF)
    $b1 = [byte](($bits -shr 16) -band 0xFF)
    $b2 = [byte](($bits -shr 8) -band 0xFF)
    $b3 = [byte]($bits -band 0xFF)
    return "$b0.$b1.$b2.$b3"
}

# ---------------------------------------------------------------------------
# adapter resolution. NOTE: v1 threw "No IPv4 adapter found" when only an APIPA
# address was left, which made the second -Undo impossible. v2 always returns an
# adapter so that -Undo can still repair the interface.
# ---------------------------------------------------------------------------
# Virtual NICs are the trap here (learned the hard way on machine B): VMware /
# VirtualBox / Hyper-V / TAP / hosted-network adapters are "Up", report MediaType
# 802.3 and carry an IPv4 address, so a naive "first Up 802.3 adapter" picks them.
# But their traffic never reaches the physical wire, so a lab address placed on
# VMnet1 is invisible to the other machine. Always prefer real hardware NICs.
function Test-VirtualAdapter {
    param($Adapter)

    $name = $(if ($Adapter.Name) { $Adapter.Name } else { '' })
    $desc = $(if ($Adapter.InterfaceDescription) { $Adapter.InterfaceDescription } else { '' })
    $text = $desc + ' | ' + $name

    $patterns = @(
        'VMware', 'VMnet', 'VirtualBox', 'Hyper-V', 'TAP-Windows', 'TAP Adapter',
        'OpenVPN', 'WireGuard', 'Npcap', 'Bluetooth', 'WAN Miniport', 'Wi-Fi Direct',
        'Hosted Network', 'Loopback', 'Teredo', 'ISATAP', 'RAS Async', 'ZeroTier',
        'Sangfor', 'PANGP', 'Fortinet', 'AnyConnect', 'NordLynx', 'ExpressVPN',
        'Microsoft KM-TEST', 'Tunnel', 'Virtual'
    )
    foreach ($p in $patterns) {
        if ($text -match [regex]::Escape($p)) { return $true }
    }

    # Windows mobile-hotspot / hosted-network adapters: "本地连接* 1"
    if ($name -match '^本地连接\s*\*') { return $true }
    if ($name -match '^Local Area Connection\s*\*') { return $true }

    return $false
}

function Resolve-Adapter {
    param([string]$Requested)

    if ($Requested -ne '') {
        $hit = @(Get-NetAdapter -InterfaceAlias $Requested -ErrorAction SilentlyContinue)
        if ($hit.Count -eq 0) {
            $hit = @(Get-NetAdapter -Name $Requested -ErrorAction SilentlyContinue)
        }
        if ($hit.Count -eq 0) { throw "No adapter named '$Requested'." }
        return $hit[0]
    }

    $up = @(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' })
    if ($up.Count -eq 0) {
        throw "No adapter is Up. Plug in the cable or pass -InterfaceAlias."
    }

    $real = @($up | Where-Object { -not (Test-VirtualAdapter $_) })
    if ($real.Count -eq 0) { $real = $up }

    # pass 1: real NIC + wired + has a default gateway  (the machine's real LAN)
    foreach ($a in $real) {
        if ($a.MediaType -ne '802.3') { continue }
        $usable = @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' })
        if ($usable.Count -eq 0) { continue }
        $hasGw = @(Get-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count -gt 0
        if ($hasGw) { return $a }
    }

    # pass 2: real NIC with any usable IPv4
    foreach ($a in $real) {
        $usable = @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' })
        if ($usable.Count -gt 0) { return $a }
    }

    Say '  WARNING: every Up adapter only has an APIPA 169.254.x.x address.' 'Yellow'
    return $real[0]
}

# ---------------------------------------------------------------------------
# private profile + inbound firewall rules
#
# The transport needs TWO ports: UDP 45872 (discovery) and TCP 45873 (control).
# An earlier revision only opened UDP 45872. That is enough for M2.1 discovery
# acceptance, but NOT for M3: the accepting machine silently drops the inbound
# SYN and the client reports "connection refused / timed out" with no hint that
# the firewall was the cause. (Measured on machine A: Windows Firewall blocks
# inbound by default on the Private profile and no TCP 45873 rule existed.)
# ---------------------------------------------------------------------------
function Set-LabNetworkProfileAndFirewall {
    param([string]$Alias)

    Say ''
    Say '  network profile + inbound firewall rules ...'

    # After switching to static the adapter briefly sits in "Identifying...", and
    # Set-NetConnectionProfile fails during that window. Retry instead of giving up.
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Set-NetConnectionProfile -InterfaceAlias $Alias -NetworkCategory Private -ErrorAction Stop
            Say '  profile = Private' 'Green'
            break
        } catch {
            if ($attempt -eq 5) {
                Say ('  could not set profile: ' + $_.Exception.Message) 'Yellow'
                Say '  set it manually: Settings > Network & Internet > Ethernet > Private.' 'Yellow'
            } else {
                Start-Sleep -Seconds 3
            }
        }
    }

    $rules = @(
        @{ Name = 'LanRemote Discovery UDP 45872'; Proto = 'UDP'; Port = 45872 },
        @{ Name = 'LanRemote Control TCP 45873';   Proto = 'TCP'; Port = 45873 }
    )
    foreach ($r in $rules) {
        if (Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue) {
            Say ('  rule exists : ' + $r.Name) 'Green'
        } else {
            New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Protocol $r.Proto `
                -LocalPort $r.Port -Action Allow -Profile Any | Out-Null
            Say ('  rule created: ' + $r.Name) 'Green'
        }
    }
}

# ---------------------------------------------------------------------------
# snapshot of the current IPv4 config
# ---------------------------------------------------------------------------
function Get-IpState {
    param($Adapter)

    $addrs = @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $Adapter.ifIndex -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' })

    $primary = $addrs | Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1
    if (-not $primary) { $primary = $addrs | Select-Object -First 1 }

    $iface = Get-NetIPInterface -InterfaceIndex $Adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue

    $gw = ''
    $route = Get-NetRoute -InterfaceIndex $Adapter.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($route) { $gw = $route.NextHop }

    $dns = @(Get-DnsClientServerAddress -InterfaceIndex $Adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        ForEach-Object { $_.ServerAddresses } | Where-Object { $_ -ne '' })

    return @{
        InterfaceAlias = $Adapter.InterfaceAlias
        InterfaceIndex = $Adapter.ifIndex
        Address        = $(if ($primary) { $primary.IPAddress } else { '' })
        PrefixLength   = $(if ($primary) { [int]$primary.PrefixLength } else { 24 })
        Gateway        = $gw
        Dns            = @($dns)
        WasDhcp        = $(if ($iface) { ($iface.Dhcp -eq 'Enabled') } else { $true })
    }
}

function Write-IpState {
    param($State)
    $State | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $stateFile -Encoding UTF8
}

function Show-IpState {
    param([string]$Title, [string]$Color = 'Cyan')
    Say '' 
    Say $Title $Color
    Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -ne '127.0.0.1' } |
        Sort-Object InterfaceIndex |
        Format-Table InterfaceAlias, IPAddress, PrefixLength, PrefixOrigin -AutoSize |
        Out-String | ForEach-Object { Say $_.TrimEnd() 'Gray' }
}

# ---------------------------------------------------------------------------
# guard: this script changes IP configuration, so require elevation
# ---------------------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "ERROR: run this from an ADMINISTRATOR PowerShell." -ForegroundColor Red
    exit 1
}

Say-Rule
Say (' LanRemote lab IPv4 setup  (v2)   ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) 'Cyan'
Say-Rule

$adapter = Resolve-Adapter -Requested $InterfaceAlias
$alias   = $adapter.InterfaceAlias
$index   = $adapter.ifIndex

$desc = $(if ($adapter.InterfaceDescription) { $adapter.InterfaceDescription } else { '(no description)' })
Say ('  adapter : ' + $alias + '  (ifIndex ' + $index + ')')
Say ('  desc    : ' + $desc)

if ($InterfaceAlias -eq '' -and (Test-VirtualAdapter $adapter)) {
    Say '' 
    Say '  WARNING: this looks like a VIRTUAL adapter (VMware / VirtualBox / Hyper-V /' 'Yellow'
    Say '           TAP / hosted-network). A lab address here will NOT reach the physical' 'Yellow'
    Say '           LAN and the other machine will never see it. If that is wrong, rerun' 'Yellow'
    Say '           with  -InterfaceAlias "<real NIC>"  (see Get-NetAdapter | ft Name,Status).' 'Yellow'
}

# ---------------------------------------------------------------------------
# UNDO
# ---------------------------------------------------------------------------
if ($Undo) {
    Say '  mode    : UNDO' 'Cyan'
    Say ''

    $prior = $null
    if (Test-Path -LiteralPath $stateFile) {
        try { $prior = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $prior = $null }
    }

    # 1. drop every lab address that exists
    foreach ($address in $labAddresses) {
        $hit = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $address -ErrorAction SilentlyContinue)
        if ($hit.Count -gt 0) {
            Remove-NetIPAddress -IPAddress $address -InterfaceIndex $hit[0].InterfaceIndex -Confirm:$false
            Say ('  removed ' + $address) 'Green'
        } else {
            Say ('  not present ' + $address) 'DarkGray'
        }
    }

    # 1b. drop the inbound rules this script creates, so -Undo really undoes
    #     everything it did (address + profile-side effects are listed above;
    #     the profile itself is left as-is because "Private" is also what the
    #     user's own LAN wants).
    foreach ($ruleName in @('LanRemote Discovery UDP 45872', 'LanRemote Control TCP 45873')) {
        if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
            Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
            Say ('  removed firewall rule: ' + $ruleName) 'Green'
        } else {
            Say ('  no firewall rule: ' + $ruleName) 'DarkGray'
        }
    }

    # 2. restore DHCP, or restore the original static config
    $restoreTo = $alias
    $restoreIdx = $index
    if ($prior -and $prior.InterfaceAlias) {
        $restoreTo  = $prior.InterfaceAlias
        $restoreIdx = [int]$prior.InterfaceIndex
    }

    Say ''
    if ($prior -and $prior.WasDhcp -eq $false) {
        Say '  original config was static - restoring it ...'
        $mask = Convert-PrefixToMask -Prefix ([int]$prior.PrefixLength)
        if ($prior.Gateway) {
            Invoke-Netsh -Alias $restoreTo -Index $restoreIdx `
                -Arguments @('set', 'address', 'source=static', ('addr=' + $prior.Address), ('mask=' + $mask), ('gateway=' + $prior.Gateway), 'gwmetric=1') | Out-Null
        } else {
            Invoke-Netsh -Alias $restoreTo -Index $restoreIdx `
                -Arguments @('set', 'address', 'source=static', ('addr=' + $prior.Address), ('mask=' + $mask)) | Out-Null
        }
        if ($prior.Dns -and @($prior.Dns).Count -gt 0) {
            $dnsList = @($prior.Dns)
            Invoke-Netsh -Alias $restoreTo -Index $restoreIdx `
                -Arguments @('set', 'dnsservers', 'source=static', ('addr=' + $dnsList[0]), 'validate=no') | Out-Null
            for ($i = 1; $i -lt $dnsList.Count; $i++) {
                Invoke-Netsh -Alias $restoreTo -Index $restoreIdx `
                    -Arguments @('add', 'dnsservers', ('addr=' + $dnsList[$i]), ('index=' + ($i + 1)), 'validate=no') | Out-Null
            }
        }
        Say '  static config restored.' 'Green'
    } else {
        Say '  switching interface back to DHCP ...'
        Invoke-Netsh -Alias $restoreTo -Index $restoreIdx -Arguments @('set', 'address', 'source=dhcp') | Out-Null
        Invoke-Netsh -Alias $restoreTo -Index $restoreIdx -Arguments @('set', 'dnsservers', 'source=dhcp') | Out-Null
        Say '  dhcp enabled, renewing lease ...'
        & ipconfig.exe /renew | Out-Null
        Say '  lease renewed.' 'Green'
    }

    if (Test-Path -LiteralPath $stateFile) { Remove-Item -LiteralPath $stateFile -Force }

    Start-Sleep -Seconds 2
    Show-IpState -Title '--- after undo -------------------------------------------'

    # 3. self-verification: a machine left on APIPA only is NOT recovered
    $stillManual = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $labAddresses -contains $_.IPAddress })
    $usable = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' })

    Say ''
    if ($stillManual.Count -gt 0) {
        Say ('  FAIL: lab address still present: ' + ($stillManual.IPAddress -join ', ')) 'Red'
        Say ('  manual removal:  netsh interface ipv4 delete address name="' + $restoreTo + '" addr=' + $stillManual[0].IPAddress) 'Yellow'
        exit 1
    }
    if ($usable.Count -eq 0) {
        Say '  FAIL: no usable IPv4 address left (only APIPA 169.254.x.x).' 'Red'
        Say '  run these three commands in an ADMIN cmd/PowerShell:' 'Yellow'
        Say ('    netsh interface ipv4 set address name="' + $restoreTo + '" source=dhcp') 'Yellow'
        Say ('    netsh interface ipv4 set dnsservers name="' + $restoreTo + '" source=dhcp') 'Yellow'
        Say '    ipconfig /renew' 'Yellow'
        exit 1
    }

    $ifaceNow = Get-NetIPInterface -InterfaceIndex $restoreIdx -AddressFamily IPv4 -ErrorAction SilentlyContinue
    $dhcpNow = $(if ($ifaceNow) { $ifaceNow.Dhcp } else { '(unknown)' })
    if ($prior -and $prior.WasDhcp -eq $true -and $dhcpNow -ne 'Enabled') {
        Say ('  FAIL: interface is still not DHCP (Dhcp=' + $dhcpNow + ').') 'Red'
        Say ('  run:  netsh interface ipv4 set address name="' + $restoreTo + '" source=dhcp') 'Yellow'
        Say ('        netsh interface ipv4 set dnsservers name="' + $restoreTo + '" source=dhcp') 'Yellow'
        Say '        ipconfig /renew' 'Yellow'
        exit 1
    }

    Say ('  OK: ' + ($usable.IPAddress -join ', ')) 'Green'
    Say-Rule
    exit 0
}

# ---------------------------------------------------------------------------
# APPLY
# ---------------------------------------------------------------------------
if (-not $Role) {
    Say '  ERROR: missing -Role A|B   (or use -Undo).' 'Red'
    exit 1
}

$target = $labBindings[$Role]
Say ('  role    : ' + $Role + '   ->   ' + $target.Address + '/' + $target.Prefix)
Say ''

# 1. snapshot BEFORE touching anything
$state = Get-IpState -Adapter $adapter
if (-not $state.Address) {
    Say '  ERROR: this adapter has no IPv4 address to preserve.' 'Red'
    exit 1
}
Write-IpState -State $state
Say ('  saved   : ' + $stateFile)
Say ('  current : ' + $state.Address + '/' + $state.PrefixLength +
     '  gw=' + $(if ($state.Gateway) { $state.Gateway } else { '(none)' }) +
     '  dhcp=' + $state.WasDhcp +
     '  dns=' + $(if (@($state.Dns).Count) { (@($state.Dns) -join ',') } else { '(none)' }))

# 2. idempotent: if the lab address is already there, leave the address alone -
#    but STILL verify the profile and BOTH firewall rules. An earlier revision
#    only opened UDP 45872, so exiting here without re-checking would leave
#    TCP 45873 blocked and the M3 control connection would fail silently.
$already = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $target.Address -ErrorAction SilentlyContinue)
if ($already.Count -gt 0) {
    Say ('  ' + $target.Address + ' already present - address left untouched.') 'Yellow'
    Set-LabNetworkProfileAndFirewall -Alias $alias
    Show-IpState -Title '--- current IPv4 -----------------------------------------'
    Say-Rule
    exit 0
}

# 3. switch the interface to static with the SAME config (keeps you online)
$mask = Convert-PrefixToMask -Prefix ([int]$state.PrefixLength)
Say ''
Say ('  step 1/3: switch to static, keeping ' + $state.Address + '/' + $mask + ' ...')
if ($state.Gateway) {
    $staticOut = Invoke-Netsh -Alias $alias -Index $index `
        -Arguments @('set', 'address', 'source=static', ('addr=' + $state.Address), ('mask=' + $mask), ('gateway=' + $state.Gateway), 'gwmetric=1')
} else {
    $staticOut = Invoke-Netsh -Alias $alias -Index $index `
        -Arguments @('set', 'address', 'source=static', ('addr=' + $state.Address), ('mask=' + $mask))
}

# verify by state, not by netsh exit code
$kept = @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $index -IPAddress $state.Address -ErrorAction SilentlyContinue)
if ($kept.Count -eq 0) {
    Say '  FAIL: the original address disappeared while switching to static.' 'Red'
    Say ('  netsh said: ' + $staticOut) 'Yellow'
    Say ('  run:  .\set-lab-ip.ps1 -Undo') 'Yellow'
    exit 1
}
Say ('  kept ' + $state.Address + ' (PrefixOrigin=' + $kept[0].PrefixOrigin + ')') 'Green'

$dnsList = @($state.Dns)
if ($dnsList.Count -gt 0) {
    Invoke-Netsh -Alias $alias -Index $index `
        -Arguments @('set', 'dnsservers', 'source=static', ('addr=' + $dnsList[0]), 'validate=no') | Out-Null
    for ($i = 1; $i -lt $dnsList.Count; $i++) {
        Invoke-Netsh -Alias $alias -Index $index `
            -Arguments @('add', 'dnsservers', ('addr=' + $dnsList[$i]), ('index=' + ($i + 1)), 'validate=no') | Out-Null
    }
    Say ('  dns kept: ' + ($dnsList -join ', '))
}
Say '  static applied.' 'Green'

# 4. append the lab address (no gateway - it must not compete with the real one)
Say ''
Say ('  step 2/3: append lab address ' + $target.Address + '/255.255.255.0 (no gateway) ...')
Invoke-Netsh -Alias $alias -Index $index `
    -Arguments @('add', 'address', ('addr=' + $target.Address), 'mask=255.255.255.0') | Out-Null
Say '  appended.' 'Green'

# 5. private profile + inbound firewall rules (UDP 45872 + TCP 45873)
Say ''
Say '  step 3/3: network profile + firewall (UDP 45872 discovery, TCP 45873 control) ...'
Set-LabNetworkProfileAndFirewall -Alias $alias

Show-IpState -Title '--- resulting IPv4 ---------------------------------------'

# 6. verify the lab address really landed
$check = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $target.Address -ErrorAction SilentlyContinue)
Say-Rule
if ($check.Count -gt 0) {
    Say (' READY: this machine is LanRemote Lab ' + $Role + ' (' + $target.Address + ').') 'Green'
    if ($Role -eq 'A') {
        Say ' Now run:  .\set-lab-ip.ps1 -Role B   on the OTHER machine.' 'Green'
    } else {
        Say ' Both machines configured. Start LanRemote on both.' 'Green'
    }
    Say '' 
    Say (' Undo afterwards:   .\set-lab-ip.ps1 -Undo') 'Gray'
} else {
    Say ' FAILED: lab address was not applied. Run -Undo and check the log.' 'Red'
    exit 1
}
Say (' Log: ' + $logFile) 'Gray'
Say-Rule

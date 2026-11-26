$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Emit([string]$k, [string]$v) {
    if ($null -eq $v) { $v = '' }
    Write-Output ($k + '=' + $v)
}

function Load-WinRt {
    [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime] | Out-Null
    [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
    [Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
    [Windows.Networking.NetworkOperators.TetheringWiFiBand, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime] | Out-Null
}

function Get-AllProfiles {
    @([Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles())
}

function Resolve-Profile([string]$preferred) {
    $list = Get-AllProfiles
    if ($preferred) {
        foreach ($p in $list) { if ($p.ProfileName -eq $preferred) { return $p } }
    }
    $ip = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    if ($null -ne $ip) { return $ip }
    foreach ($p in $list) {
        if ($p.IsWlanConnectionProfile) { return $p }
    }
    if ($list.Count -gt 0) { return $list[0] }
    return $null
}

function Get-TetheringManager([string]$preferred) {
    $prof = Resolve-Profile $preferred
    if ($null -eq $prof) { throw 'NO_CONNECTION_PROFILE' }
    [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
}

function Wait-State($tm, [string]$target, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        try { $s = [string]$tm.TetheringOperationalState } catch { $s = 'Unknown' }
        if ($s -eq $target) { return $true }
        if ($s -eq 'Unknown') { return $false }
    }
    return $false
}

function Do-Info {
    Load-WinRt
    $prof = Resolve-Profile $env:WH_PROFILE
    Emit 'PROFILE' ($(if ($null -ne $prof) { $prof.ProfileName } else { '' }))
    $tm = Get-TetheringManager $env:WH_PROFILE
    Emit 'STATE' ([string]$tm.TetheringOperationalState)
    try {
        $cfg = $tm.GetCurrentAccessPointConfiguration()
        Emit 'SSID' $cfg.Ssid
        Emit 'KEY' $cfg.Passphrase
        Emit 'BAND' ([string]$cfg.Band)
    } catch { Emit 'CFG_ERR' $_.Exception.Message }
    Emit 'CLIENTS' ([string]$tm.ClientCount)
    Emit 'MAXCLIENTS' ([string]$tm.MaxClientCount)
    try {
        foreach ($c in @($tm.GetTetheringClients())) {
            if ($null -ne $c) { Emit 'CLIENT' (([string]$c.ClientName) + '|' + ([string]$c.HostAddress)) }
        }
    } catch { }
    Emit 'RESULT' 'OK'
}

function Do-Caps {
    Load-WinRt
    $prof = Resolve-Profile $env:WH_PROFILE
    if ($null -eq $prof) { Emit 'CAPABILITY' 'NoProfile'; Emit 'RESULT' 'OK'; return }
    Emit 'PROFILE' $prof.ProfileName
    try {
        $cap = [string][Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::GetTetheringCapabilityFromConnectionProfile($prof)
        Emit 'CAPABILITY' $cap
    } catch {
        Emit 'CAPABILITY' 'Error'
        Emit 'CAP_ERROR' $_.Exception.Message
    }
    try {
        $tm = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($prof)
        Emit 'STATE' ([string]$tm.TetheringOperationalState)
    } catch { Emit 'STATE' 'Unknown' }
    Emit 'RESULT' 'OK'
}

function Do-Profiles {
    Load-WinRt
    $ip = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    $ipName = $(if ($null -ne $ip) { $ip.ProfileName } else { '' })
    Emit 'INTERNET' $ipName
    foreach ($p in Get-AllProfiles) {
        $tag = 'other'
        if ($p.IsWlanConnectionProfile) { $tag = 'wlan' }
        if ($p.IsWwanConnectionProfile) { $tag = 'wwan' }
        Emit 'PROFILE' ($p.ProfileName + '|' + $tag)
    }
    Emit 'RESULT' 'OK'
}

function Do-Start {
    Load-WinRt
    $tm = Get-TetheringManager $env:WH_PROFILE
    $state = [string]$tm.TetheringOperationalState
    if ($state -eq 'On') {
        $tm.StopTetheringAsync() | Out-Null
        Wait-State $tm 'Off' 20 | Out-Null
    }
    try { $tm.DisableNoConnectionsTimeout() } catch { }
    $cfg = New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
    $cfg.Ssid = $env:WH_SSID
    $cfg.Passphrase = $env:WH_KEY
    $band = $env:WH_BAND
    if ($band -and $band -ne 'Auto') {
        try {
            $bandType = [Windows.Networking.NetworkOperators.TetheringWiFiBand]
            $cfg.Band = [Enum]::Parse($bandType, $band)
        } catch { Emit 'BAND_WARN' $_.Exception.Message }
    }
    $tm.ConfigureAccessPointAsync($cfg) | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        try {
            if ([string]$tm.GetCurrentAccessPointConfiguration().Ssid -eq [string]$cfg.Ssid) { break }
        } catch { }
    }
    $tm.StartTetheringAsync() | Out-Null
    if (Wait-State $tm 'On' 45) {
        $c = $tm.GetCurrentAccessPointConfiguration()
        Emit 'SSID' $c.Ssid
        Emit 'KEY' $c.Passphrase
        Emit 'BAND' ([string]$c.Band)
        Emit 'RESULT' 'OK'
    } else {
        Emit 'STATE' ([string]$tm.TetheringOperationalState)
        Emit 'RESULT' 'FAIL'
        Emit 'MSG' 'StartTethering did not reach On state'
    }
}

function Do-Stop {
    Load-WinRt
    $tm = Get-TetheringManager $env:WH_PROFILE
    if ([string]$tm.TetheringOperationalState -ne 'Off') {
        $tm.StopTetheringAsync() | Out-Null
        if (-not (Wait-State $tm 'Off' 25)) {
            Emit 'RESULT' 'FAIL'
            Emit 'MSG' 'StopTethering timeout'
            return
        }
    }
    Emit 'RESULT' 'OK'
}

function Do-Ics {
    $share = New-Object -ComObject HNetCfg.HNetShare
    $privName = $env:WH_PRIVATE
    $pubName = $env:WH_PUBLIC
    $found = 0
    foreach ($conn in @($share.EnumEveryConnection)) {
        $props = $share.NetConnectionProps($conn)
        $name = [string]$props.Name
        if ($name -eq $privName -and $privName) {
            $found++
            $ic = $share.INetSharingConfigurationForINetConnection($conn)
            if ($ic.SharingEnabled) { $ic.DisableSharing() }
            $ic.EnableSharing(0)
            Emit 'PRIVATE' ($name + ' sharing=' + $ic.SharingEnabled)
        } elseif ($name -eq $pubName -and $pubName) {
            $found++
            $ic2 = $share.INetSharingConfigurationForINetConnection($conn)
            if ($ic2.SharingEnabled) { $ic2.DisableSharing() }
            $ic2.EnableSharing(1)
            Emit 'PUBLIC' ($name + ' sharing=' + $ic2.SharingEnabled)
        }
    }
    if ($found -ge 1) { Emit 'RESULT' 'OK' } else { Emit 'RESULT' 'FAIL'; Emit 'MSG' 'NIC_NOT_FOUND' }
}

function Do-IcsOff {
    $share = New-Object -ComObject HNetCfg.HNetShare
    foreach ($conn in @($share.EnumEveryConnection)) {
        $props = $share.NetConnectionProps($conn)
        $name = [string]$props.Name
        if (($name -eq $env:WH_PRIVATE -and $env:WH_PRIVATE) -or ($name -eq $env:WH_PUBLIC -and $env:WH_PUBLIC)) {
            $ic = $share.INetSharingConfigurationForINetConnection($conn)
            if ($ic.SharingEnabled) { $ic.DisableSharing() }
        }
    }
    Emit 'RESULT' 'OK'
}

try {
    switch ($env:WH_ACTION) {
        'caps' { Do-Caps }
        'info' { Do-Info }
        'profiles' { Do-Profiles }
        'start' { Do-Start }
        'stop' { Do-Stop }
        'ics' { Do-Ics }
        'icsoff' { Do-IcsOff }
        default { Emit 'RESULT' 'FAIL'; Emit 'MSG' ('UNKNOWN_ACTION:' + $env:WH_ACTION) }
    }
} catch {
    Emit 'RESULT' 'FAIL'
    Emit 'ERRTYPE' $_.Exception.GetType().Name
    Emit 'MSG' ($_.Exception.Message -replace "`r`n", ' | ' -replace "`n", ' | ')
}

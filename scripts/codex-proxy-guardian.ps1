<#
.SYNOPSIS
    Codex 代理守护脚本 v2（健壮版）
.DESCRIPTION
    检测 FlClash（127.0.0.1:9090 Clash API）并维护 Codex 的代理配置。
    - 代理在线时：设置用户级环境变量 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY = http://127.0.0.1:<mixed-port>
    - 代理下线时：清空环境变量、关闭残留系统代理，让境内直连 API 正常访问
    - 常驻循环：默认每 35 秒检测，连续 90 秒失败判定下线
    - FlClash 频繁开关：上线即时恢复，下线有时间窗口滞后保护
    - 国内 API 直连白名单：DeepSeek / 通义 / Moonshot 等不走代理
    - 日志防写爆：硬上限 + 轮转，异常时不会无限写盘
    - 单轮容错：每轮检测失败记录并继续，不退出守护

    使用方式：
        .\codex-proxy-guardian.ps1 -Test -DryRun   # 只检测不修改
        .\codex-proxy-guardian.ps1 -Test            # 单次检测并应用
        无参数                                # 常驻守护模式（由计划任务调用）

    停用/卸载：
        Stop-ScheduledTask -TaskName "CodexProxyDaemon"
        或运行 .\uninstall-daemon.ps1 -ClearEnv -DisableSystemProxy
#>

[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# 路径
$script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:Root      = Split-Path -Parent $script:ScriptDir
$script:ConfigPath = Join-Path (Join-Path $script:Root 'config') 'daemon.config.json'
$script:StatePath  = Join-Path $script:Root 'state.json'
$script:LogDir     = Join-Path $script:Root 'logs'

# 运行时状态
$script:Config      = $null
$script:failCount   = 0
$script:firstFailAt = [datetime]::MinValue
$script:lastUp      = $true
$script:seenUp        = $false
$script:DaemonVersion = '2.4.1'
$script:configLastWrite = [datetime]::MinValue
$script:lastPort    = 0
$script:lastNode    = ''
$script:lastNodeLogTime = [datetime]::MinValue
$script:logQuietUntil   = [datetime]::MinValue
$script:logFailStreak   = 0
$script:lastLogMain     = [datetime]::MinValue

# 默认配置
$script:DefaultConfig = @{
    clashApiUrl           = 'http://127.0.0.1:9090'
    clashApiSecret        = ''
    pollIntervalSeconds   = 35
    requestTimeoutSeconds = 8
    downThreshold         = 3
    downSeconds           = 90
    proxyTestUrl          = 'https://www.gstatic.com/generate_204'
    proxyTestUrls         = @('https://www.gstatic.com/generate_204', 'https://cp.cloudflare.com/generate_204', 'https://www.google.com/generate_204')
    noProxy               = 'localhost,127.*,10.*,192.168.*,*.local'
    proxyOverride         = 'localhost;*.local;127.*;10.*;192.168.*'
    directDomains         = @('*.deepseek.com', '*.qwen.ai', '*.dashscope.aliyuncs.com', '*.moonshot.cn', '*.bigmodel.cn', '*.siliconflow.cn', '*.minimaxi.com', '*.api.volces.com', '*.xfyun.cn', '*.stepfun.com', '*.lingyiwanwu.com', '*.baichuan-ai.com')
    nodeLogCooldownSeconds = 60
    maxLogBytes           = 2097152
    maxLogFiles           = 3
    logMinFreeMB          = 512
    logCleanupFreeMB      = 2048
    maxLogTotalMB         = 200
}

$script:DefaultLogFile = Join-Path $script:LogDir 'daemon.log'

# Win32 广播
if (-not ('CodexDaemon.Native' -as [type])) {
    $memberDef = @"
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
"@
    Add-Type -Namespace CodexDaemon -Name Native -MemberDefinition $memberDef
}

function Get-LogFreeMB {
    # 返回日志所在磁盘剩余空间（MB）；无法获取时返回 $null
    $logRoot = Split-Path -Qualifier $script:LogDir
    if (-not $logRoot) { return $null }
    $drive = Get-PSDrive -Name ($logRoot.TrimEnd(':').TrimEnd('\')) -ErrorAction SilentlyContinue
    if ($null -eq $drive -or -not $drive.Free) { return $null }
    return [long]($drive.Free / 1MB)
}

function Invoke-LogCleanup {
    # 磁盘空间低时自动清理：删除本守护产生的临时文件与最旧轮转日志，尽量恢复空间。
    # 只删自己管理范围内的文件，绝不碰用户数据。
    try {
        $freeMB = Get-LogFreeMB
        if ($null -eq $freeMB) { return }
        $cleanupMB = 2048
        if ($null -ne $script:Config) {
            $cleanupMB = [int]$script:Config.logCleanupFreeMB
        }
        if ($cleanupMB -lt 1) { $cleanupMB = 2048 }
        if ($freeMB -ge $cleanupMB) { return }
        # 1) 本守护产生的临时文件
        Get-ChildItem -Path $script:LogDir -Filter '*.tmp' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        $tmpState = "$script:StatePath.tmp"
        if (Test-Path -LiteralPath $tmpState) { Remove-Item -LiteralPath $tmpState -Force -ErrorAction SilentlyContinue }
        # 2) 从最旧轮转日志开始删除，直到空间恢复或无可删
        $maxFiles = 3
        if ($null -ne $script:Config) { $maxFiles = [int]$script:Config.maxLogFiles }
        if ($maxFiles -lt 1) { $maxFiles = 1 }
        for ($i = $maxFiles; $i -ge 1; $i--) {
            $freeMB = Get-LogFreeMB
            if ($null -eq $freeMB -or $freeMB -ge $cleanupMB) { break }
            $f = "$script:DefaultLogFile.$i"
            if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
        }
    } catch { }
}

function Write-Log {
    param([string]$Message)
    $now = [datetime]::Now
    if ($now -lt $script:logQuietUntil) { return }
    if ($script:logFailStreak -ge 20) {
        $script:logQuietUntil = $now.AddSeconds(300)
        $script:logFailStreak = 0
        return
    }
    $ts = $now.ToString('yyyy-MM-dd HH:mm:ss')
    $line = "$ts $Message"
    try {
        # 磁盘空间防护（30 秒节流，避免每次写日志都枚举磁盘）：
        # 低于清理阈值先自动清理旧日志/临时文件；仍低于停止阈值则跳过写日志
        if ($null -eq $script:lastLogMain) { $script:lastLogMain = [datetime]::MinValue }
        $doMaintenance = ($now - $script:lastLogMain).TotalSeconds -ge 30
        if ($doMaintenance) {
            $minFreeMB = 512
            $cleanupFreeMB = 2048
            if ($null -ne $script:Config) {
                $minFreeMB = [int]$script:Config.logMinFreeMB
                $cleanupFreeMB = [int]$script:Config.logCleanupFreeMB
            }
            if ($minFreeMB -lt 1) { $minFreeMB = 512 }
            if ($cleanupFreeMB -lt 1) { $cleanupFreeMB = 2048 }
            $freeMB = Get-LogFreeMB
            if ($null -ne $freeMB) {
                if ($freeMB -lt $cleanupFreeMB) { Invoke-LogCleanup }
                $freeMB = Get-LogFreeMB
                if ($null -ne $freeMB -and $freeMB -lt $minFreeMB) {
                    $script:lastLogMain = $now
                    return
                }
            }
            $script:lastLogMain = $now
        }
        if (-not (Test-Path $script:LogDir)) {
            New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
        }
        $logFile = $script:DefaultLogFile
        $maxBytes = 2097152
        $maxFiles = 3
        if ($null -ne $script:Config) {
            $maxBytes = [int]$script:Config.maxLogBytes
            $maxFiles = [int]$script:Config.maxLogFiles
        }
        if ($maxBytes -lt 102400) { $maxBytes = 102400 }
        if ($maxFiles -lt 1) { $maxFiles = 1 }
        if ($maxFiles -gt 10) { $maxFiles = 10 }
        if (Test-Path $logFile) {
            $len = (Get-Item $logFile).Length
            if ($len -gt $maxBytes) {
                # 标准移位轮转：先删最旧（.maxFiles），再把 .N-1 -> .N ... .1 -> .2，主文件 -> .1
                $oldest = "$logFile.$maxFiles"
                if (Test-Path -LiteralPath $oldest) { Remove-Item -LiteralPath $oldest -Force }
                for ($i = $maxFiles - 1; $i -ge 1; $i--) {
                    $src = "$logFile.$i"
                    if (Test-Path -LiteralPath $src) { Move-Item -LiteralPath $src -Destination "$logFile.$($i+1)" -Force }
                }
                Move-Item -LiteralPath $logFile -Destination "$logFile.1" -Force
            }
        }
        Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
        # 总量硬上限（与磁盘检查同节流）：即使配置异常，所有日志文件合计也不超过 maxLogTotalMB
        if ($doMaintenance) {
            $totalMB = 200
            if ($null -ne $script:Config) { $totalMB = [int]$script:Config.maxLogTotalMB }
            if ($totalMB -lt 64) { $totalMB = 200 }
            $capBytes = $totalMB * 1MB
            $totalBytes = 0
            $allLogs = @(Get-ChildItem -Path $script:LogDir -Filter 'daemon.log*' -File -ErrorAction SilentlyContinue)
            foreach ($f in $allLogs) { $totalBytes += $f.Length }
            if ($totalBytes -gt $capBytes) {
                # 从最旧轮转文件开始删，直到总量低于上限；主日志始终保留
                for ($i = $maxFiles; $i -ge 1; $i--) {
                    $f = "$logFile.$i"
                    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
                    $totalBytes = 0
                    foreach ($g in @(Get-ChildItem -Path $script:LogDir -Filter 'daemon.log*' -File -ErrorAction SilentlyContinue)) { $totalBytes += $g.Length }
                    if ($totalBytes -le $capBytes) { break }
                }
            }
        }
        $script:logFailStreak = 0
    } catch {
        $script:logFailStreak++
        if ($script:logFailStreak -ge 20) {
            $script:logQuietUntil = [datetime]::Now.AddSeconds(300)
            $script:logFailStreak = 0
        }
    }
}
function Clamp-Int {
    param($Value, [int]$Min, [int]$Max)
    try { $v = [int]$Value } catch { return $Min }
    if ($v -lt $Min) { return $Min }
    if ($v -gt $Max) { return $Max }
    return $v
}

function Get-DaemonConfig {
    $cfg = @{}
    foreach ($k in $script:DefaultConfig.Keys) { $cfg[$k] = $script:DefaultConfig[$k] }
    if (Test-Path -LiteralPath $script:ConfigPath) {
        try {
            $loaded = Get-Content -Raw -LiteralPath $script:ConfigPath | ConvertFrom-Json
            foreach ($k in @($cfg.Keys)) {
                $v = $loaded.$k
                if ($null -ne $v) { $cfg[$k] = $v }
            }
        } catch {
            Write-Log "配置读取失败，使用默认配置: $($_.Exception.Message)"
        }
    }
    # 数值钳制，防止配置写错导致异常
    $cfg['pollIntervalSeconds']    = Clamp-Int $cfg['pollIntervalSeconds'] 5 600
    $cfg['requestTimeoutSeconds']  = Clamp-Int $cfg['requestTimeoutSeconds'] 2 30
    $cfg['downSeconds']            = Clamp-Int $cfg['downSeconds'] 15 600
    $cfg['nodeLogCooldownSeconds'] = Clamp-Int $cfg['nodeLogCooldownSeconds'] 5 3600
    $cfg['maxLogBytes']            = Clamp-Int $cfg['maxLogBytes'] 102400 10485760
    $cfg['maxLogFiles']            = Clamp-Int $cfg['maxLogFiles'] 1 10
    $cfg['logMinFreeMB']           = Clamp-Int $cfg['logMinFreeMB'] 64 65536
    $cleanupMB                     = Clamp-Int $cfg['logCleanupFreeMB'] 256 65536
    if ($cleanupMB -lt $cfg['logMinFreeMB']) { $cleanupMB = $cfg['logMinFreeMB'] }
    $cfg['logCleanupFreeMB']       = $cleanupMB
    $cfg['maxLogTotalMB']          = Clamp-Int $cfg['maxLogTotalMB'] 64 2048
    # proxyTestUrls 统一为数组
    $urls = @()
    foreach ($u in @($cfg['proxyTestUrls'])) {
        $us = [string]$u
        if ($us.Trim() -ne '' -and $urls -notcontains $us.Trim()) { $urls += $us.Trim() }
    }
    if ($urls.Count -eq 0) {
        $single = [string]$cfg['proxyTestUrl']
        if ($single.Trim() -ne '') { $urls += $single.Trim() }
    }
    $cfg['proxyTestUrls'] = $urls
    # 记录配置文件的修改时间，供热重载比较
    if (Test-Path -LiteralPath $script:ConfigPath) {
        $script:configLastWrite = (Get-Item -LiteralPath $script:ConfigPath).LastWriteTime
    }

    # 合并 directDomains 到 NO_PROXY / ProxyOverride
    $rawDirect = $cfg['directDomains']
    if ($rawDirect -is [System.Array]) { $directList = @($rawDirect) }
    elseif ($null -ne $rawDirect -and [string]$rawDirect -ne '') { $directList = @(([string]$rawDirect) -split ',') }
    else { $directList = @() }
    $direct = @()
    foreach ($d in $directList) {
        $s = ([string]$d).Trim()
        if ($s -ne '' -and $direct -notcontains $s) { $direct += $s }
    }
    $cfg['directDomains'] = $direct
    $noProxy = @()
    foreach ($p in (([string]$cfg['noProxy']) -split ',')) {
        $s = $p.Trim()
        if ($s -ne '' -and $noProxy -notcontains $s) { $noProxy += $s }
    }
    $cfg['noProxy'] = (($noProxy + $direct) | Select-Object -Unique) -join ','
    $override = @()
    foreach ($p in (([string]$cfg['proxyOverride']) -split ';')) {
        $s = $p.Trim()
        if ($s -ne '' -and $override -notcontains $s) { $override += $s }
    }
    $cfg['proxyOverride'] = (($override + $direct) | Select-Object -Unique) -join ';'
    return $cfg
}

function Reload-ConfigIfChanged {
    # 配置文件 mtime 变化则热重载，无需重启守护
    if (Test-Path -LiteralPath $script:ConfigPath) {
        $t = (Get-Item -LiteralPath $script:ConfigPath).LastWriteTime
        if ($t -gt $script:configLastWrite) {
            $script:Config = Get-DaemonConfig
            Write-Log "配置已热重载 (poll=$($script:Config.pollIntervalSeconds)s, downSeconds=$($script:Config.downSeconds)s, urls=$($script:Config.proxyTestUrls.Count))"
            return $true
        }
    }
    return $false
}
function Invoke-ClashApi {
    param([string]$Path)
    $uri = "$($script:Config.clashApiUrl)$Path"
    try {
        $req = [System.Net.HttpWebRequest]::Create($uri)
        $req.Timeout = ([int]$script:Config.requestTimeoutSeconds * 1000)
        $req.Accept = 'application/json'
        $secret = [string]$script:Config.clashApiSecret
        if ($secret -ne '') {
            $req.Headers.Add('Authorization', ('Bearer ' + $secret))
        }
        $resp = $req.GetResponse()
        try {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
            $json = $reader.ReadToEnd()
            $reader.Dispose()
        } finally {
            $resp.Close()
        }
        return ($json | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Test-ProxyHealth {
    param([int]$Port)
    if ($Port -le 0) { return $false }
    # 任一探活 URL 成功即判定隧道可用，降低单点 URL 被墙/抽风导致的误判
    $urls = @($script:Config.proxyTestUrls)
    if ($urls.Count -eq 0) {
        $single = [string]$script:Config.proxyTestUrl
        if ($single -ne '') { $urls = @($single) }
    }
    foreach ($u in $urls) {
        try {
            $r = Invoke-WebRequest -Uri ([string]$u) -Proxy "http://127.0.0.1:$Port" -TimeoutSec ([int]$script:Config.requestTimeoutSeconds) -UseBasicParsing
            if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500) { return $true }
        } catch {
            # 尝试下一个 URL
        }
    }
    return $false
}

function Get-DetectedState {
    param([switch]$UseGrace)
    $cfg = $script:Config
    $state = [PSCustomObject]@{
        Up     = $false
        Port   = 0
        Node   = ''
        Mode   = ''
        ApiOk  = $false
        Health = $false
    }
    $configs = Invoke-ClashApi '/configs'
    if ($null -ne $configs) {
        $state.ApiOk = $true
        $state.Mode = [string]$configs.mode
        $state.Port = [int]$configs.'mixed-port'
        $state.Health = (Test-ProxyHealth -Port $state.Port)
        if ($state.Health) {
            $proxies = Invoke-ClashApi '/proxies'
            if ($null -ne $proxies -and $null -ne $proxies.proxies) {
                $g = $proxies.proxies.'GLOBAL'
                if ($null -ne $g) { $state.Node = [string]$g.now }
            }
        }
    } else {
        $state.Mode = 'api-unreachable'
    }
    if ($UseGrace) {
        # 时间窗判定：连续 downSeconds 秒失败才判定下线，避免 FlClash 频繁开关误清配置
        if ($state.ApiOk -and $state.Health) {
            $script:seenUp = $true
            $script:firstFailAt = [datetime]::MinValue
            $state.Up = $true
        } else {
            if (-not $script:seenUp) {
                # 本会话从未观测到代理在线（如开机时 FlClash 尚未启动）：立即判下线，不残留死代理配置
                $state.Up = $false
                $script:firstFailAt = [datetime]::MinValue
            } elseif ($script:firstFailAt -eq [datetime]::MinValue) {
                # 首次失败：进入宽限期，保持在线
                $script:firstFailAt = [datetime]::Now
                $state.Up = $true
            } else {
                $elapsed = ([datetime]::Now - $script:firstFailAt).TotalSeconds
                if ($elapsed -ge [double]$cfg.downSeconds) {
                    $state.Up = $false
                } else {
                    $state.Up = $true
                }
            }
        }
    } else {
        $state.Up = ($state.ApiOk -and $state.Health)
    }
    return $state
}

function Get-UserEnvProxyState {
    $result = @{}
    foreach ($v in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
        $val = [Environment]::GetEnvironmentVariable($v, 'User')
        if ($null -eq $val) { $val = '' }
        $result[$v] = $val
    }
    return $result
}

function Get-EffectivePort {
    param([bool]$Up, [int]$Port)
    # 宽限期内 API 可能暂时不可达（Port=0），沿用上一个已知端口，
    # 避免把 http://127.0.0.1:0 写入环境变量/系统代理
    $eff = $Port
    if ($Up -and $eff -le 0 -and $script:lastPort -gt 0) { $eff = $script:lastPort }
    return $eff
}

function Get-DesiredEnvState {
    param([bool]$Up, [int]$Port)
    $desired = @{}
    $effPort = Get-EffectivePort -Up $Up -Port $Port
    if ($Up -and $effPort -gt 0) {
        $proxy = "http://127.0.0.1:$effPort"
        $desired['HTTP_PROXY']  = $proxy
        $desired['HTTPS_PROXY'] = $proxy
        $desired['ALL_PROXY']   = $proxy
        $desired['NO_PROXY']    = [string]$script:Config.noProxy
    } else {
        foreach ($v in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
            $desired[$v] = ''
        }
    }
    return $desired
}

function Broadcast-SettingsChanged {
    param([string]$Section)
    try {
        $HWND_BROADCAST = [IntPtr]0xffff
        $WM_SETTINGCHANGE = 0x001A
        $SMTO_ABORTIFHUNG = 0x0002
        $result = [UIntPtr]::Zero
        [void][CodexDaemon.Native]::SendMessageTimeout($HWND_BROADCAST, $WM_SETTINGCHANGE, [UIntPtr]::Zero, $Section, $SMTO_ABORTIFHUNG, 5000, [ref]$result)
    } catch {
        Write-Log "广播设置变更失败: $($_.Exception.Message)"
    }
}

function Set-UserEnvState {
    param($Desired)
    $changed = $false
    $current = Get-UserEnvProxyState
    foreach ($v in $Desired.Keys) {
        $cur = $current[$v]
        $new = $Desired[$v]
        if (($new -eq '' -and $cur -ne '') -or ($new -ne '' -and $cur -ne $new)) {
            if ($new -eq '') {
                [Environment]::SetEnvironmentVariable($v, $null, 'User')
            } else {
                [Environment]::SetEnvironmentVariable($v, $new, 'User')
            }
            $changed = $true
        }
    }
    if ($changed) {
        Broadcast-SettingsChanged -Section 'Environment'
    }
    return $changed
}

function Get-WinInetState {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $p = Get-ItemProperty -Path $key
    return [PSCustomObject]@{
        ProxyEnable   = [int]$p.ProxyEnable
        ProxyServer   = [string]$p.ProxyServer
        ProxyOverride = [string]$p.ProxyOverride
    }
}

function Merge-OverrideList {
    param([string]$Current, [string]$Want)
    $have = @()
    foreach ($p in ($Current -split ';')) {
        $s = $p.Trim()
        if ($s -ne '' -and $have -notcontains $s) { $have += $s }
    }
    $wantList = @()
    foreach ($p in ($Want -split ';')) {
        $s = $p.Trim()
        if ($s -ne '' -and $wantList -notcontains $s) { $wantList += $s }
    }
    $missing = @($wantList | Where-Object { $have -notcontains $_ })
    if ($missing.Count -eq 0) { return $Current }
    return (($have + $missing) | Select-Object -Unique) -join ';'
}
function Set-WinInetState {
    param([bool]$Up, [int]$Port)
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $current = Get-WinInetState
    $changed = $false
    $effPort = Get-EffectivePort -Up $Up -Port $Port
    if ($Up -and $effPort -gt 0) {
        $server = "127.0.0.1:$effPort"
        if ($current.ProxyEnable -ne 1 -or $current.ProxyServer -ne $server) {
            Set-ItemProperty -Path $key -Name ProxyEnable -Value 1
            Set-ItemProperty -Path $key -Name ProxyServer -Value $server
            $changed = $true
        }
        # 保留用户既有的绕过列表，仅追加缺失的默认/直连条目
        $merged = Merge-OverrideList -Current $current.ProxyOverride -Want ([string]$script:Config.proxyOverride)
        if ($merged -ne $current.ProxyOverride) {
            Set-ItemProperty -Path $key -Name ProxyOverride -Value $merged
            $changed = $true
        }
    } else {
        # 只关闭我们自己写过的 127.0.0.1 系统代理，不碰用户其他代理设置
        if ($current.ProxyEnable -eq 1 -and $current.ProxyServer -match '^127\.0\.0\.1:\d+$') {
            Set-ItemProperty -Path $key -Name ProxyEnable -Value 0
            $changed = $true
        }
    }
    if ($changed) {
        Broadcast-SettingsChanged -Section 'Internet Settings'
    }
    return $changed
}

function Update-StateFile {
    param($State, [string]$Message, [bool]$EnvChanged, [bool]$InetChanged)
    $obj = [PSCustomObject]@{
        version            = $script:DaemonVersion
        updatedAt          = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        proxyUp            = $State.Up
        port               = (Get-EffectivePort -Up $State.Up -Port $State.Port)
        node               = $State.Node
        mode               = $State.Mode
        envChanged         = $EnvChanged
        systemProxyChanged = $InetChanged
        message            = $Message
        nextCheck          = (Get-Date).AddSeconds([int]$script:Config.pollIntervalSeconds).ToString('yyyy-MM-dd HH:mm:ss')
    }
    try {
        $obj | ConvertTo-Json | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
    } catch {
        Write-Log "state.json 写入失败: $($_.Exception.Message)"
    }
}

function Invoke-ApplyProxyState {
    param($State)
    $effPort = Get-EffectivePort -Up $State.Up -Port $State.Port
    $envChanged = Set-UserEnvState -Desired (Get-DesiredEnvState -Up $State.Up -Port $State.Port)
    $inetChanged = Set-WinInetState -Up $State.Up -Port $State.Port
    $now = [datetime]::Now

    if ($State.Up) {
        if (-not $script:lastUp) {
            Write-Log "代理上线：端口 $effPort，节点 $($State.Node)，模式 $($State.Mode)"
        } elseif ($script:lastPort -gt 0 -and $script:lastPort -ne $effPort) {
            Write-Log "代理端口变化：$script:lastPort -> $effPort"
        } elseif ($script:lastNode -ne '' -and $script:lastNode -ne $State.Node -and ($now - $script:lastNodeLogTime).TotalSeconds -ge [double]$script:Config.nodeLogCooldownSeconds) {
            Write-Log "代理节点变化：$script:lastNode -> $($State.Node)"
            $script:lastNodeLogTime = $now
        }
    } else {
        if ($script:lastUp) {
            Write-Log '代理下线（检测失败），已清空代理配置'
        }
    }
    if ($envChanged) {
        if ($State.Up) {
            Write-Log "已应用用户环境变量代理: http://127.0.0.1:$effPort"
        } else {
            Write-Log '已清空用户环境变量代理'
        }
    }
    if ($inetChanged) {
        if ($State.Up) {
            Write-Log '已开启系统代理'
        } else {
            Write-Log '已关闭残留系统代理'
        }
    }

    $codexRunning = ($null -ne (Get-Process -Name 'codex' -ErrorAction SilentlyContinue))
    if ($State.Up) {
        if ($State.ApiOk -and $State.Health) {
            $message = "proxy up, port=$effPort, node=$($State.Node)"
        } else {
            $message = "proxy holding (grace), health check failing"
        }
    } else {
        $message = 'proxy down, cleared'
    }
    if (($envChanged -or $inetChanged) -and $codexRunning) {
        $message += ' | Codex 正在运行，重启后生效'
        Write-Log 'Codex 正在运行，代理配置已更新，重启 Codex 后生效'
    }
    Update-StateFile -State $State -Message $message -EnvChanged $envChanged -InetChanged $inetChanged

    $script:lastUp = $State.Up
    $script:lastPort = $State.Port
    $script:lastNode = $State.Node
}

$script:Config = Get-DaemonConfig

if ($Test) {
    $state = Get-DetectedState
    if ($DryRun) {
        "DRY-RUN up=$($state.Up) port=$($state.Port) node=$($state.Node) mode=$($state.Mode) health=$($state.Health)"
    } else {
        Invoke-ApplyProxyState -State $state
        "APPLIED up=$($state.Up) port=$($state.Port) node=$($state.Node)"
    }
    exit
}

$mutex = New-Object System.Threading.Mutex($false, 'CodexProxyDaemonMutex')
if (-not $mutex.WaitOne(0)) {
    exit
}
try {
    Write-Log "守护脚本启动 v$($script:DaemonVersion) (poll=$($script:Config.pollIntervalSeconds)s, downSeconds=$($script:Config.downSeconds)s, directDomains=$([string]::Join(',', @($script:Config.directDomains))))"
    while ($true) {
        try {
            Reload-ConfigIfChanged | Out-Null
            Invoke-LogCleanup
            $state = Get-DetectedState -UseGrace
            Invoke-ApplyProxyState -State $state
        } catch {
            Write-Log "本轮检测异常（继续运行）: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds ([int]$script:Config.pollIntervalSeconds)
    }
} catch {
    $errMsg = $_.Exception.ToString()
    Write-Log "未处理异常，守护进程退出: $errMsg"
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        [System.Windows.Forms.MessageBox]::Show(
            "守护脚本出错: $($_.Exception.Message)`n`n日志路径: $script:DefaultLogFile",
            'Codex 代理守护 - 崩溃',
            'OK',
            'Error'
        ) | Out-Null
    } catch {
        Write-Log "崩溃弹窗失败: $($_.Exception.Message)"
    }
    exit 1
} finally {
    try { $mutex.ReleaseMutex() } catch {}
}

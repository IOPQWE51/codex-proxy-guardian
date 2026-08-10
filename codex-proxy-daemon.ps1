[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:ConfigPath = Join-Path $script:Root 'daemon.config.json'
$script:StatePath = Join-Path $script:Root 'state.json'
$script:LogDir = Join-Path $script:Root 'logs'
$script:Config = $null
$script:failCount = 0
$script:lastUp = $true
$script:lastPort = 0
$script:lastNode = ''

$script:DefaultConfig = @{
    clashApiUrl         = 'http://127.0.0.1:9090'
    pollIntervalSeconds = 35
    requestTimeoutSeconds = 8
    downThreshold       = 3
    proxyTestUrl        = 'https://www.gstatic.com/generate_204'
    noProxy             = 'localhost,127.*,10.*,192.168.*,*.local'
    proxyOverride       = 'localhost;*.local;127.*;10.*;192.168.*'
    maxLogBytes         = 2097152
    maxLogFiles         = 3
}

# 供 Write-Log 在配置尚未加载时使用的默认日志路径
$script:DefaultLogFile = Join-Path $script:LogDir 'daemon.log'

if (-not ('CodexDaemon.Native' -as [type])) {
    Add-Type -Namespace CodexDaemon -Name Native -MemberDefinition @"
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
"@
}

function Write-Log {
    param([string]$Message)
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "$ts $Message"
    try {
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
        if (Test-Path $logFile) {
            $len = (Get-Item $logFile).Length
            if ($len -gt $maxBytes) {
                for ($i = $maxFiles; $i -ge 1; $i--) {
                    $old = "$logFile.$i"
                    if (Test-Path $old) { Remove-Item -LiteralPath $old -Force }
                }
                for ($i = $maxFiles - 1; $i -ge 1; $i--) {
                    $src = "$logFile.$i"
                    if (Test-Path $src) { Move-Item -LiteralPath $src -Destination "$logFile.$($i+1)" -Force }
                }
                Move-Item -LiteralPath $logFile -Destination "$logFile.1" -Force
            }
        }
        Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
    } catch {
        # 日志不可写时静默，避免递归报错
    }
}

function Get-DaemonConfig {
    $cfg = $script:DefaultConfig.Clone()
    if (Test-Path $script:ConfigPath) {
        try {
            $loaded = Get-Content -Raw -LiteralPath $script:ConfigPath | ConvertFrom-Json
            foreach ($p in @($cfg.Keys)) {
                $v = $loaded.$p
                if ($null -ne $v) { $cfg[$p] = $v }
            }
        } catch {
            Write-Log "配置读取失败，使用默认配置: $($_.Exception.Message)"
        }
    }
    return $cfg
}

function Invoke-ClashApi {
    param([string]$Path)
    $uri = "$($script:Config.clashApiUrl)$Path"
    try {
        $req = [System.Net.HttpWebRequest]::Create($uri)
        $req.Timeout = ([int]$script:Config.requestTimeoutSeconds * 1000)
        $req.Accept = 'application/json'
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
    try {
        $r = Invoke-WebRequest -Uri $script:Config.proxyTestUrl -Proxy "http://127.0.0.1:$Port" -TimeoutSec $script:Config.requestTimeoutSeconds -UseBasicParsing
        return ($r.StatusCode -eq 204)
    } catch {
        return $false
    }
}

function Get-DetectedState {
    param([switch]$UseThreshold)
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
    if ($UseThreshold) {
        if ($state.ApiOk -and $state.Health) {
            $script:failCount = 0
            $state.Up = $true
        } else {
            $script:failCount++
            if ($script:failCount -ge $cfg.downThreshold) {
                $state.Up = $false
            } else {
                $state.Up = $script:lastUp
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

function Get-DesiredEnvState {
    param([bool]$Up, [int]$Port)
    $desired = @{}
    if ($Up -and $Port -gt 0) {
        $proxy = "http://127.0.0.1:$Port"
        $desired['HTTP_PROXY'] = $proxy
        $desired['HTTPS_PROXY'] = $proxy
        $desired['ALL_PROXY'] = $proxy
        $desired['NO_PROXY'] = [string]$script:Config.noProxy
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

function Set-WinInetState {
    param([bool]$Up, [int]$Port)
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $current = Get-WinInetState
    $changed = $false
    if ($Up -and $Port -gt 0) {
        $server = "127.0.0.1:$Port"
        if ($current.ProxyEnable -ne 1 -or $current.ProxyServer -ne $server) {
            Set-ItemProperty -Path $key -Name ProxyEnable -Value 1
            Set-ItemProperty -Path $key -Name ProxyServer -Value $server
            $changed = $true
        }
        if ($current.ProxyOverride -eq '') {
            Set-ItemProperty -Path $key -Name ProxyOverride -Value $script:Config.proxyOverride
        }
    } else {
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
        updatedAt          = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        proxyUp            = $State.Up
        port               = $State.Port
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
    $envChanged = Set-UserEnvState -Desired (Get-DesiredEnvState -Up $State.Up -Port $State.Port)
    $inetChanged = Set-WinInetState -Up $State.Up -Port $State.Port

    if ($State.Up) {
        if (-not $script:lastUp) {
            Write-Log "代理上线：端口 $($State.Port)，节点 $($State.Node)，模式 $($State.Mode)"
        } elseif ($script:lastPort -ne $State.Port) {
            Write-Log "代理端口变化：$script:lastPort -> $($State.Port)"
        } elseif ($script:lastNode -ne $State.Node) {
            Write-Log "代理节点变化：$script:lastNode -> $($State.Node)"
        }
    } else {
        if ($script:lastUp) {
            Write-Log '代理下线（连续失败达到阈值），已清空代理配置'
        }
    }
    if ($envChanged) {
        if ($State.Up) {
            Write-Log "已应用用户环境变量代理: http://127.0.0.1:$($State.Port)"
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
        $message = "proxy up, port=$($State.Port), node=$($State.Node)"
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

Write-Log "守护脚本启动 (poll=$($script:Config.pollIntervalSeconds)s, threshold=$($script:Config.downThreshold))"

$mutex = New-Object System.Threading.Mutex($false, 'CodexProxyDaemonMutex')
if (-not $mutex.WaitOne(0)) {
    Write-Log '已有实例在运行，本实例退出'
    exit
}

try {
    $state = Get-DetectedState -UseThreshold
    Invoke-ApplyProxyState -State $state
    while ($true) {
        Start-Sleep -Seconds $script:Config.pollIntervalSeconds
        $state = Get-DetectedState -UseThreshold
        Invoke-ApplyProxyState -State $state
    }
} finally {
    $mutex.ReleaseMutex()
}
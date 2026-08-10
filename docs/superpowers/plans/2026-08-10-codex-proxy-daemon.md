# Codex 代理守护脚本 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现一个开机自启、静默运行的 PowerShell 守护脚本，每 35 秒检测 FlClash（`127.0.0.1:9090` Clash API），自动维护用户级代理环境变量与 WinINET 系统代理，确保 Codex 始终连接当前代理；FlClash 下线时自动清空代理配置，让 DeepSeek 可直连；不自动重启 Codex。

**Architecture:** 单目录小项目：`codex-proxy-daemon.ps1`（检测 + 应用 + 常驻循环，支持 `-Test`/`-DryRun` 单次模式）、`daemon.config.json`（可调参数）、`install-daemon.ps1`/`uninstall-daemon.ps1`（注册/移除登录时计划任务）、`logs/daemon.log`（轮转日志）、`state.json`（当前状态）。计划任务以 `powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass` 方式静默启动。

**Tech Stack:** Windows PowerShell 5.1（`powershell.exe`）、Windows 计划任务（Scheduled Tasks）、FlClash Clash API（REST/JSON）、HKCU 注册表（环境变量 + WinINET Internet Settings）。

---

## 文件结构

```text
codex-proxy-daemon/                        # 项目根（已 git init）
├── codex-proxy-daemon.ps1                 # 主守护脚本（Task 2 创建）
├── install-daemon.ps1                     # 注册计划任务（Task 7 创建）
├── uninstall-daemon.ps1                   # 移除计划任务（Task 9 创建）
├── daemon.config.json                     # 配置（Task 1 创建）
├── README.md                              # 使用说明（Task 1 骨架，Task 10 完善）
├── state.json                             # 运行时状态（脚本维护，gitignore）
├── logs/daemon.log                        # 运行时日志（脚本维护，gitignore）
└── docs/superpowers/                      # 设计文档与计划
```

## 前置说明

- 所有命令在项目根目录 `G:\AGENT\proxy\codex-proxy-daemon` 下执行。
- 所有验证基于当前真实环境：FlClash 在线、mixed 端口 7890、Clash API `http://127.0.0.1:9090` 免鉴权。
- 验证命令会短暂修改当前用户环境变量与 HKCU 注册表，均为可逆操作，每步都提供恢复命令。

---

### Task 1: 创建配置与 README 骨架

**Files:**
- Create: `daemon.config.json`
- Create: `README.md`

- [ ] **Step 1: 创建 `daemon.config.json`**

```json
{
  "clashApiUrl": "http://127.0.0.1:9090",
  "pollIntervalSeconds": 35,
  "requestTimeoutSeconds": 8,
  "downThreshold": 3,
  "proxyTestUrl": "https://www.gstatic.com/generate_204",
  "noProxy": "localhost,127.*,10.*,192.168.*,*.local",
  "proxyOverride": "localhost;*.local;127.*;10.*;192.168.*",
  "maxLogBytes": 2097152,
  "maxLogFiles": 3
}
```

- [ ] **Step 2: 创建 `README.md`（骨架，Task 10 完善）**

```markdown
# Codex 代理守护脚本

检测 FlClash 并维护 Codex 的代理配置（用户级环境变量 + 系统代理）。

详见 `docs/superpowers/specs/2026-08-10-codex-proxy-daemon-design.md`。
```

- [ ] **Step 3: 提交**

```bash
git add daemon.config.json README.md
git commit -m "chore: 初始化配置与 README"
```

Expected: commit 成功。

---

### Task 2: 实现主守护脚本

**Files:**
- Create: `codex-proxy-daemon.ps1`

- [ ] **Step 1: 创建完整脚本 `codex-proxy-daemon.ps1`**

```powershell
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
    Add-Type -Namespace CodexDaemon -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
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
            foreach ($p in $cfg.Keys) {
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
        return (Invoke-RestMethod -Uri $uri -TimeoutSec $script:Config.requestTimeoutSeconds)
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
```

- [ ] **Step 2: 提交**

```bash
git add codex-proxy-daemon.ps1
git commit -m "feat: 主守护脚本（检测 FlClash 并维护 Codex 代理配置）"
```

Expected: commit 成功。

---

### Task 3: 检测逻辑验证（Dry-Run）

**Files:**
- 无（仅验证）

- [ ] **Step 1: 运行 Dry-Run 单次检测**

```bash
.\codex-proxy-daemon.ps1 -Test -DryRun
```

Expected: 输出 `DRY-RUN up=True port=7890 node=... mode=rule health=True`（node 为当前节点，如 `美国洛杉矶2号`；若 FlClash 模式不同，mode 可能不同）。此命令不修改任何配置。

- [ ] **Step 2: 验证未产生副作用**

```bash
[Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
```

Expected: 输出为空（Dry-Run 不写环境变量）。

---

### Task 4: 环境变量应用验证

**Files:**
- 无（仅验证，`-Test` 会真实应用）

- [ ] **Step 1: 应用一次代理配置**

```bash
.\codex-proxy-daemon.ps1 -Test
```

Expected: 输出 `APPLIED up=True port=7890 node=...`，日志出现"已应用用户环境变量代理"与"已开启系统代理"。

- [ ] **Step 2: 验证用户环境变量**

```bash
[Environment]::GetEnvironmentVariable('HTTP_PROXY', 'User')
[Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
[Environment]::GetEnvironmentVariable('ALL_PROXY', 'User')
[Environment]::GetEnvironmentVariable('NO_PROXY', 'User')
```

Expected: 前三个输出 `http://127.0.0.1:7890`，最后一个输出 `localhost,127.*,10.*,192.168.*,*.local`。

- [ ] **Step 3: 验证系统代理注册表**

```bash
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' | Select-Object ProxyEnable, ProxyServer | Format-List
```

Expected: `ProxyEnable : 1`，`ProxyServer : 127.0.0.1:7890`。

- [ ] **Step 4: 验证日志与 state.json**

```bash
Get-Content -Tail 10 logs\daemon.log
Get-Content -Raw state.json
```

Expected: 日志含"已应用用户环境变量代理"；state.json 的 `proxyUp` 为 `true`，`port` 为 7890。

---

### Task 5: 下线路径验证（临时指向死端口）

**Files:**
- Modify: `daemon.config.json`（临时，随后恢复）

- [ ] **Step 1: 临时修改配置指向不可达 API**

将 `daemon.config.json` 的 `clashApiUrl` 改为 `http://127.0.0.1:59999`。

- [ ] **Step 2: 运行应用命令（模拟下线）**

```bash
.\codex-proxy-daemon.ps1 -Test
```

Expected: 输出 `APPLIED up=False port=0 node= mode=api-unreachable`；用户环境变量被清空（变量被删除，`GetEnvironmentVariable` 返回空）；若之前系统代理指向 `127.0.0.1:7890`，则 `ProxyEnable` 变为 0；日志出现"已清空用户环境变量代理"与"已关闭残留系统代理"。

- [ ] **Step 3: 恢复配置并重新应用**

将 `clashApiUrl` 恢复为 `http://127.0.0.1:9090`，然后：

```bash
.\codex-proxy-daemon.ps1 -Test
```

Expected: 输出 `APPLIED up=True port=7890 ...`；环境变量与系统代理恢复。

- [ ] **Step 4: 阈值路径验证（常驻模式 + 临时配置）**

临时修改 `daemon.config.json`：

```json
{
  "clashApiUrl": "http://127.0.0.1:59999",
  "pollIntervalSeconds": 5,
  "downThreshold": 1
}
```

然后手动启动一个守护实例：

```bash
Start-Process powershell -ArgumentList '-NoProfile','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File',"$PWD\codex-proxy-daemon.ps1"
Start-Sleep -Seconds 12
Get-Content -Tail 8 logs\daemon.log
[Environment]::GetEnvironmentVariable('HTTPS_PROXY', 'User')
```

Expected: 日志含"守护脚本启动"与"代理下线（连续失败达到阈值），已清空代理配置"、"已清空用户环境变量代理"；`HTTPS_PROXY` 用户变量为空。

停止测试实例并恢复配置：

```bash
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*codex-proxy-daemon.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

将 `daemon.config.json` 恢复为基线（`clashApiUrl=http://127.0.0.1:9090`、`pollIntervalSeconds=35`、`downThreshold=3`），再运行：

```bash
.\codex-proxy-daemon.ps1 -Test
```

Expected: 环境变量与系统代理恢复为 `http://127.0.0.1:7890`。

---

### Task 6: 幂等性验证

**Files:**
- 无（仅验证）

- [ ] **Step 1: 连续运行两次应用命令**

```bash
.\codex-proxy-daemon.ps1 -Test
.\codex-proxy-daemon.ps1 -Test
```

Expected: 第二次运行不产生"已应用/已开启"日志（无变化、无广播），state.json 的 `envChanged` 与 `systemProxyChanged` 均为 `false`，`updatedAt` 更新。

---

### Task 7: 安装脚本与计划任务

**Files:**
- Create: `install-daemon.ps1`

- [ ] **Step 1: 创建 `install-daemon.ps1`**

```powershell
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$main = Join-Path $root 'codex-proxy-daemon.ps1'
if (-not (Test-Path -LiteralPath $main)) {
    throw "未找到 $main"
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$main`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Description 'Codex 代理守护：检测 FlClash 并维护 Codex 代理配置'
Register-ScheduledTask -TaskName 'CodexProxyDaemon' -InputObject $task -Force | Out-Null

"已注册计划任务 CodexProxyDaemon"
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Select-Object TaskName, State | Format-List
```

- [ ] **Step 2: 运行安装脚本**

```bash
.\install-daemon.ps1
```

Expected: 输出"已注册计划任务 CodexProxyDaemon"，`State` 为 `Ready`。

- [ ] **Step 3: 验证任务参数**

```bash
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo | Format-List
$task = Get-ScheduledTask -TaskName 'CodexProxyDaemon'
$task.Actions | Format-List Execute, Arguments
```

Expected: 动作 Execute 为 `powershell.exe`，Arguments 含 `-WindowStyle Hidden` 与脚本绝对路径。

- [ ] **Step 4: 提交**

```bash
git add install-daemon.ps1
git commit -m "feat: 注册计划任务实现开机自启"
```

---

### Task 8: 常驻循环验证

**Files:**
- 无（仅验证）

- [ ] **Step 1: 启动计划任务实例**

```bash
Start-ScheduledTask -TaskName 'CodexProxyDaemon'
```

Expected: 命令无输出即成功；`Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo` 的 `LastRunTime` 更新。

- [ ] **Step 2: 等待 40 秒后检查运行状态**

```bash
Start-Sleep -Seconds 40
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo | Select-Object LastRunTime, LastTaskResult | Format-List
Get-Content -Tail 5 logs\daemon.log
Get-Content -Raw state.json
```

Expected: `LastTaskResult` 为 0（运行中）；日志含"守护脚本启动"；state.json 的 `updatedAt` 在最近 40 秒内。

- [ ] **Step 3: 验证多开防护（可选）**

```bash
Start-Process powershell -ArgumentList '-NoProfile','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File',"$PWD\codex-proxy-daemon.ps1"
Start-Sleep -Seconds 3
Get-Content -Tail 3 logs\daemon.log
```

Expected: 日志出现"已有实例在运行，本实例退出"，不产生重复应用。

- [ ] **Step 4: 停止测试实例**

```bash
Stop-ScheduledTask -TaskName 'CodexProxyDaemon'
```

Expected: 任务状态变为 `Ready`；守护进程退出。

---

### Task 9: 卸载脚本与恢复验证

**Files:**
- Create: `uninstall-daemon.ps1`

- [ ] **Step 1: 创建 `uninstall-daemon.ps1`**

```powershell
[CmdletBinding()]
param(
    [switch]$ClearEnv,
    [switch]$DisableSystemProxy
)

$ErrorActionPreference = 'Stop'

$task = Get-ScheduledTask -TaskName 'CodexProxyDaemon' -ErrorAction SilentlyContinue
if ($task) {
    Unregister-ScheduledTask -TaskName 'CodexProxyDaemon' -Confirm:$false
    '已移除计划任务 CodexProxyDaemon'
} else {
    '计划任务 CodexProxyDaemon 不存在'
}

if ($ClearEnv) {
    foreach ($v in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
        [Environment]::SetEnvironmentVariable($v, $null, 'User')
    }
    '已清空用户级代理环境变量'
}

if ($DisableSystemProxy) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    Set-ItemProperty -Path $key -Name ProxyEnable -Value 0
    '已关闭系统代理'
}
```

- [ ] **Step 2: 验证卸载（仅移除任务）**

```bash
.\uninstall-daemon.ps1
```

Expected: 输出"已移除计划任务 CodexProxyDaemon"；`Get-ScheduledTask -TaskName 'CodexProxyDaemon'` 报不存在；用户环境变量与系统代理保持现状（未指定开关）。

- [ ] **Step 3: 提交**

```bash
git add uninstall-daemon.ps1
git commit -m "feat: 卸载脚本"
```

- [ ] **Step 4: 重新安装并启动（最终状态）**

```bash
.\install-daemon.ps1
Start-ScheduledTask -TaskName 'CodexProxyDaemon'
```

Expected: 计划任务注册成功并运行；`Get-Content -Tail 5 logs\daemon.log` 出现新的"守护脚本启动"。

---

### Task 10: README 完善与收尾

**Files:**
- Modify: `README.md`

- [ ] **Step 1: 完善 README**

```markdown
# Codex 代理守护脚本

检测 FlClash（`127.0.0.1:9090` Clash API）并维护 Codex 的代理配置：用户级环境变量
（`HTTP_PROXY` / `HTTPS_PROXY` / `ALL_PROXY` / `NO_PROXY`）与 WinINET 系统代理。
FlClash 下线时自动清空代理配置，使 DeepSeek 等可直连；不自动重启 Codex，
代理配置变化时重启 Codex 后生效。

## 文件

- `codex-proxy-daemon.ps1`：主脚本。`-Test` 单次检测并应用；`-Test -DryRun` 只检测不应用。
- `install-daemon.ps1`：注册计划任务 `CodexProxyDaemon`（登录时静默启动）。
- `uninstall-daemon.ps1`：移除任务；`-ClearEnv` 清空用户代理变量；`-DisableSystemProxy` 关闭系统代理。
- `daemon.config.json`：轮询间隔（默认 35 秒）、Clash API 地址、探活 URL、NO_PROXY 等。
- `state.json` / `logs\daemon.log`：当前状态与日志。

## 使用

```powershell
.\install-daemon.ps1          # 安装（注册计划任务）
.\codex-proxy-daemon.ps1 -Test -DryRun   # 只检测，预览状态
.\codex-proxy-daemon.ps1 -Test           # 单次检测并应用
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Get-ScheduledTaskInfo
.\uninstall-daemon.ps1 -ClearEnv         # 卸载并清空用户代理变量
```

## 说明

- 默认每 35 秒检测一次；连续 3 次失败（约 105 秒）判定 FlClash 下线。
- Codex 需重启后才会继承最新的用户环境变量；守护脚本不会自动重启 Codex。
- 后续切换国外模型只需改 Codex 的 `config.toml` provider，无需改本脚本。
- 若更换代理软件（非 FlClash），把 `daemon.config.json` 的 `clashApiUrl` 改为对应
  Clash 内核管理地址即可。
```

- [ ] **Step 2: 最终提交**

```bash
git add README.md
git commit -m "docs: 完善使用说明"
git log --oneline
```

Expected: git log 展示全部任务提交。

---

## 验收清单（对应设计文档）

- [ ] 计划任务 `CodexProxyDaemon` 存在，登录后静默运行（Task 7/8）。
- [ ] FlClash 在线时，`[Environment]::GetEnvironmentVariable('HTTPS_PROXY','User')` 返回 `http://127.0.0.1:7890`；系统代理开启且指向 7890（Task 4）。
- [ ] 模拟 FlClash 下线后变量清空、残留系统代理关闭（Task 5）。
- [ ] 恢复后变量与系统代理自动恢复（Task 5 Step 3）。
- [ ] mixed 端口变化自动跟随（修改 `daemon.config.json` 或真实改 FlClash 端口后，运行 `-Test` 验证；默认端口 7890）。
- [ ] 换国外模型无需改守护脚本（仅改 Codex `config.toml`，本脚本只维护代理层）。

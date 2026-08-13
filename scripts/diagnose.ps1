<#
.SYNOPSIS
    Codex 代理守护 - 只读诊断
.DESCRIPTION
    输出任务、守护状态、代理、环境变量、系统代理、Clash API、出口探活、日志摘要。
    不修改任何设置。适合排查 "为什么代理没生效 / 连不上"。
    退出码：0 = 正常，1 = 发现异常。
#>

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$issues = @()

function Emit($name, $value) {
    '{0,-28} {1}' -f $name, $value
}

'===== Codex 代理守护 诊断 ====='

# 1. 路径与任务
Emit '项目根' $root
$task = Get-ScheduledTask -TaskName 'CodexProxyDaemon' -ErrorAction SilentlyContinue
if ($task) {
    Emit '守护任务' $task.State
    $info = Get-ScheduledTaskInfo -TaskName 'CodexProxyDaemon' -ErrorAction SilentlyContinue
    if ($info) {
        Emit '最后运行' "$($info.LastRunTime) (结果 $($info.LastTaskResult))"
        # 任务在“运行中”或结果=0/267009(任务尚未运行过) 均不算异常
        $notRun = ($info.LastTaskResult -eq 267009) -or ($info.LastTaskResult -eq 0)
        if ($task.State -ne 'Running' -and -not $notRun -and $info.LastRunTime -ne [datetime]::MinValue) {
            $issues += '任务上次运行返回非零结果'
        }
    }
} else {
    Emit '守护任务' '未安装'
    $issues += '未安装守护任务'
}

# 2. 状态文件
$statePath = Join-Path $root 'state.json'
if (Test-Path -LiteralPath $statePath) {
    try {
        $s = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
        $age = [int]((Get-Date) - [datetime]::ParseExact($s.updatedAt, 'yyyy-MM-dd HH:mm:ss', $null)).TotalSeconds
        Emit '状态版本' $s.version
        Emit '代理状态' $s.message
        Emit '状态新鲜度' "$age 秒前更新"
        if ($age -gt 300) { $issues += "状态文件已 $age 秒未更新（守护可能未运行）" }
    } catch {
        Emit '状态文件' '解析失败'
        $issues += 'state.json 解析失败'
    }
} else {
    Emit '状态文件' '不存在'
    $issues += 'state.json 不存在'
}

# 3. 环境变量
foreach ($v in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY') {
    $val = [Environment]::GetEnvironmentVariable($v, 'User')
    Emit "用户 $v" $(if ($val) { $val } else { '(未设置)' })
}

# 4. 系统代理
$p = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
Emit '系统代理' "Enable=$($p.ProxyEnable) Server=$($p.ProxyServer)"

# 5. Clash API 连通性
$cfg = $null
try { $cfg = Get-Content -Raw -LiteralPath (Join-Path $root 'config\daemon.config.json') | ConvertFrom-Json } catch { $issues += '配置文件读取失败' }
if ($cfg) {
    Emit 'Clash API' $cfg.clashApiUrl
    try {
        $req = [System.Net.HttpWebRequest]::Create("$($cfg.clashApiUrl)/configs")
        $req.Timeout = 5000
        $req.Accept = 'application/json'
        $secret = [string]$cfg.clashApiSecret
        if ($secret -ne '') { $req.Headers.Add('Authorization', ('Bearer ' + $secret)) }
        $resp = $req.GetResponse()
        try {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
            $json = $reader.ReadToEnd(); $reader.Dispose()
        } finally { $resp.Close() }
        $c = $json | ConvertFrom-Json
        Emit '内核模式' $c.mode
        Emit '混合端口' $c.'mixed-port'
        $port = [int]$c.'mixed-port'
        # 6. 出口探活
        $urls = @($cfg.proxyTestUrls)
        if ($urls.Count -eq 0) { $urls = @($cfg.proxyTestUrl) }
        $ok = $false
        foreach ($u in $urls) {
            try {
                $r = Invoke-WebRequest -Uri ([string]$u) -Proxy "http://127.0.0.1:$port" -TimeoutSec 8 -UseBasicParsing
                if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500) {
                    Emit '出口探活' "通过 ($u) HTTP $($r.StatusCode)"
                    $ok = $true
                    break
                }
            } catch { }
        }
        if (-not $ok) {
            Emit '出口探活' '失败（代理端口无响应或无法访问外网）'
            $issues += '代理出口探活失败'
        }
    } catch {
        Emit 'Clash API' '不可达'
        $issues += 'Clash API 不可达（FlClash 未运行或端口不符）'
    }
}

# 7. 日志摘要
$log = Join-Path $root 'logs\daemon.log'
if (Test-Path -LiteralPath $log) {
    Emit '日志最后 5 行' ''
    Get-Content -LiteralPath $log -Tail 5 | ForEach-Object { '    ' + $_ }
} else {
    Emit '日志' '不存在'
}

''
if ($issues.Count -eq 0) {
    '结论: 一切正常'
    exit 0
} else {
    '发现异常:'
    $issues | ForEach-Object { '  - ' + $_ }
    exit 1
}
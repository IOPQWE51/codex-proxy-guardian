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

# Unregister 不会结束仍在运行的守护进程，这里显式停止，避免孤儿进程继续改代理
$daemonProcs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'codex-proxy-guardian\.ps1' }
$stopped = 0
foreach ($proc in $daemonProcs) {
    try { Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop; $stopped++ } catch { }
}
if ($stopped -gt 0) { "已停止守护进程（$stopped 个）" }

if ($ClearEnv) {
    foreach ($v in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
        [Environment]::SetEnvironmentVariable($v, $null, 'User')
    }
    '已清空用户级代理环境变量'
}

if ($DisableSystemProxy) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $p = Get-ItemProperty -Path $key
    # 只关闭守护脚本写入的 127.0.0.1 系统代理，不动用户自己的其他代理设置
    if ([string]$p.ProxyServer -match '^127\.0\.0\.1:\d+$') {
        Set-ItemProperty -Path $key -Name ProxyEnable -Value 0
        '已关闭系统代理（127.0.0.1）'
    } else {
        "系统代理指向非本守护配置（$($p.ProxyServer)），保持不动"
    }
}
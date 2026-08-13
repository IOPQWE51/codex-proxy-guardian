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
    $p = Get-ItemProperty -Path $key
    # 只关闭守护脚本写入的 127.0.0.1 系统代理，不动用户自己的其他代理设置
    if ([string]$p.ProxyServer -match '^127\.0\.0\.1:\d+$') {
        Set-ItemProperty -Path $key -Name ProxyEnable -Value 0
        '已关闭系统代理（127.0.0.1）'
    } else {
        "系统代理指向非本守护配置（$($p.ProxyServer)），保持不动"
    }
}
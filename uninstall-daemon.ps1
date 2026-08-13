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
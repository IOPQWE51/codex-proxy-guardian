[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$main = Join-Path $scriptDir 'codex-proxy-guardian.ps1'
if (-not (Test-Path -LiteralPath $main)) {
    throw "未找到 $main"
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$main`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Description 'Codex 代理守护：检测 FlClash 并维护 Codex 代理配置'
Register-ScheduledTask -TaskName 'CodexProxyDaemon' -InputObject $task -Force | Out-Null

"已注册计划任务 CodexProxyDaemon"
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Select-Object TaskName, State | Format-List
<#
.SYNOPSIS
    Codex 代理守护 - 一键完整卸载
.DESCRIPTION
    1) 移除托盘开机自启（HKCU Run）并关闭托盘进程
    2) 移除守护计划任务
    3) 默认清空用户代理环境变量并关闭本守护写入的系统代理
       用 -KeepProxy 保留这些设置（适合之后手动接管代理的场景）
#>
[CmdletBinding()]
param(
    [switch]$KeepProxy
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'CodexProxyGuardianTray' -ErrorAction SilentlyContinue
'已移除托盘开机自启'

Get-Process -Name 'GuardianTray' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name 'GuardianDaemon' -ErrorAction SilentlyContinue | Stop-Process -Force
'已关闭托盘进程'

if ($KeepProxy) {
    & (Join-Path $scriptDir 'uninstall-daemon.ps1')
} else {
    & (Join-Path $scriptDir 'uninstall-daemon.ps1') -ClearEnv -DisableSystemProxy
}
''
'卸载完成。目录可整体删除，无残留后台进程或自启项。'
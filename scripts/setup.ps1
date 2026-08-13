<#
.SYNOPSIS
    Codex 代理守护 - 一键安装
.DESCRIPTION
    1) 注册计划任务 CodexProxyDaemon（登录自启、静默）
    2) 立即启动守护
    3) 注册托盘开机自启（HKCU Run，可用 -NoTrayAutostart 跳过）
    4) -StartTray 可立即启动托盘控制台
    换环境部署：克隆仓库后运行本脚本即可。
#>
[CmdletBinding()]
param(
    [switch]$NoTrayAutostart,
    [switch]$StartTray
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir

& (Join-Path $scriptDir 'install-daemon.ps1') -Force
Start-ScheduledTask -TaskName 'CodexProxyDaemon' -ErrorAction SilentlyContinue

if (-not $NoTrayAutostart) {
    $exe = Join-Path $root 'dist\GuardianTray.exe'
    if (Test-Path -LiteralPath $exe) {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        Set-ItemProperty -Path $runKey -Name 'CodexProxyGuardianTray' -Value ('"' + $exe + '"')
        '已注册托盘开机自启'
    } else {
        "未找到托盘程序，跳过自启注册: $exe"
    }
}

if ($StartTray) {
    $exe = Join-Path $root 'dist\GuardianTray.exe'
    if (Test-Path -LiteralPath $exe) {
        Start-Process -FilePath $exe
        '已启动托盘控制台'
    } else {
        "未找到托盘程序: $exe"
    }
}

''
'安装完成。守护已注册并启动；托盘随登录自动启动。'
Get-ScheduledTask -TaskName 'CodexProxyDaemon' | Select-Object TaskName, State | Format-List
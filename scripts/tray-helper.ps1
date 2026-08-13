<#
.SYNOPSIS
    Codex 代理守护 - 托盘控制台辅助脚本（由 GuardianTray.exe 调用）
.DESCRIPTION
    提供守护状态查询、启停、开机自启开关、安装/卸载、只读检测等操作。
    输出为 UTF-8 的 key=value 扁平文本，便于 C# 托盘解析。
#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Action)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$guardian = Join-Path $root 'scripts\codex-proxy-guardian.ps1'

switch ($Action) {
    'State' {
        $t = Get-ScheduledTask -TaskName 'CodexProxyDaemon' -ErrorAction SilentlyContinue
        if ($t) { "task=$($t.State)" } else { 'task=NotInstalled' }
        $sf = Join-Path $root 'state.json'
        try {
            $s = Get-Content -Raw -LiteralPath $sf | ConvertFrom-Json
            "proxyUp=$($s.proxyUp)"
            "port=$($s.port)"
            "node=$($s.node)"
            "mode=$($s.mode)"
"message=$($s.message)"
            "version=$($s.version)"
            "nextCheck=$($s.nextCheck)"
        } catch {
            'proxyUp='
            'port='
            'node='
            'mode='
            'message=state.json 读取失败'
            'nextCheck='
        }
        foreach ($v in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY') {
            $val = [Environment]::GetEnvironmentVariable($v, 'User')
            if ($null -eq $val) { $val = '' }
            "env$v=$val"
        }
        $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        try {
            $p = Get-ItemProperty -Path $k
            "sysProxy=Enable=$($p.ProxyEnable)|Server=$($p.ProxyServer)"
        } catch {
            'sysProxy=Enable=0|Server='
        }
    }
    'Start' {
        Start-ScheduledTask -TaskName 'CodexProxyDaemon'
        'started'
    }
    'Stop' {
        Stop-ScheduledTask -TaskName 'CodexProxyDaemon'
        'stopped'
    }
    'Enable' {
        Enable-ScheduledTask -TaskName 'CodexProxyDaemon'
        'enabled'
    }
    'Disable' {
        Disable-ScheduledTask -TaskName 'CodexProxyDaemon'
        'disabled'
    }
    'Detect' {
        & $guardian -Test -DryRun
    }
    'Install' {
        & (Join-Path $root 'scripts\install-daemon.ps1') -Force
    }
    'Uninstall' {
        & (Join-Path $root 'scripts\uninstall-daemon.ps1')
    }
    default {
        throw "未知操作: $Action"
    }
}
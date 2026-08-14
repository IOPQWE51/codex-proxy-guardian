<#
.SYNOPSIS
    Codex 代理守护 - 托盘控制台辅助脚本（由 GuardianTray.exe 调用）
.DESCRIPTION
    提供守护状态查询、启停、开机自启开关、安装/卸载、只读检测、
    节点列表/切换、重启 Codex 等操作。UTF-8 输出。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Action,
    [string]$Group,
    [string]$Node,
    [string]$Value,
    [switch]$SyncDefaults
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$guardian = Join-Path $root 'scripts\codex-proxy-guardian.ps1'
$cfgPath  = Join-Path $root 'config\daemon.config.json'

function Invoke-ClashApiUtf8 {
    param([string]$Method, [string]$Path, $Body)
    $cfg = Get-Content -Raw -LiteralPath $cfgPath | ConvertFrom-Json
    $base = [string]$cfg.clashApiUrl
    $secret = [string]$cfg.clashApiSecret
    $req = [System.Net.HttpWebRequest]::Create("$base$Path")
    $req.Method = $Method
    $req.Timeout = 8000
    $req.Accept = 'application/json'
    if ($secret -ne '') {
        $req.Headers.Add('Authorization', ('Bearer ' + $secret))
    }
    if ($null -ne $Body) {
        $req.ContentType = 'application/json'
        $bytes = [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress))
        $stream = $req.GetRequestStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Dispose()
    }
    $resp = $req.GetResponse()
    try {
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
        $json = $reader.ReadToEnd()
        $reader.Dispose()
    } finally { $resp.Close() }
    if ($Method -eq 'PUT') { return $null }
    if ($json -and $json.Trim().Length -gt 0) { return ($json | ConvertFrom-Json) }
    return $null
}

switch ($Action) {
    'State' {
        $t = Get-ScheduledTask -TaskName 'CodexProxyDaemon' -ErrorAction SilentlyContinue
        if ($t) { "task=$($t.State)" } else { 'task=NotInstalled' }
        try {
            $s = Get-Content -Raw -LiteralPath (Join-Path $root 'state.json') | ConvertFrom-Json
            "proxyUp=$($s.proxyUp)"
            "port=$($s.port)"
            "node=$($s.node)"
            "mode=$($s.mode)"
            "message=$($s.message)"
            "nextCheck=$($s.nextCheck)"
            "version=$($s.version)"
        } catch {
            'proxyUp='
            'port='
            'node='
            'mode='
            'message=state.json 读取失败'
            'nextCheck='
            'version='
        }
        foreach ($v in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY') {
            $val = [Environment]::GetEnvironmentVariable($v, 'User')
            if ($null -eq $val) { $val = '' }
            "env$v=$val"
        }
        try {
            $p = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
            "sysProxy=Enable=$($p.ProxyEnable)|Server=$($p.ProxyServer)"
        } catch {
            'sysProxy=Enable=0|Server='
        }
    }
    'Start'    { Start-ScheduledTask -TaskName 'CodexProxyDaemon'; 'started' }
    'Stop'     { Stop-ScheduledTask  -TaskName 'CodexProxyDaemon'; 'stopped' }
    'Enable'   { Enable-ScheduledTask  -TaskName 'CodexProxyDaemon'; 'enabled' }
    'Disable'  { Disable-ScheduledTask -TaskName 'CodexProxyDaemon'; 'disabled' }
    'Detect'   {
        # 优先用独立守护 exe；GUI 子系统 exe 不会让 PowerShell 同步等待，用 Process 重定向同步读取
        $exe = Join-Path $root 'dist\GuardianDaemon.exe'
        if (Test-Path -LiteralPath $exe) {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $exe
            $psi.Arguments = '-Test -DryRun'
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.CreateNoWindow = $true
            $pr = [System.Diagnostics.Process]::Start($psi)
            $out = $pr.StandardOutput.ReadToEnd()
            $pr.WaitForExit()
            $out.Trim()
        } else {
            & $guardian -Test -DryRun
        }
    }
    'Install'  { & (Join-Path $root 'scripts\install-daemon.ps1') -Force }
    'Uninstall'{ & (Join-Path $root 'scripts\uninstall-daemon.ps1') }

    'Nodes' {
        try {
            $proxies = Invoke-ClashApiUtf8 -Method 'GET' -Path '/proxies'
            $groups = @()
            foreach ($name in ($proxies.proxies.PSObject.Properties.Name)) {
                $item = $proxies.proxies.$name
                if ($null -ne $item.now) {
                    $optArray = @()
                    foreach ($o in $item.all) { $optArray += [string]$o }
                    $groups += [PSCustomObject]@{ name = $name; now = [string]$item.now; options = $optArray }
                }
            }
            [PSCustomObject]@{ groups = $groups } | ConvertTo-Json -Depth 5 -Compress
        } catch {
            "ERR=$($_.Exception.Message)"
        }
    }

    'SwitchNode' {
        if ([string]::IsNullOrEmpty($Group) -or [string]::IsNullOrEmpty($Node)) {
            throw 'SwitchNode 需要 Group 和 Node 参数'
        }
        $path = '/proxies/' + [uri]::EscapeDataString($Group)
        try {
            Invoke-ClashApiUtf8 -Method 'PUT' -Path $path -Body @{ name = $Node } | Out-Null
            ('switched group=' + $Group + ' node=' + $Node)
        } catch {
            "ERR=$($_.Exception.Message)"
        }
    }

    'RestartCodex' {
        $procs = @(Get-Process -Name 'codex' -ErrorAction SilentlyContinue)
        $exePaths = @($procs | ForEach-Object { $_.Path } | Where-Object { $_ -ne '' } | Select-Object -Unique)
        foreach ($pr in $procs) {
            try { $pr.CloseMainWindow() | Out-Null } catch {}
        }
        Start-Sleep -Seconds 2
        foreach ($pr in @(Get-Process -Name 'codex' -ErrorAction SilentlyContinue)) {
            try { Stop-Process -Id $pr.Id -Force } catch {}
        }
        Start-Sleep -Milliseconds 500
        $started = 0
        foreach ($exe in $exePaths) {
            if (-not [string]::IsNullOrEmpty($exe) -and (Test-Path -LiteralPath $exe)) {
                Start-Process -FilePath $exe
                $started++
            }
        }
        if ($started -eq 0) {
            $cmd = Get-Command codex -ErrorAction SilentlyContinue
            if ($cmd) { Start-Process -FilePath $cmd.Source; $started++ }
        }
        "codex restarted ($started processes)"
    }


    'AddDirect' {
        if ([string]::IsNullOrEmpty($Value)) { throw 'AddDirect 需要 Value 参数' }
        $addScript = Join-Path $root 'scripts\add-direct.ps1'
        $urlArgs = @()
        foreach ($u in ($Value -split '\s+|,')) {
            $u = $u.Trim()
            if ($u -ne '') { $urlArgs += $u }
        }
        if ($urlArgs.Count -eq 0) { throw '未提供任何 Base URL' }
        $callArgs = @($urlArgs)
        if ($SyncDefaults) { $callArgs += '-SyncDefaults' }
        $out = & $addScript @callArgs 2>&1
        $added = @(); $skipped = @(); $errs = @()
        foreach ($line in @($out)) {
            $ls = [string]$line
            if ($ls -like 'ADD:*') { $added += $ls.Substring(4).Trim() }
            elseif ($ls -like 'SKIP:*') { $skipped += $ls.Substring(5).Trim() }
            elseif ($ls -like 'ERR:*') { $errs += $ls.Substring(4).Trim() }
        }
        if ($errs.Count -gt 0 -and $added.Count -eq 0) {
            "ERR=" + ($errs -join '；')
        } elseif ($added.Count -eq 0 -and $skipped.Count -eq 0) {
            "ERR=未知结果: " + (($out | ForEach-Object { [string]$_ }) -join ' | ')
        } else {
            [PSCustomObject]@{ ok = $true; added = $added; skipped = $skipped; errs = $errs } | ConvertTo-Json -Compress
        }
    }
    default { throw "未知操作: $Action" }
}
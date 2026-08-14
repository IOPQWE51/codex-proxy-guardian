<#
.SYNOPSIS
    添加 API Base URL / 域名到「直连白名单」，让该 API 走直连不经代理。

.DESCRIPTION
    用法（项目根目录下）:
        .\add-direct.ps1 https://api.longcat.chat
        .\add-direct.ps1 *.longcat.chat
        .\add-direct.ps1 https://api.volces.com -SyncDefaults
        .\add-direct.ps1 https://api.example.com -DryRun

    行为:
      1) 把 BaseUrl 归一化为直连规则：api.xxx.com -> *.xxx.com；
         已带 *. 前缀、IP、localhost 时原样使用。
      2) 写入 config\daemon.config.json 的 directDomains，守护进程 35 秒内热重载，
         自动并入系统代理绕过与 NO_PROXY。
      3) -SyncDefaults 时同步 scripts\codex-proxy-guardian.ps1、src\GuardianDaemon.cs
         的默认清单与 README 家数，让新环境默认也带该直连项。

    注意:
      - 域名范围想更精确时，直接传 *.api.xxx.com 形式。
      - 只有国内站才建议加直连；国外 API 走代理通常更稳。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$BaseUrl,
    [switch]$SyncDefaults,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cfgPath = Join-Path $root 'config\daemon.config.json'
$enc = New-Object System.Text.UTF8Encoding($true)

# --- 1) 归一化为直连规则 ---
$raw = $BaseUrl.Trim()
if ($raw -match '^[a-z][a-z0-9+.-]*://') { $raw = ([uri]$raw).Host }
$raw = $raw.TrimEnd('/')
if ($raw -match '^\*\.') {
    $rule = $raw
} elseif ($raw -match '^(localhost|\d{1,3}(\.\d{1,3}){3})$') {
    $rule = $raw
} else {
    $parts = $raw.Split('.')
    if ($parts.Count -ge 3) { $rule = '*.' + ($parts[1..($parts.Count-1)] -join '.') } else { $rule = '*.' + $raw }
}
Write-Host "直连规则: $rule"

# --- 2) 更新运行配置 ---
$cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$list = @($cfg.directDomains)
if ($DryRun) {
    if ($list -contains $rule) { Write-Host "(DryRun) 已存在，无需修改: $rule" }
    else { Write-Host "(DryRun) 将新增到 directDomains: $rule" }
    exit 0
}
if ($list -contains $rule) {
    Write-Host "config 中已存在: $rule（无需重复添加）"
} else {
    $cfg.directDomains = $list + $rule
    $json = $cfg | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($cfgPath, $json, $enc)
    Write-Host "已写入 config\daemon.config.json，守护将在下次轮询（<=35s）热重载生效"
}

# --- 3) 校验 ---
$check = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($check.directDomains) -contains $rule) { Write-Host '校验通过: 新规则已在直连名单中' }
else { throw '校验失败: 规则未出现在配置中' }

# --- 4) 同步默认清单（可选） ---
if ($SyncDefaults) {
    $psPath = Join-Path $root 'scripts\codex-proxy-guardian.ps1'
    $csPath = Join-Path $root 'src\GuardianDaemon.cs'
    $readmePath = Join-Path $root 'README.md'
    $nl = [char]13 + [char]10

    # PS 默认清单：定位 directDomains 数组行，在末尾元素后插入
    $t = [IO.File]::ReadAllText($psPath, $enc)
    if ($t.Contains($rule)) { Write-Host 'PS 默认清单已含该规则' }
    else {
        $m = [regex]::Match($t, 'directDomains\s*=\s*@\([^)]*\)')
        if (-not $m.Success) { throw 'PS 默认清单中未找到 directDomains 数组，请手动同步' }
        $line = $m.Value
        $line2 = $line.Substring(0, $line.Length - 1) + ", '$rule')"
        $t = $t.Remove($m.Index, $m.Length).Insert($m.Index, $line2)
        [IO.File]::WriteAllText($psPath, $t, $enc)
        Write-Host '已同步 PS 默认清单'
    }

    # C# 默认清单：定位最后一个 Add 调用，在其后插入新行
    $t = [IO.File]::ReadAllText($csPath, $enc)
    if ($t.Contains($rule)) { Write-Host 'C# 默认清单已含该规则' }
    else {
        $ms = [regex]::Matches($t, 'c\.DirectDomains\.Add\("[^"]+"\);')
        if ($ms.Count -eq 0) { throw 'C# 默认清单中未找到 Add 调用，请手动同步' }
        $last = $ms[$ms.Count - 1]
        $t = $t.Insert($last.Index + $last.Length, $nl + '            c.DirectDomains.Add("' + $rule + '");')
        [IO.File]::WriteAllText($csPath, $t, $enc)
        Write-Host '已同步 C# 默认清单'
    }

    # README 家数
    $n = @($check.directDomains).Count
    $t = [IO.File]::ReadAllText($readmePath, $enc)
    $oldN = $n - 1
    if ($t.Contains("$oldN 家境内 API")) {
        $t = $t.Replace("$oldN 家境内 API", "$n 家境内 API")
        [IO.File]::WriteAllText($readmePath, $t, $enc)
        Write-Host "README 家数已更新为 $n"
    }
}
Write-Host '完成'
<#
.SYNOPSIS
    添加一个或多个 API Base URL / 域名到「直连白名单」，让这些 API 走直连不经代理。

.DESCRIPTION
    用法（项目根目录下）:
        .\add-direct.ps1 https://api.longcat.chat
        .\add-direct.ps1 https://api.a.com https://b.com
        .\add-direct.ps1 "https://api.a.com https://b.com" -DryRun
        .\add-direct.ps1 *.xxx.com -SyncDefaults

    行为:
      1) 每个 BaseUrl 归一化为直连规则：api.xxx.com -> *.xxx.com；
         已带 *. 前缀、IP、localhost 时原样使用。
      2) 写入 config\daemon.config.json 的 directDomains，守护进程 35 秒内热重载，
         自动并入系统代理绕过与 NO_PROXY。
      3) -SyncDefaults 时同步 scripts\codex-proxy-guardian.ps1、src\GuardianDaemon.cs
         的默认清单与 README 家数，让新环境默认也带该直连项。

    输出（供托盘等程序解析）:
        ADD:  新增规则
        SKIP: 已存在规则
        ERR:  无法解析的输入

    注意:
      - 域名范围想更精确时，直接传 *.api.xxx.com 形式。
      - 只有国内站才建议加直连；国外 API 走代理通常更稳。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$BaseUrls,
    [switch]$SyncDefaults,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cfgPath = Join-Path $root 'config\daemon.config.json'
$enc = New-Object System.Text.UTF8Encoding($true)

function ConvertTo-DirectRule {
    param([string]$Raw)
    if ($Raw -match '^[a-z][a-z0-9+.-]*://') { $Raw = ([uri]$Raw).Host }
    $Raw = $Raw.TrimEnd('/')
    if ($Raw -match '^\*\.') { return $Raw }
    if ($Raw -match '^(localhost|\d{1,3}(\.\d{1,3}){3})$') { return $Raw }
    $parts = $Raw.Split('.')
    if ($parts.Count -ge 3) { return '*.' + ($parts[1..($parts.Count-1)] -join '.') }
    return '*.' + $Raw
}

# --- 1) 全部归一化 ---
$rules = New-Object System.Collections.Generic.List[string]
$rawInputs = @($BaseUrls | ForEach-Object { ($_ -split '\s+|,') } | Where-Object { $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
if ($rawInputs.Count -eq 0) { throw '未提供任何 Base URL' }
foreach ($u in $rawInputs) {
    try {
        $r = ConvertTo-DirectRule -Raw $u
        if (-not $rules.Contains($r)) { $rules.Add($r) }
    } catch { Write-Host "ERR: $u" }
}
Write-Host "直连规则: $($rules -join ', ')"

# --- 2) 与现有配置对比 ---
$cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$list = @($cfg.directDomains)
$added = New-Object System.Collections.Generic.List[string]
foreach ($r in $rules) {
    if ($list -contains $r) { Write-Host "SKIP: $r" }
    else { $added.Add($r) }
}
if ($DryRun) {
    foreach ($r in $rules) {
        Write-Host "$(if ($added.Contains($r)) { 'ADD (dry):' } else { 'SKIP:' }) $r"
    }
    Write-Host 'DryRun: 未写入任何文件'
    exit 0
}
if ($added.Count -eq 0) { Write-Host '全部已存在，无需修改'; exit 0 }

# --- 3) 写运行配置 ---
$cfg.directDomains = $list + @($added)
$json = $cfg | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($cfgPath, $json, $enc)
Write-Host "已写入 config\daemon.config.json"

# --- 4) 校验 ---
$check = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$missing = @($added | Where-Object { -not (@($check.directDomains) -contains $_) })
if ($missing.Count -gt 0) { throw "校验失败: $($missing -join ', ') 未出现在配置中" }
foreach ($r in $added) { Write-Host "ADD: $r" }

# --- 5) 同步默认清单（可选） ---
if ($SyncDefaults) {
    $psPath = Join-Path $root 'scripts\codex-proxy-guardian.ps1'
    $csPath = Join-Path $root 'src\GuardianDaemon.cs'
    $readmePath = Join-Path $root 'README.md'
    $nl = [char]13 + [char]10

    $t = [IO.File]::ReadAllText($psPath, $enc)
    foreach ($r in $added) {
        if ($t.Contains($r)) { Write-Host "PS 默认清单已含 $r" }
        else {
            $m = [regex]::Match($t, 'directDomains\s*=\s*@\([^)]*\)')
            if (-not $m.Success) { throw 'PS 默认清单中未找到 directDomains 数组，请手动同步' }
            $line2 = $m.Value.Substring(0, $m.Value.Length - 1) + ", '$r')"
            $t = $t.Remove($m.Index, $m.Length).Insert($m.Index, $line2)
        }
    }
    [IO.File]::WriteAllText($psPath, $t, $enc)
    Write-Host '已同步 PS 默认清单'

    $t = [IO.File]::ReadAllText($csPath, $enc)
    foreach ($r in $added) {
        if ($t.Contains($r)) { Write-Host "C# 默认清单已含 $r" }
        else {
            $ms = [regex]::Matches($t, 'c\.DirectDomains\.Add\("[^"]+"\);')
            if ($ms.Count -eq 0) { throw 'C# 默认清单中未找到 Add 调用，请手动同步' }
            $last = $ms[$ms.Count - 1]
            $t = $t.Insert($last.Index + $last.Length, $nl + '            c.DirectDomains.Add("' + $r + '");')
        }
    }
    [IO.File]::WriteAllText($csPath, $t, $enc)
    Write-Host '已同步 C# 默认清单'

    $n = @($check.directDomains).Count
    $t = [IO.File]::ReadAllText($readmePath, $enc)
    $m = [regex]::Match($t, '\d+ 家境内 API')
    if ($m.Success) {
        $oldN = [int]($m.Value -replace ' 家境内 API', '')
        $t = $t.Replace("$oldN 家境内 API", "$n 家境内 API")
        [IO.File]::WriteAllText($readmePath, $t, $enc)
        Write-Host "README 家数已更新为 $n"
    }
}
Write-Host '完成'
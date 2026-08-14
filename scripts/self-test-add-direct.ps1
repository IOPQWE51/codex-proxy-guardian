<#
.SYNOPSIS
    add-direct.ps1 自测：多 URL、归一化、幂等、SyncDefaults、DryRun
    在临时根副本中执行，不触碰真实项目文件。
#>
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$tmp = Join-Path $env:TEMP ('gad-test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $tmp 'config'), (Join-Path $tmp 'scripts'), (Join-Path $tmp 'src') | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'config\daemon.config.json') -Destination (Join-Path $tmp 'config\daemon.config.json')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\codex-proxy-guardian.ps1') -Destination (Join-Path $tmp 'scripts\codex-proxy-guardian.ps1')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\GuardianDaemon.cs') -Destination (Join-Path $tmp 'src\GuardianDaemon.cs')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $tmp 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\add-direct.ps1') -Destination (Join-Path $tmp 'scripts\add-direct.ps1')

    function Assert-True($cond, $msg) {
        if ($cond) { "PASS  $msg" } else { throw "FAIL  $msg" }
    }
    function Invoke-It([string]$argsLine) {
        $parts = @($argsLine -split '\s+' | Where-Object { $_ -ne '' })
        $out = powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tmp 'scripts\add-direct.ps1') @parts 2>&1
        return ($out | ForEach-Object { [string]$_ })
    }

    # --- 1) 幂等：已存在的域名应输出 SKIP 且不重复 ---
    $out = Invoke-It 'https://api.longcat.chat'
    $cfg = Get-Content -LiteralPath (Join-Path $tmp 'config\daemon.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($out -match 'SKIP: \*\.longcat\.chat') '已存在规则输出 SKIP'
    Assert-True ((@($cfg.directDomains) | Where-Object { $_ -eq '*.longcat.chat' }).Count -eq 1) '幂等不重复'

    # --- 2) 多 URL 空格分隔：3 段与 4 段域名归一化 ---
    $before = @($cfg.directDomains).Count
    $out = Invoke-It 'https://api.test1.cn https://x.test2.com.cn'
    Assert-True ($out -match 'ADD: \*\.test1\.cn') '3 段域名剥一层 -> *.test1.cn'
    Assert-True ($out -match 'ADD: \*\.test2\.com\.cn') '4 段域名剥一层 -> *.test2.com.cn'
    $cfg = Get-Content -LiteralPath (Join-Path $tmp 'config\daemon.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ((@($cfg.directDomains).Count) -eq ($before + 2)) 'config 增加 2 条'

    # --- 3) 已有 *. 前缀原样 ---
    $out = Invoke-It '*.raw.example.com'
    Assert-True ($out -match 'ADD: \*\.raw\.example\.com') '带 *. 原样添加'

    # --- 4) SyncDefaults 同步 PS/C#/README 副本 ---
    $out = Invoke-It 'https://api.sync1.cn -SyncDefaults'
    $t = Get-Content -LiteralPath (Join-Path $tmp 'scripts\codex-proxy-guardian.ps1') -Raw -Encoding UTF8
    Assert-True ($t.Contains('*.sync1.cn')) 'PS 默认清单已同步'
    $t = Get-Content -LiteralPath (Join-Path $tmp 'src\GuardianDaemon.cs') -Raw -Encoding UTF8
    Assert-True ($t.Contains('*.sync1.cn')) 'C# 默认清单已同步'
    $t = Get-Content -LiteralPath (Join-Path $tmp 'README.md') -Raw -Encoding UTF8
    Assert-True ($t -match '17 家境内 API') 'README 家数更新'

    # --- 5) DryRun 不写入 ---
    $out = Invoke-It 'https://api.dry1.cn -DryRun'
    $cfg = Get-Content -LiteralPath (Join-Path $tmp 'config\daemon.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True (-not (@($cfg.directDomains) -contains '*.dry1.cn')) 'DryRun 不写入'
    Assert-True ($out -match 'ADD \(dry\): \*\.dry1\.cn') 'DryRun 预览输出'

    '结果: 全部通过'
}
finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
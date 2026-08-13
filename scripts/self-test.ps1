<#
.SYNOPSIS
    Codex 代理守护 - 自测脚本
.DESCRIPTION
    从守护脚本中提取核心纯函数，在隔离沙盒中验证：
    - 配置合并与数值钳制（Get-DaemonConfig / Clamp-Int）
    - 系统代理绕过列表合并（Merge-OverrideList）
    - 宽限时间窗判定（Get-DetectedState -UseGrace：开机首轮、宽限、超窗、恢复、再次失败）
    - 日志防写爆与轮转（Write-Log）
    不修改任何真实配置/环境变量/注册表。
    用法:
        .\self-test.ps1            # 运行全部
#>

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$guardian = Join-Path $root 'scripts\codex-proxy-guardian.ps1'
if (-not (Test-Path -LiteralPath $guardian)) { throw "未找到 $guardian" }

$script:pass = 0
$script:fail = 0

function Assert-True {
    param([bool]$Cond, [string]$Name)
    if ($Cond) { $script:pass++; "PASS  $Name" }
    else { $script:fail++; "FAIL  $Name" }
}

# ---- 提取守护脚本中的函数（隔离运行，不执行入口） ----
$src = Get-Content -Raw -LiteralPath $guardian
foreach ($fn in @('Clamp-Int', 'Merge-OverrideList', 'Get-DaemonConfig', 'Get-DetectedState', 'Write-Log')) {
    $m = [regex]::Match($src, "(?ms)^function $fn \{.*?^}")
    if (-not $m.Success) { throw "未找到函数 $fn（守护脚本结构可能已变化）" }
    Invoke-Expression $m.Value
}

"=== 1. Clamp-Int ==="
Assert-True ((Clamp-Int 5 5 600) -eq 5) 'Clamp-Int 下界'
Assert-True ((Clamp-Int 600 5 600) -eq 600) 'Clamp-Int 上界'
Assert-True ((Clamp-Int 1 5 600) -eq 5) 'Clamp-Int 低于下界'
Assert-True ((Clamp-Int 5000 5 600) -eq 600) 'Clamp-Int 高于上界'
Assert-True ((Clamp-Int 'abc' 5 600) -eq 5) 'Clamp-Int 非法值回退下界'

"=== 2. Merge-OverrideList ==="
Assert-True ((Merge-OverrideList -Current '' -Want 'localhost;*.local;*.deepseek.com') -eq 'localhost;*.local;*.deepseek.com') '空列表填入期望值'
$r = Merge-OverrideList -Current '*zhihu.com;localhost' -Want 'localhost;*.local;*.deepseek.com'
Assert-True ($r -eq '*zhihu.com;localhost;*.local;*.deepseek.com') "已有列表仅追加缺失项 -> $r"
$r = Merge-OverrideList -Current 'localhost;*.local;*.deepseek.com' -Want 'localhost;*.local;*.deepseek.com'
Assert-True ($r -eq 'localhost;*.local;*.deepseek.com') '全齐时保持原样'

"=== 3. Get-DaemonConfig（临时配置） ==="
$tmpDir = Join-Path $env:TEMP ('cpd-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
try {
    $cfgPath = Join-Path $tmpDir 'daemon.config.json'
    $testCfg = @{
        clashApiUrl = 'http://127.0.0.1:9999'
        pollIntervalSeconds = 1
        requestTimeoutSeconds = 99
        downSeconds = 5000
        maxLogBytes = 500
        maxLogFiles = 99
        noProxy = 'localhost'
        proxyOverride = 'localhost'
        directDomains = @('*.foo.com', '*.bar.com', '*.foo.com')
    } | ConvertTo-Json
    [IO.File]::WriteAllText($cfgPath, $testCfg, (New-Object System.Text.UTF8Encoding($true)))
    $script:DefaultConfig = @{
        clashApiUrl = 'http://127.0.0.1:9090'
        pollIntervalSeconds = 35
        requestTimeoutSeconds = 8
        downThreshold = 3
        downSeconds = 90
        proxyTestUrl = 'https://www.gstatic.com/generate_204'
        noProxy = 'localhost,127.*,10.*,192.168.*,*.local'
        proxyOverride = 'localhost;*.local;127.*;10.*;192.168.*'
        directDomains = @('*.deepseek.com')
        nodeLogCooldownSeconds = 60
        maxLogBytes = 2097152
        maxLogFiles = 3
        clashApiSecret = ''
    }
    $script:ConfigPath = $cfgPath
    $script:LogDir = Join-Path $tmpDir 'logs'
    $script:DefaultLogFile = Join-Path $script:LogDir 'daemon.log'
    $cfg = Get-DaemonConfig
    Assert-True ($cfg.clashApiUrl -eq 'http://127.0.0.1:9999') 'clashApiUrl 生效'
    Assert-True ($cfg.pollIntervalSeconds -eq 5) "poll 钳制到 5 -> $($cfg.pollIntervalSeconds)"
    Assert-True ($cfg.requestTimeoutSeconds -eq 30) "timeout 钳制到 30 -> $($cfg.requestTimeoutSeconds)"
    Assert-True ($cfg.downSeconds -eq 600) "downSeconds 钳制到 600 -> $($cfg.downSeconds)"
    Assert-True ($cfg.maxLogBytes -eq 102400) "maxLogBytes 钳制到 102400 -> $($cfg.maxLogBytes)"
    Assert-True ($cfg.maxLogFiles -eq 10) "maxLogFiles 钳制到 10 -> $($cfg.maxLogFiles)"
    Assert-True ($cfg.noProxy -eq 'localhost,*.foo.com,*.bar.com') "directDomains 并入 NO_PROXY -> $($cfg.noProxy)"
    Assert-True ($cfg.proxyOverride -eq 'localhost;*.foo.com;*.bar.com') "directDomains 并入 ProxyOverride -> $($cfg.proxyOverride)"
    Assert-True (($cfg.directDomains | Measure-Object).Count -eq 2) 'directDomains 去重'
} finally {
    if (Test-Path -LiteralPath $tmpDir) { Remove-Item -LiteralPath $tmpDir -Recurse -Force }
}

"=== 4. Get-DetectedState 宽限时间窗 ==="
$script:Config = @{ downSeconds = 2 }
$script:seenUp = $false
$script:firstFailAt = [datetime]::MinValue
$script:stubOk = $false
function Invoke-ClashApi { param([string]$Path) if ($script:stubOk) { return ([PSCustomObject]@{ mode = 'rule'; 'mixed-port' = 7890 }) } return $null }
function Test-ProxyHealth { param([int]$Port) return $script:stubOk }

$r1 = Get-DetectedState -UseGrace
Assert-True (-not $r1.Up) '开机首轮失败：立即下线（不残留死代理）'
$script:stubOk = $true
$r2 = Get-DetectedState -UseGrace
Assert-True ($r2.Up -and $script:seenUp) '代理上线：seenUp 置位'
$script:stubOk = $false
$r3 = Get-DetectedState -UseGrace
Assert-True ($r3.Up) '首次失败：进入宽限期保持在线'
Start-Sleep -Milliseconds 1100
$r4 = Get-DetectedState -UseGrace
Assert-True ($r4.Up) '宽限期内（1.1s<2s）：保持在线'
Start-Sleep -Milliseconds 1100
$r5 = Get-DetectedState -UseGrace
Assert-True (-not $r5.Up) '超过时间窗（2.2s>=2s）：判定下线'
$script:stubOk = $true
$r6 = Get-DetectedState -UseGrace
Assert-True ($r6.Up -and $script:firstFailAt -eq [datetime]::MinValue) '恢复后重置宽限计时'
$script:stubOk = $false
$r7 = Get-DetectedState -UseGrace
Assert-True ($r7.Up) '再次失败：宽限重新开始'
$r8 = Get-DetectedState
Assert-True (-not $r8.Up) '非宽限模式（-Test）：立即判定'

"=== 5. Write-Log 防写爆 ==="
$tmpDir2 = Join-Path $env:TEMP ('cpd-log-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir2 -Force | Out-Null
try {
    $script:LogDir = $tmpDir2
    $script:DefaultLogFile = Join-Path $tmpDir2 'daemon.log'
    # 日志最小钳制为 102400 字节，这里设到该值以真实触发轮转
    $script:Config = @{ maxLogBytes = 102400; maxLogFiles = 3 }
    $script:logQuietUntil = [datetime]::MinValue
    $script:logFailStreak = 0
    for ($i = 0; $i -lt 1500; $i++) {
        Write-Log ('测试日志行 ' + $i + ' ' + ('x' * 100))
    }
    $mainLen = (Get-Item $script:DefaultLogFile).Length
    Assert-True ($mainLen -lt 104000) "主日志大小受控 -> $mainLen bytes"
    $files = @(Get-ChildItem -Path $tmpDir2 -File -ErrorAction SilentlyContinue)
    $rotCount = @($files | Where-Object { $_.Name -like 'daemon.log.*' }).Count
    Assert-True ($rotCount -ge 1) "轮转文件已产生 -> $rotCount 个"
    Assert-True ($files.Count -le 4) "轮转份数不超过 maxLogFiles -> $($files.Count) 个"
    $total = ($files | Measure-Object -Property Length -Sum).Sum
    Assert-True ($total -lt 450000) "日志总量受控 -> $total bytes"
} finally {
    if (Test-Path -LiteralPath $tmpDir2) { Remove-Item -LiteralPath $tmpDir2 -Recurse -Force }
}

"=== 6. Write-Log 写入失败静默降级 ==="
$tmpDir3 = Join-Path $env:TEMP ('cpd-quiet-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir3 -Force | Out-Null
try {
    $script:Config = @{ maxLogBytes = 4096; maxLogFiles = 3 }
    $script:logQuietUntil = [datetime]::MinValue
    $script:logFailStreak = 0
    # 让日志目录路径被一个文件占用，使 New-Item/Add-Content 必然失败
    $script:LogDir = Join-Path $tmpDir3 'blocked.log'
    $script:DefaultLogFile = Join-Path $script:LogDir 'daemon.log'
    [IO.File]::WriteAllText($script:LogDir, 'blocked', (New-Object System.Text.UTF8Encoding($true)))
    for ($i = 0; $i -lt 25; $i++) { Write-Log 'should fail' }
    Assert-True ($script:logFailStreak -eq 0 -and $script:logQuietUntil -gt [datetime]::Now) '连续失败后进入静默窗口'
} finally {
    if (Test-Path -LiteralPath $tmpDir3) { Remove-Item -LiteralPath $tmpDir3 -Recurse -Force }
}

""
"结果: PASS=$($script:pass) FAIL=$($script:fail)"
if ($script:fail -gt 0) { exit 1 }
'全部通过'
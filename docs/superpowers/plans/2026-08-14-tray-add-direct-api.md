# 托盘图形化「添加直连 API」实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在托盘右键菜单提供图形化表单，支持一次性添加多个 API Base URL 到直连白名单（空格/换行/逗号分隔），守护热重载生效，并复用现有 add-direct.ps1 保证唯一实现。

**Architecture:** 三层复用：`add-direct.ps1` 升级为多 URL 命令行入口（唯一写配置逻辑）；`tray-helper.ps1` 新增 `AddDirect` 动作做中转；新增独立源 `src/AddDirectForm.cs` 提供 WinForms 表单，`GuardianTray.cs` 仅挂菜单项并回调 RunHelper。build-tray.ps1 同时编译两个 C# 源文件。

**Tech Stack:** PowerShell 5.1、C# / .NET Framework 4.8 WinForms、csc.exe 编译、JSON。

---

### Task 1: 编写 add-direct.ps1 自测脚本（临时根）

**Files:**
- Create: `scripts/self-test-add-direct.ps1`
- Test: `scripts/self-test-add-direct.ps1`

- [ ] **Step 1: 创建测试脚本（完整内容）**

```powershell
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
    Assert-True ($t -match '14 家境内 API') 'README 家数更新'

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
```

- [ ] **Step 2: 运行测试，确认失败（红）**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test-add-direct.ps1`
Expected: FAIL（add-direct.ps1 当前 `$BaseUrl` 是单字符串，多 URL 位置参数绑定报错或 ADD 断言不成立）

### Task 2: 升级 add-direct.ps1 支持多 URL

**Files:**
- Modify: `scripts/add-direct.ps1`（整体替换为下方内容）

- [ ] **Step 1: 写入新版本（完整内容）**

```powershell
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
    [Parameter(Mandatory = $true, Position = 0)]
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
    $oldN = $n - $added.Count
    if ($t.Contains("$oldN 家境内 API")) {
        $t = $t.Replace("$oldN 家境内 API", "$n 家境内 API")
        [IO.File]::WriteAllText($readmePath, $t, $enc)
        Write-Host "README 家数已更新为 $n"
    }
}
Write-Host '完成'
```

- [ ] **Step 2: 统一行尾**

Run: 将 `scripts/add-direct.ps1` 文本中所有独立 LF 替换为 CRLF，保持 UTF8 BOM（用与仓库一致的方式写回并验证 `LF-only=0`）
Expected: CRLF 且含 BOM

- [ ] **Step 3: 运行测试，确认通过（绿）**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test-add-direct.ps1`
Expected: `结果: 全部通过`，无 FAIL

- [ ] **Step 4: 真实项目冒烟——重复添加 longcat（应 SKIP）**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\add-direct.ps1 https://api.longcat.chat`
Expected: 输出含 `SKIP: *.longcat.chat`，config 数量不变

- [ ] **Step 5: Commit**

```bash
git -C G:/AGENT/proxy/codex-proxy-daemon add scripts/add-direct.ps1 scripts/self-test-add-direct.ps1
git -C G:/AGENT/proxy/codex-proxy-daemon commit -m "feat: add-direct.ps1 支持多 URL 与 ADD/SKIP/ERR 输出"
```

### Task 3: tray-helper.ps1 新增 AddDirect 动作

**Files:**
- Modify: `scripts/tray-helper.ps1`（param 区块 + switch 分支）

- [ ] **Step 1: 扩展参数**

将 param 区块替换为：

```powershell
param(
    [Parameter(Mandatory=$true)][string]$Action,
    [string]$Group,
    [string]$Node,
    [string]$Value,
    [switch]$SyncDefaults
)
```

- [ ] **Step 2: 在 switch 的 `'RestartCodex'` 分支之后、`default` 之前插入 AddDirect 分支**

```powershell
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
        } elseif ($errs.Count -eq 0 -and $added.Count -eq 0 -and $LASTEXITCODE -ne 0) {
            "ERR=脚本异常: " + (($out | ForEach-Object { [string]$_ }) -join ' | ')
        } else {
            [PSCustomObject]@{ ok = $true; added = $added; skipped = $skipped; errs = $errs } | ConvertTo-Json -Compress
        }
    }
```

- [ ] **Step 3: 验证 AddDirect 动作（幂等，不产生改动）**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\tray-helper.ps1 AddDirect -Value "https://api.longcat.chat"`
Expected: 输出 JSON 形如 `{"added":[],"errs":[],"ok":true,"skipped":["*.longcat.chat"]}`（或字段顺序不同）

- [ ] **Step 4: 验证多 URL + SyncDefaults 走 helper（用临时测试域？不写真实仓库）**

说明：多 URL + SyncDefaults 的真实写入路径已有 Task 2 测试覆盖；此处验证 helper 的参数通道：
Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\tray-helper.ps1 AddDirect -Value "https://api.longcat.chat https://api.deepseek.com"`
Expected: JSON 中 `skipped` 含 `*.longcat.chat`、`*.deepseek.com`，`added` 为空

- [ ] **Step 5: Commit**

```bash
git -C G:/AGENT/proxy/codex-proxy-daemon add scripts/tray-helper.ps1
git -C G:/AGENT/proxy/codex-proxy-daemon commit -m "feat: tray-helper 新增 AddDirect 动作"
```

### Task 4: 新建 AddDirectForm.cs 表单

**Files:**
- Create: `src/AddDirectForm.cs`

- [ ] **Step 1: 创建文件（完整内容）**

```csharp
// Codex 代理守护 - 托盘「添加直连 API」表单
// 独立源文件，由 scripts\build-tray.ps1 与 GuardianTray.cs 一并编译（.NET Framework 4.8 WinForms）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CodexProxyGuardian
{
    internal sealed class AddDirectForm : Form
    {
        private readonly Func<string, string> _runHelper;
        private readonly Action<string, string> _notify;
        private readonly TextBox _input = new TextBox();
        private readonly Label _preview = new Label();
        private readonly CheckBox _chkSync = new CheckBox();
        private readonly Button _btnOk = new Button();
        private readonly Button _btnCancel = new Button();

        public AddDirectForm(Func<string, string> runHelper, Action<string, string> notify)
        {
            _runHelper = runHelper;
            _notify = notify;

            Text = "添加直连 API";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(540, 265);
            Font = new Font("Microsoft YaHei UI", 9F);

            var tip = new Label();
            tip.Text = "输入一个或多个 API Base URL，用空格（或换行/逗号）分隔：\r\n例：https://api.longcat.chat https://api.volces.com";
            tip.AutoSize = true;
            tip.Location = new Point(14, 12);

            _input.Multiline = true;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.Location = new Point(14, 54);
            _input.Size = new Size(512, 70);
            _input.TextChanged += (s, e) => UpdatePreview();
            _input.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter) { Submit(); }
            };

            var preLbl = new Label();
            preLbl.Text = "将添加的直连规则：";
            preLbl.AutoSize = true;
            preLbl.Location = new Point(14, 134);

            _preview.AutoSize = false;
            _preview.BorderStyle = BorderStyle.FixedSingle;
            _preview.Location = new Point(14, 156);
            _preview.Size = new Size(512, 42);
            _preview.ForeColor = Color.FromArgb(34, 120, 90);
            _preview.TextAlign = ContentAlignment.MiddleLeft;

            var hint = new Label();
            hint.Text = "仅建议国内 API 走直连；已存在/重复的规则会自动跳过。";
            hint.AutoSize = true;
            hint.ForeColor = Color.FromArgb(120, 120, 120);
            hint.Location = new Point(14, 204);

            _chkSync.Text = "同步默认清单（换环境/重装也生效）";
            _chkSync.AutoSize = true;
            _chkSync.Location = new Point(14, 226);

            _btnCancel.Text = "取消";
            _btnCancel.Width = 88;
            _btnCancel.DialogResult = DialogResult.Cancel;
            _btnCancel.Location = new Point(438, 222);

            _btnOk.Text = "添加";
            _btnOk.Width = 88;
            _btnOk.Location = new Point(344, 222);
            _btnOk.Click += (s, e) => Submit();
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            Controls.Add(tip);
            Controls.Add(_input);
            Controls.Add(preLbl);
            Controls.Add(_preview);
            Controls.Add(hint);
            Controls.Add(_chkSync);
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            UpdatePreview();
        }

        private static string NormalizeRule(string raw)
        {
            raw = raw.Trim();
            int scheme = raw.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) { raw = new Uri(raw).Host; }
            raw = raw.TrimEnd('/');
            if (raw.StartsWith("*.")) { return raw; }
            if (raw.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(raw, @"^\d{1,3}(\.\d{1,3}){3}$")) { return raw; }
            string[] parts = raw.Split('.');
            if (parts.Length >= 3) { return "*." + string.Join(".", parts, 1, parts.Length - 1); }
            return "*." + raw;
        }

        private static List<string> ParseInput(string text)
        {
            var list = new List<string>();
            foreach (var raw in text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string u = raw.Trim();
                if (u.Length == 0) { continue; }
                try { list.Add(NormalizeRule(u)); } catch { }
            }
            return list;
        }

        private void UpdatePreview()
        {
            var rules = ParseInput(_input.Text);
            _preview.Text = rules.Count == 0 ? "（空）" : string.Join("  ", rules);
            _btnOk.Enabled = rules.Count > 0;
        }

        private void Submit()
        {
            var rules = ParseInput(_input.Text);
            if (rules.Count == 0)
            {
                MessageBox.Show("请先输入至少一个 API Base URL。", "添加直连 API", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string action = "AddDirect -Value " + Quote(string.Join(" ", rules)) + (_chkSync.Checked ? " -SyncDefaults" : "");
            string result = _runHelper(action);
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "添加直连 API", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _notify("直连已添加", SummaryOf(result) + "\n守护将在 35 秒内热重载生效。");
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string SummaryOf(string json)
        {
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var d = ser.DeserializeObject(json) as Dictionary<string, object>;
                int added = CountOf(d, "added");
                int skipped = CountOf(d, "skipped");
                return "新增 " + added + " 条" + (skipped > 0 ? "，已存在跳过 " + skipped + " 条" : "");
            }
            catch
            {
                return "已提交";
            }
        }

        private static int CountOf(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) { return 0; }
            object o = d[key];
            if (o is object[]) { return ((object[])o).Length; }
            if (o is System.Collections.ArrayList) { return ((System.Collections.ArrayList)o).Count; }
            return 0;
        }
    }
}
```

- [ ] **Step 2: 统一行尾 & 编码**

Run: 转换为 CRLF + UTF8 BOM，验证 `LF-only=0`

### Task 5: GuardianTray.cs 挂菜单项

**Files:**
- Modify: `src/GuardianTray.cs`（菜单构建处 + 新增方法）

- [ ] **Step 1: 菜单项（在 `menu.Items.Add("编辑配置", ...)` 前插入）**

```csharp
            menu.Items.Add("添加直连 API", null, (s, e) => ShowAddDirect());
            menu.Items.Add("编辑配置", null, (s, e) => EditConfig());
```

- [ ] **Step 2: 新增方法（放在 `EditConfig()` 方法之前）**

```csharp
        private void ShowAddDirect()
        {
            using (var f = new AddDirectForm(RunHelper, (title, msg) =>
            {
                _icon.ShowBalloonTip(3500, title, msg, ToolTipIcon.Info);
            }))
            {
                f.ShowDialog();
            }
            RefreshState();
        }
```

### Task 6: build-tray.ps1 编译双源文件

**Files:**
- Modify: `scripts/build-tray.ps1`

- [ ] **Step 1: 加源文件路径与增量检查**

将 `$src = Join-Path $root 'src\GuardianTray.cs'` 后追加：

```powershell
$src2 = Join-Path $root 'src\AddDirectForm.cs'
```

将增量判断改为同时检查两个源：

```powershell
if ((Test-Path -LiteralPath $out) -and -not $Force) {
    $srcTime = (Get-Item -LiteralPath $src).LastWriteTime
    $src2Time = (Get-Item -LiteralPath $src2).LastWriteTime
    $outTime = (Get-Item -LiteralPath $out).LastWriteTime
    if ($outTime -ge $srcTime -and $outTime -ge $src2Time) {
        "已是最新: $out"
        exit 0
    }
}
```

将 csc 编译行末尾追加 `"$src2"`：

```powershell
& $csc /nologo /target:winexe /utf8output /win32icon:"$(Join-Path $dist 'guardian.ico')" /out:"$out" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "$src" "$src2"
```

- [ ] **Step 2: 强制编译**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\build-tray.ps1 -Force`
Expected: `编译完成: ...\dist\GuardianTray.exe`，无 CS 错误

### Task 7: 重启托盘并验证

**Files:**
- 无（运行验证）

- [ ] **Step 1: 停旧托盘、启动新托盘**

Run（PowerShell）:
```powershell
Stop-Process -Name GuardianTray -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Start-Process -FilePath 'G:\AGENT\proxy\codex-proxy-daemon\dist\GuardianTray.exe'
Start-Sleep -Seconds 3
Get-Process -Name GuardianTray -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, StartTime
```
Expected: 新 PID 存在，托盘图标恢复，守护（GuardianDaemon）不受影响

- [ ] **Step 2: 请用户人工确认**

右键托盘图标 → 应看到「添加直连 API」→ 填入 `https://api.longcat.chat https://api.deepseek.com` → 预览显示 `*.longcat.chat *.deepseek.com` → 添加 → 气泡提示"新增 0 条，已存在跳过 2 条"

### Task 8: 文档同步

**Files:**
- Modify: `README.md`、`USAGE.md`

- [ ] **Step 1: README 常见问题补一条**

在"添加新的国内 API 直连"条目下追加一行：
```
  - 托盘入口：右键托盘图标 →「添加直连 API」，支持一次填写多个 Base URL（空格分隔）。
```

- [ ] **Step 2: USAGE.md 入口章节补说明**

在 `## 添加新的国内 API 直连（入口）` 的代码块后追加：
```markdown
托盘图形化入口：右键托盘图标 →「添加直连 API」→ 输入一个或多个 Base URL（空格/换行/逗号分隔）→ 预览规则 → 添加。
```

### Task 9: 最终验证与推送

**Files:**
- 无

- [ ] **Step 1: 全量回归**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test.ps1`
Expected: PASS=53 FAIL=0

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test-add-direct.ps1`
Expected: 全部通过

- [ ] **Step 2: 守护与托盘状态**

Run: `Get-Process -Name GuardianDaemon, GuardianTray -ErrorAction SilentlyContinue | Select-Object ProcessName, Id, StartTime`
Expected: 两者都在运行，StartTime 为今天重启后的时间

- [ ] **Step 3: Commit + Push**

```bash
git -C G:/AGENT/proxy/codex-proxy-daemon add -A
git -C G:/AGENT/proxy/codex-proxy-daemon commit -m "feat: 托盘图形化添加直连 API 表单"
git -C G:/AGENT/proxy/codex-proxy-daemon push origin HEAD
```
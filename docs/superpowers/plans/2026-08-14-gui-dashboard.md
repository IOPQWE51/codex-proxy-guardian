# 图形化主界面（浅色卡片仪表盘）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增浅色卡片式「概览」主窗口（顶部状态条 + 2×2 卡片 + 底部操作区），卡片点击进入白名单管理、节点切换、日志查看三个二级页；托盘双击/菜单打开主窗口。

**Architecture:** 新增 `src/GuardianMainForm.cs`（主窗体 + 自绘 `RoundedCard`）、`src/DirectListForm.cs`、`src/NodesForm.cs`、`src/LogViewForm.cs` 四个 WinForms 源文件；`GuardianTray.cs` 增加主窗口管理与菜单/双击入口；`tray-helper.ps1` 新增 `RemoveDirect` 动作（删除白名单）；`build-tray.ps1` 编译全部源文件。数据与操作全部复用现有 helper 动作。

**Tech Stack:** .NET Framework 4.8、WinForms + GDI+ 自绘、C# 5（csc 默认语言版本，无 local function/expression-bodied/range）、PowerShell 5.1（helper）、csc 编译。

---

### Task 1: tray-helper.ps1 新增 RemoveDirect 动作（TDD）

**Files:**
- Modify: `scripts/tray-helper.ps1`
- Test: `scripts/self-test-add-direct.ps1`（追加删除用例）

- [ ] **Step 1: 在 self-test-add-direct.ps1 的 finally 前追加删除用例（红）**

在测试脚本 `# --- 5) DryRun 不写入 ---` 块之后追加：

```powershell
    # --- 6) RemoveDirect 删除白名单 ---
    $cfg = Get-Content -LiteralPath (Join-Path $tmp 'config\daemon.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $cnt = @($cfg.directDomains).Count
    $delOut = powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tmp 'scripts\tray-helper.ps1') 'RemoveDirect' -Value '*.test1.cn,*.raw.example.com' 2>&1
    $delOut | Out-Null
    $cfg = Get-Content -LiteralPath (Join-Path $tmp 'config\daemon.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True (-not (@($cfg.directDomains) -contains '*.test1.cn')) 'RemoveDirect 删除 test1.cn'
    Assert-True (-not (@($cfg.directDomains) -contains '*.raw.example.com')) 'RemoveDirect 删除 raw.example.com'
    Assert-True ((@($cfg.directDomains).Count) -eq ($cnt - 2)) '删除后数量减少 2'
```

注意：该用例依赖临时根中存在 `scripts\tray-helper.ps1`，因此 Step 1 同时把项目内最新 tray-helper.ps1 复制进临时根（在现有 `Copy-Item add-direct.ps1` 行后追加一行 `Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\tray-helper.ps1') -Destination (Join-Path $tmp 'scripts\tray-helper.ps1')`）。

- [ ] **Step 2: 运行测试，确认删除用例失败（红）**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test-add-direct.ps1`
Expected: 在删除用例处 FAIL（`RemoveDirect 需要 Value 参数` / 未知操作）

- [ ] **Step 3: 实现 RemoveDirect 动作**

在 `tray-helper.ps1` 的 `'AddDirect'` 分支之后、`default` 之前插入：

```powershell
    'RemoveDirect' {
        if ([string]::IsNullOrEmpty($Value)) { throw 'RemoveDirect 需要 Value 参数' }
        $cfgPath2 = Join-Path $root 'config\daemon.config.json'
        $enc2 = New-Object System.Text.UTF8Encoding($true)
        $rules = @($Value -split '\s*,\s*' | Where-Object { $_ -ne '' })
        if ($rules.Count -eq 0) { throw '未提供任何规则' }
        $cfg = Get-Content -Raw -LiteralPath $cfgPath2 -Encoding UTF8 | ConvertFrom-Json
        $list = @($cfg.directDomains)
        $removed = @(); $missing = @()
        foreach ($r in $rules) {
            if ($list -contains $r) { $removed += $r } else { $missing += $r }
        }
        if ($removed.Count -gt 0) {
            $cfg.directDomains = @($list | Where-Object { $removed -notcontains $_ })
            $json = $cfg | ConvertTo-Json -Depth 20
            [IO.File]::WriteAllText($cfgPath2, $json, $enc2)
            $check = Get-Content -Raw -LiteralPath $cfgPath2 -Encoding UTF8 | ConvertFrom-Json
            $still = @($check.directDomains | Where-Object { $removed -contains $_ })
            if ($still.Count -gt 0) { throw "删除校验失败: $($still -join ',') 仍存在" }
        }
        [PSCustomObject]@{ ok = $true; removed = $removed; missing = $missing } | ConvertTo-Json -Compress
    }
```

- [ ] **Step 4: 统一行尾（CRLF + BOM）并运行测试（绿）**

Run: 同 Step 2。Expected: 全部 PASS（含新增删除用例），`结果: 全部通过`

- [ ] **Step 5: 真实项目冒烟（临时域不落地，只删除不存在的规则验证 JSON 返回）**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\tray-helper.ps1 RemoveDirect -Value '*.not-exist.cn'`
Expected: `{"ok":true,"missing":["*.not-exist.cn"],"removed":[]}`，config 未变

- [ ] **Step 6: Commit**

```bash
git -C G:/AGENT/proxy/codex-proxy-daemon add scripts/tray-helper.ps1 scripts/self-test-add-direct.ps1
git -C G:/AGENT/proxy/codex-proxy-daemon commit -m "feat: tray-helper 新增 RemoveDirect 动作"
```

### Task 2: 新建主窗体 GuardianMainForm.cs

**Files:**
- Create: `src/GuardianMainForm.cs`

- [ ] **Step 1: 创建文件（完整内容）**

```csharp
// Codex 代理守护 - 概览主窗体（浅色卡片仪表盘）
// 独立源文件，由 scripts\build-tray.ps1 一并编译（.NET Framework 4.8 WinForms，C#5 兼容）。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexProxyGuardian
{
    internal sealed class RoundedCard : Control
    {
        private bool _hover;
        private readonly System.Windows.Forms.Timer _t;

        public string TitleText { get; set; }
        public string ValueText { get; set; }
        public string HintText { get; set; }
        public Color Accent { get; set; }

        public event EventHandler CardClick;

        public RoundedCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Accent = Color.FromArgb(37, 99, 235);
            BackColor = Color.White;
            _t = new System.Windows.Forms.Timer();
            _t.Interval = 60;
            _t.Tick += (s, e) => { _t.Stop(); Refresh(); };
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; _t.Start(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _t.Start(); }
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            EventHandler h = CardClick;
            if (h != null) { h(this, EventArgs.Empty); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            r.Width--; r.Height--;
            using (GraphicsPath path = RoundRect(r, 10))
            {
                using (SolidBrush b = new SolidBrush(_hover ? Color.FromArgb(240, 246, 255) : Color.White))
                { g.FillPath(b, path); }
                using (Pen p = new Pen(Color.FromArgb(227, 231, 239)))
                { g.DrawPath(p, path); }
            }
            using (SolidBrush b = new SolidBrush(Accent))
            { g.FillRectangle(b, 18, 16, 4, 34); }
            using (Font f = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 98, 112)))
            { g.DrawString(TitleText, f, b, 30, 16); }
            using (Font f = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(25, 30, 40)))
            { g.DrawString(ValueText, f, b, 30, 40); }
            if (!string.IsNullOrEmpty(HintText))
            {
                using (Font f = new Font("Microsoft YaHei UI", 8.5F))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(130, 138, 150)))
                { g.DrawString(HintText, f, b, 30, 66); }
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class GuardianMainForm : Form
    {
        private readonly Func<string, string> _runHelper;
        private readonly Func<Form> _openAddDirect;
        private readonly Func<string, string, string, string> _openDirectList;
        private readonly Func<string> _openNodes;
        private readonly Func<string> _openLog;
        private Dictionary<string, string> _state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.Timer _timer;
        private bool _refreshing;

        private Label _badge;
        private Label _headText;
        private RoundedCard _cardDirect;
        private RoundedCard _cardNode;
        private RoundedCard _cardLog;
        private RoundedCard _cardDaemon;
        private Button _btnDaemon;
        private Button _btnAutoStart;

        public GuardianMainForm(Func<string, string> runHelper,
            Func<Form> openAddDirect,
            Func<string, string, string, string> openDirectList,
            Func<string> openNodes,
            Func<string> openLog)
        {
            _runHelper = runHelper;
            _openAddDirect = openAddDirect;
            _openDirectList = openDirectList;
            _openNodes = openNodes;
            _openLog = openLog;

            Text = "Codex 代理助手";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(720, 540);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildHeader();
            BuildCards();
            BuildActions();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15000;
            _timer.Tick += (s, e) => RefreshStateAsync();
            _timer.Start();
            RefreshStateAsync();
        }

        private void BuildHeader()
        {
            var bar = new Panel();
            bar.BackColor = Color.White;
            bar.Bounds = new Rectangle(0, 0, 720, 58);
            bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(bar);

            _badge = new Label();
            _badge.AutoSize = true;
            _badge.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            _badge.Location = new Point(22, 12);
            bar.Controls.Add(_badge);

            _headText = new Label();
            _headText.AutoSize = true;
            _headText.Font = new Font("Microsoft YaHei UI", 9F);
            _headText.ForeColor = Color.FromArgb(90, 98, 112);
            _headText.Location = new Point(22, 36);
            bar.Controls.Add(_headText);

            var refresh = new Button();
            refresh.Text = "刷新";
            refresh.Width = 76;
            refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refresh.Location = new Point(628, 16);
            refresh.Click += (s, e) => RefreshStateAsync();
            bar.Controls.Add(refresh);
        }

        private void BuildCards()
        {
            _cardDirect = new RoundedCard();
            _cardDirect.Bounds = new Rectangle(22, 74, 332, 104);
            _cardDirect.TitleText = "直连白名单";
            _cardDirect.HintText = "点击管理境内 API 直连";
            _cardDirect.CardClick += (s, e) => ShowDirectList();
            Controls.Add(_cardDirect);

            _cardNode = new RoundedCard();
            _cardNode.Bounds = new Rectangle(366, 74, 332, 104);
            _cardNode.TitleText = "节点";
            _cardNode.HintText = "点击切换 Clash 节点";
            _cardNode.CardClick += (s, e) => ShowNodes();
            Controls.Add(_cardNode);

            _cardLog = new RoundedCard();
            _cardLog.Bounds = new Rectangle(22, 190, 332, 104);
            _cardLog.TitleText = "日志";
            _cardLog.HintText = "点击查看最近日志";
            _cardLog.CardClick += (s, e) => ShowLog();
            Controls.Add(_cardLog);

            _cardDaemon = new RoundedCard();
            _cardDaemon.Bounds = new Rectangle(366, 190, 332, 104);
            _cardDaemon.TitleText = "守护";
            _cardDaemon.HintText = "";
            _cardDaemon.CardClick += (s, e) => { };
            Controls.Add(_cardDaemon);

            _btnDaemon = new Button();
            _btnDaemon.Text = "启动守护";
            _btnDaemon.Width = 96;
            _btnDaemon.Location = new Point(388, 236);
            _btnDaemon.Click += (s, e) => ToggleDaemon();
            Controls.Add(_btnDaemon);

            _btnAutoStart = new Button();
            _btnAutoStart.Text = "开机自启";
            _btnAutoStart.Width = 96;
            _btnAutoStart.Location = new Point(492, 236);
            _btnAutoStart.Click += (s, e) => ToggleAutoStart();
            Controls.Add(_btnAutoStart);
        }

        private void BuildActions()
        {
            string[] labels = { "添加直连 API", "只读检测", "重启 Codex", "编辑配置" };
            int x = 22;
            foreach (string label in labels)
            {
                var b = new Button();
                b.Text = label;
                b.Width = 120;
                b.Height = 32;
                b.Location = new Point(x, 330);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 230);
                b.Click += (s, e) => ActionClick(label);
                Controls.Add(b);
                x += 132;
            }

            var status = new Label();
            status.Text = "环境变量：HTTP/HTTPS/ALL/NO_PROXY 由守护自动维护 · 日志上限 200MB";
            status.AutoSize = true;
            status.ForeColor = Color.FromArgb(130, 138, 150);
            status.Location = new Point(22, 384);
            Controls.Add(status);
        }

        private void ActionClick(string label)
        {
            if (label == "添加直连 API") { if (_openAddDirect != null) { _openAddDirect(); } }
            else if (label == "只读检测") { RunDetect(); }
            else if (label == "重启 Codex") { RestartCodex(); }
            else { EditConfig(); }
        }

        private void ShowDirectList()
        {
            if (_openDirectList != null)
            {
                _openDirectList("直连白名单", _runHelper("State"), "");
            }
        }

        private void ShowNodes() { if (_openNodes != null) { _openNodes(); } }
        private void ShowLog() { if (_openLog != null) { _openLog(); } }

        private void ToggleDaemon()
        {
            string task = Get(_state, "task", "");
            string result = _runHelper(task == "Running" ? "Stop" : "Start");
            if (result.StartsWith("ERR=")) { MessageBox.Show(result.Substring(4), "Codex 代理助手", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshStateAsync();
        }

        private void ToggleAutoStart()
        {
            string task = Get(_state, "task", "");
            string result = _runHelper(task == "Disabled" ? "Enable" : "Disable");
            if (result.StartsWith("ERR=")) { MessageBox.Show(result.Substring(4), "Codex 代理助手", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshStateAsync();
        }

        private void RunDetect()
        {
            string result = _runHelper("Detect");
            MessageBox.Show(result, "只读检测", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RestartCodex()
        {
            var r = MessageBox.Show("重启将关闭当前 Codex 窗口和对话，确定吗？", "Codex 代理助手", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) { _runHelper("RestartCodex"); }
        }

        private void EditConfig()
        {
            string cfg = FindConfigPath();
            if (cfg != null && System.IO.File.Exists(cfg)) { System.Diagnostics.Process.Start("notepad.exe", "\"" + cfg + "\""); }
            else { MessageBox.Show("未找到配置文件", "Codex 代理助手", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private static string FindConfigPath()
        {
            string root = FindRoot();
            return root == null ? null : System.IO.Path.Combine(root, "config", "daemon.config.json");
        }

        private static string FindRoot()
        {
            string home = Environment.GetEnvironmentVariable("CODEX_PROXY_GUARDIAN_HOME");
            if (IsRoot(home)) { return home; }
            string exeDir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            if (exeDir != null)
            {
                if (IsRoot(exeDir)) { return exeDir; }
                string parent = System.IO.Path.GetDirectoryName(exeDir);
                if (IsRoot(parent)) { return parent; }
            }
            return null;
        }

        private static bool IsRoot(string root)
        {
            return !string.IsNullOrEmpty(root) &&
                   System.IO.File.Exists(System.IO.Path.Combine(root, "scripts", "codex-proxy-guardian.ps1"));
        }

        private void RefreshStateAsync()
        {
            if (_refreshing) { return; }
            _refreshing = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                string text = _runHelper("State");
                try
                {
                    if (IsDisposed) { return; }
                    Invoke((Action)(() =>
                    {
                        _state = ParseFlat(text);
                        ApplyState();
                        _refreshing = false;
                    }));
                }
                catch { _refreshing = false; }
            });
        }

        private void ApplyState()
        {
            string up = Get(_state, "proxyUp", "");
            if (up == "True")
            {
                _badge.Text = "● 代理在线";
                _badge.ForeColor = Color.FromArgb(42, 107, 72);
                _headText.Text = "端口 " + Get(_state, "port", "?") + " · " + Get(_state, "node", "-") + " · " + Get(_state, "mode", "-");
            }
            else if (up == "False")
            {
                _badge.Text = "● 代理已关闭";
                _badge.ForeColor = Color.FromArgb(216, 80, 80);
                _headText.Text = "直连模式";
            }
            else
            {
                _badge.Text = "○ 状态未知";
                _badge.ForeColor = Color.FromArgb(130, 138, 150);
                _headText.Text = Get(_state, "message", "-");
            }

            _cardDirect.ValueText = Get(_state, "directCount", "-") + " 家境内 API";
            _cardNode.ValueText = Get(_state, "node", "-");
            _cardLog.ValueText = "最近日志";
            string task = Get(_state, "task", "Unknown");
            _cardDaemon.ValueText = task == "Running" ? "运行中" : (task == "Disabled" ? "自启已暂停" : task);
            _btnDaemon.Text = task == "Running" ? "停止守护" : "启动守护";
            _btnAutoStart.Text = task == "Disabled" ? "恢复自启" : "开机自启";
            _btnAutoStart.Enabled = !(task == "NotInstalled");
        }

        private static Dictionary<string, string> ParseFlat(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) { return d; }
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                int idx = line.IndexOf('=');
                if (idx <= 0) { continue; }
                d[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string key, string fallback)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : fallback;
        }
    }
}
```

说明：`directCount` 由 Task 3 的 helper State 扩展提供（Task 3 Step 2 会在 tray-helper State 输出中追加一行 `directCount=N`）。主窗体卡片数字依赖它；Task 3 完成前该行缺失时显示 "-"，不报错。

- [ ] **Step 2: 统一行尾（CRLF + BOM）**
### Task 3: helper State 扩展 directCount + 白名单管理页 DirectListForm.cs

**Files:**
- Modify: `scripts/tray-helper.ps1`（State 动作追加 directCount 输出）
- Create: `src/DirectListForm.cs`

- [ ] **Step 1: tray-helper.ps1 State 动作输出 directCount**

在 State 动作中 `foreach ($v in 'HTTP_PROXY', ...)` 循环之前插入：

```powershell
        try {
            $c = Get-Content -Raw -LiteralPath $cfgPath -Encoding UTF8 | ConvertFrom-Json
            "directCount=" + @($c.directDomains).Count
        } catch { 'directCount=0' }
```

- [ ] **Step 2: 创建 src/DirectListForm.cs（完整内容）**

```csharp
// Codex 代理守护 - 直连白名单管理页（模态）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CodexProxyGuardian
{
    internal sealed class DirectListForm : Form
    {
        private readonly Func<string, string> _runHelper;
        private readonly Func<Form> _openAdd;
        private readonly ListView _list;

        public DirectListForm(Func<string, string> runHelper, Func<Form> openAdd)
        {
            _runHelper = runHelper;
            _openAdd = openAdd;

            Text = "直连白名单";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 420);
            Font = new Font("Microsoft YaHei UI", 9F);

            _list = new ListView();
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.Columns.Add("直连规则", 430);
            _list.Bounds = new Rectangle(14, 14, 492, 320);
            _list.MultiSelect = false;
            Controls.Add(_list);

            var add = new Button();
            add.Text = "添加";
            add.Width = 96;
            add.Location = new Point(14, 346);
            add.Click += (s, e) => AddDirect();
            Controls.Add(add);

            var del = new Button();
            del.Text = "删除所选";
            del.Width = 96;
            del.Location = new Point(120, 346);
            del.Click += (s, e) => RemoveDirect();
            Controls.Add(del);

            var refresh = new Button();
            refresh.Text = "刷新";
            refresh.Width = 96;
            refresh.Location = new Point(226, 346);
            refresh.Click += (s, e) => LoadList();
            Controls.Add(refresh);

            var close = new Button();
            close.Text = "关闭";
            close.Width = 96;
            close.DialogResult = DialogResult.Cancel;
            close.Location = new Point(410, 346);
            Controls.Add(close);

            LoadList();
        }

        private void LoadList()
        {
            _list.Items.Clear();
            var rules = ReadDirectDomains();
            foreach (string r in rules)
            {
                _list.Items.Add(r);
            }
        }

        private List<string> ReadDirectDomains()
        {
            var list = new List<string>();
            try
            {
                string cfg = FindConfigPath();
                if (cfg == null || !File.Exists(cfg)) { return list; }
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var d = ser.DeserializeObject(File.ReadAllText(cfg, Encoding.UTF8)) as Dictionary<string, object>;
                if (d != null && d.ContainsKey("directDomains"))
                {
                    var arr = d["directDomains"] as object[];
                    if (arr != null)
                    {
                        foreach (object o in arr) { list.Add(Convert.ToString(o)); }
                    }
                }
            }
            catch { }
            return list;
        }

        private static string FindConfigPath()
        {
            string root = FindRoot();
            return root == null ? null : Path.Combine(root, "config", "daemon.config.json");
        }

        private static string FindRoot()
        {
            string home = Environment.GetEnvironmentVariable("CODEX_PROXY_GUARDIAN_HOME");
            if (IsRoot(home)) { return home; }
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (exeDir != null)
            {
                if (IsRoot(exeDir)) { return exeDir; }
                string parent = Path.GetDirectoryName(exeDir);
                if (IsRoot(parent)) { return parent; }
            }
            return null;
        }

        private static bool IsRoot(string root)
        {
            return !string.IsNullOrEmpty(root) &&
                   File.Exists(Path.Combine(root, "scripts", "codex-proxy-guardian.ps1"));
        }

        private void AddDirect()
        {
            if (_openAdd != null)
            {
                using (Form f = _openAdd()) { f.ShowDialog(); }
                LoadList();
            }
        }

        private void RemoveDirect()
        {
            if (_list.SelectedItems.Count == 0) { return; }
            string rule = _list.SelectedItems[0].Text;
            var r = MessageBox.Show("确定从直连白名单删除 " + rule + " 吗？\n守护将在 35 秒内热重载生效。",
                "删除直连规则", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { return; }
            string result = _runHelper("RemoveDirect -Value " + Quote(rule));
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                LoadList();
            }
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
```

- [ ] **Step 3: 统一行尾（CRLF + BOM）**

### Task 4: 节点切换页 NodesForm.cs

**Files:**
- Create: `src/NodesForm.cs`

- [ ] **Step 1: 创建文件（完整内容）**

```csharp
// Codex 代理守护 - 节点切换页（模态）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CodexProxyGuardian
{
    internal sealed class NodesForm : Form
    {
        private readonly Func<string, string> _runHelper;
        private readonly ListBox _groups;
        private readonly ListBox _nodes;
        private readonly Label _tip;
        private List<string> _groupNames = new List<string>();

        public NodesForm(Func<string, string> runHelper)
        {
            _runHelper = runHelper;

            Text = "切换节点";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 420);
            Font = new Font("Microsoft YaHei UI", 9F);

            var gl = new Label();
            gl.Text = "选择器组";
            gl.Location = new Point(14, 12);
            gl.AutoSize = true;
            Controls.Add(gl);

            _groups = new ListBox();
            _groups.Bounds = new Rectangle(14, 34, 200, 340);
            _groups.SelectedIndexChanged += (s, e) => LoadNodes();
            Controls.Add(_groups);

            var nl = new Label();
            nl.Text = "节点";
            nl.Location = new Point(228, 12);
            nl.AutoSize = true;
            Controls.Add(nl);

            _nodes = new ListBox();
            _nodes.Bounds = new Rectangle(228, 34, 318, 340);
            _nodes.DoubleClick += (s, e) => SwitchSelected();
            Controls.Add(_nodes);

            _tip = new Label();
            _tip.Text = "双击节点切换";
            _tip.AutoSize = true;
            _tip.ForeColor = Color.FromArgb(130, 138, 150);
            _tip.Location = new Point(14, 384);
            Controls.Add(_tip);

            LoadGroups();
        }

        private void LoadGroups()
        {
            _groups.Items.Clear();
            _groupNames.Clear();
            string json = _runHelper("Nodes");
            if (json.StartsWith("ERR="))
            {
                _groups.Items.Add(json.Substring(4));
                return;
            }
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var d = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("groups")) { return; }
                var groups = d["groups"] as object[];
                if (groups == null) { return; }
                foreach (object o in groups)
                {
                    var g = o as Dictionary<string, object>;
                    if (g == null) { continue; }
                    string name = Convert.ToString(g["name"]);
                    _groupNames.Add(name);
                    _groups.Items.Add(name);
                }
                if (_groups.Items.Count > 0) { _groups.SelectedIndex = 0; }
            }
            catch { }
        }

        private void LoadNodes()
        {
            _nodes.Items.Clear();
            if (_groups.SelectedIndex < 0) { return; }
            string group = _groupNames[_groups.SelectedIndex];
            string json = _runHelper("Nodes");
            if (json.StartsWith("ERR=")) { _nodes.Items.Add(json.Substring(4)); return; }
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var d = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("groups")) { return; }
                var groups = d["groups"] as object[];
                foreach (object o in groups)
                {
                    var g = o as Dictionary<string, object>;
                    if (g == null || Convert.ToString(g["name"]) != group) { continue; }
                    string now = Convert.ToString(g["now"]);
                    var opts = g["options"] as object[];
                    if (opts == null) { return; }
                    foreach (object op in opts)
                    {
                        string node = Convert.ToString(op);
                        _nodes.Items.Add(node == now ? node + "（当前）" : node);
                    }
                    break;
                }
            }
            catch { }
        }

        private void SwitchSelected()
        {
            if (_groups.SelectedIndex < 0 || _nodes.SelectedIndex < 0) { return; }
            string group = _groupNames[_groups.SelectedIndex];
            string node = _nodes.SelectedItem.ToString();
            if (node.EndsWith("（当前）", StringComparison.Ordinal)) { return; }
            string result = _runHelper("SwitchNode " + Quote(group) + " " + Quote(node));
            if (result.StartsWith("ERR="))
            {
                _tip.Text = "切换失败：" + result.Substring(4);
                _tip.ForeColor = Color.FromArgb(216, 80, 80);
            }
            else
            {
                _tip.Text = "已切换 " + group + " → " + node;
                _tip.ForeColor = Color.FromArgb(42, 107, 72);
                LoadGroups();
            }
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
```

- [ ] **Step 2: 统一行尾（CRLF + BOM）**

### Task 5: 日志查看页 LogViewForm.cs

**Files:**
- Create: `src/LogViewForm.cs`

- [ ] **Step 1: 创建文件（完整内容）**

```csharp
// Codex 代理守护 - 日志查看页（模态，只读）

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CodexProxyGuardian
{
    internal sealed class LogViewForm : Form
    {
        private readonly TextBox _box;

        public LogViewForm()
        {
            Text = "最近日志";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(720, 480);
            Font = new Font("Microsoft YaHei UI", 9F);

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.Font = new Font("Consolas", 9.5F);
            _box.Bounds = new Rectangle(14, 14, 692, 400);
            Controls.Add(_box);

            var refresh = new Button();
            refresh.Text = "刷新";
            refresh.Width = 96;
            refresh.Location = new Point(14, 428);
            refresh.Click += (s, e) => LoadLog();
            Controls.Add(refresh);

            var close = new Button();
            close.Text = "关闭";
            close.Width = 96;
            close.DialogResult = DialogResult.Cancel;
            close.Location = new Point(610, 428);
            Controls.Add(close);

            LoadLog();
        }

        private void LoadLog()
        {
            try
            {
                string log = FindLogPath();
                if (log == null || !File.Exists(log))
                {
                    _box.Text = "日志文件不存在：" + log;
                    return;
                }
                string[] lines = File.ReadAllLines(log, Encoding.UTF8);
                int take = Math.Min(200, lines.Length);
                var sb = new StringBuilder();
                for (int i = lines.Length - take; i < lines.Length; i++) { sb.AppendLine(lines[i]); }
                _box.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                _box.Text = "读取日志失败：" + ex.Message;
            }
        }

        private static string FindLogPath()
        {
            string root = FindRoot();
            return root == null ? null : Path.Combine(root, "logs", "daemon.log");
        }

        private static string FindRoot()
        {
            string home = Environment.GetEnvironmentVariable("CODEX_PROXY_GUARDIAN_HOME");
            if (IsRoot(home)) { return home; }
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (exeDir != null)
            {
                if (IsRoot(exeDir)) { return exeDir; }
                string parent = Path.GetDirectoryName(exeDir);
                if (IsRoot(parent)) { return parent; }
            }
            return null;
        }

        private static bool IsRoot(string root)
        {
            return !string.IsNullOrEmpty(root) &&
                   File.Exists(Path.Combine(root, "scripts", "codex-proxy-guardian.ps1"));
        }
    }
}
```

- [ ] **Step 2: 统一行尾（CRLF + BOM）**

### Task 6: GuardianTray.cs 集成主窗口

**Files:**
- Modify: `src/GuardianTray.cs`

- [ ] **Step 1: 添加主窗口字段与打开方法**

在 `private bool _refreshing;` 后加：

```csharp
        private GuardianMainForm _mainForm;
```

在 `ExitApp()` 方法前插入：

```csharp
        private void ShowMainForm()
        {
            if (_mainForm == null || _mainForm.IsDisposed)
            {
                _mainForm = new GuardianMainForm(
                    RunHelper,
                    () => BuildAddDirectForm(),
                    (title, state, extra) => { using (var f = new DirectListForm(RunHelper, BuildAddDirectForm)) { f.ShowDialog(); } return ""; },
                    () => { using (var f = new NodesForm(RunHelper)) { f.ShowDialog(); } return ""; },
                    () => { using (var f = new LogViewForm()) { f.ShowDialog(); } return ""; });
                _mainForm.FormClosed += (s, e) => _mainForm = null;
            }
            _mainForm.Show();
            _mainForm.Activate();
        }

        private Form BuildAddDirectForm()
        {
            return new AddDirectForm(RunHelper, (title, msg) =>
            {
                _icon.ShowBalloonTip(3500, title, msg, ToolTipIcon.Info);
            });
        }
```

- [ ] **Step 2: 双击图标打开主窗口**

将 `_icon.DoubleClick += (s, e) => ShowStatus();` 替换为：

```csharp
            _icon.DoubleClick += (s, e) => ShowMainForm();
```

- [ ] **Step 3: 菜单加入口**

在 `menu.Items.Add("状态详情", ...)` 前插入：

```csharp
            menu.Items.Add("打开主界面", null, (s, e) => ShowMainForm());
```

### Task 7: build-tray.ps1 编译全部新源文件

**Files:**
- Modify: `scripts/build-tray.ps1`

- [ ] **Step 1: 扩展源文件列表与增量检查**

将 `$src2 = Join-Path $root 'src\AddDirectForm.cs'` 替换为：

```powershell
$src2 = Join-Path $root 'src\AddDirectForm.cs'
$src3 = Join-Path $root 'src\GuardianMainForm.cs'
$src4 = Join-Path $root 'src\DirectListForm.cs'
$src5 = Join-Path $root 'src\NodesForm.cs'
$src6 = Join-Path $root 'src\LogViewForm.cs'
```

增量检查改为检查全部源：

```powershell
    $times = @((Get-Item -LiteralPath $src).LastWriteTime,
               (Get-Item -LiteralPath $src2).LastWriteTime,
               (Get-Item -LiteralPath $src3).LastWriteTime,
               (Get-Item -LiteralPath $src4).LastWriteTime,
               (Get-Item -LiteralPath $src5).LastWriteTime,
               (Get-Item -LiteralPath $src6).LastWriteTime)
    if ($outTime -ge ($times | Measure-Object -Maximum).Maximum) {
```

编译行追加全部源：

```powershell
& $csc /nologo /target:winexe /utf8output /win32icon:"$(Join-Path $dist 'guardian.ico')" /out:"$out" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "$src" "$src2" "$src3" "$src4" "$src5" "$src6"
```

- [ ] **Step 2: 停托盘、强制编译、启动托盘**

Run:
```powershell
Stop-Process -Name GuardianTray -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\build-tray.ps1 -Force
Start-Process -FilePath 'G:\AGENT\proxy\codex-proxy-daemon\dist\GuardianTray.exe'
Start-Sleep -Seconds 4
Get-Process -Name GuardianTray, GuardianDaemon -ErrorAction SilentlyContinue | Select-Object ProcessName, Id
```
Expected: 编译无 CS 错误；两个进程均在运行。

- [ ] **Step 3: 冒烟验证主窗口打开**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process 'G:\AGENT\proxy\codex-proxy-daemon\dist\GuardianTray.exe'"`（托盘已在运行则忽略）
人工确认：双击托盘图标可打开主窗口；状态条/四卡片显示数据；点击卡片打开三个二级页；守护卡启停/自启按钮可用；底部四个操作按钮可用。

### Task 8: 文档同步

**Files:**
- Modify: `README.md`、`USAGE.md`

- [ ] **Step 1: README 特性与托盘说明补充主窗口**

在 README「托盘控制台」段落补一行：

```markdown
- **图形化主界面**：双击托盘图标（或菜单「打开主界面」）打开浅色卡片仪表盘：代理状态条 + 直连白名单/节点/日志/守护四卡片 + 常用操作；卡片点击进入管理页（白名单增删、节点切换、日志查看）。
```

- [ ] **Step 2: USAGE.md 补主窗口说明**

在 USAGE.md「Tray console」小节追加：

```markdown
- **图形化主界面**：双击托盘图标打开；提供状态条、四张卡片（白名单/节点/日志/守护）与常用操作按钮。
```

### Task 9: 全量回归 + 提交推送

- [ ] **Step 1: 回归**

Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test.ps1` → PASS=53 FAIL=0
Run: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\self-test-add-direct.ps1` → 全部通过

- [ ] **Step 2: 进程与配置状态**

Run: `Get-Process -Name GuardianDaemon, GuardianTray` → 均在运行；`config\daemon.config.json` directDomains 仍 13 家且无测试域名。

- [ ] **Step 3: Commit + Push**

```bash
git -C G:/AGENT/proxy/codex-proxy-daemon add -A
git -C G:/AGENT/proxy/codex-proxy-daemon commit -m "feat: 图形化主界面（浅色卡片仪表盘）+ 白名单管理/节点切换/日志页"
git -C G:/AGENT/proxy/codex-proxy-daemon push origin HEAD
```
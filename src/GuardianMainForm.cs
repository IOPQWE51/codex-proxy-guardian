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
            TitleText = "";
            ValueText = "加载中…";
            HintText = "";
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
            { g.DrawString(TitleText ?? "", f, b, 30, 16); }
            using (Font f = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(25, 30, 40)))
            { g.DrawString(ValueText ?? "", f, b, 30, 40); }
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
            DoubleBuffered = true;

            BuildHeader();
            BuildCards();
            BuildActions();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15000;
            _timer.Tick += (s, e) => RefreshStateAsync();
            _timer.Start();
            Shown += (s, e) => RefreshStateAsync();
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
            _badge.Text = "○ 状态检测中…";
            _badge.ForeColor = Color.FromArgb(130, 138, 150);
            bar.Controls.Add(_badge);

            _headText = new Label();
            _headText.AutoSize = true;
            _headText.Font = new Font("Microsoft YaHei UI", 9F);
            _headText.ForeColor = Color.FromArgb(90, 98, 112);
            _headText.Location = new Point(22, 36);
            _headText.Text = "正在读取守护状态…";
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
            _cardDirect.Invalidate();
            _cardNode.Invalidate();
            _cardLog.Invalidate();
            _cardDaemon.Invalidate();
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
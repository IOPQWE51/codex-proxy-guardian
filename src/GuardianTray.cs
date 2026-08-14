// Codex 代理守护 - 托盘控制台（轻量 GUI）
// 由 scripts\build-tray.ps1 编译，目标 .NET Framework 4.8（Win10/11 自带，无运行时依赖）。
// 功能：状态查看、启停守护、开机自启开关、只读检测、节点切换、重启Codex、日志/配置目录、安装/卸载。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace CodexProxyGuardian
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (new MutexHolder("CodexProxyGuardianTrayMutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("托盘控制台已在运行。", "Codex 代理守护");
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApp());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("托盘程序出错：\n" + ex, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private sealed class MutexHolder : IDisposable
        {
            private readonly System.Threading.Mutex _m;
            public MutexHolder(string name, out bool createdNew)
            {
                _m = new System.Threading.Mutex(true, name, out createdNew);
            }
            public void Dispose()
            {
                try { _m.ReleaseMutex(); } catch { }
                _m.Dispose();
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _miStartStop;
        private readonly ToolStripMenuItem _miAutoStart;
        private readonly ToolStripMenuItem _miTrayAutoStart;
        private readonly ToolStripMenuItem _miInstall;
        private readonly ToolStripMenuItem _miUninstall;
        private readonly ToolStripMenuItem _miNodes;
        private readonly ToolStripMenuItem _miRestartCodex;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly string _root;
        private readonly string _helper;
        private readonly string _logDir;
        private readonly string _configDir;

        private Dictionary<string, string> _state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _lastProxyUp = "";
        private bool _refreshing;

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayApp()
        {
            _root = FindRoot();
            _helper = _root == null ? null : Path.Combine(_root, "scripts", "tray-helper.ps1");
            _logDir = _root == null ? null : Path.Combine(_root, "logs");
            _configDir = _root == null ? null : Path.Combine(_root, "config");

            var miHeader = new ToolStripMenuItem("Codex 代理守护");
            miHeader.Enabled = false;
            miHeader.Font = new Font(SystemFonts.MenuFont, FontStyle.Bold);

            _miStartStop = new ToolStripMenuItem("启动守护", null, (s, e) => ToggleDaemon());
            _miAutoStart = new ToolStripMenuItem("暂停开机自启", null, (s, e) => ToggleTaskAutoStart());
            _miTrayAutoStart = new ToolStripMenuItem("托盘开机自启", null, (s, e) => ToggleTrayAutoStart());
            _miTrayAutoStart.CheckOnClick = true;
            _miInstall = new ToolStripMenuItem("安装守护（注册自启任务）", null, (s, e) => InstallDaemon());
            _miUninstall = new ToolStripMenuItem("卸载守护任务", null, (s, e) => UninstallDaemon());
            _miNodes = new ToolStripMenuItem("切换节点");
            _miNodes.DropDownOpening += (s, e) => LoadNodes();
            _miRestartCodex = new ToolStripMenuItem("重启 Codex 应用", null, (s, e) => RestartCodex());

            var menu = new ContextMenuStrip();
            menu.Items.Add(miHeader);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("状态详情", null, (s, e) => ShowStatus());
            menu.Items.Add("只读检测", null, (s, e) => RunDetect());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miNodes);
            menu.Items.Add(_miRestartCodex);
            menu.Items.Add(_miStartStop);
            menu.Items.Add(_miAutoStart);
            menu.Items.Add(_miTrayAutoStart);
            menu.Items.Add(_miInstall);
            menu.Items.Add(_miUninstall);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("查看日志", null, (s, e) => ViewLog());
            menu.Items.Add("打开日志目录", null, (s, e) => OpenFolder(_logDir));
            menu.Items.Add("添加直连 API", null, (s, e) => ShowAddDirect());
            menu.Items.Add("编辑配置", null, (s, e) => EditConfig());
            menu.Items.Add("打开配置目录", null, (s, e) => OpenFolder(_configDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            menu.CreateControl();
            menu.Opening += (s, e) => RefreshStateAsync();

            _icon = new NotifyIcon();
            _icon.Icon = MakeIcon(Color.FromArgb(150, 150, 150));
            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.Text = "Codex 代理守护";
            _icon.DoubleClick += (s, e) => ShowStatus();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15000;
            _timer.Tick += (s, e) => RefreshStateAsync();
            _timer.Start();

            RefreshState();
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

        private string RunHelper(string action)
        {
            if (_helper == null)
            {
                return "ERR=找不到守护目录。请把托盘程序放在项目 dist 目录，或设置环境变量 CODEX_PROXY_GUARDIAN_HOME 指向项目根目录。";
            }
            try
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + _helper + "\" " + action
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0 && stdout.Trim().Length == 0)
                    {
                        return "ERR=" + (stderr.Trim().Length > 0 ? stderr.Trim() : "exit code " + p.ExitCode);
                    }
                    return stdout.Trim();
                }
            }
            catch (Exception ex)
            {
                return "ERR=" + ex.Message;
            }
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

        private void RefreshState()
        {
            ApplyState(RunHelper("State"));
        }

        private void RefreshStateAsync()
        {
            if (_refreshing) { return; }
            _refreshing = true;
            ContextMenuStrip ctx = _icon.ContextMenuStrip;
            System.Threading.Tasks.Task.Run(() =>
            {
                string text = RunHelper("State");
                try
                {
                    if (ctx == null || ctx.IsDisposed)
                    {
                        _refreshing = false;
                        return;
                    }
                    ctx.Invoke((Action)(() =>
                    {
                        try { ApplyState(text); }
                        finally { _refreshing = false; }
                    }));
                }
                catch
                {
                    _refreshing = false;
                }
            });
        }

        private void ApplyState(string text)
        {
            _state = ParseFlat(text);


            string task = Get(_state, "task", "Unknown");
            string message = Get(_state, "message", "");
            string proxyUp = Get(_state, "proxyUp", "");

            string tooltip = "Codex 代理守护 - " + TaskLabel(task);
            if (message.Length > 0) { tooltip += "\n" + message; }
            _icon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;

            if (_lastProxyUp.Length > 0 && proxyUp.Length > 0 && _lastProxyUp != proxyUp)
            {
                string title = proxyUp == "True" ? "代理已上线" : "代理已下线";
                _icon.ShowBalloonTip(4000, title, message.Length > 0 ? message : proxyUp, ToolTipIcon.Info);
            }
            _lastProxyUp = proxyUp;

            _miStartStop.Text = task == "Running" ? "停止守护" : "启动守护";
            _miAutoStart.Text = task == "Disabled" ? "恢复开机自启" : "暂停开机自启";
            _miInstall.Visible = task == "NotInstalled";
            Icon prevIcon = _icon.Icon;
            _icon.Icon = MakeIcon(StatusColor());
            if (prevIcon != null) { prevIcon.Dispose(); }
            _miUninstall.Visible = task != "NotInstalled";
            _miTrayAutoStart.Checked = IsTrayAutoStart();
        }

        private static string TaskLabel(string task)
        {
            switch (task)
            {
                case "Running": return "运行中";
                case "Ready": return "已就绪（未运行）";
                case "Disabled": return "自启已暂停";
                case "NotInstalled": return "未安装";
                default: return task;
            }
        }

        private void ShowStatus()
        {
            RefreshState();
            using (var f = new Form())
            {
                f.Text = "代理守护 · 状态";
                f.StartPosition = FormStartPosition.CenterScreen;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.Width = 560;

                var layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.ColumnCount = 2;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.Padding = new Padding(18, 14, 18, 4);
                layout.AutoScroll = true;

                string up = Get(_state, "proxyUp", "");
                string headText;
                Color headColor;
                if (up == "True") { headText = "代理在线 · 端口 " + Get(_state, "port", "?"); headColor = Color.FromArgb(34, 160, 95); }
                else if (up == "False") { headText = "代理已关闭 · 直连模式"; headColor = Color.FromArgb(216, 80, 80); }
                else { headText = "状态未知"; headColor = Color.FromArgb(130, 130, 130); }

                var head = new Label();
                head.Text = headText;
                head.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
                head.ForeColor = headColor;
                head.AutoSize = true;
                head.Margin = new Padding(0, 2, 0, 14);
                layout.Controls.Add(head, 0, 0);
                layout.SetColumnSpan(head, 2);

                int row = 1;
                AddRow(layout, ref row, "守护任务", TaskLabel(Get(_state, "task", "Unknown")));
                AddRow(layout, ref row, "版本", Get(_state, "version", "-"));
                AddRow(layout, ref row, "节点", Get(_state, "node", "-"));
                AddRow(layout, ref row, "模式", Get(_state, "mode", "-"));
                AddRow(layout, ref row, "下次检测", Get(_state, "nextCheck", "-"));
                AddRow(layout, ref row, "消息", Get(_state, "message", "-"));
                AddRow(layout, ref row, "HTTP_PROXY", EnvOrDash("HTTP_PROXY"));
                AddRow(layout, ref row, "HTTPS_PROXY", EnvOrDash("HTTPS_PROXY"));
                AddRow(layout, ref row, "ALL_PROXY", EnvOrDash("ALL_PROXY"));
                AddRow(layout, ref row, "NO_PROXY", EnvOrDash("NO_PROXY"));
                AddRow(layout, ref row, "系统代理", Get(_state, "sysProxy", "-"));
                if (_root == null)
                {
                    AddRow(layout, ref row, "警告", "未找到守护目录，请设置 CODEX_PROXY_GUARDIAN_HOME");
                }

                var ok = new Button();
                ok.Text = "关闭";
                ok.Width = 88;
                ok.DialogResult = DialogResult.Cancel;
                ok.Anchor = AnchorStyles.Right;
                ok.Margin = new Padding(0, 10, 0, 6);
                ok.Click += (s, e) => f.Close();
                layout.Controls.Add(ok, 1, row);

                f.Controls.Add(layout);
                int height = 150 + row * 34;
                f.Height = Math.Min(580, Math.Max(340, height));
                f.ShowDialog();
            }
        }

        private static void AddRow(TableLayoutPanel layout, ref int row, string name, string value)
        {
            var lbl = new Label();
            lbl.Text = name;
            lbl.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lbl.ForeColor = Color.FromArgb(105, 105, 105);
            lbl.AutoSize = true;
            lbl.Anchor = AnchorStyles.Left;
            lbl.Margin = new Padding(0, 3, 8, 3);
            layout.Controls.Add(lbl, 0, row);

            var val = new Label();
            val.Text = value;
            val.Font = new Font("Microsoft YaHei UI", 9F);
            val.ForeColor = Color.FromArgb(35, 35, 35);
            val.AutoSize = true;
            val.MaximumSize = new Size(350, 0);
            val.Anchor = AnchorStyles.Left;
            val.Margin = new Padding(0, 3, 8, 3);
            layout.Controls.Add(val, 1, row);
            row++;
        }

        private string EnvOrDash(string name)
        {
            string v = Get(_state, "env" + name, "");
            return v.Length > 0 ? v : "(未设置)";
        }

        private Color StatusColor()
        {
            string task = Get(_state, "task", "");
            if (task == "NotInstalled") { return Color.FromArgb(150, 150, 150); }
            string up = Get(_state, "proxyUp", "");
            if (up == "True") { return Color.FromArgb(34, 160, 95); }
            if (up == "False") { return Color.FromArgb(216, 80, 80); }
            return Color.FromArgb(150, 150, 150);
        }

        private static Icon MakeIcon(Color color)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 1, 1, 14, 14);
                }
                using (var ring = new Pen(Color.White, 1.0F))
                {
                    g.DrawEllipse(ring, 2.5F, 2.5F, 11, 11);
                }
                using (var f = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var tf = new SolidBrush(Color.White))
                {
                    SizeF size = g.MeasureString("P", f);
                    g.DrawString("P", f, tf, (16 - size.Width) / 2F, (16 - size.Height) / 2F - 1F);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            bmp.Dispose();
            return icon;
        }

        private void RunDetect()
        {
            string result = RunHelper("Detect");
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护 - 检测", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                MessageBox.Show(result, "Codex 代理守护 - 只读检测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ToggleDaemon()
        {
            string task = Get(_state, "task", "");
            string result = RunHelper(task == "Running" ? "Stop" : "Start");
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                RefreshState();
                _icon.ShowBalloonTip(3000, "Codex 代理守护", task == "Running" ? "守护已停止" : "守护已启动", ToolTipIcon.Info);
            }
        }

        private void ToggleTaskAutoStart()
        {
            string task = Get(_state, "task", "");
            if (task == "NotInstalled")
            {
                InstallDaemon();
                return;
            }
            string result = RunHelper(task == "Disabled" ? "Enable" : "Disable");
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                RefreshState();
            }
        }

        private void ToggleTrayAutoStart()
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "CodexProxyGuardianTray";
            using (var k = Registry.CurrentUser.CreateSubKey(runKey))
            {
                if (k != null)
                {
                    if (IsTrayAutoStart())
                    {
                        k.DeleteValue(valueName, false);
                    }
                    else
                    {
                        k.SetValue(valueName, "\"" + Application.ExecutablePath + "\"");
                    }
                }
            }
            RefreshState();
        }

        private static bool IsTrayAutoStart()
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (var k = Registry.CurrentUser.OpenSubKey(runKey))
            {
                return k != null && k.GetValue("CodexProxyGuardianTray") != null;
            }
        }

        private void InstallDaemon()
        {
            var r = MessageBox.Show("将注册计划任务 CodexProxyDaemon（登录自启、静默运行）。继续？",
                "Codex 代理守护", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { return; }
            string result = RunHelper("Install");
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                RefreshState();
                _icon.ShowBalloonTip(3000, "Codex 代理守护", "守护已安装并启动", ToolTipIcon.Info);
            }
        }

        private void UninstallDaemon()
        {
            var r = MessageBox.Show(
                "将移除守护计划任务（保留代理环境变量和系统代理设置）。\n如需一并清空代理设置，请手动运行: scripts\\uninstall-daemon.ps1 -ClearEnv -DisableSystemProxy\n\n继续？",
                "Codex 代理守护", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { return; }
            string result = RunHelper("Uninstall");
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                RefreshState();
                _icon.ShowBalloonTip(3000, "Codex 代理守护", "守护任务已移除", ToolTipIcon.Info);
            }
        }

        private static void OpenFolder(string dir)
        {
            if (dir == null || !Directory.Exists(dir))
            {
                MessageBox.Show("目录不存在：" + dir, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开目录失败：" + ex.Message, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadNodes()
        {
            string json = RunHelper("Nodes");
            _miNodes.DropDownItems.Clear();
            if (json.StartsWith("ERR=") || string.IsNullOrEmpty(json))
            {
                _miNodes.DropDownItems.Add(new ToolStripMenuItem("无法获取节点列表") { Enabled = false });
                return;
            }
            try
            {
                var ser = new JavaScriptSerializer();
                var dict = (Dictionary<string,object>)ser.DeserializeObject(json);
                object groupsRaw;
                if (!dict.TryGetValue("groups", out groupsRaw) || !(groupsRaw is object[])) return;
                var groups = (object[])groupsRaw;
                if (groups.Length == 0)
                {
                    _miNodes.DropDownItems.Add(new ToolStripMenuItem("无选择器组") { Enabled = false });
                    return;
                }
                foreach (var g in groups)
                {
                    var gd = (Dictionary<string,object>)g;
                    string name = (string)gd["name"];
                    string now = (string)gd["now"];
                    var opts = (object[])gd["options"];
                    var groupItem = new ToolStripMenuItem(name);
                    foreach (var o in opts)
                    {
                        string opt = (string)o;
                        bool isCurrent = opt == now;
                        var item = new ToolStripMenuItem(opt);
                        if (isCurrent)
                        {
                            item.Checked = true;
                            item.Font = new Font(SystemFonts.MenuFont, FontStyle.Bold);
                        }
                        string grp = name;
                        string optv = opt;
                        item.Click += (sender, ea) => SwitchNode(grp, optv);
                        groupItem.DropDownItems.Add(item);
                    }
                    _miNodes.DropDownItems.Add(groupItem);
                }
            }
            catch
            {
                _miNodes.DropDownItems.Add(new ToolStripMenuItem("JSON 解析失败") { Enabled = false });
            }
        }

        private void SwitchNode(string group, string node)
        {
            string args = "SwitchNode " + QuoteArg(group) + " " + QuoteArg(node);
            string result = RunHelper(args);
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                RefreshState();
                _icon.ShowBalloonTip(3000, "节点已切换", group + " → " + node, ToolTipIcon.Info);
            }
        }

        private static string QuoteArg(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private void RestartCodex()
        {
            var r = MessageBox.Show(
                "Codex 正在运行中，重启将关闭当前窗口和对话。确定要重启吗？\n\n提示：如仅需应用代理配置，可重启后再建立对话。",
                "Codex 代理守护", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            string result = RunHelper("RestartCodex");
            if (result.StartsWith("ERR="))
            {
                MessageBox.Show(result.Substring(4), "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                _icon.ShowBalloonTip(3000, "Codex 代理守护", result, ToolTipIcon.Info);
            }
        }

        private void ViewLog()
        {
            string logFile = _root == null ? null : Path.Combine(_root, "logs", "daemon.log");
            if (logFile == null || !File.Exists(logFile))
            {
                MessageBox.Show("日志文件不存在：" + logFile, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string content;
            try
            {
                var lines = File.ReadAllLines(logFile, Encoding.UTF8);
                int take = Math.Min(25, lines.Length);
                var sb = new StringBuilder();
                for (int i = lines.Length - take; i < lines.Length; i++) { sb.AppendLine(lines[i]); }
                content = sb.ToString();
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取日志失败：" + ex.Message, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (var f = new Form())
            {
                f.Text = "Codex 代理守护 - 最近日志";
                f.Width = 780;
                f.Height = 430;
                f.StartPosition = FormStartPosition.CenterScreen;
                var tb = new TextBox();
                tb.Multiline = true;
                tb.ReadOnly = true;
                tb.ScrollBars = ScrollBars.Vertical;
                tb.Dock = DockStyle.Fill;
                tb.Font = new Font("Consolas", 9.5F);
                tb.Text = content;
                f.Controls.Add(tb);
                f.ShowDialog();
            }
        }

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

        private void EditConfig()
        {
            string cfg = _root == null ? null : Path.Combine(_root, "config", "daemon.config.json");
            if (cfg == null || !File.Exists(cfg))
            {
                MessageBox.Show("配置文件不存在：" + cfg, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Process.Start("notepad.exe", "\"" + cfg + "\"");
                _icon.ShowBalloonTip(2500, "Codex 代理守护", "保存后守护会自动热重载配置，无需重启", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开配置失败：" + ex.Message, "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExitApp()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }
    }
}
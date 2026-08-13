// Codex 代理守护 - 托盘控制台（轻量 GUI）
// 由 scripts\build-tray.ps1 编译，目标 .NET Framework 4.8（Win10/11 自带，无运行时依赖）。
// 功能：状态查看、启停守护、开机自启开关、只读检测、日志/配置目录、安装/卸载。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

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
        private readonly System.Windows.Forms.Timer _timer;
        private readonly string _root;
        private readonly string _helper;
        private readonly string _logDir;
        private readonly string _configDir;

        private Dictionary<string, string> _state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _lastProxyUp = "";

        public TrayApp()
        {
            _root = FindRoot();
            _helper = _root == null ? null : Path.Combine(_root, "scripts", "tray-helper.ps1");
            _logDir = _root == null ? null : Path.Combine(_root, "logs");
            _configDir = _root == null ? null : Path.Combine(_root, "config");

            _miStartStop = new ToolStripMenuItem("启动守护", null, (s, e) => ToggleDaemon());
            _miAutoStart = new ToolStripMenuItem("暂停开机自启", null, (s, e) => ToggleTaskAutoStart());
            _miTrayAutoStart = new ToolStripMenuItem("托盘开机自启", null, (s, e) => ToggleTrayAutoStart());
            _miTrayAutoStart.CheckOnClick = true;
            _miInstall = new ToolStripMenuItem("安装守护（注册自启任务）", null, (s, e) => InstallDaemon());
            _miUninstall = new ToolStripMenuItem("卸载守护任务", null, (s, e) => UninstallDaemon());

            var menu = new ContextMenuStrip();
            menu.Items.Add("状态详情...", null, (s, e) => ShowStatus());
            menu.Items.Add("只读检测代理", null, (s, e) => RunDetect());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miStartStop);
            menu.Items.Add(_miAutoStart);
            menu.Items.Add(_miTrayAutoStart);
            menu.Items.Add(_miInstall);
            menu.Items.Add(_miUninstall);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开日志目录", null, (s, e) => OpenFolder(_logDir));
            menu.Items.Add("打开配置目录", null, (s, e) => OpenFolder(_configDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            menu.Opening += (s, e) => RefreshState();

            _icon = new NotifyIcon();
            _icon.Icon = SystemIcons.Application;
            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.Text = "Codex 代理守护";
            _icon.DoubleClick += (s, e) => ShowStatus();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15000;
            _timer.Tick += (s, e) => RefreshState();
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
            string text = RunHelper("State");
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
            var sb = new StringBuilder();
            sb.AppendLine("守护任务: " + TaskLabel(Get(_state, "task", "Unknown")));
            string up = Get(_state, "proxyUp", "");
            if (up == "True")
            {
                sb.AppendLine("代理状态: 在线（端口 " + Get(_state, "port", "?") + "）");
            }
            else if (up == "False")
            {
                sb.AppendLine("代理状态: 已关闭（直连模式）");
            }
            else
            {
                sb.AppendLine("代理状态: 未知");
            }
            sb.AppendLine("节点: " + Get(_state, "node", "-"));
            sb.AppendLine("模式: " + Get(_state, "mode", "-"));
            sb.AppendLine("消息: " + Get(_state, "message", "-"));
            sb.AppendLine("下次检测: " + Get(_state, "nextCheck", "-"));
            sb.AppendLine();
            sb.AppendLine("用户环境变量:");
            AppendEnv(sb, "HTTP_PROXY");
            AppendEnv(sb, "HTTPS_PROXY");
            AppendEnv(sb, "ALL_PROXY");
            AppendEnv(sb, "NO_PROXY");
            sb.AppendLine();
            sb.AppendLine("系统代理: " + Get(_state, "sysProxy", "-"));
            if (_root == null)
            {
                sb.AppendLine();
                sb.AppendLine("警告: 未找到守护目录，请设置 CODEX_PROXY_GUARDIAN_HOME。");
            }
            MessageBox.Show(sb.ToString(), "Codex 代理守护 - 状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void AppendEnv(StringBuilder sb, string name)
        {
            string v = Get(_state, "env" + name, "");
            sb.AppendLine(name + "=" + (v.Length > 0 ? v : "(未设置)"));
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

        private void ExitApp()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }
    }
}
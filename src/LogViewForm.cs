// Codex 代理守护 - 日志查看页（模态，只读）

using System;
using System.Drawing;
using System.IO;
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
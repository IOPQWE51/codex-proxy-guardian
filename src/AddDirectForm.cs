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
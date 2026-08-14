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
            foreach (string r in ReadDirectDomains()) { _list.Items.Add(r); }
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
                    if (arr != null) { foreach (object o in arr) { list.Add(Convert.ToString(o)); } }
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
            if (_openAdd != null) { using (Form f = _openAdd()) { f.ShowDialog(); } LoadList(); }
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
            { MessageBox.Show(result.Substring(4), "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            else { LoadList(); }
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
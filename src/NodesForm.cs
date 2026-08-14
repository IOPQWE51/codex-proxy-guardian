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
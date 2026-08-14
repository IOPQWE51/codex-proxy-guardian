// Codex 代理守护 - 独立守护进程（轻量 exe）
// 由 scripts\build-daemon.ps1 编译，目标 .NET Framework 4.8（Win10/11 自带，无运行时依赖）。
// 与 PowerShell 版守护（codex-proxy-guardian.ps1）共享配置/状态/日志格式与互斥锁，
// 作为计划任务直接运行：轮询 Clash API，维护用户代理环境变量与系统代理，
// 境内直连域名自动并入 NO_PROXY/ProxyOverride，日志有界轮转 + 磁盘空间防护。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexProxyGuardian
{
    internal static class DaemonProgram
    {
        private const string DaemonVersion = "2.4.1";
        private const string MutexName = "CodexProxyDaemonMutex";
        private const string InetKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private sealed class DaemonConfig
        {
            public string ClashApiUrl = "http://127.0.0.1:9090";
            public string ClashApiSecret = "";
            public int PollIntervalSeconds = 35;
            public int RequestTimeoutSeconds = 8;
            public int DownThreshold = 3;
            public int DownSeconds = 90;
            public string ProxyTestUrl = "https://www.gstatic.com/generate_204";
            public List<string> ProxyTestUrls = new List<string>();
            public string NoProxy = "localhost,127.*,10.*,192.168.*,*.local";
            public string ProxyOverride = "localhost;*.local;127.*;10.*;192.168.*";
            public List<string> DirectDomains = new List<string>();
            public int NodeLogCooldownSeconds = 60;
            public int MaxLogBytes = 2097152;
            public int MaxLogFiles = 3;
            public int LogMinFreeMB = 512;
            public int LogCleanupFreeMB = 2048;
            public int MaxLogTotalMB = 200;
        }

        private sealed class DetectedState
        {
            public bool Up;
            public int Port;
            public string Node = "";
            public string Mode = "";
            public bool ApiOk;
            public bool Health;
        }

        private static string _root;
        private static string _configPath;
        private static string _statePath;
        private static string _logDir;
        private static string _logFile;
        private static DaemonConfig _config;

        private static DateTime _configLastWrite = DateTime.MinValue;
        private static bool _seenUp;
        private static DateTime _firstFailAt = DateTime.MinValue;
        private static bool _lastUp = true;
        private static int _lastPort;
        private static string _lastNode = "";
        private static DateTime _lastNodeLogTime = DateTime.MinValue;
        private static int _logFailStreak;
        private static DateTime _logQuietUntil = DateTime.MinValue;
        private static DateTime _lastLogMaintenance = DateTime.MinValue;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        [STAThread]
        private static int Main(string[] args)
        {
            bool testMode = false;
            bool dryRun = false;
            bool showVersion = false;
            foreach (string arg in args)
            {
                string a = arg == null ? "" : arg.Trim();
                if (a.Equals("-Test", StringComparison.OrdinalIgnoreCase)) { testMode = true; }
                else if (a.Equals("-DryRun", StringComparison.OrdinalIgnoreCase)) { dryRun = true; }
                else if (a.Equals("-Version", StringComparison.OrdinalIgnoreCase)) { showVersion = true; }
            }

            _root = FindRoot();
            if (_root == null)
            {
                MessageBox.Show("未找到守护目录。\n\n请把 GuardianDaemon.exe 放在项目 dist 目录，或设置环境变量 CODEX_PROXY_GUARDIAN_HOME 指向项目根目录。",
                    "Codex 代理守护", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            _configPath = Path.Combine(Path.Combine(_root, "config"), "daemon.config.json");
            _statePath = Path.Combine(_root, "state.json");
            _logDir = Path.Combine(_root, "logs");
            _logFile = Path.Combine(_logDir, "daemon.log");
            _config = LoadConfig();

            if (showVersion)
            {
                Console.WriteLine(DaemonVersion);
                return 0;
            }
            if (testMode)
            {
                DetectedState s = Detect(false);
                if (dryRun)
                {
                    Console.WriteLine("DRY-RUN up=" + s.Up + " port=" + s.Port + " node=" + s.Node + " mode=" + s.Mode + " health=" + s.Health);
                }
                else
                {
                    ApplyState(s);
                    Console.WriteLine("APPLIED up=" + s.Up + " port=" + s.Port + " node=" + s.Node);
                }
                return 0;
            }
            return RunDaemon();
        }

        private static int RunDaemon()
        {
            bool acquired = false;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, MutexName);
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // 前一个实例崩溃退出：互斥锁已放弃，接管运行
                    acquired = true;
                }
                if (!acquired)
                {
                    return 0;
                }
                WriteLog("守护进程启动 v" + DaemonVersion + " (poll=" + _config.PollIntervalSeconds + "s, downSeconds=" + _config.DownSeconds + "s, directDomains=" + string.Join(",", _config.DirectDomains.ToArray()) + ")");
                while (true)
                {
                    try
                    {
                        ReloadIfChanged();
                        LogCleanup();
                        DetectedState s = Detect(true);
                        ApplyState(s);
                    }
                    catch (Exception ex)
                    {
                        WriteLog("本轮检测异常（继续运行）: " + ex.Message);
                    }
                    Thread.Sleep(_config.PollIntervalSeconds * 1000);
                }
            }
            catch (Exception ex)
            {
                string errMsg = ex.ToString();
                WriteLog("未处理异常，守护进程退出: " + errMsg);
                try
                {
                    MessageBox.Show("守护进程出错: " + ex.Message + "\n\n日志路径: " + _logFile,
                        "Codex 代理守护 - 崩溃", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }
            finally
            {
                if (mutex != null)
                {
                    try { if (acquired) { mutex.ReleaseMutex(); } } catch { }
                    mutex.Dispose();
                }
            }
        }

        // ---------- 路径 ----------

        private static string FindRoot()
        {
            string home = Environment.GetEnvironmentVariable("CODEX_PROXY_GUARDIAN_HOME");
            if (IsRoot(home)) { return home; }
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (!string.IsNullOrEmpty(exeDir))
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
                   File.Exists(Path.Combine(root, "scripts", "codex-proxy-guardian.ps1")) &&
                   Directory.Exists(Path.Combine(root, "config"));
        }

        // ---------- 配置 ----------

        private static DaemonConfig DefaultConfig()
        {
            var c = new DaemonConfig();
            c.ProxyTestUrls.Add("https://www.gstatic.com/generate_204");
            c.ProxyTestUrls.Add("https://cp.cloudflare.com/generate_204");
            c.ProxyTestUrls.Add("https://www.google.com/generate_204");
            c.DirectDomains.Add("*.deepseek.com");
            c.DirectDomains.Add("*.qwen.ai");
            c.DirectDomains.Add("*.dashscope.aliyuncs.com");
            c.DirectDomains.Add("*.moonshot.cn");
            c.DirectDomains.Add("*.bigmodel.cn");
            c.DirectDomains.Add("*.siliconflow.cn");
            c.DirectDomains.Add("*.minimaxi.com");
            c.DirectDomains.Add("*.api.volces.com");
            c.DirectDomains.Add("*.xfyun.cn");
            c.DirectDomains.Add("*.stepfun.com");
            c.DirectDomains.Add("*.lingyiwanwu.com");
            c.DirectDomains.Add("*.baichuan-ai.com");
            return c;
        }

        private static int ClampInt(int v, int min, int max)
        {
            if (v < min) { return min; }
            if (v > max) { return max; }
            return v;
        }

        private static DaemonConfig LoadConfig()
        {
            DaemonConfig c = DefaultConfig();
            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath, Encoding.UTF8);
                    var dict = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    if (dict != null)
                    {
                        object v;
                        if (dict.TryGetValue("clashApiUrl", out v)) { c.ClashApiUrl = AsString(v); }
                        if (dict.TryGetValue("clashApiSecret", out v)) { c.ClashApiSecret = AsString(v); }
                        if (dict.TryGetValue("pollIntervalSeconds", out v)) { c.PollIntervalSeconds = AsInt(v); }
                        if (dict.TryGetValue("requestTimeoutSeconds", out v)) { c.RequestTimeoutSeconds = AsInt(v); }
                        if (dict.TryGetValue("downThreshold", out v)) { c.DownThreshold = AsInt(v); }
                        if (dict.TryGetValue("downSeconds", out v)) { c.DownSeconds = AsInt(v); }
                        if (dict.TryGetValue("proxyTestUrl", out v)) { c.ProxyTestUrl = AsString(v); }
                        if (dict.TryGetValue("nodeLogCooldownSeconds", out v)) { c.NodeLogCooldownSeconds = AsInt(v); }
                        if (dict.TryGetValue("maxLogBytes", out v)) { c.MaxLogBytes = AsInt(v); }
                        if (dict.TryGetValue("maxLogFiles", out v)) { c.MaxLogFiles = AsInt(v); }
                        if (dict.TryGetValue("logMinFreeMB", out v)) { c.LogMinFreeMB = AsInt(v); }
                        if (dict.TryGetValue("logCleanupFreeMB", out v)) { c.LogCleanupFreeMB = AsInt(v); }
                        if (dict.TryGetValue("maxLogTotalMB", out v)) { c.MaxLogTotalMB = AsInt(v); }
                        if (dict.TryGetValue("noProxy", out v)) { c.NoProxy = AsString(v); }
                        if (dict.TryGetValue("proxyOverride", out v)) { c.ProxyOverride = AsString(v); }
                        if (dict.TryGetValue("directDomains", out v))
                        {
                            var list = new List<string>();
                            if (v is object[])
                            {
                                foreach (object o in (object[])v) { AddUnique(list, AsString(o).Trim()); }
                            }
                            else
                            {
                                foreach (string s in AsString(v).Split(',')) { AddUnique(list, s.Trim()); }
                            }
                            if (list.Count > 0) { c.DirectDomains = list; }
                        }
                        if (dict.TryGetValue("proxyTestUrls", out v))
                        {
                            var urls = new List<string>();
                            if (v is object[])
                            {
                                foreach (object o in (object[])v) { AddUnique(urls, AsString(o).Trim()); }
                            }
                            else
                            {
                                string single = AsString(v).Trim();
                                if (single.Length > 0) { AddUnique(urls, single); }
                            }
                            if (urls.Count > 0) { c.ProxyTestUrls = urls; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("配置读取失败，使用默认配置: " + ex.Message);
                }
            }
            // 数值钳制
            c.PollIntervalSeconds = ClampInt(c.PollIntervalSeconds, 5, 600);
            c.RequestTimeoutSeconds = ClampInt(c.RequestTimeoutSeconds, 2, 30);
            c.DownSeconds = ClampInt(c.DownSeconds, 15, 600);
            c.NodeLogCooldownSeconds = ClampInt(c.NodeLogCooldownSeconds, 5, 3600);
            c.MaxLogBytes = ClampInt(c.MaxLogBytes, 102400, 10485760);
            c.MaxLogFiles = ClampInt(c.MaxLogFiles, 1, 10);
            c.LogMinFreeMB = ClampInt(c.LogMinFreeMB, 64, 65536);
            c.LogCleanupFreeMB = ClampInt(c.LogCleanupFreeMB, 256, 65536);
            if (c.LogCleanupFreeMB < c.LogMinFreeMB) { c.LogCleanupFreeMB = c.LogMinFreeMB; }
            c.MaxLogTotalMB = ClampInt(c.MaxLogTotalMB, 64, 2048);
            if (c.ProxyTestUrls == null || c.ProxyTestUrls.Count == 0)
            {
                if (!string.IsNullOrEmpty(c.ProxyTestUrl)) { c.ProxyTestUrls = new List<string>(); c.ProxyTestUrls.Add(c.ProxyTestUrl); }
            }
            // 直连域名并入 NO_PROXY 与 ProxyOverride
            c.NoProxy = MergeCommaUnique(c.NoProxy, c.DirectDomains);
            c.ProxyOverride = MergeSemicolonUnique(c.ProxyOverride, c.DirectDomains);
            if (File.Exists(_configPath))
            {
                _configLastWrite = File.GetLastWriteTime(_configPath);
            }
            return c;
        }

        private static void ReloadIfChanged()
        {
            if (!File.Exists(_configPath)) { return; }
            DateTime t = File.GetLastWriteTime(_configPath);
            if (t > _configLastWrite)
            {
                _config = LoadConfig();
                WriteLog("配置已热重载 (poll=" + _config.PollIntervalSeconds + "s, downSeconds=" + _config.DownSeconds + "s, urls=" + _config.ProxyTestUrls.Count + ")");
            }
        }

        // ---------- Clash API ----------

        private static Dictionary<string, object> ClashGet(string path)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(_config.ClashApiUrl + path);
                req.Timeout = _config.RequestTimeoutSeconds * 1000;
                req.Accept = "application/json";
                if (!string.IsNullOrEmpty(_config.ClashApiSecret))
                {
                    req.Headers["Authorization"] = "Bearer " + _config.ClashApiSecret;
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool ProxyHealth(int port)
        {
            if (port <= 0) { return false; }
            List<string> urls = _config.ProxyTestUrls;
            if (urls == null || urls.Count == 0)
            {
                if (!string.IsNullOrEmpty(_config.ProxyTestUrl))
                {
                    urls = new List<string>();
                    urls.Add(_config.ProxyTestUrl);
                }
            }
            if (urls == null) { return false; }
            foreach (string u in urls)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(u);
                    req.Timeout = _config.RequestTimeoutSeconds * 1000;
                    req.Proxy = new WebProxy("http://127.0.0.1:" + port);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        int code = (int)resp.StatusCode;
                        if (code >= 200 && code < 500) { return true; }
                    }
                }
                catch
                {
                    // 尝试下一个 URL
                }
            }
            return false;
        }

        private static DetectedState Detect(bool useGrace)
        {
            var s = new DetectedState();
            Dictionary<string, object> configs = ClashGet("/configs");
            if (configs != null)
            {
                s.ApiOk = true;
                object v;
                s.Mode = configs.TryGetValue("mode", out v) ? AsString(v) : "";
                s.Port = configs.TryGetValue("mixed-port", out v) ? AsInt(v) : 0;
                s.Health = ProxyHealth(s.Port);
                if (s.Health)
                {
                    Dictionary<string, object> proxies = ClashGet("/proxies");
                    if (proxies != null)
                    {
                        object pv;
                        if (proxies.TryGetValue("proxies", out pv))
                        {
                            var all = pv as Dictionary<string, object>;
                            if (all != null)
                            {
                                object gv;
                                if (all.TryGetValue("GLOBAL", out gv))
                                {
                                    var g = gv as Dictionary<string, object>;
                                    if (g != null)
                                    {
                                        object nv;
                                        s.Node = g.TryGetValue("now", out nv) ? AsString(nv) : "";
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                s.Mode = "api-unreachable";
            }
            if (useGrace)
            {
                // 时间窗判定：连续 downSeconds 秒失败才判定下线，避免 FlClash 频繁开关误清配置
                if (s.ApiOk && s.Health)
                {
                    _seenUp = true;
                    _firstFailAt = DateTime.MinValue;
                    s.Up = true;
                }
                else
                {
                    if (!_seenUp)
                    {
                        // 本会话从未观测到代理在线（如开机时 FlClash 尚未启动）：立即判下线，不残留死代理配置
                        s.Up = false;
                        _firstFailAt = DateTime.MinValue;
                    }
                    else if (_firstFailAt == DateTime.MinValue)
                    {
                        // 首次失败：进入宽限期，保持在线
                        _firstFailAt = DateTime.Now;
                        s.Up = true;
                    }
                    else
                    {
                        double elapsed = (DateTime.Now - _firstFailAt).TotalSeconds;
                        s.Up = elapsed < (double)_config.DownSeconds;
                    }
                }
            }
            else
            {
                s.Up = s.ApiOk && s.Health;
            }
            return s;
        }

        // ---------- 环境变量 ----------

        private static int EffectivePort(bool up, int port)
        {
            // 宽限期内 API 可能暂时不可达（port=0），沿用上一个已知端口，
            // 避免把 http://127.0.0.1:0 写入环境变量/系统代理
            int eff = port;
            if (up && eff <= 0 && _lastPort > 0) { eff = _lastPort; }
            return eff;
        }

        private static bool ApplyEnv(bool up, int port)
        {
            int effPort = EffectivePort(up, port);
            string proxy = (up && effPort > 0) ? "http://127.0.0.1:" + effPort : "";
            string noProxy = (up && effPort > 0) ? _config.NoProxy : "";
            bool changed = false;
            if (SetEnvVar("HTTP_PROXY", proxy)) { changed = true; }
            if (SetEnvVar("HTTPS_PROXY", proxy)) { changed = true; }
            if (SetEnvVar("ALL_PROXY", proxy)) { changed = true; }
            if (SetEnvVar("NO_PROXY", noProxy)) { changed = true; }
            if (changed) { Broadcast("Environment"); }
            return changed;
        }

        private static bool SetEnvVar(string name, string value)
        {
            string cur = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (cur == null) { cur = ""; }
            if (cur == value) { return false; }
            if (value.Length == 0)
            {
                Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            }
            else
            {
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            }
            return true;
        }

        // ---------- 系统代理（WinINET 注册表 + 广播） ----------

        private static void Broadcast(string section)
        {
            try
            {
                UIntPtr result;
                SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, section, 0x0002, 5000, out result);
            }
            catch (Exception ex)
            {
                WriteLog("广播设置变更失败: " + ex.Message);
            }
        }

        private static string MergeOverride(string current, string want)
        {
            var have = new List<string>();
            foreach (string p in current.Split(';')) { AddUnique(have, p.Trim()); }
            var wantList = new List<string>();
            foreach (string p in want.Split(';')) { AddUnique(wantList, p.Trim()); }
            bool changed = false;
            foreach (string w in wantList)
            {
                if (!have.Contains(w)) { have.Add(w); changed = true; }
            }
            if (!changed) { return current; }
            return string.Join(";", have.ToArray());
        }

        private static bool ApplyInet(bool up, int port)
        {
            bool changed = false;
            int effPort = EffectivePort(up, port);
            try
            {
                int curEnable = 0;
                string curServer = "";
                string curOverride = "";
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InetKeyPath))
                {
                    if (k != null)
                    {
                        curEnable = Convert.ToInt32(k.GetValue("ProxyEnable", 0));
                        curServer = AsString(k.GetValue("ProxyServer", ""));
                        curOverride = AsString(k.GetValue("ProxyOverride", ""));
                    }
                }
                if (up && effPort > 0)
                {
                    string server = "127.0.0.1:" + effPort;
                    if (curEnable != 1 || curServer != server)
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(InetKeyPath))
                        {
                            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                            k.SetValue("ProxyServer", server, RegistryValueKind.String);
                        }
                        changed = true;
                    }
                    // 保留用户既有的绕过列表，仅追加缺失的默认/直连条目
                    string merged = MergeOverride(curOverride, _config.ProxyOverride);
                    if (merged != curOverride)
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(InetKeyPath))
                        {
                            k.SetValue("ProxyOverride", merged, RegistryValueKind.String);
                        }
                        changed = true;
                    }
                }
                else
                {
                    // 只关闭我们自己写过的 127.0.0.1 系统代理，不碰用户其他代理设置
                    if (curEnable == 1 && System.Text.RegularExpressions.Regex.IsMatch(curServer, "^127\\.0\\.0\\.1:\\d+$"))
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(InetKeyPath))
                        {
                            k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        }
                        changed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("系统代理设置失败: " + ex.Message);
            }
            if (changed) { Broadcast("Internet Settings"); }
            return changed;
        }

        // ---------- 状态文件 ----------

        private static void WriteState(DetectedState s, string message, bool envChanged, bool inetChanged)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["version"] = DaemonVersion;
                d["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                d["proxyUp"] = s.Up;
                d["port"] = EffectivePort(s.Up, s.Port);
                d["node"] = s.Node;
                d["mode"] = s.Mode;
                d["envChanged"] = envChanged;
                d["systemProxyChanged"] = inetChanged;
                d["message"] = message;
                d["nextCheck"] = DateTime.Now.AddSeconds(_config.PollIntervalSeconds).ToString("yyyy-MM-dd HH:mm:ss");
                string json = new JavaScriptSerializer().Serialize(d);
                string tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(true));
                File.Copy(tmp, _statePath, true);
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex)
            {
                WriteLog("state.json 写入失败: " + ex.Message);
            }
        }

        // ---------- 应用状态 ----------

        private static void ApplyState(DetectedState s)
        {
            int effPort = EffectivePort(s.Up, s.Port);
            bool envChanged = ApplyEnv(s.Up, s.Port);
            bool inetChanged = ApplyInet(s.Up, s.Port);
            DateTime now = DateTime.Now;

            if (s.Up)
            {
                if (!_lastUp)
                {
                    WriteLog("代理上线：端口 " + effPort + "，节点 " + s.Node + "，模式 " + s.Mode);
                }
                else if (_lastPort > 0 && _lastPort != effPort)
                {
                    WriteLog("代理端口变化：" + _lastPort + " -> " + effPort);
                }
                else if (_lastNode.Length > 0 && _lastNode != s.Node && (now - _lastNodeLogTime).TotalSeconds >= (double)_config.NodeLogCooldownSeconds)
                {
                    WriteLog("代理节点变化：" + _lastNode + " -> " + s.Node);
                    _lastNodeLogTime = now;
                }
            }
            else
            {
                if (_lastUp)
                {
                    WriteLog("代理下线（检测失败），已清空代理配置");
                }
            }
            if (envChanged)
            {
                if (s.Up)
                {
                    WriteLog("已应用用户环境变量代理: http://127.0.0.1:" + effPort);
                }
                else
                {
                    WriteLog("已清空用户环境变量代理");
                }
            }
            if (inetChanged)
            {
                if (s.Up)
                {
                    WriteLog("已开启系统代理");
                }
                else
                {
                    WriteLog("已关闭残留系统代理");
                }
            }

            bool codexRunning = Process.GetProcessesByName("codex").Length > 0;
            string message;
            if (s.Up)
            {
                message = (s.ApiOk && s.Health) ? "proxy up, port=" + effPort + ", node=" + s.Node : "proxy holding (grace), health check failing";
            }
            else
            {
                message = "proxy down, cleared";
            }
            if ((envChanged || inetChanged) && codexRunning)
            {
                message += " | Codex 正在运行，重启后生效";
                WriteLog("Codex 正在运行，代理配置已更新，重启 Codex 后生效");
            }
            WriteState(s, message, envChanged, inetChanged);

            _lastUp = s.Up;
            _lastPort = s.Port;
            _lastNode = s.Node;
        }

        // ---------- 磁盘空间守护 ----------

        private static long GetFreeMB()
        {
            try
            {
                string rootPath = Path.GetPathRoot(_logDir);
                if (string.IsNullOrEmpty(rootPath)) { return -1; }
                DriveInfo drive = new DriveInfo(rootPath);
                if (!drive.IsReady) { return -1; }
                return drive.AvailableFreeSpace / (1024L * 1024L);
            }
            catch { return -1; }
        }

        private static void LogCleanup()
        {
            // 磁盘空间低时自动清理：删除本守护产生的临时文件与最旧轮转日志，尽量恢复空间。
            // 只删自己管理范围内的文件，绝不碰用户数据。
            try
            {
                long freeMB = GetFreeMB();
                int cleanupFreeMB = _config == null ? 2048 : _config.LogCleanupFreeMB;
                if (cleanupFreeMB < 1) { cleanupFreeMB = 2048; }
                if (freeMB < 0 || freeMB >= cleanupFreeMB) { return; }
                // 1) 本守护产生的临时文件
                if (Directory.Exists(_logDir))
                {
                    foreach (string f in Directory.GetFiles(_logDir, "*.tmp"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                string tmpState = _statePath + ".tmp";
                if (File.Exists(tmpState)) { try { File.Delete(tmpState); } catch { } }
                // 2) 从最旧轮转日志开始删除，直到空间恢复或无可删
                int maxFiles = _config == null ? 3 : _config.MaxLogFiles;
                if (maxFiles < 1) { maxFiles = 1; }
                for (int i = maxFiles; i >= 1; i--)
                {
                    freeMB = GetFreeMB();
                    if (freeMB < 0 || freeMB >= cleanupFreeMB) { break; }
                    string f = _logFile + "." + i;
                    if (File.Exists(f)) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        }

        // ---------- 日志（有界轮转 + 磁盘防护 + 静默窗口） ----------

        private static void WriteLog(string message)
        {
            DateTime now = DateTime.Now;
            if (now < _logQuietUntil) { return; }
            if (_logFailStreak >= 20)
            {
                _logQuietUntil = now.AddSeconds(300);
                _logFailStreak = 0;
                return;
            }
            try
            {
                // 磁盘空间防护（30 秒节流，避免每次写日志都枚举磁盘）：
                // 低于清理阈值先自动清理旧日志/临时文件；仍低于停止阈值则跳过写日志
                bool doMaintenance = (now - _lastLogMaintenance).TotalSeconds >= 30;
                if (doMaintenance)
                {
                    int minFreeMB = _config == null ? 512 : _config.LogMinFreeMB;
                    int cleanupFreeMB = _config == null ? 2048 : _config.LogCleanupFreeMB;
                    if (minFreeMB < 1) { minFreeMB = 512; }
                    if (cleanupFreeMB < 1) { cleanupFreeMB = 2048; }
                    long freeMB = GetFreeMB();
                    if (freeMB >= 0)
                    {
                        if (freeMB < cleanupFreeMB) { LogCleanup(); }
                        freeMB = GetFreeMB();
                        if (freeMB >= 0 && freeMB < minFreeMB)
                        {
                            _lastLogMaintenance = now;
                            return;
                        }
                    }
                    _lastLogMaintenance = now;
                }
                if (!Directory.Exists(_logDir)) { Directory.CreateDirectory(_logDir); }
                int maxBytes = _config == null ? 2097152 : _config.MaxLogBytes;
                int maxFiles = _config == null ? 3 : _config.MaxLogFiles;
                maxBytes = ClampInt(maxBytes, 102400, 10485760);
                maxFiles = ClampInt(maxFiles, 1, 10);
                if (File.Exists(_logFile))
                {
                    var fi = new FileInfo(_logFile);
                    if (fi.Length > maxBytes)
                    {
                        // 标准移位轮转：先删最旧（.maxFiles），再 .N-1 -> .N ... .1 -> .2，主文件 -> .1
                        string oldest = _logFile + "." + maxFiles;
                        if (File.Exists(oldest)) { File.Delete(oldest); }
                        for (int i = maxFiles - 1; i >= 1; i--)
                        {
                            string src = _logFile + "." + i;
                            string dst = _logFile + "." + (i + 1);
                            if (File.Exists(src)) { File.Move(src, dst); }
                        }
                        if (File.Exists(_logFile)) { File.Move(_logFile, _logFile + ".1"); }
                    }
                }
                string line = now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + "\r\n";
                if (!File.Exists(_logFile))
                {
                    File.WriteAllText(_logFile, line, new UTF8Encoding(true));
                }
                else
                {
                    File.AppendAllText(_logFile, line, new UTF8Encoding(false));
                }
                // 总量硬上限（与磁盘检查同节流）：即使配置异常，所有日志文件合计也不超过 MaxLogTotalMB
                if (!doMaintenance) { _logFailStreak = 0; }
                else
                {
                int totalMB = _config == null ? 200 : _config.MaxLogTotalMB;
                if (totalMB < 64) { totalMB = 200; }
                long capBytes = (long)totalMB * 1024L * 1024L;
                long totalBytes = 0;
                if (Directory.Exists(_logDir))
                {
                    foreach (string f in Directory.GetFiles(_logDir, "daemon.log*"))
                    {
                        try { totalBytes += new FileInfo(f).Length; } catch { }
                    }
                }
                if (totalBytes > capBytes)
                {
                    // 从最旧轮转文件开始删，直到总量低于上限；主日志始终保留
                    for (int i = maxFiles; i >= 1; i--)
                    {
                        string f = _logFile + "." + i;
                        if (File.Exists(f)) { try { File.Delete(f); } catch { } }
                        totalBytes = 0;
                        if (Directory.Exists(_logDir))
                        {
                            foreach (string g in Directory.GetFiles(_logDir, "daemon.log*"))
                            {
                                try { totalBytes += new FileInfo(g).Length; } catch { }
                            }
                        }
                        if (totalBytes <= capBytes) { break; }
                    }
                }
                _logFailStreak = 0;
                }
            }
            catch
            {
                _logFailStreak++;
                if (_logFailStreak >= 20)
                {
                    _logQuietUntil = DateTime.Now.AddSeconds(300);
                    _logFailStreak = 0;
                }
            }
        }

        // ---------- 工具 ----------

        private static void AddUnique(List<string> list, string s)
        {
            if (s.Length > 0 && !list.Contains(s)) { list.Add(s); }
        }

        private static string MergeCommaUnique(string current, List<string> direct)
        {
            var list = new List<string>();
            foreach (string p in current.Split(',')) { AddUnique(list, p.Trim()); }
            foreach (string d in direct) { AddUnique(list, d.Trim()); }
            return string.Join(",", list.ToArray());
        }

        private static string MergeSemicolonUnique(string current, List<string> direct)
        {
            var list = new List<string>();
            foreach (string p in current.Split(';')) { AddUnique(list, p.Trim()); }
            foreach (string d in direct) { AddUnique(list, d.Trim()); }
            return string.Join(";", list.ToArray());
        }

        private static int AsInt(object o)
        {
            if (o == null) { return 0; }
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static string AsString(object o)
        {
            if (o == null) { return ""; }
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }
    }
}
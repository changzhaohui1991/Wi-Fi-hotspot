using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WiFiHotspotApp
{
    internal sealed class PsResult
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Lines = new List<string>();
        public int ExitCode = -1;
        public string Raw = "";

        public string Get(string key)
        {
            string v;
            if (Values.TryGetValue(key, out v)) return v;
            return null;
        }

        public List<string> GetAll(string key)
        {
            List<string> list = new List<string>();
            foreach (string line in Lines)
            {
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                if (string.Equals(line.Substring(0, idx), key, StringComparison.OrdinalIgnoreCase))
                    list.Add(line.Substring(idx + 1));
            }
            return list;
        }
    }

    internal static class Ps
    {
        private static string _scriptCache;

        private static string Script()
        {
            if (_scriptCache != null) return _scriptCache;
            using (Stream s = typeof(Ps).Assembly.GetManifestResourceStream("HotspotPs.ps1"))
            {
                if (s == null) throw new InvalidOperationException("缺少内置脚本资源 HotspotPs.ps1");
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    _scriptCache = sr.ReadToEnd();
            }
            return _scriptCache;
        }

        public static PsResult Run(string action, Dictionary<string, string> env, int timeoutMs)
        {
            PsResult res = new PsResult();
            string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe");

            string script = Script();
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            ProcessStartInfo psi = new ProcessStartInfo(psExe);
            psi.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["WH_ACTION"] = action;
            if (env != null)
            {
                foreach (KeyValuePair<string, string> kv in env)
                    psi.EnvironmentVariables[kv.Key] = kv.Value == null ? "" : kv.Value;
            }

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                res.Raw = "启动 PowerShell 失败: " + ex.Message;
                return res;
            }

            StringBuilder so = new StringBuilder();
            StringBuilder se = new StringBuilder();
            Thread outThread = new Thread(delegate() { try { so.Append(p.StandardOutput.ReadToEnd()); } catch { } });
            Thread errThread = new Thread(delegate() { try { se.Append(p.StandardError.ReadToEnd()); } catch { } });
            outThread.IsBackground = true;
            errThread.IsBackground = true;
            outThread.Start();
            errThread.Start();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                outThread.Join(1000);
                errThread.Join(1000);
                res.Raw = "执行超时 (" + action + ")";
                res.Lines.Add("RESULT=FAIL");
                res.Lines.Add("MSG=命令执行超时");
                foreach (string line in res.Lines)
                {
                    int idx = line.IndexOf('=');
                    if (idx > 0) res.Values[line.Substring(0, idx)] = line.Substring(idx + 1);
                }
                return res;
            }

            outThread.Join(3000);
            errThread.Join(1000);
            p.WaitForExit(2000);

            res.ExitCode = p.ExitCode;
            res.Raw = so.ToString();
            if (se.Length > 0) res.Raw = res.Raw + "\r\n[stderr]\r\n" + se.ToString();

            string[] lines = res.Raw.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                res.Lines.Add(line);
                int idx = line.IndexOf('=');
                if (idx > 0) res.Values[line.Substring(0, idx)] = line.Substring(idx + 1);
            }
            return res;
        }
    }

    internal static class NetShell
    {
        public static string Run(string arguments)
        {
            string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\netsh.exe");
            ProcessStartInfo psi = new ProcessStartInfo(netsh, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            int oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            Encoding enc;
            try { enc = Encoding.GetEncoding(oem); }
            catch { enc = Encoding.Default; }
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;

            try
            {
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    return (o + "\r\n" + e).Trim();
                }
            }
            catch (Exception ex)
            {
                return "netsh 调用失败: " + ex.Message;
            }
        }

        public static bool ReportsSuccess(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            string lower = output.ToLowerInvariant();
            if (lower.Contains("已启动") || lower.Contains("started") || lower.Contains("已成功") || lower.Contains("successfully")) return true;
            return false;
        }

        public static bool ReportsFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            string lower = output.ToLowerInvariant();
            return lower.Contains("失败") || lower.Contains("错误") || lower.Contains("failed") || lower.Contains("error")
                || lower.Contains("无法") || lower.Contains("not support") || lower.Contains("不支持");
        }
    }

    internal static class Adapters
    {
        public static bool IsApAdapter(NetworkInterface ni)
        {
            string desc = ni.Description;
            string name = ni.Name;
            if (desc.IndexOf("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (desc.IndexOf("WiFi Direct", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.StartsWith("本地连接*", StringComparison.Ordinal)) return true;
            if (name.StartsWith("Local Area Connection*", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static NetworkInterface FindApAdapter()
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            NetworkInterface candidate = null;
            foreach (NetworkInterface ni in all)
            {
                if (!IsApAdapter(ni)) continue;
                if (ni.OperationalStatus == OperationalStatus.Up) return ni;
                if (candidate == null) candidate = ni;
            }
            return candidate;
        }

        public static NetworkInterface FindInternetAdapter(string excludeName)
        {
            NetworkInterface fallback = null;
            int best = -1;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (excludeName != null && ni.Name == excludeName) continue;
                if (IsApAdapter(ni)) continue;
                if (ni.Description.IndexOf("Tunnel", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                IPInterfaceProperties ip = ni.GetIPProperties();
                bool hasGw = false;
                try
                {
                    foreach (GatewayIPAddressInformation g in ip.GatewayAddresses)
                    {
                        if (g.Address == null) continue;
                        if (IPAddress.Any.Equals(g.Address) || IPAddress.None.Equals(g.Address)) continue;
                        if (g.Address.Equals(IPAddress.Parse("0.0.0.0"))) continue;
                        hasGw = true;
                        break;
                    }
                }
                catch { }

                int score = hasGw ? 10 : 0;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 3;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 1;
                if (score > best)
                {
                    best = score;
                    fallback = ni;
                }
            }
            return fallback;
        }

        public static List<NetworkInterface> WlanAdapters()
        {
            List<NetworkInterface> list = new List<NetworkInterface>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    list.Add(ni);
            }
            return list;
        }
    }

    internal sealed class Settings
    {
        public string Ssid = "";
        public string Key = "";
        public bool Hidden = false;
        public string Band = "Auto";
        public string Source = "";
        public string Engine = "auto";
        public bool StopOnExit = false;

        private static string PathFile()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WiFiHotspot");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                string file = PathFile();
                if (!File.Exists(file)) return s;
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    string k = line.Substring(0, idx).Trim();
                    string v = line.Substring(idx + 1);
                    if (k == "ssid") s.Ssid = v;
                    else if (k == "key") s.Key = v;
                    else if (k == "hidden") s.Hidden = (v == "1");
                    else if (k == "band") s.Band = v;
                    else if (k == "source") s.Source = v;
                    else if (k == "engine") s.Engine = v;
                    else if (k == "stoponexit") s.StopOnExit = (v == "1");
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("ssid=" + Ssid);
                sb.AppendLine("key=" + Key);
                sb.AppendLine("hidden=" + (Hidden ? "1" : "0"));
                sb.AppendLine("band=" + Band);
                sb.AppendLine("source=" + Source);
                sb.AppendLine("engine=" + Engine);
                sb.AppendLine("stoponexit=" + (StopOnExit ? "1" : "0"));
                File.WriteAllText(PathFile(), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    internal sealed class MainForm : Form
    {
        private const string HiddenSsid = "\u200B";
        private const string AppTitle = "电脑 WiFi 热点";

        private TextBox txtSsid;
        private TextBox txtKey;
        private CheckBox chkShowKey;
        private CheckBox chkHidden;
        private ComboBox cboBand;
        private ComboBox cboSource;
        private Button btnCopySsid;
        private Button btnStart;
        private Button btnStop;
        private Button btnSelfCheck;
        private CheckBox chkLegacy;
        private CheckBox chkStopOnExit;

        private Label lblStateValue;
        private Label lblSsidValue;
        private Label lblBandValue;
        private Label lblClientsValue;
        private Label lblAdapterValue;
        private ToolTip _tip;
        private ListView lstClients;
        private RichTextBox txtLog;

        private Settings _settings;
        private string _visibleSsid = "";
        private volatile bool _busy;
        private bool _running;
        private string _engine = "winrt";
        private string _apAdapterName = "";
        private string _sourceAdapterName = "";
        private System.Windows.Forms.Timer _timer;
        private int _tick;

        public MainForm()
        {
            _settings = Settings.Load();
            BuildUi();
            LoadSettingsIntoUi();
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 3000;
            _timer.Tick += delegate { OnTick(); };
            _timer.Start();
            Shown += delegate { OnShown(); };
        }

        private void BuildUi()
        {
            SuspendLayout();
            Text = AppTitle;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ClientSize = new Size(716, 606);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.WhiteSmoke;

            GroupBox grpCfg = new GroupBox();
            grpCfg.Text = "热点设置";
            grpCfg.Location = new Point(12, 8);
            grpCfg.Size = new Size(692, 172);
            grpCfg.BackColor = Color.White;
            Controls.Add(grpCfg);

            Label l1 = new Label();
            l1.Text = "网络名称 (SSID)：";
            l1.Location = new Point(14, 30);
            l1.AutoSize = true;
            grpCfg.Controls.Add(l1);

            txtSsid = new TextBox();
            txtSsid.Location = new Point(132, 26);
            txtSsid.Size = new Size(318, 25);
            grpCfg.Controls.Add(txtSsid);

            btnCopySsid = new Button();
            btnCopySsid.Text = "复制 SSID";
            btnCopySsid.Location = new Point(462, 25);
            btnCopySsid.Size = new Size(88, 27);
            btnCopySsid.Click += delegate { CopySsid(); };
            grpCfg.Controls.Add(btnCopySsid);

            Label l2 = new Label();
            l2.Text = "密码 (8-63 位)：";
            l2.Location = new Point(14, 64);
            l2.AutoSize = true;
            grpCfg.Controls.Add(l2);

            txtKey = new TextBox();
            txtKey.Location = new Point(132, 60);
            txtKey.Size = new Size(318, 25);
            txtKey.UseSystemPasswordChar = true;
            grpCfg.Controls.Add(txtKey);

            chkShowKey = new CheckBox();
            chkShowKey.Text = "显示";
            chkShowKey.Location = new Point(462, 61);
            chkShowKey.AutoSize = true;
            chkShowKey.CheckedChanged += delegate { txtKey.UseSystemPasswordChar = !chkShowKey.Checked; };
            grpCfg.Controls.Add(chkShowKey);

            chkHidden = new CheckBox();
            chkHidden.Text = "不显示名称（手机扫描会看到空白条目）";
            chkHidden.Location = new Point(14, 94);
            chkHidden.AutoSize = true;
            chkHidden.CheckedChanged += delegate { OnHiddenChanged(); };
            grpCfg.Controls.Add(chkHidden);

            ToolTip tip = new ToolTip();
            _tip = tip;
            tip.SetToolTip(chkHidden, "Windows 没有关闭 SSID 广播的接口，此项只是把名称换成不可见字符：\n手机 Wi-Fi 列表里会出现一个“有锁、无名字”的空白条目，并不是扫不到。\n要真正扫不到，只能用路由器 / 随身 Wi-Fi。\n连接时需在客户端“手动添加网络”里粘贴本工具复制的名称。");

            Label l3 = new Label();
            l3.Text = "频段：";
            l3.Location = new Point(14, 128);
            l3.AutoSize = true;
            grpCfg.Controls.Add(l3);

            cboBand = new ComboBox();
            cboBand.DropDownStyle = ComboBoxStyle.DropDownList;
            cboBand.Location = new Point(132, 124);
            cboBand.Size = new Size(130, 25);
            cboBand.Items.AddRange(new object[] { "自动", "2.4GHz", "5GHz" });
            grpCfg.Controls.Add(cboBand);

            Label l4 = new Label();
            l4.Text = "共享上网源：";
            l4.Location = new Point(276, 128);
            l4.AutoSize = true;
            grpCfg.Controls.Add(l4);

            cboSource = new ComboBox();
            cboSource.DropDownStyle = ComboBoxStyle.DropDownList;
            cboSource.Location = new Point(378, 124);
            cboSource.Size = new Size(190, 25);
            grpCfg.Controls.Add(cboSource);

            Button btnRefreshSource = new Button();
            btnRefreshSource.Text = "刷新";
            btnRefreshSource.Location = new Point(576, 123);
            btnRefreshSource.Size = new Size(60, 27);
            btnRefreshSource.Click += delegate { LoadSources(true); };
            grpCfg.Controls.Add(btnRefreshSource);

            chkLegacy = new CheckBox();
            chkLegacy.Text = "兼容模式（旧版托管网络）";
            chkLegacy.Location = new Point(14, 148);
            chkLegacy.AutoSize = true;
            chkLegacy.CheckedChanged += delegate { if (chkLegacy.Checked) _engine = "legacy"; else _engine = "winrt"; };
            grpCfg.Controls.Add(chkLegacy);
            tip.SetToolTip(chkLegacy, "仅当现代移动热点不可用（如 Windows Server 系统策略限制）或老网卡时才需要勾选。\n部分新网卡（如 Intel AX 系列）驱动已移除该能力。");

            btnStart = new Button();
            btnStart.Text = "启动热点";
            btnStart.Location = new Point(12, 188);
            btnStart.Size = new Size(150, 42);
            btnStart.BackColor = Color.FromArgb(0, 120, 215);
            btnStart.ForeColor = Color.White;
            btnStart.FlatStyle = FlatStyle.Flat;
            btnStart.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            btnStart.Click += delegate { StartHotspot(); };
            Controls.Add(btnStart);

            btnStop = new Button();
            btnStop.Text = "停止热点";
            btnStop.Location = new Point(172, 188);
            btnStop.Size = new Size(150, 42);
            btnStop.FlatStyle = FlatStyle.Flat;
            btnStop.Font = new Font("Microsoft YaHei UI", 11F);
            btnStop.Enabled = false;
            btnStop.Click += delegate { StopHotspot(); };
            Controls.Add(btnStop);

            btnSelfCheck = new Button();
            btnSelfCheck.Text = "环境自检";
            btnSelfCheck.Location = new Point(332, 188);
            btnSelfCheck.Size = new Size(110, 42);
            btnSelfCheck.FlatStyle = FlatStyle.Flat;
            btnSelfCheck.Click += delegate { SelfCheck(); };
            Controls.Add(btnSelfCheck);

            chkStopOnExit = new CheckBox();
            chkStopOnExit.Text = "关闭窗口时停止热点";
            chkStopOnExit.Location = new Point(470, 200);
            chkStopOnExit.AutoSize = true;
            Controls.Add(chkStopOnExit);

            GroupBox grpStatus = new GroupBox();
            grpStatus.Text = "运行状态";
            grpStatus.Location = new Point(12, 240);
            grpStatus.Size = new Size(692, 128);
            grpStatus.BackColor = Color.White;
            Controls.Add(grpStatus);

            Label s1 = new Label();
            s1.Text = "状态：";
            s1.Location = new Point(14, 24);
            s1.Size = new Size(52, 22);
            grpStatus.Controls.Add(s1);

            lblStateValue = new Label();
            lblStateValue.Text = "未启动";
            lblStateValue.Location = new Point(66, 24);
            lblStateValue.Size = new Size(150, 22);
            lblStateValue.ForeColor = Color.Gray;
            grpStatus.Controls.Add(lblStateValue);

            Label s5 = new Label();
            s5.Text = "引擎：";
            s5.Location = new Point(228, 24);
            s5.Size = new Size(48, 22);
            grpStatus.Controls.Add(s5);

            Label lblEngineValue = new Label();
            lblEngineValue.Text = "检测中…";
            lblEngineValue.Name = "lblEngineValue";
            lblEngineValue.Location = new Point(276, 24);
            lblEngineValue.Size = new Size(120, 22);
            grpStatus.Controls.Add(lblEngineValue);

            Label s2 = new Label();
            s2.Text = "当前 SSID：";
            s2.Location = new Point(14, 50);
            s2.Size = new Size(88, 22);
            grpStatus.Controls.Add(s2);

            lblSsidValue = new Label();
            lblSsidValue.Text = "-";
            lblSsidValue.Location = new Point(102, 50);
            lblSsidValue.Size = new Size(290, 22);
            lblSsidValue.AutoEllipsis = true;
            grpStatus.Controls.Add(lblSsidValue);

            Label s3 = new Label();
            s3.Text = "频段：";
            s3.Location = new Point(14, 76);
            s3.Size = new Size(44, 22);
            grpStatus.Controls.Add(s3);

            lblBandValue = new Label();
            lblBandValue.Text = "-";
            lblBandValue.Location = new Point(58, 76);
            lblBandValue.Size = new Size(90, 22);
            grpStatus.Controls.Add(lblBandValue);

            Label s4 = new Label();
            s4.Text = "已连设备：";
            s4.Location = new Point(156, 76);
            s4.Size = new Size(76, 22);
            grpStatus.Controls.Add(s4);

            lblClientsValue = new Label();
            lblClientsValue.Text = "0";
            lblClientsValue.Location = new Point(232, 76);
            lblClientsValue.Size = new Size(60, 22);
            grpStatus.Controls.Add(lblClientsValue);

            Label s6 = new Label();
            s6.Text = "网卡：";
            s6.Location = new Point(14, 101);
            s6.Size = new Size(44, 22);
            grpStatus.Controls.Add(s6);

            lblAdapterValue = new Label();
            lblAdapterValue.Text = "检测中…";
            lblAdapterValue.Location = new Point(58, 101);
            lblAdapterValue.Size = new Size(330, 22);
            lblAdapterValue.AutoEllipsis = true;
            lblAdapterValue.ForeColor = Color.DimGray;
            grpStatus.Controls.Add(lblAdapterValue);
            tip.SetToolTip(lblAdapterValue, "第一段：当前处于启用状态的无线网卡（已排除 Wi-Fi Direct 虚拟适配器）。\n第二段：当前承载上网的连接。\n只做显示，程序不会切换网卡。");

            lstClients = new ListView();
            lstClients.View = View.Details;
            lstClients.FullRowSelect = true;
            lstClients.GridLines = false;
            lstClients.Location = new Point(404, 20);
            lstClients.Size = new Size(272, 98);
            lstClients.Columns.Add("设备", 156);
            lstClients.Columns.Add("IP 地址", 110);
            grpStatus.Controls.Add(lstClients);

            txtLog = new RichTextBox();
            txtLog.Location = new Point(12, 376);
            txtLog.Size = new Size(692, 218);
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.White;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.Font = new Font("Consolas", 9F);
            Controls.Add(txtLog);

            ResumeLayout(false);
        }

        private void LoadSettingsIntoUi()
        {
            string defaultSsid = "Hotspot-" + Environment.MachineName;
            if (defaultSsid.Length > 32) defaultSsid = defaultSsid.Substring(0, 32);
            _visibleSsid = string.IsNullOrEmpty(_settings.Ssid) ? defaultSsid : _settings.Ssid;
            txtSsid.Text = _visibleSsid;
            txtKey.Text = string.IsNullOrEmpty(_settings.Key) ? GenerateKey() : _settings.Key;

            if (_settings.Band == "2.4GHz") cboBand.SelectedIndex = 1;
            else if (_settings.Band == "5GHz") cboBand.SelectedIndex = 2;
            else cboBand.SelectedIndex = 0;

            chkHidden.Checked = _settings.Hidden;
            chkStopOnExit.Checked = _settings.StopOnExit;
            chkLegacy.Checked = _settings.Engine == "legacy";
        }

        private static string GenerateKey()
        {
            const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] buf = new byte[16];
            System.Security.Cryptography.RNGCryptoServiceProvider rng = new System.Security.Cryptography.RNGCryptoServiceProvider();
            rng.GetBytes(buf);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in buf) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        private void OnHiddenChanged()
        {
            if (chkHidden.Checked)
            {
                if (txtSsid.Text.Trim().Length > 0) _visibleSsid = txtSsid.Text;
                txtSsid.Text = "（不可见名称）";
                txtSsid.ReadOnly = true;
                txtSsid.ForeColor = Color.Gray;
                Log("已启用“不显示名称”：热点将以不可见字符作为网络名，手机扫描会看到一个空白条目（Windows 无法关闭 SSID 广播）。客户端需手动添加网络，粘贴本工具复制的名称。");
            }
            else
            {
                txtSsid.ReadOnly = false;
                txtSsid.ForeColor = Color.Black;
                txtSsid.Text = _visibleSsid;
                Log("已关闭“不显示名称”：热点将正常广播网络名称。");
            }
        }

        private void CopySsid()
        {
            string eff = EffectiveSsid();
            if (chkHidden.Checked && eff.Length > 0)
            {
                try { Clipboard.SetText(eff); } catch { }
                Log("已复制隐藏 SSID（不可见字符）到剪贴板，请在客户端“手动添加网络”中粘贴。");
            }
            else
            {
                try { Clipboard.SetText(eff); } catch { }
                Log("已复制 SSID：" + eff);
            }
        }

        private string EffectiveSsid()
        {
            if (chkHidden.Checked) return HiddenSsid;
            return txtSsid.Text.Trim();
        }

        private string SelectedBand()
        {
            if (cboBand.SelectedIndex == 1) return "TwoPointFourGigahertz";
            if (cboBand.SelectedIndex == 2) return "FiveGigahertz";
            return "Auto";
        }

        private void Log(string msg)
        {
            if (txtLog.IsDisposed) return;
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText("[" + stamp + "] " + msg + "\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private void UiLog(string msg)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(delegate { Log(msg); })); } catch { }
                return;
            }
            Log(msg);
        }

        private static string TrimDesc(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return "";
            desc = desc.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();
            return desc;
        }

        private static string ShortDesc(string desc)
        {
            desc = TrimDesc(desc);
            if (desc.Length > 26) desc = desc.Substring(0, 26) + "…";
            return desc;
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "…";
        }

        // 只读显示：哪块无线网卡在用 / 上网走哪条连接（不做任何切换）
        private void RefreshAdapterInfo()
        {
            if (lblAdapterValue == null) return;
            try
            {
                List<string> wifi = new List<string>();
                List<string> name = new List<string>();
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (Adapters.IsApAdapter(ni)) continue;
                    wifi.Add(TrimDesc(ni.Description));
                    name.Add(ni.Name);
                }

                NetworkInterface src = Adapters.FindInternetAdapter(null);
                string upShort;
                string upFull;
                if (src == null)
                {
                    upShort = "未检测到";
                    upFull = "未检测到联网网卡";
                }
                else if (src.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    upShort = "同块网卡";
                    upFull = TrimDesc(src.Description) + "（" + src.Name + "，热点与上网共用同一块卡）";
                }
                else
                {
                    upShort = Cut(TrimDesc(src.Description), 16) + "（" + src.Name + "）";
                    upFull = TrimDesc(src.Description) + "（" + src.Name + "）";
                }

                string wifiShort;
                if (wifi.Count == 0) wifiShort = "未找到无线网卡";
                else wifiShort = Cut(wifi[0], 20) + "（" + name[0] + "）";
                string extra = wifi.Count > 1 ? "　另 " + (wifi.Count - 1) + " 块已启用" : "";
                lblAdapterValue.Text = wifiShort + extra + "　上行：" + upShort;

                if (_tip != null)
                {
                    List<string> full = new List<string>();
                    for (int i = 0; i < wifi.Count; i++) full.Add(wifi[i] + "（" + name[i] + "）");
                    _tip.SetToolTip(lblAdapterValue,
                        "无线网卡：" + (wifi.Count == 0 ? "无（在设备管理器里启用一块无线网卡）" : string.Join(" / ", full.ToArray()))
                        + "\n上行连接：" + upFull
                        + "\n（此处仅显示，不切换网卡；要换网卡请在设备管理器启用/禁用。）");
                }
            }
            catch { }
        }

        private void OnShown()
        {
            RefreshAdapterInfo();
            Log("程序已启动，管理员权限：" + (IsAdmin() ? "是" : "否"));
            ThreadPool.QueueUserWorkItem(delegate { DetectCapability(true); });
        }

        private static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private void DetectCapability(bool updateUi)
        {
            PsResult caps = Ps.Run("caps", null, 25000);
            string capability = caps.Get("CAPABILITY");
            string prof = caps.Get("PROFILE");
            string state = caps.Get("STATE");

            string engine;
            if (capability == "Enabled") engine = "winrt";
            else engine = "legacy";

            if (updateUi)
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _engine = (chkLegacy.Checked) ? "legacy" : engine;
                        if (engine == "legacy" && !chkLegacy.Checked)
                        {
                            chkLegacy.Checked = true;
                        }
                        SetEngineLabel();
                        Log("移动热点能力：" + (capability == null ? "未知" : capability) + "，共享连接：" + (prof == null ? "-" : prof) + "，当前状态：" + (state == null ? "-" : state));
                        if (capability != null && capability != "Enabled")
                        {
                            Log("现代移动热点不可用（" + capability + "），已自动切换到兼容模式（旧版托管网络）。");
                        }
                        if (_settings.StopOnExit == false && state == "On")
                        {
                            _running = true;
                            btnStart.Enabled = false;
                            btnStop.Enabled = true;
                        }
                        LoadSources(false);
                    }));
                }
                catch { }
            }
        }

        private void SetEngineLabel()
        {
            Label lb = Controls.Find("lblEngineValue", true).Length > 0 ? (Label)Controls.Find("lblEngineValue", true)[0] : null;
            GroupBox grp = null;
            foreach (Control c in Controls)
            {
                if (c is GroupBox && c.Text == "运行状态") grp = (GroupBox)c;
            }
            if (grp != null)
            {
                foreach (Control c in grp.Controls)
                {
                    if (c is Label && c.Name == "lblEngineValue") lb = (Label)c;
                }
            }
            if (lb != null) lb.Text = _engine == "winrt" ? "现代热点" : "兼容模式";
        }

        private static bool IsInvisible(string s)
        {
            if (s == null) return true;
            foreach (char c in s)
            {
                if (c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF' || c == ' ') continue;
                return false;
            }
            return true;
        }

        private void LoadSources(bool manual)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                PsResult pr = Ps.Run("profiles", null, 25000);
                List<string> names = new List<string>();
                foreach (string item in pr.GetAll("PROFILE"))
                {
                    int idx = item.LastIndexOf('|');
                    string name = idx > 0 ? item.Substring(0, idx) : item;
                    string kind = idx > 0 ? item.Substring(idx + 1) : "other";
                    names.Add(name + (kind == "wlan" ? "（Wi-Fi）" : (kind == "wwan" ? "（移动网络）" : "")));
                }
                string internet = pr.Get("INTERNET");
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        string keep = cboSource.Text;
                        cboSource.Items.Clear();
                        cboSource.Items.Add("自动（当前联网连接）");
                        foreach (string n in names) cboSource.Items.Add(n);
                        int idx = 0;
                        if (!string.IsNullOrEmpty(_settings.Source))
                        {
                            for (int i = 0; i < cboSource.Items.Count; i++)
                            {
                                string it = (string)cboSource.Items[i];
                                if (it == _settings.Source || it.StartsWith(_settings.Source)) { idx = i; break; }
                            }
                        }
                        if (keep.Length > 0)
                        {
                            for (int i = 0; i < cboSource.Items.Count; i++)
                                if ((string)cboSource.Items[i] == keep) { idx = i; break; }
                        }
                        cboSource.SelectedIndex = idx;
                        if (manual) Log("已刷新连接列表，当前联网连接：" + (internet == null ? "-" : internet));
                    }));
                }
                catch { }
            });
        }

        private void StartHotspot()
        {
            if (_busy) return;
            string key = txtKey.Text.Trim();
            if (key.Length < 8 || key.Length > 63)
            {
                MessageBox.Show("密码长度必须为 8-63 位字符。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string ssid = EffectiveSsid();
            if (!chkHidden.Checked)
            {
                if (ssid.Length == 0)
                {
                    MessageBox.Show("请填写网络名称。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (Encoding.UTF8.GetByteCount(ssid) > 32)
                {
                    MessageBox.Show("网络名称最多 32 字节（中文约 10 个字）。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            PersistSettings();

            _busy = true;
            btnStart.Enabled = false;
            btnStop.Enabled = false;
            if (_running)
                Log("热点正在运行，将按新设置重新启动以应用修改…");
            Log("正在启动热点（" + (_engine == "winrt" ? "现代移动热点" : "旧版托管网络") + "）…");

            string band = SelectedBand();
            string source = cboSource.SelectedIndex > 0 ? StripSuffix(cboSource.Text) : "";
            string engine = _engine;

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string failMsg = "";
                try
                {
                    if (engine == "winrt") ok = StartWinRt(ssid, key, band, source, out failMsg);
                    else ok = StartLegacy(ssid, key, out failMsg);
                }
                catch (Exception ex)
                {
                    ok = false;
                    failMsg = ex.Message;
                }

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        _running = ok;
                        btnStart.Enabled = true;
                        btnStop.Enabled = ok;
                        if (ok) Log("热点已启动。");
                        else Log("启动失败：" + failMsg + "\n      可点击“环境自检”查看原因与修复建议。");
                    }));
                }
                catch { }
            });
        }

        private static string StripSuffix(string s)
        {
            int idx = s.LastIndexOf('（');
            if (idx > 0) return s.Substring(0, idx);
            return s;
        }

        private bool StartWinRt(string ssid, string key, string band, string source, out string msg)
        {
            Dictionary<string, string> env = new Dictionary<string, string>();
            env["WH_SSID"] = ssid;
            env["WH_KEY"] = key;
            env["WH_BAND"] = band;
            env["WH_PROFILE"] = source;
            PsResult r = Ps.Run("start", env, 90000);
            string result = r.Get("RESULT");
            if (result == "OK")
            {
                msg = "";
                string actualSsid = r.Get("SSID");
                string actualBand = r.Get("BAND");
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        Log("热点已开启：SSID=" + (actualSsid != null && actualSsid.Trim().Length == 0 ? "（不可见）" : actualSsid) + " 频段=" + actualBand);
                    }));
                }
                catch { }
                return true;
            }
            msg = r.Get("MSG");
            if (msg == null) msg = r.Raw;
            string capErr = r.Get("CAP_ERROR");
            if (capErr != null) msg = msg + " " + capErr;
            if (msg != null && msg.Length > 400) msg = msg.Substring(0, 400);
            return false;
        }

        private bool StartLegacy(string ssid, string key, out string msg)
        {
            msg = "";
            string setOut = NetShell.Run("wlan set hostednetwork mode=allow ssid=\"" + ssid + "\" key=\"" + key + "\" keyusage=persistent");
            Log("netsh set: " + Compact(setOut));

            string startOut = NetShell.Run("wlan start hostednetwork");
            Log("netsh start: " + Compact(startOut));

            if (!NetShell.ReportsSuccess(startOut))
            {
                msg = "旧版托管网络启动失败：" + Compact(startOut) + "（常见原因：网卡驱动不支持承载网络，或无线服务未安装）";
                return false;
            }

            _apAdapterName = "";
            NetworkInterface ap = Adapters.FindApAdapter();
            if (ap != null) _apAdapterName = ap.Name;
            NetworkInterface src = Adapters.FindInternetAdapter(_apAdapterName);
            if (src != null) _sourceAdapterName = src.Name;

            Log("共享网卡：热点=" + (_apAdapterName.Length == 0 ? "未找到" : _apAdapterName) + "，上网源=" + (_sourceAdapterName.Length == 0 ? "未找到" : _sourceAdapterName));

            if (_apAdapterName.Length > 0 && _sourceAdapterName.Length > 0)
            {
                Dictionary<string, string> env = new Dictionary<string, string>();
                env["WH_PRIVATE"] = _apAdapterName;
                env["WH_PUBLIC"] = _sourceAdapterName;
                PsResult ics = Ps.Run("ics", env, 40000);
                string r = ics.Get("RESULT");
                Log("ICS 共享配置：" + (r == null ? "无返回" : r) + " " + Compact(ics.Raw));
                if (r != "OK") Log("提示：ICS 自动共享未完全成功，可在“网络连接”中手动为热点网卡启用共享。");
            }
            else
            {
                Log("提示：未自动识别热点/上网网卡，请在“网络和共享中心”手动设置共享。");
            }
            return true;
        }

        private static string Compact(string s)
        {
            if (s == null) return "";
            string t = Regex.Replace(s, "\r\n|\n|\r", " / ");
            t = Regex.Replace(t, "\\s+", " ");
            if (t.Length > 300) t = t.Substring(0, 300) + "…";
            return t.Trim();
        }

        private void StopHotspot()
        {
            if (_busy) return;
            _busy = true;
            btnStart.Enabled = false;
            btnStop.Enabled = false;
            Log("正在停止热点…");
            string engine = _engine;
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (engine == "winrt")
                {
                    PsResult r = Ps.Run("stop", null, 60000);
                    string result = r.Get("RESULT");
                    if (result == "OK") Log("热点已停止。");
                    else Log("停止热点返回：" + (r.Get("MSG") == null ? r.Raw : r.Get("MSG")));
                }
                else
                {
                    string out1 = NetShell.Run("wlan stop hostednetwork");
                    Log("netsh stop: " + Compact(out1));
                    Dictionary<string, string> env = new Dictionary<string, string>();
                    env["WH_PRIVATE"] = _apAdapterName;
                    env["WH_PUBLIC"] = _sourceAdapterName;
                    Ps.Run("icsoff", env, 30000);
                    Log("热点已停止。");
                }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        _running = false;
                        btnStart.Enabled = true;
                        btnStop.Enabled = false;
                        ApplyState("未启动", "-", "-", 0, new List<string[]>());
                    }));
                }
                catch { }
            });
        }

        private void OnTick()
        {
            if (_busy || !Visible) return;
            _tick++;
            RefreshAdapterInfo();
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (_engine == "winrt")
                {
                    PsResult r = Ps.Run("info", null, 25000);
                    string state = r.Get("STATE");
                    string ssid = r.Get("SSID");
                    string band = r.Get("BAND");
                    int clients = 0;
                    try { clients = int.Parse(r.Get("CLIENTS")); } catch { }
                    List<string[]> rows = new List<string[]>();
                    foreach (string item in r.GetAll("CLIENT"))
                    {
                        int idx = item.LastIndexOf('|');
                        if (idx > 0) rows.Add(new string[] { item.Substring(0, idx), item.Substring(idx + 1) });
                    }
                    string label;
                    if (state == "On") label = "热点运行中";
                    else if (state == "Transitioning") label = "切换中…";
                    else if (state == "Off") label = "未启动";
                    else label = "未知";

                    bool on = (state == "On");
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            ApplyState(label, ssid, BandText(band), clients, rows);
                            if (!_busy)
                            {
                                _running = on;
                                btnStart.Enabled = true;
                                btnStop.Enabled = on;
                            }
                        }));
                    }
                    catch { }
                }
                else
                {
                    string hout = NetShell.Run("wlan show hostednetwork");
                    string state = "未启动";
                    int clients = 0;
                    bool started = hout.IndexOf("已启动", StringComparison.Ordinal) >= 0 || hout.IndexOf("Started", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (started) state = "热点运行中（旧版托管网络）";
                    Match m = Regex.Match(hout, "客户端数量\\s*:\\s*(\\d+)|Number of clients\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        try { clients = int.Parse(v); } catch { }
                    }
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            ApplyState(state, txtSsid.ReadOnly ? "（不可见）" : txtSsid.Text, "-", clients, new List<string[]>());
                            if (!_busy)
                            {
                                _running = started;
                                btnStart.Enabled = true;
                                btnStop.Enabled = started;
                            }
                        }));
                    }
                    catch { }
                }
            });
        }

        private static string BandText(string band)
        {
            if (band == "TwoPointFourGigahertz" || band == "2.4GHz") return "2.4GHz";
            if (band == "FiveGigahertz" || band == "5GHz") return "5GHz";
            if (string.IsNullOrEmpty(band) || band == "Auto") return "自动";
            return band;
        }

        private void ApplyState(string state, string ssid, string band, int clients, List<string[]> rows)
        {
            lblStateValue.Text = state;
            lblStateValue.ForeColor = state.StartsWith("热点运行中") ? Color.FromArgb(0, 128, 0) : Color.Gray;
            string ssidText;
            if (string.IsNullOrEmpty(ssid)) ssidText = "-";
            else if (IsInvisible(ssid)) ssidText = "（不可见名称）";
            else ssidText = ssid;
            lblSsidValue.Text = ssidText;
            lblBandValue.Text = band;
            lblClientsValue.Text = clients.ToString();
            lstClients.BeginUpdate();
            lstClients.Items.Clear();
            foreach (string[] row in rows)
            {
                ListViewItem it = new ListViewItem(row[0]);
                it.SubItems.Add(row[1]);
                lstClients.Items.Add(it);
            }
            lstClients.EndUpdate();
            SetEngineLabel();
        }

        private void SelfCheck()
        {
            if (_busy) return;
            _busy = true;
            btnSelfCheck.Enabled = false;
            Log("---- 开始环境自检 ----");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    StringBuilder sb = new StringBuilder();
                    string cap = Environment.ExpandEnvironmentVariables("%SystemRoot%");
                    string osCaption = "";
                    try
                    {
                        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        {
                            if (k != null)
                            {
                                osCaption = (string)k.GetValue("ProductName");
                                string display = (string)k.GetValue("DisplayVersion");
                                string build = (string)k.GetValue("CurrentBuild");
                                if (display != null) osCaption += " " + display;
                                if (build != null) osCaption += " (Build " + build + ")";
                            }
                        }
                    }
                    catch { }
                    StringBuilder report = new StringBuilder();
                    report.AppendLine("操作系统：" + (osCaption.Length == 0 ? Environment.OSVersion.ToString() : osCaption));
                    report.AppendLine("管理员权限：" + (IsAdmin() ? "是" : "否（必须以管理员身份运行）"));

                    ServiceControllerState("WlanSvc", report);
                    ServiceControllerState("SharedAccess", report);

                    List<NetworkInterface> wlan = Adapters.WlanAdapters();
                    report.AppendLine("无线网卡数量：" + wlan.Count);
                    foreach (NetworkInterface ni in wlan)
                        report.AppendLine("  - " + ni.Name + " / " + ni.Description + " / " + ni.OperationalStatus);

                    string drv = NetShell.Run("wlan show drivers");
                    string hostedLine = "";
                    foreach (string line in drv.Split('\n'))
                    {
                        if (line.Contains("承载网络") || line.IndexOf("Hosted network supported", StringComparison.OrdinalIgnoreCase) >= 0)
                            hostedLine = line.Trim();
                    }
                    report.AppendLine("驱动承载网络支持：" + (hostedLine.Length == 0 ? "未获取到（可能未安装无线服务）" : hostedLine));

                    PsResult caps = Ps.Run("caps", null, 25000);
                    report.AppendLine("移动热点能力：" + (caps.Get("CAPABILITY") == null ? "未获取" : caps.Get("CAPABILITY")));
                    report.AppendLine("共享连接：" + (caps.Get("PROFILE") == null ? "-" : caps.Get("PROFILE")));
                    report.AppendLine("热点当前状态：" + (caps.Get("STATE") == null ? "-" : caps.Get("STATE")));
                    if (caps.Get("CAP_ERROR") != null) report.AppendLine("能力查询异常：" + caps.Get("CAP_ERROR"));

                    report.AppendLine("");
                    report.AppendLine("建议：");
                    if (wlan.Count == 0)
                        report.AppendLine("  1) 未检测到无线网卡：请在设备管理器安装无线网卡驱动，或使用 USB 无线网卡。");
                    if (caps.Get("CAPABILITY") != null && caps.Get("CAPABILITY") != "Enabled")
                        report.AppendLine("  2) 系统策略/版本限制了移动热点，本工具会自动改用旧版托管网络；若旧版也失败，请检查驱动是否支持承载网络。");
                    if (hostedLine.Contains("否") || hostedLine.IndexOf(": No", StringComparison.OrdinalIgnoreCase) >= 0)
                        report.AppendLine("  3) 该网卡驱动已移除“承载网络”能力，旧版兼容模式不可用，建议使用支持 Wi-Fi Direct 的新驱动或外接 USB 无线网卡。");
                    report.AppendLine("  Windows Server 2022 如提示无线服务缺失，请以管理员执行：");
                    report.AppendLine("    Install-WindowsFeature Wireless-Networking");
                    report.AppendLine("  然后重启系统并安装厂商无线网卡驱动。");

                    string text = report.ToString();
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            Log(text);
                            Log("---- 环境自检结束 ----");
                        }));
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    try { BeginInvoke(new Action(delegate { Log("自检失败：" + ex.Message); })); } catch { }
                }
                finally
                {
                    try { BeginInvoke(new Action(delegate { _busy = false; btnSelfCheck.Enabled = true; })); } catch { }
                }
            });
        }

        private static void ServiceControllerState(string name, StringBuilder report)
        {
            try
            {
                System.ServiceProcess.ServiceController sc = new System.ServiceProcess.ServiceController(name);
                report.AppendLine("服务 " + name + "：" + sc.Status.ToString() + " / " + sc.StartType.ToString());
            }
            catch (Exception ex)
            {
                report.AppendLine("服务 " + name + "：不可用（" + ex.Message + "）");
            }
        }

        private void PersistSettings()
        {
            _settings.Ssid = chkHidden.Checked ? _visibleSsid : txtSsid.Text.Trim();
            _settings.Key = txtKey.Text.Trim();
            _settings.Hidden = chkHidden.Checked;
            _settings.Band = cboBand.SelectedIndex == 1 ? "2.4GHz" : (cboBand.SelectedIndex == 2 ? "5GHz" : "Auto");
            _settings.Source = cboSource.SelectedIndex > 0 ? StripSuffix(cboSource.Text) : "";
            _settings.Engine = _engine;
            _settings.StopOnExit = chkStopOnExit.Checked;
            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            PersistSettings();
            if (chkStopOnExit.Checked && _running)
            {
                Log("正在停止热点（关闭窗口）…");
                if (_engine == "winrt") Ps.Run("stop", null, 30000);
                else NetShell.Run("wlan stop hostednetwork");
            }
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0].Equals("/selftest", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                return;
            }

            Application.Run(new MainForm());
        }

        private static void RunSelfTest()
        {
            string logPath = Path.Combine(Path.GetTempPath(), "wh_selftest.log");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("admin=" + (Environment.UserName));
            PsResult caps = Ps.Run("caps", null, 25000);
            sb.AppendLine("caps_raw=" + caps.Raw.Replace("\r\n", " | "));
            PsResult profs = Ps.Run("profiles", null, 25000);
            sb.AppendLine("profiles_raw=" + profs.Raw.Replace("\r\n", " | "));

            Dictionary<string, string> env = new Dictionary<string, string>();
            env["WH_SSID"] = "SELFTEST_AP";
            env["WH_KEY"] = "Selftest123";
            env["WH_BAND"] = "Auto";
            PsResult start = Ps.Run("start", env, 90000);
            sb.AppendLine("start_raw=" + start.Raw.Replace("\r\n", " | "));

            System.Threading.Thread.Sleep(1500);
            PsResult info = Ps.Run("info", null, 25000);
            sb.AppendLine("info_raw=" + info.Raw.Replace("\r\n", " | "));

            PsResult stop = Ps.Run("stop", null, 60000);
            sb.AppendLine("stop_raw=" + stop.Raw.Replace("\r\n", " | "));

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }
    }
}

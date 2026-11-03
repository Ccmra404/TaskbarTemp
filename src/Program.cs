using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

namespace TaskbarTemp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using var mutex = new Mutex(true, @"Global\TaskbarTemp_SingleInstance", out bool isNew);
            if (!isNew)
            {
                MessageBox.Show("TaskbarTemp 已在运行（看任务栏托盘）。", "TaskbarTemp",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Settings.EnsureDefault();
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Computer _computer;
        private readonly ContextMenuStrip _menu;
        private readonly string _logPath;
        private readonly IconStyle _style;
        private Form _details;
        private FlowLayoutPanel _detailsContent;
        private Rectangle _detailsScreenArea;
        private Label _cpuName, _cpuTemp, _cpuLoad, _cpuClock;
        private Label _gpuNameLabel, _gpuTempLabel, _gpuLoadLabel, _vramLabel;
        private Label _memoryLabel, _memoryValue;
        private Label _networkSpeed, _npuName, _npuUsageLabel;
        private readonly HashSet<string> _physicalNetworkNames;

        private Icon _currentIcon;
        private string _lastKey = "";
        private string _lastTip = "";
        private int _failCount;
        private bool _loggedFirstRead;
        private bool _loggedNetworkRead, _loggedNpuRead;
        private bool _loggedNpuProbe;
        private bool _exiting;

        private float? _temp;
        private float? _load;
        private float? _clock;
        private string _hwName = "";
        private readonly int _iconSize;
        private readonly System.Collections.Generic.List<float> _tHistory = new();

        private float? _gpuTemp;
        private float? _gpuLoad;
        private float? _vramUsed;
        private float? _vramTotal;
        private string _gpuName = "";
        private float? _memUsedGb;
        private float? _memTotalGb;
        private float? _networkDownloadBps, _networkUploadBps, _npuUsage;

        public TrayContext()
        {
            _style = Settings.Style;

            _logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaskbarTemp", "run.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)); } catch { }
            _physicalNetworkNames = ReadPhysicalNetworkNames();

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = true
            };
            try { _computer.Open(); Log("Computer.Open OK"); }
            catch (Exception ex) { Log("Open failed: " + ex.Message); }

            _menu = new ContextMenuStrip();
            _menu.Items.Add("立即刷新", null, (s, e) => Refresh());
            _menu.Items.Add("详情", null, (s, e) => ShowDetails());
            _menu.Items.Add("复制数值", null, (s, e) => CopyValue());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("打开配置（style = mono/number/pill/ring）", null, (s, e) => OpenConfig());
            _menu.Items.Add("安装开机自启", null, (s, e) => InstallAutostart());
            _menu.Items.Add("移除开机自启", null, (s, e) => RemoveAutostart());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, (s, e) => ExitApp());

            _iconSize = IconRenderer.TrayIconSize;
            _tray = new NotifyIcon
            {
                Visible = true,
                ContextMenuStrip = _menu,
                Text = "TaskbarTemp 启动中…",
                Icon = IconRenderer.Render("--", IconRenderer.ColorFor(null), _iconSize, _style)
            };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDetails(); };

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            Refresh();
            Log($"started (style={_style})");
        }

        private void Refresh()
        {
            try
            {
                float? t = null, load = null, clk = null;
                string hwname = "";

                float? gpuTemp = null, gpuLoad = null, vramUsed = null, vramTotal = null;
                string gpuName = "";
                float? memUsedGb = null, memTotalGb = null;
                float? netDownload = null, netUpload = null;

                foreach (var hw in _computer.Hardware)
                {
                    try { hw.Update(); }
                    catch (Exception ex) { Log($"hardware update failed: {hw.Name}: {ex.Message}"); continue; }

                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        hwname = hw.Name;

                        var temps = hw.Sensors
                            .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                            .ToList();

                        var best = temps.FirstOrDefault(s => s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                                ?? temps.FirstOrDefault(s => s.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                                ?? temps.FirstOrDefault(s => s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                                ?? temps.FirstOrDefault();

                        if (best != null) t = best.Value;

                        var ld = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load &&
                                    s.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (ld != null) load = ld.Value;

                        var ck = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Value.HasValue &&
                                    s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (ck != null) clk = ck.Value;
                    }
                    else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd ||
                             hw.HardwareType == HardwareType.GpuIntel)
                    {
                        if (string.IsNullOrEmpty(gpuName)) gpuName = hw.Name;

                        var gt = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                                    (s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     s.Name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     s.Name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0));
                        if (gt != null && (gpuTemp == null || gt.Value > gpuTemp)) gpuTemp = gt.Value;

                        // 核显负载：优先 D3D 3D，其次 GPU Core
                        var gl = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Value.HasValue &&
                                    (s.Name.IndexOf("D3D 3D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                                     s.Name.Equals("Core", StringComparison.OrdinalIgnoreCase)));
                        if (gl != null) gpuLoad = gl.Value;

                        // 显存：D3D Shared Memory（核显共享内存）
                        var vm = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Value.HasValue &&
                                    s.Name.IndexOf("Shared Memory Used", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (vm != null) vramUsed = vm.Value;

                        var vt = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Value.HasValue &&
                                    s.Name.IndexOf("Shared Memory Total", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (vt != null) vramTotal = vt.Value;
                    }
                    else if (hw.HardwareType == HardwareType.Network && _physicalNetworkNames.Contains(hw.Name))
                    {
                        var up = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Equals("Upload Speed", StringComparison.OrdinalIgnoreCase));
                        var down = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name.Equals("Download Speed", StringComparison.OrdinalIgnoreCase));
                        if (up?.Value is float upValue) netUpload = (netUpload ?? 0) + upValue;
                        if (down?.Value is float downValue) netDownload = (netDownload ?? 0) + downValue;
                        if (!_loggedNetworkRead && (up?.Value.HasValue == true || down?.Value.HasValue == true))
                        {
                            _loggedNetworkRead = true;
                            Log($"network counter OK: {hw.Name} ↓{FormatNetworkSpeed(down?.Value)} ↑{FormatNetworkSpeed(up?.Value)}");
                        }
                    }
                    else if (hw.HardwareType == HardwareType.Memory)
                    {
                        // Total Memory 组：物理内存
                        if (hw.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var mt = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Value.HasValue &&
                                        s.Name.IndexOf("Memory Used", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (mt != null) memUsedGb = mt.Value;

                            var mtot = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Value.HasValue &&
                                        s.Name.IndexOf("Memory Available", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (mtot != null && memUsedGb.HasValue) memTotalGb = memUsedGb + mtot.Value;
                        }
                    }
                }

                _temp = t; _load = load; _clock = clk; _hwName = hwname;
                _gpuTemp = gpuTemp; _gpuLoad = gpuLoad; _vramUsed = vramUsed; _vramTotal = vramTotal; _gpuName = gpuName;
                _memUsedGb = memUsedGb;
                _memTotalGb = memTotalGb;
                _networkDownloadBps = netDownload;
                _networkUploadBps = netUpload;
                _npuUsage = ReadNpuUsage();
                if (!_loggedNpuRead && _npuUsage.HasValue)
                {
                    _loggedNpuRead = true;
                    Log($"NPU counter OK: {_npuUsage.Value:0.0}%");
                }

                if (t.HasValue)
                {
                    _failCount = 0;
                    if (!_loggedFirstRead)
                    {
                        _loggedFirstRead = true;
                        Log("physical network adapters: " + string.Join(", ", _physicalNetworkNames));
                        Log($"first read OK: temp={t.Value:0.0}C gpu={(gpuLoad.HasValue ? gpuLoad.Value.ToString("0.0") + "%" : "n/a")} mem={(_memUsedGb.HasValue && _memTotalGb.HasValue ? $"{_memUsedGb.Value:0.0}/{_memTotalGb.Value:0.0}GB" : "n/a")} net=↓{FormatNetworkSpeed(netDownload)} ↑{FormatNetworkSpeed(netUpload)} npu={(_npuUsage.HasValue ? _npuUsage.Value.ToString("0.0") + "%" : "n/a")}");
                    }

                    // 中值滤波：核心从 C-state 醒来瞬间传感器会报虚高尖峰（单采样毛刺），
                    // 取最近 5 次读数的中位数显示，毛刺被滤掉，真实变化不受影响。
                    _tHistory.Add(t.Value);
                    if (_tHistory.Count > 5) _tHistory.RemoveAt(0);
                    var sorted = _tHistory.OrderBy(v => v).ToList();
                    float disp = sorted[sorted.Count / 2];
                    if (_tHistory.Count == 5 && Math.Abs(disp - t.Value) > 12f)
                        Log($"spike filtered: raw={t.Value:0.0} -> shown={disp:0.0}");

                    string text = Math.Round(disp).ToString("0");
                    var accent = IconRenderer.ColorFor(disp);
                    string key = text + "|" + accent.ToArgb() + "|" + _iconSize;
                    if (key != _lastKey)
                    {
                        var newIcon = IconRenderer.Render(text, accent, _iconSize, _style);
                        _tray.Icon = newIcon;
                        _currentIcon?.Dispose();
                        _currentIcon = newIcon;
                        _lastKey = key;
                    }

                    string tip = $"CPU {disp:0}°C{(load.HasValue ? $" {load.Value:0}%" : "")}";
                    if (gpuTemp.HasValue) tip += $" GPU {gpuTemp.Value:0}°C";
                    else if (gpuLoad.HasValue) tip += $" GPU {gpuLoad.Value:0}%";
                    if (_memUsedGb.HasValue && _memTotalGb.HasValue)
                        tip += $" 内存{_memUsedGb.Value:0.#}/{_memTotalGb.Value:0.#}G";
                    SetTip(tip);
                }
                else
                {
                    _failCount++;
                    if (_lastKey != "nodata")
                    {
                        _tray.Icon = IconRenderer.Render("--", IconRenderer.ColorFor(null), _iconSize, _style);
                        _lastKey = "nodata";
                    }
                    SetTip("CPU 温度：读取不到（需管理员权限）");
                }
                if (_details != null && !_details.IsDisposed) UpdateDetails();
            }
            catch (Exception ex)
            {
                Log("refresh error: " + ex.Message);
            }
        }

        private void SetTip(string tip)
        {
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tip == _lastTip) return;
            _tray.Text = tip;
            _lastTip = tip;
        }

        private void ShowDetails()
        {
            if (_details == null || _details.IsDisposed)
            {
                _detailsContent = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 10f),
                    AutoScroll = true,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    Padding = new Padding(6),
                    BackColor = SystemColors.Control
                };
                _details = new Form
                {
                    Text = "TaskbarTemp 详情",
                    ClientSize = new Size(380, 260),
                    MinimumSize = new Size(300, 180),
                    Font = new Font("Segoe UI", 10f),
                    AutoScaleMode = AutoScaleMode.Dpi,
                    Opacity = 0.92,
                    FormBorderStyle = FormBorderStyle.SizableToolWindow,
                    StartPosition = FormStartPosition.Manual,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };
                _cpuName = AddSection(new (string, string)[] { ("温度", ""), ("负载", ""), ("频率", "") }, out var cpuRows, true);
                _cpuTemp = cpuRows[0]; _cpuLoad = cpuRows[1]; _cpuClock = cpuRows[2];
                _gpuNameLabel = AddSection(_gpuTemp.HasValue ? new (string, string)[] { ("温度", ""), ("负载", ""), ("显存", "") } : new (string, string)[] { ("负载", ""), ("显存", "") }, out var gpuRows);
                int gpuRow = 0;
                _gpuTempLabel = _gpuTemp.HasValue ? gpuRows[gpuRow++] : null;
                _gpuLoadLabel = gpuRows[gpuRow++]; _vramLabel = gpuRows[gpuRow];
                _npuName = AddSection(new (string, string)[] { ("负载", "") }, out var npuRows);
                _npuUsageLabel = npuRows[0];
                _memoryLabel = AddSection(new (string, string)[] { ("用量", "") }, out var memoryRows);
                _memoryValue = memoryRows[0];
                _details.Controls.Add(_detailsContent);
                _details.FormClosing += (s, e) => { if (!_exiting) { e.Cancel = true; _details.Hide(); } };
            }

            UpdateDetails();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (_detailsScreenArea != area) FitDetailsToScreen(area);
            _details.Show();
            ApplyRoundedCorners();
            PositionDetails(area);
            _details.Activate();
        }

        private void ApplyRoundedCorners()
        {
            int preference = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(_details.Handle, 33, ref preference, sizeof(int)); }
            catch (DllNotFoundException) { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, uint size);
        private HashSet<string> ReadPhysicalNetworkNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT NetConnectionID, PhysicalAdapter FROM Win32_NetworkAdapter");
                using var adapters = searcher.Get();
                foreach (ManagementObject adapter in adapters)
                using (adapter)
                    if (adapter["PhysicalAdapter"] is bool physical && physical &&
                        adapter["NetConnectionID"] is string name && !string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            catch (Exception ex) { Log("physical network lookup failed: " + ex.Message); }
            return names;
        }

        private float? ReadNpuUsage()
        {
            try
            {
                var neural = new List<(string Luid, string Engine, float Usage)>();
                var has3D = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var searcher = new ManagementObjectSearcher("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_Neural%' OR Name LIKE '%engtype_3D%'");
                using var instances = searcher.Get();
                foreach (ManagementObject instance in instances)
                using (instance)
                {
                    string name = instance["Name"] as string ?? "";
                    int luidStart = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                    if (luidStart < 0) continue;
                    int physStart = name.IndexOf("_phys_", luidStart, StringComparison.OrdinalIgnoreCase);
                    if (physStart < 0) continue;
                    string luid = name.Substring(luidStart + 5, physStart - luidStart - 5);
                    if (name.Contains("_engtype_3D", StringComparison.OrdinalIgnoreCase)) has3D.Add(luid);
                    if (!name.Contains("_engtype_Neural", StringComparison.OrdinalIgnoreCase)) continue;
                    int engineEnd = name.IndexOf("_engtype_", physStart, StringComparison.OrdinalIgnoreCase);
                    if (engineEnd < 0) continue;
                    string engine = name.Substring(physStart, engineEnd - physStart);
                    float usage = Convert.ToSingle(instance["UtilizationPercentage"]);
                    neural.Add((luid, engine, usage));
                }

                string[] npuAdapters = neural.Select(item => item.Luid).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(luid => !has3D.Contains(luid)).ToArray();
                // shortcut: treats the Neural-only adapter as the NPU; add PnP-to-LUID mapping if multiple adapters match.
                if (npuAdapters.Length != 1)
                {
                    if (!_loggedNpuProbe)
                    {
                        _loggedNpuProbe = true;
                        Log($"NPU counter unavailable: Neural rows={neural.Count}, candidates={npuAdapters.Length}");
                    }
                    return null;
                }
                _loggedNpuProbe = true;
                return Math.Min(100f, Math.Max(0f, neural.Where(item => item.Luid.Equals(npuAdapters[0], StringComparison.OrdinalIgnoreCase))
                    .GroupBy(item => item.Engine).Select(group => group.Sum(item => item.Usage)).DefaultIfEmpty().Max()));
            }
            catch (Exception ex)
            {
                if (!_loggedNpuProbe) { _loggedNpuProbe = true; Log("NPU counter failed: " + ex.Message); }
                return null;
            }
        }

        private static string FormatNetworkSpeed(float? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue) return "—";
            float speed = Math.Max(0, bytesPerSecond.Value);
            if (speed >= 1_048_576) return $"{speed / 1_048_576:0.0} MB/s";
            if (speed >= 1_024) return $"{speed / 1_024:0} KB/s";
            return $"{speed:0} B/s";
        }
        private static void DrawRoundedPanel(object sender, PaintEventArgs e)
        {
            var panel = (Panel)sender;
            var bounds = Rectangle.Inflate(panel.ClientRectangle, -1, -1);
            int diameter = 44;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(panel.BackColor);
            using var border = new Pen(Color.FromArgb(100, SystemColors.ControlDark), 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }
        private Label AddSection((string, string)[] rows, out Label[] values, bool withNetwork = false)
        {
            var section = new Panel
            {
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.ControlLightLight,
                Width = 340,
                Margin = new Padding(0, 0, 0, 3)
            };
            section.Paint += DrawRoundedPanel;
            var layout = new TableLayoutPanel
            {
                AutoSize = false,
                ColumnCount = 2,
                RowCount = rows.Length + (withNetwork ? 2 : 1),
                Dock = DockStyle.Top,
                Padding = new Padding(2)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var name = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false
            };
            layout.Controls.Add(name, 0, 0);
            layout.SetColumnSpan(name, 2);
            if (withNetwork)
            {
                _networkSpeed = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(285, 0), Margin = new Padding(3, 1, 3, 1) };
            }
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            values = new Label[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                int row = i + 1;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { Text = rows[i].Item1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 1, 6, 1) }, 0, row);
                values[i] = new Label { Text = "—", AutoSize = true, MaximumSize = new Size(285, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 1, 3, 1) };
                layout.Controls.Add(values[i], 1, row);
            }
            if (withNetwork)
            {
                int networkRow = rows.Length + 1;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { Text = "网速", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 1, 6, 1) }, 0, networkRow);
                layout.Controls.Add(_networkSpeed, 1, networkRow);
            }
            section.Controls.Add(layout);
            layout.Location = new Point(8, 6);
            layout.Width = section.ClientSize.Width - 16;
            layout.Height = layout.GetPreferredSize(new Size(layout.Width, 0)).Height;
            section.Height = layout.Height + 12;
            _detailsContent.Controls.Add(section);
            return name;
        }

        private void UpdateDetails()
        {
            _cpuName.Text = string.IsNullOrEmpty(_hwName) ? "CPU" : _hwName;
            _cpuTemp.Text = _temp.HasValue ? $"{_temp.Value:0.0} °C" : "未读到温度（请以管理员身份运行）";
            _cpuLoad.Text = _load.HasValue ? $"{_load.Value:0} %" : "—";
            _cpuClock.Text = _clock.HasValue ? $"{_clock.Value / 1000f:0.00} GHz" : "—";
            _gpuNameLabel.Text = string.IsNullOrEmpty(_gpuName) ? "GPU" : _gpuName;
            if (_gpuTempLabel != null) _gpuTempLabel.Text = $"{_gpuTemp.Value:0.0} °C";
            _gpuLoadLabel.Text = _gpuLoad.HasValue ? $"{_gpuLoad.Value:0.0} %" : "—";
            _vramLabel.Text = _vramUsed.HasValue && _vramTotal.HasValue ? $"共享 {_vramUsed.Value / 1024f:0.0} / {_vramTotal.Value / 1024f:0.0} GB" : "—";
            _memoryLabel.Text = "物理内存";
            _memoryValue.Text = _memUsedGb.HasValue && _memTotalGb.HasValue ? $"{_memUsedGb.Value:0.0} / {_memTotalGb.Value:0.0} GB" : "—";
            _networkSpeed.Text = $"↓ {FormatNetworkSpeed(_networkDownloadBps)}   ↑ {FormatNetworkSpeed(_networkUploadBps)}";
            _npuName.Text = "Intel NPU";
            _npuUsageLabel.Text = _npuUsage.HasValue ? $"{_npuUsage.Value:0.0} %" : "—";
            _detailsContent.PerformLayout();
        }

        private void FitDetailsToScreen(Rectangle area)
        {
            int width = Math.Min(430, Math.Max(280, area.Width - 24));
            foreach (Panel section in _detailsContent.Controls)
            {
                section.Width = width - 46;
                var layout = (TableLayoutPanel)section.Controls[0];
                layout.Width = section.ClientSize.Width - 16;
                layout.GetControlFromPosition(0, 0).MaximumSize = new Size(layout.ClientSize.Width - layout.Padding.Horizontal, 0);
                for (int row = 1; row < layout.RowCount; row++)
                    layout.GetControlFromPosition(1, row).MaximumSize = new Size(Math.Max(80, layout.ClientSize.Width - 112), 0);
                layout.PerformLayout();
                layout.Height = layout.GetPreferredSize(new Size(layout.Width, 0)).Height;
                section.Height = layout.Height + 12;
            }
            _detailsContent.PerformLayout();
            int contentHeight = _detailsContent.Padding.Vertical + _detailsContent.Controls
                .Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
            _details.ClientSize = new Size(width, Math.Min(Math.Max(250, contentHeight), Math.Max(180, area.Height - 24)));
            _detailsScreenArea = area;
        }

        private void PositionDetails(Rectangle area)
        {
            if (_details.Width > area.Width - 24 || _details.Height > area.Height - 24)
                _details.ClientSize = new Size(Math.Min(_details.ClientSize.Width, area.Width - 24), Math.Min(_details.ClientSize.Height, area.Height - 24));
            _details.Location = new Point(
                Math.Max(area.Left + 12, area.Right - _details.Width - 12),
                Math.Max(area.Top + 12, area.Bottom - _details.Height - 12));
        }

        private void CopyValue()
        {
            try { if (_temp.HasValue) Clipboard.SetText(_temp.Value.ToString("0.0") + " °C"); } catch { }
        }

        private void OpenConfig()
        {
            try { System.Diagnostics.Process.Start("notepad.exe", Path.Combine(AppContext.BaseDirectory, "config.ini")); }
            catch { }
        }

        private void InstallAutostart()
        {
            try
            {
                // 用 PowerShell 创建任务并立即修电源条件（电池供电也启动/不停/无时限），
                // 避免计划任务默认 StopIfGoingOnBatteries 拔电源杀进程的坑。
                string script =
                    "$a = New-ScheduledTaskAction -Execute '\"' + '\"' + $env:TT_EXE + '\"' + '\"';" +
                    "$t = New-ScheduledTaskTrigger -AtLogOn;" +
                    "$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest;" +
                    "$s = New-ScheduledTaskSettingsSet -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries " +
                    "-ExecutionTimeLimit (New-TimeSpan -Seconds 0) -StartWhenAvailable;" +
                    "Register-ScheduledTask -TaskName TaskbarTemp -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null;" +
                    "if ($?) { 'OK' } else { 'FAIL' }";
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.EnvironmentVariables["TT_EXE"] = Application.ExecutablePath;
                using var p = System.Diagnostics.Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                bool ok = outp.Contains("OK");
                Log("install autostart rc=" + p.ExitCode + " out=" + outp.Trim());
                MessageBox.Show(ok
                        ? "已安装开机自启（电池供电也运行，拔电源不停止，无时限）。"
                        : "安装失败：\n" + outp,
                    "TaskbarTemp", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex) { MessageBox.Show("安装失败：" + ex.Message); }
        }

        private void RemoveAutostart()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/delete /tn \"TaskbarTemp\" /f")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit();
                MessageBox.Show(p.ExitCode == 0 ? "已移除开机自启。" : "移除失败（可能本就没有）。");
            }
            catch (Exception ex) { MessageBox.Show("移除失败：" + ex.Message); }
        }

        private void ExitApp()
        {
            _exiting = true;
            Log("exit");
            _timer.Stop();
            try { _computer.Close(); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }

        private void Log(string m)
        {
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine); }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer?.Dispose(); _tray?.Dispose(); _details?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

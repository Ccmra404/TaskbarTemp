using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using Microsoft.Win32;
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
            var context = new TrayContext();
            Application.Run(context);
            if (!context.WaitForShutdown(TimeSpan.FromSeconds(5)))
                context.Log("shutdown timed out; process is leaving without waiting for the blocked sampler");
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private sealed record SampleSnapshot(
            float? Temp, float? Load, float? Clock, string HwName,
            float? GpuTemp, float? GpuLoad, float? VramUsed, float? VramTotal, string GpuName,
            float? MemUsedGb, float? MemTotalGb, float? NetworkDownload, float? NetworkUpload);

        private sealed class BufferedSectionPanel : Panel
        {
            public BufferedSectionPanel()
                => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        private sealed class BufferedCategoryIcon : Control
        {
            private readonly string _category;
            private Bitmap _image;
            private Color _tileColor, _glyphColor;

            public BufferedCategoryIcon(string category, Color tileColor, Color glyphColor)
            {
                _category = category;
                _tileColor = Color.FromArgb(tileColor.ToArgb());
                _glyphColor = Color.FromArgb(glyphColor.ToArgb());
                TabStop = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            public void SetTheme(Color tileColor, Color glyphColor)
            {
                tileColor = Color.FromArgb(tileColor.ToArgb());
                glyphColor = Color.FromArgb(glyphColor.ToArgb());
                if (_tileColor.ToArgb() == tileColor.ToArgb() && _glyphColor.ToArgb() == glyphColor.ToArgb()) return;
                _tileColor = tileColor;
                _glyphColor = glyphColor;
                ResetImage();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_image == null)
                {
                    _image = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using var graphics = Graphics.FromImage(_image);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    var bounds = new Rectangle(Point.Empty, _image.Size);
                    using var brush = new SolidBrush(_tileColor);
                    using var path = new GraphicsPath();
                    int radius = Math.Max(2, (int)Math.Round(10 * DeviceDpi / 96f));
                    path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90);
                    path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90);
                    path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
                    path.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90);
                    path.CloseFigure();
                    graphics.FillPath(brush, path);
                    DrawCategoryIcon(graphics, _category, bounds, _glyphColor, DeviceDpi / 96f);
                }
                e.Graphics.DrawImageUnscaled(_image, Point.Empty);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                ResetImage();
            }

            protected override void OnDpiChangedAfterParent(EventArgs e)
            {
                base.OnDpiChangedAfterParent(e);
                ResetImage();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _image?.Dispose();
                base.Dispose(disposing);
            }

            private void ResetImage()
            {
                _image?.Dispose();
                _image = null;
                Invalidate();
            }
        }

        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _npuTimer;
        private readonly Computer _computer;
        private readonly Control _dispatcher = new Control();
        private readonly object _sampleLock = new();
        private readonly object _npuLock = new();
        private readonly object _logLock = new();
        private readonly TaskCompletionSource<bool> _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ContextMenuStrip _menu;
        private readonly string _logPath;
        private readonly IconStyle _style;
        private Form _details;
        private FlowLayoutPanel _detailsContent;
        private Rectangle _detailsScreenArea;
        private Label _cpuName, _cpuTemp, _cpuLoad, _cpuClock;
        private Label _gpuNameLabel, _gpuTempCaption, _gpuTempLabel, _gpuLoadLabel, _vramLabel;
        private Label _memoryLabel, _memoryValue;
        private Label _networkDownSpeed, _networkUpSpeed, _npuName, _npuUsageLabel;
        private bool _systemBackdropApplied;
        private bool _detailsDarkMode, _detailsHighContrast;
        private int _detailsCardFillArgb, _detailsCardBorderArgb;
        private bool? _gpuTempRowVisible;
        private int _gpuTempMissingSamples;
        private bool _hasGpuTempSample;
        private readonly HashSet<string> _physicalNetworkNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hardwareFailures = new(StringComparer.OrdinalIgnoreCase);

        private const int RefreshIntervalMs = 2000;
        private const int NpuSampleIntervalMs = 6000;
        private const long MaxLogBytes = 1024 * 1024;

        private Icon _currentIcon;
        private string _lastKey = "";
        private string _lastTip = "";
        private bool _loggedFirstRead;
        private bool _loggedNetworkRead, _loggedNpuRead;
        private bool _loggedNpuProbe;
        private volatile bool _exiting;
        private volatile bool _disposed;
        private bool _sampleRunning, _samplePending;
        private bool _computerOpenAttempted, _shutdownStarted, _networkNamesLoaded;
        private int _npuVisible, _npuGeneration, _npuWorkerRunning;

        private float? _temp;
        private float? _load;
        private float? _clock;
        private string _hwName = "";
        private readonly int _iconSize;
        private readonly float[] _tempSamples = new float[5];
        private int _tempSampleCount;
        private int _tempSampleIndex;

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
            try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is InvalidOperationException)
            { Log("Theme change notifications unavailable: " + ex.Message); }

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
            _ = _dispatcher.Handle;

            _menu = new ContextMenuStrip();
            _menu.Items.Add("立即刷新", null, (s, e) => RequestSample());
            _menu.Items.Add("详情", null, (s, e) => ShowDetails());
            _menu.Items.Add("复制数值", null, (s, e) => CopyValue());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("图标样式设置（修改后重启生效）", null, (s, e) => OpenConfig());
            _menu.Items.Add("安装开机自启", null, (s, e) => InstallAutostart());
            _menu.Items.Add("移除开机自启", null, (s, e) => RemoveAutostart());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出", null, (s, e) => ExitApp());

            _iconSize = IconRenderer.TrayIconSize;
            _currentIcon = IconRenderer.Render("--", IconRenderer.ColorFor(null), _iconSize, _style);
            _tray = new NotifyIcon
            {
                Visible = true,
                ContextMenuStrip = _menu,
                Text = "TaskbarTemp 启动中…",
                Icon = _currentIcon
            };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDetails(); };

            _timer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMs };
            _timer.Tick += (s, e) => RequestSample();
            _timer.Start();

            _npuTimer = new System.Windows.Forms.Timer { Interval = NpuSampleIntervalMs };
            _npuTimer.Tick += (s, e) => RequestNpuSample();

            RequestSample();
            Log($"started (style={_style})");
        }

        private void RequestSample()
        {
            lock (_sampleLock)
            {
                if (_exiting || _disposed) return;
                if (_sampleRunning)
                {
                    _samplePending = true;
                    return;
                }
                _sampleRunning = true;
                StartSampleWorker();
            }
        }

        private void RequestNpuSample()
        {
            int generation;
            lock (_npuLock)
            {
                if (_exiting || _disposed || _npuVisible == 0 || _npuWorkerRunning != 0) return;
                _npuWorkerRunning = 1;
                generation = _npuGeneration;
            }

            StartNpuWorker(generation);
        }

        private void StartNpuWorker(int generation)
        {
            try
            {
                _ = Task.Factory.StartNew<float?>(() =>
                {
                    lock (_npuLock)
                        if (_exiting || _disposed || _npuVisible == 0 || generation != _npuGeneration) return null;
                    return ReadNpuUsage();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .ContinueWith(worker => CompleteNpuSample(worker, generation), TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                int retryGeneration = 0;
                bool retry = false;
                lock (_npuLock)
                {
                    _npuWorkerRunning = 0;
                    if (!_exiting && !_disposed && _npuVisible != 0 && generation != _npuGeneration)
                    {
                        _npuWorkerRunning = 1;
                        retryGeneration = _npuGeneration;
                        retry = true;
                    }
                }
                Log("NPU sampling worker failed: " + ex.Message);
                if (retry) StartNpuWorker(retryGeneration);
            }
        }

        private void CompleteNpuSample(Task<float?> worker, int generation)
        {
            if (worker.Exception is Exception ex)
                Log("NPU sampling worker failed: " + ex.GetBaseException().Message);

            bool applySample;
            bool startFollowUp = false;
            int followUpGeneration = 0;
            lock (_npuLock)
            {
                _npuWorkerRunning = 0;
                int currentGeneration = _npuGeneration;
                bool visible = _npuVisible != 0;
                applySample = worker.Exception == null && !_exiting && !_disposed && visible &&
                              generation == currentGeneration;

                if (!_exiting && !_disposed && visible && generation != currentGeneration)
                {
                    _npuWorkerRunning = 1;
                    followUpGeneration = currentGeneration;
                    startFollowUp = true;
                }
            }

            if (applySample)
            {
                try { _dispatcher.BeginInvoke((Action)(() => ApplyNpuSample(worker.Result, generation))); }
                catch (InvalidOperationException) { }
            }
            if (startFollowUp) StartNpuWorker(followUpGeneration);
        }

        private void StartSampleWorker()
            => _ = Task.Run(SampleLoop).ContinueWith(SampleWorkerCompleted, TaskScheduler.Default);

        private void SampleWorkerCompleted(Task worker)
        {
            if (worker.Exception is Exception ex) Log("sampling worker task failed: " + ex.GetBaseException().Message);
            bool finishShutdown = false;
            lock (_sampleLock)
            {
                if (_exiting)
                {
                    _sampleRunning = false;
                    finishShutdown = true;
                }
                else if (_samplePending)
                    StartSampleWorker();
                else _sampleRunning = false;
            }
            if (finishShutdown) FinishShutdown();
        }

        private void SampleLoop()
        {
            try
            {
                while (true)
                {
                    lock (_sampleLock)
                    {
                        if (_exiting) break;
                        _samplePending = false;
                    }

                    var snapshot = SampleHardware();
                    lock (_sampleLock)
                    {
                        if (_exiting) break;
                        if (snapshot != null)
                        {
                            try { _dispatcher.BeginInvoke((Action)(() => ApplySnapshot(snapshot))); }
                            catch (InvalidOperationException) { }
                        }
                        if (!_samplePending)
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("sampling worker failed: " + ex.Message);
            }
        }

        private SampleSnapshot SampleHardware()
        {
            try
            {
                if (!_computerOpenAttempted)
                {
                    _computerOpenAttempted = true;
                    try { _computer.Open(); Log("Computer.Open OK"); }
                    catch (Exception ex) { Log("Open failed: " + ex.Message); }
                }
                if (_exiting) return null;
                if (!_networkNamesLoaded)
                {
                    _physicalNetworkNames.UnionWith(ReadPhysicalNetworkNames());
                    _networkNamesLoaded = true;
                }
                if (_exiting) return null;

                float? t = null, load = null, clk = null;
                string hwname = "";

                float? gpuTemp = null, gpuLoad = null, vramUsed = null, vramTotal = null;
                string gpuName = "";
                float? memUsedGb = null, memTotalGb = null;
                float? netDownload = null, netUpload = null;

                foreach (var hw in _computer.Hardware)
                {
                    if (_exiting) return null;
                    try
                    {
                        hw.Update();
                        if (_hardwareFailures.Remove(hw.Name))
                            Log($"hardware update recovered: {hw.Name}");
                    }
                    catch (Exception ex)
                    {
                        if (_hardwareFailures.Add(hw.Name))
                            Log($"hardware update failed: {hw.Name}: {ex.Message}");
                        continue;
                    }

                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        hwname = hw.Name;

                        var best = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                                    s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                                ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                                    s.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
                                ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                                    s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                                ?? hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);

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

                if (t.HasValue)
                {
                    if (!_loggedFirstRead)
                    {
                        _loggedFirstRead = true;
                        Log("physical network adapters: " + string.Join(", ", _physicalNetworkNames));
                        Log($"first read OK: temp={t.Value:0.0}C gpu={(gpuLoad.HasValue ? gpuLoad.Value.ToString("0.0") + "%" : "n/a")} mem={(memUsedGb.HasValue && memTotalGb.HasValue ? $"{memUsedGb.Value:0.0}/{memTotalGb.Value:0.0}GB" : "n/a")} net=↓{FormatNetworkSpeed(netDownload)} ↑{FormatNetworkSpeed(netUpload)}");
                    }
                }
                return new SampleSnapshot(t, load, clk, hwname, gpuTemp, gpuLoad, vramUsed, vramTotal,
                    gpuName, memUsedGb, memTotalGb, netDownload, netUpload);
            }
            catch (Exception ex)
            {
                Log("refresh error: " + ex.Message);
                return null;
            }
        }

        private void ApplySnapshot(SampleSnapshot snapshot)
        {
            try
            {
                if (_exiting || _disposed) return;
                _temp = snapshot.Temp; _load = snapshot.Load; _clock = snapshot.Clock; _hwName = snapshot.HwName;
                _gpuTemp = snapshot.GpuTemp; _gpuLoad = snapshot.GpuLoad; _vramUsed = snapshot.VramUsed;
                if (snapshot.GpuTemp.HasValue)
                {
                    _hasGpuTempSample = true;
                    _gpuTempMissingSamples = 0;
                }
                else if (_hasGpuTempSample && _gpuTempMissingSamples < 3)
                    _gpuTempMissingSamples++;
                _vramTotal = snapshot.VramTotal; _gpuName = snapshot.GpuName;
                _memUsedGb = snapshot.MemUsedGb; _memTotalGb = snapshot.MemTotalGb;
                _networkDownloadBps = snapshot.NetworkDownload; _networkUploadBps = snapshot.NetworkUpload;

                if (snapshot.Temp.HasValue)
                {
                    float disp = FilterTemperature(snapshot.Temp.Value);
                    if (_tempSampleCount == _tempSamples.Length && Math.Abs(disp - snapshot.Temp.Value) > 12f)
                        Log($"spike filtered: raw={snapshot.Temp.Value:0.0} -> shown={disp:0.0}");
                    string text = Math.Round(disp).ToString("0");
                    var accent = IconRenderer.ColorFor(disp);
                    ReplaceTrayIcon(text, accent, text + "|" + accent.ToArgb() + "|" + _iconSize);
                    string tip = $"CPU {disp:0}°C{(snapshot.Load.HasValue ? $" {snapshot.Load.Value:0}%" : "")}";
                    if (snapshot.GpuTemp.HasValue) tip += $" GPU {snapshot.GpuTemp.Value:0}°C";
                    else if (snapshot.GpuLoad.HasValue) tip += $" GPU {snapshot.GpuLoad.Value:0}%";
                    if (snapshot.MemUsedGb.HasValue && snapshot.MemTotalGb.HasValue)
                        tip += $" 内存{snapshot.MemUsedGb.Value:0.#}/{snapshot.MemTotalGb.Value:0.#}G";
                    SetTip(tip);
                }
                else
                {
                    ReplaceTrayIcon("--", IconRenderer.ColorFor(null), "nodata");
                    SetTip("CPU 温度：未读到，请检查驱动和管理员权限");
                }
                if (_details != null && !_details.IsDisposed && _details.Visible) UpdateDetails();
            }
            catch (Exception ex) { Log("refresh error: " + ex.Message); }
        }

        private void ApplyNpuSample(float? usage, int generation)
        {
            try
            {
                if (_exiting || _disposed || Volatile.Read(ref _npuVisible) == 0 ||
                    generation != Volatile.Read(ref _npuGeneration)) return;

                _npuUsage = usage;
                if (!_loggedNpuRead && usage.HasValue)
                {
                    _loggedNpuRead = true;
                    Log($"NPU counter OK: {usage.Value:0.0}%");
                }
                if (_details != null && !_details.IsDisposed && _details.Visible) UpdateDetails();
            }
            catch (Exception ex) { Log("refresh error: " + ex.Message); }
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
                    Font = SystemFonts.MessageBoxFont,
                    AutoScroll = true,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    Padding = new Padding(14),
                    BackColor = System.Windows.Forms.SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(235, 240, 247)
                };
                _details = new Form
                {
                    Text = "TaskbarTemp 详情",
                    ClientSize = new Size(410, 340),
                    MinimumSize = new Size(280, 220),
                    Font = SystemFonts.MessageBoxFont,
                    AutoScaleMode = AutoScaleMode.Dpi,
                    Opacity = 1.0,
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    StartPosition = FormStartPosition.Manual,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = false
                };
                _cpuName = AddSection("CPU", new (string, string)[] { ("温度", ""), ("负载", ""), ("频率", "") }, out var cpuRows, true);
                _cpuTemp = cpuRows[0]; _cpuLoad = cpuRows[1]; _cpuClock = cpuRows[2];

                _gpuNameLabel = AddSection("GPU", new (string, string)[] { ("温度", ""), ("负载", ""), ("显存", "") }, out var gpuRows);
                _gpuTempLabel = gpuRows[0]; _gpuLoadLabel = gpuRows[1]; _vramLabel = gpuRows[2];
                _gpuTempCaption = (Label)((TableLayoutPanel)_gpuNameLabel.Parent).GetControlFromPosition(0, 1);
                _gpuTempRowVisible = null;

                _npuName = AddSection("NPU", new (string, string)[] { ("负载", "") }, out var npuRows);
                _npuUsageLabel = npuRows[0];

                _memoryLabel = AddSection("RAM", new (string, string)[] { ("用量", "") }, out var memoryRows);
                _memoryValue = memoryRows[0];
                _details.Controls.Add(_detailsContent);
                _detailsContent.Resize += (s, e) => LayoutDetails();
                _details.DpiChanged += (s, e) =>
                {
                    var workingArea = Screen.FromControl(_details).WorkingArea;
                    FitDetailsToScreen(workingArea);
                    PositionDetails(workingArea);
                };
                _details.FormClosing += (s, e) => { if (!_exiting) { e.Cancel = true; _details.Hide(); } };
                _details.VisibleChanged += (s, e) =>
                {
                    bool visible = _details.Visible;
                    lock (_npuLock)
                    {
                        Volatile.Write(ref _npuVisible, visible ? 1 : 0);
                        Interlocked.Increment(ref _npuGeneration);
                    }
                    if (visible)
                    {
                        _npuUsage = null;
                        UpdateDetails();
                        _npuTimer.Start();
                        RequestNpuSample();
                    }
                    else _npuTimer.Stop();
                };
            }

            UpdateDetails();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (_detailsScreenArea != area) FitDetailsToScreen(area);
            ApplyWindowStyle();
            _details.Show();
            PositionDetails(area);
            _details.Activate();
        }

        private void ApplyWindowStyle()
        {
            bool previousBackdrop = _systemBackdropApplied;
            bool highContrast = _detailsHighContrast = System.Windows.Forms.SystemInformation.HighContrast;
            bool dark = IsDarkMode();
            _detailsDarkMode = dark;
            int cardFillArgb = CardFillColor(highContrast, dark).ToArgb();
            int cardBorderArgb = CardBorderColor(highContrast, dark).ToArgb();
            bool cardColorsChanged = _detailsCardFillArgb != cardFillArgb || _detailsCardBorderArgb != cardBorderArgb;
            _detailsCardFillArgb = cardFillArgb;
            _detailsCardBorderArgb = cardBorderArgb;
            _systemBackdropApplied = false;
            if (!highContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                int backdropType = 3; // DWMSBT_TRANSIENTWINDOW: Desktop Acrylic for a flyout.
                try
                {
                    if (DwmSetWindowAttribute(_details.Handle, 38, ref backdropType, sizeof(int)) == 0)
                    {
                        _details.BackColor = Color.Transparent;
                        _detailsContent.BackColor = Color.Transparent;
                        _details.Opacity = 1.0;
                        _systemBackdropApplied = true;
                    }
                }
                catch (ArgumentException) { }
                catch (DllNotFoundException) { }
            }

            if (!_systemBackdropApplied)
            {
                Color background = highContrast ? SystemColors.Window : dark ? Color.FromArgb(30, 33, 38) : Color.FromArgb(235, 240, 247);
                _details.BackColor = background;
                _detailsContent.BackColor = background;
                _details.Opacity = highContrast ? 1.0 : 0.97;
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                if (!highContrast)
                {
                    int darkMode = dark ? 1 : 0;
                    DwmSetWindowAttribute(_details.Handle, 20, ref darkMode, sizeof(int));
                }
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(_details.Handle, 33, ref preference, sizeof(int));
            }

            ApplyDetailsTheme(highContrast, dark);
            if (cardColorsChanged || previousBackdrop != _systemBackdropApplied)
                foreach (Panel section in _detailsContent.Controls)
                {
                    section.Invalidate();
                    section.Controls.OfType<BufferedCategoryIcon>().FirstOrDefault()?.Invalidate();
                }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_disposed || _details == null || _details.IsDisposed || !_details.IsHandleCreated) return;
            try
            {
                _details.BeginInvoke((Action)(() =>
                {
                    if (_disposed || _details == null || _details.IsDisposed) return;
                    ApplyWindowStyle();
                    _details.Invalidate();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyDetailsTheme(bool highContrast, bool dark)
        {
            Color primary = Color.FromArgb((highContrast ? SystemColors.WindowText : dark ? Color.FromArgb(241, 244, 248) : SystemColors.ControlText).ToArgb());
            Color secondary = Color.FromArgb((highContrast ? SystemColors.GrayText : dark ? Color.FromArgb(181, 190, 202) : Color.FromArgb(96, 106, 119)).ToArgb());
            foreach (Panel section in _detailsContent.Controls)
            {
                section.Controls.OfType<BufferedCategoryIcon>().FirstOrDefault()?.SetTheme(
                    TileFillColor(highContrast, dark), AccentColor(highContrast, dark));
                if (section.Controls.Count == 0) continue;
                foreach (Control control in section.Controls.OfType<TableLayoutPanel>().First().Controls)
                {
                    if (control is Label label)
                        label.ForeColor = string.Equals(label.Tag as string, "caption", StringComparison.Ordinal) ? secondary : primary;
                    else if (control is FlowLayoutPanel speeds)
                        foreach (Label speed in speeds.Controls.OfType<Label>()) speed.ForeColor = primary;
                }
            }
        }

        private static bool IsDarkMode()
        {
            if (System.Windows.Forms.SystemInformation.HighContrast) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (System.Security.SecurityException) { return false; }
        }

        private static Color CardFillColor(bool highContrast, bool dark) =>
            highContrast ? SystemColors.Window : dark ? Color.FromArgb(224, 39, 43, 50) : Color.FromArgb(224, 250, 252, 255);
        private static Color CardBorderColor(bool highContrast, bool dark) =>
            highContrast ? SystemColors.WindowText : dark ? Color.FromArgb(67, 74, 84) : Color.FromArgb(220, 229, 240);
        private static Color TileFillColor(bool highContrast, bool dark) =>
            highContrast ? SystemColors.Highlight : dark ? Color.FromArgb(49, 61, 79) : Color.FromArgb(226, 237, 252);
        private static Color AccentColor(bool highContrast, bool dark) =>
            highContrast ? SystemColors.HighlightText : dark ? Color.FromArgb(174, 201, 244) : Color.FromArgb(49, 105, 179);

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
                var neuralByAdapter = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                var has3D = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_Neural%' OR Name LIKE '%engtype_3D%'",
                    // shortcut: Timeout bounds collection enumeration, not a blocked Get(); revisit if Get() must be cancellable.
                    new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) });
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
                    if (name.Contains("_engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        has3D.Add(luid);
                        continue;
                    }

                    if (!name.Contains("_engtype_Neural", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int engineEnd = name.IndexOf("_engtype_", physStart, StringComparison.OrdinalIgnoreCase);
                    if (engineEnd < 0) continue;

                    string engine = name.Substring(physStart, engineEnd - physStart);
                    float usage = instance["UtilizationPercentage"] == null
                        ? 0f
                        : Convert.ToSingle(instance["UtilizationPercentage"]);

                    if (!neuralByAdapter.TryGetValue(luid, out var engines))
                    {
                        engines = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                        neuralByAdapter[luid] = engines;
                    }

                    engines.TryGetValue(engine, out float current);
                    engines[engine] = current + usage;
                }

                string candidate = null;
                int candidateCount = 0;
                foreach (var adapter in neuralByAdapter)
                {
                    if (has3D.Contains(adapter.Key)) continue;
                    candidate = adapter.Key;
                    candidateCount++;
                }

                if (candidateCount != 1)
                {
                    if (!_loggedNpuProbe)
                    {
                        _loggedNpuProbe = true;
                        Log($"NPU counter unavailable: Neural adapters={neuralByAdapter.Count}, candidates={candidateCount}");
                    }
                    return null;
                }

                // shortcut: treats the sole Neural-only adapter as the NPU; add PnP-to-LUID mapping if multiple match.
                _loggedNpuProbe = true;
                float maxUsage = 0f;
                foreach (float usage in neuralByAdapter[candidate].Values)
                    if (usage > maxUsage) maxUsage = usage;

                return Math.Clamp(maxUsage, 0f, 100f);
            }
            catch (Exception ex)
            {
                if (!_loggedNpuProbe)
                {
                    _loggedNpuProbe = true;
                    Log("NPU counter failed: " + ex.Message);
                }
                return null;
            }
        }

        private float FilterTemperature(float value)
        {
            _tempSamples[_tempSampleIndex] = value;
            _tempSampleIndex = (_tempSampleIndex + 1) % _tempSamples.Length;
            if (_tempSampleCount < _tempSamples.Length)
                _tempSampleCount++;

            Span<float> sorted = stackalloc float[5];
            for (int i = 0; i < _tempSampleCount; i++)
                sorted[i] = _tempSamples[i];

            for (int i = 1; i < _tempSampleCount; i++)
            {
                float current = sorted[i];
                int j = i - 1;
                while (j >= 0 && sorted[j] > current)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = current;
            }

            return sorted[_tempSampleCount / 2];
        }

        private void ReplaceTrayIcon(string text, Color accent, string key)
        {
            if (_style == IconStyle.Mono)
                key += "|" + IconRenderer.MonoGlyphColor.ToArgb();

            if (key == _lastKey) return;

            var nextIcon = IconRenderer.Render(text, accent, _iconSize, _style);
            _tray.Icon = nextIcon;

            var previousIcon = _currentIcon;
            _currentIcon = nextIcon;
            _lastKey = key;
            previousIcon?.Dispose();
        }

        private static string FormatNetworkSpeed(float? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue) return "—";
            float speed = Math.Max(0, bytesPerSecond.Value);
            if (speed >= 1_073_741_824) return $"{speed / 1_073_741_824:0.00} GB/s";
            if (speed >= 104_857_600) return $"{speed / 1_048_576:0} MB/s";
            if (speed >= 1_048_576) return $"{speed / 1_048_576:0.0} MB/s";
            if (speed >= 1_024) return $"{speed / 1_024:0} KB/s";
            return $"{speed:0} B/s";
        }
        private void DrawRoundedPanel(object sender, PaintEventArgs e)
        {
            var panel = (Panel)sender;
            float scale = panel.DeviceDpi / 96f;
            var bounds = Rectangle.Inflate(panel.ClientRectangle, -1, -1);
            int diameter = Math.Min((int)(28 * scale), Math.Min(bounds.Width, bounds.Height));
            if (diameter < 2) return;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(CardFillColor(_detailsHighContrast, _detailsDarkMode));
            using var border = new Pen(CardBorderColor(_detailsHighContrast, _detailsDarkMode));
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        private static void DrawCategoryIcon(Graphics graphics, string category, Rectangle bounds, Color color, float scale)
        {
            int u(float value) => (int)Math.Round(value * scale);
            using var pen = new Pen(color, Math.Max(1.2f, 1.5f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int x = bounds.X, y = bounds.Y;
            switch (category)
            {
                case "CPU":
                    graphics.DrawRectangle(pen, x + u(10), y + u(10), u(10), u(10));
                    for (int i = 0; i < 3; i++)
                    {
                        int offset = u(12 + i * 3);
                        graphics.DrawLine(pen, x + offset, y + u(6), x + offset, y + u(10));
                        graphics.DrawLine(pen, x + offset, y + u(20), x + offset, y + u(24));
                        graphics.DrawLine(pen, x + u(6), y + offset, x + u(10), y + offset);
                        graphics.DrawLine(pen, x + u(20), y + offset, x + u(24), y + offset);
                    }
                    break;
                case "GPU":
                    graphics.DrawRectangle(pen, x + u(6), y + u(9), u(17), u(12));
                    graphics.DrawEllipse(pen, x + u(10), y + u(11), u(8), u(8));
                    graphics.DrawLine(pen, x + u(23), y + u(12), x + u(26), y + u(12));
                    graphics.DrawLine(pen, x + u(23), y + u(16), x + u(26), y + u(16));
                    break;
                case "NPU":
                    graphics.DrawLine(pen, x + u(9), y + u(17), x + u(15), y + u(10));
                    graphics.DrawLine(pen, x + u(15), y + u(10), x + u(22), y + u(17));
                    graphics.DrawLine(pen, x + u(9), y + u(17), x + u(17), y + u(23));
                    graphics.DrawLine(pen, x + u(22), y + u(17), x + u(17), y + u(23));
                    graphics.DrawEllipse(pen, x + u(6), y + u(14), u(6), u(6));
                    graphics.DrawEllipse(pen, x + u(12), y + u(7), u(6), u(6));
                    graphics.DrawEllipse(pen, x + u(19), y + u(14), u(6), u(6));
                    graphics.DrawEllipse(pen, x + u(14), y + u(20), u(6), u(6));
                    break;
                case "RAM":
                    graphics.DrawRectangle(pen, x + u(5), y + u(10), u(20), u(10));
                    for (int i = 0; i < 4; i++)
                        graphics.DrawLine(pen, x + u(9 + i * 4), y + u(12), x + u(9 + i * 4), y + u(18));
                    for (int i = 0; i < 4; i++)
                        graphics.DrawLine(pen, x + u(8 + i * 4), y + u(20), x + u(8 + i * 4), y + u(23));
                    break;
            }
        }
        private Label AddSection(string tile, (string, string)[] rows, out Label[] values, bool withNetwork = false)
        {
            var section = new BufferedSectionPanel
            {
                BorderStyle = BorderStyle.None,
                BackColor = Color.Transparent,
                Tag = tile,
                Width = 340,
                Margin = new Padding(0, 0, 0, 8)
            };
            section.Paint += DrawRoundedPanel;

            var icon = new BufferedCategoryIcon(tile, TileFillColor(_detailsHighContrast, _detailsDarkMode),
                AccentColor(_detailsHighContrast, _detailsDarkMode))
            {
                Bounds = new Rectangle(10, 10, 30, 30)
            };

            var layout = new TableLayoutPanel
            {
                AutoSize = false,
                ColumnCount = 2,
                RowCount = rows.Length + (withNetwork ? 2 : 1),
                Dock = DockStyle.Top,
                Padding = new Padding(52, 10, 10, 10),
                BackColor = Color.Transparent
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var name = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(320, 0),
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                Tag = "title",
                Margin = new Padding(3, 2, 3, 4)
            };
            layout.Controls.Add(name, 0, 0);
            layout.SetColumnSpan(name, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            values = new Label[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                int row = i + 1;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label
                {
                    Text = rows[i].Item1,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Tag = "caption",
                    Margin = new Padding(3, 2, 6, 2)
                }, 0, row);

                values[i] = new Label
                {
                    Text = "—",
                    AutoSize = true,
                    MaximumSize = new Size(285, 0),
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(3, 2, 3, 2)
                };
                layout.Controls.Add(values[i], 1, row);
            }

            if (withNetwork)
            {
                int row = rows.Length + 1;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { Text = "网速", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "caption", Margin = new Padding(3, 2, 6, 2) }, 0, row);
                var speeds = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
                _networkDownSpeed = new Label { AutoSize = true, Margin = Padding.Empty };
                _networkUpSpeed = new Label { AutoSize = true, Margin = new Padding(4, 0, 0, 0) };
                speeds.Controls.Add(_networkDownSpeed);
                speeds.Controls.Add(_networkUpSpeed);
                layout.Controls.Add(speeds, 1, row);
            }

            section.Controls.Add(layout);
            section.Controls.Add(icon);
            icon.BringToFront();
            layout.Width = section.ClientSize.Width;
            layout.Height = layout.GetPreferredSize(new Size(layout.Width, 0)).Height;
            section.Height = layout.Height;
            _detailsContent.Controls.Add(section);
            return name;
        }

        private void UpdateDetails()
        {
            bool layoutChanged = false;
            layoutChanged |= SetLabelText(_cpuName, string.IsNullOrEmpty(_hwName) ? "CPU" : _hwName);
            layoutChanged |= SetLabelText(_cpuTemp, _temp.HasValue ? $"{_temp.Value:0.0} °C" : "未读到温度");
            layoutChanged |= SetLabelText(_cpuLoad, _load.HasValue ? $"{_load.Value:0} %" : "—");
            layoutChanged |= SetLabelText(_cpuClock, _clock.HasValue ? $"{_clock.Value / 1000f:0.00} GHz" : "—");

            layoutChanged |= SetLabelText(_gpuNameLabel, string.IsNullOrEmpty(_gpuName) ? "GPU" : _gpuName);
            bool showGpuTemp = _gpuTemp.HasValue || (_hasGpuTempSample && _gpuTempMissingSamples < 3);
            if (_gpuTempRowVisible != showGpuTemp)
            {
                _gpuTempCaption.Visible = showGpuTemp;
                _gpuTempLabel.Visible = showGpuTemp;
                _gpuTempRowVisible = showGpuTemp;
                layoutChanged = true;
            }
            if (showGpuTemp) layoutChanged |= SetLabelText(_gpuTempLabel, _gpuTemp.HasValue ? $"{_gpuTemp.Value:0.0} °C" : "—");
            layoutChanged |= SetLabelText(_gpuLoadLabel, _gpuLoad.HasValue ? $"{_gpuLoad.Value:0.0} %" : "—");
            layoutChanged |= SetLabelText(_vramLabel, _vramUsed.HasValue && _vramTotal.HasValue
                ? $"共享 {_vramUsed.Value / 1024f:0.0} / {_vramTotal.Value / 1024f:0.0} GB"
                : "—");

            layoutChanged |= SetLabelText(_memoryLabel, "物理内存");
            layoutChanged |= SetLabelText(_memoryValue, _memUsedGb.HasValue && _memTotalGb.HasValue
                ? $"{_memUsedGb.Value:0.0} / {_memTotalGb.Value:0.0} GB"
                : "—");

            layoutChanged |= SetLabelText(_networkDownSpeed, "↓" + FormatNetworkSpeed(_networkDownloadBps));
            layoutChanged |= SetLabelText(_networkUpSpeed, "↑" + FormatNetworkSpeed(_networkUploadBps));

            layoutChanged |= SetLabelText(_npuName, "NPU");
            layoutChanged |= SetLabelText(_npuUsageLabel, _npuUsage.HasValue ? $"{_npuUsage.Value:0.0} %" : "—");
            if (layoutChanged) LayoutDetails();
        }

        private static bool SetLabelText(Label label, string text)
        {
            if (label == null || string.Equals(label.Text, text, StringComparison.Ordinal)) return false;
            Size preferredSize = label.GetPreferredSize(Size.Empty);
            label.Text = text;
            return label.GetPreferredSize(Size.Empty).Height != preferredSize.Height;
        }

        private void LayoutDetails()
        {
            if (_detailsContent == null || _detailsContent.ClientSize.Width <= 0) return;
            int scale(int value) => (int)Math.Round(value * _detailsContent.DeviceDpi / 96.0);
            int width = Math.Max(1, _detailsContent.ClientSize.Width - _detailsContent.Padding.Horizontal - System.Windows.Forms.SystemInformation.VerticalScrollBarWidth);
            foreach (Panel section in _detailsContent.Controls)
            {
                section.Width = width;
                var layout = section.Controls.OfType<TableLayoutPanel>().First();
                var icon = section.Controls.OfType<BufferedCategoryIcon>().FirstOrDefault();
                if (icon != null) icon.Bounds = new Rectangle(scale(10), scale(10), scale(30), scale(30));
                layout.Width = width;
                layout.Padding = new Padding(scale(44), scale(7), scale(8), scale(7));
                layout.ColumnStyles[0].Width = scale(40);
                layout.GetControlFromPosition(0, 0).MaximumSize = new Size(Math.Max(scale(60), width - layout.Padding.Horizontal), 0);
                for (int row = 1; row < layout.RowCount; row++)
                    layout.GetControlFromPosition(1, row).MaximumSize = new Size(Math.Max(scale(60), width - layout.Padding.Horizontal - scale(40)), 0);
                layout.PerformLayout();
                layout.Height = layout.GetPreferredSize(new Size(width, 0)).Height;
                section.Height = layout.Height;
            }
        }

        private void FitDetailsToScreen(Rectangle area)
        {
            int scale(int value) => (int)Math.Round(value * _details.DeviceDpi / 96.0);
            int frameWidth = Math.Max(0, _details.Width - _details.ClientSize.Width);
            int frameHeight = Math.Max(0, _details.Height - _details.ClientSize.Height);
            int maxWidth = Math.Max(1, area.Width - scale(24) - frameWidth);
            int maxHeight = Math.Max(1, area.Height - scale(48) - frameHeight);
            _details.MinimumSize = new Size(Math.Min(scale(280) + frameWidth, maxWidth + frameWidth), Math.Min(scale(180) + frameHeight, maxHeight + frameHeight));
            int width = Math.Min(scale(300), maxWidth);
            _details.ClientSize = new Size(width, _details.ClientSize.Height);
            LayoutDetails();
            int contentHeight = _detailsContent.Padding.Vertical + _detailsContent.Controls
                .Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
            _details.ClientSize = new Size(width, Math.Min(Math.Max(scale(220), contentHeight), maxHeight));
            _detailsScreenArea = area;
        }

        private void PositionDetails(Rectangle area)
        {
            int scale(int value) => (int)Math.Round(value * _details.DeviceDpi / 96.0);
            int frameWidth = Math.Max(0, _details.Width - _details.ClientSize.Width);
            int frameHeight = Math.Max(0, _details.Height - _details.ClientSize.Height);
            int maxWidth = Math.Max(1, area.Width - scale(24) - frameWidth);
            int maxHeight = Math.Max(1, area.Height - scale(24) - frameHeight);
            if (_details.Width > area.Width - scale(24) || _details.Height > area.Height - scale(24))
                _details.ClientSize = new Size(Math.Min(_details.ClientSize.Width, maxWidth), Math.Min(_details.ClientSize.Height, maxHeight));
            _details.Location = new Point(
                Math.Max(area.Left + scale(12), area.Right - _details.Width - scale(12)),
                Math.Max(area.Top + scale(12), area.Bottom - _details.Height - scale(12)));
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
                    "$a = New-ScheduledTaskAction -Execute $env:TT_EXE;" +
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
            Log("exit");
            Dispose();
            ExitThread();
        }

        internal void Log(string m)
        {
            lock (_logLock)
            {
                try
                {
                    if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxLogBytes)
                        File.Move(_logPath, _logPath + ".old", true);

                    File.AppendAllText(_logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine);
                }
                catch { }
            }
        }

        internal bool WaitForShutdown(TimeSpan timeout) => _shutdownCompletion.Task.Wait(timeout);

        private void FinishShutdown()
        {
            lock (_sampleLock)
            {
                if (_shutdownStarted) return;
                _shutdownStarted = true;
            }
            try
            {
                if (_computerOpenAttempted) _computer.Close();
            }
            catch (Exception ex) { Log("Computer.Close failed: " + ex.Message); }
            finally { _shutdownCompletion.TrySetResult(true); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                bool closeComputer = false;
                lock (_sampleLock)
                {
                    _exiting = true;
                    _disposed = true;
                    _samplePending = false;
                    if (!_sampleRunning)
                    {
                        _sampleRunning = true;
                        closeComputer = true;
                    }
                }
                _timer?.Stop();
                _npuTimer?.Stop();
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

                if (closeComputer)
                    _ = Task.Run(() =>
                    {
                        FinishShutdown();
                        lock (_sampleLock) _sampleRunning = false;
                    });

                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Icon = null;
                }

                _currentIcon?.Dispose();
                _currentIcon = null;

                _details?.Dispose();
                _menu?.Dispose();
                _tray?.Dispose();
                _timer?.Dispose();
                _npuTimer?.Dispose();
                _dispatcher.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

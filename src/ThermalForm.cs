using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DellFans
{
    // The on-demand window: borderless, one painted surface that also draws the caption (see DashboardSurface). Native behaviours (Aero Snap, caption
    // double-click, Alt+Space menu, edge resize, taskbar minimise) are kept through WM_NCHITTEST / WM_NCCALCSIZE. Closing it ends the process.
    public sealed class ThermalForm : Form
    {
        readonly bool preview;
        IThermalClient engine;
        Task polling, command;
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly DashboardSurface surface = new DashboardSurface { Dock = DockStyle.Fill };
        readonly ProcessMeter meter = new ProcessMeter();
        bool commandBusy, closing, connecting, connected, sampleFresh, disposedResources;
        Snapshot latest;
        FloorSettings savedProfile;
        readonly int? startupRpm;
        string lastError, lastErrorAtUtc, operation;
        bool operationWarn;
        DateTime operationUntil;
        static Icon appIcon;
        ChromeForm chrome; Size shapedSize; bool sizing, nativeCorners;
        // PawnIO install: the tray's --sensor-driver opens the consent dialog straight away; tests swap the starter and never run the real CLI.
        public bool StartWithDriverConsent;
        internal IElevatedStarter DriverStarter = new ShellElevatedStarter();
        internal string CliPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DellFans.Cli.exe");
        internal Task DriverTask;
        bool driverBusy;

        public ThermalForm(): this(false, null) { }
        public ThermalForm(IThermalClient client) : this(false, client)
        { if (client == null) throw new ArgumentNullException("client"); }
        public ThermalForm(int targetRpm) : this(false, null)
        { RpmRange.Validate(targetRpm); startupRpm = targetRpm; surface.SelectedMode = FanModes.Custom; surface.CustomPending = true; SetDraft(targetRpm.ToString(CultureInfo.CurrentCulture)); }
        ThermalForm(bool preview, IThermalClient client)
        {
            this.preview = preview; engine = client;
            Name = "ThermalWindow"; Text = "DellFans"; FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            float scale = Math.Max(0.75f, DeviceDpi / 96f);
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size minimum = new Size((int)(DashboardSurface.MinimumWidth * scale), (int)(560 * scale));
            ClientSize = new Size(Math.Min((int)(DashboardSurface.DefaultWidth * scale), work.Width - 40), Math.Min((int)(760 * scale), work.Height - 80));
            MinimumSize = new Size(Math.Min(minimum.Width, work.Width), Math.Min(minimum.Height, work.Height));
            StartPosition = FormStartPosition.Manual; Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
            Icon icon = AppIcon(); if (icon != null) Icon = icon;
            surface.StatusText = UiText.T("正在连接本机传感器…"); surface.Name = "Dashboard";
            surface.DeckOn = UiPreferences.DockVisible; surface.StartupOn = SafeStartup(); surface.ExportPath = exportFolder;
            surface.Activated += OnActivated; Controls.Add(surface);
            timer.Interval = 2000; timer.Tick += async delegate
            {
                if (latest != null && !IsFresh(latest)) { sampleFresh = false; state.Fresh = false; Refresh(); }
                if (engine == null) await ConnectAsync(); else await PollAsync();
            };
            Resize += delegate { timer.Interval = PollInterval; };
            VisibleChanged += delegate { timer.Interval = PollInterval; };
            if (!preview) Shown += async delegate
            {
                if (engine == null) StartBackground(); // a real window (tests pass their own client)
                await ConnectAsync();
                if (StartWithDriverConsent && !closing) { DriverTask = InstallDriverAsync(); Forget(DriverTask); }
                if (startupRpm.HasValue && !closing && CanWrite) await ExecuteAsync(UiText.F("保存自定义最低转速 {0} RPM", startupRpm.Value), delegate { engine.SetTargetRpm(startupRpm.Value); });
            };
            FormClosing += delegate { closing = true; timer.Stop(); cancellation.Cancel(); };
            UiTheme.Changed += ThemeChanged; UiText.IgnoredChanged += Redraw;
            ThemeChanged();
        }
        DashboardState state { get { return surface.State; } }
        internal DashboardSurface Surface { get { return surface; } }
        internal string StatusLine { get { return surface.StatusText; } }
        internal bool IsBusy { get { return commandBusy; } }
        internal static Icon AppIcon()
        {
            if (appIcon == null)
                try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            return appIcon;
        }
        static bool SafeStartup() { try { return UiPreferences.StartupEnabled; } catch (Exception) { return false; } }
        public void ShowFront()
        {
            if (IsDisposed) return;
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        void Redraw() { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) surface.Invalidate(); }); }
        void ThemeChanged()
        {
            if (IsDisposed || Disposing) return;
            UiPalette p = UiTheme.Current; BackColor = p.Background; surface.BackColor = p.Background;
            if (IsHandleCreated) UpdateFrame();
            surface.Invalidate();
        }
        // ---- frame ----
        const int HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
        // No WS_CAPTION and no visible frame, but WS_THICKFRAME / WS_SYSMENU / min / max boxes stay so Windows still snaps, sizes and animates the window.
        protected override CreateParams CreateParams
        { get { CreateParams p = base.CreateParams; p.Style = (p.Style & ~0x00C70000) | 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000; return p; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11 rounds and shadows the window itself (DWMWCP_ROUND); on Windows 10 the call fails and the 0.8 region + shadow window is used.
            int round = 2; nativeCorners = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) == 0;
            shapedSize = Size.Empty; UpdateFrame();
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateFrame(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); surface.Maximized = WindowState == FormWindowState.Maximized; UpdateFrame(); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); UpdateFrame(); }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); UpdateFrame(); }
        protected override void OnResizeEnd(EventArgs e) { sizing = false; base.OnResizeEnd(e); UpdateFrame(); }
        int FrameRadius { get { return (int)Math.Round(Math.Max(8, Math.Min(28, UiTheme.Current.CornerRadius)) * DeviceDpi / 96f); } }
        void ClearRegion() { Region old = Region; Region = null; if (old != null) old.Dispose(); shapedSize = Size.Empty; }
        void UpdateFrame()
        {
            if (IsDisposed || Disposing || !IsHandleCreated || nativeCorners) return;
            if (!Visible || WindowState != FormWindowState.Normal)
            { if (chrome != null) { chrome.Dispose(); chrome = null; } if (WindowState == FormWindowState.Maximized && Region != null) ClearRegion(); return; }
            if (Width < 8 || Height < 8) return;
            // While dragging an edge the shaped region and shadow are dropped (rebuilding them per mouse move is slow) and come back at ResizeEnd.
            if (sizing || Width > 2048 || Height > 2048) { if (chrome != null) chrome.Hide(); if (Region != null) ClearRegion(); return; }
            int radius = FrameRadius;
            if (Region == null || shapedSize != ClientSize) { Region previous = Region; Region = ChromeSurface.CreateHostRegion(ClientSize, radius); if (previous != null) previous.Dispose(); shapedSize = ClientSize; }
            if (chrome == null) chrome = new ChromeForm(false);
            chrome.Present(this, radius, BackColor);
        }
        // Where a screen point lands: resize edge / corner, caption (drag, snap, double-click) or the client (the painted surface handles it).
        internal int HitTest(Point screen)
        {
            Point p = PointToClient(screen);
            if (WindowState == FormWindowState.Normal)
            {
                int b = (int)Math.Round(8 * DeviceDpi / 96f);
                bool left = p.X < b, right = p.X >= ClientSize.Width - b, top = p.Y < b, bottom = p.Y >= ClientSize.Height - b;
                int edge = top ? (left ? HtTopLeft : right ? HtTopRight : HtTop) : bottom ? (left ? HtBottomLeft : right ? HtBottomRight : HtBottom) : left ? HtLeft : right ? HtRight : 0;
                if (edge != 0) return edge;
            }
            return surface.IsCaptionAt(surface.PointToClient(screen)) ? HtCaption : HtClient;
        }
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0214: sizing = true; break; // WM_SIZING
                case 0x0083: m.Result = IntPtr.Zero; return; // WM_NCCALCSIZE: the client area is the whole window, no native frame
                case 0x0085: if (WindowState != FormWindowState.Minimized) { m.Result = IntPtr.Zero; return; } break; // WM_NCPAINT
                case 0x0086: // WM_NCACTIVATE: keep activation semantics, but never let DefWindowProc repaint a caption
                    if (WindowState != FormWindowState.Minimized) { m.LParam = new IntPtr(-1); base.WndProc(ref m); return; }
                    break;
                case 0x0084: // WM_NCHITTEST
                    { long v = m.LParam.ToInt64(); m.Result = (IntPtr)HitTest(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF))); return; }
                case 0x0024: // WM_GETMINMAXINFO: a maximised window fills the work area, not the area plus the invisible sizing border
                    {
                        base.WndProc(ref m);
                        MinMaxInfo info = (MinMaxInfo)Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                        Screen screen = Screen.FromHandle(Handle); Rectangle work = screen.WorkingArea, bounds = screen.Bounds;
                        info.MaxPosition = new NativePoint { X = work.Left - bounds.Left, Y = work.Top - bounds.Top }; info.MaxSize = new NativePoint { X = work.Width, Y = work.Height };
                        Marshal.StructureToPtr(info, m.LParam, false); return;
                    }
            }
            base.WndProc(ref m);
        }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        protected override bool ShowWithoutActivation { get { return preview; } }
        int NormalPollInterval { get { return latest != null && latest.Resources != null && latest.Resources.LowOverheadMode ? 4000 : 2000; } }
        internal int PollInterval { get { return Visible && WindowState != FormWindowState.Minimized ? NormalPollInterval : 10000; } }
        public Task StartMonitoringAsync() { return ConnectAsync(); }
        bool CanWrite
        {
            get
            {
                object capability;
                return !closing && connected && sampleFresh && latest != null && latest.Background != null && latest.Background.Settings != null &&
                    latest.Background.State != "Initializing" && latest.Background.State != "Unavailable" && !latest.Background.ProbeActive &&
                    latest.ManualControl != null && latest.ManualControl.State != "Unavailable" && latest.ControlCapabilities != null &&
                    latest.ControlCapabilities.TryGetValue("TargetRpmImplemented", out capability) && capability is bool && (bool)capability;
            }
        }
        // ---- input ----
        void OnActivated(string id)
        {
            if (closing) return;
            if (id.StartsWith("cap:", StringComparison.Ordinal)) { Caption(id.Substring(4)); return; }
            if (id.StartsWith("nav:", StringComparison.Ordinal)) { ShowPage(id.Substring(4) == "details" ? UiPage.Details : id.Substring(4) == "settings" ? UiPage.Settings : UiPage.Home); return; }
            if (id.StartsWith("mode:", StringComparison.Ordinal)) { SelectMode(id.Substring(5)); return; }
            if (id.StartsWith("theme:", StringComparison.Ordinal)) { Guarded(delegate { UiPreferences.SelectTheme(id.Substring(6)); }); return; }
            if (id.StartsWith("lang:", StringComparison.Ordinal)) { Guarded(delegate { UiPreferences.SelectLanguage(id.Substring(5)); RefreshText(); }); return; }
            switch (id)
            {
                case "apply": ApplyCustom(); break;
                case "retry": Forget(ConnectAsync()); break;
                case "driver:install": DriverTask = InstallDriverAsync(); Forget(DriverTask); break;
                case "driver:later": surface.DriverDismissed = true; surface.Invalidate(); break;
                case "langmore": surface.MoreLanguages = !surface.MoreLanguages; surface.Invalidate(); break;
                case "toggle:deck": Guarded(delegate { UiPreferences.DockVisible = !UiPreferences.DockVisible; surface.DeckOn = UiPreferences.DockVisible; surface.Invalidate(); }); break;
                case "toggle:startup": Guarded(delegate { UiPreferences.SetStartup(!UiPreferences.StartupEnabled); surface.StartupOn = UiPreferences.StartupEnabled; surface.Invalidate(); }); break;
                case "tool:export": ExportTask = ExportAsync(); Forget(ExportTask); break;
                case "tool:exportdir": OpenExportFolder(); break;
                case "tool:themes": UiPreferences.OpenDirectory(UiTheme.ThemesDirectory); break;
                case "tool:languages": UiPreferences.OpenDirectory(UiText.PacksDirectory); break;
            }
        }
        void Caption(string action)
        {
            if (action == "min") WindowState = FormWindowState.Minimized;
            else if (action == "max") WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            else if (action == "close") Close();
        }
        // Commands report their own errors on the status line; this only observes the task so nothing is left unhandled.
        static void Forget(Task task) { task.ContinueWith(delegate (Task t) { GC.KeepAlive(t.Exception); }, TaskContinuationOptions.OnlyOnFaulted); }
        void Guarded(Action action)
        { try { action(); } catch (Exception e) { ShowError(e); } }
        void RefreshText() { bool warn; surface.StatusText = StatusMessage(out warn); surface.StatusWarn = warn; surface.Input.AccessibleName = UiText.T("最低 RPM"); surface.Invalidate(); }
        void ShowPage(UiPage page)
        {
            if (surface.Page == page) return;
            surface.Page = page; surface.ResetScroll();
            if (page == UiPage.Details) { state.Totals = meter.Sample(latest == null ? null : latest.Resources); }
            surface.Invalidate();
            if (page == UiPage.Home && surface.CustomPending) surface.Input.Focus();
        }
        void SelectMode(string mode)
        {
            if (!FanModes.IsValid(mode) || commandBusy || !CanWrite) return;
            if (mode == FanModes.Custom)
            {
                surface.SelectedMode = mode; surface.CustomPending = true;
                if (string.IsNullOrEmpty(surface.DraftText)) SetDraft((savedProfile != null && savedProfile.MinimumRpm.HasValue ? savedProfile.MinimumRpm.Value : 2500).ToString(CultureInfo.CurrentCulture));
                surface.Invalidate(); surface.BeginInvoke((MethodInvoker)delegate { if (!closing) { surface.Input.Focus(); surface.Input.SelectAll(); } });
                return;
            }
            surface.CustomPending = false;
            if (savedProfile != null && savedProfile.Mode == mode) { surface.SelectedMode = mode; surface.Invalidate(); return; }
            if (mode == FanModes.Auto && !UiDialogs.ConfirmAutomatic(this)) { surface.SelectedMode = surface.CustomPending ? FanModes.Custom : state.Mode; surface.Invalidate(); return; }
            surface.SelectedMode = mode;
            Forget(ExecuteAsync(UiText.F("保存模式 {0}", DashboardState.ModeName(mode)), delegate { engine.SetMode(mode, null); }));
        }
        void ApplyCustom()
        {
            decimal value;
            if (!decimal.TryParse(surface.DraftText, NumberStyles.Number, CultureInfo.CurrentCulture, out value) || value != decimal.Truncate(value) || value < RpmRange.Minimum || value > RpmRange.Maximum)
            { SetOperation(UiText.T("请输入 1000–5570 的整数 RPM。"), true); surface.Input.Focus(); return; }
            int rpm = (int)value;
            Forget(ExecuteAsync(UiText.F("保存自定义最低转速 {0} RPM", rpm), delegate { engine.SetMode(FanModes.Custom, rpm); }, true));
        }
        void SetDraft(string text) { surface.DraftText = text; surface.Input.Text = text; }
        void SetOperation(string text, bool warn)
        { operation = text; operationWarn = warn; operationUntil = DateTime.UtcNow.AddSeconds(10); surface.StatusText = text; surface.StatusWarn = warn; surface.Invalidate(); }
        // ---- service ----
        async Task ConnectAsync()
        {
            if (closing || commandBusy || connecting) return;
            connecting = true; surface.Busy = true;
            try { timer.Start(); if (engine == null) engine = await Task.Run<IThermalClient>(delegate { return new ThermalClient(); }); if (closing) { if (engine != null) engine.Dispose(); return; } await PollAsync(); }
            catch (Exception e) { if (!closing) MarkDisconnected(e); }
            finally { connecting = false; if (!closing) { surface.Busy = false; surface.Invalidate(); } }
        }
        async Task PollAsync()
        {
            if (engine == null || commandBusy || closing || (polling != null && !polling.IsCompleted)) return;
            Task<Snapshot> task = Task.Run(delegate { return engine.Poll(); }); polling = task;
            try { Snapshot sample = await task; if (!closing) Display(sample); }
            catch (Exception e) { if (!closing) MarkDisconnected(e); }
        }
        void MarkDisconnected(Exception error)
        {
            RememberError(error); connected = sampleFresh = false;
            state.MarkDisconnected(DateTime.Now);
            // A service that is not running is its own state (the unelevated interface cannot start it), not a lost connection.
            operation = null;
            surface.StatusText = UiText.T(error is ServiceNotRunningException ? "后台控速未运行 · 重新打开窗口可启动（需要管理员授权）" : "后台断连 · 最后读数已过期，控制状态无法确认");
            surface.StatusWarn = true; surface.CanWrite = false; surface.Invalidate();
        }
        async Task ExecuteAsync(string label, Action action, bool acceptsDraft = false)
        {
            if (commandBusy || engine == null || !CanWrite) return;
            commandBusy = true; surface.Busy = true; surface.CanWrite = false; SetOperation(label + "…", false);
            bool requestAccepted = false;
            try
            {
                Task<Snapshot> task = Task.Run(delegate { action(); requestAccepted = true; return engine.Poll(); }); command = task; Snapshot sample = await task;
                if (!closing)
                {
                    if (!IsFresh(sample)) throw new InvalidOperationException("读数已过期 · 等待后台新快照");
                    if (acceptsDraft) surface.CustomPending = false;
                    Display(sample); SetOperation(UiText.F("{0}，已应用，当前状态已更新。", label), false);
                }
            }
            catch (Exception e)
            {
                if (!closing)
                {
                    if (requestAccepted) MarkDisconnected(e);
                    else { ShowError(e); operation += " " + UiText.T("保存失败，输入已保留，可重试。"); surface.StatusText = operation; }
                    // The saved mode is what the service still runs; show it again so the selection never lies.
                    if (!surface.CustomPending) surface.SelectedMode = state.Mode;
                }
            }
            finally { commandBusy = false; if (!closing) { surface.Busy = false; surface.CanWrite = CanWrite; surface.Invalidate(); } }
        }
        static bool IsFresh(Snapshot sample)
        { DateTime observed; return sample != null && DateTime.TryParse(sample.PolledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out observed) && Math.Abs((DateTime.UtcNow - observed.ToUniversalTime()).TotalSeconds) <= 10; }
        void Display(Snapshot sample)
        {
            if (sample == null) throw new InvalidOperationException("服务正在初始化，控制暂不可用");
            latest = sample; connected = true; sampleFresh = IsFresh(sample);
            state.Update(sample, DateTime.Now, sampleFresh);
            if (state.SensorDriverState == "Installed") surface.DriverOverride = null;
            if (sample.Background != null && sample.Background.Settings != null) savedProfile = sample.Background.Settings.Copy();
            if (!surface.CustomPending) surface.SelectedMode = state.Mode;
            if (surface.CustomPending && string.IsNullOrEmpty(surface.DraftText) && savedProfile != null && savedProfile.MinimumRpm.HasValue) SetDraft(savedProfile.MinimumRpm.Value.ToString(CultureInfo.CurrentCulture));
            surface.CanWrite = CanWrite && !commandBusy;
            if (surface.Page == UiPage.Details) state.Totals = meter.Sample(sample.Resources);
            timer.Interval = PollInterval;
            if (operation != null && DateTime.UtcNow < operationUntil) { surface.StatusText = operation; surface.StatusWarn = operationWarn; }
            else { operation = null; bool warn; surface.StatusText = StatusMessage(out warn); surface.StatusWarn = warn; }
            if (Visible && WindowState != FormWindowState.Minimized) surface.Invalidate();
            CheckCapability(sample);
            surface.AccessibleDescription = (state.Rpm.HasValue ? state.Rpm.Value.ToString("N0") + " RPM, " : "") + (state.CpuPackage.HasValue ? state.CpuPackage.Value.ToString("0") + " °C" : "");
        }
        string StatusMessage(out bool warn)
        {
            warn = true; Snapshot sample = latest;
            if (!connected || sample == null) return surface.StatusText ?? "";
            if (!sampleFresh) return UiText.T("读数已过期 · 等待后台新快照");
            BackgroundStatus b = sample.Background;
            if (b == null || b.Settings == null || b.State == "Initializing") { warn = false; return UiText.T("服务正在初始化，控制暂不可用"); }
            if (b.ProbeActive) { warn = false; return UiText.T("正在探测转速；完成后恢复已保存配置…"); }
            if (b.State == "FaultAutomatic") return UiText.T("故障暂停 · BIOS 自动，已保存的模式仍保留");
            if (b.State == "EmergencyFullSpeed") return UiText.T("CPU 温度达到 90 °C，保持全速，降到 85 °C 后恢复模式规则。");
            CapabilityStatus capability = b.Capability;
            if (capability != null && capability.State == CapabilityStates.ReadPassed) return UiText.T("尚未检测风扇控制接口 · 风扇保持 BIOS 自动；重新打开窗口可开始检测");
            if (capability != null && (capability.State == CapabilityStates.Failed || capability.State == CapabilityStates.Unsupported)) return UiText.T("当前电脑不支持 · 风扇保持 BIOS 自动");
            if (!CanWrite) return UiText.T("当前机型 / BIOS 未提供手动 RPM 控制");
            if (sample.Warnings != null && sample.Warnings.Count > 0) return string.Join(" | ", sample.Warnings.Select(w => UiText.Error(w)).ToArray());
            warn = false;
            DateTime time; DateTime.TryParse(sample.PolledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time);
            return UiText.F("更新 {0}", time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)) + (b.Settings.Mode == FanModes.Auto ? " · " + UiText.T("BIOS 控制 · 选择模式后立即生效") : " · " + UiText.T("关闭窗口后继续按已保存模式运行"));
        }
        // ---- PawnIO driver ----
        async Task InstallDriverAsync()
        {
            DashboardSurface.DriverNotice notice = surface.Notice;
            if (closing || driverBusy || notice == null || !notice.Buttons) return;
            if (!UiDialogs.ConfirmDriverInstall(this)) return;
            driverBusy = true; surface.DriverInstalling = true; surface.Invalidate(); SetOperation(UiText.T("正在安装…"), false);
            string resultFile = null; DriverInstallOutcome outcome;
            try
            {
                resultFile = Path.GetTempFileName(); // empty, owned by this user; the elevated CLI only fills a file like this
                string cli = CliPath, file = resultFile; IElevatedStarter starter = DriverStarter;
                outcome = await Task.Run(delegate { return DriverInstallFlow.Run(starter, cli, file); });
            }
            catch (Exception e) { outcome = new DriverInstallOutcome { Kind = DriverInstallKind.Failed, Message = e.GetBaseException().Message }; }
            finally { if (resultFile != null) try { File.Delete(resultFile); } catch (Exception) { } }
            driverBusy = false;
            if (closing) return;
            surface.DriverInstalling = false;
            if (outcome.Kind == DriverInstallKind.Installed) surface.DriverOverride = "Installed";
            else if (outcome.Kind == DriverInstallKind.RestartRequired) surface.DriverOverride = "NeedsRestart";
            SetOperation(DriverInstallFlow.Describe(outcome), outcome.Kind == DriverInstallKind.Failed);
            await PollAsync();
        }
        // ---- portable package: the elevated controller and the tray run next to this window ----
        void StartBackground()
        {
            BackgroundHost.StartTray();
            bool running = false;
            try { running = BackgroundHost.Running(); } catch (Exception e) { RememberError(e); }
            if (running) return;
            bool started = false;
            try { started = BackgroundHost.Start(); } // UAC; the window waits for the answer
            catch (Exception e) { ShowError(e); return; }
            SetOperation(UiText.T(started ? "正在启动后台控速…" : "已取消管理员授权，后台控速未启动；重新打开窗口可再次启动。"), !started);
        }
        // ---- capability: checked once per window; every machine passes the consented full-speed test before control ----
        internal Task CapabilityTask;
        internal string CapabilityRoot = UiPreferences.DirectoryPath; // reports go to <root>\reports, readable by this user
        bool capabilityHandled;
        const string UnsupportedNotifiedValue = "UnsupportedNotified";
        void CheckCapability(Snapshot sample)
        {
            if (preview || capabilityHandled || closing || sample.Background == null || sample.Background.Capability == null) return;
            CapabilityStatus c = sample.Background.Capability;
            if (c.State == CapabilityStates.ReadPassed)
            { capabilityHandled = true; BeginInvoke((MethodInvoker)delegate { CapabilityTask = TestCapabilityAsync(); Forget(CapabilityTask); }); }
            else if (c.State == CapabilityStates.Failed || c.State == CapabilityStates.Unsupported)
            { capabilityHandled = true; BeginInvoke((MethodInvoker)delegate { ReportUnsupported(c, false); }); }
        }
        async Task TestCapabilityAsync()
        {
            if (closing || engine == null || commandBusy) return;
            if (!UiDialogs.ConfirmCapabilityTest(this)) { SetOperation(UiText.T("尚未检测风扇控制接口，风扇保持 BIOS 自动；重新打开窗口可开始检测。"), true); return; }
            commandBusy = true; surface.Busy = true; surface.CanWrite = false; SetOperation(UiText.T("正在检测风扇控制接口（风扇将短暂全速，约 20 秒）…"), false);
            Snapshot sample = null; Exception error = null;
            try { IThermalClient client = engine; sample = await Task.Run(delegate { return client.RunCapabilityProbe(); }); }
            catch (Exception e) { error = e; }
            finally { commandBusy = false; if (!closing) { surface.Busy = false; surface.CanWrite = CanWrite; } }
            if (closing) return;
            if (error != null || sample == null) { ShowError(error ?? new InvalidOperationException("服务正在初始化，控制暂不可用")); return; }
            Display(sample);
            CapabilityStatus c = sample.Background == null ? null : sample.Background.Capability;
            if (c != null && c.State == CapabilityStates.Passed) SetOperation(UiText.T("检测通过：这台电脑支持 DellFans 风扇控制。"), false);
            else if (c != null) ReportUnsupported(c, true);
        }
        // Writes an anonymous report the user can read and submit, then says so once per model and BIOS (always after a test the user ran).
        void ReportUnsupported(CapabilityStatus c, bool afterTest)
        {
            if (closing) return;
            string key = (c.Model ?? "?") + "|" + (c.BiosVersion ?? "?");
            string notified = null;
            try { notified = UiPreferences.Registry.GetString(UnsupportedNotifiedValue); } catch (Exception) { }
            if (!afterTest && notified == key) return;
            string path = null;
            try
            {
                Snapshot sample = latest;
                path = new CapabilityStore(CapabilityRoot).WriteReport(c.Model, c.BiosVersion, Environment.OSVersion.Version.ToString(), CpuName(sample),
                    sample == null ? null : sample.SensorSource, c.Checks ?? new List<CapabilityCheck>(), DateTime.UtcNow);
            }
            catch (Exception e) { RememberError(e); }
            try { UiPreferences.Registry.SetString(UnsupportedNotifiedValue, key); } catch (Exception) { }
            if (UiDialogs.ShowUnsupported(this, path) && path != null) Guarded(delegate { StartProcess("explorer.exe", "/select,\"" + path + "\""); });
        }
        static string CpuName(Snapshot sample)
        {
            if (sample == null || sample.Sensors == null) return null;
            SensorRow cpu = sample.Sensors.FirstOrDefault(r => r.HardwareType == "Cpu" && !string.IsNullOrEmpty(r.Device));
            return cpu == null ? null : cpu.Device;
        }
        // ---- export ----
        // Tests swap the folder, the file writer and the process starter so nothing real is written or launched.
        string exportFolder = Path.Combine(UiPreferences.DirectoryPath, "Reports");
        internal string ExportFolder { get { return exportFolder; } set { exportFolder = value; surface.ExportPath = value; } }
        internal Action<string, object> ExportWriter = ReportFiles.Write;
        internal Action<string, string> StartProcess = delegate (string file, string arguments) { Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false }); };
        internal Task ExportTask;
        void OpenExportFolder()
        { Guarded(delegate { Directory.CreateDirectory(ExportFolder); StartProcess("explorer.exe", "\"" + ExportFolder + "\""); }); }
        async Task ExportAsync()
        {
            string path;
            try
            {
                // Routine polls carry summary rows only; export asks the service for every sensor.
                Snapshot sample = latest;
                if (engine is ThermalClient)
                    try { sample = await Task.Run(delegate { return ThermalClient.Send(new ServiceRequest { Command = "Status", Detail = true }); }) ?? latest; }
                    catch (Exception) { sample = latest; } // Service busy or down: export the last displayed snapshot.
                if (closing) return;
                var parser = new System.Web.Script.Serialization.JavaScriptSerializer();
                Dictionary<string, object> document = sample == null ? new Dictionary<string, object>() : parser.Deserialize<Dictionary<string, object>>(ReportFiles.Json(sample));
                document["UiDiagnostics"] = new { Error = lastError, AtUtc = lastErrorAtUtc };
                path = Path.Combine(ExportFolder, "dellfans-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                ExportWriter(path, document); SetOperation(UiText.F("已导出：{0}", Path.GetFileName(path)), false);
            }
            catch (Exception e)
            {
                ShowError(e);
                if (!closing) UiDialogs.ShowMessage(this, UiText.F("无法导出当前数据：\n{0}", e.GetBaseException().Message), UiText.T("导出失败"));
                return;
            }
            if (closing) return;
            if (UiDialogs.ShowExportDone(this, path)) Guarded(delegate { StartProcess("explorer.exe", "/select,\"" + path + "\""); });
        }
        void RememberError(Exception error) { lastError = error.ToString(); lastErrorAtUtc = DateTime.UtcNow.ToString("o"); }
        void ShowError(Exception error) { RememberError(error); SetOperation(UiText.Error(error.GetBaseException().Message), true); }
        // ---- offline rendering ----
        public static void RenderPreview(Snapshot sample, string path) { RenderPreview(sample, path, new Size(DashboardSurface.DefaultWidth, 760)); }
        public static void RenderPreview(Snapshot sample, string path, Size size)
        { RenderPage(sample, path, UiPage.Home, 1, size.Width, null); }
        // Draws one page of the window to a PNG without a window or a service. The caption is drawn by the surface, so it is part of the image; the shadow and rounded corners belong to the window.
        public static void RenderPage(Snapshot sample, string path, UiPage page, float scale, int logicalWidth, Action<DashboardSurface> decorate)
        {
            using (DashboardSurface s = new DashboardSurface { PreviewMode = true, Page = page, CanWrite = true })
            {
                s.State.Update(sample, DateTime.Now, true);
                s.SelectedMode = s.State.Mode; s.StatusText = UiText.F("更新 {0}", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)) + " · " + UiText.T("关闭窗口后继续按已保存模式运行");
                if (decorate != null) decorate(s);
                float height = Math.Max(760, DashboardSurface.HeaderHeight + s.MeasureContent(logicalWidth, scale));
                using (Bitmap bitmap = new Bitmap((int)Math.Ceiling(logicalWidth * scale), (int)Math.Ceiling(height * scale), System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    s.Render(g, new SizeF(logicalWidth, height), scale);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposedResources)
            {
                disposedResources = true; closing = true; timer.Dispose(); cancellation.Cancel();
                UiTheme.Changed -= ThemeChanged; UiText.IgnoredChanged -= Redraw;
                if (chrome != null) { chrome.Dispose(); chrome = null; }
                if (command == null || command.IsCompleted) cancellation.Dispose(); else command.ContinueWith(delegate { cancellation.Dispose(); });
                if (engine != null) engine.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

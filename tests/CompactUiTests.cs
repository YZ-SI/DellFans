using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DellFans;
using Microsoft.Win32;

internal sealed class OfflineThermalClient : IThermalClient
{
    public bool FailSave;
    public bool FailPoll, FrozenTime;
    public bool LosePollAfterSave;
    DateTime probeEnd;
    public Snapshot Sample;
    public int PollCount, WriteCount, DisposeCount;
    public ManualResetEvent PollGate;
    public Exception PollError;
    public OfflineThermalClient()
    {
        Sample = new Snapshot { Model = "Offline OptiPlex", BiosVersion = "offline", PolledAtUtc = DateTime.UtcNow.ToString("o"),
            Sensors = new List<SensorRow>(), Warnings = new List<string>(),
            ControlCapabilities = new Dictionary<string, object> { { "TargetRpmImplemented", true } },
            ManualControl = new ManualFanStatus { State = "Idle" },
            Background = new BackgroundStatus { State = "FloorCurve", Settings = FloorSettings.Defaults() } };
        Sample.Background.Settings.Mode = "custom"; Sample.Background.Settings.MinimumRpm = 2500; Sample.Background.Mode = "custom";
        Sample.Background.ModeLimits = FanModes.Limits("custom", 2500);
        Sample.Background.EffectiveTargetRpm = 2500;
        Sample.Sensors.Add(new SensorRow { Id = "cpu/package", HardwareType = "Cpu", Name = "CPU Package", Type = "Temperature", Role = "Temperature", Value = 52, Quality = "OK" });
        Sample.Sensors.Add(new SensorRow { Id = "cpu/max", HardwareType = "Cpu", Name = "Core Max", Type = "Temperature", Role = "DerivedTemperature", Value = 55, Quality = "OK" });
        Sample.Sensors.Add(new SensorRow { Id = "cpu/avg", HardwareType = "Cpu", Name = "Core Average", Type = "Temperature", Role = "DerivedTemperature", Value = 49, Quality = "OK" });
        Sample.Sensors.Add(new SensorRow { Id = "dell/fan/F600", HardwareType = "DellFirmware", Name = "System / CPU Fan", Type = "Fan", Role = "Measurement", Value = 2520, Minimum = 1500, Maximum = 3000, Quality = "OK" });
        Sample.Sensors.Add(new SensorRow { Id = "nvme/0", HardwareType = "Storage", Name = "Composite", Type = "Temperature", Role = "Temperature", Device = "Test NVMe", Value = 38, Maximum = 44, Quality = "OK" });
    }
    public Snapshot Poll()
    {
        Interlocked.Increment(ref PollCount);
        if (PollGate != null) PollGate.WaitOne();
        if (PollError != null) throw PollError;
        if (FailPoll) throw new InvalidOperationException("offline connection lost");
        if (!FrozenTime) Sample.PolledAtUtc = DateTime.UtcNow.ToString("o");
        if (Sample.Background.ProbeActive && DateTime.UtcNow > probeEnd) { Sample.Background.ProbeActive = false; Sample.Background.State = "FloorCurve"; }
        return Sample;
    }
    public void SetMode(string mode, int? minimumRpm)
    {
        WriteCount++;
        if (FailSave) throw new InvalidOperationException("offline save rejected");
        BackgroundStatus background = Sample.Background;
        background.Settings.Mode = mode; if (mode == "custom") background.Settings.MinimumRpm = minimumRpm; // other modes keep the remembered custom minimum
        background.Mode = mode; background.ModeLimits = FanModes.Limits(mode, background.Settings.MinimumRpm); background.State = mode == "auto" ? "BiosAutomatic" : "FloorCurve";
        LastMode = mode; LastMinimum = minimumRpm;
        if (LosePollAfterSave) FailPoll = true;
    }
    public string LastMode; public int? LastMinimum;
    public void SetTargetRpm(int rpm) { SetMode("custom", rpm); }
    public void SetAutomatic() { SetMode("auto", null); }
    public void SetFullPersistent() { SetTargetRpm(5570); }
    public Snapshot BeginProbe() { Sample.Background.ProbeActive = true; Sample.Background.State = "Probing"; probeEnd = DateTime.UtcNow.AddMilliseconds(100); return Sample; }
    public CapabilityStatus ProbeResult; public int ProbeCount;
    public Snapshot RunCapabilityProbe() { ProbeCount++; Sample.Background.Capability = ProbeResult; return Sample; }
    public void Dispose() { DisposeCount++; }
}

// Drives the real window headlessly against OfflineThermalClient: no service, no pipe, and preferences go to a temporary HKCU subkey.
internal static class CompactUiTests
{
    static int passed;
    static readonly List<string> roots = new List<string>();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static void Until(Func<bool> ready, string message)
    {
        Stopwatch time = Stopwatch.StartNew();
        while (!ready() && time.ElapsedMilliseconds < 7000) { Application.DoEvents(); Thread.Sleep(15); }
        if (!ready()) throw new Exception(message);
    }
    static UiRegistry UseTemporaryRegistry()
    {
        string root = @"Software\DellFans-UiTest-" + Guid.NewGuid().ToString("N"); roots.Add(root);
        UiRegistry registry = new UiRegistry(root + @"\Ui", root + @"\Run");
        UiPreferences.Registry = registry; return registry;
    }
    static void Restore() { foreach (string root in roots) Registry.CurrentUser.DeleteSubKeyTree(root, false); roots.Clear(); UiPreferences.Registry = null; UiDialogs.ConfirmOverride = null; UiDialogs.DriverConsentOverride = null; UiDialogs.CapabilityConsentOverride = null; UiDialogs.UnsupportedOverride = null; UiTheme.Select("black"); UiText.Select("zh-CN"); }
    static ThermalForm Open(OfflineThermalClient client)
    {
        ThermalForm form = new ThermalForm(client); form.Show();
        Until(() => form.Surface.CanWrite || client.FailPoll, "offline service did not connect"); return form;
    }
    static void ModesApplyImmediately()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = Open(client))
        {
            DashboardSurface s = form.Surface;
            Check(s.SelectedMode == "custom" && s.State.Mode == "custom" && s.State.CustomMinimum == 2500, "the saved mode is selected after the first poll");
            Check(s.State.Rpm == 2520 && s.State.CpuPackage == 52 && s.State.CoreMax == 55 && s.State.DiskTemp == 38 && s.State.DiskName == "Test NVMe", "fan, CPU package, core max and the hottest disk are shown");
            s.Activate("mode:quiet"); Until(() => client.LastMode == "quiet" && !form.IsBusy, "quiet was not saved");
            Check(client.LastMinimum == null && s.SelectedMode == "quiet" && client.WriteCount == 1, "a preset mode is applied by one click, without a minimum");
            s.Activate("mode:quiet"); Application.DoEvents();
            Check(client.WriteCount == 1, "selecting the active mode again must not write");
            s.Activate("mode:performance"); Until(() => client.LastMode == "performance" && !form.IsBusy, "performance was not saved");
            Check(client.Sample.Background.Settings.MinimumRpm == 2500, "presets keep the remembered custom minimum");
            UiDialogs.ConfirmOverride = delegate { return false; };
            s.Activate("mode:auto"); Application.DoEvents();
            Check(client.WriteCount == 2 && s.SelectedMode == "performance", "cancelling the BIOS confirmation writes nothing and keeps the selection");
            UiDialogs.ConfirmOverride = delegate { return true; };
            s.Activate("mode:auto"); Until(() => client.LastMode == "auto" && !form.IsBusy, "BIOS automatic was not saved");
            Check(s.SelectedMode == "auto" && s.State.Mode == "auto", "BIOS automatic selected");
        }
        Check(client.DisposeCount == 1, "closing the window disposes the client");
        Console.WriteLine("PASS modes apply with one click; BIOS automatic asks first; the custom minimum is remembered");
    }
    static void CustomMinimum()
    {
        OfflineThermalClient client = new OfflineThermalClient { FailSave = true };
        client.Sample.Background.Settings.Mode = "balanced"; client.Sample.Background.Mode = "balanced"; client.Sample.Background.ModeLimits = FanModes.Limits("balanced", null);
        using (ThermalForm form = Open(client))
        {
            DashboardSurface s = form.Surface;
            Check(!s.Input.Visible, "the minimum input is hidden unless custom is chosen");
            s.Activate("mode:custom"); Application.DoEvents();
            Check(s.CustomPending && s.SelectedMode == "custom" && s.DraftText == "2500" && client.WriteCount == 0, "choosing custom only opens the minimum input, prefilled with the saved value");
            s.Input.Text = "999"; s.Activate("apply"); Application.DoEvents();
            Check(client.WriteCount == 0 && form.StatusLine.Contains("1000") && s.StatusWarn, "an out-of-range minimum is refused with a message");
            s.Input.Text = "3000"; s.Activate("apply"); Until(() => form.StatusLine.Contains(UiText.Error("offline save rejected")), "the save error was not shown");
            Check(s.DraftText == "3000" && s.CustomPending && s.SelectedMode == "custom", "a failed save keeps the draft for a retry");
            Check(client.Sample.Background.Settings.Mode == "balanced", "a failed save changes nothing at the service");
            client.FailSave = false; Until(() => !form.IsBusy, "still busy"); s.Activate("apply");
            Until(() => client.LastMode == "custom" && !form.IsBusy, "the retry was not saved");
            Check(client.LastMinimum == 3000 && !s.CustomPending && s.State.CustomMinimum == 3000, "the retry saved the typed minimum and ended the draft");
            s.Activate("nav:details"); Application.DoEvents();
            Check(!s.Input.Visible, "the minimum input is hidden on other pages");
        }
        Console.WriteLine("PASS custom minimum: prefilled, validated, kept after a failed save, saved on retry");
    }
    static void Disconnected()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = Open(client))
        {
            DashboardSurface s = form.Surface;
            client.FailPoll = true; client.Sample.PolledAtUtc = DateTime.UtcNow.AddMinutes(-1).ToString("o");
            Until(() => !s.State.Connected, "a lost connection was not noticed");
            Check(s.StatusWarn && s.StatusText == UiText.T("后台断连 · 最后读数已过期，控制状态无法确认") && !s.CanWrite, "disconnected: warning status, controls disabled");
            s.Activate("mode:quiet"); Application.DoEvents();
            Check(client.WriteCount == 0, "no write while disconnected");
            Check(s.State.Events.Any(e => e.Key == "后台断连" && e.Warn), "the disconnect is recorded as an event");
            client.FailPoll = false; client.FrozenTime = false; s.Activate("retry");
            Until(() => s.State.Connected && s.CanWrite, "reconnect did not restore control");
            Check(s.State.Events.Count(e => e.Key == "后台已连接") == 2, "the reconnect is recorded");
        }
        Console.WriteLine("PASS disconnect disables writes, records events and recovers");
    }
    static void StateAndEvents()
    {
        UiText.Select("en");
        DashboardState state = new DashboardState(); OfflineThermalClient client = new OfflineThermalClient(); DateTime now = DateTime.Now;
        state.Update(client.Sample, now, true);
        Check(state.Events.Count == 1 && state.Events[0].Text == "Service connected", "the first snapshot records the connection");
        client.SetMode("quiet", null); state.Update(client.Sample, now.AddSeconds(2), true);
        Check(state.Events[0].Text == "Mode changed to Quiet", "a mode change is an event, in the current language");
        UiText.Select("zh-TW"); Check(state.Events[0].Text == "模式切換為 靜音", "events are re-localized when the language changes");
        client.Sample.Warnings.Add("等待有效 CPU 温度采样后再应用设置。");
        for (int i = 0; i < 30; i++) { state.Update(client.Sample, now.AddSeconds(3 + i), true); client.SetMode(i % 2 == 0 ? "balanced" : "quiet", null); }
        Check(state.Events.Count == DashboardState.MaxEvents, "only the last 20 events are kept");
        Check(state.Events.Count(e => e.Raw != null) <= 1, "a repeated warning is not repeated every second");
        // trend: 10 minutes, replays ignored, hover lookup
        DateTime t0 = DateTime.Now; DashboardState trend = new DashboardState();
        trend.AddTrend(t0, 2000, 50); trend.AddTrend(t0.AddSeconds(2), 2100, 51);
        for (int i = 0; i < 500; i++) trend.AddTrend(t0.AddSeconds(2), 9999, 99);
        Check(trend.Trend.Count == 2 && trend.Trend[1].Rpm == 2100, "a replayed snapshot is not a new point");
        trend.AddTrend(t0.AddMinutes(11), 3000, 60);
        Check(trend.Trend.Count == 1, "points older than 10 minutes are dropped");
        for (int i = 1; i <= 800; i++) trend.AddTrend(t0.AddMinutes(11).AddMilliseconds(i * 100), i, i);
        Check(trend.Trend.Count <= DashboardState.MaxTrendPoints, "the trend is bounded");
        Check(trend.Nearest(t0.AddMinutes(11).AddMilliseconds(75000)).Rpm == 750, "hover finds the nearest point");
        UiText.Select("zh-CN");
        Console.WriteLine("PASS events (cap 20, localized later), trend (10 min, replay-proof, bounded)");
    }
    static void Pages()
    {
        UseTemporaryRegistry();
        try
        {
            OfflineThermalClient client = new OfflineThermalClient();
            using (ThermalForm form = Open(client))
            {
                DashboardSurface s = form.Surface;
                foreach (string id in new[] { "nav:details", "nav:settings", "nav:home" }) { s.Activate(id); Application.DoEvents(); }
                Check(s.Page == UiPage.Home, "navigation returns home");
                s.Activate("nav:details"); Until(() => s.State.Totals != null, "totals missing");
                Check(s.State.Totals.Count >= 0, "process totals are sampled on the details page");
                s.Activate("nav:settings");
                s.Activate("theme:white"); Check(UiTheme.Current.Id == "white" && UiPreferences.Registry.GetString("Theme") == "white", "a swatch selects and stores the skin");
                s.Activate("lang:en"); Check(UiText.Language == "en" && UiPreferences.Registry.GetString("Language") == "en", "a language button selects and stores the language");
                Check(form.StatusLine.Length > 0, "status text is refreshed for the new language");
                s.Activate("lang:zh-TW"); Check(UiText.Language == "zh-Hant" && UiPreferences.Registry.GetString("Language") == "zh-TW", "traditional Chinese stores zh-TW");
                s.Activate("toggle:deck"); Check(!UiPreferences.DockVisible && !s.DeckOn, "the deck switch writes DockVisible");
                s.Activate("toggle:deck"); Check(UiPreferences.DockVisible && s.DeckOn, "and back");
                s.Activate("toggle:startup"); Application.DoEvents();
                Check(!UiPreferences.StartupEnabled && s.StatusWarn, "without DellFans.Tray.exe the sign-in switch reports an error and stays off");
                s.Activate("nav:home"); s.Activate("langmore"); Check(s.MoreLanguages, "the More toggle flips");
            }
            Console.WriteLine("PASS pages and settings actions use the tray's registry values");
        }
        finally { Restore(); }
    }
    // ---- PawnIO notice, consent and install flow: the real CLI is never started; a fake starter stands in ----
    sealed class FakeStarter : IElevatedStarter
    {
        public readonly List<string[]> Calls = new List<string[]>();
        public int ExitCode; public Exception Throw; public string Json; public ManualResetEvent Gate; public string ResultFileSeen; public bool ResultFileExistedEmpty;
        public int Run(string file, string arguments, string verb)
        {
            Calls.Add(new[] { file, arguments, verb });
            int start = arguments.IndexOf('"') + 1; ResultFileSeen = arguments.Substring(start, arguments.LastIndexOf('"') - start);
            ResultFileExistedEmpty = File.Exists(ResultFileSeen) && new FileInfo(ResultFileSeen).Length == 0;
            if (Gate != null) Gate.WaitOne();
            if (Throw != null) throw Throw;
            if (Json != null) File.WriteAllText(ResultFileSeen, Json);
            return ExitCode;
        }
    }
    static OfflineThermalClient DriverClient(string state, string source)
    { OfflineThermalClient c = new OfflineThermalClient(); c.Sample.SensorDriverState = state; c.Sample.SensorSource = source; return c; }
    static ThermalForm OpenWith(OfflineThermalClient client, FakeStarter starter, bool consentOnStart = false)
    {
        ThermalForm form = new ThermalForm(client) { DriverStarter = starter, StartWithDriverConsent = consentOnStart }; form.Show();
        Until(() => form.Surface.CanWrite, "offline service did not connect");
        form.Refresh(); Application.DoEvents(); return form;
    }
    static bool HasHit(DashboardSurface s, string id) { return s.Hits.Any(h => h.Id == id); }
    static void DriverNotice()
    {
        UseTemporaryRegistry();
        try
        {
            using (ThermalForm form = OpenWith(DriverClient("Missing", "Intel ESIF"), new FakeStarter()))
            {
                DashboardSurface s = form.Surface; DashboardSurface.DriverNotice n = s.Notice;
                Check(n != null && n.Title == "CPU 温度驱动（PawnIO）未安装" && n.Detail == "当前使用 Intel ESIF，精度较低，仍可控速" && n.Buttons, "Missing + ESIF: title, ESIF line, buttons");
                Check(HasHit(s, "driver:install") && HasHit(s, "driver:later"), "the card paints its two buttons");
                UiText.Select("en"); Check(s.Notice.Title == "CPU temperature driver (PawnIO) is not installed" && s.Notice.Detail.StartsWith("Using Intel ESIF"), "English card text");
                UiText.Select("zh-TW"); Check(s.Notice.Title == "CPU 溫度驅動（PawnIO）未安裝", "traditional card text"); UiText.Select("zh-CN");
                s.Activate("driver:later"); Application.DoEvents(); form.Refresh();
                Check(s.Notice == null && !HasHit(s, "driver:install"), "以后再说 hides the card and its buttons");
                Application.DoEvents(); Thread.Sleep(50);
                Check(s.Notice == null, "a dismissed card stays hidden on later polls");
            }
            using (ThermalForm form = OpenWith(DriverClient("HalfUninstalled", "None"), new FakeStarter()))
            {
                DashboardSurface.DriverNotice n = form.Surface.Notice;
                Check(n.Title == "CPU 温度驱动（PawnIO）需要重新安装" && n.Detail == "无法读取 CPU 温度，风扇保持 BIOS 自动" && n.Buttons, "HalfUninstalled + no source: reinstall title and BIOS-automatic line");
            }
            using (ThermalForm form = OpenWith(DriverClient("NeedsRestart", "None"), new FakeStarter()))
            {
                DashboardSurface s = form.Surface; DashboardSurface.DriverNotice n = s.Notice;
                Check(n != null && n.Title == "驱动已安装，重启电脑后生效" && !n.Buttons && !HasHit(s, "driver:install") && !HasHit(s, "driver:later"), "NeedsRestart: restart card without buttons");
            }
            using (ThermalForm form = OpenWith(DriverClient("Installed", "PawnIO IntelMSR"), new FakeStarter()))
                Check(form.Surface.Notice == null && !HasHit(form.Surface, "driver:install"), "Installed: no card");
            using (ThermalForm form = OpenWith(new OfflineThermalClient(), new FakeStarter()))
                Check(form.Surface.Notice == null, "a service that reports no driver state: no card");
            // the state may also arrive on Background only
            OfflineThermalClient background = new OfflineThermalClient(); background.Sample.Background.SensorDriverState = "Missing"; background.Sample.Background.SensorSource = "Intel ESIF";
            using (ThermalForm form = OpenWith(background, new FakeStarter())) Check(form.Surface.Notice != null && form.Surface.Notice.Buttons, "Background.SensorDriverState is used too");
            Console.WriteLine("PASS driver notice: Missing / HalfUninstalled / NeedsRestart / Installed, ESIF and no-source lines, dismiss, three languages");
        }
        finally { Restore(); }
    }
    static void DriverConsent()
    {
        UseTemporaryRegistry();
        try
        {
            string text = UiDialogs.DriverConsentMessage();
            foreach (string part in new[] { "PawnIO 2.2.0", "namazso", SensorDriverInstaller.Url, "SHA-256", "Authenticode", "CN=namazso.eu", "GPL-2.0", "不随 DellFans 分发", "管理员", "应用和功能" })
                Check(text.Contains(part), "consent text mentions " + part);
            using (Form dialog = UiDialogs.CreateDriverConsent())
            {
                Check(dialog.Controls.Find("DialogMessage", true)[0].Text == text, "the dialog shows the consent text");
                Check(dialog.Controls.Find("DriverInstallButton", true)[0].Text == "安装" && dialog.Controls.Find("CancelAutomaticButton", true)[0].Text == "取消", "安装 / 取消 buttons");
                Check(dialog.CancelButton == dialog.Controls.Find("CancelAutomaticButton", true)[0] && dialog.AcceptButton != dialog.Controls.Find("DriverInstallButton", true)[0], "Enter and Escape cancel; installing needs a deliberate click");
            }
            // cancel: nothing starts
            int asked = 0; UiDialogs.DriverConsentOverride = delegate { asked++; return false; };
            FakeStarter starter = new FakeStarter();
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Application.DoEvents();
                Check(asked == 1 && starter.Calls.Count == 0 && !form.Surface.DriverInstalling && form.Surface.Notice.Buttons, "取消 starts nothing and keeps the card");
            }
            // the tray's --sensor-driver opens the consent dialog by itself
            asked = 0;
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), new FakeStarter(), true)) { Until(() => asked == 1, "the consent dialog was not shown"); Thread.Sleep(100); Application.DoEvents(); Check(asked == 1, "StartWithDriverConsent shows the consent dialog once"); }
            asked = 0;
            using (ThermalForm form = OpenWith(DriverClient("Installed", "PawnIO IntelMSR"), new FakeStarter(), true)) { Thread.Sleep(200); Application.DoEvents(); Check(asked == 0, "no consent dialog when the driver is already fine"); }
            Console.WriteLine("PASS consent dialog content, safe default buttons, cancel starts nothing, tray start opens it");
        }
        finally { Restore(); }
    }
    static OfflineThermalClient CapabilityClient(string state)
    {
        OfflineThermalClient client = new OfflineThermalClient();
        client.Sample.Background.Capability = new CapabilityStatus { State = state, Model = "Test Dell", BiosVersion = "1.0.0",
            Checks = new List<CapabilityCheck> { new CapabilityCheck { Name = "SMM fan RPM read", Result = state == "Failed" ? "Fail" : "Pass", Error = state == "Failed" ? "no endpoint" : null } } };
        return client;
    }
    static void CapabilityFirstOpen()
    {
        UseTemporaryRegistry();
        string root = Path.Combine(Path.GetTempPath(), "DellFans-CapabilityUi-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Read checks passed: the window asks once; declining runs nothing.
            int asked = 0; UiDialogs.CapabilityConsentOverride = delegate { asked++; return false; };
            OfflineThermalClient client = CapabilityClient("ReadPassed");
            using (ThermalForm form = Open(client))
            {
                Until(() => form.CapabilityTask != null && form.CapabilityTask.IsCompleted, "the consent was not asked");
                for (int i = 0; i < 3; i++) { form.Surface.Activate("nav:home"); Application.DoEvents(); Thread.Sleep(30); }
                Check(asked == 1 && client.ProbeCount == 0 && form.StatusLine.StartsWith("尚未检测风扇控制接口"), "declined: asked once, nothing tested, status says so");
            }
            // Consent: the full-speed test runs through the service and a pass is reported.
            UiDialogs.CapabilityConsentOverride = delegate { return true; };
            client = CapabilityClient("ReadPassed"); client.ProbeResult = new CapabilityStatus { State = "Passed", Model = "Test Dell", BiosVersion = "1.0.0" };
            using (ThermalForm form = Open(client))
            {
                Until(() => form.CapabilityTask != null && form.CapabilityTask.IsCompleted, "the test did not finish");
                Check(client.ProbeCount == 1 && form.StatusLine == "检测通过：这台电脑支持 DellFans 风扇控制。" && !form.IsBusy, "consented: one test, pass reported");
            }
            // Failed checks: an anonymous report in the user's folder and the unsupported dialog, once per model and BIOS.
            List<string> shown = new List<string>(); UiDialogs.UnsupportedOverride = delegate (IWin32Window owner, string path) { shown.Add(path); return false; };
            client = CapabilityClient("Failed");
            using (ThermalForm form = new ThermalForm(client) { CapabilityRoot = root }) { form.Show(); Until(() => shown.Count == 1, "the unsupported dialog was not shown"); }
            Check(shown[0] != null && File.Exists(shown[0]) && Path.GetDirectoryName(shown[0]) == Path.Combine(root, "reports"), "the report is written to the user's folder");
            string report = File.ReadAllText(shown[0]);
            Check(report.Contains("no endpoint") && report.Contains("Test Dell") && !report.Contains(Environment.MachineName) && !report.Contains(Environment.UserName), "the report carries the checks and no machine or user name");
            using (ThermalForm form = new ThermalForm(CapabilityClient("Failed")) { CapabilityRoot = root })
            {
                form.Show(); Until(() => form.Surface.CanWrite, "no connection");
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
                Check(shown.Count == 1 && form.StatusLine == "当前电脑不支持 · 风扇保持 BIOS 自动", "not shown again for the same model and BIOS; the status line says it");
            }
            // A test the user ran that fails always says so.
            client = CapabilityClient("ReadPassed"); client.ProbeResult = new CapabilityStatus { State = "Failed", Model = "Test Dell", BiosVersion = "1.0.0", Checks = new List<CapabilityCheck>() };
            using (ThermalForm form = new ThermalForm(client) { CapabilityRoot = root })
            { form.Show(); Until(() => form.CapabilityTask != null && form.CapabilityTask.IsCompleted, "no test"); Check(client.ProbeCount == 1 && shown.Count == 2, "a failed test shows the dialog"); }
            Console.WriteLine("PASS first open: consent before the full-speed test, pass reported, unsupported dialog with an anonymous report, shown once");
        }
        finally { Restore(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    static void DriverInstall()
    {
        UseTemporaryRegistry();
        try
        {
            UiDialogs.DriverConsentOverride = delegate { return true; };
            // success, with the UI staying responsive while the elevated CLI runs
            FakeStarter starter = new FakeStarter { Gate = new ManualResetEvent(false) };
            OfflineThermalClient client = DriverClient("Missing", "Intel ESIF");
            using (ThermalForm form = OpenWith(client, starter))
            {
                DashboardSurface s = form.Surface; int polls = client.PollCount;
                s.Activate("driver:install");
                Until(() => starter.Calls.Count == 1, "the starter was not called");
                Check(s.DriverInstalling && s.Notice.Title == "正在安装…" && !s.Notice.Buttons && form.StatusLine == "正在安装…", "while installing: 正在安装… and no buttons");
                Application.DoEvents(); s.Activate("nav:details"); Application.DoEvents(); s.Activate("nav:home");
                Check(s.Page == UiPage.Home && !form.IsBusy, "the window stays responsive while the installer runs");
                string[] call = starter.Calls[0];
                Check(call[0] == form.CliPath && Path.GetFileName(call[0]) == "DellFans.Cli.exe", "the CLI next to the window is started");
                Check(call[1] == "--install-sensor-driver --result-file \"" + starter.ResultFileSeen + "\"" && call[2] == "runas", "exactly --install-sensor-driver --result-file <tmp>, verb runas");
                Check(starter.ResultFileExistedEmpty && Path.GetDirectoryName(starter.ResultFileSeen).TrimEnd('\\').Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "the GUI creates the empty result file in %TEMP%");
                starter.Gate.Set(); Until(() => form.DriverTask.IsCompleted, "install flow did not finish");
                Check(s.Notice == null && !s.DriverInstalling && form.StatusLine == "驱动已安装，CPU 温度将在几秒内恢复读取。", "exit 0: card gone, success message");
                Check(client.PollCount > polls && !File.Exists(starter.ResultFileSeen), "the snapshot is refreshed and the temp file removed");
            }
            // restart required (3010)
            starter = new FakeStarter { ExitCode = 3010 };
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(form.Surface.Notice.Title == "驱动已安装，重启电脑后生效" && !form.Surface.Notice.Buttons && form.StatusLine == "驱动已安装，重启电脑后生效。", "exit 3010: restart card and message");
            }
            // failure with the CLI's own message
            starter = new FakeStarter { ExitCode = 1, Json = "{\"Outcome\":\"Failed\",\"Message\":\"PawnIO installer SHA-256 mismatch; nothing was run.\",\"ExitCode\":0}" };
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(form.StatusLine == "安装失败：PawnIO installer SHA-256 mismatch; nothing was run." && form.Surface.StatusWarn && form.Surface.Notice.Buttons, "exit 1: the CLI message is shown, the card stays so the user can retry");
                Check(!File.Exists(starter.ResultFileSeen), "temp file removed after a failure");
            }
            // failure without a message
            starter = new FakeStarter { ExitCode = 5 };
            using (ThermalForm form = OpenWith(DriverClient("HalfUninstalled", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(form.StatusLine == "安装失败（退出码 5）。", "no result file: the exit code is shown");
            }
            // UAC declined (ERROR_CANCELLED 1223)
            starter = new FakeStarter { Throw = new System.ComponentModel.Win32Exception(1223) };
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(form.StatusLine == "已取消：未授予管理员权限，没有安装任何内容。" && !form.Surface.StatusWarn && form.Surface.Notice.Buttons, "1223: cancelled at UAC, card stays");
            }
            // any other launch error
            starter = new FakeStarter { Throw = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified") };
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(form.StatusLine == "安装失败：The system cannot find the file specified" && form.Surface.StatusWarn, "a launch error is a failure with its message");
            }
            // a second click while installing starts nothing more
            starter = new FakeStarter { Gate = new ManualResetEvent(false) };
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => starter.Calls.Count == 1, "not started");
                form.Surface.Activate("driver:install"); Application.DoEvents();
                starter.Gate.Set(); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(starter.Calls.Count == 1, "only one installer run at a time");
            }
            // English result text
            UiText.Select("en");
            starter = new FakeStarter { Throw = new System.ComponentModel.Win32Exception(1223) };
            using (ThermalForm form = OpenWith(DriverClient("Missing", "None"), starter))
            {
                form.Surface.Activate("driver:install"); Until(() => form.DriverTask.IsCompleted, "no result");
                Check(form.StatusLine.StartsWith("Cancelled:"), "English result text");
            }
            Console.WriteLine("PASS driver install: fake starter gets --install-sensor-driver --result-file <tmp> with runas; 0, 3010, 1 (with/without message), 1223 and launch errors map to the right text; UI stays responsive");
        }
        finally { Restore(); }
    }
    // Render every page in all skins and languages at two scales: controls stay inside the window, never overlap, and the page height is bounded.
    static void LayoutIsSound()
    {
        int cases = 0;
        foreach (float scale in new[] { 1f, 1.5f })
            foreach (string theme in PreviewRender.Themes)
                foreach (string language in PreviewRender.Languages)
                    foreach (UiPage page in new[] { UiPage.Home, UiPage.Details, UiPage.Settings })
                        foreach (bool custom in new[] { false, true })
                        {
                            if (custom && page != UiPage.Home) continue;
                            UiTheme.Select(theme); UiText.Select(language);
                            using (DashboardSurface s = new DashboardSurface { Page = page, CanWrite = true, PreviewMode = true })
                            {
                                s.State.Update(PreviewRender.Fixture(DateTime.UtcNow), DateTime.Now, true); PreviewRender.Decorate(s);
                                s.SelectedMode = custom ? "custom" : s.State.Mode; s.CustomPending = custom; s.DraftText = "2800";
                                if (custom) { s.State.SensorDriverState = "Missing"; s.State.SensorSource = "Intel ESIF"; } // home with the PawnIO card
                                float width = DashboardSurface.MinimumWidth;
                                float height = s.MeasureContent(width, scale);
                                Check(height > 200 && height < 1400, "page height out of bounds: " + page + " " + height);
                                using (Bitmap bitmap = new Bitmap((int)(width * scale), (int)(900 * scale)))
                                using (Graphics g = Graphics.FromImage(bitmap)) s.Render(g, new SizeF(width, 900), scale);
                                RectangleF bounds = new RectangleF(0, 0, width, 100000);
                                List<UiHit> hits = s.Hits.ToList();
                                foreach (UiHit h in hits)
                                {
                                    Check(h.Rect.Left >= 0 && h.Rect.Right <= width && h.Rect.Width >= 24 && h.Rect.Height >= 28, "control outside the window or too small: " + h.Id + " " + h.Rect);
                                    foreach (UiHit o in hits) if (o != h && o.Fixed == h.Fixed && h.Rect.IntersectsWith(RectangleF.Inflate(o.Rect, -0.5f, -0.5f)) && !h.Id.StartsWith("toggle:") && !o.Id.StartsWith("toggle:"))
                                        throw new Exception("controls overlap: " + h.Id + " " + h.Rect + " / " + o.Id + " " + o.Rect + " in " + page + " " + theme + " " + language);
                                }
                                cases++;
                            }
                        }
        UiTheme.Select("black"); UiText.Select("zh-CN");
        Console.WriteLine("PASS " + cases + " rendered layouts (page x skin x language x dpi) keep controls inside, apart and big enough");
    }
    static void HitTesting()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = Open(client))
        {
            DashboardSurface s = form.Surface; form.Refresh(); Application.DoEvents();
            UiHit quiet = s.Hits.First(h => h.Id == "mode:quiet");
            float scale = Math.Max(0.75f, form.DeviceDpi / 96f);
            Point centre = new Point((int)((quiet.Rect.X + quiet.Rect.Width / 2 + UiMetrics.Margin) * scale), (int)((quiet.Rect.Y + quiet.Rect.Height / 2 + DashboardSurface.HeaderHeight) * scale));
            Check(s.HitIdAt(centre) == "mode:quiet", "the centre of a segment hits that segment");
            Check(s.HitIdAt(new Point(centre.X, 2)) == null, "the title area hits nothing");
            Check(s.HitIdAt(new Point((int)(40 * scale), (int)((DashboardSurface.HeaderHeight + 4) * scale))) == null, "a card title hits nothing");
        }
        Console.WriteLine("PASS hit testing maps pixels to controls through the scale and header offset");
    }
    // Every literal passed to UiText.T / UiText.F in the sources exists in the message table, and the shipped English template is current.
    static void EveryMessageIsTranslated(string root)
    {
        string template = UiText.ExportTemplate("en", "English translation template");
        string shipped = File.ReadAllText(Path.Combine(root, "translations", "en.template.json")).Replace("\r\n", "\n");
        Check(shipped == template.Replace("\r\n", "\n"), "translations\\en.template.json is out of date; regenerate it with UiText.ExportTemplate");
        System.Web.Script.Serialization.JavaScriptSerializer json = new System.Web.Script.Serialization.JavaScriptSerializer();
        int found = 0;
        foreach (string file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs"))
        {
            if (Path.GetFileName(file) == "Localization.cs") continue;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "UiText\\.(?:T|F)\\(\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string key = json.Deserialize<string>("\"" + m.Groups[1].Value + "\"");
                Check(template.Contains(json.Serialize(key) + ":"), "UiText key is missing from the message table: " + key + " (" + Path.GetFileName(file) + ")"); found++;
            }
        }
        foreach (string language in new[] { "en", "zh-TW" })
        {
            UiText.Select(language);
            foreach (string key in new[] { "主页", "详情", "设置", "外观", "语言", "显示 deck", "登录后启动", "DellFans 与 Dell Inc. 无关；Dell 是其所有者的商标。", "最近 10 分钟", "硬盘", "风扇" })
                Check(UiText.T(key) != key, language + " translation missing for " + key);
        }
        Check(UiText.T("DellFans 与 Dell Inc. 无关；Dell 是其所有者的商标。") == "DellFans 與 Dell Inc. 無關；Dell 是其所有者的商標。" || UiText.Language == "en", "zh-TW disclaimer");
        UiText.Select("en"); Check(UiText.T("DellFans 与 Dell Inc. 无关；Dell 是其所有者的商标。") == "DellFans is not affiliated with Dell Inc. Dell is a trademark of its owner.", "English disclaimer");
        UiText.Select("zh-CN");
        Console.WriteLine("PASS " + found + " UiText literals are all in the message table; the English template is current; the disclaimer is translated");
    }
    [STAThread]
    static int Main(string[] args)
    {
        UseTemporaryRegistry();
        try { if (args.Length > 0) EveryMessageIsTranslated(args[0]); ModesApplyImmediately(); CustomMinimum(); Disconnected(); StateAndEvents(); DriverNotice(); DriverConsent(); DriverInstall(); CapabilityFirstOpen(); Pages(); LayoutIsSound(); HitTesting(); Console.WriteLine("RESULT " + passed + " checks passed; offline client, temporary HKCU subkeys"); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally { Restore(); }
    }
}

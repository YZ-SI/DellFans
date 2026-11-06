using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TimeoutException = System.TimeoutException;

namespace DellFans
{
    public sealed class ServiceRequest
    {
        public string Command { get; set; }
        public int? MinimumRpm { get; set; }
        // SetMode only: quiet, balanced, performance, custom or auto. MinimumRpm accompanies custom.
        public string Mode { get; set; }
        // Status only: false returns the summary rows; absent or true returns every row.
        public bool? Detail { get; set; }
        // RunCapabilityProbe only: must be true. The probe runs the fan at full speed for 15 s.
        public bool? Consent { get; set; }
    }
    public sealed class ServiceReply
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public Snapshot Snapshot { get; set; }
    }
    public static class PipeProtocol
    {
        public static void Write(Stream stream, object value)
        {
            byte[] data = Encoding.UTF8.GetBytes(ReportFiles.Json(value));
            if (data.Length > 4 * 1024 * 1024) throw new InvalidDataException("IPC response exceeds its size limit.");
            byte[] prefix = BitConverter.GetBytes(data.Length);
            stream.Write(prefix, 0, prefix.Length); stream.Write(data, 0, data.Length); stream.Flush();
        }
        // The deadline covers the whole frame, so a client that trickles bytes cannot hold a pipe instance.
        static byte[] ReadBytes(Stream stream, int count, Stopwatch clock, int timeoutMilliseconds)
        {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count)
            {
                Task<int> read = stream.ReadAsync(bytes, offset, count - offset);
                long left = timeoutMilliseconds - clock.ElapsedMilliseconds;
                if (left <= 0 || !read.Wait((int)left)) throw new TimeoutException("Background service response timed out.");
                if (read.Result == 0) throw new EndOfStreamException("IPC connection closed."); offset += read.Result;
            }
            return bytes;
        }
        public static T Read<T>(Stream stream, int maximumBytes)
        { return Read<T>(stream, maximumBytes, 35000); }
        public static T Read<T>(Stream stream, int maximumBytes, int totalTimeoutMilliseconds)
        { List<string> ignored; return Read<T>(stream, maximumBytes, totalTimeoutMilliseconds, out ignored); }
        // `unknown` lists JSON keys the type has no member for; the server refuses them on write commands.
        public static T Read<T>(Stream stream, int maximumBytes, int totalTimeoutMilliseconds, out List<string> unknown)
        {
            Stopwatch clock = Stopwatch.StartNew();
            int length = BitConverter.ToInt32(ReadBytes(stream, 4, clock, totalTimeoutMilliseconds), 0);
            if (length < 2 || length > maximumBytes) throw new InvalidDataException("Invalid IPC frame length.");
            return MiniJson.Deserialize<T>(Encoding.UTF8.GetString(ReadBytes(stream, length, clock, totalTimeoutMilliseconds)), maximumBytes, out unknown);
        }
    }

    public interface IThermalClient : IDisposable
    {
        Snapshot Poll();
        void SetTargetRpm(int rpm);
        void SetAutomatic();
        void SetFullPersistent();
        void SetMode(string mode, int? minimumRpm);
        Snapshot BeginProbe();
        // The consented 15 s full-speed capability test; returns the snapshot taken after it (Background.Capability has the result).
        Snapshot RunCapabilityProbe();
    }

    public sealed class ThermalClient : IThermalClient
    {
        public bool Detailed { get; set; }
        // Never starts the controller itself (the window does that through BackgroundHost, with UAC). A controller that is not running
        // surfaces as ServiceNotRunningException; requests go only to the elevated DellFans.Service.exe next to this program.
        public static Snapshot Send(ServiceRequest request)
        { return ControlPipeClient.SendToHost(ServicePaths.PipeName, request); }
        public Snapshot Poll()
        {
            Snapshot sample = Send(new ServiceRequest { Command = "Status", Detail = Detailed });
            if (sample == null) throw new InvalidOperationException("后台服务正在初始化传感器，请稍候…");
            return sample;
        }
        public void SetMode(string mode, int? minimumRpm)
        { Send(new ServiceRequest { Command = "SetMode", Mode = mode, MinimumRpm = minimumRpm }); }
        // Minimum RPM = the custom mode.
        public void SetTargetRpm(int rpm) { SetMode(FanModes.Custom, rpm); }
        public void SetAutomatic() { SetMode(FanModes.Auto, null); }
        public void SetFullPersistent() { SetTargetRpm(RpmRange.Maximum); }
        public Snapshot BeginProbe() { return Send(new ServiceRequest { Command = "Probe" }); }
        public Snapshot RunCapabilityProbe() { return Send(new ServiceRequest { Command = "RunCapabilityProbe", Consent = true }); }
        public void Dispose() { } // Closing a client never stops the service or changes fan settings.
    }

    public sealed class SnapshotDiagnostics
    {
        readonly string path;
        double lastAttempt = -100;
        string lastError;
        public SnapshotDiagnostics(string path) { this.path = path; }
        public void Write(Snapshot sample, double elapsedSeconds, bool force)
        {
            // A configuration change may force a healthy report, never a failed IO retry.
            if (elapsedSeconds - lastAttempt >= 60 || (force && lastError == null))
            {
                lastAttempt = elapsedSeconds;
                try { ReportFiles.Write(path, sample); lastError = null; }
                catch (IOException error) { lastError = "Snapshot report: " + error.Message; }
                catch (UnauthorizedAccessException error) { lastError = "Snapshot report: " + error.Message; }
                catch (System.Security.SecurityException error) { lastError = "Snapshot report: " + error.Message; }
            }
            if (lastError != null && !sample.Warnings.Contains(lastError)) sample.Warnings.Add(lastError);
        }
    }

    public sealed class ThermalServiceRuntime : IDisposable
    {
        sealed class Pending
        {
            public ServiceRequest Request;
            public ServiceReply Reply;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
            public bool Started, Abandoned;
        }
        readonly ConcurrentQueue<Pending> commands = new ConcurrentQueue<Pending>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly ModeController controller = new ModeController();
        readonly TemperatureRecovery tempRecovery = new TemperatureRecovery();
        readonly AutomaticRelease release = new AutomaticRelease();
        readonly PublishSchedule schedule = new PublishSchedule();
        readonly CapabilityStore capabilityStore = new CapabilityStore(ServicePaths.Root);
        CapabilityStatus capability;
        string capabilityKey;
        internal int SnapshotRebuilds { get { return schedule.Rebuilds; } }
        readonly object snapshotGate = new object();
        readonly ManualResetEventSlim suspendReleased = new ManualResetEventSlim();
        readonly SnapshotDiagnostics diagnostics = new SnapshotDiagnostics(Path.Combine(ServicePaths.Reports, "live-latest.json"));
        Thread worker;
        ControlPipeServer pipeServer;
        volatile bool suspended, resume;
        double clientSeen, probeUntil;
        bool snapshotDirty = true;
        bool ownsControl;
        bool faultLatched;
        string faultReason, startupNotice;
        string state = "Starting", message, reportedState;
        FloorSettings settings;
        Snapshot latest;
        ThermalEngine engine;
        double lastInitialization;
        int processId;
        public event Action Failed;
        // Raised (after the reply went out) when a client asked the controller to hand the fan back and exit.
        public event Action ExitRequested;
        public void Start()
        {
            ReportFiles.ServiceMode = true; Directory.CreateDirectory(ServicePaths.Reports);
            using (Process process = Process.GetCurrentProcess()) processId = process.Id;
            // The control loop must keep its 1 s cadence under an all-core user load (seen on the real machine); it is light, so this costs nothing.
            try { using (Process self = Process.GetCurrentProcess()) self.PriorityClass = ProcessPriorityClass.AboveNormal; } catch (Exception) { }
            worker = new Thread(Loop) { IsBackground = true, Name = "DellFans service", Priority = ThreadPriority.AboveNormal };
            pipeServer = new ControlPipeServer(ServicePaths.PipeName, Handle) { OnError = ReportPipeError };
            worker.Start(); pipeServer.StartResilient();
        }
        // on Suspend the worker gets up to waitMilliseconds to hand the fan back to BIOS before the caller (the power broadcast) returns.
        public bool Power(bool sleeping, int waitMilliseconds = 0)
        {
            if (sleeping) suspendReleased.Reset();
            suspended = sleeping; if (!sleeping) resume = true; wake.Set();
            return !sleeping || waitMilliseconds <= 0 || suspendReleased.Wait(waitMilliseconds);
        }
        internal static Snapshot Reply(Snapshot sample, bool? detail) { return detail == false && sample != null ? sample.Summary() : sample; }
        ServiceReply Status(bool? detail)
        {
            clientSeen = clock.Elapsed.TotalSeconds; wake.Set();
            Snapshot sample; lock (snapshotGate) sample = latest;
            return new ServiceReply { Success = true, Snapshot = Reply(sample, detail) };
        }
        void Publish(Snapshot sample, double? cpu, int? target)
        {
            engine.UpdateControlSnapshot(sample);
            sample.Background = new BackgroundStatus { State = state, Message = message, Settings = settings.Copy(),
                Mode = settings.Mode, ModeLimits = settings.Limits(),
                EffectiveTargetRpm = target, ControlTemperatureC = cpu, ProbeActive = probeUntil > 0,
                ProcessId = processId, SensorSource = sample.SensorSource, SensorDriverState = sample.SensorDriverState,
                Capability = capability ?? new CapabilityStatus { State = CapabilityStates.Unknown, Model = sample.Model, BiosVersion = sample.BiosVersion } };
            if (settings.LastFault != null) sample.Warnings.Add(settings.LastFault);
            if (faultReason != null) sample.Warnings.Add(faultReason);
            if (startupNotice != null) sample.Warnings.Add(startupNotice);
            diagnostics.Write(sample, clock.Elapsed.TotalSeconds, snapshotDirty || state != reportedState);
            snapshotDirty = false; reportedState = state; schedule.Built(clock.Elapsed.TotalSeconds);
            lock (snapshotGate) latest = sample;
        }
        // Only the sensor layer's 1 s mean controls the fan; a missing value means no control (BIOS stays in charge), never a row-maximum guess.
        internal static double? Cpu(Snapshot sample)
        {
            double? value = sample.CpuControlTemperatureC;
            return value.HasValue && value.Value > 0 && value.Value <= 125 ? value : null;
        }
        // The fan speed measured within the last 3 s, or null; a takeover starts there instead of at the mode minimum.
        internal static int? MeasuredRpm(Snapshot sample, DateTime nowUtc)
        {
            if (sample == null || sample.Sensors == null) return null;
            foreach (SensorRow row in sample.Sensors)
            {
                DateTime at;
                if (row.Type == "Fan" && row.Value.HasValue && row.Value.Value >= 100 && row.PolledAtUtc != null &&
                    DateTime.TryParse(row.PolledAtUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out at) &&
                    Math.Abs((nowUtc - at.ToUniversalTime()).TotalSeconds) <= 3)
                    return (int)Math.Round(row.Value.Value);
            }
            return null;
        }
        // How to enter full speed at 90 C. A running manual controller reaches full within ~120 ms; SetFullPersistent restores BIOS first
        // (a 2 s mode read, NVRAM writes) and so only serves when manual control is not running. Null = already in emergency and owned.
        internal static string EmergencyEntry(bool ownsControl, string state, bool manualActive)
        { return ownsControl && state == "EmergencyFullSpeed" ? null : manualActive ? "SetTarget" : "SetFullPersistent"; }
        internal static bool AutoNeedsRelease(bool wasAuto, bool ownsControl, bool probeActive) { return ownsControl || probeActive || !wasAuto; }
        bool CapabilityPassed { get { return capability != null && capability.State == CapabilityStates.Passed; } }
        // Null when the fan may be driven; otherwise why not. Every command that writes the fan (modes, the 15 s Probe) uses it.
        internal static string CapabilityRefusal(CapabilityStatus status)
        {
            return status != null && status.State == CapabilityStates.Passed ? null :
                "这台电脑尚未通过风扇控制接口检测，不能接管风扇（状态 " + (status == null ? CapabilityStates.Unknown : status.State) + "）；打开 DellFans 窗口即可开始检测。";
        }
        void RequireCapability(Snapshot sample)
        {
            EnsureCapability(sample);
            string refusal = CapabilityRefusal(capability);
            if (refusal != null) throw new NotSupportedException(refusal);
        }
        // Read-only capability checks, once per Model + BIOS (a BIOS change is a new key and is probed again). Hardware that reads
        // clean waits as ReadPassed for the user's consent to the full-speed test.
        void EnsureCapability(Snapshot sample)
        {
            if (sample.Model == null) return;
            string key = sample.Model + "|" + sample.BiosVersion;
            if (key == capabilityKey) return;
            if (capabilityStore.Allows(sample.Model, sample.BiosVersion) && sample.ManualControl != null && sample.ManualControl.State == "Unavailable") return; // interface still starting
            capabilityKey = key;
            try { capability = new CapabilityProbe(new LiveCapabilityHardware(engine), capabilityStore, null).ReadChecks(); }
            catch (Exception e) { capability = new CapabilityStatus { State = CapabilityStates.Failed, Model = sample.Model, BiosVersion = sample.BiosVersion, Checks = new List<CapabilityCheck> { new CapabilityCheck { Name = "Capability probe", Result = "Fail", Error = capabilityStore.Redact(e.Message) } } }; }
        }
        // Hands the fan back to BIOS. A failed release keeps ownsControl and is retried on every tick (see Tick).
        bool ReleaseFan()
        {
            release.Request();
            bool released = release.Attempt(delegate { engine.SetAutomatic(); });
            if (released) { ownsControl = false; if (startupNotice != null) startupNotice = "启动时遗留的 BIOS 全速覆盖已恢复为 BIOS 自动。"; }
            return released;
        }
        // a full-speed write left behind by a crashed or killed service (its marker file) goes back to BIOS automatic once.
        void RestoreStaleFullOverride()
        {
            try
            {
                if (!FullOverrideMarker.RestoreStale(new FullOverrideMarker(), delegate { engine.SetAutomatic(); })) return;
                startupNotice = "检测到上次遗留的 BIOS 全速覆盖，已在服务启动时恢复 BIOS 自动。";
                ReportFiles.Write(Path.Combine(ServicePaths.Reports, "full-override-restored.json"), new { AtUtc = DateTime.UtcNow.ToString("o"), Reason = "marker" });
            }
            catch (Exception e)
            {
                startupNotice = "检测到遗留的 BIOS 全速覆盖但尚未确认恢复，将继续重试：" + e.Message;
                release.Request(); // Tick retries until the restore is read back; the marker stays until then
            }
        }
        void PauseForFault(string reason)
        {
            faultLatched = true; faultReason = reason;
            probeUntil = 0; controller.Reset();
            ReleaseFan();
            state = "FaultAutomatic"; message = "已临时交回 BIOS 自动；配置已保留，重启服务或重新应用模式可恢复。";
        }
        // A lost temperature is not a firmware fault: BIOS takes over now and control resumes after 10 s of valid readings.
        void HandBackForTemperature()
        {
            tempRecovery.HandBack(); probeUntil = 0; controller.Reset(); ReleaseFan();
            state = "WaitingForTemperature"; message = "CPU 温度读取失效，已临时恢复 BIOS 自动；有效读数持续 10 秒后自动恢复接管。";
        }
        void Tick()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (release.Pending)
            {
                ReleaseFan();
                if (release.Failing) { faultLatched = true; faultReason = "无法恢复 BIOS 自动，将继续重试：" + release.Error; }
            }
            // While the service owns the fan the controller is fed every second, but the full snapshot is rebuilt every 2 s
            // (every second while a client is looking).
            bool viewing = now - clientSeen < 10;
            bool light = ownsControl && !faultLatched && probeUntil == 0 && latest != null && !snapshotDirty && settings.Limits() != null && !schedule.Due(now, viewing, false);
            Snapshot sample = light ? engine.ControlSample() : engine.Poll(); double? cpu = Cpu(sample); int? target = null;
            tempRecovery.Observe(cpu.HasValue, now);
            if (!light && !ownsControl) EnsureCapability(sample);
            ModeLimits limits = settings.Limits(); // null in auto: the service never takes over
            if (faultLatched)
            { state = "FaultAutomatic"; Publish(sample, cpu, null); return; }
            bool expectedHardware = sample.Model != null && CapabilityPassed;
            bool interfacePending = sample.Model == null || (expectedHardware && sample.ManualControl != null && sample.ManualControl.State == "Unavailable");
            if (!ownsControl && (interfacePending || !cpu.HasValue) && now - lastInitialization >= 30)
            {
                engine.Dispose(); engine = new ThermalEngine(true); lastInitialization = now;
                sample = engine.Poll(); cpu = Cpu(sample);
                expectedHardware = sample.Model != null && CapabilityPassed;
                interfacePending = sample.Model == null || (expectedHardware && sample.ManualControl != null && sample.ManualControl.State == "Unavailable");
            }
            if (sample.Resources != null && sample.Resources.RestoreAutomaticRequested && (settings.Enabled || ownsControl))
                PauseForFault("后台服务资源持续超限，已临时恢复 BIOS 自动，保存的模式未修改。");
            else if (ownsControl && !cpu.HasValue && limits != null)
                HandBackForTemperature();
            else if (ownsControl && sample.ManualControl != null && FanSafetyPolicy.IsTransientControllerError(sample.ManualControl.Error))
            {
                tempRecovery.HandBack(); probeUntil = 0; controller.Reset(); ReleaseFan();
                state = "WaitingForTemperature"; message = "控速线程被系统延迟超过 3 秒（如 CPU 满载），已临时交回 BIOS 自动；10 秒后自动恢复接管。";
            }
            else if (ownsControl && sample.ManualControl != null && sample.ManualControl.Error != null && sample.ManualControl.State != "ProtectionFullSpeed")
                PauseForFault("转速控制异常，已临时恢复 BIOS 自动：" + sample.ManualControl.Error);
            else if (probeUntil > 0)
            {
                if (now >= probeUntil)
                { probeUntil = 0; engine.Restore(); ownsControl = false; controller.Reset(); }
                else { state = "Probing"; message = "短时探测完成后自动恢复原配置。"; target = RpmRange.Maximum; }
            }
            if (limits == null && ownsControl && probeUntil == 0 && !faultLatched) ReleaseFan(); // auto must not leave the fan owned
            if (probeUntil == 0 && !faultLatched)
            {
                if (limits == null)
                { state = settings.LastFault == null ? "BiosAutomatic" : "FaultAutomatic"; if (settings.LastFault == null) message = "BIOS 自动模式，沿用 BIOS 风扇控制。"; }
                else if (!CapabilityPassed)
                { state = "CapabilityRequired"; message = "这台电脑尚未通过风扇控制接口检测，风扇保持 BIOS 自动；打开 DellFans 窗口即可开始检测。"; }
                else if (!cpu.HasValue)
                { state = "WaitingForTemperature"; message = "等待有效 CPU 温度；暂不接管 BIOS。"; }
                else if (!ownsControl && !tempRecovery.MayResume(now))
                { state = "WaitingForTemperature"; message = "CPU 温度刚恢复，有效读数持续 10 秒后再接管 BIOS。"; }
                else if (interfacePending)
                { state = "WaitingForInterface"; message = "等待本机控制接口就绪；暂不接管 BIOS。"; }
                else if (sample.ManualControl == null || sample.ManualControl.State == "Unavailable")
                    PauseForFault("当前机型或 BIOS 的手动控制接口未通过验证，保存的配置未修改。");
                else
                {
                    // Control follows the 1 s mean; protection uses the raw 1 s maximum. A takeover starts at the measured fan speed (full if already hot).
                    if (!ownsControl) controller.Seed(limits, MeasuredRpm(sample, DateTime.UtcNow), cpu.Value);
                    tempRecovery.Clear();
                    target = controller.Update(limits, cpu.Value, ModeController.Hottest(cpu.Value, sample.CpuProtectionTemperatureC), now);
                    if (controller.Emergency)
                    {
                        string entry = EmergencyEntry(ownsControl, state, sample.ManualControl.Active);
                        if (entry == "SetTarget") engine.SetTargetRpm(RpmRange.Maximum); else if (entry == "SetFullPersistent") engine.SetFullPersistent();
                        if (entry != null) ownsControl = true;
                        state = "EmergencyFullSpeed"; message = "CPU 温度达到 90 °C，保持全速至少 10 秒，降到 85 °C 后沿用当前转速并按模式规则下降。";
                    }
                    else
                    {
                        if (!sample.ManualControl.Active || sample.ManualControl.TargetRpm != target) engine.SetTargetRpm(target.Value);
                        ownsControl = true; state = "FloorCurve";
                        message = "模式控制：高于起调温度每秒 +50 RPM，低于起调温度 3 °C 并稳定 20 秒后每秒 −100 RPM。";
                    }
                }
            }
            if (light)
            {
                if (state == reportedState) return; // nothing to publish this second
                sample = engine.Poll();
            }
            Publish(sample, cpu, target);
        }
        // Persist and apply a mode. Auto hands the fan to BIOS; every other mode takes over and lets Tick follow the mode rules.
        void ApplyMode(string mode, int? minimumRpm)
        {
            FloorSettings value = settings.Copy(); value.Mode = mode; value.LastFault = null;
            if (mode == FanModes.Custom) value.MinimumRpm = minimumRpm; // the other modes keep the remembered custom minimum
            value.Validate();
            if (mode == FanModes.Auto)
            {
                bool wasAuto = settings.Mode == FanModes.Auto, needsRelease = AutoNeedsRelease(wasAuto, ownsControl, probeUntil > 0);
                faultLatched = false; faultReason = null; probeUntil = 0; controller.Reset(); tempRecovery.Clear();
                string saveError = null;
                try { FloorSettingsStore.Save(value); settings = value; } catch (Exception e) { saveError = e.Message; }
                if (needsRelease) { release.Request(); if (!release.Attempt(delegate { engine.SetAutomatic(); })) throw new InvalidOperationException("尚未恢复 BIOS 自动，将继续重试：" + release.Error); ownsControl = false; }
                if (saveError != null) throw new InvalidOperationException("已恢复 BIOS 自动，但设置未保存：" + saveError);
                return;
            }
            ModeLimits limits = value.Limits();
            Snapshot before = engine.Poll(); double? cpu = Cpu(before);
            RequireCapability(before);
            if (!cpu.HasValue) throw new InvalidOperationException("CPU 温度未就绪，模式尚未保存或启用。");
            if (before.ManualControl == null || before.ManualControl.State == "Unavailable") throw new NotSupportedException("本机的手动控速接口不可用。");
            if (probeUntil > 0) { engine.SetAutomatic(); ownsControl = false; probeUntil = 0; }
            if (!ownsControl)
            {
                bool hot = ModeController.Hottest(cpu.Value, before.CpuProtectionTemperatureC) >= FanSafetyPolicy.EmergencyC;
                int? measured = MeasuredRpm(before, DateTime.UtcNow);
                engine.SetTargetRpm(hot ? RpmRange.Maximum : ModeController.StartRpm(limits, measured, cpu.Value)); ownsControl = true; controller.Reset(); controller.Seed(limits, measured, cpu.Value);
            }
            try { FloorSettingsStore.Save(value); } catch { ReleaseFan(); throw; }
            settings = value; faultLatched = false; faultReason = null; tempRecovery.Clear();
        }
        // The 15 s full-speed test. Consent was checked by ControlPolicy; the service additionally refuses while it owns the fan.
        void RunCapabilityProbe()
        {
            // Without a pass the service never owns the fan, whatever mode is saved; only an active takeover or probe blocks the test.
            if (ownsControl || probeUntil > 0) throw new InvalidOperationException("风扇正由 DellFans 控制或正在探测，请稍后再检测。");
            Snapshot sample = engine.Poll(); EnsureCapability(sample);
            capability = new CapabilityProbe(new LiveCapabilityHardware(engine), capabilityStore, null).Test(true);
            capabilityKey = sample.Model + "|" + sample.BiosVersion;
            if (capability.State == CapabilityStates.Passed)
            { engine.Dispose(); engine = new ThermalEngine(true); lastInitialization = clock.Elapsed.TotalSeconds; } // rebuild so the RPM controller opens for this model
        }
        ServiceReply Execute(ServiceRequest request)
        {
            if (request == null) throw new ArgumentException("Request is empty.");
            switch (request.Command)
            {
                case "SetMode": ApplyMode(request.Mode, request.MinimumRpm); break;
                case "Probe":
                    if (probeUntil > 0) throw new InvalidOperationException("已有探测正在运行。");
                    RequireCapability(engine.Poll()); // same gate as ApplyMode: an unverified machine is never driven to full speed
                    engine.SetFullPersistent(); ownsControl = true; probeUntil = clock.Elapsed.TotalSeconds + 15; controller.Reset(); break;
                case "RunCapabilityProbe": RunCapabilityProbe(); break;
                case "Exit":
                    { Action exit = ExitRequested; if (exit != null) ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(300); exit(); }); }
                    return Status(false);
                default: throw new ArgumentException("Unsupported service command.");
            }
            snapshotDirty = true;
            // The mode is saved and applied; a failing first tick must hand the fan back, not turn into a failed request.
            try { Tick(); }
            catch (Exception e)
            {
                PauseForFault("后台温控异常，已临时恢复 BIOS 自动，配置已保留：" + e.Message);
                Snapshot safe = engine.Poll(); Publish(safe, Cpu(safe), null);
            }
            return Status(false);
        }
        void Loop()
        {
            IDisposable hardware = null; // lock file in the protected lock directory, held for the life of the loop
            try
            {
                try { hardware = ProtectedLocks.Acquire("hardware.lock", 0); }
                catch (TimeoutException) { throw new InvalidOperationException("Another process owns the Dell hardware interface."); }
                List<string> quarantined = FanWatchdog.RecoverPreviousServiceSessions(); // throws only while a session can still be retried
                try { FanWatchdog.PruneSessionFiles(ServicePaths.Reports, ServicePaths.Locks, DateTime.UtcNow, 7); } catch (Exception) { } // housekeeping never blocks start
                try { settings = FloorSettingsStore.Load(); }
                catch (Exception e) { settings = FloorSettings.Defaults(); settings.Mode = FanModes.Auto; settings.LastFault = "配置无效，未启用接管：" + e.Message; } // a broken file never takes over
                engine = new ThermalEngine(true); double nextPoll = 0;
                RestoreStaleFullOverride();
                if (quarantined.Count != 0) PauseForFault("上次的风扇会话经 3 次尝试仍无法恢复，已隔离 " + quarantined.Count + " 个并强制交回 BIOS 自动；保存的模式未修改，重新应用模式可恢复。");
                while (!stop.WaitOne(0))
                {
                    if (suspended)
                    {
                        if (ownsControl) ReleaseFan();
                        controller.Reset(); if (!ownsControl) suspendReleased.Set(); wake.WaitOne(500); continue;
                    }
                    if (resume)
                    { resume = false; if (settings.Enabled || ownsControl) ReleaseFan(); else ownsControl = false; controller.Reset(); nextPoll = 0; }
                    Pending item;
                    while (commands.TryDequeue(out item))
                    {
                        lock (item)
                        { if (item.Abandoned) { item.Done.Dispose(); continue; } item.Started = true; }
                        try { item.Reply = Execute(item.Request); }
                        catch (Exception e) { item.Reply = new ServiceReply { Success = false, Error = e.Message }; }
                        finally { item.Done.Set(); }
                    }
                    if (clock.Elapsed.TotalSeconds >= nextPoll)
                    {
                        try { Tick(); }
                        catch (Exception e)
                        {
                            PauseForFault("后台温控异常，已临时恢复 BIOS 自动，配置已保留：" + e.Message);
                            Snapshot safe = engine.Poll(); Publish(safe, Cpu(safe), null);
                        }
                        bool controlling = (settings.Enabled && !faultLatched) || probeUntil > 0;
                        bool viewing = clock.Elapsed.TotalSeconds - clientSeen < 10;
                        bool lowOverhead = latest != null && latest.Resources != null && latest.Resources.LowOverheadMode;
                        nextPoll = clock.Elapsed.TotalSeconds + (controlling ? (ownsControl || probeUntil > 0 ? 1 : 2) : viewing ? (lowOverhead ? 4 : 2) : 10); // 1 s while the service owns the fan
                    }
                    WaitHandle.WaitAny(new WaitHandle[] { stop, wake }, 250);
                }
            }
            catch (Exception e)
            {
                try { ReportFiles.Write(Path.Combine(ServicePaths.Reports, "service-error.json"), new { AtUtc = DateTime.UtcNow.ToString("o"), Error = e.ToString() }); } catch { }
                Action failed = Failed; if (failed != null) ThreadPool.QueueUserWorkItem(delegate { failed(); });
            }
            finally
            {
                if (engine != null)
                { try { if (ownsControl) engine.SetAutomatic(); engine.Dispose(); } catch (Exception e) { ReportFiles.Write(Path.Combine(ServicePaths.Reports, "service-stop-error.json"), new { Error = e.ToString() }); } }
                if (hardware != null) hardware.Dispose();
            }
        }
        // Runs on one of the pipe instance threads after the caller was classified, authorized and the request validated.
        ServiceReply Handle(ServiceRequest request)
        {
            if (request.Command == "Status") return Status(request.Detail);
            if (request.Command == "Capability") return Status(false);
            Pending item = new Pending { Request = request }; commands.Enqueue(item); wake.Set();
            if (!item.Done.Wait(30000))
            {
                lock (item) { if (!item.Started) item.Abandoned = true; }
                return new ServiceReply { Success = false, Error = "后台操作超时，请读取状态确认结果。" };
            }
            ServiceReply reply = item.Reply; item.Done.Dispose(); return reply;
        }
        static void ReportPipeError(Exception error)
        {
            try { ReportFiles.Write(Path.Combine(ServicePaths.Reports, "pipe-error.json"), new { AtUtc = DateTime.UtcNow.ToString("o"), Error = error.ToString() }); } catch { }
        }
        public void Dispose()
        {
            stop.Set(); wake.Set();
            if (pipeServer != null) pipeServer.Dispose();
            if (worker != null && !worker.Join(25000)) throw new TimeoutException("后台服务尚未完成风扇恢复，独立监护仍有效。");
        }
    }

    // The background controller of the portable package: DellFans.Service.exe --portable, an elevated process of the signed-in user
    // (no Windows service). It hands the fan back to the BIOS when asked to exit, at sign-out or shutdown, and before sleep.
    public static class PortableHost
    {
        public static bool IsElevated()
        { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        public static int Run()
        {
            // SystemEvents needs a thread without its own unpumped STA window; an MTA thread makes it use its own hidden window thread.
            int code = 0;
            Thread host = new Thread(delegate () { code = RunCore(); }) { Name = "DellFans controller host" };
            host.SetApartmentState(ApartmentState.MTA); host.Start(); host.Join();
            return code;
        }
        static int RunCore()
        {
            if (!IsElevated()) { Console.Error.WriteLine("DellFans.Service.exe --portable must run as administrator."); return 5; }
            SecureDirectory.ProtectRoot(ServicePaths.Root);
            ReportFiles.ServiceMode = true;
            int exitCode = 0;
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                ThermalServiceRuntime runtime = new ThermalServiceRuntime();
                runtime.Failed += delegate { exitCode = 1; done.Set(); };
                runtime.ExitRequested += delegate { done.Set(); };
                // Sign-out and shutdown: hand back while WMI is still running, before Windows ends the process.
                Microsoft.Win32.SessionEndingEventHandler ending = delegate { try { runtime.Dispose(); } catch (Exception) { } done.Set(); };
                Microsoft.Win32.PowerModeChangedEventHandler power = delegate (object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
                {
                    if (e.Mode == Microsoft.Win32.PowerModes.Suspend) runtime.Power(true, 3000);
                    else if (e.Mode == Microsoft.Win32.PowerModes.Resume) runtime.Power(false);
                };
                Microsoft.Win32.SystemEvents.SessionEnding += ending;
                Microsoft.Win32.SystemEvents.PowerModeChanged += power;
                try
                {
                    runtime.Start();
                    done.WaitOne();
                }
                finally
                {
                    Microsoft.Win32.SystemEvents.SessionEnding -= ending;
                    Microsoft.Win32.SystemEvents.PowerModeChanged -= power;
                    try { runtime.Dispose(); } catch (Exception) { exitCode = 1; } // the watchdog still guards an unfinished handback
                }
            }
            return exitCode;
        }
    }
}

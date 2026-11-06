using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace DellFans
{
    public interface ISmmFan : IDisposable
    {
        int ReadRpm();
        void SetRunningState(int state);
        void EnableAutomatic();
        void DisableAutomatic();
    }

    public sealed class DellSmmFan : ISmmFan
    {
        readonly object gate = new object();
        ManagementObject endpoint;
        // Where capability.json lives; tests point it at a temp folder.
        public static string CapabilityRoot = null;
        public DellSmmFan(string model, string bios) : this(model, bios, false) { }
        // `readOnlyProbe`: the capability probe may open the endpoint (signature and nominal-state checks only, no writes) on any model.
        // Otherwise this exact model and BIOS must have passed the capability probe.
        public DellSmmFan(string model, string bios, bool readOnlyProbe)
        {
            if (!readOnlyProbe && !new CapabilityStore(CapabilityRoot ?? ServicePaths.Root).Allows(model, bios))
                throw new NotSupportedException("SMM manual control is enabled only after this model and BIOS pass the capability probe.");
            using (ManagementObjectSearcher search = DellFanBackend.Query(@"root\dcim\sysman\diagnostics", "SELECT * FROM LegacyDiags"))
            using (ManagementObjectCollection results = search.Get())
            foreach (ManagementObject item in results)
            {
                if (Convert.ToBoolean(item["Active"])) { endpoint = item; break; }
                item.Dispose();
            }
            if (endpoint == null) throw new NotSupportedException("Dell WMI-SMM endpoint is unavailable.");
            uint[] signature = Call(0xFEA3, 0);
            if (signature[0] != 0x44494147 || signature[3] != 0x44454C4C)
                throw new NotSupportedException("Dell WMI-SMM signature does not match.");
            if (Call(0x04A3, 256)[0] != 1000 || Call(0x04A3, 512)[0] != 4000)
                throw new NotSupportedException("Fan nominal states differ from the verified profile.");
        }
        uint[] Call(uint command, uint ebx)
        {
            lock (gate)
            using (ManagementBaseObject input = endpoint.GetMethodParameters("Execute"))
            {
                string[] names = { "Eax", "Ebx", "Ecx", "Edx" };
                uint[] values = { command, ebx, 0, 0 };
                for (int i = 0; i < names.Length; i++)
                { input[names[i] + "Len"] = (uint)4; input[names[i] + "Val"] = BitConverter.GetBytes(values[i]); }
                using (ManagementBaseObject output = endpoint.InvokeMethod("Execute", input,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) }))
                {
                    uint[] result = new uint[4];
                    for (int i = 0; i < names.Length; i++)
                    {
                        byte[] bytes = (byte[])output[names[i] + "Val"];
                        if (bytes == null || bytes.Length < 4) throw new InvalidDataException("Truncated SMM response.");
                        result[i] = BitConverter.ToUInt32(bytes, 0);
                    }
                    if (result[0] == command || (result[0] & 0xFFFF) == 0xFFFF)
                        throw new InvalidOperationException("Dell SMM command 0x" + command.ToString("X4") + " was rejected.");
                    return result;
                }
            }
        }
        public int ReadRpm()
        {
            int rpm = (int)(Call(0x02A3, 0)[0] & 0xFFFF);
            if (rpm < 100 || rpm > 10000) throw new InvalidDataException("Invalid raw fan RPM feedback.");
            return rpm;
        }
        public void SetRunningState(int state)
        {
            if (state != 1 && state != 2) throw new ArgumentOutOfRangeException("state", "Only verified running states 1 and 2 are writable.");
            Call(0x01A3, (uint)(state << 8));
        }
        public void EnableAutomatic() { Call(0x35A3, 0); }
        public void DisableAutomatic() { Call(0x34A3, 0); }
        public void Dispose() { if (endpoint != null) endpoint.Dispose(); }
    }

    public static class RpmRange
    {
        public const int Minimum = 1000, Medium = 4000, Maximum = 5570;
        // Percent is a position in the verified RPM interval, not a PWM duty cycle.
        public static int FromPercent(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0 || percent > 100)
                throw new ArgumentOutOfRangeException("percent");
            return (int)Math.Round(Minimum + (Maximum - Minimum) * percent / 100);
        }
        public static double ToPercent(int rpm)
        { Validate(rpm); return (rpm - Minimum) * 100.0 / (Maximum - Minimum); }
        public static void Validate(int rpm)
        { if (rpm < Minimum || rpm > Maximum) throw new ArgumentOutOfRangeException("rpm", "Verified adjustable range: 1000-5570 RPM."); }
        public static int ChooseState(int target, int actual, int previousState)
        {
            Validate(target);
            if (target == Minimum) return 1;
            if (target == Medium) return 2;
            if (target == Maximum) return 3;
            int lower = target < Medium ? 1 : 2;
            int upper = target < Medium ? 2 : 3;
            if (actual < target - 15) return upper;
            if (actual > target + 15) return lower;
            return previousState == lower || previousState == upper ? previousState : upper;
        }
    }

    public static class FanActuation
    {
        // Logical actuator 3 enables the already configured BIOS full-speed override.
        // It never sends the unsupported raw fan-state-3 command.
        public static void Apply(ISmmFan fan, int requested, int previous, Action validateManualWrite = null)
        {
            if (requested < 1 || requested > 3 || previous < 1 || previous > 3)
                throw new ArgumentOutOfRangeException("requested");
            if (requested == previous) return;
            if (requested == 3) fan.EnableAutomatic();
            else
            {
                if (previous == 3)
                {
                    if (validateManualWrite != null) validateManualWrite();
                    fan.DisableAutomatic();
                }
                if (validateManualWrite != null) validateManualWrite();
                fan.SetRunningState(requested);
            }
        }
    }

    public static class FanSafetyPolicy
    {
        // CPU protection in every mode: full speed at EmergencyC, back to the mode rules once ReleaseC or lower.
        public const float EmergencyC = 90, ReleaseC = 85;
        public static bool TemperatureUsable(float? temperature, DateTime observedUtc, DateTime nowUtc, double maximumAge)
        {
            double age = (nowUtc - observedUtc).TotalSeconds;
            return temperature.HasValue && !float.IsNaN(temperature.Value) && !float.IsInfinity(temperature.Value) &&
                age >= 0 && age <= maximumAge;
        }
        // Monotonic variant (ARCHITECTURE section 4): ages come from Stopwatch milliseconds, so wall-clock jumps cannot freshen or stale a sample.
        public static bool TemperatureUsable(float? temperature, long observedMs, long nowMs, double maximumAge)
        {
            double age = (nowMs - observedMs) / 1000.0;
            return temperature.HasValue && !float.IsNaN(temperature.Value) && !float.IsInfinity(temperature.Value) && age >= 0 && age <= maximumAge;
        }
        public static long MonotonicMilliseconds() { return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency; }
        public static bool RequiresFullSpeed(float? temperature, DateTime observedUtc, DateTime nowUtc)
        { return !TemperatureUsable(temperature, observedUtc, nowUtc, 8) || temperature.Value >= EmergencyC; }
        public static bool FeedbackFresh(string timestamp, DateTime nowUtc)
        {
            DateTime observed;
            if (!DateTime.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out observed)) return false;
            double age = (nowUtc - observed.ToUniversalTime()).TotalSeconds;
            return age >= 0 && age <= 1;
        }
        public static bool LoopInterrupted(DateTime previousUtc, DateTime nowUtc)
        { double gap = (nowUtc - previousUtc).TotalSeconds; return gap < 0 || gap > 3; }
        public const string LoopInterruptedMessage = "控制循环中断，已请求恢复自动模式；请重新应用转速设置。";
        public const string WatchdogExitedMessage = "独立监护进程已退出，已交回 BIOS 自动；将重新启动监护后恢复接管。";
        // A starved or paused loop (heavy CPU load, sleep) or a lost watchdog is transient: BIOS takes over and the service
        // resumes by itself (a new takeover starts a new watchdog).
        public static bool IsTransientControllerError(string error) { return error == LoopInterruptedMessage || error == WatchdogExitedMessage; }
    }

    public sealed class ManualFanStatus
    {
        public bool Active { get; set; }
        public int? TargetRpm { get; set; }
        public double? RangePercent { get; set; }
        public int? ActualRpm { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
        public string FeedbackAtUtc { get; set; }
    }

    public sealed class FanWatchdogRecord
    {
        public int ParentId { get; set; }
        public long ParentStartTicks { get; set; }
        public string Model { get; set; }
        public string Bios { get; set; }
        public string RestoreOverride { get; set; }
        public bool ServiceOwned { get; set; }
    }

    // External firmware boundary: recovery tests never instantiate a WMI provider.
    public interface IFanRecoveryHardware
    {
        void RestoreAutomatic(FanWatchdogRecord record);
        void ForceFullSpeed();
    }

    public static class FanWatchdog
    {
        // a lock file in the protected lock directory (see ProtectedLocks), never a Global named mutex.
        internal static IDisposable LockSession(string path)
        {
            try { return ProtectedLocks.Acquire("recover-" + Path.GetFileName(path) + ".lock", 30000); }
            catch (TimeoutException) { throw new TimeoutException("Fan session is still busy; recovery remains pending."); }
        }
        // Holds the parent's process handle: waiting on it wakes as soon as the parent exits and cannot be fooled by PID reuse.
        sealed class ParentWatch : IDisposable
        {
            Process process;
            public ParentWatch(FanWatchdogRecord record)
            {
                try
                {
                    process = Process.GetProcessById(record.ParentId);
                    if (process.StartTime.ToUniversalTime().Ticks != record.ParentStartTicks) Release();
                }
                catch (ArgumentException) { Release(); }
                catch (InvalidOperationException) { Release(); }
                catch (System.ComponentModel.Win32Exception) { Release(); } // cannot prove the parent is alive: treat as gone
            }
            void Release() { if (process != null) process.Dispose(); process = null; }
            public bool Alive
            {
                get
                {
                    if (process == null) return false;
                    try { return !process.HasExited; }
                    catch (InvalidOperationException) { return false; }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                }
            }
            public void Wait(int milliseconds)
            {
                if (process == null) return;
                try { process.WaitForExit(milliseconds); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            public void Dispose() { if (process != null) process.Dispose(); }
        }
        // Command line used to start the independent monitor: the Rust DellFans.Watchdog.exe when shipped next to the service, else the C# loop.
        internal static void WatchdogCommand(string baseDirectory, string recordPath, out string file, out string arguments)
        {
            string rust = Path.Combine(baseDirectory, "DellFans.Watchdog.exe");
            if (File.Exists(rust)) { file = rust; arguments = "\"" + recordPath + "\""; }
            else { file = Path.Combine(baseDirectory, "DellFans.Cli.exe"); arguments = "--watchdog \"" + recordPath + "\""; }
        }
        internal static void RequireArmedSession(string path)
        {
            if (!File.Exists(path + ".armed") || File.Exists(path + ".recovered") ||
                File.Exists(path + ".recovery-failed") || File.Exists(path + ".recovery-invalid"))
                throw new InvalidOperationException("The fan session has ended or recovery is pending; manual writes are blocked.");
        }
        static bool ParentAlive(FanWatchdogRecord record)
        { using (ParentWatch parent = new ParentWatch(record)) return parent.Alive; }
        static bool HeartbeatFresh(string path)
        {
            string heartbeat = path + ".heartbeat";
            if (!File.Exists(heartbeat)) return false;
            TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat);
            return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(8);
        }
        static FanWatchdogRecord ReadRecord(string path)
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Watchdog record exceeds its size limit.");
            FanWatchdogRecord record = MiniJson.Deserialize<FanWatchdogRecord>(File.ReadAllText(path), 65536);
            if (record == null || record.ParentId <= 0 || record.ParentStartTicks <= 0 ||
                string.IsNullOrWhiteSpace(record.Model) || string.IsNullOrWhiteSpace(record.Bios) ||
                (record.RestoreOverride != "Enabled" && record.RestoreOverride != "Disabled"))
                throw new InvalidDataException("Invalid watchdog recovery record; no firmware protocol was inferred.");
            return record;
        }
        static void ReportInvalidRecord(string path, Exception error)
        {
            ReportFiles.Write(path + ".recovery-invalid", new { AtUtc = DateTime.UtcNow.ToString("o"),
                AutomaticRestored = false, Error = error.Message });
        }
        static bool RecoverySucceeded(string path)
        {
            string resultPath = path + ".recovered";
            if (!File.Exists(resultPath)) return false;
            try
            {
                if (new FileInfo(resultPath).Length > 65536) return false;
                Dictionary<string, object> result = MiniJson.Deserialize<Dictionary<string, object>>(File.ReadAllText(resultPath), 65536);
                object restored, error;
                return result != null && result.TryGetValue("AutomaticRestored", out restored) && restored is bool && (bool)restored &&
                    (!result.TryGetValue("Error", out error) || error == null);
            }
            catch (Exception) { return false; } // An unreadable result is not proof of successful recovery.
        }
        sealed class FirmwareRecoveryHardware : IFanRecoveryHardware
        {
            public void RestoreAutomatic(FanWatchdogRecord record)
            {
                using (DellFanBackend backend = new DellFanBackend())
                {
                    if (backend.Model != record.Model || backend.BiosVersion != record.Bios)
                        throw new NotSupportedException("The platform or BIOS changed; automatic recovery is unconfirmed and no old SMM protocol was sent.");
                    using (DellSmmFan smm = new DellSmmFan(record.Model, record.Bios)) smm.EnableAutomatic();
                    backend.SetMode(record.RestoreOverride);
                }
            }
            public void ForceFullSpeed()
            { using (DellFanBackend backend = new DellFanBackend()) backend.SetMode("Enabled"); }
        }
        public static int Run(string path)
        { return Run(path, ReportFiles.ReportsDirectory, new FirmwareRecoveryHardware()); }
        public static int Run(string path, string reportsDirectory, IFanRecoveryHardware hardware)
        {
            string folder = Path.GetFullPath(reportsDirectory) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("fan-watchdog-")) return 2;
            string armed = path + ".armed";
            FanWatchdogRecord record;
            try { record = ReadRecord(path); }
            catch (Exception error) { ReportInvalidRecord(path, error); return 1; }
            File.WriteAllText(path + ".ready", "ready");
            DateTime started = DateTime.UtcNow;
            bool sawArmed = false;
            using (ParentWatch parent = new ParentWatch(record))
            while (true)
            {
                bool parentAlive = parent.Alive;
                bool isArmed = File.Exists(armed);
                sawArmed |= isArmed;
                if (sawArmed && !isArmed) return 0; // Normal restoration completed.
                // Waiting on the parent handle returns at once when the parent exits; otherwise the heartbeat is rechecked every second.
                if (!sawArmed && parentAlive && DateTime.UtcNow - started < TimeSpan.FromSeconds(20)) { parent.Wait(1000); continue; }
                bool fresh = HeartbeatFresh(path);
                if (isArmed && parentAlive && fresh) { parent.Wait(1000); continue; }
                if (!sawArmed && parentAlive) return 0;
                int code = Recover(path, reportsDirectory, hardware);
                if (code != SessionAlive) return code;
                parent.Wait(1000);
            }
        }
        // Exit codes of Recover: 0 recovered or nothing to do; 1 failed, the caller must retry; 2 refused path; SessionAlive = the parent is
        // alive with a fresh heartbeat again, so nothing was written and the caller keeps monitoring.
        public const int SessionAlive = 3;
        public static int Recover(string path)
        { return Recover(path, ReportFiles.ReportsDirectory, new FirmwareRecoveryHardware()); }
        // One recovery pass (the part of the watchdog that touches firmware); `DellFans.Cli.exe --recover <session>` and the Rust watchdog call it.
        public static int Recover(string path, string reportsDirectory, IFanRecoveryHardware hardware)
        {
            string folder = Path.GetFullPath(reportsDirectory) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("fan-watchdog-")) return 2;
            string armed = path + ".armed";
            FanWatchdogRecord record;
            try { record = ReadRecord(path); }
            catch (Exception error) { ReportInvalidRecord(path, error); return 1; }
            if (!File.Exists(armed) || RecoverySucceeded(path)) return 0;
            // A replacement service may start immediately, before the old helper has recovered.
            // Serialize recovery per record, then recheck completion inside the lock.
            IDisposable recovery;
            try { recovery = LockSession(path); }
            catch (Exception error)
            {
                ReportFiles.Write(path + ".recovery-failed", new { AtUtc = DateTime.UtcNow.ToString("o"), AutomaticRestored = false, Error = error.Message });
                return 1;
            }
            using (recovery)
            {
                if (RecoverySucceeded(path) || !File.Exists(armed)) return 0;
                if (ParentAlive(record) && HeartbeatFresh(path)) return SessionAlive;
                string failure = null; bool restored = false;
                try
                {
                    hardware.RestoreAutomatic(record);
                    restored = true;
                }
                catch (Exception e)
                {
                    failure = e.Message;
                    try { hardware.ForceFullSpeed(); }
                    catch (Exception fallback) { failure += " Full-speed fallback: " + fallback.Message; }
                }
                ReportFiles.Write(path + (restored ? ".recovered" : ".recovery-failed"),
                    new { AtUtc = DateTime.UtcNow.ToString("o"), AutomaticRestored = restored, Error = failure });
                return restored ? 0 : 1;
            }
        }
        public static List<string> RecoverPreviousServiceSessions()
        { return RecoverPreviousServiceSessions(ServicePaths.Reports, new FirmwareRecoveryHardware()); }
        public const int MaximumRecoveryAttempts = 3;
        // Returns the sessions that failed MaximumRecoveryAttempts times and were quarantined (the armed marker is renamed to
        // *.armed.recovery-failed, so they are not retried): the caller forces BIOS automatic through the normal backend and starts in
        // FaultAutomatic. A session that has failed fewer times still throws, so the next start retries it.
        public static List<string> RecoverPreviousServiceSessions(string reportsDirectory, IFanRecoveryHardware hardware)
        {
            List<string> quarantined = new List<string>();
            if (!Directory.Exists(reportsDirectory)) return quarantined;
            List<Exception> failures = new List<Exception>();
            using (Process self = Process.GetCurrentProcess())
            foreach (string path in Directory.GetFiles(reportsDirectory, "fan-watchdog-*.json"))
            {
                if (!File.Exists(path + ".armed") || RecoverySucceeded(path)) continue;
                try
                {
                    FanWatchdogRecord record;
                    try { record = ReadRecord(path); }
                    catch (Exception error) { ReportInvalidRecord(path, error); throw; }
                    if (!record.ServiceOwned || (record.ParentId == self.Id && record.ParentStartTicks == self.StartTime.ToUniversalTime().Ticks)) continue;
                    if (Run(path, reportsDirectory, hardware) != 0) throw new InvalidOperationException("Previous service session did not complete automatic recovery.");
                    TryDelete(path + ".recovery-attempts");
                }
                catch (Exception error)
                {
                    if (CountFailedAttempt(path) >= MaximumRecoveryAttempts)
                    {
                        try { File.Move(path + ".armed", path + ".armed.recovery-failed"); quarantined.Add(path); continue; }
                        catch (Exception) { } // could not quarantine: keep failing loudly rather than silently resume
                    }
                    failures.Add(new InvalidOperationException("Pending watchdog recovery: " + Path.GetFileName(path), error));
                }
            }
            if (failures.Count != 0) throw new AggregateException("Some previous sessions remain unresolved; control must not resume.", failures);
            return quarantined;
        }
        static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }
        static int CountFailedAttempt(string path)
        {
            int count = 0;
            try { int.TryParse(File.ReadAllText(path + ".recovery-attempts").Trim(), out count); } catch (Exception) { }
            count++;
            try { File.WriteAllText(path + ".recovery-attempts", count.ToString()); } catch (Exception) { }
            return count;
        }
        static readonly string[] SessionSuffixes = { ".armed", ".heartbeat", ".ready", ".recovered", ".watchdog-error", ".recovery-failed", ".recovery-invalid", ".recovery-attempts", ".armed.recovery-failed" };
        // deletes session files and recover-*.lock files not touched for `days` days. A session is only pruned when it is disarmed or
        // its recovery succeeded; an armed one is never touched. Best effort: a locked file stays until the next start.
        public static int PruneSessionFiles(string reportsDirectory, string locksDirectory, DateTime nowUtc, int days)
        {
            int removed = 0; DateTime limit = nowUtc.AddDays(-days);
            if (Directory.Exists(reportsDirectory))
                foreach (string path in Directory.GetFiles(reportsDirectory, "fan-watchdog-*.json"))
                {
                    if (File.Exists(path + ".armed") && !RecoverySucceeded(path)) continue;
                    List<string> group = new List<string> { path };
                    foreach (string suffix in SessionSuffixes) if (File.Exists(path + suffix)) group.Add(path + suffix);
                    if (group.Any(f => File.GetLastWriteTimeUtc(f) >= limit)) continue;
                    foreach (string f in group) { try { File.Delete(f); removed++; } catch (Exception) { } }
                }
            if (Directory.Exists(locksDirectory))
                foreach (string lockFile in Directory.GetFiles(locksDirectory, "recover-*.lock"))
                {
                    string name = Path.GetFileName(lockFile), session = Path.Combine(reportsDirectory, name.Substring(8, name.Length - 8 - 5));
                    if (File.GetLastWriteTimeUtc(lockFile) >= limit || (File.Exists(session + ".armed") && !RecoverySucceeded(session))) continue;
                    try { File.Delete(lockFile); removed++; } catch (Exception) { }
                }
            return removed;
        }
    }

    public interface IFanWatchdogStarter
    {
        int Start(string recordPath);
    }

    public sealed class ManualRpmController : IDisposable
    {
        readonly object gate = new object();
        readonly ISmmFan smm;
        readonly IFanBackend backend;
        readonly string model, bios;
        readonly string reportsDirectory;
        readonly IFanWatchdogStarter watchdogStarter;
        readonly bool backgroundSafety;
        CancellationTokenSource cancel;
        TaskThread worker;
        string watchdogPath, originalOverride;
        ManualFanStatus status = new ManualFanStatus { State = "Automatic" };
        int target;
        long lastTemperatureMs;
        readonly Func<long> clockMs;
        float? temperature;
        bool armed;
        int initialState = 2;
        public int WatchdogProcessId { get; private set; }
        Process watchdogProcess;

        // One dedicated thread owns the feedback loop; regular sensor polling remains independent.
        sealed class TaskThread
        {
            public Thread Thread;
            // Highest: an all-core user load must not starve this light 120 ms loop past its 3 s interruption limit (seen on the real machine).
            public TaskThread(ThreadStart action) { Thread = new Thread(action) { IsBackground = true, Name = "Dell fan RPM feedback", Priority = ThreadPriority.Highest }; }
        }
        sealed class ProcessWatchdogStarter : IFanWatchdogStarter
        {
            public int Start(string recordPath)
            {
                string file, arguments;
                FanWatchdog.WatchdogCommand(AppDomain.CurrentDomain.BaseDirectory, recordPath, out file, out arguments);
                ProcessStartInfo start = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
                using (Process helper = Process.Start(start))
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!File.Exists(recordPath + ".ready"))
                    {
                        if (helper.HasExited || DateTime.UtcNow > deadline) throw new InvalidOperationException("Fan watchdog did not start; manual mode was not entered.");
                        Thread.Sleep(50);
                    }
                    return helper.Id;
                }
            }
        }
        public ManualRpmController(ISmmFan smm, IFanBackend backend, string model, string bios, bool backgroundSafety = false)
            : this(smm, backend, model, bios, backgroundSafety, ReportFiles.ReportsDirectory, new ProcessWatchdogStarter()) { }
        public ManualRpmController(ISmmFan smm, IFanBackend backend, string model, string bios, bool backgroundSafety,
            string reportsDirectory, IFanWatchdogStarter watchdogStarter)
            : this(smm, backend, model, bios, backgroundSafety, reportsDirectory, watchdogStarter, FanSafetyPolicy.MonotonicMilliseconds) { }
        // clockMs is injectable so tests can move time (and the wall clock) without sleeping.
        public ManualRpmController(ISmmFan smm, IFanBackend backend, string model, string bios, bool backgroundSafety,
            string reportsDirectory, IFanWatchdogStarter watchdogStarter, Func<long> clockMs)
        {
            this.clockMs = clockMs;
            this.smm = smm; this.backend = backend; this.model = model; this.bios = bios; this.backgroundSafety = backgroundSafety;
            this.reportsDirectory = Path.GetFullPath(reportsDirectory); this.watchdogStarter = watchdogStarter;
        }
        public ManualFanStatus Status
        {
            get { lock (gate) return new ManualFanStatus { Active = status.Active, TargetRpm = status.TargetRpm,
                RangePercent = status.RangePercent, ActualRpm = status.ActualRpm, State = status.State,
                Error = status.Error, FeedbackAtUtc = status.FeedbackAtUtc }; }
        }
        public void ObserveTemperature(float? value)
        { lock (gate) { temperature = value; lastTemperatureMs = clockMs(); } }

        void PrepareWatchdog()
        {
            using (Process self = Process.GetCurrentProcess())
            {
                watchdogPath = Path.Combine(reportsDirectory, "fan-watchdog-" + Guid.NewGuid().ToString("N") + ".json");
                ReportFiles.Write(watchdogPath, new FanWatchdogRecord { ParentId = self.Id,
                    ParentStartTicks = self.StartTime.ToUniversalTime().Ticks, Model = model, Bios = bios,
                    RestoreOverride = originalOverride, ServiceOwned = backgroundSafety });
            }
            WatchdogProcessId = watchdogStarter.Start(watchdogPath);
            // A handle (not the PID) so a reused PID cannot pass for the watchdog; 0 = no process to watch (test starters).
            if (watchdogProcess != null) watchdogProcess.Dispose();
            try { watchdogProcess = WatchdogProcessId > 0 ? Process.GetProcessById(WatchdogProcessId) : null; }
            catch (ArgumentException) { throw new InvalidOperationException("Fan watchdog exited before manual mode was entered."); }
            File.WriteAllText(watchdogPath + ".heartbeat", "alive");
            File.WriteAllText(watchdogPath + ".armed", "armed");
            armed = true;
        }

        public void SetTarget(int rpm)
        {
            RpmRange.Validate(rpm);
            lock (gate)
            {
                if (status.Active) { target = rpm; status.TargetRpm = rpm; status.RangePercent = RpmRange.ToPercent(rpm); return; }
                if (armed) throw new InvalidOperationException("前一次恢复尚未完成，请先恢复 BIOS 自动模式。");
                if (!FanSafetyPolicy.TemperatureUsable(temperature, lastTemperatureMs, clockMs(), 6))
                    throw new InvalidOperationException("等待有效 CPU 温度采样后再应用设置。");
                if (temperature.Value >= FanSafetyPolicy.EmergencyC && rpm != RpmRange.Maximum)
                    throw new InvalidOperationException("CPU 温度较高，请先使用全速覆盖或等待温度下降。");
            }
            CleanupWorker();
            originalOverride = backgroundSafety ? "Disabled" : backend.ReadMode();
            PrepareWatchdog();
            try
            {
                using (FanWatchdog.LockSession(watchdogPath))
                {
                    FanWatchdog.RequireArmedSession(watchdogPath);
                    // Set NVRAM once at entry. All feedback adjustments use runtime SMM calls.
                    backend.SetMode("Enabled");
                    smm.EnableAutomatic(); // Validate recovery before disabling firmware control.
                    FanWatchdog.RequireArmedSession(watchdogPath);
                    initialState = rpm == RpmRange.Maximum ? 3 : 2;
                    if (initialState == 2)
                    {
                        smm.DisableAutomatic();
                        FanWatchdog.RequireArmedSession(watchdogPath);
                        smm.SetRunningState(2);
                    }
                    FanWatchdog.RequireArmedSession(watchdogPath);
                    lock (gate)
                    {
                        target = rpm;
                        status = new ManualFanStatus { Active = true, TargetRpm = rpm, RangePercent = RpmRange.ToPercent(rpm), State = "Tracking" };
                    }
                    File.WriteAllText(watchdogPath + ".heartbeat", "alive");
                    cancel = new CancellationTokenSource();
                    worker = new TaskThread(Run); worker.Thread.Start();
                }
            }
            catch
            {
                // Automatic restoration must not depend on a preceding fan-state write succeeding.
                try { RestoreSession(originalOverride); }
                catch { } // Keep the independent watchdog armed if rollback fails.
                throw;
            }
        }

        void Run()
        {
            int previous = initialState;
            // Entry blocks the caller (and so new temperature samples) for the BIOS write, measured at 8-10 s. The loop's 8 s guard
            // therefore counts from its own start until the first new sample arrives; without one within 8 s it still hands back.
            long started = clockMs();
            DateTime lastHeartbeat = DateTime.MinValue;
            DateTime lastIteration = DateTime.UtcNow;
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    DateTime iteration = DateTime.UtcNow;
                    if (FanSafetyPolicy.LoopInterrupted(lastIteration, iteration))
                        throw new InvalidOperationException(FanSafetyPolicy.LoopInterruptedMessage);
                    Process guard = watchdogProcess;
                    if (guard != null && guard.HasExited) throw new InvalidOperationException(FanSafetyPolicy.WatchdogExitedMessage);
                    lastIteration = iteration;
                    int requested;
                    using (FanWatchdog.LockSession(watchdogPath))
                    {
                        FanWatchdog.RequireArmedSession(watchdogPath);
                        if (File.Exists(watchdogPath + ".recovered")) throw new InvalidOperationException("独立监护进程已恢复风扇自动控制，请重新应用设置。");
                        bool unsafeTemperature, invalidTemperature;
                        lock (gate)
                        {
                            requested = target;
                            invalidTemperature = !FanSafetyPolicy.TemperatureUsable(temperature, Math.Max(lastTemperatureMs, started), clockMs(), 8);
                            unsafeTemperature = invalidTemperature || temperature.Value >= FanSafetyPolicy.EmergencyC;
                        }
                        if (backgroundSafety && invalidTemperature) throw new InvalidOperationException("CPU 温度采样失效，已请求恢复 BIOS 自动控制。");
                        if (unsafeTemperature)
                        {
                            smm.EnableAutomatic(); backend.SetMode("Enabled");
                            lock (gate) { status.Active = false; status.State = "ProtectionFullSpeed"; status.Error = "CPU 温度过高或采样中断，已启用保护全速。点击 BIOS 自动可恢复。"; }
                            Disarm();
                            return;
                        }
                        int actual = smm.ReadRpm();
                        FanWatchdog.RequireArmedSession(watchdogPath);
                        int state = RpmRange.ChooseState(requested, actual, previous);
                        if (state != previous)
                        {
                            FanActuation.Apply(smm, state, previous, delegate { FanWatchdog.RequireArmedSession(watchdogPath); });
                            previous = state;
                        }
                        lock (gate)
                        {
                            status.ActualRpm = actual; status.FeedbackAtUtc = DateTime.UtcNow.ToString("o");
                            status.State = Math.Abs(actual - requested) <= 100 ? "Holding" : "Tracking";
                        }
                        if (DateTime.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(1))
                        { File.WriteAllText(watchdogPath + ".heartbeat", "alive"); lastHeartbeat = DateTime.UtcNow; }
                    }
                    if (cancel.Token.WaitHandle.WaitOne(FeedbackSchedule.DelayMilliseconds(requested))) break;
                }
            }
            catch (Exception e)
            {
                bool restored = false;
                try { RestoreSession(originalOverride); restored = true; }
                catch { } // Watchdog will retry if this process cannot restore.
                lock (gate) { status.Active = false; status.State = restored ? "AutomaticAfterError" : "RecoveryPending"; status.Error = e.Message; }
            }
        }

        void Disarm()
        {
            if (watchdogPath != null && File.Exists(watchdogPath + ".armed")) File.Delete(watchdogPath + ".armed");
            armed = false;
        }
        void RestoreSession(string mode)
        {
            using (FanWatchdog.LockSession(watchdogPath))
            {
                smm.EnableAutomatic();
                backend.SetMode(mode);
                Disarm();
            }
        }
        void CleanupWorker()
        {
            if (worker != null && !worker.Thread.Join(12000)) throw new InvalidOperationException("Previous feedback worker is still stopping.");
            if (cancel != null) { cancel.Dispose(); cancel = null; }
            worker = null;
        }
        public void Stop(bool restoreOriginalOverride)
        {
            if (cancel != null) cancel.Cancel();
            if (worker != null && !worker.Thread.Join(12000)) throw new InvalidOperationException("Feedback worker is still stopping; watchdog remains active.");
            if (armed || (Status.Active))
            {
                RestoreSession(restoreOriginalOverride ? originalOverride : "Disabled");
            }
            lock (gate) status = new ManualFanStatus { State = "Automatic" };
            CleanupWorker();
        }
        public void Dispose() { Stop(true); smm.Dispose(); if (watchdogProcess != null) watchdogProcess.Dispose(); }
    }
}

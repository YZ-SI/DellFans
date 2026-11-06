using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DellFans;

internal sealed class SimulatedRecoveryHardware : IFanRecoveryHardware
{
    public bool Available;
    public string Mode = "Manual";
    public void RestoreAutomatic(FanWatchdogRecord record)
    {
        if (!Available) throw new IOException("Simulated hardware unavailable");
        Mode = "Automatic";
    }
    public void ForceFullSpeed()
    {
        if (!Available) throw new IOException("Simulated fallback unavailable");
        Mode = "Full";
    }
}

internal static class RecoveryTests
{
    sealed class CapturedWatchdog : IFanWatchdogStarter
    {
        public string Path;
        public bool Fail;
        public int Start(string path)
        { Path = path; if (Fail) throw new IOException("Simulated process start failure"); return 0; }
    }
    sealed class PausedEntryHardware : IFanBackend, ISmmFan, IFanRecoveryHardware
    {
        readonly CapturedWatchdog watchdog;
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Proceed = new ManualResetEventSlim();
        public volatile string Mode = "Automatic";
        bool pause = true;
        public PausedEntryHardware(CapturedWatchdog watchdog) { this.watchdog = watchdog; }
        public string ReadMode() { return Mode == "Full" ? "Enabled" : "Disabled"; }
        public void SetMode(string mode)
        {
            if (mode == "Enabled" && pause)
            {
                pause = false;
                File.SetLastWriteTimeUtc(watchdog.Path + ".heartbeat", DateTime.UtcNow.AddMinutes(-1));
                Entered.Set();
                if (!Proceed.Wait(10000)) throw new TimeoutException("Test did not release simulated slow firmware");
            }
            Mode = mode == "Enabled" ? "Full" : "Automatic";
        }
        public int ReadRpm() { return 2500; }
        public void SetRunningState(int state) { Mode = "Manual"; }
        public void EnableAutomatic() { Mode = "Automatic"; }
        public void DisableAutomatic() { Mode = "Manual"; }
        public void RestoreAutomatic(FanWatchdogRecord record) { Mode = "Automatic"; }
        public void ForceFullSpeed() { Mode = "Full"; }
        public void Dispose() { }
    }
    sealed class SteadyHardware : IFanBackend, ISmmFan
    {
        public volatile string Mode = "Disabled";
        public string ReadMode() { return Mode; }
        public Action<string> OnSetMode;
        public void SetMode(string mode) { if (OnSetMode != null) OnSetMode(mode); Mode = mode; }
        public int ReadRpm() { return 2500; }
        public void SetRunningState(int state) { }
        public void EnableAutomatic() { }
        public void DisableAutomatic() { }
        public void Dispose() { }
    }
    sealed class ResumedHardware : IFanBackend, ISmmFan, IFanRecoveryHardware
    {
        readonly CapturedWatchdog watchdog;
        int reads;
        bool recovered;
        public volatile string Mode = "Automatic";
        public volatile bool ManualAfterRecovery;
        public ResumedHardware(CapturedWatchdog watchdog) { this.watchdog = watchdog; }
        public string ReadMode() { return Mode == "Full" ? "Enabled" : "Disabled"; }
        public void SetMode(string mode) { Mode = mode == "Enabled" ? "Full" : "Automatic"; }
        public int ReadRpm()
        {
            File.SetLastWriteTimeUtc(watchdog.Path + ".heartbeat", DateTime.UtcNow.AddMinutes(-1));
            return ++reads % 2 == 0 ? 1000 : 5000;
        }
        void Manual() { if (recovered) ManualAfterRecovery = true; Mode = "Manual"; }
        public void SetRunningState(int state) { Manual(); }
        public void EnableAutomatic() { Mode = "Automatic"; }
        public void DisableAutomatic() { Manual(); }
        public void RestoreAutomatic(FanWatchdogRecord record) { recovered = true; Mode = "Automatic"; }
        public void ForceFullSpeed() { Mode = "Full"; }
        public void Dispose() { }
    }
    sealed class PausedFeedbackHardware : IFanBackend, ISmmFan, IFanRecoveryHardware
    {
        readonly CapturedWatchdog watchdog;
        int reads;
        int disables;
        readonly bool pauseDuringTransition;
        public readonly ManualResetEventSlim Paused = new ManualResetEventSlim();
        public readonly ManualResetEventSlim Proceed = new ManualResetEventSlim();
        public volatile string Mode = "Automatic";
        public volatile bool ManualAfterFailedRecovery;
        public PausedFeedbackHardware(CapturedWatchdog watchdog, bool pauseDuringTransition = false)
        { this.watchdog = watchdog; this.pauseDuringTransition = pauseDuringTransition; }
        public string ReadMode() { return Mode == "Full" ? "Enabled" : "Disabled"; }
        public void SetMode(string mode) { Mode = mode == "Enabled" ? "Full" : "Automatic"; }
        public int ReadRpm()
        {
            if (pauseDuringTransition) return ++reads == 1 ? 1000 : 5000;
            if (++reads == 1) return 5000;
            Pause();
            return 1000;
        }
        void Pause()
        {
            File.SetLastWriteTimeUtc(watchdog.Path + ".heartbeat", DateTime.UtcNow.AddMinutes(-1));
            Paused.Set();
            if (!Proceed.Wait(40000)) throw new TimeoutException("Test did not release simulated blocked feedback");
        }
        void Manual()
        {
            if (File.Exists(watchdog.Path + ".recovery-failed")) ManualAfterFailedRecovery = true;
            Mode = "Manual";
        }
        public void SetRunningState(int state) { Manual(); }
        public void EnableAutomatic() { Mode = "Automatic"; }
        public void DisableAutomatic()
        {
            Manual();
            if (pauseDuringTransition && ++disables == 2) Pause();
        }
        public void RestoreAutomatic(FanWatchdogRecord record) { Mode = "Automatic"; }
        public void ForceFullSpeed() { Mode = "Full"; }
        public void Dispose() { }
    }
    static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    static string NewRecord(string folder)
    {
        string path = Path.Combine(folder, "fan-watchdog-" + Guid.NewGuid().ToString("N") + ".json");
        ReportFiles.Write(path, new FanWatchdogRecord { ParentId = int.MaxValue, ParentStartTicks = 1,
            Model = "OptiPlex Example", Bios = "1.0.0", RestoreOverride = "Disabled", ServiceOwned = true });
        File.WriteAllText(path + ".armed", "armed");
        return path;
    }
    static void FailedRecoveryRemainsRetryable(string folder)
    {
        string path = NewRecord(folder);
        ReportFiles.Write(path + ".recovered", new { AutomaticRestored = false, Error = "Legacy failed attempt" });
        SimulatedRecoveryHardware hardware = new SimulatedRecoveryHardware();
        Require(FanWatchdog.Run(path, folder, hardware) == 1, "An unavailable recovery must report failure");
        Require(FanWatchdog.Run(path, folder, hardware) == 1,
            "A previous failed result must not turn a still-failed recovery into success");
        hardware.Available = true;
        Require(FanWatchdog.Run(path, folder, hardware) == 0 && hardware.Mode == "Automatic",
            "Recovery must succeed when the same external hardware becomes available again");
    }
    static void CorruptSessionDoesNotPreventOtherRecoveries(string folder)
    {
        string bad = Path.Combine(folder, "fan-watchdog-invalid.json");
        File.WriteAllText(bad, "null"); File.WriteAllText(bad + ".armed", "armed");
        NewRecord(folder);
        SimulatedRecoveryHardware hardware = new SimulatedRecoveryHardware { Available = true };
        bool incomplete = false;
        try { FanWatchdog.RecoverPreviousServiceSessions(folder, hardware); }
        catch (AggregateException) { incomplete = true; }
        Require(incomplete && hardware.Mode == "Automatic",
            "Invalid session must report incomplete recovery without preventing valid sessions from recovering");
    }
    static void SlowEntryCannotBeOvertakenByRecovery(string folder)
    {
        CapturedWatchdog starter = new CapturedWatchdog();
        PausedEntryHardware hardware = new PausedEntryHardware(starter);
        ManualRpmController controller = new ManualRpmController(hardware, hardware, "OptiPlex Example", "1.0.0", true, folder, starter);
        Task entry = null, recovery = null;
        try
        {
            controller.ObserveTemperature(50);
            entry = Task.Run(delegate { controller.SetTarget(2500); });
            Require(hardware.Entered.Wait(2000), "Simulated slow entry was not reached");
            recovery = Task.Run(delegate { return FanWatchdog.Run(starter.Path, folder, hardware); });
            Require(!recovery.Wait(500), "Watchdog must not overtake an in-flight parent entry sequence");
            hardware.Proceed.Set();
            Require(entry.Wait(3000), "Parent entry did not finish");
            Thread.Sleep(300);
            Require(controller.Status.Active && hardware.Mode == "Manual" && !recovery.IsCompleted,
                "A watchdog that waited for entry must recheck its refreshed heartbeat instead of restoring late");
            controller.Stop(false);
            Require(recovery.Wait(2000) && hardware.Mode == "Automatic",
                "Normal stop must finish automatic restoration before the watchdog can finish");
        }
        finally
        {
            hardware.Proceed.Set();
            if (entry != null) try { entry.Wait(3000); } catch { }
            controller.Stop(false);
            if (recovery != null) try { recovery.Wait(2000); } catch { }
        }
    }
    // Found on the real machine: killing the watchdog left the service writing firmware with nobody to recover it.
    sealed class RealProcessWatchdog : IFanWatchdogStarter
    {
        public string Path; public Process Helper;
        public int Start(string path) { Path = path; Helper = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }); return Helper.Id; }
    }
    sealed class WatchdogTestHardware : IFanBackend, ISmmFan
    {
        public volatile string Mode = "Automatic";
        public string ReadMode() { return Mode == "Full" ? "Enabled" : "Disabled"; }
        public void SetMode(string mode) { Mode = mode == "Enabled" ? "Full" : "Automatic"; }
        public int ReadRpm() { return 2500; }
        public void SetRunningState(int state) { Mode = "Manual"; }
        public void EnableAutomatic() { Mode = "Automatic"; }
        public void DisableAutomatic() { Mode = "Manual"; }
        public void Dispose() { }
    }
    static void WatchdogExitStopsManualControl(string folder)
    {
        folder = Path.Combine(folder, "watchdog-exit"); Directory.CreateDirectory(folder); // isolated from sessions other tests leave pending
        RealProcessWatchdog starter = new RealProcessWatchdog(); WatchdogTestHardware hardware = new WatchdogTestHardware();
        using (ManualRpmController controller = new ManualRpmController(hardware, hardware, "OptiPlex Example", "1.0.0", true, folder, starter))
        {
            controller.ObserveTemperature(50); controller.SetTarget(2500);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!controller.Status.Active && DateTime.UtcNow < deadline) Thread.Sleep(25);
            Require(controller.Status.Active && hardware.Mode == "Manual", "manual control did not start");
            starter.Helper.Kill(); starter.Helper.WaitForExit();
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (controller.Status.Active && DateTime.UtcNow < deadline) Thread.Sleep(25);
            Require(!controller.Status.Active && hardware.Mode == "Automatic" && controller.Status.Error == FanSafetyPolicy.WatchdogExitedMessage,
                "A dead watchdog must end manual control within 3 s and hand the fan back: active=" + controller.Status.Active + " mode=" + hardware.Mode + " error=" + controller.Status.Error);
            Require(FanSafetyPolicy.IsTransientControllerError(controller.Status.Error), "a dead watchdog is a transient handback (the service restarts it and resumes)");
        }
    }
    static void RecoveredParentCannotWriteManualAgain(string folder)
    {
        CapturedWatchdog starter = new CapturedWatchdog();
        ResumedHardware hardware = new ResumedHardware(starter);
        using (ManualRpmController controller = new ManualRpmController(hardware, hardware, "OptiPlex Example", "1.0.0", true, folder, starter))
        {
            controller.ObserveTemperature(50); controller.SetTarget(2500);
            Task<int> recovery = Task.Run(delegate { return FanWatchdog.Run(starter.Path, folder, hardware); });
            // The simulated parent re-freshens its heartbeat about once a second, in step with the watchdog's 1 s check; allow the phases to drift apart.
            Require(recovery.Wait(15000) && recovery.Result == 0, "Stale active parent was not recovered");
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (controller.Status.Active && DateTime.UtcNow < deadline) Thread.Sleep(25);
            Require(!controller.Status.Active && hardware.Mode == "Automatic" && !hardware.ManualAfterRecovery,
                "A recovered parent must stay automatic rather than issue another manual write");
        }
    }
    static void FailedPreparationDoesNotRequestFirmwareRecovery(string folder)
    {
        CapturedWatchdog starter = new CapturedWatchdog { Fail = true };
        PausedEntryHardware hardware = new PausedEntryHardware(starter);
        using (ManualRpmController controller = new ManualRpmController(hardware, hardware, "OptiPlex Example", "1.0.0", true, folder, starter))
        {
            controller.ObserveTemperature(50);
            bool rejected = false;
            try { controller.SetTarget(2500); } catch (IOException) { rejected = true; }
            SimulatedRecoveryHardware recoveryHardware = new SimulatedRecoveryHardware { Available = true };
            Require(rejected && hardware.Mode == "Automatic" &&
                FanWatchdog.Run(starter.Path, folder, recoveryHardware) == 0 && recoveryHardware.Mode == "Manual",
                "Failed helper preparation must leave control unchanged and its unarmed record must not request recovery");
        }
    }
    static void BusySessionLeavesPendingRecoveryAndRejectsResumedActuation(string folder, bool pauseDuringTransition = false)
    {
        CapturedWatchdog starter = new CapturedWatchdog();
        PausedFeedbackHardware hardware = new PausedFeedbackHardware(starter, pauseDuringTransition);
        using (ManualRpmController controller = new ManualRpmController(hardware, hardware, "OptiPlex Example", "1.0.0", true, folder, starter))
        {
            try
            {
                controller.ObserveTemperature(50); controller.SetTarget(pauseDuringTransition ? 4500 : 2500);
                Require(hardware.Paused.Wait(2000), "Simulated blocked feedback was not reached");
                Task<int> recovery = Task.Run(delegate { return FanWatchdog.Run(starter.Path, folder, hardware); });
                Require(recovery.Wait(35000) && recovery.Result == 1,
                    "A busy session must report pending recovery rather than claim success");
                hardware.Proceed.Set();
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (controller.Status.Active && DateTime.UtcNow < deadline) Thread.Sleep(25);
                Require(!controller.Status.Active && hardware.Mode == "Automatic" && !hardware.ManualAfterFailedRecovery,
                    pauseDuringTransition ? "A manual transition resuming after recovery timeout must not issue another running-state write" :
                    "Feedback resuming after a recovery lock timeout must not issue another manual actuation");
            }
            finally { hardware.Proceed.Set(); }
        }
    }
    // The hardware-side protection trips at 90 C in every mode, and never below.
    static void ProtectionTripsAtNinetyCelsius(string folder)
    {
        foreach (float hot in new[] { 89.9f, 90f, 92f })
        {
            SteadyHardware hardware = new SteadyHardware();
            using (ManualRpmController controller = new ManualRpmController(hardware, hardware, "OptiPlex Example", "1.0.0", true, folder, new CapturedWatchdog()))
            {
                controller.ObserveTemperature(50); controller.SetTarget(2500);
                controller.ObserveTemperature(hot);
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (controller.Status.Active && DateTime.UtcNow < deadline) Thread.Sleep(25);
                if (hot >= 90) Require(!controller.Status.Active && controller.Status.State == "ProtectionFullSpeed", "A " + hot + " C reading did not hand the fan to BIOS");
                else Require(controller.Status.Active, "A " + hot + " C reading tripped the protection early");
            }
        }
        SteadyHardware idle = new SteadyHardware();
        using (ManualRpmController controller = new ManualRpmController(idle, idle, "OptiPlex Example", "1.0.0", true, folder, new CapturedWatchdog()))
        {
            controller.ObserveTemperature(90);
            bool refused = false;
            try { controller.SetTarget(2500); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "Taking over at 90 C with a non-full target must be refused");
            controller.ObserveTemperature(89.9f); controller.SetTarget(2500);
            Require(controller.Status.Active, "Taking over just below 90 C must work");
        }
    }
    static void SessionLocksAreProtectedFilesNotNamedMutexes(string folder)
    {
        string root = Path.Combine(folder, "locks");
        string name = "fan-watchdog-" + Guid.NewGuid().ToString("N") + ".json";
        ProtectedLocks.RootOverride = root;
        using (FanWatchdog.LockSession(Path.Combine(folder, name)))
        {
            Mutex squatted;
            Require(!Mutex.TryOpenExisting(@"Global\DellFans.Recover." + name, out squatted),
                "Session locking must not create a Global named mutex that any local user could pre-create");
            Require(File.Exists(Path.Combine(root, "recover-" + name + ".lock")), "Session lock must be a file in the injected lock root");
            bool busy = false;
            try { using (ProtectedLocks.Acquire("recover-" + name + ".lock", 200)) { } } catch (TimeoutException) { busy = true; }
            Require(busy, "A held session lock must be exclusive (FileShare.None) and time out for a second holder");
        }
        using (ProtectedLocks.Acquire("recover-" + name + ".lock", 200)) { } // released with the handle
        DirectorySecurity security = new DirectoryInfo(root).GetAccessControl();
        Require(security.AreAccessRulesProtected, "Lock directory must not inherit ACEs from its parent");
        SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Require(rule.AccessControlType == AccessControlType.Allow && (rule.IdentityReference.Equals(system) || rule.IdentityReference.Equals(admins)),
                "Lock directory ACL may only grant SYSTEM and Administrators, found " + rule.IdentityReference);
    }
    static void LockRootThatIsAReparsePointIsRefused(string folder)
    {
        string target = Path.Combine(folder, "junction-target"), link = Path.Combine(folder, "junction-root");
        Directory.CreateDirectory(target);
        ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true };
        using (Process mk = Process.Start(start)) mk.WaitForExit();
        Require(Directory.Exists(link), "Test could not create a junction");
        ProtectedLocks.RootOverride = link;
        bool refused = false;
        try { using (ProtectedLocks.Acquire("hardware.lock", 100)) { } } catch (UnauthorizedAccessException) { refused = true; }
        ProtectedLocks.RootOverride = Path.Combine(folder, "locks");
        Require(refused, "A lock directory that is a junction must be refused");
    }
    static void RecoverIsCallableOnce(string folder)
    {
        SimulatedRecoveryHardware hardware = new SimulatedRecoveryHardware { Available = true };
        string path = NewRecord(folder);
        Require(FanWatchdog.Recover(path, folder, hardware) == 0 && hardware.Mode == "Automatic" && File.Exists(path + ".recovered"),
            "--recover must restore BIOS automatic once and exit 0");
        hardware.Mode = "Manual";
        Require(FanWatchdog.Recover(path, folder, hardware) == 0 && hardware.Mode == "Manual", "An already recovered session is nothing to do");
        string ended = NewRecord(folder); File.Delete(ended + ".armed");
        Require(FanWatchdog.Recover(ended, folder, hardware) == 0 && hardware.Mode == "Manual", "A disarmed session is nothing to do");
        string failing = NewRecord(folder); hardware.Available = false;
        Require(FanWatchdog.Recover(failing, folder, hardware) != 0 && File.Exists(failing + ".recovery-failed"),
            "A failed recovery must exit non-zero so the caller retries");
        Require(FanWatchdog.Recover(Path.Combine(Path.GetTempPath(), "fan-watchdog-outside.json"), folder, hardware) == 2, "A record outside the reports folder is rejected");
        hardware.Available = true;
        using (Process self = Process.GetCurrentProcess())
        {
            string alive = Path.Combine(folder, "fan-watchdog-" + Guid.NewGuid().ToString("N") + ".json");
            ReportFiles.Write(alive, new FanWatchdogRecord { ParentId = self.Id, ParentStartTicks = self.StartTime.ToUniversalTime().Ticks,
                Model = "OptiPlex Example", Bios = "1.0.0", RestoreOverride = "Disabled", ServiceOwned = true });
            File.WriteAllText(alive + ".armed", "armed"); File.WriteAllText(alive + ".heartbeat", "alive");
            hardware.Mode = "Manual";
            int code = FanWatchdog.Recover(alive, folder, hardware);
            Require(code != 0 && code != 2 && hardware.Mode == "Manual", "A live parent with a fresh heartbeat must not be recovered; the caller keeps monitoring");
        }
    }
    static void WatchdogWaitsOnParentHandle(string folder)
    {
        ProcessStartInfo start = new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (Process parent = Process.Start(start))
        {
            try
            {
                string path = Path.Combine(folder, "fan-watchdog-" + Guid.NewGuid().ToString("N") + ".json");
                ReportFiles.Write(path, new FanWatchdogRecord { ParentId = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
                    Model = "OptiPlex Example", Bios = "1.0.0", RestoreOverride = "Disabled", ServiceOwned = true });
                File.WriteAllText(path + ".armed", "armed"); File.WriteAllText(path + ".heartbeat", "alive");
                SimulatedRecoveryHardware hardware = new SimulatedRecoveryHardware { Available = true, Mode = "Manual" };
                Task<int> watchdog = Task.Run(delegate { return FanWatchdog.Run(path, folder, hardware); });
                Thread.Sleep(1500);
                Require(!watchdog.IsCompleted && hardware.Mode == "Manual", "A live parent with a fresh heartbeat must be left alone");
                parent.Kill();
                Require(watchdog.Wait(3000) && watchdog.Result == 0 && hardware.Mode == "Automatic",
                    "The watchdog must restore automatic within about 1 s of the parent exiting");
            }
            finally { if (!parent.HasExited) parent.Kill(); }
        }
    }
    static void StarterPrefersRustWatchdog(string folder)
    {
        string dir = Path.Combine(folder, "starter"); Directory.CreateDirectory(dir);
        string record = Path.Combine(folder, "fan-watchdog-x.json"), file, args;
        FanWatchdog.WatchdogCommand(dir, record, out file, out args);
        Require(file == Path.Combine(dir, "DellFans.Cli.exe") && args == "--watchdog \"" + record + "\"", "Without DellFans.Watchdog.exe the C# monitor is the fallback");
        File.WriteAllText(Path.Combine(dir, "DellFans.Watchdog.exe"), "x");
        FanWatchdog.WatchdogCommand(dir, record, out file, out args);
        Require(file == Path.Combine(dir, "DellFans.Watchdog.exe") && args == "\"" + record + "\"", "DellFans.Watchdog.exe next to the service must be preferred");
    }
    // temperature ages follow the injected monotonic clock; the wall clock plays no part.
    static void TemperatureAgeUsesMonotonicClock(string folder)
    {
        long now = 5000;
        SteadyHardware a = new SteadyHardware();
        using (ManualRpmController c = new ManualRpmController(a, a, "OptiPlex Example", "1.0.0", true, folder, new CapturedWatchdog(), delegate { return now; }))
        {
            c.ObserveTemperature(50); now += 5900; c.SetTarget(2500);
            Require(c.Status.Active, "A sample 5.9 s old on the monotonic clock must still be usable for entry");
        }
        SteadyHardware b = new SteadyHardware(); now = 5000;
        using (ManualRpmController c = new ManualRpmController(b, b, "OptiPlex Example", "1.0.0", true, folder, new CapturedWatchdog(), delegate { return now; }))
        {
            c.ObserveTemperature(50); now += 6100;
            bool refused = false;
            try { c.SetTarget(2500); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "A sample 6.1 s old on the monotonic clock must be refused even though the wall clock barely moved");
            now = 1000; refused = false; // a backwards step is a negative age, therefore stale
            try { c.SetTarget(2500); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "A negative monotonic age must be treated as stale");
        }
        Require(FanSafetyPolicy.TemperatureUsable(50, 1000L, 8000L, 8) && !FanSafetyPolicy.TemperatureUsable(50, 1000L, 9001L, 8) && !FanSafetyPolicy.TemperatureUsable(null, 1000L, 1000L, 8),
            "Millisecond TemperatureUsable boundaries");
    }
    // Found on the real machine: the BIOS write at entry took longer than 8 s, so the loop started with an "old" sample and handed back.
    static void SlowEntryDoesNotStaleTheFirstSample(string folder)
    {
        long now = 5000;
        SteadyHardware slow = new SteadyHardware();
        slow.OnSetMode = delegate (string mode) { if (mode == "Enabled") now += 10000; };
        using (ManualRpmController c = new ManualRpmController(slow, slow, "OptiPlex Example", "1.0.0", true, folder, new CapturedWatchdog(), delegate { return now; }))
        {
            c.ObserveTemperature(50); now += 1000; c.SetTarget(2500);
            Thread.Sleep(500);
            Require(c.Status.Active && c.Status.Error == null, "An 11 s old sample right after a slow entry must not stop the loop: " + c.Status.Error);
            now += 9000; Thread.Sleep(500); // no new sample for 9 s after the loop started
            Require(!c.Status.Active && c.Status.Error != null && c.Status.Error.Contains("CPU 温度采样失效"), "Without a new sample 8 s after the start the loop still hands back");
        }
    }
    // a session that cannot be recovered three times is quarantined instead of failing every start.
    static void UnrecoverableSessionIsQuarantinedAfterThreeAttempts(string folder)
    {
        string dir = Path.Combine(folder, "f4"); Directory.CreateDirectory(dir);
        string path = NewRecord(dir);
        SimulatedRecoveryHardware hardware = new SimulatedRecoveryHardware(); // unavailable
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            bool threw = false;
            try { FanWatchdog.RecoverPreviousServiceSessions(dir, hardware); } catch (AggregateException) { threw = true; }
            Require(threw && File.Exists(path + ".armed"), "Attempt " + attempt + " must still fail and keep the session armed");
        }
        System.Collections.Generic.List<string> quarantined = FanWatchdog.RecoverPreviousServiceSessions(dir, hardware);
        Require(quarantined.Count == 1 && quarantined[0] == path && !File.Exists(path + ".armed") && File.Exists(path + ".armed.recovery-failed"),
            "The third failure must quarantine the session (armed marker renamed to *.recovery-failed) without throwing");
        Require(FanWatchdog.RecoverPreviousServiceSessions(dir, hardware).Count == 0, "A quarantined session is not retried");
        string good = NewRecord(dir); hardware.Available = true;
        Require(FanWatchdog.RecoverPreviousServiceSessions(dir, hardware).Count == 0 && hardware.Mode == "Automatic" && !File.Exists(good + ".recovery-attempts"),
            "A recoverable session still recovers and leaves no attempt counter");
    }
    // old disarmed/recovered session files and recover locks go; armed ones and fresh ones stay.
    static void OldSessionFilesArePruned(string folder)
    {
        string reports = Path.Combine(folder, "f5r"), locks = Path.Combine(folder, "f5l"); Directory.CreateDirectory(reports); Directory.CreateDirectory(locks);
        DateTime now = DateTime.UtcNow, old = now.AddDays(-8);
        Func<string, string[], string> make = delegate(string label, string[] suffixes)
        {
            string p = Path.Combine(reports, "fan-watchdog-" + label + ".json"); ReportFiles.Write(p, new FanWatchdogRecord { ParentId = 1, ParentStartTicks = 1, Model = "m", Bios = "b", RestoreOverride = "Disabled" });
            foreach (string suffix in suffixes) File.WriteAllText(p + suffix, "x");
            File.WriteAllText(Path.Combine(locks, "recover-fan-watchdog-" + label + ".json.lock"), "");
            return p;
        };
        Action<string> age = delegate(string p) { foreach (string f in Directory.GetFiles(reports, Path.GetFileName(p) + "*").Concat(Directory.GetFiles(locks, "recover-" + Path.GetFileName(p) + ".lock"))) File.SetLastWriteTimeUtc(f, old); };
        string disarmed = make("disarmed", new[] { ".heartbeat", ".ready", ".watchdog-error" }); age(disarmed);
        string recovered = make("recovered", new[] { ".armed", ".recovered" }); ReportFiles.Write(recovered + ".recovered", new { AutomaticRestored = true, Error = (string)null }); age(recovered);
        string armed = make("armed", new[] { ".armed", ".heartbeat" }); age(armed);
        string fresh = make("fresh", new[] { ".heartbeat" });
        string quarantined = make("quarantined", new[] { ".armed.recovery-failed" }); age(quarantined);
        File.WriteAllText(Path.Combine(reports, "live-latest.json"), "{}"); File.SetLastWriteTimeUtc(Path.Combine(reports, "live-latest.json"), old);
        FanWatchdog.PruneSessionFiles(reports, locks, now, 7);
        Require(!File.Exists(disarmed) && !File.Exists(disarmed + ".heartbeat") && !File.Exists(disarmed + ".ready") && !File.Exists(disarmed + ".watchdog-error"), "Old disarmed session files must be removed");
        Require(!File.Exists(recovered) && !File.Exists(recovered + ".armed") && !File.Exists(recovered + ".recovered"), "Old recovered session files must be removed");
        Require(!File.Exists(quarantined) && !File.Exists(quarantined + ".armed.recovery-failed"), "Old quarantined session files must be removed");
        Require(File.Exists(armed) && File.Exists(armed + ".armed") && File.Exists(armed + ".heartbeat"), "An armed session is never touched, however old");
        Require(File.Exists(fresh) && File.Exists(fresh + ".heartbeat"), "Files younger than 7 days stay");
        Require(File.Exists(Path.Combine(reports, "live-latest.json")), "Only fan-watchdog-* files are considered");
        Require(!File.Exists(Path.Combine(locks, "recover-fan-watchdog-disarmed.json.lock")) && !File.Exists(Path.Combine(locks, "recover-fan-watchdog-recovered.json.lock")), "Old locks of finished sessions are removed");
        Require(File.Exists(Path.Combine(locks, "recover-fan-watchdog-armed.json.lock")) && File.Exists(Path.Combine(locks, "recover-fan-watchdog-fresh.json.lock")), "Locks of armed or fresh sessions stay");
    }
    static int Main(string[] args)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DellFans-RecoveryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ProtectedLocks.RootOverride = Path.Combine(folder, "locks"); // never touch %ProgramData%\DellFans
        try
        {
            if (args.Length == 1 && args[0] == "lock-timeout")
            {
                BusySessionLeavesPendingRecoveryAndRejectsResumedActuation(folder);
                Console.WriteLine("PASS busy session remains pending and resumed feedback stays automatic");
                return 0;
            }
            if (args.Length == 1 && args[0] == "transition-timeout")
            {
                BusySessionLeavesPendingRecoveryAndRejectsResumedActuation(folder, true);
                Console.WriteLine("PASS busy transition remains pending and resumes without a running-state write");
                return 0;
            }
            UnrecoverableSessionIsQuarantinedAfterThreeAttempts(folder);
            Console.WriteLine("PASS F4 unrecoverable session is quarantined after 3 attempts");
            OldSessionFilesArePruned(folder);
            Console.WriteLine("PASS F5 old session files and locks are pruned, armed ones kept");
            TemperatureAgeUsesMonotonicClock(folder); SlowEntryDoesNotStaleTheFirstSample(folder);
            Console.WriteLine("PASS F7 temperature ages use the monotonic clock");
            ProtectionTripsAtNinetyCelsius(folder);
            Console.WriteLine("PASS hardware-side protection trips at 90 C and not below");
            SessionLocksAreProtectedFilesNotNamedMutexes(folder);
            Console.WriteLine("PASS session locks are exclusive files in a protected directory, not Global named mutexes");
            LockRootThatIsAReparsePointIsRefused(folder);
            Console.WriteLine("PASS a reparse-point lock directory is refused");
            RecoverIsCallableOnce(folder);
            Console.WriteLine("PASS --recover performs one recovery pass with retry-friendly exit codes");
            WatchdogWaitsOnParentHandle(folder);
            Console.WriteLine("PASS watchdog recovers promptly when the parent process exits");
            StarterPrefersRustWatchdog(folder);
            Console.WriteLine("PASS DellFans.Watchdog.exe is preferred over the C# fallback");
            FailedRecoveryRemainsRetryable(folder);
            Console.WriteLine("PASS failed recovery remains retryable");
            CorruptSessionDoesNotPreventOtherRecoveries(folder);
            Console.WriteLine("PASS corrupt session does not prevent other recoveries");
            SlowEntryCannotBeOvertakenByRecovery(folder);
            Console.WriteLine("PASS slow entry is not overtaken by delayed watchdog recovery");
            WatchdogExitStopsManualControl(folder);
            Console.WriteLine("PASS a dead watchdog ends manual control and hands the fan back");
            RecoveredParentCannotWriteManualAgain(folder);
            Console.WriteLine("PASS recovered parent cannot resume manual writes");
            FailedPreparationDoesNotRequestFirmwareRecovery(folder);
            Console.WriteLine("PASS failed preparation does not request firmware recovery");
            BusySessionLeavesPendingRecoveryAndRejectsResumedActuation(folder);
            Console.WriteLine("PASS busy session remains pending and resumed feedback stays automatic");
            BusySessionLeavesPendingRecoveryAndRejectsResumedActuation(folder, true);
            Console.WriteLine("PASS busy transition remains pending and resumes without a running-state write");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.ToString().Replace(Environment.NewLine, " | ")); return 1; }
        finally
        {
            string expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string actual = Path.GetFullPath(folder);
            if (actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(actual).StartsWith("DellFans-RecoveryTests-"))
            {
                string junction = Path.Combine(actual, "junction-root"); // remove the link itself, never what it points to
                if (Directory.Exists(junction)) Directory.Delete(junction, false);
                Directory.Delete(actual, true);
            }
        }
    }
}

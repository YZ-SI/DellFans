using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace DellFans
{
    public static class ServicePaths
    {
        public const string Name = "DellFans";
        public const string PipeName = "DellFans.Control.v2";
        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DellFans"); } }
        public static string Reports { get { return Path.Combine(Root, "reports"); } }
        public static string Settings { get { return Path.Combine(Root, "settings.json"); } }
        public static string Locks { get { return Path.Combine(Root, "locks"); } }
    }

    // cross-process locks are exclusive (FileShare.None) files in a directory only SYSTEM and Administrators can touch.
    // Unlike a Global\ named mutex, a local user can neither pre-create nor hold them. The handle is the lock: it dies with its process.
    public static class ProtectedLocks
    {
        public static string RootOverride; // tests only; production always uses ServicePaths.Locks
        public static string Root { get { return RootOverride ?? ServicePaths.Locks; } }
        sealed class Held : IDisposable
        {
            FileStream stream;
            public Held(FileStream stream) { this.stream = stream; }
            public void Dispose() { FileStream value = Interlocked.Exchange(ref stream, null); if (value != null) value.Dispose(); }
        }
        static SecurityIdentifier Sid(WellKnownSidType type) { return new SecurityIdentifier(type, null); }
        static string EnsureDirectory(string root)
        {
            root = Path.GetFullPath(root);
            DirectoryInfo info = new DirectoryInfo(root);
            if (!info.Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(root));
                DirectorySecurity security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    security.AddAccessRule(new FileSystemAccessRule(Sid(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                try { info.Create(security); } catch (IOException) { if (!info.Exists) throw; } // lost a creation race: validate what is there
                info.Refresh();
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("The lock directory is a reparse point and is refused: " + root);
            DirectorySecurity actual = info.GetAccessControl();
            SecurityIdentifier owner = (SecurityIdentifier)actual.GetOwner(typeof(SecurityIdentifier));
            if (!owner.Equals(Sid(WellKnownSidType.LocalSystemSid)) && !owner.Equals(Sid(WellKnownSidType.BuiltinAdministratorsSid)))
                throw new UnauthorizedAccessException("The lock directory is owned by " + owner.Value + ", not SYSTEM or Administrators.");
            const FileSystemRights writing = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
            foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                SecurityIdentifier who = (SecurityIdentifier)rule.IdentityReference;
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writing) != 0 &&
                    !who.Equals(Sid(WellKnownSidType.LocalSystemSid)) && !who.Equals(Sid(WellKnownSidType.BuiltinAdministratorsSid)))
                    throw new UnauthorizedAccessException("The lock directory grants write access to " + who.Value + ".");
            }
            return root;
        }
        // Throws TimeoutException while another handle holds the lock; other failures (including a refused directory) propagate.
        public static IDisposable Acquire(string name, int timeoutMs)
        {
            if (Path.GetFileName(name) != name) throw new ArgumentException("Lock name must be a file name.");
            string path = Path.Combine(EnsureDirectory(Root), name);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                try { return new Held(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1)); }
                catch (IOException error)
                {
                    int code = error.HResult & 0xFFFF; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
                    if (code != 32 && code != 33) throw;
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Lock is held by another process: " + name);
                    Thread.Sleep(25);
                }
            }
        }
    }

    // The RPM window and start temperature a mode works inside.
    public sealed class ModeLimits
    {
        public int Min { get; set; }
        public int Max { get; set; }
        public double StartC { get; set; }
    }

    public static class FanModes
    {
        public const string Auto = "auto", Quiet = "quiet", Balanced = "balanced", Performance = "performance", Custom = "custom";
        public static readonly string[] All = { Quiet, Balanced, Performance, Custom, Auto };
        public static bool IsValid(string mode) { return mode != null && Array.IndexOf(All, mode) >= 0; }
        // null for auto: BIOS control, the service never takes over.
        public static ModeLimits Limits(string mode, int? customMinimum)
        {
            switch (mode)
            {
                case Auto: return null;
                case Quiet: return new ModeLimits { Min = 1500, Max = 3000, StartC = 55 };
                case Balanced: return new ModeLimits { Min = 2500, Max = RpmRange.Maximum, StartC = 65 };
                case Performance: return new ModeLimits { Min = 4000, Max = RpmRange.Maximum, StartC = 65 };
                case Custom:
                    if (!customMinimum.HasValue) throw new ArgumentException("A minimum RPM is required for custom mode.");
                    RpmRange.Validate(customMinimum.Value);
                    return new ModeLimits { Min = customMinimum.Value, Max = RpmRange.Maximum, StartC = 65 };
                default: throw new ArgumentException("Unsupported mode.");
            }
        }
    }

    public sealed class FloorSettings
    {
        public int SchemaVersion { get; set; }
        public string Mode { get; set; }
        // Derived: any mode but auto takes over the fan. Serialized for older readers.
        public bool Enabled { get { return Mode != FanModes.Auto; } }
        // The custom mode's minimum; kept while another mode is active so switching back remembers it.
        public int? MinimumRpm { get; set; }
        public string LastFault { get; set; }
        // First run (no settings file): balanced. Control still starts only after this computer passes the capability check.
        public static FloorSettings Defaults()
        { return new FloorSettings { SchemaVersion = 2, Mode = FanModes.Balanced }; }
        public FloorSettings Copy()
        { return new FloorSettings { SchemaVersion = SchemaVersion, Mode = Mode, MinimumRpm = MinimumRpm, LastFault = LastFault }; }
        public ModeLimits Limits() { return FanModes.Limits(Mode, MinimumRpm); }
        public void Validate()
        {
            if (SchemaVersion != 2) throw new InvalidDataException("Unsupported settings schema.");
            if (!FanModes.IsValid(Mode)) throw new InvalidDataException("Unsupported mode.");
            if (MinimumRpm.HasValue) RpmRange.Validate(MinimumRpm.Value);
            if (Mode == FanModes.Custom && !MinimumRpm.HasValue) throw new InvalidDataException("A minimum RPM is required before enabling custom mode.");
            if (LastFault != null && LastFault.Length > 2048) throw new InvalidDataException("Fault description is too long.");
        }
    }

    public static class FloorSettingsStore
    {
        static readonly object gate = new object();
        // What a settings file holds; Validate rejects any schema but the current one (the service then starts in auto).
        sealed class Stored
        {
            public int SchemaVersion { get; set; }
            public bool Enabled { get; set; }
            public string Mode { get; set; }
            public int? MinimumRpm { get; set; }
            public string LastFault { get; set; }
        }
        public static FloorSettings Load()
        { return Load(ServicePaths.Settings); }
        public static FloorSettings Load(string path)
        {
            lock (gate)
            {
                if (!File.Exists(path)) return FloorSettings.Defaults();
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Settings file exceeds its size limit.");
                Stored stored = MiniJson.Deserialize<Stored>(File.ReadAllText(path), 65536);
                if (stored == null) throw new InvalidDataException("Settings are empty.");
                FloorSettings value = new FloorSettings { SchemaVersion = stored.SchemaVersion, Mode = stored.Mode, MinimumRpm = stored.MinimumRpm, LastFault = stored.LastFault };
                value.Validate(); return value;
            }
        }
        public static void Save(FloorSettings value)
        { Save(ServicePaths.Settings, value); }
        public static void Save(string path, FloorSettings value)
        {
            lock (gate)
            {
                value.Validate();
                if (File.Exists(path))
                {
                    try { Load(path); File.Copy(path, path + ".previous", true); }
                    catch (Exception) { } // a corrupt or wrong-typed old file must not block writing a valid new one
                }
                ReportFiles.Write(path, value);
            }
        }
    }

    // Pure: the same series of readings and clock values always gives the same targets.
    // Above the start temperature the target rises 50 RPM per second to the mode maximum; below start - 3 C for 20 s it falls
    // 100 RPM per second to the mode minimum; in between it holds. 90 C (protection or control reading) is full speed at once,
    // for at least 10 s, and lasts until both readings are 85 C or lower. On release the target is held (not snapped to the mode
    // maximum) and only the normal cooling rule brings it down, so a capped mode cannot hunt between 90 and 85 C.
    public sealed class ModeController
    {
        const double MaximumGapSeconds = 2; // a long pause (service busy, resume) must not turn into a large jump
        public const double MinimumEmergencySeconds = 10;
        int? target;
        double? lastSeconds, coolingSince, emergencySince;
        double accrued;
        bool heldAboveMaximum, haveLimits;
        int knownMin, knownMax; double knownStart;
        public bool Emergency { get; private set; }
        public void Reset() { target = null; lastSeconds = coolingSince = emergencySince = null; accrued = 0; Emergency = false; heldAboveMaximum = haveLimits = false; }
        // Where a takeover starts: full (mode maximum) when already hot, else the fan's measured speed inside the mode window,
        // else the mode minimum.
        public static int StartRpm(ModeLimits limits, int? measuredRpm, double? controlC = null)
        {
            if (controlC.HasValue && !double.IsNaN(controlC.Value) && (controlC.Value >= limits.StartC + 15 || controlC.Value >= 80)) return limits.Max;
            if (controlC.HasValue && controlC.Value < limits.StartC - 3) return limits.Min; // already cool: no reason to keep a higher speed
            return measuredRpm.HasValue ? Math.Max(limits.Min, Math.Min(limits.Max, measuredRpm.Value)) : limits.Min;
        }
        // No effect once a target exists, so only the first update of a takeover starts from the seed.
        public void Seed(int rpm) { if (!target.HasValue) target = rpm; }
        public void Seed(ModeLimits limits, int? measuredRpm, double? controlC = null)
        { if (limits != null && (measuredRpm.HasValue || controlC.HasValue)) Seed(StartRpm(limits, measuredRpm, controlC)); }
        // The hottest usable reading: a missing, NaN or implausible protection value falls back to the control value.
        public static double Hottest(double controlC, double? protectionC)
        {
            return protectionC.HasValue && !double.IsNaN(protectionC.Value) && protectionC.Value > 0 && protectionC.Value <= 150 ? Math.Max(controlC, protectionC.Value) : controlC;
        }
        public int Update(ModeLimits limits, double controlC, double protectionC, double nowSeconds)
        {
            if (limits == null) throw new ArgumentNullException("limits", "Auto mode has no target.");
            foreach (double reading in new[] { controlC, nowSeconds })
                if (double.IsNaN(reading) || double.IsInfinity(reading)) throw new ArgumentException("Valid temperatures and time are required.");
            double hottest = Hottest(controlC, protectionC);
            if (haveLimits && (limits.Min != knownMin || limits.Max != knownMax || limits.StartC != knownStart))
            {
                coolingSince = null; accrued = 0;
                // A mode switch while already cool lands on the new minimum at once instead of inheriting the old target and the 20 s wait.
                if (controlC < limits.StartC - 3) { target = limits.Min; heldAboveMaximum = false; }
            }
            haveLimits = true; knownMin = limits.Min; knownMax = limits.Max; knownStart = limits.StartC;
            if (hottest >= FanSafetyPolicy.EmergencyC) { if (!Emergency) emergencySince = nowSeconds; Emergency = true; }
            else if (Emergency && hottest <= FanSafetyPolicy.ReleaseC && nowSeconds - emergencySince.Value >= MinimumEmergencySeconds)
            { Emergency = false; heldAboveMaximum = true; }
            if (Emergency) { target = RpmRange.Maximum; lastSeconds = nowSeconds; accrued = 0; coolingSince = null; return RpmRange.Maximum; }
            int current = Math.Max(limits.Min, target ?? limits.Min);
            if (heldAboveMaximum && current <= limits.Max) heldAboveMaximum = false;
            if (!heldAboveMaximum) current = Math.Min(limits.Max, current);
            double gap = lastSeconds.HasValue ? Math.Max(0, Math.Min(MaximumGapSeconds, nowSeconds - lastSeconds.Value)) : 0;
            lastSeconds = nowSeconds; accrued += gap;
            bool cooling = controlC < limits.StartC - 3;
            if (!cooling) coolingSince = null; else if (!coolingSince.HasValue) coolingSince = nowSeconds;
            int steps = (int)Math.Floor(accrued + 1e-9); accrued -= steps; if (accrued < 0) accrued = 0;
            for (int i = 0; i < steps; i++)
            {
                if (controlC > limits.StartC) { if (!heldAboveMaximum) current = Math.Min(limits.Max, current + 50); }
                else if (cooling && nowSeconds - coolingSince.Value >= 20) { current = Math.Max(limits.Min, current - 100); if (current <= limits.Max) heldAboveMaximum = false; }
            }
            target = current; return current;
        }
    }

    // A temperature-only handback is not a fault: control resumes once a valid reading has been seen for 10 s without a gap.
    public sealed class TemperatureRecovery
    {
        public const double ResumeAfterSeconds = 10;
        double? validSince;
        public bool HandedBack { get; private set; }
        public void HandBack() { HandedBack = true; validSince = null; }
        public void Clear() { HandedBack = false; validSince = null; }
        public void Observe(bool valid, double nowSeconds) { if (!valid) validSince = null; else if (!validSince.HasValue) validSince = nowSeconds; }
        public bool MayResume(double nowSeconds) { return !HandedBack || (validSince.HasValue && nowSeconds - validSince.Value >= ResumeAfterSeconds); }
    }

    // Handing the fan back to BIOS must not be forgotten: a failed release is retried every tick and warns after repeated failures.
    public sealed class AutomaticRelease
    {
        public const int WarnAfter = 3;
        public bool Pending { get; private set; }
        public int Failures { get; private set; }
        public string Error { get; private set; }
        public void Request() { Pending = true; }
        // True when the fan is back with BIOS (or nothing was pending).
        public bool Attempt(Action release)
        {
            if (!Pending) return true;
            try { release(); Pending = false; Failures = 0; Error = null; return true; }
            catch (Exception e) { Failures++; Error = e.Message; return false; }
        }
        public bool Failing { get { return Failures >= WarnAfter; } }
    }

    // Controlling at 1 s needs fresh sensor samples every second, not a rebuilt full snapshot: that is published every 2 s, or every
    // second while a client is looking, and at once when something changed.
    public sealed class PublishSchedule
    {
        public const double IdleSeconds = 2;
        double last = double.MinValue;
        public int Rebuilds { get; private set; }
        public bool Due(double nowSeconds, bool clientViewing, bool changed)
        {
            return changed || clientViewing || nowSeconds - last >= IdleSeconds - 1e-9;
        }
        public void Built(double nowSeconds) { last = nowSeconds; Rebuilds++; }
    }

    public sealed class BackgroundStatus
    {
        public string State { get; set; }
        public string Message { get; set; }
        public FloorSettings Settings { get; set; }
        public string Mode { get; set; }
        public ModeLimits ModeLimits { get; set; }
        public int? EffectiveTargetRpm { get; set; }
        public double? ControlTemperatureC { get; set; }
        public bool ProbeActive { get; set; }
        public int ProcessId { get; set; }
        public string SensorSource { get; set; }
        public string SensorDriverState { get; set; }
        // Capability probe state for this Model + BIOS.
        public CapabilityStatus Capability { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DellFans
{
    // ARCHITECTURE section 7: an untested model may only be controlled after (1) read-only checks pass and (2) the user agreed to a
    // 15 s full-speed test that shows the fan responds and that BIOS automatic can be restored. The result is kept per Model + BIOS.
    public static class CapabilityStates
    {
        public const string Unknown = "Unknown", ReadPassed = "ReadPassed", Passed = "Passed", Failed = "Failed", Unsupported = "Unsupported";
    }

    public sealed class CapabilityCheck
    {
        public string Name { get; set; }
        public string Result { get; set; } // Pass, Fail, Warn (informational, never blocks) or Skipped
        public string Error { get; set; }
        public string Detail { get; set; }
    }

    // What capability.json holds.
    public sealed class CapabilityRecord
    {
        public string Model { get; set; }
        public string BiosVersion { get; set; }
        public string PassedAtUtc { get; set; }
        public List<CapabilityCheck> Checks { get; set; }
    }

    // Snapshot.Background.Capability (add-only).
    public sealed class CapabilityStatus
    {
        public string State { get; set; }
        public string Model { get; set; }
        public string BiosVersion { get; set; }
        public string ReportPath { get; set; }
        public List<CapabilityCheck> Checks { get; set; }
        public bool WriteCheckRequired { get; set; } // ReadPassed: the 15 s full-speed test still needs the user's consent
    }

    // Everything the probe touches on the machine, so the probe is testable with fakes and never writes in tests.
    public interface ICapabilityHardware
    {
        string Model { get; }
        string BiosVersion { get; }
        string WindowsBuild { get; }
        string CpuName { get; }
        void CheckDellWmi();             // Dell WMI classes and SMBIOS fan tokens; NotSupportedException = not a Dell system
        int ReadSmmRpm();                // SMM fan RPM read (read-only)
        string ReadBiosMode();           // FanCtrlOvrd: "Enabled" (full speed) or "Disabled" (BIOS automatic)
        string CpuTemperatureSource();   // throws when no usable CPU temperature exists
        int StorageTemperatureCount();   // informational
        IDisposable ArmWatchdog();       // restoring BIOS automatic must not depend on the service surviving
        void SetFullSpeed();
        void RestoreAutomatic();
        void Wait(int milliseconds);
    }

    public sealed class CapabilityStore
    {
        readonly string root;
        readonly string[] redactions;
        public CapabilityStore(string root) : this(root, new[] { Environment.MachineName, Environment.UserName }) { }
        public CapabilityStore(string root, string[] redactions)
        { this.root = root; this.redactions = redactions.Where(r => !string.IsNullOrEmpty(r) && r.Length >= 2).ToArray(); }
        public string RecordPath { get { return Path.Combine(root, "capability.json"); } }
        public string ReportsDirectory { get { return Path.Combine(root, "reports"); } }
        // The persisted pass for exactly this Model + BIOS, or null (missing, unreadable, other model, other BIOS).
        public CapabilityRecord Load(string model, string bios)
        {
            try
            {
                if (model == null || bios == null || !File.Exists(RecordPath) || new FileInfo(RecordPath).Length > 65536) return null;
                CapabilityRecord record = MiniJson.Deserialize<CapabilityRecord>(File.ReadAllText(RecordPath), 65536);
                return record != null && record.Model == model && record.BiosVersion == bios && !string.IsNullOrEmpty(record.PassedAtUtc) && WriteTested(record) ? record : null;
            }
            catch (Exception) { return null; }
        }
        public bool Allows(string model, string bios) { return Load(model, bios) != null; }
        // Every machine must have answered the full-speed test itself; a record without that pass is not a pass.
        static bool WriteTested(CapabilityRecord record)
        { return record.Checks != null && record.Checks.Any(c => c.Name == CapabilityProbe.WriteCheckName && c.Result == "Pass"); }
        public void SavePassed(CapabilityRecord record) { ReportFiles.Write(RecordPath, record); }
        // Personal data stays out of reports: the machine and user names are replaced wherever an error text mentions them.
        public string Redact(string text)
        {
            if (text == null) return null;
            foreach (string secret in redactions) text = ReplaceIgnoreCase(text, secret, "<redacted>");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(serial(?:\s*number)?|service\s*tag|asset\s*tag|s/n)\b[\s:=#]*[A-Za-z0-9\-]{4,}", "$1 <redacted>");
            return text.Length > 600 ? text.Substring(0, 600) : text;
        }
        static string ReplaceIgnoreCase(string text, string find, string replacement)
        {
            int at;
            while ((at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase)) >= 0) text = text.Substring(0, at) + replacement + text.Substring(at + find.Length);
            return text;
        }
        public string WriteReport(string model, string bios, string windowsBuild, string cpuName, string sensorSource, List<CapabilityCheck> checks, DateTime nowUtc)
        {
            string path = Path.Combine(ReportsDirectory, "capability-report-" + nowUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".json");
            ReportFiles.Write(path, new CapabilityReport { Model = Redact(model), BiosVersion = Redact(bios), WindowsBuild = Redact(windowsBuild), CpuName = Redact(cpuName),
                SensorSource = Redact(sensorSource), CreatedAtUtc = nowUtc.ToString("o", CultureInfo.InvariantCulture),
                Checks = checks.Select(c => new CapabilityCheck { Name = c.Name, Result = c.Result, Error = Redact(c.Error), Detail = Redact(c.Detail) }).ToList() });
            return path;
        }
    }

    public sealed class CapabilityReport
    {
        public string Model { get; set; }
        public string BiosVersion { get; set; }
        public string WindowsBuild { get; set; }
        public string CpuName { get; set; }
        public string SensorSource { get; set; }
        public string CreatedAtUtc { get; set; }
        public List<CapabilityCheck> Checks { get; set; }
    }

    public sealed class CapabilityProbe
    {
        public const string WriteCheckName = "Full-speed response test";
        public const int FullSpeedSeconds = 15, RequiredRpmRise = 300;
        readonly ICapabilityHardware hardware;
        readonly CapabilityStore store;
        readonly Func<DateTime> now;
        public CapabilityProbe(ICapabilityHardware hardware, CapabilityStore store, Func<DateTime> utcNow)
        { this.hardware = hardware; this.store = store; this.now = utcNow ?? (Func<DateTime>)(() => DateTime.UtcNow); }

        static CapabilityCheck Run(string name, Func<string> action, bool informational = false)
        {
            try { return new CapabilityCheck { Name = name, Result = "Pass", Detail = action() }; }
            catch (Exception e) { return new CapabilityCheck { Name = name, Result = informational ? "Warn" : "Fail", Error = e.GetType().Name + ": " + e.Message }; }
        }

        // Cheap and write-free: a stored pass for this Model + BIOS answers at once.
        public CapabilityStatus Current()
        {
            string model = hardware.Model, bios = hardware.BiosVersion;
            CapabilityRecord record = store.Load(model, bios);
            if (record != null) return new CapabilityStatus { State = CapabilityStates.Passed, Model = model, BiosVersion = bios, Checks = record.Checks };
            return new CapabilityStatus { State = CapabilityStates.Unknown, Model = model, BiosVersion = bios };
        }

        // Read-only checks. Hardware that reads clean is ReadPassed until the consented full-speed test (Test) passes.
        public CapabilityStatus ReadChecks()
        {
            CapabilityStatus current = Current();
            if (current.State == CapabilityStates.Passed) return current;
            string model = hardware.Model, bios = hardware.BiosVersion;
            List<CapabilityCheck> checks = new List<CapabilityCheck>();
            string source = null;
            CapabilityCheck wmi = Run("Dell WMI classes and tokens", delegate { hardware.CheckDellWmi(); return "present"; });
            checks.Add(wmi);
            checks.Add(Run("SMM fan RPM read", delegate { return hardware.ReadSmmRpm().ToString(CultureInfo.InvariantCulture) + " RPM"; }));
            checks.Add(Run("BIOS fan mode read", delegate { string mode = hardware.ReadBiosMode(); if (mode != "Enabled" && mode != "Disabled") throw new InvalidDataException("unexpected value"); return mode; }));
            checks.Add(Run("CPU temperature source", delegate { source = hardware.CpuTemperatureSource(); return source; }));
            checks.Add(Run("Storage temperature", delegate { int count = hardware.StorageTemperatureCount(); if (count < 1) throw new InvalidDataException("none readable"); return count + " device(s)"; }, true));
            if (checks.Any(c => c.Result == "Fail"))
            {
                bool unsupported = wmi.Result == "Fail" && wmi.Error.StartsWith("NotSupportedException", StringComparison.Ordinal);
                return Fail(unsupported ? CapabilityStates.Unsupported : CapabilityStates.Failed, checks, source);
            }
            return new CapabilityStatus { State = CapabilityStates.ReadPassed, Model = model, BiosVersion = bios, Checks = checks, WriteCheckRequired = true };
        }

        // The write step. Only ever called with the user's explicit consent; with fakes in tests, with the real fan only on request.
        public CapabilityStatus Test(bool consent)
        {
            CapabilityStatus read = ReadChecks();
            if (read.State != CapabilityStates.ReadPassed) return read;
            if (!consent) throw new InvalidOperationException("The full-speed test needs the user's consent.");
            List<CapabilityCheck> checks = new List<CapabilityCheck>(read.Checks);
            CapabilityCheck write = new CapabilityCheck { Name = WriteCheckName, Result = "Fail" };
            checks.Add(write);
            CapabilityCheck restore = new CapabilityCheck { Name = "BIOS automatic restore", Result = "Fail", Error = "not reached" };
            checks.Add(restore);
            string source = null;
            try
            {
                using (hardware.ArmWatchdog())
                {
                    int before = hardware.ReadSmmRpm();
                    try
                    {
                        hardware.SetFullSpeed();
                        hardware.Wait(FullSpeedSeconds * 1000);
                        int after = hardware.ReadSmmRpm();
                        write.Detail = before + " -> " + after + " RPM";
                        if (after - before >= RequiredRpmRise) write.Result = "Pass"; else write.Error = "RPM rose by " + (after - before) + ", needs " + RequiredRpmRise;
                    }
                    catch (Exception e) { write.Error = e.GetType().Name + ": " + e.Message; }
                    finally
                    {
                        try
                        {
                            hardware.RestoreAutomatic();
                            string mode = hardware.ReadBiosMode();
                            if (mode == "Disabled") { restore.Result = "Pass"; restore.Error = null; restore.Detail = "readback Disabled"; }
                            else restore.Error = "readback was " + mode;
                        }
                        catch (Exception e) { restore.Error = e.GetType().Name + ": " + e.Message; }
                    }
                }
            }
            catch (Exception e) { if (write.Error == null) write.Error = "watchdog: " + e.GetType().Name + ": " + e.Message; }
            try { source = hardware.CpuTemperatureSource(); } catch (Exception) { }
            if (write.Result == "Pass" && restore.Result == "Pass") return Pass(checks);
            return Fail(CapabilityStates.Failed, checks, source);
        }

        CapabilityStatus Pass(List<CapabilityCheck> checks)
        {
            CapabilityRecord record = new CapabilityRecord { Model = hardware.Model, BiosVersion = hardware.BiosVersion, PassedAtUtc = now().ToString("o", CultureInfo.InvariantCulture), Checks = checks };
            store.SavePassed(record);
            return new CapabilityStatus { State = CapabilityStates.Passed, Model = record.Model, BiosVersion = record.BiosVersion, Checks = checks };
        }

        CapabilityStatus Fail(string state, List<CapabilityCheck> checks, string source)
        {
            string path = null; string reportError = null;
            try { path = store.WriteReport(hardware.Model, hardware.BiosVersion, hardware.WindowsBuild, hardware.CpuName, source, checks, now()); }
            catch (Exception e) { reportError = e.Message; }
            return new CapabilityStatus { State = state, Model = hardware.Model, BiosVersion = hardware.BiosVersion, ReportPath = path, Checks = reportError == null ? checks :
                checks.Concat(new[] { new CapabilityCheck { Name = "Report file", Result = "Fail", Error = store.Redact(reportError) } }).ToList() };
        }
    }
}

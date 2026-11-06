using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace DellFans
{
    public sealed class SensorRow
    {
        public string Id { get; set; }
        public string Device { get; set; }
        public string HardwareType { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Role { get; set; }
        public string Unit { get; set; }
        public float? Value { get; set; }
        public float? Minimum { get; set; }
        public float? Maximum { get; set; }
        public string Source { get; set; }
        public string Quality { get; set; }
        // Fan, CPU and storage rows carry the UTC time of their own acquisition; the dock judges freshness by it.
        public string PolledAtUtc { get; set; }
    }

    public sealed class Snapshot
    {
        public int SchemaVersion { get { return 1; } }
        public string PolledAtUtc { get; set; }
        public string Model { get; set; }
        public string BiosVersion { get; set; }
        public string FanMode { get; set; }
        public double? FullSpeedSecondsRemaining { get; set; }
        public long PollDurationMilliseconds { get; set; }
        public List<SensorRow> Sensors { get; set; }
        public List<string> Warnings { get; set; }
        public Dictionary<string, string> Coverage { get; set; }
        public Dictionary<string, object> ControlCapabilities { get; set; }
        public ManualFanStatus ManualControl { get; set; }
        public int FirmwareConfigurationWriteCount { get; set; }
        public ResourceStatus Resources { get; set; }
        public BackgroundStatus Background { get; set; }
        // Sensor layer: control = 1 s mean, protection = raw max over 1 s; both null when the CPU source is stale or absent.
        public double? CpuControlTemperatureC { get; set; }
        public double? CpuProtectionTemperatureC { get; set; }
        public string SensorSource { get; set; }
        public string SensorDriverState { get; set; }
        public Dictionary<string, double> SensorTimings { get; set; }
        // True only on summary replies; older services never send it, so their full rows read as complete.
        public bool SummaryOnly { get; set; }
        // IPC replies carry only the rows the panel and dock display; the details page asks for all rows.
        public Snapshot Summary()
        {
            Snapshot copy = (Snapshot)MemberwiseClone();
            copy.SummaryOnly = true;
            if (Sensors != null) copy.Sensors = Sensors.Where(SensorRoles.IsSummary).ToList();
            return copy;
        }
    }

    public static class SensorRoles
    {
        public static bool IsSummary(SensorRow row)
        {
            return row.Type == "Fan" || (row.Type == "Temperature" && (row.HardwareType == "Storage" ||
                (row.HardwareType == "Cpu" && (row.Name == "CPU Package" || row.Name == "Core Max" || row.Name == "Core Average"))));
        }
        public static string Classify(string type, string name)
        {
            if (type != "Temperature") return "Measurement";
            if (name.IndexOf("Distance to TjMax", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ThermalMargin";
            if (name.IndexOf("Warning Temperature", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Critical Temperature", StringComparison.OrdinalIgnoreCase) >= 0)
                return "TemperatureLimit";
            if (name == "Core Max" || name == "Core Average") return "DerivedTemperature";
            return "Temperature";
        }

        public static bool IsCurrentTemperature(SensorRow sensor)
        {
            return sensor.Type == "Temperature" &&
                (sensor.Role == "Temperature" || sensor.Role == "DerivedTemperature");
        }

        public static string UnitFor(string type)
        {
            switch (type)
            {
                case "Temperature": return "°C";
                case "Fan": return "RPM";
                case "Load": case "Control": case "Level": return "%";
                case "Power": return "W";
                case "Clock": return "MHz";
                case "Voltage": return "V";
                default: return "";
            }
        }
    }

    public sealed class DellToken
    {
        public ushort Id;
        public ushort Location;
        public ushort Value;
    }

    public static class DellTokenTable
    {
        public static List<DellToken> Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 6) throw new InvalidDataException("SMBIOS table is empty.");
            List<DellToken> tokens = new List<DellToken>();
            int offset = 0;
            while (offset + 4 <= bytes.Length)
            {
                int type = bytes[offset], length = bytes[offset + 1];
                if (length < 4 || offset + length > bytes.Length)
                    throw new InvalidDataException("Invalid SMBIOS structure boundary.");
                if (type == 0xDA)
                {
                    if (length < 11) throw new InvalidDataException("Truncated Dell type 0xDA.");
                    for (int pos = offset + 11; pos + 6 <= offset + length; pos += 6)
                    {
                        ushort id = BitConverter.ToUInt16(bytes, pos);
                        if (id == 0xFFFF) break;
                        if (id != 0) tokens.Add(new DellToken { Id = id,
                            Location = BitConverter.ToUInt16(bytes, pos + 2),
                            Value = BitConverter.ToUInt16(bytes, pos + 4) });
                    }
                }
                offset += length;
                while (offset + 1 < bytes.Length && !(bytes[offset] == 0 && bytes[offset + 1] == 0)) offset++;
                if (offset + 1 >= bytes.Length) throw new InvalidDataException("Unterminated SMBIOS strings.");
                offset += 2;
                if (type == 127) return tokens;
            }
            throw new InvalidDataException("SMBIOS end marker not found.");
        }
    }

    public interface IFanBackend
    {
        string ReadMode();
        void SetMode(string mode);
    }

    // The only firmware write exposed by this application is FanCtrlOvrd.
    public sealed class DellFanBackend : IFanBackend, IDisposable
    {
        int configurationWriteCount;
        public int ConfigurationWriteCount { get { return configurationWriteCount; } }
        const string BiosNamespace = @"root\dcim\sysman\biosattributes";
        ManagementObject bfn;
        ManagementClass bdat;
        readonly List<DellToken> tokens;
        public string Model { get; private set; }
        public string BiosVersion { get; private set; }

        public DellFanBackend()
        {
            using (ManagementObjectSearcher s = Query(@"root\cimv2", "SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
            using (ManagementObjectCollection results = s.Get())
            foreach (ManagementObject o in results)
            using (o)
            {
                if (Convert.ToString(o["Manufacturer"]).IndexOf("Dell", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new NotSupportedException("This tool supports Dell systems only.");
                Model = Convert.ToString(o["Model"]);
            }
            using (ManagementObjectSearcher s = Query(@"root\cimv2", "SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            using (ManagementObjectCollection results = s.Get())
            foreach (ManagementObject o in results) using (o) BiosVersion = Convert.ToString(o["SMBIOSBIOSVersion"]);
            tokens = new List<DellToken>();
            using (ManagementObjectSearcher s = Query(@"root\wmi", "SELECT SMBiosData FROM MSSmBios_RawSMBiosTables"))
            using (ManagementObjectCollection results = s.Get())
            foreach (ManagementObject o in results) using (o) tokens.AddRange(DellTokenTable.Parse((byte[])o["SMBiosData"]));
            using (ManagementObjectSearcher s = Query(@"root\wmi", "SELECT * FROM BFn"))
            using (ManagementObjectCollection results = s.Get())
            foreach (ManagementObject o in results)
            {
                if (Convert.ToBoolean(o["Active"]) && Convert.ToString(o["InstanceName"]) == @"ACPI\PNP0C14\0_0")
                { bfn = o; break; }
                o.Dispose();
            }
            if (bfn != null) bdat = new ManagementClass(@"root\wmi", "BDat", null);
        }

        // Capability read check: the SMBIOS fan tokens, the BFn RPM interface and the BIOS fan attribute are all present.
        public void Verify()
        {
            if (tokens.Count == 0) throw new InvalidDataException("no Dell SMBIOS tokens");
            if (!tokens.Any(t => t.Id == 0xF600)) throw new InvalidDataException("the Dell fan token 0xF600 is missing");
            if (bfn == null) throw new NotSupportedException("the Dell BFn WMI interface is unavailable");
            ReadMode();
        }

        public static ManagementObjectSearcher Query(string scope, string query)
        {
            return new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(8) });
        }

        public string ReadMode()
        {
            using (ManagementObjectSearcher s = Query(BiosNamespace, "SELECT * FROM EnumerationAttribute WHERE AttributeName='FanCtrlOvrd'"))
            using (ManagementObjectCollection results = s.Get())
            foreach (ManagementObject o in results) using (o)
            {
                string value = Convert.ToString(o["CurrentValue"]);
                if (value != "Enabled" && value != "Disabled") throw new InvalidDataException("Unexpected fan override value.");
                return value;
            }
            throw new NotSupportedException("FanCtrlOvrd is not advertised by this BIOS.");
        }

        public void SetMode(string mode)
        {
            if (mode != "Enabled" && mode != "Disabled")
                throw new ArgumentException("Only BIOS automatic and full speed are supported.");
            string current = ReadMode();
            if (current == mode) return;
            using (ManagementObjectSearcher s = Query(BiosNamespace, "SELECT * FROM BIOSAttributeInterface"))
            using (ManagementObjectCollection results = s.Get())
            {
                foreach (ManagementObject o in results) using (o)
                using (ManagementBaseObject input = o.GetMethodParameters("SetAttribute"))
                {
                    input["SecType"] = (uint)0;
                    input["SecHndCount"] = (uint)0;
                    input["SecHandle"] = new byte[0];
                    input["AttributeName"] = "FanCtrlOvrd";
                    input["AttributeValue"] = mode;
                    System.Threading.Interlocked.Increment(ref configurationWriteCount);
                    using (ManagementBaseObject output = o.InvokeMethod("SetAttribute", input,
                        new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) }))
                    {
                        int status = Convert.ToInt32(output["Status"]);
                        if (status != 0) throw new InvalidOperationException("Dell BIOS SetAttribute status " + status +
                            " (3=access denied/password, 4=not supported).");
                    }
                    if (ReadMode() != mode) throw new InvalidOperationException("Fan mode readback did not match the request.");
                    return;
                }
            }
            throw new NotSupportedException("BIOSAttributeInterface is unavailable.");
        }

        public List<SensorRow> ReadFans()
        {
            if (bfn == null) throw new NotSupportedException("Dell BFn fan RPM interface is unavailable.");
            List<SensorRow> rows = new List<SensorRow>();
            foreach (DellToken token in tokens.Where(t => t.Id == 0xF600 || t.Id == 0xF610).GroupBy(t => t.Id).Select(g => g.First()))
            {
                byte[] buffer = new byte[32768];
                Buffer.BlockCopy(BitConverter.GetBytes((uint)token.Location), 0, buffer, 4, 4);
                for (int i = 20; i < 36; i += 4) Buffer.BlockCopy(BitConverter.GetBytes(uint.MaxValue), 0, buffer, i, 4);
                using (ManagementBaseObject data = bdat.CreateInstance())
                using (ManagementBaseObject input = bfn.GetMethodParameters("DoBFn"))
                {
                    data["Bytes"] = buffer;
                    input["Data"] = data;
                    using (ManagementBaseObject output = bfn.InvokeMethod("DoBFn", input,
                        new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(8) }))
                    using (ManagementBaseObject result = (ManagementBaseObject)output["Data"])
                    {
                        byte[] returned = (byte[])result["Bytes"];
                        if (returned.Length < 36) throw new InvalidDataException("Truncated RPM response.");
                        int status = BitConverter.ToInt32(returned, 20);
                        if (status != 0) throw new InvalidOperationException("Fan RPM query status " + status);
                        uint rpm = BitConverter.ToUInt32(returned, 24);
                        if (rpm > 100000) throw new InvalidDataException("Invalid fan RPM.");
                        rows.Add(new SensorRow { Id = "dell/fan/" + token.Id.ToString("X4"), Device = Model,
                            HardwareType = "DellFirmware", Name = token.Id == 0xF600 ? "System / CPU Fan" : "Fan 2",
                            Type = "Fan", Role = "Measurement", Unit = "RPM", Value = rpm,
                            Source = "Dell BFn / SMBIOS", Quality = "OK", PolledAtUtc = DateTime.UtcNow.ToString("o") });
                    }
                }
            }
            return rows;
        }

        public void Dispose() { if (bfn != null) bfn.Dispose(); if (bdat != null) bdat.Dispose(); }
    }

    // BIOS full speed (FanCtrlOvrd = Enabled) survives a service crash, so every persistent full-speed write is preceded by this marker
    // and the marker is removed only after the restore was read back. A service that starts and finds it restores BIOS automatic once.
    // Full speed is the fail-safe direction (loud, never hot); this marker only makes sure it does not stay on silently.
    public sealed class FullOverrideMarker
    {
        public static string DefaultPath { get { return Path.Combine(ServicePaths.Root, "full-override.json"); } }
        readonly string path;
        public FullOverrideMarker() : this(DefaultPath) { }
        public FullOverrideMarker(string path) { this.path = path; }
        public bool Exists { get { return File.Exists(path); } }
        public void Write(string restoreMode)
        { ReportFiles.Write(path, new { AtUtc = DateTime.UtcNow.ToString("o"), RestoreMode = restoreMode, ProcessId = Process.GetCurrentProcess().Id }); }
        public void Remove() { if (File.Exists(path)) File.Delete(path); }
        // Service start: restores BIOS automatic only when our own marker is pending. The marker is written atomically before the
        // firmware write, so a crash cannot lose it; BIOS "Enabled" without it is the user's own setting and is left alone.
        public static bool RestoreStale(FullOverrideMarker marker, Action restore)
        {
            if (!marker.Exists) return false;
            restore();
            return true;
        }
    }

    public sealed class FanLease
    {
        readonly IFanBackend backend;
        public bool Active { get; private set; }
        public string RestoreMode { get; private set; }
        public DateTime ExpiresUtc { get; private set; }
        public bool Persistent { get; private set; }
        readonly FullOverrideMarker marker;
        public FanLease(IFanBackend backend, FullOverrideMarker marker = null) { this.backend = backend; this.marker = marker; }

        public void Full(int seconds, DateTime nowUtc)
        {
            if (seconds < 5 || seconds > 300) throw new ArgumentOutOfRangeException("seconds", "Duration must be 5-300 seconds.");
            string original = Active ? RestoreMode : backend.ReadMode();
            if (original != "Enabled" && original != "Disabled") throw new InvalidDataException("Cannot determine restore mode.");
            RestoreMode = original;
            Persistent = false;
            Active = true; // Arm restoration before a write that could fail after taking effect.
            ExpiresUtc = nowUtc.AddSeconds(seconds);
            try { backend.SetMode("Enabled"); }
            catch (Exception writeError)
            {
                try { Restore(); }
                catch (Exception restoreError) { throw new AggregateException("Mode write and restoration both failed.", writeError, restoreError); }
                throw;
            }
        }

        public void Automatic()
        {
            backend.SetMode("Disabled");
            ConfirmedRestore("Disabled");
            Active = false;
        }
        // The marker goes only after the BIOS reads back the mode the fan was handed to.
        void ConfirmedRestore(string expected)
        {
            if (marker == null || !marker.Exists) return;
            if (backend.ReadMode() != expected) throw new InvalidDataException("BIOS fan mode did not read back as " + expected + "; the full-speed marker stays.");
            marker.Remove();
        }
        public void FullUntilChanged()
        {
            string original = Active ? RestoreMode : backend.ReadMode();
            if (original != "Enabled" && original != "Disabled") throw new InvalidDataException("Cannot determine restore mode.");
            if (marker != null) marker.Write(original); // before the write; if it fails nothing was changed
            RestoreMode = original; Active = true; Persistent = true; ExpiresUtc = DateTime.MaxValue;
            try { backend.SetMode("Enabled"); }
            catch { Restore(); throw; }
        }
        public void Restore()
        {
            if (!Active) return;
            backend.SetMode(RestoreMode);
            ConfirmedRestore(RestoreMode);
            Active = false;
        }
        public void Tick(DateTime nowUtc) { if (Active && nowUtc >= ExpiresUtc) Restore(); }
    }

    public sealed class ThermalEngine : IDisposable
    {
        readonly object gate = new object();
        readonly SensorLayer sensors;
        readonly Dictionary<string, Tuple<float, float>> extrema = new Dictionary<string, Tuple<float, float>>();
        readonly List<string> initializationWarnings = new List<string>();
        readonly ProcessResourceMonitor resources = new ProcessResourceMonitor();
        string resourceRecoveryNotice;
        DellFanBackend fan;
        FanLease lease;
        ManualRpmController manual;
        string manualUnavailable;
        string mode = "Unknown";
        DateTime lastModeRead = DateTime.MinValue;
        bool disposed;
        public string Model { get; private set; }
        public string BiosVersion { get; private set; }
        internal DellFanBackend Fan { get { return fan; } }
        public string InitializationProblem { get { return initializationWarnings.Count == 0 ? null : string.Join("; ", initializationWarnings); } }

        public ThermalEngine(bool backgroundSafety = false)
        {
            try
            {
                fan = new DellFanBackend();
                Model = fan.Model; BiosVersion = fan.BiosVersion;
                lease = new FanLease(fan, new FullOverrideMarker());
                mode = fan.ReadMode(); lastModeRead = DateTime.UtcNow;
                try { manual = new ManualRpmController(new DellSmmFan(Model, BiosVersion), fan, Model, BiosVersion, backgroundSafety); }
                catch (Exception e) { manualUnavailable = e.Message; }
            }
            catch (Exception e) { initializationWarnings.Add("Dell interface: " + e.Message); }
            try { sensors = SensorLayer.CreateDefault(); sensors.Start(); }
            catch (Exception e) { initializationWarnings.Add("Sensors: " + e.Message); }
        }

        // The 1 s control tick: fresh sensor samples and the controller's state only. No firmware fan read, resource read or row
        // bookkeeping; the service rebuilds the full snapshot (Poll) every 2 s.
        public Snapshot ControlSample()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ThermalEngine");
                List<SensorRow> rows = new List<SensorRow>(); List<string> warnings = new List<string>();
                SensorReadout readout = sensors == null ? new SensorReadout { Source = "None" } : sensors.Read(rows, warnings);
                if (manual != null) manual.ObserveTemperature(readout.ProtectionTemperatureC.HasValue ? (float?)readout.ProtectionTemperatureC.Value : null);
                return new Snapshot { PolledAtUtc = DateTime.UtcNow.ToString("o"), Model = Model, BiosVersion = BiosVersion, Sensors = rows, Warnings = warnings,
                    ManualControl = manual == null ? new ManualFanStatus { State = "Unavailable", Error = manualUnavailable } : manual.Status,
                    CpuControlTemperatureC = readout.ControlTemperatureC, CpuProtectionTemperatureC = readout.ProtectionTemperatureC,
                    SensorSource = readout.Source, SensorDriverState = readout.DriverState };
            }
        }

        public Snapshot Poll()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ThermalEngine");
                Stopwatch watch = Stopwatch.StartNew();
                List<string> warnings = new List<string>(initializationWarnings);
                List<SensorRow> rows = new List<SensorRow>();
                ResourceStatus usage = resources.Read(manual == null ? 0 : manual.WatchdogProcessId);
                if (usage.RestoreAutomaticRequested && manual != null && manual.Status.Active)
                {
                    try
                    {
                        manual.Stop(false); lease.Automatic(); mode = "Disabled"; lastModeRead = DateTime.UtcNow;
                        resourceRecoveryNotice = "程序资源持续超限，已恢复 BIOS 自动调速并降低界面刷新频率。";
                    }
                    catch (Exception e) { warnings.Add("Resource-limit recovery: " + e.Message); }
                }
                if (resourceRecoveryNotice != null) warnings.Add(resourceRecoveryNotice);
                if (usage.Error != null) warnings.Add("Resource monitor: " + usage.Error);
                if (lease != null)
                {
                    try
                    {
                        bool wasActive = lease.Active;
                        lease.Tick(DateTime.UtcNow);
                        if (wasActive && !lease.Active) lastModeRead = DateTime.MinValue;
                    }
                    catch (Exception e) { warnings.Add("Fan restore: " + e.Message); }
                }
                if (fan != null)
                {
                    try
                    {
                        ManualFanStatus feedback = manual == null ? null : manual.Status;
                        if (feedback != null && feedback.Active && feedback.ActualRpm.HasValue && FanSafetyPolicy.FeedbackFresh(feedback.FeedbackAtUtc, DateTime.UtcNow))
                            rows.Add(new SensorRow { Id = "dell/fan/F600", Device = Model, HardwareType = "DellFirmware", Name = "System / CPU Fan",
                                Type = "Fan", Role = "Measurement", Unit = "RPM", Value = feedback.ActualRpm.Value,
                                Source = "Dell LegacyDiags / SMM RPM", Quality = "OK", PolledAtUtc = feedback.FeedbackAtUtc });
                        else rows.AddRange(fan.ReadFans());
                    }
                    catch (Exception e) { warnings.Add("Fan RPM: " + e.Message); }
                    // BIOS configuration queries serialize firmware access for ~2 seconds on this machine.
                    // Manual feedback owns that interface until it restores the known entry configuration.
                    bool manualActive = manual != null && manual.Status.Active;
                    if (!manualActive && (DateTime.UtcNow - lastModeRead >= TimeSpan.FromSeconds(10) || (lease != null && lease.Active && !lease.Persistent)))
                    {
                        try { mode = fan.ReadMode(); lastModeRead = DateTime.UtcNow; }
                        catch (Exception e) { mode = "Unknown"; warnings.Add("Fan mode: " + e.Message); }
                    }
                }
                SensorReadout readout = sensors == null ? new SensorReadout { Source = "None" } : sensors.Read(rows, warnings);
                // Protection follows the hottest raw sample of the last second; null (no source, or stale) hands control back to BIOS.
                if (manual != null) manual.ObserveTemperature(readout.ProtectionTemperatureC.HasValue ? (float?)readout.ProtectionTemperatureC.Value : null);
                ManualFanStatus manualStatus = manual == null ? new ManualFanStatus { State = "Unavailable", Error = manualUnavailable } : manual.Status;
                if (manualStatus.Active && manualStatus.ActualRpm.HasValue && FanSafetyPolicy.FeedbackFresh(manualStatus.FeedbackAtUtc, DateTime.UtcNow))
                {
                    SensorRow fanRow = rows.FirstOrDefault(r => r.Id == "dell/fan/F600");
                    if (fanRow != null)
                    {
                        fanRow.Value = manualStatus.ActualRpm.Value;
                        fanRow.PolledAtUtc = manualStatus.FeedbackAtUtc;
                        fanRow.Source = "Dell LegacyDiags / SMM RPM";
                    }
                }
                else if (manualStatus.Active && manualStatus.ActualRpm.HasValue)
                    warnings.Add("Fan feedback is delayed; the current RPM display is using the firmware sensor instead.");
                if (manualStatus.Error != null && manualStatus.State != "Unavailable") warnings.Add("Fan controller: " + manualStatus.Error);
                foreach (SensorRow row in rows.Where(r => r.Value.HasValue))
                {
                    Tuple<float, float> old;
                    if (!extrema.TryGetValue(row.Id, out old)) old = Tuple.Create(row.Value.Value, row.Value.Value);
                    old = Tuple.Create(Math.Min(old.Item1, row.Value.Value), Math.Max(old.Item2, row.Value.Value));
                    extrema[row.Id] = old; row.Minimum = old.Item1; row.Maximum = old.Item2;
                }
                Dictionary<string, string> coverage = new Dictionary<string, string>();
                foreach (string category in new[] { "Cpu", "Storage" })
                {
                    int count = rows.Count(r => r.HardwareType == category && SensorRoles.IsCurrentTemperature(r) && r.Value.HasValue);
                    coverage[category] = count > 0 ? count + " current temperature channels" : "No current temperature exposed by available providers";
                }
                if (!rows.Any(r => r.HardwareType == "Cpu" && SensorRoles.IsCurrentTemperature(r) && r.Value.HasValue))
                    warnings.Add("CPU temperatures unavailable: no PawnIO IntelMSR and no Intel ESIF reading. Install the sensor driver with DellFans.Cli.exe --install-sensor-driver.");
                double? remaining = lease != null && lease.Active && !lease.Persistent ? (double?)Math.Max(0, (lease.ExpiresUtc - DateTime.UtcNow).TotalSeconds) : null;
                return new Snapshot { PolledAtUtc = DateTime.UtcNow.ToString("o"), Model = Model, BiosVersion = BiosVersion,
                    FanMode = manualStatus.Active ? "ManualRpm" : mode, FullSpeedSecondsRemaining = remaining, Sensors = rows, Warnings = warnings,
                    ManualControl = manualStatus,
                    CpuControlTemperatureC = readout.ControlTemperatureC, CpuProtectionTemperatureC = readout.ProtectionTemperatureC,
                    SensorSource = readout.Source, SensorDriverState = readout.DriverState, SensorTimings = readout.Timings,
                    Resources = usage,
                    FirmwareConfigurationWriteCount = fan == null ? 0 : fan.ConfigurationWriteCount,
                    Coverage = coverage, PollDurationMilliseconds = watch.ElapsedMilliseconds,
                    ControlCapabilities = new Dictionary<string, object> {
                        { "ImplementedModes", manual == null ? new[] { "BiosAutomatic", "FullSpeed" } : new[] { "BiosAutomatic", "FullSpeed", "ManualRpm" } },
                        { "ArbitraryPercentImplemented", false }, { "RangePercentImplemented", manual != null },
                        { "TargetRpmImplemented", manual != null }, { "AdjustableMinimumRpm", RpmRange.Minimum },
                        { "AdjustableMaximumRpm", RpmRange.Maximum }, { "FullSpeedReferenceRpm", 5571 },
                        { "RawSmmProbePerformed", true }, { "LhmControlChannelsExposed", rows.Count(r => r.Type == "Control") }
                    } };
            }
        }

        public void SetAutomatic()
        {
            lock (gate) { RequireControl(); if (manual != null) manual.Stop(false); lease.Automatic(); mode = fan.ReadMode(); lastModeRead = DateTime.UtcNow; resourceRecoveryNotice = null; }
        }
        public void UpdateControlSnapshot(Snapshot sample)
        {
            lock (gate)
            {
                sample.ManualControl = manual == null ? sample.ManualControl : manual.Status;
                sample.FanMode = sample.ManualControl != null && sample.ManualControl.Active ? "ManualRpm" : mode;
                sample.FirmwareConfigurationWriteCount = fan == null ? 0 : fan.ConfigurationWriteCount;
            }
        }
        public void SetFullPersistent()
        {
            lock (gate) { RequireControl(); if (manual != null) manual.Stop(false); lease.FullUntilChanged(); mode = fan.ReadMode(); lastModeRead = DateTime.UtcNow; }
        }
        public void SetTargetRpm(int rpm)
        {
            lock (gate)
            {
                if (manual == null) throw new NotSupportedException(manualUnavailable ?? "Manual fan interface unavailable.");
                RpmRange.Validate(rpm);
                lease.Restore();
                if (!manual.Status.Active) Poll(); // Refresh temperatures immediately before slow firmware entry calls.
                manual.SetTarget(rpm);
                resourceRecoveryNotice = null;
                mode = "Enabled"; lastModeRead = DateTime.UtcNow;
            }
        }
        public void Restore()
        {
            lock (gate) { if (manual != null) manual.Stop(true); if (lease != null) { lease.Restore(); mode = fan.ReadMode(); lastModeRead = DateTime.UtcNow; } }
        }
        void RequireControl() { if (lease == null) throw new NotSupportedException("Dell fan override interface is unavailable."); }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                // Restore must succeed before disposing the backend; callers can retry on failure.
                if (manual != null) manual.Dispose();
                if (lease != null) lease.Restore();
                if (sensors != null) sensors.Dispose();
                if (fan != null) fan.Dispose();
                resources.Dispose();
                disposed = true;
            }
        }
    }

    public static class ReportFiles
    {
        public static bool ServiceMode;
        public static string ReportsDirectory
        {
            get
            {
                string installed = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DellFans")).TrimEnd(Path.DirectorySeparatorChar);
                string current = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (ServiceMode) return ServicePaths.Reports;
                // The installed interface runs unelevated and cannot write ProgramData: it keeps its notes in the user's own profile.
                if (string.Equals(installed, current, StringComparison.OrdinalIgnoreCase))
                    return Elevated ? ServicePaths.Reports : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DellFans", "Reports");
                return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "reports"));
            }
        }
        static bool? elevated;
        static bool Elevated
        {
            get
            {
                if (!elevated.HasValue)
                    using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                        elevated = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                return elevated.Value;
            }
        }
        public static string Json(object data) { return MiniJson.Serialize(data); }
        public static void Write(string path, object data)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Json(data), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

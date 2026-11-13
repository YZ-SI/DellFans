using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DellFans;

internal sealed class FakeFan : IFanBackend
{
    public string Mode = "Disabled";
    public bool FailAfterWrite;
    public bool FailRestore;
    public int Writes;
    public string ReadMode() { return Mode; }
    public void SetMode(string mode)
    {
        Writes++;
        if (mode == "Disabled" && FailRestore) throw new IOException("Simulated restore failure");
        Mode = mode;
        if (mode == "Enabled" && FailAfterWrite) throw new IOException("Simulated readback failure after a write");
    }
}

internal sealed class FakeSmmFan : ISmmFan
{
    public readonly List<string> Calls = new List<string>();
    public int ReadRpm() { return 3000; }
    public void SetRunningState(int state)
    {
        if (state != 1 && state != 2) throw new Exception("Unsupported raw fan-state write attempted.");
        Calls.Add("State" + state);
    }
    public void EnableAutomatic() { Calls.Add("Auto"); }
    public void DisableAutomatic() { Calls.Add("Manual"); }
    public void Dispose() { }
}

internal static class CoreTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static void Throws(Action action, string message)
    {
        try { action(); } catch { passed++; return; }
        throw new Exception(message);
    }
    // every persistent full-speed write is paired with a marker that outlives the process.
    static void FullOverrideMarkerTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DellFans-F2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "full-override.json");
            FakeFan fan = new FakeFan(); FanLease lease = new FanLease(fan, new FullOverrideMarker(path));
            lease.FullUntilChanged();
            Check(fan.Mode == "Enabled" && File.Exists(path), "Persistent full speed must leave the marker");
            lease.Restore();
            Check(fan.Mode == "Disabled" && !File.Exists(path), "A confirmed restore removes the marker");
            // marker is written before the BIOS write
            FakeFan order = new FakeFan(); bool markerSeen = false;
            FanLease ordered = new FanLease(new HookFan(order, delegate { markerSeen = File.Exists(path); }), new FullOverrideMarker(path));
            ordered.FullUntilChanged();
            Check(markerSeen, "The marker must exist before the BIOS write");
            // restore that is not read back keeps the marker
            FakeFan stuck = new FakeFan(); stuck.FailRestore = true;
            FanLease failing = new FanLease(stuck, new FullOverrideMarker(path));
            ordered.Restore(); Check(!File.Exists(path), "setup");
            failing.FullUntilChanged();
            Throws(delegate { failing.Restore(); }, "A failed restore must surface");
            Check(File.Exists(path) && stuck.Mode == "Enabled", "A failed restore keeps the marker");
            // service start with a marker: restore once
            FakeFan stale = new FakeFan { Mode = "Enabled" }; FullOverrideMarker marker = new FullOverrideMarker(path);
            FanLease startLease = new FanLease(stale, marker);
            Check(FullOverrideMarker.RestoreStale(marker, delegate { startLease.Automatic(); }) && stale.Mode == "Disabled" && !marker.Exists, "A marker found at start must restore BIOS automatic and clear itself");
            // no marker: a BIOS "Enabled" is the user's own setting and is left alone
            stale.Mode = "Enabled";
            Check(!FullOverrideMarker.RestoreStale(marker, delegate { throw new Exception("must not run"); }) && stale.Mode == "Enabled", "Without our marker the user's BIOS setting is never overridden");
        }
        finally { Directory.Delete(dir, true); }
    }
    sealed class HookFan : IFanBackend
    {
        readonly IFanBackend inner; readonly Action beforeEnable;
        public HookFan(IFanBackend inner, Action beforeEnable) { this.inner = inner; this.beforeEnable = beforeEnable; }
        public string ReadMode() { return inner.ReadMode(); }
        public void SetMode(string mode) { if (mode == "Enabled") beforeEnable(); inner.SetMode(mode); }
    }
    static int Main()
    {
        FullOverrideMarkerTests(); Console.WriteLine("PASS F2 full-override marker written before, removed after confirmed restore, restored at start");
        DateTime start = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        FakeFan backend = new FakeFan(); FanLease lease = new FanLease(backend);
        Throws(delegate { lease.Full(0, start); }, "Zero-second request accepted");
        Throws(delegate { lease.Full(301, start); }, "Overlong request accepted");
        Check(backend.Writes == 0, "Invalid requests touched hardware");
        lease.Full(10, start);
        Check(backend.Mode == "Enabled" && lease.Active, "Full-speed lease failed");
        lease.Tick(start.AddSeconds(9)); Check(backend.Mode == "Enabled", "Lease restored too soon");
        lease.Tick(start.AddSeconds(10)); Check(backend.Mode == "Disabled" && !lease.Active, "Lease failed to restore on expiry");
        lease.Full(10, start); lease.Full(20, start.AddSeconds(5)); lease.Restore();
        Check(backend.Mode == "Disabled", "Renewal lost the original restore mode");
        backend.Mode = "Enabled"; lease.Full(5, start); lease.Restore();
        Check(backend.Mode == "Enabled", "Preexisting full-speed setting was overwritten on restore");
        backend.Mode = "Disabled"; backend.FailAfterWrite = true;
        Throws(delegate { lease.Full(10, start); }, "Post-write failure hidden");
        Check(backend.Mode == "Disabled" && !lease.Active, "Post-write failure did not restore original mode");
        backend.FailAfterWrite = false; lease.Full(5, start); backend.FailRestore = true;
        Throws(delegate { lease.Tick(start.AddSeconds(6)); }, "Restore error hidden");
        Check(lease.Active, "Failed restore lost the pending restore request");
        backend.FailRestore = false; lease.Tick(start.AddSeconds(7));
        Check(backend.Mode == "Disabled" && !lease.Active, "Restore retry failed");
        lease.Full(5, start); lease.Automatic(); lease.Tick(start.AddSeconds(6));
        Check(backend.Mode == "Disabled" && !lease.Active, "Explicit automatic mode did not cancel lease");
        lease.FullUntilChanged(); lease.Tick(start.AddYears(1));
        Check(backend.Mode == "Enabled" && lease.Active && lease.Persistent, "Persistent full mode expired unexpectedly");
        lease.Restore(); Check(backend.Mode == "Disabled", "Persistent mode did not restore original mode");
        Check(RpmRange.FromPercent(0) == 1000 && RpmRange.FromPercent(100) == 5570, "Range endpoints are wrong");
        Check(RpmRange.FromPercent(50) == 3285 && RpmRange.ToPercent(3285) == 50, "RPM/percentage synchronization is wrong");
        Throws(delegate { RpmRange.FromPercent(double.NaN); }, "NaN target accepted");
        Throws(delegate { RpmRange.FromPercent(-1); }, "Negative percentage accepted");
        Throws(delegate { RpmRange.Validate(0); }, "Stopped-fan target accepted");
        Throws(delegate { RpmRange.Validate(5600); }, "Unverified RPM range accepted");
        Check(RpmRange.ChooseState(1000, 3000, 2) == 1, "Minimum endpoint would oscillate");
        Check(RpmRange.ChooseState(4000, 3000, 1) == 2, "Maximum endpoint would oscillate");
        Check(RpmRange.ChooseState(3000, 2800, 1) == 2, "Low RPM did not request higher running state");
        Check(RpmRange.ChooseState(3000, 3200, 2) == 1, "High RPM did not request lower running state");
        Check(RpmRange.ChooseState(3000, 3005, 1) == 1, "Deadband changed actuator unnecessarily");
        Check(RpmRange.ChooseState(5570, 5000, 2) == 3, "Upper endpoint did not choose full-speed runtime actuator");
        Check(RpmRange.ChooseState(4500, 4300, 2) == 3, "Upper interval failed to accelerate");
        Check(RpmRange.ChooseState(5000, 5200, 3) == 2, "Upper interval failed to slow down");
        Check(RpmRange.ChooseState(4500, 4505, 1) == 3, "Old lower-interval actuator leaked across a target change");
        Check(RpmRange.ChooseState(3000, 3005, 3) == 2, "Old upper-interval actuator leaked across a target change");
        FakeSmmFan smm = new FakeSmmFan();
        FanActuation.Apply(smm, 3, 2);
        Check(smm.Calls.SequenceEqual(new[] { "Auto" }), "Full-speed actuation attempted an unsupported raw state");
        smm.Calls.Clear(); FanActuation.Apply(smm, 2, 3);
        Check(smm.Calls.SequenceEqual(new[] { "Manual", "State2" }), "Full-to-medium transition did not disable automatic control first");
        smm.Calls.Clear(); FanActuation.Apply(smm, 1, 3);
        Check(smm.Calls.SequenceEqual(new[] { "Manual", "State1" }), "Full-to-low transition order is wrong");
        smm.Calls.Clear(); FanActuation.Apply(smm, 2, 1);
        Check(smm.Calls.SequenceEqual(new[] { "State2" }), "Lower-range adjustment unnecessarily toggled firmware control");
        smm.Calls.Clear(); FanActuation.Apply(smm, 2, 2);
        Check(smm.Calls.Count == 0, "Stable output repeated an unnecessary firmware write");
        Throws(delegate { FanActuation.Apply(smm, 0, 2); }, "Off actuator was accepted");
        Throws(delegate { FanActuation.Apply(smm, 4, 2); }, "Unknown actuator was accepted");
        Check(smm.Calls.Count == 0, "Rejected actuator touched firmware");
        Check(!FanSafetyPolicy.RequiresFullSpeed(50, start, start.AddSeconds(2)), "Fresh cool sample incorrectly triggered protection");
        Check(!FanSafetyPolicy.RequiresFullSpeed(89.9f, start, start) && FanSafetyPolicy.RequiresFullSpeed(90, start, start), "Hot sample did not trigger protection at 90 C");
        Check(FanSafetyPolicy.IsTransientControllerError(FanSafetyPolicy.LoopInterruptedMessage) && !FanSafetyPolicy.IsTransientControllerError("SMM write failed") && !FanSafetyPolicy.IsTransientControllerError(null), "a starved control loop is a transient handback, other controller errors still latch");
        Check(FanSafetyPolicy.RequiresFullSpeed(null, start, start), "Missing temperature did not trigger protection");
        Check(FanSafetyPolicy.RequiresFullSpeed(float.NaN, start, start), "NaN bypassed protection");
        Check(FanSafetyPolicy.RequiresFullSpeed(float.PositiveInfinity, start, start), "Infinite temperature bypassed protection");
        Check(FanSafetyPolicy.RequiresFullSpeed(50, start, start.AddSeconds(9)), "Stale sample bypassed protection");
        Check(FanSafetyPolicy.RequiresFullSpeed(50, start.AddSeconds(5), start), "Future timestamp bypassed protection");
        Check(FanSafetyPolicy.FeedbackFresh(start.ToString("o"), start.AddMilliseconds(500)), "Fresh RPM was rejected");
        Check(!FanSafetyPolicy.FeedbackFresh(start.ToString("o"), start.AddSeconds(2)), "Stale RPM would hide a newer firmware reading");
        Check(!FanSafetyPolicy.FeedbackFresh("invalid", start), "Invalid feedback timestamp accepted");
        Check(!FanSafetyPolicy.LoopInterrupted(start, start.AddMilliseconds(100)), "Normal loop interval was rejected");
        Check(FanSafetyPolicy.LoopInterrupted(start, start.AddMinutes(5)), "Resume after suspension did not request recovery");
        Check(FanSafetyPolicy.LoopInterrupted(start, start.AddSeconds(-2)), "Clock reversal would leave a cached actuator active");
        ResourceBudget budget = new ResourceBudget();
        for (int t = 2; t < 20; t += 2) budget.Observe(99, 100, t);
        Check(!budget.LowOverhead && !budget.RestoreAutomatic, "Startup load triggered resource recovery");
        for (int t = 20; t <= 46; t += 2) budget.Observe(2, 100, t);
        Check(!budget.LowOverhead, "A short resource spike triggered load shedding");
        budget.Observe(2, 100, 48);
        Check(budget.LowOverhead && !budget.RestoreAutomatic, "Sustained soft CPU limit was not handled");
        for (int t = 50; t <= 76; t += 2) budget.Observe(4, 100, t);
        Check(!budget.RestoreAutomatic, "Manual control would stop before the hard limit persisted");
        budget.Observe(4, 100, 78);
        Check(budget.RestoreAutomatic, "Sustained hard CPU limit did not request automatic control");
        for (int t = 80; t <= 136; t += 2) budget.Observe(0.1, 100, t);
        Check(budget.LowOverhead && !budget.RestoreAutomatic, "Recovery hysteresis was lost");
        budget.Observe(0.1, 100, 138);
        Check(!budget.LowOverhead, "Load shedding did not recover after 60 quiet seconds");
        ResourceBudget memoryBudget = new ResourceBudget();
        for (int t = 2; t <= 48; t += 2) memoryBudget.Observe(0.1, 520, t);
        Check(memoryBudget.LowOverhead && memoryBudget.RestoreAutomatic, "Sustained private-memory limit was ignored");
        memoryBudget.Observe(20, 520, 100);
        Check(!memoryBudget.RestoreAutomatic, "A sampling gap was counted as continuous overload");
        Check(FeedbackSchedule.DelayMilliseconds(3000) == 120 && FeedbackSchedule.DelayMilliseconds(5000) == 120, "Feedback frequency changed outside its verified budget");
        Check(FeedbackSchedule.DelayMilliseconds(1000) == 750 && FeedbackSchedule.DelayMilliseconds(4000) == 750 && FeedbackSchedule.DelayMilliseconds(5570) == 750, "Fixed endpoints still use rapid feedback");
        // EC busy phase: frequency 1000 ticks per second, so one tick is one millisecond.
        EcBusyPhase ec = new EcBusyPhase(1000);
        Check(!ec.Known && ec.SafeNow(123) && ec.MillisecondsUntilSafe(123) == 0, "An unknown EC phase delayed a write");
        ec.Observe(0, 31);
        Check(!ec.Known, "A call no longer than the short busy window was taken as the long one");
        ec.Observe(1000, 1090);
        Check(ec.Known, "A 90 ms write did not calibrate the EC phase");
        Check(ec.SafeNow(1090 + 150) && ec.SafeNow(1090 + 400) && ec.SafeNow(1090 + 550), "The quiet part of the EC cycle was refused");
        Check(!ec.SafeNow(1090 + 100) && !ec.SafeNow(1090 + 660) && !ec.SafeNow(1090 + 950), "A write was allowed near an EC busy window");
        Check(ec.SafeNow(1090 + 7 * 1000 + 400) && !ec.SafeNow(1090 + 7 * 1000 + 900), "The EC phase did not repeat every second");
        Check(ec.SafeNow(1090 - 600) && !ec.SafeNow(1090 - 100), "The EC phase is wrong before its reference point");
        int untilSafe = ec.MillisecondsUntilSafe(1090 + 900);
        Check(untilSafe >= 250 && untilSafe <= 253 && ec.SafeNow(1090 + 900 + untilSafe), "Waiting did not lead to the quiet part of the EC cycle");
        ec.Observe(5000, 5009);
        Check(ec.SafeNow(1090 + 400) && !ec.SafeNow(1090 + 100), "A normal write changed the learned EC phase");
        ec.Observe(9000, 9075);
        Check(ec.SafeNow(9075 + 400) && !ec.SafeNow(9075 + 100), "A later long stall did not re-learn the EC phase");
        string resourceLog = Path.Combine(Path.GetTempPath(), "dellthermal-resource-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            BoundedResourceLog.Append(resourceLog, "\"" + new string('x', BoundedResourceLog.MaximumBytes - 4) + "\"");
            BoundedResourceLog.Append(resourceLog, "{}");
            Check(new FileInfo(resourceLog).Length <= BoundedResourceLog.MaximumBytes && new FileInfo(resourceLog + ".previous").Length <= BoundedResourceLog.MaximumBytes, "Performance log exceeded its disk budget");
            Check(File.ReadAllText(resourceLog).Trim() == "{}", "Rotation lost the newest resource record");
            Throws(delegate { BoundedResourceLog.Append(resourceLog, new string('x', BoundedResourceLog.MaximumBytes + 1)); }, "Oversized resource record was accepted");
        }
        finally { if (File.Exists(resourceLog)) File.Delete(resourceLog); if (File.Exists(resourceLog + ".previous")) File.Delete(resourceLog + ".previous"); }
        // The mode rules themselves are covered by tests/ModeTests.cs (run-modes.ps1); this keeps the persisted-store contract.
        FloorSettings floor = FloorSettings.Defaults(); floor.Validate();
        Check(floor.Enabled && floor.Mode == "balanced" && !floor.MinimumRpm.HasValue, "First run is balanced (control still waits for the capability check)");
        floor.Mode = "custom";
        Throws(delegate { floor.Validate(); }, "Custom mode enabled without explicit minimum RPM");
        floor.MinimumRpm = 2000; floor.Validate();
        floor.Mode = "auto"; floor.MinimumRpm = null; floor.Validate();
        Check(!floor.Copy().Enabled && !floor.Copy().MinimumRpm.HasValue, "Automatic mode would be re-enabled after a settings copy");
        string settingsPath = Path.Combine(Path.GetTempPath(), "dellthermal-settings-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            FloorSettingsStore.Save(settingsPath, floor);
            FloorSettings reloaded = FloorSettingsStore.Load(settingsPath);
            Check(!reloaded.Enabled && !reloaded.MinimumRpm.HasValue, "Persisted BIOS automatic mode did not survive reload");
            floor.Mode = "custom"; floor.MinimumRpm = 2300; FloorSettingsStore.Save(settingsPath, floor);
            reloaded = FloorSettingsStore.Load(settingsPath);
            Check(reloaded.Enabled && reloaded.Mode == "custom" && reloaded.MinimumRpm == 2300, "Custom minimum did not survive reload");
            Check(File.Exists(settingsPath + ".previous"), "Settings save did not retain its previous valid file");
            floor.MinimumRpm = 0;
            Throws(delegate { FloorSettingsStore.Save(settingsPath, floor); }, "Invalid setting overwrote a valid file");
            Check(FloorSettingsStore.Load(settingsPath).MinimumRpm == 2300, "A rejected save corrupted the persisted minimum");
            File.WriteAllText(settingsPath, "{invalid json");
            Throws(delegate { FloorSettingsStore.Load(settingsPath); }, "Corrupt settings silently enabled fan control");
        }
        finally { if (File.Exists(settingsPath)) File.Delete(settingsPath); if (File.Exists(settingsPath + ".previous")) File.Delete(settingsPath + ".previous"); }
        Check(SensorRoles.Classify("Temperature", "Critical Temperature") == "TemperatureLimit", "NVMe limit mislabeled as live temperature");
        Check(SensorRoles.Classify("Temperature", "P-Core #1 Distance to TjMax") == "ThermalMargin", "TjMax margin mislabeled");
        Check(!SensorRoles.IsCurrentTemperature(new SensorRow { Type = "Temperature", Role = "TemperatureLimit" }), "Limit counted as live channel");
        Check(!SensorRoles.IsCurrentTemperature(new SensorRow { Type = "Temperature", Role = "ThermalMargin" }), "Margin counted as live channel");
        Check(SensorRoles.IsCurrentTemperature(new SensorRow { Type = "Temperature", Role = "DerivedTemperature" }), "Derived temperature was lost");
        byte[] table = new byte[] { 0xDA,17,0,0,0,0,0,0,0,0,0, 0,0xF6, 0x34,0x12, 1,0, 0,0, 127,4,0,0,0,0 };
        List<DellToken> tokens = DellTokenTable.Parse(table);
        Check(tokens.Count == 1 && tokens[0].Id == 0xF600 && tokens[0].Location == 0x1234, "SMBIOS token ID assumed to equal location");
        Throws(delegate { DellTokenTable.Parse(new byte[] { 0xDA, 255, 0, 0, 0, 0 }); }, "Out-of-bounds table accepted");
        Throws(delegate { DellTokenTable.Parse(new byte[] { 127, 4, 0, 0, 1, 1 }); }, "Missing string terminator accepted");
        Throws(delegate { DellTokenTable.Parse(table.Take(19).ToArray()); }, "Missing end marker accepted");
        Snapshot full = new Snapshot { PolledAtUtc = "t", Model = "m", Sensors = new List<SensorRow> {
            new SensorRow { Id = "dell/fan/F600", Type = "Fan", HardwareType = "DellFirmware", Name = "System / CPU Fan" },
            new SensorRow { Id = "c1", Type = "Temperature", HardwareType = "Cpu", Name = "CPU Package" },
            new SensorRow { Id = "c2", Type = "Temperature", HardwareType = "Cpu", Name = "Core Max" },
            new SensorRow { Id = "c3", Type = "Temperature", HardwareType = "Cpu", Name = "Core Average" },
            new SensorRow { Id = "c4", Type = "Temperature", HardwareType = "Cpu", Name = "P-Core #1" },
            new SensorRow { Id = "c5", Type = "Load", HardwareType = "Cpu", Name = "CPU Total" },
            new SensorRow { Id = "s1", Type = "Temperature", HardwareType = "Storage", Name = "Temperature" } } };
        Snapshot summary = full.Summary();
        Check(string.Join(",", summary.Sensors.Select(s => s.Id).ToArray()) == "dell/fan/F600,c1,c2,c3,s1" && summary.Model == "m" && summary.PolledAtUtc == "t",
            "Summary snapshot keeps exactly the fan, CPU headline and storage temperature rows");
        Check(full.Sensors.Count == 7 && !full.SummaryOnly && summary.SummaryOnly, "Summary must not modify the full snapshot shared with diagnostics and must mark itself");
        Console.WriteLine(passed + " checks passed: restoration, RPM/percent mapping, actuator transitions, feedback/temperature protection, sensor roles, SMBIOS boundaries.");
        return 0;
    }
}

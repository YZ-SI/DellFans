using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using DellFans;

// Offline: pure controller driven by a fake clock, settings in temp folders only, request validation, CLI parsing.
// Nothing here opens a pipe, the service, the SCM, ProgramData or the hardware.
internal static class ModeTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static void Throws(Action action, string message)
    {
        try { action(); } catch (Exception) { passed++; return; }
        throw new Exception(message);
    }
    static ModeLimits Limits(string mode, int? minimum) { return FanModes.Limits(mode, minimum); }

    // One second per step; returns the target after each update.
    static List<int> Drive(ModeController controller, ModeLimits limits, double startSeconds, params double[] temperatures)
    {
        List<int> targets = new List<int>(); double now = startSeconds;
        foreach (double temperature in temperatures) { targets.Add(controller.Update(limits, temperature, temperature, now)); now += 1; }
        return targets;
    }
    static double[] Repeat(double value, int count) { return Enumerable.Repeat(value, count).ToArray(); }

    static void ModeTable()
    {
        ModeLimits quiet = Limits("quiet", null), balanced = Limits("balanced", null), performance = Limits("performance", null), custom = Limits("custom", 3300);
        Check(quiet.Min == 1500 && quiet.Max == 3000 && quiet.StartC == 55, "quiet is 1500-3000 from 55 C");
        Check(balanced.Min == 2500 && balanced.Max == 5570 && balanced.StartC == 65, "balanced is 2500-full from 65 C");
        Check(performance.Min == 4000 && performance.Max == 5570 && performance.StartC == 65, "performance is 4000-full from 65 C");
        Check(custom.Min == 3300 && custom.Max == 5570 && custom.StartC == 65, "custom is user minimum - full from 65 C");
        Check(Limits("custom", 1000).Min == 1000 && Limits("custom", 5570).Min == 5570, "custom accepts the 1000 and 5570 endpoints");
        Check(Limits("auto", null) == null, "auto has no limits (BIOS control, no takeover)");
        Throws(delegate { Limits("custom", null); }, "custom without a minimum accepted");
        Throws(delegate { Limits("custom", 999); }, "custom 999 accepted");
        Throws(delegate { Limits("custom", 5571); }, "custom 5571 accepted");
        Throws(delegate { Limits("turbo", null); }, "unknown mode accepted");
        Throws(delegate { Limits(null, null); }, "null mode accepted");
        Check(quiet.Min <= quiet.Max, "limits well formed");
    }

    static void RampsUpFiftyPerSecondToTheMax()
    {
        ModeController controller = new ModeController(); ModeLimits balanced = Limits("balanced", null);
        List<int> hot = Drive(controller, balanced, 0, Repeat(70, 12));
        Check(hot[0] == 2500, "first update starts at the mode minimum, got " + hot[0]);
        Check(hot[1] == 2550 && hot[2] == 2600 && hot[10] == 3000, "target did not rise 50 RPM per second: " + string.Join(",", hot));
        controller = new ModeController(); ModeLimits quiet = Limits("quiet", null);
        List<int> series = Drive(controller, quiet, 0, Repeat(60, 80));
        Check(series[0] == 1500 && series[1] == 1550 && series[30] == 3000, "quiet ramp is 1500 + 50/s: " + series[30]);
        Check(series.All(t => t <= 3000), "quiet exceeded its 3000 RPM cap");
        Check(series[79] == 3000, "quiet did not hold at the cap");
        controller = new ModeController(); ModeLimits performance = Limits("performance", null);
        series = Drive(controller, performance, 0, Repeat(80, 40));
        Check(series[0] == 4000 && series[30] == 5500 && series[31] == 5550 && series[32] == 5570 && series[39] == 5570, "performance ramps to full speed and stops: " + series[32]);
    }

    static void HoldsInsideTheBand()
    {
        ModeController controller = new ModeController(); ModeLimits balanced = Limits("balanced", null);
        List<int> up = Drive(controller, balanced, 0, Repeat(70, 7)); // 2500 .. 2800
        Check(up[6] == 2800, "setup ramp");
        // 62..65 inclusive is the band: above start rises, below start-3 may fall; in between nothing moves, however long it lasts.
        double clock = 7;
        foreach (double inside in new[] { 65.0, 64.0, 63.0, 62.0 })
        {
            List<int> held = Drive(controller, balanced, clock, Repeat(inside, 45)); clock += 45;
            Check(held.All(t => t == 2800), "target moved inside the hold band at " + inside + " C");
        }
        Check(Drive(controller, balanced, clock, 65.01)[0] == 2850, "65.01 C is above the start temperature");
    }

    static void CoolsAfterTwentyStableSeconds()
    {
        ModeController controller = new ModeController(); ModeLimits balanced = Limits("balanced", null);
        Drive(controller, balanced, 0, Repeat(70, 11)); // reaches 3000 at t=10
        List<int> cool = Drive(controller, balanced, 11, Repeat(60, 30)); // t=11 is the first reading below 62
        Check(cool.Take(20).All(t => t == 3000), "target fell before the temperature was stable for 20 s: " + string.Join(",", cool.Take(20)));
        Check(cool[20] == 2900 && cool[21] == 2800 && cool[22] == 2700 && cool[23] == 2600 && cool[24] == 2500, "cooling is -100 per second: " + string.Join(",", cool.Skip(18).Take(8)));
        Check(cool.Skip(24).All(t => t == 2500), "target went under the mode minimum");
        // quiet bottoms at 1500
        controller = new ModeController(); ModeLimits quiet = Limits("quiet", null);
        Drive(controller, quiet, 0, Repeat(60, 31)); // 3000
        List<int> down = Drive(controller, quiet, 31, Repeat(40, 60));
        Check(down[19] == 3000 && down[20] == 2900 && down[35] == 1500 && down[59] == 1500, "quiet cooling lands on 1500: " + down[35]);
    }

    static void InterruptedCoolingRestartsTheTwentySeconds()
    {
        ModeController controller = new ModeController(); ModeLimits balanced = Limits("balanced", null);
        Drive(controller, balanced, 0, Repeat(70, 11)); // 3000
        Drive(controller, balanced, 11, Repeat(60, 15));  // 15 s of cooling
        List<int> back = Drive(controller, balanced, 26, 63); // in the band: stability is lost
        Check(back[0] == 3000, "band reading changed the target");
        List<int> again = Drive(controller, balanced, 27, Repeat(60, 22)); // t=27..48, first fall at t=47
        Check(again.Take(20).All(t => t == 3000) && again[20] == 2900 && again[21] == 2800, "stability was not restarted after leaving the cooling zone: " + string.Join(",", again.Take(22)));
        // Reheating above the start temperature ends the cooling run: the new run needs its own 20 s.
        List<int> reheat = Drive(controller, balanced, 49, 70, 60, 60);
        Check(reheat[0] == 2850 && reheat[1] == 2850 && reheat[2] == 2850, "reheating or the following seconds moved the target the wrong way: " + string.Join(",", reheat));
    }

    static void EmergencyAtNinetyReleasesAtEightyFive()
    {
        ModeController controller = new ModeController(); ModeLimits quiet = Limits("quiet", null);
        Drive(controller, quiet, 0, Repeat(50, 3));
        Check(!controller.Emergency, "emergency before any heat");
        Check(controller.Update(quiet, 50, 89.9, 3) == 1500 && !controller.Emergency, "89.9 C must not trigger the emergency");
        int full = controller.Update(quiet, 70, 90, 4);
        Check(full == RpmRange.Maximum && controller.Emergency, "90 C protection reading was not full speed immediately (and above the quiet cap)");
        Check(controller.Update(quiet, 90, 90, 5) == RpmRange.Maximum, "emergency lasts while hot");
        Check(controller.Update(quiet, 86, 86, 6) == RpmRange.Maximum && controller.Emergency, "released above 85 C");
        Check(controller.Update(quiet, 80, 86, 7) == RpmRange.Maximum && controller.Emergency, "released while the raw peak was still above 85 C");
        Check(controller.Update(quiet, 85, 85, 8) == RpmRange.Maximum && controller.Emergency, "released before the 10 s minimum dwell");
        int released = controller.Update(quiet, 85, 85, 14);
        Check(!controller.Emergency && released == RpmRange.Maximum, "release must hold the current target, not snap to the mode max: " + released);
        // control mean alone also triggers the emergency
        controller = new ModeController();
        Check(controller.Update(Limits("balanced", null), 90, 60, 0) == RpmRange.Maximum && controller.Emergency, "90 C control mean did not trigger the emergency");
        // after release the normal rules continue from full speed: still hot above start, so it holds there
        List<int> after = Drive(controller, Limits("balanced", null), 1, Repeat(80, 12));
        Check(after.All(t => t == RpmRange.Maximum) && !controller.Emergency, "released controller dropped from full speed too fast");
    }

    static void TickJitterDoesNotSkipOrDoubleSteps()
    {
        ModeController controller = new ModeController(); ModeLimits balanced = Limits("balanced", null);
        int start = controller.Update(balanced, 70, 70, 0);
        foreach (double at in new[] { 0.5, 1.02, 1.5, 1.98 }) controller.Update(balanced, 70, 70, at);
        Check(start == 2500 && controller.Update(balanced, 70, 70, 2.0) == 2600, "two seconds of jittered ticks must add exactly two 50 RPM steps");
        // a long pause (service busy, resume) never jumps more than two steps
        Check(controller.Update(balanced, 70, 70, 62.0) <= 2700, "a long gap produced a large jump");
    }

    static void LimitsChangeClampsTheTarget()
    {
        ModeController controller = new ModeController();
        Drive(controller, Limits("performance", null), 0, Repeat(70, 3)); // 4100
        Check(controller.Update(Limits("quiet", null), 60, 60, 3) == 3000, "switching to quiet did not clamp to its max");
        Check(controller.Update(Limits("performance", null), 60, 60, 4) == 4000, "switching to performance did not lift to its minimum");
        Throws(delegate { controller.Update(null, 60, 60, 5); }, "auto (no limits) must never produce a target");
        Throws(delegate { controller.Update(Limits("quiet", null), double.NaN, 60, 5); }, "NaN control temperature accepted");
        Check(controller.Update(Limits("quiet", null), 60, double.PositiveInfinity, 5) <= 3000 && controller.Update(Limits("quiet", null), 60, double.NaN, 6) <= 3000 && !controller.Emergency, "a bad protection temperature must fall back to the control value, never throw or trigger");
        controller.Reset();
        Check(controller.Update(Limits("balanced", null), 40, 40, 0) == 2500 && !controller.Emergency, "Reset did not restart from the minimum");
    }

    static void TakeoverStartsAtTheMeasuredSpeed()
    {
        ModeLimits balanced = Limits("balanced", null), quiet = Limits("quiet", null);
        ModeController hot = new ModeController(); hot.Seed(balanced, 4500);
        List<int> series = Drive(hot, balanced, 0, 85, 85, 85);
        Check(series[0] == 4500 && series[1] == 4550 && series[2] == 4600, "hot takeover at 85 C with 4500 RPM must start at 4500 then ramp: " + string.Join(",", series));
        Check(ModeController.StartRpm(quiet, 4500) == 3000 && ModeController.StartRpm(balanced, 1200) == 2500 && ModeController.StartRpm(balanced, null) == 2500, "start RPM is clamped to the mode window, no reading = minimum");
        ModeController noReading = new ModeController(); noReading.Seed(balanced, null);
        Check(noReading.Update(balanced, 60, 60, 0) == 2500, "no fresh RPM must start at the mode minimum");
        ModeController running = new ModeController(); running.Update(balanced, 60, 60, 0); running.Seed(balanced, 5000);
        Check(running.Update(balanced, 60, 60, 1) == 2500, "Seed must not move a running controller");
        ModeController emergency = new ModeController(); emergency.Seed(balanced, 4500);
        Check(emergency.Update(balanced, 91, 91, 0) == RpmRange.Maximum, "90 C is still full speed whatever the seed");
        DateTime now = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        Func<string, float?, Snapshot> snap = delegate(string at, float? value) { return new Snapshot { Sensors = new List<SensorRow> {
            new SensorRow { Type = "Fan", Value = value, PolledAtUtc = at } } }; };
        Check(ThermalServiceRuntime.MeasuredRpm(snap(now.AddSeconds(-1).ToString("o"), 4499.6f), now) == 4500, "fresh fan row is the measured RPM");
        Check(ThermalServiceRuntime.MeasuredRpm(snap(now.AddSeconds(-10).ToString("o"), 4500f), now) == null, "a stale fan row is not a measurement");
        Check(ThermalServiceRuntime.MeasuredRpm(snap(now.ToString("o"), null), now) == null && ThermalServiceRuntime.MeasuredRpm(snap(null, 4500f), now) == null, "missing value/time is not a measurement");
    }

    // Found on the real machine: switching modes while cool kept the previous target (performance 4000) and only crept down after 20 s.
    static void ModeSwitchWhileCoolGoesStraightToTheNewMinimum()
    {
        ModeLimits balanced = Limits("balanced", null), quiet = Limits("quiet", null), performance = Limits("performance", null);
        ModeController c = new ModeController();
        Check(c.Update(performance, 41, 41, 0) == 4000 && c.Update(performance, 41, 41, 1) == 4000, "performance at 41 C holds its 4000 minimum");
        Check(c.Update(balanced, 41, 41, 2) == 2500, "performance -> balanced at 41 C goes straight to 2500");
        Check(c.Update(quiet, 41, 41, 3) == 1500, "balanced -> quiet at 41 C goes straight to 1500");
        Check(c.Update(performance, 41, 41, 4) == 4000 && c.Update(quiet, 41, 41, 5) == 1500, "quiet <-> performance at 41 C lands on the new minimum at once");
        ModeController warm = new ModeController(); warm.Seed(balanced, 4000);
        Check(warm.Update(balanced, 64, 64, 0) == 4000 && warm.Update(performance, 64, 64, 1) == 4000, "inside the hold band (62..65 C) the current speed is kept, only clamped");
        ModeController hotQuiet = new ModeController(); hotQuiet.Seed(performance, 5570);
        Check(hotQuiet.Update(performance, 70, 70, 0) == 5570 && hotQuiet.Update(quiet, 70, 70, 1) == 3000, "hot: switching to quiet clamps to its 3000 maximum, not the minimum");
        Check(ModeController.StartRpm(balanced, 4000, 41) == 2500 && ModeController.StartRpm(balanced, 4000, 63) == 4000 && ModeController.StartRpm(quiet, 2000, 50) == 1500,
            "takeover start: below start-3 = mode minimum; hold band = measured speed clamped");
    }

    static void ReviewFixes()
    {
        ModeLimits quiet = Limits("quiet", null), balanced = Limits("balanced", null);
        // B: quiet at 88 -> 86 -> 84 C after an emergency does not hunt between 90 and 85
        ModeController c = new ModeController(); int flips = 0; bool was = false; List<int> t = new List<int>();
        double[] series = { 91, 88, 88, 88, 88, 88, 88, 88, 88, 88, 88, 86, 86, 84, 84, 84, 84, 84, 84, 84, 84 };
        for (int i = 0; i < series.Length; i++) { t.Add(c.Update(quiet, series[i], series[i], i)); if (c.Emergency != was) { flips++; was = c.Emergency; } }
        Check(flips == 2 && !c.Emergency && t[t.Count - 1] == RpmRange.Maximum, "quiet 88/86/84 C after an emergency: one entry, one release, target held: " + string.Join(",", t));
        Check(t[10] == RpmRange.Maximum && t.Take(11).All(x => x == RpmRange.Maximum), "emergency lasts at least 10 s");
        List<int> cool = Drive(c, quiet, 21, Repeat(40, 60));
        Check(cool[0] > 3000 && cool.Last() <= 3000, "the cooling rule brings the held target below the cap: " + string.Join(",", cool));
        // C: a limits change restarts the cooling timer
        c = new ModeController(); c.Update(balanced, 40, 40, 0);
        for (int i = 1; i <= 15; i++) c.Update(balanced, 40, 40, i);
        c = new ModeController(); c.Seed(5000);
        c.Update(balanced, 40, 40, 0); for (int i = 1; i <= 19; i++) c.Update(balanced, 40, 40, i);
        int before = c.Update(balanced, 40, 40, 20); // cooling for 20 s: first -100
        ModeController d = new ModeController(); d.Seed(5000);
        d.Update(balanced, 40, 40, 0); for (int i = 1; i <= 15; i++) d.Update(balanced, 40, 40, i);
        d.Update(new ModeLimits { Min = 2500, Max = 5000, StartC = 65 }, 40, 40, 16); // max changed at 16 s
        int afterChange = d.Update(new ModeLimits { Min = 2500, Max = 5000, StartC = 65 }, 40, 40, 20);
        Check(before < 5000 && afterChange == 2500, "a limits change while cool goes straight to the new minimum (no inherited 20 s wait): " + before + " / " + afterChange);
        // seeds: hot takeover starts at the mode maximum
        Check(ModeController.StartRpm(balanced, 2600, 80) == balanced.Max && ModeController.StartRpm(balanced, 2600, 79) == 2600 && ModeController.StartRpm(quiet, 2000, 70) == 3000 && ModeController.StartRpm(quiet, 2000, 69.9) == 2000, "seed full speed at StartC+15 or 80 C");
        // D: bad protection values fall back to the control value
        Check(ModeController.Hottest(60, double.NaN) == 60 && ModeController.Hottest(60, -5) == 60 && ModeController.Hottest(60, 999) == 60 && ModeController.Hottest(60, null) == 60 && ModeController.Hottest(60, 72) == 72, "protection fallback");
        // E: temperature-only handback resumes after 10 s of continuous valid readings
        TemperatureRecovery r = new TemperatureRecovery();
        Check(r.MayResume(0), "no handback, no gate");
        r.HandBack(); r.Observe(false, 1); Check(!r.MayResume(1), "no valid reading yet");
        r.Observe(true, 2); Check(!r.MayResume(11.9), "9.9 s of valid readings is not enough");
        r.Observe(true, 8); r.Observe(false, 9); r.Observe(true, 10); Check(!r.MayResume(19.9), "a gap restarts the 10 s");
        Check(r.MayResume(20.0), "10 s of continuous valid readings resumes");
        r.Clear(); Check(r.MayResume(0), "cleared");
        // F: a failed release is retried every tick; repeated failure is reported
        AutomaticRelease a = new AutomaticRelease(); int calls = 0;
        Check(a.Attempt(delegate { calls++; }) && calls == 0, "nothing pending, nothing written");
        a.Request(); Check(!a.Attempt(delegate { calls++; throw new InvalidOperationException("nvram"); }) && a.Pending && a.Error == "nvram", "failure keeps the release pending");
        a.Attempt(delegate { calls++; throw new InvalidOperationException("nvram"); }); Check(!a.Failing, "two failures: not yet a fault");
        a.Attempt(delegate { calls++; throw new InvalidOperationException("nvram"); }); Check(a.Failing, "three failures: fault + warning");
        Check(a.Attempt(delegate { calls++; }) && !a.Pending && !a.Failing && calls == 4, "success clears it");
        Check(a.Attempt(delegate { calls++; }) && calls == 4, "no write once released");
        // G: repeated auto writes nothing; any other state releases
        Check(!ThermalServiceRuntime.AutoNeedsRelease(true, false, false) && ThermalServiceRuntime.AutoNeedsRelease(true, true, false) && ThermalServiceRuntime.AutoNeedsRelease(true, false, true) && ThermalServiceRuntime.AutoNeedsRelease(false, false, false), "auto-after-auto skips SetAutomatic");
        // A: emergency entry
        Check(ThermalServiceRuntime.EmergencyEntry(true, "FloorCurve", true) == "SetTarget" && ThermalServiceRuntime.EmergencyEntry(true, "FloorCurve", false) == "SetFullPersistent"
            && ThermalServiceRuntime.EmergencyEntry(true, "EmergencyFullSpeed", true) == null && ThermalServiceRuntime.EmergencyEntry(false, "EmergencyFullSpeed", false) == "SetFullPersistent", "emergency entry uses the running controller; after resume (not owned) it always forces full");
        // J: at 1 s control the full snapshot is rebuilt every 2 s, every second while a client looks
        PublishSchedule schedule = new PublishSchedule();
        for (int s = 0; s < 20; s++) if (schedule.Due(s, false, false)) schedule.Built(s);
        Check(schedule.Rebuilds == 10, "20 s of 1 s control rebuilds 10 snapshots, got " + schedule.Rebuilds);
        schedule = new PublishSchedule();
        for (int s = 0; s < 20; s++) if (schedule.Due(s, true, false)) schedule.Built(s);
        Check(schedule.Rebuilds == 20, "a viewing client gets every second");
        schedule = new PublishSchedule(); schedule.Built(0);
        Check(!schedule.Due(1, false, false) && schedule.Due(1, false, true) && schedule.Due(2, false, false), "a change publishes at once");
    }

    static void StoreRobustness()
    {
        string folder = Temp();
        try
        {
            // H: a corrupt or wrong-typed old file does not block a valid new one
            string path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "{\"SchemaVersion\":\"two\",\"Mode\":[1]}");
            FloorSettings value = FloorSettings.Defaults(); value.Mode = "quiet";
            FloorSettingsStore.Save(path, value);
            Check(FloorSettingsStore.Load(path).Mode == "quiet", "valid settings replace a corrupt file");
            File.WriteAllText(path, "not json at all");
            FloorSettingsStore.Save(path, value); Check(FloorSettingsStore.Load(path).Mode == "quiet", "valid settings replace garbage");
        }
        finally { Directory.Delete(folder, true); }
    }

    static void SafetyThresholds()
    {
        Check(FanSafetyPolicy.EmergencyC == 90 && FanSafetyPolicy.ReleaseC == 85, "one pair of protection constants");
        DateTime now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        Check(!FanSafetyPolicy.RequiresFullSpeed(89.9f, now, now) && FanSafetyPolicy.RequiresFullSpeed(90, now, now), "RequiresFullSpeed switches at 90 C");
        Check(FanSafetyPolicy.RequiresFullSpeed(null, now, now) && FanSafetyPolicy.RequiresFullSpeed(60, now.AddSeconds(-9), now), "invalid or stale temperature still requires the BIOS handback");
    }

    static string Temp() { string path = Path.Combine(Path.GetTempPath(), "DellFans-ModeTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static string Hash(string path) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))); }

    static void SettingsModelAndRoundTrip()
    {
        FloorSettings defaults = FloorSettings.Defaults(); defaults.Validate();
        Check(defaults.SchemaVersion == 2 && defaults.Mode == "balanced" && defaults.Enabled && defaults.Limits() != null && defaults.Limits().Min == 2500, "a first run is balanced, schema 2");
        string folder = Temp();
        try
        {
            string path = Path.Combine(folder, "settings.json");
            foreach (string mode in new[] { "quiet", "balanced", "performance", "auto" })
            {
                FloorSettings value = FloorSettings.Defaults(); value.Mode = mode; FloorSettingsStore.Save(path, value);
                FloorSettings back = FloorSettingsStore.Load(path);
                Check(back.Mode == mode && back.SchemaVersion == 2 && back.Enabled == (mode != "auto"), mode + " did not survive a reload");
            }
            FloorSettings custom = FloorSettings.Defaults(); custom.Mode = "custom"; custom.MinimumRpm = 3100; FloorSettingsStore.Save(path, custom);
            FloorSettings reloaded = FloorSettingsStore.Load(path);
            Check(reloaded.Mode == "custom" && reloaded.MinimumRpm == 3100 && reloaded.Limits().Min == 3100, "custom minimum did not survive a reload");
            custom.Mode = "quiet"; FloorSettingsStore.Save(path, custom);
            Check(FloorSettingsStore.Load(path).MinimumRpm == 3100, "the remembered custom minimum was dropped by another mode");
            FloorSettings bad = FloorSettings.Defaults(); bad.Mode = "custom";
            Throws(delegate { FloorSettingsStore.Save(path, bad); }, "custom without a minimum saved");
            bad.Mode = "turbo"; Throws(delegate { FloorSettingsStore.Save(path, bad); }, "unknown mode saved");
            bad.Mode = "custom"; bad.MinimumRpm = 999; Throws(delegate { FloorSettingsStore.Save(path, bad); }, "custom 999 saved");
            Check(FloorSettingsStore.Load(path).Mode == "quiet", "a rejected save damaged the file");
            File.WriteAllText(path, "{\"SchemaVersion\":3,\"Mode\":\"quiet\"}");
            Throws(delegate { FloorSettingsStore.Load(path); }, "a newer schema was accepted");
            File.WriteAllText(path, "{\"SchemaVersion\":2,\"Mode\":\"custom\"}");
            Throws(delegate { FloorSettingsStore.Load(path); }, "custom without a minimum loaded");
            File.WriteAllText(path, "{\"SchemaVersion\":2,\"Mode\":\"Quiet\"}");
            Throws(delegate { FloorSettingsStore.Load(path); }, "mode names are lower case only");
        }
        finally { Directory.Delete(folder, true); }
    }

    static void OldSchemaIsRejected()
    {
        string folder = Temp();
        try
        {
            string path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"Enabled\":true,\"MinimumRpm\":2300}");
            Throws(delegate { FloorSettingsStore.Load(path); }, "a schema-1 file was accepted");
        }
        finally { Directory.Delete(folder, true); }
    }

    static bool Bad(ServiceRequest request)
    {
        try { ControlPolicy.Validate(request); return false; } catch (ArgumentException) { return true; }
    }
    static void SetModeValidation()
    {
        foreach (string mode in new[] { "quiet", "balanced", "performance", "auto" }) ControlPolicy.Validate(new ServiceRequest { Command = "SetMode", Mode = mode });
        ControlPolicy.Validate(new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 1000 });
        ControlPolicy.Validate(new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 5570 });
        Check(ControlPolicy.Required("SetMode") == CallerLevel.Interactive, "SetMode is an interactive command");
        Check(Bad(new ServiceRequest { Command = "SetMode" }), "SetMode needs a mode");
        Check(Bad(new ServiceRequest { Command = "SetMode", Mode = "turbo" }) && Bad(new ServiceRequest { Command = "SetMode", Mode = "Quiet" }) && Bad(new ServiceRequest { Command = "SetMode", Mode = "" }), "unknown or mis-cased modes");
        Check(Bad(new ServiceRequest { Command = "SetMode", Mode = "custom" }), "custom needs MinimumRpm");
        Check(Bad(new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 999 }) && Bad(new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 5571 }), "custom range 1000-5570");
        Check(Bad(new ServiceRequest { Command = "SetMode", Mode = "quiet", MinimumRpm = 2000 }) && Bad(new ServiceRequest { Command = "SetMode", Mode = "auto", MinimumRpm = 2000 }), "MinimumRpm only belongs to custom");
        Check(Bad(new ServiceRequest { Command = "SetMode", Mode = "quiet", Detail = true }), "SetMode takes no Detail");
        Check(Bad(new ServiceRequest { Command = "SetMode", Mode = new string('a', 5000) }), "oversized mode");
        Check(Bad(new ServiceRequest { Command = "Status", Mode = "quiet" }) && Bad(new ServiceRequest { Command = "Probe", Mode = "quiet" }), "Mode is rejected on other commands");
        Check(Bad(new ServiceRequest { Command = "Configure" }) && Bad(new ServiceRequest { Command = "Configure", Mode = "quiet" }), "Configure (the old temperature curve) is gone");
        // The wire form a client sends.
        ServiceRequest wire = new JavaScriptSerializer().Deserialize<ServiceRequest>("{\"Command\":\"SetMode\",\"Mode\":\"custom\",\"MinimumRpm\":3200}");
        ControlPolicy.Validate(wire);
        Check(wire.Mode == "custom" && wire.MinimumRpm == 3200, "the SetMode wire form is parsed");
        ControlPolicy.Validate(new JavaScriptSerializer().Deserialize<ServiceRequest>("{\"Command\":\"SetMode\",\"Mode\":\"custom\",\"MinimumRpm\":2000,\"StartTemperatureC\":65,\"FullTemperatureC\":85}"));
    }

    static void SnapshotCarriesTheMode()
    {
        BackgroundStatus status = new BackgroundStatus { State = "FloorCurve", Mode = "quiet", ModeLimits = Limits("quiet", null) };
        string json = ReportFiles.Json(status);
        Check(json.Contains("\"Mode\":\"quiet\"") && json.Contains("\"ModeLimits\":{\"Min\":1500,\"Max\":3000,\"StartC\":55}"), "Background.Mode and ModeLimits are published: " + json);
        BackgroundStatus none = new BackgroundStatus { State = "BiosAutomatic", Mode = "auto" };
        Check(ReportFiles.Json(none).Contains("\"ModeLimits\":null"), "auto publishes no limits");
    }

    static ServiceRequest Cli(params string[] args) { return ClientCommands.ModeRequest(args); }
    static bool CliBad(params string[] args) { try { ClientCommands.ModeRequest(args); return false; } catch (ArgumentException) { return true; } }
    static void CommandLine()
    {
        foreach (string mode in new[] { "quiet", "balanced", "performance", "auto" })
        { ServiceRequest request = Cli("--mode", mode); Check(request.Command == "SetMode" && request.Mode == mode && !request.MinimumRpm.HasValue, "--mode " + mode); }
        ServiceRequest custom = Cli("--mode", "custom", "--rpm", "3000");
        Check(custom.Command == "SetMode" && custom.Mode == "custom" && custom.MinimumRpm == 3000, "--mode custom --rpm N");
        ServiceRequest swapped = Cli("--rpm", "3000", "--mode", "custom");
        Check(swapped.Mode == "custom" && swapped.MinimumRpm == 3000, "option order does not matter");
        ServiceRequest rpm = Cli("--rpm", "2400");
        Check(rpm.Command == "SetMode" && rpm.Mode == "custom" && rpm.MinimumRpm == 2400, "--rpm N is custom with minimum N");
        Check(Cli("--minimum-rpm", "2400").MinimumRpm == 2400, "--minimum-rpm remains an alias of --rpm");
        Check(CliBad("--mode", "custom"), "--mode custom requires --rpm");
        Check(CliBad("--mode", "quiet", "--rpm", "3000") && CliBad("--mode", "auto", "--rpm", "3000"), "--rpm only combines with custom");
        Check(CliBad("--rpm", "999") && CliBad("--rpm", "5571") && CliBad("--rpm", "abc") && CliBad("--mode", "custom", "--rpm", "1e3"), "--rpm range and syntax");
        Check(CliBad("--mode", "full") && CliBad("--mode", "turbo") && CliBad("--mode", "Quiet"), "unknown modes (full is gone)");
        Check(CliBad("--percent", "50"), "--percent is removed");
        Check(CliBad("--start-temp", "65") && CliBad("--full-temp", "85") && CliBad("--configure"), "the curve options are removed");
        Check(CliBad("--rpm", "2000", "--rpm", "3000"), "duplicate option");
        Check(CliBad("--mode", "quiet", "--snapshot"), "one action only");
        Check(Cli("--snapshot") == null && Cli("--monitor", "--seconds", "5") == null && Cli("--test-fan") == null, "other actions do not produce a mode request");
        // --help describes the new options and not the removed ones.
        TextWriter original = Console.Out; StringWriter text = new StringWriter();
        try { Console.SetOut(text); Check(ClientCommands.Run(new[] { "--help" }) == 0, "--help exit code"); } finally { Console.SetOut(original); }
        string help = text.ToString();
        Check(help.Contains("--mode quiet|balanced|performance|custom|auto") && help.Contains("--rpm"), "--help documents --mode and --rpm: " + help);
        Check(!help.Contains("--start-temp") && !help.Contains("--full-temp") && !help.Contains("--percent") && !help.Contains("--configure"), "--help still mentions removed options");
    }

    // / F6 pure parts of the service runtime.
    static void ServiceGuards()
    {
        Check(ThermalServiceRuntime.CapabilityRefusal(new CapabilityStatus { State = CapabilityStates.Passed }) == null, "F3 a passed capability allows Probe and modes");
        foreach (string state in new[] { CapabilityStates.Unknown, CapabilityStates.ReadPassed, CapabilityStates.Failed, CapabilityStates.Unsupported })
            Check(ThermalServiceRuntime.CapabilityRefusal(new CapabilityStatus { State = state }) != null, "F3 " + state + " must refuse the Probe");
        Check(ThermalServiceRuntime.CapabilityRefusal(null) != null, "F3 no capability result yet refuses");
        // Suspend waits for the worker only while there is one; a runtime that never started must not hang or throw
        ThermalServiceRuntime idle = new ThermalServiceRuntime();
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        Check(!idle.Power(true, 150) && watch.ElapsedMilliseconds >= 100 && watch.ElapsedMilliseconds < 2500, "F6 Suspend waits at most the given time for the release");
        Check(idle.Power(false) && idle.Power(true, 0), "F6 resume and no-wait calls return at once");
    }

    static int Main()
    {
        try
        {
            ModeTable(); RampsUpFiftyPerSecondToTheMax(); HoldsInsideTheBand(); CoolsAfterTwentyStableSeconds(); InterruptedCoolingRestartsTheTwentySeconds();
            EmergencyAtNinetyReleasesAtEightyFive(); TickJitterDoesNotSkipOrDoubleSteps(); LimitsChangeClampsTheTarget(); TakeoverStartsAtTheMeasuredSpeed(); ModeSwitchWhileCoolGoesStraightToTheNewMinimum(); ReviewFixes(); StoreRobustness(); SafetyThresholds();
            SettingsModelAndRoundTrip(); OldSchemaIsRejected(); SetModeValidation(); SnapshotCarriesTheMode(); CommandLine();
            ServiceGuards();
            Console.WriteLine("PASS " + passed + " mode checks: limits table, +50/s ramp, hold band, 20 s stable then -100/s, 90/85 protection, quiet cap, auto has no limits, settings migration (temp folders), SetMode validation, CLI parsing.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); Console.Error.WriteLine(e.StackTrace); return 1; }
    }
}

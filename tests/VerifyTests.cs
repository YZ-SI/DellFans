using System;
using System.Collections.Generic;
using System.Linq;
using DellFans;

// Offline: the pure analysis behind `--verify-sensors` on hand-made fixtures. No sensors, no pipe, no HWiNFO, no firmware.
internal static class VerifyTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static void Throws(Action action, string message)
    {
        try { action(); } catch (Exception) { passed++; return; }
        throw new Exception(message);
    }

    // `count` ticks every 0.25 s; the package is `package` plus the offset function.
    static List<VerifyTick> Ticks(int count, string source, Func<int, double> package, Func<int, double?> hw = null, Func<int, double?> esif = null, double[] cores = null)
    {
        List<VerifyTick> list = new List<VerifyTick>();
        for (int i = 0; i < count; i++)
        {
            double p = package(i);
            list.Add(new VerifyTick { T = i * 0.25, Source = source, Package = p, AcquiredStamp = "stamp-" + (i / 4), // a new acquisition every second
                Cores = cores, CoreMax = cores == null ? (double?)null : cores.Max(), CoreAverage = cores == null ? (double?)null : cores.Average(),
                HwPackage = hw == null ? null : hw(i), EsifPackage = esif == null ? null : esif(i) });
        }
        return list;
    }
    static VerifyItem Item(VerifyReport report, string startsWith) { return report.Items.First(i => i.Name.StartsWith(startsWith)); }
    static List<VerifyServiceSample> Service(int count, double age, double package, bool controlling = false)
    {
        List<VerifyServiceSample> list = new List<VerifyServiceSample>();
        for (int i = 0; i < count; i++)
            list.Add(new VerifyServiceSample { T = i, Reachable = true, Package = package, Stamp = "svc-" + i, AgeSeconds = age, AcquireToSnapshotSeconds = age / 2, SnapshotToClientSeconds = age / 4, RoundTripSeconds = 0.01, Controlling = controlling });
        return list;
    }

    static void Statistics()
    {
        Stat s = SensorVerifier.Describe(new double[] { 1, 2, 3, 4, 100 });
        Check(s.Count == 5 && s.Median == 3 && s.Mean == 22 && s.Max == 100 && Math.Abs(s.P95.Value - 80.8) < 1e-9, "median/mean/p95/max: " + s.Median + "/" + s.Mean + "/" + s.P95 + "/" + s.Max);
        Check(SensorVerifier.Describe(new double[0]).Count == 0 && SensorVerifier.Describe(new[] { double.NaN, 5 }).Count == 1, "empty and NaN input");
        // refresh: a new stamp every second, sampled at 250 ms
        double? one = SensorVerifier.RefreshInterval(Ticks(40, "x", i => 50).Select(t => new KeyValuePair<double, string>(t.T, t.AcquiredStamp)));
        Check(one.HasValue && Math.Abs(one.Value - 1.0) < 1e-9, "1 s refresh: " + one);
        double? fast = SensorVerifier.RefreshInterval(Ticks(40, "x", i => 50).Select(t => new KeyValuePair<double, string>(t.T, "k" + (int)(t.T / 0.25))));
        Check(Math.Abs(fast.Value - 0.25) < 1e-9, "every tick is a new acquisition: " + fast);
        Check(SensorVerifier.RefreshInterval(new[] { new KeyValuePair<double, string>(0, "a"), new KeyValuePair<double, string>(1, "a") }) == null, "too few changes give no interval");
    }

    static void Invariants()
    {
        double[] cores = { 60, 62, 64, 66 };
        VerifyReport good = SensorVerifier.Analyze(Ticks(20, "PawnIO IntelMSR", i => 68, null, null, cores), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(good.InvariantChecks == 60 && good.InvariantViolations == 0 && Item(good, "invariants").Result == "PASS", "consistent ticks pass: " + good.InvariantViolations + "/" + good.InvariantChecks);
        List<VerifyTick> bad = Ticks(20, "PawnIO IntelMSR", i => 68, null, null, cores);
        bad[3].CoreMax = 70; bad[4].CoreAverage = 70; bad[5].Package = 80; // core max lies, average lies, package 14 C above core max
        VerifyReport broken = SensorVerifier.Analyze(bad, new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(broken.InvariantViolations == 3 && Item(broken, "invariants").Result == "WARN", "one violation each for core max, average and package spread: " + broken.InvariantViolations);
        VerifyReport none = SensorVerifier.Analyze(Ticks(8, "Intel ESIF", i => 60), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(Item(none, "invariants").Result == "SKIP", "no per-core data (ESIF only) skips the invariants");
    }

    static void StaleEvents()
    {
        List<VerifyTick> ticks = Ticks(10, "PawnIO IntelMSR", i => 60);
        foreach (int i in new[] { 2, 3, 6 }) ticks[i].Stale = true;
        VerifyReport report = SensorVerifier.Analyze(ticks, new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(report.StaleEvents == 2 && report.FirstStaleSeconds == 0.5 && Item(report, "stale events").Result == "WARN" && report.Overall == "WARN", "two stale runs, first at 0.5 s");
        Check(SensorVerifier.Analyze(Ticks(10, "x", i => 60), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null).StaleEvents == 0, "no stale events");
    }

    static void HwInfoAndEsif()
    {
        VerifyReport ok = SensorVerifier.Analyze(Ticks(40, "PawnIO IntelMSR", i => 70, i => 70 + (i % 2 == 0 ? 0.5 : -0.5), i => 70 + (i % 4 == 0 ? 1.5 : (double?)null) - (i % 4 == 0 ? 0 : 0)), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(Item(ok, "vs HWiNFO").Result == "PASS" && Item(ok, "vs ESIF").Result == "PASS", "0.5 C HWiNFO median and 1.5 C ESIF mean pass: " + Item(ok, "vs HWiNFO").Detail + " | " + Item(ok, "vs ESIF").Detail);
        VerifyReport off = SensorVerifier.Analyze(Ticks(40, "PawnIO IntelMSR", i => 70, i => 72, i => i % 4 == 0 ? 74.5 : (double?)null), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(Item(off, "vs HWiNFO").Result == "WARN" && Item(off, "vs ESIF").Result == "WARN" && off.Overall == "WARN", "2 C vs HWiNFO and 4.5 C vs ESIF warn");
        VerifyReport edge = SensorVerifier.Analyze(Ticks(40, "PawnIO IntelMSR", i => 70, i => 71, i => i % 4 == 0 ? 72 : (double?)null), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(Item(edge, "vs HWiNFO").Result == "PASS" && Item(edge, "vs ESIF").Result == "PASS", "exactly 1 C and 2 C pass");
        VerifyReport esifOnly = SensorVerifier.Analyze(Ticks(40, "Intel ESIF", i => 70, null, i => 70), new List<VerifyServiceSample>(), new List<VerifyFanSample>(), null);
        Check(Item(esifOnly, "vs ESIF").Result == "SKIP" && Item(esifOnly, "vs HWiNFO").Result == "SKIP" && esifOnly.Overall != "WARN", "ESIF-as-source cannot be compared with itself; HWiNFO absent skips");
    }

    static void ServiceAndLatency()
    {
        VerifyReport fresh = SensorVerifier.Analyze(Ticks(40, "x", i => 70), Service(10, 0.8, 70), new List<VerifyFanSample>(), null);
        Check(Item(fresh, "service age").Result == "PASS" && Item(fresh, "service vs direct").Result == "PASS", "0.8 s old and identical passes");
        Check(Math.Abs(fresh.Latency["sensor -> service snapshot (s)"].Median.Value - 0.4) < 1e-9 && Math.Abs(fresh.Latency["service snapshot -> client (s)"].Median.Value - 0.2) < 1e-9 && fresh.Latency["pipe round trip (s)"].Count == 10, "per-hop latency");
        Check(fresh.RefreshSeconds["service package row"] == 1, "service rows refresh once a second");
        VerifyReport stale = SensorVerifier.Analyze(Ticks(40, "x", i => 70), Service(10, 2.1, 75), new List<VerifyFanSample>(), null);
        Check(Item(stale, "service age").Result == "WARN" && Item(stale, "service vs direct").Result == "WARN", "2.1 s old and 5 C off warn");
        VerifyReport missing = SensorVerifier.Analyze(Ticks(40, "x", i => 70), new List<VerifyServiceSample> { new VerifyServiceSample { T = 0, Reachable = false, Error = "not found" }, new VerifyServiceSample { T = 1, Reachable = false, Error = "not found" } }, new List<VerifyFanSample>(), null);
        Check(Item(missing, "service age").Result == "SKIP" && Item(missing, "service age").Detail.Contains("not found") && missing.ServiceFailures == 2, "service not found is reported, not failed");
        // the p95, not the mean, decides: one slow reply in twenty does not fail, three do
        List<VerifyServiceSample> mostlyFresh = Service(20, 0.5, 70); mostlyFresh[3].AgeSeconds = 3;
        Check(Item(SensorVerifier.Analyze(Ticks(40, "x", i => 70), mostlyFresh, new List<VerifyFanSample>(), null), "service age").Result == "PASS", "one slow outlier passes");
        foreach (int i in new[] { 3, 8, 12 }) mostlyFresh[i].AgeSeconds = 3;
        Check(Item(SensorVerifier.Analyze(Ticks(40, "x", i => 70), mostlyFresh, new List<VerifyFanSample>(), null), "service age").Result == "WARN", "repeated slow replies warn");
    }

    static void Fan()
    {
        List<VerifyFanSample> fans = new List<VerifyFanSample> { new VerifyFanSample { T = 1, Smm = 2500, Bfn = 2540 }, new VerifyFanSample { T = 2, Smm = 2600, Bfn = 2590 }, new VerifyFanSample { T = 3, Smm = 2700, Bfn = 2690 } };
        VerifyReport idle = SensorVerifier.Analyze(Ticks(8, "x", i => 70), Service(3, 0.5, 70, false), fans, null);
        Check(Item(idle, "fan RPM").Result == "PASS" && Item(idle, "fan RPM").Detail.Contains("n=3"), "SMM and BFn agree while the service is idle");
        VerifyReport owned = SensorVerifier.Analyze(Ticks(8, "x", i => 70), Service(3, 0.5, 70, true), fans, null);
        Check(Item(owned, "fan RPM").Result == "SKIP" && Item(owned, "fan RPM").Detail == "skipped: service owns firmware", "service owns the firmware: " + Item(owned, "fan RPM").Detail);
        VerifyReport unknown = SensorVerifier.Analyze(Ticks(8, "x", i => 70), new List<VerifyServiceSample>(), fans, null);
        Check(Item(unknown, "fan RPM").Result == "SKIP" && Item(unknown, "fan RPM").Detail.Contains("firmware not touched"), "no service answer: the tool never touches firmware");
        for (int i = 4; i < 8; i++) fans.Add(new VerifyFanSample { T = i, Smm = 3000, Bfn = 2000 });
        Check(Item(SensorVerifier.Analyze(Ticks(8, "x", i => 70), Service(3, 0.5, 70), fans, null), "fan RPM").Result == "WARN", "SMM and BFn 1000 RPM apart warn");
    }

    static void ReportAndOutputs()
    {
        VerifyReport report = SensorVerifier.Analyze(Ticks(40, "PawnIO IntelMSR", i => 70, i => 70.4, i => i % 4 == 0 ? 71 : (double?)null, new[] { 60.0, 66.0 }), Service(5, 0.6, 70), new List<VerifyFanSample>(), null);
        string json = ReportFiles.Json(new { Report = report });
        object parsed = MiniJson.Parse(json);
        Check(parsed is IDictionary<string, object> && json.Contains("\"Overall\":\"PASS\"") && json.Contains("\"Items\":["), "report serializes through MiniJson");
        string summary = SensorVerifier.Summary(report);
        Check(summary.StartsWith("Sensor verification: PASS") && summary.Contains("PASS  vs HWiNFO") && summary.Contains("refresh") && summary.Contains("latency"), "console summary: " + summary);
        string csv = SensorVerification.Csv(Ticks(3, "PawnIO IntelMSR", i => 70, i => 70.4, i => 71, new[] { 60.0, 66.0 }), Service(2, 0.6, 70), new List<VerifyFanSample> { new VerifyFanSample { T = 1, Smm = 2500, Bfn = 2540 } });
        string[] lines = csv.TrimEnd('\n').Split('\n');
        Check(lines.Length == 1 + 3 + 2 + 1 && lines.All(l => l.Split(',').Length == lines[0].Split(',').Length), "every CSV row has the header's " + lines[0].Split(',').Length + " columns: " + string.Join(" | ", lines.Select(l => l.Split(',').Length.ToString())));
        Check(lines[4].Split(',')[1] == "service" && lines[4].Split(',')[13] == "70" && lines[6].Split(',')[19] == "2500" && lines[6].Split(',')[20] == "2540", "service and fan values land in their columns");
    }

    static void CommandLine()
    {
        Throws(delegate { ClientCommands.Run(new[] { "--verify-sensors", "--seconds", "3" }); }, "--seconds below 5 accepted");
        Throws(delegate { ClientCommands.Run(new[] { "--verify-sensors", "--seconds", "10", "--load", "30" }); }, "--load longer than the run accepted");
        Throws(delegate { ClientCommands.Run(new[] { "--verify-sensors", "--rpm", "2000" }); }, "verify with a mode accepted");
        Throws(delegate { ClientCommands.Run(new[] { "--verify-sensors", "--snapshot" }); }, "verify with another action accepted");
        Throws(delegate { ClientCommands.Run(new[] { "--load", "10", "--snapshot" }); }, "--load outside --verify-sensors accepted");
    }

    static int Main()
    {
        try
        {
            Statistics(); Invariants(); StaleEvents(); HwInfoAndEsif(); ServiceAndLatency(); Fan(); ReportAndOutputs(); CommandLine();
            Console.WriteLine("PASS " + passed + " verify checks (fixtures only): percentiles, refresh intervals, invariants, stale events, HWiNFO/ESIF thresholds, service age p95 and hops, fan skip rules, report/CSV, argument checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); Console.Error.WriteLine(e.StackTrace); return 1; }
    }
}

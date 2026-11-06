using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DellFans
{
    // docs/SENSOR-VERIFICATION.md: pure analysis of what `DellFans.Cli.exe --verify-sensors` collected. No clocks, no hardware, no files.
    public sealed class VerifyTick
    {
        public double T;                 // seconds since the run started (monotonic)
        public string Source;            // sensor layer source ("PawnIO IntelMSR", "Intel ESIF", "None")
        public bool Stale;
        public double? Package, CoreMax, CoreAverage, Control, Protection;
        public double[] Cores;           // per physical core (NaN = invalid); null when the source has no per-core data
        public string AcquiredStamp;     // PolledAtUtc of the package row: a new stamp = a new acquisition
        public double? EsifPackage;      // independent ESIF read on this tick, otherwise null
        public double? HwPackage, HwCoreMax, HwCoreAverage; // HWiNFO shared memory on this tick, otherwise null
    }

    public sealed class VerifyServiceSample
    {
        public double T;
        public bool Reachable;
        public string Error;
        public double? Package, CoreMax, CoreAverage;
        public string Stamp;                     // PolledAtUtc of the package row
        public double? AgeSeconds;               // client receive time - row acquisition time
        public double? AcquireToSnapshotSeconds; // snapshot build time - row acquisition time (service-side wait)
        public double? SnapshotToClientSeconds;  // client receive time - snapshot build time (pipe + queueing)
        public double? RoundTripSeconds;         // request sent -> reply read
        public bool Controlling;                 // the service owns the fan, so firmware is off limits for this tool
    }

    public sealed class VerifyFanSample { public double T; public int Smm, Bfn; }

    public sealed class VerifyThresholds
    {
        public double HwInfoMedianC = 1, EsifMeanC = 2, ServiceAgeSeconds = 1.5, PackageToCoreMaxC = 6, RoundingToleranceC = 0.15, FanRpm = 200;
    }

    public sealed class VerifyItem
    {
        public string Name { get; set; }
        public string Result { get; set; } // PASS, WARN or SKIP
        public string Detail { get; set; }
    }

    public sealed class Stat
    {
        public int Count { get; set; }
        public double? Mean { get; set; }
        public double? Median { get; set; }
        public double? P95 { get; set; }
        public double? Max { get; set; }
    }

    public sealed class VerifyReport
    {
        public string Overall { get; set; }
        public List<VerifyItem> Items { get; set; }
        public Dictionary<string, double?> RefreshSeconds { get; set; }
        public Dictionary<string, Stat> Differences { get; set; }
        public Dictionary<string, Stat> Latency { get; set; }
        public int InvariantChecks { get; set; }
        public int InvariantViolations { get; set; }
        public int StaleEvents { get; set; }
        public double? FirstStaleSeconds { get; set; }
        public int LayerSamples { get; set; }
        public int ServiceSamples { get; set; }
        public int ServiceFailures { get; set; }
    }

    public static class SensorVerifier
    {
        public static Stat Describe(IEnumerable<double> values)
        {
            List<double> v = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToList();
            if (v.Count == 0) return new Stat();
            return new Stat { Count = v.Count, Mean = v.Average(), Median = Percentile(v, 0.5), P95 = Percentile(v, 0.95), Max = v[v.Count - 1] };
        }
        // Linear interpolation between closest ranks.
        public static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 1) return sorted[0];
            double rank = p * (sorted.Count - 1); int low = (int)Math.Floor(rank); int high = Math.Min(sorted.Count - 1, low + 1);
            return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
        }
        // Median time between first appearances of a new key (a new acquisition stamp, or a changed value).
        public static double? RefreshInterval(IEnumerable<KeyValuePair<double, string>> series)
        {
            List<double> starts = new List<double>(); string last = null; bool any = false;
            foreach (KeyValuePair<double, string> point in series)
            {
                if (point.Value == null) continue;
                if (!any || point.Value != last) starts.Add(point.Key);
                last = point.Value; any = true;
            }
            if (starts.Count < 3) return null; // the first interval is cut by the start of the run
            List<double> gaps = new List<double>();
            for (int i = 2; i < starts.Count; i++) gaps.Add(starts[i] - starts[i - 1]);
            gaps.Sort(); return Percentile(gaps, 0.5);
        }
        static string F(double? value, string unit) { return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) + unit : "n/a"; }

        // The reading of `series` closest in time to `t`, within `window` seconds.
        static double? Nearest(List<KeyValuePair<double, double>> series, double t, double window)
        {
            double best = double.MaxValue; double? value = null;
            foreach (KeyValuePair<double, double> point in series)
            {
                double distance = Math.Abs(point.Key - t);
                if (distance < best) { best = distance; value = point.Value; }
            }
            return best <= window ? value : null;
        }

        public static VerifyReport Analyze(List<VerifyTick> ticks, List<VerifyServiceSample> service, List<VerifyFanSample> fans, VerifyThresholds limits)
        {
            limits = limits ?? new VerifyThresholds();
            VerifyReport report = new VerifyReport { Items = new List<VerifyItem>(), RefreshSeconds = new Dictionary<string, double?>(),
                Differences = new Dictionary<string, Stat>(), Latency = new Dictionary<string, Stat>(), LayerSamples = ticks.Count, ServiceSamples = service.Count };
            string source = ticks.Select(t => t.Source).FirstOrDefault(s => s != null && s != "None") ?? "None";
            bool layerIsEsif = source.IndexOf("ESIF", StringComparison.OrdinalIgnoreCase) >= 0;

            // ---- refresh intervals per source ----
            report.RefreshSeconds["layer (" + source + ")"] = RefreshInterval(ticks.Select(t => new KeyValuePair<double, string>(t.T, t.Stale ? null : t.AcquiredStamp)));
            report.RefreshSeconds["ESIF direct (value changes)"] = RefreshInterval(ticks.Where(t => t.EsifPackage.HasValue).Select(t => new KeyValuePair<double, string>(t.T, t.EsifPackage.Value.ToString("R", CultureInfo.InvariantCulture))));
            report.RefreshSeconds["HWiNFO (value changes)"] = RefreshInterval(ticks.Where(t => t.HwPackage.HasValue).Select(t => new KeyValuePair<double, string>(t.T, t.HwPackage.Value.ToString("R", CultureInfo.InvariantCulture))));
            report.RefreshSeconds["service package row"] = RefreshInterval(service.Where(s => s.Reachable).Select(s => new KeyValuePair<double, string>(s.T, s.Stamp)));

            // ---- invariants on every layer tick that has per-core data ----
            int checks = 0, violations = 0;
            foreach (VerifyTick tick in ticks)
            {
                if (tick.Stale) continue;
                double[] valid = tick.Cores == null ? new double[0] : tick.Cores.Where(c => !double.IsNaN(c)).ToArray();
                if (valid.Length > 0 && tick.CoreMax.HasValue) { checks++; if (Math.Abs(tick.CoreMax.Value - valid.Max()) > limits.RoundingToleranceC) violations++; }
                if (valid.Length > 0 && tick.CoreAverage.HasValue) { checks++; if (Math.Abs(tick.CoreAverage.Value - valid.Average()) > limits.RoundingToleranceC) violations++; }
                if (tick.Package.HasValue && tick.CoreMax.HasValue) { checks++; if (Math.Abs(tick.Package.Value - tick.CoreMax.Value) > limits.PackageToCoreMaxC) violations++; }
            }
            report.InvariantChecks = checks; report.InvariantViolations = violations;

            // ---- stale events: runs of stale ticks ----
            bool wasStale = false;
            foreach (VerifyTick tick in ticks)
            {
                if (tick.Stale && !wasStale) { report.StaleEvents++; if (!report.FirstStaleSeconds.HasValue) report.FirstStaleSeconds = tick.T; }
                wasStale = tick.Stale;
            }

            // ---- accuracy ----
            List<double> hw = ticks.Where(t => !t.Stale && t.Package.HasValue && t.HwPackage.HasValue).Select(t => Math.Abs(t.Package.Value - t.HwPackage.Value)).ToList();
            report.Differences["layer package vs HWiNFO (abs)"] = Describe(hw);
            report.Differences["layer core max vs HWiNFO (abs)"] = Describe(ticks.Where(t => !t.Stale && t.CoreMax.HasValue && t.HwCoreMax.HasValue).Select(t => Math.Abs(t.CoreMax.Value - t.HwCoreMax.Value)));
            report.Differences["layer core average vs HWiNFO (abs)"] = Describe(ticks.Where(t => !t.Stale && t.CoreAverage.HasValue && t.HwCoreAverage.HasValue).Select(t => Math.Abs(t.CoreAverage.Value - t.HwCoreAverage.Value)));
            List<double> esif = ticks.Where(t => !t.Stale && t.Package.HasValue && t.EsifPackage.HasValue).Select(t => Math.Abs(t.Package.Value - t.EsifPackage.Value)).ToList();
            report.Differences["layer package vs ESIF (abs)"] = Describe(esif);
            List<KeyValuePair<double, double>> direct = ticks.Where(t => !t.Stale && t.Package.HasValue).Select(t => new KeyValuePair<double, double>(t.T, t.Package.Value)).ToList();
            List<double> serviceDiff = new List<double>();
            foreach (VerifyServiceSample s in service)
            {
                if (!s.Reachable || !s.Package.HasValue) continue;
                double? other = Nearest(direct, s.T, 0.5);
                if (other.HasValue) serviceDiff.Add(Math.Abs(s.Package.Value - other.Value));
            }
            report.Differences["service package vs direct layer (abs)"] = Describe(serviceDiff);

            // ---- latency per hop (service) ----
            IEnumerable<VerifyServiceSample> reached = service.Where(s => s.Reachable);
            report.Latency["service age at client (s)"] = Describe(reached.Where(s => s.AgeSeconds.HasValue).Select(s => s.AgeSeconds.Value));
            report.Latency["sensor -> service snapshot (s)"] = Describe(reached.Where(s => s.AcquireToSnapshotSeconds.HasValue).Select(s => s.AcquireToSnapshotSeconds.Value));
            report.Latency["service snapshot -> client (s)"] = Describe(reached.Where(s => s.SnapshotToClientSeconds.HasValue).Select(s => s.SnapshotToClientSeconds.Value));
            report.Latency["pipe round trip (s)"] = Describe(reached.Where(s => s.RoundTripSeconds.HasValue).Select(s => s.RoundTripSeconds.Value));
            report.ServiceFailures = service.Count(s => !s.Reachable);

            // ---- verdict ----
            if (hw.Count == 0) Skip(report, "vs HWiNFO", "HWiNFO shared memory (Global\\HWiNFO_SENS_SM2) not found or no overlapping samples; enable SensorsSM=1 in HWiNFO64.INI");
            else Judge(report, "vs HWiNFO (package, median |diff| <= " + limits.HwInfoMedianC.ToString("0.#", CultureInfo.InvariantCulture) + " C)", report.Differences["layer package vs HWiNFO (abs)"].Median <= limits.HwInfoMedianC,
                "median " + F(report.Differences["layer package vs HWiNFO (abs)"].Median, " C") + ", p95 " + F(report.Differences["layer package vs HWiNFO (abs)"].P95, " C") + ", n=" + hw.Count);
            if (layerIsEsif) Skip(report, "vs ESIF", "the sensor layer itself uses ESIF here (no PawnIO IntelMSR), so there is no independent second path");
            else if (esif.Count == 0) Skip(report, "vs ESIF", "no ESIF readings (Intel Innovation Platform Framework driver absent)");
            else Judge(report, "vs ESIF (package, mean |diff| <= " + limits.EsifMeanC.ToString("0.#", CultureInfo.InvariantCulture) + " C)", report.Differences["layer package vs ESIF (abs)"].Mean <= limits.EsifMeanC,
                "mean " + F(report.Differences["layer package vs ESIF (abs)"].Mean, " C") + ", n=" + esif.Count);
            if (!reached.Any()) Skip(report, "service age", service.Count == 0 ? "service: not found" : "service: " + (service.Select(s => s.Error).FirstOrDefault(e => e != null) ?? "not reachable"));
            else
            {
                Stat age = report.Latency["service age at client (s)"];
                Judge(report, "service age (p95 <= " + limits.ServiceAgeSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)", age.Count > 0 && age.P95 <= limits.ServiceAgeSeconds,
                    "median " + F(age.Median, " s") + ", p95 " + F(age.P95, " s") + ", max " + F(age.Max, " s") + ", n=" + age.Count);
                Stat sd = report.Differences["service package vs direct layer (abs)"];
                if (sd.Count == 0) Skip(report, "service vs direct", "no overlapping package readings");
                else Judge(report, "service vs direct (mean |diff| <= " + limits.EsifMeanC.ToString("0.#", CultureInfo.InvariantCulture) + " C)", sd.Mean <= limits.EsifMeanC,
                    "mean " + F(sd.Mean, " C") + ", median " + F(sd.Median, " C") + ", max " + F(sd.Max, " C") + ", n=" + sd.Count);
            }
            if (checks == 0) Skip(report, "invariants", "no per-core data from this source");
            else Judge(report, "invariants (core max = max cores, average = mean, package within " + limits.PackageToCoreMaxC.ToString("0", CultureInfo.InvariantCulture) + " C of core max)", violations == 0, violations + " violations in " + checks + " checks");
            Judge(report, "stale events", report.StaleEvents == 0, report.StaleEvents + " stale run(s)" + (report.FirstStaleSeconds.HasValue ? ", first at " + F(report.FirstStaleSeconds, " s") : ""));

            // ---- fan: SMM vs BFn, only while the service does not own the firmware ----
            if (service.Count > 0 && service.Any(s => s.Reachable && s.Controlling)) Skip(report, "fan RPM SMM vs BFn", "skipped: service owns firmware");
            else if (!service.Any(s => s.Reachable)) Skip(report, "fan RPM SMM vs BFn", "skipped: service state unknown, firmware not touched");
            else if (fans.Count == 0) Skip(report, "fan RPM SMM vs BFn", "no fan readings");
            else
            {
                Stat fan = Describe(fans.Select(f => (double)Math.Abs(f.Smm - f.Bfn)));
                Judge(report, "fan RPM SMM vs BFn (median |diff| <= " + limits.FanRpm.ToString("0", CultureInfo.InvariantCulture) + " RPM)", fan.Median <= limits.FanRpm, "median " + F(fan.Median, " RPM") + ", max " + F(fan.Max, " RPM") + ", n=" + fans.Count);
            }
            report.Overall = report.Items.Any(i => i.Result == "WARN") ? "WARN" : report.Items.Any(i => i.Result == "PASS") ? "PASS" : "SKIP";
            return report;
        }
        static void Judge(VerifyReport report, string name, bool ok, string detail) { report.Items.Add(new VerifyItem { Name = name, Result = ok ? "PASS" : "WARN", Detail = detail }); }
        static void Skip(VerifyReport report, string name, string detail) { report.Items.Add(new VerifyItem { Name = name, Result = "SKIP", Detail = detail }); }

        public static string Summary(VerifyReport report)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.AppendLine("Sensor verification: " + report.Overall + "  (layer " + report.LayerSamples + " samples, service " + report.ServiceSamples + " samples, " + report.ServiceFailures + " failed)");
            foreach (VerifyItem item in report.Items) text.AppendLine("  " + item.Result.PadRight(4) + "  " + item.Name + " -- " + item.Detail);
            foreach (KeyValuePair<string, double?> refresh in report.RefreshSeconds) text.AppendLine("  refresh  " + refresh.Key + ": " + F(refresh.Value, " s"));
            foreach (KeyValuePair<string, Stat> latency in report.Latency) if (latency.Value.Count > 0) text.AppendLine("  latency  " + latency.Key + ": median " + F(latency.Value.Median, "") + ", p95 " + F(latency.Value.P95, "") + ", max " + F(latency.Value.Max, ""));
            return text.ToString().TrimEnd();
        }
    }
}

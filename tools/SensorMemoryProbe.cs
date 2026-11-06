using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using DellFans;

// Read-only resident probe of the in-house sensor layer: starts the sampler threads, polls at the service cadence (2 s),
// serializes snapshots the way the service does and reports private memory, poll time, per-source acquisition time and
// the freshness of the last readings. It never constructs the Dell SMM/BIOS backend, so it can run beside the live service.
internal static class SensorMemoryProbe
{
    static int Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 120;
        List<long> privateBytes = new List<long>();
        List<double> pollMs = new List<double>();
        SensorReadout last = null; List<SensorRow> lastRows = null; List<string> lastWarnings = null;
        int fullChars = 0, replyChars = 0, polls = 0;
        Stopwatch clock = Stopwatch.StartNew();
        using (Process self = Process.GetCurrentProcess())
        using (SensorLayer layer = SensorLayer.CreateDefault())
        {
            Stopwatch start = Stopwatch.StartNew();
            layer.Start();
            double startMs = start.Elapsed.TotalMilliseconds;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Stopwatch poll = Stopwatch.StartNew();
                List<SensorRow> rows = new List<SensorRow>(); List<string> warnings = new List<string>();
                SensorReadout readout = layer.Read(rows, warnings);
                Snapshot sample = new Snapshot { PolledAtUtc = DateTime.UtcNow.ToString("o"), Sensors = rows, Warnings = warnings,
                    CpuControlTemperatureC = readout.ControlTemperatureC, CpuProtectionTemperatureC = readout.ProtectionTemperatureC,
                    SensorSource = readout.Source, SensorDriverState = readout.DriverState, SensorTimings = readout.Timings };
                fullChars = ReportFiles.Json(sample).Length;
                replyChars = ReportFiles.Json(sample.Summary()).Length;
                pollMs.Add(poll.Elapsed.TotalMilliseconds);
                last = readout; lastRows = rows; lastWarnings = warnings; polls++;
                self.Refresh(); privateBytes.Add(self.PrivateMemorySize64);
                Thread.Sleep(2000);
            }
            List<long> settled = privateBytes.Skip(privateBytes.Count / 2).OrderBy(v => v).ToList();
            List<double> sorted = pollMs.OrderBy(v => v).ToList();
            Console.WriteLine("layerStartMs={0:0.0} polls={1} rows={2} fullJsonChars={3} replyJsonChars={4}", startMs, polls, lastRows.Count, fullChars, replyChars);
            Console.WriteLine("privateMiB median(2nd half)={0:0.0} max={1:0.0} workingSetMiB={2:0.0} threads={3}",
                settled[settled.Count / 2] / 1048576.0, privateBytes.Max() / 1048576.0, self.WorkingSet64 / 1048576.0, self.Threads.Count);
            Console.WriteLine("readPollMs median={0:0.00} max={1:0.00}", sorted[sorted.Count / 2], sorted.Last());
            Console.WriteLine("source={0} driver={1} control={2} protection={3}", last.Source, last.DriverState,
                last.ControlTemperatureC.HasValue ? last.ControlTemperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture) : "null",
                last.ProtectionTemperatureC.HasValue ? last.ProtectionTemperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture) : "null");
            foreach (KeyValuePair<string, double> t in last.Timings) Console.WriteLine("acquire {0}: {1:0.00} ms", t.Key, t.Value);
            foreach (SensorRow row in lastRows.Where(r => SensorRoles.IsSummary(r)))
                Console.WriteLine("row {0} | {1} | {2} = {3} {4} | {5} | acquired {6:0.0} s ago", row.Id, row.Device, row.Name,
                    row.Value.HasValue ? row.Value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "null", row.Quality, row.Source,
                    row.PolledAtUtc == null ? double.NaN : (DateTime.UtcNow - DateTime.Parse(row.PolledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()).TotalSeconds);
            foreach (string warning in lastWarnings) Console.WriteLine("warning: " + warning);
        }
        return 0;
    }
}

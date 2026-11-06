using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;

namespace DellFans
{
    // HWiNFO shared memory (Global\HWiNFO_SENS_SM2, SensorsSM=1 in HWiNFO64.INI), read-only.
    public sealed class HwInfoReader : IDisposable
    {
        MemoryMappedFile map; MemoryMappedViewAccessor view;
        public static HwInfoReader TryOpen()
        {
            try
            {
                HwInfoReader reader = new HwInfoReader();
                reader.map = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
                reader.view = reader.map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (reader.view.ReadUInt32(0) != 0x53695748) { reader.Dispose(); return null; }
                return reader;
            }
            catch (Exception) { return null; }
        }
        static string Str(MemoryMappedViewAccessor v, long at, int max)
        {
            byte[] b = new byte[max]; v.ReadArray(at, b, 0, max);
            int n = Array.IndexOf(b, (byte)0); return Encoding.Default.GetString(b, 0, n < 0 ? max : n);
        }
        // CPU package, core max, core average from the DTS temperature group.
        public void Read(out double? package, out double? coreMax, out double? coreAverage)
        {
            package = coreMax = coreAverage = null;
            uint sensorOffset = view.ReadUInt32(20), sensorSize = view.ReadUInt32(24), sensorCount = view.ReadUInt32(28);
            uint readingOffset = view.ReadUInt32(32), readingSize = view.ReadUInt32(36), readingCount = view.ReadUInt32(40);
            string[] sensors = new string[sensorCount];
            for (int i = 0; i < sensorCount; i++) sensors[i] = Str(view, sensorOffset + i * sensorSize + 8, 128); // szSensorNameOrig: untranslated, unlike the user-visible name
            List<double> cores = new List<double>();
            for (int i = 0; i < readingCount; i++)
            {
                long b = readingOffset + (long)i * readingSize;
                if (view.ReadUInt32(b) != 1) continue; // temperatures only
                uint index = view.ReadUInt32(b + 4);
                string sensor = index < sensorCount ? sensors[index] : "?";
                if (sensor.IndexOf("DTS", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // szLabelOrig ("CPU Package"), not szLabelUser which follows the HWiNFO UI language
                string label = Str(view, b + 12, 128); double value = view.ReadDouble(b + 12 + 256 + 16);
                if (label == "CPU Package") package = value;
                else if (label == "Core Max") coreMax = value;
                else if (System.Text.RegularExpressions.Regex.IsMatch(label, @"^(P-core|E-core|Core) \d+$")) cores.Add(value);
            }
            if (cores.Count > 0) coreAverage = cores.Average();
        }
        public void Dispose() { if (view != null) view.Dispose(); if (map != null) map.Dispose(); view = null; map = null; }
    }

    // `DellFans.Cli.exe --verify-sensors [--seconds 120] [--load 30] [--output dir]`. Read-only: the sensor layer runs in this process at
    // 250 ms, the service is asked for Status (Detail:true) once a second, and optionally every core is loaded to move the temperature.
    // Firmware (SMM/BFn fan reads) is touched only when the service answered and does not own the fan.
    public static class SensorVerification
    {
        public static int Run(int seconds, int loadSeconds, string outputDirectory, TextWriter console)
        {
            if (seconds < 5 || seconds > 3600) throw new ArgumentOutOfRangeException("seconds", "--seconds must be 5-3600.");
            if (loadSeconds < 0 || loadSeconds > seconds) throw new ArgumentOutOfRangeException("load", "--load must be 0 to --seconds.");
            Directory.CreateDirectory(outputDirectory);
            List<VerifyTick> ticks = new List<VerifyTick>();
            List<VerifyServiceSample> service = new List<VerifyServiceSample>();
            List<VerifyFanSample> fans = new List<VerifyFanSample>();
            object gate = new object();
            Stopwatch clock = Stopwatch.StartNew();
            DateTime started = DateTime.UtcNow;
            string cpuName = null, driver = null, sensorSource = "None";
            bool hwInfo = false;
            ManualResetEvent stop = new ManualResetEvent(false), loadStop = new ManualResetEvent(false);
            List<Thread> burners = new List<Thread>();

            // ---- service sampler: Status(Detail:true) once a second; the pipe call may block for seconds, so it has its own thread ----
            Thread serviceThread = new Thread(delegate()
            {
                DellFanBackend backend = null; DellSmmFan smm = null; bool fanFailed = false;
                try
                {
                    double next = 0;
                    while (!stop.WaitOne(0))
                    {
                        double now = clock.Elapsed.TotalSeconds;
                        if (now < next) { Thread.Sleep(20); continue; }
                        next = Math.Max(next + 1, now);
                        VerifyServiceSample sample = new VerifyServiceSample { T = now };
                        Snapshot reply = null;
                        try
                        {
                            Stopwatch trip = Stopwatch.StartNew();
                            reply = ThermalClient.Send(new ServiceRequest { Command = "Status", Detail = true });
                            DateTime received = DateTime.UtcNow; sample.RoundTripSeconds = trip.Elapsed.TotalSeconds;
                            if (reply == null) throw new InvalidOperationException("service sensors are not ready");
                            sample.Reachable = true;
                            SensorRow package = reply.Sensors == null ? null : reply.Sensors.FirstOrDefault(r => r.Id == "cpu/package");
                            SensorRow coreMax = reply.Sensors == null ? null : reply.Sensors.FirstOrDefault(r => r.Id == "cpu/core-max");
                            SensorRow coreAverage = reply.Sensors == null ? null : reply.Sensors.FirstOrDefault(r => r.Id == "cpu/core-average");
                            sample.Package = package != null && package.Value.HasValue ? (double?)package.Value.Value : null;
                            sample.CoreMax = coreMax != null && coreMax.Value.HasValue ? (double?)coreMax.Value.Value : null;
                            sample.CoreAverage = coreAverage != null && coreAverage.Value.HasValue ? (double?)coreAverage.Value.Value : null;
                            DateTime acquired, built;
                            if (package != null && package.PolledAtUtc != null && DateTime.TryParse(package.PolledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out acquired))
                            {
                                sample.Stamp = package.PolledAtUtc;
                                sample.AgeSeconds = (received - acquired.ToUniversalTime()).TotalSeconds;
                                if (reply.PolledAtUtc != null && DateTime.TryParse(reply.PolledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out built))
                                {
                                    sample.AcquireToSnapshotSeconds = (built.ToUniversalTime() - acquired.ToUniversalTime()).TotalSeconds;
                                    sample.SnapshotToClientSeconds = (received - built.ToUniversalTime()).TotalSeconds;
                                }
                            }
                            sample.Controlling = (reply.ManualControl != null && reply.ManualControl.Active) ||
                                (reply.Background != null && (reply.Background.EffectiveTargetRpm.HasValue || reply.Background.State == "EmergencyFullSpeed" || reply.Background.State == "Probing"));
                        }
                        catch (ServiceNotRunningException) { sample.Error = "not found"; }
                        catch (Exception e) { sample.Error = e.GetBaseException().Message; }
                        lock (gate) service.Add(sample);
                        // Firmware reads only when the service answered and says it does not own the fan.
                        if (sample.Reachable && !sample.Controlling && !fanFailed && reply.Model != null)
                        {
                            try
                            {
                                if (backend == null) { backend = new DellFanBackend(); smm = new DellSmmFan(backend.Model, backend.BiosVersion, true); }
                                List<SensorRow> bfn = backend.ReadFans();
                                SensorRow main = bfn.FirstOrDefault(r => r.Id == "dell/fan/F600");
                                if (main != null && main.Value.HasValue) lock (gate) fans.Add(new VerifyFanSample { T = clock.Elapsed.TotalSeconds, Smm = smm.ReadRpm(), Bfn = (int)main.Value.Value });
                            }
                            catch (Exception) { fanFailed = true; }
                        }
                    }
                }
                finally { if (smm != null) smm.Dispose(); if (backend != null) backend.Dispose(); }
            }) { IsBackground = true, Name = "Verify service sampler" };

            using (HwInfoReader hw = HwInfoReader.TryOpen())
            using (SensorLayer layer = SensorLayer.CreateDefault())
            {
                hwInfo = hw != null;
                ICpuTemperatureSource esif = null;
                try { esif = EsifSource.Create(delegate { return DateTime.UtcNow; }); } catch (Exception) { }
                layer.Start();
                serviceThread.Start();
                try
                {
                    double loadStart = loadSeconds > 0 ? Math.Min(10, seconds / 4.0) : double.MaxValue;
                    bool loading = false; int tickNumber = 0;
                    while (clock.Elapsed.TotalSeconds < seconds)
                    {
                        double now = clock.Elapsed.TotalSeconds;
                        VerifyTick tick = new VerifyTick { T = now };
                        List<SensorRow> rows = new List<SensorRow>(); List<string> warnings = new List<string>();
                        SensorReadout readout = layer.Read(rows, warnings);
                        tick.Source = readout.Source; tick.Control = readout.ControlTemperatureC; tick.Protection = readout.ProtectionTemperatureC;
                        sensorSource = readout.Source ?? sensorSource; driver = readout.DriverState;
                        SensorRow package = rows.FirstOrDefault(r => r.Id == "cpu/package");
                        SensorRow coreMax = rows.FirstOrDefault(r => r.Id == "cpu/core-max");
                        SensorRow coreAverage = rows.FirstOrDefault(r => r.Id == "cpu/core-average");
                        List<SensorRow> coreRows = rows.Where(r => r.Id != null && r.Id.StartsWith("cpu/core/", StringComparison.Ordinal)).ToList();
                        IEnumerable<SensorRow> cpuRows = rows.Where(r => r.HardwareType == "Cpu");
                        tick.Stale = cpuRows.Any() && cpuRows.Any(r => r.Quality == "Stale");
                        if (!tick.Stale)
                        {
                            tick.Package = package != null && package.Value.HasValue ? (double?)package.Value.Value : null;
                            tick.CoreMax = coreMax != null && coreMax.Value.HasValue ? (double?)coreMax.Value.Value : null;
                            tick.CoreAverage = coreAverage != null && coreAverage.Value.HasValue ? (double?)coreAverage.Value.Value : null;
                            if (coreRows.Count > 0) tick.Cores = coreRows.Where(r => r.Value.HasValue).Select(r => (double)r.Value.Value).ToArray();
                        }
                        if (package != null) { tick.AcquiredStamp = package.PolledAtUtc; if (cpuName == null) cpuName = package.Device; }
                        if (esif != null && tickNumber % 4 == 0) { try { CpuSample s = esif.Read(); tick.EsifPackage = s.Package; } catch (Exception) { } }
                        if (hw != null)
                        {
                            try { double? p, m, a; hw.Read(out p, out m, out a); tick.HwPackage = p; tick.HwCoreMax = m; tick.HwCoreAverage = a; } catch (Exception) { }
                        }
                        lock (gate) ticks.Add(tick);
                        // All-core load for --load seconds, from a few seconds in; abandoned at once if the package gets hot (85 C, the release threshold).
                        double hottest = Math.Max(tick.Package ?? 0, tick.Protection ?? 0);
                        if (!loading && now >= loadStart && now < loadStart + loadSeconds)
                        {
                            loading = true; int cpus = Environment.ProcessorCount;
                            for (int i = 0; i < cpus; i++)
                            {
                                Thread burner = new Thread(delegate() { double x = 1; while (!loadStop.WaitOne(0)) { for (int n = 0; n < 200000; n++) x = x * 1.0000001 + 1e-9; if (x > 1e300) x = 1; } }) { IsBackground = true, Name = "Verify load", Priority = ThreadPriority.BelowNormal };
                                burners.Add(burner); burner.Start();
                            }
                        }
                        if (loading && (now >= loadStart + loadSeconds || hottest >= FanSafetyPolicy.ReleaseC)) { loadStop.Set(); loading = false; loadSeconds = 0; }
                        tickNumber++;
                        double wait = (tickNumber * 0.25) - clock.Elapsed.TotalSeconds;
                        if (wait > 0) Thread.Sleep((int)(wait * 1000));
                    }
                }
                finally
                {
                    stop.Set(); loadStop.Set(); foreach (Thread b in burners) b.Join(2000);
                    serviceThread.Join(8000);
                    if (esif != null) esif.Dispose();
                }
            }

            List<VerifyTick> tickCopy; List<VerifyServiceSample> serviceCopy; List<VerifyFanSample> fanCopy;
            lock (gate) { tickCopy = ticks.ToList(); serviceCopy = service.ToList(); fanCopy = fans.ToList(); }
            VerifyReport report = SensorVerifier.Analyze(tickCopy, serviceCopy, fanCopy, null);
            ReportFiles.Write(Path.Combine(outputDirectory, "report.json"), new { Tool = "DellFans.Cli.exe --verify-sensors", StartedUtc = started.ToString("o"), Seconds = seconds, LoadSeconds = loadSeconds,
                Cpu = cpuName, SensorSource = sensorSource, SensorDriver = driver, HwInfoSharedMemory = hwInfo,
                Service = serviceCopy.Any(s => s.Reachable) ? "reachable" : serviceCopy.Count == 0 ? "no samples" : "not found", Report = report });
            File.WriteAllText(Path.Combine(outputDirectory, "samples.csv"), Csv(tickCopy, serviceCopy, fanCopy), new UTF8Encoding(false));
            console.WriteLine(SensorVerifier.Summary(report));
            console.WriteLine("Service: " + (serviceCopy.Any(s => s.Reachable) ? "reachable" : "not found") + "; HWiNFO shared memory: " + (hwInfo ? "yes" : "no") + "; source: " + sensorSource + ".");
            console.WriteLine("Wrote " + Path.Combine(outputDirectory, "report.json") + " and samples.csv");
            return 0;
        }

        // One row per event, a column per series; blanks where a series has nothing at that moment.
        public static string Csv(List<VerifyTick> ticks, List<VerifyServiceSample> service, List<VerifyFanSample> fans)
        {
            StringBuilder text = new StringBuilder("t_s,kind,source,package,core_max,core_avg,control_1s,protection_raw,stale,esif_package,hwinfo_package,hwinfo_core_max,hwinfo_core_avg,svc_package,svc_core_max,svc_core_avg,svc_age_s,svc_rtt_s,svc_controlling,fan_smm,fan_bfn,error\n");
            Func<double?, string> n = delegate(double? v) { return v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : ""; };
            foreach (VerifyTick t in ticks)
                text.Append(t.T.ToString("0.000", CultureInfo.InvariantCulture)).Append(",layer,").Append((t.Source ?? "").Replace(',', ' ')).Append(',').Append(n(t.Package)).Append(',').Append(n(t.CoreMax)).Append(',').Append(n(t.CoreAverage)).Append(',')
                    .Append(n(t.Control)).Append(',').Append(n(t.Protection)).Append(',').Append(t.Stale ? "1" : "0").Append(',').Append(n(t.EsifPackage)).Append(',').Append(n(t.HwPackage)).Append(',').Append(n(t.HwCoreMax)).Append(',').Append(n(t.HwCoreAverage)).Append(",,,,,,,,,\n");
            foreach (VerifyServiceSample s in service)
                text.Append(s.T.ToString("0.000", CultureInfo.InvariantCulture)).Append(",service").Append(new string(',', 11)).Append(',').Append(n(s.Package)).Append(',').Append(n(s.CoreMax)).Append(',').Append(n(s.CoreAverage)).Append(',')
                    .Append(n(s.AgeSeconds)).Append(',').Append(n(s.RoundTripSeconds)).Append(',').Append(s.Reachable ? (s.Controlling ? "1" : "0") : "").Append(",,,").Append((s.Error ?? "").Replace(',', ' ').Replace('\n', ' ')).Append('\n');
            foreach (VerifyFanSample f in fans)
                text.Append(f.T.ToString("0.000", CultureInfo.InvariantCulture)).Append(",fan").Append(new string(',', 18)).Append(f.Smm).Append(',').Append(f.Bfn).Append(",\n");
            return text.ToString();
        }
    }
}

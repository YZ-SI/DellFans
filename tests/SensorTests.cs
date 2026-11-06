using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DellFans;

internal sealed class FakeClock
{
    public DateTime Now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    public double Mono = 1000;                       // monotonic seconds; never follows wall-clock jumps
    public DateTime Get() { return Now; }
    public double GetMono() { return Mono; }
    public void Advance(double seconds) { Now = Now.AddSeconds(seconds); Mono += seconds; }
    public void WallJump(double seconds) { Now = Now.AddSeconds(seconds); } // NTP / user clock change: wall time only
}

internal sealed class FakeMsr : IMsrTransport
{
    public long Target = 0x640000;              // TjMax 100
    public long Package = 0x80000000L | (30L << 16); // 70 C
    public long[] Core;                          // per logical CPU
    public List<string> Calls = new List<string>();
    public HashSet<int> Throw = new HashSet<int>(); // logical ids whose reads fail
    public bool Disposed;
    public long ReadMsr(uint msr, int cpu)
    {
        if (Throw.Contains(cpu)) throw new IOException("rdmsr failed on " + cpu);
        if (msr != 0x19C && msr != 0x1B1 && msr != 0x1A2) throw new InvalidOperationException("Non-whitelisted MSR");
        Calls.Add(msr.ToString("X") + "@" + cpu);
        if (msr == 0x1A2) return Target;
        if (msr == 0x1B1) return Package;
        return Core[cpu];
    }
    public void Dispose() { Disposed = true; }
}

// Delegates to a script so tests can freeze it, fail it or advance its readings.
internal sealed class ScriptedSource : ICpuTemperatureSource
{
    public string Name { get; set; }
    public Func<CpuSample> Next;
    public bool Disposed;
    public int Reads;
    public CpuSample Read() { Reads++; return Next(); }
    public void Dispose() { Disposed = true; }
}

internal sealed class FakeProbe : IStorageProbe
{
    public List<StorageSample> Samples = new List<StorageSample>();
    public int Calls;
    public List<StorageSample> Read(Func<DateTime> clock)
    {
        Calls++;
        return Samples.Select(s => new StorageSample { Index = s.Index, Model = s.Model, TempC = s.TempC, Source = s.Source, AtUtc = clock(), AcquireMs = s.AcquireMs, Error = s.Error }).ToList();
    }
}

internal sealed class FakePawnEnv : IPawnIoEnvironment
{
    public bool Opens, Exists, Running;
    public bool DeviceOpens() { return Opens; }
    public bool ServiceExists() { return Exists; }
    public bool ServiceRunning() { return Running; }
}

internal sealed class FakeHost : IInstallerHost
{
    public string Hash = SensorDriverInstaller.Sha256;
    public string Signer = "CN=namazso.eu, O=namazso, C=HU";
    public int ExitCode;
    public bool Elevated = true;
    public bool HangOnRun;
    public bool Loads = true;
    public bool DriverLoads() { Log.Add("loads"); return Loads; }
    public List<string> Log = new List<string>();
    public Stream Open;                       // the one stream the installer must hold across hash, verify and run
    public bool OpenedOnce, SameStream = true, LockedDuringRun;
    public bool IsElevated() { Log.Add("elevated"); return Elevated; }
    public void Download(string url, string path) { Log.Add("download " + url); File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); }
    public Stream OpenLocked(string path) { Log.Add("open"); OpenedOnce = true; Open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Open; }
    public string Sha256Hex(Stream stream) { Log.Add("hash"); SameStream &= ReferenceEquals(stream, Open); return Hash; }
    public string VerifiedSignerSubject(string path, Stream stream) { Log.Add("verify"); SameStream &= ReferenceEquals(stream, Open); return Signer; }
    public int Run(string path, string arguments, TimeSpan timeout)
    {
        Log.Add("run " + arguments);
        try { using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
        catch (IOException) { LockedDuringRun = true; }
        try { File.Delete(path); LockedDuringRun &= File.Exists(path); } catch (IOException) { }
        if (HangOnRun) throw new System.TimeoutException();
        return ExitCode;
    }
}

internal static class SensorTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static void Near(double? actual, double expected, string message)
    { Check(actual.HasValue && Math.Abs(actual.Value - expected) < 0.001, message + " (got " + (actual.HasValue ? actual.Value.ToString() : "null") + ", expected " + expected + ")"); }
    static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { passed++; return; } throw new Exception(message); }

    static long Status(int readout, bool valid, int resolution)
    { return (valid ? 0x80000000L : 0) | ((long)(resolution & 0xF) << 27) | ((long)(readout & 0x7F) << 16); }

    static CpuSample Sample(FakeClock clock, string source, double? package, params double[] cores)
    { return new CpuSample { Source = source, AtUtc = clock.Now, Package = package, Cores = cores }; }

    static int Main()
    {
        try { Decoders(); Topology(); MsrSource(); Esif(); Sampler(); Hardening(); Stale(); CrossCheck(); Storage(); Layer(); Driver(); ResultFile(); Pin(); }
        catch (Exception e) { Console.WriteLine("FAIL: " + e.Message + (e.GetType() == typeof(Exception) ? "" : Environment.NewLine + e)); return 1; }
        Console.WriteLine(passed + " sensor checks passed: Intel SDM decode tables, MSR/ESIF sources, 1 s window, stale rules, cross-check, storage decode and staleness, SensorRows, PawnIO status, installer gates, module pin.");
        return 0;
    }

    static void Decoders()
    {
        // IA32_TEMPERATURE_TARGET bits 23:16
        Check(SensorDecode.TjMax(0x640000) == 100, "TjMax 100");
        Check(SensorDecode.TjMax(0x690000) == 105, "TjMax 105");
        Check(SensorDecode.TjMax(0x5F0000) == 95 && SensorDecode.TjMax(0x320000) == 100, "TjMax 95 accepted, 50 below the plausible window falls back");
        Check(SensorDecode.TjMax(0) == 100 && SensorDecode.TjMax(0xFF0000) == 100, "TjMax 0 / 255 fall back to 100");
        Check(SensorDecode.TjMax(0x69640000L | 0xFFFF) == 100 && SensorDecode.TjMax(0x55690000L) == 105, "Bits outside 23:16 are ignored");
        Check(SensorDecode.TjMaxIsReported(0x640000) && !SensorDecode.TjMaxIsReported(0), "TjMaxIsReported");
        // IA32_THERM_STATUS / PACKAGE_THERM_STATUS: reading = bits 22:16, valid only with bit 31
        Near(SensorDecode.DigitalReadout(Status(37, true, 1), 100), 63, "readout 37 below TjMax 100");
        Near(SensorDecode.DigitalReadout(Status(0, true, 1), 100), 100, "readout 0 = at TjMax");
        Near(SensorDecode.DigitalReadout(Status(37, true, 0), 105), 68, "TjMax 105");
        Near(SensorDecode.DigitalReadout(Status(99, true, 0), 100), 1, "readout 99 = 1 C");
        Check(SensorDecode.DigitalReadout(Status(37, false, 1), 100) == null, "valid bit clear must give no reading");
        // IA32_PACKAGE_THERM_STATUS has no Reading Valid bit (SDM: 22:16 Pkg Digital Readout, 63:23 reserved): decode with bit 31 clear or set.
        Near(SensorDecode.PackageReadout(Status(37, false, 0), 100), 63, "package readout without a valid bit");
        Near(SensorDecode.PackageReadout(Status(37, true, 0), 100), 63, "package readout with bit 31 set");
        Check(SensorDecode.PackageReadout(Status(100, false, 0), 100) == null && SensorDecode.PackageReadout(Status(127, false, 0), 100) == null && SensorDecode.PackageReadout(0, 100) == null, "package out-of-range and all-zero (absent register) guards");
        Check(SensorDecode.DigitalReadout(Status(100, true, 1), 100) == null, "readout == TjMax (0 C) rejected");
        Check(SensorDecode.DigitalReadout(Status(127, true, 1), 100) == null, "out-of-range readout 127 rejected");
        Check(SensorDecode.DigitalReadout(0x00000000, 100) == null, "all-zero status");
        Check(SensorDecode.DigitalReadout(Status(37, true, 1) | 0x7FFFL, 100).Value == 63, "bits 15:0 and 26:23 do not alter the reading");
        Check(SensorDecode.Resolution(Status(37, true, 1)) == 1 && SensorDecode.Resolution(Status(37, true, 0)) == 0 && SensorDecode.Resolution(Status(0, true, 15)) == 15, "Resolution bits 30:27");

        // Storage temperature descriptor: header 24 bytes then 16-byte infos
        byte[] d = new byte[24 + 32];
        BitConverter.GetBytes(1u).CopyTo(d, 0); BitConverter.GetBytes((uint)d.Length).CopyTo(d, 4);
        BitConverter.GetBytes((short)90).CopyTo(d, 8); BitConverter.GetBytes((short)80).CopyTo(d, 10); BitConverter.GetBytes((ushort)2).CopyTo(d, 12);
        BitConverter.GetBytes((ushort)1).CopyTo(d, 24); BitConverter.GetBytes((short)61).CopyTo(d, 26);  // sensor 1
        BitConverter.GetBytes((ushort)0).CopyTo(d, 40); BitConverter.GetBytes((short)45).CopyTo(d, 42);  // composite (index 0) preferred
        Near(SensorDecode.StorageTemperatureDescriptor(d, d.Length), 45, "composite sensor preferred");
        BitConverter.GetBytes((ushort)1).CopyTo(d, 40);
        Near(SensorDecode.StorageTemperatureDescriptor(d, d.Length), 61, "first sensor when no index 0");
        BitConverter.GetBytes((ushort)0).CopyTo(d, 12);
        Check(SensorDecode.StorageTemperatureDescriptor(d, d.Length) == null, "InfoCount 0");
        BitConverter.GetBytes((ushort)2).CopyTo(d, 12); BitConverter.GetBytes((short)3000).CopyTo(d, 26); BitConverter.GetBytes((short)-300).CopyTo(d, 42);
        Check(SensorDecode.StorageTemperatureDescriptor(d, d.Length) == null, "implausible temperatures rejected");
        BitConverter.GetBytes((short)61).CopyTo(d, 26);
        Check(SensorDecode.StorageTemperatureDescriptor(d, 30) == null && SensorDecode.StorageTemperatureDescriptor(null, 0) == null, "truncated / null buffers");
        BitConverter.GetBytes((ushort)9).CopyTo(d, 12);
        Near(SensorDecode.StorageTemperatureDescriptor(d, d.Length), 61, "InfoCount larger than the buffer is bounded");

        // NVMe health log: composite temperature in Kelvin at bytes 1-2
        byte[] n = new byte[48 + 512]; BitConverter.GetBytes((ushort)318).CopyTo(n, 49);
        Near(SensorDecode.NvmeCompositeTemperature(n, 48, n.Length), 45, "318 K = 45 C");
        BitConverter.GetBytes((ushort)0).CopyTo(n, 49);
        Check(SensorDecode.NvmeCompositeTemperature(n, 48, n.Length) == null, "0 K means no data");
        BitConverter.GetBytes((ushort)0xFFFF).CopyTo(n, 49);
        Check(SensorDecode.NvmeCompositeTemperature(n, 48, n.Length) == null, "0xFFFF means no data");
        BitConverter.GetBytes((ushort)5000).CopyTo(n, 49);
        Check(SensorDecode.NvmeCompositeTemperature(n, 48, n.Length) == null, "implausible Kelvin");
        Check(SensorDecode.NvmeCompositeTemperature(n, 48, 49) == null, "truncated log");

        // SMART data sector: 194 preferred over 190, raw byte 0
        byte[] s = new byte[16 + 512];
        Action<int, int, int> attribute = delegate (int slot, int id, int raw) { int at = 16 + 2 + slot * 12; s[at] = (byte)id; s[at + 3] = 100; s[at + 4] = 100; s[at + 5] = (byte)raw; };
        attribute(0, 9, 200); attribute(1, 190, 52); attribute(2, 194, 38);
        Near(SensorDecode.SmartTemperature(s, 16, s.Length), 38, "attribute 194 preferred");
        attribute(2, 0, 0);
        Near(SensorDecode.SmartTemperature(s, 16, s.Length), 52, "attribute 190 fallback");
        attribute(1, 190, 0);
        Check(SensorDecode.SmartTemperature(s, 16, s.Length) == null, "zero raw value rejected");
        attribute(1, 194, 200);
        Check(SensorDecode.SmartTemperature(s, 16, s.Length) == null, "raw 200 rejected as implausible");
        Check(SensorDecode.SmartTemperature(s, 16, 100) == null, "short SMART buffer");

        // STORAGE_DEVICE_DESCRIPTOR
        byte[] m = new byte[128];
        BitConverter.GetBytes(40u).CopyTo(m, 12); BitConverter.GetBytes(48u).CopyTo(m, 16); BitConverter.GetBytes(17u).CopyTo(m, 28);
        System.Text.Encoding.ASCII.GetBytes("ACME").CopyTo(m, 40); System.Text.Encoding.ASCII.GetBytes("FastDisk 1TB ").CopyTo(m, 48);
        Check(SensorDecode.DeviceModel(m, m.Length) == "ACME FastDisk 1TB", "vendor + product");
        Check(SensorDecode.DeviceBusType(m, m.Length) == 17, "bus type");
        BitConverter.GetBytes(0u).CopyTo(m, 12); 
        Check(SensorDecode.DeviceModel(m, m.Length) == "FastDisk 1TB", "missing vendor offset");
        BitConverter.GetBytes(500u).CopyTo(m, 16);
        Check(SensorDecode.DeviceModel(m, m.Length) == null, "offset past the buffer");
    }

    // x64 SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records for RelationProcessorCore: one {class, group, mask} per core.
    static byte[] CoreBuffer(params long[][] cores)
    {
        List<byte> b = new List<byte>();
        foreach (long[] c in cores)
        {
            byte[] r = new byte[48];
            BitConverter.GetBytes(0).CopyTo(r, 0); BitConverter.GetBytes(48).CopyTo(r, 4);
            r[8] = 1; r[9] = (byte)c[0]; BitConverter.GetBytes((ushort)1).CopyTo(r, 30);
            BitConverter.GetBytes(c[2]).CopyTo(r, 32); BitConverter.GetBytes((ushort)c[1]).CopyTo(r, 40);
            b.AddRange(r);
        }
        return b.ToArray();
    }

    static byte[] Hybrid6P8E()
    {
        List<long[]> c = new List<long[]>();
        for (int i = 0; i < 6; i++) c.Add(new long[] { 1, 0, 3L << (2 * i) });   // P: HT pair
        for (int i = 12; i < 20; i++) c.Add(new long[] { 0, 0, 1L << i });       // E: single thread
        return CoreBuffer(c.ToArray());
    }

    static void Topology()
    {
        byte[] hy = Hybrid6P8E();
        List<CpuCore> cores = CpuTopology.Parse(hy, hy.Length);
        Check(cores.Count == 14 && cores.Count(x => x.EfficiencyClass == 1) == 6 && cores.Sum(x => x.LogicalCount) == 20, "6P(HT)+8E = 14 cores, 6 P, 20 logical");
        Check(string.Join(",", cores.Take(7).Select(x => x.LogicalIndex.ToString()).ToArray()) == "0,2,4,6,8,10,12" && cores[13].LogicalIndex == 19, "pin to the first set bit of each mask");
        Check(CpuTopology.IsHybrid(cores) && string.Join(",", CpuTopology.Names(cores).ToArray()) == "P-core 0,P-core 1,P-core 2,P-core 3,P-core 4,P-core 5,E-core 6,E-core 7,E-core 8,E-core 9,E-core 10,E-core 11,E-core 12,E-core 13", "hybrid names: one index sequence, P then E");

        List<long[]> plain = new List<long[]>();
        for (int i = 0; i < 4; i++) plain.Add(new long[] { 0, 0, 3L << (2 * i) });
        byte[] pb = CoreBuffer(plain.ToArray());
        cores = CpuTopology.Parse(pb, pb.Length);
        Check(cores.Count == 4 && cores.Sum(x => x.LogicalCount) == 8 && !CpuTopology.IsHybrid(cores), "4C/8T is not hybrid");
        Check(string.Join(",", CpuTopology.Names(cores).ToArray()) == "Core 0,Core 1,Core 2,Core 3", "non-hybrid names");

        byte[] two = CoreBuffer(new long[] { 0, 0, 1 }, new long[] { 0, 1, 0x10 });
        cores = CpuTopology.Parse(two, two.Length);
        Check(cores[1].Group == 1 && cores[1].LogicalIndex == 4, "processor group is kept");
        Check(CpuTopology.Parse(new byte[0], 0).Count == 0, "empty buffer");
        Check(CpuTopology.Parse(hy, 20).Count == 0, "truncated record is ignored, not read past the end");
        byte[] bad = (byte[])hy.Clone(); BitConverter.GetBytes(0).CopyTo(bad, 4);
        Check(CpuTopology.Parse(bad, bad.Length).Count == 0, "zero-size record stops the parse");
    }

    static void MsrSource()
    {
        FakeClock clock = new FakeClock();
        FakeMsr msr = new FakeMsr { Core = new long[] { Status(40, true, 1), Status(35, true, 1), Status(50, false, 1), Status(120, true, 1) } };
        PawnIoMsrSource source = new PawnIoMsrSource(msr, 4, clock.Get);
        CpuSample sample = source.Read();
        Near(sample.Package, 70, "package");
        Check(sample.Source == "PawnIO IntelMSR" && sample.AtUtc == clock.Now, "source name and acquisition time");
        Near(sample.Cores[0], 60, "core 0"); Near(sample.Cores[1], 65, "core 1");
        Check(double.IsNaN(sample.Cores[2]) && double.IsNaN(sample.Cores[3]), "invalid / out-of-range core readouts are NaN, not numbers");
        Near(sample.Hottest(), 70, "hottest");
        Check(msr.Calls.Contains("19C@0") && msr.Calls.Contains("19C@3") && msr.Calls.Contains("1B1@0") && msr.Calls.Contains("1A2@0"), "per-logical-CPU reads of 0x19C, package 0x1B1, TjMax 0x1A2");
        Check(msr.Calls.All(c => c.StartsWith("19C@") || c.StartsWith("1B1@") || c.StartsWith("1A2@")), "only whitelisted MSRs");
        Check(sample.Note == null, "TjMax reported => no note");
        msr.Target = 0; msr.Core = new long[] { Status(40, true, 1) };
        PawnIoMsrSource assumed = new PawnIoMsrSource(msr, 1, clock.Get);
        Check(assumed.Read().Note != null, "assumed TjMax is flagged");
        msr.Package = Status(127, false, 0); msr.Core = new long[] { Status(1, false, 0) };
        Throws<InvalidDataException>(delegate { assumed.Read(); }, "No valid readout must throw");
        source.Dispose();
        Check(msr.Disposed, "dispose closes the transport");

        // per-core isolation: one failing core is NaN, the rest survive; only a total failure throws
        byte[] iso = Hybrid6P8E();
        msr = new FakeMsr { Core = Enumerable.Repeat(Status(40, true, 1), 20).ToArray() };
        msr.Throw.Add(2); msr.Throw.Add(12);
        sample = new PawnIoMsrSource(msr, CpuTopology.Parse(iso, iso.Length), clock.Get).Read();
        Check(double.IsNaN(sample.Cores[1]) && double.IsNaN(sample.Cores[6]) && sample.Cores.Count(double.IsNaN) == 2 && sample.Package.HasValue, "a failing core reads NaN, the other 12 and the package survive");
        msr = new FakeMsr { Core = Enumerable.Repeat(Status(40, true, 1), 20).ToArray() };
        PawnIoMsrSource doomed = new PawnIoMsrSource(msr, CpuTopology.Parse(iso, iso.Length), clock.Get);
        for (int i = 0; i < 20; i++) msr.Throw.Add(i);
        Throws<IOException>(delegate { doomed.Read(); }, "every core failing fails the read");

        // topology: one 0x19C read per physical core, pinned to its first logical CPU (group*64 + index)
        byte[] hy = Hybrid6P8E(); List<CpuCore> topo = CpuTopology.Parse(hy, hy.Length);
        msr = new FakeMsr { Core = Enumerable.Repeat(Status(40, true, 1), 20).ToArray() };
        PawnIoMsrSource hybrid = new PawnIoMsrSource(msr, topo, clock.Get);
        sample = hybrid.Read();
        Check(sample.Cores.Length == 14 && msr.Calls.Count(c => c.StartsWith("19C@")) == 14 && !msr.Calls.Contains("19C@1") && msr.Calls.Contains("19C@12") && msr.Calls.Contains("19C@19"), "one read per physical core, HT siblings skipped");
        Check(sample.CoreNames[0] == "P-core 0" && sample.CoreNames[13] == "E-core 13", "sample carries core names");
        byte[] g = CoreBuffer(new long[] { 0, 1, 1 });
        msr = new FakeMsr { Core = new long[65] }; msr.Core[64] = Status(30, true, 1);
        Near(new PawnIoMsrSource(msr, CpuTopology.Parse(g, g.Length), clock.Get).Read().Cores[0], 70, "group 1 / bit 0 is addressed as logical id 64");
    }

    static void Esif()
    {
        var items = new List<KeyValuePair<string, double>> {
            new KeyValuePair<string, double>(@"PCI\VEN_8086&DEV_461D\3&1&0&20_0", 0), new KeyValuePair<string, double>(@"PCI\VEN_8086&DEV_461D\3&1&0&20_1", 63),
            new KeyValuePair<string, double>(@"PCI\VEN_8086&DEV_461D\3&1&0&20_2", 64), new KeyValuePair<string, double>(@"PCI\VEN_8086&DEV_461D\3&1&0&20_3", 62),
            new KeyValuePair<string, double>(@"PCI\VEN_8086&DEV_461D\3&1&0&20_4", 99) };
        Near(EsifSource.Aggregate(items), 64, "max of instances 1-3, instance 4 ignored");
        items[2] = new KeyValuePair<string, double>(items[2].Key, 0);
        Near(EsifSource.Aggregate(items), 63, "zero reading ignored");
        Check(EsifSource.Aggregate(new[] { new KeyValuePair<string, double>("x_0", 50), new KeyValuePair<string, double>("noindex", 50), new KeyValuePair<string, double>("x_1", 300) }) == null, "no usable instance");
    }

    static CpuSensorSampler MakeSampler(FakeClock clock, Func<ICpuTemperatureSource> msr, Func<ICpuTemperatureSource> esif)
    { return new CpuSensorSampler(msr, esif, clock.Get, clock.GetMono, delegate (Action work) { work(); }); } // cross-check runs inline for determinism

    static ScriptedSource Scripted(string name, Func<CpuSample> next) { return new ScriptedSource { Name = name, Next = next }; }

    static void Sampler()
    {
        FakeClock clock = new FakeClock();
        double value = 60;
        ScriptedSource msr = Scripted("PawnIO IntelMSR", () => Sample(clock, "PawnIO IntelMSR", value, value - 2, value + 1));
        ScriptedSource esif = Scripted("Intel ESIF", () => Sample(clock, "Intel ESIF", value));
        CpuSensorSampler sampler = MakeSampler(clock, () => msr, () => esif);
        sampler.Step();
        CpuReading r = sampler.GetReading();
        Check(r.Source == "PawnIO IntelMSR" && !r.Stale, "MSR preferred when it works");
        // 1 s window: samples at 60, 62, 64, 66 over 0.75 s => mean 63, max (hottest core = value+1) 67
        for (int i = 1; i <= 3; i++) { clock.Advance(0.25); value = 60 + 2 * i; sampler.Step(); }
        r = sampler.GetReading();
        Near(r.ControlMeanC, 64, "1 s mean of the per-sample hottest channel (61,63,65,67)");
        Near(r.RawMaxC, 67, "raw max inside the window");
        clock.Advance(0.5); sampler.Step(); // still 4+1 samples? window drops anything older than 1 s
        clock.Advance(0.75); // no new sample for 0.75 s; GetReading must still be fresh
        Check(!sampler.GetReading().Stale, "0.75 s old is not stale");
        // ESIF fallback when MSR cannot open
        FakeClock clock2 = new FakeClock();
        int msrAttempts = 0; ScriptedSource lateMsr = null;
        ScriptedSource esif2 = Scripted("Intel ESIF", () => Sample(clock2, "Intel ESIF", 55));
        CpuSensorSampler fallback = MakeSampler(clock2, delegate { msrAttempts++; if (lateMsr == null) throw new IOException("PawnIO missing"); return lateMsr; }, () => esif2);
        fallback.Step();
        Check(fallback.GetReading().Source == "Intel ESIF" && msrAttempts == 1, "ESIF used when the MSR source is unavailable");
        for (int i = 0; i < 100; i++) { clock2.Advance(0.25); fallback.Step(); }
        Check(msrAttempts <= 1 + 1, "MSR re-probed at most once a minute while on ESIF (attempts " + msrAttempts + ")");
        lateMsr = Scripted("PawnIO IntelMSR", () => Sample(clock2, "PawnIO IntelMSR", 58, 57));
        clock2.Advance(61); fallback.Step();
        Check(fallback.GetReading().Source == "PawnIO IntelMSR", "upgrades to MSR once the driver appears");
        // neither available
        FakeClock clock3 = new FakeClock();
        CpuSensorSampler none = MakeSampler(clock3, delegate { throw new IOException("no pawnio"); }, delegate { throw new IOException("no esif"); });
        none.Step(); r = none.GetReading();
        Check(r.Sample == null && r.ControlMeanC == null && r.RawMaxC == null && r.Warnings.Count == 1 && r.Warnings[0].Contains("no pawnio") && r.Warnings[0].Contains("no esif"), "no source => no values + explanatory warning");
        // 8 consecutive failures drop the source and reselect
        FakeClock clock4 = new FakeClock(); bool fail = false; int made = 0;
        Func<ICpuTemperatureSource> factory = delegate { made++; return Scripted("PawnIO IntelMSR", delegate { if (fail) throw new IOException("ioctl failed"); return Sample(clock4, "PawnIO IntelMSR", 50, 50); }); };
        CpuSensorSampler recover = MakeSampler(clock4, factory, null);
        recover.Step(); fail = true;
        for (int i = 0; i < 8; i++) { clock4.Advance(0.25); recover.Step(); }
        Check(made == 1, "source still held until the 8th failure completes");
        fail = false; clock4.Advance(0.25); recover.Step();
        Check(made == 2 && !recover.GetReading().Stale, "source dropped after 8 failures and re-created (made " + made + ")");
    }

    static void Hardening()
    {
        // 2. monotonic time: wall-clock jumps of +/-1 h change neither age nor staleness nor ordering
        FakeClock clock = new FakeClock();
        ScriptedSource msr = Scripted("PawnIO IntelMSR", () => Sample(clock, "PawnIO IntelMSR", 61, 60, 61));
        CpuSensorSampler sampler = MakeSampler(clock, () => msr, null);
        sampler.Step();
        clock.WallJump(3600);
        CpuReading r = sampler.GetReading();
        Check(!r.Stale && r.AgeSeconds < 0.01 && r.ControlMeanC.HasValue, "wall clock +1 h: sample still fresh (age by monotonic clock)");
        clock.Advance(0.25); sampler.Step();
        clock.WallJump(-7200); clock.Advance(0.25); sampler.Step();
        r = sampler.GetReading();
        Check(!r.Stale && r.ControlMeanC.HasValue && Math.Abs(r.AgeSeconds) < 0.01, "wall clock -1 h: a new sample is still accepted and fresh");
        clock.Advance(2.5); clock.WallJump(3600);
        Check(sampler.GetReading().Stale && sampler.GetReading().RawMaxC == null, "a real 2.5 s silence is stale whatever the wall clock did");
        FakeClock sc = new FakeClock(); FakeProbe probe = new FakeProbe(); probe.Samples.Add(Disk(0, 40, "D", "NVMe IOCTL"));
        StorageSensorSampler storage = new StorageSensorSampler(probe, sc.Get, sc.GetMono); storage.Step();
        List<SensorRow> rows = new List<SensorRow>();
        sc.WallJump(3600); storage.AddRows(rows);
        Check(rows[0].Quality == "OK", "storage: wall clock +1 h does not stale a fresh reading");
        sc.WallJump(-7200); rows.Clear(); storage.AddRows(rows);
        Check(rows[0].Quality == "OK", "storage: wall clock -1 h does not break freshness");
        sc.Advance(60); sc.WallJump(-3600); storage.Step();
        Check(probe.Calls == 2, "storage polling cadence follows the monotonic clock");
        sc.Mono -= 500; rows.Clear(); storage.AddRows(rows);
        Check(rows[0].Quality == "Stale" && rows[0].Value == null, "storage: a negative age is rejected, not treated as fresh");

        // 3. peak hold: a one-sample spike between two reads is reported once
        clock = new FakeClock(); double value = 60;
        msr = Scripted("PawnIO IntelMSR", () => Sample(clock, "PawnIO IntelMSR", value, value));
        sampler = MakeSampler(clock, () => msr, null);
        sampler.Step(); sampler.GetReading();
        clock.Advance(0.25); value = 95; sampler.Step(); value = 60;
        for (int i = 0; i < 6; i++) { clock.Advance(0.25); sampler.Step(); } // the spike is now 1.5 s old: outside the 1 s window
        r = sampler.GetReading();
        Near(r.RawMaxC, 95, "protection value holds the spike seen since the previous read"); Near(r.ControlMeanC, 60, "control mean ignores it");
        Near(sampler.GetReading().RawMaxC, 60, "the spike is reported once");

        // 4. a blocked ESIF cross-check never makes MSR samples stale
        clock = new FakeClock();
        ManualResetEvent release = new ManualResetEvent(false);
        ScriptedSource slowEsif = Scripted("Intel ESIF", () => { release.WaitOne(10000); return Sample(clock, "Intel ESIF", 62); });
        msr = Scripted("PawnIO IntelMSR", () => Sample(clock, "PawnIO IntelMSR", 60, 60));
        sampler = new CpuSensorSampler(() => msr, () => slowEsif, clock.Get, clock.GetMono, null);
        bool everStale = false;
        for (int i = 0; i < 80; i++) { clock.Advance(0.25); sampler.Step(); everStale |= sampler.GetReading().Stale; if (i == 0) SpinUntil(() => slowEsif.Reads == 1); }
        Check(!everStale && msr.Reads >= 80 && slowEsif.Reads == 1, "20 s with the ESIF call blocked: MSR keeps sampling, no stale, no second ESIF call (esif reads " + slowEsif.Reads + ")");
        release.Set(); Thread.Sleep(300);
        clock.Advance(11); // the slow call (20 fake seconds) skips one interval, then the cross-check resumes
        SpinUntil(() => { sampler.Step(); return slowEsif.Reads == 2; });
        Check(slowEsif.Reads == 2, "cross-check resumes after the slow call returns");
        sampler.Dispose();
        FakeClock slowClock = new FakeClock(); int slowReads = 0;
        ScriptedSource timedEsif = Scripted("Intel ESIF", () => { slowReads++; slowClock.Advance(0.6); return Sample(slowClock, "Intel ESIF", 60); });
        CpuSensorSampler timed = MakeSampler(slowClock, () => Scripted("PawnIO IntelMSR", () => Sample(slowClock, "PawnIO IntelMSR", 60, 60)), () => timedEsif);
        timed.Step();
        for (int i = 0; i < 20; i++) { slowClock.Advance(0.25); timed.Step(); }
        Check(slowReads == 1, "a cross-check call that took > 500 ms skips the next interval (reads " + slowReads + ")");
        for (int i = 0; i < 24; i++) { slowClock.Advance(0.25); timed.Step(); }
        Check(slowReads == 2, "and then resumes (reads " + slowReads + ")");

        // 5. ESIF as the only source: a value frozen for 30 s is flagged but stays usable
        clock = new FakeClock(); value = 60;
        ScriptedSource esifOnly = Scripted("Intel ESIF", () => Sample(clock, "Intel ESIF", value));
        sampler = MakeSampler(clock, delegate { throw new IOException("no pawnio"); }, () => esifOnly);
        for (int i = 0; i < 100; i++) { clock.Advance(0.25); sampler.Step(); }
        Check(!sampler.GetReading().Warnings.Any(w => w.Contains("not changed")), "25 s unchanged: no warning yet");
        for (int i = 0; i < 40; i++) { clock.Advance(0.25); sampler.Step(); }
        r = sampler.GetReading();
        Check(r.Warnings.Any(w => w.Contains("Intel ESIF") && w.Contains("not changed")) && r.ControlMeanC.HasValue && !r.Stale, "35 s unchanged: warning, still usable for control");
        value = 61; clock.Advance(0.25); sampler.Step();
        Check(!sampler.GetReading().Warnings.Any(w => w.Contains("not changed")), "a changing value clears the warning");

        // 8. catch-all loop errors are recorded and surfaced
        string recorded = null;
        SensorLayer.Guarded(delegate { throw new InvalidOperationException("boom"); }, m => recorded = m);
        Check(recorded == "boom", "Guarded step records the exception message");
        CpuSensorSampler broken = MakeSampler(new FakeClock(), null, null);
        broken.RecordError("boom");
        Check(broken.GetReading().Warnings.Any(w => w.Contains("boom")), "recorded loop error shows up as a warning");

        // 9. Dispose during a slow read leaks nothing and recreates nothing
        clock = new FakeClock();
        ManualResetEvent inRead = new ManualResetEvent(false), go = new ManualResetEvent(false);
        List<ScriptedSource> made = new List<ScriptedSource>();
        sampler = MakeSampler(clock, delegate
        {
            ScriptedSource s = Scripted("PawnIO IntelMSR", delegate { inRead.Set(); go.WaitOne(10000); return Sample(clock, "PawnIO IntelMSR", 60, 60); });
            lock (made) made.Add(s); return s;
        }, null);
        System.Threading.Thread stepper = new System.Threading.Thread(sampler.Step); stepper.Start();
        Check(inRead.WaitOne(5000), "stepper is inside the slow read");
        sampler.Dispose(); go.Set(); stepper.Join(5000);
        Check(made.Count == 1 && made.All(s => s.Disposed), "the source created before Dispose is disposed once its read returns");
        sampler.Step();
        Check(made.Count == 1, "no source is created after Dispose");

        // 6. the CPU thread runs above normal priority
        clock = new FakeClock();
        SensorLayer layer = new SensorLayer(MakeSampler(clock, () => Scripted("PawnIO IntelMSR", () => Sample(clock, "PawnIO IntelMSR", 60, 60)), null), new StorageSensorSampler(new FakeProbe(), clock.Get, clock.GetMono), () => "CPU", () => "Installed");
        layer.Start();
        Check(layer.CpuThreadPriority == System.Threading.ThreadPriority.AboveNormal, "CPU sampler thread priority is AboveNormal");
        layer.Dispose();
        layer.Start();
        Check(true, "Start after Dispose is harmless");
    }

    static void SpinUntil(Func<bool> condition)
    {
        System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && w.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(5);
    }

    static void Stale()
    {
        // A frozen source: Read() keeps returning the same acquisition time. No new sample => stale after 2 s.
        FakeClock clock = new FakeClock();
        DateTime frozenAt = clock.Now; bool frozen = false;
        ScriptedSource source = Scripted("PawnIO IntelMSR", () => frozen ? new CpuSample { Source = "PawnIO IntelMSR", AtUtc = frozenAt, Package = 61, Cores = new[] { 60.0, 61.0 } } : Sample(clock, "PawnIO IntelMSR", 61, 60, 61));
        CpuSensorSampler sampler = MakeSampler(clock, () => source, null);
        List<SensorRow> rows = new List<SensorRow>();
        SensorLayer layer = new SensorLayer(sampler, new StorageSensorSampler(new FakeProbe(), clock.Get, clock.GetMono), () => "Test CPU", () => "Installed");
        sampler.Step(); frozenAt = clock.Now; frozen = true;
        SensorReadout readout = layer.Read(rows, new List<string>());
        Check(readout.ControlTemperatureC.HasValue && rows.All(x => x.HardwareType != "Cpu" || x.Quality == "OK"), "fresh before freezing");
        for (int i = 0; i < 7; i++) { clock.Advance(0.25); sampler.Step(); } // 1.75 s of repeated identical samples
        rows.Clear(); readout = layer.Read(rows, new List<string>());
        Check(readout.ControlTemperatureC.HasValue, "1.75 s old is still fresh");
        clock.Advance(0.5); sampler.Step(); // 2.25 s
        rows.Clear(); List<string> warnings = new List<string>(); readout = layer.Read(rows, warnings);
        Check(readout.ControlTemperatureC == null && readout.ProtectionTemperatureC == null, "stale => no control or protection temperature");
        Check(rows.Count(x => x.HardwareType == "Cpu") > 0 && rows.Where(x => x.HardwareType == "Cpu").All(x => x.Quality == "Stale" && x.Value == null), "every CPU row Stale with Value=null");
        Check(rows.Where(x => x.HardwareType == "Cpu").All(x => x.PolledAtUtc == frozenAt.ToString("o")), "stale rows keep the time of their last real acquisition");
        Check(warnings.Any(w => w.Contains("stale")), "stale warning");
        Check(!rows.Any(x => SensorRoles.IsCurrentTemperature(x) && x.Value.HasValue), "nothing usable for the fan controller");
        // recovery
        frozen = false; clock.Advance(0.25); sampler.Step();
        rows.Clear(); readout = layer.Read(rows, new List<string>());
        Check(readout.ControlTemperatureC.HasValue && rows.Where(x => x.HardwareType == "Cpu").All(x => x.Quality == "OK"), "a fresh sample clears the stale state");
    }

    static void CrossCheck()
    {
        FakeClock clock = new FakeClock();
        double msrValue = 60, esifValue = 62;
        ScriptedSource msr = Scripted("PawnIO IntelMSR", () => Sample(clock, "PawnIO IntelMSR", msrValue, msrValue));
        ScriptedSource esif = Scripted("Intel ESIF", () => Sample(clock, "Intel ESIF", esifValue));
        CpuSensorSampler sampler = MakeSampler(clock, () => msr, () => esif);
        for (int i = 0; i < 80; i++) { clock.Advance(0.25); sampler.Step(); }
        Check(sampler.GetReading().Warnings.Count == 0, "2 C apart: no warning");
        esifValue = 72; // 12 C apart
        for (int i = 0; i < 36; i++) { clock.Advance(0.25); sampler.Step(); } // 9 s
        Check(sampler.GetReading().Warnings.Count == 0, "disagreement shorter than 10 s: no warning");
        for (int i = 0; i < 12; i++) { clock.Advance(0.25); sampler.Step(); } // 12 s
        Check(sampler.GetReading().Warnings.Any(w => w.Contains("disagree")), "disagreement beyond 8 C for 10 s => warning");
        esifValue = 66; // 6 C apart (cross-check runs every 5 s)
        for (int i = 0; i < 24; i++) { clock.Advance(0.25); sampler.Step(); }
        Check(sampler.GetReading().Warnings.Count == 0, "warning clears once the sources agree again");
        esifValue = 68; // exactly 8 C: not greater
        for (int i = 0; i < 80; i++) { clock.Advance(0.25); sampler.Step(); }
        Check(sampler.GetReading().Warnings.Count == 0, "exactly 8 C apart is not a disagreement");
    }

    static StorageSample Disk(int index, double? temp, string model, string source) { return new StorageSample { Index = index, Model = model, TempC = temp, Source = source, Error = temp.HasValue ? null : "no answer" }; }

    static void Storage()
    {
        FakeClock clock = new FakeClock();
        FakeProbe probe = new FakeProbe();
        probe.Samples.Add(Disk(0, 45, "ACME NVMe 1TB", "NVMe IOCTL")); probe.Samples.Add(Disk(2, 31, "ACME HDD", "SATA SMART")); probe.Samples.Add(Disk(3, null, "Odd Disk", "NVMe IOCTL"));
        StorageSensorSampler storage = new StorageSensorSampler(probe, clock.Get, clock.GetMono);
        storage.Step(); storage.Step();
        Check(probe.Calls == 1, "enumeration is rate limited");
        List<SensorRow> rows = new List<SensorRow>(); storage.AddRows(rows);
        SensorRow nvme = rows.Single(x => x.Id == "storage/0/temperature");
        Check(nvme.Device == "ACME NVMe 1TB" && nvme.HardwareType == "Storage" && nvme.Name == "Temperature" && nvme.Type == "Temperature" && nvme.Role == "Temperature" && nvme.Unit == "°C", "storage row shape");
        Check(nvme.Value == 45 && nvme.Quality == "OK" && nvme.Source == "NVMe IOCTL" && nvme.PolledAtUtc == clock.Now.ToString("o"), "NVMe value, source and acquisition time");
        Check(rows.Single(x => x.Id == "storage/2/temperature").Source == "SATA SMART", "SATA source label");
        SensorRow odd = rows.Single(x => x.Id == "storage/3/temperature");
        Check(odd.Quality == "Unavailable" && odd.Value == null && odd.PolledAtUtc == null, "never-read disk is Unavailable without a fake timestamp");
        Check(storage.Warnings().Any(w => w.Contains("PhysicalDrive3")), "unreadable disk explained");
        DateTime firstRead = clock.Now;
        // the drive stops answering; the last good value is kept for 120 s, then goes stale
        probe.Samples[0].TempC = null; probe.Samples[0].Error = "timeout";
        clock.Advance(60); storage.Step();
        rows.Clear(); storage.AddRows(rows);
        nvme = rows.Single(x => x.Id == "storage/0/temperature");
        Check(probe.Calls == 2 && nvme.Quality == "OK" && nvme.Value == 45 && nvme.PolledAtUtc == firstRead.ToString("o"), "60 s old reading is still OK and keeps its original time");
        clock.Advance(61); storage.Step(); // 121 s since the last good read
        rows.Clear(); storage.AddRows(rows);
        nvme = rows.Single(x => x.Id == "storage/0/temperature");
        Check(nvme.Quality == "Stale" && nvme.Value == null, "older than 120 s => Stale, Value null");
        // frozen without a fresh enumeration also goes stale
        FakeClock clock2 = new FakeClock(); FakeProbe probe2 = new FakeProbe(); probe2.Samples.Add(Disk(0, 40, "D", "NVMe IOCTL"));
        StorageSensorSampler s2 = new StorageSensorSampler(probe2, clock2.Get, clock2.GetMono); s2.Step(); clock2.Advance(119); rows.Clear(); s2.AddRows(rows);
        Check(rows[0].Quality == "OK", "119 s: OK");
        clock2.Advance(2); rows.Clear(); s2.AddRows(rows);
        Check(rows[0].Quality == "Stale" && rows[0].Value == null, "121 s without any step: Stale");
        // a removed drive disappears
        probe2.Samples.Clear(); clock2.Advance(60); s2.Step(); rows.Clear(); s2.AddRows(rows);
        Check(rows.Count == 0, "unplugged disk row removed");
        // recovery after stale
        probe2.Samples.Add(Disk(0, 42, "D", "NVMe IOCTL")); clock2.Advance(60); s2.Step(); rows.Clear(); s2.AddRows(rows);
        Check(rows[0].Quality == "OK" && rows[0].Value == 42, "recovers");
    }

    static void Layer()
    {
        FakeClock clock = new FakeClock();
        ScriptedSource msr = Scripted("PawnIO IntelMSR", () => new CpuSample { Source = "PawnIO IntelMSR", AtUtc = clock.Now, AcquireMs = 0.7, Package = 63, Cores = new[] { 60.0, double.NaN, 66.0 } });
        FakeProbe probe = new FakeProbe(); probe.Samples.Add(Disk(0, 44, "ACME NVMe", "NVMe IOCTL")); probe.Samples[0].AcquireMs = 3;
        SensorLayer layer = new SensorLayer(MakeSampler(clock, () => msr, null), new StorageSensorSampler(probe, clock.Get, clock.GetMono), () => "Intel Core Test", () => "Installed");
        layer.Step();
        List<SensorRow> rows = new List<SensorRow>(); List<string> warnings = new List<string>();
        SensorReadout readout = layer.Read(rows, warnings);
        Check(string.Join(",", rows.Where(x => x.HardwareType == "Cpu").Select(x => x.Name).ToArray()) == "CPU Package,Core Max,Core Average,Core 0,Core 2", "CPU row names (invalid core skipped; unnamed sample = Core n)");
        Check(string.Join(",", rows.Where(x => x.Id.StartsWith("cpu/core/")).Select(x => x.Id).ToArray()) == "cpu/core/0,cpu/core/2", "per-core Ids are cpu/core/<0-based physical index>");
        Check(rows.Where(x => x.HardwareType == "Cpu").All(x => x.Type == "Temperature" && x.Device == "Intel Core Test" && x.Source == "PawnIO IntelMSR" && x.Unit == "°C" && x.PolledAtUtc == clock.Now.ToString("o") && x.Quality == "OK"), "CPU row shape, source and acquisition time");
        Check(rows.Single(x => x.Name == "Core Max").Role == "DerivedTemperature" && rows.Single(x => x.Name == "Core Average").Role == "DerivedTemperature" && rows.Single(x => x.Name == "CPU Package").Role == "Temperature", "roles via SensorRoles.Classify");
        Near(rows.Single(x => x.Name == "Core Max").Value, 66, "Core Max"); Near(rows.Single(x => x.Name == "Core Average").Value, 63, "Core Average");
        Check(rows.Where(x => x.HardwareType == "Cpu").Count(SensorRoles.IsSummary) == 3, "headline CPU rows are the summary rows");
        Near(readout.ControlTemperatureC, 66, "control temperature = hottest current channel (mean of one sample)");
        Near(readout.ProtectionTemperatureC, 66, "protection temperature");
        Check(readout.Source == "PawnIO IntelMSR" && readout.DriverState == "Installed", "readout source and driver state");
        Check(Math.Abs(readout.Timings["PawnIO IntelMSR"] - 0.7) < 1e-9 && readout.Timings.ContainsKey("Storage"), "acquisition timing per source");
        Check(rows.Count(x => x.HardwareType == "Storage") == 1 && warnings.Count == 0, "storage row present, no warnings");
    }

    static void Driver()
    {
        Check(PawnIo.Status(new FakePawnEnv { Opens = true, Exists = true, Running = true }, false) == PawnIoState.Installed, "device opens => Installed");
        Check(PawnIo.Status(new FakePawnEnv(), false) == PawnIoState.Missing, "nothing present => Missing");
        Check(PawnIo.Status(new FakePawnEnv { Exists = true, Running = true }, false) == PawnIoState.HalfUninstalled, "running service, device gone => HalfUninstalled");
        Check(PawnIo.Status(new FakePawnEnv { Exists = true, Running = false }, false) == PawnIoState.NeedsRestart, "registered but not running => NeedsRestart");
        Check(PawnIo.Status(new FakePawnEnv(), true) == PawnIoState.NeedsRestart, "installer asked for a restart => NeedsRestart");
        Check(PawnIo.Status(new FakePawnEnv { Opens = true }, true) == PawnIoState.Installed, "device opened after restart => Installed");

        Check(SensorDriverInstaller.SignerIsExpected("CN=namazso.eu, O=namazso, C=HU") && SensorDriverInstaller.SignerIsExpected("O=x, CN=namazso.eu"), "expected signer accepted");
        Check(!SensorDriverInstaller.SignerIsExpected("CN=evil-namazso.eu") && !SensorDriverInstaller.SignerIsExpected("CN=namazso.eu.evil.com") && !SensorDriverInstaller.SignerIsExpected(null) && !SensorDriverInstaller.SignerIsExpected("O=namazso.eu"), "look-alike signers rejected");
        string dir = Path.Combine(Path.GetTempPath(), "DellFans-SensorTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            FakeHost host = new FakeHost();
            SensorDriverInstallResult result = SensorDriverInstaller.Install(host, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Installed && host.Log.Contains("run -install -silent"), "installer ran");
            Check(host.Log.SequenceEqual(new[] { "elevated", "download " + SensorDriverInstaller.Url, "open", "hash", "verify", "run -install -silent", "loads" }) && host.SameStream, "one stream opened after download and used for hash and signature: " + string.Join("|", host.Log.ToArray()));
            FakeHost stale = new FakeHost { Loads = false };
            result = SensorDriverInstaller.Install(stale, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && result.Message.Contains("did not load") && result.Message.Contains("pnputil"), "installer exit 0 but driver not loaded (stale driver-store package) => Failed with repair steps");
            Check(host.LockedDuringRun, "installer file cannot be written or deleted while it runs");
            FakeHost notAdmin = new FakeHost { Elevated = false };
            result = SensorDriverInstaller.Install(notAdmin, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && result.Message.Contains("administrator") && !notAdmin.Log.Any(l => l.StartsWith("download")), "not elevated: clear message, nothing downloaded");
            FakeHost hang = new FakeHost { HangOnRun = true };
            result = SensorDriverInstaller.Install(hang, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && result.Message.Contains("timed out"), "hung installer => Failed (timeout)");
            Check(SensorDriverInstaller.Url.Contains("/2.2.0/") && SensorDriverInstaller.Sha256 == "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032", "pinned version and hash match Install-SensorDriver.ps1");
            host = new FakeHost { Hash = "00" };
            result = SensorDriverInstaller.Install(host, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && !host.Log.Any(l => l.StartsWith("run") || l == "verify"), "hash mismatch: installer never runs, signature never consulted");
            Check(!File.Exists(Path.Combine(dir, "PawnIO_setup-2.2.0.exe")), "a bad download is deleted");
            host = new FakeHost { Signer = "CN=someone else" };
            result = SensorDriverInstaller.Install(host, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && !host.Log.Any(l => l.StartsWith("run")), "wrong signer: installer never runs");
            host = new FakeHost { Signer = null };
            result = SensorDriverInstaller.Install(host, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && !host.Log.Any(l => l.StartsWith("run")), "invalid signature: installer never runs");
            PawnIo.RestartPending = false;
            host = new FakeHost { ExitCode = 3010 };
            result = SensorDriverInstaller.Install(host, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.RestartRequired && result.ExitCode == 3010 && PawnIo.RestartPending, "exit 3010 => restart required");
            Check(PawnIo.Status(new FakePawnEnv(), PawnIo.RestartPending) == PawnIoState.NeedsRestart, "status reports NeedsRestart afterwards");
            PawnIo.RestartPending = false;
            host = new FakeHost { ExitCode = 1603 };
            result = SensorDriverInstaller.Install(host, dir);
            Check(result.Outcome == SensorDriverInstallOutcome.Failed && result.ExitCode == 1603 && !PawnIo.RestartPending, "other exit codes fail");
            // Real Authenticode verification, read-only: the unsigned test binary must not verify.
            Check(!AuthenticodeVerifier.IsTrusted(typeof(SensorTests).Assembly.Location), "unsigned file does not verify");
            string vendor = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\vendor\PawnIO_setup-2.2.0.exe"));
            if (!File.Exists(vendor)) vendor = Path.GetFullPath(@"vendor\PawnIO_setup-2.2.0.exe");
            if (File.Exists(vendor))
            {
                WindowsInstallerHost real = new WindowsInstallerHost();
                using (Stream locked = real.OpenLocked(vendor))
                {
                    Check(real.Sha256Hex(locked) == SensorDriverInstaller.Sha256, "cached PawnIO_setup-2.2.0.exe matches the pinned SHA-256");
                    Check(SensorDriverInstaller.SignerIsExpected(real.VerifiedSignerSubject(vendor, locked)), "cached PawnIO_setup-2.2.0.exe verifies with signer CN=namazso.eu (WinVerifyTrust + certificate)");
                }
            }
            else Console.WriteLine("note: vendor\\PawnIO_setup-2.2.0.exe not present; real signature check skipped");
            // Protected download directory: explicit DACL (SYSTEM + Administrators, inheritance off), junctions refused.
            string secure = Path.Combine(dir, "secure", "downloads"), junction = Path.Combine(dir, "junction");
            string made = SecureDirectory.CreateFresh(secure);
            System.Security.AccessControl.DirectorySecurity acl = Directory.GetAccessControl(made);
            string[] sids = acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().OrderBy(x => x).ToArray();
            Check(acl.AreAccessRulesProtected && string.Join(",", sids) == "S-1-5-18,S-1-5-32-544", "download directory DACL: protected, SYSTEM + Administrators only: " + string.Join(",", sids));
            Directory.CreateDirectory(Path.Combine(dir, "target"));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + Path.Combine(dir, "target") + "\"") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit();
            Throws<IOException>(delegate { SecureDirectory.CreateFresh(Path.Combine(junction, "downloads")); }, "a reparse-point parent must be refused");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --install-sensor-driver --result-file: the GUI reads the CLI's outcome from a small JSON file; nothing here runs a real installer.
    static void ResultFile()
    {
        Check(SensorDriverInstaller.ResultFileArgument(new[] { "--install-sensor-driver" }) == null, "no result file by default");
        Check(SensorDriverInstaller.ResultFileArgument(new[] { "--install-sensor-driver", "--result-file", @"C:\x y\r.json" }) == @"C:\x y\r.json", "result file argument");
        Throws<ArgumentException>(() => SensorDriverInstaller.ResultFileArgument(new[] { "--install-sensor-driver", "--result-file" }), "missing path refused");
        Throws<ArgumentException>(() => SensorDriverInstaller.ResultFileArgument(new[] { "--install-sensor-driver", "--other", "x" }), "unknown option refused");
        string dir = Path.Combine(Path.GetTempPath(), "DellFans-ResultFile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            Func<string> blank = delegate { string f = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllBytes(f, new byte[0]); return f; };
            TextWriter oldOut = Console.Out, oldErr = Console.Error; Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
            string failed, ok, restart, full, missing = Path.Combine(dir, "missing.json");
            int failedCode, okCode, restartCode;
            try
            {
                failed = blank(); failedCode = SensorDriverInstaller.Report(SensorDriverInstaller.Install(new FakeHost { Elevated = false }, dir), failed);
                ok = blank(); okCode = SensorDriverInstaller.Report(SensorDriverInstaller.Install(new FakeHost(), dir), ok);
                restart = blank(); restartCode = SensorDriverInstaller.Report(SensorDriverInstaller.Install(new FakeHost { ExitCode = 3010 }, dir), restart);
                full = Path.Combine(dir, "full.json"); File.WriteAllText(full, "keep");
                SensorDriverInstaller.Report(new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.Installed, Message = "x" }, full);
                SensorDriverInstaller.Report(new SensorDriverInstallResult { Outcome = SensorDriverInstallOutcome.Installed, Message = "x" }, missing);
            }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); PawnIo.RestartPending = false; }
            Func<string, Dictionary<string, object>> read = f => (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(f));
            Check(failedCode == 1 && (string)read(failed)["Outcome"] == "Failed" && ((string)read(failed)["Message"]).Contains("administrator"), "failure: exit 1, message in the file");
            Check(okCode == 0 && (string)read(ok)["Outcome"] == "Installed", "success: exit 0");
            Check(restartCode == 3010 && (string)read(restart)["Outcome"] == "RestartRequired" && (int)read(restart)["ExitCode"] == 3010, "restart: exit 3010");
            Check(File.ReadAllText(full) == "keep", "a result file that already has content is never overwritten");
            Check(!File.Exists(missing), "a missing result file is not created");
        }
        finally { Directory.Delete(dir, true); }
    }

    static void Pin()
    {
        Check(SensorDecode.Sha256Matches(new byte[] { 1, 2, 3 }, "039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81"), "SHA-256 helper");
        Check(!SensorDecode.Sha256Matches(new byte[] { 1, 2, 4 }, "039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81"), "SHA-256 mismatch");
        string dir = Path.Combine(Path.GetTempPath(), "DellFans-PinTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            Throws<FileNotFoundException>(delegate { PawnIoMsrSource.Create(dir, () => DateTime.UtcNow); }, "Missing IntelMSR.bin");
            File.WriteAllBytes(Path.Combine(dir, "IntelMSR.bin"), new byte[] { 1, 2, 3, 4 });
            Throws<InvalidDataException>(delegate { PawnIoMsrSource.Create(dir, () => DateTime.UtcNow); }, "A tampered module must be rejected before the PawnIO device is touched");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        string shipped = Path.GetFullPath(@"third-party\PawnIO.Modules\IntelMSR.bin");
        if (File.Exists(shipped)) Check(SensorDecode.Sha256Matches(File.ReadAllBytes(shipped), PawnIoMsrSource.ModuleSha256), "repository IntelMSR.bin matches the pinned SHA-256");
        else throw new Exception("third-party\\PawnIO.Modules\\IntelMSR.bin missing from the repository");
    }
}

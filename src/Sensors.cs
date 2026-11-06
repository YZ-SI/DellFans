using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DellFans
{
    // Pure decoders. Every function is table-tested against byte fixtures; none touches hardware.
    public static class SensorDecode
    {
        public const int DefaultTjMax = 100;

        // IA32_TEMPERATURE_TARGET (0x1A2) bits 23:16 = TjMax in degrees C. Zero or implausible values fall back to 100.
        public static int TjMax(long target)
        {
            int value = (int)((target >> 16) & 0xFF);
            return value >= 60 && value <= 125 ? value : DefaultTjMax;
        }
        public static bool TjMaxIsReported(long target)
        {
            int value = (int)((target >> 16) & 0xFF);
            return value >= 60 && value <= 125;
        }

        // IA32_THERM_STATUS (0x19C) bits 30:27 = resolution in degrees C (informational; Intel reports 1 C steps).
        // IA32_PACKAGE_THERM_STATUS (0x1B1) leaves those bits reserved.
        public static int Resolution(long status) { return (int)((status >> 27) & 0xF); }

        // Digital readout = bits 22:16 = degrees BELOW TjMax, valid only while bit 31 is set.
        // A readout deeper than TjMax would be <= 0 C and is rejected as out of range.
        public static double? DigitalReadout(long status, int tjMax)
        {
            if (((status >> 31) & 1) == 0) return null;
            int readout = (int)((status >> 16) & 0x7F);
            double celsius = tjMax - readout;
            return celsius > 0 && celsius <= tjMax ? (double?)celsius : null;
        }

        // IA32_PACKAGE_THERM_STATUS (0x1B1): Intel SDM Vol. 4 / Vol. 3C table, bits 22:16 = Pkg Digital Readout, bits 63:23 reserved - there is
        // no Reading Valid bit (that is 0x19C bit 31 only), so none is required. An all-zero register is an absent read, not 100 C.
        public static double? PackageReadout(long status, int tjMax)
        {
            if (status == 0) return null;
            return DigitalReadout(status | 0x80000000L, tjMax);
        }

        public static bool PlausibleCelsius(double value) { return value >= -40 && value <= 150 && !double.IsNaN(value); }

        // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: Version u32, Size u32, Critical s16, Warning s16, InfoCount u16, reserved to 24;
        // then STORAGE_TEMPERATURE_INFO[16 bytes]: Index u16, Temperature s16, Over s16, Under s16, flags/reserved.
        public static double? StorageTemperatureDescriptor(byte[] b, int length)
        {
            if (b == null || length < 28 || length > b.Length) return null;
            int count = BitConverter.ToUInt16(b, 12);
            double? first = null;
            for (int i = 0; i < count && 24 + 16 * (i + 1) <= length; i++)
            {
                int at = 24 + 16 * i;
                double t = BitConverter.ToInt16(b, at + 2);
                if (!PlausibleCelsius(t)) continue;
                if (BitConverter.ToUInt16(b, at) == 0) return t;
                if (!first.HasValue) first = t;
            }
            return first;
        }

        // NVMe SMART / Health log page 02h: byte 0 critical warning, bytes 1-2 composite temperature in Kelvin.
        public static double? NvmeCompositeTemperature(byte[] b, int offset, int length)
        {
            if (b == null || offset < 0 || length > b.Length || offset + 3 > length) return null;
            int kelvin = BitConverter.ToUInt16(b, offset + 1);
            if (kelvin == 0 || kelvin == 0xFFFF) return null;
            double celsius = kelvin - 273;
            return PlausibleCelsius(celsius) ? (double?)celsius : null;
        }

        // ATA SMART data sector: 2-byte revision then 30 attributes of 12 bytes (id, flags u16, current, worst, raw[6], reserved).
        // Attribute 194 (Temperature) is preferred over 190 (Airflow Temperature); raw byte 0 is the Celsius value.
        public static double? SmartTemperature(byte[] b, int offset, int length)
        {
            if (b == null || offset < 0 || length > b.Length || offset + 512 > length) return null;
            double? airflow = null;
            for (int k = 0; k < 30; k++)
            {
                int at = offset + 2 + k * 12;
                int id = b[at];
                if (id != 194 && id != 190) continue;
                double t = b[at + 5];
                if (t <= 0 || t > 120) continue;
                if (id == 194) return t;
                if (!airflow.HasValue) airflow = t;
            }
            return airflow;
        }

        // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset @12, ProductIdOffset @16 point at NUL-terminated ASCII strings; BusType @28.
        public static string DeviceModel(byte[] b, int length)
        {
            if (b == null || length < 32 || length > b.Length) return null;
            string vendor = AsciiAt(b, length, (int)BitConverter.ToUInt32(b, 12)), product = AsciiAt(b, length, (int)BitConverter.ToUInt32(b, 16));
            string model = ((vendor ?? "") + " " + (product ?? "")).Trim();
            return model.Length == 0 ? null : model;
        }
        public static int DeviceBusType(byte[] b, int length)
        { return b == null || length < 32 || length > b.Length ? 0 : (int)BitConverter.ToUInt32(b, 28); }
        static string AsciiAt(byte[] b, int length, int offset)
        {
            if (offset <= 0 || offset >= length) return null;
            int end = offset;
            while (end < length && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, offset, end - offset).Trim();
        }

        public static bool Sha256Matches(byte[] data, string expectedHex)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""), expectedHex, StringComparison.OrdinalIgnoreCase);
        }
    }

    // One physical core. Pin to (Group, LogicalIndex) = the first set bit of the core's mask; HT siblings share the same 0x19C.
    public sealed class CpuCore
    {
        public int Group, LogicalIndex, LogicalCount, EfficiencyClass;
        public int LogicalId { get { return Group * 64 + LogicalIndex; } } // id handed to IMsrTransport
    }

    public static class CpuTopology
    {
        // x64 SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX, RelationProcessorCore (0): Relationship@0 Size@4, PROCESSOR_RELATIONSHIP@8
        // (EfficiencyClass@9, GroupCount@30, GROUP_AFFINITY[]@32 = Mask(8) Group@+8). Other relationships are skipped by Size.
        // shortcut: a core is read as lying in its first group only (true on Windows), 64-bit layout only (x64 build).
        public static List<CpuCore> Parse(byte[] buffer, int length)
        {
            List<CpuCore> cores = new List<CpuCore>();
            int o = 0;
            while (o + 8 <= length)
            {
                int relation = BitConverter.ToInt32(buffer, o), size = BitConverter.ToInt32(buffer, o + 4);
                if (size < 8 || o + size > length) break;
                if (relation == 0 && size >= 48 && BitConverter.ToUInt16(buffer, o + 30) >= 1)
                {
                    ulong mask = BitConverter.ToUInt64(buffer, o + 32);
                    if (mask != 0)
                    {
                        int first = 0, count = 0;
                        while ((mask & (1UL << first)) == 0) first++;
                        for (ulong m = mask; m != 0; m &= m - 1) count++;
                        cores.Add(new CpuCore { Group = BitConverter.ToUInt16(buffer, o + 40), LogicalIndex = first, LogicalCount = count, EfficiencyClass = buffer[o + 9] });
                    }
                }
                o += size;
            }
            return cores;
        }

        public static bool IsHybrid(List<CpuCore> cores) { return cores.Any(c => c.EfficiencyClass != cores[0].EfficiencyClass); }

        // Hybrid: higher EfficiencyClass = P-core; one index sequence in enumeration order (HWiNFO style). Otherwise "Core n".
        public static List<string> Names(List<CpuCore> cores)
        {
            bool hybrid = cores.Count > 0 && IsHybrid(cores);
            int top = hybrid ? cores.Max(c => c.EfficiencyClass) : 0;
            List<string> names = new List<string>();
            for (int i = 0; i < cores.Count; i++)
                names.Add((hybrid ? (cores[i].EfficiencyClass == top ? "P-core " : "E-core ") : "Core ") + i);
            return names;
        }

        public static List<CpuCore> Detect()
        {
            int length = 0;
            SensorNative.GetLogicalProcessorInformationEx(0, null, ref length);
            if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetLogicalProcessorInformationEx size query");
            byte[] buffer = new byte[length];
            if (!SensorNative.GetLogicalProcessorInformationEx(0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetLogicalProcessorInformationEx");
            return Parse(buffer, length);
        }
    }

    public sealed class CpuSample
    {
        public string Source;
        public DateTime AtUtc;          // taken right after the IOCTL / WMI call returned; display and duplicate detection only
        public double MonoSeconds;      // monotonic acquisition time stamped by the sampler; ages and staleness use this
        public double AcquireMs;
        public double? Package;
        public double[] Cores = new double[0]; // one per physical core; NaN = invalid readout
        public string[] CoreNames;             // parallel to Cores; null = "Core n"
        public string Note;
        public double? Hottest()
        {
            double? best = Package;
            foreach (double c in Cores) if (!double.IsNaN(c) && (!best.HasValue || c > best.Value)) best = c;
            return best;
        }
    }

    public interface ICpuTemperatureSource : IDisposable
    {
        string Name { get; }
        CpuSample Read(); // throws when no valid temperature could be obtained
    }

    // ---- MSR path (PawnIO IntelMSR) ----
    public interface IMsrTransport : IDisposable
    {
        long ReadMsr(uint msr, int logicalCpu);
    }

    public sealed class PawnIoMsrSource : ICpuTemperatureSource
    {
        public const string SourceName = "PawnIO IntelMSR";
        // SHA-256 of IntelMSR.bin as shipped (PawnIO.Modules 0.2.2 as vendored by LibreHardwareMonitor v0.9.6).
        public const string ModuleSha256 = "ED41A0D0DE082F4668A9CACCB9681EBBAD97A8ED3452C41918DDA1EB454B77A9";
        readonly IMsrTransport transport;
        readonly Func<DateTime> clock;
        readonly List<CpuCore> cores;
        readonly string[] coreNames;
        readonly int tjMax;
        readonly bool tjMaxReported;
        public string Name { get { return SourceName; } }

        // Fallback when the topology is unknown: treat every logical CPU of group 0 as its own core.
        public PawnIoMsrSource(IMsrTransport transport, int logicalCpus, Func<DateTime> clock)
            : this(transport, Enumerable.Range(0, Math.Max(1, Math.Min(64, logicalCpus))).Select(i => new CpuCore { LogicalIndex = i, LogicalCount = 1 }).ToList(), clock) { }

        public PawnIoMsrSource(IMsrTransport transport, List<CpuCore> cores, Func<DateTime> clock)
        {
            if (cores == null || cores.Count == 0) throw new ArgumentException("No physical cores.");
            this.transport = transport; this.clock = clock; this.cores = cores; coreNames = CpuTopology.Names(cores).ToArray();
            long target = transport.ReadMsr(0x1A2, 0);
            tjMax = SensorDecode.TjMax(target); tjMaxReported = SensorDecode.TjMaxIsReported(target);
        }

        public CpuSample Read()
        {
            Stopwatch watch = Stopwatch.StartNew();
            // A failing read (the transport restores the thread affinity in its own finally) only loses that one channel.
            int failed = 0; string firstError = null;
            double? package = null;
            try { package = SensorDecode.PackageReadout(transport.ReadMsr(0x1B1, 0), tjMax); }
            catch (Exception e) { failed++; firstError = e.Message; }
            double[] temps = new double[cores.Count];
            bool any = package.HasValue;
            for (int i = 0; i < temps.Length; i++)
            {
                double? value = null;
                try { value = SensorDecode.DigitalReadout(transport.ReadMsr(0x19C, cores[i].LogicalId), tjMax); }
                catch (Exception e) { failed++; if (firstError == null) firstError = e.Message; }
                temps[i] = value.HasValue ? value.Value : double.NaN;
                any |= value.HasValue;
            }
            DateTime at = clock();
            if (failed == temps.Length + 1) throw new IOException("Every MSR read failed: " + firstError);
            if (!any) throw new InvalidDataException("No valid digital thermal readout (bit 31 clear on every 0x19C, no package readout).");
            return new CpuSample { Source = SourceName, AtUtc = at, AcquireMs = watch.Elapsed.TotalMilliseconds, Package = package, Cores = temps, CoreNames = coreNames,
                Note = tjMaxReported ? null : "TjMax was not reported by MSR 0x1A2; assuming 100 C." };
        }
        public void Dispose() { transport.Dispose(); }

        public static ICpuTemperatureSource Create(string directory, Func<DateTime> clock)
        {
            byte[] module = File.ReadAllBytes(Path.Combine(directory, "IntelMSR.bin"));
            if (!SensorDecode.Sha256Matches(module, ModuleSha256)) throw new InvalidDataException("IntelMSR.bin does not match its pinned SHA-256; not loaded.");
            PawnIoTransport transport = new PawnIoTransport(module);
            List<CpuCore> topology = null;
            try { topology = CpuTopology.Detect(); } catch (Win32Exception) { }
            try { return topology != null && topology.Count > 0 ? new PawnIoMsrSource(transport, topology, clock) : new PawnIoMsrSource(transport, Environment.ProcessorCount, clock); }
            catch { transport.Dispose(); throw; }
        }
    }

    // Raw PawnIO device protocol. The only function ever executed is ioctl_read_msr, and only for three whitelisted registers.
    public sealed class PawnIoTransport : IMsrTransport
    {
        static uint Ctl(uint function) { return (41394u << 16) | (function << 2); }
        readonly SafeFileHandle device;
        readonly object gate = new object();
        public PawnIoTransport(byte[] module)
        {
            device = SensorNative.CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the PawnIO device");
            int returned;
            if (!SensorNative.DeviceIoControl(device, Ctl(0x821), module, module.Length, null, 0, out returned, IntPtr.Zero))
            { int error = Marshal.GetLastWin32Error(); device.Dispose(); throw new Win32Exception(error, "PawnIO rejected the IntelMSR module"); }
        }
        public long ReadMsr(uint msr, int logicalCpu)
        {
            if (msr != 0x19C && msr != 0x1B1 && msr != 0x1A2) throw new ArgumentException("Only MSRs 0x19C, 0x1B1 and 0x1A2 may be read.");
            if (logicalCpu < 0 || logicalCpu >= 64 * 65535) throw new ArgumentOutOfRangeException("logicalCpu");
            lock (gate) // logicalCpu = processor group * 64 + bit
            {
                IntPtr thread = SensorNative.GetCurrentThread();
                SensorNative.GroupAffinity wanted = new SensorNative.GroupAffinity { Mask = (UIntPtr)(1UL << (logicalCpu & 63)), Group = (ushort)(logicalCpu >> 6) }, previous, ignored;
                if (!SensorNative.SetThreadGroupAffinity(thread, ref wanted, out previous)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadGroupAffinity");
                try
                {
                    byte[] input = new byte[40], output = new byte[8]; int returned;
                    Encoding.ASCII.GetBytes("ioctl_read_msr").CopyTo(input, 0);
                    BitConverter.GetBytes((long)msr).CopyTo(input, 32);
                    if (!SensorNative.DeviceIoControl(device, Ctl(0x841), input, input.Length, output, output.Length, out returned, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO rdmsr 0x" + msr.ToString("X"));
                    return BitConverter.ToInt64(output, 0);
                }
                finally { SensorNative.SetThreadGroupAffinity(thread, ref previous, out ignored); }
            }
        }
        public void Dispose() { device.Dispose(); }
    }

    // ---- ESIF path (Intel Innovation Platform Framework, root\wmi) ----
    public interface IEsifReader : IDisposable
    {
        List<KeyValuePair<string, double>> Read(); // InstanceName, Temperature
    }

    public sealed class EsifSource : ICpuTemperatureSource
    {
        public const string SourceName = "Intel ESIF";
        readonly IEsifReader reader;
        readonly Func<DateTime> clock;
        public string Name { get { return SourceName; } }
        public EsifSource(IEsifReader reader, Func<DateTime> clock) { this.reader = reader; this.clock = clock; }

        // Instance names end in _N; instances 1-3 carry the package-level temperature, 0 is unused, 4+ are not CPU.
        public static double? Aggregate(IEnumerable<KeyValuePair<string, double>> instances)
        {
            double? best = null;
            foreach (KeyValuePair<string, double> item in instances)
            {
                string name = item.Key ?? "";
                int underscore = name.LastIndexOf('_'), index;
                if (underscore < 0 || !int.TryParse(name.Substring(underscore + 1), NumberStyles.None, CultureInfo.InvariantCulture, out index)) continue;
                if (index < 1 || index > 3 || item.Value <= 0 || item.Value > 125) continue;
                if (!best.HasValue || item.Value > best.Value) best = item.Value;
            }
            return best;
        }
        public CpuSample Read()
        {
            Stopwatch watch = Stopwatch.StartNew();
            double? value = Aggregate(reader.Read());
            DateTime at = clock();
            if (!value.HasValue) throw new InvalidDataException("ESIF returned no usable temperature (instances 1-3).");
            return new CpuSample { Source = SourceName, AtUtc = at, AcquireMs = watch.Elapsed.TotalMilliseconds, Package = value };
        }
        public void Dispose() { reader.Dispose(); }
        public static ICpuTemperatureSource Create(Func<DateTime> clock) { return new EsifSource(new WmiEsifReader(), clock); }
    }

    public sealed class WmiEsifReader : IEsifReader
    {
        System.Management.ManagementObjectSearcher searcher;
        public WmiEsifReader()
        {
            searcher = new System.Management.ManagementObjectSearcher(new System.Management.ManagementScope(@"root\wmi"),
                new System.Management.ObjectQuery("SELECT InstanceName,Temperature FROM EsifDeviceInformation"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false });
        }
        public List<KeyValuePair<string, double>> Read()
        {
            List<KeyValuePair<string, double>> result = new List<KeyValuePair<string, double>>();
            using (System.Management.ManagementObjectCollection collection = searcher.Get())
                foreach (System.Management.ManagementBaseObject item in collection)
                {
                    using (item) result.Add(new KeyValuePair<string, double>(Convert.ToString(item["InstanceName"]), Convert.ToDouble(item["Temperature"], CultureInfo.InvariantCulture)));
                }
            return result;
        }
        public void Dispose() { if (searcher != null) searcher.Dispose(); searcher = null; }
    }

    // Ages, ordering, staleness and scheduling use this monotonic clock; wall-clock DateTime is only kept for display (PolledAtUtc).
    public static class SensorClock
    {
        public static double Monotonic() { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
    }

    public sealed class CpuReading
    {
        public string Source;
        public CpuSample Sample;
        public double AgeSeconds;
        public bool Stale;
        public double? ControlMeanC;  // mean of the last 1 s of samples; null when stale or absent
        public double? RawMaxC;       // protection value: hottest raw reading since the previous GetReading and in the last 1 s; null when stale
        public List<string> Warnings = new List<string>();
    }

    // Owns source selection, the 1 s window and the stale rule. Step() is one iteration; the layer's thread calls it every 250 ms.
    public sealed class CpuSensorSampler : IDisposable
    {
        public const double StaleSeconds = 2.0;
        public const double WindowSeconds = 1.0;
        public const double MismatchDegrees = 8.0, MismatchSeconds = 10.0;
        public const double CrossCheckSeconds = 5.0, CrossCheckSlowSeconds = 0.5, FrozenEsifSeconds = 30.0;
        readonly Func<ICpuTemperatureSource> msrFactory, esifFactory;
        readonly Func<double> mono;
        readonly Action<Action> background;
        readonly object gate = new object();
        readonly List<KeyValuePair<double, double>> window = new List<KeyValuePair<double, double>>(); // monotonic time, hottest channel
        ICpuTemperatureSource active, cross;
        bool activeIsMsr, disposed, stepping, crossBusy, crossDiscard;
        double nextSelect = double.MinValue, nextUpgrade = double.MinValue, nextCross = double.MinValue, nextCrossCreate = double.MinValue;
        CpuSample last;
        int failures;
        string lastError, mismatchText;
        double? mismatchSince, peakSinceRead, esifValue;
        double esifChangedAt;
        double crossAcquireMs;

        // Sources stamp AtUtc for display; mono drives every decision. background runs the ESIF cross-check (null = thread pool).
        public CpuSensorSampler(Func<ICpuTemperatureSource> msrFactory, Func<ICpuTemperatureSource> esifFactory, Func<DateTime> clock, Func<double> mono, Action<Action> background)
        {
            this.msrFactory = msrFactory; this.esifFactory = esifFactory; this.mono = mono;
            this.background = background ?? delegate (Action work) { ThreadPool.QueueUserWorkItem(delegate { work(); }); };
        }

        public double CrossCheckAcquireMs { get { lock (gate) return crossAcquireMs; } }

        public void RecordError(string message) { lock (gate) lastError = message; }

        public void Step()
        {
            lock (gate) { if (disposed) return; stepping = true; }
            try { StepCore(); }
            finally
            {
                bool cleanup; lock (gate) { stepping = false; cleanup = disposed; }
                if (cleanup) Cleanup(); // Dispose arrived mid-step: the stepping thread releases the handles itself
            }
        }

        void StepCore()
        {
            double now = mono();
            if (active == null) { if (now >= nextSelect) Select(now); }
            else if (!activeIsMsr && msrFactory != null && now >= nextUpgrade) Upgrade(now);
            if (active == null) return;
            CpuSample sample = null;
            try { sample = active.Read(); failures = 0; }
            catch (Exception e)
            {
                failures++;
                lock (gate) lastError = active.Name + ": " + e.Message;
                if (failures >= 8) Drop(now); // ~2 s of consecutive failures; the stale rule has already fired
            }
            if (sample != null) Accept(sample, mono());
            if (active != null && activeIsMsr && esifFactory != null && now >= nextCross) StartCross(now);
        }

        void Accept(CpuSample sample, double at)
        {
            lock (gate)
            {
                // A source that returns the same acquisition time again produced no new sample; it must go stale.
                if (last != null && sample.AtUtc == last.AtUtc) return;
                sample.MonoSeconds = at;
                last = sample;
                double? hottest = sample.Hottest();
                if (hottest.HasValue)
                {
                    window.Add(new KeyValuePair<double, double>(at, hottest.Value));
                    if (!peakSinceRead.HasValue || hottest.Value > peakSinceRead.Value) peakSinceRead = hottest;
                }
                window.RemoveAll(p => at - p.Key > WindowSeconds);
                if (sample.Source == EsifSource.SourceName)
                {
                    if (!esifValue.HasValue || sample.Package != esifValue) { esifValue = sample.Package; esifChangedAt = at; }
                }
                else esifValue = null;
                lastError = null;
            }
        }

        void Select(double now)
        {
            List<string> errors = new List<string>();
            Func<ICpuTemperatureSource>[] factories = new[] { msrFactory, esifFactory };
            for (int i = 0; i < factories.Length; i++)
            {
                if (factories[i] == null) continue;
                lock (gate) if (disposed) return;
                ICpuTemperatureSource source = null;
                try
                {
                    source = factories[i]();
                    CpuSample sample = source.Read();
                    active = source; activeIsMsr = i == 0; failures = 0;
                    nextUpgrade = now + 60; nextCross = now;
                    Accept(sample, mono());
                    return;
                }
                catch (Exception e) { if (source != null) source.Dispose(); errors.Add((i == 0 ? PawnIoMsrSource.SourceName : EsifSource.SourceName) + ": " + e.Message); }
            }
            nextSelect = now + 5;
            lock (gate) lastError = string.Join("; ", errors.ToArray());
        }

        void Upgrade(double now)
        {
            nextUpgrade = now + 60;
            lock (gate) if (disposed) return;
            ICpuTemperatureSource source = null;
            try
            {
                source = msrFactory();
                CpuSample sample = source.Read();
                ICpuTemperatureSource old = active;
                active = source; activeIsMsr = true; failures = 0; nextCross = now;
                old.Dispose();
                Accept(sample, mono());
            }
            catch { if (source != null) source.Dispose(); }
        }

        void Drop(double now)
        {
            active.Dispose(); active = null; activeIsMsr = false; failures = 0; nextSelect = now;
            ReleaseCross();
        }

        // Disposes the cross-check reader now, or lets the running cross-check do it when it returns.
        void ReleaseCross()
        {
            ICpuTemperatureSource c = null;
            lock (gate) { if (cross != null) { if (crossBusy) crossDiscard = true; else { c = cross; cross = null; } } }
            if (c != null) c.Dispose();
        }

        void Cleanup()
        {
            ICpuTemperatureSource a = active; active = null;
            if (a != null) a.Dispose();
            ReleaseCross();
        }

        // The ESIF cross-check never runs on the sampler thread: a stuck WMI call must not delay MSR samples.
        void StartCross(double now)
        {
            lock (gate) { if (disposed || crossBusy) return; crossBusy = true; nextCross = now + CrossCheckSeconds; }
            background(RunCross);
        }

        void RunCross()
        {
            try
            {
                ICpuTemperatureSource source;
                lock (gate) source = cross;
                if (source == null)
                {
                    lock (gate) if (disposed || mono() < nextCrossCreate) return;
                    source = esifFactory();
                    bool keep; lock (gate) { keep = !disposed && !crossDiscard; if (keep) cross = source; }
                    if (!keep) { source.Dispose(); return; }
                }
                double started = mono();
                CpuSample reference = source.Read();
                double finished = mono();
                CpuSample msr; lock (gate) msr = last;
                if (msr == null) return;
                double mine = msr.Package ?? msr.Hottest() ?? double.NaN;
                bool differs = !double.IsNaN(mine) && Math.Abs(mine - reference.Package.Value) > MismatchDegrees;
                lock (gate)
                {
                    crossAcquireMs = reference.AcquireMs;
                    if (finished - started > CrossCheckSlowSeconds) nextCross = finished + 2 * CrossCheckSeconds; // a slow call skips the next interval
                    if (!differs) { mismatchSince = null; mismatchText = null; }
                    else
                    {
                        if (!mismatchSince.HasValue) mismatchSince = finished;
                        if (finished - mismatchSince.Value >= MismatchSeconds)
                            mismatchText = string.Format(CultureInfo.InvariantCulture, "CPU temperature sources disagree: {0} {1:0} C vs {2} {3:0} C for more than {4:0} s.",
                                PawnIoMsrSource.SourceName, mine, EsifSource.SourceName, reference.Package.Value, MismatchSeconds);
                    }
                }
            }
            catch
            {
                ICpuTemperatureSource bad;
                lock (gate) { bad = cross; cross = null; nextCrossCreate = mono() + 60; }
                if (bad != null) bad.Dispose();
            }
            finally
            {
                ICpuTemperatureSource drop = null;
                lock (gate) { crossBusy = false; if (crossDiscard) { drop = cross; cross = null; crossDiscard = false; } }
                if (drop != null) drop.Dispose();
            }
        }

        public CpuReading GetReading()
        {
            lock (gate)
            {
                double now = mono();
                CpuReading reading = new CpuReading();
                if (mismatchText != null) reading.Warnings.Add(mismatchText);
                double? peak = peakSinceRead; peakSinceRead = null; // the peak is handed out exactly once
                if (last == null)
                {
                    if (lastError != null) reading.Warnings.Add("CPU temperature source: " + lastError);
                    return reading;
                }
                reading.Sample = last; reading.Source = last.Source;
                reading.AgeSeconds = Math.Max(0, now - last.MonoSeconds);
                reading.Stale = reading.AgeSeconds > StaleSeconds;
                if (last.Note != null) reading.Warnings.Add(last.Note);
                if (reading.Stale)
                {
                    reading.Warnings.Add("CPU temperature is stale (" + reading.AgeSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s since the last " + last.Source + " sample)." +
                        (lastError == null ? "" : " " + lastError));
                    return reading;
                }
                if (lastError != null) reading.Warnings.Add("CPU temperature source: " + lastError);
                if (esifValue.HasValue && now - esifChangedAt >= FrozenEsifSeconds)
                    reading.Warnings.Add("Intel ESIF value has not changed for " + (now - esifChangedAt).ToString("0", CultureInfo.InvariantCulture) + " s and is the only CPU source; it may be frozen. Install the PawnIO driver for MSR readings.");
                List<double> recent = window.Where(p => now - p.Key <= WindowSeconds).Select(p => p.Value).ToList();
                if (recent.Count > 0) { reading.ControlMeanC = recent.Average(); reading.RawMaxC = recent.Max(); }
                else if (last.Hottest().HasValue) reading.ControlMeanC = reading.RawMaxC = last.Hottest(); // 1-2 s old: the newest sample until the stale limit
                if (peak.HasValue && (!reading.RawMaxC.HasValue || peak.Value > reading.RawMaxC.Value)) reading.RawMaxC = peak;
                return reading;
            }
        }

        // Safe while a step is running: the stepping thread releases the sources when it returns.
        public void Dispose()
        {
            bool now; lock (gate) { disposed = true; now = !stepping; }
            if (now) Cleanup();
        }
    }

    // ---- Storage ----
    public sealed class StorageSample
    {
        public int Index;
        public string Model;
        public double? TempC;
        public string Source;
        public DateTime AtUtc;          // display only
        public double MonoSeconds;      // stamped by the sampler when the enumeration returned
        public double AcquireMs;
        public string Error;
    }

    public interface IStorageProbe
    {
        List<StorageSample> Read(Func<DateTime> clock);
    }

    public sealed class StorageSensorSampler
    {
        public const double StaleSeconds = 120.0;
        public const double IntervalSeconds = 60.0;
        sealed class Slot { public StorageSample Good; public string Model; public string Error; }
        readonly IStorageProbe probe;
        readonly Func<DateTime> clock;
        readonly Func<double> mono;
        readonly object gate = new object();
        readonly SortedDictionary<int, Slot> slots = new SortedDictionary<int, Slot>();
        double next = double.MinValue;
        double lastEnumerationMs;
        string loopError;
        public StorageSensorSampler(IStorageProbe probe, Func<DateTime> clock, Func<double> mono) { this.probe = probe; this.clock = clock; this.mono = mono; }
        public double LastEnumerationMs { get { lock (gate) return lastEnumerationMs; } }
        public void RecordError(string message) { lock (gate) loopError = message; }

        public void Step()
        {
            double now = mono();
            if (now < next) return;
            next = now + IntervalSeconds;
            Stopwatch watch = Stopwatch.StartNew();
            List<StorageSample> samples;
            try { samples = probe.Read(clock); }
            catch (Exception e) { lock (gate) foreach (Slot slot in slots.Values) slot.Error = e.Message; return; }
            double at = mono();
            lock (gate)
            {
                lastEnumerationMs = watch.Elapsed.TotalMilliseconds;
                loopError = null;
                foreach (int gone in slots.Keys.Where(k => !samples.Any(s => s.Index == k)).ToList()) slots.Remove(gone);
                foreach (StorageSample sample in samples)
                {
                    sample.MonoSeconds = at;
                    Slot slot;
                    if (!slots.TryGetValue(sample.Index, out slot)) slots[sample.Index] = slot = new Slot();
                    slot.Model = sample.Model ?? slot.Model; slot.Error = sample.Error;
                    if (sample.TempC.HasValue) slot.Good = sample;
                }
            }
        }

        public void AddRows(List<SensorRow> rows)
        {
            lock (gate)
            {
                double now = mono();
                foreach (KeyValuePair<int, Slot> item in slots)
                {
                    Slot slot = item.Value; StorageSample good = slot.Good;
                    double age = good == null ? 0 : now - good.MonoSeconds;
                    bool fresh = good != null && age >= 0 && age <= StaleSeconds; // a negative age is a broken clock, not a fresh reading
                    rows.Add(new SensorRow { Id = "storage/" + item.Key + "/temperature", Device = slot.Model ?? ("PhysicalDrive" + item.Key),
                        HardwareType = "Storage", Name = "Temperature", Type = "Temperature", Role = SensorRoles.Classify("Temperature", "Temperature"),
                        Unit = SensorRoles.UnitFor("Temperature"), Value = fresh ? (float?)good.TempC.Value : null,
                        Source = good != null ? good.Source : "Windows storage IOCTL",
                        Quality = good == null ? "Unavailable" : fresh ? "OK" : "Stale",
                        PolledAtUtc = good == null ? null : good.AtUtc.ToString("o") });
                }
            }
        }
        public List<string> Warnings()
        {
            lock (gate)
            {
                List<string> warnings = slots.Where(s => s.Value.Good == null && s.Value.Error != null).Select(s => "Storage PhysicalDrive" + s.Key + ": " + s.Value.Error).ToList();
                if (loopError != null) warnings.Add("Storage sampler: " + loopError);
                return warnings;
            }
        }
    }

    public sealed class WindowsStorageProbe : IStorageProbe
    {
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400, SMART_RCV_DRIVE_DATA = 0x0007C088;
        const uint StorageDeviceProperty = 0, StorageDeviceProtocolSpecificProperty = 50, StorageDeviceTemperatureProperty = 52;

        public List<StorageSample> Read(Func<DateTime> clock)
        {
            List<StorageSample> result = new List<StorageSample>();
            for (int n = 0; n < 16; n++)
            {
                // Access 0: metadata queries only. Nothing is read from or written to the media.
                using (SafeFileHandle handle = SensorNative.CreateFile(@"\\.\PhysicalDrive" + n, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) continue;
                    StorageSample sample = new StorageSample { Index = n, AtUtc = clock() };
                    Stopwatch watch = Stopwatch.StartNew();
                    int bus = 0;
                    try
                    {
                        byte[] descriptor = new byte[1024]; int length;
                        if (Query(handle, StorageDeviceProperty, descriptor, out length))
                        { sample.Model = SensorDecode.DeviceModel(descriptor, length); bus = SensorDecode.DeviceBusType(descriptor, length); }
                        double? value = ReadTemperatureProperty(handle, out length);
                        if (value.HasValue) { sample.TempC = value; sample.Source = "NVMe IOCTL"; }
                        else if (bus == 17 || bus == 0)
                        {
                            value = ReadNvmeHealth(handle);
                            if (value.HasValue) { sample.TempC = value; sample.Source = "NVMe IOCTL"; }
                        }
                        if (!sample.TempC.HasValue && (bus == 3 || bus == 11))
                        {
                            value = ReadSmart(n, out sample.Error);
                            if (value.HasValue) { sample.TempC = value; sample.Source = "SATA SMART"; }
                        }
                        if (!sample.TempC.HasValue && sample.Error == null) sample.Error = "No temperature interface answered (bus type " + bus + ").";
                    }
                    catch (Exception e) { sample.Error = e.Message; }
                    sample.AtUtc = clock(); sample.AcquireMs = watch.Elapsed.TotalMilliseconds;
                    result.Add(sample);
                }
            }
            return result;
        }

        static bool Query(SafeFileHandle handle, uint propertyId, byte[] output, out int length)
        {
            byte[] query = new byte[12]; BitConverter.GetBytes(propertyId).CopyTo(query, 0);
            return SensorNative.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out length, IntPtr.Zero);
        }
        static double? ReadTemperatureProperty(SafeFileHandle handle, out int length)
        {
            byte[] output = new byte[512];
            return Query(handle, StorageDeviceTemperatureProperty, output, out length) ? SensorDecode.StorageTemperatureDescriptor(output, length) : null;
        }
        static double? ReadNvmeHealth(SafeFileHandle handle)
        {
            byte[] b = new byte[8 + 40 + 512]; int length;
            BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(b, 0);
            BitConverter.GetBytes(3u).CopyTo(b, 8);   // ProtocolTypeNvme
            BitConverter.GetBytes(2u).CopyTo(b, 12);  // NVMeDataTypeLogPage
            BitConverter.GetBytes(2u).CopyTo(b, 16);  // log page 02h: SMART / Health Information
            BitConverter.GetBytes(0u).CopyTo(b, 20);
            BitConverter.GetBytes(40u).CopyTo(b, 24); // data starts after the 40-byte specific-data block
            BitConverter.GetBytes(512u).CopyTo(b, 28);
            if (!SensorNative.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, b, b.Length, b, b.Length, out length, IntPtr.Zero)) return null;
            return SensorDecode.NvmeCompositeTemperature(b, 48, length);
        }
        // SMART_RCV_DRIVE_DATA needs a read/write handle. The one command sent is SMART READ DATA (feature 0xD0): it reads the
        // attribute table and changes nothing on the drive.
        static double? ReadSmart(int n, out string error)
        {
            error = null;
            using (SafeFileHandle handle = SensorNative.CreateFile(@"\\.\PhysicalDrive" + n, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) { error = "SMART needs a read/write handle: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return null; }
                byte[] input = new byte[33], output = new byte[16 + 512]; int length;
                BitConverter.GetBytes(512).CopyTo(input, 0);
                input[4] = 0xD0; input[5] = 1; input[6] = 1; input[7] = 0x4F; input[8] = 0xC2; input[9] = 0xA0; input[10] = 0xB0; // SMART_READ_DATA
                if (!SensorNative.DeviceIoControl(handle, SMART_RCV_DRIVE_DATA, input, input.Length, output, output.Length, out length, IntPtr.Zero))
                { error = "SMART read failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return null; }
                double? value = SensorDecode.SmartTemperature(output, 16, length);
                if (!value.HasValue) error = "SMART table has no temperature attribute (190/194).";
                return value;
            }
        }
    }

    internal static class SensorNative
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
        [StructLayout(LayoutKind.Sequential)]
        public struct GroupAffinity { public UIntPtr Mask; public ushort Group; public ushort Reserved0, Reserved1, Reserved2; }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetLogicalProcessorInformationEx(int relationship, byte[] buffer, ref int length);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread();
    }

    // ---- Layer: threads + SensorRows ----
    public sealed class SensorReadout
    {
        public double? ControlTemperatureC;
        public double? ProtectionTemperatureC;
        public string Source;
        public string DriverState;
        public Dictionary<string, double> Timings = new Dictionary<string, double>();
    }

    public sealed class SensorLayer : IDisposable
    {
        readonly CpuSensorSampler cpu;
        readonly StorageSensorSampler storage;
        readonly Func<string> cpuName;
        readonly Func<string> driverState;
        readonly object stateGate = new object();
        string cachedCpuName, cachedDriverState;
        double nextDriverCheck = double.MinValue;
        bool disposed;
        Thread cpuThread, storageThread;
        readonly ManualResetEvent stop = new ManualResetEvent(false);

        public SensorLayer(CpuSensorSampler cpu, StorageSensorSampler storage, Func<string> cpuName, Func<string> driverState)
        { this.cpu = cpu; this.storage = storage; this.cpuName = cpuName; this.driverState = driverState; }

        public static SensorLayer CreateDefault()
        {
            Func<DateTime> clock = delegate { return DateTime.UtcNow; };
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            CpuSensorSampler cpu = new CpuSensorSampler(delegate { return PawnIoMsrSource.Create(directory, clock); },
                delegate { return EsifSource.Create(clock); }, clock, SensorClock.Monotonic, null);
            return new SensorLayer(cpu, new StorageSensorSampler(new WindowsStorageProbe(), clock, SensorClock.Monotonic), ProcessorName,
                delegate { return PawnIo.Status().ToString(); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string ProcessorName()
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(@"root\cimv2", "SELECT Name FROM Win32_Processor"))
                using (System.Management.ManagementObjectCollection c = s.Get())
                    foreach (System.Management.ManagementBaseObject o in c) using (o) return Collapse(Convert.ToString(o["Name"]));
            }
            catch { }
            return "CPU";
        }
        static string Collapse(string text) { return string.Join(" ", (text ?? "CPU").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)); }

        public void Start()
        {
            lock (stateGate)
            {
                if (cpuThread != null || disposed) return;
                Guarded(cpu.Step, cpu.RecordError); // the first poll right after construction already has a CPU reading
                // AboveNormal: a busy machine must not starve the sampler into false staleness (which would hand the fan back to the BIOS).
                cpuThread = new Thread(delegate () { Loop(cpu.Step, cpu.RecordError, 250); }) { IsBackground = true, Name = "Sensor CPU sampler", Priority = ThreadPriority.AboveNormal };
                storageThread = new Thread(delegate () { Loop(storage.Step, storage.RecordError, 1000); }) { IsBackground = true, Name = "Sensor storage sampler" };
                cpuThread.Start(); storageThread.Start();
            }
        }
        public ThreadPriority? CpuThreadPriority { get { lock (stateGate) return cpuThread == null ? (ThreadPriority?)null : cpuThread.Priority; } }

        // A step that throws must not kill its loop; the message becomes a warning instead of vanishing.
        public static void Guarded(Action step, Action<string> record)
        {
            try { step(); }
            catch (Exception e) { try { record(e.Message); } catch { } }
        }
        void Loop(Action step, Action<string> record, int periodMs)
        {
            Stopwatch watch = new Stopwatch();
            while (true)
            {
                watch.Restart();
                Guarded(step, record);
                int wait = Math.Max(10, periodMs - (int)watch.ElapsedMilliseconds);
                if (stop.WaitOne(wait)) return;
            }
        }
        // Tests drive the layer without threads.
        public void Step() { cpu.Step(); storage.Step(); }

        public SensorReadout Read(List<SensorRow> rows, List<string> warnings)
        {
            CpuReading reading = cpu.GetReading();
            SensorReadout readout = new SensorReadout { Source = reading.Source ?? "None" };
            foreach (string warning in reading.Warnings) warnings.Add(warning);
            if (reading.Sample != null)
            {
                string device;
                lock (stateGate) device = cachedCpuName ?? (cachedCpuName = cpuName());
                string quality = reading.Stale ? "Stale" : "OK";
                string at = reading.Sample.AtUtc.ToString("o");
                if (reading.Sample.Package.HasValue) rows.Add(CpuRow("cpu/package", device, "CPU Package", reading.Sample.Package.Value, reading.Sample, quality, at, reading.Stale));
                double max = double.NaN, sum = 0; int valid = 0;
                for (int i = 0; i < reading.Sample.Cores.Length; i++)
                {
                    double v = reading.Sample.Cores[i];
                    if (double.IsNaN(v)) continue;
                    valid++; sum += v; if (double.IsNaN(max) || v > max) max = v;
                }
                if (valid > 0)
                {
                    rows.Add(CpuRow("cpu/core-max", device, "Core Max", max, reading.Sample, quality, at, reading.Stale));
                    rows.Add(CpuRow("cpu/core-average", device, "Core Average", sum / valid, reading.Sample, quality, at, reading.Stale));
                    string[] names = reading.Sample.CoreNames;
                    for (int i = 0; i < reading.Sample.Cores.Length; i++) // Id cpu/core/<0-based physical core index>
                        if (!double.IsNaN(reading.Sample.Cores[i]))
                            rows.Add(CpuRow("cpu/core/" + i, device, names != null && i < names.Length ? names[i] : "Core " + i, reading.Sample.Cores[i], reading.Sample, quality, at, reading.Stale));
                }
                readout.Timings[reading.Sample.Source] = reading.Sample.AcquireMs;
            }
            double crossMs = cpu.CrossCheckAcquireMs;
            if (crossMs > 0) readout.Timings["Intel ESIF cross-check"] = crossMs;
            readout.ControlTemperatureC = reading.ControlMeanC;
            readout.ProtectionTemperatureC = reading.RawMaxC;
            storage.AddRows(rows);
            foreach (string warning in storage.Warnings()) warnings.Add(warning);
            double storageMs = storage.LastEnumerationMs;
            if (storageMs > 0) readout.Timings["Storage"] = storageMs;
            readout.DriverState = DriverState();
            return readout;
        }

        string DriverState()
        {
            double now = SensorClock.Monotonic();
            lock (stateGate)
            {
                if (cachedDriverState == null || now >= nextDriverCheck)
                {
                    try { cachedDriverState = driverState(); } catch { cachedDriverState = "Unknown"; }
                    nextDriverCheck = now + 60;
                }
                return cachedDriverState;
            }
        }

        static SensorRow CpuRow(string id, string device, string name, double value, CpuSample sample, string quality, string at, bool stale)
        {
            return new SensorRow { Id = id, Device = device, HardwareType = "Cpu", Name = name, Type = "Temperature",
                Role = SensorRoles.Classify("Temperature", name), Unit = SensorRoles.UnitFor("Temperature"),
                Value = stale ? null : (float?)Math.Round(value, 1), Source = sample.Source, Quality = quality, PolledAtUtc = at };
        }

        public void Dispose()
        {
            lock (stateGate) disposed = true;
            stop.Set();
            if (cpuThread != null) cpuThread.Join(3000);
            if (storageThread != null) storageThread.Join(3000);
            cpu.Dispose(); // if the thread is still inside a slow read, the sampler releases its handles when that step returns
        }
    }
}

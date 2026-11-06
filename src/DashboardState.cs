using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace DellFans
{
    public sealed class TrendPoint
    {
        public DateTime Time;
        public float? Rpm, Cpu;
    }

    // An event is stored as message key + arguments so a language switch re-renders it; Raw is a service warning shown through UiText.Error.
    public sealed class UiEvent
    {
        public DateTime Time;
        public string Key, Raw;
        public object[] Args;
        public bool Warn;
        public string Text { get { return Raw != null ? UiText.Error(Raw) : Args == null ? UiText.T(Key) : UiText.F(Key, Args); } }
    }

    public sealed class ProcessTotals
    {
        public double? CpuPercent, MemoryMiB;
        public int Count;
    }

    // Everything the three pages show, built from snapshots; no controls, so it is cheap to keep and to test.
    public sealed class DashboardState
    {
        public const int TrendMinutes = 10, MaxEvents = 20, MaxTrendPoints = 700;
        public bool Connected, Fresh;
        public string Model, Bios, Mode, ControlState, DiskName;
        public string SensorDriverState, SensorSource; // PawnIoState name and sensor source ("PawnIO IntelMSR", "Intel ESIF", "None")
        public int? CustomMinimum, TargetRpm;
        public ModeLimits Limits;
        public float? Rpm, FanMin, FanMax, CpuPackage, CoreMax, CoreAvg, DiskTemp, DiskPeak;
        public readonly List<TrendPoint> Trend = new List<TrendPoint>();
        public readonly List<UiEvent> Events = new List<UiEvent>();
        public ProcessTotals Totals = new ProcessTotals();
        bool everConnected;

        public void AddEvent(DateTime time, string key, bool warn, params object[] args)
        { Push(new UiEvent { Time = time, Key = key, Args = args.Length == 0 ? null : args, Warn = warn }); }
        void Push(UiEvent item)
        {
            Events.Insert(0, item);
            if (Events.Count > MaxEvents) Events.RemoveRange(MaxEvents, Events.Count - MaxEvents);
        }
        public static string StateName(string state)
        {
            switch (state)
            {
                case "FloorCurve": return UiText.T("温控运行");
                case "BiosAutomatic": return UiText.T("BIOS 自动");
                case "EmergencyFullSpeed": return UiText.T("保护全速");
                case "FaultAutomatic": return UiText.T("故障暂停");
                case "Probing": return UiText.T("正在探测");
                case "Initializing": return UiText.T("初始化中");
                case "Unavailable": return UiText.T("不可用");
                default: return string.IsNullOrEmpty(state) ? "—" : state;
            }
        }
        // Event arguments that are translated when the event is drawn, so a language switch also updates old events.
        sealed class Localized
        {
            readonly Func<string> text;
            public Localized(Func<string> text) { this.text = text; }
            public override string ToString() { return text(); }
        }
        public static object ModeToken(string mode) { return new Localized(delegate { return ModeName(mode); }); }
        public static object StateToken(string state) { return new Localized(delegate { return StateName(state); }); }
        public static string ModeName(string mode)
        { return UiText.T(mode == FanModes.Quiet ? "静音" : mode == FanModes.Balanced ? "平衡" : mode == FanModes.Performance ? "高性能" : mode == FanModes.Custom ? "自定义" : "BIOS 自动"); }
        static bool Good(float? value) { return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value); }
        public void MarkDisconnected(DateTime now)
        {
            if (Connected) AddEvent(now, "后台断连", true);
            Connected = Fresh = false;
        }
        public void Update(Snapshot sample, DateTime now, bool fresh)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            bool wasConnected = Connected;
            Connected = true; Fresh = fresh;
            if (!wasConnected) AddEvent(now, "后台已连接", false);
            everConnected = true;
            Model = sample.Model; Bios = sample.BiosVersion;
            List<SensorRow> sensors = sample.Sensors ?? new List<SensorRow>();
            SensorRow cpu = sensors.FirstOrDefault(s => s.Name == "CPU Package" && s.Type == "Temperature");
            SensorRow max = sensors.FirstOrDefault(s => s.Name == "Core Max" && s.Type == "Temperature");
            SensorRow avg = sensors.FirstOrDefault(s => s.Name == "Core Average" && s.Type == "Temperature");
            SensorRow fan = sensors.FirstOrDefault(s => s.Id == "dell/fan/F600") ?? sensors.FirstOrDefault(s => s.Type == "Fan");
            CpuPackage = Good(cpu == null ? null : cpu.Value) ? cpu.Value : (sample.CpuControlTemperatureC.HasValue ? (float?)sample.CpuControlTemperatureC.Value : null);
            CoreMax = Good(max == null ? null : max.Value) ? max.Value : null;
            CoreAvg = Good(avg == null ? null : avg.Value) ? avg.Value : null;
            Rpm = Good(fan == null ? null : fan.Value) ? fan.Value : null;
            FanMin = Good(fan == null ? null : fan.Minimum) ? fan.Minimum : null;
            FanMax = Good(fan == null ? null : fan.Maximum) ? fan.Maximum : null;
            SensorRow hottest = null;
            foreach (SensorRow row in sensors)
                if (row.HardwareType == "Storage" && SensorRoles.IsCurrentTemperature(row) && Good(row.Value) && (hottest == null || row.Value.Value > hottest.Value.Value)) hottest = row;
            DiskTemp = hottest == null ? null : hottest.Value;
            DiskName = hottest == null ? null : hottest.Device;
            DiskPeak = hottest != null && Good(hottest.Maximum) ? hottest.Maximum : DiskTemp;
            BackgroundStatus background = sample.Background;
            SensorDriverState = sample.SensorDriverState ?? (background == null ? null : background.SensorDriverState);
            SensorSource = sample.SensorSource ?? (background == null ? null : background.SensorSource);
            string newMode = background == null ? null : background.Settings != null && FanModes.IsValid(background.Settings.Mode) ? background.Settings.Mode : background.Mode;
            if (background != null && background.Settings != null) CustomMinimum = background.Settings.MinimumRpm;
            string state = background == null ? null : background.State;
            if (wasConnected && newMode != null && Mode != null && newMode != Mode) AddEvent(now, "模式切换为 {0}", false, ModeToken(newMode));
            if (wasConnected && state != null && ControlState != null && state != ControlState)
                AddEvent(now, "控制状态：{0}", state == "FaultAutomatic" || state == "EmergencyFullSpeed", StateToken(state));
            Mode = newMode; ControlState = state;
            Limits = background == null ? null : background.ModeLimits;
            if (Limits == null && background != null && background.Settings != null)
                try { Limits = background.Settings.Limits(); } catch (ArgumentException) { Limits = null; }
            TargetRpm = background == null ? null : background.EffectiveTargetRpm;
            if (sample.Warnings != null)
                foreach (string warning in sample.Warnings)
                    if (!Events.Any(e => e.Raw == warning && now - e.Time < TimeSpan.FromMinutes(5))) Push(new UiEvent { Time = now, Raw = warning, Warn = true });
            DateTime observed;
            if (DateTime.TryParse(sample.PolledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out observed)) AddTrend(observed.ToLocalTime(), Rpm, CpuPackage);
        }
        // A stalled service can replay its last snapshot; that is not a new observation.
        public void AddTrend(DateTime time, float? rpm, float? cpu)
        {
            if (Trend.Count > 0 && time <= Trend[Trend.Count - 1].Time) return;
            Trend.Add(new TrendPoint { Time = time, Rpm = rpm, Cpu = cpu });
            Trend.RemoveAll(p => p.Time < time.AddMinutes(-TrendMinutes));
            if (Trend.Count > MaxTrendPoints) Trend.RemoveRange(0, Trend.Count - MaxTrendPoints);
        }
        // The point closest to a moment, for the hover read-out.
        public TrendPoint Nearest(DateTime time)
        {
            TrendPoint best = null; double distance = double.MaxValue;
            foreach (TrendPoint p in Trend) { double d = Math.Abs((p.Time - time).TotalSeconds); if (d < distance) { distance = d; best = p; } }
            return best;
        }
        public bool HasEverConnected { get { return everConnected; } }
    }

    // CPU and memory of the DellFans programs, from read-only Process APIs. The service reports its own figures in the snapshot
    // (a normal user cannot open it for CPU times); every other DellFans process is measured here. Sampling is on demand, never in a timer of its own.
    public sealed class ProcessMeter
    {
        static readonly string[] Names = { "DellFans", "DellFans.Cli", "DellFans.Tray", "DellFans.Watchdog" };
        readonly Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastTicks = -1;
        public ProcessTotals Sample(ResourceStatus service)
        {
            double cpu = 0, memory = 0; int count = 0; bool cpuKnown = false;
            long now = clock.ElapsedTicks; double seconds = lastTicks < 0 ? 0 : (now - lastTicks) / (double)Stopwatch.Frequency; lastTicks = now;
            Dictionary<int, TimeSpan> seen = new Dictionary<int, TimeSpan>();
            foreach (string name in Names)
                foreach (Process process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            memory += process.PrivateMemorySize64 / 1048576.0; count++;
                            TimeSpan used = process.TotalProcessorTime; seen[process.Id] = used;
                            TimeSpan before;
                            if (seconds > 0.2 && lastCpu.TryGetValue(process.Id, out before))
                            { cpu += (used - before).TotalSeconds / seconds / Environment.ProcessorCount * 100; cpuKnown = true; }
                        }
                        catch (Exception) { } // exited meanwhile, or a protected process: leave it out
                    }
            lastCpu.Clear(); foreach (KeyValuePair<int, TimeSpan> pair in seen) lastCpu[pair.Key] = pair.Value;
            if (service != null) { memory += service.PrivateMemoryMB; cpu += service.CpuPercent; cpuKnown = true; count++; }
            return new ProcessTotals { CpuPercent = cpuKnown ? (double?)Math.Max(0, cpu) : null, MemoryMiB = count == 0 ? null : (double?)memory, Count = count };
        }
    }
}

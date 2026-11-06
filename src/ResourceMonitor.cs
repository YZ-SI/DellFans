using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DellFans
{
    public sealed class ResourceStatus
    {
        public string AtUtc { get; set; }
        public double UptimeSeconds { get; set; }
        public int LogicalProcessors { get; set; }
        public double CpuPercent { get; set; }
        public double AverageCpu60Seconds { get; set; }
        public double AverageCpuSession { get; set; }
        public double PeakCpuPercent { get; set; }
        public double WorkingSetMB { get; set; }
        public double PrivateMemoryMB { get; set; }
        public double PeakWorkingSetMB { get; set; }
        public double PeakPrivateMemoryMB { get; set; }
        public int Handles { get; set; }
        public int Threads { get; set; }
        public double? SharedWmiCpuPercent { get; set; }
        public bool IncludesWatchdog { get; set; }
        public bool LowOverheadMode { get; set; }
        public bool RestoreAutomaticRequested { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
    }

    // Uses monotonic elapsed time. A short spike never triggers a mode change.
    public sealed class ResourceBudget
    {
        double previous, softSeconds, hardSeconds, recoverySeconds;
        public bool LowOverhead { get; private set; }
        public bool RestoreAutomatic { get; private set; }
        public void Observe(double recentCpuPercent, double privateMB, double elapsed)
        {
            RestoreAutomatic = false;
            double gap = elapsed - previous; previous = elapsed;
            if (gap <= 0 || gap > 10 || elapsed < 20 || double.IsNaN(recentCpuPercent) || double.IsInfinity(recentCpuPercent))
            { softSeconds = hardSeconds = recoverySeconds = 0; return; }
            bool soft = recentCpuPercent >= 1 || privateMB >= 256;
            bool hard = recentCpuPercent >= 3 || privateMB >= 512;
            softSeconds = soft ? softSeconds + gap : 0;
            hardSeconds = hard ? hardSeconds + gap : 0;
            if (softSeconds >= 30) LowOverhead = true;
            RestoreAutomatic = hardSeconds >= 30;
            recoverySeconds = recentCpuPercent < 0.5 && privateMB < 220 ? recoverySeconds + gap : 0;
            if (recoverySeconds >= 60) LowOverhead = false;
        }
    }

    public static class FeedbackSchedule
    {
        public static int DelayMilliseconds(int target)
        { return target == RpmRange.Minimum || target == RpmRange.Medium || target == RpmRange.Maximum ? 750 : 120; }
    }

    public static class BoundedResourceLog
    {
        public const int MaximumBytes = 512 * 1024;
        public static void Append(string path, string json)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json + Environment.NewLine);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Resource log record exceeds its size budget.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumBytes)
            { File.Copy(path, path + ".previous", true); File.WriteAllBytes(path, new byte[0]); }
            using (FileStream file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) file.Write(bytes, 0, bytes.Length);
        }
    }

    public sealed class ProcessResourceMonitor : IDisposable
    {
        sealed class CpuPoint { public double At, Duration, CpuSeconds; }
        readonly Process self = Process.GetCurrentProcess();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Queue<CpuPoint> history = new Queue<CpuPoint>();
        readonly ResourceBudget budget = new ResourceBudget();
        readonly int processors = Environment.ProcessorCount;
        Process helper;
        int helperId;
        double lastAt, lastSelfCpu, lastHelperCpu, peak, lastLog;
        double sessionCpu, peakWorkingSet, peakPrivate;
        string logError;
        Dictionary<int, double> wmiPrevious = new Dictionary<int, double>();
        double wmiAt;
        double? wmiPercent;
        public ProcessResourceMonitor() { lastSelfCpu = self.TotalProcessorTime.TotalSeconds; }
        public ResourceStatus Read(int watchdogId)
        {
            double now = clock.Elapsed.TotalSeconds;
            ResourceStatus result = new ResourceStatus { AtUtc = DateTime.UtcNow.ToString("o"), UptimeSeconds = now, LogicalProcessors = processors };
            try
            {
                self.Refresh();
                double selfCpu = self.TotalProcessorTime.TotalSeconds;
                double delta = Math.Max(0, selfCpu - lastSelfCpu); lastSelfCpu = selfCpu;
                long working = self.WorkingSet64, memory = self.PrivateMemorySize64;
                int handles = self.HandleCount, threads = self.Threads.Count;
                if (watchdogId != helperId)
                {
                    if (helper != null) helper.Dispose(); helper = null; helperId = watchdogId;
                    if (watchdogId > 0)
                    {
                        try { helper = Process.GetProcessById(watchdogId); lastHelperCpu = helper.TotalProcessorTime.TotalSeconds; }
                        catch (ArgumentException) { helper = null; }
                    }
                }
                if (helper != null)
                {
                    helper.Refresh();
                    if (!helper.HasExited)
                    {
                        double cpu = helper.TotalProcessorTime.TotalSeconds;
                        delta += Math.Max(0, cpu - lastHelperCpu); lastHelperCpu = cpu;
                        working += helper.WorkingSet64; memory += helper.PrivateMemorySize64;
                        handles += helper.HandleCount; threads += helper.Threads.Count; result.IncludesWatchdog = true;
                    }
                }
                double duration = now - lastAt;
                result.CpuPercent = duration > 0 ? Math.Min(100, delta / duration / processors * 100) : 0;
                lastAt = now;
                history.Enqueue(new CpuPoint { At = now, Duration = duration, CpuSeconds = delta });
                while (history.Count > 120 || (history.Count > 1 && now - history.Peek().At > 60)) history.Dequeue();
                result.AverageCpu60Seconds = Average(history);
                sessionCpu += delta; result.AverageCpuSession = now > 0 ? sessionCpu / now / processors * 100 : 0;
                peak = Math.Max(peak, result.CpuPercent); result.PeakCpuPercent = peak;
                result.WorkingSetMB = working / 1048576.0; result.PrivateMemoryMB = memory / 1048576.0;
                peakWorkingSet = Math.Max(peakWorkingSet, result.WorkingSetMB); peakPrivate = Math.Max(peakPrivate, result.PrivateMemoryMB);
                result.PeakWorkingSetMB = peakWorkingSet; result.PeakPrivateMemoryMB = peakPrivate;
                result.Handles = handles; result.Threads = threads;
                if (now - wmiAt >= 10 || wmiAt == 0)
                {
                    Dictionary<int, double> current = new Dictionary<int, double>(); double seconds = 0;
                    foreach (Process provider in Process.GetProcessesByName("WmiPrvSE"))
                    using (provider)
                    {
                        try
                        {
                            double cpu = provider.TotalProcessorTime.TotalSeconds, before;
                            current[provider.Id] = cpu;
                            if (wmiPrevious.TryGetValue(provider.Id, out before)) seconds += Math.Max(0, cpu - before);
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                    if (wmiPrevious.Count > 0 && now > wmiAt) wmiPercent = seconds / (now - wmiAt) / processors * 100;
                    wmiPrevious = current; wmiAt = now;
                }
                result.SharedWmiCpuPercent = wmiPercent;
                budget.Observe(Average(history.Where(p => now - p.At <= 10)), result.PrivateMemoryMB, now);
                result.LowOverheadMode = budget.LowOverhead; result.RestoreAutomaticRequested = budget.RestoreAutomatic;
                result.State = now < 20 ? "WarmingUp" : budget.RestoreAutomatic ? "RestoreAutomatic" : budget.LowOverhead ? "LowOverhead" : "Normal";
            }
            catch (Exception e) { result.Error = e.Message; result.State = "Unavailable"; }
            if (now - lastLog >= 10)
            {
                lastLog = now; // Failed diagnostics are throttled too.
                try { BoundedResourceLog.Append(Path.Combine(ReportFiles.ReportsDirectory, "resource-usage.jsonl"), ReportFiles.Json(result)); logError = null; }
                catch (Exception e) { logError = "Resource log: " + e.Message; }
            }
            if (logError != null) result.Error = result.Error == null ? logError : result.Error + " | " + logError;
            return result;
        }
        static double Average(IEnumerable<CpuPoint> items)
        {
            double seconds = 0, duration = 0;
            foreach (CpuPoint item in items) { seconds += item.CpuSeconds; duration += item.Duration; }
            return duration > 0 ? seconds / duration / Environment.ProcessorCount * 100 : 0;
        }
        public void Dispose() { self.Dispose(); if (helper != null) helper.Dispose(); }
    }
}

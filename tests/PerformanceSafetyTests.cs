using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DellFans;

internal static class PerformanceSafetyTests
{
    static int passed, failed;
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    static Snapshot Sample()
    {
        return new Snapshot {
            Warnings = new System.Collections.Generic.List<string> { "Existing control failure" },
            ManualControl = new ManualFanStatus { Active = false, Error = "Existing control failure", State = "AutomaticAfterError" },
            Background = new BackgroundStatus { State = "FaultAutomatic", Settings = FloorSettings.Defaults() }
        };
    }
    static void Test(string label, Action run)
    { try { run(); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + label + ": " + error.GetBaseException().Message); } }
    static void SnapshotFailure(string folder)
    {
        string path = Path.Combine(folder, "snapshot.json");
        SnapshotDiagnostics writer = new SnapshotDiagnostics(path);
        Snapshot first = Sample();
        using (FileStream locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            writer.Write(first, 0, false);
        Check(first.Warnings.Any(w => w.StartsWith("Snapshot report: ")), "Snapshot IO failure becomes a visible warning");
        Check(first.Warnings.Contains("Existing control failure") && first.ManualControl.Error == "Existing control failure" && first.Background.State == "FaultAutomatic",
            "Snapshot failure preserves the existing control error and state");
        Snapshot waiting = Sample(); writer.Write(waiting, 1, true);
        Check(new FileInfo(path).Length == 0 && waiting.Warnings.Any(w => w.StartsWith("Snapshot report: ")),
            "A forced snapshot cannot bypass the retry interval after a write failure");
        Snapshot early = Sample(); writer.Write(early, 59, false);
        Check(new FileInfo(path).Length == 0, "Routine snapshot writes wait sixty seconds");
        Snapshot recovered = Sample(); writer.Write(recovered, 60, false);
        Check(File.ReadAllText(path).Contains("Existing control failure") && !recovered.Warnings.Any(w => w.StartsWith("Snapshot report: ")),
            "Snapshot writing recovers at the retry boundary without hiding a control error");
        Snapshot quiet = Sample(); quiet.PolledAtUtc = "routine-report"; writer.Write(quiet, 90, false);
        Check(!File.ReadAllText(path).Contains("routine-report"), "A routine snapshot inside sixty seconds is not written");
        Snapshot forced = Sample(); forced.PolledAtUtc = "forced-report"; writer.Write(forced, 91, true);
        Check(File.ReadAllText(path).Contains("forced-report"), "A healthy forced snapshot still writes immediately");
        ServiceRequest legacy = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ServiceRequest>("{\"Command\":\"Status\"}");
        Snapshot rows = Sample(); rows.Sensors = new System.Collections.Generic.List<SensorRow> {
            new SensorRow { Type = "Fan", Name = "System / CPU Fan" }, new SensorRow { Type = "Load", HardwareType = "Cpu", Name = "CPU Total" } };
        Check(legacy.Detail == null && ThermalServiceRuntime.Reply(rows, legacy.Detail).Sensors.Count == 2,
            "A Status request without Detail still receives every sensor row");
        Snapshot brief = ThermalServiceRuntime.Reply(rows, false);
        Check(brief.SummaryOnly && brief.Sensors.Count == 1 && ThermalServiceRuntime.Reply(rows, true).Sensors.Count == 2 && ThermalServiceRuntime.Reply(null, false) == null,
            "Detail=false replies with summary rows; Detail=true and a missing snapshot are unchanged");
        Snapshot noControl = Sample(); noControl.CpuControlTemperatureC = null;
        noControl.Sensors = new System.Collections.Generic.List<SensorRow> { new SensorRow { Type = "Temperature", HardwareType = "Cpu", Name = "Core Max", Role = "DerivedTemperature", Value = 70 } };
        Check(ThermalServiceRuntime.Cpu(noControl) == null, "Control temperature comes only from the sensor layer: a row maximum is never substituted");
        noControl.CpuControlTemperatureC = 66;
        Check(ThermalServiceRuntime.Cpu(noControl) == 66 && (noControl.CpuControlTemperatureC = 130) > 0 && ThermalServiceRuntime.Cpu(noControl) == null, "Control value used when plausible, rejected above 125 C");
        string directoryTarget = Path.Combine(folder, "directory-target.json"); Directory.CreateDirectory(directoryTarget);
        Snapshot denied = Sample(); new SnapshotDiagnostics(directoryTarget).Write(denied, 0, false);
        Check(denied.Warnings.Any(w => w.StartsWith("Snapshot report: ")), "An inaccessible snapshot destination is also nonfatal");
    }
    static void ResourceFailure()
    {
        string folder = ReportFiles.ReportsDirectory;
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(folder).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Compile and run this offline test under a temporary directory; workspace and installed logs are protected.");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "resource-usage.jsonl");
        using (ProcessResourceMonitor monitor = new ProcessResourceMonitor())
        {
            using (FileStream locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Thread.Sleep(10100);
                ResourceStatus failedLog = monitor.Read(0);
                Check(failedLog.Error != null && failedLog.Error.Contains("Resource log: "), "Resource log IO failure is reported");
            }
            ResourceStatus waiting = monitor.Read(0);
            Check(new FileInfo(path).Length == 0 && waiting.Error != null && waiting.Error.Contains("Resource log: "),
                "Resource log failure is retained and not retried on the next poll");
            Thread.Sleep(10100);
            ResourceStatus recovered = monitor.Read(0);
            Check(new FileInfo(path).Length > 0 && recovered.Error == null, "Resource log writing recovers after ten seconds");
        }
    }
    static int Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dell-performance-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { Test("Snapshot diagnostics", delegate { SnapshotFailure(folder); }); Test("Resource log", ResourceFailure); }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed; offline files/process counters only");
        return failed == 0 ? 0 : 1;
    }
}

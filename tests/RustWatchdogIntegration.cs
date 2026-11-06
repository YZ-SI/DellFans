using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using DellFans;

// Runs the real C# watchdog starter (ManualRpmController.ProcessWatchdogStarter) against the Rust DellFans.Watchdog.exe that sits next to
// this exe. DellFans.Cli.exe next to it is a stub (dellfans-clistub): it logs the call and answers with the code in clistub.exe's folder file
// clistub.exit. The parent is `ping -t`; no firmware, service or real DellFans.Cli.exe is involved.
internal static class RustWatchdogIntegration
{
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Log = Path.Combine(Dir, "clistub.log");
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    static int checks;
    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL " + message); checks++; Console.WriteLine("ok   " + message); }
    static string[] Calls() { return File.Exists(Log) ? File.ReadAllLines(Log) : new string[0]; }
    static long Millis(string line) { return long.Parse(line.Split('\t')[0]); }
    static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    static void Touch(string path) { File.WriteAllText(path, "alive"); }

    static int Main()
    {
        Process ping = null, watchdog = null;
        string session = Path.Combine(Dir, "fan-watchdog-" + Guid.NewGuid().ToString("N") + ".json");
        try { return Run(session, ref ping, ref watchdog); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally
        {
            if (watchdog != null) { try { if (!watchdog.HasExited) watchdog.Kill(); } catch (Exception) { } }
            if (ping != null) { try { if (!ping.HasExited) ping.Kill(); } catch (Exception) { } }
            foreach (Process stray in Process.GetProcessesByName("DellFans.Watchdog"))
            {
                try { if (string.Equals(Path.GetDirectoryName(stray.MainModule.FileName), Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) stray.Kill(); }
                catch (Exception) { }
            }
        }
    }

    static int Run(string session, ref Process ping, ref Process watchdog)
    {
        File.Delete(Log);
        File.WriteAllText(Path.Combine(Dir, "clistub.exit"), "3"); // first recovery answer: the session is alive again
        ping = Process.Start(new ProcessStartInfo("ping", "-t 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        FanWatchdogRecord record = new FanWatchdogRecord { ParentId = ping.Id, ParentStartTicks = ping.StartTime.ToUniversalTime().Ticks,
            Model = "TestModel", Bios = "0.0", RestoreOverride = "Enabled", ServiceOwned = true };
        File.WriteAllText(session, new JavaScriptSerializer().Serialize(record));

        // The command line the service would use must pick the Rust watchdog (it exists next to us).
        string file, arguments;
        FanWatchdog.WatchdogCommand(Dir, session, out file, out arguments);
        Require(Path.GetFileName(file) == "DellFans.Watchdog.exe" && arguments == "\"" + session + "\"", "service chooses DellFans.Watchdog.exe with the quoted record");

        // The real starter: spawns the watchdog and waits (<= 5 s) for <record>.ready.
        Type starterType = typeof(ManualRpmController).GetNestedType("ProcessWatchdogStarter", BindingFlags.NonPublic);
        IFanWatchdogStarter starter = (IFanWatchdogStarter)Activator.CreateInstance(starterType);
        Stopwatch ready = Stopwatch.StartNew();
        int watchdogId = starter.Start(session);
        Require(File.Exists(session + ".ready") && ready.Elapsed < TimeSpan.FromSeconds(5), string.Format(".ready appeared after {0:N2} s", ready.Elapsed.TotalSeconds));
        watchdog = Process.GetProcessById(watchdogId);
        IntPtr handle = OpenProcess(0x100000 | 0x1000, false, watchdogId); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION: ExitCode of a process we did not start
        Require(watchdog.ProcessName == "DellFans.Watchdog", "starter returned the watchdog process id");
        Touch(session + ".heartbeat");
        File.WriteAllText(session + ".armed", "armed");

        // Healthy for 3 s: heartbeat fresh, nothing happens.
        Stopwatch healthy = Stopwatch.StartNew();
        long lastTouch = 0;
        while (healthy.Elapsed < TimeSpan.FromSeconds(3)) { Touch(session + ".heartbeat"); lastTouch = Now(); Thread.Sleep(500); }
        Require(Calls().Length == 0 && !watchdog.HasExited, "no recovery while the heartbeat is fresh");

        // Heartbeat stops (parent alive): recovery 8-10 s later; the stub answers 3 = alive again.
        Stopwatch wait = Stopwatch.StartNew();
        while (Calls().Length == 0 && wait.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        string[] calls = Calls();
        Require(calls.Length >= 1, "heartbeat stop triggered DellFans.Cli.exe --recover");
        double lag = (Millis(calls[0]) - lastTouch) / 1000.0;
        Require(lag >= 7.9 && lag <= 10.5, string.Format("recovery started {0:N1} s after the last heartbeat (8-10 s)", lag));
        string[] fields = calls[0].Split('\t');
        Require(fields.Length == 3 && fields[1] == "--recover" && fields[2] == session, "CLI called as --recover <record>");
        File.WriteAllText(Path.Combine(Dir, "clistub.exit"), "0"); // the next recovery really completes

        // Exit 3 (the stub refreshed the heartbeat): the watchdog resumes monitoring instead of retrying or exiting.
        Thread.Sleep(4000);
        Require(!watchdog.HasExited && Calls().Length == 1, "exit code 3: still monitoring, no immediate retry");

        // Heartbeat stops again: second recovery ~8 s after the stub's refresh; exit 0 ends the watchdog.
        wait.Restart();
        while (Calls().Length < 2 && wait.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
        calls = Calls();
        Require(calls.Length == 2, "monitoring resumed: second recovery after the heartbeat went stale again");
        double gap = (Millis(calls[1]) - Millis(calls[0])) / 1000.0;
        Require(gap >= 7.5 && gap <= 10.5, string.Format("second recovery {0:N1} s after the first", gap));
        uint exitCode;
        Require(WaitForSingleObject(handle, 5000) == 0 && GetExitCodeProcess(handle, out exitCode) && exitCode == 0, "exit code 0: watchdog stops with 0");
        Console.WriteLine("PASS rust watchdog integration: " + checks + " checks");
        return 0;
    }
}

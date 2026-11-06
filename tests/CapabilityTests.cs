using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DellFans;

// Offline. Every hardware call goes to a fake; files go to temp folders. Nothing here touches ProgramData, the service or the fan.
internal static class CapabilityTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static void Throws(Action action, string message)
    {
        try { action(); } catch (Exception) { passed++; return; }
        throw new Exception(message);
    }

    sealed class Fake : ICapabilityHardware
    {
        public string Model { get; set; }
        public string BiosVersion { get; set; }
        public string WindowsBuild { get; set; }
        public string CpuName { get; set; }
        public List<string> Calls = new List<string>();
        public Action OnWmi = delegate { }, OnStorage = delegate { };
        public Func<string> OnMode = delegate { return "Disabled"; };
        public Func<string> OnCpu = delegate { return "PawnIO IntelMSR"; };
        public Queue<int> Rpms = new Queue<int>(new[] { 2000, 2000, 2600 });
        public Action OnFull = delegate { };
        public int ModeAfterRestoreCalls;
        public int Storage = 2;
        public Fake() { Model = "OptiPlex Micro 9999"; BiosVersion = "9.9.9"; WindowsBuild = "10.0.19045.0"; CpuName = "13th Gen Intel(R) Core(TM) i5-13400"; }
        public void CheckDellWmi() { Calls.Add("wmi"); OnWmi(); }
        public int ReadSmmRpm() { Calls.Add("rpm"); return Rpms.Count > 1 ? Rpms.Dequeue() : Rpms.Peek(); }
        public string ReadBiosMode() { Calls.Add("mode"); return OnMode(); }
        public string CpuTemperatureSource() { Calls.Add("cpu"); return OnCpu(); }
        public int StorageTemperatureCount() { Calls.Add("storage"); return Storage; }
        public IDisposable ArmWatchdog() { Calls.Add("arm"); return new Disarm(this); }
        sealed class Disarm : IDisposable { readonly Fake f; public Disarm(Fake f) { this.f = f; } public void Dispose() { f.Calls.Add("disarm"); } }
        public void SetFullSpeed() { Calls.Add("full"); OnFull(); }
        public void RestoreAutomatic() { Calls.Add("restore"); }
        public void Wait(int milliseconds) { Calls.Add("wait" + milliseconds); }
    }

    static string Temp() { string path = Path.Combine(Path.GetTempPath(), "DellFans-CapabilityTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static readonly DateTime When = new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc);
    static CapabilityProbe Probe(Fake hardware, CapabilityStore store) { return new CapabilityProbe(hardware, store, () => When); }

    static void ReadFailureWritesAnAnonymousReport()
    {
        string root = Temp();
        try
        {
            CapabilityStore store = new CapabilityStore(root);
            Fake hardware = new Fake();
            hardware.Rpms = new Queue<int>();
            string secret = "Service Tag: 7ABC123 on " + Environment.MachineName + " for " + Environment.UserName;
            hardware.OnWmi = delegate { throw new InvalidOperationException("WMI query failed. " + secret); };
            CapabilityStatus status = Probe(hardware, store).Test(true);
            Check(status.State == "Failed" && status.ReportPath != null && File.Exists(status.ReportPath), "a failed read check must write a report");
            Check(status.ReportPath.StartsWith(Path.Combine(root, "reports") + Path.DirectorySeparatorChar) && Path.GetFileName(status.ReportPath).StartsWith("capability-report-") && status.ReportPath.EndsWith(".json"), "report path pattern: " + status.ReportPath);
            string report = File.ReadAllText(status.ReportPath);
            Check(report.Contains("OptiPlex Micro 9999") && report.Contains("9.9.9") && report.Contains("10.0.19045.0") && report.Contains("i5-13400"), "report must carry model, BIOS, Windows build and CPU");
            Check(report.Contains("Dell WMI classes and tokens") && report.Contains("\"Result\":\"Fail\"") && report.Contains("WMI query failed"), "report must carry each check name, result and error");
            Check(report.Contains("\"SensorSource\":\"PawnIO IntelMSR\""), "report must carry the sensor source");
            Check(!report.Contains(Environment.MachineName) && !report.Contains(Environment.UserName) && !report.ToLowerInvariant().Contains("7abc123"), "report leaked the machine name, user name or service tag: " + report);
            Check(!File.Exists(store.RecordPath), "a failure must not persist a pass");
            Check(!hardware.Calls.Contains("full") && !hardware.Calls.Contains("arm"), "a failed read check must never reach the write step");
            // a fake with the real machine name inside every field is scrubbed too
            Fake named = new Fake { Model = "OptiPlex Micro 9999", CpuName = "CPU of " + Environment.MachineName };
            named.OnCpu = delegate { throw new InvalidOperationException("sensor failed for " + Environment.UserName); };
            CapabilityStatus second = Probe(named, new CapabilityStore(root)).ReadChecks();
            Check(second.State == "Failed" && !File.ReadAllText(second.ReportPath).Contains(Environment.MachineName), "names in the CPU/Model fields are scrubbed");
        }
        finally { Directory.Delete(root, true); }
    }

    static void ReadPassWithoutConsentChangesNothing()
    {
        string root = Temp();
        try
        {
            CapabilityStore store = new CapabilityStore(root); Fake hardware = new Fake();
            CapabilityStatus status = Probe(hardware, store).ReadChecks();
            Check(status.State == "ReadPassed" && status.WriteCheckRequired && !File.Exists(store.RecordPath), "read checks alone do not enable control");
            Check(!hardware.Calls.Any(c => c == "full" || c == "arm" || c == "restore" || c.StartsWith("wait")), "read checks never write");
            Throws(delegate { Probe(hardware, store).Test(false); }, "the full-speed test ran without consent");
            Check(!hardware.Calls.Contains("full") && !store.Allows("OptiPlex Micro 9999", "9.9.9"), "no consent, no write, no pass");
            Fake storageless = new Fake { Storage = 0 };
            Check(Probe(storageless, store).ReadChecks().State == "ReadPassed", "missing storage temperature is informational");
        }
        finally { Directory.Delete(root, true); }
    }

    static void ConsentedTestPassesAndPersists()
    {
        string root = Temp();
        try
        {
            CapabilityStore store = new CapabilityStore(root); Fake hardware = new Fake();
            CapabilityStatus status = Probe(hardware, store).Test(true);
            Check(status.State == "Passed" && status.ReportPath == null, "a responding fan passes");
            string[] write = hardware.Calls.SkipWhile(c => c != "arm").ToArray();
            Check(string.Join(",", write) == "arm,rpm,full,wait15000,rpm,restore,mode,disarm,cpu", "write step order (arm, baseline, full 15 s, read, restore, readback, disarm): " + string.Join(",", write));
            string saved = File.ReadAllText(store.RecordPath);
            Check(saved.Contains("\"Model\":\"OptiPlex Micro 9999\"") && saved.Contains("\"BiosVersion\":\"9.9.9\"") && saved.Contains("\"PassedAtUtc\":\"2026-11-06T08:30:00.0000000Z\"") && saved.Contains("\"Checks\":["), "capability.json content: " + saved);
            Check(store.Allows("OptiPlex Micro 9999", "9.9.9"), "the pass is found for the same model and BIOS");
            Check(!store.Allows("OptiPlex Micro 9999", "9.9.10") && !store.Allows("OptiPlex Micro 7777", "9.9.9") && !store.Allows(null, null), "a BIOS or model change needs a new probe");
            Fake again = new Fake { BiosVersion = "9.9.10" };
            Check(Probe(again, store).Current().State == "Unknown", "after a BIOS change the stored pass no longer applies");
            Check(Probe(new Fake(), store).ReadChecks().State == "Passed", "a stored pass answers without new reads");
            File.WriteAllText(store.RecordPath, "{not json"); Check(!store.Allows("OptiPlex Micro 9999", "9.9.9"), "an unreadable record is no pass");
        }
        finally { Directory.Delete(root, true); }
    }

    static void WriteStepFailuresRestoreAndReport()
    {
        string root = Temp();
        try
        {
            CapabilityStore store = new CapabilityStore(root);
            Fake slow = new Fake(); slow.Rpms = new Queue<int>(new[] { 2000, 2000, 2250 });
            CapabilityStatus status = Probe(slow, store).Test(true);
            Check(status.State == "Failed" && status.ReportPath != null && !File.Exists(store.RecordPath), "a 250 RPM rise (< 300) fails");
            Check(slow.Calls.Contains("restore"), "BIOS automatic is restored after a failed test");
            Check(status.Checks.Any(c => c.Name == "Full-speed response test" && c.Result == "Fail" && c.Error.Contains("250")), "the failure names the rise");
            Fake edge = new Fake(); edge.Rpms = new Queue<int>(new[] { 2000, 2000, 2300 });
            Check(Probe(edge, store).Test(true).State == "Passed", "exactly +300 RPM passes");
            Fake throwing = new Fake(); throwing.OnFull = delegate { throw new InvalidOperationException("SetAttribute status 3"); };
            status = Probe(throwing, new CapabilityStore(Temp())).Test(true);
            Check(status.State == "Failed" && throwing.Calls.Contains("restore") && throwing.Calls.Contains("disarm"), "a failing full-speed write still restores and disarms");
            Fake stuck = new Fake(); stuck.OnMode = delegate { return stuck.Calls.Contains("restore") ? "Enabled" : "Disabled"; };
            status = Probe(stuck, new CapabilityStore(Temp())).Test(true);
            Check(status.State == "Failed" && status.Checks.Any(c => c.Name == "BIOS automatic restore" && c.Result == "Fail" && c.Error.Contains("Enabled")), "BIOS automatic must be confirmed by readback");
            Fake noRestore = new Fake(); int modeReads = 0; noRestore.OnMode = delegate { modeReads++; if (noRestore.Calls.Contains("restore")) throw new InvalidOperationException("WMI busy"); return "Disabled"; };
            Check(Probe(noRestore, new CapabilityStore(Temp())).Test(true).State == "Failed", "an unreadable restore readback fails");
        }
        finally { Directory.Delete(root, true); }
    }

    static void EveryModelNeedsTheWriteStep()
    {
        string root = Temp();
        try
        {
            CapabilityStore store = new CapabilityStore(root);
            Fake dell = new Fake { Model = "OptiPlex Micro 1000", BiosVersion = "1.0.0" };
            CapabilityStatus status = Probe(dell, store).ReadChecks();
            Check(status.State == "ReadPassed" && status.WriteCheckRequired && !store.Allows("OptiPlex Micro 1000", "1.0.0"), "clean reads alone never pass any model");
            Check(!dell.Calls.Any(c => c == "full" || c == "arm" || c == "restore" || c.StartsWith("wait")), "the read checks never write");
            // A record without a passed full-speed test is not a pass.
            File.WriteAllText(store.RecordPath, "{\"Model\":\"OptiPlex Micro 1000\",\"BiosVersion\":\"1.0.0\",\"PassedAtUtc\":\"2026-01-01T00:00:00Z\",\"Checks\":[{\"Name\":\"Full-speed response test\",\"Result\":\"Skipped\"}]}");
            Check(!store.Allows("OptiPlex Micro 1000", "1.0.0") && Probe(dell, store).ReadChecks().State == "ReadPassed", "a record without the write pass is ignored");
            CapabilityStatus passed = Probe(new Fake { Model = "OptiPlex Micro 1000", BiosVersion = "1.0.0" }, store).Test(true);
            Check(passed.State == "Passed" && store.Allows("OptiPlex Micro 1000", "1.0.0"), "the consented test passes and persists");
            Fake otherBios = new Fake { Model = "OptiPlex Micro 1000", BiosVersion = "1.1.0" };
            Check(Probe(otherBios, store).ReadChecks().State == "ReadPassed", "another BIOS is tested again");
            string root2 = Temp();
            try
            {
                Fake broken = new Fake { Model = "OptiPlex Micro 1000", BiosVersion = "1.0.0" }; broken.OnCpu = delegate { throw new InvalidOperationException("no sensor"); };
                CapabilityStatus failed = Probe(broken, new CapabilityStore(root2)).ReadChecks();
                Check(failed.State == "Failed" && failed.ReportPath != null, "a failed read writes a report");
            }
            finally { Directory.Delete(root2, true); }
            Fake notDell = new Fake(); notDell.OnWmi = delegate { throw new NotSupportedException("This tool supports Dell systems only."); };
            CapabilityStatus unsupported = Probe(notDell, new CapabilityStore(Temp())).ReadChecks();
            Check(unsupported.State == "Unsupported" && unsupported.ReportPath != null, "a non-Dell system is Unsupported with a report");
        }
        finally { Directory.Delete(root, true); }
    }

    static void IpcAndCli()
    {
        ServiceRequest probe = new ServiceRequest { Command = "RunCapabilityProbe", Consent = true };
        ControlPolicy.Validate(probe); passed++;
        Check(ControlPolicy.Required("RunCapabilityProbe") == CallerLevel.Interactive && ControlPolicy.Required("Capability") == CallerLevel.Interactive, "interactive users may query and run the probe");
        Throws(delegate { ControlPolicy.Validate(new ServiceRequest { Command = "RunCapabilityProbe" }); }, "RunCapabilityProbe without Consent accepted");
        Throws(delegate { ControlPolicy.Validate(new ServiceRequest { Command = "RunCapabilityProbe", Consent = false }); }, "RunCapabilityProbe with Consent=false accepted");
        Throws(delegate { ControlPolicy.Validate(new ServiceRequest { Command = "RunCapabilityProbe", Consent = true, MinimumRpm = 2000 }); }, "RunCapabilityProbe with extra fields accepted");
        Throws(delegate { ControlPolicy.Validate(new ServiceRequest { Command = "Status", Consent = true }); }, "Consent accepted on Status");
        ControlPolicy.Validate(new ServiceRequest { Command = "Capability" }); passed++;
        Throws(delegate { ControlPolicy.Validate(new ServiceRequest { Command = "Capability", Detail = true }); }, "Capability takes no fields");
        // snapshot add-only field survives the wire
        Snapshot snapshot = new Snapshot { Background = new BackgroundStatus { State = "CapabilityRequired", Capability = new CapabilityStatus { State = "ReadPassed", Model = "M", BiosVersion = "B", WriteCheckRequired = true,
            Checks = new List<CapabilityCheck> { new CapabilityCheck { Name = "SMM fan RPM read", Result = "Pass", Detail = "2000 RPM" } } } } };
        Snapshot back = MiniJson.Deserialize<Snapshot>(MiniJson.Serialize(snapshot));
        Check(back.Background.Capability.State == "ReadPassed" && back.Background.Capability.Checks[0].Name == "SMM fan RPM read" && back.Background.Capability.WriteCheckRequired, "Background.Capability round trips");
        // CLI
        List<ServiceRequest> sent = new List<ServiceRequest>();
        Func<ServiceRequest, Snapshot> send = delegate(ServiceRequest request)
        { sent.Add(request); return request.Command == "RunCapabilityProbe" ? Result("Passed") : Result("ReadPassed"); };
        StringWriter output = new StringWriter();
        Check(ClientCommands.RunCapability(false, send, new StringReader(""), output) == 0 && sent.Count == 1 && sent[0].Command == "Capability" && output.ToString().Contains("ReadPassed"), "--capability only asks");
        sent.Clear(); output = new StringWriter();
        Check(ClientCommands.RunCapability(true, send, new StringReader("n\n"), output) == 0 && sent.Count == 1 && output.ToString().Contains("[y/N]") && output.ToString().Contains("Not run"), "--capability-probe answered n sends no consent");
        sent.Clear(); output = new StringWriter();
        Check(ClientCommands.RunCapability(true, send, new StringReader(""), output) == 0 && sent.Count == 1, "end of input is not a yes");
        sent.Clear(); output = new StringWriter();
        Check(ClientCommands.RunCapability(true, send, new StringReader("y\n"), output) == 0 && sent.Count == 2 && sent[1].Command == "RunCapabilityProbe" && sent[1].Consent == true && output.ToString().Contains("Passed"), "--capability-probe answered y sends Consent=true");
        sent.Clear(); output = new StringWriter();
        Func<ServiceRequest, Snapshot> failing = delegate(ServiceRequest request) { sent.Add(request); return Result("Failed"); };
        Check(ClientCommands.RunCapability(true, failing, new StringReader("y\n"), output) == 1 && sent.Count == 1 && output.ToString().Contains("https://github.com/YZ-SI/DellFans/issues/new") && output.ToString().Contains("report.json"), "a failed state is not offered the test and points at the issue page");
        Check(ClientCommands.FormatCapability(null).Contains("unknown"), "no capability field is reported, not crashed on");
        // argument parsing: one action only, and both are accepted
        Throws(delegate { ClientCommands.Run(new[] { "--capability", "--snapshot" }); }, "two actions accepted");
        Throws(delegate { ClientCommands.Run(new[] { "--capability", "--rpm", "2000" }); }, "capability combined with a mode accepted");
    }

    static Snapshot Result(string state)
    {
        return new Snapshot { Background = new BackgroundStatus { Capability = new CapabilityStatus { State = state, Model = "M", BiosVersion = "B", ReportPath = state == "Failed" ? @"C:\x\report.json" : null,
            Checks = new List<CapabilityCheck> { new CapabilityCheck { Name = "SMM fan RPM read", Result = state == "Failed" ? "Fail" : "Pass", Error = state == "Failed" ? "boom" : null } } } } };
    }

    static int Main()
    {
        try
        {
            ReadFailureWritesAnAnonymousReport(); ReadPassWithoutConsentChangesNothing(); ConsentedTestPassesAndPersists(); WriteStepFailuresRestoreAndReport(); EveryModelNeedsTheWriteStep(); IpcAndCli();
            Console.WriteLine("PASS " + passed + " capability checks (fakes only): read checks, anonymous failure report, consent gate, +300 RPM rule, restore readback, no model skips the write test, per-BIOS record, IPC policy, CLI y/N");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); Console.Error.WriteLine(e.StackTrace); return 1; }
    }
}

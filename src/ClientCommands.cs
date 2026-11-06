using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DellFans
{
    public static class ClientCommands
    {
        static readonly HashSet<string> Flags = new HashSet<string>(new[] { "--snapshot", "--monitor", "--test-fan", "--render-preview", "--capability", "--capability-probe", "--verify-sensors" });
        static readonly HashSet<string> Values = new HashSet<string>(new[] { "--rpm", "--minimum-rpm", "--mode", "--output", "--seconds", "--interval", "--input", "--load" });
        static Dictionary<string, string> Parse(string[] args)
        {
            Dictionary<string, string> options = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i]; if (options.ContainsKey(key)) throw new ArgumentException("Duplicate option: " + key);
                if (Flags.Contains(key)) options[key] = "true";
                else if (Values.Contains(key) && i + 1 < args.Length) options[key] = args[++i];
                else throw new ArgumentException("Unsupported option: " + key);
            }
            // --mode and --rpm together are one action: --mode custom --rpm N.
            if (Flags.Count(options.ContainsKey) + (options.ContainsKey("--rpm") || options.ContainsKey("--minimum-rpm") || options.ContainsKey("--mode") ? 1 : 0) != 1)
                throw new ArgumentException("Choose one action. Use --help.");
            return options;
        }
        // The SetMode request for --mode / --rpm, or null when the arguments ask for something else. Throws ArgumentException for a bad one.
        public static ServiceRequest ModeRequest(string[] args) { return ModeRequest(Parse(args)); }
        static ServiceRequest ModeRequest(Dictionary<string, string> options)
        {
            bool rpm = options.ContainsKey("--rpm"), alias = options.ContainsKey("--minimum-rpm");
            if (!rpm && !alias && !options.ContainsKey("--mode")) return null;
            if (rpm && alias) throw new ArgumentException("Use --rpm once.");
            int? minimum = null; int value;
            if (rpm || alias)
            {
                if (!int.TryParse(options[rpm ? "--rpm" : "--minimum-rpm"], NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("--rpm needs a whole number of RPM.");
                minimum = value;
            }
            ServiceRequest request = new ServiceRequest { Command = "SetMode", Mode = options.ContainsKey("--mode") ? options["--mode"] : FanModes.Custom, MinimumRpm = minimum };
            ControlPolicy.Validate(request); return request;
        }
        public static string FormatCapability(CapabilityStatus status)
        {
            if (status == null) return "Capability: unknown (the service did not report a capability state).";
            StringBuilder text = new StringBuilder();
            text.AppendLine("Capability: " + status.State + "  (" + (status.Model ?? "?") + ", BIOS " + (status.BiosVersion ?? "?") + ")");
            if (status.Checks != null)
                foreach (CapabilityCheck check in status.Checks)
                    text.AppendLine("  " + check.Result + "  " + check.Name + (check.Detail == null ? "" : "  " + check.Detail) + (check.Error == null ? "" : "  -- " + check.Error));
            if (status.ReportPath != null) text.AppendLine("Report: " + status.ReportPath + "\nIf you want support for this model, attach it at https://github.com/YZ-SI/DellFans/issues/new");
            if (status.State == CapabilityStates.ReadPassed) text.AppendLine("Read checks passed. Fan control stays off until the full-speed test passes: open the DellFans window, or run DellFans.Cli.exe --capability-probe (15 s at full speed).");
            return text.ToString().TrimEnd();
        }
        // --capability prints the state; --capability-probe asks first (console y/N) and only then sends the consent.
        public static int RunCapability(bool probe, Func<ServiceRequest, Snapshot> send, TextReader input, TextWriter output)
        {
            Snapshot sample = send(new ServiceRequest { Command = "Capability" });
            CapabilityStatus status = sample == null || sample.Background == null ? null : sample.Background.Capability;
            output.WriteLine(FormatCapability(status));
            if (!probe) return 0;
            if (status == null || status.State == CapabilityStates.Passed) { output.WriteLine(status == null ? "Nothing to test." : "Already passed for this model and BIOS."); return 0; }
            if (status.State != CapabilityStates.ReadPassed && status.State != CapabilityStates.Unknown) { output.WriteLine("The read checks did not pass, so the full-speed test is not offered."); return 1; }
            output.WriteLine("The test runs the fan at FULL SPEED for 15 seconds, checks that the RPM rises by at least " + CapabilityProbe.RequiredRpmRise + ", then restores BIOS automatic control.");
            output.Write("Run it now? [y/N] ");
            string answer = input.ReadLine();
            if (answer == null || !(answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))) { output.WriteLine("Not run."); return 0; }
            sample = send(new ServiceRequest { Command = "RunCapabilityProbe", Consent = true });
            status = sample == null || sample.Background == null ? null : sample.Background.Capability;
            output.WriteLine(FormatCapability(status));
            return status != null && status.State == CapabilityStates.Passed ? 0 : 1;
        }
        public static int Run(string[] args)
        {
            if (args.Length == 1 && args[0] == "--help")
            {
                Console.WriteLine("DellFans.Cli.exe --snapshot [--output file.json]\n" +
                    "  --mode quiet|balanced|performance|custom|auto (persistent; custom requires --rpm N)\n" +
                    "      quiet 1500-3000 RPM from 55 C; balanced 2500-full from 65 C; performance 4000-full from 65 C;\n" +
                    "      custom N-full from 65 C; auto = BIOS control, no takeover\n" +
                    "  --rpm 2000 (same as --mode custom --rpm 2000; 1000-5570)\n" +
                    "  --monitor [--seconds 60 --interval 2] [--output file.jsonl]\n" +
                    "  --verify-sensors [--seconds 120] [--load 30] [--output dir] (read-only sensor accuracy/freshness report: report.json, samples.csv; --load = all-core load for N s)\n" +
                    "  --capability (show whether this model + BIOS may be controlled)\n" +
                    "  --capability-probe (read checks, then after a console y/N a 15 s full-speed test that restores BIOS automatic; docs\\CAPABILITY.md)\n" +
                    "  --test-fan (15-second service probe, then restore the saved profile)\n" +
                    "  --render-preview --input snapshot.json [--output preview.png] (offline; no service connection)\n" +
                    "  --install-sensor-driver [--result-file path.json] (download pinned PawnIO 2.2.0, verify SHA-256 and signer CN=namazso.eu, install silently; administrator; the result file must already exist and be empty)\n" +
                    "Closing a client does not stop the service. Initial settings preserve BIOS control.");
                return 0;
            }
            if (args.Length > 0 && args[0] == "--install-sensor-driver") return SensorDriverInstaller.RunCli(SensorDriverInstaller.ResultFileArgument(args));
            Dictionary<string, string> options = Parse(args);
            ServiceRequest modeRequest = ModeRequest(options); // validated before any service connection
            if (options.ContainsKey("--capability") || options.ContainsKey("--capability-probe"))
                return RunCapability(options.ContainsKey("--capability-probe"), ThermalClient.Send, Console.In, Console.Out);
            if (options.ContainsKey("--verify-sensors"))
            {
                if (options.Keys.Any(key => key != "--verify-sensors" && key != "--seconds" && key != "--load" && key != "--output"))
                    throw new ArgumentException("--verify-sensors accepts only --seconds, --load and --output.");
                int verifySeconds = options.ContainsKey("--seconds") ? int.Parse(options["--seconds"], NumberStyles.None, CultureInfo.InvariantCulture) : 120;
                int load = options.ContainsKey("--load") ? int.Parse(options["--load"], NumberStyles.None, CultureInfo.InvariantCulture) : 0;
                string folder = options.ContainsKey("--output") ? Path.GetFullPath(options["--output"]) :
                    Path.Combine(ReportFiles.ReportsDirectory, "sensor-verification-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));
                return SensorVerification.Run(verifySeconds, load, folder, Console.Out);
            }
            if (options.ContainsKey("--load")) throw new ArgumentException("--load is only valid with --verify-sensors.");
            if (options.ContainsKey("--render-preview"))
            {
                if (!options.ContainsKey("--input") || options.Keys.Any(key => key != "--render-preview" && key != "--input" && key != "--output"))
                    throw new ArgumentException("Offline preview requires --input snapshot.json and accepts only --output.");
                string input = Path.GetFullPath(options["--input"]);
                if (new FileInfo(input).Length > 4 * 1024 * 1024) throw new InvalidDataException("Preview snapshot exceeds its size limit.");
                Snapshot preview = MiniJson.Deserialize<Snapshot>(File.ReadAllText(input), 4 * 1024 * 1024);
                if (preview == null || preview.Sensors == null || preview.Warnings == null)
                    throw new InvalidDataException("Preview requires a valid sensor snapshot.");
                string image = options.ContainsKey("--output") ? Path.GetFullPath(options["--output"]) : Path.Combine(ReportFiles.ReportsDirectory, "interface-preview.png");
                if (string.Equals(input, image, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Preview output must be different from its input snapshot.");
                ThermalForm.RenderPreview(preview, image);
                return 0;
            }
            if (options.ContainsKey("--input")) throw new ArgumentException("--input is only valid for offline preview.");
            if (options.ContainsKey("--seconds") && !options.ContainsKey("--monitor")) throw new ArgumentException("模式设置持续生效，不接受持续时间；--seconds 仅用于记录时长。");
            int seconds = options.ContainsKey("--seconds") ? int.Parse(options["--seconds"], CultureInfo.InvariantCulture) : 60;
            int interval = options.ContainsKey("--interval") ? int.Parse(options["--interval"], CultureInfo.InvariantCulture) : 2;
            if (seconds < 1 || seconds > 86400 || interval < 1 || interval > 30) throw new ArgumentOutOfRangeException("monitor interval/duration");
            using (ThermalClient client = new ThermalClient { Detailed = true })
            {
                Snapshot sample = null; DateTime readyDeadline = DateTime.UtcNow.AddSeconds(25);
                while (sample == null)
                {
                    sample = ThermalClient.Send(new ServiceRequest { Command = "Status", Detail = true });
                    if (sample == null) { if (DateTime.UtcNow > readyDeadline) throw new TimeoutException("Service sensors are not ready."); Thread.Sleep(250); }
                }
                if (modeRequest != null) client.SetMode(modeRequest.Mode, modeRequest.MinimumRpm);
                string output = options.ContainsKey("--output") ? Path.GetFullPath(options["--output"]) : Path.Combine(ReportFiles.ReportsDirectory,
                    options.ContainsKey("--monitor") ? "monitor.jsonl" : "snapshot.json");
                if (options.ContainsKey("--monitor"))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (StreamWriter writer = new StreamWriter(output, false, new System.Text.UTF8Encoding(false)))
                    {
                        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                        do { string json = ReportFiles.Json(client.Poll()); writer.WriteLine(json); writer.Flush(); Console.WriteLine(json); Thread.Sleep(interval * 1000); } while (DateTime.UtcNow < end);
                    }
                }
                else if (options.ContainsKey("--test-fan"))
                {
                    sample = client.BeginProbe(); DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    do { Thread.Sleep(2000); sample = client.Poll(); if (DateTime.UtcNow > deadline) throw new TimeoutException("Probe did not finish."); } while (sample.Background.ProbeActive);
                    ReportFiles.Write(output, sample); Console.WriteLine(ReportFiles.Json(sample));
                }
                else { sample = client.Poll(); ReportFiles.Write(output, sample); Console.WriteLine(ReportFiles.Json(sample)); }
            }
            return 0;
        }
    }
}

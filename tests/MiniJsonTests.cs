using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using DellFans;

// Offline. The old JavaScriptSerializer is only a reference here; the product no longer uses it on the service/CLI/pipe path.
internal static class MiniJsonTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    static void Throws(Action action, string message)
    {
        try { action(); } catch (Exception) { passed++; return; }
        throw new Exception(message);
    }

    // Two JSON documents are the same data: objects by key (order-insensitive), numbers by value.
    static bool Same(object a, object b)
    {
        IDictionary<string, object> da = a as IDictionary<string, object>, db = b as IDictionary<string, object>;
        if (da != null || db != null)
            return da != null && db != null && da.Count == db.Count && da.All(p => db.ContainsKey(p.Key) && Same(p.Value, db[p.Key]));
        IList la = a as IList, lb = b as IList;
        if (la != null && !(a is string) || lb != null && !(b is string))
        {
            if (la == null || lb == null || la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i])) return false;
            return true;
        }
        if (a == null || b == null) return a == b;
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
        return a.Equals(b);
    }
    static bool IsNumber(object o) { return o is int || o is long || o is double || o is float || o is decimal; }

    static Snapshot FullSnapshot()
    {
        return new Snapshot
        {
            PolledAtUtc = "2026-11-06T01:02:03.4567890Z", Model = "OptiPlex Example", BiosVersion = "1.0.0", FanMode = "ManualRpm",
            FullSpeedSecondsRemaining = null, PollDurationMilliseconds = 12,
            Sensors = new List<SensorRow> {
                new SensorRow { Id = "cpu/package", Device = "13th Gen Intel(R) Core(TM) i5-13400", HardwareType = "Cpu", Name = "CPU Package", Type = "Temperature", Role = "Temperature", Unit = "°C", Value = 67.5f, Minimum = 40f, Maximum = 80.1f, Source = "PawnIO IntelMSR", Quality = "OK", PolledAtUtc = "2026-11-06T01:02:03Z" },
                new SensorRow { Id = "dell/fan/F600", Name = "System / CPU Fan", Type = "Fan", Unit = "RPM", Value = 4500f, Quality = "Stale", PolledAtUtc = null } },
            Warnings = new List<string> { "quote \" backslash \\ tab \t newline \n 中文 \u0001", "second" },
            Coverage = new Dictionary<string, string> { { "Cpu", "3 current temperature channels" } },
            ControlCapabilities = new Dictionary<string, object> { { "ImplementedModes", new[] { "BiosAutomatic", "ManualRpm" } }, { "TargetRpmImplemented", true }, { "AdjustableMinimumRpm", 1000 }, { "Ratio", 0.25 } },
            ManualControl = new ManualFanStatus { Active = true, TargetRpm = 3000, RangePercent = 43.9, ActualRpm = 2990, State = "Holding", Error = null, FeedbackAtUtc = "2026-11-06T01:02:03Z" },
            FirmwareConfigurationWriteCount = 2,
            Resources = new ResourceStatus { AtUtc = "2026-11-06T01:02:03Z", UptimeSeconds = 3600.5, LogicalProcessors = 20, CpuPercent = 0.4, WorkingSetMB = 31.2, PrivateMemoryMB = 19.9 },
            Background = new BackgroundStatus { State = "FloorCurve", Message = "m", Settings = new FloorSettings { SchemaVersion = 2, Mode = "balanced", MinimumRpm = 2500 }, Mode = "balanced",
                ModeLimits = new ModeLimits { Min = 2500, Max = 5570, StartC = 65 }, EffectiveTargetRpm = 3000, ControlTemperatureC = 66.25, ProbeActive = false, ProcessId = 1234, SensorSource = "PawnIO IntelMSR", SensorDriverState = "Ready" },
            CpuControlTemperatureC = 66.25, CpuProtectionTemperatureC = 70, SensorSource = "PawnIO IntelMSR", SensorDriverState = "Ready",
            SensorTimings = new Dictionary<string, double> { { "PawnIO IntelMSR", 0.31 } }
        };
    }

    static void WriterMatchesTheOldSerializer()
    {
        JavaScriptSerializer old = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        object[] fixtures = {
            FullSnapshot(), FullSnapshot().Summary(), new Snapshot(), FloorSettings.Defaults(),
            new FloorSettings { SchemaVersion = 2, Mode = "custom", MinimumRpm = 3300, LastFault = "x" },
            new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 3000 }, new ServiceRequest { Command = "Status", Detail = false },
            new ServiceReply { Success = false, Error = "boom", Snapshot = null }, new ServiceReply { Success = true, Snapshot = FullSnapshot() },
            new { AtUtc = "2026-11-06T00:00:00Z", Error = "System.Exception: a\r\n   at b", Count = 7, Flag = true, Nothing = (string)null, Ratio = 0.1 },
            new FanWatchdogRecord { ParentId = 99, ParentStartTicks = 639000000000000000L, Model = "M", Bios = "B", RestoreOverride = "Disabled", ServiceOwned = true }
        };
        foreach (object fixture in fixtures)
        {
            string mine = MiniJson.Serialize(fixture), theirs = old.Serialize(fixture);
            Check(Same(MiniJson.Parse(mine), MiniJson.Parse(theirs)), "serialization differs from the old serializer for " + fixture.GetType().Name + ":\n" + mine + "\n" + theirs);
        }
        // Where the old serializer needs no HTML escaping the text is identical, including property order and nulls.
        ServiceRequest request = new ServiceRequest { Command = "SetMode", Mode = "custom", MinimumRpm = 3000 };
        Check(MiniJson.Serialize(request) == old.Serialize(request), "request text differs: " + MiniJson.Serialize(request));
        FloorSettings settings = new FloorSettings { SchemaVersion = 2, Mode = "quiet" };
        Check(MiniJson.Serialize(settings) == old.Serialize(settings) && MiniJson.Serialize(settings).Contains("\"MinimumRpm\":null"), "settings text differs / null not emitted: " + MiniJson.Serialize(settings));
        string snapshotText = MiniJson.Serialize(FullSnapshot());
        Check(snapshotText.StartsWith("{\"SchemaVersion\":1,\"PolledAtUtc\":\"2026-11-06T01:02:03.4567890Z\",\"Model\":\"OptiPlex Example\""), "properties are written in declaration order: " + snapshotText.Substring(0, 120));
        Check(MiniJson.Serialize(0.1) == "0.1" && MiniJson.Serialize(67.5f) == "67.5" && MiniJson.Serialize(double.NaN) == "null" && MiniJson.Serialize(1234567890123L) == "1234567890123", "number formatting");
    }

    static void ReaderRoundTrips()
    {
        JavaScriptSerializer old = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        Snapshot snapshot = FullSnapshot();
        foreach (string text in new[] { MiniJson.Serialize(snapshot), old.Serialize(snapshot) })
        {
            Snapshot back = MiniJson.Deserialize<Snapshot>(text);
            Check(Same(MiniJson.Parse(MiniJson.Serialize(back)), MiniJson.Parse(MiniJson.Serialize(snapshot))), "Snapshot did not round trip");
            Check(back.Sensors.Count == 2 && back.Sensors[0].Value == 67.5f && back.Sensors[1].PolledAtUtc == null && back.Background.Settings.Mode == "balanced"
                && back.ManualControl.TargetRpm == 3000 && back.Warnings[0].Contains("中文") && (bool)back.ControlCapabilities["TargetRpmImplemented"] && back.SensorTimings["PawnIO IntelMSR"] == 0.31, "Snapshot fields lost");
        }
        // the old serializer reads what MiniJson writes
        Snapshot viaOld = old.Deserialize<Snapshot>(MiniJson.Serialize(snapshot));
        Check(viaOld.Model == "OptiPlex Example" && viaOld.Sensors.Count == 2, "the old reader cannot read the new writer");
        FloorSettings settings = MiniJson.Deserialize<FloorSettings>(old.Serialize(new FloorSettings { SchemaVersion = 2, Mode = "custom", MinimumRpm = 2200 }));
        Check(settings.Mode == "custom" && settings.MinimumRpm == 2200, "FloorSettings round trip");
        ServiceRequest legacy = MiniJson.Deserialize<ServiceRequest>("{\"Command\":\"Status\"}");
        Check(legacy.Command == "Status" && !legacy.Detail.HasValue, "a Status request without Detail has no Detail value");
        Check(MiniJson.Deserialize<ServiceRequest>("{\"command\":\"Status\",\"detail\":false}").Detail == false, "member names match case-insensitively like the old reader");
    }

    static void StrictReader()
    {
        Throws(delegate { MiniJson.Parse("{\"a\":1,\"a\":2}"); }, "duplicate key accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":{\"b\":1,\"b\":2}}"); }, "nested duplicate key accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":1,}"); }, "trailing comma accepted");
        Throws(delegate { MiniJson.Parse("[1,]"); }, "array trailing comma accepted");
        Throws(delegate { MiniJson.Parse("{} x"); }, "trailing text accepted");
        Throws(delegate { MiniJson.Parse(""); }, "empty text accepted");
        Throws(delegate { MiniJson.Parse("{'a':1}"); }, "single quotes accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":01}"); }, "leading zero accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":\"\\u12\"}"); }, "short \\u escape accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":\"\\q\"}"); }, "bad escape accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":\"tab\there\"}"); }, "raw control character in a string accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":1e999}"); }, "infinite number accepted");
        Throws(delegate { MiniJson.Parse("// c\n{}"); }, "comment accepted");
        Check(MiniJson.Parse("\uFEFF {\"a\" : [ 1 , 2.5 , -3 , \"\\u00e9\\n\" , true , null ] } ") != null, "BOM and whitespace are fine");
        string deep16 = new string('[', 16) + new string(']', 16), deep17 = new string('[', 17) + new string(']', 17);
        MiniJson.Parse(deep16); passed++;
        Throws(delegate { MiniJson.Parse(deep17); }, "depth 17 accepted");
        Throws(delegate { MiniJson.Parse("{\"a\":" + new string('1', 100) + "}", 50); }, "size limit not enforced");
        Throws(delegate { MiniJson.Deserialize<ServiceRequest>("{\"Command\":5}"); }, "wrong-typed field accepted");
        Throws(delegate { MiniJson.Deserialize<ServiceRequest>("{\"MinimumRpm\":2.5}"); }, "fractional int accepted");
        Throws(delegate { MiniJson.Deserialize<ServiceRequest>("{\"MinimumRpm\":99999999999}"); }, "overflowing int accepted");
        Throws(delegate { MiniJson.Deserialize<FanWatchdogRecord>("[1]"); }, "array for an object accepted");
        // unknown keys are ignored, and listed
        List<string> unknown;
        ServiceRequest request = MiniJson.Deserialize<ServiceRequest>("{\"Command\":\"SetMode\",\"Mode\":\"custom\",\"MinimumRpm\":2000,\"StartTemperatureC\":65,\"Nested\":{\"x\":1}}", 65536, out unknown);
        Check(request.Command == "SetMode" && request.MinimumRpm == 2000 && unknown.SequenceEqual(new[] { "StartTemperatureC", "Nested" }), "unknown keys: " + string.Join(",", unknown));
        object number = MiniJson.Parse("[1,3000000000,1.5,-0]");
        IList numbers = (IList)number;
        Check(numbers[0] is int && numbers[1] is long && numbers[2] is double, "number kinds");
    }

    static void CommandsRefuseUnknownFields()
    {
        List<string> unknown;
        ServiceRequest floor = MiniJson.Deserialize<ServiceRequest>("{\"Command\":\"SetMode\",\"Mode\":\"custom\",\"MinimumRpm\":2000,\"StartTemperatureC\":65,\"FullTemperatureC\":85}", 65536, out unknown);
        Throws(delegate { ControlPolicy.RejectUnknownFields(floor, unknown); }, "a SetMode with curve fields must be refused");
        try { ControlPolicy.RejectUnknownFields(floor, unknown); } catch (ArgumentException e) { Check(e.Message.Contains("StartTemperatureC") && e.Message.Contains("SetMode"), "the refusal names the fields and the replacement: " + e.Message); }
        ServiceRequest status = MiniJson.Deserialize<ServiceRequest>("{\"Command\":\"Status\",\"Detail\":false,\"FutureField\":1}", 65536, out unknown);
        ControlPolicy.RejectUnknownFields(status, unknown); passed++; // Status keeps ignoring unknown keys
        ServiceRequest mode = MiniJson.Deserialize<ServiceRequest>("{\"Command\":\"SetMode\",\"Mode\":\"quiet\"}", 65536, out unknown);
        ControlPolicy.RejectUnknownFields(mode, unknown); passed++;
        ServiceRequest bad = MiniJson.Deserialize<ServiceRequest>("{\"Command\":\"SetMode\",\"Mode\":\"quiet\",\"Curve\":[1]}", 65536, out unknown);
        Throws(delegate { ControlPolicy.RejectUnknownFields(bad, unknown); }, "SetMode with an unknown field must be refused");
        // through the real frame reader
        MemoryStream frame = new MemoryStream();
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"Command\":\"SetMode\",\"Mode\":\"custom\",\"MinimumRpm\":2000,\"StartTemperatureC\":65}");
        frame.Write(BitConverter.GetBytes(body.Length), 0, 4); frame.Write(body, 0, body.Length); frame.Position = 0;
        ServiceRequest framed = PipeProtocol.Read<ServiceRequest>(frame, 65536, 1000, out unknown);
        Check(framed.MinimumRpm == 2000 && unknown.Count == 1, "PipeProtocol reports unknown keys");
    }

    // rust\crates\core\fixtures\status.json is what MiniJson writes for a representative summary reply, with the values the Rust tests
    // vary replaced by placeholders. DELLFANS_WRITE_FIXTURE=1 regenerates it.
    static void StatusFixtureIsRealMiniJsonOutput()
    {
        string stamp = "2026-10-31T04:00:00.0000000Z";
        Func<string, string, string, string, float?, SensorRow> row = (id, type, hw, name, value) =>
            new SensorRow { Id = id, HardwareType = hw, Name = name, Type = type, Value = value, Quality = "OK", PolledAtUtc = stamp };
        FloorSettings settings = new FloorSettings { SchemaVersion = 2, Mode = "__MODE__", MinimumRpm = 987654 };
        Snapshot full = new Snapshot { PolledAtUtc = stamp, Model = "OptiPlex Example", BiosVersion = "1.0.0", FanMode = "ManualRpm",
            Warnings = new List<string> { "__WARN__" }, SensorSource = "PawnIO IntelMSR", SensorDriverState = "Ready",
            Sensors = new List<SensorRow> {
                row("dell/fan/F600", "Fan", "DellFirmware", "System / CPU Fan", 7654.5f),
                row("cpu/package", "Temperature", "Cpu", "CPU Package", 76.5f),
                row("cpu/max", "Temperature", "Cpu", "Core Max", 76.5f),
                row("cpu/core/0", "Temperature", "Cpu", "P-core 0", 70f),
                row("storage/nvme0", "Temperature", "Storage", "Temperature", 54.5f),
                row("storage/nvme1", "Temperature", "Storage", "Temperature", 38f) },
            Background = new BackgroundStatus { State = "Running", Settings = settings, Mode = "__MODE__", EffectiveTargetRpm = 987655,
                ControlTemperatureC = 76.5, ProcessId = 4242, SensorSource = "PawnIO IntelMSR", SensorDriverState = "Ready",
                Capability = new CapabilityStatus { State = CapabilityStates.Passed, Model = "OptiPlex Example", BiosVersion = "1.0.0" } } };
        string json = ReportFiles.Json(new ServiceReply { Success = true, Snapshot = full.Summary() })
            .Replace("7654.5", "__RPM__").Replace("987655", "__RPM__").Replace("76.5", "__CPU__").Replace("54.5", "__DISK__").Replace("987654", "__MINRPM__");
        string root = Environment.GetEnvironmentVariable("DELLFANS_ROOT");
        Check(root != null && Directory.Exists(root), "DELLFANS_ROOT must point at the repository");
        string path = Path.Combine(root, "rust", "crates", "core", "fixtures", "status.json");
        if (Environment.GetEnvironmentVariable("DELLFANS_WRITE_FIXTURE") == "1") File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        Check(File.ReadAllText(path).Trim() == json, "status.json differs from the MiniJson output (set DELLFANS_WRITE_FIXTURE=1 to regenerate)");
    }

    static int Main()
    {
        try
        {
            WriterMatchesTheOldSerializer(); ReaderRoundTrips(); StrictReader(); CommandsRefuseUnknownFields(); StatusFixtureIsRealMiniJsonOutput();
            Console.WriteLine("PASS " + passed + " MiniJson checks: writer vs JavaScriptSerializer (Snapshot/settings/request/reply), declaration order, nulls, strict reader (duplicates, depth 16, size, escapes, numbers), unknown keys ignored/listed, write commands refuse unknown fields");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e.Message); Console.Error.WriteLine(e.StackTrace); return 1; }
    }
}

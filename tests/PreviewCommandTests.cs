using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using DellFans;

internal static class PreviewCommandTests
{
    [STAThread]
    static int Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DellFans-OfflinePreview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Snapshot fixture = new Snapshot {
                PolledAtUtc = DateTime.UtcNow.ToString("o"), Model = "Offline preview fixture", BiosVersion = "1.0.0", FanMode = "ManualRpm",
                Sensors = new List<SensorRow> {
                    new SensorRow { Id = "cpu/package", HardwareType = "Cpu", Name = "CPU Package", Type = "Temperature", Role = "Temperature", Value = 52, Unit = "°C", Quality = "OK" },
                    new SensorRow { Id = "cpu/max", HardwareType = "Cpu", Name = "Core Max", Type = "Temperature", Role = "DerivedTemperature", Value = 55, Unit = "°C", Quality = "OK" },
                    new SensorRow { Id = "cpu/average", HardwareType = "Cpu", Name = "Core Average", Type = "Temperature", Role = "DerivedTemperature", Value = 49, Unit = "°C", Quality = "OK" },
                    new SensorRow { Id = "dell/fan/F600", HardwareType = "DellFirmware", Name = "System / CPU Fan", Type = "Fan", Role = "Measurement", Value = 2520, Unit = "RPM", Quality = "OK" }
                },
                Warnings = new List<string>(), Coverage = new Dictionary<string, string>(),
                ControlCapabilities = new Dictionary<string, object> { { "TargetRpmImplemented", true }, { "AdjustableMinimumRpm", 1000 }, { "AdjustableMaximumRpm", 5570 } },
                ManualControl = new ManualFanStatus { Active = true, TargetRpm = 2500, ActualRpm = 2520, State = "Holding" },
                Background = new BackgroundStatus { State = "FloorCurve", Settings = new FloorSettings { SchemaVersion = 2, Mode = "custom", MinimumRpm = 2500 }, Mode = "custom", ModeLimits = new ModeLimits { Min = 2500, Max = 5570, StartC = 65 }, EffectiveTargetRpm = 2500, ControlTemperatureC = 55 }
            };
            string input = Path.Combine(folder, "fixture.json"), output = Path.Combine(folder, "preview.png");
            ReportFiles.Write(input, fixture);
            string original = File.ReadAllText(input);
            if (ClientCommands.Run(new[] { "--render-preview", "--input", input, "--output", output }) != 0)
                throw new Exception("Offline preview command did not succeed.");
            using (Image image = Image.FromFile(output))
                if (image.Width < 600 || image.Height < 400) throw new Exception("Offline preview did not produce a usable window image.");
            if (File.ReadAllText(input) != original) throw new Exception("Offline preview modified its input snapshot.");
            Console.WriteLine("PASS offline preview renders an input snapshot to PNG and preserves the input file.");
            bool rejected = false;
            try { ClientCommands.Run(new[] { "--render-preview", "--input", input, "--output", input }); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected || File.ReadAllText(input) != original) throw new Exception("Offline preview must reject overwriting its input snapshot.");
            Console.WriteLine("PASS offline preview rejects output equal to its input without changing the snapshot.");
            Console.WriteLine("Offline artifacts: " + folder);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); Console.Error.WriteLine("Offline artifacts: " + folder); return 1; }
    }
}

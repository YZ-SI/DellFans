using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DellFans
{
    // "DellFans.Cli.exe --render-preview [--output folder] [--dpi 100,150]": every page x built-in skin x language to PNG with made-up
    // readings. No service, pipe, registry write or window is involved.
    public static class PreviewRender
    {
        public static readonly string[] Themes = { "white", "black", "purple-black" }, Languages = { "zh-CN", "zh-TW", "en" };
        public static Snapshot Fixture(DateTime nowUtc)
        {
            Func<string, string, string, string, float, SensorRow> row = delegate(string id, string hardware, string name, string type, float value)
            { return new SensorRow { Id = id, HardwareType = hardware, Name = name, Type = type, Role = SensorRoles.Classify(type, name), Value = value, Quality = "OK", Unit = SensorRoles.UnitFor(type) }; };
            SensorRow fan = row("dell/fan/F600", "DellFirmware", "System / CPU Fan", "Fan", 3120); fan.Minimum = 1580; fan.Maximum = 4100;
            SensorRow disk = row("nvme/0/temp", "Storage", "Composite", "Temperature", 41); disk.Device = "Samsung SSD 980 PRO 1TB"; disk.Maximum = 47;
            return new Snapshot
            {
                PolledAtUtc = nowUtc.ToString("o"), Model = "OptiPlex Example", BiosVersion = "1.0.0", FanMode = "ManualRpm",
                Sensors = new List<SensorRow> { row("cpu/package", "Cpu", "CPU Package", "Temperature", 62), row("cpu/max", "Cpu", "Core Max", "Temperature", 66), row("cpu/avg", "Cpu", "Core Average", "Temperature", 57), fan, disk },
                Warnings = new List<string>(), Coverage = new Dictionary<string, string>(), CpuControlTemperatureC = 62,
                ControlCapabilities = new Dictionary<string, object> { { "TargetRpmImplemented", true } },
                ManualControl = new ManualFanStatus { Active = true, TargetRpm = 3100, ActualRpm = 3120, State = "Holding" },
                Resources = new ResourceStatus { CpuPercent = 0.3, PrivateMemoryMB = 14.2, WorkingSetMB = 24 },
                Background = new BackgroundStatus { State = "FloorCurve", Settings = new FloorSettings { SchemaVersion = 2, Mode = FanModes.Balanced, MinimumRpm = 2500 }, Mode = FanModes.Balanced,
                    ModeLimits = FanModes.Limits(FanModes.Balanced, null), EffectiveTargetRpm = 3100, ControlTemperatureC = 62 }
            };
        }
        // Ten minutes of history, some events and process totals, as a running window would hold them.
        public static void Decorate(DashboardSurface s)
        {
            DashboardState state = s.State; DateTime now = DateTime.Now; state.Trend.Clear();
            for (int i = 120; i >= 0; i--)
            {
                double t = (120 - i) / 120.0;
                state.AddTrend(now.AddSeconds(-i * 5), (float)(2700 + 1500 * Math.Sin(t * 5.2) * Math.Sin(t * 1.4 + 0.4) + 200 * Math.Sin(t * 31)), (float)(54 + 12 * Math.Sin(t * 5.2 - 0.3) + 2 * Math.Sin(t * 23)));
            }
            state.Events.Clear();
            state.AddEvent(now.AddMinutes(-9), "后台已连接", false);
            state.AddEvent(now.AddMinutes(-8), "模式切换为 {0}", false, DashboardState.ModeToken(FanModes.Quiet));
            state.AddEvent(now.AddMinutes(-6), "控制状态：{0}", false, DashboardState.StateToken("FloorCurve"));
            state.AddEvent(now.AddMinutes(-4), "模式切换为 {0}", false, DashboardState.ModeToken(FanModes.Performance));
            state.AddEvent(now.AddMinutes(-3), "控制状态：{0}", true, DashboardState.StateToken("EmergencyFullSpeed"));
            state.AddEvent(now.AddMinutes(-2), "控制状态：{0}", false, DashboardState.StateToken("FloorCurve"));
            state.AddEvent(now.AddMinutes(-1), "模式切换为 {0}", false, DashboardState.ModeToken(FanModes.Balanced));
            state.Totals = new ProcessTotals { CpuPercent = 0.6, MemoryMiB = 61.3, Count = 4 };
            s.StartupOn = true; s.DeckOn = true; s.ExportPath = @"C:\Users\Example\AppData\Local\DellFans\Reports";
        }
        public static int RenderAll(string folder, float[] scales)
        {
            string theme = UiTheme.Current.Id, language = UiText.Language; int count = 0;
            try
            {
                Directory.CreateDirectory(folder);
                foreach (float scale in scales)
                    foreach (string t in Themes)
                        foreach (string lang in Languages)
                        {
                            UiTheme.Select(t); UiText.Select(lang);
                            string suffix = "-" + t + "-" + lang + "-" + ((int)Math.Round(scale * 100)).ToString(CultureInfo.InvariantCulture) + ".png";
                            foreach (UiPage page in new[] { UiPage.Home, UiPage.Details, UiPage.Settings })
                            { ThermalForm.RenderPage(Fixture(DateTime.UtcNow), Path.Combine(folder, page.ToString().ToLowerInvariant() + suffix), page, scale, DashboardSurface.DefaultWidth, Decorate); count++; }
                            ThermalForm.RenderPage(Fixture(DateTime.UtcNow), Path.Combine(folder, "home-custom" + suffix), UiPage.Home, scale, DashboardSurface.DefaultWidth,
                                delegate(DashboardSurface s) { Decorate(s); s.SelectedMode = FanModes.Custom; s.CustomPending = true; s.DraftText = "2800"; }); count++;
                            ThermalForm.RenderPage(Fixture(DateTime.UtcNow), Path.Combine(folder, "details-hover" + suffix), UiPage.Details, scale, DashboardSurface.DefaultWidth,
                                delegate(DashboardSurface s) { Decorate(s); s.PreviewHover = 0.62f; }); count++;
                            string skins = Path.Combine(Path.GetTempPath(), "DellFans-PreviewSkin-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(skins);
                            File.WriteAllText(Path.Combine(skins, "sunset.json"), "{\"schemaVersion\":1,\"id\":\"sunset\",\"name\":\"Sunset\",\"base\":\"white\",\"colors\":{\"background\":\"#F6EDE6\",\"accent\":\"#B4440E\",\"selection\":\"#F6D9C6\"}}");
                            UiTheme.ReloadCustomThemes(skins);
                            ThermalForm.RenderPage(Fixture(DateTime.UtcNow), Path.Combine(folder, "settings-more" + suffix), UiPage.Settings, scale, DashboardSurface.DefaultWidth,
                                delegate(DashboardSurface s)
                                {
                                    Decorate(s); s.MoreLanguages = true; s.DeckOn = false;
                                    s.PreviewLanguages = new[] { new KeyValuePair<string, string>("fr-FR", "Français"), new KeyValuePair<string, string>("ja-JP", "日本語"), new KeyValuePair<string, string>("de-DE", "Deutsch") };
                                }); count++;
                            UiTheme.ReloadCustomThemes(Path.Combine(skins, "none")); Directory.Delete(skins, true);
                        }
            }
            finally { UiTheme.Select(theme); UiText.Select(language); }
            return count;
        }
    }
}

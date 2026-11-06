using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DellFans;

internal static class ThemeTests
{
    static int passed;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); passed++; }
    static double Channel(byte value)
    { double c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    static double Luminance(Color c)
    { return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B); }
    static double Contrast(Color a, Color b)
    { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
    static void Builtins()
    {
        Check(UiTheme.Current.Id == "black", "A new user must start with the black skin (the deck default)");
        Check(UiTheme.Available.Count == 3, "Exactly three built-in skins must be available without resource loading");
        int changed = 0;
        Action handler = delegate { changed++; }; UiTheme.Changed += handler;
        try
        {
            foreach (string id in new[] { "white", "black", "purple-black" })
            {
                Check(UiTheme.Select(id) && UiTheme.Current.Id == id, "Cannot select built-in skin " + id);
                UiPalette p = UiTheme.Current;
                foreach (Color surface in new[] { p.Background, p.Surface, p.SurfaceAlt, p.Input })
                {
                    Check(Contrast(p.Ink, surface) >= 4.5, id + " primary text contrast");
                    Check(Contrast(p.Muted, surface) >= 4.5, id + " secondary text contrast");
                    Check(Contrast(p.Warning, surface) >= 4.5, id + " warning text contrast");
                }
                Check(Contrast(p.AccentInk, p.Accent) >= 4.5, id + " action label contrast");
                Check(Contrast(p.Ink, p.Selection) >= 4.5, id + " selected-grid text contrast");
            }
            Check(changed == 3, "Changed must fire only when the selected skin changes");
            Check(!UiTheme.Select("../../untrusted") && UiTheme.Current.Id == "black", "Unknown skin ids must fall back to black");
            Check(changed == 4, "Fallback selection must refresh the UI once");
            Check(!UiTheme.Select(null) && changed == 4, "Repeated fallback must not raise a second event");
        }
        finally { UiTheme.Changed -= handler; UiTheme.Select("black"); }
        Console.WriteLine("PASS built-in selection, safe fallback and text/action/selection contrast");
    }
    static void CustomPalette()
    {
        UiPalette custom; string error;
        string json = "{\"schemaVersion\":1,\"id\":\"my-white\",\"name\":\"我的白色\",\"base\":\"white\",\"fontFamily\":\"Microsoft YaHei UI\",\"cornerRadius\":6,\"colors\":{\"accent\":\"#075985\",\"edge\":\"#CCD5E0\"}}";
        Check(UiTheme.TryParse(json, out custom, out error), "A valid custom palette must load: " + error);
        Check(error == null && custom.Id == "my-white" && custom.Name == "我的白色" && custom.IsCustom, "Custom metadata was lost");
        Check(custom.Accent.ToArgb() == Color.FromArgb(7, 89, 133).ToArgb() && custom.CornerRadius == 6 && custom.FontFamily == "Microsoft YaHei UI", "Custom color/font/radius overrides were lost");
        Check(custom.Background.ToArgb() == Color.FromArgb(237, 240, 245).ToArgb(), "Missing tokens must inherit the selected built-in base");
        Check(UiTheme.Current.Id == "black" && UiTheme.Current.Accent.ToArgb() == Color.FromArgb(76, 141, 255).ToArgb(), "Validation must not mutate the active or built-in palette");
        Console.WriteLine("PASS custom palette overrides and immutable built-in inheritance");
    }
    static void RejectedResources()
    {
        string prefix = "{\"schemaVersion\":1,\"id\":\"safe-test\",\"name\":\"Safe\",\"base\":\"white\"";
        string[] invalid = {
            prefix + ",\"colors\":{\"ink\":\"#FFFFFF\"}}",
            prefix + ",\"colors\":{\"muted\":\"#A0A0A0\"}}",
            prefix + ",\"colors\":{\"warning\":\"#FFCC00\"}}",
            prefix + ",\"colors\":{\"accentInk\":\"#146B88\"}}",
            prefix + ",\"colors\":{\"selection\":\"#182230\"}}",
            prefix + ",\"script\":\"calc.exe\"}", prefix + ",\"backgroundImage\":\"file:///secret\"}",
            prefix + ",\"colors\":{\"ink\":\"red\"}}", prefix + ",\"colors\":{\"ink\":\"#FFF\"}}",
            prefix + ",\"colors\":{\"ink\":\"#00112233\"}}", prefix + ",\"colors\":{\"ink\":\"url(x)\"}}",
            prefix + ",\"colors\":{\"unknown\":\"#123456\"}}", prefix + ",\"colors\":[]}",
            prefix + ",\"fontFamily\":\"../font.ttf\"}", prefix + ",\"fontFamily\":null}",
            prefix + ",\"cornerRadius\":29}", prefix + ",\"cornerRadius\":-1}", prefix + ",\"cornerRadius\":8.5}",
            prefix.Replace("safe-test", "../../escape") + "}", prefix.Replace("safe-test", "white") + "}",
            prefix.Replace("safe-test", "Uppercase") + "}", prefix.Replace("safe-test", new string('a', 41)) + "}",
            prefix.Replace("safe-test", "safe-test\\n") + "}", prefix + ",\"colors\":{\"edge\":\"#FFFFFF\\n\"}}",
            prefix.Replace("\"white\"", "\"custom-base\"") + "}", prefix.Replace("\"schemaVersion\":1", "\"schemaVersion\":2") + "}",
            prefix.Replace("\"Safe\"", "\"Line\\nBreak\"") + "}", prefix.Replace("\"Safe\"", "\" \"") + "}",
            "{}", "[]", "null", "{", prefix + "} trailing", new string(' ', 16385), new string('白', 5500), null
        };
        foreach (string json in invalid)
        {
            UiPalette p; string error;
            Check(!UiTheme.TryParse(json, out p, out error) && p == null && !String.IsNullOrEmpty(error), "Unsafe resource accepted: " + (json == null ? "<null>" : json.Substring(0, Math.Min(160, json.Length))));
        }
        Check(UiTheme.Current.Id == "black", "Rejected JSON must not change selection");
        Console.WriteLine("PASS bounded schema/color/font/radius validation and contrast rejection");
    }
    // Shared tokens: every built-in uses the same large card radius, external skins may only tune radius and shadow within bounds.
    static void ShadowAndRadius()
    {
        foreach (UiPalette p in UiTheme.Available)
            Check(p.CornerRadius == UiMetrics.CardRadius && p.ShadowStrength > 0 && p.ShadowStrength <= 100, "Built-in skin tokens drifted: " + p.Id);
        UiPalette custom; string error;
        string head = "{\"schemaVersion\":1,\"id\":\"soft\",\"name\":\"Soft\",\"base\":\"black-gray\"";
        Check(UiTheme.TryParse(head + ",\"cornerRadius\":28,\"shadowStrength\":0}", out custom, out error) && custom.CornerRadius == 28 && custom.ShadowStrength == 0 && custom.Id == "soft", "legacy base ids and bounds must be accepted: " + error);
        Check(custom.Background.ToArgb() == UiTheme.Available[1].Background.ToArgb(), "the legacy base black-gray must inherit the black palette");
        foreach (string bad in new[] { ",\"shadowStrength\":101}", ",\"shadowStrength\":-1}", ",\"shadowStrength\":1.5}", ",\"shadowStrength\":\"9\"}" })
            Check(!UiTheme.TryParse(head + bad, out custom, out error), "shadowStrength outside 0-100 was accepted: " + bad);
        Check(UiTheme.Font(13, false) == UiTheme.Font(13, false) && UiTheme.Font(13, true) != UiTheme.Font(13, false), "fonts must be cached per size and style");
        int before = UiTheme.CachedFontCount;
        for (int i = 0; i < 50; i++) { UiTheme.Font(13, false); UiTheme.Font(12, false); }
        Check(UiTheme.CachedFontCount == Math.Max(before, 3), "repeated font requests must not create new fonts");
        UiTheme.ReleaseFonts(); Check(UiTheme.CachedFontCount == 0, "ReleaseFonts must free the cache");
        Console.WriteLine("PASS shared radius/shadow tokens, legacy base aliases and the font cache");
    }
    static void TemplateResources(string root)
    {
        foreach (string id in new[] { "diy-white", "diy-black-gray", "diy-purple-black" })
        {
            UiPalette p; string error;
            Check(UiTheme.TryParse(File.ReadAllText(Path.Combine(root, "themes", id + ".json")), out p, out error), "Published template must validate: " + id + ": " + error);
            Check(p.Id == id && p.IsCustom, "Template filename/id mismatch: " + id);
        }
        Console.WriteLine("PASS all three published DIY JSON templates");
    }
    static string Resource(string id)
    { return "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"Fixture\",\"base\":\"white\"}"; }
    static void IsolatedDirectory()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DellFans-ThemeFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "fixture.json"), Resource("fixture"), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(folder, "wrong.json"), Resource("different-id"));
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{");
            File.WriteAllText(Path.Combine(folder, "oversized.json"), new string(' ', 16385));
            File.WriteAllBytes(Path.Combine(folder, "invalid-utf8.json"), new byte[] { 0xFF, 0xFE, 0xFF });
            File.WriteAllText(Path.Combine(folder, "reserved.json"), Resource("white"));
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "nested", "nested.json"), Resource("nested"));
            string[] errors = UiTheme.ReloadCustomThemes(folder);
            Check(errors.Length == 5 && UiTheme.Diagnostics.Count == 5, "Invalid file diagnostics must remain bounded and visible: " + string.Join("; ", errors));
            Check(UiTheme.Available.Count == 4 && UiTheme.Available.IsReadOnly && UiTheme.Diagnostics.IsReadOnly, "Only the valid top-level resource may join the built-in read-only list");
            int changed = 0; Action handler = delegate { changed++; }; UiTheme.Changed += handler;
            try
            {
                Check(UiTheme.Select("fixture") && UiTheme.Current.IsCustom, "Valid UTF-8 BOM resource was not selectable");
                File.WriteAllText(Path.Combine(folder, "fixture.json"), Resource("fixture").Replace("\"white\"", "\"black\""));
                UiTheme.ReloadCustomThemes(folder);
                Check(UiTheme.Current.Id == "fixture" && UiTheme.Current.Background.ToArgb() == Color.FromArgb(13, 14, 16).ToArgb(), "Reload must refresh the selected custom resource");
                File.Delete(Path.Combine(folder, "fixture.json")); UiTheme.ReloadCustomThemes(folder);
                Check(UiTheme.Current.Id == "black" && changed == 3, "Removing the selected resource must notify and fall back to black");
            }
            finally { UiTheme.Changed -= handler; }
            foreach (string file in Directory.GetFiles(folder, "*.json")) File.Delete(file);
            for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(folder, "fixture-" + i + ".json"), Resource("fixture-" + i));
            errors = UiTheme.ReloadCustomThemes(folder);
            Check(UiTheme.Available.Count == 35 && errors.Length == 1, "Reload must stop after 32 local JSON resources");
            errors = UiTheme.ReloadCustomThemes("\\\\invalid-server\\share");
            Check(errors.Length == 1 && UiTheme.Available.Count == 3 && UiTheme.Current.Id == "black", "Network resource paths must be rejected before directory access");
            UiTheme.ReloadCustomThemes(Path.Combine(folder, "missing"));
            Check(UiTheme.Available.Count == 3 && UiTheme.Diagnostics.Count == 0, "A missing optional directory must keep built-ins without an error");
        }
        finally
        {
            string expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string actual = Path.GetFullPath(folder);
            if (actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(actual).StartsWith("DellFans-ThemeFixture-", StringComparison.Ordinal)) Directory.Delete(actual, true);
        }
        Console.WriteLine("PASS isolated file loading, UTF-8/BOM, invalid resources, selected-theme reload/removal and 32-file cap");
    }
    // JavaScriptSerializer throws FormatException (not ArgumentException) for malformed \u escapes.
    static void MalformedEscapesSkipOnlyTheirFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DellFans-ThemeEscape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "bad-escape.json"), "{\"schemaVersion\":1,\"id\":\"bad-escape\",\"name\":\"\\u00\",\"base\":\"white\"}");
            File.WriteAllText(Path.Combine(folder, "bad-hex.json"), "{\"schemaVersion\":1,\"id\":\"bad-hex\",\"name\":\"\\uZZZZ\",\"base\":\"white\"}");
            File.WriteAllText(Path.Combine(folder, "good.json"), Resource("good"));
            string[] errors = null;
            try { errors = UiTheme.ReloadCustomThemes(folder); }
            catch (Exception e) { Check(false, "A malformed escape escaped the theme loader as " + e.GetType().Name); }
            Check(errors.Length == 2, "Each malformed theme file must produce exactly one diagnostic, got " + errors.Length);
            Check(UiTheme.Available.Count == 4 && UiTheme.Available.Any(p => p.Id == "good"), "The valid sibling theme must still load");
            IgnoredFile[] ignored = UiText.IgnoredFiles;
            Check(ignored.Any(i => i.FileName == "bad-escape.json" && i.Reason.Length > 0) && ignored.Any(i => i.FileName == "bad-hex.json"), "Both bad files must be listed with a reason");
            Check(!ignored.Any(i => i.FileName == "good.json"), "A valid file must not be listed");
            UiTheme.ReloadCustomThemes(Path.Combine(folder, "missing"));
            Check(UiText.IgnoredFiles.All(i => i.FileName != "bad-escape.json"), "A clean reload must clear the theme entries");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS malformed \\u escapes skip only their theme file and are listed");
    }
    [STAThread]
    static int Main(string[] args)
    {
        try { Builtins(); CustomPalette(); RejectedResources(); ShadowAndRadius(); TemplateResources(args[0]); IsolatedDirectory(); MalformedEscapesSkipOnlyTheirFile(); Console.WriteLine("PASS " + passed + " theme assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
    }
}

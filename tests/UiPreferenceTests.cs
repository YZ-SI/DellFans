using System;
using System.Linq;
using DellFans;
using Microsoft.Win32;

// Preferences live in HKCU\Software\DellFans\Ui; every case here uses a temporary subkey and deletes it.
internal static class UiPreferenceTests
{
    static int passed;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static string root;
    static UiRegistry Fresh()
    {
        root = @"Software\DellFans-PrefTest-" + Guid.NewGuid().ToString("N");
        UiRegistry registry = new UiRegistry(root + @"\Ui", root + @"\Run");
        UiPreferences.Registry = registry; UiTheme.Select("black"); UiText.Select("zh-CN"); UiText.SetIgnored("preferences", null);
        return registry;
    }
    static void Cleanup() { Registry.CurrentUser.DeleteSubKeyTree(root, false); UiPreferences.Registry = null; UiTheme.Select("black"); UiText.SetIgnored("preferences", null); }
    static void Defaults()
    {
        UiRegistry registry = Fresh();
        try
        {
            UiPreferences.Load();
            Check(UiTheme.Current.Id == "black" && UiPreferences.PreferredLanguage == null && UiPreferences.DockVisible && !UiPreferences.StartupEnabled, "empty registry: black skin, system language, deck on, no sign-in start");
            Check(UiText.IgnoredFiles.Length == 0, "an empty registry must not report ignored values");
            Check(registry.GetString("Theme") == null, "loading must not write anything");
        }
        finally { Cleanup(); }
        Console.WriteLine("PASS defaults and read-only load");
    }
    static void ThemeRoundTrip()
    {
        UiRegistry registry = Fresh();
        try
        {
            foreach (string id in new[] { "white", "purple-black", "black" })
            {
                UiPreferences.SelectTheme(id);
                Check(registry.GetString("Theme") == id, "Theme value must be the Rust-compatible id: " + id);
                UiTheme.Select(id == "white" ? "black" : "white"); UiPreferences.Load();
                Check(UiTheme.Current.Id == id, "Load must restore " + id);
            }
            bool rejected = false;
            try { UiPreferences.SelectTheme("../x"); } catch (ArgumentException) { rejected = true; }
            Check(rejected && registry.GetString("Theme") == "black", "an unknown skin must be refused and not stored");
            registry.SetString("Theme", "neon"); UiTheme.Select("white"); UiPreferences.Load();
            Check(UiTheme.Current.Id == "white" && UiText.IgnoredFiles.Any(f => f.Reason.Contains("neon")), "an unknown stored skin is ignored and reported, the current skin is kept");
            registry.SetDword("Theme", 7); UiPreferences.Load();
            Check(UiText.IgnoredFiles.Length == 0, "a value of the wrong type is treated as absent");
        }
        finally { Cleanup(); }
        Console.WriteLine("PASS Theme string round trip (white | black | purple-black) and bad values");
    }
    static void LanguageRoundTrip()
    {
        UiRegistry registry = Fresh();
        try
        {
            string[,] cases = { { "zh-Hans", "zh-CN", "zh-Hans" }, { "zh-TW", "zh-TW", "zh-Hant" }, { "zh-HK", "zh-TW", "zh-Hant" }, { "en-US", "en", "en" }, { "en", "en", "en" } };
            for (int i = 0; i < cases.GetLength(0); i++)
            {
                UiPreferences.SelectLanguage(cases[i, 0]);
                Check(registry.GetString("Language") == cases[i, 1] && UiText.Language == cases[i, 2], "SelectLanguage(" + cases[i, 0] + ") stores " + cases[i, 1] + " and applies " + cases[i, 2]);
                UiText.Select("en"); UiPreferences.Load(); UiText.Select(UiPreferences.PreferredLanguage ?? "en");
                Check(UiPreferences.PreferredLanguage == cases[i, 1] && UiText.Language == cases[i, 2], "Load restores " + cases[i, 1]);
            }
            UiPreferences.SelectLanguage(null);
            Check(registry.GetString("Language") == "" && UiPreferences.PreferredLanguage == null, "clearing the language falls back to the system language");
            registry.SetString("Language", new string('x', 90)); UiPreferences.Load();
            Check(UiPreferences.PreferredLanguage == null && UiText.IgnoredFiles.Length == 1, "an over-long language value is ignored and reported");
        }
        finally { Cleanup(); }
        Console.WriteLine("PASS Language string round trip (zh-CN | zh-TW | en) and bad values");
    }
    static void DeckAndStartup()
    {
        UiRegistry registry = Fresh();
        try
        {
            UiPreferences.DockVisible = false; Check(!UiPreferences.DockVisible && registry.GetDword("DockVisible") == 0, "deck off = DockVisible 0");
            UiPreferences.DockVisible = true; Check(UiPreferences.DockVisible && registry.GetDword("DockVisible") == 1, "deck on = DockVisible 1");
            bool missing = false;
            try { UiPreferences.SetStartup(true); } catch (System.IO.FileNotFoundException) { missing = true; }
            Check(missing && !UiPreferences.StartupEnabled, "without DellFans.Tray.exe next to the program the switch fails and writes nothing");
            registry.SetStartup(true, @"C:\Program Files\DellFans\DellFans.Tray.exe");
            Check(UiPreferences.StartupEnabled, "the Run value DellFans.Tray reads as enabled");
            UiPreferences.SetStartup(false); Check(!UiPreferences.StartupEnabled, "turning it off removes the Run value");
        }
        finally { Cleanup(); }
        Console.WriteLine("PASS deck visibility and sign-in start use the tray's registry values");
    }
    static int Main()
    {
        try { Defaults(); ThemeRoundTrip(); LanguageRoundTrip(); DeckAndStartup(); Console.WriteLine("RESULT " + passed + " checks passed; temporary HKCU subkeys only"); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
    }
}

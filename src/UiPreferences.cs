using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DellFans
{
    // Theme, language and deck visibility live in HKCU\Software\DellFans\Ui, shared with the Rust tray; the Run value DellFans.Tray is the sign-in switch.
    public static class UiPreferences
    {
        static UiRegistry registry = new UiRegistry();
        public static string PreferredLanguage { get; private set; }
        // Tests inject a registry that points at temporary subkeys.
        public static UiRegistry Registry { get { return registry; } set { registry = value ?? new UiRegistry(); } }
        public static string DirectoryPath { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DellFans"); } }
        public static void Load()
        {
            PreferredLanguage = null; UiText.SetIgnored("preferences", null);
            // An unusable value is skipped on its own (defaults apply) and reported; nothing else is affected.
            try
            {
                string reason = TryLoad();
                if (reason != null) UiText.SetIgnored("preferences", new[] { new IgnoredFile { FileName = "HKCU\\Software\\DellFans\\Ui", Reason = reason } });
            }
            catch (Exception e)
            { UiText.SetIgnored("preferences", new[] { new IgnoredFile { FileName = "HKCU\\Software\\DellFans\\Ui", Reason = e.GetBaseException().Message } }); }
        }
        static string TryLoad()
        {
            string reason = null, theme = registry.GetString("Theme"), language = registry.GetString("Language");
            if (!string.IsNullOrEmpty(theme))
            {
                bool known = false; foreach (UiPalette p in UiTheme.Available) if (p.Id == theme) known = true;
                if (known) UiTheme.Select(theme); else reason = "Unknown theme: " + Clip(theme);
            }
            if (!string.IsNullOrEmpty(language))
            {
                if (language.Length > 85) reason = "Invalid language value.";
                else
                    try { PreferredLanguage = ToStored(language); }
                    catch (CultureNotFoundException) { reason = "Unknown language: " + Clip(language); }
            }
            return reason;
        }
        static string Clip(string value) { return value.Length > 40 ? value.Substring(0, 40) : value; }
        // The tray reads zh-CN | zh-TW | en; other cultures (external packs) are stored by culture name.
        static string ToStored(string culture)
        {
            string name = new CultureInfo(culture).Name;
            string resolved = UiText.Resolve(name);
            return resolved == "zh-Hans" ? "zh-CN" : resolved == "zh-Hant" ? "zh-TW" : resolved;
        }
        public static void SelectTheme(string id)
        {
            if (!UiTheme.Select(id)) throw new ArgumentException("Unknown theme.", "id");
            registry.SetString("Theme", id);
        }
        public static void SelectLanguage(string culture)
        {
            string stored = String.IsNullOrEmpty(culture) ? null : ToStored(culture);
            PreferredLanguage = stored;
            registry.SetString("Language", stored ?? "");
            UiText.Select(stored ?? CultureInfo.CurrentUICulture.Name);
        }
        public static bool DockVisible { get { return registry.DockVisible; } set { registry.DockVisible = value; } }
        public static bool StartupEnabled { get { return registry.StartupEnabled; } }
        public static void SetStartup(bool enabled)
        {
            if (enabled && !System.IO.File.Exists(UiRegistry.TrayExePath)) throw new System.IO.FileNotFoundException("DellFans.Tray.exe was not found next to DellFans.exe.", UiRegistry.TrayExePath);
            registry.SetStartup(enabled, UiRegistry.TrayExePath);
            if (registry.StartupEnabled != enabled) throw new InvalidOperationException("Sign-in startup verification failed.");
        }
        public static void OpenDirectory(string directory)
        { try { System.IO.Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); } catch (Exception e) { UiDialogs.ShowMessage(UiText.Error(e.GetBaseException().Message), "DellFans"); } }
    }
}

using System;
using System.IO;
using Microsoft.Win32;

namespace DellFans
{
    // The per-user values the Rust tray also reads: HKCU\Software\DellFans\Ui (DockVisible, Theme, Language) and the Run value DellFans.Tray.
    // Tests inject temporary subkeys; production uses the defaults.
    public sealed class UiRegistry
    {
        public const string DefaultUiKey = @"Software\DellFans\Ui", DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValueName = "DellFans.Tray";
        readonly string uiKey, runKey;
        public UiRegistry() : this(DefaultUiKey, DefaultRunKey) { }
        public UiRegistry(string uiKey, string runKey)
        {
            if (string.IsNullOrEmpty(uiKey) || string.IsNullOrEmpty(runKey)) throw new ArgumentException("Registry subkeys are required.");
            this.uiKey = uiKey; this.runKey = runKey;
        }
        public static string TrayExePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DellFans.Tray.exe"); } }
        object Read(string subKey, string name)
        { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey)) return key == null ? null : key.GetValue(name); }
        void Write(string subKey, string name, object value, RegistryValueKind kind)
        { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey)) key.SetValue(name, value, kind); }
        public int? GetDword(string name) { object value = Read(uiKey, name); return value is int ? (int?)(int)value : null; }
        public void SetDword(string name, int value) { Write(uiKey, name, value, RegistryValueKind.DWord); }
        public string GetString(string name) { return Read(uiKey, name) as string; }
        public void SetString(string name, string value) { Write(uiKey, name, value, RegistryValueKind.String); }
        // Absent = on.
        public bool DockVisible { get { int? value = GetDword("DockVisible"); return !value.HasValue || value.Value != 0; } set { SetDword("DockVisible", value ? 1 : 0); } }
        public bool StartupEnabled { get { string value = Read(runKey, RunValueName) as string; return !string.IsNullOrEmpty(value); } }
        public void SetStartup(bool enabled, string trayExe)
        {
            if (enabled)
            {
                if (string.IsNullOrEmpty(trayExe) || trayExe.IndexOf('"') >= 0 || !Path.IsPathRooted(trayExe)) throw new ArgumentException("A fully qualified tray executable path is required.", "trayExe");
                Write(runKey, RunValueName, "\"" + trayExe + "\"", RegistryValueKind.String);
            }
            else using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKey, true)) { if (key != null) key.DeleteValue(RunValueName, false); }
        }
    }
}

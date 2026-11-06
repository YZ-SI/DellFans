using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DellFans
{
    // Starts the pieces of the portable package that run next to DellFans.exe: the elevated background controller and the tray.
    public static class BackgroundHost
    {
        // Tests replace the elevated start; it then never shows a UAC prompt or starts a real controller.
        internal static Func<bool> StartOverride = null;
        internal static Action TrayOverride = null;
        public static bool Running() { return ControlPipeClient.PipeExists(ServicePaths.PipeName); }
        // DellFans.Service.exe --portable, elevated (UAC). False when the user declined the prompt.
        public static bool Start()
        {
            if (StartOverride != null) return StartOverride();
            ProcessStartInfo start = new ProcessStartInfo(ControlPipeClient.HostImage, "--portable")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
            try { using (Process.Start(start)) { } return true; }
            catch (Win32Exception e) { if (e.NativeErrorCode == 1223) return false; throw; } // ERROR_CANCELLED: the user said no
        }
        // The tray allows one instance per session, so starting it again is harmless.
        public static void StartTray()
        {
            if (TrayOverride != null) { TrayOverride(); return; }
            string tray = UiRegistry.TrayExePath;
            if (!File.Exists(tray)) return;
            try { using (Process.Start(new ProcessStartInfo(tray) { UseShellExecute = false, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory })) { } }
            catch (Win32Exception) { } // the window works without the tray
        }
    }
}

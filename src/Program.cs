using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace DellFans
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            int language = Array.IndexOf(args, "--language");
            string requestedLanguage = null;
            if (language >= 0)
            {
                if (language + 1 >= args.Length) return 1;
                requestedLanguage = args[language + 1];
                args = args.Where((value, index) => index != language && index != language + 1).ToArray();
            }
            if (args.Length == 1 && args[0] == "--portable") return RunHost();
            if (args.Length == 2 && args[0] == "--watchdog") return RunWatchdog(args[1]);
            if (args.Length == 2 && args[0] == "--recover") return RunRecover(args[1]);
            return RunInteractive(args, requestedLanguage);
        }
        // Each entry path is its own non-inlined method so JIT-compiling Main never loads WinForms or
        // System.Drawing into the service and watchdog processes.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunHost() { return PortableHost.Run(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunWatchdog(string session)
        {
            UseServiceReports(session);
            try { return FanWatchdog.Run(session); } catch { return 1; }
        }
        // One recovery pass for an external watchdog: 0 recovered or nothing to do, anything else means retry.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunRecover(string session)
        {
            UseServiceReports(session);
            try { return FanWatchdog.Recover(session); } catch { return 1; }
        }
        static void UseServiceReports(string session)
        { ReportFiles.ServiceMode = Path.GetFullPath(session).StartsWith(Path.GetFullPath(ServicePaths.Reports) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int RunInteractive(string[] args, string requestedLanguage)
        {
            if(requestedLanguage!=null) UiText.Select(requestedLanguage);
            if (Array.IndexOf(args, "--render-preview") >= 0 && Array.IndexOf(args, "--input") < 0) return RenderPreviews(args);
            bool created;
            bool driverConsent = args.Length == 1 && args[0] == "--sensor-driver"; // from the tray: home page with the PawnIO consent dialog
            bool gui = args.Length == 0 || driverConsent || (args.Length == 2 && args[0] == "--gui-rpm");
            using (EventWaitHandle showSignal = gui ? CreateShowSignal() : null)
            using (Mutex instance = new Mutex(true, gui ? @"Local\DellFans.Window.v2" : null, out created))
            {
                if (!created)
                {
                    if (args.Length == 0 || driverConsent) { showSignal.Set(); return 0; }
                    else Console.Error.WriteLine("Another instance is running; use its controls or close it first.");
                    return 2;
                }
                try
                {
                    // The interface and the other commands run as the invoking user; the service decides what each caller may do.
                    if (RequiresAdministrator(args) && !(new WindowsPrincipal(WindowsIdentity.GetCurrent())).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new UnauthorizedAccessException("此命令需要管理员身份，请以管理员身份运行。");
                    // From the tray at sign-in: start the elevated controller (UAC) without opening a window.
                    if (args.Length == 1 && args[0] == "--start-background") return BackgroundHost.Running() || BackgroundHost.Start() ? 0 : 1;
                    // Sign-in startup is the tray's Run value.
                    if (args.Length == 2 && args[0] == "--startup" && (args[1] == "enable" || args[1] == "disable"))
                    {
                        bool enable = args[1] == "enable";
                        UiRegistry registry = new UiRegistry();
                        registry.SetStartup(enable, UiRegistry.TrayExePath);
                        if (registry.StartupEnabled != enable) throw new InvalidOperationException("Sign-in startup verification failed.");
                        return 0;
                    }
                    if (gui)
                    {
                        UiText.InitializePacks();
                        UiTheme.ReloadCustomThemes();
                        UiPreferences.Load();
                        UiText.Select(requestedLanguage ?? UiPreferences.PreferredLanguage ?? CultureInfo.CurrentUICulture.Name);
                        int? startupRpm = args.Length == 2 ? (int?)int.Parse(args[1], CultureInfo.InvariantCulture) : null;
                        if (startupRpm.HasValue) RpmRange.Validate(startupRpm.Value);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        // The window is on demand: closing it ends the process; the tray and the service keep running.
                        using (ThermalForm panel = startupRpm.HasValue ? new ThermalForm(startupRpm.Value) : new ThermalForm { StartWithDriverConsent = driverConsent })
                        {
                            RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { ShowWindow(panel); }, null, Timeout.Infinite, false);
                            try
                            {
                                Application.Run(panel);
                            }
                            finally { wait.Unregister(null); }
                        }
                        return 0;
                    }
                    return ClientCommands.Run(args);
                }
                catch (Exception e)
                {
                    string message = e.GetBaseException().Message;
                    if (gui) UiDialogs.ShowMessage(UiText.Error(message),UiText.T("启动失败"));
                    else Console.Error.WriteLine(message);
                    try { ReportFiles.Write(Path.Combine(ReportFiles.ReportsDirectory, "last-error.json"),
                        new { TimeUtc = DateTime.UtcNow.ToString("o"), Error = e.ToString() }); } catch { }
                    return 1;
                }
            }
        }
        // Offline: every page x skin x language with made-up readings; no service connection.
        static int RenderPreviews(string[] args)
        {
            int output = Array.IndexOf(args, "--output"), dpi = Array.IndexOf(args, "--dpi");
            string folder = output >= 0 && output + 1 < args.Length ? args[output + 1] : Path.Combine("docs", "screenshots");
            string[] steps = dpi >= 0 && dpi + 1 < args.Length ? args[dpi + 1].Split(',') : new[] { "100", "150" };
            float[] scales = steps.Select(s => int.Parse(s, CultureInfo.InvariantCulture) / 100f).ToArray();
            try { Console.WriteLine("Rendered " + PreviewRender.RenderAll(Path.GetFullPath(folder), scales) + " previews to " + Path.GetFullPath(folder)); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.GetBaseException().Message + Environment.NewLine + e.StackTrace); return 1; }
        }
        static void ShowWindow(ThermalForm panel)
        {
            try { if (!panel.IsDisposed && panel.IsHandleCreated) panel.BeginInvoke((MethodInvoker)panel.ShowFront); }
            catch (InvalidOperationException) { }
        }
        // Only the commands the service itself restricts to Administrators (see ControlPolicy) keep a client-side check.
        internal static bool RequiresAdministrator(string[] args)
        { return Array.IndexOf(args, "--configure") >= 0 || Array.IndexOf(args, "--test-fan") >= 0; }
        static EventWaitHandle CreateShowSignal()
        {
            EventWaitHandleSecurity security = new EventWaitHandleSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            bool created;
            return new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DellFans.ShowWindow.v2", out created, security);
        }
    }
}

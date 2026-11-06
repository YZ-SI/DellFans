using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DellFans;
using Microsoft.Win32;

// Opens and closes the real window many times (pages, skins, languages) against OfflineThermalClient and checks that GDI objects,
// USER objects and handles do not grow, that closing ends the message loop, and reports the private memory with each page open.
internal static class ResourceHygieneTests
{
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [StructLayout(LayoutKind.Sequential)] struct MemoryCounters { public uint Size, PageFaults; public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonPagedPool, QuotaNonPagedPool, Pagefile, PeakPagefile, Private; }
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    static int passed;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static void Pump(int milliseconds)
    {
        Stopwatch time = Stopwatch.StartNew();
        while (time.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    static void Cycle(int index)
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = new ThermalForm(client))
        {
            form.Show();
            DashboardSurface s = form.Surface;
            Stopwatch wait = Stopwatch.StartNew(); while (!s.CanWrite && wait.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(s.CanWrite, "offline service did not connect in cycle " + index);
            string[] themes = { "white", "black", "purple-black" }, languages = { "lang:zh-CN", "lang:zh-TW", "lang:en" };
            s.Activate("theme:" + themes[index % 3]); s.Activate(languages[index % 3]);
            foreach (string page in new[] { "nav:details", "nav:settings", "nav:home" }) { s.Activate(page); form.Refresh(); Application.DoEvents(); }
            s.Activate("mode:custom"); form.Refresh(); s.Activate("mode:quiet"); Pump(30);
            form.Close();
        }
        client.Dispose();
    }
    static void Settle() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); Application.DoEvents(); Thread.Sleep(20); } }
    static void NoGrowth()
    {
        IntPtr process = GetCurrentProcess();
        for (int i = 0; i < 6; i++) Cycle(i); // warm-up: JIT, font cache, first-use handles
        Settle();
        uint gdi = GetGuiResources(process, 0), user = GetGuiResources(process, 1); int handles = Process.GetCurrentProcess().HandleCount; int fonts = UiTheme.CachedFontCount;
        for (int i = 0; i < 20; i++) Cycle(i);
        Settle();
        uint gdi2 = GetGuiResources(process, 0), user2 = GetGuiResources(process, 1); int handles2 = Process.GetCurrentProcess().HandleCount;
        Console.WriteLine("HANDLES gdi " + gdi + " -> " + gdi2 + ", user " + user + " -> " + user2 + ", handles " + handles + " -> " + handles2 + ", cached fonts " + fonts + " -> " + UiTheme.CachedFontCount);
        Check(gdi2 <= gdi + 2, "GDI objects grew over 20 open/close cycles: " + gdi + " -> " + gdi2);
        Check(user2 <= user + 2, "USER objects grew over 20 open/close cycles: " + user + " -> " + user2);
        Check(handles2 <= handles + 8, "handles grew over 20 open/close cycles: " + handles + " -> " + handles2);
        Check(UiTheme.CachedFontCount <= fonts + 12, "the font cache grew without bound");
        Console.WriteLine("PASS 20 open/close/theme/language cycles: no GDI, USER or handle growth");
    }
    static void CloseEndsTheMessageLoop()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        bool closed = false; ThermalForm form = new ThermalForm(client);
        form.Shown += delegate { form.BeginInvoke((MethodInvoker)delegate { form.Close(); }); };
        form.FormClosed += delegate { closed = true; };
        Stopwatch time = Stopwatch.StartNew();
        System.Windows.Forms.Timer guard = new System.Windows.Forms.Timer { Interval = 6000 }; guard.Tick += delegate { Application.ExitThread(); }; guard.Start();
        Application.Run(form); guard.Dispose();
        Check(closed && time.ElapsedMilliseconds < 5500, "closing the window must end Application.Run (the process exits)");
        form.Dispose();
        Console.WriteLine("PASS closing the window ends the message loop; nothing stays resident");
    }
    static void ShowFrontRestores()
    {
        using (ThermalForm form = new ThermalForm(new OfflineThermalClient()))
        {
            form.Show(); form.WindowState = FormWindowState.Minimized; Application.DoEvents();
            Check(form.PollInterval == 10000, "a minimized window polls slowly");
            form.ShowFront(); Application.DoEvents();
            Check(form.WindowState == FormWindowState.Normal && form.Visible && form.PollInterval <= 4000, "the show signal restores a minimized window");
        }
        Console.WriteLine("PASS the show signal restores a minimized window");
    }
    static void Memory()
    {
        Settle();
        Process me = Process.GetCurrentProcess();
        double baseline = Private(me);
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = new ThermalForm(client))
        {
            form.Show(); DashboardSurface s = form.Surface;
            Stopwatch wait = Stopwatch.StartNew(); while (!s.CanWrite && wait.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            string line = "";
            foreach (string page in new[] { "nav:home", "nav:details", "nav:settings" })
            {
                s.Activate(page); form.Refresh(); Pump(300); Settle();
                double value = Private(me); line += page.Substring(4) + " " + value.ToString("0.0") + " MiB, ";
                Check(value < 80, "private memory with the window open is far above the 40 MiB target on " + page + ": " + value);
            }
            Console.WriteLine("MEMORY private (test harness, offline client): before window " + baseline.ToString("0.0") + " MiB; open: " + line.TrimEnd(',', ' '));
        }
        Console.WriteLine("PASS private memory with each page open (see MEMORY line)");
    }
    static double Private(Process process) { MemoryCounters c; GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)Marshal.SizeOf(typeof(MemoryCounters))); return (double)c.Private / 1048576.0; }
    [STAThread]
    static int Main()
    {
        string root = @"Software\DellFans-HygieneTest-" + Guid.NewGuid().ToString("N");
        UiPreferences.Registry = new UiRegistry(root + @"\Ui", root + @"\Run");
        try
        {
            Application.EnableVisualStyles();
            Memory(); NoGrowth(); CloseEndsTheMessageLoop(); ShowFrontRestores();
            Console.WriteLine("RESULT " + passed + " checks passed; offline client, temporary HKCU subkeys");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); UiPreferences.Registry = null; }
    }
}

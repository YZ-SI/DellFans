using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DellFans;

// Dev tool, not a test: hosts the real ThermalForm against OfflineThermalClient (never the service pipe) and saves what is actually on screen,
// once per skin and page, as a screen grab (with the shadow) and a PrintWindow of the window itself. Arguments: output folder.
internal static class CaptureWindow
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); } }
    static void Grab(Form form, string path, bool screen)
    {
        Rectangle r = screen ? Rectangle.Inflate(form.Bounds, 20, 20) : new Rectangle(Point.Empty, form.Size);
        using (Bitmap bitmap = new Bitmap(r.Width, r.Height))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            if (screen) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            else { g.Clear(Color.Magenta); IntPtr dc = g.GetHdc(); try { PrintWindow(form.Handle, dc, 2); } finally { g.ReleaseHdc(dc); } }
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        string folder = Path.GetFullPath(args.Length > 0 ? args[0] : "window-captures"); Directory.CreateDirectory(folder);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        UiText.Select("zh-CN");
        foreach (string skin in new[] { "white", "black", "purple-black" })
        {
            UiTheme.Select(skin);
            OfflineThermalClient client = new OfflineThermalClient { Sample = PreviewRender.Fixture(DateTime.UtcNow) };
            using (ThermalForm form = new ThermalForm(client))
            {
                form.Show(); form.Activate(); SetForegroundWindow(form.Handle);
                for (int i = 0; i < 200 && !form.Surface.CanWrite; i++) Pump(15);
                PreviewRender.Decorate(form.Surface);
                foreach (string page in new[] { "home", "details", "settings" })
                {
                    form.Surface.Activate("nav:" + page); form.Refresh(); Pump(400);
                    Grab(form, Path.Combine(folder, "window-" + skin + "-" + page + "-screen.png"), true);
                    Grab(form, Path.Combine(folder, "window-" + skin + "-" + page + "-printwindow.png"), false);
                }
                form.Surface.Activate("nav:home"); form.Surface.Activate("cap:max"); form.Refresh(); Pump(600);
                Grab(form, Path.Combine(folder, "window-" + skin + "-maximized-screen.png"), true);
            }
        }
        Console.WriteLine("Captured to " + folder);
        return 0;
    }
}

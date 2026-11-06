using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DellFans;

// The borderless main window against OfflineThermalClient (no service, no pipe): no native frame or caption anywhere, the painted caption still drags,
// snaps and resizes through WM_NCHITTEST, and minimise / maximise / restore / close keep working.
internal static class FrameTests
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    static int passed;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); } }
    static IntPtr Position(Point p) { return new IntPtr(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)); }
    static int HitTest(IntPtr window, Point screen) { return (int)(long)SendMessage(window, 0x84, IntPtr.Zero, Position(screen)); }
    static void WholeClient(Form form, string phase)
    {
        Rect client, window; GetClientRect(form.Handle, out client); GetWindowRect(form.Handle, out window);
        Check(client.Right - client.Left == window.Right - window.Left && client.Bottom - client.Top == window.Bottom - window.Top, phase + ": a native frame reserves space (client != window)");
        Check((GetWindowLong(form.Handle, -16) & 0x00C00000) == 0, phase + ": WS_CAPTION / WS_BORDER is set");
    }
    static ThermalForm Open(OfflineThermalClient client)
    {
        ThermalForm form = new ThermalForm(client); form.Show();
        for (int i = 0; i < 300 && !form.Surface.CanWrite; i++) { Application.DoEvents(); Thread.Sleep(15); }
        Check(form.Surface.CanWrite, "offline service did not connect"); form.Refresh(); return form;
    }
    static Point ScreenOf(ThermalForm form, RectangleF logical)
    {
        float s = form.DeviceDpi / 96f;
        return form.Surface.PointToScreen(new Point((int)((logical.X + logical.Width / 2) * s), (int)((logical.Y + logical.Height / 2) * s)));
    }
    static RectangleF HitRect(ThermalForm form, string id) { return form.Surface.Hits.First(h => h.Id == id).Rect; }
    static void NoNativeFrame()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = Open(client))
        {
            Check(form.FormBorderStyle == FormBorderStyle.None, "the window is borderless");
            WholeClient(form, "created");
            IntPtr rect = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Rect)));
            try
            {
                Marshal.StructureToPtr(new Rect { Left = 0, Top = 0, Right = 520, Bottom = 320 }, rect, false);
                SendMessage(form.Handle, 0x83, IntPtr.Zero, rect);
                Rect after = (Rect)Marshal.PtrToStructure(rect, typeof(Rect));
                Check(after.Left == 0 && after.Top == 0 && after.Right == 520 && after.Bottom == 320, "WM_NCCALCSIZE keeps the whole rectangle as client area");
            }
            finally { Marshal.FreeHGlobal(rect); }
            for (int i = 0; i < 3; i++) { SendMessage(form.Handle, 0x86, IntPtr.Zero, IntPtr.Zero); SendMessage(form.Handle, 0x86, (IntPtr)1, IntPtr.Zero); SendMessage(form.Handle, 0x85, (IntPtr)1, IntPtr.Zero); form.Refresh(); Application.DoEvents(); }
            WholeClient(form, "repeated activation and paint");
            Check((GetWindowLong(form.Handle, -16) & 0x00040000) != 0 && (GetWindowLong(form.Handle, -16) & 0x00080000) != 0, "sizing border and system menu remain for Snap and Alt+Space");
            Check(form.Surface.Hits.Any(h => h.Id == "cap:close") && form.Surface.Hits.Any(h => h.Id == "cap:min") && form.Surface.Hits.Any(h => h.Id == "cap:max"), "the surface paints the three window buttons");
        }
        Console.WriteLine("PASS borderless: no WS_CAPTION, client == window, NCCALCSIZE and repeated activation leave no native frame");
    }
    static void HitTests()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = Open(client))
        {
            Rectangle w = form.Bounds; float s = form.DeviceDpi / 96f;
            Point drag = new Point(w.Left + (int)(500 * s), w.Top + (int)(40 * s));
            Check(form.Surface.HitIdAt(form.Surface.PointToClient(drag)) == null, "test point is empty header space");
            Check(HitTest(form.Surface.Handle, drag) == -1, "the surface lets the caption through (HTTRANSPARENT)");
            Check(HitTest(form.Handle, drag) == 2, "empty header space is a native caption (drag, snap, double-click)");
            foreach (string id in new[] { "cap:min", "cap:max", "cap:close", "nav:details" })
            {
                Point at = ScreenOf(form, HitRect(form, id));
                Check(HitTest(form.Surface.Handle, at) == 1 && HitTest(form.Handle, at) == 1, id + " is client area, not draggable");
            }
            Point body = new Point(w.Left + w.Width / 2, w.Top + w.Height / 2);
            Check(HitTest(form.Handle, body) == 1, "card area is client");
            Check(HitTest(form.Handle, new Point(w.Left + 2, w.Top + w.Height / 2)) == 10, "left edge sizes");
            Check(HitTest(form.Handle, new Point(w.Right - 3, w.Top + w.Height / 2)) == 11, "right edge sizes");
            Check(HitTest(form.Handle, new Point(w.Left + w.Width / 2, w.Top + 2)) == 12, "top edge sizes");
            Check(HitTest(form.Handle, new Point(w.Left + w.Width / 2, w.Bottom - 3)) == 15, "bottom edge sizes");
            Check(HitTest(form.Handle, new Point(w.Left + 3, w.Top + 3)) == 13 && HitTest(form.Handle, new Point(w.Right - 4, w.Top + 3)) == 14, "top corners size diagonally");
            Check(HitTest(form.Handle, new Point(w.Left + 3, w.Bottom - 4)) == 16 && HitTest(form.Handle, new Point(w.Right - 4, w.Bottom - 4)) == 17, "bottom corners size diagonally");
            Check(HitTest(form.Surface.Handle, new Point(w.Left + 2, w.Top + w.Height / 2)) == -1, "the surface lets the sizing edge through");
        }
        Console.WriteLine("PASS hit tests: caption drags, buttons and tabs are client, edges and corners resize");
    }
    static void WindowOperations()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        using (ThermalForm form = Open(client))
        {
            Rectangle work = Screen.FromControl(form).WorkingArea;
            form.Surface.Activate("cap:min"); Application.DoEvents();
            Check(form.WindowState == FormWindowState.Minimized, "the minimise button minimises");
            form.ShowFront(); Application.DoEvents(); Check(form.WindowState == FormWindowState.Normal, "restore from minimised"); WholeClient(form, "minimise/restore");
            form.Surface.Activate("cap:max"); Pump(100);
            Check(form.WindowState == FormWindowState.Maximized, "the maximise button maximises");
            Check(Math.Abs(form.Bounds.Left - work.Left) <= 1 && Math.Abs(form.Bounds.Top - work.Top) <= 1 && Math.Abs(form.Bounds.Width - work.Width) <= 1 && Math.Abs(form.Bounds.Height - work.Height) <= 1,
                "maximised fills the work area exactly: " + form.Bounds + " vs " + work);
            WholeClient(form, "maximised"); form.Refresh();
            Check(form.Surface.AccessibleNameFor("cap:max") == UiText.T("还原"), "the maximise button turns into restore");
            Check(HitTest(form.Handle, new Point(form.Left + 1, form.Top + form.Height / 2)) != 10, "no sizing edges while maximised");
            form.Surface.Activate("cap:max"); Pump(100);
            Check(form.WindowState == FormWindowState.Normal, "the button restores"); WholeClient(form, "restored");
            IntPtr point = new IntPtr((((form.Top + 40) & 0xFFFF) << 16) | ((form.Left + 500) & 0xFFFF));
            SendMessage(form.Handle, 0xA3, (IntPtr)2, point); Pump(100);
            Check(form.WindowState == FormWindowState.Maximized, "caption double-click maximises (native)");
            SendMessage(form.Handle, 0xA3, (IntPtr)2, point); Pump(100);
            Check(form.WindowState == FormWindowState.Normal, "second caption double-click restores");
            Check(form.ShowInTaskbar && form.Text == "DellFans" && form.Icon != null, "taskbar title and icon");
        }
        Console.WriteLine("PASS minimise / maximise to work area / restore / caption double-click keep their native behaviour");
    }
    static void KeyboardAndClose()
    {
        OfflineThermalClient client = new OfflineThermalClient();
        ThermalForm form = Open(client);
        form.Surface.Focus();
        SendMessage(form.Surface.Handle, 0x100, (IntPtr)0x1B, IntPtr.Zero); SendMessage(form.Surface.Handle, 0x101, (IntPtr)0x1B, IntPtr.Zero); Application.DoEvents();
        Check(!form.IsDisposed && form.Visible, "Esc does not close the window");
        DashboardSurface s = form.Surface; s.SetFocusId("nav:home");
        for (int i = 0; i < s.Hits.Count && !(s.Hits[i].Id == "cap:close"); i++) SendMessage(s.Handle, 0x100, (IntPtr)0x09, IntPtr.Zero);
        AccessibleObject root = s.AccessibilityObject; string[] names = new string[root.GetChildCount()];
        for (int i = 0; i < names.Length; i++) names[i] = root.GetChild(i).Name;
        foreach (string expected in new[] { UiText.T("最小化"), UiText.T("关闭"), UiText.T("最大化") }) Check(names.Contains(expected), "accessible name: " + expected);
        AccessibleObject close = Enumerable.Range(0, names.Length).Select(i => root.GetChild(i)).First(a => a.Name == UiText.T("关闭"));
        Check(close.Role == AccessibleRole.PushButton && !close.Bounds.IsEmpty, "the close button is an accessible push button with bounds");
        s.SetFocusId("cap:close"); SendMessage(s.Handle, 0x100, (IntPtr)0x0D, IntPtr.Zero); Application.DoEvents();
        Check(form.IsDisposed || !form.Visible, "the close button closes the window (the process exits)");
        form.Dispose();
        Console.WriteLine("PASS keyboard and accessibility: Esc keeps the window, caption buttons have names and roles, close ends the window");
    }
    [STAThread] static int Main()
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (string skin in new[] { "white", "black", "purple-black" })
            { UiTheme.Select(skin); UiText.Select("zh-CN"); NoNativeFrame(); HitTests(); WindowOperations(); KeyboardAndClose(); }
            Console.WriteLine("RESULT " + passed + " checks passed");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message + Environment.NewLine + e.StackTrace); return 1; }
    }
}

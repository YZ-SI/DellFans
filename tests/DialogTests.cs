using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DellFans;
using Microsoft.Win32;

// Offline: every dialog is a borderless skinned card (no live pipe, no real Explorer, no real export file; preferences in temporary HKCU subkeys).
internal static class DialogTests
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    static int passed;
    static readonly List<string> roots = new List<string>();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static void Until(Func<bool> ready, string message)
    {
        Stopwatch time = Stopwatch.StartNew();
        while (!ready() && time.ElapsedMilliseconds < 7000) { Application.DoEvents(); Thread.Sleep(15); }
        if (!ready()) throw new Exception(message);
    }
    static void UseTemporaryRegistry()
    {
        string root = @"Software\DellFans-DialogTest-" + Guid.NewGuid().ToString("N"); roots.Add(root);
        UiPreferences.Registry = new UiRegistry(root + @"\Ui", root + @"\Run");
    }
    static void Restore()
    {
        foreach (string root in roots) Registry.CurrentUser.DeleteSubKeyTree(root, false);
        roots.Clear(); UiPreferences.Registry = null; UiDialogs.Presenter = null; UiDialogs.ConfirmOverride = null; UiDialogs.DriverConsentOverride = null;
        UiTheme.Select("black"); UiText.Select("zh-CN");
    }
    static SkinnedDialog[] All(float? scale)
    {
        return new[] { UiDialogs.CreateConfirmAutomatic(scale), UiDialogs.CreateDriverConsent(scale), UiDialogs.CreateMessage("出错了：连接被拒绝。", "DellFans", scale),
            UiDialogs.CreateExportDone(@"C:\Users\Someone Long\AppData\Local\DellFans\Reports\dellfans-20260101-120000-000.json", scale) };
    }
    static void Borderless()
    {
        foreach (SkinnedDialog d in All(null))
            using (d)
            {
                d.Show(); Application.DoEvents();
                Check((GetWindowLong(d.Handle, -16) & 0x00C00000) == 0, d.Name + " has WS_CAPTION");
                Rect window, client; GetWindowRect(d.Handle, out window); GetClientRect(d.Handle, out client);
                Check(d.FormBorderStyle == FormBorderStyle.None && client.Right - client.Left == window.Right - window.Left && client.Bottom - client.Top == window.Bottom - window.Top, d.Name + " client rect must equal the window rect");
                Check(!d.ShowInTaskbar && !d.MinimizeBox && !d.MaximizeBox, d.Name + " is a plain dialog");
                Check(d.AccessibleRole == AccessibleRole.Dialog && !string.IsNullOrEmpty(d.AccessibleName), d.Name + " keeps its accessible role and name");
                Check(d.CloseCross.AccessibleName == UiText.T("关闭") && d.Buttons.All(b => !string.IsNullOrEmpty(b.AccessibleName) && b.AccessibleRole == AccessibleRole.Default), d.Name + " buttons keep accessible names");
            }
        Console.WriteLine("PASS every dialog is borderless (no WS_CAPTION, client rect == window rect), accessible");
    }
    static Color Pixel(Bitmap b, float x, float y) { return b.GetPixel((int)x, (int)y); }
    static bool Same(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }
    static void Skinned()
    {
        foreach (string theme in PreviewRender.Themes)
        {
            UiTheme.Select(theme); UiPalette p = UiTheme.Current;
            foreach (SkinnedDialog d in All(1f))
                using (d)
                {
                    using (Bitmap bitmap = d.Body())
                    {
                        Check(Same(Pixel(bitmap, d.Width - 74, SkinnedDialog.HeaderHeight / 2), p.Surface), d.Name + " header is the card colour in " + theme);
                        Check(Same(Pixel(bitmap, d.Width - 10, d.Height / 2), p.Surface) && Same(Pixel(bitmap, 8, d.Height - 8), p.Surface), d.Name + " body is the card colour in " + theme);
                        foreach (DialogButton b in d.Buttons)
                        {
                            Color got = Pixel(bitmap, b.Left + 8, b.Top + b.Height / 2), want = b.Primary ? p.Accent : p.SurfaceAlt;
                            Check(Same(got, want), d.Name + " " + b.Name + " fill in " + theme + ": " + got + " vs " + want);
                        }
                    }
                }
        }
        UiTheme.Select("black");
        Console.WriteLine("PASS header, body and primary/secondary buttons are painted in the palette of white, black and purple-black");
    }
    static bool Key(Form form, Keys key)
    { return (bool)typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { key }); }
    static void Behaviour()
    {
        // Enter and Esc take the safe button; clicking the primary button gives OK; the cross cancels.
        foreach (Keys key in new[] { Keys.Escape, Keys.Enter })
            using (SkinnedDialog d = UiDialogs.CreateConfirmAutomatic())
            { d.Show(); Application.DoEvents(); Key(d, key); Check(d.DialogResult == DialogResult.Cancel, "confirm: " + key + " cancels"); }
        using (SkinnedDialog d = UiDialogs.CreateConfirmAutomatic())
        { d.Show(); Application.DoEvents(); Check(d.ActiveControl == d.Buttons[1] && d.Buttons[0].Primary && !d.Buttons[1].Primary, "the safe button has the focus and is the secondary one"); d.Buttons[0].PerformClick(); Check(d.DialogResult == DialogResult.OK, "primary confirms"); }
        using (SkinnedDialog d = UiDialogs.CreateConfirmAutomatic())
        { d.Show(); Application.DoEvents(); d.CloseCross.PerformClick(); Check(d.DialogResult == DialogResult.Cancel, "the cross cancels"); }
        using (SkinnedDialog d = UiDialogs.CreateDriverConsent())
        {
            d.Show(); Application.DoEvents();
            Check(d.Buttons[0].Name == "DriverInstallButton" && d.AcceptButton == d.Buttons[1] && d.CancelButton == d.Buttons[1], "installing needs a deliberate click");
            Key(d, Keys.Enter); Check(d.DialogResult == DialogResult.Cancel, "Enter on the consent cancels");
        }
        using (SkinnedDialog d = UiDialogs.CreateMessage("x", "t"))
        { d.Show(); Application.DoEvents(); Key(d, Keys.Enter); Check(d.DialogResult == DialogResult.Cancel && d.Buttons.Count == 1 && d.Buttons[0].Primary, "message: one primary button, Enter closes"); }
        using (SkinnedDialog d = UiDialogs.CreateExportDone(@"C:\x\y.json"))
        {
            d.Show(); Application.DoEvents();
            Check(d.Buttons.Select(b => b.Text).SequenceEqual(new[] { "打开所在文件夹", "确定" }) && d.Buttons[1].Primary && d.ActiveControl == d.Buttons[1], "export dialog: 打开所在文件夹 + 确定 (primary, default)");
            Key(d, Keys.Enter); Check(d.DialogResult == DialogResult.OK, "Enter on the export dialog is 确定");
        }
        using (SkinnedDialog d = UiDialogs.CreateExportDone(@"C:\x\y.json"))
        { d.Show(); Application.DoEvents(); d.Buttons[0].PerformClick(); Check(d.DialogResult == DialogResult.Yes, "打开所在文件夹 answers Yes"); }
        // tab order: buttons in order, the cross is not a tab stop
        using (SkinnedDialog d = UiDialogs.CreateExportDone(@"C:\x\y.json"))
            Check(d.Buttons[0].TabIndex < d.Buttons[1].TabIndex && d.Buttons.All(b => b.TabStop) && !d.CloseCross.TabStop, "tab order");
        // modal to the owner and centred on it
        using (Form owner = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 150, 900, 600), ShowInTaskbar = false })
        {
            owner.Show(); Application.DoEvents(); Rectangle where = Rectangle.Empty; bool ownerDisabled = false; IntPtr ownerOfDialog = IntPtr.Zero;
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 150 };
            using (SkinnedDialog d = UiDialogs.CreateMessage("x", "t"))
            {
                timer.Tick += delegate { timer.Stop(); where = d.Bounds; ownerDisabled = !IsWindowEnabled(owner.Handle); ownerOfDialog = d.Owner == null ? IntPtr.Zero : d.Owner.Handle; d.CloseCross.PerformClick(); };
                timer.Start(); d.ShowCentered(owner);
            }
            timer.Dispose();
            Point mid = new Point(where.Left + where.Width / 2, where.Top + where.Height / 2), omid = new Point(owner.Left + owner.Width / 2, owner.Top + owner.Height / 2);
            Check(Math.Abs(mid.X - omid.X) <= 2 && Math.Abs(mid.Y - omid.Y) <= 2, "the dialog is centred on its owner: " + mid + " vs " + omid);
            Check(ownerDisabled && ownerOfDialog == owner.Handle, "the dialog is modal to its owner");
        }
        UiDialogs.Presenter = null;
        // the static entry points go through the presenter and the safe default holds
        UiDialogs.Presenter = delegate (Form d, IWin32Window o) { return d.Name == "ConfirmAutomaticDialog" ? DialogResult.OK : DialogResult.Cancel; };
        Check(UiDialogs.ConfirmAutomatic(null) && !UiDialogs.ConfirmDriverInstall(null), "ConfirmAutomatic / ConfirmDriverInstall return the dialog's answer");
        UiDialogs.Presenter = null;
        Console.WriteLine("PASS Esc/Enter take the safe button, primary confirms, the cross cancels, tab order, modal + centred on the owner");
    }
    static void Layout()
    {
        int cases = 0; Rectangle work = Screen.PrimaryScreen.WorkingArea;
        foreach (float scale in new[] { 1f, 1.5f })
            foreach (string theme in PreviewRender.Themes)
                foreach (string language in PreviewRender.Languages)
                {
                    UiTheme.Select(theme); UiText.Select(language);
                    foreach (SkinnedDialog d in All(scale))
                        using (d)
                        {
                            string at = d.Name + " " + language + " " + scale;
                            Rectangle client = new Rectangle(Point.Empty, d.Size);
                            Check(d.TitleFits, "title is clipped: " + at);
                            Check(d.Message.GetPreferredSize(new Size(d.Message.Width, 0)).Height <= d.Message.Height, "message text is clipped: " + at + " need " + d.Message.GetPreferredSize(new Size(d.Message.Width, 0)).Height + " have " + d.Message.Height + " screen " + work);
                            Check(client.Contains(d.Message.Bounds) && client.Contains(d.CloseCross.Bounds) && d.Buttons.All(b => client.Contains(b.Bounds)), "controls outside the dialog: " + at);
                            Check(d.Buttons.All(b => b.Top >= d.Message.Bottom) && d.Buttons.All(b => b.Left >= d.Message.Left - 1), "buttons crowd the text: " + at);
                            for (int i = 1; i < d.Buttons.Count; i++) Check(d.Buttons[i - 1].Right < d.Buttons[i].Left, "buttons overlap: " + at);
                            Check(d.Message.Top >= d.CloseCross.Bottom, "the cross overlaps the text: " + at);
                            Check(d.Height <= work.Height, "dialog taller than the screen: " + at + " " + d.Height);
                            Check(d.Buttons.All(b => b.Height >= (int)(36 * scale)), "buttons too small: " + at);
                            cases++;
                        }
                }
        UiTheme.Select("black"); UiText.Select("zh-CN");
        Console.WriteLine("PASS " + cases + " dialog layouts (dialog x skin x language x dpi): text, title and buttons fit, inside, apart");
    }
    static void NoGrowth()
    {
        IntPtr process = GetCurrentProcess();
        Action cycle = delegate
        {
            foreach (SkinnedDialog d in All(null)) using (d) { d.Show(); Application.DoEvents(); d.Buttons[0].Focus(); Application.DoEvents(); }
        };
        for (int i = 0; i < 4; i++) cycle();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); Application.DoEvents(); Thread.Sleep(20); }
        uint gdi = GetGuiResources(process, 0), user = GetGuiResources(process, 1);
        for (int i = 0; i < 20; i++) { UiTheme.Select(PreviewRender.Themes[i % 3]); cycle(); }
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); Application.DoEvents(); Thread.Sleep(20); }
        uint gdi2 = GetGuiResources(process, 0), user2 = GetGuiResources(process, 1);
        Console.WriteLine("HANDLES gdi " + gdi + " -> " + gdi2 + ", user " + user + " -> " + user2);
        Check(gdi2 <= gdi + 2 && user2 <= user + 2, "GDI/USER objects grew over 20 dialog open/close cycles: " + gdi + "/" + user + " -> " + gdi2 + "/" + user2);
        UiTheme.Select("black");
        Console.WriteLine("PASS 20 open/close cycles of every dialog: no GDI or USER growth");
    }
    static void Export()
    {
        UseTemporaryRegistry();
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "DellFans-DialogTest-Reports"); // never created: the writer and the starter are fakes
            List<string> written = new List<string>(), started = new List<string>(); string shownText = null, shownTitle = null; DialogResult answer = DialogResult.OK; bool failWrite = false;
            OfflineThermalClient client = new OfflineThermalClient();
            ThermalForm form = new ThermalForm(client); form.Show(); Until(() => form.Surface.CanWrite, "offline service did not connect");
            using (form)
            {
                form.ExportFolder = folder;
                form.ExportWriter = delegate (string path, object data) { if (failWrite) throw new IOException("disk full"); written.Add(path); };
                form.StartProcess = delegate (string file, string args) { started.Add(file + " " + args); };
                UiDialogs.Presenter = delegate (Form d, IWin32Window o) { shownText = d.Controls.Find("DialogMessage", true)[0].Text; shownTitle = d.Text; return answer; };
                DashboardSurface s = form.Surface;
                // success, 确定
                s.Activate("tool:export"); Until(() => form.ExportTask.IsCompleted, "export did not finish");
                Check(written.Count == 1 && written[0].StartsWith(folder + "\\dellfans-") && written[0].EndsWith(".json"), "the export goes to the export folder");
                Check(shownTitle == "导出完成" && shownText.Contains(written[0]) && shownText.Contains("已导出"), "the success dialog states the full file path: " + shownText);
                Check(form.StatusLine == "已导出：" + Path.GetFileName(written[0]) && !s.StatusWarn, "the status line shows the file name: " + form.StatusLine);
                Check(started.Count == 0, "确定 starts nothing");
                // success, 打开所在文件夹
                answer = DialogResult.Yes; s.Activate("tool:export"); Until(() => form.ExportTask.IsCompleted && started.Count == 1, "Explorer was not started");
                Check(started[0] == "explorer.exe /select,\"" + written[1] + "\"", "Explorer selects the file: " + started[0]);
                // real button wiring of the dialog
                UiDialogs.Presenter = delegate (Form d, IWin32Window o) { d.Show(); ((Button)d.Controls.Find("OpenFolderButton", true)[0]).PerformClick(); return d.DialogResult; };
                s.Activate("tool:export"); Until(() => form.ExportTask.IsCompleted && started.Count == 2, "the 打开所在文件夹 button did not start Explorer");
                Check(started[1] == "explorer.exe /select,\"" + written[2] + "\"", "the button opens the exported file's folder");
                // failure: same style, error text, nothing started
                failWrite = true; answer = DialogResult.Cancel; UiDialogs.Presenter = delegate (Form d, IWin32Window o) { shownText = d.Controls.Find("DialogMessage", true)[0].Text; shownTitle = d.Text; return answer; };
                s.Activate("tool:export"); Until(() => form.ExportTask.IsCompleted, "failed export did not finish");
                Check(shownTitle == "导出失败" && shownText.Contains("disk full") && s.StatusWarn && started.Count == 2, "the failure is shown in the dialog and on the status line: " + shownText);
                // the folder line on the Settings card
                failWrite = false; s.Page = UiPage.Settings; s.Width = 1040; s.Height = 760; s.CreateControl(); s.Refresh(); Application.DoEvents();
                UiHit link = s.Hits.FirstOrDefault(h => h.Id == "tool:exportdir");
                Check(link != null && s.ExportPath == folder, "the Settings tools card has the folder link");
                s.Activate("tool:exportdir");
                Check(started.Count == 3 && started[2] == "explorer.exe \"" + folder + "\"", "clicking the folder opens it: " + started[2]);
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true); // only the folder link creates it (empty)
        }
        finally { Restore(); }
        Console.WriteLine("PASS export: success dialog has the full path, 打开所在文件夹 starts explorer /select,\"path\" (fake starter), failure uses the same dialog, status line and folder link");
    }
    static void Previews(string root)
    {
        string folder = Environment.GetEnvironmentVariable("DELLFANS_DIALOG_PREVIEWS") == "1" ? Path.Combine(root, "docs", "screenshots", "dialogs") : Path.Combine(Path.GetTempPath(), "DellFans-DialogPreviews-" + Guid.NewGuid().ToString("N"));
        int count = 0;
        try
        {
            foreach (string theme in PreviewRender.Themes) { UiTheme.Select(theme); UiText.Select("zh-CN"); count += UiDialogs.RenderPreviews(folder, 1f, "-" + theme + "-zh-CN-100"); }
            Check(count == 18 && Directory.GetFiles(folder, "*.png").Length >= 18, "every dialog was rendered in every skin");
        }
        finally { UiTheme.Select("black"); UiText.Select("zh-CN"); if (!folder.Contains("screenshots")) Directory.Delete(folder, true); }
        Console.WriteLine("PASS " + count + " dialog previews rendered");
    }
    [STAThread]
    static int Main(string[] args)
    {
        UseTemporaryRegistry(); UiText.Select("zh-CN"); // the checks compare Chinese labels; CI runners default to an English UI
        try { Borderless(); Skinned(); Behaviour(); Layout(); NoGrowth(); Export(); Previews(args.Length > 0 ? args[0] : "."); Console.WriteLine("RESULT " + passed + " checks passed; offline dialogs, fake explorer, temporary HKCU subkeys"); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
        finally { Restore(); }
    }
}

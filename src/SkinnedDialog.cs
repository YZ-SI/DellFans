using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DellFans
{
    internal sealed class DialogChoice
    {
        public string Name, Text; public DialogResult Result; public bool Primary;
    }

    // A flat button painted with the dashboard's own button look (primary = accent), or the caption-style close cross.
    internal sealed class DialogButton : Button
    {
        public bool Primary, Cross; public float Zoom = 1;
        bool hot, down;
        public DialogButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current; Graphics g = e.Graphics;
            g.Clear(p.Surface); UiPaint.Quality(g); g.ScaleTransform(Zoom, Zoom);
            RectangleF r = new RectangleF(0, 0, Width / Zoom, Height / Zoom);
            if (Cross)
            {
                Color ink = p.Muted;
                if (hot || down) { UiPaint.Fill(g, r, 10, down ? Color.FromArgb(0xB7, 0x1C, 0x1C) : Color.FromArgb(0xE8, 0x11, 0x23)); ink = Color.White; }
                float cx = r.Width / 2, cy = r.Height / 2;
                using (Pen pen = new Pen(ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                { g.DrawLine(pen, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f); g.DrawLine(pen, cx - 4.5f, cy + 4.5f, cx + 4.5f, cy - 4.5f); }
            }
            else
            {
                Color fill = Primary ? (hot || down ? UiPaint.Blend(p.Accent, p.Ink, down ? .22f : .12f) : p.Accent) : (hot || down ? p.Selection : p.SurfaceAlt);
                UiPaint.Fill(g, r, r.Height / 2.4f, fill);
                if (!Primary) UiPaint.Outline(g, new RectangleF(.5f, .5f, r.Width - 1, r.Height - 1), r.Height / 2.4f, p.Edge, 1);
                UiPaint.Text(g, Text, UiTheme.Font(UiMetrics.BodySize, Primary), Primary ? p.AccentInk : p.Ink, r, StringAlignment.Center);
            }
            if (Focused && ShowFocusCues) UiPaint.Outline(g, RectangleF.Inflate(r, -1, -1), Cross ? 10 : r.Height / 2.4f, p.Accent, 2);
        }
    }

    // A borderless modal card in the skin's palette: soft shadow and rounded body from ChromeSurface (Windows 10) or DWM (Windows 11),
    // title and close cross in the header, which also drags the dialog.
    internal sealed class SkinnedDialog : Form
    {
        public const int HeaderHeight = 56, Pad = 24, GapAbove = 20;
        readonly string title; readonly float s;
        public readonly Label Message; public readonly DialogButton CloseCross; public readonly List<DialogButton> Buttons = new List<DialogButton>();
        public readonly bool TitleFits;
        ChromeForm chrome; bool nativeCorners; Size shapedSize;
        public float Zoom { get { return s; } }

        public SkinnedDialog(string name, string title, string message, DialogChoice[] choices, int safeIndex, int baseWidth, float? scale)
        {
            UiPalette p = UiTheme.Current; this.title = title;
            Name = name; Text = title; AccessibleName = title; AccessibleRole = AccessibleRole.Dialog;
            FormBorderStyle = FormBorderStyle.None; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; BackColor = p.Surface; ForeColor = p.Ink;
            s = scale ?? Math.Max(0.75f, DeviceDpi / 96f);
            Font body = UiTheme.Font(UiMetrics.BodySize * s, false); Font = body;
            int width = (int)Math.Round(baseWidth * s), pad = (int)Math.Round(Pad * s), textW = width - 2 * pad;
            int fixedParts = (int)Math.Round((HeaderHeight + GapAbove + UiMetrics.ControlHeight + Pad + 48) * s);
            Message = new Label { Name = "DialogMessage", Text = message, Font = body, ForeColor = p.Ink, BackColor = p.Surface, AutoSize = false, UseMnemonic = false };
            int textH = Math.Min(Message.GetPreferredSize(new Size(textW, 0)).Height + (int)Math.Round(4 * s), Math.Max((int)(60 * s), Screen.PrimaryScreen.WorkingArea.Height - fixedParts));
            Message.Location = new Point(pad, (int)Math.Round(HeaderHeight * s)); Message.Size = new Size(textW, textH);
            int buttonH = (int)Math.Round(UiMetrics.ControlHeight * s), top = Message.Bottom + (int)Math.Round(GapAbove * s);
            Size = new Size(width, top + buttonH + pad); // no non-client area: window size == client size
            // Right-aligned row: the safe, closing button sits at the right edge, the others to its left.
            using (Bitmap probe = new Bitmap(4, 4)) using (Graphics g = Graphics.FromImage(probe))
            {
                float right = width - pad;
                DialogButton[] made = new DialogButton[choices.Length];
                for (int i = choices.Length - 1; i >= 0; i--)
                {
                    DialogChoice c = choices[i];
                    float w = Math.Max(96, UiPaint.Measure(g, c.Text, UiTheme.Font(UiMetrics.BodySize, c.Primary)) + 44) * s;
                    made[i] = new DialogButton { Name = c.Name, Text = c.Text, AccessibleName = c.Text, Primary = c.Primary, Zoom = s, DialogResult = c.Result,
                        Bounds = Rectangle.Round(new RectangleF(right - w, top, w, buttonH)) };
                    right -= w + 12 * s;
                }
                for (int i = 0; i < made.Length; i++) { made[i].TabIndex = i; Controls.Add(made[i]); Buttons.Add(made[i]); }
                DialogButton safe = made[safeIndex]; AcceptButton = safe; CancelButton = safe; ActiveControl = safe;
                TitleFits = UiPaint.Measure(g, title, UiTheme.Font(17, true)) <= TitleBounds.Width / s;
            }
            CloseCross = new DialogButton { Name = "DialogCloseButton", Text = "×", AccessibleName = UiText.T("关闭"), Cross = true, Zoom = s, DialogResult = DialogResult.Cancel, TabStop = false,
                Bounds = Rectangle.Round(new RectangleF(width - (14 + DashboardSurface.CaptionButtonWidth) * s, (HeaderHeight - DashboardSurface.CaptionButtonHeight) / 2 * s,
                    DashboardSurface.CaptionButtonWidth * s, DashboardSurface.CaptionButtonHeight * s)) };
            Controls.Add(CloseCross); Controls.Add(Message); Message.TabIndex = 90; CloseCross.TabIndex = 91;
            Shown += delegate { if (!IsDisposed) ((Control)CancelButton).Focus(); };
        }
        RectangleF TitleBounds { get { return new RectangleF(Pad * s, 0, Width - (Pad + 14 + DashboardSurface.CaptionButtonWidth + 8) * s, HeaderHeight * s); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); UiPalette p = UiTheme.Current; Graphics g = e.Graphics;
            UiPaint.Quality(g); g.ScaleTransform(s, s);
            RectangleF t = TitleBounds; UiPaint.Text(g, title, UiTheme.Font(17, true), p.Ink, new RectangleF(t.X / s, 0, t.Width / s, HeaderHeight));
        }
        // Centred on the owner window (or the screen without one), modal to it.
        public DialogResult ShowCentered(IWin32Window owner)
        {
            Control c = owner as Control; Form host = c == null ? null : c.FindForm();
            Rectangle area = host != null && host.Visible && host.WindowState == FormWindowState.Normal ? host.Bounds : Screen.FromPoint(Cursor.Position).WorkingArea;
            Rectangle work = Screen.FromRectangle(area).WorkingArea;
            int x = Math.Max(work.Left, Math.Min(work.Right - Width, area.Left + (area.Width - Width) / 2)), y = Math.Max(work.Top, Math.Min(work.Bottom - Height, area.Top + (area.Height - Height) / 2));
            Location = new Point(x, y);
            return owner != null ? ShowDialog(owner) : ShowDialog();
        }
        // ---- frame (same technique as the main window) ----
        protected override CreateParams CreateParams
        { get { CreateParams p = base.CreateParams; p.Style = (p.Style & ~0x00C70000) | 0x00080000 | 0x00040000; return p; } } // no caption or border; sysmenu + thickframe keep Alt+F4 and DWM's shadow
        int Radius { get { return (int)Math.Round(Math.Max(8, Math.Min(28, UiTheme.Current.CornerRadius)) * s); } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; nativeCorners = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)) == 0;
            UpdateFrame();
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateFrame(); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); UpdateFrame(); }
        // Activating a modal dialog lifts its owner directly beneath it, above the unowned shadow window; put the shadow back.
        protected override void OnShown(EventArgs e) { base.OnShown(e); UpdateFrame(); }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); UpdateFrame(); }
        void UpdateFrame()
        {
            if (IsDisposed || Disposing || !IsHandleCreated || nativeCorners) return;
            if (!Visible) { if (chrome != null) { chrome.Dispose(); chrome = null; } return; }
            if (Region == null || shapedSize != Size) { Region previous = Region; Region = ChromeSurface.CreateHostRegion(Size, Radius); if (previous != null) previous.Dispose(); shapedSize = Size; }
            if (chrome == null) chrome = new ChromeForm(false);
            chrome.Present(this, Radius, BackColor);
        }
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0083: m.Result = IntPtr.Zero; return; // WM_NCCALCSIZE: client area = whole window
                case 0x0085: m.Result = IntPtr.Zero; return; // WM_NCPAINT
                case 0x0086: m.LParam = new IntPtr(-1); base.WndProc(ref m); return; // WM_NCACTIVATE: never repaint a caption
                case 0x0084: // WM_NCHITTEST: the header (outside its close button, a child window) drags the dialog
                    { long v = m.LParam.ToInt64(); Point c = PointToClient(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF))); m.Result = (IntPtr)(c.Y >= 0 && c.Y < HeaderHeight * s ? 2 : 1); return; }
            }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (chrome != null) { chrome.Dispose(); chrome = null; }
                Region old = Region; if (old != null) { Region = null; old.Dispose(); }
            }
            base.Dispose(disposing);
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        // Offline preview: the dialog on its owner's background colour with the same soft shadow the window has.
        public Bitmap Body()
        {
            CreateControl();
            Bitmap body = new Bitmap(Width, Height); DrawToBitmap(body, new Rectangle(Point.Empty, Size));
            // A form that was never shown does not print its children; draw each one itself.
            using (Graphics g = Graphics.FromImage(body))
                foreach (Control c in Controls) using (Bitmap part = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(part, new Rectangle(Point.Empty, c.Size)); g.DrawImageUnscaled(part, c.Location); }
            return body;
        }
        public Bitmap Snapshot()
        {
            using (Bitmap body = Body())
            {
                int m = ChromeSurface.ShadowMargin + 20; UiPalette p = UiTheme.Current;
                Bitmap result = new Bitmap(Width + 2 * m, Height + 2 * m, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(result))
                using (Bitmap shadow = ChromeSurface.CreateWithShadow(Size, Radius, BackColor))
                using (GraphicsPath clip = UiPaint.Round(new RectangleF(m, m, Width, Height), Radius))
                {
                    g.Clear(p.Background); g.DrawImageUnscaled(shadow, m - ChromeSurface.ShadowMargin, m - ChromeSurface.ShadowMargin);
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.SetClip(clip); g.DrawImageUnscaled(body, m, m);
                }
                return result;
            }
        }
    }
}

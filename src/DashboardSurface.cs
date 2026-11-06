using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace DellFans
{
    public enum UiPage { Home, Details, Settings }

    public sealed class UiHit
    {
        public string Id;
        public RectangleF Rect;
        public bool Fixed;
    }

    // The whole window content is painted here: a few cards per page, one hit list, one optional text box. Geometry is in logical pixels
    // (96 dpi); Render applies the scale, so the same code draws the window at any DPI and offline previews.
    public sealed class DashboardSurface : Control
    {
        public const int HeaderHeight = 72, DefaultWidth = 1040, MinimumWidth = 780;
        public DashboardState State = new DashboardState();
        public UiPage Page = UiPage.Home;
        public string StatusText = "", SelectedMode;
        public bool Maximized, StatusWarn, CanWrite, CustomPending, DeckOn = true, StartupOn, MoreLanguages, PreviewMode, Busy;
        public string DraftText = "", ExportPath = "";
        // PawnIO notice: "以后再说" hides it for this window only; DriverOverride holds what an install just reported until a snapshot catches up.
        public bool DriverDismissed, DriverInstalling;
        public string DriverOverride;
        public readonly NumericTextBox Input = new NumericTextBox();
        public event Action<string> Activated;
        readonly List<UiHit> hits = new List<UiHit>();
        string hover, focus, pressed;
        float scrollY, contentHeight, scale = 1;
        RectangleF plot; DateTime plotEnd; float? hoverX;
        // Offline previews only: a pointer position on the trend (0-1 of its width) and made-up external languages.
        public float? PreviewHover; public KeyValuePair<string, string>[] PreviewLanguages;
        bool drawingForPaint;

        public DashboardSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; AccessibleName = "DellFans"; AccessibleRole = AccessibleRole.Client;
            Input.Minimum = RpmRange.Minimum; Input.Maximum = RpmRange.Maximum; Input.Increment = 100; Input.Name = "MinimumRpmInput"; Input.AccessibleName = UiText.T("最低 RPM");
            Input.Visible = false; Input.BorderStyle = BorderStyle.None; Input.TextAlign = HorizontalAlignment.Right; Input.TextChanged += delegate { DraftText = Input.Text; };
            Controls.Add(Input);
        }
        public IList<UiHit> Hits { get { return hits; } }
        public string HoverId { get { return hover; } }
        public float ContentHeight { get { return contentHeight; } }
        float DpiScale { get { return Math.Max(0.75f, DeviceDpi / 96f); } }

        // ---- drawing ----
        public void Render(Graphics g, SizeF logical, float s)
        {
            scale = s; hits.Clear();
            UiPalette p = UiTheme.Current;
            g.ResetTransform(); UiPaint.Quality(g);
            using (Brush b = new SolidBrush(p.Background)) g.FillRectangle(b, 0, 0, logical.Width * s + 1, logical.Height * s + 1);
            float width = logical.Width - 2 * UiMetrics.Margin;
            g.ScaleTransform(s, s);
            // Content first (clipped under the header), then the fixed header on top.
            g.SetClip(new RectangleF(0, HeaderHeight, logical.Width, logical.Height - HeaderHeight));
            GraphicsState state = g.Save();
            g.TranslateTransform(UiMetrics.Margin, HeaderHeight - scrollY);
            float used = Page == UiPage.Home ? PaintHome(g, p, width) : Page == UiPage.Details ? PaintDetails(g, p, width) : PaintSettings(g, p, width);
            g.Restore(state);
            contentHeight = used + UiMetrics.Margin;
            float max = Math.Max(0, contentHeight - (logical.Height - HeaderHeight));
            if (scrollY > max) scrollY = max;
            g.ResetClip();
            PaintScrollbar(g, p, logical, max);
            PaintHeader(g, p, logical);
            if (focus != null) foreach (UiHit h in hits) if (h.Id == focus) { RectangleF f = h.Rect; if (!h.Fixed) f.Offset(UiMetrics.Margin, HeaderHeight - scrollY); f.Inflate(2, 2); UiPaint.Outline(g, f, 12, p.Accent, 2); }
            g.ResetTransform();
        }
        // The page height a preview needs to show everything without scrolling.
        public float MeasureContent(float logicalWidth, float s)
        {
            using (Bitmap probe = new Bitmap(4, 4))
            using (Graphics g = Graphics.FromImage(probe))
            {
                float saved = scrollY; scrollY = 0; Render(g, new SizeF(logicalWidth, 4000), s); scrollY = saved;
                return contentHeight;
            }
        }
        void PaintScrollbar(Graphics g, UiPalette p, SizeF logical, float max)
        {
            if (max <= 0) return;
            float view = logical.Height - HeaderHeight, h = Math.Max(40, view * view / contentHeight), y = HeaderHeight + (view - h) * scrollY / max;
            UiPaint.Fill(g, new RectangleF(logical.Width - 8, y, 4, h), 2, Color.FromArgb(110, p.Muted));
        }
        // The caption lives in the header: icon, title, page switch and the three window buttons, all in the skin's own colours.
        public const float CaptionButtonWidth = 44, CaptionButtonHeight = 32, CaptionGap = 2;
        static readonly string[] captionIds = { "cap:min", "cap:max", "cap:close" };
        RectangleF CaptionButton(float width, int index)
        { return new RectangleF(width - 14 - (3 - index) * CaptionButtonWidth - (2 - index) * CaptionGap, 10, CaptionButtonWidth, CaptionButtonHeight); }
        static Bitmap captionIcon;
        static Bitmap CaptionIcon()
        {
            if (captionIcon == null)
            {
                System.Drawing.Icon icon = ThermalForm.AppIcon();
                if (icon != null) try { using (System.Drawing.Icon big = new System.Drawing.Icon(icon, 48, 48)) captionIcon = big.ToBitmap(); } catch (Exception) { }
            }
            return captionIcon;
        }
        void PaintHeader(Graphics g, UiPalette p, SizeF logical)
        {
            using (Brush b = new SolidBrush(p.Background)) g.FillRectangle(b, 0, 0, logical.Width, HeaderHeight);
            float x = UiMetrics.Margin;
            Bitmap icon = CaptionIcon();
            if (icon != null)
            {
                InterpolationMode mode = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new RectangleF(x, 22, 28, 28)); g.InterpolationMode = mode; x += 38;
            }
            UiPaint.Text(g, "DellFans", UiTheme.Font(22, true), p.Ink, new RectangleF(x, 12, 220, 30));
            string[] ids = { "home", "details", "settings" }, labels = { UiText.T("主页"), UiText.T("详情"), UiText.T("设置") };
            float w = 88, total = w * 3 + 8, left = CaptionButton(logical.Width, 0).X - 20 - total;
            string sub = State.Connected ? (State.Model ?? "") + (string.IsNullOrEmpty(State.Bios) ? "" : " · BIOS " + State.Bios) : "";
            UiPaint.Text(g, PreviewMode && sub.Length > 0 ? sub + " · " + UiText.T("离线预览") : sub, UiTheme.Font(UiMetrics.SmallSize, false), p.Muted, new RectangleF(x, 42, Math.Max(100, left - x - 16), 20));
            RectangleF rail = new RectangleF(left, 14, total, 40);
            UiPaint.Fill(g, rail, 20, p.SurfaceAlt);
            for (int i = 0; i < 3; i++)
            {
                RectangleF seg = new RectangleF(left + 4 + i * w, 18, w, 32);
                bool on = (int)Page == i;
                if (on) UiPaint.Fill(g, seg, 16, p.Accent);
                else if (hover == "nav:" + ids[i]) UiPaint.Fill(g, seg, 16, p.Selection);
                UiPaint.Text(g, labels[i], UiTheme.Font(UiMetrics.BodySize, on), on ? p.AccentInk : p.Ink, seg, StringAlignment.Center);
                hits.Add(new UiHit { Id = "nav:" + ids[i], Rect = seg, Fixed = true });
            }
            for (int i = 0; i < 3; i++)
            {
                RectangleF r = CaptionButton(logical.Width, i); string id = captionIds[i]; bool close = i == 2, hot = hover == id, down = hot && pressed == id;
                Color ink = p.Ink;
                if (hot || down)
                {
                    Color fill = close ? (down ? Color.FromArgb(0xB7, 0x1C, 0x1C) : Color.FromArgb(0xE8, 0x11, 0x23)) : (down ? UiPaint.Blend(p.Selection, p.Muted, .35f) : p.Selection);
                    UiPaint.Fill(g, r, 10, fill); if (close) ink = Color.White;
                }
                else ink = p.Muted;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                using (Pen pen = new Pen(ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (i == 0) g.DrawLine(pen, cx - 5, cy + 1, cx + 5, cy + 1);
                    else if (i == 1 && !Maximized) using (GraphicsPath box = UiPaint.Round(new RectangleF(cx - 5, cy - 5, 10, 10), 2)) g.DrawPath(pen, box);
                    else if (i == 1)
                    {
                        using (GraphicsPath front = UiPaint.Round(new RectangleF(cx - 5, cy - 2, 8, 8), 1.6f)) g.DrawPath(pen, front);
                        g.DrawLines(pen, new[] { new PointF(cx - 2, cy - 2), new PointF(cx - 2, cy - 5), new PointF(cx + 5, cy - 5), new PointF(cx + 5, cy + 2), new PointF(cx + 3, cy + 2) });
                    }
                    else { g.DrawLine(pen, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f); g.DrawLine(pen, cx - 4.5f, cy + 4.5f, cx + 4.5f, cy - 4.5f); }
                }
                hits.Add(new UiHit { Id = id, Rect = r, Fixed = true });
            }
        }
        // Text a screen reader announces for a hit id.
        public string AccessibleNameFor(string id)
        {
            if (id == "cap:min") return UiText.T("最小化");
            if (id == "cap:max") return (Maximized ? UiText.T("还原") : UiText.T("最大化"));
            if (id == "cap:close") return UiText.T("关闭");
            if (id == "nav:home") return UiText.T("主页");
            if (id == "nav:details") return UiText.T("详情");
            if (id == "nav:settings") return UiText.T("设置");
            if (id == "tool:export") return UiText.T("导出当前数据");
            if (id == "tool:exportdir") return UiText.T("打开导出文件夹");
            if (id == "tool:themes") return UiText.T("打开皮肤文件夹");
            if (id == "tool:languages") return UiText.T("打开语言文件夹");
            return id;
        }
        // True for the part of the header that drags the window: everything in it that is not a button.
        public bool IsCaptionAt(Point client)
        { return client.Y >= 0 && client.X >= 0 && client.X < ClientSize.Width && client.Y / ScaleNow < HeaderHeight && HitAt(client) == null; }
        float ScaleNow { get { return Math.Max(0.75f, scale); } }
        protected override AccessibleObject CreateAccessibilityInstance() { return new SurfaceAccessible(this); }
        sealed class SurfaceAccessible : ControlAccessibleObject
        {
            readonly DashboardSurface owner;
            public SurfaceAccessible(DashboardSurface owner) : base(owner) { this.owner = owner; }
            public override int GetChildCount() { return owner.hits.Count; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < owner.hits.Count ? new HitAccessible(owner, owner.hits[index]) : null; }
        }
        sealed class HitAccessible : AccessibleObject
        {
            readonly DashboardSurface owner; readonly UiHit hit;
            public HitAccessible(DashboardSurface owner, UiHit hit) { this.owner = owner; this.hit = hit; }
            public override string Name { get { return owner.AccessibleNameFor(hit.Id); } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
            public override string DefaultAction { get { return "Press"; } }
            public override void DoDefaultAction() { owner.Raise(hit.Id); }
            public override Rectangle Bounds
            {
                get
                {
                    RectangleF r = hit.Rect; float s = owner.ScaleNow; if (!hit.Fixed) r.Offset(UiMetrics.Margin, HeaderHeight - owner.scrollY);
                    return owner.RectangleToScreen(Rectangle.Round(new RectangleF(r.X * s, r.Y * s, r.Width * s, r.Height * s)));
                }
            }
        }
        protected override void WndProc(ref Message m)
        {
            // Over the caption and the sizing edges the surface steps aside (HTTRANSPARENT) so the window decides: drag, snap, resize.
            if (m.Msg == 0x84)
            {
                ThermalForm form = Parent as ThermalForm;
                if (form != null)
                {
                    long v = m.LParam.ToInt64();
                    if (form.HitTest(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF))) != 1) { m.Result = new IntPtr(-1); return; }
                }
            }
            base.WndProc(ref m);
        }
        // ---- shared card pieces ----
        static Font F(float size, bool bold) { return UiTheme.Font(size, bold); }
        void Title(Graphics g, UiPalette p, RectangleF r, string text)
        { UiPaint.Text(g, text, F(UiMetrics.TitleSize, true), p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 16, r.Width - 2 * UiMetrics.Pad, 22)); }
        RectangleF CardAt(Graphics g, UiPalette p, float x, float y, float w, float h)
        { RectangleF r = new RectangleF(x, y, w, h); UiPaint.Card(g, r, p.CornerRadius, p); return r; }
        static string Rpm(float? v) { return v.HasValue ? v.Value.ToString("N0", CultureInfo.CurrentCulture) : "—"; }
        static string Deg(float? v) { return v.HasValue ? v.Value.ToString("0", CultureInfo.CurrentCulture) : "—"; }
        static Color TempColor(UiPalette p, float? t)
        { return !t.HasValue || t.Value < 60 ? p.Accent : t.Value < 80 ? UiTheme.ModeColor(FanModes.Performance) : p.Warning; }
        void Button(Graphics g, UiPalette p, string id, RectangleF r, string text, bool primary, bool enabled)
        {
            bool hot = hover == id && enabled;
            Color fill = primary ? (enabled ? (hot ? UiPaint.Blend(p.Accent, p.Ink, .12f) : p.Accent) : p.SurfaceAlt) : (hot ? p.Selection : p.SurfaceAlt);
            UiPaint.Fill(g, r, r.Height / 2.4f, fill);
            if (!primary) UiPaint.Outline(g, new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), r.Height / 2.4f, p.Edge, 1);
            UiPaint.Text(g, text, F(UiMetrics.BodySize, primary), !enabled ? p.Muted : primary ? p.AccentInk : p.Ink, r, StringAlignment.Center);
            if (enabled) hits.Add(new UiHit { Id = id, Rect = r });
        }
        // ---- driver notice ----
        public sealed class DriverNotice
        {
            public string Title, Detail;
            public bool Buttons;
        }
        // The card at the top of the home page, or null when the sensor driver needs no attention.
        public DriverNotice Notice
        {
            get
            {
                if (DriverInstalling) return new DriverNotice { Title = UiText.T("正在安装…"), Detail = UiText.T("请在弹出的管理员确认窗口中选择“是”。") };
                string driver = DriverOverride ?? State.SensorDriverState;
                if (driver == "NeedsRestart") return new DriverNotice { Title = UiText.T("驱动已安装，重启电脑后生效") };
                if ((driver != "Missing" && driver != "HalfUninstalled") || DriverDismissed) return null;
                string source = State.SensorSource ?? "";
                return new DriverNotice
                {
                    Title = UiText.T(driver == "Missing" ? "CPU 温度驱动（PawnIO）未安装" : "CPU 温度驱动（PawnIO）需要重新安装"),
                    Detail = source.IndexOf("ESIF", StringComparison.OrdinalIgnoreCase) >= 0 ? UiText.T("当前使用 Intel ESIF，精度较低，仍可控速") :
                        source.Length == 0 || source == "None" ? UiText.T("无法读取 CPU 温度，风扇保持 BIOS 自动") : null,
                    Buttons = true
                };
            }
        }
        float PaintNotice(Graphics g, UiPalette p, float width, float y, DriverNotice notice)
        {
            float height = notice.Detail == null ? 64 : 84;
            RectangleF r = CardAt(g, p, 0, y, width, height);
            float textLeft = r.X + UiMetrics.Pad + 20, buttons = notice.Buttons ? 244 : 0, textWidth = r.Width - (textLeft - r.X) - UiMetrics.Pad - buttons;
            UiPaint.Dot(g, r.X + UiMetrics.Pad + 5, notice.Detail == null ? r.Y + height / 2 : r.Y + 29, 10, DriverInstalling ? p.Accent : p.Warning);
            UiPaint.Text(g, notice.Title, F(UiMetrics.BodySize + 1, true), p.Ink, new RectangleF(textLeft, notice.Detail == null ? r.Y + (height - 26) / 2 : r.Y + 16, textWidth, 26));
            if (notice.Detail != null) UiPaint.Text(g, notice.Detail, F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(textLeft, r.Y + 44, textWidth, 24));
            if (notice.Buttons)
            {
                float by = r.Y + (height - UiMetrics.ControlHeight) / 2;
                Button(g, p, "driver:later", new RectangleF(r.Right - UiMetrics.Pad - 104, by, 104, UiMetrics.ControlHeight), UiText.T("以后再说"), false, !Busy);
                Button(g, p, "driver:install", new RectangleF(r.Right - UiMetrics.Pad - 104 - 8 - 124, by, 124, UiMetrics.ControlHeight), UiText.T("安装驱动…"), true, !Busy);
            }
            return y + height + UiMetrics.Gap;
        }
        // ---- home ----
        float PaintHome(Graphics g, UiPalette p, float width)
        {
            float y = 4, col = (width - 2 * UiMetrics.Gap) / 3, rowHeight = 236;
            DriverNotice notice = Notice;
            if (notice != null) y = PaintNotice(g, p, width, y, notice);
            string modeColorKey = State.Mode;
            // fan
            RectangleF r = CardAt(g, p, 0, y, col, rowHeight);
            Title(g, p, r, UiText.T("风扇转速"));
            if (State.Mode != null)
            {
                string mode = DashboardState.ModeName(State.Mode); float mw = UiPaint.Measure(g, mode, F(UiMetrics.SmallSize, false));
                UiPaint.Dot(g, r.Right - UiMetrics.Pad - mw - 10, r.Y + 27, 8, UiTheme.ModeColor(State.Mode));
                UiPaint.Text(g, mode, F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(r.Right - UiMetrics.Pad - mw - 2, r.Y + 16, mw + 4, 22));
            }
            Font big = F(UiMetrics.ValueSize, true); string rpm = Rpm(State.Rpm);
            UiPaint.Text(g, rpm, big, State.Connected ? p.Ink : p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 62, r.Width - 2 * UiMetrics.Pad, 56));
            float rw = UiPaint.Measure(g, rpm, big);
            UiPaint.Text(g, "RPM", F(UiMetrics.BodySize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad + rw + 8, r.Y + 86, 60, 22));
            UiPaint.Bar(g, new RectangleF(r.X + UiMetrics.Pad, r.Y + 148, r.Width - 2 * UiMetrics.Pad, 8), State.Rpm.HasValue ? State.Rpm.Value / RpmRange.Maximum : 0, p.SurfaceAlt, State.Mode != null ? UiTheme.ModeColor(State.Mode) : p.Accent);
            UiPaint.Text(g, State.TargetRpm.HasValue ? UiText.F("目标 {0} RPM", State.TargetRpm.Value.ToString("N0")) : "", F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 166, r.Width - 2 * UiMetrics.Pad, 20));
            UiPaint.Text(g, State.FanMax.HasValue ? UiText.F("本次最低 / 最高 {0} / {1}", Rpm(State.FanMin), Rpm(State.FanMax)) : "", F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 190, r.Width - 2 * UiMetrics.Pad, 20));
            // cpu ring
            r = CardAt(g, p, col + UiMetrics.Gap, y, col, rowHeight);
            Title(g, p, r, UiText.T("CPU 温度"));
            float ring = 148; RectangleF rr = new RectangleF(r.X + (r.Width - ring) / 2, r.Y + 44, ring, ring);
            float? cpu = State.CpuPackage;
            UiPaint.Ring(g, rr, 14, cpu.HasValue ? (cpu.Value - 20) / 80f : 0, p.SurfaceAlt, TempColor(p, cpu));
            Font gauge = F(UiMetrics.GaugeSize, true); string deg = Deg(cpu); float dw = UiPaint.Measure(g, deg, gauge);
            UiPaint.Text(g, deg, gauge, State.Connected ? p.Ink : p.Muted, new RectangleF(rr.X, rr.Y + ring / 2 - 30, ring, 46), StringAlignment.Center);
            UiPaint.Text(g, "°C", F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(rr.X, rr.Y + ring / 2 + 14, ring, 20), StringAlignment.Center);
            UiPaint.Text(g, State.CoreMax.HasValue || State.CoreAvg.HasValue ? UiText.F("核心最高 {0}°C · 平均 {1}°C", Deg(State.CoreMax), Deg(State.CoreAvg)) : "", F(UiMetrics.SmallSize, false), p.Muted,
                new RectangleF(r.X + UiMetrics.Pad, r.Bottom - 38, r.Width - 2 * UiMetrics.Pad, 22), StringAlignment.Center);
            // disk
            r = CardAt(g, p, 2 * (col + UiMetrics.Gap), y, col, rowHeight);
            Title(g, p, r, UiText.T("最热硬盘"));
            string dt = Deg(State.DiskTemp); float tw = UiPaint.Measure(g, dt, big);
            UiPaint.Text(g, dt, big, State.Connected ? p.Ink : p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 62, r.Width - 2 * UiMetrics.Pad, 56));
            UiPaint.Text(g, "°C", F(UiMetrics.BodySize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad + tw + 8, r.Y + 86, 40, 22));
            UiPaint.Bar(g, new RectangleF(r.X + UiMetrics.Pad, r.Y + 148, r.Width - 2 * UiMetrics.Pad, 8), State.DiskTemp.HasValue ? (State.DiskTemp.Value - 20) / 60f : 0, p.SurfaceAlt, TempColor(p, State.DiskTemp));
            UiPaint.Text(g, State.DiskName ?? "", F(UiMetrics.BodySize, false), p.Ink, new RectangleF(r.X + UiMetrics.Pad, r.Y + 166, r.Width - 2 * UiMetrics.Pad, 22));
            UiPaint.Text(g, State.DiskPeak.HasValue ? UiText.F("本次最高 {0}°C", Deg(State.DiskPeak)) : "", F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 192, r.Width - 2 * UiMetrics.Pad, 20));
            y += rowHeight + UiMetrics.Gap;
            // modes
            bool custom = SelectedMode == FanModes.Custom;
            float modeHeight = custom ? 206 : 150;
            r = CardAt(g, p, 0, y, width, modeHeight);
            Title(g, p, r, UiText.T("模式"));
            RectangleF rail = new RectangleF(r.X + UiMetrics.Pad, r.Y + 50, r.Width - 2 * UiMetrics.Pad, 48);
            UiPaint.Fill(g, rail, 16, p.SurfaceAlt);
            float sw = (rail.Width - 8) / FanModes.All.Length;
            for (int i = 0; i < FanModes.All.Length; i++)
            {
                string mode = FanModes.All[i]; RectangleF seg = new RectangleF(rail.X + 4 + i * sw, rail.Y + 4, sw, 40);
                bool on = SelectedMode == mode, hot = hover == "mode:" + mode && CanWrite;
                if (on) UiPaint.Fill(g, seg, 13, p.Accent); else if (hot) UiPaint.Fill(g, seg, 13, p.Selection);
                string label = DashboardState.ModeName(mode); Font font = F(UiMetrics.BodySize, on);
                float lw = UiPaint.Measure(g, label, font), start = seg.X + (seg.Width - lw - 16) / 2;
                UiPaint.Dot(g, start + 4, seg.Y + 20, 8, on ? p.AccentInk : UiTheme.ModeColor(mode));
                UiPaint.Text(g, label, font, on ? p.AccentInk : CanWrite ? p.Ink : p.Muted, new RectangleF(start + 16, seg.Y, lw + 8, seg.Height));
                if (CanWrite) hits.Add(new UiHit { Id = "mode:" + mode, Rect = seg });
            }
            string description = ModeDescription();
            float below = r.Y + 110;
            if (custom)
            {
                UiPaint.Text(g, UiText.T("最低 RPM"), F(UiMetrics.BodySize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad, below, 90, UiMetrics.ControlHeight));
                RectangleF field = new RectangleF(r.X + UiMetrics.Pad + 100, below, 150, UiMetrics.ControlHeight);
                UiPaint.Fill(g, field, 11, p.Input); UiPaint.Outline(g, new RectangleF(field.X + .5f, field.Y + .5f, field.Width - 1, field.Height - 1), 11, hover == "field" ? p.Accent : p.Edge, 1);
                PlaceInput(g, p, field);
                Button(g, p, "apply", new RectangleF(field.Right + 12, below, 96, UiMetrics.ControlHeight), UiText.T("应用"), true, CanWrite && !Busy);
                UiPaint.Text(g, "1000–5570 RPM", F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(field.Right + 120, below, 160, UiMetrics.ControlHeight));
                below += UiMetrics.ControlHeight + 12;
            }
            else HideInput();
            UiPaint.Text(g, description, F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad, below, r.Width - 2 * UiMetrics.Pad, 22));
            y += modeHeight + 14;
            // status
            UiPaint.Dot(g, 8, y + 12, 8, StatusWarn ? p.Warning : State.Connected && State.Fresh ? UiTheme.ModeColor(FanModes.Quiet) : p.Muted);
            float retry = State.Connected ? 0 : 96;
            UiPaint.Text(g, StatusText, F(UiMetrics.SmallSize, false), StatusWarn ? p.Warning : p.Muted, new RectangleF(24, y, width - 24 - retry - 8, 24));
            if (!State.Connected) Button(g, p, "retry", new RectangleF(width - 92, y - 6, 92, 36), UiText.T("重试连接"), false, !Busy);
            return y + 36;
        }
        string ModeDescription()
        {
            string mode = SelectedMode;
            if (mode == null) return "";
            if (mode == FanModes.Auto) return UiText.F("{0} · BIOS 控制，不接管风扇", DashboardState.ModeName(mode));
            ModeLimits limits = State.Limits;
            if (State.Mode != mode || limits == null || (mode == FanModes.Custom && CustomPending))
                try { limits = FanModes.Limits(mode, mode == FanModes.Custom ? DraftMinimum() : (int?)null); } catch (ArgumentException) { limits = null; }
            return limits == null ? DashboardState.ModeName(mode) : UiText.F("{0} · {1:N0}–{2:N0} RPM · {3}°C 起调", DashboardState.ModeName(mode), limits.Min, limits.Max, limits.StartC);
        }
        // The minimum being typed when it is valid, else the saved one.
        int DraftMinimum()
        {
            int typed;
            if (CustomPending && int.TryParse(DraftText, NumberStyles.Number, CultureInfo.CurrentCulture, out typed) && typed >= RpmRange.Minimum && typed <= RpmRange.Maximum) return typed;
            return State.CustomMinimum ?? RpmRange.Minimum;
        }
        void PlaceInput(Graphics g, UiPalette p, RectangleF field)
        {
            Input.BackColor = p.Input; Input.ForeColor = p.Ink;
            if (!drawingForPaint) { PaintInputText(g, p, field); return; }
            Input.Font = F((UiMetrics.TitleSize + 2) * scale, false);
            int h = Input.PreferredHeight;
            Input.SetBounds((int)Math.Round((field.X + UiMetrics.Margin + 12) * scale), (int)Math.Round((field.Y + HeaderHeight - scrollY + field.Height / 2) * scale) - h / 2, (int)Math.Round((field.Width - 24) * scale), h);
            if (!Input.Visible) Input.Visible = true;
        }
        void PaintInputText(Graphics g, UiPalette p, RectangleF field)
        { UiPaint.Text(g, DraftText, F(UiMetrics.TitleSize + 2, false), p.Ink, new RectangleF(field.X + 12, field.Y, field.Width - 24, field.Height), StringAlignment.Far); }
        void HideInput() { if (Input.Visible) Input.Visible = false; }
        // ---- details ----
        float PaintDetails(Graphics g, UiPalette p, float width)
        {
            HideInput();
            float y = 4, trendHeight = 270;
            RectangleF r = CardAt(g, p, 0, y, width, trendHeight);
            Title(g, p, r, UiText.T("最近 10 分钟"));
            Font small = F(UiMetrics.SmallSize, false);
            Color rpmColor = p.Accent, cpuColor = UiTheme.ModeColor(FanModes.Performance);
            string l1 = UiText.T("风扇转速") + " RPM", l2 = UiText.T("CPU 温度") + " °C"; float w2 = UiPaint.Measure(g, l2, small), w1 = UiPaint.Measure(g, l1, small);
            float lx = r.Right - UiMetrics.Pad - w2 - 14;
            UiPaint.Dot(g, lx, r.Y + 27, 8, cpuColor); UiPaint.Text(g, l2, small, p.Muted, new RectangleF(lx + 10, r.Y + 16, w2 + 6, 22));
            lx -= w1 + 34; UiPaint.Dot(g, lx, r.Y + 27, 8, rpmColor); UiPaint.Text(g, l1, small, p.Muted, new RectangleF(lx + 10, r.Y + 16, w1 + 6, 22));
            plot = new RectangleF(r.X + 64, r.Y + 52, r.Width - 64 - 52, trendHeight - 52 - 34);
            PaintTrend(g, p, small, rpmColor, cpuColor);
            y += trendHeight + UiMetrics.Gap;
            float left = (width - UiMetrics.Gap) * 0.42f, right = width - UiMetrics.Gap - left;
            float fanHeight = 238, diskHeight = 190, eventsHeight = Math.Max(fanHeight + diskHeight + UiMetrics.Gap, 52 + Math.Max(1, State.Events.Count) * 22 + 20);
            // fan
            r = CardAt(g, p, 0, y, left, fanHeight); Title(g, p, r, UiText.T("风扇"));
            string[] keys = { UiText.T("目标"), UiText.T("实际"), UiText.T("模式"), UiText.T("控制状态"), UiText.T("本次最低 / 最高") };
            string[] values = {
                State.TargetRpm.HasValue ? State.TargetRpm.Value.ToString("N0") + " RPM" : "—", State.Rpm.HasValue ? Rpm(State.Rpm) + " RPM" : "—",
                State.Mode == null ? "—" : DashboardState.ModeName(State.Mode), DashboardState.StateName(State.ControlState), State.FanMax.HasValue ? Rpm(State.FanMin) + " / " + Rpm(State.FanMax) : "—" };
            for (int i = 0; i < keys.Length; i++) Row(g, p, r, 56 + i * 34, keys[i], values[i]);
            // disk
            RectangleF d = CardAt(g, p, 0, y + fanHeight + UiMetrics.Gap, left, eventsHeight - fanHeight - UiMetrics.Gap); Title(g, p, d, UiText.T("硬盘"));
            Row(g, p, d, 56, UiText.T("型号"), State.DiskName ?? "—"); Row(g, p, d, 90, UiText.T("当前温度"), State.DiskTemp.HasValue ? Deg(State.DiskTemp) + " °C" : "—");
            Row(g, p, d, 124, UiText.T("本次最高"), State.DiskPeak.HasValue ? Deg(State.DiskPeak) + " °C" : "—");
            // events
            r = CardAt(g, p, left + UiMetrics.Gap, y, right, eventsHeight); Title(g, p, r, UiText.T("事件"));
            if (State.Events.Count == 0) UiPaint.Text(g, UiText.T("暂无事件"), F(UiMetrics.BodySize, false), p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 56, r.Width - 40, 24));
            for (int i = 0; i < State.Events.Count; i++)
            {
                UiEvent e = State.Events[i]; float ey = r.Y + 52 + i * 22;
                UiPaint.Text(g, e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), small, p.Muted, new RectangleF(r.X + UiMetrics.Pad, ey, 64, 22));
                UiPaint.Text(g, e.Text, small, e.Warn ? p.Warning : p.Ink, new RectangleF(r.X + UiMetrics.Pad + 72, ey, r.Width - 2 * UiMetrics.Pad - 72, 22));
            }
            y += eventsHeight + UiMetrics.Gap;
            // processes
            r = CardAt(g, p, 0, y, width, 56);
            ProcessTotals t = State.Totals ?? new ProcessTotals();
            UiPaint.Text(g, UiText.T("DellFans 进程合计"), F(UiMetrics.BodySize, true), p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y, 220, 56));
            UiPaint.Text(g, "CPU  " + (t.CpuPercent.HasValue ? t.CpuPercent.Value.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "—"), F(UiMetrics.BodySize, false), p.Ink, new RectangleF(r.X + 250, r.Y, 150, 56));
            UiPaint.Text(g, UiText.T("内存") + "  " + (t.MemoryMiB.HasValue ? t.MemoryMiB.Value.ToString("0.0", CultureInfo.CurrentCulture) + " MiB" : "—"), F(UiMetrics.BodySize, false), p.Ink, new RectangleF(r.X + 420, r.Y, 190, 56));
            UiPaint.Text(g, t.Count > 0 ? UiText.F("{0} 个进程", t.Count) : "", small, p.Muted, new RectangleF(r.Right - UiMetrics.Pad - 140, r.Y, 140, 56), StringAlignment.Far);
            return y + 56;
        }
        void Row(Graphics g, UiPalette p, RectangleF card, float dy, string key, string value)
        {
            Font f = F(UiMetrics.BodySize, false);
            UiPaint.Text(g, key, f, p.Muted, new RectangleF(card.X + UiMetrics.Pad, card.Y + dy, 120, 26));
            UiPaint.Text(g, value, F(UiMetrics.BodySize, true), p.Ink, new RectangleF(card.X + UiMetrics.Pad + 120, card.Y + dy, card.Width - 2 * UiMetrics.Pad - 120, 26), StringAlignment.Far);
        }
        void PaintTrend(Graphics g, UiPalette p, Font small, Color rpmColor, Color cpuColor)
        {
            for (int i = 0; i <= 4; i++)
            {
                float gy = plot.Top + plot.Height * i / 4;
                using (Pen grid = new Pen(Color.FromArgb(i == 4 ? 140 : 70, p.Edge))) g.DrawLine(grid, plot.Left, gy, plot.Right, gy);
                UiPaint.Text(g, ((4 - i) * 1500).ToString("N0"), small, p.Muted, new RectangleF(plot.Left - 60, gy - 10, 52, 20), StringAlignment.Far);
                UiPaint.Text(g, (20 + (4 - i) * 20).ToString(), small, p.Muted, new RectangleF(plot.Right + 8, gy - 10, 40, 20));
            }
            List<TrendPoint> points = State.Trend;
            plotEnd = points.Count > 0 ? points[points.Count - 1].Time : DateTime.Now;
            float span = DashboardState.TrendMinutes * 60f;
            Func<DateTime, float> xOf = delegate(DateTime t) { return plot.Right - (float)(plotEnd - t).TotalSeconds / span * plot.Width; };
            UiPaint.Text(g, UiText.T("10 分钟前"), small, p.Muted, new RectangleF(plot.Left, plot.Bottom + 8, 100, 20));
            UiPaint.Text(g, UiText.T("现在"), small, p.Muted, new RectangleF(plot.Right - 60, plot.Bottom + 8, 60, 20), StringAlignment.Far);
            GraphicsState state = g.Save(); g.SetClip(RectangleF.Inflate(plot, 4, 4));
            Series(g, points, xOf, delegate(TrendPoint t) { return t.Rpm.HasValue ? (float?)(t.Rpm.Value / 6000f) : null; }, rpmColor, true);
            Series(g, points, xOf, delegate(TrendPoint t) { return t.Cpu.HasValue ? (float?)((t.Cpu.Value - 20) / 80f) : null; }, cpuColor, false);
            g.Restore(state);
            float? at = hoverX ?? (PreviewHover.HasValue ? (float?)(plot.Left + PreviewHover.Value * plot.Width) : null);
            if (at.HasValue && points.Count > 0)
            {
                DateTime when = plotEnd.AddSeconds(-(plot.Right - at.Value) / plot.Width * span);
                TrendPoint near = State.Nearest(when);
                if (near != null)
                {
                    float x = xOf(near.Time);
                    using (Pen pen = new Pen(Color.FromArgb(150, p.Muted), 1) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                    string text = near.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "   " + Rpm(near.Rpm) + " RPM   " + (near.Cpu.HasValue ? near.Cpu.Value.ToString("0") : "—") + " °C";
                    float tw = UiPaint.Measure(g, text, F(UiMetrics.SmallSize, true)) + 24, tx = Math.Min(Math.Max(plot.Left, x - tw / 2), plot.Right - tw);
                    RectangleF tip = new RectangleF(tx, plot.Top - 2, tw, 28);
                    UiPaint.Fill(g, tip, 10, p.Ink); UiPaint.Text(g, text, F(UiMetrics.SmallSize, true), p.Background, tip, StringAlignment.Center);
                    if (near.Rpm.HasValue) UiPaint.Dot(g, x, plot.Bottom - near.Rpm.Value / 6000f * plot.Height, 9, rpmColor);
                    if (near.Cpu.HasValue) UiPaint.Dot(g, x, plot.Bottom - (near.Cpu.Value - 20) / 80f * plot.Height, 9, cpuColor);
                }
            }
            if (points.Count < 2) UiPaint.Text(g, UiText.T("等待数据"), F(UiMetrics.BodySize, false), p.Muted, plot, StringAlignment.Center);
        }
        void Series(Graphics g, List<TrendPoint> points, Func<DateTime, float> xOf, Func<TrendPoint, float?> value, Color color, bool area)
        {
            List<PointF> run = new List<PointF>();
            Action flush = delegate
            {
                if (run.Count >= 2)
                {
                    if (area)
                    {
                        List<PointF> poly = new List<PointF>(run); poly.Add(new PointF(run[run.Count - 1].X, plot.Bottom)); poly.Add(new PointF(run[0].X, plot.Bottom));
                        using (Brush b = new SolidBrush(Color.FromArgb(34, color))) g.FillPolygon(b, poly.ToArray());
                    }
                    using (Pen pen = new Pen(color, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, run.ToArray());
                }
                run.Clear();
            };
            foreach (TrendPoint t in points)
            {
                float? v = value(t);
                if (!v.HasValue) { flush(); continue; }
                run.Add(new PointF(xOf(t.Time), plot.Bottom - Math.Max(0, Math.Min(1, v.Value)) * plot.Height));
            }
            flush();
        }
        // ---- settings ----
        float PaintSettings(Graphics g, UiPalette p, float width)
        {
            HideInput();
            float y = 4; Font body = F(UiMetrics.BodySize, false), small = F(UiMetrics.SmallSize, false);
            IList<UiPalette> themes = UiTheme.Available;
            const float swW = 168, swH = 118, swGap = 14;
            int perRow = Math.Max(1, (int)((width - 2 * UiMetrics.Pad + swGap) / (swW + swGap)));
            int rows = (themes.Count + perRow - 1) / perRow;
            KeyValuePair<string, string>[] external = PreviewLanguages ?? ExternalLanguages();
            float langExtra = MoreLanguages && external.Length > 0 ? 50 : 0;
            float appearance = 52 + rows * (swH + 36) + 4 + 32 + 44 + 24 + langExtra;
            RectangleF r = CardAt(g, p, 0, y, width, appearance); Title(g, p, r, UiText.T("外观"));
            for (int i = 0; i < themes.Count; i++)
            {
                UiPalette t = themes[i]; float sx = r.X + UiMetrics.Pad + (i % perRow) * (swW + swGap), sy = r.Y + 52 + (i / perRow) * (swH + 36);
                RectangleF box = new RectangleF(sx, sy, swW, swH); bool on = UiTheme.Current.Id == t.Id, hot = hover == "theme:" + t.Id;
                UiPaint.Fill(g, box, 14, t.Background);
                RectangleF mini = new RectangleF(box.X + 14, box.Y + 14, box.Width - 28, box.Height - 28);
                UiPaint.Fill(g, mini, 10, t.Surface); UiPaint.Outline(g, mini, 10, Color.FromArgb(140, t.Edge), 1);
                UiPaint.Fill(g, new RectangleF(mini.X + 12, mini.Y + 12, 52, 8), 4, t.Ink);
                UiPaint.Fill(g, new RectangleF(mini.X + 12, mini.Y + 28, mini.Width - 24, 7), 3.5f, t.SurfaceAlt);
                UiPaint.Fill(g, new RectangleF(mini.X + 12, mini.Y + 28, (mini.Width - 24) * .55f, 7), 3.5f, t.Accent);
                UiPaint.Fill(g, new RectangleF(mini.X + 12, mini.Bottom - 22, 34, 12), 6, t.Accent);
                UiPaint.Outline(g, box, 14, on ? p.Accent : hot ? p.Muted : Color.FromArgb(150, p.Edge), on ? 3 : 1);
                if (on) { UiPaint.Fill(g, new RectangleF(box.Right - 30, box.Y + 6, 24, 24), 12, p.Accent); UiPaint.Check(g, new RectangleF(box.Right - 30, box.Y + 6, 24, 24), p.AccentInk); }
                string name = t.IsCustom ? t.Name : UiText.T(t.Id == "white" ? "白色" : t.Id == "black" ? "黑色" : "紫黑色");
                UiPaint.Text(g, name, body, on ? p.Ink : p.Muted, new RectangleF(box.X, box.Bottom + 6, box.Width, 24), StringAlignment.Center);
                hits.Add(new UiHit { Id = "theme:" + t.Id, Rect = box });
            }
            float ly = r.Y + 52 + rows * (swH + 36) + 4;
            UiPaint.Text(g, UiText.T("语言"), F(UiMetrics.TitleSize, true), p.Muted, new RectangleF(r.X + UiMetrics.Pad, ly, 200, 24));
            ly += 32;
            string[] codes = { "zh-CN", "zh-TW", "en" }, labels = { "简中", "繁中", "English" }; string current = UiText.Language;
            float seg = 104, lx = r.X + UiMetrics.Pad; RectangleF rail = new RectangleF(lx, ly, seg * 3 + 8, 44);
            UiPaint.Fill(g, rail, 16, p.SurfaceAlt);
            bool packActive = false; foreach (KeyValuePair<string, string> e in external) if (e.Key == current) packActive = true;
            for (int i = 0; i < 3; i++)
            {
                RectangleF s = new RectangleF(lx + 4 + i * seg, ly + 4, seg, 36);
                bool on = !packActive && (i == 0 && current == "zh-Hans" || i == 1 && current == "zh-Hant" || i == 2 && current == "en");
                if (on) UiPaint.Fill(g, s, 13, p.Accent); else if (hover == "lang:" + codes[i]) UiPaint.Fill(g, s, 13, p.Selection);
                UiPaint.Text(g, labels[i], F(UiMetrics.BodySize, on), on ? p.AccentInk : p.Ink, s, StringAlignment.Center);
                hits.Add(new UiHit { Id = "lang:" + codes[i], Rect = s });
            }
            if (external.Length > 0)
            {
                RectangleF more = new RectangleF(rail.Right + 14, ly, 112, 44);
                Button(g, p, "langmore", more, UiText.T("更多") + (MoreLanguages ? "  ▴" : "  ▾"), packActive, true);
                if (MoreLanguages)
                {
                    float cx = lx;
                    foreach (KeyValuePair<string, string> e in external)
                    {
                        float cw = UiPaint.Measure(g, e.Value, body) + 32; RectangleF chip = new RectangleF(cx, ly + 54, cw, 36);
                        bool on = e.Key == current;
                        UiPaint.Fill(g, chip, 14, on ? p.Accent : hover == "lang:" + e.Key ? p.Selection : p.SurfaceAlt);
                        UiPaint.Text(g, e.Value, body, on ? p.AccentInk : p.Ink, chip, StringAlignment.Center);
                        hits.Add(new UiHit { Id = "lang:" + e.Key, Rect = chip }); cx += cw + 8;
                    }
                }
            }
            y += appearance + UiMetrics.Gap;
            // general
            r = CardAt(g, p, 0, y, width, 160); Title(g, p, r, UiText.T("通用"));
            Toggle(g, p, r, 52, "toggle:deck", UiText.T("显示 deck"), UiText.T("在桌面角落显示转速与温度的小卡片，由托盘程序提供。"), DeckOn);
            Toggle(g, p, r, 104, "toggle:startup", UiText.T("登录后启动"), UiText.T("登录 Windows 后自动运行托盘程序。"), StartupOn);
            y += 160 + UiMetrics.Gap;
            // tools
            float toolsH = string.IsNullOrEmpty(ExportPath) ? 112 : 148;
            r = CardAt(g, p, 0, y, width, toolsH); Title(g, p, r, UiText.T("工具"));
            string[] ids = { "tool:export", "tool:themes", "tool:languages" }, names = { UiText.T("导出当前数据"), UiText.T("打开皮肤文件夹"), UiText.T("打开语言文件夹") };
            float bx = r.X + UiMetrics.Pad;
            for (int i = 0; i < 3; i++)
            {
                float bw = UiPaint.Measure(g, names[i], body) + 44; Button(g, p, ids[i], new RectangleF(bx, r.Y + 52, bw, 44), names[i], false, true); bx += bw + 12;
            }
            if (!string.IsNullOrEmpty(ExportPath))
            {
                // Small muted line under the buttons; clicking it opens the folder.
                RectangleF line = new RectangleF(r.X + UiMetrics.Pad, r.Y + 104, width - 2 * UiMetrics.Pad, 28);
                UiPaint.Text(g, UiText.F("导出到 {0}", ExportPath), small, hover == "tool:exportdir" ? p.Accent : p.Muted, line);
                hits.Add(new UiHit { Id = "tool:exportdir", Rect = line });
            }
            y += toolsH + UiMetrics.Gap;
            // ignored files
            string ignored = UiText.IgnoredSummary();
            if (ignored.Length > 0)
            {
                float h = UiPaint.Paragraph(g, ignored, small, p.Warning, new RectangleF(0, 0, width - 2 * UiMetrics.Pad, 0), false);
                r = CardAt(g, p, 0, y, width, h + 32);
                UiPaint.Paragraph(g, ignored, small, p.Warning, new RectangleF(r.X + UiMetrics.Pad, r.Y + 16, width - 2 * UiMetrics.Pad, h), true);
                y += h + 32 + UiMetrics.Gap;
            }
            // about
            string about = UiText.T("DellFans 与 Dell Inc. 无关；Dell 是其所有者的商标。");
            float ah = UiPaint.Paragraph(g, about, small, p.Muted, new RectangleF(0, 0, width - 2 * UiMetrics.Pad, 0), false);
            r = CardAt(g, p, 0, y, width, 72 + ah + 20); Title(g, p, r, UiText.T("关于"));
            UiPaint.Text(g, UiText.F("版本 {0}", Version()), body, p.Ink, new RectangleF(r.X + UiMetrics.Pad, r.Y + 44, 200, 24));
            UiPaint.Paragraph(g, about, small, p.Muted, new RectangleF(r.X + UiMetrics.Pad, r.Y + 72, width - 2 * UiMetrics.Pad, ah), true);
            return y + 72 + ah + 20;
        }
        static string Version()
        {
            try { Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; } catch (Exception) { return "0.9.0"; }
        }
        public static KeyValuePair<string, string>[] ExternalLanguages()
        {
            return UiText.AvailableLanguages.Where(l => l.Key != "zh-Hans" && l.Key != "zh-Hant" && l.Key != "en").ToArray();
        }
        void Toggle(Graphics g, UiPalette p, RectangleF card, float dy, string id, string title, string detail, bool on)
        {
            UiPaint.Text(g, title, F(UiMetrics.BodySize, true), p.Ink, new RectangleF(card.X + UiMetrics.Pad, card.Y + dy, card.Width - 140, 24));
            UiPaint.Text(g, detail, F(UiMetrics.SmallSize, false), p.Muted, new RectangleF(card.X + UiMetrics.Pad, card.Y + dy + 22, card.Width - 140, 22));
            RectangleF sw = new RectangleF(card.Right - UiMetrics.Pad - 52, card.Y + dy + 8, 52, 30);
            UiPaint.Fill(g, sw, 15, on ? p.Accent : hover == id ? p.Edge : p.SurfaceAlt);
            if (!on) UiPaint.Outline(g, new RectangleF(sw.X + .5f, sw.Y + .5f, sw.Width - 1, sw.Height - 1), 15, p.Edge, 1);
            UiPaint.Dot(g, on ? sw.Right - 15 : sw.X + 15, sw.Y + 15, 22, on ? p.AccentInk : p.Muted);
            hits.Add(new UiHit { Id = id, Rect = new RectangleF(card.X + UiMetrics.Pad, card.Y + dy - 4, card.Width - 2 * UiMetrics.Pad, 52) });
        }
        // ---- input ----
        UiHit HitAt(Point client)
        {
            float x = client.X / scale, y = client.Y / scale;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                UiHit h = hits[i]; RectangleF r = h.Rect;
                if (!h.Fixed) { if (y < HeaderHeight) continue; r.Offset(UiMetrics.Margin, HeaderHeight - scrollY); }
                if (r.Contains(x, y)) return h;
            }
            return null;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            drawingForPaint = true;
            try { Render(e.Graphics, new SizeF(ClientSize.Width / DpiScale, ClientSize.Height / DpiScale), DpiScale); }
            finally { drawingForPaint = false; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        public string HitIdAt(Point client) { UiHit h = HitAt(client); return h == null ? null : h.Id; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UiHit h = HitAt(e.Location); string id = h == null ? null : h.Id;
            float? hx = null;
            if (Page == UiPage.Details && State.Trend.Count > 0)
            {
                float x = e.X / scale - UiMetrics.Margin, y = e.Y / scale - HeaderHeight + scrollY;
                if (plot.Contains(x, y) || RectangleF.Inflate(plot, 0, 30).Contains(x, y) && x >= plot.Left && x <= plot.Right) hx = x;
            }
            bool same = id == hover && hx.HasValue == hoverX.HasValue && (!hx.HasValue || Math.Abs(hx.Value - hoverX.Value) < 2);
            hover = id; hoverX = hx; Cursor = id != null ? Cursors.Hand : Cursors.Default;
            if (!same) Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); UiHit h = HitAt(e.Location); pressed = h == null || e.Button != MouseButtons.Left ? null : h.Id; if (pressed != null) Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (pressed != null) { pressed = null; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != null || hoverX.HasValue) { hover = null; hoverX = null; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e); Focus();
            UiHit h = HitAt(e.Location);
            if (h != null && e.Button == MouseButtons.Left) Raise(h.Id);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e); Scroll(-e.Delta / 120f * 48);
        }
        public void Scroll(float delta)
        {
            float max = Math.Max(0, contentHeight - (ClientSize.Height / scale - HeaderHeight)), next = Math.Max(0, Math.Min(max, scrollY + delta));
            if (next != scrollY) { scrollY = next; Invalidate(); }
        }
        public void ResetScroll() { scrollY = 0; }
        void Raise(string id) { Action<string> handler = Activated; if (handler != null) handler(id); }
        // Keyboard: Tab / Shift+Tab walks the controls, Enter or Space activates, arrows scroll.
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Tab || k == Keys.Enter || k == Keys.Space || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Tab && hits.Count > 0)
            {
                int index = hits.FindIndex(h => h.Id == focus);
                index = e.Shift ? (index <= 0 ? hits.Count - 1 : index - 1) : (index + 1) % hits.Count;
                focus = hits[index].Id; EnsureVisible(hits[index]); Invalidate(); e.Handled = true;
            }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && focus != null && hits.Any(h => h.Id == focus)) { Raise(focus); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Scroll(48); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { Scroll(-48); e.Handled = true; }
        }
        void EnsureVisible(UiHit h)
        {
            if (h.Fixed) return;
            float view = ClientSize.Height / scale - HeaderHeight;
            if (h.Rect.Bottom - scrollY > view) Scroll(h.Rect.Bottom - scrollY - view + 16); else if (h.Rect.Top - scrollY < 0) Scroll(h.Rect.Top - scrollY - 16);
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); focus = null; Invalidate(); }
        // Test and preview hook: activates a control by id exactly as a click would.
        public void Activate(string id) { Raise(id); }
        public void SetFocusId(string id) { focus = id; }
    }
}

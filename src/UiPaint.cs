using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DellFans
{
    // Drawing primitives in logical pixels; the caller sets the scale transform. Nothing here keeps a GDI handle between calls.
    public static class UiPaint
    {
        static readonly StringFormat line = Format(false), wrap = Format(true);
        static StringFormat Format(bool wrapped)
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            if (!wrapped) f.FormatFlags |= StringFormatFlags.NoWrap;
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }
        public static void Quality(Graphics g)
        { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality; }
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Max(0.01f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        public static void Fill(Graphics g, RectangleF r, float radius, Color color)
        { using (GraphicsPath p = Round(r, radius)) using (Brush b = new SolidBrush(color)) g.FillPath(b, p); }
        public static void Outline(Graphics g, RectangleF r, float radius, Color color, float width)
        { using (GraphicsPath p = Round(r, radius)) using (Pen pen = new Pen(color, width)) g.DrawPath(pen, p); }
        // A card is a soft shadow, a filled body and a hairline edge. strength is UiPalette.ShadowStrength (0-100).
        public static void Card(Graphics g, RectangleF r, float radius, UiPalette p)
        {
            const int layers = 12; const float spread = 16, drop = 5;
            if (p.ShadowStrength > 0)
                for (int i = layers; i >= 1; i--)
                {
                    float grow = spread * i / layers, t = 1f - (float)(i - 1) / layers;
                    int alpha = (int)Math.Round(255 * p.ShadowStrength / 100f * 0.032f * (0.4f + t));
                    RectangleF s = new RectangleF(r.X - grow, r.Y - grow + drop, r.Width + 2 * grow, r.Height + 2 * grow);
                    Fill(g, s, radius + grow, Color.FromArgb(Math.Min(255, alpha), 0, 0, 0));
                }
            Fill(g, r, radius, p.Surface);
            Outline(g, new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), radius, Color.FromArgb(120, p.Edge), 1);
        }
        public static void Text(Graphics g, string text, Font font, Color color, RectangleF r, StringAlignment horizontal)
        {
            if (string.IsNullOrEmpty(text)) return;
            line.Alignment = horizontal;
            using (Brush b = new SolidBrush(color)) g.DrawString(text, font, b, r, line);
        }
        public static void Text(Graphics g, string text, Font font, Color color, RectangleF r)
        { Text(g, text, font, color, r, StringAlignment.Near); }
        // Wrapped, top-aligned paragraph; returns the height it needs.
        public static float Paragraph(Graphics g, string text, Font font, Color color, RectangleF r, bool draw)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            wrap.Alignment = StringAlignment.Near; wrap.LineAlignment = StringAlignment.Near;
            SizeF size = g.MeasureString(text, font, new SizeF(r.Width, 10000), wrap);
            if (draw) using (Brush b = new SolidBrush(color)) g.DrawString(text, font, b, r, wrap);
            wrap.LineAlignment = StringAlignment.Center;
            return (float)Math.Ceiling(size.Height);
        }
        public static float Measure(Graphics g, string text, Font font)
        { line.Alignment = StringAlignment.Near; return (float)Math.Ceiling(g.MeasureString(text ?? "", font, new SizeF(10000, 1000), line).Width); }
        public static Color Blend(Color back, Color front, float amount)
        {
            return Color.FromArgb((int)(back.R + (front.R - back.R) * amount), (int)(back.G + (front.G - back.G) * amount), (int)(back.B + (front.B - back.B) * amount));
        }
        // Ink that stays readable on a given fill.
        public static Color InkOn(Color fill, Color light, Color dark)
        { return UiTheme.Contrast(light, fill) >= UiTheme.Contrast(dark, fill) ? light : dark; }
        // 270-degree gauge, opening at the bottom. fraction is 0-1.
        public static void Ring(Graphics g, RectangleF r, float thickness, float fraction, Color track, Color value)
        {
            RectangleF inner = new RectangleF(r.X + thickness / 2, r.Y + thickness / 2, r.Width - thickness, r.Height - thickness);
            using (Pen t = new Pen(track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(t, inner, 135, 270);
            fraction = Math.Max(0, Math.Min(1, fraction));
            if (fraction <= 0.002f) return;
            using (Pen v = new Pen(value, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(v, inner, 135, 270 * fraction);
        }
        public static void Bar(Graphics g, RectangleF r, float fraction, Color track, Color value)
        {
            Fill(g, r, r.Height / 2, track);
            fraction = Math.Max(0, Math.Min(1, fraction));
            if (fraction > 0) Fill(g, new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * fraction), r.Height), r.Height / 2, value);
        }
        public static void Check(Graphics g, RectangleF r, Color color)
        {
            using (Pen pen = new Pen(color, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(r.X + r.Width * .24f, r.Y + r.Height * .52f), new PointF(r.X + r.Width * .43f, r.Y + r.Height * .70f), new PointF(r.X + r.Width * .76f, r.Y + r.Height * .32f) });
        }
        public static void Dot(Graphics g, float cx, float cy, float diameter, Color color)
        { using (Brush b = new SolidBrush(color)) g.FillEllipse(b, cx - diameter / 2, cy - diameter / 2, diameter, diameter); }
    }
}

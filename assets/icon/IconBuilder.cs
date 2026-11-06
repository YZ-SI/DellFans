// DellFans icon generator: one geometry source -> SVG, PNG rasters, ICO, preview sheet.
// Build/run through build-icons.ps1. Original geometry, no fonts/assets inside icons.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

static class P
{
    // Accent: blue-violet. White glyph on it reads on white, black and purple-black UIs.
    public static readonly Color AccentA = ColorTranslator.FromHtml("#3F8CFF");    // gradient start (large only)
    public static readonly Color AccentB = ColorTranslator.FromHtml("#5546D9");    // gradient end (large only)
    public static readonly Color AccentFlat = ColorTranslator.FromHtml("#3A6BE6"); // small sizes, no gradient
    public static readonly Color TrayDark = ColorTranslator.FromHtml("#1F1F1F");   // glyph on light taskbar
    public static readonly Color TrayLight = Color.White;                          // glyph on dark taskbar
}

abstract class Prim { public Color Fill; public float Opacity = 1f; }
class RRect : Prim { public float X, Y, W, H, R; public Color? Fill2; }
class Poly : Prim { public PointF[] Pts; }
class Disc : Prim { public float Cx, Cy, R, Hole; } // Hole>0 => annulus

class Scene
{
    public string Name; public float V; public List<Prim> Items = new List<Prim>();
}

static class Geo
{
    // Swept blade. Result is scaled so its farthest point is exactly 'reach' from (cx,cy).
    // rIn/hBase/hTip are fractions of the unit blade length (rOut = 1).
    public static PointF[] Blade(double cx, double cy, double angleDeg, double rIn, double hBase, double hTip,
        double sweepDeg, double reach, double rightFactor)
    {
        const int N = 40, TIP = 12;
        Func<double, double[]> center = t =>
        {
            double r = rIn + (1.0 - rIn) * t, th = sweepDeg * Math.PI / 180 * Math.Pow(t, 1.4);
            return new[] { r * Math.Cos(th), r * Math.Sin(th) };
        };
        Func<double, double> hw = t => hBase + (hTip - hBase) * Math.Pow(t, 0.8);
        var L = new List<double[]>(); var R = new List<double[]>();
        double[] nTip = null, pTip = null; double hLt = 0, hRt = 0;
        for (int i = 0; i <= N; i++)
        {
            double t = (double)i / N, e = 1e-4;
            double[] p = center(t), p2 = center(Math.Min(1, t + e)), p1 = center(Math.Max(0, t - e));
            double dx = p2[0] - p1[0], dy = p2[1] - p1[1], len = Math.Sqrt(dx * dx + dy * dy);
            double nx = dy / len, ny = -dx / len; // normal toward the trailing side
            double h = hw(t);
            L.Add(new[] { p[0] - nx * h, p[1] - ny * h });
            R.Add(new[] { p[0] + nx * h * rightFactor, p[1] + ny * h * rightFactor });
            if (i == N) { nTip = new[] { nx, ny }; hLt = h; hRt = h * rightFactor; pTip = p; }
        }
        var pts = new List<double[]>(L);
        // rounded tip: half turn from the left edge round the outward tangent to the right edge
        double tgx = -nTip[1], tgy = nTip[0];
        double sgn = (tgx * pTip[0] + tgy * pTip[1]) >= 0 ? 1 : -1;
        for (int k = 1; k < TIP; k++)
        {
            double u = (double)k / TIP, ang = Math.PI * u, hh = hLt + (hRt - hLt) * u;
            double ux = -nTip[0] * Math.Cos(ang) + sgn * tgx * Math.Sin(ang);
            double uy = -nTip[1] * Math.Cos(ang) + sgn * tgy * Math.Sin(ang);
            pts.Add(new[] { pTip[0] + ux * hh, pTip[1] + uy * hh });
        }
        for (int i = R.Count - 1; i >= 0; i--) pts.Add(R[i]);
        double max = 0; foreach (var q in pts) max = Math.Max(max, Math.Sqrt(q[0] * q[0] + q[1] * q[1]));
        double s = reach / max, ca = Math.Cos(angleDeg * Math.PI / 180), sa = Math.Sin(angleDeg * Math.PI / 180);
        var res = new PointF[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            double x = pts[i][0] * s, y = pts[i][1] * s;
            res[i] = new PointF((float)(cx + x * ca - y * sa), (float)(cy + x * sa + y * ca));
        }
        return res;
    }

    public static void Blades(Scene s, int n, double cx, double cy, double rIn, double hBase, double hTip,
        double sweep, double reach, double rightFactor, Color col)
    {
        for (int i = 0; i < n; i++)
            s.Items.Add(new Poly { Fill = col, Pts = Blade(cx, cy, 360.0 * i / n - 90, rIn, hBase, hTip, sweep, reach, rightFactor) });
    }
}

static class Scenes
{
    public static Scene Master()
    {
        var s = new Scene { Name = "dellfans", V = 256 };
        s.Items.Add(new RRect { X = 10, Y = 10, W = 236, H = 236, R = 56, Fill = P.AccentA, Fill2 = P.AccentB });
        s.Items.Add(new Disc { Cx = 128, Cy = 128, R = 103, Hole = 98, Fill = Color.White, Opacity = 0.30f }); // housing ring
        Geo.Blades(s, 7, 128, 128, 0.10, 0.10, 0.24, 38, 88, 0.8, Color.White);
        s.Items.Add(new Disc { Cx = 128, Cy = 128, R = 25, Fill = Color.White });
        s.Items.Add(new Disc { Cx = 128, Cy = 128, R = 10, Fill = P.AccentB });
        return s;
    }
    public static Scene Small()
    {
        var s = new Scene { Name = "dellfans-small", V = 32 };
        s.Items.Add(new RRect { X = 0, Y = 0, W = 32, H = 32, R = 7, Fill = P.AccentFlat });
        Geo.Blades(s, 5, 16, 16, 0.12, 0.19, 0.32, 36, 13.5, 0.85, Color.White);
        s.Items.Add(new Disc { Cx = 16, Cy = 16, R = 4f, Fill = Color.White });
        return s;
    }
    public static Scene Tray(string name, Color c)
    {
        var s = new Scene { Name = name, V = 32 };
        Geo.Blades(s, 5, 16, 16, 0.12, 0.2, 0.33, 36, 15, 0.85, c);
        s.Items.Add(new Disc { Cx = 16, Cy = 16, R = 5f, Hole = 1.9f, Fill = c });
        return s;
    }
}

static class Svg
{
    static string F(double v) { return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }
    static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
    static string Op(Prim p) { return p.Opacity < 1f ? " fill-opacity=\"" + F(p.Opacity) + "\"" : ""; }

    public static string Write(Scene s)
    {
        var sb = new StringBuilder();
        string v = F(s.V);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 " + v + " " + v + "\" width=\"" + v + "\" height=\"" + v + "\">\n");
        sb.Append("  <title>DellFans " + s.Name + "</title>\n");
        var grad = s.Items.Find(x => x is RRect && ((RRect)x).Fill2.HasValue) as RRect;
        if (grad != null)
            sb.Append("  <defs><linearGradient id=\"bg\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"" + Hex(grad.Fill) + "\"/><stop offset=\"1\" stop-color=\"" + Hex(grad.Fill2.Value) + "\"/></linearGradient></defs>\n");
        foreach (var p in s.Items)
        {
            var rr = p as RRect; var po = p as Poly; var d = p as Disc;
            if (rr != null)
                sb.Append("  <rect x=\"" + F(rr.X) + "\" y=\"" + F(rr.Y) + "\" width=\"" + F(rr.W) + "\" height=\"" + F(rr.H) + "\" rx=\"" + F(rr.R) + "\" fill=\"" + (rr.Fill2.HasValue ? "url(#bg)" : Hex(rr.Fill)) + "\"/>\n");
            else if (po != null)
            {
                sb.Append("  <path fill=\"" + Hex(po.Fill) + "\"" + Op(po) + " d=\"M");
                for (int i = 0; i < po.Pts.Length; i++) sb.Append((i == 0 ? "" : "L") + F(po.Pts[i].X) + " " + F(po.Pts[i].Y));
                sb.Append("Z\"/>\n");
            }
            else if (d != null)
            {
                if (d.Hole > 0)
                    sb.Append("  <path fill=\"" + Hex(d.Fill) + "\"" + Op(d) + " fill-rule=\"evenodd\" d=\"" + Circ(d.Cx, d.Cy, d.R) + Circ(d.Cx, d.Cy, d.Hole) + "\"/>\n");
                else
                    sb.Append("  <circle cx=\"" + F(d.Cx) + "\" cy=\"" + F(d.Cy) + "\" r=\"" + F(d.R) + "\" fill=\"" + Hex(d.Fill) + "\"" + Op(d) + "/>\n");
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }
    static string Circ(double cx, double cy, double r)
    {
        return "M" + F(cx - r) + " " + F(cy) + "a" + F(r) + " " + F(r) + " 0 1 0 " + F(2 * r) + " 0a" + F(r) + " " + F(r) + " 0 1 0 " + F(-2 * r) + " 0Z";
    }
}

static class Raster
{
    public static Bitmap Render(Scene s, int px)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.ScaleTransform(px / s.V, px / s.V);
            foreach (var p in s.Items)
            {
                Color c = Color.FromArgb((int)Math.Round(p.Opacity * 255), p.Fill);
                var rr = p as RRect; var po = p as Poly; var d = p as Disc;
                if (rr != null)
                {
                    using (var path = RoundRect(rr))
                    {
                        if (rr.Fill2.HasValue)
                            using (var b = new LinearGradientBrush(new RectangleF(rr.X, rr.Y, rr.W, rr.H), rr.Fill, rr.Fill2.Value, LinearGradientMode.ForwardDiagonal)) g.FillPath(b, path);
                        else using (var b = new SolidBrush(c)) g.FillPath(b, path);
                    }
                }
                else if (po != null) using (var b = new SolidBrush(c)) g.FillPolygon(b, po.Pts, FillMode.Winding);
                else if (d != null)
                    using (var path = new GraphicsPath(FillMode.Alternate))
                    using (var b = new SolidBrush(c))
                    {
                        path.AddEllipse(d.Cx - d.R, d.Cy - d.R, 2 * d.R, 2 * d.R);
                        if (d.Hole > 0) path.AddEllipse(d.Cx - d.Hole, d.Cy - d.Hole, 2 * d.Hole, 2 * d.Hole);
                        g.FillPath(b, path);
                    }
            }
        }
        return bmp;
    }
    static GraphicsPath RoundRect(RRect r)
    {
        var p = new GraphicsPath(); float d = r.R * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.X + r.W - d, r.Y, d, d, 270, 90);
        p.AddArc(r.X + r.W - d, r.Y + r.H - d, d, d, 0, 90); p.AddArc(r.X, r.Y + r.H - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

static class Ico
{
    // BMP (32bpp + AND mask) entries up to 48 px for max shell compatibility, PNG from 64 px up.
    public static void Write(string path, List<KeyValuePair<int, Bitmap>> imgs)
    {
        var blobs = new List<byte[]>();
        foreach (var kv in imgs) blobs.Add(kv.Key >= 64 ? Png(kv.Value) : Dib(kv.Value));
        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)imgs.Count);
            int off = 6 + 16 * imgs.Count;
            for (int i = 0; i < imgs.Count; i++)
            {
                int sz = imgs[i].Key;
                w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
                w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
                w.Write(blobs[i].Length); w.Write(off); off += blobs[i].Length;
            }
            foreach (var b in blobs) w.Write(b);
        }
    }
    static byte[] Png(Bitmap b) { using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); } }
    static byte[] Dib(Bitmap b)
    {
        int n = b.Width, h = b.Height;
        var data = b.LockBits(new Rectangle(0, 0, n, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] px = new byte[n * h * 4];
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), px, y * n * 4, n * 4);
        b.UnlockBits(data);
        int maskRow = ((n + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(h * 2); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(0); w.Write(n * h * 4 + maskRow * h); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = h - 1; y >= 0; y--) w.Write(px, y * n * 4, n * 4); // bottom-up BGRA
            w.Write(new byte[maskRow * h]); // AND mask left empty: alpha channel does the work
            return ms.ToArray();
        }
    }
}

static class Preview
{
    static readonly Color Purple = ColorTranslator.FromHtml("#1A1625");
    static Bitmap Pick(Func<Scene, int, Bitmap> r, Scene m, Scene s, int n) { return r(n <= 32 ? s : m, n); }

    public static void Write(string path, Func<Scene, int, Bitmap> render, Scene master, Scene small, Scene trayL, Scene trayD)
    {
        int PW = 560, PH = 620, W = PW * 3, H = PH + 330;
        using (var bmp = new Bitmap(W, H))
        using (var g = Graphics.FromImage(bmp))
        using (var f = new Font("Segoe UI", 9f))
        using (var f2 = new Font("Segoe UI", 8f))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
            Color[] bgs = { Color.White, Color.Black, Purple };
            string[] names = { "white", "black", "purple-black #1A1625" };
            int[] nat = { 16, 20, 24, 32, 40, 48, 64 };
            for (int i = 0; i < 3; i++)
            {
                int ox = i * PW; Color fg = i == 0 ? Color.Black : Color.White;
                using (var b = new SolidBrush(bgs[i])) g.FillRectangle(b, ox, 0, PW, PH);
                using (var tb = new SolidBrush(fg)) g.DrawString(names[i] + "   (16 20 24 32 40 48 64 | 128 | 256 | zoom 16/24/32)", f, tb, ox + 10, 6);
                int x = ox + 16, y = 34;
                foreach (int n in nat) { g.DrawImageUnscaled(Pick(render, master, small, n), x, y + (64 - n) / 2); x += n + 14; }
                g.DrawImageUnscaled(render(master, 128), ox + 16, 120);
                g.DrawImageUnscaled(render(master, 256), ox + 16 + 128 + 16, 120);
                int zy = 400; x = ox + 16;
                foreach (int[] z in new[] { new[] { 16, 10 }, new[] { 24, 6 }, new[] { 32, 4 } })
                { g.DrawImage(Pick(render, master, small, z[0]), x, zy, z[0] * z[1], z[0] * z[1]); x += z[0] * z[1] + 12; }
                g.DrawImageUnscaled(render(small, 48), ox + 16, zy + 175);
                using (var tb = new SolidBrush(fg)) g.DrawString("small-variant @48, for comparison with master @48 above", f2, tb, ox + 74, zy + 190);
            }
            int[] ts = { 16, 20, 24, 32, 40, 48 };
            for (int k = 0; k < 2; k++)
            {
                Color bg = k == 0 ? ColorTranslator.FromHtml("#F3F3F3") : ColorTranslator.FromHtml("#202020");
                Scene sc = k == 0 ? trayL : trayD;
                int y0 = PH + k * 165;
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, y0, W, 165);
                using (var tb = new SolidBrush(k == 0 ? Color.Black : Color.White)) g.DrawString(k == 0 ? "tray-light (light taskbar)" : "tray-dark (dark taskbar)", f, tb, 10, y0 + 4);
                int x = 16;
                foreach (int n in ts) { g.DrawImageUnscaled(render(sc, n), x, y0 + 40 + (48 - n) / 2); x += n + 16; }
                x += 30;
                foreach (int[] z in new[] { new[] { 16, 8 }, new[] { 24, 5 }, new[] { 32, 4 }, new[] { 48, 3 } })
                { g.DrawImage(render(sc, z[0]), x, y0 + 20, z[0] * z[1], z[0] * z[1]); x += z[0] * z[1] + 14; }
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}

static class Program
{
    static int Main(string[] a)
    {
        string dir = a.Length > 0 ? a[0] : ".";
        var master = Scenes.Master(); var small = Scenes.Small();
        var trayL = Scenes.Tray("tray-light", P.TrayDark); var trayD = Scenes.Tray("tray-dark", P.TrayLight);
        foreach (var s in new[] { master, small, trayL, trayD })
            File.WriteAllText(Path.Combine(dir, s.Name + ".svg"), Svg.Write(s), new UTF8Encoding(false));

        Func<Scene, int, Bitmap> render = Raster.Render;
        var app = new List<KeyValuePair<int, Bitmap>>();
        foreach (int n in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
            app.Add(new KeyValuePair<int, Bitmap>(n, render(n <= 32 ? small : master, n)));
        Ico.Write(Path.Combine(dir, "dellfans.ico"), app);
        foreach (var sc in new[] { trayL, trayD })
        {
            var l = new List<KeyValuePair<int, Bitmap>>();
            foreach (int n in new[] { 16, 20, 24, 32, 40, 48 }) l.Add(new KeyValuePair<int, Bitmap>(n, render(sc, n)));
            Ico.Write(Path.Combine(dir, sc.Name + ".ico"), l);
        }
        Preview.Write(Path.Combine(dir, "preview.png"), render, master, small, trayL, trayD);
        Console.WriteLine("ok");
        return 0;
    }
}

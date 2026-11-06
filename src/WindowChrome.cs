using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DellFans
{
    public static class ChromeSurface
    {
        public const int ShadowMargin=12;
        public static Bitmap CreateWithShadow(Size size,int radius,Color color)
        {
            using(Bitmap body=CreateBitmap(size,radius,color))
            {
                Bitmap result=new Bitmap(size.Width+ShadowMargin*2,size.Height+ShadowMargin*2,PixelFormat.Format32bppPArgb);
                using(Graphics g=Graphics.FromImage(result))
                {
                    g.SmoothingMode=SmoothingMode.AntiAlias;
                    int strength=color.R+color.G+color.B<180 ? 30 : 20;
                    for(int spread=ShadowMargin-4;spread>=1;spread--)
                    {
                        int distance=ShadowMargin-3-spread,alpha=Math.Max(1,strength*(distance*distance-(distance-1)*(distance-1))/64);
                        using(GraphicsPath path=Rounded(new RectangleF(ShadowMargin-spread+.5f,ShadowMargin-spread+2.5f,size.Width+spread*2-1,size.Height+spread*2-1),radius+spread))
                        using(Brush fill=new SolidBrush(Color.FromArgb(alpha,0,0,0))) g.FillPath(fill,path);
                    }
                    g.DrawImageUnscaled(body,ShadowMargin,ShadowMargin);
                }
                return result;
            }
        }
        static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath(); radius = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height)/2));
            if (radius < 1) { path.AddRectangle(bounds); return path; }
            float d = radius*2;
            path.AddArc(bounds.Left,bounds.Top,d,d,180,90); path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
            path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90); path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90); path.CloseFigure(); return path;
        }
        public static Bitmap CreateBitmap(Size size, int radius, Color color)
        {
            if(size.Width<1 || size.Height<1 || size.Width>2048 || size.Height>2048 || radius<0 || color.A!=255) throw new ArgumentOutOfRangeException("size/radius/color");
            if(size.Width*size.Height>262144) return PaintSurface(size,radius,color);
            const int samples=3;
            using(Bitmap high=PaintSurface(new Size(size.Width*samples,size.Height*samples),radius*samples,color))
            {
                Bitmap result=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
                using(Graphics g=Graphics.FromImage(result))
                { g.CompositingMode=CompositingMode.SourceCopy; g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.DrawImage(high,new Rectangle(Point.Empty,size),0,0,high.Width,high.Height,GraphicsUnit.Pixel); }
                return result;
            }
        }
        static Bitmap PaintSurface(Size size,int radius,Color color)
        {
            Bitmap bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(bitmap))
            using(GraphicsPath path=Rounded(new RectangleF(0,0,size.Width,size.Height),radius))
            using(Brush fill=new SolidBrush(color))
            {g.Clear(Color.Transparent);g.CompositingMode=CompositingMode.SourceCopy;g.SmoothingMode=SmoothingMode.AntiAlias;g.FillPath(fill,path);}
            return bitmap;
        }
        public static Region CreateHostRegion(Size size, int radius)
        {
            const int inset=3;
            if(size.Width<=inset*2 || size.Height<=inset*2) return new Region(Rectangle.Empty);
            using(GraphicsPath path=Rounded(new RectangleF(inset,inset,size.Width-inset*2,size.Height-inset*2),Math.Max(0,radius-inset))) return new Region(path);
        }
    }

    // A click-through alpha surface behind the native controls, repainted only when geometry or skin changes.
    // Windows 10 only: a click-through alpha window behind the host that supplies the soft shadow and the anti-aliased rounded body (0.8 technique).
    sealed class ChromeForm : Form
    {
        readonly bool topmost;
        Size renderedSize;
        int renderedRadius=-1, renderedColor;
        public ChromeForm() : this(true) { }
        public ChromeForm(bool topmost) { this.topmost=topmost; FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; StartPosition=FormStartPosition.Manual; }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { CreateParams p=base.CreateParams; p.ExStyle |= 0x080800A0 | (topmost?8:0); return p; } }
        protected override void WndProc(ref Message message)
        { if(message.Msg==0x21) { message.Result=new IntPtr(3); return; } base.WndProc(ref message); }
        public void Present(Control host, int radius, Color color)
        {
            if(IsDisposed || !host.Visible) return;
            Rectangle bounds=Rectangle.Inflate(host.Bounds,ChromeSurface.ShadowMargin,ChromeSurface.ShadowMargin);
            if(bounds.Size!=renderedSize || radius!=renderedRadius || color.ToArgb()!=renderedColor)
            {
                using(Bitmap bitmap=ChromeSurface.CreateWithShadow(host.Size,radius,color)) Upload(bitmap,bounds.Location);
                renderedSize=bounds.Size; renderedRadius=radius; renderedColor=color.ToArgb();
            }
            else if(Location!=bounds.Location) SetWindowPos(Handle,host.Handle,bounds.Left,bounds.Top,0,0,0x0011);
            if(!Visible) Show();
            if(!SetWindowPos(Handle,host.Handle,bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x0010)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        void Upload(Bitmap image, Point location)
        {
            IntPtr screen=GetDC(IntPtr.Zero), memory=IntPtr.Zero, dib=IntPtr.Zero, old=IntPtr.Zero;
            if(screen==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                memory=CreateCompatibleDC(screen); if(memory==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                BitmapInfo header=new BitmapInfo { Size=40,Width=image.Width,Height=-image.Height,Planes=1,BitCount=32 };
                IntPtr pixels; dib=CreateDIBSection(screen,ref header,0,out pixels,IntPtr.Zero,0);
                if(dib==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                BitmapData data=image.LockBits(new Rectangle(Point.Empty,image.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                try
                {
                    byte[] row=new byte[image.Width*4];
                    for(int y=0;y<image.Height;y++) { Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length); Marshal.Copy(row,0,IntPtr.Add(pixels,y*row.Length),row.Length); }
                }
                finally { image.UnlockBits(data); }
                old=SelectObject(memory,dib);
                if(old==IntPtr.Zero || old==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                NativePoint destination=new NativePoint { X=location.X,Y=location.Y }, source=new NativePoint();
                NativeSize size=new NativeSize { Width=image.Width,Height=image.Height };
                Blend blend=new Blend { Alpha=255,Format=1 };
                if(!UpdateLayeredWindow(Handle,screen,ref destination,ref size,memory,ref source,0,ref blend,2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if(old!=IntPtr.Zero && old!=new IntPtr(-1)) SelectObject(memory,old); if(dib!=IntPtr.Zero) DeleteObject(dib); if(memory!=IntPtr.Zero) DeleteDC(memory); ReleaseDC(IntPtr.Zero,screen); }
        }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width,Height; }
        [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte Operation,Flags,Alpha,Format; }
        [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public uint Size; public int Width,Height; public ushort Planes,BitCount; public uint Compression,ImageSize; public int XPels,YPels; public uint Used,Important; }
        [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr destinationDc,ref NativePoint position,ref NativeSize size,IntPtr sourceDc,ref NativePoint source,uint key,ref Blend blend,uint flags);
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll",SetLastError=true)] static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    }
}

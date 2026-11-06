//! Deck rendering: software-rasterised rounded card (SDF coverage) + GDI text, pushed with UpdateLayeredWindow.
use crate::util::w;
use dfcore::i18n::{tr, Lang, K};
use dfcore::mode::Mode;
use dfcore::status::Status;
use dfcore::theme::Palette;
use std::mem::{size_of, zeroed};
use std::ptr::null_mut;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

/// Shadow margin around the card, logical px.
pub const M: f32 = 14.0;
const CAP: (f32, f32) = (188.0, 44.0);
pub const EXP: (f32, f32) = (264.0, 200.0);

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Hit { None, Mode(Mode), Open, Pin }

#[derive(Clone, Copy)]
pub struct FRect { pub x: f32, pub y: f32, pub w: f32, pub h: f32 }
pub const fn fr(x: f32, y: f32, w: f32, h: f32) -> FRect { FRect { x, y, w, h } }

pub const QUICK: [Mode; 4] = [Mode::Quiet, Mode::Balanced, Mode::Performance, Mode::Custom];
const CHIP_CPU: FRect = fr(14.0, 90.0, 114.0, 30.0);
const CHIP_DISK: FRect = fr(136.0, 90.0, 114.0, 30.0);
const OPEN: FRect = fr(14.0, 164.0, 236.0, 26.0); // 10 px to the card edge
/// Visible pin toggle: a pinned card stays expanded after the pointer leaves.
const PIN: FRect = fr(226.0, 10.0, 26.0, 26.0);
fn button(i: usize) -> FRect { fr(14.0 + 60.0 * i as f32, 128.0, 54.0, 28.0) }

pub fn card_size(expanded: bool) -> (f32, f32) { if expanded { EXP } else { CAP } }

/// Hit test in logical card coordinates (origin = card top-left). Collapsed card has no buttons.
pub fn hit_test(expanded: bool, x: f32, y: f32) -> Hit {
    if !expanded { return Hit::None; }
    let inside = |r: FRect| x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h;
    for (i, m) in QUICK.iter().enumerate() { if inside(button(i)) { return Hit::Mode(*m); } }
    if inside(OPEN) { Hit::Open } else if inside(PIN) { Hit::Pin } else { Hit::None }
}

pub fn mode_color(m: Option<Mode>, muted: u32) -> u32 {
    match m {
        Some(Mode::Quiet) => 0x3FB68B, Some(Mode::Balanced) => 0x4C8DFF, Some(Mode::Performance) => 0xFF8A3D,
        Some(Mode::Custom) => 0xA27BFF, Some(Mode::Auto) => 0x8A8F99, None => muted,
    }
}

pub fn fmt_rpm(n: u32) -> String { if n >= 1000 { format!("{},{:03}", n / 1000, n % 1000) } else { n.to_string() } }
fn fmt_temp(t: Option<f32>) -> String { t.map_or("\u{2014}".into(), |v| format!("{:.0}\u{b0}C", v)) }

pub struct View<'a> {
    pub pal: Palette,
    pub lang: Lang,
    pub st: Option<&'a Status>,
    pub connected: bool,
    pub expanded: bool,
    pub hover: Hit,
    pub pinned: bool,
}

pub struct Surface {
    pub dpi: u32,
    pub lang: Lang,
    dc: HDC,
    bmp: HBITMAP,
    old: HGDIOBJ,
    bits: *mut u32,
    pw: i32,
    ph: i32,
    fonts: [HFONT; 4], // small, medium, big, body
}

fn sdf(px: f32, py: f32, c: (f32, f32), half: (f32, f32), r: f32) -> f32 {
    let qx = (px - c.0).abs() - (half.0 - r);
    let qy = (py - c.1).abs() - (half.1 - r);
    (qx.max(0.0).powi(2) + qy.max(0.0).powi(2)).sqrt() + qx.max(qy).min(0.0) - r
}

/// Source-over of an opaque colour with coverage `a` onto a premultiplied ARGB pixel.
fn blend(px: &mut u32, rgb: u32, a: f32) {
    if a <= 0.003 { return; }
    let a = a.min(1.0);
    let d = *px;
    let ia = 1.0 - a;
    let c = |sh: u32| (((rgb >> sh) & 255) as f32 * a + ((d >> sh) & 255) as f32 * ia + 0.5) as u32;
    let al = (255.0 * a + (d >> 24) as f32 * ia + 0.5) as u32;
    *px = al << 24 | c(16) << 16 | c(8) << 8 | c(0);
}

impl Surface {
    pub fn new(dpi: u32, lang: Lang, max: (f32, f32)) -> Surface {
        unsafe {
            let s = dpi as f32 / 96.0;
            let pw = ((max.0 + 2.0 * M) * s).ceil() as i32;
            let ph = ((max.1 + 2.0 * M) * s).ceil() as i32;
            let mut bi: BITMAPINFO = zeroed();
            bi.bmiHeader.biSize = size_of::<BITMAPINFOHEADER>() as u32;
            bi.bmiHeader.biWidth = pw;
            bi.bmiHeader.biHeight = -ph;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;
            let sdc = GetDC(null_mut());
            let mut bits: *mut core::ffi::c_void = null_mut();
            let bmp = CreateDIBSection(sdc, &bi, DIB_RGB_COLORS, &mut bits, null_mut(), 0);
            let dc = CreateCompatibleDC(sdc);
            ReleaseDC(null_mut(), sdc);
            let old = SelectObject(dc, bmp as _);
            SetBkMode(dc, TRANSPARENT as i32);
            let face = w(if lang == Lang::En { "Segoe UI" } else { "Microsoft YaHei UI" });
            let mk = |px: f32, weight: u32| CreateFontW(-(px * s).round() as i32, 0, 0, 0, weight as i32, 0, 0, 0,
                DEFAULT_CHARSET as u32, 0, 0, CLEARTYPE_QUALITY as u32, 0, face.as_ptr()); // ClearType: text sits on opaque fills (alpha kept in text())
            Surface { dpi, lang, dc, bmp, old, bits: bits as *mut u32, pw, ph,
                fonts: [mk(12.0, 400), mk(14.0, 600), mk(30.0, 700), mk(14.0, 400)] }
        }
    }

    pub fn free(self) {
        unsafe {
            SelectObject(self.dc, self.old);
            for f in self.fonts { DeleteObject(f as _); }
            DeleteObject(self.bmp as _);
            DeleteDC(self.dc);
        }
    }

    fn scale(&self) -> f32 { self.dpi as f32 / 96.0 }

    pub fn fill(&mut self, r: FRect, radius: f32, rgb: u32, alpha: f32) {
        let s = self.scale();
        let (x, y, ww, hh, rad) = ((r.x + M) * s, (r.y + M) * s, r.w * s, r.h * s, (radius * s).min(r.h * s / 2.0));
        let buf = unsafe { std::slice::from_raw_parts_mut(self.bits, (self.pw * self.ph) as usize) };
        let (x0, x1) = ((x.floor() as i32 - 1).max(0), ((x + ww).ceil() as i32 + 1).min(self.pw));
        let (y0, y1) = ((y.floor() as i32 - 1).max(0), ((y + hh).ceil() as i32 + 1).min(self.ph));
        for py in y0..y1 {
            for px in x0..x1 {
                let d = sdf(px as f32 + 0.5, py as f32 + 0.5, (x + ww / 2.0, y + hh / 2.0), (ww / 2.0, hh / 2.0), rad);
                blend(&mut buf[(py * self.pw + px) as usize], rgb, (0.5 - d).clamp(0.0, 1.0) * alpha);
            }
        }
    }

    /// Width of `s` in logical (96 dpi) units.
    pub fn text_width(&self, font: usize, s: &str) -> f32 {
        if s.is_empty() { return 0.0; }
        let t: Vec<u16> = s.encode_utf16().collect();
        let mut size = SIZE { cx: 0, cy: 0 };
        unsafe { SelectObject(self.dc, self.fonts[font] as _); GetTextExtentPoint32W(self.dc, t.as_ptr(), t.len() as i32, &mut size); }
        size.cx as f32 / self.scale()
    }

    pub fn text(&self, font: usize, s: &str, r: FRect, rgb: u32, align: u32) {
        // An empty Vec's pointer is a dangling non-null address; user32 DrawTextW may read it even for count 0
        // (seen as an access violation in user32.dll while the service had no snapshot and the mode name was empty).
        if s.is_empty() { return; }
        let sc = self.scale();
        let t: Vec<u16> = s.encode_utf16().collect();
        let mut rc = RECT { left: ((r.x + M) * sc) as i32, top: ((r.y + M) * sc) as i32,
            right: ((r.x + r.w + M) * sc) as i32, bottom: ((r.y + r.h + M) * sc) as i32 };
        // GDI text zeroes the alpha byte of every pixel it touches, which made glyphs half-transparent on the layered window
        // (thin, washed-out text). Text is always drawn on opaque card/button fills, so keep the alpha that was there.
        let (x0, y0) = (rc.left.max(0), rc.top.max(0));
        let (x1, y1) = (rc.right.min(self.pw), rc.bottom.min(self.ph));
        let buf = unsafe { std::slice::from_raw_parts_mut(self.bits, (self.pw * self.ph) as usize) };
        let mut alpha = Vec::with_capacity(((x1 - x0).max(0) * (y1 - y0).max(0)) as usize);
        for y in y0..y1 { for x in x0..x1 { alpha.push(buf[(y * self.pw + x) as usize] & 0xFF00_0000); } }
        unsafe {
            SelectObject(self.dc, self.fonts[font] as _);
            SetTextColor(self.dc, (rgb & 0xFF) << 16 | (rgb & 0xFF00) | (rgb >> 16));
            DrawTextW(self.dc, t.as_ptr(), t.len() as i32, &mut rc, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | align);
        }
        let mut i = 0;
        for y in y0..y1 { for x in x0..x1 { let p = &mut buf[(y * self.pw + x) as usize]; *p = (*p & 0x00FF_FFFF) | alpha[i]; i += 1; } }
    }

    /// Clears the surface and draws shadow + card body + border; returns the window pixel size to present.
    pub fn draw_card(&mut self, cw: f32, ch: f32, radius: f32, p: &Palette) -> (i32, i32) {
        let s = self.scale();
        let (pw, ph) = (((cw + 2.0 * M) * s).ceil() as i32, ((ch + 2.0 * M) * s).ceil() as i32);
        let (cx, cy, half, r) = ((M + cw / 2.0) * s, (M + ch / 2.0) * s, (cw * s / 2.0, ch * s / 2.0), radius * s);
        let stride = self.pw;
        let buf = unsafe { std::slice::from_raw_parts_mut(self.bits, (self.pw * self.ph) as usize) };
        buf.fill(0);
        let blur = 12.0 * s;
        let sh_a = p.shadow as f32 / 255.0;
        for y in 0..ph {
            for x in 0..pw {
                let (fx, fy) = (x as f32 + 0.5, y as f32 + 0.5);
                let px = &mut buf[(y * stride + x) as usize];
                let ds = sdf(fx, fy, (cx, cy + 3.0 * s), half, r);
                if ds < blur {
                    let t = (1.0 - ds / blur).clamp(0.0, 1.0);
                    blend(px, 0x000000, sh_a * t * t);
                }
                let d = sdf(fx, fy, (cx, cy), half, r);
                if d < 0.5 {
                    let cov = (0.5 - d).clamp(0.0, 1.0);
                    blend(px, p.bg, cov);
                    if d > -1.5 { blend(px, p.border, cov * (1.0 - (-d - 0.5).clamp(0.0, 1.0)) * 0.9); }
                }
            }
        }
        (pw, ph)
    }

    /// GDI text clobbers the alpha byte; the card interior is opaque by construction, so restore it.
    pub fn finish(&mut self, cw: f32, ch: f32, radius: f32) {
        let s = self.scale();
        let (pw, ph) = (((cw + 2.0 * M) * s).ceil() as i32, ((ch + 2.0 * M) * s).ceil() as i32);
        let (cx, cy, half, r) = ((M + cw / 2.0) * s, (M + ch / 2.0) * s, (cw * s / 2.0, ch * s / 2.0), radius * s);
        let stride = self.pw;
        let buf = unsafe { std::slice::from_raw_parts_mut(self.bits, (self.pw * self.ph) as usize) };
        for y in 0..ph {
            for x in 0..pw {
                if sdf(x as f32 + 0.5, y as f32 + 0.5, (cx, cy), half, r) < -1.5 { buf[(y * stride + x) as usize] |= 0xFF00_0000; }
            }
        }
    }

    /// Anti-aliased round-capped line in card logical coordinates (used for check marks).
    pub fn line(&mut self, a: (f32, f32), b: (f32, f32), width: f32, rgb: u32) {
        let s = self.scale();
        let (ax, ay, bx, by, hw) = ((a.0 + M) * s, (a.1 + M) * s, (b.0 + M) * s, (b.1 + M) * s, width * s / 2.0);
        let buf = unsafe { std::slice::from_raw_parts_mut(self.bits, (self.pw * self.ph) as usize) };
        let (x0, x1) = (((ax.min(bx) - hw).floor() as i32 - 1).max(0), ((ax.max(bx) + hw).ceil() as i32 + 1).min(self.pw));
        let (y0, y1) = (((ay.min(by) - hw).floor() as i32 - 1).max(0), ((ay.max(by) + hw).ceil() as i32 + 1).min(self.ph));
        let (dx, dy) = (bx - ax, by - ay);
        let len2 = (dx * dx + dy * dy).max(1e-6);
        for py in y0..y1 {
            for px in x0..x1 {
                let (fx, fy) = (px as f32 + 0.5 - ax, py as f32 + 0.5 - ay);
                let t = ((fx * dx + fy * dy) / len2).clamp(0.0, 1.0);
                let d = ((fx - t * dx).powi(2) + (fy - t * dy).powi(2)).sqrt() - hw;
                blend(&mut buf[(py * self.pw + px) as usize], rgb, (0.5 - d).clamp(0.0, 1.0));
            }
        }
    }

    /// Draws the deck view and returns the window pixel size to present.
    pub fn render(&mut self, v: &View) -> (i32, i32) {
        let (cw, ch) = card_size(v.expanded);
        let p = &v.pal;
        let radius = if v.expanded { 18.0 } else { ch / 2.0 };
        let size = self.draw_card(cw, ch, radius, p);
        let mode = v.st.and_then(|s| s.mode);
        let dotc = mode_color(mode, p.muted);
        let rpm = v.st.and_then(|s| s.rpm).map(fmt_rpm);
        let cpu = fmt_temp(v.st.and_then(|s| s.cpu_c));
        let disk = fmt_temp(v.st.and_then(|s| s.disk_c));
        let lg = v.lang;
        let center = DT_CENTER as u32;
        if !v.expanded {
            self.fill(fr(16.0, 15.0, 14.0, 14.0), 7.0, dotc, 1.0);
            if v.connected {
                self.text(1, &format!("{} RPM", rpm.as_deref().unwrap_or("\u{2014}")), fr(38.0, 0.0, 84.0, CAP.1), p.text, DT_LEFT as u32);
                self.text(1, &cpu, fr(122.0, 0.0, 52.0, CAP.1), p.muted, DT_RIGHT as u32);
            } else {
                self.text(1, tr(lg, K::Disconnected), fr(38.0, 0.0, 140.0, CAP.1), p.warn, DT_LEFT as u32);
            }
        } else {
            self.fill(fr(18.0, 18.0, 12.0, 12.0), 6.0, dotc, 1.0);
            let head = if v.connected { mode.map_or("", |m| lg.mode_name(m)) } else { tr(lg, K::Disconnected) };
            self.text(0, head, fr(38.0, 12.0, 182.0, 24.0), if v.connected { p.muted } else { p.warn }, DT_LEFT as u32);
            let number = rpm.as_deref().unwrap_or("\u{2014}");
            self.text(2, number, fr(16.0, 38.0, 160.0, 46.0), p.text, DT_LEFT as u32);
            // The unit sits right after the number, on its baseline, instead of in a fixed far-away column.
            self.text(0, "RPM", fr(16.0 + self.text_width(2, number) + 6.0, 56.0, 60.0, 22.0), p.muted, DT_LEFT as u32);
            for (r, lab, val) in [(CHIP_CPU, K::Cpu, &cpu), (CHIP_DISK, K::Disk, &disk)] {
                self.fill(r, 10.0, p.chip, 1.0);
                self.text(0, tr(lg, lab), fr(r.x + 10.0, r.y, 50.0, r.h), p.muted, DT_LEFT as u32);
                self.text(1, val, fr(r.x + 50.0, r.y, r.w - 60.0, r.h), p.text, DT_RIGHT as u32);
            }
            for (i, m) in QUICK.iter().enumerate() {
                let b = button(i);
                let sel = mode == Some(*m);
                let hot = v.hover == Hit::Mode(*m);
                self.fill(b, 10.0, if sel { p.accent } else if hot { p.chip_hot } else { p.chip }, 1.0);
                let lab = if *m == Mode::Performance { tr(lg, K::PerfShort) } else { lg.mode_name(*m) };
                self.text(0, lab, b, if sel { 0xFFFFFF } else { p.text }, center);
            }
            let pin_hot = v.hover == Hit::Pin;
            if v.pinned || pin_hot { self.fill(PIN, 8.0, if v.pinned { p.accent } else { p.chip_hot }, 1.0); }
            let ink = if v.pinned { 0xFFFFFF } else if pin_hot { p.text } else { p.muted };
            let (cx, cy) = (PIN.x + PIN.w / 2.0, PIN.y + PIN.h / 2.0);
            self.fill(fr(cx - 3.5, cy - 7.0, 7.0, 7.0), 2.0, ink, 1.0); // pushpin head
            self.line((cx - 5.5, cy), (cx + 5.5, cy), 1.8, ink); // collar
            self.line((cx, cy), (cx, cy + 7.0), 1.4, ink); // needle
            let hot = v.hover == Hit::Open;
            if hot { self.fill(OPEN, 8.0, p.chip, 1.0); }
            self.text(1, tr(lg, K::OpenMain), OPEN, p.accent, center);
        }
        self.finish(cw, ch, radius);
        size
    }

    /// Pushes the rendered bitmap to the layered window at screen position (x, y) of its top-left corner.
    pub fn present(&self, hwnd: HWND, x: i32, y: i32, size: (i32, i32)) {
        unsafe {
            let dst = POINT { x, y };
            let sz = SIZE { cx: size.0, cy: size.1 };
            let src = POINT { x: 0, y: 0 };
            let bf = BLENDFUNCTION { BlendOp: AC_SRC_OVER as u8, BlendFlags: 0, SourceConstantAlpha: 255, AlphaFormat: AC_SRC_ALPHA as u8 };
            UpdateLayeredWindow(hwnd, null_mut(), &dst, &sz, self.dc, &src, 0, &bf, ULW_ALPHA);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn hit_regions() {
        assert_eq!(hit_test(false, 20.0, 20.0), Hit::None);
        assert_eq!(hit_test(true, 20.0, 140.0), Hit::Mode(Mode::Quiet));
        assert_eq!(hit_test(true, 140.0, 140.0), Hit::Mode(Mode::Performance));
        assert_eq!(hit_test(true, 200.0, 140.0), Hit::Mode(Mode::Custom));
        assert_eq!(hit_test(true, 100.0, 170.0), Hit::Open);
        assert_eq!(hit_test(true, 100.0, 70.0), Hit::None);
        assert_eq!(hit_test(true, 238.0, 22.0), Hit::Pin);
        assert_eq!(hit_test(false, 238.0, 22.0), Hit::None);
    }
    #[test]
    fn rpm_format() {
        assert_eq!(fmt_rpm(2450), "2,450");
        assert_eq!(fmt_rpm(1005), "1,005");
        assert_eq!(fmt_rpm(950), "950");
    }
}

//! Custom-drawn tray popup (replaces the native menu). Layered window, same card style as the deck.
//! The window and its bitmap exist only while the menu is open.
use crate::deck::{fr, Surface};
use crate::util::w;
use crate::{app, autostart_on, MenuWin, CMD_AUTOSTART, CMD_DECK, CMD_DRIVER, CMD_EXIT, CMD_MODE0, CMD_OPEN};
use dfcore::i18n::{tr, K};
use dfcore::menu::{self, Cmd, GROUPS, GROUPS_DRIVER};
use dfcore::mode::Mode;
use dfcore::rect::Rect;
use std::mem::{size_of, zeroed};
use std::ptr::{null, null_mut};
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::AttachThreadInput;
use windows_sys::Win32::UI::HiDpi::*;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

const RADIUS: f32 = 16.0;

fn lo(lp: LPARAM) -> i32 { (lp & 0xFFFF) as i16 as i32 }
fn hi(lp: LPARAM) -> i32 { ((lp >> 16) & 0xFFFF) as i16 as i32 }

pub unsafe fn open() {
    let a = app();
    if a.menu.is_some() { return; }
    let mut cur = POINT { x: 0, y: 0 };
    GetCursorPos(&mut cur);
    let mon = MonitorFromPoint(cur, MONITOR_DEFAULTTONEAREST);
    let (mut dx, mut dy) = (96u32, 96u32);
    if GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &mut dx, &mut dy) != 0 || dx == 0 { dx = 96; }
    let mut mi: MONITORINFO = zeroed();
    mi.cbSize = size_of::<MONITORINFO>() as u32;
    GetMonitorInfoW(mon, &mut mi);
    let work = Rect { l: mi.rcWork.left, t: mi.rcWork.top, r: mi.rcWork.right, b: mi.rcWork.bottom };
    let driver = crate::net::NET.lock().ok().map_or(false, |n| n.status.as_ref().map_or(false, |st| st.driver_installable()));
    let layout = if driver { menu::layout(&GROUPS_DRIVER) } else { menu::layout(&GROUPS) };
    let s = dx as f32 / 96.0;
    let (ox, oy) = menu::place((cur.x, cur.y), ((layout.w * s).ceil() as i32, (layout.h * s).ceil() as i32), work);
    let cur_mode = crate::net::NET.lock().ok().and_then(|n| n.status.as_ref().and_then(|st| st.mode));
    let checked = layout.rows.iter().map(|r| match r.cmd {
        Cmd::Mode(m) => cur_mode == Some(m),
        Cmd::Deck => a.deck_on,
        Cmd::Autostart => autostart_on(),
        _ => false,
    }).collect();

    let hinst = GetModuleHandleW(null());
    let cls = w("DellFans.Menu");
    let mut wc: WNDCLASSW = zeroed();
    wc.lpfnWndProc = Some(menu_proc);
    wc.hInstance = hinst;
    wc.lpszClassName = cls.as_ptr();
    wc.hCursor = LoadCursorW(null_mut(), IDC_ARROW);
    RegisterClassW(&wc);
    let hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, cls.as_ptr(), cls.as_ptr(), WS_POPUP,
        0, 0, 1, 1, null_mut(), null_mut(), hinst, null());
    if hwnd.is_null() { return; }
    let surf = Surface::new(dx, a.lang, (layout.w, layout.h));
    let mut mw = MenuWin { hwnd, surf, layout, sel: None, checked, ox, oy };
    paint(&mut mw);
    a.menu = Some(mw);
    ShowWindow(hwnd, SW_SHOW);
    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    // Classic tray-menu rule: the popup must own the foreground so it sees keys and focus loss.
    // Attaching to the current foreground thread's input queue makes this succeed even when the click came from a no-activate window.
    let ft = GetWindowThreadProcessId(GetForegroundWindow(), null_mut());
    let me = windows_sys::Win32::System::Threading::GetCurrentThreadId();
    if ft != 0 && ft != me { AttachThreadInput(me, ft, 1); }
    SetForegroundWindow(hwnd);
    SetFocus(hwnd);
    if ft != 0 && ft != me { AttachThreadInput(me, ft, 0); }
    SetCapture(hwnd);
}

pub unsafe fn close() {
    if let Some(m) = app().menu.take() {
        ReleaseCapture();
        DestroyWindow(m.hwnd);
        m.surf.free();
    }
}

fn label(c: Cmd, l: dfcore::i18n::Lang) -> &'static str {
    match c {
        Cmd::Open => tr(l, K::OpenMain), Cmd::InstallDriver => tr(l, K::InstallDriver), Cmd::Mode(m) => l.mode_name(m), Cmd::Deck => tr(l, K::Deck),
        Cmd::Autostart => tr(l, K::Autostart), Cmd::Exit => tr(l, K::Exit),
    }
}

unsafe fn paint(m: &mut MenuWin) {
    let a = app();
    let p = a.theme.palette();
    let (w_, h_) = (m.layout.w, m.layout.h);
    let size = m.surf.draw_card(w_, h_, RADIUS, &p);
    for y in &m.layout.seps { m.surf.fill(fr(16.0, y + 4.0, w_ - 32.0, 1.0), 0.0, p.border, 1.0); }
    for (i, r) in m.layout.rows.iter().enumerate() {
        let hot = m.sel == Some(i);
        if hot { m.surf.fill(fr(r.x, r.y, r.w, r.h), 10.0, p.accent, 1.0); }
        let fg = if hot { 0xFFFFFF } else { p.text };
        let mark = if hot { 0xFFFFFF } else { p.accent };
        if m.checked[i] {
            let (cx, cy) = (r.x + 18.0, r.y + r.h / 2.0);
            if let Cmd::Mode(_) = r.cmd {
                m.surf.fill(fr(cx - 4.0, cy - 4.0, 8.0, 8.0), 4.0, mark, 1.0);
            } else {
                m.surf.line((cx - 5.0, cy + 0.5), (cx - 1.5, cy + 4.0), 2.0, mark);
                m.surf.line((cx - 1.5, cy + 4.0), (cx + 5.5, cy - 4.0), 2.0, mark);
            }
        }
        let font = if r.cmd == Cmd::Open || r.cmd == Cmd::InstallDriver { 1 } else { 3 };
        m.surf.text(font, label(r.cmd, a.lang), fr(r.x + 36.0, r.y, r.w - 44.0, r.h), fg, DT_LEFT as u32);
    }
    m.surf.finish(w_, h_, RADIUS);
    let mpx = (crate::deck::M * m.surf.dpi as f32 / 96.0).round() as i32;
    m.surf.present(m.hwnd, m.ox - mpx, m.oy - mpx, size);
}

unsafe fn activate(i: usize) {
    let Some(m) = app().menu.as_ref() else { return };
    let id = match m.layout.rows[i].cmd {
        Cmd::Open => CMD_OPEN,
        Cmd::InstallDriver => CMD_DRIVER,
        Cmd::Mode(md) => CMD_MODE0 + Mode::ALL.iter().position(|x| *x == md).unwrap_or(0),
        Cmd::Deck => CMD_DECK,
        Cmd::Autostart => CMD_AUTOSTART,
        Cmd::Exit => CMD_EXIT,
    };
    let main = app().main;
    close();
    PostMessageW(main, WM_COMMAND, id, 0);
}

unsafe fn set_sel(sel: Option<usize>) {
    if let Some(m) = app().menu.as_mut() {
        if m.sel != sel { m.sel = sel; paint(m); }
    }
}

unsafe extern "system" fn menu_proc(h: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let Some(m) = app().menu.as_mut() else { return DefWindowProcW(h, msg, wp, lp) };
    if m.hwnd != h { return DefWindowProcW(h, msg, wp, lp); }
    let s = m.surf.dpi as f32 / 96.0;
    let (cx, cy) = (lo(lp) as f32 / s - crate::deck::M, hi(lp) as f32 / s - crate::deck::M);
    match msg {
        WM_ACTIVATE if (wp & 0xFFFF) as u32 == WA_INACTIVE => { close(); 0 }
        WM_KILLFOCUS => { close(); 0 }
        WM_MOUSEMOVE => { let sel = menu::hit(&m.layout, cx, cy); set_sel(sel); 0 }
        WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN => {
            // With capture, clicks anywhere arrive here: outside the card dismisses the menu.
            if !(cx >= 0.0 && cy >= 0.0 && cx < m.layout.w && cy < m.layout.h) { close(); }
            0
        }
        WM_LBUTTONUP => { if let Some(i) = menu::hit(&m.layout, cx, cy) { activate(i); } 0 }
        WM_KEYDOWN => {
            let n = m.layout.rows.len();
            match wp as u16 {
                VK_ESCAPE => close(),
                VK_UP => { let i = menu::step(m.sel, n, -1); set_sel(Some(i)) }
                VK_DOWN | VK_TAB => { let i = menu::step(m.sel, n, 1); set_sel(Some(i)) }
                VK_HOME => set_sel(Some(0)),
                VK_END => set_sel(Some(n - 1)),
                VK_RETURN | VK_SPACE => { if let Some(i) = m.sel { activate(i); } }
                _ => {}
            }
            0
        }
        _ => DefWindowProcW(h, msg, wp, lp),
    }
}

#![windows_subsystem = "windows"]
//! DellFans.Tray.exe: tray icon + deck. Talks to the service over IPC only; no hardware access.
mod deck;
mod menu;
mod net;
mod util;

use deck::{Hit, Surface, View, M};
use dfcore::i18n::{tr, Lang, K};
use dfcore::mode::{deck_wanted, set_mode_request, Mode};
use dfcore::rect::{blocks_deck, Rect};
use dfcore::status::Status;
use dfcore::theme::Theme;
use net::{DECK_VISIBLE, NET, WM_NET};
use std::mem::{size_of, zeroed};
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicPtr, AtomicU32, Ordering::Relaxed};
use util::*;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Globalization::GetUserDefaultUILanguage;
use windows_sys::Win32::Graphics::Gdi::*;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::CreateMutexW;
use windows_sys::Win32::UI::HiDpi::*;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
use windows_sys::Win32::UI::Shell::*;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

const WM_TRAY: u32 = WM_APP + 1;
const WM_MOUSELEAVE: u32 = 0x02A3;
const TIMER_FS: usize = 1;
const TIMER_HOVER: usize = 2;
/// The collapsed deck expands after the pointer rests on it this long (or at once on a click), so it can be grabbed and dragged small.
const TIMER_EXPAND: usize = 3;
const EXPAND_DELAY_MS: u32 = 300;
pub const CMD_OPEN: usize = 1;
pub const CMD_DRIVER: usize = 2;
pub const CMD_MODE0: usize = 10;
pub const CMD_DECK: usize = 20;
pub const CMD_AUTOSTART: usize = 21;
pub const CMD_EXIT: usize = 30;
const RUN_VALUE: &str = "DellFans.Tray";

struct Press { sx: i32, sy: i32, ox: i32, oy: i32, moved: bool, hit: Hit }
type Shown = (Option<Status>, bool, bool, bool, Hit, Theme, u32, i32, i32);
struct DeckWin {
    hwnd: HWND, surf: Surface, ox: i32, oy: i32, pin: bool, inside: bool, tracking: bool,
    hover: Hit, press: Option<Press>, fs_hidden: bool, last: Option<Shown>,
}
pub struct MenuWin { pub hwnd: HWND, pub surf: Surface, pub layout: dfcore::menu::Layout, pub sel: Option<usize>, pub checked: Vec<bool>, pub ox: i32, pub oy: i32 }
pub struct App { pub main: HWND, pub lang: Lang, pub theme: Theme, pub deck_on: bool, pub menu: Option<MenuWin>, deck: Option<DeckWin>, tip: String, icon: HICON }

static APP: AtomicPtr<App> = AtomicPtr::new(null_mut());
static TASKBAR_CREATED: AtomicU32 = AtomicU32::new(0);
// Single UI thread owns App; window procedures re-enter it, so access goes through a raw pointer.
pub fn app() -> &'static mut App { unsafe { &mut *APP.load(Relaxed) } }

fn lo(lp: LPARAM) -> i32 { (lp & 0xFFFF) as i16 as i32 }
fn hi(lp: LPARAM) -> i32 { ((lp >> 16) & 0xFFFF) as i16 as i32 }
fn exe_dir() -> std::path::PathBuf { std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf())).unwrap_or_default() }

// ---------- tray icon ----------
unsafe fn nid(h: HWND) -> NOTIFYICONDATAW {
    let mut n: NOTIFYICONDATAW = zeroed();
    n.cbSize = size_of::<NOTIFYICONDATAW>() as u32;
    n.hWnd = h;
    n.uID = 1;
    n
}

fn set_tip(n: &mut NOTIFYICONDATAW, s: &str) {
    let t: Vec<u16> = s.encode_utf16().take(127).collect();
    n.szTip = [0; 128];
    n.szTip[..t.len()].copy_from_slice(&t);
}

fn tip_text(a: &App) -> String {
    let l = a.lang;
    let n = NET.lock().unwrap();
    match (&n.status, n.connected) {
        (_, false) => format!("{} \u{b7} {}", tr(l, K::Tip), tr(l, K::Disconnected)),
        (None, true) => tr(l, K::Tip).to_string(),
        (Some(s), true) => format!("{} \u{b7} {} RPM \u{b7} CPU {}", tr(l, K::Tip),
            s.rpm.map_or("\u{2014}".into(), deck::fmt_rpm),
            s.cpu_c.map_or("\u{2014}".into(), |t| format!("{:.0}\u{b0}C", t))),
    }
}

unsafe fn add_icon(a: &App) {
    let mut n = nid(a.main);
    n.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    n.uCallbackMessage = WM_TRAY;
    n.hIcon = a.icon;
    set_tip(&mut n, &a.tip);
    Shell_NotifyIconW(NIM_ADD, &n);
}

const PERSONALIZE_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

/// Embedded glyphs (build.rs): resource group 2 is for a light taskbar, 3 for a dark one (white glyph).
/// SystemUsesLightTheme = 0 means a dark taskbar; absent (older Windows) counts as light.
unsafe fn load_tray_icon() -> HICON {
    let dark = reg_get_dword(PERSONALIZE_KEY, "SystemUsesLightTheme") == Some(0);
    let size = GetSystemMetrics(SM_CXSMICON).max(16);
    let id: usize = if dark { 3 } else { 2 };
    LoadImageW(GetModuleHandleW(null()), id as *const u16, IMAGE_ICON, size, size, LR_DEFAULTCOLOR)
}

/// WM_SETTINGCHANGE "ImmersiveColorSet": swap the glyph when the taskbar theme changed.
unsafe fn refresh_icon() {
    let a = app();
    let new = load_tray_icon();
    if new.is_null() { return; }
    let old = a.icon;
    a.icon = new;
    let mut n = nid(a.main);
    n.uFlags = NIF_ICON;
    n.hIcon = new;
    Shell_NotifyIconW(NIM_MODIFY, &n);
    DestroyIcon(old);
}

unsafe fn refresh_tip() {
    let a = app();
    let t = tip_text(a);
    if t != a.tip {
        a.tip = t;
        let mut n = nid(a.main);
        n.uFlags = NIF_TIP;
        set_tip(&mut n, &a.tip);
        Shell_NotifyIconW(NIM_MODIFY, &n);
    }
}

// ---------- actions ----------
unsafe fn open_main() { open_main_with(None); }

/// `--sensor-driver` opens the window on the home page with the PawnIO consent dialog.
unsafe fn open_main_with(arg: Option<&str>) {
    let wide = arg.map(w);
    let dir = exe_dir();
    let exe = dir.join("DellFans.exe");
    let r = ShellExecuteW(null_mut(), w("open").as_ptr(), w(&exe.to_string_lossy()).as_ptr(), wide.as_ref().map_or(null(), |v| v.as_ptr()),
        w(&dir.to_string_lossy()).as_ptr(), SW_SHOWNORMAL);
    if (r as isize) <= 32 { log(&format!("cannot launch {}: code {}", exe.display(), r as isize)); }
}

fn request_mode(m: Mode) {
    let saved = NET.lock().ok().and_then(|n| n.status.as_ref().and_then(|s| s.custom_min));
    net::send(set_mode_request(m, saved));
}

pub fn autostart_on() -> bool { reg_get_sz(RUN_KEY, RUN_VALUE).is_some() }

fn toggle_autostart() {
    if autostart_on() { reg_delete(RUN_KEY, RUN_VALUE); }
    else if let Ok(p) = std::env::current_exe() { reg_set_sz(RUN_KEY, RUN_VALUE, &format!("\"{}\"", p.display())); }
}

// ---------- preferences ----------
fn load_theme() -> Theme { Theme::from_id(&reg_get_sz(UI_KEY, "Theme").unwrap_or_default()) }

fn load_lang() -> Lang {
    reg_get_sz(UI_KEY, "Language").and_then(|s| Lang::from_id(&s))
        .unwrap_or_else(|| Lang::from_langid(unsafe { GetUserDefaultUILanguage() }))
}

/// Re-reads shared UI preferences so the settings page and the tray stay in step (D14).
unsafe fn sync_prefs() {
    let a = app();
    let want = deck_wanted(reg_get_dword(UI_KEY, "DockVisible"));
    if want != a.deck_on { set_deck(want, false); }
    let (t, l) = (load_theme(), load_lang());
    if t != a.theme || l != a.lang {
        a.theme = t;
        a.lang = l;
        redraw(true);
        refresh_tip();
    }
}

unsafe fn set_deck(on: bool, persist: bool) {
    let a = app();
    a.deck_on = on;
    DECK_VISIBLE.store(on, Relaxed);
    net::wake();
    if on {
        if a.deck.is_none() { deck_create(); }
        SetTimer(a.main, TIMER_FS, 2000, None);
    } else {
        KillTimer(a.main, TIMER_FS);
        deck_destroy();
    }
    if persist { reg_set_dword(UI_KEY, "DockVisible", on as u32); }
}

// ---------- deck window ----------
unsafe fn work_area() -> RECT {
    let mut r: RECT = zeroed();
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &mut r as *mut _ as _, 0);
    r
}

unsafe fn deck_create() {
    let a = app();
    let hi = GetModuleHandleW(null());
    let cls = w("DellFans.Deck");
    let mut wc: WNDCLASSW = zeroed();
    wc.lpfnWndProc = Some(deck_proc);
    wc.hInstance = hi;
    wc.lpszClassName = cls.as_ptr();
    wc.hCursor = LoadCursorW(null_mut(), IDC_ARROW);
    RegisterClassW(&wc);
    let hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, cls.as_ptr(), cls.as_ptr(),
        WS_POPUP, 0, 0, 1, 1, null_mut(), null_mut(), hi, null());
    if hwnd.is_null() { log("deck window creation failed"); return; }
    let wr = work_area();
    let s = GetDpiForSystem() as f32 / 96.0;
    let (dx, dy) = ((wr.right as f32 - 212.0 * s) as i32, (wr.top as f32 + 24.0 * s) as i32);
    let (mut ox, mut oy) = (reg_get_dword(UI_KEY, "DockX").map(|v| v as i32), reg_get_dword(UI_KEY, "DockY").map(|v| v as i32));
    if let (Some(x), Some(y)) = (ox, oy) {
        if MonitorFromPoint(POINT { x, y }, MONITOR_DEFAULTTONULL).is_null() { ox = None; oy = None; }
    }
    let dpi = GetDpiForWindow(hwnd).max(96);
    a.deck = Some(DeckWin { hwnd, surf: Surface::new(dpi, a.lang, deck::EXP), ox: ox.unwrap_or(dx), oy: oy.unwrap_or(dy),
        pin: false, inside: false, tracking: false, hover: Hit::None, press: None, fs_hidden: false, last: None });
    redraw(true);
    redraw(false); // window now sits on its real monitor: picks up a different DPI if needed
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
}

unsafe fn deck_destroy() {
    if let Some(d) = app().deck.take() {
        DestroyWindow(d.hwnd);
        d.surf.free();
    }
}

fn card_origin(d: &DeckWin, expanded: bool, dpi: u32) -> (i32, i32) {
    let s = dpi as f32 / 96.0;
    let (cw, ch) = deck::card_size(expanded);
    unsafe {
        let mon = MonitorFromPoint(POINT { x: d.ox, y: d.oy }, MONITOR_DEFAULTTONEAREST);
        let mut mi: MONITORINFO = zeroed();
        mi.cbSize = size_of::<MONITORINFO>() as u32;
        GetMonitorInfoW(mon, &mut mi);
        let wr = mi.rcWork;
        (d.ox.clamp(wr.left, (wr.right - (cw * s) as i32).max(wr.left)), d.oy.clamp(wr.top, (wr.bottom - (ch * s) as i32).max(wr.top)))
    }
}

unsafe fn redraw(force: bool) {
    let a = app();
    let (theme, lang) = (a.theme, a.lang);
    let Some(d) = a.deck.as_mut() else { return };
    if d.fs_hidden { return; }
    let dpi = GetDpiForWindow(d.hwnd).max(96);
    let mut force = force;
    if dpi != d.surf.dpi || lang != d.surf.lang {
        let old = std::mem::replace(&mut d.surf, Surface::new(dpi, lang, deck::EXP));
        old.free();
        force = true;
    }
    let (st, connected) = { let n = NET.lock().unwrap(); (n.status.clone(), n.connected) };
    let expanded = d.pin || d.inside;
    let shown: Shown = (st, connected, expanded, d.pin, d.hover, theme, dpi, d.ox, d.oy);
    if !force && d.last.as_ref() == Some(&shown) { return; }
    let view = View { pal: theme.palette(), lang, st: shown.0.as_ref(), connected, expanded, hover: d.hover, pinned: d.pin };
    let size = d.surf.render(&view);
    let (x, y) = card_origin(d, expanded, dpi);
    let m = (M * dpi as f32 / 96.0).round() as i32;
    d.surf.present(d.hwnd, x - m, y - m, size);
    d.last = Some(shown);
}

unsafe fn fullscreen_on_deck_monitor(deck: HWND, main: HWND) -> bool {
    let fg = GetForegroundWindow();
    if fg.is_null() || fg == deck || fg == main || IsWindowVisible(fg) == 0 || IsIconic(fg) != 0 { return false; }
    let mut cls = [0u16; 64];
    let n = GetClassNameW(fg, cls.as_mut_ptr(), 64) as usize;
    let cls = String::from_utf16_lossy(&cls[..n]);
    if ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"].contains(&cls.as_str()) { return false; }
    let rect_of = |r: RECT| Rect { l: r.left, t: r.top, r: r.right, b: r.bottom };
    let mon_rect = |h: HWND| {
        let mut mi: MONITORINFO = zeroed();
        mi.cbSize = size_of::<MONITORINFO>() as u32;
        GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), &mut mi);
        rect_of(mi.rcMonitor)
    };
    let mut wr: RECT = zeroed();
    GetWindowRect(fg, &mut wr);
    blocks_deck(rect_of(wr), mon_rect(fg), mon_rect(deck), 2)
}

unsafe fn fs_tick() {
    let a = app();
    let main = a.main;
    let Some(d) = a.deck.as_mut() else { return };
    let hide = fullscreen_on_deck_monitor(d.hwnd, main);
    if hide != d.fs_hidden {
        d.fs_hidden = hide;
        log(if hide { "deck hidden: fullscreen window on its monitor" } else { "deck shown" });
        ShowWindow(d.hwnd, if hide { SW_HIDE } else { SW_SHOWNOACTIVATE });
        if !hide { redraw(true); }
    } else if !hide && a.menu.is_none() {
        SetWindowPos(d.hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

unsafe extern "system" fn deck_proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    let Some(d) = app().deck.as_mut() else { return DefWindowProcW(h, m, wp, lp) };
    if d.hwnd != h { return DefWindowProcW(h, m, wp, lp); }
    let s = d.surf.dpi as f32 / 96.0;
    match m {
        WM_MOUSEACTIVATE => MA_NOACTIVATE as LRESULT,
        WM_MOUSEMOVE => {
            if !d.tracking {
                let mut t = TRACKMOUSEEVENT { cbSize: size_of::<TRACKMOUSEEVENT>() as u32, dwFlags: TME_LEAVE, hwndTrack: h, dwHoverTime: 0 };
                TrackMouseEvent(&mut t);
                d.tracking = true;
            }
            if !d.inside && d.press.is_none() { SetTimer(h, TIMER_EXPAND, EXPAND_DELAY_MS, None); }
            SetTimer(h, TIMER_HOVER, 400, None); // safety net: the shrunken window may never send WM_MOUSELEAVE
            let mut p = POINT { x: 0, y: 0 };
            GetCursorPos(&mut p);
            if let Some(pr) = d.press.as_mut() {
                if (p.x - pr.sx).abs() + (p.y - pr.sy).abs() > 4 { pr.moved = true; }
                if pr.moved { d.ox = pr.ox + p.x - pr.sx; d.oy = pr.oy + p.y - pr.sy; }
            } else {
                let expanded = d.pin || d.inside;
                d.hover = deck::hit_test(expanded, lo(lp) as f32 / s - M, hi(lp) as f32 / s - M);
            }
            redraw(false);
            0
        }
        WM_MOUSELEAVE => { d.tracking = false; KillTimer(h, TIMER_EXPAND); if d.press.is_none() { d.inside = false; d.hover = Hit::None; redraw(false); } 0 }
        WM_TIMER => {
            let mut p = POINT { x: 0, y: 0 };
            let mut r: RECT = zeroed();
            GetCursorPos(&mut p);
            GetWindowRect(h, &mut r);
            if wp == TIMER_EXPAND {
                KillTimer(h, TIMER_EXPAND);
                if d.press.is_none() && p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom { d.inside = true; redraw(false); }
                return 0;
            }
            if d.press.is_none() && (p.x < r.left || p.x >= r.right || p.y < r.top || p.y >= r.bottom) {
                KillTimer(h, TIMER_HOVER);
                KillTimer(h, TIMER_EXPAND);
                d.inside = false;
                d.hover = Hit::None;
                redraw(false);
            }
            0
        }
        WM_LBUTTONDOWN => {
            KillTimer(h, TIMER_EXPAND); // pressing during the wait grabs the small deck for dragging
            let mut p = POINT { x: 0, y: 0 };
            GetCursorPos(&mut p);
            let hit = deck::hit_test(d.pin || d.inside, lo(lp) as f32 / s - M, hi(lp) as f32 / s - M);
            d.press = Some(Press { sx: p.x, sy: p.y, ox: d.ox, oy: d.oy, moved: false, hit });
            SetCapture(h);
            0
        }
        WM_LBUTTONUP => {
            ReleaseCapture();
            let Some(pr) = d.press.take() else { return 0 };
            if pr.moved {
                let (x, y) = card_origin(d, d.pin || d.inside, d.surf.dpi);
                d.ox = x;
                d.oy = y;
                reg_set_dword(UI_KEY, "DockX", x as u32);
                reg_set_dword(UI_KEY, "DockY", y as u32);
            } else {
                match pr.hit {
                    Hit::Mode(md) => request_mode(md),
                    Hit::Open => open_main(),
                    Hit::Pin => d.pin = !d.pin,
                    Hit::None => d.inside = true, // a click expands a collapsed card; it never pins it open
                }
            }
            redraw(true);
            0
        }
        WM_RBUTTONUP => { menu::open(); 0 }
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

// ---------- main (hidden) window ----------
unsafe extern "system" fn main_proc(h: HWND, m: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if m == TASKBAR_CREATED.load(Relaxed) && m != 0 {
        add_icon(app());
        return 0;
    }
    match m {
        WM_NET => { sync_prefs(); refresh_tip(); redraw(false); 0 }
        WM_TRAY => {
            match lp as u32 {
                WM_RBUTTONUP | WM_CONTEXTMENU => menu::open(),
                WM_LBUTTONUP => open_main(),
                _ => {}
            }
            0
        }
        WM_COMMAND => {
            match wp & 0xFFFF {
                CMD_OPEN => open_main(),
                CMD_DRIVER => open_main_with(Some("--sensor-driver")),
                CMD_DECK => { let on = !app().deck_on; set_deck(on, true); }
                CMD_AUTOSTART => toggle_autostart(),
                CMD_EXIT => { net::exit_host(); DestroyWindow(h); }
                c if (CMD_MODE0..CMD_MODE0 + 5).contains(&c) => request_mode(Mode::ALL[c - CMD_MODE0]),
                _ => {}
            }
            0
        }
        WM_TIMER => { if wp == TIMER_FS { fs_tick(); } 0 }
        WM_DISPLAYCHANGE => { fs_tick(); redraw(true); 0 }
        WM_SETTINGCHANGE => {
            // lParam is a NUL-terminated area name or null; read it only up to the length being compared.
            let p = lp as *const u16;
            let name: Vec<u16> = if p.is_null() { Vec::new() } else { (0..32).map(|i| *p.add(i)).take_while(|&c| c != 0).collect() };
            if String::from_utf16_lossy(&name) == "ImmersiveColorSet" { refresh_icon(); }
            0
        }
        WM_CLOSE => { DestroyWindow(h); 0 }
        WM_DESTROY => {
            Shell_NotifyIconW(NIM_DELETE, &nid(h));
            menu::close();
            deck_destroy();
            net::stop();
            DestroyIcon(app().icon);
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(h, m, wp, lp),
    }
}

fn main() {
    unsafe {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let mtx = CreateMutexW(null(), 0, w("Local\\DellFans.Tray.v2").as_ptr());
        if mtx.is_null() || GetLastError() == ERROR_ALREADY_EXISTS { return; }
        TASKBAR_CREATED.store(RegisterWindowMessageW(w("TaskbarCreated").as_ptr()), Relaxed);
        let hinst = GetModuleHandleW(null());
        let cls = w("DellFans.Tray.Msg");
        let mut wc: WNDCLASSW = zeroed();
        wc.lpfnWndProc = Some(main_proc);
        wc.hInstance = hinst;
        wc.lpszClassName = cls.as_ptr();
        RegisterClassW(&wc);
        let hwnd = CreateWindowExW(0, cls.as_ptr(), cls.as_ptr(), 0, 0, 0, 0, 0, null_mut(), null_mut(), hinst, null());
        let icon = load_tray_icon();
        let a0 = Box::new(App { main: hwnd, lang: load_lang(), theme: load_theme(), deck_on: false, menu: None, deck: None,
            tip: String::new(), icon });
        APP.store(Box::into_raw(a0), Relaxed);
        app().tip = tip_text(app());
        add_icon(app());
        net::start(hwnd);
        if deck_wanted(reg_get_dword(UI_KEY, "DockVisible")) { set_deck(true, false); }
        let mut msg: MSG = zeroed();
        while GetMessageW(&mut msg, null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

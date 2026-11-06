//! Small Win32 helpers: wide strings, registry, log file.
use std::ptr::{null, null_mut};
use windows_sys::Win32::Foundation::ERROR_SUCCESS;
use windows_sys::Win32::System::Registry::*;

pub fn w(s: &str) -> Vec<u16> { s.encode_utf16().chain(Some(0)).collect() }

pub const UI_KEY: &str = r"Software\DellFans\Ui";
pub const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

pub fn reg_get_dword(key: &str, name: &str) -> Option<u32> {
    let (k, n) = (w(key), w(name));
    let (mut v, mut cb) = (0u32, 4u32);
    let r = unsafe { RegGetValueW(HKEY_CURRENT_USER, k.as_ptr(), n.as_ptr(), RRF_RT_REG_DWORD, null_mut(), &mut v as *mut u32 as _, &mut cb) };
    (r == ERROR_SUCCESS).then_some(v)
}

pub fn reg_get_sz(key: &str, name: &str) -> Option<String> {
    let (k, n) = (w(key), w(name));
    let mut buf = [0u16; 260];
    let mut cb = (buf.len() * 2) as u32;
    let r = unsafe { RegGetValueW(HKEY_CURRENT_USER, k.as_ptr(), n.as_ptr(), RRF_RT_REG_SZ, null_mut(), buf.as_mut_ptr() as _, &mut cb) };
    if r != ERROR_SUCCESS { return None; }
    let len = (cb as usize / 2).saturating_sub(1).min(buf.len());
    Some(String::from_utf16_lossy(&buf[..len]))
}

fn reg_set(key: &str, name: &str, ty: u32, data: &[u8]) {
    let (k, n) = (w(key), w(name));
    unsafe {
        let mut h: HKEY = null_mut();
        if RegCreateKeyExW(HKEY_CURRENT_USER, k.as_ptr(), 0, null(), 0, KEY_SET_VALUE, null(), &mut h, null_mut()) == ERROR_SUCCESS {
            RegSetValueExW(h, n.as_ptr(), 0, ty, data.as_ptr(), data.len() as u32);
            RegCloseKey(h);
        }
    }
}

pub fn reg_set_dword(key: &str, name: &str, v: u32) { reg_set(key, name, REG_DWORD, &v.to_le_bytes()); }

pub fn reg_set_sz(key: &str, name: &str, v: &str) {
    let d: Vec<u8> = w(v).iter().flat_map(|c| c.to_le_bytes()).collect();
    reg_set(key, name, REG_SZ, &d);
}

pub fn reg_delete(key: &str, name: &str) {
    let (k, n) = (w(key), w(name));
    unsafe { RegDeleteKeyValueW(HKEY_CURRENT_USER, k.as_ptr(), n.as_ptr()); }
}

/// Transition-only diagnostics to %LOCALAPPDATA%\DellFans\tray.log, truncated past 32 KiB.
pub fn log(msg: &str) {
    use std::io::Write;
    let Some(dir) = std::env::var_os("LOCALAPPDATA").map(|d| std::path::PathBuf::from(d).join("DellFans")) else { return };
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("tray.log");
    let big = std::fs::metadata(&path).map(|m| m.len() > 32 * 1024).unwrap_or(false);
    let mut o = std::fs::OpenOptions::new();
    o.create(true).write(true);
    if big { o.truncate(true); } else { o.append(true); }
    if let Ok(mut f) = o.open(&path) {
        let t = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
        let _ = writeln!(f, "[{t}] {msg}");
    }
}

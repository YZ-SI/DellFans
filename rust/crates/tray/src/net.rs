//! IPC client: one worker thread polls `Status` and sends queued `SetMode` requests over
//! \\.\pipe\DellFans.Control.v2 (override: DELLFANS_PIPE, `test-hooks` feature only). The UI thread only reads `NET`.
use crate::util::{log, w};
use dfcore::ipc::{self, MAX_REPLY};
use dfcore::status::{self, Status};
use std::mem::zeroed;
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering::Relaxed};
use std::sync::Mutex;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Storage::FileSystem::*;
use windows_sys::Win32::System::IO::*;
use windows_sys::Win32::System::Pipes::*;
use windows_sys::Win32::Security::{GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY};
use windows_sys::Win32::System::Threading::*;
use windows_sys::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_APP};

pub const WM_NET: u32 = WM_APP + 2;
const IO_TIMEOUT_MS: u32 = 3000;

pub struct Net { pub status: Option<Status>, pub connected: bool }
pub static NET: Mutex<Net> = Mutex::new(Net { status: None, connected: false });
pub static DECK_VISIBLE: AtomicBool = AtomicBool::new(false);
static QUIT: AtomicBool = AtomicBool::new(false);
static WAKE: AtomicIsize = AtomicIsize::new(0);
static MAIN: AtomicIsize = AtomicIsize::new(0);
static QUEUE: Mutex<Vec<String>> = Mutex::new(Vec::new());
static PIPE: Mutex<Option<(Vec<u16>, bool)>> = Mutex::new(None);

pub fn wake() { unsafe { SetEvent(WAKE.load(Relaxed) as HANDLE); } }
pub fn send(req: String) { if let Ok(mut q) = QUEUE.lock() { q.push(req); } wake(); }
pub fn stop() { QUIT.store(true, Relaxed); wake(); }

pub fn start(main: HWND) {
    MAIN.store(main as isize, Relaxed);
    let ev = unsafe { CreateEventW(null(), 0, 0, null()) };
    WAKE.store(ev as isize, Relaxed);
    #[cfg(feature = "test-hooks")]
    let (path, test_pipe) = {
        let env = std::env::var("DELLFANS_PIPE").ok();
        let path = ipc::pipe_path(env.as_deref());
        (path, env.as_deref().is_some_and(|s| !s.is_empty()))
    };
    #[cfg(not(feature = "test-hooks"))]
    let (path, test_pipe) = (ipc::pipe_path(None), false);
    if let Ok(mut p) = PIPE.lock() { *p = Some((w(&path), test_pipe)); }
    let _ = std::thread::Builder::new().stack_size(256 * 1024).spawn(move || run(w(&path), test_pipe));
}

struct Pipe { h: HANDLE, ev: HANDLE }
impl Drop for Pipe { fn drop(&mut self) { unsafe { CloseHandle(self.h); CloseHandle(self.ev); } } }

impl Pipe {
    /// Full transfer with a per-operation timeout; false on any failure.
    fn io(&self, buf: *mut u8, len: usize, write: bool) -> bool {
        let mut done = 0usize;
        while done < len {
            unsafe {
                let mut ov: OVERLAPPED = zeroed();
                ov.hEvent = self.ev;
                ResetEvent(self.ev);
                let mut n = 0u32;
                let p = buf.add(done);
                let want = (len - done) as u32;
                let ok = if write { WriteFile(self.h, p, want, &mut n, &mut ov) } else { ReadFile(self.h, p, want, &mut n, &mut ov) };
                if ok == 0 {
                    if GetLastError() != ERROR_IO_PENDING { return false; }
                    if WaitForSingleObject(self.ev, IO_TIMEOUT_MS) != WAIT_OBJECT_0 {
                        CancelIoEx(self.h, &ov);
                        GetOverlappedResult(self.h, &ov, &mut n, 1);
                        return false;
                    }
                }
                if GetOverlappedResult(self.h, &ov, &mut n, 0) == 0 && GetLastError() != ERROR_MORE_DATA { return false; }
                if n == 0 { return false; }
                done += n as usize;
            }
        }
        true
    }

    fn call(&self, req: &str) -> Option<Vec<u8>> {
        let mut frame = ipc::encode_frame(req.as_bytes()).ok()?;
        if !self.io(frame.as_mut_ptr(), frame.len(), true) { return None; }
        let mut hdr = [0u8; 4];
        if !self.io(hdr.as_mut_ptr(), 4, false) { return None; }
        let n = ipc::frame_len(hdr, MAX_REPLY).ok()?;
        let mut body = vec![0u8; n];
        if n > 0 && !self.io(body.as_mut_ptr(), n, false) { return None; }
        Some(body)
    }
}

/// The only server the tray talks to: `DellFans.Service.exe` next to the tray, running elevated (src\ControlPipe.cs, same rule).
/// An unelevated program cannot start an elevated process, so a squatter on the pipe name fails even from the same folder.
fn trusted_host(pid: u32) -> bool {
    let Some(expected) = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.join("DellFans.Service.exe"))) else { return false };
    unsafe {
        let p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if p.is_null() { return false; }
        let mut buf = [0u16; 1024];
        let mut size = buf.len() as u32;
        let named = QueryFullProcessImageNameW(p, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut size) != 0;
        let same = named && String::from_utf16_lossy(&buf[..size as usize]).to_lowercase() == expected.to_string_lossy().to_lowercase();
        let mut elevated = false;
        if same {
            let mut token: HANDLE = null_mut();
            if OpenProcessToken(p, TOKEN_QUERY, &mut token) != 0 {
                let mut e: TOKEN_ELEVATION = zeroed();
                let mut got = 0u32;
                elevated = GetTokenInformation(token, TokenElevation, &mut e as *mut _ as *mut core::ffi::c_void,
                    std::mem::size_of::<TOKEN_ELEVATION>() as u32, &mut got) != 0 && e.TokenIsElevated != 0;
                CloseHandle(token);
            }
        }
        CloseHandle(p);
        same && elevated
    }
}

pub const NOT_RUNNING: &str = "controller not running";

fn connect(name: &[u16], test_pipe: bool) -> Result<Pipe, &'static str> {
    unsafe {
        let mut h = INVALID_HANDLE_VALUE;
        for _ in 0..2 {
            h = CreateFileW(name.as_ptr(), GENERIC_READ | GENERIC_WRITE, 0, null(), OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, null_mut());
            if h != INVALID_HANDLE_VALUE { break; }
            let e = GetLastError();
            if e == ERROR_FILE_NOT_FOUND { return Err(NOT_RUNNING); }
            if e != ERROR_PIPE_BUSY { break; }
            WaitNamedPipeW(name.as_ptr(), 500);
        }
        if h == INVALID_HANDLE_VALUE { return Err("controller pipe not available"); }
        let pipe = Pipe { h, ev: CreateEventW(null(), 1, 0, null()) };
        #[cfg(feature = "test-hooks")]
        if test_pipe {
            log_once(&SKIP_LOGGED, "identity check skipped: DELLFANS_PIPE test pipe");
            return Ok(pipe);
        }
        #[cfg(not(feature = "test-hooks"))]
        let _ = test_pipe;
        let mut server = 0u32;
        if GetNamedPipeServerProcessId(h, &mut server) == 0 { return Err("cannot read pipe server PID"); }
        if !trusted_host(server) { return Err("pipe server is not the elevated DellFans.Service.exe next to the tray"); }
        Ok(pipe)
    }
}

#[cfg(feature = "test-hooks")]
static SKIP_LOGGED: AtomicBool = AtomicBool::new(false);
#[cfg(feature = "test-hooks")]
fn log_once(flag: &AtomicBool, msg: &str) { if !flag.swap(true, Relaxed) { log(msg); } }

fn publish(status: Option<Status>, connected: bool) {
    if let Ok(mut n) = NET.lock() { n.status = status; n.connected = connected; }
    unsafe { PostMessageW(MAIN.load(Relaxed) as HWND, WM_NET, 0, 0); }
}

/// The service answers exactly one request per connection and then closes it (src\ControlPipe.cs), so every request connects anew.
fn call_once(name: &[u16], test_pipe: bool, req: &str) -> Result<Vec<u8>, &'static str> {
    connect(name, test_pipe)?.call(req).ok_or("no reply from service")
}

/// Tray menu "Exit DellFans": the controller hands the fan back to the BIOS and exits. Waits for the reply (a few seconds at most).
pub fn exit_host() {
    let target = PIPE.lock().ok().and_then(|p| p.clone());
    if let Some((name, test_pipe)) = target {
        match call_once(&name, test_pipe, ipc::exit_request()) { Ok(_) => log("controller asked to exit"), Err(e) => log(&format!("exit not sent: {e}")) }
    }
}

/// At sign-in the tray starts first: ask for the elevated controller once (UAC) through the window program, which owns that step.
fn start_controller_once(started: &mut bool, test_pipe: bool) {
    if *started || test_pipe { return; }
    *started = true;
    if let Some(dir) = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf())) {
        match std::process::Command::new(dir.join("DellFans.exe")).arg("--start-background").current_dir(&dir).spawn() {
            Ok(_) => log("asked DellFans.exe to start the controller"),
            Err(e) => log(&format!("cannot start DellFans.exe: {e}")),
        }
    }
}

fn run(name: Vec<u16>, test_pipe: bool) {
    let mut last = "";
    let mut start_asked = false;
    loop {
        let ms = if DECK_VISIBLE.load(Relaxed) { 1000 } else { 5000 };
        unsafe { WaitForSingleObject(WAKE.load(Relaxed) as HANDLE, ms); }
        if QUIT.load(Relaxed) { return; }
        let cmds: Vec<String> = QUEUE.lock().map(|mut q| std::mem::take(&mut *q)).unwrap_or_default();
        for c in &cmds {
            match call_once(&name, test_pipe, c) {
                Ok(r) => { if let Err(e) = status::parse_reply(&r) { if e != "no snapshot" { log(&format!("SetMode rejected: {e}")); } } }
                Err(e) => log(&format!("SetMode not sent: {e}")),
            }
        }
        match call_once(&name, test_pipe, ipc::status_request()) {
            Ok(r) => {
                if last != "connected" { log("connected"); last = "connected"; }
                match status::parse_reply(&r) {
                    Ok(s) => publish(Some(s), true),
                    Err(e) => { log(&format!("Status failed: {e}")); publish(None, true); }
                }
            }
            Err(e) => {
                if e != last { log(e); last = e; }
                if e == NOT_RUNNING { start_controller_once(&mut start_asked, test_pipe); }
                publish(None, false);
            }
        }
    }
}

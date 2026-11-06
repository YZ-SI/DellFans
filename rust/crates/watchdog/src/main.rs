#![windows_subsystem = "windows"]
//! DellFans.Watchdog.exe <session-file>
//! Watches the service (parent) and the session heartbeat; on failure launches `DellFans.Cli.exe --recover <session>`.
//! All recovery logic stays in C#; this process only decides and launches.
use dfcore::wdog::{self, Decision, RecoverAction};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime};
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Storage::FileSystem::SYNCHRONIZE;
use windows_sys::Win32::System::Threading::*;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

fn sidecar(session: &Path, ext: &str) -> PathBuf {
    let mut s = session.as_os_str().to_owned();
    s.push(ext);
    PathBuf::from(s)
}

/// Opens the parent only if its start time matches the record (PID reuse protection).
fn open_parent(rec: &wdog::Record) -> Option<HANDLE> {
    unsafe {
        let h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, 0, rec.parent_id);
        if h.is_null() { return None; }
        let z = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
        let (mut c, mut e, mut k, mut u) = (z, z, z, z);
        let ok = GetProcessTimes(h, &mut c, &mut e, &mut k, &mut u) != 0;
        let ft = ((c.dwHighDateTime as u64) << 32) | c.dwLowDateTime as u64;
        if ok && wdog::filetime_to_ticks(ft) == rec.parent_start_ticks { Some(h) } else { CloseHandle(h); None }
    }
}

fn heartbeat_age(session: &Path) -> Option<Duration> {
    let m = std::fs::metadata(sidecar(session, ".heartbeat")).ok()?.modified().ok()?;
    SystemTime::now().duration_since(m).ok()
}

fn sleep_or_done(session: &Path, d: Duration) -> bool {
    // Backoff in 100 ms slices so a finished session (.armed removed / .recovered) ends us promptly.
    let end = Instant::now() + d;
    while Instant::now() < end {
        if sidecar(session, ".recovered").exists() || !sidecar(session, ".armed").exists() { return true; }
        std::thread::sleep(Duration::from_millis(100));
    }
    false
}

/// Ok(()) = session is alive again, resume monitoring; Err(code) = process exit code.
fn recover(session: &Path) -> Result<(), i32> {
    let cli = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.join("DellFans.Cli.exe")));
    let Some(cli) = cli else { return Err(1) };
    for i in 0..wdog::MAX_RECOVER_TRIES {
        if sidecar(session, ".recovered").exists() { return Err(0); }
        // Never kill a running recovery: it may be mid firmware write. Wait for it however long it takes.
        let code = std::process::Command::new(&cli).arg("--recover").arg(session)
            .creation_flags(CREATE_NO_WINDOW).spawn().and_then(|mut c| c.wait()).ok().and_then(|s| s.code());
        match wdog::after_recover(code) {
            RecoverAction::Stop => return Err(0),
            RecoverAction::Resume => {
                std::thread::sleep(wdog::pause_after(RecoverAction::Resume)); // no hot loop of --recover spawns while the parent is still alive
                return Ok(());
            }
            RecoverAction::StopError => {
                let _ = std::fs::write(sidecar(session, ".watchdog-error"), "DellFans.Cli.exe --recover refused the session path (exit 2)");
                return Err(2);
            }
            RecoverAction::Retry => {}
        }
        if sidecar(session, ".recovered").exists() { return Err(0); }
        if let Some(s) = wdog::RECOVER_BACKOFF_SECS.get(i) {
            if sleep_or_done(session, Duration::from_secs(*s)) { return Err(0); }
        }
    }
    Err(1)
}

fn main() {
    let Some(arg) = std::env::args_os().nth(1) else { std::process::exit(2) };
    // absolute() keeps the caller's spelling (no \?\ prefix) because the CLI compares it with its reports directory.
    let session = std::path::absolute(&arg).unwrap_or_else(|_| PathBuf::from(&arg));
    let name = session.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    if !name.starts_with("fan-watchdog-") { std::process::exit(2); }
    let rec = match std::fs::read(&session).map_err(|e| e.to_string()).and_then(|b| wdog::parse_record(&b)) {
        Ok(r) => r,
        Err(e) => {
            let _ = std::fs::write(sidecar(&session, ".recovery-invalid"),
                format!("{{\"AutomaticRestored\":false,\"Error\":{}}}", serde_str(&e)));
            std::process::exit(1);
        }
    };
    let parent = open_parent(&rec);
    let _ = std::fs::write(sidecar(&session, ".ready"), "ready");
    let started = Instant::now();
    let mut saw_armed = false;
    let armed_path = sidecar(&session, ".armed");
    loop {
        let alive = match parent {
            // Wait doubles as the 1 s tick; returns early the moment the parent exits.
            Some(h) => unsafe { WaitForSingleObject(h, 1000) != WAIT_OBJECT_0 },
            None => false,
        };
        let armed = armed_path.exists();
        saw_armed |= armed;
        match wdog::decide(armed, saw_armed, alive, heartbeat_age(&session), started.elapsed()) {
            Decision::Exit => std::process::exit(0),
            Decision::Recover => if let Err(code) = recover(&session) { std::process::exit(code) },
            Decision::Wait => {
                if parent.is_none() { std::thread::sleep(Duration::from_millis(1000)); }
            }
        }
    }
}

fn serde_str(s: &str) -> String { serde_json::to_string(s).unwrap_or_else(|_| "\"error\"".into()) }

//! Fake DellFans service for manual tray testing. Serves canned Status replies on the pipe named by
//! DELLFANS_PIPE (required). Never touches hardware, services or real pipes.
//! SetMode is remembered and reflected in later Status replies. Optional DELLFANS_FAKELOG=<file> records requests.
use dfcore::ipc::{self, MAX_REPLY, MAX_REQUEST};
use std::io::Write;
use std::ptr::{null, null_mut};
use std::time::{SystemTime, UNIX_EPOCH};
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Storage::FileSystem::{ReadFile, WriteFile, PIPE_ACCESS_DUPLEX};
use windows_sys::Win32::System::Pipes::*;

const TEMPLATE: &str = include_str!("../../../core/fixtures/status.json");

fn read_exact(h: HANDLE, buf: &mut [u8]) -> bool {
    let mut got = 0usize;
    while got < buf.len() {
        let mut n = 0u32;
        let ok = unsafe { ReadFile(h, buf[got..].as_mut_ptr(), (buf.len() - got) as u32, &mut n, null_mut()) };
        if ok == 0 || n == 0 { return false; }
        got += n as usize;
    }
    true
}

fn write_all(h: HANDLE, data: &[u8]) -> bool {
    let mut n = 0u32;
    unsafe { WriteFile(h, data.as_ptr(), data.len() as u32, &mut n, null_mut()) != 0 && n as usize == data.len() }
}

fn reply(mode: &str, min_rpm: u32, t: u64) -> String {
    // rpm and temperatures wander so the tray visibly updates.
    let wave = (t % 20) as u32;
    let rpm = match mode { "quiet" => 1500, "balanced" => 2500, "performance" => 4000, "custom" => min_rpm, _ => 2100 } + wave * 10;
    TEMPLATE.replace("__RPM__", &rpm.to_string()).replace("__CPU__", &format!("{}.5", 48 + wave % 7))
        .replace("__DISK__", "44.0").replace("__MODE__", mode).replace("__MINRPM__", &min_rpm.to_string())
        .replace("__WARN__", "fake service")
}

fn main() {
    let env = std::env::var("DELLFANS_PIPE").ok();
    let Some(path) = env.as_deref().filter(|s| !s.is_empty()).map(|e| ipc::pipe_path(Some(e))) else {
        eprintln!("DELLFANS_PIPE must name a test pipe");
        std::process::exit(2);
    };
    if path.eq_ignore_ascii_case(ipc::DEFAULT_PIPE) { eprintln!("refusing the production pipe name"); std::process::exit(2); }
    let log = std::env::var("DELLFANS_FAKELOG").ok();
    let wname: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
    let (mut mode, mut min_rpm) = ("balanced".to_string(), 3200u32);
    let pipe = unsafe {
        CreateNamedPipeW(wname.as_ptr(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1,
            65536, 65536, 0, null())
    };
    if pipe == INVALID_HANDLE_VALUE { eprintln!("CreateNamedPipe failed: {}", unsafe { GetLastError() }); std::process::exit(1); }
    println!("fakesvc serving {path}");
    loop {
        unsafe {
            if ConnectNamedPipe(pipe, null_mut()) == 0 && GetLastError() != ERROR_PIPE_CONNECTED { continue; }
        }
        // Like the real service (src\ControlPipe.cs): exactly one request per connection, then the instance is closed.
        #[allow(clippy::never_loop)]
        loop {
            let mut hdr = [0u8; 4];
            if !read_exact(pipe, &mut hdr) { break; }
            let Ok(n) = ipc::frame_len(hdr, MAX_REQUEST) else { break };
            let mut body = vec![0u8; n];
            if !read_exact(pipe, &mut body) { break; }
            let req: serde_json::Value = serde_json::from_slice(&body).unwrap_or_default();
            if let Some(f) = &log {
                if let Ok(mut fh) = std::fs::OpenOptions::new().create(true).append(true).open(f) {
                    let _ = writeln!(fh, "{}", String::from_utf8_lossy(&body));
                }
            }
            if req["Command"] == "SetMode" {
                if let Some(m) = req["Mode"].as_str() { mode = m.to_string(); }
                if let Some(r) = req["MinimumRpm"].as_u64() { min_rpm = r as u32; }
            }
            let t = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
            let out = reply(&mode, min_rpm, t);
            debug_assert!(out.len() < MAX_REPLY);
            let Ok(frame) = ipc::encode_frame_max(out.as_bytes(), MAX_REPLY) else { break };
            let _ = write_all(pipe, &frame);
            unsafe { windows_sys::Win32::Storage::FileSystem::FlushFileBuffers(pipe); }
            break;
        }
        unsafe { DisconnectNamedPipe(pipe); }
    }
}

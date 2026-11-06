//! Test stub for DellFans.Cli.exe: appends its arguments (and a timestamp) to <exe dir>\clistub.log and exits 0.
//! Touches nothing else. Build output is copied next to the watchdog under the name DellFans.Cli.exe.
fn main() {
    let dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf())).unwrap_or_default();
    let ms = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0);
    let args: Vec<String> = std::env::args().skip(1).collect();
    let line = format!("{ms}\t{}\n", args.join("\t"));
    use std::io::Write;
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(dir.join("clistub.log")) {
        let _ = f.write_all(line.as_bytes());
    }
    // Optional: mimic a successful recovery by writing the .recovered marker next to the session file.
    if std::env::var_os("CLISTUB_WRITE_RECOVERED").is_some() {
        if let Some(p) = args.get(1) { let _ = std::fs::write(format!("{p}.recovered"), "{\"AutomaticRestored\":true,\"Error\":null}"); }
    }
    // CLISTUB_EXIT=<n> simulates a --recover exit code: 1 retry, 2 refused path, 3 session alive again.
    // <exe dir>\clistub.exit (one number) overrides it, so a test can change the answer while the watchdog runs.
    let from_file = std::fs::read_to_string(dir.join("clistub.exit")).ok().and_then(|v| v.trim().parse::<i32>().ok());
    if let Some(c) = from_file.or_else(|| std::env::var("CLISTUB_EXIT").ok().and_then(|v| v.parse::<i32>().ok())) {
        // Exit 3 means the parent is alive with a fresh heartbeat, as the real CLI reports.
        if c == 3 { if let Some(p) = args.get(1) { let _ = std::fs::write(format!("{p}.heartbeat"), "alive"); } }
        std::process::exit(c);
    }
}

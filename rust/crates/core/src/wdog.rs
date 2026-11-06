//! Watchdog decision logic; contract identical to FanWatchdog in src/SmmFanControl.cs.
use serde_json::Value;
use std::time::Duration;

pub const HEARTBEAT_STALE: Duration = Duration::from_secs(8);
/// Before the service has armed the session the watchdog only waits this long.
pub const ARM_GRACE: Duration = Duration::from_secs(20);
/// .NET ticks (100 ns since 0001-01-01) at the FILETIME epoch (1601-01-01).
pub const TICKS_AT_FILETIME_EPOCH: i64 = 504_911_232_000_000_000;
pub const RECOVER_BACKOFF_SECS: [u64; 4] = [1, 2, 4, 8];
pub const MAX_RECOVER_TRIES: usize = 5;

pub fn filetime_to_ticks(filetime: u64) -> i64 { filetime as i64 + TICKS_AT_FILETIME_EPOCH }

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum Decision { Wait, Recover, Exit }

/// `saw_armed` must already include the current `armed`. `hb_age` None = heartbeat file missing/unreadable.
pub fn decide(armed: bool, saw_armed: bool, parent_alive: bool, hb_age: Option<Duration>, since_start: Duration) -> Decision {
    if saw_armed && !armed { return Decision::Exit; } // normal end: service removed .armed after restoring
    if !saw_armed && parent_alive && since_start < ARM_GRACE { return Decision::Wait; }
    if armed && parent_alive && hb_age.is_some_and(|a| a < HEARTBEAT_STALE) { return Decision::Wait; }
    if !armed { return Decision::Exit; } // never armed (or already cleaned up): nothing to recover
    Decision::Recover
}

/// What to do after one `DellFans.Cli.exe --recover` run (exit codes in ARCHITECTURE section 3).
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum RecoverAction { Stop, Retry, Resume, StopError }

/// `code` None = the CLI could not be started or ended without an exit code: retry like exit 1.
pub fn after_recover(code: Option<i32>) -> RecoverAction {
    match code {
        Some(0) => RecoverAction::Stop,
        Some(2) => RecoverAction::StopError,
        Some(3) => RecoverAction::Resume,
        _ => RecoverAction::Retry,
    }
}

/// How long the monitor loop sleeps after a recovery pass. `Resume` (exit 3: the parent is alive with a fresh heartbeat again) must not
/// loop straight back into another `--recover` spawn; with the parent handle already signalled or absent nothing else would pace it.
pub fn pause_after(action: RecoverAction) -> Duration {
    match action {
        RecoverAction::Resume => Duration::from_secs(1),
        _ => Duration::ZERO,
    }
}

#[derive(Debug, PartialEq, Eq)]
pub struct Record { pub parent_id: u32, pub parent_start_ticks: i64 }

/// Same validation as FanWatchdog.ReadRecord; Model/Bios are required but only checked, not used.
pub fn parse_record(bytes: &[u8]) -> Result<Record, String> {
    if bytes.len() > 65536 { return Err("record exceeds size limit".into()); }
    let v: Value = serde_json::from_slice(bytes).map_err(|e| e.to_string())?;
    let id = v["ParentId"].as_i64().filter(|x| *x > 0 && *x <= u32::MAX as i64).ok_or("bad ParentId")?;
    let ticks = v["ParentStartTicks"].as_i64().filter(|x| *x > 0).ok_or("bad ParentStartTicks")?;
    for k in ["Model", "Bios"] {
        if v[k].as_str().is_none_or(|s| s.trim().is_empty()) { return Err(format!("missing {k}")); }
    }
    if !matches!(v["RestoreOverride"].as_str(), Some("Enabled" | "Disabled")) { return Err("bad RestoreOverride".into()); }
    Ok(Record { parent_id: id as u32, parent_start_ticks: ticks })
}

#[cfg(test)]
mod tests {
    use super::*;
    const S: fn(u64) -> Duration = Duration::from_secs;

    #[test]
    fn filetime_conversion() {
        // 1970-01-01 as FILETIME = 116444736000000000 -> .NET ticks 621355968000000000
        assert_eq!(filetime_to_ticks(116_444_736_000_000_000), 621_355_968_000_000_000);
        assert_eq!(filetime_to_ticks(0), 504_911_232_000_000_000);
    }
    #[test]
    fn healthy_waits() {
        assert_eq!(decide(true, true, true, Some(S(1)), S(100)), Decision::Wait);
        assert_eq!(decide(true, true, true, Some(Duration::from_millis(7999)), S(100)), Decision::Wait);
    }
    #[test]
    fn stale_heartbeat_recovers() {
        assert_eq!(decide(true, true, true, Some(S(8)), S(100)), Decision::Recover);
        assert_eq!(decide(true, true, true, None, S(100)), Decision::Recover);
    }
    #[test]
    fn parent_gone_recovers_even_with_fresh_heartbeat() {
        assert_eq!(decide(true, true, false, Some(S(0)), S(100)), Decision::Recover);
    }
    #[test]
    fn normal_end_exits() {
        assert_eq!(decide(false, true, true, Some(S(0)), S(100)), Decision::Exit);
        assert_eq!(decide(false, true, false, None, S(100)), Decision::Exit);
    }
    #[test]
    fn before_arming() {
        assert_eq!(decide(false, false, true, None, S(5)), Decision::Wait);
        assert_eq!(decide(false, false, true, None, S(20)), Decision::Exit); // never armed
        assert_eq!(decide(false, false, false, None, S(1)), Decision::Exit); // parent died unarmed: nothing to restore
    }
    #[test]
    fn recover_exit_codes() {
        assert_eq!(after_recover(Some(0)), RecoverAction::Stop);
        assert_eq!(after_recover(Some(1)), RecoverAction::Retry);
        assert_eq!(after_recover(Some(2)), RecoverAction::StopError);
        assert_eq!(after_recover(Some(3)), RecoverAction::Resume);
        assert_eq!(after_recover(Some(-1)), RecoverAction::Retry); // unknown code: never give up on firmware
        assert_eq!(after_recover(None), RecoverAction::Retry);
    }
    #[test]
    fn resume_sleeps_so_the_loop_cannot_spin() {
        assert_eq!(pause_after(RecoverAction::Resume), Duration::from_secs(1));
        for a in [RecoverAction::Stop, RecoverAction::Retry, RecoverAction::StopError] { assert_eq!(pause_after(a), Duration::ZERO); }
    }
    #[test]
    fn record_validation() {
        let ok = br#"{"ParentId":4,"ParentStartTicks":639000000000000000,"Model":"M","Bios":"1.0.0","RestoreOverride":"Enabled","ServiceOwned":true}"#;
        assert_eq!(parse_record(ok).unwrap(), Record { parent_id: 4, parent_start_ticks: 639_000_000_000_000_000 });
        assert!(parse_record(br#"{"ParentId":0,"ParentStartTicks":1,"Model":"M","Bios":"B","RestoreOverride":"Enabled"}"#).is_err());
        assert!(parse_record(br#"{"ParentId":4,"ParentStartTicks":1,"Model":" ","Bios":"B","RestoreOverride":"Enabled"}"#).is_err());
        assert!(parse_record(br#"{"ParentId":4,"ParentStartTicks":1,"Model":"M","Bios":"B","RestoreOverride":"x"}"#).is_err());
        assert!(parse_record(b"{").is_err());
    }
}

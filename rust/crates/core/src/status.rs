//! Parses a `Status`/`SetMode` reply into the few fields the tray shows (ARCHITECTURE section 3).
use crate::mode::{Mode, MAX_RPM, MIN_RPM};
use serde_json::Value;

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Status {
    pub rpm: Option<u32>,
    pub cpu_c: Option<f32>,
    pub disk_c: Option<f32>,
    pub mode: Option<Mode>,
    /// Saved custom minimum from Background.Settings.MinimumRpm (valid range only).
    pub custom_min: Option<u32>,
    pub state: String,
    pub warnings: usize,
    /// Background.SensorDriverState (Installed, Missing, HalfUninstalled, NeedsRestart); empty when the service did not report it.
    pub sensor_driver: String,
}

impl Status {
    /// The PawnIO driver is absent or broken and the user can fix it from the app.
    pub fn driver_installable(&self) -> bool { matches!(self.sensor_driver.as_str(), "Missing" | "HalfUninstalled") }
}

fn valid_temp(v: f64) -> bool { v > 0.0 && v <= 125.0 }

fn usable(row: &Value) -> bool {
    !matches!(row["Quality"].as_str(), Some(q) if ["stale", "invalid", "unavailable", "error"].iter().any(|b| q.eq_ignore_ascii_case(b)))
}

pub fn parse_reply(bytes: &[u8]) -> Result<Status, String> {
    let v: Value = serde_json::from_slice(bytes).map_err(|e| format!("bad JSON: {e}"))?;
    if v["Success"].as_bool() != Some(true) {
        return Err(v["Error"].as_str().unwrap_or("request failed").to_string());
    }
    let snap = v.get("Snapshot").filter(|s| s.is_object()).ok_or("no snapshot")?;
    let mut s = Status::default();
    let (mut pkg, mut core_max, mut cpu_any): (Option<f64>, Option<f64>, Option<f64>) = (None, None, None);
    for row in snap["Sensors"].as_array().map(|a| a.as_slice()).unwrap_or(&[]) {
        let (Some(val), true) = (row["Value"].as_f64(), usable(row)) else { continue };
        let ty = row["Type"].as_str().unwrap_or("");
        let hw = row["HardwareType"].as_str().unwrap_or("");
        let name = row["Name"].as_str().unwrap_or("");
        if ty == "Fan" && (0.0..=100000.0).contains(&val) {
            s.rpm = Some(s.rpm.unwrap_or(0).max(val.round() as u32));
        } else if ty == "Temperature" && valid_temp(val) {
            let max = |o: Option<f64>| Some(o.map_or(val, |x| x.max(val)));
            if hw == "Storage" { s.disk_c = max(s.disk_c.map(f64::from)).map(|x| x as f32); }
            else if hw == "Cpu" {
                if name == "CPU Package" { pkg = max(pkg); }
                else if name == "Core Max" { core_max = max(core_max); }
                cpu_any = max(cpu_any);
            }
        }
    }
    let bg = &snap["Background"];
    let ctrl = bg["ControlTemperatureC"].as_f64().filter(|t| valid_temp(*t));
    s.cpu_c = pkg.or(core_max).or(cpu_any).or(ctrl).map(|x| x as f32);
    s.mode = bg["Mode"].as_str().and_then(Mode::parse);
    s.state = bg["State"].as_str().unwrap_or("").to_string();
    s.custom_min = bg["Settings"]["MinimumRpm"].as_u64().map(|x| x as u32).filter(|x| (MIN_RPM..=MAX_RPM).contains(x));
    s.sensor_driver = bg["SensorDriverState"].as_str().or(snap["SensorDriverState"].as_str()).unwrap_or("").to_string();
    s.warnings = snap["Warnings"].as_array().map_or(0, |a| a.len());
    Ok(s)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(rpm: &str, cpu: &str, disk: &str, mode: &str, minrpm: &str) -> String {
        include_str!("../fixtures/status.json").replace("__RPM__", rpm).replace("__CPU__", cpu)
            .replace("__DISK__", disk).replace("__MODE__", mode).replace("__MINRPM__", minrpm).replace("__WARN__", "w")
    }

    #[test]
    fn parses_fields_the_tray_uses() {
        let s = parse_reply(sample("2450", "52.5", "41.0", "custom", "3200").as_bytes()).unwrap();
        assert_eq!(s.rpm, Some(2450));
        assert_eq!(s.cpu_c, Some(52.5));
        assert_eq!(s.disk_c, Some(41.0)); // hottest of the two drives
        assert_eq!(s.mode, Some(Mode::Custom));
        assert_eq!(s.custom_min, Some(3200));
        assert_eq!(s.state, "Running");
        assert_eq!(s.warnings, 1);
    }
    #[test]
    fn null_and_invalid_values_are_absent() {
        let s = parse_reply(sample("null", "0.0", "null", "auto", "null").as_bytes()).unwrap();
        assert_eq!((s.rpm, s.disk_c), (None, Some(38.0))); // second drive still valid
        assert_eq!(s.cpu_c, None);
        assert_eq!(s.mode, Some(Mode::Auto));
        assert_eq!(s.custom_min, None);
    }
    #[test]
    fn out_of_range_minimum_ignored() {
        assert_eq!(parse_reply(sample("1", "50", "40", "custom", "999").as_bytes()).unwrap().custom_min, None);
    }
    #[test]
    fn unknown_mode_is_none() {
        assert_eq!(parse_reply(sample("1", "50", "40", "turbo", "2000").as_bytes()).unwrap().mode, None);
    }
    #[test]
    fn failure_reply() {
        let e = parse_reply(br#"{"Success":false,"Error":"denied","Snapshot":null}"#).unwrap_err();
        assert_eq!(e, "denied");
        assert!(parse_reply(br#"{"Success":true}"#).is_err());
        assert!(parse_reply(b"not json").is_err());
    }
    #[test]
    fn sensor_driver_state() {
        let r = |bg: &str, top: &str| format!(r#"{{"Success":true,"Snapshot":{{{top}"Sensors":[],"Background":{{"State":"Idle"{bg}}}}}}}"#);
        let s = parse_reply(r(r#","SensorDriverState":"Missing""#, "").as_bytes()).unwrap();
        assert!(s.driver_installable() && s.sensor_driver == "Missing");
        assert!(parse_reply(r(r#","SensorDriverState":"HalfUninstalled""#, "").as_bytes()).unwrap().driver_installable());
        assert!(parse_reply(r("", r#""SensorDriverState":"Missing","#).as_bytes()).unwrap().driver_installable()); // top-level fallback
        for ok in ["Installed", "NeedsRestart"] { assert!(!parse_reply(r(&format!(r#","SensorDriverState":"{ok}""#), "").as_bytes()).unwrap().driver_installable()); }
        assert!(!parse_reply(r("", "").as_bytes()).unwrap().driver_installable());
    }
    #[test]
    fn control_temperature_fallback() {
        let r = br#"{"Success":true,"Snapshot":{"Sensors":[],"Background":{"State":"Idle","ControlTemperatureC":61.0}}}"#;
        assert_eq!(parse_reply(r).unwrap().cpu_c, Some(61.0));
    }
}

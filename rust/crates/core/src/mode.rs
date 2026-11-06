#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Mode { Quiet, Balanced, Performance, Custom, Auto }

pub const MIN_RPM: u32 = 1000;
pub const MAX_RPM: u32 = 5570;
/// Used by "custom" only when the service has not reported a saved custom minimum yet.
pub const DEFAULT_CUSTOM_RPM: u32 = 2500;

impl Mode {
    pub const ALL: [Mode; 5] = [Mode::Quiet, Mode::Balanced, Mode::Performance, Mode::Custom, Mode::Auto];
    pub fn id(self) -> &'static str {
        match self {
            Mode::Quiet => "quiet", Mode::Balanced => "balanced", Mode::Performance => "performance",
            Mode::Custom => "custom", Mode::Auto => "auto",
        }
    }
    pub fn parse(s: &str) -> Option<Mode> {
        Mode::ALL.iter().copied().find(|m| m.id().eq_ignore_ascii_case(s))
    }
}

/// HKCU `DockVisible`: absent means ON; only an explicit 0 turns the deck off.
pub fn deck_wanted(reg: Option<u32>) -> bool { reg.is_none_or(|v| v != 0) }

/// Menu / deck button -> SetMode request JSON. Custom carries the saved minimum (clamped 1000-5570).
pub fn set_mode_request(mode: Mode, saved_custom_min: Option<u32>) -> String {
    if mode == Mode::Custom {
        let rpm = saved_custom_min.unwrap_or(DEFAULT_CUSTOM_RPM).clamp(MIN_RPM, MAX_RPM);
        format!(r#"{{"Command":"SetMode","Mode":"custom","MinimumRpm":{rpm}}}"#)
    } else {
        format!(r#"{{"Command":"SetMode","Mode":"{}"}}"#, mode.id())
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn deck_defaults_on() {
        assert!(super::deck_wanted(None));
        assert!(super::deck_wanted(Some(1)));
        assert!(!super::deck_wanted(Some(0)));
    }
    use super::*;
    #[test]
    fn plain_modes() {
        for m in [Mode::Quiet, Mode::Balanced, Mode::Performance, Mode::Auto] {
            let v: serde_json::Value = serde_json::from_str(&set_mode_request(m, Some(3000))).unwrap();
            assert_eq!(v["Command"], "SetMode");
            assert_eq!(v["Mode"], m.id());
            assert!(v.get("MinimumRpm").is_none());
        }
        assert_eq!(set_mode_request(Mode::Quiet, None), r#"{"Command":"SetMode","Mode":"quiet"}"#);
    }
    #[test]
    fn custom_uses_saved_minimum() {
        assert_eq!(set_mode_request(Mode::Custom, Some(3200)), r#"{"Command":"SetMode","Mode":"custom","MinimumRpm":3200}"#);
        assert!(set_mode_request(Mode::Custom, None).contains(r#""MinimumRpm":2500"#));
        assert!(set_mode_request(Mode::Custom, Some(99)).contains(r#""MinimumRpm":1000"#));
        assert!(set_mode_request(Mode::Custom, Some(9999)).contains(r#""MinimumRpm":5570"#));
    }
    #[test]
    fn parse_ids() {
        assert_eq!(Mode::parse("Balanced"), Some(Mode::Balanced));
        assert_eq!(Mode::parse("x"), None);
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Rect { pub l: i32, pub t: i32, pub r: i32, pub b: i32 }

/// A window is "fullscreen" when its rect equals its monitor's rect within `tol` px on every edge.
/// Equality (not containment) keeps maximised windows out: their invisible frame overflows the monitor.
pub fn is_fullscreen(win: Rect, mon: Rect, tol: i32) -> bool {
    (win.l - mon.l).abs() <= tol && (win.t - mon.t).abs() <= tol &&
    (win.r - mon.r).abs() <= tol && (win.b - mon.b).abs() <= tol
}

/// Hide the deck only when the fullscreen window sits on the deck's own monitor.
pub fn blocks_deck(win: Rect, win_monitor: Rect, deck_monitor: Rect, tol: i32) -> bool {
    win_monitor == deck_monitor && is_fullscreen(win, win_monitor, tol)
}

#[cfg(test)]
mod tests {
    use super::*;
    const M1: Rect = Rect { l: 0, t: 0, r: 1920, b: 1080 };
    const M2: Rect = Rect { l: 1920, t: 0, r: 3840, b: 1080 };
    #[test]
    fn exact_and_slightly_off() {
        assert!(is_fullscreen(M1, M1, 2));
        assert!(is_fullscreen(Rect { l: -1, t: 0, r: 1921, b: 1080 }, M1, 2));
    }
    #[test]
    fn maximised_with_frame_overflow_is_not_fullscreen() {
        assert!(!is_fullscreen(Rect { l: -8, t: -8, r: 1928, b: 1088 }, M1, 2));
        assert!(!is_fullscreen(Rect { l: 0, t: 0, r: 1920, b: 1040 }, M1, 2)); // work area only
    }
    #[test]
    fn other_monitor_does_not_block() {
        assert!(blocks_deck(M1, M1, M1, 2));
        assert!(!blocks_deck(M2, M2, M1, 2));
    }
}

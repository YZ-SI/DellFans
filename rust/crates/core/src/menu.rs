//! Tray popup menu model: pure layout, hit-testing, keyboard stepping and screen placement (logical px).
use crate::mode::Mode;
use crate::rect::Rect;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Cmd { Open, InstallDriver, Mode(Mode), Deck, Autostart, Exit }

/// [open] | [modes] | [deck, autostart] | [exit]
pub const GROUPS: [&[Cmd]; 4] = [
    &[Cmd::Open],
    &[Cmd::Mode(Mode::Quiet), Cmd::Mode(Mode::Balanced), Cmd::Mode(Mode::Performance), Cmd::Mode(Mode::Custom), Cmd::Mode(Mode::Auto)],
    &[Cmd::Deck, Cmd::Autostart],
    &[Cmd::Exit],
];

/// Same menu with the PawnIO install item on top, shown while the driver is Missing / HalfUninstalled.
pub const GROUPS_DRIVER: [&[Cmd]; 5] = [
    &[Cmd::InstallDriver],
    GROUPS[0], GROUPS[1], GROUPS[2], GROUPS[3],
];

pub const WIDTH: f32 = 228.0;
pub const PAD: f32 = 8.0;
pub const ROW_H: f32 = 34.0;
pub const SEP_H: f32 = 9.0;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Row { pub cmd: Cmd, pub x: f32, pub y: f32, pub w: f32, pub h: f32 }

#[derive(Debug)]
pub struct Layout { pub rows: Vec<Row>, /// y of each separator band (top)
    pub seps: Vec<f32>, pub w: f32, pub h: f32 }

pub fn layout(groups: &[&[Cmd]]) -> Layout {
    let (mut rows, mut seps, mut y) = (Vec::new(), Vec::new(), PAD);
    for (gi, g) in groups.iter().enumerate() {
        if gi > 0 { seps.push(y); y += SEP_H; }
        for c in g.iter() {
            rows.push(Row { cmd: *c, x: PAD, y, w: WIDTH - 2.0 * PAD, h: ROW_H });
            y += ROW_H;
        }
    }
    Layout { rows, seps, w: WIDTH, h: y + PAD }
}

/// Row under a point in card-relative logical coordinates.
pub fn hit(l: &Layout, x: f32, y: f32) -> Option<usize> {
    l.rows.iter().position(|r| x >= r.x && x < r.x + r.w && y >= r.y && y < r.y + r.h)
}

/// Up/Down navigation with wrap-around; with no selection Down picks the first and Up the last row.
pub fn step(cur: Option<usize>, n: usize, delta: i32) -> usize {
    match cur {
        None => if delta >= 0 { 0 } else { n - 1 },
        Some(i) => (i as i32 + delta).rem_euclid(n as i32) as usize,
    }
}

/// Card origin in screen px: above the cursor (taskbar at the bottom), flipped below when there is no room,
/// then clamped into the work area. `size` is the card size in px.
pub fn place(cursor: (i32, i32), size: (i32, i32), work: Rect) -> (i32, i32) {
    let mut y = cursor.1 - size.1;
    if y < work.t { y = cursor.1; }
    let x = cursor.0.clamp(work.l, (work.r - size.0).max(work.l));
    let y = y.clamp(work.t, (work.b - size.1).max(work.t));
    (x, y)
}

#[cfg(test)]
mod tests {
    use super::*;
    const WORK: Rect = Rect { l: 0, t: 0, r: 1920, b: 1040 };

    #[test]
    fn layout_dimensions() {
        let l = layout(&GROUPS);
        assert_eq!(l.rows.len(), 9);
        assert_eq!(l.seps.len(), 3);
        assert_eq!(l.h, 2.0 * PAD + 9.0 * ROW_H + 3.0 * SEP_H);
        assert_eq!(l.w, WIDTH);
        assert_eq!(l.rows[0].cmd, Cmd::Open);
        assert_eq!(l.rows[1].cmd, Cmd::Mode(Mode::Quiet));
        assert_eq!(l.rows[5].cmd, Cmd::Mode(Mode::Auto));
        assert_eq!(l.rows[8].cmd, Cmd::Exit);
    }
    #[test]
    fn driver_item_is_first_when_offered() {
        let l = layout(&GROUPS_DRIVER);
        assert_eq!(l.rows.len(), 10);
        assert_eq!(l.rows[0].cmd, Cmd::InstallDriver);
        assert_eq!(l.rows[1].cmd, Cmd::Open);
        assert_eq!(l.seps.len(), 4);
    }
    #[test]
    fn rows_ordered_inside_card_with_gaps_at_groups() {
        let l = layout(&GROUPS);
        for p in l.rows.windows(2) { assert!(p[1].y >= p[0].y + p[0].h); }
        assert_eq!(l.rows[1].y - (l.rows[0].y + ROW_H), SEP_H); // group boundary
        assert_eq!(l.rows[2].y - (l.rows[1].y + ROW_H), 0.0); // same group
        assert!(l.rows.iter().all(|r| r.x >= 0.0 && r.x + r.w <= l.w && r.y + r.h <= l.h));
        assert_eq!(l.seps[0], PAD + ROW_H);
    }
    #[test]
    fn hit_testing() {
        let l = layout(&GROUPS);
        for (i, r) in l.rows.iter().enumerate() { assert_eq!(hit(&l, r.x + r.w / 2.0, r.y + r.h / 2.0), Some(i)); }
        assert_eq!(hit(&l, 100.0, l.seps[0] + 4.0), None); // separator band
        assert_eq!(hit(&l, 2.0, 20.0), None); // left padding
        assert_eq!(hit(&l, 100.0, l.h + 1.0), None);
        assert_eq!(hit(&l, 100.0, -1.0), None);
        let r = l.rows[3];
        assert_eq!(hit(&l, r.x, r.y), Some(3)); // inclusive top-left
        assert_eq!(hit(&l, r.x + r.w, r.y), None); // exclusive right edge
    }
    #[test]
    fn keyboard_steps() {
        assert_eq!(step(None, 9, 1), 0);
        assert_eq!(step(None, 9, -1), 8);
        assert_eq!(step(Some(8), 9, 1), 0);
        assert_eq!(step(Some(0), 9, -1), 8);
        assert_eq!(step(Some(3), 9, 1), 4);
    }
    #[test]
    fn placement_above_cursor() {
        assert_eq!(place((1000, 1000), (228, 400), WORK), (1000, 600));
    }
    #[test]
    fn placement_clamps_right_and_flips_below() {
        assert_eq!(place((1900, 1000), (228, 400), WORK), (1692, 600));
        assert_eq!(place((100, 200), (228, 400), WORK), (100, 200)); // no room above -> below the cursor
        assert_eq!(place((100, 900), (228, 400), WORK).1, 500); // above fits
        // below-flip that would overflow the bottom gets clamped
        assert_eq!(place((100, 700), (228, 400), Rect { l: 0, t: 400, r: 1920, b: 1000 }), (100, 600));
    }
    #[test]
    fn placement_second_monitor() {
        let w2 = Rect { l: -1920, t: 0, r: 0, b: 1040 };
        assert_eq!(place((-10, 1000), (228, 400), w2), (-228, 600));
        assert_eq!(place((-1919, 1000), (228, 400), w2), (-1919, 600));
    }
    #[test]
    fn placement_larger_than_work_area_pins_top_left() {
        assert_eq!(place((50, 50), (2000, 2000), WORK), (0, 0));
    }
}

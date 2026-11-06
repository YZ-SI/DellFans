// The watchdog has no window; the app icon only makes the exe look like the rest of DellFans in Explorer.
include!("../../build-support/icons_res.rs");

fn main() {
    let icons = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../assets/icon");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") { return; }
    link_icons(&[&icons.join("dellfans.ico")]);
}

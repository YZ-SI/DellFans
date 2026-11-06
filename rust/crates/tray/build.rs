// Embeds the app icon (group 1, the exe icon) and the two tray glyphs (group 2 = for a light taskbar, 3 = for a dark taskbar).
include!("../../build-support/icons_res.rs");

fn main() {
    let icons = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../assets/icon");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") { return; }
    link_icons(&[&icons.join("dellfans.ico"), &icons.join("tray-light.ico"), &icons.join("tray-dark.ico")]);
}

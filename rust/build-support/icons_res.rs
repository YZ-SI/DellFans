// Shared by the build.rs of dellfans-tray and dellfans-watchdog (include!). Writes a Win32 .res file holding .ico files as
// RT_ICON / RT_GROUP_ICON resources, so no resource-compiler crate or SDK tool is needed, and links it into the exe.
// Group ids follow the order of `icons`: 1 = first file (the exe icon Explorer shows), 2 = second, ...
use std::path::Path;

fn le16(b: &[u8], o: usize) -> u16 { u16::from_le_bytes([b[o], b[o + 1]]) }
fn le32(b: &[u8], o: usize) -> u32 { u32::from_le_bytes([b[o], b[o + 1], b[o + 2], b[o + 3]]) }

fn res_entry(out: &mut Vec<u8>, ty: u16, id: u16, data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_le_bytes());
    out.extend_from_slice(&32u32.to_le_bytes()); // header size
    out.extend_from_slice(&[0xFF, 0xFF]); out.extend_from_slice(&ty.to_le_bytes());
    out.extend_from_slice(&[0xFF, 0xFF]); out.extend_from_slice(&id.to_le_bytes());
    out.extend_from_slice(&0u32.to_le_bytes()); // data version
    out.extend_from_slice(&0x1030u16.to_le_bytes()); // MOVEABLE | PURE | DISCARDABLE
    out.extend_from_slice(&0x0409u16.to_le_bytes()); // en-US
    out.extend_from_slice(&0u32.to_le_bytes()); // version
    out.extend_from_slice(&0u32.to_le_bytes()); // characteristics
    out.extend_from_slice(data);
    while out.len() % 4 != 0 { out.push(0); }
}

pub fn link_icons(icons: &[&Path]) {
    let mut res = Vec::new();
    res_entry(&mut res, 0, 0, &[]); // mandatory empty first entry (type/name 0)
    res[8..12].copy_from_slice(&[0xFF, 0xFF, 0, 0]);
    res[12..16].copy_from_slice(&[0xFF, 0xFF, 0, 0]);
    let mut next_id: u16 = 1;
    for (group, path) in icons.iter().enumerate() {
        println!("cargo:rerun-if-changed={}", path.display());
        let ico = std::fs::read(path).unwrap_or_else(|e| panic!("cannot read {}: {e}", path.display()));
        assert!(le16(&ico, 0) == 0 && le16(&ico, 2) == 1, "{} is not an .ico file", path.display());
        let count = le16(&ico, 4) as usize;
        let mut dir = Vec::new();
        dir.extend_from_slice(&[0, 0, 1, 0]);
        dir.extend_from_slice(&(count as u16).to_le_bytes());
        for i in 0..count {
            let e = 6 + i * 16;
            let (size, offset) = (le32(&ico, e + 8) as usize, le32(&ico, e + 12) as usize);
            res_entry(&mut res, 3, next_id, &ico[offset..offset + size]);
            dir.extend_from_slice(&ico[e..e + 12]); // width, height, colors, reserved, planes, bit count, bytes in resource
            dir.extend_from_slice(&next_id.to_le_bytes());
            next_id += 1;
        }
        res_entry(&mut res, 14, group as u16 + 1, &dir);
    }
    let out = std::path::PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("icons.res");
    std::fs::write(&out, &res).unwrap();
    println!("cargo:rustc-link-arg-bins={}", out.display());
}

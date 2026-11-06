#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Theme { White, Black, PurpleBlack }

/// Colours are 0xRRGGBB.
#[derive(Clone, Copy)]
pub struct Palette {
    pub bg: u32, pub chip: u32, pub chip_hot: u32, pub text: u32, pub muted: u32,
    pub accent: u32, pub border: u32, pub warn: u32, pub shadow: u8,
}

impl Theme {
    /// Unknown or missing id -> black (default).
    pub fn from_id(s: &str) -> Theme {
        match s { "white" => Theme::White, "purple-black" => Theme::PurpleBlack, _ => Theme::Black }
    }
    pub fn id(self) -> &'static str {
        match self { Theme::White => "white", Theme::Black => "black", Theme::PurpleBlack => "purple-black" }
    }
    pub fn palette(self) -> Palette {
        match self {
            Theme::White => Palette { bg: 0xF7F8FA, chip: 0xE8EBF1, chip_hot: 0xD9DEE8, text: 0x1B1F27, muted: 0x687080,
                accent: 0x3B6FE0, border: 0xCDD3DE, warn: 0xD9534F, shadow: 70 },
            Theme::Black => Palette { bg: 0x17181B, chip: 0x25272C, chip_hot: 0x34373E, text: 0xF2F3F5, muted: 0x9BA0AA,
                accent: 0x4C8DFF, border: 0x34363C, warn: 0xFF6B6B, shadow: 150 },
            Theme::PurpleBlack => Palette { bg: 0x14111C, chip: 0x241E33, chip_hot: 0x352C4B, text: 0xEFEAFB, muted: 0xA79CC4,
                accent: 0x9B6BFF, border: 0x3A3054, warn: 0xFF6B8A, shadow: 160 },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ids() {
        for t in [Theme::White, Theme::Black, Theme::PurpleBlack] { assert_eq!(Theme::from_id(t.id()), t); }
        assert_eq!(Theme::from_id(""), Theme::Black);
        assert_eq!(Theme::from_id("neon"), Theme::Black);
    }
}

//! All user-visible strings in one table: columns zh-CN, zh-TW, en.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Lang { ZhCn = 0, ZhTw = 1, En = 2 }

#[derive(Clone, Copy)]
#[repr(usize)]
pub enum K {
    OpenMain, Mode, Quiet, Balanced, Performance, PerfShort, Custom, Auto, Deck, Autostart, Exit,
    Disconnected, Cpu, Disk, Rpm, Tip, InstallDriver,
}

static T: [[&str; 3]; 17] = [
    ["打开主界面", "開啟主介面", "Open DellFans"],
    ["模式", "模式", "Mode"],
    ["静音", "靜音", "Quiet"],
    ["平衡", "平衡", "Balanced"],
    ["高性能", "高效能", "Performance"],
    ["高性能", "高效能", "Perf"],
    ["自定义", "自訂", "Custom"],
    ["BIOS 自动", "BIOS 自動", "BIOS auto"],
    ["显示 deck", "顯示 deck", "Show deck"],
    ["登录后启动", "登入後啟動", "Start at sign-in"],
    ["退出 DellFans", "結束 DellFans", "Exit DellFans"],
    ["后台断连", "後台斷連", "Service disconnected"],
    ["CPU", "CPU", "CPU"],
    ["硬盘", "硬碟", "Disk"],
    ["转速", "轉速", "Fan"],
    ["DellFans", "DellFans", "DellFans"],
    ["安装 CPU 温度驱动…", "安裝 CPU 溫度驅動…", "Install CPU temperature driver…"],
];

pub fn tr(l: Lang, k: K) -> &'static str { T[k as usize][l as usize] }

impl Lang {
    /// Windows LANGID (GetUserDefaultUILanguage): zh-TW/HK/MO -> traditional, other zh -> simplified, rest English.
    pub fn from_langid(id: u16) -> Lang {
        if id & 0x3FF != 0x04 { return Lang::En; }
        match id { 0x0404 | 0x0C04 | 0x1404 | 0x7C04 => Lang::ZhTw, _ => Lang::ZhCn }
    }
    pub fn from_id(s: &str) -> Option<Lang> {
        match s.to_ascii_lowercase().as_str() {
            "zh-cn" => Some(Lang::ZhCn), "zh-tw" => Some(Lang::ZhTw), "en" => Some(Lang::En), _ => None,
        }
    }
    pub fn mode_name(self, m: crate::mode::Mode) -> &'static str {
        use crate::mode::Mode::*;
        tr(self, match m { Quiet => K::Quiet, Balanced => K::Balanced, Performance => K::Performance, Custom => K::Custom, Auto => K::Auto })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn langid() {
        assert_eq!(Lang::from_langid(0x0804), Lang::ZhCn);
        assert_eq!(Lang::from_langid(0x0404), Lang::ZhTw);
        assert_eq!(Lang::from_langid(0x0409), Lang::En);
        assert_eq!(Lang::from_langid(0x0411), Lang::En);
    }
    #[test]
    fn table_complete() {
        assert_eq!(tr(Lang::ZhCn, K::Tip), "DellFans");
        assert_eq!(tr(Lang::En, K::Exit), "Exit DellFans");
        assert_eq!(tr(Lang::ZhCn, K::InstallDriver), "安装 CPU 温度驱动…");
        assert!(T.iter().all(|r| r.iter().all(|s| !s.is_empty())));
        assert_eq!(Lang::En.mode_name(crate::mode::Mode::Auto), "BIOS auto");
    }
}

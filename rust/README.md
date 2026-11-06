# DellFans Rust resident processes

Two small Windows processes of DellFans (see `docs/ARCHITECTURE.md` sections 2, 3, 6):

| Output name | Cargo bin | Role |
|---|---|---|
| `DellFans.Tray.exe` | `dellfans-tray` | tray icon, custom tray menu, deck; IPC client only, no hardware access |
| `DellFans.Watchdog.exe` | `dellfans-watchdog` | decides and launches `DellFans.Cli.exe --recover <session>`; no WMI, no SMM |
| (test only) | `dellfans-fakesvc`, `dellfans-clistub` | fake pipe server, stub CLI; never shipped |

Workspace: `crates/core` (`dfcore`: all pure logic, unit tested), `crates/tray`, `crates/watchdog`, `crates/testtools`.
Dependencies: `windows-sys` 0.61 and `serde_json` only.

## Build and test

```powershell
.\build.ps1                 # cargo build --release --offline, copies dist\DellFans.Tray.exe / DellFans.Watchdog.exe
.\build.ps1 -TestHooks      # also builds the test-hooks tray into dist-test\ (needed by tray-measure / menu-test)
cargo test --offline        # unit tests (dfcore and tray)
.\tests\watchdog-test.ps1   # watchdog against `ping -t` parent + stub CLI (needs build.ps1 first)
..\tests\run-rust-integration.ps1   # repo-level: real C# ProcessWatchdogStarter vs Rust watchdog + stub CLI (builds everything, ~40 s)
.\tests\tray-measure.ps1 -Deck 1 -Seconds 90   # tray vs fake service: private bytes, GDI, USER
.\tests\menu-test.ps1 -Cycles 50 -Shot docs\menu.png   # custom menu open/close x50, keyboard, dismissal
```

The scripts write only to a temp directory, `HKCU\Software\DellFans\Ui` (removed afterwards if it did not exist) and
`%LOCALAPPDATA%\DellFans\tray.log`. They use the `dist-test` tray and a private pipe name (`DELLFANS_PIPE`).

## Tray

* IPC `\\.\pipe\DellFans.Control.v2`: frames = 4-byte LE length + UTF-8 JSON (64 KiB request / 4 MiB reply). Worker thread polls
  `{"Command":"Status","Detail":false}` every 1 s while the deck is visible, otherwise every 5 s; `SetMode` is queued from the UI.
  Opened with `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`; every read/write is overlapped with a 3 s timeout.
  The server process (`GetNamedPipeServerProcessId`) must be `DellFans.Service.exe` in the tray's own folder (`QueryFullProcessImageNameW`) with an
  elevated token (`TokenElevation`). Always enforced in release builds. Disconnects show "后台断连 / 後台斷連 / Service disconnected" and retry; when no
  controller is running at all, the tray runs `DellFans.exe --start-background` once, which asks for UAC and starts it. "Exit DellFans" sends `{"Command":"Exit"}`
  (the controller hands the fan back to BIOS automatic and exits) before the tray quits.
* Status fields used: `Sensors[]` (`Type=="Fan"` max Value; `HardwareType=="Cpu"`, `Type=="Temperature"`, prefer `CPU Package`,
  then `Core Max`, any Cpu temp, then `Background.ControlTemperatureC`; hottest `HardwareType=="Storage"` temperature, valid 0-125 C),
  `Background.Mode`, `Background.Settings.MinimumRpm` (saved custom minimum, valid 1000-5570), `Background.State`, `Warnings`.
  "custom" in the menu or deck sends `{"Command":"SetMode","Mode":"custom","MinimumRpm":<saved>}` (2500 if the service has not reported one).
* Tray menu: custom-drawn layered popup (not the native menu), same card style, palette and font as the deck.
  Groups `[打开主界面] | [静音 平衡 高性能 自定义 BIOS 自动 (single select, current marked)] | [显示 deck ✓, 登录后启动 ✓] | [退出托盘]`.
  Opens above the cursor (flips below if no room), clamped to the work area of the monitor under the cursor, per-monitor DPI.
  Closes on outside click, focus loss, Esc, or after a choice; Up/Down/Home/End/Tab/Enter/Space work.
  Window and bitmap exist only while the menu is open. Left click on the tray icon opens the main window.
  Layout, hit-testing, keyboard stepping and placement are pure functions in `dfcore::menu` with unit tests.
* Deck: layered always-on-top capsule / card (`UpdateLayeredWindow`, per-pixel alpha, SDF-antialiased rounded corners, soft shadow),
  hover or click (pin) expands. Drag to move; right-click opens the tray menu. Hidden while a window whose rect equals its monitor
  rect (within 2 px; maximised windows overflow by their frame and do not count) is foreground on the deck's monitor; checked every 2 s
  and on `WM_DISPLAYCHANGE`. When the deck is off there is no deck window, no fullscreen timer, and IPC polls every 5 s.
  Redraws only when the shown values change. Collapsed capsule: mode dot + RPM + CPU; expanded: RPM, CPU, hottest disk,
  quiet / balanced / performance / custom buttons, open main window. Mode icon is a coloured dot (no fan glyph yet).
* Preferences (`HKCU\Software\DellFans\Ui`, re-read on every poll so the settings page stays in sync): `DockVisible` DWORD (absent = on; 0 = off),
  `DockX`/`DockY` DWORD (screen pixels of the card's top-left), `Theme` string `white|black|purple-black` (default `black`),
  optional `Language` string `zh-CN|zh-TW|en` (otherwise the system UI language: zh-TW/HK/MO -> zh-TW, other zh -> zh-CN, else en).
  "登录后启动" = `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `DellFans.Tray` (quoted exe path); no Task Scheduler.
* "打开主界面" starts `DellFans.exe` from the tray exe's directory. Single instance via `Local\DellFans.Tray.v2`.
  Log: `%LOCALAPPDATA%\DellFans\tray.log` (state transitions only, truncated past 32 KiB).
* Test knob: only in builds with cargo feature `test-hooks` (off by default; `build.ps1 -TestHooks` -> `dist-test\`): `DELLFANS_PIPE`
  (bare name or full path) points the tray at the fake service and skips the identity check. Release builds do not read it.

## Watchdog

`DellFans.Watchdog.exe <session-file>`; same contract as `FanWatchdog` in `src/SmmFanControl.cs`:
record JSON (`ParentId`, `ParentStartTicks` = UTC .NET ticks, `Model`, `Bios`, `RestoreOverride`, `ServiceOwned`) validated like
`ReadRecord`; invalid -> `<session>.recovery-invalid`, exit 1; file name must start with `fan-watchdog-` (exit 2).
Writes `<session>.ready`, opens the parent (`SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION`), checks its start time
(`GetProcessTimes`, ticks = FILETIME + 504911232000000000; mismatch = parent gone), loops on `WaitForSingleObject(parent, 1000)`.
`dfcore::wdog::decide` mirrors the C# loop: exit 0 when `.armed` disappears after being seen; wait while unarmed for up to 20 s;
wait while armed, parent alive and `<session>.heartbeat` mtime < 8 s; otherwise recover. Recovery runs
`DellFans.Cli.exe --recover "<session>"` from the watchdog's directory (hidden), waits for it (never kills it) and maps its exit code: 0 stop (exit 0); 1 retry, up to 5 tries with 1, 2, 4, 8 s backoff, stops early
on `<session>.recovered` or `.armed` removal, exit 1 when exhausted; 2 path refused (write `<session>.watchdog-error`, exit 2);
3 parent alive with a fresh heartbeat again (resume monitoring).
Difference from C#: the C# checks that the record lives in the reports directory; Rust only checks the file-name prefix (the CLI
`--recover` still enforces the directory).

## Measured (release build, Windows 10 x64)

| Item | Result |
|---|---|
| `DellFans.Tray.exe` / `DellFans.Watchdog.exe` size | 228,864 / 243,200 bytes |
| Tray, deck ON, 90 s vs fake service (median of last half, 2 s samples) | 1.94 MiB private (max 2.03), GDI 14, USER 8 (flat) |
| Tray, deck OFF, 90 s | 1.37 MiB private (max 1.46), GDI 7, USER 5 (flat) |
| Tray, custom menu opened/closed 50x (deck on) | 2.39 -> 2.57 MiB private, GDI 17 -> 17, USER 18 -> 18, handles 197 -> 198 |
| Tray, deck switched on/off 12x via registry | GDI 8, USER 5 constant, private 1.58 MiB |
| Watchdog, idle with armed session | 0.61 MiB private, 42 handles |
| Watchdog: heartbeat stops (parent alive) | recovery launched 8.3 s after last touch |
| Watchdog: parent killed | recovery launched 0.07 s later |
| Watchdog: `.armed` removed | exit 0 after 0.94 s, no recovery |
| Watchdog: failing CLI | 5 calls, gaps 1 / 2 / 4 / 8 s, exit 1 |

Screenshots are produced under `docs/` by the scripts (not tracked: they include the desktop behind the deck).

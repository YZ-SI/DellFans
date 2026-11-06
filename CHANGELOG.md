# Changelog

## 0.1.0 — first release

### Programs
- Portable package, nothing to install: unzip and run `DellFans.exe`.
- `DellFans.Service.exe --portable` (C#, an elevated background process started with the user's consent, not a Windows service): sensors, mode control, SMM/BIOS fan writes, local control pipe, capability probe.
- `DellFans.Watchdog.exe` (Rust): hands the fan back to the BIOS when the controller fails.
- `DellFans.Tray.exe` (Rust): tray icon with a custom menu ("Exit DellFans" hands the fan back to the BIOS first), desktop deck (expands after a 0.3 s rest or a click, pin to keep it open), quick mode switch, sign-in start.
- `DellFans.exe` (C# WinForms, unelevated, on demand): home, details and settings pages; closing the window exits the process.
- `DellFans.Cli.exe`: snapshot, monitor, mode, capability, sensor verification, offline preview rendering, recovery and PawnIO installation.

### Fan control
- Modes: quiet (1500–3000 RPM, from 55 °C), balanced (2500–full, from 65 °C), performance (4000–full, from 65 °C), custom (user minimum 1000–5570, from 65 °C) and auto (BIOS control).
  Above the start temperature the target rises 50 RPM/s; below start − 3 °C for 20 s it falls 100 RPM/s; otherwise it holds. Switching modes while cool starts at the new minimum.
- 90 °C: full speed in every mode, back to the mode rules at 85 °C or lower.
- Stale CPU temperature, feedback faults or sustained resource overuse hand the fan back to BIOS auto; the saved mode is kept.
- The control thread runs at raised priority; if the system still delays it by more than 3 s, it hands back and resumes after 10 s.
- First run uses balanced mode; control starts once the computer passes the capability check.

### Sensors
- CPU package, core maximum and core average from Intel MSR through the PawnIO driver (installed only after consent, pinned version, SHA-256 and signer checked, driver load confirmed afterwards); Intel ESIF fallback.
- NVMe (IOCTL) and SATA (SMART) disk temperatures.
- `--verify-sensors` compares against HWiNFO shared memory, Intel ESIF and the controller snapshot and reports timing.

### Security
- Control pipe `\\.\pipe\DellFans.Control.v2`: local interactive users and administrators only, remote (SMB) clients and network logons rejected, client verifies the server process, every request field validated; `--test-fan` requires administrator rights.
- Clients (window, tray, CLI) talk only to an elevated `DellFans.Service.exe` in their own folder; the controller protects `%ProgramData%\DellFans` (administrators only, no reparse points). Sign-in start never uses an elevated scheduled task.
- No model list: every computer runs read-only interface checks, then a consented 15 s full-speed response test the first time the window opens (or `--capability-probe`). Until it passes, DellFans never controls the fan; a failure shows "This computer is not supported" and writes an anonymous report to `%LocalAppData%\DellFans\reports`.

### Interface
- Borderless rounded main window and dialogs in three skins (white, black, purple-black); Simplified Chinese, Traditional Chinese and English; custom themes and language packs as data files.
- Export of the current data shows where the file was written.

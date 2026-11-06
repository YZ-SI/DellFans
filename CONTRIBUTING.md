# Contributing to DellFans

Thanks for helping. By contributing you agree that your work is licensed under MIT OR Apache-2.0, like the rest of the project
(see `LICENSE`).

## Build and test

Requirements: Windows x64, .NET Framework 4.8 (its `csc.exe` is used), Rust stable with the MSVC toolchain.

```powershell
powershell -File build.ps1              # C# programs into bin\, Rust tray and watchdog from rust\
powershell -File tests\run-all.ps1      # every offline test runner; must exit 0
powershell -File Package.ps1            # dist\DellFans-<version>-win-x64.zip (+ .sha256): the portable release
```

Rust unit tests: `cargo test --offline` in `rust\`. The tray memory check is `rust\tests\tray-measure.ps1` (needs `rust\build.ps1 -TestHooks`).

CI (`.github/workflows/ci.yml`) runs exactly the first three commands. **Hardware tests never run in CI.**

## Hardware-safety rules

This program writes to fan-control firmware (Dell SMM / BIOS). A mistake can leave a fan stopped or stuck.

* **Never run a test or command that writes to firmware unless you are sitting at the machine** and can power it off. That
  includes `DellFans.Service.exe --portable`, `--test-fan`, `--capability-probe`, `--rpm`, `--mode` (other than `auto`) and `--recover`.
  Do not start them from a script, a scheduled task or an unattended agent.
* Offline tests must stay offline: no background controller, scheduled tasks, `HKLM`, `Program Files` or `ProgramData`. Registry tests use an
  injected temporary `HKCU` subkey and delete it; pipe tests use private pipe names and fakes.
* Keep the safety paths intact: 90 C full speed (back to the mode rules at 85 C), the 8 s invalid-temperature handback to BIOS,
  the watchdog, and restoring BIOS auto on exit. Changes there need a test.
* **Changes to the capability check or fan control need real-hardware evidence**: model, BIOS, Windows build, the capability report JSON (it contains no
  serial numbers) and the read-only verification output (`--verify-sensors`). There is no model list; a computer is controlled only after the read-only checks and the consented full-speed test pass.
* Do not add telemetry, network listeners or background uploads.

## Pull requests

Keep changes focused, update `CHANGELOG.md` and the relevant `docs\` page, and note in the PR what you tested and what you could
not (for example "hardware not tested"). Third-party code or binaries need a license entry in `licenses\THIRD-PARTY-NOTICES.txt`.

Security issues: see `SECURITY.md`; do not file them as public issues.

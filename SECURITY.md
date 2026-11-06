# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub Security Advisories:
the repository's **Security** tab, then **Report a vulnerability**. Do not open a public issue or pull request for them.

Include the DellFans version, Windows build, steps to reproduce and the impact you see. You can expect an acknowledgement
within about 7 days; this is a volunteer project, so fix times vary with severity.

## Scope

In scope:

* **Control pipe** (`\\.\pipe\DellFans.Control.v2`): ACL, caller identification, request validation, anything that lets a
  non-administrator or a network logon change fan behavior beyond what the documented protocol allows.
* **Background controller start-up** (`DellFans.Service.exe --portable`): protection of `%ProgramData%\DellFans` (owner, ACL,
  junction or symbolic-link tricks) and the clients' check that the pipe server is the elevated `DellFans.Service.exe` next to them.
* **PawnIO download**: fetching the pinned `PawnIO_setup.exe`, its SHA-256 and signer (`CN=namazso.eu`) verification, and the
  consent and elevation flow around it. Bypasses of the hash or signature check are in scope.
* Controller, watchdog and recovery code that runs as administrator.

Out of scope: vulnerabilities in PawnIO itself (report them to its author), Windows or Dell BIOS/firmware issues, attacks that
already require administrator rights on the machine, and fan-control behavior on computers whose capability check fails (they stay on BIOS automatic).

## Supported versions

Only the latest release receives fixes.

## Residual trust in the extraction folder (portable package)

DellFans runs from the folder the user extracted the zip to, and `DellFans.Service.exe` runs elevated from there. Anything that can
write to that folder can replace the program before the next start, exactly as with any portable administrator tool. What is done
about it: elevation always goes through a UAC prompt the user answers (sign-in start uses the unelevated tray and a UAC prompt, never
an elevated scheduled task, so a replaced file cannot run elevated silently); the clients only talk to an *elevated* process with
that exact image path, which an unelevated program cannot create; the controller keeps its data in an administrators-only folder.
Extract the package to a folder only you can write (for example under your profile, not a shared or world-writable path).

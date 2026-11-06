# Dell interface compatibility and validation boundaries

DellFans has no model list. Every Dell computer goes through the same read-only checks and a consented full-speed test before the
fan is controlled (see [CAPABILITY.md](CAPABILITY.md)). This page records why that is required and what the checks do and do not prove.

## What differs between Dell computers

* Linux upstream (`dell-smm-hwmon`) maps some products to the automatic-control command pair `34A3`/`35A3` and others to `30A3`/`31A3`.
  Never try both pairs blindly on an unknown machine.
* Fan count, running-state limits, RPM range and thermal behavior differ by product and firmware. Shared branding does not prove a
  shared control contract.
* Server products (PowerEdge BMC/IPMI) do not use the client WMI/SMM interface.
* The numbers in this project (nominal 1000/4000 RPM, full speed around 5570 RPM) were measured on one test computer and are not
  universal Dell fan limits.

Sources: [Linux hwmon documentation](https://www.kernel.org/doc/html/latest/hwmon/dell-smm-hwmon.html) and the
[upstream driver](https://github.com/torvalds/linux/blob/master/drivers/hwmon/dell-smm-hwmon.c).

## Detection precedes control

1. Read manufacturer, product name and BIOS version.
2. Identify the Windows interfaces and validate the expected signature. Class presence alone is insufficient.
3. Read the fan RPM, the BIOS fan mode and the CPU temperature source. These checks write nothing.
4. Only after those pass, and only with the user's consent, run the full-speed response test: RPM must rise by at least 300 and
   BIOS automatic control must read back as restored.
5. The result is bound to the model and BIOS version; a BIOS update repeats the test.

The full-speed test uses a BIOS attribute write, not SMM write commands. Control afterwards uses running states 1 and 2 only; state 0
is never written. A passing test shows that the fan responds on this computer; it does not prove the thermal behavior of every
Dell product, so unsupported or failing computers stay on BIOS automatic and can submit the anonymous capability report.

## Persistence

A persistent BIOS full-speed override, a temporary SMM running state, an arbitrary minimum with firmware thermal acceleration, and
replaying a saved setting when a program starts are different outcomes. DellFans replays the saved mode when its background controller starts; it does
not keep an arbitrary minimum in firmware, so the fan returns to BIOS control when the controller is not running.

## Validation limits

Offline simulations can prove program decisions and failure handling. They cannot prove another computer's firmware behavior.

# HVMC Bootstrapper

`start.bat` is the Windows entrypoint for Hero's Vault MC.

It starts `HVBootstrapper.ps1` without leaving a visible console window. The bootstrapper checks the repository for newer `HV.mrpack` and `HV.lnk` files, caches them under `%LOCALAPPDATA%\Bendemen\HVMC`, and continues to launch the existing Hero's Vault shortcut when the network is unavailable.

The updater is intentionally non-fatal: an update-server outage must not prevent an already installed game from starting.

## Modrinth App

The bootstrapper does not depend on an undocumented Modrinth App command-line switch. The Modrinth App currently does not expose a supported CLI for directly launching a named client instance, so the existing `HV.lnk` remains the compatibility launch mechanism.

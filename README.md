# HVMC

HVMC is the school Minecraft launcher and account-pool client.

## Current launcher

- App name: **HVMC School Launcher**
- Executable: **HVMC.exe**
- Minecraft: 26.2
- Fabric Loader: 0.19.3
- Launcher version: 3.14
- Windows x64, self-contained single-file executable

The launcher keeps the existing PC authorization, heartbeat, account leasing, Minecraft/Fabric startup, fullscreen and self-update functionality.

HVMC content is synchronized by the bundled updater. The updater uses the local manifest for a fast path and performs full SHA verification when local content is incomplete or cannot be trusted.

The updater script is embedded in the launcher executable, so the installed launcher does not need to download executable updater code from the repository before every Minecraft start.

### Windows naming

The physical executable file is **HVMC.exe**, while the Windows application/product name is **HVMC School Launcher**.

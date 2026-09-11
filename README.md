# HVMC

HVMC is the school Minecraft launcher and account-pool client.

## Current launcher

- Minecraft: 1.21.11
- Fabric Loader: 0.19.2
- Launcher: 2.4.0
- Windows x64, self-contained single-file executable

The launcher keeps the existing PC authorization, heartbeat, account leasing, Minecraft/Fabric startup, fullscreen and self-update functionality.

HVMC content is synchronized by the bundled updater. The updater uses the local manifest for a fast path and performs full SHA verification when local content is incomplete or cannot be trusted.

The updater script is embedded in the launcher executable, so the installed launcher does not need to download executable updater code from the repository before every Minecraft start.

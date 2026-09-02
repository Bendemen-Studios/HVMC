# Hero's Vault MC Launcher

A school launcher for the kid-friendly Hero's Vault server. The project is intended for schools and care organisations.

## Windows launcher

Run `start.bat`. It starts the HVMC bootstrapper without a visible updater console.

The bootstrapper now treats `HV.mrpack` as the pack manifest and synchronizes the actual Minecraft instance incrementally:

- unchanged mods/files are kept;
- changed files are replaced after hash verification;
- new files are downloaded;
- files removed from the pack are removed from the previously managed local set;
- mrpack `overrides` and `client-overrides` are applied;
- the instance directories are created automatically;
- an existing installation can still start when GitHub is temporarily unavailable.

It does **not** repeatedly import the complete modpack into the Minecraft/Modrinth launcher.

## Where to upload mods and configs

Operator-managed files can be placed in these repository folders:

```text
content/
├─ mods/          # custom/privately distributed mod jars
├─ config/        # server/client configs
├─ resourcepacks/ # resource packs
├─ shaderpacks/   # shader packs
└─ datapacks/     # datapacks
```

The bootstrapper mirrors `content/` into the local Minecraft instance. Use `HV.mrpack` for normal Modrinth-pack dependencies because it already contains the official file URLs and hashes. Put a mod in `content/mods/` only when you have the right to redistribute that file.

## Local installation

The managed instance is stored under:

```text
%LOCALAPPDATA%\Bendemen\HVMC\instance
```

Bootstrapper state and logs are stored under `%LOCALAPPDATA%\Bendemen\HVMC`.

## Important launch note

The updater/synchronizer is independent from authentication. The current compatibility launch still uses `HV.lnk` after synchronization, so a Microsoft/Minecraft account session already configured on the machine can be reused. We deliberately do not depend on an undocumented Modrinth App command-line switch.

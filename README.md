# Hero's Vault MC Launcher

A school launcher for the kid-friendly Hero's Vault server. The project is intended for schools and care organisations.

## Windows launcher

Run `start.bat`. It starts the HVMC bootstrapper without a visible updater console.

The bootstrapper synchronizes the actual Minecraft installation incrementally:

- unchanged mods/files are kept;
- changed files are replaced after hash verification;
- new files are downloaded;
- files removed from the pack are removed from the previously managed local set;
- mrpack `overrides` and `client-overrides` are applied;
- Minecraft directories are created automatically;
- the official Minecraft Launcher is installed automatically when possible;
- Fabric Loader 0.18.1 for Minecraft 1.21.11 is installed automatically when missing;
- an existing installation can still be prepared when GitHub is temporarily unavailable.

The old `HV.lnk` / Modrinth App shortcut is no longer used.

## Official Minecraft Launcher

HVMC now targets the official Minecraft Launcher. Mojang provides the launcher from the official Minecraft downloads page. Fabric's official documentation supports installing Fabric into the official launcher and also documents command-line installation. citeturn0search10turn0search0turn0search5

The bootstrapper installs the launcher through WinGet when available, with the official Mojang MSI as a fallback. It then creates the Fabric 1.21.11 profile if it is missing and opens the official launcher.

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

The bootstrapper mirrors `content/` into the normal official Minecraft `.minecraft` directory. Use `HV.mrpack` for normal Modrinth-pack dependencies because it already contains the official file URLs and hashes. Put a mod in `content/mods/` only when you have the right to redistribute that file.

## Local installation

The official Minecraft instance is stored under:

```text
%APPDATA%\.minecraft
```

Bootstrapper state and logs are stored under:

```text
%LOCALAPPDATA%\Bendemen\HVMC
```

## VPS account pool

`pool.json.example` documents the optional central account-pool endpoint. A local `pool.json` can be placed under `%LOCALAPPDATA%\Bendemen\HVMC\pool.json` and enabled when the VPS service is ready.

The pool service is intended to lease accounts so two school PCs do not select the same account simultaneously. **Microsoft passwords/tokens must not be stored in GitHub or in the VPS pool database.** Microsoft authentication remains handled by the official Minecraft Launcher. Minecraft requires a Microsoft account for play. citeturn0search13

Important limitation: the official Minecraft Launcher does not expose a supported command-line interface for silently switching between already-authenticated Microsoft accounts. Therefore the VPS can decide which account/slot is reserved, but HVMC cannot safely inject credentials or force an account switch inside the official launcher. The launcher must use the selected signed-in account on the PC.

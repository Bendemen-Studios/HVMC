A School Launcher for the Kid Friendly server Hero's Vault
This is used as a Auto Updater for Schools and Care organisations

## Windows launcher

Run `start.bat`. It starts the HVMC bootstrapper without a visible command window. The bootstrapper checks for updated `HV.mrpack` and `HV.lnk` files and then launches Hero's Vault.

The update check is non-blocking: if GitHub or the network is unavailable, an existing installation can still be launched.

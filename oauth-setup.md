# HVMC Microsoft OAuth setup

HVMC uses Microsoft OAuth device-code authentication as a **public client**. No client secret belongs in the launcher or repository.

## 1. Create the Microsoft app registration

In Microsoft Entra admin center, create an App Registration for HVMC.

Use a supported desktop/public-client configuration and enable **Allow public client flows**. Microsoft documents device code flow for public clients and documents the Mobile and desktop applications platform for desktop apps.

Required values:

- Application (client) ID
- Public client flow enabled

Use the `common` authority so both personal Microsoft accounts and work/school accounts can be handled where permitted by the tenant/account.

## 2. Configure the client

On the Windows machine, create:

`%LOCALAPPDATA%\\Bendemen\\HVMC\\oauth.json`

Example:

```json
{
  "clientId": "YOUR-APPLICATION-CLIENT-ID",
  "authority": "https://login.microsoftonline.com/common",
  "scopes": [
    "openid",
    "profile",
    "offline_access",
    "https://graph.microsoft.com/User.Read"
  ]
}
```

`oauth.json` is intentionally local. Do not put refresh tokens, passwords, or other user credentials in GitHub.

## 3. First sign-in

Run `HVMCOAuth.ps1` once on the Windows machine. It uses Microsoft device-code flow and displays Microsoft's sign-in instructions. After successful sign-in, the refresh token is protected with Windows DPAPI for the current Windows user and the local account profile is written to the HVMC application data directory.

## 4. Account pool

The VPS at `https://accounts.hvmc.nl` should only manage leases/reservations. The client sends its authenticated Microsoft account identifier to the pool so the pool can reserve a slot without receiving a password.

The pool does not need to store Microsoft passwords.

## Important limitation

The Microsoft OAuth token issued to HVMC is **not** a direct Minecraft Launcher login token. The official Minecraft Launcher remains responsible for the Minecraft account session. HVMC OAuth is used for safe account identity/enrollment and pool management, not for bypassing the official launcher authentication.

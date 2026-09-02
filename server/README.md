# HVMC Account Pool Server

This service provides a central 5-account lease pool for HVMC. It verifies Microsoft OAuth ID tokens, assigns a stable account slot to each Microsoft identity, and exposes acquire/heartbeat/release operations.

It does **not** store Microsoft passwords or refresh tokens.

## VPS

Target hostname:

`https://accounts.hvmc.nl`

The service listens only on `127.0.0.1:8080`; Caddy terminates HTTPS and proxies to it.

## Install on Ubuntu 24.04

Clone the repository and run:

```bash
cd HVMC/server
chmod +x install-ubuntu.sh
./install-ubuntu.sh
```

Then edit:

`/opt/hvmc-account-pool/.env`

Set:

- `MICROSOFT_CLIENT_ID` to the client ID from the Microsoft public-client App Registration.
- `ADMIN_TOKEN` to a long random value.

Restart:

```bash
sudo systemctl restart hvmc-account-pool
```

## Caddy

Install Caddy on the VPS using the official Caddy Ubuntu instructions, then copy `Caddyfile` to `/etc/caddy/Caddyfile` and run:

```bash
sudo systemctl reload caddy
```

Caddy will obtain/renew the TLS certificate for `accounts.hvmc.nl` when DNS points to the VPS and ports 80/443 are reachable.

## Pool behavior

Five slots are created automatically:

- account-01
- account-02
- account-03
- account-04
- account-05

On first successful OAuth lease, a Microsoft identity is permanently associated with the first free slot. Later leases use that same slot for the same identity. Active leases block a slot until release or expiry.

## Important

The OAuth identity proves who the HVMC client authenticated as. It is not a Minecraft Launcher credential. The official Minecraft Launcher remains responsible for the actual Minecraft account session.

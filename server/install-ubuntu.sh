#!/usr/bin/env bash
set -euo pipefail

APP_DIR=/opt/hvmc-account-pool
REPO_URL=${REPO_URL:-https://github.com/Bendemen-Studios/HVMC.git}

sudo apt-get update
sudo apt-get install -y ca-certificates curl git ufw

if ! command -v node >/dev/null 2>&1; then
  curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
  sudo apt-get install -y nodejs
fi

sudo mkdir -p "$APP_DIR/data"
sudo chown -R "$USER:$USER" "$APP_DIR"

if [ -z "${MICROSOFT_CLIENT_ID:-}" ]; then
  echo "MICROSOFT_CLIENT_ID is required. Export it before running this installer."
  exit 1
fi

if [ -z "${ADMIN_PASSWORD_HASH:-}" ]; then
  if [ ! -t 0 ]; then
    echo "ADMIN_PASSWORD_HASH is required for non-interactive installation."
    echo "Generate it first and export ADMIN_PASSWORD_HASH, then rerun the installer."
    exit 1
  fi
  read -r -s -p "HVMC admin wachtwoord: " ADMIN_PASSWORD
  echo
  if [ -z "$ADMIN_PASSWORD" ]; then
    echo "Admin wachtwoord mag niet leeg zijn."
    exit 1
  fi
  ADMIN_PASSWORD_HASH=$(printf '%s' "$ADMIN_PASSWORD" | node -e '
const crypto = require("node:crypto");
const password = require("fs").readFileSync(0, "utf8");
const N = 16384, r = 8, p = 1;
const salt = crypto.randomBytes(16);
const hash = crypto.scryptSync(password, salt, 64, { N, r, p, maxmem: 64 * 1024 * 1024 });
process.stdout.write(["scrypt", N, r, p, salt.toString("base64"), hash.toString("base64")].join("$"));
')
  unset ADMIN_PASSWORD
fi

ADMIN_TOKEN="${ADMIN_TOKEN:-$(node -e 'console.log(require("node:crypto").randomBytes(32).toString("base64url"))')}"
POOL_ENCRYPTION_KEY="${POOL_ENCRYPTION_KEY:-$(node -e 'console.log(require("node:crypto").randomBytes(32).toString("base64"))')}"

tmpdir=$(mktemp -d)
git clone --depth 1 "$REPO_URL" "$tmpdir/repo"
rm -rf "$APP_DIR/app"
mkdir -p "$APP_DIR/app"
cp -a "$tmpdir/repo/server/." "$APP_DIR/app/"
rm -rf "$tmpdir"

cd "$APP_DIR/app"
npm install --omit=dev

cat > "$APP_DIR/.env" <<EOF
PORT=8080
DB_PATH=/opt/hvmc-account-pool/data/hvmc-pool.db
MICROSOFT_CLIENT_ID=$MICROSOFT_CLIENT_ID
ADMIN_TOKEN=$ADMIN_TOKEN
ADMIN_USERNAME=${ADMIN_USERNAME:-bendemen}
ADMIN_PASSWORD_HASH=$ADMIN_PASSWORD_HASH
POOL_ENCRYPTION_KEY=$POOL_ENCRYPTION_KEY
LEASE_SECONDS=${LEASE_SECONDS:-60}
EMAIL_FROM=${EMAIL_FROM:-automail@hvmc.nl}
EMAIL_SMTP_HOST=${EMAIL_SMTP_HOST:-}
EMAIL_SMTP_PORT=${EMAIL_SMTP_PORT:-587}
EMAIL_SMTP_SECURE=${EMAIL_SMTP_SECURE:-false}
EMAIL_SMTP_USER=${EMAIL_SMTP_USER:-}
EMAIL_SMTP_PASSWORD=${EMAIL_SMTP_PASSWORD:-}
EOF
chmod 600 "$APP_DIR/.env"

sudo cp "$APP_DIR/app/hvmc-account-pool.service" /etc/systemd/system/hvmc-account-pool.service

sudo systemctl daemon-reload
sudo systemctl enable --now hvmc-account-pool

sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw --force enable

echo 'HVMC Account Pool installed. Edit /opt/hvmc-account-pool/.env before using the API if you have not already done so.'

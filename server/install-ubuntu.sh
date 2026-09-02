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

tmpdir=$(mktemp -d)
git clone --depth 1 "$REPO_URL" "$tmpdir/repo"
rm -rf "$APP_DIR/app"
mkdir -p "$APP_DIR/app"
cp -a "$tmpdir/repo/server/." "$APP_DIR/app/"
rm -rf "$tmpdir"

cd "$APP_DIR/app"
npm install --omit=dev

cat > "$APP_DIR/.env" <<'EOF'
PORT=8080
DB_PATH=/opt/hvmc-account-pool/data/hvmc-pool.db
MICROSOFT_CLIENT_ID=PASTE-YOUR-MICROSOFT-APP-CLIENT-ID-HERE
ADMIN_TOKEN=PASTE-A-LONG-RANDOM-ADMIN-TOKEN-HERE
LEASE_SECONDS=3600
EOF
chmod 600 "$APP_DIR/.env"

sudo cp "$APP_DIR/app/hvmc-account-pool.service" /etc/systemd/system/hvmc-account-pool.service
sudo sed -i "s#ExecStart=/usr/bin/node /opt/hvmc-account-pool/server.js#ExecStart=/usr/bin/node /opt/hvmc-account-pool/app/server.js#" /etc/systemd/system/hvmc-account-pool.service

sudo systemctl daemon-reload
sudo systemctl enable --now hvmc-account-pool

sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw --force enable

echo 'HVMC Account Pool installed. Edit /opt/hvmc-account-pool/.env before using the API if you have not already done so.'

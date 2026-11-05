#!/usr/bin/env bash
# Upgrades this installation to the newest release (or ./upgrade.sh 1.4.0 for a specific one). The database migration
# runs to completion before the new web app starts. Back up first.
set -euo pipefail
cd "$(dirname "$0")"
ver="${1:-latest}"
if [[ "$ver" == latest ]]; then base="https://github.com/finepointcgi/workbench/releases/latest/download"
else ver="${ver#v}"; base="https://github.com/finepointcgi/workbench/releases/download/v$ver"; fi
for f in docker-compose.yml Caddyfile upgrade.sh first-login-link.sh; do curl -fsSL -o "$f.new" "$base/$f" && mv "$f.new" "$f"; done
chmod +x upgrade.sh first-login-link.sh
sed -i.bak "s/^WORKBENCH_VERSION=.*/WORKBENCH_VERSION=$ver/" .env && rm -f .env.bak
docker compose pull
docker compose up -d --wait
echo "Workbench is now on $ver."

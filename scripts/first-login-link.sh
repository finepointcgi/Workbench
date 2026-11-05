#!/usr/bin/env bash
# Prints a link that lets the administrator choose their first password (valid for 60 minutes). Run it again for a fresh
# one. scripts/install.sh calls it for you. Needs the installation's .env (made by install.sh) in the repository folder.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ -f .env ]] || { echo "No .env here: run scripts/install.sh first." >&2; exit 1; }
set -a; . ./.env; set +a
base="https://${WORKBENCH_DOMAIN}"; [[ "${HTTPS_PORT:-443}" != "443" ]] && base="$base:${HTTPS_PORT}"

jar="$(mktemp)"; trap 'rm -f "$jar"' EXIT
page="$(curl -ksf -c "$jar" "$base/account/forgot-password")"
token="$(grep -oE 'name="__RequestVerificationToken"[^>]*value="[^"]+"' <<<"$page" | sed -E 's/.*value="([^"]+)"/\1/' | head -1)"
[[ -n "$token" ]] || { echo "Could not reach $base. Is Workbench running? (docker compose -f docker-compose.prod.yml ps)" >&2; exit 1; }
curl -ks -b "$jar" -o /dev/null --data-urlencode "__RequestVerificationToken=$token" \
  --data-urlencode "email=${WORKBENCH_BOOTSTRAP_ADMIN}" --data-urlencode "returnUrl=/" "$base/account/forgot-password"
sleep 2

# Email isn't set up on a new install, so the message is saved on the server; read the link out of it.
link="$(scripts/show-mail.sh | grep -oE 'https://[^ ]+/account/reset-password\?token=[A-Za-z0-9_-]+' | head -1)"
[[ -n "$link" ]] || { echo "No link was saved. If email is already set up, use \"Forgot your password?\" on the sign-in page." >&2; exit 1; }
# The saved link names the site without a port; point it at the address you reach it on.
echo "${link/https:\/\/${WORKBENCH_DOMAIN}\//$base/}"

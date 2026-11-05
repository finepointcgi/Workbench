#!/usr/bin/env bash
# Prints a link that lets the administrator choose a password (valid 60 minutes). Run again for a fresh one.
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env; set +a
base="https://${WORKBENCH_DOMAIN}${PUBLIC_PORT_SUFFIX:-}"
jar="$(mktemp)"; trap 'rm -f "$jar"' EXIT
page="$(curl -ksf -c "$jar" "$base/account/forgot-password")"
token="$(grep -oE 'name="__RequestVerificationToken"[^>]*value="[^"]+"' <<<"$page" | sed -E 's/.*value="([^"]+)"/\1/' | head -1)"
[[ -n "$token" ]] || { echo "Could not reach $base. Is it running? (docker compose ps)" >&2; exit 1; }
curl -ks -b "$jar" -o /dev/null --data-urlencode "__RequestVerificationToken=$token" \
  --data-urlencode "email=${WORKBENCH_BOOTSTRAP_ADMIN}" --data-urlencode "returnUrl=/" "$base/account/forgot-password"
sleep 2
# Until email is set up the message is saved on the server; read the link out of it.
body="$(docker run --rm -v workbench_mail:/mail:ro caddy:2.10 sh -c 'cat "$(ls -t /mail/* | head -1)"' | tr -d '\r')"
text="$(awk '/^Content-Transfer-Encoding: base64/ && !s {s=1; next} s==1 && /^$/ {s=2; next} s==2 && /^--/ {exit} s==2 {print}' <<<"$body" | openssl base64 -d)"
link="$(grep -oE 'https://[^ ]+/account/reset-password\?token=[A-Za-z0-9_-]+' <<<"$text" | head -1)"
[[ -n "$link" ]] || { echo "No link was saved. If email is set up, use \"Forgot your password?\" on the sign-in page." >&2; exit 1; }
echo "${link/https:\/\/${WORKBENCH_DOMAIN}\//$base/}"

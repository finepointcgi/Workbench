#!/usr/bin/env bash
# Installs Workbench on this machine with Docker. Needs only Docker and openssl; no source code, no build.
#
#   curl -fsSL https://github.com/finepointcgi/workbench/releases/latest/download/install.sh | bash -s -- \
#       --domain workbench.example.com --admin you@example.com
#
# Trial on this machine only:  ... --domain localhost --admin you@example.com
# Installs into ./workbench (change with --dir). Options: --http-port N, --https-port N.
set -euo pipefail

VERSION="__VERSION__"        # filled in when the release is made
REPO="finepointcgi/workbench"
dir="workbench" domain="" admin="" http_port=80 https_port=443
while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain) domain="$2"; shift 2 ;;
    --admin) admin="$2"; shift 2 ;;
    --dir) dir="$2"; shift 2 ;;
    --http-port) http_port="$2"; shift 2 ;;
    --https-port) https_port="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$domain" && -n "$admin" ]] || { echo "Usage: install.sh --domain <name> --admin <email> [--dir D] [--http-port N --https-port N]" >&2; exit 2; }
command -v docker >/dev/null || { echo "Docker is required: https://docs.docker.com/get-docker/" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "Docker Compose v2 is required (the 'docker compose' command)." >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl is required." >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }

[[ -e "$dir/.env" ]] && { echo "$dir/.env exists, so this looks installed. To upgrade: cd $dir && ./upgrade.sh" >&2; exit 1; }
mkdir -p "$dir/secrets" && cd "$dir"

if [[ "$VERSION" == "__VERSION__" ]]; then base="https://github.com/$REPO/releases/latest/download"; VERSION=latest
else base="https://github.com/$REPO/releases/download/v$VERSION"; fi
echo "Downloading Workbench $VERSION files..."
for f in docker-compose.yml Caddyfile upgrade.sh first-login-link.sh; do curl -fsSL -o "$f" "$base/$f"; done
chmod +x upgrade.sh first-login-link.sh

# A real domain gets a Let's Encrypt certificate (ports 80 and 443 must be open to the internet);
# "localhost" gets a local test certificate your browser will warn about.
if [[ "$domain" == "localhost" ]]; then tls="internal"; else tls="$admin"; fi
suffix=""; [[ "$https_port" != "443" ]] && suffix=":$https_port"

rand() { openssl rand -base64 48 | tr -d '/+=\n' | cut -c1-"$1"; }
dp_password="$(rand 32)"
# Encrypts the keys Workbench uses to protect sensitive data (bank accounts, tax IDs). Back up ./secrets.
openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -subj "/CN=Workbench Data Protection" \
  -addext "keyUsage=keyEncipherment,dataEncipherment" -keyout secrets/dp.key -out secrets/dp.crt 2>/dev/null
openssl pkcs12 -export -inkey secrets/dp.key -in secrets/dp.crt -out secrets/dataprotection.pfx -passout "pass:$dp_password" \
  -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256
rm -f secrets/dp.key secrets/dp.crt
chmod 644 secrets/dataprotection.pfx; chmod 700 secrets

umask 077
cat > .env <<ENV
WORKBENCH_VERSION=$VERSION
WORKBENCH_DOMAIN=$domain
WORKBENCH_TLS=$tls
WORKBENCH_BOOTSTRAP_ADMIN=$admin
HTTP_PORT=$http_port
HTTPS_PORT=$https_port
PUBLIC_PORT_SUFFIX=$suffix
POSTGRES_PASSWORD=$(rand 32)
DATAPROTECTION_PASSWORD=$dp_password
SCIM_TOKEN=$(rand 48)
# Email: invites and password resets are only saved on this machine until you set this up.
EMAIL_MODE=Pickup
EMAIL_FROM=workbench@$domain
# SMTP_HOST=
# SMTP_PORT=587
# Single sign-on (optional): OIDC_AUTHORITY=, OIDC_CLIENT_ID=, OIDC_CLIENT_SECRET=
ENV

echo "Starting Workbench (the first start downloads the images)..."
docker compose up -d --wait

link="$(./first-login-link.sh 2>/dev/null || true)"
url="https://$domain$suffix"
cat <<MSG

Workbench is running at $url

Next:
  1. Choose your password with this link (works once, for 60 minutes), then you are signed in:

       ${link:-$url/account/forgot-password   (enter $admin; the link is saved on this machine until email is set up)}

     Need a new link later:  cd $dir && ./first-login-link.sh
  2. Follow the setup checklist on the home page.
  3. Set up email so invites are sent: https://github.com/$REPO/blob/master/docs/operations/email.md
  4. Back up the $dir/secrets folder and the postgres-data volume. They hold your data and the keys that protect it.

Upgrade later:  cd $dir && ./upgrade.sh
MSG

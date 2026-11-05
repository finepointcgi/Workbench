#!/usr/bin/env bash
# One-command install of Workbench on a single Linux or macOS machine with Docker: PostgreSQL, the web host and a proxy
# that gets an HTTPS certificate for you. People sign in with built-in email-and-password accounts; add single sign-on
# later under Setup (a Business feature).
#
#   scripts/install.sh --domain workbench.example.com --admin you@example.com
#
# For a trial on this machine only:  scripts/install.sh --domain localhost --admin you@example.com
# Details, upgrades, backups and every setting: docs/operations/README.md
set -euo pipefail
cd "$(dirname "$0")/.."

domain="" admin="" http_port=80 https_port=443
while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain) domain="$2"; shift 2 ;;
    --admin) admin="$2"; shift 2 ;;
    --http-port) http_port="$2"; shift 2 ;;
    --https-port) https_port="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$domain" && -n "$admin" ]] || { echo "Usage: scripts/install.sh --domain <name> --admin <email> [--http-port N --https-port N]" >&2; exit 2; }
command -v docker >/dev/null || { echo "Docker is required: https://docs.docker.com/get-docker/" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "Docker Compose v2 is required (the 'docker compose' command)." >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl is required." >&2; exit 1; }

if [[ -e .env ]]; then
  echo ".env already exists, so this looks installed already. To upgrade run: scripts/upgrade.sh" >&2
  exit 1
fi

# A real domain gets a public certificate (Let's Encrypt, needs ports 80 and 443 open to the internet);
# "localhost" gets a local test certificate your browser will warn about.
if [[ "$domain" == "localhost" ]]; then tls="internal"; else tls="$admin"; fi

echo "Creating secrets..."
scripts/prod/init-secrets.sh secrets >/dev/null

umask 077
cat > .env <<ENV
WORKBENCH_DOMAIN=$domain
WORKBENCH_TLS=$tls
WORKBENCH_BOOTSTRAP_ADMIN=$admin
HTTP_PORT=$http_port
HTTPS_PORT=$https_port
EMAIL_MODE=Pickup
ENV

echo "Building and starting Workbench (the first build takes several minutes)..."
docker compose -f docker-compose.prod.yml up -d --build

echo "Waiting for it to come up..."
base="https://$domain"; [[ "$https_port" != "443" ]] && base="$base:$https_port"
for _ in $(seq 1 60); do
  if curl -ksf -o /dev/null "$base/health/live" 2>/dev/null || curl -ks -o /dev/null -w '%{http_code}' "$base/" 2>/dev/null | grep -qE '^(200|302)$'; then ready=1; break; fi
  sleep 5
done
[[ "${ready:-}" == 1 ]] || { echo "It did not answer in time. See: docker compose -f docker-compose.prod.yml logs web" >&2; exit 1; }

link="$(scripts/first-login-link.sh 2>/dev/null || true)"

cat <<MSG

Workbench is running at $base

Next:
  1. Choose your password with this link (it works once, for 60 minutes), then you are signed in:

       ${link:-$base/account/forgot-password   (enter $admin; then run scripts/show-mail.sh to read the link)}

     Need a new link later: scripts/first-login-link.sh
  2. Follow the setup checklist on the home page.
  3. Set up email so invites and password resets are sent: docs/operations/email.md
     Until then they are saved on this machine; read the latest with scripts/show-mail.sh
  4. Keep secrets/ safe and back it up. It holds the keys that protect your data: docs/operations/data-protection-keys.md
  5. Set up nightly backups: docs/operations/backup-restore.md

Documentation: docs/operations/README.md
MSG

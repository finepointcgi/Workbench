#!/usr/bin/env bash
# Creates ./secrets for docker-compose.prod.yml: random database password and SCIM token, a self-signed certificate
# that encrypts the Data Protection key ring, and a gpg key pair for backups. Run once per environment; never commit
# the folder (it is git-ignored). For a real deployment put these in your vault and keep the backup private key and the
# Data Protection certificate offline/escrowed (docs/operations/data-protection-keys.md, backup-restore.md).
#
#   scripts/prod/init-secrets.sh [secrets-dir]
set -euo pipefail

dir="${1:-secrets}"
[[ -e "$dir/ConnectionStrings__Workbench" ]] && { echo "$dir already has secrets; not overwriting." >&2; exit 1; }
mkdir -p "$dir"
chmod 700 "$dir"
# Files are readable inside the containers (which run as other users); the 0700 folder protects them on the host.
umask 022

rand() { openssl rand -base64 48 | tr -d '/+=\n' | cut -c1-"$1"; }

pg_password="$(rand 32)"
printf '%s' "$pg_password" > "$dir/postgres_password"
printf 'Host=postgres;Port=5432;Database=workbench;Username=workbench;Password=%s;Maximum Pool Size=50' "$pg_password" > "$dir/ConnectionStrings__Workbench"
(umask 077; printf 'postgres:5432:*:workbench:%s\n' "$pg_password" > "$dir/pgpass")
rand 48 > "$dir/Scim__BearerToken"
printf 'replace-with-the-client-secret-from-your-identity-provider' > "$dir/Authentication__Oidc__ClientSecret"

# Data Protection key-encryption certificate: RSA 3072, 5 years (rotate before expiry; old keys stay readable while
# the old certificate is listed under DataProtection:PreviousCertificates).
dp_password="$(rand 32)"
printf '%s' "$dp_password" > "$dir/DataProtection__CertificatePassword"
# A short path: gpg-agent's socket lives inside GNUPGHOME and fails when the path is long (macOS temp folders are).
tmp="$(mktemp -d /tmp/wbsec.XXXXXX)"
trap 'rm -rf "$tmp"' EXIT
openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -subj "/CN=Workbench Data Protection" \
  -addext "keyUsage=keyEncipherment,dataEncipherment" -keyout "$tmp/dp.key" -out "$tmp/dp.crt" 2>/dev/null
openssl pkcs12 -export -inkey "$tmp/dp.key" -in "$tmp/dp.crt" -out "$dir/dataprotection.pfx" -passout "pass:$dp_password" \
  -keypbe AES-256-CBC -certpbe AES-256-CBC -macalg sha256
chmod 644 "$dir/dataprotection.pfx"

# Backup encryption key pair. The public key stays with the backup job; the private key belongs in the vault.
# Run gpg from the backup image rather than the host's, so a missing or misconfigured gpg-agent can't stop the install.
# The key ring lives inside the container (gpg-agent's socket can't be created on a folder shared from macOS); only the
# two exported key files come out.
docker build -q -t workbench-backup:local "$(dirname "$0")/../../deploy/backup" >/dev/null
mkdir -p "$tmp/out" && chmod 777 "$tmp/out"
docker run --rm -v "$tmp/out:/out" workbench-backup:local sh -c '
  set -e
  export GNUPGHOME=/tmp/gnupg; mkdir -m 700 "$GNUPGHOME"
  gpg --batch --pinentry-mode loopback --passphrase "" --quick-gen-key "Workbench backups <backups@workbench.invalid>" rsa4096 encr never 2>/dev/null
  gpg --batch --armor --export backups@workbench.invalid > /out/public.asc
  gpg --batch --pinentry-mode loopback --passphrase "" --armor --export-secret-keys backups@workbench.invalid > /out/private.asc'
cp "$tmp/out/public.asc" "$dir/backup_public_key.asc"
(umask 077; cp "$tmp/out/private.asc" "$dir/backup_private_key.asc")
[[ -s "$dir/backup_public_key.asc" && -s "$dir/backup_private_key.asc" ]] || { echo "Could not create the backup key pair." >&2; exit 1; }

cat <<MSG
Secrets written to $dir.
Next:
  1. Put your identity provider's client secret in $dir/Authentication__Oidc__ClientSecret and set OIDC_AUTHORITY in .env.
  2. Move $dir/backup_private_key.asc into your vault (it decrypts backups; the server must not keep it).
  3. Escrow $dir/dataprotection.pfx and its password: without them encrypted data (vendor TINs, bank accounts) is lost.
MSG

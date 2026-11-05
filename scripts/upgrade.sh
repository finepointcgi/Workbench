#!/usr/bin/env bash
# Upgrades an installation made with scripts/install.sh: pull the new code, rebuild, and restart. The database migration
# runs to completion before the new web host starts. Take a backup first: docs/operations/backup-restore.md
set -euo pipefail
cd "$(dirname "$0")/.."
git pull --ff-only
docker compose -f docker-compose.prod.yml up -d --build

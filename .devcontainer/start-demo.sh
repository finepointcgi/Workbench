#!/usr/bin/env bash
# Starts Workbench in the codespace with the seeded demo data (SQLite, created on first start) on port 5104.
# Runs in the background so it survives the terminal; log: /tmp/workbench-demo.log.
#   bash .devcontainer/start-demo.sh            start (or restart)
#   bash .devcontainer/start-demo.sh --reset    start over with fresh demo data
set -euo pipefail
cd "$(dirname "$0")/.."

pkill -f "Workbench.Web" 2>/dev/null || true
if [[ "${1:-}" == "--reset" ]]; then
  rm -f src/Workbench.Web/workbench.db src/Workbench.Web/workbench.db-shm src/Workbench.Web/workbench.db-wal
  rm -rf src/Workbench.Web/App_Data
fi

# Development enables the sign-in page that lists the demo users (no passwords). Demo data only: anyone with the
# public link can sign in as any of them, including the administrator.
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://0.0.0.0:5104
export Email__Mode=Pickup
export Email__PickupFolder=/tmp/workbench-mail

setsid nohup dotnet run --project src/Workbench.Web -c Release --no-launch-profile > /tmp/workbench-demo.log 2>&1 < /dev/null &

for _ in $(seq 1 120); do
  if curl -s -o /dev/null -w "%{http_code}" http://localhost:5104/account/dev-signin | grep -q 200; then
    echo "Workbench is running on port 5104."
    # Share it: make the forwarded port public. Works when the codespace's token allows it; otherwise use the Ports tab.
    if [[ -n "${CODESPACE_NAME:-}" ]]; then
      gh codespace ports visibility 5104:public -c "$CODESPACE_NAME" >/dev/null 2>&1 \
        && echo "Port 5104 is public: https://${CODESPACE_NAME}-5104.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-app.github.dev}/" \
        || echo "Make port 5104 Public in the Ports tab to share https://${CODESPACE_NAME}-5104.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-app.github.dev}/"
    fi
    exit 0
  fi
  sleep 3
done
echo "Workbench didn't start; see /tmp/workbench-demo.log" >&2
tail -40 /tmp/workbench-demo.log >&2
exit 1

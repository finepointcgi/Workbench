#!/usr/bin/env bash
# After a deploy: the site answers over HTTPS, readiness reports the database (and catalog) healthy, and the sign-in
# page is served with HSTS. Retries for up to two minutes while the new version starts.
#   deploy/smoke-test.sh https://workbench.example.com
set -euo pipefail
base="${1:?base URL}"
for attempt in $(seq 1 24); do
  if ready="$(curl -fsS --max-time 10 "$base/health/ready")" && jq -e '.checks.database == "Healthy" and .checks.catalog == "Healthy"' <<<"$ready" > /dev/null; then
    break
  fi
  [[ $attempt -eq 24 ]] && { echo "Not ready: ${ready:-no answer}" >&2; exit 1; }
  sleep 5
done
echo "Ready: $ready"
[[ "$(jq -r .status <<<"$ready")" == "Healthy" ]] || echo "::warning::Readiness is $(jq -r .status <<<"$ready") (check email)."
curl -fsSI --max-time 10 "$base/account/signin" | grep -qi '^strict-transport-security:' || { echo "No HSTS header on the sign-in page" >&2; exit 1; }
echo "Smoke test passed for $base"

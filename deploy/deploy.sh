#!/usr/bin/env bash
# Deploys IMAGE (a digest reference) to one environment: runs the migrate job to completion, then rolls out the web
# host. Called by .github/workflows/deploy.yml; runnable by hand with the same variables. DEPLOY_TARGET picks how:
#
#   compose-ssh  DEPLOY_SSH_HOST (user@host), DEPLOY_SSH_KEY, DEPLOY_SSH_KNOWN_HOSTS; the host has the repository's
#                docker-compose.prod.yml, .env and secrets/ in ~/workbench (docs/operations/deployment.md).
#   kubernetes   KUBE_CONFIG (kubeconfig contents), KUBE_NAMESPACE; expects a Deployment "workbench-web" and runs a
#                Job "workbench-migrate-<sha>" from the same image with the same env/secrets (envFrom workbench-config,
#                workbench-secrets).
#
# Add other platforms (Azure Container Apps jobs, ECS run-task) as new cases: migrate first, wait, then roll out.
set -euo pipefail
: "${IMAGE:?IMAGE (registry/name@sha256:...) is required}"
: "${DEPLOY_TARGET:?Set the DEPLOY_TARGET variable on the GitHub environment (compose-ssh or kubernetes)}"

case "$DEPLOY_TARGET" in
  compose-ssh)
    key="$(mktemp)"; known="$(mktemp)"
    trap 'rm -f "$key" "$known"' EXIT
    printf '%s\n' "$DEPLOY_SSH_KEY" > "$key"; chmod 600 "$key"
    printf '%s\n' "$DEPLOY_SSH_KNOWN_HOSTS" > "$known"
    ssh -i "$key" -o UserKnownHostsFile="$known" -o StrictHostKeyChecking=yes "$DEPLOY_SSH_HOST" \
      "cd ~/workbench && export WORKBENCH_IMAGE='$IMAGE' \
        && docker compose -f docker-compose.prod.yml pull web migrate \
        && docker compose -f docker-compose.prod.yml run --rm --no-deps migrate \
        && docker compose -f docker-compose.prod.yml up -d --no-build --wait web proxy"
    ;;
  kubernetes)
    KUBECONFIG="$(mktemp)"
    export KUBECONFIG
    trap 'rm -f "$KUBECONFIG"' EXIT
    printf '%s\n' "$KUBE_CONFIG" > "$KUBECONFIG"
    ns="${KUBE_NAMESPACE:-workbench}"
    job="workbench-migrate-$(date -u +%Y%m%d%H%M%S)"
    kubectl -n "$ns" create job "$job" --image="$IMAGE" -- dotnet /app/Workbench.Web.dll --migrate --dry-run=client -o yaml \
      | kubectl -n "$ns" patch --local -f - --type=json -o yaml -p '[
          {"op":"add","path":"/spec/backoffLimit","value":0},
          {"op":"add","path":"/spec/template/spec/containers/0/envFrom","value":[{"configMapRef":{"name":"workbench-config"}},{"secretRef":{"name":"workbench-secrets"}}]}]' \
      | kubectl -n "$ns" apply -f -
    kubectl -n "$ns" wait --for=condition=complete --timeout=15m "job/$job"
    kubectl -n "$ns" set image deployment/workbench-web web="$IMAGE"
    kubectl -n "$ns" rollout status deployment/workbench-web --timeout=10m
    ;;
  *)
    echo "Unknown DEPLOY_TARGET '$DEPLOY_TARGET'." >&2
    exit 2
    ;;
esac
echo "Deployed $IMAGE ($DEPLOY_TARGET)."

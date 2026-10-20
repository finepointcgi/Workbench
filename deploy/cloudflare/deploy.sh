#!/bin/sh
# Deploys the Worker and the demo container from this machine; wrangler builds the image (needs Docker). Run from anywhere.
set -eu
cd "$(dirname "$0")"
[ -d node_modules ] || npm install
npx wrangler deploy "$@"

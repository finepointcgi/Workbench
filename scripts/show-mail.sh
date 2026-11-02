#!/usr/bin/env bash
# Prints the newest message Workbench saved to its mail folder, decoded. Until email is set up (docs/operations/email.md)
# this is how you read your first password-reset link.
set -euo pipefail
mail="$(docker run --rm -v workbench_mail:/mail:ro caddy:2.10 sh -c 'f=$(ls -t /mail/* 2>/dev/null | head -1); [ -n "$f" ] && cat "$f"' | tr -d '\r')"
[[ -n "$mail" ]] || { echo "No saved mail yet."; exit 0; }
grep -E '^(To|Subject):' <<<"$mail"
echo
# The first text part is base64: the lines after its blank line, up to the next boundary.
awk '/^Content-Transfer-Encoding: base64/ && !s {s=1; next} s==1 && /^$/ {s=2; next} s==2 && /^--/ {exit} s==2 {print}' <<<"$mail" | openssl base64 -d

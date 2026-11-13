#!/usr/bin/env bash
# Extracts the public declarations of the legacy credential-based SDK and compares them with the
# golden snapshot. Any removed or changed public member fails the check. Additions are reported
# but allowed. Run from the package root.
set -euo pipefail
cd "$(dirname "$0")/../.."
extract() {
  grep -hE '^\s*public ' Jiwe/JiweAuth.cs Jiwe/JiweWallet.cs \
    | sed -E 's/[[:space:]]+/ /g; s/ +$//' \
    | sed -E 's/\{.*$//; s/=.*$//; s/ +$//' \
    | sort
}
extract > "${TMPDIR:-/tmp}/legacy-api-current.txt"
if [ "${1:-}" = "--update" ]; then
  cp "${TMPDIR:-/tmp}/legacy-api-current.txt" Tests~/compat/legacy-public-api.golden
  echo "Golden snapshot updated."; exit 0
fi
# Lines present in golden but missing now = breaking removal or change.
removed=$(comm -23 <(sort Tests~/compat/legacy-public-api.golden) "${TMPDIR:-/tmp}/legacy-api-current.txt" || true)
added=$(comm -13 <(sort Tests~/compat/legacy-public-api.golden) "${TMPDIR:-/tmp}/legacy-api-current.txt" || true)
if [ -n "$added" ]; then echo "Added legacy public members (allowed):"; echo "$added"; fi
if [ -n "$removed" ]; then echo "BREAKING: legacy public members removed or changed:"; echo "$removed"; exit 1; fi
echo "Legacy public API: all golden members present."

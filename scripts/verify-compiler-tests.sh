#!/usr/bin/env bash
# Canonical compiler behavior gate. Builds the current local SDK, runs every categorized NUnit suite,
# and formally verifies each DotKt-emitted assembly through tests/run-ilverify.sh.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "${1:-}" in
  -h|--help)
    echo "usage: $(basename "$0")"
    echo "Builds the local SDK, runs all NUnit compiler suites, and applies the ILVerify baseline."
    exit 0
    ;;
  "") ;;
  *) echo "$(basename "$0"): unknown argument '$1'" >&2; exit 2 ;;
esac

make -C "$ROOT" pack
exec bash "$ROOT/tests/run-nunit-tests.sh"

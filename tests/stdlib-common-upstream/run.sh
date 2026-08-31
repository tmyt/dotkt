#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "$0")/../.." && pwd -P)"
COMMON="$ROOT/libraries/stdlib/common"
EXPECTED_VERSION="2.4.10"
MANIFEST="$ROOT/tests/stdlib-common-upstream/upstream-v${EXPECTED_VERSION}.sha256"
CONFIGURED_VERSION="$(sed -n 's:.*<DotKtKotlinVersion>\([^<]*\)</DotKtKotlinVersion>.*:\1:p' "$ROOT/packaging/DotKt.Versions.props")"

[[ "$CONFIGURED_VERSION" == "$EXPECTED_VERSION" ]] || {
	echo "stdlib-common-upstream: Kotlin version is $CONFIGURED_VERSION, but the checked snapshot is $EXPECTED_VERSION"
	exit 1
}

actual_paths="$(cd "$COMMON" && find . -type f -print | LC_ALL=C sort)"
expected_paths="$(sed -n 's/^[0-9a-f]\{64\}  //p' "$MANIFEST" | LC_ALL=C sort)"
[[ "$actual_paths" == "$expected_paths" ]] || {
	echo "stdlib-common-upstream: common source membership differs from upstream Kotlin v$EXPECTED_VERSION"
	comm -3 <(printf '%s\n' "$expected_paths") <(printf '%s\n' "$actual_paths")
	exit 1
}

(cd "$COMMON" && sha256sum --check --strict "$MANIFEST")
echo "stdlib-common-upstream: GREEN (Kotlin v$EXPECTED_VERSION)"

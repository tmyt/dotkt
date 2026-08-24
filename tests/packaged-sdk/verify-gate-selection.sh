#!/usr/bin/env bash
# Keep the change-aware selector honest about the expensive packaged-SDK gate. A broad FULL plan is
# deliberately compiler-only; release/package inputs must add packagedsdk without making every broad
# fallback pay for package creation and isolated restore.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "$0")/../.." && pwd -P)"
source "$ROOT/scripts/lib.sh"

FULL_SUITES="compiler_tests schema sanity msbuild targetuniverse"

assert_suites() { # <fixture-name> <changed-path> <expected suites>
	local name="$1" path="$2" expected="$3" output actual
	output="$(bash "$ROOT/scripts/gate.sh" --dry-run "$path")"
	actual="$(sed -n 's/^suites to run: //p' <<<"$output")"
	[[ "$actual" == "$expected" ]] || die "$name: expected suites '$expected', got '$actual'"
}

# Mechanical release/version bumps and package layout changes must exercise the nupkgs that consumers restore.
assert_suites version-bump global.json "$FULL_SUITES packagedsdk"
assert_suites package-version packaging/DotKt.Versions.props "$FULL_SUITES packagedsdk"
assert_suites nested-package-input packaging/DotKt.Sdk/Sdk/Sdk.targets "$FULL_SUITES packagedsdk"
assert_suites package-assembly scripts/pack-nuget.sh "$FULL_SUITES packagedsdk"

# Conservative fallback remains the compiler FULL set; it must not silently become the release gate.
assert_suites unrelated-broad-change build-logic/unknown.input "$FULL_SUITES"
assert_suites compiler-full toolchain/bir-common/TypeNode.cs "$FULL_SUITES"

echo "gate-selection: GREEN"

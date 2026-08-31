#!/usr/bin/env bash
# Self-test the change-aware selector with its public dry-run surface. The FULL compiler set is derived
# from gate.sh itself; these fixtures pin only when the separate packaged-SDK release gate is added.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "$0")/../.." && pwd -P)"
source "$ROOT/scripts/lib.sh"

suite_line() { # [gate.sh arguments...]
	bash "$ROOT/scripts/gate.sh" --dry-run "$@" | sed -n 's/^suites to run: //p'
}

FULL_SUITES="$(suite_line --full)"
[[ -n "$FULL_SUITES" && "$FULL_SUITES" != '(none)' ]] || die "could not obtain the FULL suite set"
[[ " $FULL_SUITES " != *' packagedsdk '* ]] || die "ordinary --full unexpectedly includes packagedsdk"

assert_suites() { # <fixture-name> <changed-path> <expected suites>
	local name="$1" path="$2" expected="$3" actual
	actual="$(suite_line "$path")"
	[[ "$actual" == "$expected" ]] || die "$name: expected suites '$expected', got '$actual'"
}

# Mechanical release/version bumps and package layout changes must exercise the nupkgs that consumers restore.
assert_suites version-bump global.json "$FULL_SUITES packagedsdk"
assert_suites package-version packaging/DotKt.Versions.props "$FULL_SUITES packagedsdk"
assert_suites nested-package-input packaging/DotKt.Sdk/Sdk/Sdk.targets "$FULL_SUITES packagedsdk"
assert_suites package-assembly scripts/pack-nuget.sh "$FULL_SUITES packagedsdk"

# Documentation consumed or guarded by package assembly needs the package gate, but not compiler FULL.
assert_suites readme-version-guard README.md packagedsdk
assert_suites getting-started-version-guard docs/user/getting-started.md packagedsdk
assert_suites packaged-readme packaging/DotKt.README.md packagedsdk
assert_suites packaged-notices THIRD-PARTY-NOTICES.md packagedsdk

# Conservative fallback remains the compiler FULL set; it must not silently become the release gate.
assert_suites unrelated-broad-change build-logic/unknown.input "$FULL_SUITES"
assert_suites compiler-full toolchain/bir-common/TypeNode.cs "$FULL_SUITES"
assert_suites stdlib-source libraries/stdlib/common/src/generated/_Arrays.kt "$FULL_SUITES"
assert_suites stdlib-snapshot-test tests/stdlib-common-upstream/upstream-v2.4.10.sha256 stdlib_upstream

# Exercise the default Git collector, not only explicit path classification. With rename folding enabled,
# Git reports only docs/moved.props and loses the removed packaging path, incorrectly selecting no gate.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/scripts" "$work/packaging" "$work/docs"
cp "$ROOT/scripts/gate.sh" "$ROOT/scripts/lib.sh" "$work/scripts/"
cp "$ROOT/packaging/DotKt.Versions.props" "$work/packaging/moved.props"
git -C "$work" init -q -b main
git -C "$work" add .
git -C "$work" -c user.name=gate-selection -c user.email=gate-selection.invalid \
	-c commit.gpgsign=false commit -qm baseline
git -C "$work" switch -qc topic
git -C "$work" mv packaging/moved.props docs/moved.props
git -C "$work" -c user.name=gate-selection -c user.email=gate-selection.invalid \
	-c commit.gpgsign=false commit -qam rename
rename_suites="$(cd "$work" && bash scripts/gate.sh --dry-run | sed -n 's/^suites to run: //p')"
[[ "$rename_suites" == "$FULL_SUITES packagedsdk" ]] ||
	die "rename-out: expected suites '$FULL_SUITES packagedsdk', got '$rename_suites'"

echo "gate-selection: GREEN"

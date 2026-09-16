#!/usr/bin/env bash
# Self-test the selector's public dry-run surface and canonical Make/CI gate composition.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "$0")/../.." && pwd -P)"
source "$ROOT/scripts/lib.sh"

suite_line() { # [gate.sh arguments...]
	bash "$ROOT/scripts/gate.sh" --dry-run "$@" | sed -n 's/^suites to run: //p'
}

FULL_SUITES="$(suite_line --full)"
[[ -n "$FULL_SUITES" && "$FULL_SUITES" != '(none)' ]] || die "could not obtain the FULL suite set"
[[ " $FULL_SUITES " != *' packagedsdk '* ]] || die "ordinary --full unexpectedly includes packagedsdk"
expected_full="compiler_tests schema sanity lowering stdlib_upstream msbuild targetuniverse csharp14 pinvoke dll2klib xfail gate_selection"
[[ "$FULL_SUITES" == "$expected_full" ]] || die "FULL must cover every canonical core gate once: got '$FULL_SUITES'"

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
assert_suites ilverify-harness tests/ilverify/test_harness.py compiler_tests
verifier_suites="compiler_tests csharp14 pinvoke dll2klib packagedsdk"
assert_suites shared-ilverify tests/run-ilverify.sh "$verifier_suites"
mixed_suites="$(suite_line tests/run-ilverify.sh tests/basic/fixtures/SomeTest.kt tests/packaged-sdk/run.sh)"
[[ "$mixed_suites" == "$verifier_suites" ]] || die "overlapping verifier consumers were duplicated: '$mixed_suites'"
assert_suites dll2klib-test tests/special/dll2klib-e2e/run.sh "$FULL_SUITES"

# Inspect real Make composition without running tool builds or tests. The standalone E2E target
# belongs to integration, so both local canonical entry points and that CI shard must reach it once.
# The other CI shards must not execute a second copy.
for target in verify verify-core verify-integration verify-test-corpus verify-compile-fail verify-lowering verify-packaged-sdk; do
	plan="$(make --no-print-directory -n -C "$ROOT" -o toolchain -o stdlib -o pack -o bir2cir -o stdlib-ref "$target")"
	count="$(grep -Fxc 'bash tests/special/dll2klib-e2e/run.sh' <<<"$plan" || true)"
	case "$target" in verify|verify-core|verify-integration) expected_count=1 ;; *) expected_count=0 ;; esac
	[[ "$count" == "$expected_count" ]] || die "$target: expected $expected_count DLL-to-KLIB E2E invocation(s), got $count"
done

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

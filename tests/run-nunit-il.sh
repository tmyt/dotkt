#!/usr/bin/env bash
# NUnit IL-battery gate driver — the production replacement lane for the migrated cases/il-* families
# (docs/design-nunit-test-harness.md; playbook docs/nunit-migration-playbook.md).
#
# It drives the tests/il project against the LOCALLY-BUILT DotKt SDK (build/nuget-feed via tests/nuget.config,
# design D4) and enforces the audit's machine-readable governance:
#
#   TASK 1 (local SDK): resolves DotKt.Sdk from the local feed, not a published nuget — so the suite tests the
#     compiler in THIS working tree. Requires `make pack` (build/nuget-feed) first.
#   TASK 2 (discovered-count guard): asserts `dotnet test` DISCOVERED exactly the expected number of test
#     methods per project (from the TRX <Counters total=...>), so a silently dropped fixture/method — or a
#     total discovery failure (0 tests, e.g. a TypeLoadException breaking fixture load) — REDDENS the gate
#     instead of passing quietly. This is the governance the cases-test-design audit (#8/#14) demands.
#
# Order (design §5): recreate DotKt.* in the isolated cache -> build -> ilverify (--no-build) -> dotnet test
# --no-build with the count assertion. Green (exit 0) iff every project builds, every test passes, the
# discovered count matches EXPECTED, and ilverify is clean against tests/run-ilverify.sh's baseline.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FEED="$ROOT/build/nuget-feed"
CACHE="$ROOT/build/test-package-cache"

# --- expected discovered-test count per battery project (the single machine-readable manifest; each family
#     migration bumps its project's number in the SAME change, so a dropped method is a red gate). ------------
declare -A EXPECTED=(
	["tests/il"]=103  # Generics 6 + Inline 17 + Collections 16 + Maps 10 + Strings 22 + Nullable 12 + Float 7 + Enum 5 + Exception 8
)

# Read the packed SDK version from the single source of truth so a version bump needs no edit here.
VER_PREFIX="$(grep -oE '<DotKtVersionPrefix>[^<]+' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VER_SUFFIX="$(grep -oE '<DotKtVersionSuffix>[^<]*' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VER="$VER_PREFIX${VER_SUFFIX:+-$VER_SUFFIX}"

[[ -d "$FEED" ]] || { echo "run-nunit-il: local feed $FEED missing — run 'make pack' first"; exit 1; }
ls "$FEED"/DotKt.Sdk.*.nupkg >/dev/null 2>&1 || { echo "run-nunit-il: no DotKt.Sdk nupkg in $FEED — run 'make pack'"; exit 1; }
echo "run-nunit-il: local SDK version $VER  (feed: $FEED)"

# Recreate the extracted DotKt.* packages in the ISOLATED cache so a repacked SAME-version SDK always wins
# (NuGet prefers an already-extracted exact-version package; design D4 caveat). NUnit/test-sdk stay cached.
if [[ -d "$CACHE" ]]; then
	find "$CACHE" -maxdepth 1 -type d -iname 'dotkt.*' -exec rm -rf {} + 2>/dev/null || true
fi

rc=0
declare -a EMITTED=()
for proj in "${!EXPECTED[@]}"; do
	dir="$ROOT/$proj"
	want="${EXPECTED[$proj]}"
	name="$(basename "$dir")"
	echo "=========================================================="
	echo "IL battery: $proj  (expect $want discovered tests)"

	# Build (restore from the local feed via tests/nuget.config). A build failure is a red gate.
	if ! dotnet build "$dir" -v q --nologo >"$ROOT/build/nunit-$name.build.log" 2>&1; then
		echo "  BUILD FAIL — see build/nunit-$name.build.log"; tail -25 "$ROOT/build/nunit-$name.build.log"; rc=1; continue
	fi
	# The emitted assembly is named after the .ktproj (e.g. DotKt.Tests.Il.ktproj -> DotKt.Tests.Il.dll).
	proj_file="$(find "$dir" -maxdepth 1 -name '*.ktproj' | head -1)"
	asm="$(basename "$proj_file" .ktproj).dll"
	emitted="$(find "$dir/bin" -name "$asm" 2>/dev/null | head -1)"
	[[ -f "$emitted" ]] && EMITTED+=("$emitted")

	# Run the tests, capturing a TRX for the machine-readable discovered-count assertion.
	trxdir="$dir/TestResults"; rm -rf "$trxdir"
	dotnet test "$dir" --no-build --logger "trx;LogFileName=results.trx" -v q --nologo \
		>"$ROOT/build/nunit-$name.test.log" 2>&1 || true
	trx="$(find "$trxdir" -name '*.trx' 2>/dev/null | head -1)"
	if [[ ! -f "$trx" ]]; then
		echo "  DISCOVERY FAIL — no TRX produced (0 tests / host crash); see build/nunit-$name.test.log"; rc=1; continue
	fi

	# TRX: <Counters total="N" executed="N" passed="N" failed="0" .../>
	counters="$(grep -oE '<Counters[^/]*/>' "$trx" | head -1)"
	get() { grep -oE "$1=\"[0-9]+\"" <<<"$counters" | grep -oE '[0-9]+' | head -1; }
	total="$(get total)"; passed="$(get passed)"; failed="$(get failed)"
	total="${total:-0}"; passed="${passed:-0}"; failed="${failed:-0}"

	echo "  discovered=$total  passed=$passed  failed=$failed"
	if (( total != want )); then
		echo "  COUNT GUARD FAIL — discovered $total, expected $want (a fixture/method was added or dropped without updating EXPECTED)"; rc=1
	fi
	if (( failed != 0 || passed != total )); then
		echo "  TEST FAIL — $failed failed / $passed of $total passed; see build/nunit-$name.test.log"; rc=1
	fi
	if (( total == want && failed == 0 && passed == total )); then
		echo "  OK — $total/$want tests green"
	fi
done

# ilverify once per emitted battery assembly (design §5 order; baseline in tests/run-ilverify.sh).
if (( ${#EMITTED[@]} )); then
	echo "=========================================================="
	echo "ilverify (once per emitted battery assembly)"
	bash "$ROOT/tests/run-ilverify.sh" "${EMITTED[@]}" || rc=1
fi

echo "=========================================================="
(( rc == 0 )) && echo "run-nunit-il: GREEN" || echo "run-nunit-il: RED"
exit $rc

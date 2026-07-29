#!/usr/bin/env bash
# NUnit gate driver for the Basic, Interop, Coroutines, and Roundtrip suites.
# See docs/design-nunit-test-harness.md.
#
# It drives the test projects against the LOCALLY-BUILT DotKt SDK (build/nuget-feed via tests/nuget.config,
# design D4) and enforces the audit's machine-readable governance:
#
#   TASK 1 (local SDK): resolves DotKt.Sdk from the local feed, not a published nuget — so the suite tests the
#     compiler in THIS working tree. Requires `make pack` (build/nuget-feed) first.
#   TASK 2 (verdict = dotnet test $? + nonzero discovery + ilverify): the gate reddens iff a project fails to
#     build, `dotnet test` returns non-zero, the TRX is missing or reports zero discovered tests, or ILVerify finds
#     an issue. There is no hand-maintained exact-count manifest, but zero is explicitly rejected: VSTest can exit
#     zero after matching an empty test assembly when an incremental build has gone stale.
#
# Order (design §5): recreate DotKt.* in the isolated cache -> non-incremental build -> dotnet test --no-build ->
# ilverify. The non-incremental build is load-bearing after repacking the SDK at the same version: otherwise MSBuild
# can retain a tiny placeholder assembly even though the DotKt compiler inputs are present. Green (exit 0) iff
# every project builds, discovers at least one test, every `dotnet test` exits 0, and ILVerify is clean
# against tests/run-ilverify.sh's baseline.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FEED="$ROOT/build/nuget-feed"
CACHE="$ROOT/build/test-package-cache"

# --- the suite projects to build / test / ilverify. Verdict is per-project `dotnet test` $? + ilverify;
#     there is no hand-maintained discovered-count manifest (see TASK 2). --------------------------------------
PROJECTS=(
	"tests/basic"
	# Coroutine / suspend lane (docs/design-nunit-test-harness.md §4 "coroutine (+ the one shared harness)"): its
	# OWN suite project (subject-split: basic · interop · coroutines · roundtrip) for the cold-core state-machine
	# family. tests/support/coroutines provides the shared dotkt.support.blockOn drive imported by the fixtures.
	"tests/coroutines"
	# ProjectReference round-trip consolidation lane (docs/design-nunit-test-harness.md §3; playbook §3): a producer
	# DotKt LIBRARY (tests/roundtrip/producer) consumed via <ProjectReference> as its BUILT dll (dll2klib re-import,
	# NOT source) by this NUnit consumer. (nothing stays in the shell lane due its formal IL gap; generic-hof and
	# receiver-lambda are green after low-arity delegate ABI unification but have not yet been migrated.)
	"tests/roundtrip/consumer"
	# Bidirectional ProjectReference: Kotlin consumes C#, then a C# NUnit project consumes the emitted Kotlin library
	# at compile time. This also supersedes the former reflection-only reverse-interop case.
	"tests/roundtrip/bidirectional/consumer"
	# C#-PRODUCER interop lane: the CLR-interop cases
	# that shipped a `runtime.cs` (a separately-compiled C# assembly the DotKt code references — cross-module BY
	# CONSTRUCTION) become a PLAIN C# producer csproj (tests/interop/producer) <ProjectReference>'d by this NUnit
	# consumer, which imports the built C# dll through its generated reference KLIB (`import <Ns>.<Type>`).
	"tests/interop/consumer"
)

# Extra DotKt-emitted assemblies (beyond the .ktproj-named one) to ALSO run ilverify over, per consumer project.
# Space-separated list per project. The round-trip consumer's two <ProjectReference>s copy BOTH producer dlls into
# its bin (the single-platform RoundtripProducer + the MPP RoundtripProducerMpp); verify all (§5 order).
declare -A EXTRA_EMIT=(
	["tests/coroutines"]="DotKt.Tests.CoroutineSupport.dll"
	["tests/roundtrip/consumer"]="RoundtripProducer.dll RoundtripProducerMpp.dll"
	# The C#-producer dll is csc-emitted (not ilemit), so it needs no DotKt ilverify of its own; ilverify over the
	# CONSUMER assembly (the .ktproj-named dll) is what proves the emitted interop IL is clean. No EXTRA_EMIT entry
	# for tests/interop/consumer — adding the plain C# producer would only formally re-verify a non-DotKt assembly.
)

# Read the packed SDK version from the single source of truth so a version bump needs no edit here.
VER_PREFIX="$(grep -oE '<DotKtVersionPrefix>[^<]+' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VER_SUFFIX="$(grep -oE '<DotKtVersionSuffix>[^<]*' "$ROOT/packaging/DotKt.Versions.props" | sed 's/.*>//')"
VER="$VER_PREFIX${VER_SUFFIX:+-$VER_SUFFIX}"

[[ -d "$FEED" ]] || { echo "run-nunit-tests: local feed $FEED missing — run 'make pack' first"; exit 1; }
ls "$FEED"/DotKt.Sdk.*.nupkg >/dev/null 2>&1 || { echo "run-nunit-tests: no DotKt.Sdk nupkg in $FEED — run 'make pack'"; exit 1; }
echo "run-nunit-tests: local SDK version $VER  (feed: $FEED)"

# Build the tiny local package used by PackageReferenceTests. It deliberately reaches the Kotlin consumer as a
# NuGet package (not a ProjectReference), replacing the former Avalonia-specific surrogate with the exact contract
# under test: a virtual member from a PackageReference can be overridden by Kotlin.
dotnet pack "$ROOT/tests/interop/package-producer/PackageInterop.csproj" -o "$FEED" -v q --nologo >/dev/null

# Recreate the extracted DotKt.* packages in the ISOLATED cache so a repacked SAME-version SDK always wins
# (NuGet prefers an already-extracted exact-version package; design D4 caveat). NUnit/test-sdk stay cached.
if [[ -d "$CACHE" ]]; then
	find "$CACHE" -maxdepth 1 -type d -iname 'dotkt.*' -exec rm -rf {} + 2>/dev/null || true
	find "$CACHE" -maxdepth 1 -type d -iname 'dotkt.tests.virtualpackage' -exec rm -rf {} + 2>/dev/null || true
fi

rc=0
declare -a EMITTED=()
for proj in "${PROJECTS[@]}"; do
	dir="$ROOT/$proj"
	# Several projects are named `consumer`; derive log names from the full suite path so their diagnostics never
	# overwrite one another (roundtrip-consumer, roundtrip-bidirectional-consumer, interop-consumer).
	name="${proj//\//-}"
	echo "=========================================================="
	echo "NUnit suite: $proj"

	# Build (restore from the local feed via tests/nuget.config). A build failure is a red gate.
	if ! dotnet build "$dir" --no-incremental -m:1 -v q --nologo >"$ROOT/build/nunit-$name.build.log" 2>&1; then
		echo "  BUILD FAIL — see build/nunit-$name.build.log"; tail -25 "$ROOT/build/nunit-$name.build.log"; rc=1; continue
	fi
	# The emitted assembly is named after the .ktproj (e.g. DotKt.Tests.Basic.ktproj -> DotKt.Tests.Basic.dll).
	proj_file="$(find "$dir" -maxdepth 1 \( -name '*.ktproj' -o -name '*.csproj' \) | head -1)"
	proj_base="$(basename "$proj_file")"
	asm="${proj_base%.*}.dll"
	emitted="$(find "$dir/bin" -name "$asm" 2>/dev/null | head -1)"
	[[ "$proj_file" == *.ktproj && -f "$emitted" ]] && EMITTED+=("$emitted")
	# Also collect any declared EXTRA_EMIT assembly (e.g. a ProjectReference'd producer dll copied into bin).
	if [[ -v EXTRA_EMIT[$proj] ]]; then
		for extra_name in ${EXTRA_EMIT[$proj]}; do
			extra_emitted="$(find "$dir/bin" -name "$extra_name" 2>/dev/null | head -1)"
			[[ -f "$extra_emitted" ]] && EMITTED+=("$extra_emitted")
		done
	fi

	# Run the tests. HONOR dotnet test's EXIT STATUS as the verdict: it returns non-zero on ANY test failure,
	# a discovery/adapter error, or a host crash — so $? is authoritative (no hand-maintained discovered-count).
	# A TRX is still emitted for the informational discovered= line and the no-TRX host-crash guard.
	trxdir="$dir/TestResults"; rm -rf "$trxdir"
	if dotnet test "$dir" --no-build --logger "trx;LogFileName=results.trx" -v q --nologo \
		>"$ROOT/build/nunit-$name.test.log" 2>&1; then test_ok=1; else test_ok=0; fi
	trx="$(find "$trxdir" -name '*.trx' 2>/dev/null | head -1)"
	if [[ ! -f "$trx" ]]; then
		echo "  DISCOVERY FAIL — no TRX produced (0 tests / host crash); see build/nunit-$name.test.log"; rc=1; continue
	fi
	total="$(grep -oE 'total="[0-9]+"' "$trx" | grep -oE '[0-9]+' | head -1)"; total="${total:-0}"
	if (( test_ok == 1 && total > 0 )); then
		echo "  discovered=$total  OK — dotnet test green"
	elif (( test_ok == 1 )); then
		echo "  DISCOVERY FAIL — TRX reports 0 tests; see build/nunit-$name.test.log"; rc=1
	else
		echo "  discovered=$total  TEST FAIL — dotnet test returned non-zero; see build/nunit-$name.test.log"; rc=1
	fi
done

# ilverify once per emitted suite assembly (design §5 order; baseline in tests/run-ilverify.sh).
if (( ${#EMITTED[@]} )); then
	echo "=========================================================="
	echo "ilverify (once per emitted suite assembly)"
	bash "$ROOT/tests/run-ilverify.sh" "${EMITTED[@]}" || rc=1
fi

echo "=========================================================="
(( rc == 0 )) && echo "run-nunit-tests: GREEN" || echo "run-nunit-tests: RED"
exit $rc

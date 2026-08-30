#!/usr/bin/env bash
# NUnit gate driver for the Basic, Interop, Coroutines, and Roundtrip suites.
# See docs/design-nunit-test-harness.md.
#
# It drives the test projects against the LOCALLY-BUILT DotKt SDK (build/nuget-feed via tests/nuget.config,
# design D4) and enforces the audit's machine-readable governance:
#
#   TASK 1 (local SDK): resolves DotKt.Sdk from the local feed, not a published nuget — so the suite tests the
#     compiler in THIS working tree. Requires `make pack` (build/nuget-feed) first.
#   TASK 2 (verdict = dotnet test $? + exact discovery + ilverify): the gate reddens iff a project fails to
#     build, `dotnet test` returns non-zero, the TRX discovery count differs from the reviewed baseline, or ILVerify
#     finds an issue. Exact counts make test additions and removals visible in the same review as their rationale;
#     they also catch VSTest exiting zero after matching an empty or stale test assembly.
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
CONFIGURATION="Debug"
METADATA_INSPECTOR_PROJECT="$ROOT/tests/roundtrip/metadata-inspector/CompanionMetadataInspector.csproj"
METADATA_INSPECTOR_DLL="$ROOT/tests/roundtrip/metadata-inspector/bin/$CONFIGURATION/net10.0/CompanionMetadataInspector.dll"

# --- the suite projects to build / test / ilverify. Verdict is per-project `dotnet test` $? + exact discovery
#     count + ilverify (see TASK 2). ---------------------------------------------------------------------------
PROJECTS=(
	"tests/basic"
	# Coroutine / suspend lane (docs/design-nunit-test-harness.md §4 "coroutine (+ the one shared harness)"): its
	# OWN suite project (subject-split: basic · interop · coroutines · roundtrip) for the cold-core state-machine
	# family. tests/support/coroutines provides the shared dotkt.support.blockOn drive imported by the fixtures.
	"tests/coroutines"
	# ProjectReference round-trip lane (docs/design-nunit-test-harness.md §3; playbook §3): a producer DotKt library
	# is consumed via <ProjectReference> as its BUILT dll (dll2klib re-import, NOT source) by this NUnit consumer.
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

# Reviewed on the v0.9.8 main baseline at the start of #227. Updating a suite requires updating this number in
# the same change, making otherwise-silent test proliferation or accidental deletion an explicit review event.
declare -A EXPECTED_DISCOVERED=(
	["tests/basic"]=478
	["tests/coroutines"]=198
	["tests/roundtrip/consumer"]=87
	["tests/roundtrip/bidirectional/consumer"]=9
	["tests/interop/consumer"]=172
)

# Validate the baseline map before doing any expensive work. A new/renamed suite without a reviewed count is a
# harness configuration error, not an observed count change.
for proj in "${PROJECTS[@]}"; do
	if [[ ! -v EXPECTED_DISCOVERED[$proj] ]]; then
		echo "run-nunit-tests: HARNESS ERROR — no EXPECTED_DISCOVERED baseline for $proj"
		exit 1
	fi
done

# Extra DotKt-emitted assemblies (beyond the .ktproj-named one) to ALSO run ilverify over, per consumer project.
# Space-separated list per project. The round-trip consumer's two <ProjectReference>s copy BOTH producer dlls into
# its bin (the single-platform RoundtripProducer + the MPP RoundtripProducerMpp); verify all (§5 order).
declare -A EXTRA_EMIT=(
	["tests/coroutines"]="DotKt.Tests.CoroutineSupport.dll"
	["tests/roundtrip/consumer"]="RoundtripProducer.dll RoundtripProducerMpp.dll RoundtripDefaultPropertySlot.dll"
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

# Build the shared metadata inspector once. Individual artifact checks execute this exact binary so they neither
# repeat MSBuild/restore work nor depend on the order in which the NUnit projects happen to run.
if ! dotnet build "$METADATA_INSPECTOR_PROJECT" -c "$CONFIGURATION" --no-incremental -v:q --nologo \
	>"$ROOT/build/nunit-metadata-inspector.build.log" 2>&1; then
	echo "run-nunit-tests: METADATA INSPECTOR BUILD FAIL — see build/nunit-metadata-inspector.build.log"
	tail -25 "$ROOT/build/nunit-metadata-inspector.build.log"
	exit 1
fi

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
	if ! dotnet build "$dir" -c "$CONFIGURATION" --no-incremental -m:1 -v q --nologo >"$ROOT/build/nunit-$name.build.log" 2>&1; then
		echo "  BUILD FAIL — see build/nunit-$name.build.log"; tail -25 "$ROOT/build/nunit-$name.build.log"; rc=1; continue
	fi
	if [[ "$proj" == "tests/basic" ]]; then
		star_copy_cir="$dir/obj/$CONFIGURATION/net10.0/cir/DefaultArgumentTests.cir.json"
		if python3 "$ROOT/tests/basic/assert-star-copy-default-fields-cir.py" "$star_copy_cir" \
			>"$ROOT/build/nunit-$name.star-copy-default-fields.log" 2>&1; then
			echo "  star-projected copy defaults use existential getter slots"
		else
			echo "  STAR COPY DEFAULT FIELD BINDING FAIL — see build/nunit-$name.star-copy-default-fields.log"
			tail -25 "$ROOT/build/nunit-$name.star-copy-default-fields.log"; rc=1
		fi
	fi
	# The companion round-trip fixture has two independent metadata contracts in addition to execution: the producer
	# DLL must carry an explicit trusted owner/name/representation record, and dll2klib must wire that record into the
	# standard KLIB companion_object_name + nested class graph. Inspect both artifacts so a mutually-compatible
	# producer/consumer bug cannot make the behavioral test pass while either interchange boundary is malformed.
	if [[ "$proj" == "tests/roundtrip/consumer" ]]; then
		producer_dll="$ROOT/tests/roundtrip/producer/bin/$CONFIGURATION/net10.0/RoundtripProducer.dll"
		producer_klib="$dir/obj/$CONFIGURATION/net10.0/klib/RoundtripProducer.klib"
		producer_bir="$ROOT/tests/roundtrip/producer/obj/$CONFIGURATION/net10.0/bir/DispatchAndCompanion.bir.json"
		producer_cir="$ROOT/tests/roundtrip/producer/obj/$CONFIGURATION/net10.0/cir/DispatchAndCompanion.cir.json"
		ownership_bir="$ROOT/tests/roundtrip/producer/obj/$CONFIGURATION/net10.0/bir/NestedOwnership.bir.json"
		ownership_cir="$ROOT/tests/roundtrip/producer/obj/$CONFIGURATION/net10.0/cir/NestedOwnership.cir.json"
		consumer_dll="$dir/bin/$CONFIGURATION/net10.0/RoundtripConsumer.Tests.dll"
		if bash "$ROOT/tests/roundtrip/run-flags-lookalike-negative.sh" \
			>"$ROOT/build/nunit-$name.flags-lookalike.log" 2>&1; then
			echo "  same-FQN System.Enum/FlagsAttribute lookalikes do not project flags operations"
		else
			echo "  FLAGS LOOKALIKE NEGATIVE FAIL — see build/nunit-$name.flags-lookalike.log"
			tail -25 "$ROOT/build/nunit-$name.flags-lookalike.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			"$producer_dll" "$producer_klib" "$producer_bir" "$producer_cir" "$ownership_bir" "$ownership_cir" "$consumer_dll" \
			>"$ROOT/build/nunit-$name.metadata.log" 2>&1; then
			echo "  companion + nested-ownership BIR/CIR/DLL/KLIB metadata OK"
		else
			echo "  COMPANION METADATA FAIL — see build/nunit-$name.metadata.log"
			tail -25 "$ROOT/build/nunit-$name.metadata.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-package-properties "$producer_klib" "roundtrip.reifiednullability" \
			"contextPropertyMatches,ordinaryPropertyValue,propertyMatches,reifiedPropertyValue,secondPropertyMatches" \
			>"$ROOT/build/nunit-$name.reified-property-surface.log" 2>&1; then
			echo "  generic accessors remain package properties after DLL-to-KLIB projection"
		else
			echo "  GENERIC PROPERTY KLIB SHAPE FAIL — see build/nunit-$name.reified-property-surface.log"
			tail -25 "$ROOT/build/nunit-$name.reified-property-surface.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-package-nullable-method-bound "$producer_klib" "starprojection" \
			"referencedNullableMethodBound" \
			>"$ROOT/build/nunit-$name.nullable-method-bound.log" 2>&1; then
			echo "  nullable method type-parameter bound survives DLL-to-KLIB projection"
		else
			echo "  NULLABLE METHOD BOUND KLIB SHAPE FAIL — see build/nunit-$name.nullable-method-bound.log"
			tail -25 "$ROOT/build/nunit-$name.nullable-method-bound.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-package-nullable-method-bound "$producer_klib" "starprojection" \
			"referencedNullableSuspendMethodBound" \
			>"$ROOT/build/nunit-$name.nullable-suspend-method-bound.log" 2>&1; then
			echo "  nullable suspend method type-parameter bound survives DLL-to-KLIB projection"
		else
			echo "  NULLABLE SUSPEND METHOD BOUND KLIB SHAPE FAIL — see build/nunit-$name.nullable-suspend-method-bound.log"
			tail -25 "$ROOT/build/nunit-$name.nullable-suspend-method-bound.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-class-fake-override-nullable-method-bound "$producer_klib" \
			"starprojection.ReferencedNullableMethodImplementation" \
			"inheritedNullableMethodBound" \
			>"$ROOT/build/nunit-$name.nullable-inherited-method-bound.log" 2>&1; then
			echo "  inherited fake override preserves nullable owner-dependent method bound"
		else
			echo "  NULLABLE INHERITED METHOD BOUND KLIB SHAPE FAIL — see build/nunit-$name.nullable-inherited-method-bound.log"
			tail -25 "$ROOT/build/nunit-$name.nullable-inherited-method-bound.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-class-properties "$producer_klib" \
			"roundtrip.memberextensionsurface.GenericMemberPropertyHost" \
			"memberMatches,ordinaryMemberValue,ordinaryMemberCount" \
			>"$ROOT/build/nunit-$name.member-extension-property-surface.log" 2>&1; then
			echo "  generic member-extension accessors remain class properties after DLL-to-KLIB projection"
		else
			echo "  GENERIC MEMBER-EXTENSION PROPERTY KLIB SHAPE FAIL — see build/nunit-$name.member-extension-property-surface.log"
			tail -25 "$ROOT/build/nunit-$name.member-extension-property-surface.log"; rc=1
		fi
		if bash "$ROOT/tests/roundtrip/run-companion-metadata-negative-tests.sh" \
			>"$ROOT/build/nunit-$name.metadata-negative.log" 2>&1; then
			echo "  malformed trusted metadata carriers rejected by dll2klib + bir2cir OK"
		else
			echo "  COMPANION METADATA NEGATIVE FAIL — see build/nunit-$name.metadata-negative.log"
			tail -25 "$ROOT/build/nunit-$name.metadata-negative.log"; rc=1
		fi
		if bash "$ROOT/tests/roundtrip/run-declaration-identity-negative.sh" \
			>"$ROOT/build/nunit-$name.declaration-identity-negative.log" 2>&1; then
			echo "  mismatched cross-module declaration identity rejected with call-site context"
		else
			echo "  DECLARATION IDENTITY NEGATIVE FAIL — see build/nunit-$name.declaration-identity-negative.log"
			tail -25 "$ROOT/build/nunit-$name.declaration-identity-negative.log"; rc=1
		fi
	fi
	if [[ "$proj" == "tests/coroutines" ]]; then
		coroutine_cir="$dir/obj/$CONFIGURATION/net10.0/cir/SuspendDispatchTests.cir.json"
		if python3 "$ROOT/tests/coroutines/assert-suspend-super-cir.py" "$coroutine_cir" \
			>"$ROOT/build/nunit-$name.suspend-super-cir.log" 2>&1; then
			echo "  suspend super-call cold-entry CIR dispatch OK"
		else
			echo "  SUSPEND SUPER CIR FAIL — see build/nunit-$name.suspend-super-cir.log"
			tail -25 "$ROOT/build/nunit-$name.suspend-super-cir.log"; rc=1
		fi
		inline_frame_cir="$dir/obj/$CONFIGURATION/net10.0/cir/CrossinlineSuspendObjectTests.cir.json"
		if python3 "$ROOT/tests/coroutines/assert-inline-suspend-frame-cir.py" "$inline_frame_cir" \
			>"$ROOT/build/nunit-$name.inline-suspend-frame-cir.log" 2>&1; then
			echo "  inline suspend carrier generic frame OK"
		else
			echo "  INLINE SUSPEND FRAME CIR FAIL — see build/nunit-$name.inline-suspend-frame-cir.log"
			tail -25 "$ROOT/build/nunit-$name.inline-suspend-frame-cir.log"; rc=1
		fi
		materialized_frame_cir="$dir/obj/$CONFIGURATION/net10.0/cir/MaterializedLambdaCaptureTests.cir.json"
		if python3 "$ROOT/tests/coroutines/assert-materialized-constructed-frame-cir.py" "$materialized_frame_cir" \
			>"$ROOT/build/nunit-$name.materialized-constructed-frame-cir.log" 2>&1; then
			echo "  materialized constructed intrinsic generic frame OK"
		else
			echo "  MATERIALIZED CONSTRUCTED FRAME CIR FAIL — see build/nunit-$name.materialized-constructed-frame-cir.log"
			tail -25 "$ROOT/build/nunit-$name.materialized-constructed-frame-cir.log"; rc=1
		fi
	fi
	if [[ "$proj" == "tests/interop/consumer" ]]; then
		interop_dll="$dir/bin/$CONFIGURATION/net10.0/InteropConsumer.Tests.dll"
		interop_klib="$dir/obj/$CONFIGURATION/net10.0/klib/InteropProducer.klib"
		flags_bir="$dir/obj/$CONFIGURATION/net10.0/bir/ClrFlagsEnumTests.bir.json"
		flags_cir="$dir/obj/$CONFIGURATION/net10.0/cir/ClrFlagsEnumTests.cir.json"
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-flags-enum "$interop_klib" FlagsInterop.AccessFlags "$flags_bir" "$flags_cir" \
			>"$ROOT/build/nunit-$name.flags-enum.log" 2>&1; then
			echo "  CLR [Flags] exact KLIB signatures and BIR-to-CIR carrier consumption OK"
		else
			echo "  CLR FLAGS ENUM METADATA FAIL — see build/nunit-$name.flags-enum.log"
			tail -25 "$ROOT/build/nunit-$name.flags-enum.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-csharp-extension-shape "$interop_klib" C1Net C1Net.Ext tripled \
			>"$ROOT/build/nunit-$name.extension-shape.log" 2>&1; then
			echo "  C# extension projects as one namespace extension plus one ordinary CLR static"
		else
			echo "  C# EXTENSION KLIB SHAPE FAIL — see build/nunit-$name.extension-shape.log"
			tail -25 "$ROOT/build/nunit-$name.extension-shape.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--klib-class-properties "$interop_klib" "ExplicitMethodInterop.ExplicitOperations" "Name" \
			>"$ROOT/build/nunit-$name.explicit-property.log" 2>&1; then
			echo "  explicit-interface CLR Property/MethodImpl projects once under its simple Kotlin name"
		else
			echo "  EXPLICIT PROPERTY KLIB FAIL — see build/nunit-$name.explicit-property.log"
			tail -25 "$ROOT/build/nunit-$name.explicit-property.log"; rc=1
		fi
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--volatile-consumer "$interop_dll" "MemberShapeTests" \
			"volatileInstanceGet" "volatileInstanceSet" "volatileStaticGet" "volatileStaticSet" \
			>"$ROOT/build/nunit-$name.volatile.log" 2>&1; then
			echo "  referenced CLR volatile field access IL OK"
		else
			echo "  VOLATILE FIELD IL FAIL — see build/nunit-$name.volatile.log"
			tail -25 "$ROOT/build/nunit-$name.volatile.log"; rc=1
		fi
		csharp14_producer="$ROOT/tests/special/csharp14-static-extensions/producer/bin/$CONFIGURATION/net10.0/StaticExtensionProducer.dll"
		csharp14_consumer_project="$ROOT/tests/special/csharp14-static-extensions/consumer/StaticExtensionConsumer.csproj"
		csharp14_consumer="$ROOT/tests/special/csharp14-static-extensions/consumer/bin/$CONFIGURATION/net10.0/StaticExtensionConsumer.dll"
		if dotnet build "$csharp14_consumer_project" -c "$CONFIGURATION" --no-incremental -v:q --nologo \
			>"$ROOT/build/nunit-$name.csharp14-build.log" 2>&1 && \
			dotnet run --project "$ROOT/tests/special/csharp14-static-extensions/inspector/StaticExtensionInspector.csproj" \
				-- "$csharp14_producer" "$csharp14_consumer" "$interop_dll" \
			>"$ROOT/build/nunit-$name.csharp14.log" 2>&1; then
			echo "  C# 14 static extension KLIB projection + implementation call targets OK"
		else
			echo "  C# 14 STATIC EXTENSION METADATA FAIL — see build/nunit-$name.csharp14.log"
			tail -25 "$ROOT/build/nunit-$name.csharp14-build.log" 2>/dev/null || true
			tail -25 "$ROOT/build/nunit-$name.csharp14.log" 2>/dev/null || true
			rc=1
		fi
		if bash "$ROOT/tests/special/csharp14-static-extensions/run-malformed.sh" \
			>"$ROOT/build/nunit-$name.csharp14-negative.log" 2>&1; then
			echo "  malformed C# 14 static extension graphs rejected by dll2klib + bir2cir OK"
		else
			echo "  C# 14 STATIC EXTENSION NEGATIVE FAIL — see build/nunit-$name.csharp14-negative.log"
			tail -25 "$ROOT/build/nunit-$name.csharp14-negative.log"; rc=1
		fi
	fi
	if [[ "$proj" == "tests/basic" ]]; then
		basic_dll="$dir/bin/$CONFIGURATION/net10.0/DotKt.Tests.Basic.dll"
		if dotnet "$METADATA_INSPECTOR_DLL" \
			--volatile-consumer "$basic_dll" 'GenericVolatileBox`1' \
			"prop_get<value>" "prop_set<value>" "readValueByRef" "writeValueByRef" \
			>"$ROOT/build/nunit-$name.volatile-generic.log" 2>&1; then
			echo "  local generic volatile field access IL OK"
		else
			echo "  GENERIC VOLATILE FIELD IL FAIL — see build/nunit-$name.volatile-generic.log"
			tail -25 "$ROOT/build/nunit-$name.volatile-generic.log"; rc=1
		fi
	fi
	# The emitted assembly is named after the .ktproj (e.g. DotKt.Tests.Basic.ktproj -> DotKt.Tests.Basic.dll).
	proj_file="$(find "$dir" -maxdepth 1 \( -name '*.ktproj' -o -name '*.csproj' \) | head -1)"
	proj_base="$(basename "$proj_file")"
	asm="${proj_base%.*}.dll"
	# `dotnet build` above uses Debug. Restrict discovery to that configuration: a stale Release artifact from a
	# developer's focused build must never make ILVerify inspect yesterday's DLL while tests execute today's Debug DLL.
	emitted="$(find "$dir/bin/$CONFIGURATION" -name "$asm" 2>/dev/null | head -1)"
	[[ "$proj_file" == *.ktproj && -f "$emitted" ]] && EMITTED+=("$emitted")
	# Also collect any declared EXTRA_EMIT assembly (e.g. a ProjectReference'd producer dll copied into bin).
	if [[ -v EXTRA_EMIT[$proj] ]]; then
		for extra_name in ${EXTRA_EMIT[$proj]}; do
			extra_emitted="$(find "$dir/bin/$CONFIGURATION" -name "$extra_name" 2>/dev/null | head -1)"
			[[ -f "$extra_emitted" ]] && EMITTED+=("$extra_emitted")
		done
	fi

	# Run the tests. HONOR dotnet test's EXIT STATUS as the behavioral verdict, then reconcile the TRX discovery
	# count with EXPECTED_DISCOVERED. A count change is not inherently wrong, but it must be reviewed and recorded
	# rather than silently expanding or shrinking the suite.
	trxdir="$dir/TestResults"; rm -rf "$trxdir"
	if dotnet test "$dir" -c "$CONFIGURATION" --no-build --logger "trx;LogFileName=results.trx" -v q --nologo \
		>"$ROOT/build/nunit-$name.test.log" 2>&1; then test_ok=1; else test_ok=0; fi
	trx="$(find "$trxdir" -name '*.trx' 2>/dev/null | head -1)"
	if [[ ! -f "$trx" ]]; then
		echo "  DISCOVERY FAIL — no TRX produced (0 tests / host crash); see build/nunit-$name.test.log"; rc=1; continue
	fi
	total="$(grep -oE 'total="[0-9]+"' "$trx" | grep -oE '[0-9]+' | head -1)"; total="${total:-0}"
	expected_total="${EXPECTED_DISCOVERED[$proj]}"
	if (( test_ok == 1 && total == expected_total )); then
		echo "  discovered=$total  OK — dotnet test green"
	elif (( test_ok == 1 )); then
		echo "  DISCOVERY COUNT CHANGED — expected=$expected_total observed=$total; review the additions/removals and update EXPECTED_DISCOVERED"; rc=1
	else
		echo "  discovered=$total  TEST FAIL — dotnet test returned non-zero; see build/nunit-$name.test.log"; rc=1
	fi
done

# ilverify once per emitted suite assembly (design §5 order; baseline in tests/run-ilverify.sh).
if (( ${#EMITTED[@]} )); then
	echo "=========================================================="
	echo "ilverify (once per emitted suite assembly)"
	# --audit-baseline: this is the COMPLETE emitted set, so a baseline key that masked nothing is a dead entry.
	bash "$ROOT/tests/run-ilverify.sh" --audit-baseline "${EMITTED[@]}" || rc=1
fi

echo "=========================================================="
(( rc == 0 )) && echo "run-nunit-tests: GREEN" || echo "run-nunit-tests: RED"
exit $rc

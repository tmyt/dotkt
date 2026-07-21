#!/usr/bin/env bash
# The canonical IL gate: every sample compiles (kotc -> BIR), lowers (bir2cir -> CIR), emits (ilemit ->
# CIL), RUNS with asserted stdout, and finally ilverify formally verifies the emitted assemblies.
# Inputs: cases/** + the toolchain (rebuilt from current sources) + the cached stdlib jar/ref/rt (built
# if missing). Results: build/verify-il/run-<name> — ONE atomically-written record per sample (an EXIT
# trap in each worker guarantees the record even if the worker crashes under set -e; the old runner
# echoed directly from the parallel subshells, so a crashing sample could DROP its FAIL line and
# interleave output — the documented false-pass race). Green = every fail name is in the XFAIL_RUN /
# XFAIL_ILVERIFY baseline below (exit 0); ANY name outside it prints NEW-FAIL and exits 1.
source "$(dirname "$0")/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the full IL sample gate (no flags). -h for this help.
Green (exit 0) = no fail name outside the XFAIL_RUN/XFAIL_ILVERIFY baseline declared in this script.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

# The authoritative XFAIL baseline — MACHINE-READABLE (fail name -> reason). The verdict at the bottom
# is computed against these maps via lib.sh xfail_diff: exit 0 iff every actual fail is listed here;
# any name outside them prints NEW-FAIL and exits 1; a listed name that starts passing prints
# "FIXED — remove it from the xfail list" WITHOUT reddening the gate (prune the entry in the same
# change). Coroutine/SequenceScope-deferred samples compile+emit but CRASH at run until the coroutine
# lowering lands (MEMORY coroutine-lowering-layer-deferred); the ilverify names are formal-verification
# findings, not run failures.
declare -A XFAIL_RUN=(
)
declare -A XFAIL_ILVERIFY=(
	# GitHub #12 (the OPEN formal-only covariance follow-up; #2, its runtime-unsafe root, is CLOSED) — a FORMAL-ONLY
	# covariance finding from the bir2cir `Key<*>` -> `Key<Element>` lowering, NOT the ilemit
	# codegen. `CoroutineContext.Key<E : Element>` is INVARIANT; a specific element's companion is a `Key<Self>`
	# (`MyElem.Key : Key<MyElem>`) used through the star projection `Key<*>` (Kotlin use-site variance, which the CLR
	# has no equivalent for). bir2cir lowers the projection to the type-param BOUND `Key<Element>` (the parameter/return
	# type), so the ctor arg (`MyElem::.ctor` passing the companion) and `get_key()` return a `Key<Self>` where
	# `Key<Element>` is formally expected — StackUnexpected. Runtime-SAFE (the reference is only stored/compared, never
	# variance-cast + no `Key<Element>`-specific member is invoked on it) — both cases RUN green. Fixing the formal finding
	# is a bir2cir/representation follow-up (emit the companion as `Key<Element>` or model the projection as covariant),
	# NOT an ilemit codegen change. TRACKED as the OPEN issue #12 (repointed from the now-CLOSED root #2). Kept in ASMS
	# (no silent gap); the run lane is the behavioral gate.
	# coctxkey / cointercept / awaitintercept / classdeleg / genbaseext migrated -> tests/coroutines; their #12/#174
	# formal-only ilverify findings are baselined for DotKt.Tests.Coroutines.dll in tests/run-ilverify.sh.
	# ---- newly EXPOSED by the #99 run-derived-ASMS coverage work (these run-only samples had NO ilverify coverage
	# before; each RUNS green — a runtime-safe formal-only finding attributed to a live tracking issue) ----
	# copyofnull (#127/#86): the write/return axis of the nullable value-type OBJECT-erasure family. copyOf/arrayOfNulls
	# honestly yields Array<T?> (#124); for a value elem the array is materialized as object[] where Nullable<int32>[]
	# is formally expected at the callsite. Runtime-safe (RUN green — null tail + prefix read back correctly).
	[copyofnull]="#127/#86: nullable value-type array (copyOf -> Array<T?>, #124) materialized as object[] where Nullable<T>[] is formally expected — the object-erasure write/return axis, runtime-safe (RUN green)"
)

# The CLR stdlib (kotlin.*) is supplied to kotc via the FRONTEND KLIB (scripts/build-stdlib-klib.sh) on
# -classpath, REPLACING the old JVM frontend jar (which itself replaced the JVM kotlin-stdlib.jar that
# leaked java.util.* typealiases). This preserves full Kotlin semantics and is the BINDING invariant:
# kotlin.* comes from the KLIB, never from facadegen. (legacy coroutines jar dropped
# 2026-07-03: the stdlib cold-core surface is kotlin.clr.await ONLY; blockOn/delay were dropped from the
# stdlib and re-homed to the test harness — cases/*/harness.kt = dotkt.support.)
CP="$FE_KLIB"

# Build the compiler launcher ONCE (a plain Java app). Per-sample invokes then cost ~2s of JVM startup
# instead of ~9s for `gradlew --no-daemon :kotc:run`.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$KOTC"
need_fe_klib
need_dotnet_reference_sets

# Result records (one per sample) + the refdll handoff to the ilverify phase live here.
RESULTS="$ROOT/build/verify-il"
rm -rf "$RESULTS"; mkdir -p "$RESULTS"

# Purge stale app-sample corpus dirs whose case no longer exists (migrated to NUnit / deleted). verify-il
# rm+re-emits every LIVE case's build/{bir,cir}-<name> below, but NEVER touches a REMOVED case's dir, so a
# ghost dir (pre-change BIR/CIR from an older toolchain) lingers and verify-schema.sh globs it -> a false-RED
# "bare STRING at type slot" drift from output the CURRENT toolchain never produced. Zero-cost: any live dir
# deleted here is re-emitted below; a genuinely-removed case's ghost is simply gone.
for _d in "$ROOT"/build/bir-* "$ROOT"/build/cir-*; do
	[ -d "$_d" ] || continue
	_c="$(basename "$_d")"; _c="${_c#bir-}"; _c="${_c#cir-}"
	[ -d "$ROOT/cases/il-$_c" ] || rm -rf "$_d"
done

# Run samples concurrently (each compile is an independent ~2s JVM startup). A job pool caps parallelism.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 2 )) && JOBS=$(( JOBS - 2 ))   # use the box (24c): leave 2 cores headroom. Was capped at 6 (stale — /tmp leak is fixed; MEMORY dev-box-resources-parallelize-aggressively)
gate() { while (( $(jobs -rp | wc -l) >= JOBS )); do wait -n 2>/dev/null || true; done; }

# Every sample worker calls sample_guard FIRST: it arms an EXIT trap that writes EXACTLY ONE result
# record (PASS/FAIL line + optional diff detail) to a temp file and mv's it into place (atomic — the
# aggregator never sees a partial record, and a worker crashing under set -e still yields its record
# as a FAIL instead of silently dropping the line). The worker body sets ok=1 on success, or reason=
# (+ optional detail=) on failure.
sample_guard() { # <name>
	g_name="$1"; ok=0; reason="harness crash"; detail=""
	g_finish() {
		local f="$RESULTS/run-$g_name"
		if (( ok )); then
			echo "PASS  il:$g_name" > "$f.tmp"
		else
			{ echo "FAIL  il:$g_name ($reason)"; if [[ -n "$detail" ]]; then printf '%s\n' "$detail"; fi; } > "$f.tmp"
		fi
		mv -f "$f.tmp" "$f"
	}
	trap g_finish EXIT
}
mismatch() { # <expected> <actual> — fill reason/detail for an output comparison failure
	reason="output mismatch"
	detail="$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$1" "$2")"
}
# Per-sample RUN timeout (#108): a coroutine resume/pulse-drop regression can DEADLOCK a blocking-drain
# sample (a suspend main that blocks until it drains). Without a hard bound ONE hung sample wedges the ENTIRE gate — CI then kills the
# whole job on its outer timeout with NO NEW-FAIL diff. timeout(1) SIGTERMs at the deadline (exit 124; if
# still alive after -k, SIGKILL -> 137); either is classified as a distinct, loud "run timeout" FAIL so a
# deadlock surfaces as a clean gate record instead of an indefinite hang. Override via DOTKT_RUN_TIMEOUT.
RUN_TIMEOUT="${DOTKT_RUN_TIMEOUT:-60}"
# run_and_compare <dll> <expected> — run an emitted sample under the #108 timeout and set ok / reason+detail
# for the run+stdout compare. Single home for the four il_check* run tails (was duplicated verbatim).
run_and_compare() { # <dll> <expected>
	local dll="$1" exp="$2" out rc=0
	out="$(timeout -k 5 "${RUN_TIMEOUT}s" dotnet "$dll" 2>/dev/null)" || rc=$?
	if (( rc == 124 || rc == 137 )); then
		reason="run timeout (>${RUN_TIMEOUT}s; possible coroutine resume/pulse-drop deadlock — #108)"
		detail="$(printf -- '--- expected ---\n%s\n--- actual (before timeout) ---\n%s' "$exp" "$out")"; return
	fi
	if (( rc != 0 )); then
		reason="run crash"; detail="$(printf -- '--- expected ---\n%s\n--- actual (before crash) ---\n%s' "$exp" "$out")"; return
	fi
	if [[ "$out" == "$exp" ]]; then ok=1; else mismatch "$exp" "$out"; fi
}

# UNCONDITIONAL tool builds: the gate tests the CURRENT sources.
build_tool ilemit
# bir2cir: the canonical kotc -> bir2cir -> ilemit pipeline. kotc emits bare kotlin.* FQNs for source-type
# primitives at EVERY position; bir2cir lowers them to the CLR-codegen vocabulary ilemit consumes. App builds run
# in substitute/app mode (not a `-Xstdlib-compilation` self-build), so kotlin.* primitives lower (kotlin.Int -> int, ...).
build_tool bir2cir
# Lower a sample's BIR -> CIR (bir2cir), then emit IL (ilemit). A bir2cir failure folds into the ilemit-error bucket.
il_emit() { # <name> <ildir> <asm> <birdir> [extra ilemit args...]
	local name="$1" ildir="$2" asm="$3" birdir="$4"; shift 4
	local cirdir="$ROOT/build/cir-$name"; rm -rf "$cirdir"; mkdir -p "$cirdir"
	# bir2cir reads the REFERENCE stdlib for the @ClrTypeAlias/@ClrIntrinsic labels: app-build collection/
	# StringBuilder/Regex type tokens and member calls lower from it (bir2cir is the single substitution home).
	local compile_parts=("$FRAMEWORK_COMPILE_REFS") runtime_parts=()
	[[ -f "$STDLIB_REF_DLL" ]] && compile_parts+=("$STDLIB_REF_DLL")
	# A2 (#61): bir2cir binds a facadegen-injected .NET member call to its CLR shape by RESOLVING the owner FQN
	# against the loaded .NET reference assemblies (its long-lived MetadataLoadContext), so it needs the SAME
	# app .NET refs ilemit gets — the sample's own runtime.cs dll etc. Classify every script-level `--ref` (in "$@")
	# to bir2cir too, EXCEPT the RUNTIME stdlib (bir2cir reads the REFERENCE stdlib, added above). System.* owners
	# resolve from FRAMEWORK_COMPILE_REFS; no runtime-directory probing occurs.
	local il_args=("$@") ai=0
	while (( ai < ${#il_args[@]} )); do
		if [[ "${il_args[ai]}" == "--ref" ]]; then
			local r="${il_args[ai+1]}"
			runtime_parts+=("$r")
			[[ "$r" != "$STDLIB_RT_DLL" ]] && compile_parts+=("$r")
			ai=$((ai+2))
		else ai=$((ai+1)); fi
	done
	dotnet "$BIR2CIR_DLL" "$cirdir" --compile-refs "$(refset_join "${compile_parts[@]}")" "$birdir"/*.bir.json >/dev/null 2>&1 || return 1
	dotnet "$ILEMIT_DLL" "$ildir" "$asm" --runtime-refs "$(refset_join "${runtime_parts[@]}")" "$cirdir"/*.cir.json >/dev/null 2>&1
}

# S5 FIR-injection metadata for samples that inherit a real .NET base type (façade-free).
build_tool facadegen

# CLR stdlib (the canonical build under libraries/stdlib/): the RUNTIME assembly is --ref'd into every
# emitted case so a stdlib op resolves to its real Kotlin body (and copied next to each output for the
# run phase); the REFERENCE assembly is bir2cir's @Clr-metadata input. Build if missing, reuse if present.
need_stdlib_ref; need_stdlib_rt

# ---- #138 native-ref guard: a NON-managed PE in the compile set must never abort bir2cir ----
# An unmanaged/native Windows .dll (libSkiaSharp.dll etc.) can reach a resolved reference set; bir2cir's
# ref loader must SKIP it (it carries no CLI metadata), not crash. This asserts the loader guard on a REAL native PE
# (the SDK-shipped msdia140.dll — a PE32+ with no CLI header), producing a trivial BIR and lowering it with the
# native dll included. If no native PE is present in this environment the check SKIPs (documented — the loader guard is
# still exercised end-to-end by the triage repro `dotkt.sh --ref <native.dll> --run foo.kt`).
find_native_pe() {
	local c
	for c in /usr/share/dotnet/sdk/*/TestHostNetFramework/*/msdia140.dll /usr/share/dotnet/sdk/*/TestHost*/*/*.dll; do
		[[ -f "$c" ]] || continue
		if file "$c" 2>/dev/null | grep -q 'PE32' && ! file "$c" 2>/dev/null | grep -qi 'Mono/\.Net'; then echo "$c"; return 0; fi
	done
	return 1
}
if NATIVE_PE="$(find_native_pe)"; then
	nbir="$ROOT/build/bir-nativeref"; ncir="$ROOT/build/cir-nativeref"; rm -rf "$nbir" "$ncir"; mkdir -p "$nbir" "$ncir"
	printf 'fun main() { println("ok") }\n' > "$ROOT/build/nativeref.kt"
	if ! "$LAUNCHER" "$ROOT/build/nativeref.kt" -no-stdlib -classpath "$CP" -d "$nbir" >/dev/null 2>&1; then
		echo "IL GATE RED — #138 native-ref guard: could not produce probe BIR"; exit 1; fi
	nrc=0; nerr="$(dotnet "$BIR2CIR_DLL" "$ncir" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$NATIVE_PE")" "$nbir"/*.bir.json 2>&1)" || nrc=$?
	if (( nrc != 0 )) || ! grep -q 'skipping non-managed reference' <<<"$nerr"; then
		echo "IL GATE RED — #138 native-ref guard FAILED (rc=$nrc): bir2cir did not skip native reference $(basename "$NATIVE_PE")"
		printf '%s\n' "$nerr"; exit 1; fi
	echo "GUARD   nativeref (bir2cir skipped non-managed reference $(basename "$NATIVE_PE"))"
else
	echo "GUARD   nativeref SKIP (no native PE found in this environment; loader guard still covered by dotkt.sh triage repro)"
fi

# Build a sample's <srcDir>/runtime.cs into a referenced .NET assembly (name from <runtimeAsm>); echo its path.
# NRT-oblivious (Nullable disable): the one sample that needed real [Nullable] bytes (#150 delegnull) is migrated
# to the C#-producer NUnit lane (tests/interop/producer-nrt), so no verify-il sample builds runtime.cs with NRT on.
build_runtime() { # <srcDir> <runtimeAsm>
	local srcdir="$1" rasm="$2" rt="$ROOT/build/rt-$rasm"
	rm -rf "$rt"; mkdir -p "$rt"
	cp "$srcdir/runtime.cs" "$rt/runtime.cs"
	printf '%s\n' "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>$rasm</AssemblyName><Nullable>disable</Nullable></PropertyGroup></Project>" > "$rt/rt.csproj"
	dotnet build "$rt" -c Release -o "$rt/out" -v q --nologo >/dev/null 2>&1 || true
	echo "$rt/out/$rasm.dll"
}

# ---- resolved-reference-set guards ---------------------------------------------------------------
# Keep the tools honest about the boundary established above: a directory is never a reference universe, duplicate
# assembly identities fail loudly, and two selected assemblies defining one FQN are never resolved by list order.
REF_GUARD="$ROOT/build/ref-resolution-guard"
rm -rf "$REF_GUARD"; mkdir -p "$REF_GUARD/a" "$REF_GUARD/b" "$REF_GUARD/only-a"
printf '%s\n' 'namespace RefGuard { public class Dupe { } public class OnlyA { } }' > "$REF_GUARD/a/Types.cs"
printf '%s\n' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>RefGuardA</AssemblyName></PropertyGroup></Project>' > "$REF_GUARD/a/A.csproj"
printf '%s\n' 'namespace RefGuard { public class Dupe { } public class OnlyB { } }' > "$REF_GUARD/b/Types.cs"
printf '%s\n' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>RefGuardB</AssemblyName></PropertyGroup></Project>' > "$REF_GUARD/b/B.csproj"
dotnet build "$REF_GUARD/a/A.csproj" -c Release -o "$REF_GUARD/only-a" -v q --nologo >/dev/null
dotnet build "$REF_GUARD/b/B.csproj" -c Release -o "$REF_GUARD/only-a" -v q --nologo >/dev/null
A_DLL="$REF_GUARD/only-a/RefGuardA.dll"; B_DLL="$REF_GUARD/only-a/RefGuardB.dll"

# RefGuardB.dll is deliberately adjacent to A. Passing A alone must not make OnlyB visible.
poison_err="$(dotnet "$FACADEGEN_DLL" "$REF_GUARD/poison.meta" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$A_DLL")" RefGuard.OnlyB 2>&1)"
if ! grep -q 'resolved to no type' <<<"$poison_err" || grep -q 'RefGuard.OnlyB' "$REF_GUARD/poison.meta"; then
	echo "IL GATE RED — exact-reference guard FAILED: facadegen discovered an unlisted neighbouring DLL"; exit 1
fi
echo "GUARD   exactrefs (unlisted neighbouring DLL ignored)"

cp "$A_DLL" "$REF_GUARD/RefGuardA-copy.dll"
dup_rc=0; dup_err="$(dotnet "$FACADEGEN_DLL" "$REF_GUARD/dup.meta" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$A_DLL" "$REF_GUARD/RefGuardA-copy.dll")" RefGuard.OnlyA 2>&1)" || dup_rc=$?
if (( dup_rc == 0 )) || ! grep -q "conflicting references with assembly name 'RefGuardA'" <<<"$dup_err"; then
	echo "IL GATE RED — duplicate-identity guard FAILED"; printf '%s\n' "$dup_err"; exit 1
fi
echo "GUARD   refidentity (duplicate assembly simple name rejected)"

amb_rc=0; amb_err="$(dotnet "$FACADEGEN_DLL" "$REF_GUARD/amb.meta" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$A_DLL" "$B_DLL")" RefGuard.Dupe 2>&1)" || amb_rc=$?
if (( amb_rc == 0 )) || ! grep -q "type 'RefGuard.Dupe' is defined by multiple compile references" <<<"$amb_err"; then
	echo "IL GATE RED — duplicate-type guard FAILED"; printf '%s\n' "$amb_err"; exit 1
fi
echo "GUARD   reftype (duplicate FQN rejected)"

# Inject (façade-free) a sample's own runtime types AND reference the runtime dll: build runtime.cs, scan the
# .kt imports into a metadata file, compile with it, then classify that exact assembly into compile/runtime sets.
il_check_inject() { # <name> <asm> <srcDir> <expected> <runtimeAsm>
	gate
	(
		sample_guard "$1"
		name="$1"; asm="$2"; src="$3"; expected="$4"; rasm="$5"
		echo "$asm" > "$RESULTS/asm-$name"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"; meta="$ROOT/build/$name.meta"
		refdll="$(build_runtime "$src" "$rasm")"; echo "$refdll" > "$RESULTS/refdll-$name"
		implist="$ROOT/build/$name.imports"
		"$LAUNCHER" --scan-imports --output "$implist" "$src"/*.kt >/dev/null 2>&1 || true
		dotnet "$FACADEGEN_DLL" "$meta" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$refdll")" --import-list "$implist" >/dev/null 2>&1 || true
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! CLR_TYPES_METADATA="$meta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit "$name" "$ildir" "$asm" "$birdir" --ref "$refdll" --ref "$STDLIB_RT_DLL"; then
			reason="ilemit error"; exit 0; fi
		cp "$refdll" "$ildir/"; cp "$STDLIB_RT_DLL" "$ildir/"
		run_and_compare "$ildir/$asm.dll" "$expected"
	) &
}

il_check() { # <name> <asm> <srcArg> <expected> [metadataFile]
	gate
	(
		sample_guard "$1"
		name="$1"; asm="$2"; src="$3"; expected="$4"; meta="${5:-}"
		echo "$asm" > "$RESULTS/asm-$name"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		# The case's .NET-space facade metadata (OBSCOLLMETA/... — System.* injection) ONLY, if any. The stdlib
		# (kotlin.*) is supplied to kotc by the frontend KLIB on -classpath, NOT facadegen. --ref the runtime
		# DotKt.Stdlib.dll so a stdlib op (getOrElse, ...) resolves to its real Kotlin body instead of a retired lowering.
		if ! CLR_TYPES_METADATA="${meta:-}" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit "$name" "$ildir" "$asm" "$birdir" --ref "$STDLIB_RT_DLL"; then
			reason="ilemit error"; exit 0; fi
		cp "$STDLIB_RT_DLL" "$ildir/"
		run_and_compare "$ildir/$asm.dll" "$expected"
	) &
}

# Façade-free .NET interop via the import scan ALONE (no sample runtime.cs): scan the .kt imports, facadegen the
# referenced .NET types into metadata, compile with it, ilemit against the runtime + stdlib. This is the C-2 single
# path for a sample that consumes BCL types through `import System.X` (System.Math, System.Text.StringBuilder) and
# needs no custom runtime assembly — the same path scripts/dotkt.sh and the .ktproj targets use. (il_check_inject is
# the variant for a sample that ALSO ships its own runtime.cs; il_check is for a sample with no .NET imports at all.)
il_check_imports() { # <name> <asm> <srcDir> <expected>
	gate
	(
		sample_guard "$1"
		name="$1"; asm="$2"; src="$3"; expected="$4"
		echo "$asm" > "$RESULTS/asm-$name"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"; meta="$ROOT/build/$name.meta"
		implist="$ROOT/build/$name.imports"
		"$LAUNCHER" --scan-imports --output "$implist" "$src"/*.kt >/dev/null 2>&1 || true
		dotnet "$FACADEGEN_DLL" "$meta" --compile-refs "$FRAMEWORK_COMPILE_REFS" --import-list "$implist" >/dev/null 2>&1 || true
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! CLR_TYPES_METADATA="$meta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit "$name" "$ildir" "$asm" "$birdir" --ref "$STDLIB_RT_DLL"; then
			reason="ilemit error"; exit 0; fi
		cp "$STDLIB_RT_DLL" "$ildir/"
		run_and_compare "$ildir/$asm.dll" "$expected"
	) &
}

il_check m0    M0Kt  "$ROOT/cases/m0/M0.kt"  "$(printf 'sum = 5\nzero\nn=1\nn=2')"
# injectdedup (#15): a declaration whose identity is BOTH in the compiled SOURCE (Demo.kt: `class Plain` +
# top-level `fun hello`) AND in the facadegen injection metadata (demo.meta — as if injected from a
# <ProjectReference>'d dll that exports it). The FIR injector must SUPPRESS the injected copy so the SOURCE
# declaration wins (else `overload resolution ambiguity` + `conflicting overloads/declarations` — the #15
# double). Uses the meta ALONE (no conflicting --ref), so the source type lowers/emits LOCALLY end-to-end.
il_check injectdedup App "$ROOT/cases/il-injectdedup" "$(printf '42\nplain')" "$ROOT/cases/il-injectdedup/demo.meta"
# m-c1 (cross-file open-class/override) migrated to the NUnit battery tests/il/fixtures/MigMCrossFileTests.kt
# (crossFileClassesAndOverride; sibling decls in MigMCrossFile.kt) — the case dir + this il_check were removed same-change.
# language-core family (il-object/il-objexpr/il-companionext/il-ifacecompanion/il-op/il-ops/il-usermember/il-userrange/
# il-rangein/il-whensubj/il-smartcast/il-scope) migrated to the NUnit battery tests/il/fixtures/LanguageCoreTests.kt
# (12 methods), gated by tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs + il_check
# lines were removed same-change; their il-object/il-objexpr/il-op/il-ops/il-rangein/il-scope/il-smartcast/il-userrange/
# il-whensubj PURE entries were removed from verify-differential.sh same-change.
# lambda/closure/HOF/function-reference family (il-closure/il-lambda/il-genclosure/il-genhof/il-mfclosure/il-mflambda/
# il-writecapture/il-funref/il-extfunref/il-threadlambda) migrated to the NUnit battery tests/il/fixtures/LambdaTests.kt
# (10 methods; + LambdaTestsB.kt for the il-mfclosure/il-mflambda file-B halves), gated by tests/run-nunit-il.sh.
# Per the cases-test-design audit #14 the old per-case dirs + il_check/il_check_imports lines were removed same-change.
# (The suspend/coroutine family has its OWN NUnit lane now — tests/coroutines; the pilot batch migrated there
# same-change. il-suspendnestedcapture + the rest of the family stay in this bash lane pending later batches.)
# capref-inline / adapterref: migrated -> tests/coroutines/fixtures/CorADelegAdapterTests.kt (a coerced `::ref`
# ADAPTER_FOR_CALLABLE_REFERENCE inside a buildList{} inline lambda — receiver-capture / #84 G bound-and-unbound
# member ref forwarded as callInstance); cases/il-capref-inline + il-adapterref dirs + these il_check lines removed same-change.
# generic-types family (il-genbase/il-genctor/il-geninherit/il-genstatic/il-gencolladd/il-genlocalclass/il-genfield/
# il-objgen/il-gfac/il-genextnew) migrated to the NUnit battery tests/il/fixtures/GenericTypesTests.kt (10 methods),
# gated by tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs + il_check lines were
# removed same-change; their il-genbase/il-genctor/il-genstatic/il-gencolladd/il-gfac/il-objgen PURE entries were
# removed from verify-differential.sh same-change.
# enum family (il-enum/il-enumintr/il-enumtostr/il-enumbody/il-enumrich) migrated to the NUnit battery
# tests/il/fixtures/EnumTests.kt (+ EnumCrossFile.kt for the #90 cross-file basic-enum decl).
# icmparity (#129) -> tests/il/fixtures/MigratedIntropCIfaceImplTests.kt (icmparity_arityClashInterfaceFamily), migrated.
# m-i1 (System.Text.StringBuilder `import System.X` interop) migrated to the NUnit battery
# tests/il/fixtures/MigMInteropTests.kt (stringBuilderInterop) — the case dir + this il_check_imports were removed same-change.
# taskfam (taskfam_sameNameArityFamily): migrated -> tests/coroutines/fixtures/CorBTaskAwaitTests.kt (same-name .NET
# arity family — non-generic `Task` + `Task<TResult>`/`Task1` coexist, §8d); case dir + this line removed same-change.
# taskawait (taskawait_syncFastPath): migrated -> tests/coroutines/fixtures/TaskAwaitTests.kt (SuspendColdLowering
# P4 REVERSE bridge, Task.await() sync fast path); its cases/il-taskawait dir + this il_check line removed same-change.
# valueawait / cfgawait / cfgawaitgen / awaitintercept migrated -> tests/coroutines (CorA/CorB TaskAwait + ContextKey fixtures); dirs + il_check lines removed same-change.
# extawait (#10): `await` via a GENERIC EXTENSION GetAwaiter — the WinRT IAsyncOperation<T> shape, proved without the
# WinRT projection. `MyOp<T>` (runtime.cs) is awaitable ONLY through `static MyAwaiter<T> GetAwaiter<T>(this MyOp<T>)`.
# facadegen finds the referenced [Extension] GetAwaiter and injects `MyOp<T>.await()`; bir2cir emits
# `MyOpExtensions.GetAwaiter<Int>(op)` (clrGenericStatic, receiver-type-arg unified). Covers BOTH the sync fast path
# (IsCompleted true) AND a genuine SUSPEND+resume (OnCompleted schedules the continuation on the threadpool).
il_check_inject extawait ExtAwait "$ROOT/cases/il-extawait" "$(printf '8\n42')" KfcExtAwait
# The cold-core family (coldcf/coldgen/coforarray/coctxkey/cointercept/coldinst/coldvirt/coldsuper/coroutinectx/
# coldabstract/ifacesuspend/coldsubiface/coldbaseinherit/coldstaticmember/colddimgen/seqyieldall) migrated -> tests/coroutines
# (CorA ColdFlow/ColdMember/ColdDispatch/ContextKey + CorB SuspendCore/SuspendValue/Sequence fixtures); dirs + il_check lines removed same-change.
# The string/text family (String/Char ops, CharSequence, stringify, radix, number-parse, hashCode contract:
# il-str, il-strops, il-blank, il-strnum, il-strhash, il-radix, il-charminus, il-digittoint, il-substr, il-subseq,
# il-charseq/il-charseqs/il-charseqx/il-charseqbcl/il-charseqmore/il-charseqxfile/il-charseqlenref, il-colstr,
# il-nestedstr, il-interpnull, il-ntostr, il-nulltostr) migrated to the NUnit battery tests/il/fixtures/StringsTests.kt
# (+ StringsCrossFile.kt), gated by tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs
# + their il_check lines were deleted in that SAME change. (il-structfloateq/il-structfloateqnull matched the `str`
# grep prefix but are float-equality cases — migrated to the float/IEEE battery tests/il/fixtures/FloatTests.kt.)
# The float/IEEE family (Double/Float NaN + infinities, unordered `<=`/`>=` compares, -0.0/0.0 total-order,
# structural + direct/nullable float equality, hypot/expm1/ln1p BCL primitives: il-nan, il-nancmp, il-negzero,
# il-structfloateq, il-structfloateqnull, il-floateqnull, il-mathnumerics) migrated to the NUnit battery
# tests/il/fixtures/FloatTests.kt (8; +#181 safe-call nullable-float ==), gated by tests/run-nunit-il.sh; the old per-case dirs + il_check lines were
# deleted in that SAME change.
# printlnnull: println/print(null) render the string "null" (Kotlin semantics); non-null values print normally.
# The collections family (list/set/iteration/collection-op + Map-typed cases: il-coll*, il-map*, il-mut*, il-iter*,
# il-hashset2, il-iscoll, il-listeq, il-listplus, il-eachcount, il-emptymap, il-groupby2, …) migrated to the NUnit
# batteries tests/il/fixtures/CollectionsTests.kt (16) + MapsTests.kt (10), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14 the old per-case dirs + their il_check lines were deleted in that SAME change.
# Math/numeric family (il-math, il-mathabs, il-coerce, il-roundhalfup + differential-only il-divmin, il-mixnum)
# migrated to the NUnit battery tests/il/fixtures/MathTests.kt (6 methods), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14 the old per-case dirs + their il_check lines were deleted in that SAME change.
# pairnest: a nested collection/map INSIDE Pair/Triple.toString (C11) renders Kotlin-style — the tuple component's
# erased generic static type used to reach the raw .NET `List`1[...]` ToString; now routed through the runtime
# collection-aware stdlib stringifier (clrRenderTupleElement -> clrElemToString), matching the top-level nestedstr path.
# nullcollarg: #100 H3 regression guard — a nullable-inner collection type-arg (`Map<String, List<Int>?>`) upcast from
# a MutableMap must still collapse its V to IList and verify clean (the `?` must not smuggle an un-collapsed IReadOnly
# face past the collapse). Pure runnable guard for that shape.
# Array family (arr/arrops/arrnull/arrslice/arrplus/intarraytolist/copyintoverlap/fillrange/indices/indicesv/ubytearr/
# genarrlam) migrated to the NUnit in-process suite -> tests/il/fixtures/ArrayTests.kt (value asserts). il-copyofnull /
# il-boxgen stay here (live XFAIL_ILVERIFY findings, not migratable into the ilverify-clean lane).
# seqforin (seqforin_forInOverSequence): migrated -> tests/coroutines/fixtures/SequenceTests.kt (`for (x in seq)`
# over a Kotlin Sequence lowers through the same GetEnumerator/forEachInline path as Iterable); cases/il-seqforin + line removed same-change.
il_check boxgen BoxgenKt "$ROOT/cases/il-boxgen" "$(printf '42\n1\n42\n42\n10\n-1\n[1, 2, 3]\n[3, 2, 1]\n[a, b, c]\n[1, null, 3]\n[5, null, null]\nSUMMER')"   # C2 boxed-primitive dual-representation: getOrPut/getOrElse/compareBy/Array<Int?>/T:Enum<T>
il_check copyofnull Copyofnull "$ROOT/cases/il-copyofnull" "$(printf '[1, 2, 3, null, null]\n[1, 2]\n[1, 2, 3]\n1\nnull\n6\n[1, 2, null]\n[2.5, 3.5, null]\n[a, b, null]\n[x, y, null]\n[7, null, null]')"   # #124: Array<value-type>.copyOf(newSize) builds Nullable<elem>[] by runtime reflection (grow null-tail/shrink/prefix read-back; value + reference + already-nullable T)
# A6: rule-3 helper calls on CONCRETE generic alias receivers (HashMap/ArrayList/LinkedHashMap: class typeArgs +
# instantiated sig) + Map/MutableMap getOrDefault (bare-V map-defaults helper: retType carry, was BadImageFormat).
# regex family (il-regex/il-regexanchor/il-regexopts/il-regexreplace/il-regexgroups/il-regexseq/il-groupvalues)
# migrated to the NUnit battery tests/il/fixtures/RegexTests.kt (7 methods), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14 the old per-case dirs + il_check lines were removed same-change; their il-regex/
# il-regexgroups/il-regexreplace/il-groupvalues PURE entries were removed from verify-differential.sh same-change.
# linkedorder (#169): LinkedHashMap/LinkedHashSet (and mapOf/setOf) preserve insertion order across a MIDDLE removal —
# LinkedHashMap is backed by the insertion-ordered System...OrderedDictionary; LinkedHashSet by a pure-Kotlin set over it.
# linkedset (#169 regression): setOf/distinct()/toMutableSet() build the CONCRETE LinkedHashSet — was InvalidProgram
# (arity-only ctor pick routed `new LinkedHashSet(coll)` to the (Int) ctor; the self iterator()/ICollection Contains
# slot referenced the open generic self). Locks the crash-free build AND insertion order across a MIDDLE removal + retainAll.
# gencolladd: non-inlined GENERIC collection building via `.map`/`.add`/`.size` — the stdlib `clrCollAdd<T>`
# reads `c.size` (ICollection<!!T>.get_Count) on an OPEN method type-param. Locks the bymap/maxOrNull dispatch
# family's collection analog (an open-generic ICollection member call must bind at runtime, no EntryPointNotFound).
# cwindowed: CharSequence.windowed exercises a `break` in EXPRESSION position (its `coercedEnd` init); kotc lowers
# it to a valueBlock(goto/break + unreachable throw). eachcount: Grouping.eachCount reads a value-nullable smart-cast
# (`Int?`) in arithmetic (`count + 1`) — the C1 value-slot-unwrap class, locked here as a regression guard.
# cwindowedv: CharSequence.windowed with a VALUE-TYPE transform result (Int/Char). The transform lambda is a
# delegateNew target whose funcType keeps the synthetic <>dotkt_CharSequence, so its `it` param must stay synthetic
# (not collapse to System.String) — the stdlib passes a real <>dotkt_CharSequence (subSequence's result) in. W4-B guard.
# genseq (genseq_genericColdSequence): migrated -> tests/coroutines/fixtures/CorBSequenceTests.kt (generic cold-sequence
# SM `fun <T> wrap(x)=sequence{yield(x)}.toList()`; guards the T? nextValue double-unbox NRE); case dir + line removed same-change.
# genseq2 (C13a): a generic capturing closure passed as a DELEGATE arg (generateSequence's `{ seed }` -> the
# GeneratorSequence Function0 ctor param). ilemit's delegate-arg binding path emitted the generic closure newobj with
# an OPEN operand (Closure`1::.ctor(!0)) -> TypeLoadException; and the iterator's delegateInvoke passed a boxed T? to
# `Func<T,object>::Invoke(!0)` with no unbox -> InvalidProgramException at a VALUE element. Both fixed; value + ref drive.
il_check genseq2 GenSeq2 "$ROOT/cases/il-genseq2" "$(printf '[1, 2, 4]\n[a, ab, abb]\n18')"
# atomics -> tests/il/fixtures/MigratedIntropCAtomicsTests.kt (atomics_interlockedByrefBinding); atomicarraytry ->
# tests/il/fixtures/MigratedIntropCThreadingTests.kt (atomicarraytry_boundsThrowReleasesMonitorCrossThread), migrated.
# The nullable / null-safety battery (il-null, il-nullable-generic-list, il-nullableprim, il-nullbang,
# il-nullcollarg, il-nullcs, il-printlnnull, il-reqnn, il-safecallnv, il-trynullable) migrated to the NUnit
# battery tests/il/fixtures/NullableTests.kt (12 methods), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14, the old per-case dirs + these il_check lines were deleted in that SAME change.
# (il-nan/il-nancmp/il-negzero are NOT here — their subject is IEEE-float behavior, kept for a float battery.)
# m-s1 (nullable ?:/!!/?.) and m-s2 (data-class toString/copy/componentN/==/hashCode) were DOUBLE-registered here
# (il_check nullv/dataq) AND in verify-differential.sh PURE (m-s1/m-s2). Both are migrated to the NUnit battery
# (tests/il/fixtures/MigMNullableTests.kt nullableOperators, MigMDataClassTests.kt dataClassMembers); the case dirs
# and BOTH registrations (this il_check pair + the PURE entries) were removed in that SAME change.
# The non-coroutine inline family (il-inline, il-inline2, il-xinline, il-inlinedefaultlambda, il-inlinememberdefault,
# il-inline-klibmember-nlr, il-inlineinherit, il-inline-{nested-nlr,outerlabel,nlbreak,ownlabel,mutcapture,forward},
# il-inlinereturn{expr,unit,local}, il-inlineretcoerce) migrated to the NUnit battery tests/il/fixtures/InlineTests.kt.
# The inline cases below remain because they need a distinct lane (member-extension / generic-owner / sibling-file
# splice / transitive forwarding) or coroutine involvement (il-inline-suspend*, further down).
# #75 S4a — escape-analysis narrowing samples. Cross-module stdlib inline ops (forEach/map/run) route through the
# bir2cir InlineSplice engine ONLY when a lambda arg escapes (non-local return/break, or arm-c suspension); the
# non-escaping majority takes the plain delegate call. See docs/design-inline-s4-narrowing-95.md §8.
# classdeleg (#81): migrated -> tests/coroutines/fixtures/CorADelegAdapterTests.kt (CLASS delegation `class Foo : Bar by baz`
# — synthetic `$$delegate_N` IrField + ctor initializer, single/two/expr/generic delegates; ilverify #174 finding baselined
# in tests/run-ilverify.sh); cases/il-classdeleg dir + this il_check line removed same-change.
# #70: a genuine `::x`/`obj::p`/`Type::p` callable reference -> a lifted class implementing the REAL stdlib
# KProperty0/KMutableProperty0/KProperty1 (name/get/set/invoke), not the retired `dotkt$KProperty` synthetic.
# The G-1..G-6 generics battery (il-generic .. il-generic6) migrated to the NUnit suite:
# tests/il/fixtures/GenericsTests.kt (gated by tests/run-nunit-il.sh). Per the cases-test-design audit #14,
# the old per-case dirs + these il_check lines were deleted in that SAME change.
# Generic secondary-ctor delegation: `constructor(...) : this(...)` inside a generic class must anchor
# the sibling ctor onto the self-instantiation `C<T>` (ilemit EmitCtorBody). Regression repro for the
# RingBuffer<T> "not fully instantiated" crash behind listOf(...).windowed(3).
# A generic class extending a generic base instantiated over its OWN type param (`class D<T> : Base<T>()`):
# the base-ctor call AND inherited generic-base member access must anchor onto the CONSTRUCTED base `Base<!T>`,
# not the open def `Base<>` (else "not fully instantiated" / InvalidProgram). This is the SequenceBuilderIterator shape.
# genbaseext (genbaseext_externalGenericBaseConcreteArgs): migrated -> tests/coroutines/fixtures/CorBSequenceTests.kt
# (external generic base AbstractCoroutineContextKey<MyBase,MyDerived> concrete-arg EMIT via MakeGenericType). Its
# incidental get_key Key<Self>/Key<Element> covariance (#12, formal-only) is now baselined in tests/run-ilverify.sh
# (ILVERIFY_XFAIL "CorBGbeBase::get_key()"), not here; case dir + this line + the [genbaseext] XFAIL_ILVERIFY entry removed same-change.

# Reverse interop via an injected C# host: `il_check_inject` builds the sample's runtime.cs into a referenced .NET
# assembly, scans the .kt imports through facadegen, and references it (the same façade-free `import Kfc.X` path the other
# injected-runtime samples use).
# fieldvis/delegatearg/delegobj migrated to the C#-producer NUnit lane tests/interop/consumer
# (InteropAInjectTests.kt: fieldvis; InteropADelegateTests.kt: delegatearg, delegobj) — the former runtime.cs became
# the producer's per-namespace C# source (docs/nunit-migration-playbook.md §3).
# threadlambda (#19): a BARE lambda `{ ... }` into a .NET member overloaded on delegate-typed params — `Thread({...})`
# (ThreadStart/ParameterizedThreadStart) + `Task.Run({...})` (Action/Func<T>) — resolves without ambiguity. facadegen
# marks the Pareto-dominated sibling `lowPriority`; kotc stamps `@kotlin.internal.LowPriorityInOverloadResolution` so the
# bare lambda binds the preferred (ThreadStart/Action) sibling. Import-scan path (BCL, no runtime.cs). FAIL before / PASS after.
# CLR-interop C#-producer batch B (delegnull/injuint/ixname/netattr/netattr-vararg/netenum/netinterop/
# outref/selfref/tloverload/transinj/ubyteinj/vtprop) migrated to the ProjectReference'd C#-producer
# NUnit lane tests/interop/{producer,consumer} (InteropB*Tests.kt; delegnull's NRT producer is
# tests/interop/producer-nrt), gated by tests/run-nunit-il.sh. Per the same-change rule the per-case
# dirs + il_check_inject lines were removed here; each former runtime.cs became the producer's
# per-namespace C# source. (il-stackalloc stays below — its emitted localloc is UNVERIFIABLE, so it
# cannot join the whole-assembly-ilverify'd NUnit consumer lane.)
# injbase/injfqn migrated to the C#-producer NUnit lane tests/interop/consumer/InteropAInjectTests.kt; injstatic ->
# InteropADelegateTests.kt.
# c1net/csext/csextrecv/genextval/eventext migrated to the C#-producer NUnit lane tests/interop/consumer
# (InteropAExtTests.kt: c1net, csext, csextrecv, genextval; InteropAEventTests.kt: eventext) — the former runtime.cs
# became the producer's per-namespace C# source (docs/nunit-migration-playbook.md §3).
# firgap migrated to the C#-producer NUnit lane tests/interop/consumer/InteropAInjectTests.kt.
# CLR-interop C#-producer pilot batch (inherit/geninj/clriface/clrimpl/clrasm/genim) migrated to the
# ProjectReference'd C#-producer NUnit lane tests/interop/{producer,consumer} (InteropTests.kt), gated by
# tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs + il_check_inject lines were
# removed same-change; the former runtime.cs became the producer's per-namespace C# source (docs/nunit-migration-playbook.md §3).
# transinj migrated to tests/interop InteropB (see the batch-B breadcrumb above).
# clriface/clrimpl migrated to the C#-producer NUnit lane tests/interop/consumer/InteropTests.kt (see breadcrumb above).
# cbk/ifacechainvt migrated to the C#-producer NUnit lane tests/interop/consumer (InteropADelegateTests.kt: cbk;
# InteropAInjectTests.kt: ifacechainvt).
# clrifaceimpl -> tests/il/fixtures/MigratedIntropCIfaceImplTests.kt (clrifaceimpl_referenceTypeIfaceImpl); clrifaceimplvt
# (#128) -> same fixture (clrifaceimplvt_valueTypeIfaceSlotBridge — the value-type ValueTypeIfaceSlotBridge sibling), migrated.
# clrasm migrated to the C#-producer NUnit lane tests/interop/consumer/InteropTests.kt (see breadcrumb above).
# genim migrated to the C#-producer NUnit lane tests/interop/consumer/InteropTests.kt (see breadcrumb above).
# ixname/selfref/outref/netattr/netattr-vararg migrated to tests/interop InteropB (see the batch-B breadcrumb above).
il_check_inject stackalloc Sa "$ROOT/cases/il-stackalloc" "$(printf '16\n30\n-1\n10\n21')" SpanRt
# cobuild (coBuild_realTaskDelayAwait): migrated -> tests/coroutines/fixtures/CorATaskAwaitTests.kt (P4 real
# Task.Delay().await() suspensions drained by blockOn); cases/il-cobuild dir + this il_check line removed same-change.
# genasync (genasync_genuineAsyncTaskDelay): migrated -> tests/coroutines/fixtures/TaskAwaitTests.kt (genuine-async
# isolation: suspend fun with Task.Delay().await(), drained by blockOn); cases/il-genasync + this line removed same-change.
# suspendcatch / suspendintrinsic / suspendintrinsicowned / suspendloop / inline-suspend / suspendnestedcapture /
# comaindrain migrated -> tests/coroutines (CorB SuspendCore/Intrinsic/InlineSuspend + CorA ColdMember fixtures); dirs + il_check lines removed same-change.
# counit (counit_unitReturningSuspendTaskBridge): migrated -> tests/coroutines/fixtures/ColdCoreTests.kt (a PUBLIC
# Unit-returning suspend fun -> a NON-generic public `Task` bridge, coroutine-abi.md §1); cases/il-counit + line removed same-change.
# monitordrain -> tests/il/fixtures/MigratedIntropCThreadingTests.kt (monitordrain_waitPulseCrossThreadDrain): the
# System.Threading.Monitor Wait/Pulse cross-thread DRAIN the harness blockOn's BlockOnSink is built on, migrated.
# cofinally (cofinally_finallyRunsExactlyOnce): migrated -> tests/coroutines/fixtures/TaskAwaitTests.kt (bundle-6
# BUG 1: EmitTry gates the finally on $suspending so close() runs EXACTLY ONCE post-resume); cases/il-cofinally + line removed same-change.
# coexc (coExc_exceptionAcrossSuspendBoundary): migrated -> tests/coroutines/fixtures/CorATaskAwaitTests.kt
# (throw after resume / nested frame / faulted Task rethrow); cases/il-coexc dir + this il_check line removed same-change.
# cocancel (#86 P0) / cocancelkt (#105): migrated -> tests/coroutines/fixtures/CorACancelTests.kt (RootContinuation.resumeWith
# completes a .NET OperationCanceledException / a Kotlin CancellationException as a CANCELED Task, not FAULTED; plain
# failure still FAULTS, success still yields the value); cases/il-cocancel + il-cocancelkt dirs + these il_check lines removed same-change.
# corestrict (coRestrict_userRestrictsSuspensionScope): migrated -> tests/coroutines/fixtures/CorAColdMemberTests.kt
# (a hand-authored @RestrictsSuspension receiver driven by receiver-form startCoroutine); cases/il-corestrict dir + line removed same-change.
# suspendco (suspendco_syncResume / suspendco_syncResumeWithException): migrated -> tests/coroutines/fixtures/ColdCoreTests.kt
# (SuspendColdLowering F2 cross-module suspendCoroutine{} + F1 SafeContinuation UNDECIDED/RESUMED cache); cases/il-suspendco + line removed same-change.
# safecontresume / coinline / coevalorder / cofieldorder / coarrayorder / lam1 / lam2 migrated -> tests/coroutines
# (CorA ColdFlow/EvalOrder + CorB SuspendCore/SuspendValue fixtures); dirs + il_check lines removed same-change.
# suspendcapture (suspendcapture_enclosingInstanceCapture) + suspendvalue (suspendvalue_paramValueAndHigherOrder):
# migrated -> tests/coroutines/fixtures/SuspendValueTests.kt (#34a enclosing-instance `__outer` capture; #36 GAP 1/2
# suspend functional-value invoke via startSuspendUninterceptedOrReturn); both cases/il-* dirs + these lines removed same-change.
# suspendref (suspendref_callableReferenceToSuspendFn): migrated -> tests/coroutines/fixtures/CorBSuspendValueTests.kt (#67: a callable reference to a suspend fn `::work`/`d::apply` lowered as a newSuspendLambda adapter); case dir + line removed same-change.
# suspendval2 (suspendval2_storedSuspendValueArityN): migrated -> tests/coroutines/fixtures/CorBSuspendValueTests.kt
# (#38: invoking a stored suspend functional VALUE of arity >= 2 via startSuspendUninterceptedOrReturnN -> create(args, completion)); case dir + line removed same-change.
# inlsuspendcarrier (inlsuspendcarrier_escapingCapturingSuspendLambda) + inlsuspendlaunch (inlsuspendlaunch_suspendLambdaCapturingInlineArgLocal)
# -> tests/coroutines/fixtures/CorBInlineSuspendTests.kt; inlsuspendobj (inlsuspendobj_crossinlineSuspendIntoObjectLiteral)
# -> tests/coroutines/fixtures/CorBFlowTests.kt. BATCH B (#75): a crossinline SUSPEND carrier surviving in a non-invoke
# position, materialized §4.4ii into a real newSuspendLambda VALUE (inlsuspendobj = the former silent-miscompile cell);
# case dirs + these lines removed same-change.
# inlsuspendflow (inlsuspendflow_genericReceiverSuspendMember) + inlsuspendnest (inlsuspendnest_nestedSuspendLambdaUnderTvRemap)
# -> tests/coroutines/fixtures/CorBFlowTests.kt; inlsuspendouter (inlsuspendouter_outerReceiverRebindInPayloadSuspendLambda)
# -> tests/coroutines/fixtures/CorBInlineSuspendTests.kt. BATCH B (#75, 2A/2B): the GENERIC + RECEIVER + suspend-MEMBER
# inline-splice family (kotlinx `flow{}`) — multi-scope tv construction-typeArgs channel + nested-SM tv shield + the
# `__outer` extension-receiver rebind; case dirs + these lines removed same-change.
# flowtransform (flowtransform_nestedCrossinlineCaptureChain): migrated -> tests/coroutines/fixtures/CorBFlowTests.kt
# (rc6 #75 holistic: the cold-SM nested-closure capture family — unsafeFlow/unsafeTransform/filter/map E1-E7, the kotlinx flow port blocker); case dir + line removed same-change.
# inlsuspenddefault (inlsuspenddefault_nestedMemberInlineOmittedLambdaDefault): migrated -> tests/coroutines/fixtures/CorBInlineSuspendTests.kt
# (#43 Batch A×B seam: a §4.4ii suspend carrier nesting a member-inline call that omits a lambda default -> a re-hoisted newDelegate inside the carrier); case dir + line removed same-change.
# inlmatsetcap (inlmatsetcap_refCellWriteThroughMaterializedCarrier): migrated -> tests/coroutines/fixtures/CorBInlineSuspendTests.kt
# (§4.4ii ref-cell write-through: a materialized non-suspend newClosure carrier mutating a captured var must keep working); case dir + line removed same-change.
# bundle-6 ④ stdlib-correctness routing (bir2cir)
# exception / try-catch family (il-exc, il-customexc, il-excmap, il-nestedtry, il-result, il-throwexpr, il-tryexpr,
# il-tryexprop) migrated to the NUnit battery tests/il/fixtures/ExceptionTests.kt (8 methods), gated by
# tests/run-nunit-il.sh; the old per-case dirs + il_check lines were removed in the same change (audit #14).
# #156: a genuinely-nullable String (String? = null) UNWRAPPED into a CharSequence?-receiver slot (isNullOrEmpty) — the
# strict nullable-slot path now emits a runtime-conditional adapter wrap so String->dotkt$CharSequence is ilverify-clean.
# #89/#157: a CROSS-MODULE top-level `val` read (COROUTINE_SUSPENDED, a computed val deserialized from the metadata klib whose
# parent is a package fragment, not an IrFile) is attributed owner:null by kotc — NOT the READING file's class — so bir2cir
# binds the true declaring IntrinsicsKt off the ref.dll via the GENERAL owner-null top-level resolver (prop:get -> get_<name> ->
# TryResolveTopLevelStatic; the accessor is indexed in TopLevelStatics as a file-class static). This is the zero-arg (recvKey="")
# branch — the only reachable instance is COROUTINE_SUSPENDED (klib-package-fragment-only shape, the stdlib's lone public plain
# computed top-level val); the non-coroutine sibling of the SAME general path is il-extprop (extension-property getters, #157).
il_check xmodtopval AppKt "$ROOT/cases/il-xmodtopval/app.kt" "$(printf 'True\nTrue')"

# Reverse interop: a .NET (C#) host loads the IL-emitted Kotlin assembly and calls a Kotlin class + top-level
# fun. Proves the IL output is a consumable .NET assembly. (Compile-time <Reference> needs per-type contract-
# assembly retargeting — blocked by a Reflection.Emit limitation; see design 5.2. Reflection load works today.)
il_revinterop() {
	(
		sample_guard revinterop
		local asm=KotlinLib src="$ROOT/cases/il-revinterop"
		echo "$asm" > "$RESULTS/asm-revinterop"
		local birdir="$ROOT/build/bir-revinterop" ildir="$ROOT/build/il-revinterop"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! "$LAUNCHER" $src/lib.kt -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit revinterop "$ildir" "$asm" "$birdir"; then
			reason="ilemit error"; exit 0; fi
		cp "$src/Program.cs" "$ildir/Program.cs"
		cat > "$ildir/consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
<ItemGroup><Compile Include="Program.cs" /></ItemGroup></Project>
EOF
		local actual expected rc=0; expected="$(printf 'Hi, World\n5')"
		# Capture the C# host's stdout AND its exit status INDEPENDENTLY (issue #163): the run status is no longer
		# lost to the grep pipe / `|| true` that let a consumer print the expected text and THEN throw / return
		# non-zero pass. A non-zero `dotnet run` (build OR execution) is a run crash BEFORE any output compare.
		# #108: bound the consumer run+build too (SIGTERM at the deadline -> exit 124/137, folded into the run-crash record).
		actual="$(timeout -k 5 "${RUN_TIMEOUT}s" dotnet run --project "$ildir/consumer.csproj" -v q -- "$ildir/$asm.dll" 2>"$ildir/run.err")" || rc=$?
		actual="$(printf '%s' "$actual" | grep -vE 'warning|error |\.cs\(' || true)"
		if (( rc != 0 )); then
			reason="run crash (exit $rc)"; detail="$(printf -- '--- expected ---\n%s\n--- actual (before crash) ---\n%s\n--- stderr ---\n%s' "$expected" "$actual" "$(tail -20 "$ildir/run.err" 2>/dev/null)")"; exit 0; fi
		if [[ "$actual" == "$expected" ]]; then ok=1; else mismatch "$expected" "$actual"; fi
	)
}

# ---- issue #163 self-test: the reverse-interop run capture MUST reject a C# host that prints the EXPECTED text
# then returns non-zero. Mirrors il_revinterop's exact capture idiom; a green (exit 0) means the hole is open. ----
il_revinterop_selftest() {
	local d="$ROOT/build/il-revinterop-selftest"; rm -rf "$d"; mkdir -p "$d"
	cat > "$d/Program.cs" <<'EOF'
using System;
class P { static int Main() { Console.WriteLine("SELFTEST-EXPECTED"); throw new Exception("boom after print"); } }
EOF
	cat > "$d/st.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>
EOF
	local rc=0
	dotnet run --project "$d/st.csproj" -v q 2>"$d/run.err" >/dev/null || rc=$?
	rm -rf "$d"
	if (( rc == 0 )); then
		echo "IL GATE RED — #163 reverse-interop self-test FAILED: a print-then-crash consumer was accepted (exit-code hole open)"; exit 1; fi
	echo "SELFTEST revinterop (print-then-crash consumer correctly REJECTED, run exit $rc)"
}

wait   # let every backgrounded sample finish; each has left exactly one result record
il_revinterop_selftest   # after the parallel samples drain (isolated build dir; no contention with in-flight jobs)
il_revinterop   # synchronous; writes its own record like the rest

# ---- aggregate the records: one PASS/FAIL line per sample (sorted), details after the FAIL line ----
run_pass=0; declare -a run_fails=()
for f in "$RESULTS"/run-*; do
	[[ -e "$f" ]] || continue
	cat "$f"
	if [[ "$(head -1 "$f")" == PASS* ]]; then run_pass=$((run_pass+1)); else
		run_fails+=("$(basename "$f" | sed 's/^run-//')"); fi
done

# The refdll handoff: samples with an external runtime dll pass its path to the ilverify phase's -r.
declare -A REFDLL=()
for f in "$RESULTS"/refdll-*; do [[ -e "$f" ]] || continue; REFDLL["$(basename "$f" | sed 's/^refdll-//')"]="$(cat "$f")"; done

# ---- formal IL verification (ilverify) ----
verify_pass=0; declare -a verify_fails=()
ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
REFDIR="$DOTNET_RUNTIME_DIR"
# #107: FAIL LOUD when the formal-verification lane cannot run. Skipping it (the old `else: skipping` branch)
# left verify_fails empty -> the gate exited 0 GREEN with ZERO IL coverage, and worse, xfail_diff then printed
# "FIXED — remove it" for every legitimate XFAIL_ILVERIFY baseline entry (never actually checked). A gate that
# cannot verify MUST SAY SO and exit nonzero — never degrade silently to run-only with a misleading green.
if [[ -z "$ILV" || ! -d "$REFDIR" ]]; then
	echo "IL GATE RED — #107: cannot run the formal-verification (ilverify) lane; refusing to report green with ZERO IL coverage."
	[[ -z "$ILV" ]]    && echo "  ILVerify.dll not found under \$HOME/.dotnet — install it: 'dotnet tool install -g dotnet-ilverify'"
	[[ -d "$REFDIR" ]] || echo "  runtime reference directory missing: REFDIR='$REFDIR'"
	exit 1
fi
echo "--- ilverify ---"
# #99: the ilverify assembly set is DERIVED from the run set, never hand-maintained. Every il_check* worker wrote
# its emitted assembly name to $RESULTS/asm-<name>; we read them all back here so a run sample can NEVER silently
# escape formal verification. (The former hand-copied ASMS map drifted badly — 78+ run-only samples, incl. the
# highest-risk state-machine / generic-field / super-dispatch shapes, had NO ilverify coverage; a sample that ran
# on the current CLR but emitted formally-invalid IL passed green. Deriving from the run set closes that class
# permanently: adding a run sample AUTOMATICALLY adds its ilverify coverage.)
declare -A ASMS=()
for f in "$RESULTS"/asm-*; do [[ -e "$f" ]] || continue; ASMS["$(basename "$f" | sed 's/^asm-//')"]="$(cat "$f")"; done
# DOCUMENTED ilverify EXCLUSIONS — samples whose emitted IL is LEGITIMATELY unverifiable by ECMA-335 (NOT a defect).
# Made LOUD (printed VERIFY-SKIP with a concrete reason), never a silent gap. A runtime-safe finding that DOES reach
# ilverify and fails belongs in XFAIL_ILVERIFY (top of file), NOT here — this map is only for IL ilverify CANNOT check.
declare -A ILVERIFY_EXCLUDE=(
	[stackalloc]="emitted localloc (stackalloc/Span) is UNVERIFIABLE by ECMA-335, like C# unsafe — never passes ilverify (permanent by-design exclusion)"
)
for n in $(printf '%s\n' "${!ILVERIFY_EXCLUDE[@]}" | sort); do
	unset 'ASMS[$n]'; echo "VERIFY-SKIP $n (${ILVERIFY_EXCLUDE[$n]})"
done
for n in $(printf '%s\n' "${!ASMS[@]}" | sort); do
	dll="$ROOT/build/il-$n/${ASMS[$n]}.dll"
	# No dll = the sample failed to compile/emit; that is already a run-lane FAIL, not a hidden ilverify gap.
	[[ -f "$dll" ]] || continue
	# A sample that references an external runtime dll needs it on ilverify's resolve path too.
	refarg=(); [[ -n "${REFDLL[$n]:-}" ]] && refarg=(-r "${REFDLL[$n]}")
	if dotnet "$ILV" "$dll" -r "$REFDIR/*.dll" -r "$STDLIB_RT_DLL" "${refarg[@]}" 2>&1 | grep -qi 'Verified\.'; then
		echo "VERIFY  $n"; verify_pass=$((verify_pass+1))
	else
		echo "VERIFY FAIL  $n"; verify_fails+=("$n")
	fi
done

# ---- verdict: diff the actual fail sets against the XFAIL baseline (lib.sh xfail_diff) ----
echo "--- baseline diff (XFAIL = expected fail; NEW-FAIL = regression; FIXED = prune the xfail entry) ---"
xfail_diff run      XFAIL_RUN      ${run_fails[@]+"${run_fails[@]}"}
xfail_diff ilverify XFAIL_ILVERIFY ${verify_fails[@]+"${verify_fails[@]}"}

echo "------------------------------------"
echo "PASS(run) $run_pass   FAIL(run) ${#run_fails[@]}${run_fails[@]+ [${run_fails[*]}]}"
echo "VERIFY $verify_pass   VERIFY-FAIL ${#verify_fails[@]}${verify_fails[@]+ [${verify_fails[*]}]}"
if (( ${#XFAIL_NEW[@]} )); then
	echo "IL GATE RED — fail name(s) outside the XFAIL baseline: ${XFAIL_NEW[*]}"
	exit 1
fi
echo "IL GATE GREEN (every fail is XFAIL-listed; any FIXED line above means the baseline is stale — prune it)"

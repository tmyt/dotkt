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
	[coctxkey]="GitHub #12 (formal-only follow-up of closed #2): invariant Key<Element> <- Key<Self> companion via star projection; runtime-safe, bir2cir representation follow-up"
	[cointercept]="GitHub #12 (formal-only follow-up of closed #2): invariant Key<Element> <- Key<Self> companion via star projection; runtime-safe, bir2cir representation follow-up"
	[genbaseext]="GitHub #12 (formal-only, same class as coctxkey): the AbstractCoroutineContextKey external-generic-base RUN proves the base-arg fix; the ONLY ilverify finding is the incidental CoroutineContext.Key star-projection covariance (get_key Key<Self> <- invariant Key<Element>); runtime-safe, bir2cir representation follow-up"
	# awaitintercept (#7 Part B) carries the SAME GitHub #2 formal-only finding: its ContinuationInterceptor impl's
	# get_key() returns the Key<Self> companion where invariant Key<Element> is formally expected (StackUnexpected).
	# Runtime-SAFE — the RUN lane PASSES (interceptor precedence at the await resume verified: A:resumes=1). The #7
	# behavior does NOT depend on the #2 fix; only this formal ilverify finding does (same bir2cir/representation follow-up).
	[awaitintercept]="GitHub #12 (formal-only follow-up of closed #2): interceptor get_key() Key<Self> <- invariant Key<Element>; runtime-safe, RUN green, #7 precedence verified"
	# ---- newly EXPOSED by the #99 run-derived-ASMS coverage work (these run-only samples had NO ilverify coverage
	# before; each RUNS green — a runtime-safe formal-only finding attributed to a live tracking issue) ----
	# classdeleg (#174): a class-delegation (#81) forwarder `Tracked<T> : MutableList<T> by backing` narrows the
	# iterator()/listIterator() return to the READ-ONLY Iterator/ListIterator where the Mutable* slot is formally
	# expected. Runtime-safe (the backing MutableList returns a real Mutable* iterator; RUN green), same erased-static
	# -return-type class as #12/#46 — a bir2cir/representation follow-up.
	[classdeleg]="#174: class-delegation (#81) forwarder narrows MutableList iterator()/listIterator() return to read-only Iterator/ListIterator where Mutable* is expected — runtime-safe covariance-erasure (RUN green)"
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
# sample (il-comaindrain blocks a suspend main until it drains). Without a hard bound ONE hung sample wedges the ENTIRE gate — CI then kills the
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
# The optional <nullableMode> (default `disable`) selects the C# NRT context: `enable` emits real [Nullable] byte
# arrays so facadegen's NRT reader (e.g. #150 delegate-arg nullability) is exercised end-to-end.
build_runtime() { # <srcDir> <runtimeAsm> [nullableMode]
	local srcdir="$1" rasm="$2" nullable="${3:-disable}" rt="$ROOT/build/rt-$rasm"
	rm -rf "$rt"; mkdir -p "$rt"
	cp "$srcdir/runtime.cs" "$rt/runtime.cs"
	printf '%s\n' "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>$rasm</AssemblyName><Nullable>$nullable</Nullable></PropertyGroup></Project>" > "$rt/rt.csproj"
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

# Like il_check_inject but builds runtime.cs with C# NRT ENABLED (#150): the sample's own .NET assembly then carries
# real [Nullable] byte arrays, so facadegen surfaces a delegate's nullable type-arg (`Action<string?>`/`Func<string?>`)
# as a nullable Kotlin lambda param/return — a lambda body relying on that nullability (returning null into Func<string?>)
# compiles only when the byte is honored. Everything else is identical to il_check_inject.
il_check_inject_nrt() { # <name> <asm> <srcDir> <expected> <runtimeAsm>
	gate
	(
		sample_guard "$1"
		name="$1"; asm="$2"; src="$3"; expected="$4"; rasm="$5"
		echo "$asm" > "$RESULTS/asm-$name"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"; meta="$ROOT/build/$name.meta"
		refdll="$(build_runtime "$src" "$rasm" enable)"; echo "$refdll" > "$RESULTS/refdll-$name"
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
il_check caprefinline AppKt "$ROOT/cases/il-capref-inline/app.kt" "$(printf '2\n4\n6\n99')"   # a coerced `::pushDouble` reference inside a buildList{} inline lambda -> an ADAPTER_FOR_CALLABLE_REFERENCE local fn whose bound receiver is an ExtensionReceiver param `receiver`; liftLocalFn must emit the receiver param, else the body's `receiver.pushDouble` dangles (the kotlinx flow `__local*_add: references undeclared local 'receiver'` blocker)
il_check adapterref AppKt "$ROOT/cases/il-adapterref/app.kt" "$(printf 'sink 1\nsink 2\nsink 3\nbuilt 4\nbuilt 5')"   # #84 G: a coerced MEMBER reference (`s::add`/`::add`, Boolean-returning member adapted to (Int)->Unit) passed to an inline forEach — the ADAPTER_FOR_CALLABLE_REFERENCE must forward to the real member as callInstance (adapterRef replays the adapter body), not a top-level `callStatic owner:null` (`static method not found: add`, the consumeEach(collection::add) blocker)
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
# taskfam: a same-name .NET arity family — non-generic `Task` and `Task<TResult>` (Kotlin `Task1`) coexist in one
# file; `generic:Task1[T]` cross-refs resolve to the arity-1 definition (docs/dotkt-semantics.md §8d).
il_check_imports taskfam Tf "$ROOT/cases/il-taskfam" "$(printf 'plain=True\ngeneric=42')"
# taskawait (taskawait_syncFastPath): migrated -> tests/coroutines/fixtures/TaskAwaitTests.kt (SuspendColdLowering
# P4 REVERSE bridge, Task.await() sync fast path); its cases/il-taskawait dir + this il_check line removed same-change.
# valueawait (#10): `await` generalized to the .NET AWAITABLE PATTERN beyond Task — a NON-Task BCL awaitable
# `ValueTask<Int>` (MEMBER GetAwaiter -> ValueTaskAwaiter<Int>, no .AsTask()). facadegen pattern-detects it and
# injects `ValueTask1<T>.await()`; bir2cir's EmitAwaitPoint discovers the ValueTaskAwaiter shape from ref metadata
# and emits the SAME dance as Task. SYNC FAST PATH (value-constructed ValueTask is already completed).
il_check_imports valueawait ValueAwait "$ROOT/cases/il-valueawait" "42"
# cfgawait (#3): `await(captureContext = false)` opt-out — bir2cir lowers the await marker to the
# `task.ConfigureAwait(false).GetAwaiter()` awaiter dance (ConfiguredTaskAwaitable.ConfiguredTaskAwaiter STRUCT calls,
# no SynchronizationContext capture). SYNC FAST PATH (non-generic, already-completed Task).
il_check_imports cfgawait CfgAwait "$ROOT/cases/il-cfgawait" "5"
# cfgawaitgen (#3): the GENERIC `Task<Int>.await(captureContext = false)` path — the awaiter is the NESTED
# `ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter` struct whose arity backtick rides the OUTER type, so its FQN already
# carries a `. ilemit's ConstructGeneric must NOT append a SECOND arity suffix. SYNC FAST PATH (already-completed Task<Int>).
il_check_imports cfgawaitgen CfgAwaitGen "$ROOT/cases/il-cfgawaitgen" "10"
# awaitintercept (#7 Part B): await-point resume PRECEDENCE — interceptor > captured SyncContext > inline. A
# ContinuationInterceptor in the coroutine context OWNS the resume (routed via ContinuationImpl.intercepted()
# by SuspendColdLowering.AwaitResumeMethod), taking precedence over #3's SyncContext capture. Deterministic
# single-threaded drive: a TaskCompletionSource await that SUSPENDS, then SetResult inline. Scenario A proves
# the interceptor sees the await resume (resumes=1); B/C prove no-interceptor default (captureContext true/false)
# still resumes correctly over the SUSPEND path (12 / 11) — the #3 non-regression guard.
il_check_imports awaitintercept AppKt "$ROOT/cases/il-awaitintercept" "$(printf 'A:resumes=1 done=True value=42\nB:done=True value=12\nC:done=True value=11')"
# extawait (#10): `await` via a GENERIC EXTENSION GetAwaiter — the WinRT IAsyncOperation<T> shape, proved without the
# WinRT projection. `MyOp<T>` (runtime.cs) is awaitable ONLY through `static MyAwaiter<T> GetAwaiter<T>(this MyOp<T>)`.
# facadegen finds the referenced [Extension] GetAwaiter and injects `MyOp<T>.await()`; bir2cir emits
# `MyOpExtensions.GetAwaiter<Int>(op)` (clrGenericStatic, receiver-type-arg unified). Covers BOTH the sync fast path
# (IsCompleted true) AND a genuine SUSPEND+resume (OnCompleted schedules the continuation on the threadpool).
il_check_inject extawait ExtAwait "$ROOT/cases/il-extawait" "$(printf '8\n42')" KfcExtAwait
# coldcf/coldgen: bir2cir SuspendColdLowering P3 — the cold-core suspend state-machine transform lifted
# from straight-line (P2) to control flow across suspension (if/when via cond-lowering, while/for already
# flat), try/catch with the suspension in the try body (two-level dispatch), a suspend extension fun, and
# the GENERIC SM spike (a generic `suspend fun <T>` -> a generic SM). Sync-completion drain via `main`.
# coctxkey / cointercept: GitHub #2 — a self-ref-bounded `CoroutineContext.Key<E : Element>` star-projected to
# `Key<*>` was lowered by kotc to `Key<object>`, which violates `E : Element` on the CLR. bir2cir's
# StarProjectionBoundLowering repoints `Key<object>` -> `Key<Element>` (get_key methodimpl + the app override
# now match). Currently XFAIL_RUN: full run-green ALSO needs an ilemit fix — the inherited GENERIC
# default-interface-method `get<E : Element>(key: Key<E>)` is forwarded/implemented with E erased to object
# (`Key<object>` again), failing the loader on the subclass / the impl (GitHub #2 part-2, ilemit lane).
il_check coctxkey AppKt "$ROOT/cases/il-coctxkey" "$(printf 'True\nTrue')"
il_check cointercept AppKt "$ROOT/cases/il-cointercept" "True"
il_check coldcf ColdCf "$ROOT/cases/il-coldcf" "$(printf '11\n12\n3\n1\n2\n99\n32\n101\n-1\n42')"
il_check coforarray CoForArray "$ROOT/cases/il-coforarray" "$(printf '63\n63\n9')"
il_check coldgen ColdGen "$ROOT/cases/il-coldgen" "$(printf '7\nyo\n8\nhi')"
# coldinst: bir2cir SuspendColdLowering P3 wave-2a — INSTANCE suspend members (the SM carries a `$this`
# field; the cold entry is an instance `<name>$dotkt_suspend` on the class) + MEMBER/cross-file suspend
# CALLS (a `callInstance` suspendCall / a same-assembly cross-file top-level suspend call, rewritten to
# the callee's cold shape — resolvable by construction under R1's unconditional cold-entry declaration). INST1 (Counter.bump) + INST2
# (Svc.chain -> this.helper()) + INSTGEN (generic Box<T>.get) + MCALL1 (topUse -> c.bump()) + MCALL2
# (crossFileVal, a suspend fun in a second source file). Sync-completion drain via `main`.
il_check coldinst ColdInst "$ROOT/cases/il-coldinst" "$(printf '11\n12\n10\n42\nhi\n101\n7')"
# coldvirt: bir2cir P5 A1b — a suspending instance member of a GENERIC class (Box<T>) over the cold core: the SM
# `Box_getTwice$sm[T]` is generic over the enclosing T, its `$this` field is the constructed self, and the awaited
# value crosses the suspension typed in T. Drained by the synthesized plain `main` (sync completion). Runs -> 42,hi.
il_check coldvirt ColdVirt "$ROOT/cases/il-coldvirt" "$(printf '42\nhi')"
# coldsuper: bir2cir SuspendColdLowering (#78/#90) — a suspend `callInstance` keyed on a SUBCLASS static receiver
# (kotc emits ownerType=Derived / ownerType=ChannelImpl) resolves against a suspend member declared on a SUPERTYPE
# (Base.awaitInternal — the JobSupport.awaitInternal shape; ChannelBase/Source.receiveOrNull — the ReceiveChannel
# super-interface shape). Under R1 every super-declared suspend member has a virtual cold entry, so native virtual
# dispatch through the cold slot resolves the inherited call (no bir2cir hierarchy walk). Also guards a MUTUALLY-recursive
# suspend pair (ping/pong) — both cold entries exist by unconditional declaration. Runs -> 11,42,5.
il_check coldsuper ColdSuper "$ROOT/cases/il-coldsuper" "$(printf '11\n42\n5')"
# coroutinectx: bir2cir SuspendColdLowering #79 — the `suspend inline val coroutineContext` read (a stdlib
# throw-only intrinsic getter) bound to `<current continuation>.get_context()`: the SM itself in an SM body, the
# `completion` param in a no-SM body-direct cold entry, and the SM (not `$this`) in a suspending instance member.
# Before the binding it reached ilemit as the bogus `<fileclass>.get_coroutineContext` (method-not-found). Runs ->
# the three contexts' EmptyCoroutineContext toString + the appended ints.
il_check coroutinectx CoroutineCtx "$ROOT/cases/il-coroutinectx" "$(printf 'EmptyCoroutineContext1\nEmptyCoroutineContext\nEmptyCoroutineContext2')"
# coldabstract: bundle-6 ① BUG 3 — an abstract-CLASS suspend member's full vtable. Base emits an abstract cold
# entry + an abstract Task<Int> bridge ([KotlinFunction(Suspend)]); Impl overrides both in lockstep; `b.poll()`
# (b: Base) dispatches virtually through the cold entry. Runs sync -> 42 (no await, so ilverify-clean).
# (il_check_IMPORTS: the co-compiled dotkt.support blockOn harness imports System.Threading.Monitor -> facadegen.)
il_check_imports coldabstract ColdAbstract "$ROOT/cases/il-coldabstract" "42"
# ifacesuspend: bundle-6 ③ — the INTERFACE half of the abstract/interface suspend round-trip. kotc now tags an
# interface `suspend fun` member with the neutral `"suspend":true`+`resultType` FACT (mirroring the abstract-CLASS
# path), so bir2cir can synthesize the interface cold entry / Task<Int> bridge; Fetcher42 overrides both; `f.fetch()`
# (f: Fetcher) dispatches virtually through the interface cold entry. Runs sync -> 42.
il_check_imports ifacesuspend IfaceSuspend "$ROOT/cases/il-ifacesuspend" "42"
# R1 (#90/#101/#100) — the "declaration is unconditional" cold-entry ABI: every declared suspend member gets a cold
# entry + bridge, so an inherited/interface/static/DIM suspend callee resolves by construction (no fixpoint drop).
# coldsubiface: an interface suspend member called through a SUBTYPE static receiver (drive(p: NumberProducer)).
il_check_imports coldsubiface ColdSubIface "$ROOT/cases/il-coldsubiface" "42"
# coldbaseinherit: a base-class-declared suspend fun called via a SUBCLASS receiver, NOT overridden — native virtual
# dispatch through the base's virtual cold slot resolves the inherited member (the R1 win, no bir2cir hierarchy walk).
il_check_imports coldbaseinherit ColdBaseInherit "$ROOT/cases/il-coldbaseinherit" "42"
# coldstaticmember: a COMPANION suspend member (M3) — kotc promotes it to a static method on the outer class; cold
# entry/bridge stay static, the static SM drives a suspend call to a top-level fun.
il_check_imports coldstaticmember ColdStaticMember "$ROOT/cases/il-coldstaticmember" "42"
# colddimgen: a DEFAULTED generic-interface suspend method (a DIM, the Channel<E>.receiveOrNull shape) that
# suspend-calls an abstract sibling member through `this` — segments into a generic SM (Source<E> $this).
il_check_imports colddimgen ColdDimGen "$ROOT/cases/il-colddimgen" "42"
# seqyieldall: yieldAll E2E over the cold core — bir2cir cold-call `sig` disambiguates SequenceScope.yieldAll's
# three same-named `$dotkt_suspend` overloads + ilemit sig-driven external-generic resolution (both landed).
il_check seqyieldall SeqYieldAll "$ROOT/cases/il-seqyieldall" "$(printf 'a,b,c')"
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
# A generic cold-sequence SM: `fun <T> wrap(x) = sequence { yield(x) }.toList()` over a VALUE element (Int) and a
# reference element (String). Guards the `T?`-property `nextValue as T` double-unbox NRE (bir2cir erased-getter
# call-site retype) that broke every value-typed cold sequence, and (via the same drive) the RingBuffer path.
il_check genseq GenSeq "$ROOT/cases/il-genseq" "$(printf '[5]\n[hi]')"
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
il_check classdeleg AppKt "$ROOT/cases/il-classdeleg/app.kt" "$(printf 'p1\n1\np2\nc[p2]\n2\np40\n40\n3\nc')"   # #81: CLASS delegation `class Foo : Bar by baz` — the frontend's synthetic `$$delegate_0` IrField + its ctor initializer must be emitted (single/two/expr/generic delegates)
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
# genbaseext: a NON-GENERIC object over an EXTERNAL (stdlib) generic base with CONCRETE args
# (`object : AbstractCoroutineContextKey<MyBase, MyDerived>` — the kotlinx.coroutines CoroutineDispatcher.Key
# shape, the rc6 port blocker). kotc used to emit the base as an OPEN bare name, dropping the concrete args, so
# ilemit failed EMIT-time "cannot resolve .NET type kotlin.coroutines.AbstractCoroutineContextKey"; kotc now emits
# the base's real args (`ownerSpec`) and ilemit ResolveType(`AbstractCoroutineContextKey`2`).MakeGenericType's it.
# RUN-green proves emit resolved. Its ONLY ilverify finding is the INCIDENTAL CoroutineContext.Key star-projection
# covariance (get_key returns Key<Self> where invariant Key<Element> is expected) — the SAME runtime-safe #12 class
# as coctxkey/cointercept (unrelated to the base-arg fix), so it is XFAIL_ILVERIFY-listed.
il_check genbaseext AppKt "$ROOT/cases/il-genbaseext/app.kt" "ok"

# Reverse interop via an injected C# host: `il_check_inject` builds the sample's runtime.cs into a referenced .NET
# assembly, scans the .kt imports through facadegen, and references it (the same façade-free `import Kfc.X` path the other
# injected-runtime samples use). fieldvis: a .NET host reflects a DotKt-emitted property's CLR accessor visibility.
il_check_inject fieldvis FieldVis "$ROOT/cases/il-fieldvis" "$(printf '150\nme\nPrivate\nPublic')" KfcFv
il_check_inject delegatearg Dlg "$ROOT/cases/il-delegatearg" "$(printf '42\n20\n81')" KfcDel
# delegobj (#1): override a BCL virtual whose delegate param has an `object`/Any? Invoke arg. facadegen surfaces the
# delegate as a function type `(Any?) -> Unit` (not bare Any?), so the Kotlin override matches instead of
# `error: 'Post' overrides nothing`.
il_check_inject delegobj Dobj "$ROOT/cases/il-delegobj" "$(printf 'posted: 42\nbase-typed: 7')" KfcDelObj
# threadlambda (#19): a BARE lambda `{ ... }` into a .NET member overloaded on delegate-typed params — `Thread({...})`
# (ThreadStart/ParameterizedThreadStart) + `Task.Run({...})` (Action/Func<T>) — resolves without ambiguity. facadegen
# marks the Pareto-dominated sibling `lowPriority`; kotc stamps `@kotlin.internal.LowPriorityInOverloadResolution` so the
# bare lambda binds the preferred (ThreadStart/Action) sibling. Import-scan path (BCL, no runtime.cs). FAIL before / PASS after.
# delegnull (#150): a delegate type-arg's NRT byte survives into the Kotlin lambda param/return. The runtime.cs is
# built with C# NRT ENABLED (il_check_inject_nrt), so `Func<string?>`/`Action<string?>` carry real [Nullable] bytes;
# facadegen threads them into the fn node (contravariant sibling of #143). A lambda returning null into `Func<string?>`
# compiles only when the return surfaces as `String?` — the case would COMPILE-ERROR before the fix.
il_check_inject_nrt delegnull DlgNull "$ROOT/cases/il-delegnull" "$(printf '<null>\nhello\nworld\n<n>\nx')" DlgNrtRt
il_check_inject netenum NetEnum "$ROOT/cases/il-netenum" "$(printf '60\n6\nabbccc')" KfcNetEnum
il_check_inject injbase InjBase "$ROOT/cases/il-injbase" "placed:0" KfcInjB
il_check_inject injfqn InjFqn "$ROOT/cases/il-injfqn" "42" KfcInjF
il_check_inject injstatic InjStatic "$ROOT/cases/il-injstatic" "$(printf 'p=42\n7\n99\n123\np=42\n7\n99\n123')" KfcStatic
il_check_inject injuint InjUint "$ROOT/cases/il-injuint" "$(printf '65542\n42')" Boot
# ubyteinj: .NET-interop STRICT byte mapping (#53) — facadegen maps System.Byte->UByte and byte[]->UByteArray, so a
# .NET byte 200 reads as UByte 200 (not signed -56) and a byte[] surfaces as a native UByteArray (round-trip fidelity).
il_check_inject ubyteinj UByteInj "$ROOT/cases/il-ubyteinj" "$(printf '200\n3\n250\n200\n253')" Bt
# c1net consumes types from its OWN runtime.cs (Probe assembly) via `import Probe.X` -> il_check_inject (build the
# runtime, scan the imports through facadegen, --ref it). The old no-import-scan @Clr-facade path is gone.
il_check_inject c1net C1Net "$ROOT/cases/il-c1net" "$(printf '42\nhi\n10\n15\n105\n52\n21\n41\n117\n20\n5\nyo!')" Probe
# csext (#137, Avalonia report B): C#-origin `[Extension]` static methods (`static int Twice(this W w)`) surface as
# TOP-LEVEL Kotlin extension functions (`fun W.Twice(): Int`) so `w.Twice()` resolves via `import Interop.*` — the
# Kotlin analog of C# `using Interop;`. Covers non-generic, extra-param, and GENERIC (`fun <T> Box<T>.Echo(): T`)
# receivers. The whole Avalonia startup/render surface (UsePlatformDetect/StartWithClassicDesktopLifetime/…) is
# namespace-imported extension methods, so this is the enabling seam. (The `import Owner.member` form is in il-c1net.)
il_check_inject csext CsExt "$ROOT/cases/il-csext" "$(printf '42\n22\nhi')" CsExtRt
# csextrecv (#144): TWO static classes in ONE namespace each declare a SAME-NAME, SAME-ARITY `[Extension]` method on a
# DIFFERENT receiver type (`FooExt.Tag(this Foo)` + `BarExt.Tag(this Bar)`, the Avalonia parallel `*Extensions` shape).
# facadegen injects both as `CallableId(Interop, Tag)`; the top-level file-class disambiguation must pick by the RESOLVED
# callee's extension-RECEIVER type, not arity alone (which collides here) — else `bar.Tag()` silently binds to FooExt
# (wrong static, wrong result). Guards `clrInjectedTopLevelFileClass` receiver-keying + `injectedExtReceiverFqn`.
il_check_inject csextrecv CsExtRecv "$ROOT/cases/il-csextrecv" "$(printf '11\n120\n30\n15\n4\n1007')" CsExtRecvRt
# genextval (#157): an inferred `val c = Interop.Cell(40)` over a facadegen-injected GENERIC `Cell<T>` whose ctor param
# is an un-annotated type variable (`T v` -> meta `oblivious(Tv)`) must construct `Cell<int32>`, NOT `Cell<Nullable<int32>>`.
# ClrTypeInjection resolves an oblivious TYPE-VARIABLE to a bare `T` (not a `ConeFlexibleType` that biases the value arg
# nullable), so a NON-generic extension pinned to `Cell<int>` (`Peek(this Cell<int>)`) binds its `__self` receiver to the
# SAME reified `Cell<int32>` instantiation and reads the stored 40 (-> 41), instead of garbage off a Nullable<int32> field.
il_check_inject genextval GenExtVal "$ROOT/cases/il-genextval" "$(printf '40\n41')" GenExtValRt
# N6: STATIC events subscribe via `+=`/`-=` — on a `static class`/`object` (an object member, the Console.CancelKeyPress
# shape) and on a normal class (a companion property, the TaskScheduler.UnobservedTaskException shape). facadegen
# surfaces both as `ClrEvent<T>` properties; bir2cir binds the operator to the event's STATIC add/remove accessor.
# Regression guard: static events were absent (GetEvents was Public|Instance non-static only).
il_check_inject eventext EventExt "$ROOT/cases/il-eventext" "$(printf 'ping: 3\nping: 7\nannounce: hi\nannounce: yo\nh: yo\nannounce: bye')" EvLib
# N5: same-name same-package top-level overloads restored from DIFFERENT .NET file facades (UtilsKt.foo() /
# HelpersKt.foo(Int)) share CallableId(N5,"foo"); the A2 flat map collapsed to last-put-wins. The overload-aware key
# routes each to its own file class by the resolved callee's arity. (A2 regression guard.)
il_check_inject tloverload TlOverload "$ROOT/cases/il-tloverload" "$(printf '100\n42')" N5Lib
# vtprop: setting a MUTABLE property/field on a .NET value-type (struct) local via clrPropSet — the setter/stfld must
# run on the struct's ADDRESS (ldloca), not a spilled copy, or the mutation is lost (pre-fix: `ldloc` + `call instance
# set_V` on a value-type value = invalid IL -> segfault). Regression guard for the value-type-receiver property-set fix.
il_check_inject vtprop VtProp "$ROOT/cases/il-vtprop" "$(printf '10\n20\n30')" ProbeVt
# I4 remnants battery: .NET enum (read/pass/==/when), generic delegates (Func<int,int> + custom Mapper<T>),
# nullable value types (int?/double? both directions).
il_check_inject netinterop NetInterop "$ROOT/cases/il-netinterop" "$(printf 'Green\n4\nTrue\nfresh\ncool\n15\n18\n42\n0\n7\n0\n1.5')" I4Probe
il_check_inject firgap FirGap "$ROOT/cases/il-firgap" "$(printf '42\n60\n3\n20')" P
# CLR-interop C#-producer pilot batch (inherit/geninj/clriface/clrimpl/clrasm/genim) migrated to the
# ProjectReference'd C#-producer NUnit lane tests/interop/{producer,consumer} (InteropTests.kt), gated by
# tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs + il_check_inject lines were
# removed same-change; the former runtime.cs became the producer's per-namespace C# source (docs/nunit-migration-playbook.md §3).
# (3)+(6): constructed-generic MEMBER types (IList<T>/IReadOnlyList<T>/Dictionary<K,V>/IEnumerable<T>) + the
# transitive injection closure (Gadget/Sprocket are never imported — reached via member-signature hops).
il_check_inject transinj TransInj "$ROOT/cases/il-transinj" "$(printf '1\nw1\n1\nw1\nw1!\n3\nw1\nw1.')" TxRt
il_check_inject cbk Cbk "$ROOT/cases/il-cbk" "$(printf '=v42\nran')" PCbk
# clriface/clrimpl migrated to the C#-producer NUnit lane tests/interop/consumer/InteropTests.kt (see breadcrumb above).
# ifacechainvt (#129): a Kotlin class implements an injected .NET interface whose BASE-INTERFACE CHAIN carries a
# value-type generic slot (`IMid<Int> : IBase<Int>`). #128's value-type-generic-interface slot bridge must hold across
# the transitively-inherited base link — the inherited `Get(): Int` and the direct `Rank(Int): Int` both use bare
# int32 slots (not Nullable<int>). Direct + upcast-to-IMid<Int> dispatch.
il_check_inject ifacechainvt IfaceChainVt "$ROOT/cases/il-ifacechainvt" "$(printf '21\n10\n23')" ChainRt
# clrifaceimpl -> tests/il/fixtures/MigratedIntropCIfaceImplTests.kt (clrifaceimpl_referenceTypeIfaceImpl); clrifaceimplvt
# (#128) -> same fixture (clrifaceimplvt_valueTypeIfaceSlotBridge — the value-type ValueTypeIfaceSlotBridge sibling), migrated.
# ixname: a .NET type with a CUSTOM-NAMED indexer via [IndexerName("Cell")] — `g[i]`/`g[i]=v` must bind to
# get_Cell/set_Cell (read from the type's DefaultMemberAttribute by bir2cir.NetInteropBinding.DefaultIndexerAccessor),
# not the hardcoded get_Item/set_Item. Regression guard for the custom-indexer-name binding path.
il_check_inject ixname IxName "$ROOT/cases/il-ixname" "$(printf '10\n30\n99')" IxRt
# clrasm migrated to the C#-producer NUnit lane tests/interop/consumer/InteropTests.kt (see breadcrumb above).
il_check_inject selfref SelfRef "$ROOT/cases/il-selfref" "4" PSelf
# genim migrated to the C#-producer NUnit lane tests/interop/consumer/InteropTests.kt (see breadcrumb above).
il_check_inject outref Outref "$ROOT/cases/il-outref" "$(printf 'ok=5\nfail\n2 1\n20\n20\n109\n5\n7 5')" OutR
il_check_inject netattr NetAttr "$ROOT/cases/il-netattr" "$(printf 'widget#7\n42')" Lbl
il_check_inject netattrvararg NetAttrVararg "$ROOT/cases/il-netattr-vararg" "$(printf 'widget#7\n42')" PVararg   # #184: params object[] ctor applied bare (zero args). rasm distinct from firgap's `P` (both namespace-P but different types) so the parallel build_runtime does not race on the shared build/rt-P dir (assembly NAME only; the `P` namespace its runtime.cs declares is unchanged, so `import P.TagAttribute` still resolves)
il_check_inject stackalloc Sa "$ROOT/cases/il-stackalloc" "$(printf '16\n30\n-1\n10\n21')" SpanRt
# cobuild: the GENUINE .NET-async E2E — `Task.Delay(1).await()` truly suspends (imports System.*, so
# il_check_IMPORTS runs facadegen for the await marker). bir2cir's P4 await lowering + the whole cold-core
# SM chain are verified correct; the boxed-enum COROUTINE_SUSPENDED reference-identity issue (once the sole
# remaining fail) is fixed by caching the box (Intrinsics.kt), so this now runs green -> 25 (no XFAIL).
il_check_imports cobuild Cob "$ROOT/cases/il-cobuild" "25"
# genasync (genasync_genuineAsyncTaskDelay): migrated -> tests/coroutines/fixtures/TaskAwaitTests.kt (genuine-async
# isolation: suspend fun with Task.Delay().await(), drained by blockOn); cases/il-genasync + this line removed same-change.
il_check_imports suspendcatch SuspendCatch "$ROOT/cases/il-suspendcatch" "$(printf '10\n99\n103\n200\n300')"   # #78 Defect B: a suspend call INSIDE a catch handler (Select.kt:723 recoverAndThrow shape) — HoistSuspendingCatches lifts the handler out of the CLR catch clause so the SM can segment its suspension; the try body ALSO suspends (two-level dispatch) + multi-catch (both handlers suspend, per-clause capture)
il_check_imports suspendintrinsic SuspendIntrinsic "$ROOT/cases/il-suspendintrinsic" "42"   # #80: a direct user read of the top-level val COROUTINE_SUSPENDED in a suspendCoroutineUninterceptedOrReturn block — canonicalized to the SM's Suspended() marker in Rewrite (mis-owned by MemberCallSubstitution to the file class otherwise)
il_check suspendintrinsicowned AppKt "$ROOT/cases/il-suspendintrinsicowned/app.kt" "42"   # #157 (was #80-residual): a NON-suspend member (getResult shape) reads the top-level val COROUTINE_SUSPENDED — post-#89 kotc emits owner:null + prop:get (like every cross-module top-level val), and bir2cir binds it through the GENERAL owner-null resolver (prop:get -> get_COROUTINE_SUSPENDED -> TryResolveTopLevelStatic single-candidate -> IntrinsicsKt), NOT a COROUTINE_SUSPENDED special-case (that band-aid was deleted as redundant)
il_check_imports suspendloop SuspendLoop "$ROOT/cases/il-suspendloop" "$(printf '12\n18\n6')"   # #82: a structured collection loop (forArray + forEachInline) whose body spans a suspension — FlattenSuspendingLoops flattens it to CFG so the loop temps/element cross the resume as SM fields (else `load unknown var __inlsN$element`); + break/continue crossing the resume
# inlsuspend: #75 S4a §8.7 arm (c) — a suspend call inside a NON-suspend-typed inline-arg lambda (repeat{tick()},
# let{tick()}) splices into work()'s state machine (the delegate path would trap the await in a non-suspend closure).
il_check_imports inlsuspend InlSuspend "$ROOT/cases/il-inline-suspend" "21"
# suspendnestedcapture: #22 — a `suspend inline fun` with a `crossinline` block that NESTS a lambda capturing an
# enclosing binding (the `suspendCancellableCoroutine { cont -> cont.invokeOnCancellation { … } }` shape). bir2cir's
# §4.4ii MaterializeCarrier now allows a nested `newClosure`/`newSam` in the carrier (its captures — an invoke param
# `cont` / a carrier capture `h` -> `this.field` — are rewritten by the descending sibling scans), instead of the old
# blanket HasNestedClosure fail-loud that blocked the kotlinx.coroutines-core port. RESIDUAL (capFE/capMap/capFEI): the
# `cont` capture reaching the block through an inner inline-EXTENSION iterator (`forEach`/`map`/`forEachIndexed`, receiver
# `Array<T>`) — which splices to a `forArray` loop whose element binds in the node's `"var"` field — now counts that loop
# binder as a declared local (CollectDeclaredLocals), so the element ref is no longer flagged an unlisted stray capture.
il_check_imports suspendnestedcapture SuspendNestedCapture "$ROOT/cases/il-suspendnestedcapture" "$(printf '5\n42\nhi\n7\n7\n50\n100\n70\n80')"
# comaindrain: bundle-6 ① BUG 4 — a GENUINELY-suspending `suspend fun main` (awaits Task.Delay). bir2cir's
# DrainMain now drives the cold body under a REAL RootContinuation<Unit>/TaskCompletionSource<Unit> and
# BLOCKS on tcs.Task until the threadpool resume completes (the old null completion NRE'd on resume). RUNS
# correct -> start,42; carries the same TaskAwaiter CallVirtOnValueType ilverify formal-only finding as genasync.
il_check_imports comaindrain ComainDrain "$ROOT/cases/il-comaindrain" "$(printf 'start\n42')"
# counit (counit_unitReturningSuspendTaskBridge): migrated -> tests/coroutines/fixtures/ColdCoreTests.kt (a PUBLIC
# Unit-returning suspend fun -> a NON-generic public `Task` bridge, coroutine-abi.md §1); cases/il-counit + line removed same-change.
# monitordrain -> tests/il/fixtures/MigratedIntropCThreadingTests.kt (monitordrain_waitPulseCrossThreadDrain): the
# System.Threading.Monitor Wait/Pulse cross-thread DRAIN the harness blockOn's BlockOnSink is built on, migrated.
# cofinally (cofinally_finallyRunsExactlyOnce): migrated -> tests/coroutines/fixtures/TaskAwaitTests.kt (bundle-6
# BUG 1: EmitTry gates the finally on $suspending so close() runs EXACTLY ONCE post-resume); cases/il-cofinally + line removed same-change.
# coexc: exception propagation ACROSS a suspended Task boundary (POLISH family-6 coverage). (a) a throw AFTER a
# genuine Task.Delay().await() crosses the resume boundary -> blockOn rethrows into the caller's try/catch;
# (b) a throw across a NESTED suspend frame propagates up the resumeWithException chain; (c) awaiting a FAULTED
# .NET Task rethrows its fault at await's GetResult. All three -> caught. (imports System.* -> facadegen.)
il_check_imports coexc CoExc "$ROOT/cases/il-coexc" "$(printf 'caught: boom\ncaught2: nested\ncaught3: faulted\ndone')"
# cocancel (#86 P0): the Task<T> bridge (RootContinuation.resumeWith) completes a CANCELLED result
# (OperationCanceledException) as a CANCELED Task (TrySetCanceled -> IsCanceled), not a FAULTED one; a plain
# failure still FAULTS and success still yields the value. Drives resumeWith directly (the exact bridge resume
# path) since await can't distinguish canceled from faulted-with-OCE — both rethrow OCE; only the state differs.
il_check cocancel CoCancel "$ROOT/cases/il-cocancel" "$(printf 'True\nFalse\nFalse\nTrue\n42')"
# cocancelkt (#105): the SAME bridge sink for Kotlin's OWN kotlin.coroutines.cancellation.CancellationException,
# which extends IllegalStateException on CLR (NOT a .NET OperationCanceledException) — a coroutine that completes
# by throwing Kotlin CE must yield a CANCELED Task (IsCanceled), not a FAULTED one. Drives resumeWith directly
# (as cocancel does): a plain IllegalStateException (the CE supertype) still FAULTS, proving the `is CancellationException`
# clause is specific and does NOT over-broaden to every ISE; success still yields the value.
il_check cocancelkt CoCancelKt "$ROOT/cases/il-cocancelkt" "$(printf 'True\nFalse\nFalse\nTrue\n7')"
# corestrict: a USER-DEFINED @RestrictsSuspension receiver (POLISH family-6 coverage) driven by the receiver-form
# startCoroutine. Confirms the restriction (a frontend concern) doesn't perturb the cold lowering. Pinned + fixed a
# bir2cir bug: a synchronous Unit-returning scope member's DIRECT cold entry fell off with no return value
# (ColdEntryDirect now appends the trailing `return Unit`, mirroring the SM branch). Runs -> 1,2,3,4,5 / 5 / a-b.
il_check corestrict CoRestrict "$ROOT/cases/il-corestrict" "$(printf '1,2,3,4,5\n5\na-b')"
# suspendco (suspendco_syncResume / suspendco_syncResumeWithException): migrated -> tests/coroutines/fixtures/ColdCoreTests.kt
# (SuspendColdLowering F2 cross-module suspendCoroutine{} + F1 SafeContinuation UNDECIDED/RESUMED cache); cases/il-suspendco + line removed same-change.
# #142: a suspendCoroutine whose SafeContinuation is resumed ASYNCHRONOUSLY from a worker thread — the
# UNDECIDED->SUSPENDED (getOrThrow) and SUSPENDED->RESUMED (resumeWith) transitions genuinely race across threads,
# which the fix's Interlocked.CompareExchange CAS over the @Volatile state field makes atomic. blockOn drives the
# cold core; 42 is only observed if the cross-thread resume lands through the CAS. Uses the dotkt.support harness.
il_check_imports safecontresume AppKt "$ROOT/cases/il-safecontresume" "42"
# coinline (#22): a `suspend inline fun` with a `crossinline` lambda invoked inside `suspendCoroutineUninterceptedOrReturn`
# (the kotlinx `suspendCancellableCoroutine` shape). InlineSplice materializes the crossinline carrier as a newClosure the
# intrinsic block captures; the cold lowering cold-transforms the inline WRAPPER's standalone body (app-build gate) + the
# caller holding that materialized closure, prunes the dead intrinsic-block closure class, canonicalizes a direct
# COROUTINE_SUSPENDED block-return, and arms the unintercepted state label BEFORE the block so a sync `cont.resume(v)` does
# not recurse. caller()=5, other()=37 -> 42, two sync-resume suspensions sequenced in one `suspend fun main` SM.
il_check coinline CoInline "$ROOT/cases/il-coinline" "42"
# coevalorder: bundle-6 ① BUG 2 — strict left-to-right eval across a suspension. In `side() + g()` (g
# suspend), bir2cir now spills the impure LEFT operand into an SM field BEFORE g()'s suspension segments
# so its side effect (println "L") happens before g()'s ("G"). Before the fix: G,L; after: L,G,3.
il_check coevalorder CoEvalOrder "$ROOT/cases/il-coevalorder" "$(printf 'L\nG\n3')"
# cofieldorder: N4 (final-review) — the FIELD-read variant of the eval-order bug. In `x + bump()` where bump()
# is a suspend call that MUTATES the member field x, a raw `field`/`@ClrField` read was mis-classed PURE, left
# inline, and evaluated AFTER the suspension resumed -> observed the post-mutation value (105 not 15). bir2cir
# now spills a field read left of a suspension into an SM temp before the suspension. Runs -> 15 (10+5), then 100.
il_check cofieldorder CoFieldOrder "$ROOT/cases/il-cofieldorder" "$(printf '15\n100')"
# coarrayorder: N4-sibling — the ARRAY-ELEMENT variant of the eval-order bug. In `a[0] + bump(a)` where bump()
# is a suspend call that MUTATES a[0], an `arrayGet`/`clr.ldelem` read was mis-classed PURE (N4 only fixed the
# field read), left inline, and evaluated AFTER the suspension resumed -> observed 100 (105 not 15). `a` is a
# plain LOCAL to isolate the array read from the already-impure property-getter path. bir2cir now spills the
# element read left of a suspension into an SM temp typed from `elem` (kotlin.Int, no box). Runs -> 15 (10+5).
il_check coarrayorder CoArrayOrder "$ROOT/cases/il-coarrayorder" "15"
# lam1/lam2: bundle-6 P3 wave-2b — the suspend-LAMBDA payoff. kotc emits `suspendLambdaNew`, bir2cir builds
# the SuspendLambda SM, and the dotkt.support blockOn harness drives it to completion on the cold core.
# (il_check_IMPORTS: the co-compiled harness imports System.Threading.Monitor -> facadegen injects it.)
il_check_imports lam1 Lam1Kt "$ROOT/cases/il-lam1" "42"
il_check_imports lam2 Lam2Kt "$ROOT/cases/il-lam2" "15"
# suspendcapture (suspendcapture_enclosingInstanceCapture) + suspendvalue (suspendvalue_paramValueAndHigherOrder):
# migrated -> tests/coroutines/fixtures/SuspendValueTests.kt (#34a enclosing-instance `__outer` capture; #36 GAP 1/2
# suspend functional-value invoke via startSuspendUninterceptedOrReturn); both cases/il-* dirs + these lines removed same-change.
il_check_imports suspendref AppKt "$ROOT/cases/il-suspendref" "$(printf '6\n40')"   # #67: a callable reference to a `suspend` function (top-level `::work` + bound member `d::apply`) lowered as a `newSuspendLambda` adapter (bir2cir builds the SuspendLambda SM); kotc emits only the suspend FACTS — was a whole-compile abort (KSuspendFunctionN type-token leak + no suspend-newDelegate lowering)
# suspendval2: #38 — invoking a suspend functional VALUE of arity >= 2 (SuspendFunctionN, N>=2). The fixed
# create()/create(value) slots cover 0/1; N>=2 boxes the args into Array<Any?> and drives the value through
# `startSuspendUninterceptedOrReturnN` -> the SM's create(args, completion) override. Covers arity-2 param/local
# values (run2/local2) + an arity-3 capturing lambda (run3).
il_check_imports suspendval2 Sv2Kt "$ROOT/cases/il-suspendval2" "$(printf '42\n42\n42')"
# BATCH B (#75) — the SUSPEND carrier-value contract for the inline splicer. inlsuspendcarrier: an inline fn with a
# crossinline SUSPEND param builds a capturing suspend lambda (referencing the param + a value param) passed to a
# NON-inline fn (blockOn) — retires the payload-newSuspendLambda fail-loud guard + exercises joint-hygiene descriptor
# rewrite. inlsuspendobj: the FORMER SILENT-MISCOMPILE cell — a crossinline SUSPEND lambda captured by an `object :`
# literal, materialized by MaterializeCarrier's suspend arm into a real newSuspendLambda VALUE (was a plain newClosure
# delegate). inlsuspendlaunch: a coroutine-builder suspend lambda inside an inline-call lambda arg capturing that arg's
# own local — retires the carrier-side descriptor guard. All drive the suspend body end-to-end (value MUST be correct).
il_check_imports inlsuspendcarrier AppKt "$ROOT/cases/il-inlsuspendcarrier" "$(printf '42\n42\n7')"
il_check_imports inlsuspendobj AppKt "$ROOT/cases/il-inlsuspendobj" "$(printf 'True\nFalse\nTrue')"
il_check_imports inlsuspendlaunch AppKt "$ROOT/cases/il-inlsuspendlaunch" "$(printf '42\n10')"
# BATCH B (#75, 2A/2B) — the GENERIC + RECEIVER + suspend-MEMBER inline-splice family (kotlinx `flow{}`, 51 sites) +
# the `__outer` extension-receiver rebind (the 52nd). inlsuspendflow: a generic `inline fun <T>` with a crossinline
# suspend RECEIVER lambda captured into an `object : Src<T>` whose carrier invokes the generic receiver's suspend
# member `emit` — a MULTI-scope {(method,0),(type,0)} tv key set the old single-scope-prefix guard fail-loud'd on; 2A's
# construction-typeArgs channel (mirroring the newClosure arm) instantiates `new SM<origTvs…>(…)`. Exercised top-level
# + from a generic METHOD (method-scope free var) + a generic CLASS member (type-scope free var). inlsuspendouter: an
# extension `inline fun T.op` whose payload newSuspendLambda captures `this@op` (__outer), rebound to the splice's
# `__self` temp by 2B — incl. the DOMINANT placement (op spliced INSIDE a `suspend fun`, where GAP 2 must preserve the
# 2B override, not clobber it). Both drive end-to-end via blockOn (value MUST be correct).
il_check_imports inlsuspendflow AppKt "$ROOT/cases/il-inlsuspendflow" "$(printf '42\n42\n42')"
# #75 Batch B — a §4.4ii-materialized SUSPEND carrier whose body NESTS a `newSuspendLambda` under a GAPPED / multi-scope
# enclosing tv remap (the real unsafeFlow/combineTransform flow shape the prior 2A fix missed). The nested SM's own tv
# frame is SHIELDED (like synthClass) from the outer carrier's CollectTvKeys/RenumberTvs; the shifting method-3 tv rides
# inside a reference-typed suspend-`fn` capture (permitted by the narrowed guard). Drives end-to-end via blockOn.
il_check_imports inlsuspendnest AppKt "$ROOT/cases/il-inlsuspendnest" "$(printf '42\n42')"
il_check_imports inlsuspendouter AppKt "$ROOT/cases/il-inlsuspendouter" "$(printf '42\n7\n20')"
# rc6 (#75 holistic) — the cold-SM NESTED-closure capture family (the kotlinx.coroutines flow port blocker). A mini cold
# Flow reproducing the `unsafeFlow { collect { … transform(value) } }` / `filter { predicate(it) }` /
# `filterDivisibleBy { box.accepts(it) }` shapes: a nested newSuspendLambda + suspend SAM capturing the inline-renamed
# FlowCollector receiver ($this$unsafeFlow -> __recvN) AND a crossinline param / captured VALUE. Drives the whole unified
# fix E1-E7 (body descriptor-name shadow + capValues one-value-channel, suspend-SAM mods.suspend, inlineLambda
# capture-descriptor lockstep rename, §4.4iii dead-capture prune, spliced-carrier capture propagation) end-to-end via blockOn.
il_check_imports flowtransform AppKt "$ROOT/cases/il-flowtransform" "$(printf '12\n210\n9')"
# #43 — Batch A × Batch B integration seam. A crossinline SUSPEND carrier materialized §4.4ii (like inlsuspendcarrier)
# whose body nests a MEMBER-inline call omitting a lambda default: the inner splice (walked first) fills it via the #34
# member-inline default carriage, re-hoisting a `__dflt$lambda` app-local + minting a `newDelegate` INSIDE the carrier.
# The suspend §4.4ii arm formerly refused ANY nested newDelegate (blanket) -> FailLoud; now it refuses only a newDelegate
# that does NOT resolve app-locally, so the same-module re-hoisted delegate materializes. The suspendCancellableCoroutine*
# family shape that gated the kotlinx.coroutines port. Values: c.pick(false,{5})=-1 & (true,{5})=5, addA sums.
il_check_imports inlsuspenddefault AppKt "$ROOT/cases/il-inlsuspenddefault" "$(printf '19\n15\n-1')"
# FIX 2 no-false-positive regression: a §4.4ii-materialized (non-suspend newClosure) carrier that MUTATES a captured var
# (`acc += 10`) must KEEP working — kotc ref-cell-boxes the mutated capture, so the write reaches bir2cir as a ref-cell
# field write (not a bare setLocal-to-capture), and MaterializeCarrier's new setLocal-to-capture refusal must NOT fire on it.
il_check_imports inlmatsetcap AppKt "$ROOT/cases/il-inlmatsetcap" "10"
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

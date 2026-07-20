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
	# del2 (#60 W1): the splice-all widening (kotc now emits a callInline for EVERY cross-module inline member with a
	# lambda) moved `Delegates.observable`/`vetoable` — klib-stdlib inline MEMBERS whose CROSSINLINE lambda escapes into
	# an object-literal ObservableProperty subclass — onto the splice engine. That subclass is a STDLIB-emitted named
	# class (`dotkt$objNNNN`, referenced NOT re-emitted in the app), whose ctor param is baked as the Kotlin delegate
	# `KAction`3`/`KFunc`4`; §4.4ii materializes the app-side onChange carrier as the BCL `System.Action`3`/`System.Func`4`
	# — StackUnexpected at `C::.ctor`. Runtime-SAFE (both are MulticastDelegate with the identical Invoke signature, so
	# the CLR binds them; RUN green — the delegate-property output is correct); ILVerify only rejects the erased static
	# stack type. This is a CROSS-MODULE lifted-artifact delegate-representation ABI mismatch (a stdlib-emitted inline
	# artifact's Kotlin-delegate ABI vs the app's BCL-delegate materialization). LIVE TRACKER: #123 (OPEN — the
	# delegate-representation ABI / §4.4ii materialization follow-up); the splice-widening ORIGIN is #60 W1 (CLOSED), NOT
	# the #60 SILENT non-local-return miscompile (also fixed; a crossinline lambda has no non-local return). Kept in ASMS
	# (no silent gap); the run lane is the behavioral gate.
	[del2]="#123 (OPEN delegate-representation ABI follow-up; splice origin #60 W1, closed): splice-all widening routed Delegates.observable/vetoable (crossinline lambda -> stdlib-emitted object-literal) onto the splice engine; §4.4ii materializes a BCL System.Action/Func where the stdlib ctor bakes the Kotlin KAction/KFunc — runtime-safe cross-module delegate-representation ABI mismatch (RUN green)"
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
# sample (il-monitordrain does Monitor.Wait until a cross-thread Pulse; il-comaindrain blocks a suspend
# main until it drains). Without a hard bound ONE hung sample wedges the ENTIRE gate — CI then kills the
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
EXCMETA="$ROOT/build/exc.meta"
dotnet "$FACADEGEN_DLL" "$EXCMETA" --compile-refs "$FRAMEWORK_COMPILE_REFS" System.Exception System.Console >/dev/null 2>&1
COLLMETA="$ROOT/build/coll.meta"
dotnet "$FACADEGEN_DLL" "$COLLMETA" --compile-refs "$FRAMEWORK_COMPILE_REFS" System.Collections.ObjectModel.Collection >/dev/null 2>&1
OBSCOLLMETA="$ROOT/build/obscoll.meta"
dotnet "$FACADEGEN_DLL" "$OBSCOLLMETA" --compile-refs "$FRAMEWORK_COMPILE_REFS" System.Collections.ObjectModel.ObservableCollection >/dev/null 2>&1
GMMETA="$ROOT/build/gm.meta"
dotnet "$FACADEGEN_DLL" "$GMMETA" --compile-refs "$FRAMEWORK_COMPILE_REFS" System.Runtime.CompilerServices.Unsafe System.Runtime.CompilerServices.RuntimeHelpers System.Collections.ObjectModel.Collection >/dev/null 2>&1

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
		# The case's .NET-space facade metadata (EXCMETA/COLLMETA/... — System.* injection) ONLY, if any. The stdlib
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
il_check mc1   MC1   "$ROOT/cases/m-c1"      "$(printf 'c = (4, 6)\na.d2 = 25\nrect area=30')"
il_check iface Iface "$ROOT/cases/il-iface"  "$(printf 'Hello\nKonnichiwa')"
il_check overrideprop OverridePropKt "$ROOT/cases/il-overrideprop" "$(printf '21\n42\n7')"   # `override val` accessor fills the base CLASS abstract slot (not a fresh NewSlot) — else concrete subclass TypeLoad-fails
il_check overridemsg AppKt "$ROOT/cases/il-overridemsg" "$(printf 'overridden\noverridden\noverridden')"   # #24: `override val message` on a @ClrTypeAlias base (kotlin.Exception->System.Exception) — DeclarationRename wires the get_message accessor to the @ClrProperty("Message") slot (rename + clrOverride) so DefineMethodOverride binds System.Exception.get_Message (else every read returns the base value)
il_check supercall SuperCall "$ROOT/cases/il-supercall/app.kt" "$(printf 'derived+base\n21\nderived[base-tag]\nDerived<Base>\nABC\ndog>animal\nimpl+hi-default\nderived+base\n11')"   # #14: super.X() from an override is a non-virtual `call` to the resolved base slot (else callvirt re-dispatches → infinite recursion); covers method/prop/3-level chain/user-base toString/interface-DIM + a virtual-dispatch non-regression
il_check superobj SuperObj "$ROOT/cases/il-superobj/app.kt" "$(printf 'N:7\nTrue\nTrue\nFalse')"   # #14 RESIDUAL R1: super.toString()/hashCode()/equals() to kotlin.Any → the System.Object slot NON-virtually (MemberCallSubstitution carries the `super` marker onto clrInstance; ilemit emits `call`, not the callvirt that re-dispatched → stack overflow)
il_check_imports supernet AppKt "$ROOT/cases/il-supernet" "$(printf 'True\nTrue')"   # #14 RESIDUAL R2: super.Next() to a facadegen-injected .NET base (System.Random) → NetInteropBinding propagates the `super` marker onto clrInstance; ilemit's EmitClrCall emits `call`, not the callvirt that re-dispatched → infinite recursion
il_check xfaceimpl XFace "$ROOT/cases/il-xfaceimpl" "1"   # cross-file + namespaced interface impl/dispatch (FindMethod key regression)
# language-core family (il-object/il-objexpr/il-companionext/il-ifacecompanion/il-op/il-ops/il-usermember/il-userrange/
# il-rangein/il-whensubj/il-smartcast/il-scope) migrated to the NUnit battery tests/il/fixtures/LanguageCoreTests.kt
# (12 methods), gated by tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs + il_check
# lines were removed same-change; their il-object/il-objexpr/il-op/il-ops/il-rangein/il-scope/il-smartcast/il-userrange/
# il-whensubj PURE entries were removed from verify-differential.sh same-change.
# lambda/closure/HOF/function-reference family (il-closure/il-lambda/il-genclosure/il-genhof/il-mfclosure/il-mflambda/
# il-writecapture/il-funref/il-extfunref/il-threadlambda) migrated to the NUnit battery tests/il/fixtures/LambdaTests.kt
# (10 methods; + LambdaTestsB.kt for the il-mfclosure/il-mflambda file-B halves), gated by tests/run-nunit-il.sh.
# Per the cases-test-design audit #14 the old per-case dirs + il_check/il_check_imports lines were removed same-change.
# (il-suspendcapture/il-suspendnestedcapture stay in the bash lane — suspend/coroutine, a deferred family.)
il_check caprefinline AppKt "$ROOT/cases/il-capref-inline/app.kt" "$(printf '2\n4\n6\n99')"   # a coerced `::pushDouble` reference inside a buildList{} inline lambda -> an ADAPTER_FOR_CALLABLE_REFERENCE local fn whose bound receiver is an ExtensionReceiver param `receiver`; liftLocalFn must emit the receiver param, else the body's `receiver.pushDouble` dangles (the kotlinx flow `__local*_add: references undeclared local 'receiver'` blocker)
il_check adapterref AppKt "$ROOT/cases/il-adapterref/app.kt" "$(printf 'sink 1\nsink 2\nsink 3\nbuilt 4\nbuilt 5')"   # #84 G: a coerced MEMBER reference (`s::add`/`::add`, Boolean-returning member adapted to (Int)->Unit) passed to an inline forEach — the ADAPTER_FOR_CALLABLE_REFERENCE must forward to the real member as callInstance (adapterRef replays the adapter body), not a top-level `callStatic owner:null` (`static method not found: add`, the consumeEach(collection::add) blocker)
# generic-types family (il-genbase/il-genctor/il-geninherit/il-genstatic/il-gencolladd/il-genlocalclass/il-genfield/
# il-objgen/il-gfac/il-genextnew) migrated to the NUnit battery tests/il/fixtures/GenericTypesTests.kt (10 methods),
# gated by tests/run-nunit-il.sh. Per the cases-test-design audit #14 the old per-case dirs + il_check lines were
# removed same-change; their il-genbase/il-genctor/il-genstatic/il-gencolladd/il-gfac/il-objgen PURE entries were
# removed from verify-differential.sh same-change.
il_check inheritedgenericinline AppKt "$ROOT/cases/il-inheritedgenericinline/app.kt" "$(printf '42\nabcd\n42')"   # #88: an inherited member `inline fun` whose OWNER class is GENERIC (`IntBox/StrBox : Container<E>`) spliced at a subclass call site — kotc's F2A carries the owner's type args via the corresponding-supertype instantiation `Container<Int>`/`Container<String>`, so the spliced payload's `tv{scope:type,0}` (E) concretizes instead of staying an OPEN generic (which typed the dispatch temp as the open type -> BadImageFormatException); the third line covers a TYPE-PARAMETER receiver whose bound `T : Container<Int>` fixes the owner arg
il_check geninlinearg GenInlineArg "$ROOT/cases/il-geninlinearg/app.kt" "$(printf '[7]\n[x]\n1')"   # #122: inline collection-factory arg of a `new` in a generic fn — declared class-scope tv instantiated through the `new` binding (else Add(T[]) splat mismatch)
# enum family (il-enum/il-enumintr/il-enumtostr/il-enumbody/il-enumrich) migrated to the NUnit battery
# tests/il/fixtures/EnumTests.kt (+ EnumCrossFile.kt for the #90 cross-file basic-enum decl).
# netenumbound (#107): a facadegen-injected .NET enum (System.DayOfWeek) satisfies Kotlin's `T : Enum<T>` bound —
# facadegen declares the self-referential `kotlin.Enum<Self>` supertype so a `<T : Enum<T>>` generic fn / the
# reified `enumValues<TheNetEnum>()` / `enumValueOf<TheNetEnum>()` intrinsics resolve at the frontend; the backend
# routes `e.name` (kotlin.Enum member on a .NET-enum-bound type-param) + enumValues/enumValueOf to System.Enum.
il_check_imports netenumbound NetEnumBound "$ROOT/cases/il-netenumbound" "$(printf 'Friday\n7\n1')"
# vtboundref (#149): a bound callable-ref over a VALUE-TYPE (.NET struct) receiver — the delegate ctor's `object`
# target + ldftn/ldvirtftn need an object reference, so ilemit boxes the struct receiver in newBoundClrDelegate.
# Covers both the non-virtual (box + ldftn: TimeSpan::CompareTo) and virtual (box + dup + ldvirtftn: overridden
# Object.ToString) target; unboxed it was StackUnexpected/mis-bound (unverifiable IL).
il_check_imports vtboundref AppKt "$ROOT/cases/il-vtboundref" "$(printf -- '-1\n00:00:05')"
# icmparity (#129): a Kotlin class implements a member of a same-name .NET arity FAMILY (System.IComparable +
# System.IComparable`1). A Kotlin classifier cannot be arity-overloaded (K2 hard limit, dotkt-semantics §8d), so
# facadegen names the GENERIC `IComparable1<T>` (non-generic keeps plain `IComparable`); implementing it uses the
# VERBATIM .NET member `CompareTo(other: Ver?)`, not the Kotlin operator `compareTo`. Direct + upcast dispatch.
il_check_imports icmparity IcmpArity "$ROOT/cases/il-icmparity" "$(printf -- '-2\n6')"
# gendelegate (#140/P3): a Kotlin lambda into a GENERIC BCL delegate ctor param over a USER TypeBuilder
# (ThreadLocal<Box> = Func<Box>, Progress<Box> = Action<Box>). The constructed TypeBuilderInstantiation
# resolves the ctor on the OPEN def, so ilemit must substitute T->Box to materialize System.Func`1<Box>/
# System.Action`1<Box>, not the internal KFunc`1<Box> (ilverify StackUnexpected). Covers both delegate
# positions (return-var via Func, input-var via Action).
il_check_imports gendelegate AppKt "$ROOT/cases/il-gendelegate" "$(printf '42\nTrue')"
il_check_imports jsongeneric AppKt "$ROOT/cases/il-jsongeneric" "$(printf '42\n"hi"')"   # #44: a generic .NET method (JsonSerializer.Serialize<T>) with a facadegen-injected interop SIBLING param (JsonSerializerOptions) — ShapeSynthesis resolves the leaf off the refs to its .NET simple name so the overload-matcher shapes match ilemit's reflected shapes (was: "Object" erasure -> zero candidates -> ilemit "Sequence contains no elements")
# m2 / mi1 consume BCL types via `import System.X` (System.Math, System.Text.StringBuilder) -> the facadegen import
# scan (il_check_imports), NOT a bare il_check (which injects nothing, so the import would not resolve). No runtime.cs.
il_check_imports m2  M2    "$ROOT/cases/m2"         "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
il_check_imports mi1 MI1   "$ROOT/cases/m-i1"       "$(printf 'Hello, CLR 42\nlength = 13')"
# alias: `import System.Text.StringBuilder as SB` — the PSI import scan keeps the aliased form (feedback (5)).
il_check_imports alias Alias "$ROOT/cases/il-alias" "$(printf 'hello, alias\n12')"
# dual-rep: the imported .NET view + the stdlib kotlin.text.StringBuilder coexist as two typed views of one CLR
# type; an explicit cast crosses them (rule in docs/dotkt-semantics.md).
il_check_imports dualrep DualRep "$ROOT/cases/il-dualrep" "$(printf 'net\n3\nkt\nnet')"
# bclinject (#143): BCL-injection coverage + NRT fidelity — the generic `ThreadLocal<T>` value-factory ctor injects;
# `ThreadLocal<T>.Value` surfaces as a PLATFORM type (`String!`, its getter carries [MaybeNull]) so `v == null` is legal
# (not 'always false') and true when unset; and static `RuntimeHelpers.GetHashCode(object)` injects (the OBJECT_MEMBERS
# name-skip no longer drops a distinct static method).
il_check_imports bclinject AppKt "$ROOT/cases/il-bclinject" "$(printf 'hi\nTrue\nTrue')"
# tlvalint (#8/#11): the VALUE-type twin of bclinject — `ThreadLocal<Int>.Value` is a `[MaybeNull]` oblivious platform
# type `Int!`, so it lowers to a BARE `int32` (default `0` when unset), NEVER `Nullable<Int32>`. READ (#8): into a
# non-null `Int` yields 0; the `== null` is statically false; the `?:` elvis returns the bare value; the
# `ThreadLocal<String>` twin proves the reference-oblivious path (#143) still gives a real runtime null check. WRITE
# (#11): a bare `5` writes; a `Nullable<Int32>` source (`Int? = 7`) is coerced/unwrapped to the bare slot (-> 7); the
# `String?` reference-slot write needs no coercion (-> hi). (A literal-`null` value write is a loud bir2cir error, not a
# run sample — §9a-bis.)
il_check_imports tlvalint AppKt "$ROOT/cases/il-tlvalint" "$(printf '0\nFalse\n0\nTrue\n5\n7\nhi')"
# taskfam: a same-name .NET arity family — non-generic `Task` and `Task<TResult>` (Kotlin `Task1`) coexist in one
# file; `generic:Task1[T]` cross-refs resolve to the arity-1 definition (docs/dotkt-semantics.md §8d).
il_check_imports taskfam Tf "$ROOT/cases/il-taskfam" "$(printf 'plain=True\ngeneric=42')"
# taskawait: bir2cir SuspendColdLowering P4 REVERSE bridge — the facadegen-injected `Task.await()` marker
# lowered to the cold-core awaiter dance (GetAwaiter/IsCompleted/OnCompleted/GetResult TaskAwaiter STRUCT
# calls). SYNC FAST PATH (already-completed tasks): generic Task<Int>.await() + non-generic Task.await():Unit.
il_check_imports taskawait TaskAwait "$ROOT/cases/il-taskawait" "$(printf '43\n7')"
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
# taskgen: a GENERIC .NET static factory (Task.FromResult<TResult>) — the seam that lets Kotlin BUILD a
# Task<T> (async interop). kotc's companion generic-static builder declares the method type parameter and
# resolves the return/param against it, so `Task.FromResult(42)` binds as `FromResult<Int>(42): Task<Int>`
# and emits a `clrGenericStatic` node (bir2cir/ilemit already lower it — verified E2E with a hand-authored
# meta). XFAIL until facadegen surfaces the generic `sfun` line (it currently skips m.IsGenericMethod at
# facadegen/Program.cs:557); once it does, this auto-passes ("42").
il_check_imports taskgen Tg "$ROOT/cases/il-taskgen" "42"
# taskwhen: N3 regression — facadegen `Map` short-circuited on `t.FullName == self.FullName` where BOTH
# are null for an OPEN constructed generic (`Task<T>` inside `IEnumerable<Task<T>>`), replacing the arg
# with the ENCLOSING type's name -> `IEnumerable[IEnumerable]` / `Task1[Task1]`. Guarding the compare with
# `FullName != null` recurses into the arg. `Task.WhenAny(a,b): Task<Task<Int>>` (double-nested RETURN)
# runs for real. N3-deep: `Task.WhenAll(vararg Task<Int>): Task<Int[]>` now EXECUTES too — the generic .NET-method
# value-param builder now strips the `vararg:` prefix (else the vararg param surfaced as Any? and mis-resolved).
il_check_imports taskwhen Tw "$ROOT/cases/il-taskwhen" "$(printf '10\n6')"
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
il_check for   ForT  "$ROOT/cases/il-for"     "$(printf 'sum 1..5 = 15\ncountdown 5 = 54321')"
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
# tests/il/fixtures/FloatTests.kt (7), gated by tests/run-nunit-il.sh; the old per-case dirs + il_check lines were
# deleted in that SAME change.
# printlnnull: println/print(null) render the string "null" (Kotlin semantics); non-null values print normally.
# The collections family (list/set/iteration/collection-op + Map-typed cases: il-coll*, il-map*, il-mut*, il-iter*,
# il-hashset2, il-iscoll, il-listeq, il-listplus, il-eachcount, il-emptymap, il-groupby2, …) migrated to the NUnit
# batteries tests/il/fixtures/CollectionsTests.kt (16) + MapsTests.kt (10), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14 the old per-case dirs + their il_check lines were deleted in that SAME change.
# Math/numeric family (il-math, il-mathabs, il-coerce, il-roundhalfup + differential-only il-divmin, il-mixnum)
# migrated to the NUnit battery tests/il/fixtures/MathTests.kt (6 methods), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14 the old per-case dirs + their il_check lines were deleted in that SAME change.
il_check pairtostr PairToStr "$ROOT/cases/il-pairtostr" "$(printf '[1, 2, 3]\n[1, 2, 3]\n(1, 2)\n(1, 2, 3)\nRec(name=k, n=9)\nTrue')"  # C11 gate regression guard: collection/tuple/data-class toString + String.hashCode within-run consistency (#167)
# pairnest: a nested collection/map INSIDE Pair/Triple.toString (C11) renders Kotlin-style — the tuple component's
# erased generic static type used to reach the raw .NET `List`1[...]` ToString; now routed through the runtime
# collection-aware stdlib stringifier (clrRenderTupleElement -> clrElemToString), matching the top-level nestedstr path.
il_check pairnest PairNest "$ROOT/cases/il-pairnest" "$(printf '([1, 2], [3, 4])\n([1], [2], [3])\n({1=2}, [3])\n(1, (2, 3))\n([[1]], 5)\n(1, 2)\n(null, a)')"
# nullcollarg: #100 H3 regression guard — a nullable-inner collection type-arg (`Map<String, List<Int>?>`) upcast from
# a MutableMap must still collapse its V to IList and verify clean (the `?` must not smuggle an un-collapsed IReadOnly
# face past the collapse). Pure runnable guard for that shape.
il_check extprop ExtProp "$ROOT/cases/il-extprop" "$(printf '2\n1\n1\n3\n-1\n1\n0')"  # C7 (+ #157 NON-coroutine guard): cross-module top-level extension-property getter -> callStatic get_<name>(receiver) (generic List.lastIndex carries type args); NOT a dropped-receiver field read. Resolves through the SAME general owner-null path as xmodtopval (prop:get -> get_<name> -> TryResolveTopLevelStatic recvKey branch) — a name-keyed re-special-case of that path would break these non-coroutine names
il_check defargs DefArgs "$ROOT/cases/il-defargs" "$(printf 'x1-x2-x3\n1, 2, 3\n[1, 2, 3]\n1/2/~\nb=c\na\nnodelim\nFALLBACK\nP(x=1, y=20, z=3)\nP(x=10, y=2, z=30)\nHello, Kotlin!\nHello, Kotlin?')"  # C3: cross+same-module default args — omitted middle default must not shift a later provided arg's slot (joinToString transform / substringAfter `= this` / data-class copy(field=))
il_check defargs2 DefArgs2 "$ROOT/cases/il-defargs2" "$(printf '55\n7\n12\n30\n134\n156\n159')"  # C3 residual: same-module default referencing ANOTHER value param (`b = a * 10`, `c = a + b`) — inlined with that param's filled arg substituted
il_check infloopret InfLoopRet "$ROOT/cases/il-infloopret" "$(printf '30\nok4')"  # #141: value-returning while(true){…return x} -> ilemit appends default(ret)+ret so the unreachable fall-through terminator is ilverify-clean (ReturnMissing gone)
il_check toplateinit TopLateinit "$ROOT/cases/il-toplateinit" "$(printf 'caught: uninitialized\nhello\n5')"  # #104: top-level `lateinit var` (ref type) static field carries `"init": null` — must NOT hit the .cctor null-coercion store (crash); default-null + lateinitGet throw-before-init
il_check samcmp SamCmp "$ROOT/cases/il-samcmp" "$(printf '1,1,2,3,4,5,6,9\n9,6,5,4,3,2,1,1')"  # explicit Comparator{} SAM conversion (plain fun interface; no kotc @ClrTypeAlias read)
il_check cp    Cp    "$ROOT/cases/il-cp"      "$(printf '50\n3.5\nTrue\nTrue\nX')"
il_check ext   Ext   "$ROOT/cases/il-ext"     "$(printf '21\nHI')"
# Array family (arr/arrops/arrnull/arrslice/arrplus/intarraytolist/copyintoverlap/fillrange/indices/indicesv/ubytearr/
# genarrlam) migrated to the NUnit in-process suite -> tests/il/fixtures/ArrayTests.kt (value asserts). il-copyofnull /
# il-boxgen stay here (live XFAIL_ILVERIFY findings, not migratable into the ilverify-clean lane).
il_check arraydeque AppKt "$ROOT/cases/il-arraydeque" "$(printf 'z\nb\nc\n1\nA')"   # concrete generic stdlib class ArrayDeque<E>:AbstractMutableList<E> as a field/owner forces ilemit to resolve kotlin.collections.ArrayDeque`1 from the rt dll — exercises the ICollection/IList void-drop methodimpl bridge (ilemit) + the BCL-only slot synthesis Contains/CopyTo/IsReadOnly/IndexOf (bir2cir)
il_check utf8throw AppKt "$ROOT/cases/il-utf8throw/app.kt" "$(printf 'True\ndecode-threw\nencode-threw\nhello')"   # #143: decodeToString/encodeToByteArray honor throwOnInvalidSequence=true -> CharacterCodingException via throwing UTF8Encoding(false,true)
il_check caseinvariant AppKt "$ROOT/cases/il-caseinvariant/app.kt" "$(printf 'ß\nSTRAßE\nABC\nhello\nß\nTrue')"   # #144: String/Char uppercase()/lowercase() are CLR-native 1:1 ToUpperInvariant/ToLowerInvariant — DELIBERATELY no Unicode one-to-many expansion (ß stays ß, not SS)
il_check seq   Seq   "$ROOT/cases/il-seq"     "$(printf '6,12\n16\n3\n27\n10-20-30\n1,2,3\n4,5,6\n3')"
il_check seqforin SeqForin "$ROOT/cases/il-seqforin" "$(printf 'a\nb')"
il_check char  Char  "$ROOT/cases/il-char"    "$(printf 'True\nTrue\nTrue\nTrue\nA\nz\nTrue\nTrue\n97\nb')"
il_check sort  Sort  "$ROOT/cases/il-sort"    "$(printf '9,6,5,4,3,2,1,1\na,dd,bbb,cccc\ncccc,bbb,dd,a')"
il_check boxgen BoxgenKt "$ROOT/cases/il-boxgen" "$(printf '42\n1\n42\n42\n10\n-1\n[1, 2, 3]\n[3, 2, 1]\n[a, b, c]\n[1, null, 3]\n[5, null, null]\nSUMMER')"   # C2 boxed-primitive dual-representation: getOrPut/getOrElse/compareBy/Array<Int?>/T:Enum<T>
il_check copyofnull Copyofnull "$ROOT/cases/il-copyofnull" "$(printf '[1, 2, 3, null, null]\n[1, 2]\n[1, 2, 3]\n1\nnull\n6\n[1, 2, null]\n[2.5, 3.5, null]\n[a, b, null]\n[x, y, null]\n[7, null, null]')"   # #124: Array<value-type>.copyOf(newSize) builds Nullable<elem>[] by runtime reflection (grow null-tail/shrink/prefix read-back; value + reference + already-nullable T)
# G8 (#73 w9): UNBOUND extension-function callable references (`String::isNotBlank`, `String::repeatBy`) -> a lifted
# static forwarder whose body is the faithful ext call; bir2cir binds/substitutes the inner call (isNotBlank = the
# reverted Indent.kt case). Same-module (shout/doubleLen/repeatBy/logTo) + cross-module stdlib (isNotBlank); logTo
# covers the Unit-returning forwarder (exprStmt body).
il_check boundextref BoundExtRef "$ROOT/cases/il-boundextref" "$(printf 'hi!\nababab\nfirst!\n[x][x]\nTrue\nTrue')"   # #91: bound ext-fn ref `expr::extFn` -> capture-class lift (receiver captured eagerly; delegate over instance invoke). #106: bound CharSeq-ext ref (::isNotBlank/::isBlank) -> String field-read adapter-wrapped by StringCharSequenceBridge
# A6: rule-3 helper calls on CONCRETE generic alias receivers (HashMap/ArrayList/LinkedHashMap: class typeArgs +
# instantiated sig) + Map/MutableMap getOrDefault (bare-V map-defaults helper: retType carry, was BadImageFormat).
il_check unsgn Unsigned "$ROOT/cases/il-unsigned" "$(printf '4000000100\n4000000000\n18000000000000000000\n60000\n250')"
il_check regex Regex "$ROOT/cases/il-regex" "$(printf 'True\nFalse\na#b#c#\na_b_c\nTrue\nFalse\n42\nnull')"
# regexanchor (#162): matchEntire/matches do a TRUE anchored `\A(?:...)\z` match, not a leftmost search filtered by span
# — so a shorter alternation branch (`a` in `a|ab`) or a lazy quantifier still yields the full-input match; compiled
# options (?i) and existing anchors coexist; capture-group numbers are preserved by the non-capturing wrapper group.
il_check regexanchor RegexAnchor "$ROOT/cases/il-regexanchor" "$(printf 'ab\nTrue\na\naaa\nTrue\n12-34,12,34\nab\nTrue\nFalse\nnull')"
# regexopts (#178): Regex(String, RegexOption) / Regex(String, Set<RegexOption>) ctors — bir2cir NetInteropBinding
# converts the RegexOption / Set<RegexOption> arg to the BCL RegexOptions int bitmask (IGNORE_CASE->1 / MULTILINE->2 /
# DOT_MATCHES_ALL->16 / COMMENTS->32) at the ctor call site (was InvalidProgram / ABI-mismatch: the DotKt enum/set does
# not match nor carry the numeric value of the [Flags] System...RegexOptions int ctor param).
il_check regexopts RegexOpts "$ROOT/cases/il-regexopts" "$(printf 'True\nTrue\nFalse\nTrue\nTrue\nFalse\nTrue\nFalse\nTrue\nTrue')"
# linkedorder (#169): LinkedHashMap/LinkedHashSet (and mapOf/setOf) preserve insertion order across a MIDDLE removal —
# LinkedHashMap is backed by the insertion-ordered System...OrderedDictionary; LinkedHashSet by a pure-Kotlin set over it.
il_check linkedorder LinkedOrder "$ROOT/cases/il-linkedorder" "$(printf 'a,c,d,e\na=1,c=3,d=4,e=5\n1,3,4,5\nx,z,w,q\n4\nTrue\nFalse\none,two,three\np,d,b,a')"
# linkedset (#169 regression): setOf/distinct()/toMutableSet() build the CONCRETE LinkedHashSet — was InvalidProgram
# (arity-only ctor pick routed `new LinkedHashSet(coll)` to the (Int) ctor; the self iterator()/ICollection Contains
# slot referenced the open generic self). Locks the crash-free build AND insertion order across a MIDDLE removal + retainAll.
il_check linkedset LinkedSet "$ROOT/cases/il-linkedset" "$(printf '3,1,2,4\n4\n3\na,b,c\nx,z,w,q\n4\nTrue\nFalse\n2,4,5\n10,30,40')"
# regexreplace: Regex.replaceFirst / replace(String,String) marshaling (final-review N1). replaceFirst mis-bound the
# 3-arg System...Regex.Replace(string,string,int) — returned the input unchanged + AccessViolationException on a
# CharSequence-typed input; the fix materializes the CharSequence to a String at the call site. Also pins toString()
# (the pattern-string method binding) AND `re.pattern` (final-review N2 — a rule-3 property accessor `get()=toString()`
# that AliasHelperHoist now hoists into the ClrH helper; it was blanket-skipped as a get_ accessor -> ilemit crash).
il_check regexreplace RegexReplace "$ROOT/cases/il-regexreplace" "$(printf 'bXnana\nbXnXnX\nbXnana\na#b34\na(\\d+)b\nc(\\w+)d')"
# regexgroups: MatchResult.groups (ClrMatchGroupCollection) — by-index/by-name access, iteration, and `group in
# match.groups` (POLISH family-6 coverage). il-regex never touches .groups; this pinned + fixed a TypeLoad on the
# AbstractCollection base + a missing `contains` (ClrMatchGroupCollection now implements the collection directly).
il_check regexgroups RegexGroups "$ROOT/cases/il-regexgroups" "$(printf '3\n12-34\n12\n34\n12-34,12,34,\nTrue\nFalse\nTrue\n2026')"
# regexseq (#104): the Sequence-returning members findAll/splitToSequence + the options getter, which used to ship as
# TODO() runtime stubs that threw NotImplementedError. findAll = generateSequence over find()/MatchResult.next()
# (every non-overlapping match L-to-R, startIndex-honored); splitToSequence = split().asSequence(); options decodes the
# compiled System...RegexOptions [Flags] bitmask (default Regex -> empty set, no longer throws).
il_check regexseq RegexSeq "$ROOT/cases/il-regexseq" "$(printf '1,22,333\n0\n2,3\na|b|c\nTrue\na|b c d\nTrue')"
il_check groupvalues GroupValues "$ROOT/cases/il-groupvalues" "$(printf 'abc,a,b,c\n12 34')"
# gencolladd: non-inlined GENERIC collection building via `.map`/`.add`/`.size` — the stdlib `clrCollAdd<T>`
# reads `c.size` (ICollection<!!T>.get_Count) on an OPEN method type-param. Locks the bymap/maxOrNull dispatch
# family's collection analog (an open-generic ICollection member call must bind at runtime, no EntryPointNotFound).
il_check langtail LangTail "$ROOT/cases/il-langtail" "$(printf '6\nhi\nint:42\nstr:3\nbig:5\nsmall\n700\n9')"
il_check tailrec Tailrec "$ROOT/cases/il-tailrec" "$(printf '500000500000\n0\n2000000014\n1000000\n2000000')"   # §2b: deep `tailrec` TCO'd to a back-jump loop (self / when / extension-receiver / member); no CLR stack overflow
il_check copydef CopyDef "$ROOT/cases/il-copydef" "$(printf '(1, 20)\n(5, 2)\n(1, 9, 3)\n(7, 2, 8)\nPoint(x=1, y=20, z=3)\nPoint(x=9, y=2, z=8)')"   # C3: data-class copy(field=x) with omitted fields — cross-module Pair/Triple reconstruct this.<field>
il_check equalscall EqualsCall "$ROOT/cases/il-equalscall" "$(printf 'False\nTrue\nTrue\nFalse\nTrue\nTrue\nFalse\nTrue\nTrue\nFalse\nTrue\nTrue')"   # §5a: explicit .equals() -> total-order (Double/Float) / structural (collections), plain object stays reference
il_check bytearg ByteArg "$ROOT/cases/il-bytearg" "$(printf '5\n3\n7\n9\n4\n100\n-2')"
il_check comparator Comparator "$ROOT/cases/il-comparator" "$(printf -- '-3\n5\n0')"
il_check use Use "$ROOT/cases/il-use" "$(printf 'close abcd\nn=4\nclose x\ncaught:boom')"
il_check comparable Comparable "$ROOT/cases/il-comparable" "$(printf 'a<b\nc>b\na<=a\n-3\n1.2,1.5,2.0')"
il_check seqfilter SeqFilter "$ROOT/cases/il-seqfilter" "$(printf '3,4,5,6\n20,40,60\n4\n3,4,5,6\n3')"
il_check bmore BMore "$ROOT/cases/il-bmore" "$(printf '5 items\nx = 42\n3.14\n00007\nff\n100%% ok: yes\n0:a,1:b,2:c\n0,20,60')"
il_check chunk Chunk "$ROOT/cases/il-chunk" "$(printf '3,7,5\n3\n1-2-3 4-5\na,b,c\n3\n1,3,5\n9')"
# cwindowed: CharSequence.windowed exercises a `break` in EXPRESSION position (its `coercedEnd` init); kotc lowers
# it to a valueBlock(goto/break + unreachable throw). eachcount: Grouping.eachCount reads a value-nullable smart-cast
# (`Int?`) in arithmetic (`count + 1`) — the C1 value-slot-unwrap class, locked here as a regression guard.
il_check cwindowed CWindowed "$ROOT/cases/il-cwindowed" "$(printf '[ab, bc, cd]\n[ab, cd]\n[abc, bcd, cde, de, e]\n[ab, de]\n[ab, bc, cd]')"
# cwindowedv: CharSequence.windowed with a VALUE-TYPE transform result (Int/Char). The transform lambda is a
# delegateNew target whose funcType keeps the synthetic <>dotkt_CharSequence, so its `it` param must stay synthetic
# (not collapse to System.String) — the stdlib passes a real <>dotkt_CharSequence (subSequence's result) in. W4-B guard.
il_check cwindowedv CWindowedV "$ROOT/cases/il-cwindowedv" "$(printf '[2, 2, 2]\n[a, b, c]\n[3, 3, 3]\n[ab, bc, cd]')"
il_check localclass LocalClass "$ROOT/cases/il-localclass" "$(printf '10\n42\n101\n3,4\nTrue\n60')"
# A generic cold-sequence SM: `fun <T> wrap(x) = sequence { yield(x) }.toList()` over a VALUE element (Int) and a
# reference element (String). Guards the `T?`-property `nextValue as T` double-unbox NRE (bir2cir erased-getter
# call-site retype) that broke every value-typed cold sequence, and (via the same drive) the RingBuffer path.
il_check genseq GenSeq "$ROOT/cases/il-genseq" "$(printf '[5]\n[hi]')"
# genseq2 (C13a): a generic capturing closure passed as a DELEGATE arg (generateSequence's `{ seed }` -> the
# GeneratorSequence Function0 ctor param). ilemit's delegate-arg binding path emitted the generic closure newobj with
# an OPEN operand (Closure`1::.ctor(!0)) -> TypeLoadException; and the iterator's delegateInvoke passed a boxed T? to
# `Func<T,object>::Invoke(!0)` with no unbox -> InvalidProgramException at a VALUE element. Both fixed; value + ref drive.
il_check genseq2 GenSeq2 "$ROOT/cases/il-genseq2" "$(printf '[1, 2, 4]\n[a, ab, abb]\n18')"
il_check refcell RefCell "$ROOT/cases/il-refcell" "$(printf '3\n30\nab\n10')"
il_check annot Annot "$ROOT/cases/il-annot" "$(printf 'widget#7\n42')"
il_check props Props "$ROOT/cases/il-props" "$(printf '20\n8\n16\nnot initialized\nready')"
# computedprop (#89): a top-level/companion `val` with a backing field (initializer) AND a custom getter must
# CALL get_<name>, not read the raw static field (which skipped the getter -> 41/7 instead of 42/107). The
# symmetric `var` custom-setter write must CALL set_<name>, not store the raw field. Object property = control.
il_check computedprop ComputedProp "$ROOT/cases/il-computedprop" "$(printf '42\n107\n20\n15\n6\n49')"
il_check kstar KStar "$ROOT/cases/il-kstar/app.kt" "*"   # #82: KTypeProjection.STAR computed companion prop routes to get_STAR (not a spurious staticField STAR)
il_check valcls ValCls "$ROOT/cases/il-valclass" "$(printf '1250\n12\n1250\nff\n1010\nff')"
il_check ctorref CtorRef "$ROOT/cases/il-ctorref" "$(printf '(1,2)\n(3,4)\n(9,9)')"
il_check getcls GetClass "$ROOT/cases/il-getclass" "$(printf 'String\nWidget\nWidget\nString')"
il_check_imports forin Forin "$ROOT/cases/il-forin" "$(printf '60\n10,20,30,\n3')"
il_check ldeleg LocalDeleg "$ROOT/cases/il-localdeleg" "$(printf '42\n42\nHI\nWORLD')"
il_check langf LangFeat "$ROOT/cases/il-langfeat" "$(printf '7\n1024\n120\ntf\ncircle=12\nsq=25\n1a\n2b')"
il_check pair  Pair  "$ROOT/cases/il-pair"    "$(printf '3\n4\nx\n10\n11')"
il_check triple Triple "$ROOT/cases/il-triple" "$(printf '1\ntwo\n3\n(1, two, 3)\n(1, two, 3)\n1|two|3\n1\ntwo\n3\n(1, TWO, 3)\nTrue\nFalse\n(3, two, 1)\n([1, 2], x, {k=9})')"   # COV4: Triple ctor/destructure/componentN/full-arg copy/toString (partial-copy-with-defaults omitted — cross-module default-arg bug)
il_check typealias TypeAlias "$ROOT/cases/il-typealias" "$(printf 'a,b,c\n3\n12\n42\n9\n-1')"   # COV3: typealias over stdlib generic / function type / user class, used across a fn boundary
il_check atomics Atomics "$ROOT/cases/il-atomics" "$(printf '11\n11\n16\nTrue\nFalse\n16\n16\n100\n55\n1001\n1001\n1000\n1000\n42')"   # COV2: kotlin.concurrent.atomics AtomicInt/AtomicLong exercising the @ClrRefArgument Interlocked byref binding
il_check volatileatomic AppKt "$ROOT/cases/il-volatileatomic" "$(printf '42\n9000000000\nTrue\nb')"   # #130: scalar atomics load()/store() volatile round-trip (Volatile.Read/Write byref for int/long/bool; @Volatile field for AtomicReference)
# #129: an AtomicIntArray element op whose bounds check THROWS mid-critical-section must still release the monitor
# (try/finally). A worker thread then acquires the same instance's monitor (loadAt); pre-fix the leaked lock made
# worker.Join(2000) time out -> "DEADLOCK". Needs facadegen for System.Threading.Thread, so il_check_imports.
il_check_imports atomicarraytry AppKt "$ROOT/cases/il-atomicarraytry" "$(printf 'True\n20\n100')"
# The nullable / null-safety battery (il-null, il-nullable-generic-list, il-nullableprim, il-nullbang,
# il-nullcollarg, il-nullcs, il-printlnnull, il-reqnn, il-safecallnv, il-trynullable) migrated to the NUnit
# battery tests/il/fixtures/NullableTests.kt (12 methods), gated by tests/run-nunit-il.sh. Per the
# cases-test-design audit #14, the old per-case dirs + these il_check lines were deleted in that SAME change.
# (il-nan/il-nancmp/il-negzero are NOT here — their subject is IEEE-float behavior, kept for a float battery.)
il_check refcellnullable AppKt "$ROOT/cases/il-refcell-nullable/app.kt" "$(printf '5\n6\n105\n25\n2.5\nnull')"   # #36: a captured-and-mutated `var Int?`/`Long?`/`Double?` -> heap ref-cell whose `v` field is Nullable<T>; the INIT ctor arg (bare T -> Nullable<T>), the inline smart-cast READ (Nullable<T>.Value), and the WRITE must all agree — was `new Ref(bare int)` into a Nullable<int> ctor slot -> InvalidProgramException
il_check tryval TryVal "$ROOT/cases/il-tryval" "$(printf '5\nnull\n7\n3.5\n1.5\nnull\n2.5\nnull\n11')"   # #127: `try{value}catch{null}` in VALUE position on a value-type result -> the shared temp is typed Nullable<T> (null branch = HasValue=false), mirror of ternary()'s value+null-branch join (incl. stdlib toFloatOrNull/toDoubleOrNull)
il_check nncontract NnContract "$ROOT/cases/il-nncontract/app.kt" "$(printf '2\ntb\nnpe-param\nnpe-ctor\nnpe-member\nnpe-ret\nnpe-retexpr\nnpe-retm\nnpe-getter\nfin\nnpe-trret')"   # #6/#32: non-null CONTRACTS on the public surface — PARAMETER PRECONDITIONS (top-level fun / ctor / member fun) + RETURN POSTCONDITIONS (statement/expression-position top-level fun / member fun / getter / return-in-try: finally runs before the postcondition NPE propagates) throw NullPointerException fail-fast on a laundered null; a normal non-null call is unaffected
il_check nullv MS1   "$ROOT/cases/m-s1/app.kt" "$(printf 'fallback\npresent\nforced\nlen null = -1\nlen hello = 5')"
il_check dataq Dq    "$ROOT/cases/m-s2/app.kt" "$(printf 'Point(x=3, y=4)\nPoint(x=7, y=9)\nx=3 y=4\na==b: True\na==c: False\nhash eq: True')"
# The non-coroutine inline family (il-inline, il-inline2, il-xinline, il-inlinedefaultlambda, il-inlinememberdefault,
# il-inline-klibmember-nlr, il-inlineinherit, il-inline-{nested-nlr,outerlabel,nlbreak,ownlabel,mutcapture,forward},
# il-inlinereturn{expr,unit,local}, il-inlineretcoerce) migrated to the NUnit battery tests/il/fixtures/InlineTests.kt.
# The inline cases below remain because they need a distinct lane (member-extension / generic-owner / sibling-file
# splice / transitive forwarding) or coroutine involvement (il-inline-suspend*, further down).
il_check memberextinline MemberExtInline "$ROOT/cases/il-memberextinline/app.kt" "$(printf '3\n1\n2\n-1')"   # #20: inline MEMBER-extension (companion member + Long extension) called with a lambda via `state.withState{}`; dispatch(companion)-unused so the extension splices via __self; non-local return keeps it inline
il_check inlnestparamshadow InlNestParamShadow "$ROOT/cases/il-inlnestparamshadow/app.kt" "1060"   # F2 (#61): a nested inlineLambda param `x` that SHADOWS the outer inline callee's value param `x` — RewriteLocalRefs must NOT rebind the inner param ref to outer's temp (silent miscompile; pre-fix -> 1050). The inlineLambda scope boundary in RewriteLocalRefs/ApplyPrefix/CollectDeclared
il_check inlsiblingdelegate InlSiblingDelegate "$ROOT/cases/il-inlsiblingdelegate" "$(printf '107\n5')"   # F4 (#63): a §4.4ii materialized carrier whose body carries a `newDelegate` targeting a `__lambda` lifted into a SIBLING file's file class — `_appLocalMethods` must be MODULE-WIDE (else the sibling target is mis-judged non-app-local -> HasUnmaterializableNested fail-loud). Regression from 923a820
# #75 S4a — escape-analysis narrowing samples. Cross-module stdlib inline ops (forEach/map/run) route through the
# bir2cir InlineSplice engine ONLY when a lambda arg escapes (non-local return/break, or arm-c suspension); the
# non-escaping majority takes the plain delegate call. See docs/design-inline-s4-narrowing-95.md §8.
il_check inlcompose    InlCompose    "$ROOT/cases/il-inlcompose/app.kt"        "$(printf '11\n99')"          # F3 (#62) transitive forwarding of an inline PARAM through a user top-level inline (outer(b)=inner(b)) + escaping non-local return
il_check ctor  CtorT "$ROOT/cases/il-ctor/app.kt" "$(printf '12\n25\n5x5\nhi=7\nsolo=0')"
il_check nest  Nst   "$ROOT/cases/il-nested/app.kt" "$(printf 'outer:root\nnode(7)\n14\nleaf 3')"
il_check vis   VisT  "$ROOT/cases/il-vis/app.kt" "$(printf '98\nacct\n99')"
il_check precond Pcd "$ROOT/cases/il-precond/app.kt" "$(printf '3\nreq\nchk\nerr:boom\ntodo')"   # #73 M6/M7: precondition/error family + top-level repeat{} inline loop (moved to bir2cir)
il_check repeatnlr RptN "$ROOT/cases/il-repeatnlr/app.kt" "$(printf '3\n-1\n6\n6\n63\n9')"   # #75: NON-LOCAL return + return@repeat + nested repeat + scope-fn-in-repeat through repeat{} (kotc carries the un-closured lambda body; bir2cir InlineSplice splices it)
il_check reif  Rf    "$ROOT/cases/il-reified/app.kt" "$(printf 'String\nInt32\nTrue\nFalse\nTrue\nyo\nno')"
il_check inner Inner "$ROOT/cases/il-inner"   "$(printf '110\n120\nT2\n5')"
il_check lazy  Lazy  "$ROOT/cases/il-lazy"    "$(printf 'before\ncomputing...\nVALUE\nVALUE\n42\n42\nFalse\ncomputed\ncomputed\nTrue\n1\n42\n42\nsync\nsync\n1\npub\n1\nnone\n1\nFalse\nguarded\nTrue\n1')"
il_check volatile Volatile "$ROOT/cases/il-volatile" "$(printf '0\n41\n42\nready\nTrue')"   # @kotlin.concurrent.Volatile -> a real CLR volatile field: modreq(IsVolatile) + `volatile.` prefix (the C# volatile shape) on value-type/ref-type instance fields + a top-level static field
il_check deleg Deleg "$ROOT/cases/il-deleg"   "$(printf 'set count = 7\nget count\n7')"
il_check classdeleg AppKt "$ROOT/cases/il-classdeleg/app.kt" "$(printf 'p1\n1\np2\nc[p2]\n2\np40\n40\n3\nc')"   # #81: CLASS delegation `class Foo : Bar by baz` — the frontend's synthetic `$$delegate_0` IrField + its ctor initializer must be emitted (single/two/expr/generic delegates)
# #70: a genuine `::x`/`obj::p`/`Type::p` callable reference -> a lifted class implementing the REAL stdlib
# KProperty0/KMutableProperty0/KProperty1 (name/get/set/invoke), not the retired `dotkt$KProperty` synthetic.
il_check propref AppKt "$ROOT/cases/il-propref/app.kt" "$(printf 'x\n1\n99\n99\n7\n7\n99\ng\nt2\npay')"
il_check lateinitref AppKt "$ROOT/cases/il-lateinitref/app.kt" "$(printf 'hello\nworld\nworld\nunbound\nname')"   # #66: a callable reference to a `lateinit var` property (bound `b::name` + unbound `Box::name`) lifts a KProperty over the backing FIELD (lateinitGet/setFieldExpr), not a get_/set_ accessor — was a whole-compile abort
il_check extpropref AppKt "$ROOT/cases/il-extpropref/app.kt" "$(printf 'mySimpleName:Foo\nmySimpleName=Foo\ntag=hi')"   # #21: bound (`this::extProp` -> KProperty0) + unbound (`String::extProp` -> KProperty1) + mutable-bound (`this::varExtProp` -> KMutableProperty0, set() path) reference to a top-level EXTENSION property; get/set invoke the static ext accessor with the captured/passed receiver (was "KProperty2 has no lowering")
il_check rwp   Rwp   "$ROOT/cases/il-rwp"     "$(printf 'set n = 5\nget n\n5')"
il_check bymap Bm    "$ROOT/cases/il-bymap"   "$(printf 'Alice\n30')"
il_check topdeleg AppKt "$ROOT/cases/il-topdeleg/app.kt" "$(printf '0\n42\ninit')"   # #70: a TOP-LEVEL delegated property with an arbitrary getValue/setValue provider routes through `x$delegate.getValue/setValue` (static delegate field, null thisRef) — was a whole-compile abort (only member/local delegated props were routed)
il_check del2  D2    "$ROOT/cases/il-deleg2"  "$(printf '0 -> 1\n1 -> 2\n5\nhi')"
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
il_check netbase  Nb  "$ROOT/cases/il-netbase"  "$(printf 'app error\n7')" "$EXCMETA"
il_check netbase2 Nb2 "$ROOT/cases/il-netbase2" "$(printf 'AppError #7\nAppError #21')" "$EXCMETA"
il_check netgen  Ng  "$ROOT/cases/il-netgen"  "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check netgen2 Ng2 "$ROOT/cases/il-netgen2" "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check event   Ev  "$ROOT/cases/il-event"   "$(printf 'changed\nchanged\n2\nchanged\nh fired\nchanged\n4')" "$OBSCOLLMETA"
il_check loopjump LjT "$ROOT/cases/il-loopjump" "$(printf 'break at 3\nsumOdd=9\nouter break at 1,2')"
il_check netgen3 Ng3 "$ROOT/cases/il-netgen3" "$(printf '4\n8\n8\nFalse\nTrue\n20\n99\n3')" "$GMMETA"

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
# N6 (interface events): a public INSTANCE event of a .NET INTERFACE (INotifyPropertyChanged.PropertyChanged) surfaces
# as a `ClrEvent<T>` abstract member, subscribed via `+=`/`-=` on an INTERFACE-typed receiver. facadegen scans the .kt
# imports (il_check_imports); kotc elides the ClrEvent fake-override a .NET-subclass would inherit (isClrEventProperty).
il_check_imports ifaceevent AppKt "$ROOT/cases/il-ifaceevent" "$(printf 'count=3\nfired=True')"
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
il_check_inject inherit Inherit "$ROOT/cases/il-inherit" "$(printf 'run:derived\nshow:button\nbutton')" PInh
il_check_inject geninj GenInj "$ROOT/cases/il-geninj" "$(printf '2\na')" PGI
# (3)+(6): constructed-generic MEMBER types (IList<T>/IReadOnlyList<T>/Dictionary<K,V>/IEnumerable<T>) + the
# transitive injection closure (Gadget/Sprocket are never imported — reached via member-signature hops).
il_check_inject transinj TransInj "$ROOT/cases/il-transinj" "$(printf '1\nw1\n1\nw1\nw1!\n3\nw1\nw1.')" TxRt
il_check_inject cbk Cbk "$ROOT/cases/il-cbk" "$(printf '=v42\nran')" PCbk
il_check_inject clriface ClrIface "$ROOT/cases/il-clriface" "$(printf '2\na')" PIf
il_check_inject clrimpl ClrImpl "$ROOT/cases/il-clrimpl" "$(printf 'draw:circle\ndraw:square\ncircle')" PImpl
# ifacechainvt (#129): a Kotlin class implements an injected .NET interface whose BASE-INTERFACE CHAIN carries a
# value-type generic slot (`IMid<Int> : IBase<Int>`). #128's value-type-generic-interface slot bridge must hold across
# the transitively-inherited base link — the inherited `Get(): Int` and the direct `Rank(Int): Int` both use bare
# int32 slots (not Nullable<int>). Direct + upcast-to-IMid<Int> dispatch.
il_check_inject ifacechainvt IfaceChainVt "$ROOT/cases/il-ifacechainvt" "$(printf '21\n10\n23')" ChainRt
# clrifaceimpl: a Kotlin class IMPLEMENTING a facadegen-injected .NET generic interface (IComparer<String>) — the other
# interop-override samples only EXTEND a base class. bir2cir's DeclarationRename re-stamps the override:true/vis:public
# off the injected interface member + fills its slot, so a direct call, an interface-typed upcast dispatch, AND a BCL
# consumer (List<T>.Sort(IComparer<T>)) all dispatch into the override. (imports System.* -> il_check_imports/facadegen.)
il_check_imports clrifaceimpl ClrIfaceImpl "$ROOT/cases/il-clrifaceimpl" "$(printf '1\n-3\nz,bb,abcd')"
# clrifaceimplvt (#128): the VALUE-TYPE sibling — a class implementing a facadegen-injected .NET generic interface
# instantiated with Int (IComparer<Int>/IEquatable<Int>). The injected `T?` override lowers to `Nullable<int32>` params
# but the constructed slot wants BARE int32; bir2cir's ValueTypeIfaceSlotBridge synthesizes a bare-signature bridge
# forwarding to the Nullable method, else TypeLoadException at type load. Direct + interface-upcast + BCL-Sort dispatch.
il_check_imports clrifaceimplvt ClrIfaceImplVt "$ROOT/cases/il-clrifaceimplvt" "$(printf '2\n-2\nTrue\nFalse\n123')"
# ixname: a .NET type with a CUSTOM-NAMED indexer via [IndexerName("Cell")] — `g[i]`/`g[i]=v` must bind to
# get_Cell/set_Cell (read from the type's DefaultMemberAttribute by bir2cir.NetInteropBinding.DefaultIndexerAccessor),
# not the hardcoded get_Item/set_Item. Regression guard for the custom-indexer-name binding path.
il_check_inject ixname IxName "$ROOT/cases/il-ixname" "$(printf '10\n30\n99')" IxRt
il_check_inject clrasm ClrAsm "$ROOT/cases/il-clrasm" "$(printf '2\n2\n2')" PAsm
il_check_inject selfref SelfRef "$ROOT/cases/il-selfref" "4" PSelf
il_check_inject genim GenIM "$ROOT/cases/il-genim" "$(printf 'hello\nworld')" PGenIM
il_check_inject outref Outref "$ROOT/cases/il-outref" "$(printf 'ok=5\nfail\n2 1\n20\n20\n109\n5\n7 5')" OutR
il_check_inject netattr NetAttr "$ROOT/cases/il-netattr" "$(printf 'widget#7\n42')" Lbl
il_check_inject netattrvararg NetAttrVararg "$ROOT/cases/il-netattr-vararg" "$(printf 'widget#7\n42')" PVararg   # #184: params object[] ctor applied bare (zero args). Runtime-asm name is PVararg (NOT "P") — firgap already builds build/rt-P from a DIFFERENT runtime.cs, and the two run in parallel; a shared build/rt-P races (clobbers firgap's Widget/Engine dll). The C# namespace stays "P".
il_check_inject stackalloc Sa "$ROOT/cases/il-stackalloc" "$(printf '16\n30\n-1\n10\n21')" SpanRt
il_check fmt Fmt "$ROOT/cases/il-fmt" "$(printf '42 items, 87.5%% (ok)\n00007-ff\n[a   ]\n[bb  ]')"
il_check_inject mref Mr "$ROOT/cases/il-mref" "$(printf 'hello world\n0')" MrRt
# cobuild: the GENUINE .NET-async E2E — `Task.Delay(1).await()` truly suspends (imports System.*, so
# il_check_IMPORTS runs facadegen for the await marker). bir2cir's P4 await lowering + the whole cold-core
# SM chain are verified correct; the boxed-enum COROUTINE_SUSPENDED reference-identity issue (once the sole
# remaining fail) is fixed by caching the box (Intrinsics.kt), so this now runs green -> 25 (no XFAIL).
il_check_imports cobuild Cob "$ROOT/cases/il-cobuild" "25"
il_check_imports genasync GenAsync "$ROOT/cases/il-genasync" "7"  # genuine-async isolation: suspend fun with Task.Delay().await(), drained by blockOn
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
# counit: a PUBLIC Unit-returning suspend fun -> a NON-generic public `Task` bridge (coroutine-abi.md §1:
# T=Unit -> Task, not Task<Unit>). bir2cir's BuildBridge emits `Task greet()` and upcasts the
# TaskCompletionSource<Unit>.Task on return; `greet` suspends on step() (sync) so the SM + Unit bridge emit is
# exercised and ilverify-checked. main drives greet via its cold entry.
il_check counit CoUnit "$ROOT/cases/il-counit" "$(printf 'hello 42\ndone')"
# monitordrain: locks the System.Threading.Monitor Wait/Pulse cross-thread DRAIN mechanism that
# the harness blockOn's BlockOnSink is built on (waiter Enter/`while(!done) Wait`/Exit; completer
# Enter/set/`done=true`/Pulse/Exit on the same monitor). `99` is only observable after a genuine
# cross-thread hand-off, so it proves Wait blocks + Pulse wakes. (blockOn's own E2E true-suspension
# waits on await's slow path; this isolates the primitives it depends on — verified drain-correct.)
il_check_imports monitordrain MonitorDrainKt "$ROOT/cases/il-monitordrain" "99"
# cofinally: bundle-6 ① BUG 1 — a genuine `Task.Delay(1).await()` suspension INSIDE a try/finally (the
# use{}/withLock{} shape). bir2cir's EmitTry now gates the finally on the $suspending flag so `close()`
# runs EXACTLY ONCE at the post-resume exit (before the fix it ran EARLY + TWICE). RUNS correct -> close,42
# and passes ilverify (the gated finally shape emits no TaskAwaiter CallVirtOnValueType finding).
il_check_imports cofinally CoFinally "$ROOT/cases/il-cofinally" "$(printf 'close\n42')"
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
# suspendco: bir2cir SuspendColdLowering F2 — a CROSS-MODULE `suspendCoroutine { it.resume(v) }`. Our compiler
# does NOT inline @InlineOnly cross-module, so kotc emits a plain un-inlined `suspendCoroutine(<closure>)` call;
# bir2cir reconstructs the wrapper's SafeContinuation body inside the caller SM (via the public clr-internal
# bridges newSafeContinuation/safeGetOrThrow). F1 — SafeContinuation caches the UNDECIDED/RESUMED boxed enums so a
# SYNC resume's `cur === UNDECIDED` identity check holds (else it wrongly throws "Already resumed"). resume(42)
# -> 42; resumeWithException -> getOrThrow rethrows at the sync point, caught. Sync-completion drain via `main`.
il_check suspendco SuspendCo "$ROOT/cases/il-suspendco" "$(printf '42\ncaught:boom')"
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
# suspendcapture: #34a — a suspend lambda closing over its ENCLOSING instance. bir2cir's SuspendLambda SM
# captures the instance as `__outer` and (the fix) redirects a lambda-body `this` to that field, so `this.n`
# resolves through the capture instead of leaking the SM. Covers value / call-arg / member-method / object /
# nested capture positions, with a local-capture lambda (mk) as the non-regression control.
il_check_imports suspendcapture SuspendCapture "$ROOT/cases/il-suspendcapture" "$(printf '42\n42\n41\n40\n105\n42')"
# suspendvalue: GAP 1/2 (#36) — invoking a suspend functional VALUE `b()` (SuspendFunctionN). No named cold
# entry: the value is a SuspendLambda SM, driven through the stdlib `startSuspendUninterceptedOrReturn` helper.
# Covers a suspend param value (run1), the higher-order times/repeat idiom, a suspend value in a local (local1),
# and a suspend MEMBER building a this-capturing lambda + driving it via a suspend-value call (Box.go).
il_check_imports suspendvalue AppKt "$ROOT/cases/il-suspendvalue" "$(printf '42\n42\n42\n42')"
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
il_check dsl Dsl "$ROOT/cases/il-dsl" "a[Pb]c"
il_check xprop Xprop "$ROOT/cases/il-xprop" "7"
il_check exprbody EB "$ROOT/cases/il-exprbody" "$(printf 'greet\nviaLambda\ncleanup\npos')"
il_check overload OV "$ROOT/cases/il-overload" "$(printf 'S:x\nF:y\nI:7\nbs:p\nbf:q')"
# bundle-6 ④ stdlib-correctness routing (bir2cir)
il_check cmpord   CmpOrd   "$ROOT/cases/il-cmpord"   "$(printf '31\n-31\n0\n-1\nFalse\n-7')"
il_check starproj StarProj "$ROOT/cases/il-starproj" "$(printf '{1=2, 3=4}\n2\n[10, 20, 30]\n3\n20\n[10, 20, 30]\n[10, 20, 30]\n{1=2, 3=4}\nFalse\nFalse')"
# exception / try-catch family (il-exc, il-customexc, il-excmap, il-nestedtry, il-result, il-throwexpr, il-tryexpr,
# il-tryexprop) migrated to the NUnit battery tests/il/fixtures/ExceptionTests.kt (8 methods), gated by
# tests/run-nunit-il.sh; the old per-case dirs + il_check lines were removed in the same change (audit #14).
il_check setlocalbox SetLocalBox "$ROOT/cases/il-setlocalbox" "$(printf '42\n7')"
il_check bytewiden AppKt "$ROOT/cases/il-bytewiden/app.kt" "$(printf '200\n40000\n300\n80000\n200\n-128\n0\n0\n128\n44\n300\n4294967295\n18446744073709551615')"   # #93/#71: Byte/Short/UByte/UShort arith widens to Int/UInt & inc/dec/unaryMinus keep the declared return (bir2cir wraps the lowered op in a conv to dynRet); ilemit needs the unsigned Conv_U1/U2/U4/U8 arms — else the value truncates to the narrow left operand on box
il_check unsignedshr AppKt "$ROOT/cases/il-unsignedshr/app.kt" "$(printf '2147483647\n9223372036854775807\n267386880\n1073741824\n2147483648\n-4')"   # #94: unsigned shr is LOGICAL (zero-filling) — bir2cir lowers a UInt/ULong `shr` to ">>>" (ilemit Shr_Un), not the sign-propagating ">>"; shl + signed shr are the non-regression checks
il_check duration Duration "$ROOT/cases/il-duration" "$(printf -- '5s\n2s\n-1s\nTrue')"
# #156: a genuinely-nullable String (String? = null) UNWRAPPED into a CharSequence?-receiver slot (isNullOrEmpty) — the
# strict nullable-slot path now emits a runtime-conditional adapter wrap so String->dotkt$CharSequence is ilverify-clean.
# #40 (guard): a CROSS-MODULE @InlineOnly + @ClrIntrinsic stdlib fn keeps its @ClrIntrinsic binding across the assembly
# boundary — kotc carries the annotation as OPAQUE ref.dll metadata (attrsJson is unconditional, NOT dropped for
# @InlineOnly) and bir2cir substitutes the plain call to the bound BCL member (`sb[0]='X'` -> set_Chars, Char.is* -> System.Char.Is*).
il_check inlonlyintr AppKt "$ROOT/cases/il-inlonlyintr/app.kt" "$(printf 'Xbc\nTrue\nTrue\nTrue')"
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

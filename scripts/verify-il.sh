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
	[collops2]="bundle-6 P5 RE-DIAGNOSED (the Result-coercion hypothesis was a SYMPTOM, not the root). The bir2cir Result-monomorphization landed: ContinuationErasure now erases every kotlin.Result[X] to Result[object] in ALL builds, so getOrThrow's receiver + the getOrThrow/throwOnFailure/get_value tokens are CLOSED Result[object] (verified via MetadataReader: the pre-fix getOrThrow get_value MemberRef needed a generic context and RESOLVEFAILed; post-fix it resolves to Result1[System.Object]::get_value). il-result still passes (getOrThrow<int> works). But collops2's windowed STILL crashes at throwOnFailure with 'not fully instantiated'. TRUE ROOT (out of a pure bir2cir Result fix's reach): a GENERIC FUNCTION DRIVING A COLD SEQUENCE. Minimal repro: fun <T> f(x: T) = sequence { yield(x) }.toList() crashes (NRE at SequenceBuilderIterator.next); windowed drives the stdlib's own generic windowedIterator<T>, whose SequenceBuilderIterator<List<T>>.resumeWith -> getOrThrow<object> -> throwOnFailure hits 'not fully instantiated'. A user sequence with a CONCRETE element (sequence { yield(listOf(1,2)) }) and a ref-element sequence (sequence<String>) BOTH work — the trigger is the enclosing GENERIC method/SM context, i.e. the generic cold-sequence SM instantiation in SuspendColdLowering + ilemit generic codegen, NOT the Result ABI. Needs coordinated bir2cir-suspend + ilemit generic-SM work; tracked as its own coroutine-generics gap."
	[bymap]="bundle-6 BUG-2 RE-DIAGNOSED (NOT the clrInstance/generic-interface member-dispatch originally hypothesized): the emitted `IDictionary\`2<!!K,!!V>::ContainsKey(!0)` MemberRef metadata is byte-IDENTICAL to a working app-emitted one AND to the stdlib's own working get_Item/get_Keys (verified via MetadataReader: sig `20 01 02 13 00`, parent TypeSpec `15 12 <IDictionary\`2> 02 1e 00 1e 01`). clrMapGet WORKS when called with a concrete instantiation (map.get() for BOTH value-V and both-ref-V); an equivalent transitive shared-generic chain (outer<K,V> -> inner<K,V> -> containsKey) WORKS in an app assembly. It ONLY fails as EntryPointNotFound when the chain getValue -> getOrImplicitDefault<K,V> -> clrMapGet<!!K,!!V> -> IDictionary.ContainsKey runs entirely INSIDE the referenced PersistedAssemblyBuilder-emitted stdlib assembly (a transitive shared-generic interface-TypeSpec MemberRef resolution). This is a PersistedAssemblyBuilder / stdlib-emission-level defect (the same assembly also carries malformed MethodImpls — AbstractMutableList\`1 'signature of body and declaration do not match', MapWithDefaultImpl\`2 get_Item 'does not have an implementation'), NOT an EmitClrCall token bug (the token is correct C#-equivalent IL)."
)
declare -A XFAIL_ILVERIFY=(
)

# The CLR stdlib (kotlin.*) is supplied to kotc via the FRONTEND JAR (scripts/build-stdlib-jar.sh) on
# -classpath, REPLACING the JVM kotlin-stdlib.jar (which leaked java.util.* typealiases). This preserves
# full Kotlin semantics and is the BINDING invariant: kotlin.* comes from the JAR, never from facadegen
# --scan-asm. (legacy coroutines jar dropped 2026-07-03: the cold-core surface is kotlin.clr.blockOn/delay/await.)
CP="$FE_JAR"

# Build the compiler launcher ONCE (a plain Java app). Per-sample invokes then cost ~2s of JVM startup
# instead of ~9s for `gradlew --no-daemon :kotc:run`.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$KOTC"
need_fe_jar

# Result records (one per sample) + the refdll handoff to the ilverify phase live here.
RESULTS="$ROOT/build/verify-il"
rm -rf "$RESULTS"; mkdir -p "$RESULTS"

# Run samples concurrently (each compile is an independent ~2s JVM startup). A job pool caps parallelism.
JOBS="$(nproc 2>/dev/null || echo 4)"; (( JOBS > 6 )) && JOBS=6
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

# UNCONDITIONAL tool builds: the gate tests the CURRENT sources.
build_tool ilemit
# bir2cir: the canonical kotc -> bir2cir -> ilemit pipeline. kotc emits bare kotlin.* FQNs for source-type
# primitives at EVERY position; bir2cir lowers them to the CLR-codegen vocabulary ilemit consumes. App builds run
# in substitute/app mode (no DOTKT_STDLIB_COMPILE), so kotlin.* primitives lower (kotlin.Int -> int, ...).
build_tool bir2cir
# Lower a sample's BIR -> CIR (bir2cir), then emit IL (ilemit). A bir2cir failure folds into the ilemit-error bucket.
il_emit() { # <name> <ildir> <asm> <birdir> [extra ilemit args...]
	local name="$1" ildir="$2" asm="$3" birdir="$4"; shift 4
	local cirdir="$ROOT/build/cir-$name"; rm -rf "$cirdir"; mkdir -p "$cirdir"
	# bir2cir reads the REFERENCE stdlib for the @ClrTypeAlias/@ClrIntrinsic labels: app-build collection/
	# StringBuilder/Regex type tokens and member calls lower from it (bir2cir is the single substitution home).
	local refarg=(); [[ -f "$STDLIB_REF_DLL" ]] && refarg=(--ref "$STDLIB_REF_DLL")
	dotnet "$BIR2CIR_DLL" "$cirdir" "${refarg[@]}" "$birdir"/*.bir.json >/dev/null 2>&1 || return 1
	dotnet "$ILEMIT_DLL" "$ildir" "$asm" "$@" "$cirdir"/*.cir.json >/dev/null 2>&1
}

# S5 FIR-injection metadata for samples that inherit a real .NET base type (façade-free).
build_tool facadegen
EXCMETA="$ROOT/build/exc.meta"
dotnet "$FACADEGEN_DLL" --meta "$EXCMETA" System.Exception System.Console >/dev/null 2>&1
COLLMETA="$ROOT/build/coll.meta"
dotnet "$FACADEGEN_DLL" --meta "$COLLMETA" System.Collections.ObjectModel.Collection >/dev/null 2>&1
OBSCOLLMETA="$ROOT/build/obscoll.meta"
dotnet "$FACADEGEN_DLL" --meta "$OBSCOLLMETA" System.Collections.ObjectModel.ObservableCollection >/dev/null 2>&1
GMMETA="$ROOT/build/gm.meta"
dotnet "$FACADEGEN_DLL" --meta "$GMMETA" System.Runtime.CompilerServices.Unsafe System.Runtime.CompilerServices.RuntimeHelpers System.Collections.ObjectModel.Collection >/dev/null 2>&1

# CLR stdlib (the canonical build under libraries/stdlib/): the RUNTIME assembly is --ref'd into every
# emitted case so a stdlib op resolves to its real Kotlin body (and copied next to each output for the
# run phase); the REFERENCE assembly is bir2cir's @Clr-metadata input. Build if missing, reuse if present.
need_stdlib_ref; need_stdlib_rt

# Build a sample's <srcDir>/runtime.cs into a referenced .NET assembly (name from <runtimeAsm>); echo its path.
build_runtime() { # <srcDir> <runtimeAsm>
	local srcdir="$1" rasm="$2" rt="$ROOT/build/rt-$rasm"
	rm -rf "$rt"; mkdir -p "$rt"
	cp "$srcdir/runtime.cs" "$rt/runtime.cs"
	printf '%s\n' "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>$rasm</AssemblyName><Nullable>disable</Nullable></PropertyGroup></Project>" > "$rt/rt.csproj"
	dotnet build "$rt" -c Release -o "$rt/out" -v q --nologo >/dev/null 2>&1 || true
	echo "$rt/out/$rasm.dll"
}

# Inject (façade-free) a sample's own runtime types AND reference the runtime dll: build runtime.cs, scan the
# .kt imports into a metadata file (facadegen --meta --scan), compile with it, then ilemit with --ref.
il_check_inject() { # <name> <asm> <srcDir> <expected> <runtimeAsm>
	gate
	(
		sample_guard "$1"
		name="$1"; asm="$2"; src="$3"; expected="$4"; rasm="$5"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"; meta="$ROOT/build/$name.meta"
		refdll="$(build_runtime "$src" "$rasm")"; echo "$refdll" > "$RESULTS/refdll-$name"
		RD="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/*/ | tail -1)"
		implist="$ROOT/build/$name.imports"
		"$LAUNCHER" --scan-imports --output "$implist" "$src"/*.kt >/dev/null 2>&1 || true
		dotnet "$FACADEGEN_DLL" --meta "$meta" --refs "$(ls ${RD}*.dll | tr '\n' ';');$refdll" --import-list "$implist" >/dev/null 2>&1 || true
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! CLR_TYPES_METADATA="$meta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit "$name" "$ildir" "$asm" "$birdir" --ref "$refdll" --ref "$STDLIB_RT_DLL"; then
			reason="ilemit error"; exit 0; fi
		cp "$refdll" "$ildir/"; cp "$STDLIB_RT_DLL" "$ildir/"
		if ! actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"; then
			reason="run crash"; detail="$(printf -- '--- expected ---\n%s\n--- actual (before crash) ---\n%s' "$expected" "$actual")"; exit 0; fi
		if [[ "$actual" == "$expected" ]]; then ok=1; else mismatch "$expected" "$actual"; fi
	) &
}

il_check() { # <name> <asm> <srcArg> <expected> [metadataFile]
	gate
	(
		sample_guard "$1"
		name="$1"; asm="$2"; src="$3"; expected="$4"; meta="${5:-}"
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		# The case's .NET-space facade metadata (EXCMETA/COLLMETA/... — System.* injection) ONLY, if any. The stdlib
		# (kotlin.*) is supplied to kotc by the frontend JAR on -classpath, NOT facadegen. --ref the runtime
		# DotKt.Stdlib.dll so a stdlib op (getOrElse, ...) resolves to its real Kotlin body instead of a retired lowering.
		if ! CLR_TYPES_METADATA="${meta:-}" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit "$name" "$ildir" "$asm" "$birdir" --ref "$STDLIB_RT_DLL"; then
			reason="ilemit error"; exit 0; fi
		cp "$STDLIB_RT_DLL" "$ildir/"
		if ! actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"; then
			reason="run crash"; detail="$(printf -- '--- expected ---\n%s\n--- actual (before crash) ---\n%s' "$expected" "$actual")"; exit 0; fi
		if [[ "$actual" == "$expected" ]]; then ok=1; else mismatch "$expected" "$actual"; fi
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
		birdir="$ROOT/build/bir-$name"; ildir="$ROOT/build/il-$name"; meta="$ROOT/build/$name.meta"
		RD="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/*/ | tail -1)"
		implist="$ROOT/build/$name.imports"
		"$LAUNCHER" --scan-imports --output "$implist" "$src"/*.kt >/dev/null 2>&1 || true
		dotnet "$FACADEGEN_DLL" --meta "$meta" --refs "$(ls ${RD}*.dll | tr '\n' ';')" --import-list "$implist" >/dev/null 2>&1 || true
		rm -rf "$birdir" "$ildir"; mkdir -p "$birdir" "$ildir"
		if ! CLR_TYPES_METADATA="$meta" "$LAUNCHER" $src -no-stdlib -classpath "$CP" -d $birdir >/dev/null 2>&1; then
			reason="compile error"; exit 0; fi
		if ! il_emit "$name" "$ildir" "$asm" "$birdir" --ref "$STDLIB_RT_DLL"; then
			reason="ilemit error"; exit 0; fi
		cp "$STDLIB_RT_DLL" "$ildir/"
		if ! actual="$(dotnet "$ildir/$asm.dll" 2>/dev/null)"; then
			reason="run crash"; detail="$(printf -- '--- expected ---\n%s\n--- actual (before crash) ---\n%s' "$expected" "$actual")"; exit 0; fi
		if [[ "$actual" == "$expected" ]]; then ok=1; else mismatch "$expected" "$actual"; fi
	) &
}

il_check m0    M0Kt  "$ROOT/cases/m0/M0.kt"  "$(printf 'sum = 5\nzero\nn=1\nn=2')"
il_check mc1   MC1   "$ROOT/cases/m-c1"      "$(printf 'c = (4, 6)\na.d2 = 25\nrect area=30')"
il_check iface Iface "$ROOT/cases/il-iface"  "$(printf 'Hello\nKonnichiwa')"
il_check overrideprop OverridePropKt "$ROOT/cases/il-overrideprop" "$(printf '21\n42\n7')"   # `override val` accessor fills the base CLASS abstract slot (not a fresh NewSlot) — else concrete subclass TypeLoad-fails
il_check xfaceimpl XFace "$ROOT/cases/il-xfaceimpl" "1"   # cross-file + namespaced interface impl/dispatch (FindMethod key regression)
il_check genhof XHof "$ROOT/cases/il-genhof/app.kt" "$(printf '1\n2\n3')"   # generic fn: (T)->Unit over List<T> (TypeBuilderInstantiation.GetMethod regression)
il_check genclosure GenClo "$ROOT/cases/il-genclosure/app.kt" "$(printf '1\nfn:2\n3\n4\nret:5\nlf:6')"   # closure in a generic fn capturing T-typed values (generic closure class regression)
il_check enum  Enum  "$ROOT/cases/il-enum"   "$(printf 'red\ngreen\nblue')"
# m2 / mi1 consume BCL types via `import System.X` (System.Math, System.Text.StringBuilder) -> the facadegen import
# scan (il_check_imports), NOT a bare il_check (which injects nothing, so the import would not resolve). No runtime.cs.
il_check_imports m2  M2    "$ROOT/cases/m2"         "$(printf 'max(3, 7) = 7\nmin(3, 7) = 3\nabs(-9) = 9')"
il_check_imports mi1 MI1   "$ROOT/cases/m-i1"       "$(printf 'Hello, CLR 42\nlength = 13')"
# alias: `import System.Text.StringBuilder as SB` — the PSI import scan keeps the aliased form (feedback (5)).
il_check_imports alias Alias "$ROOT/cases/il-alias" "$(printf 'hello, alias\n12')"
# dual-rep: the imported .NET view + the stdlib kotlin.text.StringBuilder coexist as two typed views of one CLR
# type; an explicit cast crosses them (rule in docs/dotkt-semantics.md).
il_check_imports dualrep DualRep "$ROOT/cases/il-dualrep" "$(printf 'net\n3\nkt\nnet')"
# taskfam: a same-name .NET arity family — non-generic `Task` and `Task<TResult>` (Kotlin `Task1`) coexist in one
# file; `generic:Task1[T]` cross-refs resolve to the arity-1 definition (docs/dotkt-semantics.md §8d).
il_check_imports taskfam Tf "$ROOT/cases/il-taskfam" "$(printf 'plain=True\ngeneric=42')"
# taskawait: bir2cir SuspendColdLowering P4 REVERSE bridge — the facadegen-injected `Task.await()` marker
# lowered to the cold-core awaiter dance (GetAwaiter/IsCompleted/OnCompleted/GetResult TaskAwaiter STRUCT
# calls). SYNC FAST PATH (already-completed tasks): generic Task<Int>.await() + non-generic Task.await():Unit.
il_check_imports taskawait TaskAwait "$ROOT/cases/il-taskawait" "$(printf '43\n7')"
# taskgen: a GENERIC .NET static factory (Task.FromResult<TResult>) — the seam that lets Kotlin BUILD a
# Task<T> (async interop). kotc's companion generic-static builder declares the method type parameter and
# resolves the return/param against it, so `Task.FromResult(42)` binds as `FromResult<Int>(42): Task<Int>`
# and emits a `clrGenericStatic` node (bir2cir/ilemit already lower it — verified E2E with a hand-authored
# meta). XFAIL until facadegen surfaces the generic `sfun` line (it currently skips m.IsGenericMethod at
# facadegen/Program.cs:557); once it does, this auto-passes ("42").
il_check_imports taskgen Tg "$ROOT/cases/il-taskgen" "42"
# coldcf/coldgen: bir2cir SuspendColdLowering P3 — the cold-core suspend state-machine transform lifted
# from straight-line (P2) to control flow across suspension (if/when via cond-lowering, while/for already
# flat), try/catch with the suspension in the try body (two-level dispatch), a suspend extension fun, and
# the GENERIC SM spike (a generic `suspend fun <T>` -> a generic SM). Sync-completion drain via `main`.
il_check coldcf ColdCf "$ROOT/cases/il-coldcf" "$(printf '11\n12\n3\n1\n2\n99\n32\n101\n-1\n42')"
il_check coldgen ColdGen "$ROOT/cases/il-coldgen" "$(printf '7\nyo\n8\nhi')"
# coldinst: bir2cir SuspendColdLowering P3 wave-2a — INSTANCE suspend members (the SM carries a `$this`
# field; the cold entry is an instance `<name>$dotkt_suspend` on the class) + MEMBER/cross-file suspend
# CALLS (a `callInstance` suspendCall / a same-assembly cross-file top-level suspend call, rewritten to
# the callee's cold shape via the global transformability fixpoint). INST1 (Counter.bump) + INST2
# (Svc.chain -> this.helper()) + INSTGEN (generic Box<T>.get) + MCALL1 (topUse -> c.bump()) + MCALL2
# (crossFileVal, a suspend fun in a second source file). Sync-completion drain via `main`.
il_check coldinst ColdInst "$ROOT/cases/il-coldinst" "$(printf '11\n12\n10\n42\nhi\n101\n7')"
# coldabstract: bundle-6 ① BUG 3 — an abstract-CLASS suspend member's full vtable. Base emits an abstract cold
# entry + an abstract Task<Int> bridge ([KotlinFunction(Suspend)]); Impl overrides both in lockstep; `b.poll()`
# (b: Base) dispatches virtually through the cold entry. Runs sync -> 42 (no await, so ilverify-clean).
il_check coldabstract ColdAbstract "$ROOT/cases/il-coldabstract" "42"
# coldabstract: bundle-6 ① BUG 3 — an abstract-CLASS suspend member's full vtable. Base emits an abstract cold
# entry + an abstract Task<Int> bridge ([KotlinFunction(Suspend)]); Impl overrides both in lockstep; `b.poll()`
# (b: Base) dispatches virtually through the cold entry. Runs sync -> 42 (no await, so ilverify-clean).
il_check coldabstract ColdAbstract "$ROOT/cases/il-coldabstract" "42"
# ifacesuspend: bundle-6 ③ — the INTERFACE half of the abstract/interface suspend round-trip. kotc now tags an
# interface `suspend fun` member with the neutral `"suspend":true`+`resultType` FACT (mirroring the abstract-CLASS
# path), so bir2cir can synthesize the interface cold entry / Task<Int> bridge; Fetcher42 overrides both; `f.fetch()`
# (f: Fetcher) dispatches virtually through the interface cold entry. Runs sync -> 42.
il_check ifacesuspend IfaceSuspend "$ROOT/cases/il-ifacesuspend" "42"
# seqyieldall: yieldAll E2E over the cold core — bir2cir cold-call `sig` disambiguates SequenceScope.yieldAll's
# three same-named `$dotkt_suspend` overloads + ilemit sig-driven external-generic resolution (both landed).
il_check seqyieldall SeqYieldAll "$ROOT/cases/il-seqyieldall" "$(printf 'a,b,c')"
il_check for   ForT  "$ROOT/cases/il-for"     "$(printf 'sum 1..5 = 15\ncountdown 5 = 54321')"
il_check exc   Exc   "$ROOT/cases/il-exc"     "$(printf 'safeDiv(10,2) = 5\nsafeDiv(1,0) = -1')"
il_check ops   Ops   "$ROOT/cases/il-ops"     "$(printf '3\n2\n7\n3\n16\n15\n-1\n3\n5')"
# charminus: Char arithmetic result typing — `Char.minus(Char): Int` (`'a'-'B'`=31, not the blank U+001F
# glyph), while `Char.plus(Int)`/`Char.minus(Int): Char` ('a'+1='b'). kotc forces the operator's declared
# Kotlin return type on the primitive `bin` (conv int / conv char).
il_check charminus Cm "$ROOT/cases/il-charminus" "$(printf '31\n25\nb\nb')"
# digittoint: Char.digitToIntOrNull()/digitToInt() value-type-nullable (Int?) return -> 7/10/null/7. The 'z' case
# prints null via println(Any?), which the stdlib now renders as the string "null" (ConsoleClr println(Any?)).
il_check digittoint Dti "$ROOT/cases/il-digittoint" "$(printf '7\n10\nnull\n7')"
# printlnnull: println/print(null) render the string "null" (Kotlin semantics); non-null values print normally.
il_check printlnnull PrintlnNull "$ROOT/cases/il-printlnnull" "$(printf 'null\nnull5x\nnull')"
# maptostr: a Map operand of println prints Kotlin-style `{a=1, b=2}` (not the raw .NET Dictionary type
# name) — kotc routes Map/MutableMap operands to clrMapToString at the static-type level, mirroring the
# List path (clrCollToString).
il_check maptostr Mts "$ROOT/cases/il-maptostr" "$(printf '{a=1, b=2}\n{x=9}\n[1, 2, 3]')"
il_check mapof1 Mo1 "$ROOT/cases/il-mapof1" "$(printf '1
1
2
1')"
# colstr: a collection/Map operand prints Kotlin-style in EVERY stringify context — string template `"$m"`,
# string `+` concat `"" + l`, and explicit `.toString()` — not just println(x). Same static-type routing to
# clrCollToString/clrMapToString as the println path (bundle-6 FIX 1).
il_check colstr Cstr "$ROOT/cases/il-colstr" "$(printf 'm={a=1, b=2}\nl=[1, 2, 3]\nx={a=1, b=2}\n[1, 2, 3]\n[1, 2, 3]\n{a=1, b=2}')"
# interpnull: a NULL interpolated/concatenated operand renders "null", not an empty append (Kotlin/JVM parity) —
# `"$x"`/`"" + x` route a nullable operand through the stdlib null-safe Any?.toString(); non-null + Map unchanged.
il_check interpnull InterpNull "$ROOT/cases/il-interpnull" "$(printf '[null]\nn=null\nnull\ns=null end\na=5\nnn=7\nm={k=1}')"
il_check math  MathT "$ROOT/cases/il-math"    "$(printf '9\n7\n3\n4')"
il_check str   Str   "$ROOT/cases/il-str"     "$(printf 'HELLO\nhello\nhi\nello\nTrue\nTrue')"
il_check strnum StrNum "$ROOT/cases/il-strnum" "$(printf '42\n-7\n100\nnfe\niae\n3.14\n2.5\ncomma\nnfd')"
il_check ntostr NToStr "$ROOT/cases/il-ntostr" "$(printf '5\nnull\n7\n5\nnull')"   # value-type-nullable/value arg BOXED into a REFERENCED method's object param (EmitCallArgs pt==null path)
il_check cp    Cp    "$ROOT/cases/il-cp"      "$(printf '50\n3.5\nTrue\nTrue\nX')"
il_check ext   Ext   "$ROOT/cases/il-ext"     "$(printf '21\nHI')"
il_check arr   Arr   "$ROOT/cases/il-arr"     "$(printf '10\n30\n99\n3\n139\n139')"
il_check lam   Lam   "$ROOT/cases/il-lambda"  "$(printf '42\n12')"
il_check clo   Clo   "$ROOT/cases/il-closure" "$(printf '15\n105\n17')"
il_check scope Sc    "$ROOT/cases/il-scope"   "$(printf '10\n6\n9\n10\n10\n7')"
il_check coll  Coll  "$ROOT/cases/il-coll"    "$(printf '5\n5\n3\n2\n3\nTrue\nTrue\n3\n1\n4\nTrue\n5')"
il_check coll2 Coll2 "$ROOT/cases/il-coll2"   "$(printf '10\n1-2-3-4\n1, 2, 3, 4\n100')"
il_check coll3 Coll3 "$ROOT/cases/il-coll3"   "$(printf '60\n6')"
il_check seq   Seq   "$ROOT/cases/il-seq"     "$(printf '6,12\n16\n3\n27\n10-20-30\n1,2,3\n4,5,6\n3')"
il_check seqforin SeqForin "$ROOT/cases/il-seqforin" "$(printf 'a\nb')"
il_check char  Char  "$ROOT/cases/il-char"    "$(printf 'True\nTrue\nTrue\nTrue\nA\nz\nTrue\nTrue\n97\nb')"
il_check sort  Sort  "$ROOT/cases/il-sort"    "$(printf '9,6,5,4,3,2,1,1\na,dd,bbb,cccc\ncccc,bbb,dd,a')"
il_check funref Funref "$ROOT/cases/il-funref" "$(printf '2,4,6\n1,4,9,16,25,36\nHi, Kotlin\n105\n107\ncalc100\n203\n42')"
il_check mapdes MapDes "$ROOT/cases/il-mapdes" "$(printf '10\n60\n13\nx=1\ny=2\nz=3\ntotal=6')"
# A6: rule-3 helper calls on CONCRETE generic alias receivers (HashMap/ArrayList/LinkedHashMap: class typeArgs +
# instantiated sig) + Map/MutableMap getOrDefault (bare-V map-defaults helper: retType carry, was BadImageFormat).
il_check mapgen MapGen "$ROOT/cases/il-mapgen" "$(printf '1\n1\n-1\n3\n9\n2\n7\nempty\n20\n50\n5\n6\n6')"
il_check unsgn Unsigned "$ROOT/cases/il-unsigned" "$(printf '4000000100\n4000000000\n18000000000000000000\n60000\n250')"
il_check regex Regex "$ROOT/cases/il-regex" "$(printf 'True\nFalse\na#b#c#\na_b_c\nTrue\nFalse\n42\nnull')"
il_check langtail LangTail "$ROOT/cases/il-langtail" "$(printf '6\nhi\nint:42\nstr:3\nbig:5\nsmall\n700\n9')"
il_check enumbody EnumBody "$ROOT/cases/il-enumbody" "$(printf '+: 8\n-: 4\n*: 12\nPLUS\n9')"
il_check bytearg ByteArg "$ROOT/cases/il-bytearg" "$(printf '5\n3\n7\n9\n4\n100\n-2')"
il_check iterable Iterable "$ROOT/cases/il-iterable" "$(printf '321\n6\n6')"
il_check customexc CustomExc "$ROOT/cases/il-customexc" "$(printf 'error -5\ncode=-5\ncaught:boom\n42')"
il_check comparator Comparator "$ROOT/cases/il-comparator" "$(printf -- '-3\n5\n0')"
il_check use Use "$ROOT/cases/il-use" "$(printf 'close abcd\nn=4\nclose x\ncaught:boom')"
il_check comparable Comparable "$ROOT/cases/il-comparable" "$(printf 'a<b\nc>b\na<=a\n-3\n1.2,1.5,2.0')"
il_check charseq CS "$ROOT/cases/il-charseq" "$(printf '5\ne\n3\ne\n5')"
il_check charseqx CSX "$ROOT/cases/il-charseqx" "$(printf 'False\nFalse')"
il_check charseqs CSStr "$ROOT/cases/il-charseqs" "$(printf '5\ne\nllo\n5\n3\n3\nTrue\nTrue')"
il_check substr Substr "$ROOT/cases/il-substr" "$(printf 'ell\nworld\nhello\nworld')"
il_check subseq SubSeq "$ROOT/cases/il-subseq" "$(printf 'ell\n1\nhel\nllo')"
il_check seqfilter SeqFilter "$ROOT/cases/il-seqfilter" "$(printf '3,4,5,6\n20,40,60\n4\n3,4,5,6\n3')"
il_check nulltostr NullToStr "$ROOT/cases/il-nulltostr" "$(printf 'null\nabc\nnull\nv=null')"
il_check result Result "$ROOT/cases/il-result" "$(printf 'True\n10\n10\nTrue\nnull\n-99\nneg -1\nnull\nfb')"
il_check genstatic GenStatic "$ROOT/cases/il-genstatic" "$(printf '42\nTrue\nTrue\nboom\nhi')"
il_check bmore BMore "$ROOT/cases/il-bmore" "$(printf '5 items\nx = 42\n3.14\n00007\nff\n100%% ok: yes\n0:a,1:b,2:c\n0,20,60')"
il_check chunk Chunk "$ROOT/cases/il-chunk" "$(printf '3,7,5\n3\n1-2-3 4-5\na,b,c\n3\n1,3,5\n9')"
il_check collmore CollMore "$ROOT/cases/il-collmore" "$(printf '20,40\n1,10,2,20,3,30,4,40,5,50\n1,2,3,4,5\n15\n14\n-1\n3\n3')"
il_check tryexpr TryExpr "$ROOT/cases/il-tryexpr" "$(printf '42\n-1\n5\n-7\n4')"
il_check localclass LocalClass "$ROOT/cases/il-localclass" "$(printf '10\n42\n101\n3,4\nTrue\n60')"
il_check collops2 CollOps2 "$ROOT/cases/il-collops2" "$(printf '2,4,6 | 1,3,5\n0:a 1:b 2:c \n1,2,3\n0,1,3,6,10\n100,101,103,106,110\n6,9,12\n3\n-99')"
il_check refcell RefCell "$ROOT/cases/il-refcell" "$(printf '3\n30\nab\n10')"
il_check annot Annot "$ROOT/cases/il-annot" "$(printf 'widget#7\n42')"
il_check props Props "$ROOT/cases/il-props" "$(printf '20\n8\n16\nnot initialized\nready')"
il_check valcls ValCls "$ROOT/cases/il-valclass" "$(printf '1250\n12\n1250\nff\n1010\nff')"
il_check ctorref CtorRef "$ROOT/cases/il-ctorref" "$(printf '(1,2)\n(3,4)\n(9,9)')"
il_check getcls GetClass "$ROOT/cases/il-getclass" "$(printf 'String\nWidget\nWidget\nString')"
il_check_imports forin Forin "$ROOT/cases/il-forin" "$(printf '60\n10,20,30,\n3')"
il_check ldeleg LocalDeleg "$ROOT/cases/il-localdeleg" "$(printf '42\n42\nHI\nWORLD')"
il_check langf LangFeat "$ROOT/cases/il-langfeat" "$(printf '7\n1024\n120\ntf\ncircle=12\nsq=25\n1a\n2b')"
il_check pair  Pair  "$ROOT/cases/il-pair"    "$(printf '3\n4\nx\n10\n11')"
il_check null  Null  "$ROOT/cases/il-null"    "$(printf 'none\nHI\nfallback\nABC\n5')"
il_check nullv MS1   "$ROOT/cases/m-s1/app.kt" "$(printf 'fallback\npresent\nforced\nlen null = -1\nlen hello = 5')"
il_check op    OpT   "$ROOT/cases/il-op/app.kt" "$(printf '(4, 6)\n(2, 2)\n(6, 8)\n(-3, -4)\n3\n4\nTrue\nTrue\nFalse\nTrue\n7\n15')"
il_check dataq Dq    "$ROOT/cases/m-s2/app.kt" "$(printf 'Point(x=3, y=4)\nPoint(x=7, y=9)\nx=3 y=4\na==b: True\na==c: False\nhash eq: True')"
il_check inline InlF "$ROOT/cases/il-inline/app.kt" "$(printf '5\n40\n3\n0')"
il_check inline2 Inl2 "$ROOT/cases/il-inline2" "$(printf '4\n42\n3')"
il_check xinline XInl "$ROOT/cases/il-xinline" "$(printf '20\n42\n105')"
il_check ctor  CtorT "$ROOT/cases/il-ctor/app.kt" "$(printf '12\n25\n5x5\nhi=7\nsolo=0')"
il_check objex Oe    "$ROOT/cases/il-objexpr/app.kt" "$(printf 'hello from anon\n105')"
il_check objgen OGen "$ROOT/cases/il-objgen/app.kt" "$(printf '42\nhi\n7\nok')"
il_check nest  Nst   "$ROOT/cases/il-nested/app.kt" "$(printf 'outer:root\nnode(7)\n14\nleaf 3')"
il_check scast Sc2   "$ROOT/cases/il-smartcast/app.kt" "$(printf 'int:42\nother\nyo\nnone')"
il_check vis   VisT  "$ROOT/cases/il-vis/app.kt" "$(printf '98\nacct\n99')"
il_check throwx Tx   "$ROOT/cases/il-throwexpr/app.kt" "$(printf 'pos\n42\n3')"
il_check enumr Er    "$ROOT/cases/il-enumrich/app.kt" "$(printf '5\nTrue\nFalse\nJUPITER\n1\n9\nEARTH\nMARS\nJUPITER\nTrue\nFalse')"
il_check reqnn Rn    "$ROOT/cases/il-reqnn/app.kt" "$(printf 'h\n7')"
il_check reif  Rf    "$ROOT/cases/il-reified/app.kt" "$(printf 'String\nInt32\nTrue\nFalse\nTrue\nyo\nno')"
il_check iter  Iter  "$ROOT/cases/il-iter"    "$(printf 'x=10\nx=20\nx=30\nsum = 60\nn=3\nn=2\nn=1\nacc = 6')"
il_check inner Inner "$ROOT/cases/il-inner"   "$(printf '110\n120\nT2\n5')"
il_check lazy  Lazy  "$ROOT/cases/il-lazy"    "$(printf 'before\ncomputing...\nVALUE\nVALUE\n42\n42')"
il_check deleg Deleg "$ROOT/cases/il-deleg"   "$(printf 'set count = 7\nget count\n7')"
il_check rwp   Rwp   "$ROOT/cases/il-rwp"     "$(printf 'set n = 5\nget n\n5')"
il_check bymap Bm    "$ROOT/cases/il-bymap"   "$(printf 'Alice\n30')"
il_check mapforin MapForin "$ROOT/cases/il-mapforin" "$(printf 'a=1\nb=2\nc=3\nd=4\n7\nc:3\nd:4')"
il_check del2  D2    "$ROOT/cases/il-deleg2"  "$(printf '0 -> 1\n1 -> 2\n5\nhi')"
il_check gen   Gen   "$ROOT/cases/il-generic" "$(printf '42\n42\nhello\n7\nworld\n3\nthree')"
il_check gen2  Gen2  "$ROOT/cases/il-generic2" "$(printf '99\nIntBox holding an Int\ntag\nNamed holding a String')"
il_check gen3  Gen3  "$ROOT/cases/il-generic3" "$(printf '7\nbanana\n10')"
il_check gen4  Gen4  "$ROOT/cases/il-generic4" "$(printf '42\n42 & hi\n42 & 99\nx')"
il_check gen5  Gen5  "$ROOT/cases/il-generic5" "$(printf '10\n20\n99\nz')"
il_check gen6  Gen6  "$ROOT/cases/il-generic6" "$(printf 'hello\nconsumed: world')"
# A generic class extending a generic base instantiated over its OWN type param (`class D<T> : Base<T>()`):
# the base-ctor call AND inherited generic-base member access must anchor onto the CONSTRUCTED base `Base<!T>`,
# not the open def `Base<>` (else "not fully instantiated" / InvalidProgram). This is the SequenceBuilderIterator shape.
il_check genbase GenBaseKt "$ROOT/cases/il-genbase" "$(printf '42\n42\n42/42\nhi')"
il_check netbase  Nb  "$ROOT/cases/il-netbase"  "$(printf 'app error\n7')" "$EXCMETA"
il_check netbase2 Nb2 "$ROOT/cases/il-netbase2" "$(printf 'AppError #7\nAppError #21')" "$EXCMETA"
il_check netgen  Ng  "$ROOT/cases/il-netgen"  "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check netgen2 Ng2 "$ROOT/cases/il-netgen2" "$(printf '3\nTrue\n2')" "$COLLMETA"
il_check event   Ev  "$ROOT/cases/il-event"   "$(printf 'changed\nchanged\n2\nchanged\nh fired\nchanged\n4')" "$OBSCOLLMETA"
il_check loopjump LjT "$ROOT/cases/il-loopjump" "$(printf 'break at 3\nsumOdd=9\nouter break at 1,2')"
il_check netgen3 Ng3 "$ROOT/cases/il-netgen3" "$(printf '4\n8\n8\nFalse\nTrue\n20\n99\n3')" "$GMMETA"

# Reverse interop via an injected C# host: `il_check_inject` builds the sample's runtime.cs into a referenced .NET
# assembly, scans the .kt imports through facadegen, and --refs it (the same façade-free `import Kfc.X` path the other
# injected-runtime samples use). fieldvis: a .NET host reflects a DotKt-emitted property's CLR accessor visibility.
il_check_inject fieldvis FieldVis "$ROOT/cases/il-fieldvis" "$(printf '150\nme\nPrivate\nPublic')" KfcFv
il_check_inject delegatearg Dlg "$ROOT/cases/il-delegatearg" "$(printf '42\n20\n81')" KfcDel
il_check_inject netenum NetEnum "$ROOT/cases/il-netenum" "$(printf '60\n6\nabbccc')" KfcNetEnum
il_check_inject injbase InjBase "$ROOT/cases/il-injbase" "placed:0" KfcInjB
il_check_inject injfqn InjFqn "$ROOT/cases/il-injfqn" "42" KfcInjF
il_check_inject injstatic InjStatic "$ROOT/cases/il-injstatic" "$(printf 'p=42\n7\n99\n123\np=42\n7\n99\n123')" KfcStatic
il_check_inject injuint InjUint "$ROOT/cases/il-injuint" "$(printf '65542\n42')" Boot
# c1net consumes types from its OWN runtime.cs (Probe assembly) via `import Probe.X` -> il_check_inject (build the
# runtime, scan the imports through facadegen, --ref it). The old no-import-scan @Clr-facade path is gone.
il_check_inject c1net C1Net "$ROOT/cases/il-c1net" "$(printf '42\nhi\n10\n15\n105\n52\n21\n41\n117\n20\n5\nyo!')" Probe
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
il_check_inject clrasm ClrAsm "$ROOT/cases/il-clrasm" "$(printf '2\n2\n2')" PAsm
il_check_inject selfref SelfRef "$ROOT/cases/il-selfref" "4" PSelf
il_check_inject genim GenIM "$ROOT/cases/il-genim" "$(printf 'hello\nworld')" PGenIM
il_check_inject outref Outref "$ROOT/cases/il-outref" "$(printf 'ok=5\nfail\n2 1\n20\n20\n109\n5\n7 5')" OutR
il_check_inject netattr NetAttr "$ROOT/cases/il-netattr" "$(printf 'widget#7\n42')" Lbl
il_check_inject stackalloc Sa "$ROOT/cases/il-stackalloc" "$(printf '16\n30\n-1\n10\n21')" SpanRt
il_check fmt Fmt "$ROOT/cases/il-fmt" "$(printf '42 items, 87.5%% (ok)\n00007-ff\n[a   ]\n[bb  ]')"
il_check_inject mref Mr "$ROOT/cases/il-mref" "$(printf 'hello world\n0')" MrRt
# cobuild: the GENUINE .NET-async E2E — `Task.Delay(1).await()` truly suspends (imports System.*, so
# il_check_IMPORTS runs facadegen for the await marker). bir2cir's P4 await lowering + the whole cold-core
# SM chain are verified correct; the remaining fail is ONE root cause OUTSIDE bir2cir — boxed-enum
# COROUTINE_SUSPENDED loses reference identity (see XFAIL_RUN[cobuild]).
il_check_imports cobuild Cob "$ROOT/cases/il-cobuild" "25"
il_check_imports genasync GenAsync "$ROOT/cases/il-genasync" "7"  # genuine-async isolation: suspend fun with Task.Delay().await(), drained by blockOn
# comaindrain: bundle-6 ① BUG 4 — a GENUINELY-suspending `suspend fun main` (awaits Task.Delay). bir2cir's
# DrainMain now drives the cold body under a REAL RootContinuation<Unit>/TaskCompletionSource<Unit> and
# BLOCKS on tcs.Task until the threadpool resume completes (the old null completion NRE'd on resume). RUNS
# correct -> start,42; carries the same TaskAwaiter CallVirtOnValueType ilverify formal-only finding as genasync.
il_check_imports comaindrain ComainDrain "$ROOT/cases/il-comaindrain" "$(printf 'start\n42')"
# comaindrain: bundle-6 ① BUG 4 — a GENUINELY-suspending `suspend fun main` (awaits Task.Delay). bir2cir's
# DrainMain now drives the cold body under a REAL RootContinuation<Unit>/TaskCompletionSource<Unit> and
# BLOCKS on tcs.Task until the threadpool resume completes (the old null completion NRE'd on resume). RUNS
# correct -> start,42; carries the same TaskAwaiter CallVirtOnValueType ilverify formal-only finding as genasync.
il_check_imports comaindrain ComainDrain "$ROOT/cases/il-comaindrain" "$(printf 'start\n42')"
# monitordrain: locks the System.Threading.Monitor Wait/Pulse cross-thread DRAIN mechanism that
# kotlin.clr.blockOn's BlockOnSink is built on (waiter Enter/`while(!done) Wait`/Exit; completer
# Enter/set/`done=true`/Pulse/Exit on the same monitor). `99` is only observable after a genuine
# cross-thread hand-off, so it proves Wait blocks + Pulse wakes. (blockOn's own E2E true-suspension
# waits on await's slow path; this isolates the primitives it depends on — verified drain-correct.)
il_check_imports monitordrain MonitorDrainKt "$ROOT/cases/il-monitordrain" "99"
# cofinally: bundle-6 ① BUG 1 — a genuine `Task.Delay(1).await()` suspension INSIDE a try/finally (the
# use{}/withLock{} shape). bir2cir's EmitTry now gates the finally on the $suspending flag so `close()`
# runs EXACTLY ONCE at the post-resume exit (before the fix it ran EARLY + TWICE). RUNS correct -> close,42
# and passes ilverify (the gated finally shape emits no TaskAwaiter CallVirtOnValueType finding).
il_check_imports cofinally CoFinally "$ROOT/cases/il-cofinally" "$(printf 'close\n42')"
# coevalorder: bundle-6 ① BUG 2 — strict left-to-right eval across a suspension. In `side() + g()` (g
# suspend), bir2cir now spills the impure LEFT operand into an SM field BEFORE g()'s suspension segments
# so its side effect (println "L") happens before g()'s ("G"). Before the fix: G,L; after: L,G,3.
il_check coevalorder CoEvalOrder "$ROOT/cases/il-coevalorder" "$(printf 'L\nG\n3')"
# lam1/lam2: bundle-6 P3 wave-2b — the suspend-LAMBDA payoff. kotc emits `suspendLambdaNew` (STEP 2,
# landed) and bir2cir builds the SuspendLambda SM, but the generated SM `create()` returns
# Continuation<object> while the stdlib base BaseContinuationImpl.create returns Continuation<Unit> ->
# TypeLoadException at class load. Fix is a bir2cir one-liner (SuspendColdLowering.cs CreateMethod ret);
# XFAIL until it lands, then these flip to PASS (42 / 15) and the entries get pruned.
il_check lam1 Lam1Kt "$ROOT/cases/il-lam1" "42"
il_check lam2 Lam2Kt "$ROOT/cases/il-lam2" "15"
il_check dsl Dsl "$ROOT/cases/il-dsl" "a[Pb]c"
il_check object TObj "$ROOT/cases/il-object" "3"
il_check gfac TGfac "$ROOT/cases/il-gfac" "$(printf '42\nhi')"
il_check xprop Xprop "$ROOT/cases/il-xprop" "7"
il_check exprbody EB "$ROOT/cases/il-exprbody" "$(printf 'greet\nviaLambda\ncleanup\npos')"
il_check overload OV "$ROOT/cases/il-overload" "$(printf 'S:x\nF:y\nI:7\nbs:p\nbf:q')"
il_check mfclosure MfClosure "$ROOT/cases/il-mfclosure" "$(printf '10\n20')"
il_check mflambda MFL "$ROOT/cases/il-mflambda" "$(printf 'A1\nA2\nB1')"
il_check arrops Arro "$ROOT/cases/il-arrops" "$(printf '3\n6,8,10\n14\n2\n-1\n10\n30')"
	il_check collrealkt CollRealKt "$ROOT/cases/il-collrealkt" "$(printf '10
30
500
b,a,c
two')"
il_check mutcoll MutColl "$ROOT/cases/il-mutcoll" "$(printf '2,3,4\n2,4\n2\n0\n11,22,33')"
# bundle-6 ④ stdlib-correctness routing (bir2cir)
il_check cmpord   CmpOrd   "$ROOT/cases/il-cmpord"   "$(printf '31\n-31\n0\n-1\nFalse\n-7')"
il_check mutset   MutSet   "$ROOT/cases/il-mutset"   "$(printf '20\n10,99,30\n10\n99,30')"
il_check hashset2 HashSet2 "$ROOT/cases/il-hashset2" "$(printf '2\n2\n1\n1')"
il_check iscoll   IsColl   "$ROOT/cases/il-iscoll"   "$(printf 'True\nTrue\nTrue\nTrue\nFalse\nFalse')"
il_check excmap   ExcMap   "$ROOT/cases/il-excmap"   "$(printf 'caught-list\ncaught-arr\npst-ok\ncaught-super')"
il_check mapfilter MapF "$ROOT/cases/il-mapfilter" "$(printf '10,20,30,40,50\n2,4\n4,5,6\n100,200,300\n2,4,6')"
il_check nan Nan "$ROOT/cases/il-nan" "$(printf 'True\nTrue\nTrue\nFalse\nFalse')"
il_check nestedtry NestedTry "$ROOT/cases/il-nestedtry" "$(printf 'inner fin\nouter fin\n1')"
il_check trynullable TryNullable "$ROOT/cases/il-trynullable" "$(printf 'fin\n1')"
il_check tryexprop TryExprOp "$ROOT/cases/il-tryexprop" "$(printf 'n=5\n6\nbad=-1\n30')"
il_check setlocalbox SetLocalBox "$ROOT/cases/il-setlocalbox" "$(printf '42\n7')"
il_check nancmp NanCmp "$ROOT/cases/il-nancmp" "$(printf 'False\nFalse\nFalse\nFalse\nTrue\nTrue\nTrue\nFalse')"
il_check whensubj WhenSubj "$ROOT/cases/il-whensubj" "$(printf 'b\n1\nz\n2\nseven')"
il_check safecallnv SafeCallNv "$ROOT/cases/il-safecallnv" "$(printf '120\nnull\n3\nnull\n4')"
il_check rangein RangeIn "$ROOT/cases/il-rangein" "$(printf 'True\n1\nFalse\n2\nTrue')"
il_check duration Duration "$ROOT/cases/il-duration" "$(printf -- '5s\n2s\n-1s\nTrue')"

# Reverse interop: a .NET (C#) host loads the IL-emitted Kotlin assembly and calls a Kotlin class + top-level
# fun. Proves the IL output is a consumable .NET assembly. (Compile-time <Reference> needs per-type contract-
# assembly retargeting — blocked by a Reflection.Emit limitation; see design 5.2. Reflection load works today.)
il_revinterop() {
	(
		sample_guard revinterop
		local asm=KotlinLib src="$ROOT/cases/il-revinterop"
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
		local actual expected; expected="$(printf 'Hi, World\n5')"
		actual="$(dotnet run --project "$ildir/consumer.csproj" -v q -- "$ildir/$asm.dll" 2>/dev/null | grep -vE 'warning|error |\.cs\(' || true)"
		if [[ "$actual" == "$expected" ]]; then ok=1; else mismatch "$expected" "$actual"; fi
	)
}

wait   # let every backgrounded sample finish; each has left exactly one result record
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

# ---- formal IL verification (ilverify), if the tool is available ----
verify_pass=0; declare -a verify_fails=()
ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
REFDIR="$(dirname "$(find /usr/share/dotnet/shared/Microsoft.NETCore.App -name System.Private.CoreLib.dll 2>/dev/null | sort | tail -1)")"
if [[ -n "$ILV" && -d "$REFDIR" ]]; then
	echo "--- ilverify ---"
	declare -A ASMS=( [m0]=M0Kt [mc1]=MC1 [iface]=Iface [enum]=Enum [m2]=M2 [mi1]=MI1 [for]=ForT [exc]=Exc [ops]=Ops [math]=MathT [str]=Str [cp]=Cp [ext]=Ext [arr]=Arr [lam]=Lam [clo]=Clo [scope]=Sc [coll]=Coll [coll2]=Coll2 [coll3]=Coll3 [seq]=Seq [seqforin]=SeqForin [char]=Char [sort]=Sort [funref]=Funref [getcls]=GetClass [forin]=Forin [ldeleg]=LocalDeleg [langf]=LangFeat [mapdes]=MapDes [valcls]=ValCls [ctorref]=CtorRef [unsgn]=Unsigned [regex]=Regex [result]=Result [bmore]=BMore [chunk]=Chunk  [collmore]=CollMore  [tryexpr]=TryExpr  [localclass]=LocalClass [collops2]=CollOps2 [refcell]=RefCell [annot]=Annot [props]=Props [pair]=Pair [null]=Null [nullv]=MS1 [op]=OpT [dataq]=Dq [inline]=InlF [ctor]=CtorT [objex]=Oe [nest]=Nst [scast]=Sc2 [vis]=VisT [throwx]=Tx [enumr]=Er [reqnn]=Rn [reif]=Rf [iter]=Iter [inner]=Inner [lazy]=Lazy [deleg]=Deleg [rwp]=Rwp [bymap]=Bm [del2]=D2 [gen]=Gen [gen2]=Gen2 [gen3]=Gen3 [gen4]=Gen4 [gen5]=Gen5 [gen6]=Gen6 [netbase]=Nb [netbase2]=Nb2 [netgen]=Ng [netgen2]=Ng2 [event]=Ev [netgen3]=Ng3 [loopjump]=LjT [inline2]=Inl2  [c1net]=C1Net [firgap]=FirGap [fmt]=Fmt [cobuild]=Cob [dsl]=Dsl [object]=TObj [gfac]=TGfac [xprop]=Xprop [arrops]=Arro [langtail]=LangTail [enumbody]=EnumBody [fieldvis]=FieldVis [bytearg]=ByteArg [iterable]=Iterable [customexc]=CustomExc [comparator]=Comparator [use]=Use [comparable]=Comparable [charseq]=CS [charseqx]=CSX [charseqs]=CSStr [substr]=Substr [injbase]=InjBase [injfqn]=InjFqn [injstatic]=InjStatic [mfclosure]=MfClosure [mflambda]=MFL [injuint]=InjUint [exprbody]=EB [overload]=OV [collrealkt]=CollRealKt [mutcoll]=MutColl [mapfilter]=MapF [nan]=Nan [nestedtry]=NestedTry [trynullable]=TryNullable [setlocalbox]=SetLocalBox [nancmp]=NanCmp [mapgen]=MapGen [taskfam]=Tf [whensubj]=WhenSubj [safecallnv]=SafeCallNv [rangein]=RangeIn [duration]=Duration [coldcf]=ColdCf [coldgen]=ColdGen [coldinst]=ColdInst [lam1]=Lam1Kt [lam2]=Lam2Kt [taskawait]=TaskAwait [monitordrain]=MonitorDrainKt [genstatic]=GenStatic [genasync]=GenAsync [genbase]=GenBaseKt [strnum]=StrNum [mapof1]=Mo1 [seqyieldall]=SeqYieldAll [charminus]=Cm [digittoint]=Dti [printlnnull]=PrintlnNull [maptostr]=Mts [comaindrain]=ComainDrain [colstr]=Cstr [cmpord]=CmpOrd [mutset]=MutSet [hashset2]=HashSet2 [iscoll]=IsColl [excmap]=ExcMap [tryexprop]=TryExprOp [mapforin]=MapForin )
	for n in $(printf '%s\n' "${!ASMS[@]}" | sort); do
		dll="$ROOT/build/il-$n/${ASMS[$n]}.dll"
		[[ -f "$dll" ]] || continue
		# A sample that references an external runtime dll needs it on ilverify's resolve path too.
		refarg=(); [[ -n "${REFDLL[$n]:-}" ]] && refarg=(-r "${REFDLL[$n]}")
		if dotnet "$ILV" "$dll" -r "$REFDIR/*.dll" -r "$STDLIB_RT_DLL" "${refarg[@]}" 2>&1 | grep -qi 'Verified\.'; then
			echo "VERIFY  $n"; verify_pass=$((verify_pass+1))
		else
			echo "VERIFY FAIL  $n"; verify_fails+=("$n")
		fi
	done
else
	echo "(ilverify not installed; skipping formal verification — 'dotnet tool install -g dotnet-ilverify')"
fi

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

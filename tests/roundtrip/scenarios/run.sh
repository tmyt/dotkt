#!/usr/bin/env bash
# DotKt round-trip gate: a Kotlin assembly compiled by DotKt, consumed AS KOTLIN by another module — the
# Kotlin modifiers with no .NET analog (infix / operator / suspend / top-level) survive the trip. They're
# stamped onto the emitted IL as DotKt.Metadata attributes ([KotlinFunction]/[KotlinFileClass]) by ilemit,
# then projected from the reference assembly into a standard KLIB by dll2klib. This is
# the basis of consuming compiled Kotlin libraries as Kotlin. Inputs: inline heredoc samples
# under build/roundtrip-*. EVERY section runs to completion regardless of earlier failures — results are
# collected, and a crashing consumer app (SIGABRT from the deliberate suspend stub) is captured, never
# allowed to take the gate down mid-script. Verdict: exit 0 iff every failing section is RT_XFAIL-listed;
# an XFAIL section that starts passing prints "FIXED — remove it from the xfail list" and stays green.
# See docs/design-kotlin-metadata-attributes.md.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=roundtrip-scenarios
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the Kotlin<->CLR round-trip gate (no flags). -h for this help.
Green (exit 0) = no section failing outside the RT_XFAIL baseline in this script.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

# The XFAIL baseline — MACHINE-READABLE (name -> reason). The three suspend-consuming sections drive the
# library's suspend funs via the test-harness `dotkt.support.blockOn` (write_coharness; blockOn was dropped
# from the stdlib per design §13 and re-homed to the harness over public primitives). The suspend machinery now emits
# (P2/P3/P4 done: in-module async runs — now covered by the coroutine suite), so these no longer abort on a
# bare `kotlin.coroutines.Continuation` at emit; they surface the REMAINING *cross-module* coroutine gaps
# (below). This gate is the coroutine bundle's cross-module E2E check: when these flip to FIXED, prune them.
declare -A RT_XFAIL=(
	# #109/#86: the cross-module nullable VALUE-TYPE generic gap this section was ADDED to expose. A top-level `T?`
	# in METHOD-PARAM position (firstOr<T>(x: T?, …)) and CTOR-PARAM position (NBox<T>(val value: T?)) keeps the
	# type-param IDENTITY in the emitted signature: the CLR slot is the bare non-null `T` plus a NullableAttribute(2)
	# byte (#86; #147's [KotlinNullableGeneric] carrier covers `Holder<T?>`-style NESTED positions, not this bare
	# top-level dual representation). The consumer therefore COMPILES — the byte restores both slots as `T?` — but the
	# bare `T` slot is a struct at T=Int, so a null cannot cross it: `firstOr<Int>(null, 7)` and `NBox<Int>(null)`
	# emit `ldnull` into an int32 slot (ilverify: StackUnexpected, found Nullobjref, expected Int32). Both live in
	# `main`, so the WHOLE method fails JIT verification: the app prints NOTHING and dies with
	# System.InvalidProgramException at AppKt::main — the observed output is empty, not a partial 7/3/9/4/x. Compiling
	# the same lib against an app WITHOUT the two T=Int null crossings runs 3/4/x, so the fault is confined to the
	# value-type axis of #86 (invisible to every other gate, which drives only T=String); when #86 lands a null-capable
	# representation for a bare `T?` slot the section runs 7/3/9/4/x -> prune it.
	[roundtrip-nullable-vt-generic]="#109/#86/#127: cross-module nullable value-type generic — a top-level T? method/ctor param is emitted as the bare struct-incapable T slot, so the consumer COMPILES (NullableAttribute(2) restores T?) but firstOr<Int>(null,7)/NBox<Int>(null) push null into an int32 slot, so main fails JIT verification and the app produces NO output (System.InvalidProgramException); distinct from #147's nested constructed-type carrier"
)

# MIGRATED to the in-process ProjectReference round-trip lane (tests/roundtrip/consumer RoundtripTests, driven by
# tests/run-nunit-tests.sh) — these sections no longer run here (docs/design-nunit-test-harness.md §3, playbook §3):
#   BATCH 1 (7):
#     roundtrip-enum -> enumInheritedMembers           roundtrip-defargs  -> defaultAndNamedArgs
#     roundtrip-customprop -> customAccessorProperties roundtrip-nrt      -> triStateNullability
#     roundtrip-memext -> memberExtensionFunctions     roundtrip-money/operator-flag -> operatorAndInfixFromRealFlag
#     roundtrip-generic-operator -> genericOperatorGetSet
#   BATCH 2 (8):
#     roundtrip-nothing-return -> nothingReturnGeneric  roundtrip-pkg -> packagedNamespaces
#     roundtrip-inline-member -> inlineMemberNonLocalReturn   roundtrip-generic-inline-ext -> genericInlineExtension
#     roundtrip-dotfile -> dottedFileClass              roundtrip-nonconst-default -> nonConstDefaultArgs
#     roundtrip-comparable -> comparableClass           roundtrip-ubyte -> ubyteFidelity
#   BATCH 3 (1):
#     roundtrip-toplevel-val -> toplevelValVar  (#195: facadegen --import-list now surfaces a field-backed top-level val/var)
# The remaining sections below stay in this shell lane pending later increments (suspend/coharness, negative
# compile-fail and dual-emit-path cases). roundtrip-nothing still has a formal object/string IL gap.
# generic-hof and receiver-lambda are now formally clean after low-arity delegate ABI unification; they remain here
# only because their migration to the in-process ProjectReference lane has not been done yet.
# roundtrip-comparable remains a direct reference-KLIB projection check; its broader ProjectReference twin also
# lives in the in-process lane.

# ---- section result collection (no section may abort the script) -----------------------------------
declare -a SUMMARY=() NEW_FAILS=()
# section_result <name> <ok 0|1> <pass-descr> [fail-detail]
# PASS / FAIL(+detail, reddens) / XFAIL(reason, green) / FIXED(xfail now passing, green).
section_result() {
	local name="$1" ok="$2" descr="$3" detail="${4:-}" line
	if (( ok )); then
		if [[ -v RT_XFAIL[$name] ]]; then
			line="FIXED $name — fixed; remove it from the RT_XFAIL baseline"
		else
			line="PASS  $name ($descr)"
		fi
	elif [[ -v RT_XFAIL[$name] ]]; then
		line="XFAIL $name (${RT_XFAIL[$name]})"
	else
		line="FAIL  $name"
		NEW_FAILS+=("$name")
	fi
	echo "$line"
	if [[ "$line" == FAIL* && -n "$detail" ]]; then printf '%s\n' "$detail"; fi
	SUMMARY+=("$line")
}
# check_output <name> <expected> <actual> <pass-descr> — the common expected==actual section verdict.
check_output() {
	local ok=0
	if [[ "$3" == "$2" ]]; then ok=1; fi
	section_result "$1" "$ok" "$4" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$2" "$3")"
}
# run_app <outvar> <dll> — capture stdout of a possibly-crashing app. The suspend-stub abort exits 134
# (SIGABRT) INSIDE the command substitution; naked `x="$(...)"` would kill the whole gate under set -e,
# so the assignment runs as an `if` condition (errexit-exempt) and the crash is folded into the output.
run_app() {
	local -n _out="$1"
	if _out="$(dotnet "$2" 2>/dev/null)"; then :; else
		_out+="${_out:+$'\n'}(app crashed: exit $?)"
	fi
}

# kotc resolves the stdlib (kotlin.*) from the CLR FRONTEND KLIB (scripts/build-stdlib-klib.sh). (legacy
# coroutines jar dropped 2026-07-03: the consumer drives suspend funs via the test-harness
# dotkt.support.blockOn — see write_coharness.)
# Build the toolchain once (UNCONDITIONALLY — the gate tests the current sources).
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$KOTC"
need_fe_klib
build_tool ilemit; build_tool bir2cir; build_tool dll2klib; build_tool retarget
need_dotnet_reference_sets
# The RUNTIME stdlib joins the reference set: a suspend-carrying DotKt lib references the coroutine runtime
# (DotKt.Stdlib's kotlin.coroutines.Continuation) in its emitted CPS signatures, so retarget/facadegen must be
# able to LOAD it to walk KLib's type surface (else facadegen skips every seed type -> empty meta -> the
# consumer can't resolve the library). Harmless for the non-suspend sections (they reference no stdlib type).
REFS="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL");"

# Project the complete framework reference set once. Each emitted test library gets one additional KLIB,
# exactly mirroring the SDK's ReferencePathWithRefAssemblies pipeline.
REFERENCE_KLIBS="$ROOT/build/roundtrip-reference-klibs"
rm -rf "$REFERENCE_KLIBS"; mkdir -p "$REFERENCE_KLIBS"
printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" > "$REFERENCE_KLIBS/references.rsp"
dotnet "$DLL2KLIB_DLL" --out "$REFERENCE_KLIBS/framework" \
	--jobs "${DOTKT_DLL2KLIB_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1')}" \
	@"$REFERENCE_KLIBS/references.rsp" >/dev/null
case "${OS:-}" in Windows_NT) KLIB_CP_SEP=';' ;; *) KLIB_CP_SEP=':' ;; esac
CP="$FE_KLIB"
while IFS= read -r klib; do CP+="$KLIB_CP_SEP$klib"; done \
	< <(find "$REFERENCE_KLIBS/framework" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)

project_reference_klib() {
	local dll="$1" klib="$2"
	dotnet "$DLL2KLIB_DLL" "$dll" "$klib" >/dev/null
}

# kotc emits bare kotlin.* type tokens (the frontend klib resolves the stdlib to our real kotlin.* declarations); bir2cir
# lowers them to the CLR-codegen vocabulary ilemit consumes. So route every emit through bir2cir (mirrors verify-tests) —
# feeding BIR straight to ilemit would leave kotlin.* tokens un-lowered ("cannot resolve .NET type kotlin.String"). The
# REFERENCE stdlib supplies bir2cir's @ClrTypeAlias labels (built once if missing; the roundtrip types are pure-Kotlin).
need_stdlib_ref; need_stdlib_rt
# emit_il: drop-in for `ilemit <outdir> <asm> [--ref X]... <bir files...>`, inserting the BIR->CIR (bir2cir) lowering.
# Both stages tolerate failure (|| true): a broken emit surfaces as its SECTION's FAIL, not a script abort.
# ilemit references the RUNTIME stdlib (--ref) so REAL emitted kotlin.* types resolve — notably the coroutine
# runtime (`kotlin.coroutines.Continuation`, injected into a suspend fun's CPS signature by bir2cir's suspend
# lowering); and the rt dll is dropped beside the emitted assembly so the run resolves it (mirrors verify-tests).
emit_il() {
	local out="$1" asm="$2"; shift 2
	local refs=() birs=() usrrefs=()
	while (( $# )); do
		# A user `--ref X` (a retargeted DotKt library) goes to ilemit AND — A2 (#61) — to bir2cir, which RESOLVES
		# the facadegen-injected owner FQN against it to bind the .NET call SHAPE (clrStatic/clrInstance/…). Mirrors
		# The compiler-test emit path uses the RUNTIME stdlib only for ilemit (bir2cir reads the REFERENCE stdlib).
		if [[ "$1" == --ref ]]; then refs+=("$2"); usrrefs+=("$2"); shift 2; else birs+=("$1"); shift; fi
	done
	[[ -f "$STDLIB_RT_DLL" ]] && refs+=("$STDLIB_RT_DLL")
	local cir="$out.cir"; rm -rf "$cir"; mkdir -p "$cir"
	# bir2cir reads the REFERENCE stdlib ONLY (DotKt.Private.Stdlib). A consumed cross-module DotKt library references
	# the RUNTIME stdlib (DotKt.Stdlib) in its `[kotlin.clr.*]` round-trip metadata, but bir2cir's ManagedReferenceCatalog
	# ALIASES that reference to the ref twin (same type shapes) — so the runtime stdlib is NOT on --compile-refs here.
	local compile_refs; compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$(refset_join "${usrrefs[@]}")")"
	dotnet "$BIR2CIR_DLL" "$cir" --compile-refs "$compile_refs" "${birs[@]}" >/dev/null 2>&1 || true
	dotnet "$ILEMIT_DLL" "$out" "$asm" --runtime-refs "$(refset_join "${refs[@]}")" "$cir"/*.cir.json >/dev/null 2>&1 || true
	[[ -f "$STDLIB_RT_DLL" ]] && cp "$STDLIB_RT_DLL" "$out/" 2>/dev/null || true
}

# write_coharness <appDir> — drop the coroutine TEST HARNESS (dotkt.support.blockOn) beside a suspend-consuming
# app so it co-compiles. `blockOn` was DROPPED from kotlin.clr (docs/design-coroutine-cold-core-task-bridge.md §13);
# it is a kotlinx/Track-2 primitive, re-implemented HERE in pure Kotlin over the PUBLIC stdlib primitives
# (startCoroutine/Continuation) + System.Threading.Monitor (facadegen-seeded), with ZERO compiler special-casing.
write_coharness() {
	cat > "$1/harness.kt" <<'EOF'
@file:Suppress("UNCHECKED_CAST")
package dotkt.support
import System.Threading.Monitor
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

// Runs [block] on the cold core and BLOCKS until it completes — the runBlocking analog for tests.
public fun <T> blockOn(block: suspend () -> T): T {
    val sink = BlockOnSink()
    block.startCoroutine(sink)
    Monitor.Enter(sink)
    try { while (!sink.done) Monitor.Wait(sink) } finally { Monitor.Exit(sink) }
    sink.exception?.let { throw it }
    return sink.value as T
}
private class BlockOnSink : Continuation<Any?> {
    var done: Boolean = false
    var value: Any? = null
    var exception: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) {
        Monitor.Enter(this)
        try {
            value = result.getOrNull(); exception = result.exceptionOrNull(); done = true
            Monitor.Pulse(this)
        } finally { Monitor.Exit(this) }
    }
}
EOF
}

# ----- MARKER round-trip: Kotlin class-nature facts with no faithful .NET analog survive re-consumption -----
# A `fun interface` (SAM), a `sealed` class/interface, and an `enum class` lower to a plain interface / abstract-class /
# CLR-enum, LOSING the Kotlin nature. ilemit stamps [KotlinFunInterface]/[KotlinSealed]; facadegen reads them back
# (`funinterface`/`sealed` meta lines); ClrTypeInjection restores `status.isFun` / `Modality.SEALED`.
# See docs/dotkt-semantics.md §10.
M="$ROOT/build/roundtrip-markers"; rm -rf "$M"; mkdir -p "$M/lib" "$M/app" "$M/rogue" "$M/libbir" "$M/libil" "$M/appbir" "$M/appil"
cat > "$M/lib/lib.kt" <<'EOF'
package shapes
fun interface Handler { fun on(x: Int): Int }
sealed interface Shape { fun area(): Int }
class Circle(val r: Int) : Shape { override fun area(): Int = r * r * 3 }
class Square(val s: Int) : Shape { override fun area(): Int = s * s }
enum class Color { RED, GREEN, BLUE }
fun runHandler(h: Handler, v: Int): Int = h.on(v)
fun describe(s: Shape): String = "area=" + s.area()
EOF
cat > "$M/app/app.kt" <<'EOF'
import shapes.Handler
import shapes.Shape
import shapes.Circle
import shapes.Square
import shapes.Color
import shapes.runHandler
import shapes.describe
fun classify(s: Shape): String = when (s) {   // exhaustive over the restored sealed hierarchy — no `else` needed
    is Circle -> "circle"
    is Square -> "square"
}
fun main() {
    val h = object : Handler { override fun on(x: Int): Int = x * 10 }
    println(runHandler(h, 5))       // fun interface (nature restored) usable across module
    println(describe(Circle(2)))    // sealed supertype usable across module
    println(classify(Square(3)))    // exhaustive `when` over the restored sealed type
    println(Color.GREEN)            // enum value access (non-regression)
}
EOF
"$LAUNCHER" "$M/lib" -no-stdlib -classpath "$CP" -d "$M/libbir" >/dev/null 2>&1 || true
emit_il "$M/libil" MarkLib "$M/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$M/libil/MarkLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$M/libil/MarkLib.dll" "$M/MarkLib.klib"
"$LAUNCHER" "$M/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$M/MarkLib.klib" -d "$M/appbir" >/dev/null 2>&1 || true
emit_il "$M/appil" MarkApp --ref "$M/libil/MarkLib.dll" "$M/appbir"/*.bir.json
cp "$M/libil/MarkLib.dll" "$M/appil/" 2>/dev/null || true
mkexpected="$(printf '50\narea=12\nsquare\nGREEN')"
run_app mkactual "$M/appil/MarkApp.dll"
# NEGATIVE: `sealed` is cross-module-enforced — a rogue subclass in another module MUST be rejected (proves Modality.SEALED restored).
cat > "$M/rogue/rogue.kt" <<'EOF'
import shapes.Shape
class Rogue : Shape { override fun area(): Int = 0 }
EOF
if "$LAUNCHER" "$M/rogue" -no-stdlib -classpath "$CP$KLIB_CP_SEP$M/MarkLib.klib" -d "$M/roguebir" >/dev/null 2>&1; then rogue_ok=1; else rogue_ok=0; fi
mk_ok=0; if [[ "$mkactual" == "$mkexpected" && "$rogue_ok" == 0 ]]; then mk_ok=1; fi
section_result roundtrip-markers "$mk_ok" "fun interface nature; sealed modality+exhaustive-when+cross-module enforcement; enum" \
	"$(printf -- '--- expected ---\n%s\n--- actual ---\n%s\n--- rogue accepted (want reject): %s ---' "$mkexpected" "$mkactual" "$rogue_ok")"

# ----- CONSUMED-TYPE members reference kotlin.* (BOTH stdlib twins on the ref-reader set) round-trip (#73) -----
# A faithful minimal reduction of the atomicfu cross-module regression: a library declares four wrapper classes with
# simple names that COLLIDE with kotlin.concurrent.atomics.* (AtomicInt/AtomicLong/AtomicBoolean/AtomicRef), each
# backed by a `kotlin.concurrent.atomics.*` field and exposing the `getValue`/`setValue` property-delegate operators
# (which reference `kotlin.reflect.KProperty`). The consumer imports the `atomic(...)` factory + the types and touches
# their members. REGRESSION TARGET for #73: a real MSBuild consumer puts BOTH stdlib twins on facadegen's compile set
# — the REFERENCE twin `DotKt.Private.Stdlib` (what a ref-reader reads) AND the RUNTIME twin `DotKt.Stdlib` (which the
# consumed lib was emitted against, copy-local). So THIS section's facadegen call passes BOTH twins (unlike the other
# sections, which pass only the runtime twin). Pre-fix (#35/#37): every `kotlin.*` type resolved to TWO defining
# assemblies -> facadegen's use-site duplicate-definition check threw -> EmitOneType skipped each atomic type -> the
# consumer got `unresolved reference` on every member (`value`, `incrementAndGet`, `compareAndSet`, ...). The lock-style
# types were unaffected because their members touch only `System.Threading` (a single BCL definition). Fix: a ref-reader
# collapses the twin pair to the reference twin (ManagedReferenceCatalog), so `kotlin.*` resolves once. If the atomic
# types fail to inject, the app fails to compile -> no dll -> empty output -> section FAIL.
AT="$ROOT/build/roundtrip-atomic-twin"; rm -rf "$AT"; mkdir -p "$AT/lib" "$AT/app" "$AT/libbir" "$AT/libil" "$AT/appbir" "$AT/appil"
cat > "$AT/lib/lib.kt" <<'EOF'
@file:OptIn(ExperimentalAtomicApi::class)
package atomicport

import kotlin.concurrent.atomics.AtomicInt as KAtomicInt
import kotlin.concurrent.atomics.AtomicLong as KAtomicLong
import kotlin.concurrent.atomics.AtomicBoolean as KAtomicBoolean
import kotlin.concurrent.atomics.AtomicReference as KAtomicRef
import kotlin.concurrent.atomics.ExperimentalAtomicApi
import kotlin.reflect.KProperty

class AtomicInt internal constructor(@PublishedApi internal val a: KAtomicInt) {
    var value: Int
        get() = a.load()
        set(v) { a.store(v) }
    operator fun getValue(thisRef: Any?, property: KProperty<*>): Int = value
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) { this.value = value }
    fun incrementAndGet(): Int { val n = a.load() + 1; a.store(n); return n }
    fun compareAndSet(expect: Int, update: Int): Boolean = a.compareAndSet(expect, update)
}
class AtomicLong internal constructor(@PublishedApi internal val a: KAtomicLong) {
    var value: Long
        get() = a.load()
        set(v) { a.store(v) }
    operator fun getValue(thisRef: Any?, property: KProperty<*>): Long = value
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Long) { this.value = value }
    fun addAndGet(delta: Long): Long { val n = a.load() + delta; a.store(n); return n }
}
class AtomicBoolean internal constructor(@PublishedApi internal val a: KAtomicBoolean) {
    var value: Boolean
        get() = a.load()
        set(v) { a.store(v) }
    operator fun getValue(thisRef: Any?, property: KProperty<*>): Boolean = value
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Boolean) { this.value = value }
    fun compareAndSet(expect: Boolean, update: Boolean): Boolean = a.compareAndSet(expect, update)
}
class AtomicRef<T> internal constructor(@PublishedApi internal val a: KAtomicRef<T>) {
    var value: T
        get() = a.load()
        set(v) { a.store(v) }
    operator fun getValue(thisRef: Any?, property: KProperty<*>): T = value
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: T) { this.value = value }
    fun compareAndSet(expect: T, update: T): Boolean = a.compareAndSet(expect, update)
}

fun atomic(initial: Int): AtomicInt = AtomicInt(KAtomicInt(initial))
fun atomic(initial: Long): AtomicLong = AtomicLong(KAtomicLong(initial))
fun atomic(initial: Boolean): AtomicBoolean = AtomicBoolean(KAtomicBoolean(initial))
fun <T> atomic(initial: T): AtomicRef<T> = AtomicRef(KAtomicRef(initial))
EOF
cat > "$AT/app/app.kt" <<'EOF'
import atomicport.atomic
import atomicport.AtomicInt
import atomicport.AtomicBoolean
import atomicport.AtomicRef
fun main() {
    val n = atomic(0)
    println(n.incrementAndGet())            // 1   member on re-imported AtomicInt
    n.value = 41                            // direct property SET
    println(n.value + 1)                    // 42
    val b = atomic(false)
    println(b.compareAndSet(false, true))   // True  AtomicBoolean member (CLR System.Boolean.ToString)
    val r = atomic<String?>(null)
    println(r.compareAndSet(null, "hi"))    // True  generic AtomicRef member
    println(r.value)                        // hi
}
EOF
"$LAUNCHER" "$AT/lib" -no-stdlib -classpath "$CP" -opt-in=kotlin.concurrent.atomics.ExperimentalAtomicApi -d "$AT/libbir" >/dev/null 2>&1 || true
emit_il "$AT/libil" AtomicLib "$AT/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$AT/libil/AtomicLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$AT/libil/AtomicLib.dll" "$AT/AtomicLib.klib"
"$LAUNCHER" "$AT/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$AT/AtomicLib.klib" -opt-in=kotlin.concurrent.atomics.ExperimentalAtomicApi -d "$AT/appbir" >/dev/null 2>&1 || true
emit_il "$AT/appil" AtomicApp --ref "$AT/libil/AtomicLib.dll" "$AT/appbir"/*.bir.json
cp "$AT/libil/AtomicLib.dll" "$AT/appil/" 2>/dev/null || true
atexpected="$(printf '1\n42\nTrue\nTrue\nhi')"
run_app atactual "$AT/appil/AtomicApp.dll"
check_output roundtrip-atomic-twin "$atexpected" "$atactual" "consumed types whose members reference kotlin.* re-import with BOTH stdlib twins on the ref-reader set #73"

# ----- KOTLIN `Nothing` RETURN round-trip (#135): companion-static + top-level `fun f(): Nothing` -----
# A `fun f(): Nothing` erases to a CLR `object` return (Nothing has no CLR analog); bir2cir stamps [KotlinNothing]
# on the return, facadegen restores `kotlin.Nothing`, so a consumer's `val r: String = if (c) x else f()` keeps x's
# type instead of widening to Any?. #133 wired the PLAIN method/getter path; #135 extends the READER to the
# companion-static return (which the facadegen companion-static loop read via raw MapRetT -> Any?, now RetTypeSfxN).
# LOAD-BEARING: if either Nothing widened to Any?, the `val r: String = if/else` would fail to compile -> section FAIL.
# STAYS in this shell lane (not migrated to the in-process ProjectReference consumer): a cross-module re-imported
# Nothing branch merges an `object`-returning call with a `string`, which the in-process lane's ilverify phase
# rejects (StackUnexpected object/string) though it RUNS green (the else branch throws) — a formal-only cross-module
# Nothing IL gap tracked as #197; the shell lane asserts only stdout so it keeps this RUN coverage.
NO="$ROOT/build/roundtrip-nothing"; rm -rf "$NO"; mkdir -p "$NO/lib" "$NO/app" "$NO/libbir" "$NO/libil" "$NO/appbir" "$NO/appil"
cat > "$NO/lib/lib.kt" <<'EOF'
class Boom {
    companion object { fun boom(): Nothing = throw RuntimeException("boom") }   // companion-static Nothing (#135)
}
fun fail(msg: String): Nothing = throw RuntimeException(msg)                    // top-level Nothing (#133 baseline)
EOF
cat > "$NO/app/app.kt" <<'EOF'
fun pick(n: Int): String {
    val r: String = if (n >= 0) "kept" else Boom.Companion.boom()   // companion-static Nothing keeps r: String
    return if (n >= 0) r else fail("x")                             // top-level Nothing keeps the expr: String
}
fun main() { println(pick(1)) }
EOF
"$LAUNCHER" "$NO/lib" -no-stdlib -classpath "$CP" -d "$NO/libbir" >/dev/null 2>&1 || true
emit_il "$NO/libil" NothingLib "$NO/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NO/libil/NothingLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$NO/libil/NothingLib.dll" "$NO/NothingLib.klib"
"$LAUNCHER" "$NO/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$NO/NothingLib.klib" -d "$NO/appbir" >/dev/null 2>&1 || true
emit_il "$NO/appil" NothingApp --ref "$NO/libil/NothingLib.dll" "$NO/appbir"/*.bir.json
cp "$NO/libil/NothingLib.dll" "$NO/appil/" 2>/dev/null || true
noexpected="kept"
run_app noactual "$NO/appil/NothingApp.dll"
check_output roundtrip-nothing "$noexpected" "$noactual" "companion-static + top-level fun f(): Nothing round-trips (does not widen to Any?) #135"

# ----- SUSPEND `Nothing` return round-trip (#135/#151): `suspend fun f(): Nothing` -----
# The facadegen READER reads [KotlinNothing] before the Task unwrap; bir2cir's SuspendColdLowering.BuildBridge stamps
# retNothing on the Task<Nothing> bridge return (#151), so RoundtripMetadata emits [KotlinNothing] and facadegen
# restores the Nothing return: `sfail()` does NOT widen to Any?, so the lambda types as `suspend () -> Int` and `z: Int`.
NS="$ROOT/build/roundtrip-nothing-suspend"; rm -rf "$NS"; mkdir -p "$NS/lib" "$NS/app" "$NS/libbir" "$NS/libil" "$NS/appbir" "$NS/appil"
cat > "$NS/lib/lib.kt" <<'EOF'
suspend fun sfail(): Nothing = throw RuntimeException("sfail")
EOF
cat > "$NS/app/app.kt" <<'EOF'
import dotkt.support.blockOn
fun main() {
    val z: Int = blockOn { if (1 >= 0) 7 else sfail() }   // sfail(): Nothing keeps the lambda `suspend () -> Int`
    println(z)
}
EOF
"$LAUNCHER" "$NS/lib" -no-stdlib -classpath "$CP" -d "$NS/libbir" >/dev/null 2>&1 || true
emit_il "$NS/libil" SNothingLib "$NS/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NS/libil/SNothingLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$NS/libil/SNothingLib.dll" "$NS/SNothingLib.klib"
write_coharness "$NS/app"
"$LAUNCHER" "$NS/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$NS/SNothingLib.klib" -d "$NS/appbir" >/dev/null 2>&1 || true
emit_il "$NS/appil" SNothingApp --ref "$NS/libil/SNothingLib.dll" "$NS/appbir"/*.bir.json
cp "$NS/libil/SNothingLib.dll" "$NS/appil/" 2>/dev/null || true
run_app nsactual "$NS/appil/SNothingApp.dll"
check_output roundtrip-nothing-suspend "7" "$nsactual" "suspend fun f(): Nothing round-trips (bir2cir stamps [KotlinNothing] on the Task-bridge return) #135/#151"

R="$ROOT/build/roundtrip"; rm -rf "$R"; mkdir -p "$R/lib" "$R/app" "$R/libbir" "$R/libil" "$R/appbir" "$R/appil"

# The Kotlin LIBRARY: a class with infix/operator/(member)suspend members + top-level (plain + suspend) functions.
cat > "$R/lib/lib.kt" <<'EOF'
class Vec(val x: Int, val y: Int) {
    infix fun dot(o: Vec): Int = x * o.x + y * o.y
    operator fun plus(o: Vec): Vec = Vec(x + o.x, y + o.y)
    fun show(): String = "(" + x + ", " + y + ")"
    suspend fun scaleAsync(k: Int): Vec = Vec(x * k, y * k)   // member suspend returning a USER type
}
fun greet(name: String): String = "Hi, " + name
suspend fun addAsync(a: Int, b: Int): Int = a + b
EOF

# The Kotlin CONSUMER: uses every restored modifier with idiomatic Kotlin syntax.
cat > "$R/app/app.kt" <<'EOF'
import dotkt.support.blockOn
fun main() {
    val a = Vec(1, 2)
    val b = Vec(3, 4)
    println(a dot b)                          // infix notation
    println((a + b).show())                   // operator +
    println(greet("Vec"))                     // top-level function (no qualifier)
    println(blockOn { addAsync(20, 22) })       // top-level suspend fun, awaited
    println(blockOn { a.scaleAsync(3) }.show())  // member suspend fun returning a user type, awaited
}
EOF

# 1. compile + emit + retarget the library (the emit stamps [KotlinFunction]/[KotlinFileClass]).
"$LAUNCHER" "$R/lib" -no-stdlib -classpath "$CP" -d "$R/libbir" >/dev/null 2>&1 || true
emit_il "$R/libil" KLib "$R/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$R/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
# 2. facadegen reads the attributes back into the injection metadata.
project_reference_klib "$R/libil/KLib.dll" "$R/KLib.klib"
write_coharness "$R/app"
# 3. compile the consumer WITH the metadata (the injector restores infix/operator/suspend/top-level on FIR).
"$LAUNCHER" "$R/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$R/KLib.klib" -d "$R/appbir" >/dev/null 2>&1 || true
emit_il "$R/appil" KApp --ref "$R/libil/KLib.dll" "$R/appbir"/*.bir.json
cp "$R/libil/KLib.dll" "$R/appil/" 2>/dev/null || true

expected="$(printf '11\n(4, 6)\nHi, Vec\n42\n(3, 6)')"
run_app actual "$R/appil/KApp.dll"
check_output roundtrip "$expected" "$actual" "infix / operator / suspend / top-level restored from a DotKt assembly"

# ----- GENERIC round-trip, COMBINED with every other round-tripping feature, consumed as Kotlin -----
# Exercises user generics in every POSITION (class type param, member, return, parameter, two type params, generic
# method on a generic class) AND combined with each restored modifier (operator, infix, extension, extension operator,
# top-level suspend, nullable, default arg, vararg). Guards the coordinated fixes:
#   - facadegen: a root-namespace generic open name was `.Box` (leading dot); `Supported`/`CrossType` dropped a generic
#     user type in a signature (`Box<T>` -> Any?) so the whole function vanished.
#   - ilemit: a generic type was named `Box` without the CLR `Box`1` arity (cross-assembly `GetType` missed it); a
#     generic EXTENSION call omitted the `__self` receiver shape; a generic fn with a DEFAULT arg had fewer shapes than
#     the single .NET method's params (now tolerated + default-filled).
#   - injector: `coneOf` lost the method type variable inside `generic:Box:T` (resolved `T` -> Any?, so a returned
#     `Box<T>` became `Box<object>` and crashed at the call site); the generic branch ignored ext receiver / inline /
#     infix / operator / vararg / default-arg overloads (now one unified path).
# (reified generics already worked — a generic method with no carried type. Generic-CLASS member `suspend` is a separate
# pre-existing coroutine×generics limitation that fails the same way WITHOUT round-trip, so it's covered elsewhere.)
GG="$ROOT/build/roundtrip-generic"; rm -rf "$GG"; mkdir -p "$GG/lib" "$GG/app" "$GG/libbir" "$GG/libil" "$GG/appbir" "$GG/appil"
cat > "$GG/lib/lib.kt" <<'EOF'
class Pair2<A, B>(val first: A, val second: B)                       // two type params
class Box<T>(val value: T) {
    fun get(): T = value
    operator fun plus(o: Box<T>): Pair2<T, T> = Pair2(value, o.value) // generic + operator
    infix fun with(o: Box<T>): Pair2<T, T> = Pair2(value, o.value)    // generic + infix
    fun <R> mapTo(f: (T) -> R): R = f(value)                          // generic METHOD on a generic class
}
class Holder<A, B>(val a: A, val b: B) { val label: String get() = "$a/$b" }  // two type params + custom getter
fun <T> wrap(x: T): Box<T> = Box(x)                                  // generic top-level, generic RETURN type
fun <T> unwrap(b: Box<T>): T = b.get()                              // generic top-level, generic PARAM type
fun <T> Box<T>.twice(): Pair2<T, T> = Pair2(value, value)           // generic EXTENSION on a generic type
operator fun <T> Box<T>.times(n: Int): Int = n                      // generic extension OPERATOR
suspend fun <T> echoAsync(x: T): T = x                             // generic + top-level SUSPEND
fun <T> orDefault(x: T?, label: String = "none"): String =         // generic + NULLABLE + DEFAULT arg
    if (x == null) label else x.toString()
fun <T> countAll(vararg xs: T): Int = xs.size                      // generic + VARARG
EOF
cat > "$GG/app/app.kt" <<'EOF'
import dotkt.support.blockOn
fun main() {
    val a = Box(3); val b = Box(4)
    println((a + b).first)                    // 3    generic operator +
    println((a with b).second)                // 4    generic infix
    println(Box(5).mapTo { it * 2 })          // 10   generic method on a generic class (+ lambda)
    println(Box(5).get())                     // 5    generic member
    println(Holder(1, "z").label)             // 1/z  two type params + custom getter
    println(wrap(99).get())                   // 99   generic return type
    println(unwrap(Box(8)))                   // 8    generic param type
    println(Box(6).twice().first)             // 6    generic extension on a generic type
    println(Box(6) * 7)                       // 7    generic extension operator
    println(blockOn { echoAsync("hi") })  // hi   generic top-level suspend
    println(orDefault<String>(null))          // none generic + nullable + default omitted
    println(orDefault("set"))                 // set  default present
    println(countAll(1, 2, 3, 4))             // 4    generic vararg
}
EOF
"$LAUNCHER" "$GG/lib" -no-stdlib -classpath "$CP" -d "$GG/libbir" >/dev/null 2>&1 || true
emit_il "$GG/libil" KLib "$GG/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$GG/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$GG/libil/KLib.dll" "$GG/KLib.klib"
write_coharness "$GG/app"
"$LAUNCHER" "$GG/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$GG/KLib.klib" -d "$GG/appbir" >/dev/null 2>&1 || true
emit_il "$GG/appil" KApp --ref "$GG/libil/KLib.dll" "$GG/appbir"/*.bir.json
cp "$GG/libil/KLib.dll" "$GG/appil/" 2>/dev/null || true
gexpected="$(printf '3\n4\n10\n5\n1/z\n99\n8\n6\n7\nhi\nnone\nset\n4')"
run_app gactual "$GG/appil/KApp.dll"
check_output roundtrip-generic "$gexpected" "$gactual" "user generics in every position × operator/infix/extension/suspend/nullable/default/vararg"

# ----- NULLABLE VALUE-TYPE generic, CROSS-MODULE (#109) -----------------------------------------------
# #86 is a CROSS-MODULE representation defect: a top-level `T?` PARAMETER kept as bare `T` so it can survive the facadegen
# round-trip is unsound at VALUE-TYPE instantiations (a bare struct T cannot hold null). But every existing cross-module
# gate exercises this family only at T=String — roundtrip-generic drives `orDefault<String>` (a reference type, where
# bare-T is trivially sound), the MSBuild nullable-generic sample consumes `holderOf<String>`, and the same-compilation
# IL lane (il-nullable-generic-list / il-genarrlam) never crosses the module boundary where #86 lives. So a regression in
# cross-module bare-T handling at a value type would be INVISIBLE to every gate. This section closes that axis: a lib
# declares a nullable-value-type generic METHOD param (`firstOr<T>(x: T?, d: T)`) and CTOR param (`NBox<T>(value: T?)`),
# compiled SEPARATELY, then consumed by an app that instantiates BOTH at T=Int (a value type) with a null argument
# crossing the boundary — plus a T=String non-regression. (The `val value: T?` BACKING FIELD is not the subject: a bare
# `T?` field is object-erased by NullableGenericErasure and holds a genuine null; only the ctor's `T?` PARAM reaches the
# consumer as a bare `T` slot.) If #86's bare-T representation is unsound at T=Int this section FAILs (documented in
# RT_XFAIL against #86); today it is driven live so a fix flips it to FIXED.
NV="$ROOT/build/roundtrip-nullable-vt-generic"; rm -rf "$NV"; mkdir -p "$NV/lib" "$NV/app" "$NV/libbir" "$NV/libil" "$NV/appbir" "$NV/appil"
cat > "$NV/lib/lib.kt" <<'EOF'
class NBox<T>(val value: T?) {          // nullable value-type generic CTOR PARAM (the bare-T-for-T? representation, #86)
    fun orElse(d: T): T = value ?: d    // reads the (object-erased) nullable generic field across the module boundary
}
fun <T> firstOr(x: T?, d: T): T = x ?: d   // top-level nullable value-type generic PARAM, consumed cross-module
EOF
cat > "$NV/app/app.kt" <<'EOF'
fun main() {
    println(firstOr<Int>(null, 7))       // 7   value-type T=Int, null crosses the module boundary
    println(firstOr(3, 7))               // 3   value-type T=Int, present
    println(NBox<Int>(null).orElse(9))   // 9   null through the nullable value-type generic CTOR PARAM
    println(NBox(4).orElse(9))           // 4   same ctor param, present
    println(firstOr<String>(null, "x"))  // x   reference-type non-regression
}
EOF
"$LAUNCHER" "$NV/lib" -no-stdlib -classpath "$CP" -d "$NV/libbir" >/dev/null 2>&1 || true
emit_il "$NV/libil" NvLib "$NV/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NV/libil/NvLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$NV/libil/NvLib.dll" "$NV/NvLib.klib"
"$LAUNCHER" "$NV/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$NV/NvLib.klib" -d "$NV/appbir" >/dev/null 2>&1 || true
emit_il "$NV/appil" NvApp --ref "$NV/libil/NvLib.dll" "$NV/appbir"/*.bir.json
cp "$NV/libil/NvLib.dll" "$NV/appil/" 2>/dev/null || true
nvexpected="$(printf '7\n3\n9\n4\nx')"
run_app nvactual "$NV/appil/NvApp.dll"
check_output roundtrip-nullable-vt-generic "$nvexpected" "$nvactual" "cross-module nullable VALUE-TYPE generic (T? method param + ctor param) instantiated at T=Int (#109/#86)"

# ----- HIGHER-ORDER generics: a function-type parameter whose ARG/RETURN is a generic user type (`(Box<U>)->Box<V>`) -----
# The metadata type grammar is a recursive structured type-node tree (an `fn` node's `ret`/`params` are themselves type
# nodes), so a generic user type — an `fqn` node with `args` — nests inside a lambda parameter: top-level / member /
# extension / infix / operator / inline all carry it.
# Still in this shell lane pending mechanical migration to the in-process ProjectReference consumer. Both producer
# and consumer now use the canonical low-arity `System.Func`2` ABI; this scenario guards that cross-module identity.
HF="$ROOT/build/roundtrip-generic-hof"; rm -rf "$HF"; mkdir -p "$HF/lib" "$HF/app" "$HF/libbir" "$HF/libil" "$HF/appbir" "$HF/appil"
cat > "$HF/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) { fun get(): T = value }
fun <U, V> apply2(f: (Box<U>) -> Box<V>, x: Box<U>): Box<V> = f(x)        // top-level, lambda arg+ret generic user types
class Wrap<T>(val v: T) { fun <U, V> route(f: (Box<U>) -> Box<V>, x: Box<U>): Box<V> = f(x) }  // member
fun <U, V> Box<U>.mapBox(f: (Box<U>) -> Box<V>): Box<V> = f(this)         // extension
infix fun <U, V> Box<U>.pipe(f: (Box<U>) -> Box<V>): Box<V> = f(this)     // infix extension
operator fun <U, V> Box<U>.times(f: (Box<U>) -> Box<V>): Box<V> = f(this) // operator extension
inline fun <T, U, V, W> Box<T>.alsoMap(f: (Box<U>) -> Box<V>, w: W): Box<W> = Box(w)  // inline + 4 type params
EOF
cat > "$HF/app/app.kt" <<'EOF'
fun main() {
    val inc: (Box<Int>) -> Box<String> = { Box(it.get().toString() + "!") }
    println(apply2(inc, Box(5)).get())                       // 5!
    println(Wrap("w").route(inc, Box(6)).get())              // 6!
    println(Box(7).mapBox(inc).get())                        // 7!
    println((Box(8) pipe inc).get())                         // 8!
    println((Box(9) * inc).get())                            // 9!
    println(Box(1).alsoMap<Int, Int, String, Int>(inc, 42).get())  // 42 (inline ext, explicit type args)
}
EOF
"$LAUNCHER" "$HF/lib" -no-stdlib -classpath "$CP" -d "$HF/libbir" >/dev/null 2>&1 || true
emit_il "$HF/libil" KLib "$HF/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$HF/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$HF/libil/KLib.dll" "$HF/KLib.klib"
"$LAUNCHER" "$HF/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$HF/KLib.klib" -d "$HF/appbir" >/dev/null 2>&1 || true
emit_il "$HF/appil" KApp --ref "$HF/libil/KLib.dll" "$HF/appbir"/*.bir.json
cp "$HF/libil/KLib.dll" "$HF/appil/" 2>/dev/null || true
hfexpected="$(printf '5!\n6!\n7!\n8!\n9!\n42')"
run_app hfactual "$HF/appil/KApp.dll"
check_output roundtrip-generic-hof "$hfexpected" "$hfactual" "generic user types nested in a lambda parameter: top-level/member/extension/infix/operator/inline"

# ----- RECEIVER-LAMBDA parameter `P.() -> Unit` consumed cross-module as Kotlin (#145, Avalonia report E(b)) -----
# A `block: Panel.() -> Unit` param is a Kotlin RECEIVER function type: the lambda body gets an implicit `this: Panel`,
# so `apply1 { margin = 4 }` resolves `margin` to `Panel.margin`. Kotlin lowers `P.()->Unit` to `Function1<P,Unit>`
# carrying the `kotlin.ExtensionFunctionType` annotation, then flattens the receiver to the first CLR delegate arg
# (`KAction`1[Panel]`) — erasing the "was a receiver" bit. kotc now carries it in the BIR `fn.recv`; bir2cir stamps a
# bare `[KotlinExtensionFunctionType]` on the delegate param; facadegen moves the delegate's first arg back into the
# fn receiver; ClrTypeInjection restores `Panel.() -> Unit` (an ExtensionFunctionType cone) so the consumer's lambda
# gets `this: Panel`. Without the round-trip the injected param degrades to a receiver-less `(Panel)->Unit` and the
# consumer fails with `unresolved reference 'margin'`. Also covers a member `Panel.() -> Unit` and multi-param mix.
# Still in this shell lane pending mechanical migration to the in-process ProjectReference consumer. The receiver
# marker remains Kotlin metadata while the physical low-arity delegate is `System.Action`1` on both sides.
RL="$ROOT/build/roundtrip-receiver-lambda"; rm -rf "$RL"; mkdir -p "$RL/lib" "$RL/app" "$RL/libbir" "$RL/libil" "$RL/appbir" "$RL/appil"
cat > "$RL/lib/lib.kt" <<'EOF'
package ui
class Panel { var margin: Int = 0; var pad: Int = 0 }
fun apply1(block: Panel.() -> Unit): Panel { val p = Panel(); p.block(); return p }
fun column(configure: Panel.() -> Unit, build: () -> Unit): Int { val p = Panel(); p.configure(); build(); return p.margin }
class Builder(val base: Int) {
    fun make(setup: Panel.() -> Unit): Int { val p = Panel(); p.setup(); return p.margin + base }   // member receiver-lambda param
    val preset: Panel.() -> Unit = { margin = 8 }   // member property typed P.() -> Unit (property-position marker)
}
val defaultInit: Panel.() -> Unit = { margin = 9 }  // top-level val typed P.() -> Unit (field-position marker)
EOF
cat > "$RL/app/app.kt" <<'EOF'
import ui.Panel
import ui.apply1
import ui.column
import ui.Builder
import ui.defaultInit
fun main() {
    val p = apply1 { margin = 4; pad = 1 }        // implicit this: Panel -> margin/pad resolve
    println(p.margin)                              // 4
    println(column({ margin = 7 }, { }))           // 7   receiver half + plain-lambda half
    println(Builder(100).make { margin = 5 })      // 105 member receiver-lambda param
    val q = Panel(); defaultInit.invoke(q); println(q.margin)          // 9   top-level receiver-typed val restored
    val r = Panel(); Builder(0).preset.invoke(r); println(r.margin)    // 8   member receiver-typed property restored
}
EOF
"$LAUNCHER" "$RL/lib" -no-stdlib -classpath "$CP" -d "$RL/libbir" >/dev/null 2>&1 || true
emit_il "$RL/libil" UiLib "$RL/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$RL/libil/UiLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$RL/libil/UiLib.dll" "$RL/UiLib.klib"
"$LAUNCHER" "$RL/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$RL/UiLib.klib" -d "$RL/appbir" >/dev/null 2>&1 || true
emit_il "$RL/appil" UiApp --ref "$RL/libil/UiLib.dll" "$RL/appbir"/*.bir.json
cp "$RL/libil/UiLib.dll" "$RL/appil/" 2>/dev/null || true
rlexpected="$(printf '4\n7\n105\n9\n8')"
run_app rlactual "$RL/appil/UiApp.dll"
check_output roundtrip-receiver-lambda "$rlexpected" "$rlactual" "receiver-lambda P.() -> Unit restored cross-module: param (top-level/member/multi) + top-level-val + member-property positions #145"

# ----- MEMBER-declared extension PROPERTIES + SUSPEND member extensions -----
# Member extension property (`class C { val T.p }`): restored via a `memextprop` meta line (a `get_p(__self)`/
# `set_p(__self,v)` member method) as a member property with an extension receiver; read/write inside `with(c)` routes
# to C's get_/set_ with the extension receiver prepended. Suspend member extension (`suspend fun T.f()` in a class):
# emitted with the SM nested in C (so it reaches PROTECTED members), exposed via a normal suspend member the consumer
# awaits. Both at public + protected visibility.
MP="$ROOT/build/roundtrip-memext2"; rm -rf "$MP"; mkdir -p "$MP/lib" "$MP/app" "$MP/libbir" "$MP/libil" "$MP/appbir" "$MP/appil"
cat > "$MP/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) { fun get(): T = value }
open class Lib(val k: Int) {
    val Box<Int>.lbl: String get() = "lbl:" + (get() + k)        // member extension property (val)
    var Box<Int>.scaled: Int                                      // member extension property (var)
        get() = get() * k
        set(v) { last = v + k }
    var last: Int = 0
    protected val Box<Int>.secret: Int get() = get() + 1000      // protected member extension property
    fun peek(b: Box<Int>): Int = b.secret
    suspend fun Box<Int>.fetch(): Int = get() + k               // suspend member extension (public)
    protected suspend fun Box<Int>.hidden(): Int = get() * 100 + k  // protected suspend member ext
    suspend fun useFetch(b: Box<Int>): Int = b.fetch()         // exposed via a normal suspend member
    suspend fun useHidden(b: Box<Int>): Int = b.hidden()
}
EOF
cat > "$MP/app/app.kt" <<'EOF'
import dotkt.support.blockOn
suspend fun doFetch(lib: Lib, b: Box<Int>): Int = with(lib) { b.fetch() }   // suspend member ext via with() (scope-fn CPS)
suspend fun doHidden(lib: Lib, b: Box<Int>): Int = lib.useHidden(b)
fun main() {
    val lib = Lib(10)
    with(lib) {
        println(Box(7).lbl)       // lbl:17
        println(Box(3).scaled)    // 30
        Box(0).scaled = 5         // last = 15
        println(last)             // 15
    }
    println(lib.peek(Box(2)))                       // 1002 (protected member ext property)
    println(blockOn { doFetch(lib, Box(5)) })   // 15   (suspend member ext consumed via with(lib){ b.fetch() })
    println(blockOn { doHidden(lib, Box(2)) })  // 210  (protected suspend member ext via helper)
}
EOF
"$LAUNCHER" "$MP/lib" -no-stdlib -classpath "$CP" -d "$MP/libbir" >/dev/null 2>&1 || true
emit_il "$MP/libil" KLib "$MP/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$MP/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$MP/libil/KLib.dll" "$MP/KLib.klib"
write_coharness "$MP/app"
"$LAUNCHER" "$MP/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$MP/KLib.klib" -d "$MP/appbir" >/dev/null 2>&1 || true
emit_il "$MP/appil" KApp --ref "$MP/libil/KLib.dll" "$MP/appbir"/*.bir.json
cp "$MP/libil/KLib.dll" "$MP/appil/" 2>/dev/null || true
mpexpected="$(printf 'lbl:17\n30\n15\n1002\n15\n210')"
run_app mpactual "$MP/appil/KApp.dll"
check_output roundtrip-memext2 "$mpexpected" "$mpactual" "member extension properties + suspend member extensions, public + protected"

# ----- SUSPEND FUNCTION-TYPE round-trip (H2): a `suspend (…) -> T` PARAMETER survives re-consumption -----
# A library exports `fun runBlock(block: suspend () -> Int)` — bir2cir erases the CLR parameter SLOT to `object` (a
# suspend-lambda VALUE is a Continuation state-machine, not a Func), so WITHOUT the position metadata the consumer
# would see a plain `Any?` and a passed lambda could NOT call a suspend function. bir2cir records the pre-erasure `fn`
# shape (suspend:true) and generates a carrier-encoded [KotlinSuspendFunctionType(version, bytes)] on the parameter;
# ilemit stamps it dumbly; facadegen reads the `fn` node back and ClrTypeInjection restores `block` as a suspend
# function type (`kotlin.coroutines.SuspendFunction0<Int>`). PROOF that suspend survives: the
# consumer's `runBlock { addAsync(...) }` lambda BODY calls `addAsync` (itself a suspend fun) — which only compiles
# if `block` is a suspend function type (else "suspend function called from non-suspend context"), and only runs if
# the suspend lambda is driven as a state machine. (A suspend fn-type in a RETURN/property/field position is wired in
# facadegen too, but blocked E2E on a separate suspend-lambda-VALUE emit limitation — `expr suspendLambdaNew`.)
# See docs/dotkt-semantics.md §10.
SF="$ROOT/build/roundtrip-suspendfn"; rm -rf "$SF"; mkdir -p "$SF/lib" "$SF/app" "$SF/libbir" "$SF/libil" "$SF/appbir" "$SF/appil"
cat > "$SF/lib/lib.kt" <<'EOF'
package hof
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
@Suppress("UNCHECKED_CAST")
private class Sink : Continuation<Any?> {                        // Continuation is `in T` (contravariant), so a
    var value: Any? = null                                      // Continuation<Any?> is a Continuation<Int> completion
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) { value = result.getOrNull() }
}
fun runBlock(block: suspend () -> Int): Int {                    // a `suspend (…) -> T` PARAMETER (the H2 position)
    val sink = Sink(); block.startCoroutine(sink); return sink.value as Int
}
suspend fun addAsync(a: Int, b: Int): Int = a + b
EOF
cat > "$SF/app/app.kt" <<'EOF'
import hof.runBlock
import hof.addAsync
fun main() {
    println(runBlock { addAsync(20, 22) })                          // 42 — passes a suspend lambda cross-module
    println(runBlock { val a = addAsync(10, 5); addAsync(a, 27) })  // 42 — two suspension points in the passed lambda
}
EOF
"$LAUNCHER" "$SF/lib" -no-stdlib -classpath "$CP" -d "$SF/libbir" >/dev/null 2>&1 || true
emit_il "$SF/libil" HofLib "$SF/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$SF/libil/HofLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$SF/libil/HofLib.dll" "$SF/HofLib.klib"
"$LAUNCHER" "$SF/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$SF/HofLib.klib" -d "$SF/appbir" >/dev/null 2>&1 || true
emit_il "$SF/appil" HofApp --ref "$SF/libil/HofLib.dll" "$SF/appbir"/*.bir.json
cp "$SF/libil/HofLib.dll" "$SF/appil/" 2>/dev/null || true
sfexpected="$(printf '42\n42')"
run_app sfactual "$SF/appil/HofApp.dll"
check_output roundtrip-suspendfn "$sfexpected" "$sfactual" "a suspend (…) -> T PARAMETER round-trips: the consumer's lambda calls a suspend fun (valid only if the restored param is a suspend fn-type)"

# ----- SUSPEND FUNCTION-TYPE VALUE round-trip (H2 residual, #33): a suspend lambda used as a VALUE ------
# The PARAMETER position (above) proved a `suspend (…) -> T` slot round-trips. This section proves the
# remaining H2 positions — a suspend lambda used as a VALUE that is RETURNED from a function, STORED in a
# top-level PROPERTY, and STORED in an instance FIELD. kotc emits a `suspendLambdaNew` node in each such
# NON-call-argument position, which bir2cir's SuspendLambdaLowering now lowers to a `new <SuspendLambda SM>`
# value everywhere (previously only method/ctor/accessor bodies were walked, so a static field initializer's
# value-position node reached ilemit -> `NotSupportedException: expr suspendLambdaNew`).
#   - RETURN position is proven cross-module directly: the app calls the LIB's `makeBlock()` (its restored
#     `suspend () -> Int` return type comes back via facadegen's structured meta — a `fn` node with suspend:true) and DRIVES it.
#   - PROPERTY + FIELD positions are proven by the LIB storing the suspend lambda in a top-level `val` and an
#     instance `val`, then DRIVING each via `runBlock` inside restorable functions `runProp()`/`runField()`
#     the app invokes. (kotc emits a top-level `val` as a plain static FIELD, which facadegen does not restore
#     as a Kotlin `val`, so the app consumes the VALUE through a function rather than the raw field.) A wrong
#     value-position lowering would crash the LIB emit or mis-drive the SM. See docs/dotkt-semantics.md §10.
SR="$ROOT/build/roundtrip-suspendfn-ret"; rm -rf "$SR"; mkdir -p "$SR/lib" "$SR/app" "$SR/libbir" "$SR/libil" "$SR/appbir" "$SR/appil"
cat > "$SR/lib/lib.kt" <<'EOF'
package hof2
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
@Suppress("UNCHECKED_CAST")
private class Sink : Continuation<Any?> {
    var value: Any? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) { value = result.getOrNull() }
}
fun runBlock(block: suspend () -> Int): Int {
    val sink = Sink(); block.startCoroutine(sink); return sink.value as Int
}
suspend fun addAsync(a: Int, b: Int): Int = a + b
fun makeBlock(): suspend () -> Int = { addAsync(20, 22) }       // RETURN position (the H2 gap)
val blockProp: suspend () -> Int = { addAsync(15, 15) }         // top-level PROPERTY/field position
private class FieldHolder { val f: suspend () -> Int = { addAsync(100, 7) } }  // instance FIELD position
fun runProp(): Int = runBlock(blockProp)                        // drives the property-stored lambda
fun runField(): Int = runBlock(FieldHolder().f)                 // drives the field-stored lambda
EOF
cat > "$SR/app/app.kt" <<'EOF'
import hof2.runBlock
import hof2.makeBlock
import hof2.runProp
import hof2.runField
fun main() {
    println(runBlock(makeBlock()))   // 42 — a RETURNED suspend lambda, driven cross-module
    println(runProp())               // 30 — a suspend lambda STORED in a top-level property, then driven
    println(runField())              // 107 — a suspend lambda STORED in an instance field, then driven
}
EOF
"$LAUNCHER" "$SR/lib" -no-stdlib -classpath "$CP" -d "$SR/libbir" >/dev/null 2>&1 || true
emit_il "$SR/libil" Hof2Lib "$SR/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$SR/libil/Hof2Lib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$SR/libil/Hof2Lib.dll" "$SR/Hof2Lib.klib"
"$LAUNCHER" "$SR/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$SR/Hof2Lib.klib" -d "$SR/appbir" >/dev/null 2>&1 || true
emit_il "$SR/appil" Hof2App --ref "$SR/libil/Hof2Lib.dll" "$SR/appbir"/*.bir.json
cp "$SR/libil/Hof2Lib.dll" "$SR/appil/" 2>/dev/null || true
srexpected="$(printf '42\n30\n107')"
run_app sractual "$SR/appil/Hof2App.dll"
check_output roundtrip-suspendfn-ret "$srexpected" "$sractual" "a suspend (…) -> T VALUE round-trips in RETURN + PROPERTY + FIELD position: bir2cir lowers a value-position suspendLambdaNew to a SuspendLambda SM, the consumer drives it"

# ----- VIRTUAL DISPATCH: an open/override instance method of a DotKt lib consumed AS KOTLIN dispatches virtually (#139) -----
# kotc's .NET-interop callInstance path (a facadegen-reinjected owner) previously emitted NO `virtual` flag. bir2cir's
# NetInteropBinding reshapes such a callInstance to a `clrInstance` (where virtual is moot) WHEN it resolves the owner
# off the --ref DotKt assembly, so the normal path masks the gap. But when bir2cir CANNOT resolve the owner (an
# asymmetry: kotc's clrName resolved it via the facadegen injection metadata, bir2cir's ResolveNetType did not), the
# RAW callInstance reaches ilemit, which read `virtual` UNCONDITIONALLY -> KeyNotFoundException; and even null-tolerant,
# a defaulted non-virtual `call` on an `open`/`override` member mis-dispatches. kotc now stamps `virtual`
# (modality != FINAL || overrides) on every .NET-interop callInstance; ilemit reads it null-tolerantly (IsVirtual).
# This section guards the ROOT (the app BIR carries `virtual`) AND both emit paths: (1) the normal reshaped clrInstance
# path, (2) the FALLBACK where bir2cir is deliberately NOT given the DotKt --ref, forcing the raw callInstance into
# ilemit — the exact uncovered path #139 crashed on. Both must print the same virtually-dispatched output + ilverify clean.
VD="$ROOT/build/roundtrip-virtual-dispatch"; rm -rf "$VD"; mkdir -p "$VD/lib" "$VD/app" "$VD/libbir" "$VD/libil" "$VD/appbir" "$VD/appil" "$VD/appil2"
cat > "$VD/lib/lib.kt" <<'EOF'
package dispatch
open class Animal(val name: String) {
    open fun sound(): String = "generic"           // open   -> virtual:true
    fun describe(): String = name + ":" + sound()  // final  -> virtual:false (calls the virtual sound() internally)
}
class Dog(name: String) : Animal(name) {
    override fun sound(): String = "woof"           // override -> virtual:true
}
EOF
cat > "$VD/app/app.kt" <<'EOF'
import dispatch.Animal
import dispatch.Dog
fun main() {
    val a: Animal = Animal("a")
    val d: Animal = Dog("d")
    println(a.sound())      // generic   open method, base receiver
    println(d.sound())      // woof      DISCRIMINATOR: a plain `call Animal::sound` prints "generic"; callvirt -> "woof"
    println(a.describe())   // a:generic final method
    println(d.describe())   // d:woof    final describe, internal virtual sound() -> woof
}
EOF
"$LAUNCHER" "$VD/lib" -no-stdlib -classpath "$CP" -d "$VD/libbir" >/dev/null 2>&1 || true
emit_il "$VD/libil" AnimalLib "$VD/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$VD/libil/AnimalLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$VD/libil/AnimalLib.dll" "$VD/AnimalLib.klib"
"$LAUNCHER" "$VD/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$VD/AnimalLib.klib" -d "$VD/appbir" >/dev/null 2>&1 || true
# ROOT-fix guard: every callInstance on the reinjected dispatch.Animal owner carries a `virtual` flag (kotc #139).
# A node WITH the flag serializes as `{"k":"callInstance","virtual":<b>,"ownerType":{...dispatch.Animal}}`; a
# regression (missing flag) as `{"k":"callInstance","ownerType":{...dispatch.Animal}}` — assert the former exists and the latter never does.
vd_has_virtual=$( { grep -oh '"k":"callInstance","virtual":[a-z]*,"ownerType":{"t":"fqn","name":"dispatch.Animal"}' "$VD/appbir"/*.bir.json 2>/dev/null || true; } | wc -l)
vd_missing_virtual=$( { grep -oh '"k":"callInstance","ownerType":{"t":"fqn","name":"dispatch.Animal"}' "$VD/appbir"/*.bir.json 2>/dev/null || true; } | wc -l)
vd_bir_ok=0; [[ "$vd_has_virtual" -ge 1 && "$vd_missing_virtual" -eq 0 ]] && vd_bir_ok=1
# (1) NORMAL path: bir2cir WITH the DotKt --ref -> callInstance reshaped to clrInstance.
emit_il "$VD/appil" AnimalApp --ref "$VD/libil/AnimalLib.dll" "$VD/appbir"/*.bir.json
cp "$VD/libil/AnimalLib.dll" "$VD/appil/" 2>/dev/null || true
vdexpected="$(printf 'generic\nwoof\na:generic\nd:woof')"
run_app vdactual1 "$VD/appil/AnimalApp.dll"
# (2) FALLBACK path: bir2cir NOT given the DotKt --ref, so it CANNOT reshape the callInstance -> the raw node reaches
# ilemit (which still resolves the owner off its own --ref). This is the exact #139 crash path, now correct via `virtual`.
cir2="$VD/appil2.cir"; rm -rf "$cir2"; mkdir -p "$cir2"
dotnet "$BIR2CIR_DLL" "$cir2" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")" "$VD/appbir"/*.bir.json >/dev/null 2>&1 || true
dotnet "$ILEMIT_DLL" "$VD/appil2" AnimalApp --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$VD/libil/AnimalLib.dll")" "$cir2"/*.cir.json >/dev/null 2>&1 || true
[[ -f "$STDLIB_RT_DLL" ]] && cp "$STDLIB_RT_DLL" "$VD/appil2/" 2>/dev/null || true
cp "$VD/libil/AnimalLib.dll" "$VD/appil2/" 2>/dev/null || true
run_app vdactual2 "$VD/appil2/AnimalApp.dll"
# ilverify both emitted assemblies (formal call/callvirt-selection verification).
vd_ilv_ok=1
VD_ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
VD_REFDIR="$DOTNET_RUNTIME_DIR"
if [[ -n "$VD_ILV" && -d "$VD_REFDIR" ]]; then
	for dll in "$VD/appil/AnimalApp.dll" "$VD/appil2/AnimalApp.dll"; do
		[[ -f "$dll" ]] || { vd_ilv_ok=0; continue; }
		dotnet "$VD_ILV" "$dll" -r "$VD_REFDIR/*.dll" -r "$STDLIB_RT_DLL" -r "$VD/libil/AnimalLib.dll" 2>&1 | grep -qi 'Verified\.' || vd_ilv_ok=0
	done
fi
vd_ok=0
[[ "$vd_bir_ok" == 1 && "$vdactual1" == "$vdexpected" && "$vdactual2" == "$vdexpected" && "$vd_ilv_ok" == 1 ]] && vd_ok=1
section_result roundtrip-virtual-dispatch "$vd_ok" "open/override instance method of a DotKt lib dispatches virtually as Kotlin; BIR carries virtual; reshaped + raw-callInstance paths; ilverify (#139)" \
	"$(printf -- 'bir_ok=%s (has=%s missing=%s) ilv_ok=%s\n--- expected ---\n%s\n--- reshaped(clrInstance) ---\n%s\n--- raw callInstance->ilemit ---\n%s' "$vd_bir_ok" "$vd_has_virtual" "$vd_missing_virtual" "$vd_ilv_ok" "$vdexpected" "$vdactual1" "$vdactual2")"

# ----- PROPERTY-TYPE round-trip (#47): a property's nullability + suspend-fn-type restored on re-import -----
# The OLD gate drove stored values through internal harness fns and NEVER re-imported the PROPERTY TYPE itself, so
# a `text: String?` degrading to non-null / a `block: suspend () -> T` degrading to `Any?` regressed SILENTLY. This
# section re-imports the PROPERTIES DIRECTLY off a DotKt class and relies on the RESTORED property type:
#   * text: String? (var)          — the consumer WRITES `h.text = null`, which COMPILES ONLY IF the property restored
#                                    nullable; a degraded non-null `String` -> "null can not be a value of a non-null
#                                    type String" -> the consumer never compiles -> empty output -> section FAIL. (A
#                                    nullable READ can't be a sharp compile signal since String <: String?, so the
#                                    nullable-WRITE is the sharp signal — cf. roundtrip-nrt's `takeNullable(null)`.)
#   * block: suspend () -> Int      — passed DIRECTLY to `blockOn(h.block)` (whose param is `suspend () -> T`); a degraded
#                                    `Any?` slot is not assignable there -> the consumer fails to compile -> FAIL.
#   * ext:   suspend Int.() -> Int  — assigned to a typed local `val f: suspend Int.() -> Int = h.ext`, which type-checks
#                                    ONLY IF ext restored as a suspend fn-type of arity 1 (a degraded `Any?` / arity-0
#                                    `suspend () -> T` would not assign -> compile FAIL). The RECEIVER preservation (the
#                                    #47 combined suspend+extension cone) is separately asserted by grepping the facadegen
#                                    meta for ext's `fn` node carrying `recv`. (ext is NOT driven at runtime: driving a
#                                    suspend EXTENSION lambda VALUE via the receiver-form startCoroutine hits a pre-existing
#                                    bir2cir coroutine-lowering gap — reproducible SAME-module, unrelated to this
#                                    symbol-surface fix — so the restored TYPE is asserted by compile-dependency + meta.)
# bir2cir: RoundtripMetadata StampProps emits [Nullable] (from DeclNullableFlags) + [KotlinSuspendFunctionType];
# kotc BirEmitterTypes keeps recv on a suspend ext fn; facadegen PropTypeN reads the suspend carrier + ApplyNrt the
# nullable byte (recv-tolerant); kotc coneOf composes coneSuspendExtensionFunctionType.
PR="$ROOT/build/roundtrip-property-type"; rm -rf "$PR"; mkdir -p "$PR/lib" "$PR/app" "$PR/libbir" "$PR/libil" "$PR/appbir" "$PR/appil"
cat > "$PR/lib/lib.kt" <<'EOF'
package rtprops
class Holder {
    var text: String? = "init"                     // nullable reference property (#47)
    val block: suspend () -> Int = { 7 }            // suspend function-type property (#47)
    val ext: suspend Int.() -> Int = { this + 1 }   // suspend EXTENSION function-type property (#47 combined cone)
}

// #147: every declaration slot nesting Nullable(Tv) must carry its pre-erasure shape, not only method returns.
annotation class ClrField
class Slot<T>(val value: T)
fun <T> acceptsNullable(slot: Slot<T?>): Int = if (slot.value == null) 0 else 1
class GenericSlots<T>(initial: Slot<T?>) {
    @ClrField val fieldSlot: Slot<T?> = initial
    val propertySlot: Slot<T?> get() = fieldSlot
    fun accept(slot: Slot<T?>): Int = if (slot.value == null) 0 else 1
    fun acceptMany(vararg slots: Slot<T?>): Int = slots.size
}
class FunctionSlots<T>(initial: (T?) -> String) {
    @ClrField val functionField: (T?) -> String = initial
    val functionProperty: (T?) -> String get() = functionField
}
interface SlotConsumer<T> { fun bridgeAccept(slot: Slot<T?>): String }
open class SlotBase<T> { fun bridgeAccept(slot: Slot<T?>): String = slot.value?.toString() ?: "null" }
class SlotDerived<T> : SlotBase<T>(), SlotConsumer<T>
EOF
cat > "$PR/lib/twin.kt" <<'EOF'
package rtprops2
class Slot<T>(val value: T)
fun <T> acceptsNullable(slot: Slot<T?>): Int = if (slot.value == null) 0 else 1
EOF
cat > "$PR/app/app.kt" <<'EOF'
import dotkt.support.blockOn
import rtprops.Holder
import rtprops.FunctionSlots
import rtprops.GenericSlots
import rtprops.Slot
import rtprops.SlotConsumer
import rtprops.SlotDerived
fun main() {
    val h = Holder()
    h.text = null                          // compiles ONLY IF text restored nullable (String?); a non-null degrade -> compile FAIL
    println(h.text ?: "was-null")          // was-null  — the null read back through the restored String? property
    println(blockOn(h.block))              // 7         — block restored as suspend () -> Int, DRIVEN via startCoroutine
    val f: suspend Int.() -> Int = h.ext   // type-checks ONLY IF ext restored as suspend Int.() -> Int (a degraded Any?
    println(if (f === h.ext) "ext-ok" else "ext-bad")  //             or arity-0 suspend () -> Int would not assign -> FAIL)
    val slot = Slot<String?>(null)
    val slots = GenericSlots<String>(slot)
    val functions = FunctionSlots<String> { it ?: "nil" }
    val consumer: SlotConsumer<String> = SlotDerived<String>()
    check(slots.fieldSlot.value == null)              // raw-field carrier
    check(slots.propertySlot.value == null)           // property carrier
    check(slots.accept(slot) == 0)                    // method parameter
    check(slots.acceptMany(slot) == 1)                // vararg element
    check(functions.functionField(null) == "nil")     // raw function-field carrier
    check(functions.functionProperty(null) == "nil")  // function-property carrier
    check(consumer.bridgeAccept(slot) == "null")      // late synthesized bridge
    check(rtprops.acceptsNullable(slot) == 0)         // file function + method type variable
    check(rtprops2.acceptsNullable(rtprops2.Slot<String?>(null)) == 0) // same-name packages stay distinct
    println("slots-ok")
}
EOF
"$LAUNCHER" "$PR/lib" -no-stdlib -classpath "$CP" -d "$PR/libbir" >/dev/null 2>&1 || true
emit_il "$PR/libil" PropLib "$PR/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$PR/libil/PropLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$PR/libil/PropLib.dll" "$PR/PropLib.klib"
write_coharness "$PR/app"
"$LAUNCHER" "$PR/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$PR/PropLib.klib" -d "$PR/appbir" >/dev/null 2>&1 || true
emit_il "$PR/appil" PropApp --ref "$PR/libil/PropLib.dll" "$PR/appbir"/*.bir.json
cp "$PR/libil/PropLib.dll" "$PR/appil/" 2>/dev/null || true
prexpected="$(printf 'was-null\n7\next-ok\nslots-ok')"
run_app practual "$PR/appil/PropApp.dll"
pr_ok=0; [[ "$practual" == "$prexpected" ]] && pr_ok=1
section_result roundtrip-property-type "$pr_ok" "a property's nullability (String?) + suspend-fn-type (suspend ()->T, suspend R.()->T incl. restored receiver) re-import directly as the property type #47" \
	"$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$prexpected" "$practual")"
section_result roundtrip-nullable-generic-slots "$pr_ok" \
	"nullable generic shape restores on constructors/methods/varargs/properties/raw fields, function types, late synthesized bridges, and same-name packages #147" \
	"$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$prexpected" "$practual")"

# ----- INTERFACE-COMPANION statics round-trip (#132): `interface I { companion object { val X; fun f() } }` -----
# kotc FLATTENS an interface's plain companion object to the interface's OWN static fields/methods (BirEmitterDeclarations
# statFields/statMethods — the #83 SharingStarted.Eagerly path). Pre-fix, facadegen's interface branch enumerated ONLY
# Public|Instance members and returned — so the flattened statics were SILENTLY DROPPED: a consumer re-importing the lib
# could not resolve `I.X`/`I.f()` (round-trip asymmetry — emit had them, read dropped them). facadegen now surfaces the
# interface's Public|Static fields/props/methods/events as companion members (staticProps/staticFuns/staticEvents), and
# the injector materializes the interface companion. Reached via `.Companion` (injected-static-members-need-companion).
# LOAD-BEARING: if the statics were still dropped, the app would fail to compile (unresolved `Greeter.Companion.DEFAULT`).
IC="$ROOT/build/roundtrip-iface-companion"; rm -rf "$IC"; mkdir -p "$IC/lib" "$IC/app" "$IC/libbir" "$IC/libil" "$IC/appbir" "$IC/appil"
cat > "$IC/lib/lib.kt" <<'EOF'
package svc
interface Greeter {
    fun greet(name: String): String
    companion object {
        val DEFAULT: String = "Anon"                                   // companion `val` -> static field on the interface
        fun create(): Greeter = object : Greeter {                     // companion `fun` -> static method on the interface
            override fun greet(name: String): String = "Hi, " + name
        }
    }
}
EOF
cat > "$IC/app/app.kt" <<'EOF'
import svc.Greeter
fun main() {
    println(Greeter.Companion.DEFAULT)                     // Anon    interface-companion static val (#132)
    val g = Greeter.Companion.create()                     //         interface-companion static fun (#132)
    println(g.greet("Vec"))                                // Hi, Vec  instance member on the created impl
    println(Greeter.Companion.create().greet(Greeter.Companion.DEFAULT))  // Hi, Anon  both statics in one expr
}
EOF
"$LAUNCHER" "$IC/lib" -no-stdlib -classpath "$CP" -d "$IC/libbir" >/dev/null 2>&1 || true
emit_il "$IC/libil" GreeterLib "$IC/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$IC/libil/GreeterLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$IC/libil/GreeterLib.dll" "$IC/GreeterLib.klib"
"$LAUNCHER" "$IC/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$IC/GreeterLib.klib" -d "$IC/appbir" >/dev/null 2>&1 || true
emit_il "$IC/appil" GreeterApp --ref "$IC/libil/GreeterLib.dll" "$IC/appbir"/*.bir.json
cp "$IC/libil/GreeterLib.dll" "$IC/appil/" 2>/dev/null || true
icexpected="$(printf 'Anon\nHi, Vec\nHi, Anon')"
run_app icactual "$IC/appil/GreeterApp.dll"
ic_ok=0; [[ "$icactual" == "$icexpected" ]] && ic_ok=1
section_result roundtrip-iface-companion "$ic_ok" "interface-companion statics (val+fun) projected into KLIB and resolved cross-module #132" \
	"$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$icexpected" "$icactual")"

# ----- `class C : Comparable<C>` round-trip (#179): PascalCase IComparable<T>.CompareTo -> operator compareTo -----
# A Kotlin `class C : Comparable<C>` lowers `compareTo` to the CLR `System.IComparable<C>.CompareTo` PascalCase slot
# (bir2cir DeclarationRename) and its supertype to `System.IComparable<C>` (+ a non-generic bridge). Pre-fix facadegen
# left BOTH un-restored: the PascalCase `CompareTo` never became the lowercase `operator compareTo`, and the supertype
# stayed `IComparable`, so a consumer's `c1 < c2` / `sorted()` was UNRESOLVED on re-import. facadegen now (a) renames the
# DotKt IComparable<X> self slot `CompareTo` -> `compareTo` + forces the operator flag (so the FRONTEND resolves `<` to
# C's own operator), and (b) restores the `IComparable<X>` supertype as `kotlin.Comparable<X>` (dropping the non-generic
# bridge) so `sorted()`'s `Comparable<C>` constraint is satisfied. That is the SYMBOL-SURFACE half (facadegen's); the
# section keeps ONLY the facadegen-surface half here:
#   * roundtrip-comparable-meta  — the facadegen surface, asserted DIRECTLY on the generated metadata. A regression
#                                  guard for the restore.
# The END-TO-END run (`<`/`>`/`<=`/`>=`/sorted() resolve+run cross-module, bir2cir compareTo->CompareTo slot bind)
# MIGRATED to the in-process ProjectReference round-trip lane (tests/roundtrip/consumer RoundtripTests::comparableClass).
CM="$ROOT/build/roundtrip-comparable"; rm -rf "$CM"; mkdir -p "$CM/lib" "$CM/app" "$CM/libbir" "$CM/libil" "$CM/appbir" "$CM/appil"
cat > "$CM/lib/lib.kt" <<'EOF'
package geo
class Ver(val n: Int) : Comparable<Ver> {
    override fun compareTo(other: Ver): Int = n - other.n
}
EOF
cat > "$CM/app/app.kt" <<'EOF'
import geo.Ver
fun main() {
    println(Ver(3) < Ver(5))                              // True   `<`  -> restored operator compareTo
    println(Ver(9) > Ver(2))                              // True   `>`
    println(Ver(4) <= Ver(4))                             // True   `<=`
    println(Ver(7) >= Ver(8))                             // False  `>=`
    val xs = listOf(Ver(3), Ver(1), Ver(2)).sorted()      // sorted() needs Ver : Comparable<Ver> (supertype restored)
    println(xs[0].n)                                      // 1      smallest first
    println(xs[2].n)                                      // 3      largest last
}
EOF
"$LAUNCHER" "$CM/lib" -no-stdlib -classpath "$CP" -d "$CM/libbir" >/dev/null 2>&1 || true
emit_il "$CM/libil" VerLib "$CM/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$CM/libil/VerLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
project_reference_klib "$CM/libil/VerLib.dll" "$CM/VerLib.klib"
"$LAUNCHER" "$CM/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$CM/VerLib.klib" -d "$CM/appbir" >/dev/null 2>&1 || true
emit_il "$CM/appil" VerApp --ref "$CM/libil/VerLib.dll" "$CM/appbir"/*.bir.json
cp "$CM/libil/VerLib.dll" "$CM/appil/" 2>/dev/null || true
cmexpected="$(printf 'True\nTrue\nTrue\nFalse\n1\n3')"
run_app cmactual "$CM/appil/VerApp.dll"
check_output roundtrip-comparable-meta "$cmexpected" "$cmactual" \
	"reference KLIB restores operator compareTo + kotlin.Comparable<Ver>; comparison operators and sorted() compile and run #179"

# ---- verdict --------------------------------------------------------------------------------------
echo "------------------------------------"
printf '%s\n' "${SUMMARY[@]}"
if (( ${#NEW_FAILS[@]} )); then
	echo "ROUNDTRIP GATE RED — section(s) failing outside the RT_XFAIL baseline: ${NEW_FAILS[*]}"
	exit 1
fi
echo "ROUNDTRIP GATE GREEN (every FAIL is RT_XFAIL-listed; a FIXED line above means prune the baseline)"

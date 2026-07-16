#!/usr/bin/env bash
# DotKt round-trip gate: a Kotlin assembly compiled by DotKt, consumed AS KOTLIN by another module — the
# Kotlin modifiers with no .NET analog (infix / operator / suspend / top-level) survive the trip. They're
# stamped onto the emitted IL as DotKt.Metadata attributes ([KotlinFunction]/[KotlinFileClass]) by ilemit,
# then read back by facadegen (--meta) and restored on the synthesized FIR by ClrTypeInjection. This is
# the basis of consuming compiled Kotlin libraries as Kotlin. Inputs: inline heredoc samples
# under build/roundtrip-*. EVERY section runs to completion regardless of earlier failures — results are
# collected, and a crashing consumer app (SIGABRT from the deliberate suspend stub) is captured, never
# allowed to take the gate down mid-script. Verdict: exit 0 iff every failing section is RT_XFAIL-listed;
# an XFAIL section that starts passing prints "FIXED — remove it from the xfail list" and stays green.
# See docs/design-kotlin-metadata-attributes.md.
source "$(dirname "$0")/lib.sh"

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
# (P2/P3/P4 done: in-module async runs — cf. verify-il genasync/cobuild), so these no longer abort on a
# bare `kotlin.coroutines.Continuation` at emit; they surface the REMAINING *cross-module* coroutine gaps
# (below). This gate is the coroutine bundle's cross-module E2E check: when these flip to FIXED, prune them.
declare -A RT_XFAIL=(
)

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
CP="$FE_KLIB"

# Build the toolchain once (UNCONDITIONALLY — the gate tests the current sources).
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$KOTC"
need_fe_klib
build_tool ilemit; build_tool facadegen; build_tool retarget
need_dotnet_reference_sets
# The RUNTIME stdlib joins the reference set: a suspend-carrying DotKt lib references the coroutine runtime
# (DotKt.Stdlib's kotlin.coroutines.Continuation) in its emitted CPS signatures, so retarget/facadegen must be
# able to LOAD it to walk KLib's type surface (else facadegen skips every seed type -> empty meta -> the
# consumer can't resolve the library). Harmless for the non-suspend sections (they reference no stdlib type).
REFS="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL");"

# kotc emits bare kotlin.* type tokens (the frontend klib resolves the stdlib to our real kotlin.* declarations); bir2cir
# lowers them to the CLR-codegen vocabulary ilemit consumes. So route every emit through bir2cir (mirrors verify-il) —
# feeding BIR straight to ilemit would leave kotlin.* tokens un-lowered ("cannot resolve .NET type kotlin.String"). The
# REFERENCE stdlib supplies bir2cir's @ClrTypeAlias labels (built once if missing; the roundtrip types are pure-Kotlin).
build_tool bir2cir
need_stdlib_ref; need_stdlib_rt
# emit_il: drop-in for `ilemit <outdir> <asm> [--ref X]... <bir files...>`, inserting the BIR->CIR (bir2cir) lowering.
# Both stages tolerate failure (|| true): a broken emit surfaces as its SECTION's FAIL, not a script abort.
# ilemit references the RUNTIME stdlib (--ref) so REAL emitted kotlin.* types resolve — notably the coroutine
# runtime (`kotlin.coroutines.Continuation`, injected into a suspend fun's CPS signature by bir2cir's suspend
# lowering); and the rt dll is dropped beside the emitted assembly so the run resolves it (mirrors verify-il).
emit_il() {
	local out="$1" asm="$2"; shift 2
	local refs=() birs=() usrrefs=()
	while (( $# )); do
		# A user `--ref X` (a retargeted DotKt library) goes to ilemit AND — A2 (#61) — to bir2cir, which RESOLVES
		# the facadegen-injected owner FQN against it to bind the .NET call SHAPE (clrStatic/clrInstance/…). Mirrors
		# verify-il's il_emit: the RUNTIME stdlib (added below) is ilemit-only (bir2cir reads the REFERENCE stdlib).
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$M/lib" -no-stdlib -classpath "$CP" -d "$M/libbir" >/dev/null 2>&1 || true
emit_il "$M/libil" MarkLib "$M/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$M/libil/MarkLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$M/imports.txt" "$M/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$M/meta" --compile-refs "$REFS$M/libil/MarkLib.dll" --import-list "$M/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$M/meta" "$LAUNCHER" "$M/app" -no-stdlib -classpath "$CP" -d "$M/appbir" >/dev/null 2>&1 || true
emit_il "$M/appil" MarkApp --ref "$M/libil/MarkLib.dll" "$M/appbir"/*.bir.json
cp "$M/libil/MarkLib.dll" "$M/appil/" 2>/dev/null || true
mkexpected="$(printf '50\narea=12\nsquare\nGREEN')"
run_app mkactual "$M/appil/MarkApp.dll"
# NEGATIVE: `sealed` is cross-module-enforced — a rogue subclass in another module MUST be rejected (proves Modality.SEALED restored).
cat > "$M/rogue/rogue.kt" <<'EOF'
import shapes.Shape
class Rogue : Shape { override fun area(): Int = 0 }
EOF
if CLR_TYPES_METADATA="$M/meta" "$LAUNCHER" "$M/rogue" -no-stdlib -classpath "$CP" -d "$M/roguebir" >/dev/null 2>&1; then rogue_ok=1; else rogue_ok=0; fi
mk_ok=0; if [[ "$mkactual" == "$mkexpected" && "$rogue_ok" == 0 ]]; then mk_ok=1; fi
section_result roundtrip-markers "$mk_ok" "fun interface nature; sealed modality+exhaustive-when+cross-module enforcement; enum" \
	"$(printf -- '--- expected ---\n%s\n--- actual ---\n%s\n--- rogue accepted (want reject): %s ---' "$mkexpected" "$mkactual" "$rogue_ok")"

# ----- CROSS-ASSEMBLY BASIC-ENUM inherited System.Enum members (#105) -----
# A BASIC (constants-only) `enum class` emits as a CLR value-type enum (deriving System.Enum) that declares no
# ToString/GetHashCode/Equals of its own — it INHERITS them. #90 fixed the SAME-module case (bir2cir EnumMemberBinding
# boxes the value-type receiver + callvirt the System.Object slot on a `callInstance` whose LOCAL owner is unresolvable
# as a .NET type). The CROSS-ASSEMBLY case is closed EARLIER and by a DIFFERENT layer: kotc emits the inherited-member
# call as a plain `callInstance owner=palette.Color` by FQN identity; bir2cir's NetInteropBinding resolves that owner off
# the `--ref` DotKt assembly (A2/#61) and binds it to a `clrInstance`; ilemit's EmitClrCall/EmitInstanceCall then take
# the value-type receiver BY ADDRESS and emit `constrained. <Color>; callvirt object::ToString` — valid, ilverify-clean.
# So this section is a REGRESSION GUARD for that facadegen-injected-enum -> NetInteropBinding -> constrained-callvirt
# path (it is not a fail-before/pass-after for any bir2cir enum-set change — the calls never reach EnumMemberBinding).
# A `.toString()` (RED), an `==` (objEq -> False, CLR System.Boolean.ToString), and a `.hashCode()` (RED = ordinal 0 ->
# System.Enum hashes the underlying int -> 0) exercise all three inherited slots.
CE="$ROOT/build/roundtrip-enum"; rm -rf "$CE"; mkdir -p "$CE/lib" "$CE/app" "$CE/libbir" "$CE/libil" "$CE/appbir" "$CE/appil"
cat > "$CE/lib/lib.kt" <<'EOF'
package palette
enum class Color { RED, GREEN }
EOF
cat > "$CE/app/app.kt" <<'EOF'
import palette.Color
fun main() {
    println(Color.RED.toString())       // RED   inherited System.Enum.ToString on a value-type receiver
    println(Color.RED == Color.GREEN)   // False structural equality (CLR System.Boolean.ToString, cf. roundtrip-defargs)
    println(Color.RED.hashCode())       // 0     inherited System.Enum.GetHashCode (RED underlying int = 0)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$CE/lib" -no-stdlib -classpath "$CP" -d "$CE/libbir" >/dev/null 2>&1 || true
emit_il "$CE/libil" PaletteLib "$CE/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$CE/libil/PaletteLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$CE/imports.txt" "$CE/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$CE/meta" --compile-refs "$REFS$CE/libil/PaletteLib.dll" --import-list "$CE/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$CE/meta" "$LAUNCHER" "$CE/app" -no-stdlib -classpath "$CP" -d "$CE/appbir" >/dev/null 2>&1 || true
emit_il "$CE/appil" PaletteApp --ref "$CE/libil/PaletteLib.dll" "$CE/appbir"/*.bir.json
cp "$CE/libil/PaletteLib.dll" "$CE/appil/" 2>/dev/null || true
ceexpected="$(printf 'RED\nFalse\n0')"
run_app ceactual "$CE/appil/PaletteApp.dll"
check_output roundtrip-enum "$ceexpected" "$ceactual" "cross-assembly basic-enum inherited System.Enum members (toString/==/hashCode) #105"

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
CLR_TYPES_METADATA="" "$LAUNCHER" "$AT/lib" -no-stdlib -classpath "$CP" -opt-in=kotlin.concurrent.atomics.ExperimentalAtomicApi -d "$AT/libbir" >/dev/null 2>&1 || true
emit_il "$AT/libil" AtomicLib "$AT/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$AT/libil/AtomicLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$AT/imports.txt" "$AT/app"/*.kt >/dev/null 2>&1 || true
# #73 TRIGGER: pass BOTH stdlib twins (REFERENCE + RUNTIME) on facadegen's compile set, exactly as a real MSBuild
# consumer's @(ReferencePath) does — this is what the other sections do NOT do (they pass only the runtime twin).
AT_TWIN_REFS="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$STDLIB_RT_DLL");"
dotnet "$FACADEGEN_DLL" --meta "$AT/meta" --compile-refs "$AT_TWIN_REFS$AT/libil/AtomicLib.dll" --import-list "$AT/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$AT/meta" "$LAUNCHER" "$AT/app" -no-stdlib -classpath "$CP" -opt-in=kotlin.concurrent.atomics.ExperimentalAtomicApi -d "$AT/appbir" >/dev/null 2>&1 || true
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$NO/lib" -no-stdlib -classpath "$CP" -d "$NO/libbir" >/dev/null 2>&1 || true
emit_il "$NO/libil" NothingLib "$NO/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NO/libil/NothingLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$NO/meta" --compile-refs "$REFS$NO/libil/NothingLib.dll" Boom LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$NO/meta" "$LAUNCHER" "$NO/app" -no-stdlib -classpath "$CP" -d "$NO/appbir" >/dev/null 2>&1 || true
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$NS/lib" -no-stdlib -classpath "$CP" -d "$NS/libbir" >/dev/null 2>&1 || true
emit_il "$NS/libil" SNothingLib "$NS/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NS/libil/SNothingLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$NS/meta" --compile-refs "$REFS$NS/libil/SNothingLib.dll" LibKt System.Threading.Monitor >/dev/null 2>&1 || true
write_coharness "$NS/app"
CLR_TYPES_METADATA="$NS/meta" "$LAUNCHER" "$NS/app" -no-stdlib -classpath "$CP" -d "$NS/appbir" >/dev/null 2>&1 || true
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$R/lib" -no-stdlib -classpath "$CP" -d "$R/libbir" >/dev/null 2>&1 || true
emit_il "$R/libil" KLib "$R/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$R/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
# 2. facadegen --meta reads the attributes back into the injection metadata.
dotnet "$FACADEGEN_DLL" --meta "$R/k.meta" --compile-refs "$REFS$R/libil/KLib.dll" Vec LibKt System.Threading.Monitor >/dev/null 2>&1 || true
write_coharness "$R/app"
# 3. compile the consumer WITH the metadata (the injector restores infix/operator/suspend/top-level on FIR).
CLR_TYPES_METADATA="$R/k.meta" "$LAUNCHER" "$R/app" -no-stdlib -classpath "$CP" -d "$R/appbir" >/dev/null 2>&1 || true
emit_il "$R/appil" KApp --ref "$R/libil/KLib.dll" "$R/appbir"/*.bir.json
cp "$R/libil/KLib.dll" "$R/appil/" 2>/dev/null || true

expected="$(printf '11\n(4, 6)\nHi, Vec\n42\n(3, 6)')"
run_app actual "$R/appil/KApp.dll"
check_output roundtrip "$expected" "$actual" "infix / operator / suspend / top-level restored from a DotKt assembly"

# ----- PACKAGED round-trip: Kotlin packages project to .NET namespaces, consumed via package-qualified imports -----
# Also guards the correctness bug where same-named classes in different packages collided at the root namespace.
G="$ROOT/build/roundtrip-pkg"; rm -rf "$G"; mkdir -p "$G/lib" "$G/app" "$G/libbir" "$G/libil" "$G/appbir" "$G/appil"
cat > "$G/lib/geom.kt" <<'EOF'
package geom
enum class Dir { NORTH, EAST }
class Vec(var x: Int, var y: Int) {
    infix fun dot(o: Vec): Int = x * o.x + y * o.y
    val mag2: Int get() = x * x + y * y          // property with a custom getter
}
operator fun Vec.plus(o: Vec): Vec = Vec(x + o.x, y + o.y)   // top-level extension operator
val Vec.manhattan: Int get() = x + y                          // extension property
fun sumAll(vararg xs: Int): Int { var s = 0; for (v in xs) s += v; return s }   // vararg
fun tagged(s: String = "def"): String = s                    // default argument
fun orNone(s: String?): String = s ?: "none"                 // nullable parameter
fun greet(name: String): String = "Hi, " + name
inline fun <reified T> typeName(): String = T::class.simpleName ?: "?"   // reified inline -> generic method
inline fun forEach3(a: Int, b: Int, c: Int, action: (Int) -> Unit) { action(a); action(b); action(c) }
EOF
# A class with the SAME simple name in a DIFFERENT package — must not collide (they used to, at the root namespace).
cat > "$G/lib/other.kt" <<'EOF'
package other
class Vec(val tag: String)
EOF
cat > "$G/app/app.kt" <<'EOF'
import geom.Vec
import geom.Dir
import geom.greet
import geom.typeName
import geom.forEach3
import geom.plus
import geom.manhattan
import geom.sumAll
import geom.tagged
import geom.orNone
fun firstEven(): Int {
    forEach3(1, 3, 4) { if (it % 2 == 0) return it }   // NON-LOCAL return through a CROSS-MODULE inline lambda
    return -1
}
fun main() {
    println(Vec(1, 2) dot Vec(3, 4))   // geom.Vec, infix
    println(greet("pkg"))              // top-level via `import geom.greet`
    println(Dir.EAST)                  // enum in a package
    println(typeName<String>())        // cross-module reified inline -> generic method call
    println(firstEven())               // cross-module inline + lambda + non-local return -> spliced body
    val v = Vec(3, 4); println(v.mag2) // property (custom getter)
    v.x = 6; println(v.mag2)           // mutable property write
    println((Vec(1, 2) + Vec(3, 4)).mag2)  // top-level extension operator + property
    println(sumAll(1, 2, 3, 4))        // vararg
    println(Vec(3, 4).manhattan)       // extension property
    println(tagged())                  // default argument omitted
    println(orNone(null))              // nullable param (null passable)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$G/lib" -no-stdlib -classpath "$CP" -d "$G/libbir" >/dev/null 2>&1 || true
emit_il "$G/libil" GeomLib "$G/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$G/libil/GeomLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$G/imports.txt" "$G/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$G/meta" --compile-refs "$REFS$G/libil/GeomLib.dll" --import-list "$G/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$G/meta" "$LAUNCHER" "$G/app" -no-stdlib -classpath "$CP" -d "$G/appbir" >/dev/null 2>&1 || true
emit_il "$G/appil" GeomApp --ref "$G/libil/GeomLib.dll" "$G/appbir"/*.bir.json
cp "$G/libil/GeomLib.dll" "$G/appil/" 2>/dev/null || true
pkgexpected="$(printf '11\nHi, pkg\nEAST\nString\n4\n25\n52\n52\n10\n7\ndef\nnone')"
run_app pkgactual "$G/appil/GeomApp.dll"
check_output roundtrip-pkg "$pkgexpected" "$pkgactual" "namespace; reified inline; non-local return; properties; ext operator/property; vararg; default arg; nullable"


# ----- CROSS-MODULE inline MEMBER + non-local return (F1 / #60) -----
# roundtrip-pkg (above) proved a cross-module inline TOP-LEVEL fn splices a non-local return. This proves the MEMBER
# case: `class C { inline fun pick(block) }` restored from a DotKt assembly (isInline=true, body==null, a DISPATCH
# receiver). Before F1 the member failed kotc's `dispatchReceiver(call) == null` cross-module gate and fell to the plain
# `callInstance` path + a REAL delegate for the block, so a non-local `return` inside the block returned from the
# DELEGATE, not the caller — a SILENT miscompile (`caller()` fell through to -1 instead of returning 99). kotc now emits
# a member-aware `callInline` carrying `recvs.dispatch`; bir2cir's InlineSplice §4.3 binds it (the payload's `{k:this}`
# member-field reads rebind to the caller-provided receiver) and routes the non-local return to the CALLER. NOT in
# RT_XFAIL — it must pass. `matched()` also exercises the dispatch-receiver `this.c` field read in the spliced body.
IM="$ROOT/build/roundtrip-inline-member"; rm -rf "$IM"; mkdir -p "$IM/lib" "$IM/app" "$IM/libbir" "$IM/libil" "$IM/appbir" "$IM/appil"
cat > "$IM/lib/lib.kt" <<'EOF'
package picker
class C(val a: Int, val b: Int, val c: Int) {
    inline fun pick(block: (Int) -> Boolean): Int {
        if (block(a)) return a      // dispatch-receiver `this.a` read inside a spliced inline member body
        if (block(b)) return b
        if (block(c)) return c
        return -1
    }
}
EOF
cat > "$IM/app/app.kt" <<'EOF'
import picker.C
fun caller(): Int {
    val c = C(10, 20, 30)
    c.pick { x -> if (x == 20) return 99; false }   // NON-LOCAL return from caller() through the CROSS-MODULE inline MEMBER
    return -1                                        // must NOT be reached: pick sees 20 -> the block returns 99 from caller()
}
fun matched(): Int {
    val c = C(10, 20, 30)
    return c.pick { x -> x == 30 }                   // pick's own early `return c` (dispatch-receiver read) yields 30
}
fun main() {
    println(caller())    // 99 — the non-local return escapes the CALLER, not the delegate
    println(matched())   // 30 — inline-member body early-return + `this.c` field read
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$IM/lib" -no-stdlib -classpath "$CP" -d "$IM/libbir" >/dev/null 2>&1 || true
emit_il "$IM/libil" PickLib "$IM/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$IM/libil/PickLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$IM/imports.txt" "$IM/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$IM/meta" --compile-refs "$REFS$IM/libil/PickLib.dll" --import-list "$IM/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$IM/meta" "$LAUNCHER" "$IM/app" -no-stdlib -classpath "$CP" -d "$IM/appbir" >/dev/null 2>&1 || true
emit_il "$IM/appil" PickApp --ref "$IM/libil/PickLib.dll" "$IM/appbir"/*.bir.json
cp "$IM/libil/PickLib.dll" "$IM/appil/" 2>/dev/null || true
imexpected="$(printf '99\n30')"
run_app imactual "$IM/appil/PickApp.dll"
check_output roundtrip-inline-member "$imexpected" "$imactual" "cross-module inline MEMBER + non-local return from the caller + dispatch-receiver field read in the spliced body (F1 #60)"


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
CLR_TYPES_METADATA="" "$LAUNCHER" "$GG/lib" -no-stdlib -classpath "$CP" -d "$GG/libbir" >/dev/null 2>&1 || true
emit_il "$GG/libil" KLib "$GG/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$GG/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$GG/k.meta" --compile-refs "$REFS$GG/libil/KLib.dll" Box Pair2 Holder LibKt System.Threading.Monitor >/dev/null 2>&1 || true
write_coharness "$GG/app"
CLR_TYPES_METADATA="$GG/k.meta" "$LAUNCHER" "$GG/app" -no-stdlib -classpath "$CP" -d "$GG/appbir" >/dev/null 2>&1 || true
emit_il "$GG/appil" KApp --ref "$GG/libil/KLib.dll" "$GG/appbir"/*.bir.json
cp "$GG/libil/KLib.dll" "$GG/appil/" 2>/dev/null || true
gexpected="$(printf '3\n4\n10\n5\n1/z\n99\n8\n6\n7\nhi\nnone\nset\n4')"
run_app gactual "$GG/appil/KApp.dll"
check_output roundtrip-generic "$gexpected" "$gactual" "user generics in every position × operator/infix/extension/suspend/nullable/default/vararg"

# ----- HIGHER-ORDER generics: a function-type parameter whose ARG/RETURN is a generic user type (`(Box<U>)->Box<V>`) -----
# The metadata type grammar is a recursive structured type-node tree (an `fn` node's `ret`/`params` are themselves type
# nodes), so a generic user type — an `fqn` node with `args` — nests inside a lambda parameter: top-level / member /
# extension / infix / operator / inline all carry it.
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$HF/lib" -no-stdlib -classpath "$CP" -d "$HF/libbir" >/dev/null 2>&1 || true
emit_il "$HF/libil" KLib "$HF/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$HF/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$HF/k.meta" --compile-refs "$REFS$HF/libil/KLib.dll" Box Wrap LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$HF/k.meta" "$LAUNCHER" "$HF/app" -no-stdlib -classpath "$CP" -d "$HF/appbir" >/dev/null 2>&1 || true
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$RL/lib" -no-stdlib -classpath "$CP" -d "$RL/libbir" >/dev/null 2>&1 || true
emit_il "$RL/libil" UiLib "$RL/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$RL/libil/UiLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$RL/imports.txt" "$RL/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$RL/meta" --compile-refs "$REFS$RL/libil/UiLib.dll" --import-list "$RL/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$RL/meta" "$LAUNCHER" "$RL/app" -no-stdlib -classpath "$CP" -d "$RL/appbir" >/dev/null 2>&1 || true
emit_il "$RL/appil" UiApp --ref "$RL/libil/UiLib.dll" "$RL/appbir"/*.bir.json
cp "$RL/libil/UiLib.dll" "$RL/appil/" 2>/dev/null || true
rlexpected="$(printf '4\n7\n105\n9\n8')"
run_app rlactual "$RL/appil/UiApp.dll"
check_output roundtrip-receiver-lambda "$rlexpected" "$rlactual" "receiver-lambda P.() -> Unit restored cross-module: param (top-level/member/multi) + top-level-val + member-property positions #145"

# ----- MEMBER-declared extension functions: `class C { fun T.f() }` consumed via `with(c) { x.f() }` -----
# Covers the cross-product: plain / infix / operator / inline+generic-method / protected, on a generic user receiver.
# Restored via the `,ext` marker (the first param `__self` becomes the extension receiver); the consumer dispatches on
# the enclosing instance with the extension receiver prepended. (Member extension PROPERTIES and SUSPEND member
# extensions are covered by the next section.)
ME="$ROOT/build/roundtrip-memext"; rm -rf "$ME"; mkdir -p "$ME/lib" "$ME/app" "$ME/libbir" "$ME/libil" "$ME/appbir" "$ME/appil"
cat > "$ME/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) { fun get(): T = value }
open class Lib(val k: Int) {
    fun Box<Int>.boost(): Int = get() + k                          // member extension function
    infix fun Box<Int>.glue(o: Box<Int>): Int = get() + o.get() + k // member extension infix
    operator fun Box<Int>.times(n: Int): Int = get() * n + k        // member extension operator
    inline fun <R> Box<Int>.mapped(f: (Int) -> R): R = f(get())     // member extension + inline + generic method + lambda
    protected fun Box<Int>.sshh(): Int = get() * 100 + k           // protected member extension
    fun useProt(b: Box<Int>): Int = b.sshh()                       // protected used internally
}
EOF
cat > "$ME/app/app.kt" <<'EOF'
fun main() {
    val lib = Lib(10)
    with(lib) {
        println(Box(5).boost())            // 15
        println(Box(2) glue Box(3))        // 15
        println(Box(4) * 3)                // 22
        println(Box(7).mapped { it + 1 })  // 8
    }
    println(lib.useProt(Box(1)))           // 110
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$ME/lib" -no-stdlib -classpath "$CP" -d "$ME/libbir" >/dev/null 2>&1 || true
emit_il "$ME/libil" KLib "$ME/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$ME/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$ME/k.meta" --compile-refs "$REFS$ME/libil/KLib.dll" Box Lib >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$ME/k.meta" "$LAUNCHER" "$ME/app" -no-stdlib -classpath "$CP" -d "$ME/appbir" >/dev/null 2>&1 || true
emit_il "$ME/appil" KApp --ref "$ME/libil/KLib.dll" "$ME/appbir"/*.bir.json
cp "$ME/libil/KLib.dll" "$ME/appil/" 2>/dev/null || true
meexpected="$(printf '15\n15\n22\n8\n110')"
run_app meactual "$ME/appil/KApp.dll"
check_output roundtrip-memext "$meexpected" "$meactual" "member extension functions: plain/infix/operator/inline-generic/protected, consumed via with"

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
CLR_TYPES_METADATA="" "$LAUNCHER" "$MP/lib" -no-stdlib -classpath "$CP" -d "$MP/libbir" >/dev/null 2>&1 || true
emit_il "$MP/libil" KLib "$MP/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$MP/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$MP/k.meta" --compile-refs "$REFS$MP/libil/KLib.dll" Box Lib System.Threading.Monitor >/dev/null 2>&1 || true
write_coharness "$MP/app"
CLR_TYPES_METADATA="$MP/k.meta" "$LAUNCHER" "$MP/app" -no-stdlib -classpath "$CP" -d "$MP/appbir" >/dev/null 2>&1 || true
emit_il "$MP/appil" KApp --ref "$MP/libil/KLib.dll" "$MP/appbir"/*.bir.json
cp "$MP/libil/KLib.dll" "$MP/appil/" 2>/dev/null || true
mpexpected="$(printf 'lbl:17\n30\n15\n1002\n15\n210')"
run_app mpactual "$MP/appil/KApp.dll"
check_output roundtrip-memext2 "$mpexpected" "$mpactual" "member extension properties + suspend member extensions, public + protected"

# ----- DEFAULT ARGUMENTS + NAMED ARGUMENTS: trailing/named-middle/reordered omission, on functions AND constructors -----
# A restored default arg now carries a REAL constant value (`opt:Type=<const>` in the metadata -> a FirLiteralExpression
# applied via replaceDefaultValue), so the consumer can omit it ANYWHERE: trailing, NAMED-MIDDLE (`box(1, c=9)` — skip a
# middle default, provide a later one — which the old @JvmOverloads positional overloads could NOT express), or reordered
# named. Constructors too (`Pt(y=4)`; ilemit now also emits ctor parameter NAMES). String defaults with spaces survive
# (escaped in the token). (.NET BCL methods with an enum/struct default fall back to @JvmOverloads trailing overloads.)
DA="$ROOT/build/roundtrip-defargs"; rm -rf "$DA"; mkdir -p "$DA/lib" "$DA/app" "$DA/libbir" "$DA/libil" "$DA/appbir" "$DA/appil"
cat > "$DA/lib/lib.kt" <<'EOF'
fun greet(name: String, greeting: String = "Hi", punct: String = "!"): String = "$greeting, $name$punct"
fun box(a: Int, b: Int = 2, c: Int = 3): Int = a * 100 + b * 10 + c
fun flags(on: Boolean = true, label: String = "x y"): String = "$on/$label"
// non-Int kinds + a NULLABLE (`= null`) default, to lock every metaConstArg kind + the null-literal path
fun kinds(tag: String, n: Long = 5L, r: Double = 1.5, ch: Char = 'z', note: String? = null): String =
    "$tag/$n/$r/$ch/${note ?: "none"}"
class Pt(val x: Int = 0, val y: Int = 0) { override fun toString(): String = "($x,$y)" }
EOF
cat > "$DA/app/app.kt" <<'EOF'
fun main() {
    println(greet("A"))                          // Hi, A!
    println(greet("B", "Yo"))                     // Yo, B!   trailing omit
    println(greet("C", punct = "?"))              // Hi, C?   NAMED MIDDLE omission
    println(greet(greeting = "Hey", name = "E"))  // Hey, E!  reordered named
    println(box(1))                               // 123
    println(box(1, c = 9))                        // 129      NAMED MIDDLE omission
    println(box(a = 5, c = 7))                    // 527      named middle omission
    println(flags())                              // True/x y string default with a space
    println(flags(label = "z"))                   // True/z   named middle omission
    println(kinds("a"))                           // a/5/1.5/z/none      all defaults (Long/Double/Char/null)
    println(kinds("b", ch = 'q'))                 // b/5/1.5/q/none      NAMED MIDDLE omit skipping Long+Double
    println(kinds("c", note = "hi"))              // c/5/1.5/z/hi        NAMED-MIDDLE omit filling the null-default slot
    println(Pt(y = 4))                            // (0,4)    ctor named middle omission
    println(Pt(x = 7))                            // (7,0)    ctor named
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$DA/lib" -no-stdlib -classpath "$CP" -d "$DA/libbir" >/dev/null 2>&1 || true
emit_il "$DA/libil" KLib "$DA/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$DA/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$DA/k.meta" --compile-refs "$REFS$DA/libil/KLib.dll" Pt LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$DA/k.meta" "$LAUNCHER" "$DA/app" -no-stdlib -classpath "$CP" -d "$DA/appbir" >/dev/null 2>&1 || true
emit_il "$DA/appil" KApp --ref "$DA/libil/KLib.dll" "$DA/appbir"/*.bir.json
cp "$DA/libil/KLib.dll" "$DA/appil/" 2>/dev/null || true
daexpected="$(printf 'Hi, A!\nYo, B!\nHi, C?\nHey, E!\n123\n129\n527\nTrue/x y\nTrue/z\na/5/1.5/z/none\nb/5/1.5/q/none\nc/5/1.5/z/hi\n(0,4)\n(7,0)')"
run_app daactual "$DA/appil/KApp.dll"
check_output roundtrip-defargs "$daexpected" "$daactual" "default args: trailing/named-middle/reordered omission, on functions + constructors"

# ----- NON-CONST default args (#146): `= {}` / an expression default filled cross-module -----
# #134 carried a CONSTANT default as a metadata value. #146 extends the SAME @KotlinDefault mechanism to a NON-CONST
# default — an empty lambda `= {}` (THE Avalonia DSL idiom `configure: Panel.() -> Unit = {}`, composed with #145's
# receiver lambda), a plain empty lambda, and a simple-expression default `= emptyList()`. kotc carries the default as a
# CLOSED BIR sub-tree in `[kotlin.clr.KotlinDefault]` (a non-capturing lambda's lifted method rides a `defaultCarrier`
# envelope); facadegen marks the injected param OPTIONAL (nonConst) so the consumer frontend accepts the omission; and
# bir2cir's DefaultArgSplice (now PHASE 1) fills the omitted slot, re-hoisting a carried lambda app-local (fresh name) so
# it re-lowers in THIS app's context. The empty-lambda default fills to `{}`. See docs/dotkt-semantics.md §10.
NC="$ROOT/build/roundtrip-nonconst-default"; rm -rf "$NC"; mkdir -p "$NC/lib" "$NC/app" "$NC/libbir" "$NC/libil" "$NC/appbir" "$NC/appil"
cat > "$NC/lib/lib.kt" <<'EOF'
package ui
class Panel { var margin: Int = 0; fun add(s: String): Int { margin += s.length; return margin } }
fun column(configure: Panel.() -> Unit = {}, build: Panel.() -> Unit): Int { val p = Panel(); p.configure(); p.build(); return p.margin }
fun run2(pre: () -> Unit = {}, body: () -> Unit): String { pre(); body(); return "ok" }
fun tagged(name: String, items: List<String> = emptyList()): String = "$name=${items.size}"
EOF
cat > "$NC/app/app.kt" <<'EOF'
import ui.Panel
import ui.column
import ui.run2
import ui.tagged
fun main() {
    println(column(build = { add("hi") }))                          // 2   configure defaults to {} (empty receiver lambda)
    println(column(configure = { add("ab") }, build = { add("c") })) // 3   both provided (no fill)
    println(run2(body = { print("") }))                             // ok  pre defaults to {} (empty plain lambda)
    println(tagged("z"))                                            // z=0 items defaults to emptyList() (simple-expr default)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$NC/lib" -no-stdlib -classpath "$CP" -d "$NC/libbir" >/dev/null 2>&1 || true
emit_il "$NC/libil" UiLib "$NC/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NC/libil/UiLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$NC/imports.txt" "$NC/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$NC/meta" --compile-refs "$REFS$NC/libil/UiLib.dll" --import-list "$NC/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$NC/meta" "$LAUNCHER" "$NC/app" -no-stdlib -classpath "$CP" -d "$NC/appbir" >/dev/null 2>&1 || true
emit_il "$NC/appil" UiApp --ref "$NC/libil/UiLib.dll" "$NC/appbir"/*.bir.json
cp "$NC/libil/UiLib.dll" "$NC/appil/" 2>/dev/null || true
ncexpected="$(printf '2\n3\nok\nz=0')"
run_app ncactual "$NC/appil/UiApp.dll"
check_output roundtrip-nonconst-default "$ncexpected" "$ncactual" "non-const default args (#146): empty receiver/plain lambda = {} + simple-expr = emptyList() filled cross-module"

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
CLR_TYPES_METADATA="" "$LAUNCHER" "$SF/lib" -no-stdlib -classpath "$CP" -d "$SF/libbir" >/dev/null 2>&1 || true
emit_il "$SF/libil" HofLib "$SF/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$SF/libil/HofLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$SF/k.meta" --compile-refs "$REFS$SF/libil/HofLib.dll" hof.LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$SF/k.meta" "$LAUNCHER" "$SF/app" -no-stdlib -classpath "$CP" -d "$SF/appbir" >/dev/null 2>&1 || true
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
CLR_TYPES_METADATA="" "$LAUNCHER" "$SR/lib" -no-stdlib -classpath "$CP" -d "$SR/libbir" >/dev/null 2>&1 || true
emit_il "$SR/libil" Hof2Lib "$SR/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$SR/libil/Hof2Lib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$SR/k.meta" --compile-refs "$REFS$SR/libil/Hof2Lib.dll" hof2.LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$SR/k.meta" "$LAUNCHER" "$SR/app" -no-stdlib -classpath "$CP" -d "$SR/appbir" >/dev/null 2>&1 || true
emit_il "$SR/appil" Hof2App --ref "$SR/libil/Hof2Lib.dll" "$SR/appbir"/*.bir.json
cp "$SR/libil/Hof2Lib.dll" "$SR/appil/" 2>/dev/null || true
srexpected="$(printf '42\n30\n107')"
run_app sractual "$SR/appil/Hof2App.dll"
check_output roundtrip-suspendfn-ret "$srexpected" "$sractual" "a suspend (…) -> T VALUE round-trips in RETURN + PROPERTY + FIELD position: bir2cir lowers a value-position suspendLambdaNew to a SuspendLambda SM, the consumer drives it"

# ----- TOP-LEVEL VAL/VAR round-trip (#34b): read a library's top-level property DIRECTLY, no fn workaround ----
# A top-level `val greeting = "hi"` compiles (kotc) to a plain Public|Static FIELD on the file class (`tlval.LibKt`),
# with NO get_/set_ accessor (only backing-field-LESS props — extension/computed — get accessors). facadegen now
# surfaces each such field as a `tlprop <name> <type> <ro|rw>` meta token (Program.cs EmitKotlinFileClass), mirroring
# the `tlfun`/`tlextprop` top-level path; the .NET file-class FQN rides the enclosing `file` line. This section proves
# a consumer reads the library's top-level `val`/`var` DIRECTLY (`import tlval.greeting`), NOT via a function that
# re-exposes the value (the H2 workaround the roundtrip-suspendfn-ret section had to use). Cases: a `val: String`, a
# `var: Int` (read + write `+=`), and a `val` of a USER type (`Point`).
SV="$ROOT/build/roundtrip-toplevel-val"; rm -rf "$SV"; mkdir -p "$SV/lib" "$SV/app" "$SV/libbir" "$SV/libil" "$SV/appbir" "$SV/appil"
cat > "$SV/lib/lib.kt" <<'EOF'
package tlval
class Point(val x: Int, val y: Int) { override fun toString(): String = "($x, $y)" }
val greeting: String = "hi"       // top-level val -> static field, read cross-module directly
var counter: Int = 40             // top-level var -> read + write cross-module
val origin: Point = Point(1, 2)   // top-level val of a USER type
EOF
cat > "$SV/app/app.kt" <<'EOF'
import tlval.greeting
import tlval.counter
import tlval.origin
fun main() {
    println(greeting)   // hi
    counter += 2
    println(counter)    // 42
    println(origin)     // (1, 2)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$SV/lib" -no-stdlib -classpath "$CP" -d "$SV/libbir" >/dev/null 2>&1 || true
emit_il "$SV/libil" TlvalLib "$SV/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$SV/libil/TlvalLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$SV/k.meta" --compile-refs "$REFS$SV/libil/TlvalLib.dll" tlval.LibKt tlval.Point >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$SV/k.meta" "$LAUNCHER" "$SV/app" -no-stdlib -classpath "$CP" -d "$SV/appbir" >/dev/null 2>&1 || true
emit_il "$SV/appil" TlvalApp --ref "$SV/libil/TlvalLib.dll" "$SV/appbir"/*.bir.json
cp "$SV/libil/TlvalLib.dll" "$SV/appil/" 2>/dev/null || true
svexpected="$(printf 'hi\n42\n(1, 2)')"
run_app svactual "$SV/appil/TlvalApp.dll"
check_output roundtrip-toplevel-val "$svexpected" "$svactual" "a top-level val/var round-trips: the consumer reads the library's top-level property DIRECTLY (no fn workaround) via the facadegen tlprop meta token"

# ----- CUSTOM-ACCESSOR field-backed property round-trip (#103) -------------------------------------------
# A field-backed property with a CUSTOM accessor (`val x = 41; get() = field + 1`) compiles to a static/backing FIELD
# PLUS a `get_/set_<name>` accessor method carrying the custom body. Read/written cross-module, the consumer must INVOKE
# the accessor, NOT touch the raw field — else the custom getter/setter is silently BYPASSED (the #103 miscompile: a
# top-level `val topProp get()=field+1` returned the raw 41 instead of 42). #89 fixed the SAME-MODULE shape; this is its
# cross-module twin: facadegen marks the tlprop `customGet`/`customSet` (Program.cs EmitKotlinFileClass, skipping the
# loose accessor fun), kotc restores it and routes the read/write through the accessor (BirEmitterCalls injected branch).
# Covers TOP-LEVEL (the broken case) + companion + member field-backed props, and the independent get/set customness.
CA="$ROOT/build/roundtrip-customprop"; rm -rf "$CA"; mkdir -p "$CA/lib" "$CA/app" "$CA/libbir" "$CA/libil" "$CA/appbir" "$CA/appil"
cat > "$CA/lib/lib.kt" <<'EOF'
package cprop
val topProp: Int = 41
    get() = field + 1               // custom getter -> 42, NOT the raw 41
var topVar: Int = 0
    set(value) { field = value + 5 } // custom setter: set(10) -> 15 (default getter reads the field)
var topGetVar: Int = 100
    get() = field - 1                // custom getter + DEFAULT setter: set(50) then read -> 49
class Host {
    val kProp: Int = 7
        get() = field + 100          // member field-backed val, custom getter -> 107
    var kVar: Int = 0
        set(value) { field = value * 2 } // member var, custom setter: set(3) -> 6
    companion object {
        val cProp: Int = 10
            get() = field * 2        // companion field-backed val, custom getter -> 20
    }
}
EOF
cat > "$CA/app/app.kt" <<'EOF'
import cprop.topProp
import cprop.topVar
import cprop.topGetVar
import cprop.Host
fun main() {
    println(topProp)          // 42 (custom getter, not raw 41)
    val h = Host()
    println(h.kProp)          // 107
    println(Host.cProp)       // 20
    topVar = 10
    println(topVar)           // 15 (custom setter)
    h.kVar = 3
    println(h.kVar)           // 6 (custom setter)
    topGetVar = 50
    println(topGetVar)        // 49 (custom getter, default setter)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$CA/lib" -no-stdlib -classpath "$CP" -d "$CA/libbir" >/dev/null 2>&1 || true
emit_il "$CA/libil" CpropLib "$CA/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$CA/libil/CpropLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$CA/k.meta" --compile-refs "$REFS$CA/libil/CpropLib.dll" cprop.LibKt cprop.Host >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$CA/k.meta" "$LAUNCHER" "$CA/app" -no-stdlib -classpath "$CP" -d "$CA/appbir" >/dev/null 2>&1 || true
emit_il "$CA/appil" CpropApp --ref "$CA/libil/CpropLib.dll" "$CA/appbir"/*.bir.json
cp "$CA/libil/CpropLib.dll" "$CA/appil/" 2>/dev/null || true
caexpected="$(printf '42\n107\n20\n15\n6\n49')"
run_app caactual "$CA/appil/CpropApp.dll"
check_output roundtrip-customprop "$caexpected" "$caactual" "field-backed property with a CUSTOM accessor, consumed cross-module, invokes the getter/setter (not the raw field) — #103; top-level + companion + member, independent get/set customness"

# ----- TRI-STATE NULLABILITY (NRT) round-trip (#48): T / T? restored via the NullableAttribute byte + value Nullable<int> -----
# #48 unified tri-state nullability (T / T? / T!) with proper NRT emission. The sharp proof that the NullableAttribute
# byte round-trips faithfully is NOT runtime output (a reference `String?` is bare `String` at the CLR level — null
# passes regardless of the byte), it is the CONSUMER's COMPILE-ABILITY: a mis-restored nullability makes the consumer
# fail to compile, reddening the gate. This section exercises both CLR nullability MECHANISMS:
#   - reference NRT byte:  non-null String (byte 1) and nullable String? (byte 2).
#   - value structural:    Int? = System.Nullable<int> (NOT NRT — a distinct CLR shape that must also round-trip).
# Sharp compile-dependencies (a wrong NRT byte -> the consumer will NOT compile -> `nrtactual` stays empty -> FAIL):
#   * `retNonNull().length` with NO `?.` compiles ONLY if the return restored non-null (byte 1). A mis-restore to
#     String? -> "only safe (?.) or non-null asserted (!!.) calls allowed on a nullable receiver" -> consumer fails.
#   * `takeNullable(null)` compiles ONLY if the param restored nullable (byte 2). A mis-restore to non-null String ->
#     "null can not be a value of a non-null type String" -> consumer fails. (THE sharp T? signal.)
# The consumer also prints deterministic values (lengths / -1 for nulls) as a second signal: a value Int? mis-restored
# to non-null would mis-drive at runtime. (T! / oblivious byte 0 is covered by the netbase/netgen/netinterop il-samples —
# a .NET member with no NullableAttribute round-trips to ConeFlexibleType there; adding a System.* seed here would mix
# facadegen's --meta and import-list paths, so per the task it is intentionally left to those sections.)
NRT="$ROOT/build/roundtrip-nrt"; rm -rf "$NRT"; mkdir -p "$NRT/lib" "$NRT/app" "$NRT/libbir" "$NRT/libil" "$NRT/appbir" "$NRT/appil"
cat > "$NRT/lib/lib.kt" <<'EOF'
fun retNonNull(): String = "x"                                     // T  (non-null return, NullableAttribute byte 1)
fun takeNonNull(s: String): Int = s.length                         // T  (non-null param)
fun retNullable(flag: Boolean): String? = if (flag) "y" else null  // T? (nullable return, byte 2)
fun takeNullable(s: String?): Int = s?.length ?: -1                // T? (nullable param — the sharp signal)
fun retNullableInt(flag: Boolean): Int? = if (flag) 1 else null    // value T? = System.Nullable<int> (structural)
EOF
cat > "$NRT/app/app.kt" <<'EOF'
fun main() {
    println(retNonNull().length)             // 1   NO ?. — compiles only if the return restored non-null (byte 1)
    println(takeNonNull("abcd"))             // 4   non-null param called with a non-null
    println(retNullable(false)?.length ?: -1)// -1  nullable return, null branch
    println(retNullable(true)?.length ?: -1) // 1   nullable return, value branch
    println(takeNullable(null))              // -1  passing null compiles only if the param restored nullable (byte 2)
    println(takeNullable("hello"))           // 5   nullable param with a non-null arg
    println(retNullableInt(false) ?: -1)     // -1  value Nullable<int> — the null (HasValue=false) branch
    println(retNullableInt(true) ?: -1)      // 1   value Nullable<int> — the value branch
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$NRT/lib" -no-stdlib -classpath "$CP" -d "$NRT/libbir" >/dev/null 2>&1 || true
emit_il "$NRT/libil" NrtLib "$NRT/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NRT/libil/NrtLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$NRT/k.meta" --compile-refs "$REFS$NRT/libil/NrtLib.dll" LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$NRT/k.meta" "$LAUNCHER" "$NRT/app" -no-stdlib -classpath "$CP" -d "$NRT/appbir" >/dev/null 2>&1 || true
emit_il "$NRT/appil" NrtApp --ref "$NRT/libil/NrtLib.dll" "$NRT/appbir"/*.bir.json
cp "$NRT/libil/NrtLib.dll" "$NRT/appil/" 2>/dev/null || true
nrtexpected="$(printf '1\n4\n-1\n1\n-1\n5\n-1\n1')"
run_app nrtactual "$NRT/appil/NrtApp.dll"
check_output roundtrip-nrt "$nrtexpected" "$nrtactual" "tri-state NRT fidelity: non-null (byte 1) + nullable (byte 2) reference via consumer compile-dependency, + value Nullable<int> structural"

# ---- UNSIGNED BYTE round-trip (#53): UByte / UByteArray fidelity through the DotKt emit -> facadegen consume cycle ----
# A DotKt lib exposes UByte / UByteArray / a UByte-consuming fun. Emitted, the lib's CLR surface is System.Byte /
# System.Byte[]. facadegen (STRICT #53) must surface them BACK as kotlin.UByte / kotlin.UByteArray (NOT the lossy signed
# Byte/ByteArray) — proven by the consumer compiling AND reading value 200 as UByte 200 (a signed Byte would be -56).
UB="$ROOT/build/roundtrip-ubyte"; rm -rf "$UB"; mkdir -p "$UB/lib" "$UB/app" "$UB/libbir" "$UB/libil" "$UB/appbir" "$UB/appil"
cat > "$UB/lib/lib.kt" <<'EOF'
@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)
fun ub(): UByte = 200u                                   // emits System.Byte 200 -> facadegen surfaces as UByte
fun uba(): UByteArray = ubyteArrayOf(1u, 2u, 250u)       // emits System.Byte[] -> facadegen surfaces as UByteArray
fun takeUb(x: UByte): Int = x.toInt()                    // System.Byte param -> facadegen surfaces as UByte
EOF
cat > "$UB/app/app.kt" <<'EOF'
@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)
fun main() {
    val u: UByte = ub()          // compiles ONLY if the return restored UByte (not Byte)
    println(u.toInt())           // 200  unsigned fidelity (a mis-restored signed Byte would print -56)
    val a: UByteArray = uba()    // compiles ONLY if byte[] restored to UByteArray (not ByteArray/Array<UByte>)
    println(a.size)              // 3
    println(a[2].toInt())        // 250
    println(takeUb(200u))        // 200  pass a UByte to a UByte-restored param
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$UB/lib" -no-stdlib -classpath "$CP" -d "$UB/libbir" >/dev/null 2>&1 || true
emit_il "$UB/libil" UbLib "$UB/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$UB/libil/UbLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$UB/k.meta" --compile-refs "$REFS$UB/libil/UbLib.dll" LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$UB/k.meta" "$LAUNCHER" "$UB/app" -no-stdlib -classpath "$CP" -d "$UB/appbir" >/dev/null 2>&1 || true
emit_il "$UB/appil" UbApp --ref "$UB/libil/UbLib.dll" "$UB/appbir"/*.bir.json
cp "$UB/libil/UbLib.dll" "$UB/appil/" 2>/dev/null || true
ubexpected="$(printf '200\n3\n250\n200')"
run_app ubactual "$UB/appil/UbApp.dll"
check_output roundtrip-ubyte "$ubexpected" "$ubactual" "UByte/UByteArray strict-mapping fidelity: System.Byte->UByte + System.Byte[]->UByteArray via consumer compile-dependency + value 200"

# ----- #133 GENERIC-FIDELITY gaps (atomicfu CLR port): three RT_XFAIL reproducers, one per owning layer ----------
# The atomicfu port reported that a DotKt lib consumed AS KOTLIN loses fidelity for (1) a generic INLINE EXTENSION on a
# generic receiver, (2) an OPERATOR on a generic type, (3) a Kotlin `Nothing` return. Reproduced in-repo: in ALL three
# the facadegen META is CORRECT (verified: inline+ext+typeParams+receiver-generic Cell<T> for (1); operator bit +
# clrName:get for (2); the Nothing reader is landed for (3)). The failures are DOWNSTREAM of facadegen — each section is
# a ready reproducer that flips to FIXED when its owning layer (kotc / bir2cir / bir2cir+kotc) lands. See RT_XFAIL above.

# (1) GENERIC INLINE EXTENSION on a generic receiver — `c.update { it + 1 }` must infer T=Int from `c: Cell<Int>`. FIR
# DOES infer it (facadegen's __self: Cell<T> meta is correct); kotc's facadegen inline-splice path refuses the lambda +
# extension-receiver shape at BIR emit. Route: kotc BirEmitterCalls.kt.
GIE="$ROOT/build/roundtrip-generic-inline-ext"; rm -rf "$GIE"; mkdir -p "$GIE/lib" "$GIE/app" "$GIE/libbir" "$GIE/libil" "$GIE/appbir" "$GIE/appil"
cat > "$GIE/lib/lib.kt" <<'EOF'
class Cell<T>(var v: T)
inline fun <T> Cell<T>.update(fn: (T) -> T) { v = fn(v) }   // generic inline ext on a generic receiver
EOF
cat > "$GIE/app/app.kt" <<'EOF'
fun main() {
    val c = Cell(1)
    c.update { it + 1 }   // infer T=Int from the receiver Cell<T>
    println(c.v)          // 2
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$GIE/lib" -no-stdlib -classpath "$CP" -d "$GIE/libbir" >/dev/null 2>&1 || true
emit_il "$GIE/libil" KLib "$GIE/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$GIE/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$GIE/k.meta" --compile-refs "$REFS$GIE/libil/KLib.dll" Cell LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$GIE/k.meta" "$LAUNCHER" "$GIE/app" -no-stdlib -classpath "$CP" -d "$GIE/appbir" >/dev/null 2>&1 || true
emit_il "$GIE/appil" KApp --ref "$GIE/libil/KLib.dll" "$GIE/appbir"/*.bir.json
cp "$GIE/libil/KLib.dll" "$GIE/appil/" 2>/dev/null || true
run_app gieactual "$GIE/appil/KApp.dll"
check_output roundtrip-generic-inline-ext "2" "$gieactual" "a generic inline extension on a generic receiver infers T from the receiver and splices cross-module"

# (2) OPERATOR on a generic type — `r[1]` / `r2[0] = x` on `class Arr<T> { operator fun get/set }`. facadegen surfaces
# the operator bit + clrName:get; bir2cir binds the facadegen-injected owner's operator to the BCL indexer accessor
# get_Item/set_Item instead of the plain get/set method Kotlin emitted. Route: bir2cir NetInteropBinding.cs.
GOP="$ROOT/build/roundtrip-generic-operator"; rm -rf "$GOP"; mkdir -p "$GOP/lib" "$GOP/app" "$GOP/libbir" "$GOP/libil" "$GOP/appbir" "$GOP/appil"
cat > "$GOP/lib/lib.kt" <<'EOF'
class Arr<T>(val a: Array<T>) {
    operator fun get(i: Int): T = a[i]
    operator fun set(i: Int, x: T) { a[i] = x }
}
EOF
cat > "$GOP/app/app.kt" <<'EOF'
fun main() {
    val r = Arr(arrayOf("a", "b"))
    println(r[1])          // b   generic operator get
    val r2 = Arr(arrayOf(10, 20))
    r2[0] = 99             // generic operator set
    println(r2[0])         // 99
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$GOP/lib" -no-stdlib -classpath "$CP" -d "$GOP/libbir" >/dev/null 2>&1 || true
emit_il "$GOP/libil" KLib "$GOP/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$GOP/libil/KLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$GOP/k.meta" --compile-refs "$REFS$GOP/libil/KLib.dll" Arr LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$GOP/k.meta" "$LAUNCHER" "$GOP/app" -no-stdlib -classpath "$CP" -d "$GOP/appbir" >/dev/null 2>&1 || true
emit_il "$GOP/appil" KApp --ref "$GOP/libil/KLib.dll" "$GOP/appbir"/*.bir.json
cp "$GOP/libil/KLib.dll" "$GOP/appil/" 2>/dev/null || true
gopexpected="$(printf 'b\n99')"
run_app gopactual "$GOP/appil/KApp.dll"
check_output roundtrip-generic-operator "$gopexpected" "$gopactual" "a Kotlin operator get/set on a generic DotKt type resolves cross-module (plain get/set method, not the BCL get_Item indexer)"

# (3) Kotlin `Nothing` return — `fun fail(): Nothing`. Sharp COMPILE-dependency: `val y: String = if (c) "ok" else
# fail(...)` compiles to `String` ONLY if `fail` restored `Nothing` (else the if/else widens to Any? -> "expected String,
# actual Any?"). facadegen's reader is landed; needs bir2cir to stamp [KotlinNothing] + kotc coneOf to resolve it.
NR="$ROOT/build/roundtrip-nothing-return"; rm -rf "$NR"; mkdir -p "$NR/lib" "$NR/app" "$NR/libbir" "$NR/libil" "$NR/appbir" "$NR/appil"
cat > "$NR/lib/lib.kt" <<'EOF'
package fx
fun fail(msg: String): Nothing = throw RuntimeException(msg)
fun <T> pick(cond: Boolean, x: T): T = if (cond) x else fail("no")
EOF
cat > "$NR/app/app.kt" <<'EOF'
import fx.fail
import fx.pick
fun main() {
    println(pick(true, 7))                        // 7
    val y: String = if (true) "ok" else fail("x") // compiles as String only if fail(): Nothing round-tripped
    println(y)                                    // ok
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$NR/lib" -no-stdlib -classpath "$CP" -d "$NR/libbir" >/dev/null 2>&1 || true
emit_il "$NR/libil" NothLib "$NR/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$NR/libil/NothLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$NR/k.meta" --compile-refs "$REFS$NR/libil/NothLib.dll" fx.LibKt >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$NR/k.meta" "$LAUNCHER" "$NR/app" -no-stdlib -classpath "$CP" -d "$NR/appbir" >/dev/null 2>&1 || true
emit_il "$NR/appil" NothApp --ref "$NR/libil/NothLib.dll" "$NR/appbir"/*.bir.json
cp "$NR/libil/NothLib.dll" "$NR/appil/" 2>/dev/null || true
nrexpected="$(printf '7\nok')"
run_app nractual "$NR/appil/NothApp.dll"
check_output roundtrip-nothing-return "$nrexpected" "$nractual" "a Kotlin Nothing return round-trips: the consumer's if/else with a Nothing branch keeps the non-Nothing type (no Any? widening)"

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
CLR_TYPES_METADATA="" "$LAUNCHER" "$VD/lib" -no-stdlib -classpath "$CP" -d "$VD/libbir" >/dev/null 2>&1 || true
emit_il "$VD/libil" AnimalLib "$VD/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$VD/libil/AnimalLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$VD/imports.txt" "$VD/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$VD/meta" --compile-refs "$REFS$VD/libil/AnimalLib.dll" --import-list "$VD/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$VD/meta" "$LAUNCHER" "$VD/app" -no-stdlib -classpath "$CP" -d "$VD/appbir" >/dev/null 2>&1 || true
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

# ----- DOTTED FILENAME (#16): top-level funcs in a `*.common.kt` file class round-trip cross-module -----------
# A source file whose stem contains a dot (`api.common.kt`, the standard MPP common-fragment convention) compiles to
# a file-facade class. kotc must sanitize the stem's non-identifier chars to `_` (stock Kotlin: `Api_commonKt`)
# BEFORE it derives the class name — else the raw `Api.commonKt` is read by ilemit's DefineType as
# Namespace=demo.Api / Name=commonKt, so facadegen scanning package `demo` never surfaces its TOP-LEVEL functions
# (top-level CLASSES round-trip fine either way — they carry their own type name) -> a cross-module `unresolved
# reference` on `commonOnly`. After the fix the file class is `demo.Api_commonKt` and its top-level fun resolves.
DF="$ROOT/build/roundtrip-dotfile"; rm -rf "$DF"; mkdir -p "$DF/lib" "$DF/app" "$DF/libbir" "$DF/libil" "$DF/appbir" "$DF/appil"
cat > "$DF/lib/api.common.kt" <<'EOF'
package demo
fun commonOnly(x: Int): Int = x + 1     // top-level fun in a DOTTED-name file (the #16 regression surface)
class Box(var v: Int)                    // top-level class in the same file (round-trips either way)
EOF
cat > "$DF/app/app.kt" <<'EOF'
import demo.commonOnly
import demo.Box
fun main() {
    println(commonOnly(1))   // 2   top-level fun from the dotted-name file class (was `unresolved reference`)
    println(Box(2).v)        // 2   top-level class from the same file
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$DF/lib" -no-stdlib -classpath "$CP" -d "$DF/libbir" >/dev/null 2>&1 || true
emit_il "$DF/libil" DemoLib "$DF/libbir"/*.bir.json
dotnet "$RETARGET_DLL" "$DF/libil/DemoLib.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
"$LAUNCHER" --scan-imports --output "$DF/imports.txt" "$DF/app"/*.kt >/dev/null 2>&1 || true
dotnet "$FACADEGEN_DLL" --meta "$DF/meta" --compile-refs "$REFS$DF/libil/DemoLib.dll" --import-list "$DF/imports.txt" >/dev/null 2>&1 || true
CLR_TYPES_METADATA="$DF/meta" "$LAUNCHER" "$DF/app" -no-stdlib -classpath "$CP" -d "$DF/appbir" >/dev/null 2>&1 || true
emit_il "$DF/appil" DemoApp --ref "$DF/libil/DemoLib.dll" "$DF/appbir"/*.bir.json
cp "$DF/libil/DemoLib.dll" "$DF/appil/" 2>/dev/null || true
dfexpected="$(printf '2\n2')"
run_app dfactual "$DF/appil/DemoApp.dll"
check_output roundtrip-dotfile "$dfexpected" "$dfactual" "#16: top-level fun in a dotted-name file class (api.common.kt -> demo.Api_commonKt) resolves cross-module"

# ---- verdict --------------------------------------------------------------------------------------
echo "------------------------------------"
printf '%s\n' "${SUMMARY[@]}"
if (( ${#NEW_FAILS[@]} )); then
	echo "ROUNDTRIP GATE RED — section(s) failing outside the RT_XFAIL baseline: ${NEW_FAILS[*]}"
	exit 1
fi
echo "ROUNDTRIP GATE GREEN (every FAIL is RT_XFAIL-listed; a FIXED line above means prune the baseline)"

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
	# ---- #86, one entry per OBSERVABLE ---------------------------------------------------------------
	# These are deliberately NOT bundled. A section is a single stdout comparison, so an app driving three
	# faulty shapes reports one verdict and the FIRST fault hides the rest — a `main` that dies of
	# InvalidProgramException never reaches the TypeLoadException line, and the entry would keep claiming
	# both long after one was fixed. So each shape gets its own app, its own section, and its own
	# RT_XFAIL_SHAPE below; each prunes independently and flips to FIXED on its own.
	#
	# PRUNED by the uniform-erasure core step (`physical(s) = Erase(declaredKotlinType(s))` at every position,
	# the carrier and its NRT byte recorded from the PRE-erasure type, every USE typed `Subst(Erase(decl))`, and
	# the erasure propagated through override slots): the cross-module param/ctor section, its two same-module
	# siblings, the same-module and cross-module override narrowings, and all three top-level-`T?`-RETURN
	# entries. Their sections are green and unlisted now; the shapes they drove stay as the controls they were.
	#
	# PRUNED by the cross-module carrier READ (bir2cir reads `[KotlinNullableGeneric]` off the REFERENCED assembly
	# and types the consumer's use as `Subst(Erase(declared))`): the three nested-`Slot<T?>`-at-a-VALUE sections
	# below this list — the param, the property and the T=Boolean param. They now run 5 / 7 / False and are green
	# and unlisted; the null-path and reference-instantiation controls beside them stay as the controls they were.
	#
	# The LAZY half of the pair, and it is NOT the carrier read — measured. Its eager twin (`Iterable.mapNotNull`,
	# and `filterNotNullTo` at a value element, which this list used to carry) is green, so neither the declaration
	# axis nor the collection-receiver conversion is the variable. The defect is inside the stdlib's own lazy path
	# and needs no module boundary: `Sequence.mapNotNull` builds a `TransformingSequence<T, object>` — its
	# `(T) -> R?` transform erases `R?` to `object` — and hands it to `filterNotNull`, whose body is
	# `filterNot { it == null } as Sequence<T>`, an UNCHECKED cast over a lazily-WRAPPED object-elemented sequence.
	# On the CLR a `Sequence<T>` genuinely IS a reified `IEnumerable<T>`, so at T=Int the wrapper does not implement
	# `IEnumerable<int32>` and the terminal's `GetEnumerator` is not found. The eager twin is green because it
	# MATERIALIZES a fresh, correctly-typed list rather than wrapping. So the fix is an element-converting adapter
	# on the lazy path, stdlib-side — not a declaration any consumer can re-derive.
	[roundtrip-nullable-vt-generic-seq-mapnotnull]="#86: NOT the cross-module carrier read (measured) — the defect is same-module, inside the stdlib's lazy path. Sequence.mapNotNull builds TransformingSequence<T, object> (its (T) -> R? transform erases R? to object) and hands it to filterNotNull, whose body unchecked-casts a lazily-wrapped object-elemented sequence to Sequence<T>; on the CLR that IS a reified IEnumerable<T>, so at T=Int the wrapper does not implement IEnumerable<int32> and the terminal toList's GetEnumerator is not found — System.EntryPointNotFoundException. The eager Iterable.mapNotNull twin is green because it materializes a fresh typed list instead of wrapping. Needs an element-converting adapter on the lazy sequence path, stdlib-side."
	# PRUNED by the OVERRIDE-SLOT BRIDGE (#86 D3): the narrowed override called through its OWN declared type. The
	# override now keeps its own physical `accept(Nullable<int32>)`, so the re-imported surface and the assembly name
	# the same member, and the base's erased `accept(object)` slot is filled by a private forwarding bridge. Both
	# entry points are live: the section above reaches it through the INTERFACE and this one through its own type.
	#
	# PRUNED by CARRIER-ARGUMENT ERASURE (#86): the array-to-collection and collection-to-array sections, which were
	# the two observables of the POSITION split `Array<X?>` = `object[]` left open. `X?` for a possibly-value `X` is
	# now `object` at EVERY reified-argument position, so `List<Int?>` is an `IReadOnlyList<object>` and an
	# `Array<T>` extension instantiated at `object` hands its result to a slot that names the same type. Both
	# directions run green and the controls beside them are unchanged.
	# The bridge's SCOPE, measured rather than left as prose (#86 D3). The supertype graph the bridge walks is the
	# CURRENT compilation's, so a class whose base interface or base class is declared in a REFERENCED assembly gets
	# no bridge at all and the base's erased slot goes unfilled. It is the same cross-module reader gap that keeps
	# every other referenced-declaration derivation out, and it predates the bridge — the erase-in-place design this
	# replaced had it too — but nothing measured it, so nothing stopped the rule being stated unconditionally.
	# Closing it needs the base slot's pre-erasure declaration read off the referenced assembly through
	# ReferenceMetadataIndex, which is the same reader D1 built for the argument axis.
	[roundtrip-nullable-vt-generic-override-crossmodule-base]="#86 D3: an override whose base interface is declared in a REFERENCED assembly gets no bridge — KotlinOverrideSlotBridge walks the CURRENT compilation's supertype graph, so the base's erased accept(object) slot goes unfilled and the type fails to LOAD (System.TypeLoadException: Method 'accept' in type 'XIntSink' does not have an implementation). Pre-existing: the erase-in-place design this replaced had the same gap, and its same-module twin above is green. Needs the base slot's pre-erasure declaration read off the referenced assembly (ReferenceMetadataIndex), the same reader D1 built for the argument axis."
	#
	# PRUNED by the `Array<X?>`-is-`object[]` canonicalisation (#86 D2): both cross-module `Array<Int?>` sections,
	# param and return. `Array<X?>` is now `object[]` at every position for a possibly-value `X`, the pre-erasure
	# `Array<Int?>` rides the same `[KotlinNullableGeneric]` carrier every other erased slot does, and the reader
	# serves it — so the consumer's own `Array<Int?>` compiles against the re-imported slot and the null element
	# survives the boundary. Their `Array<String?>` control stays as the control it was: a reference element keeps
	# its `string[]`, which is the half of D2 that did NOT move.
)

# The documented failure SHAPE of each RT_XFAIL entry: a substring the section's EVIDENCE (every compiler /
# emitter diagnostic plus the app's stderr and exit status) must contain for the entry to absorb the failure.
# Without it an XFAIL is keyed on a section NAME alone, and any other cause — a missing assembly, an unrelated
# compiler break, a different exception — silently satisfies it. An entry that fails for a shape not listed
# here reddens as an XFAIL SHAPE MISMATCH instead. Every entry above carries one; a listed name with no shape
# would be a name-only XFAIL again.
declare -A RT_XFAIL_SHAPE=(
	[roundtrip-nullable-vt-generic-seq-mapnotnull]='System.EntryPointNotFoundException'
	[roundtrip-nullable-vt-generic-override-crossmodule-base]='does not have an implementation'
)

# A listed name with no documented shape is the hole this map exists to close, so it is rejected here rather
# than discovered later as a silently-absorbed failure.
for _n in "${!RT_XFAIL[@]}"; do
	[[ -n "${RT_XFAIL_SHAPE[$_n]:-}" ]] || die "RT_XFAIL[$_n] has no RT_XFAIL_SHAPE — a name-only XFAIL cannot verify its own failure mode"
done
unset _n

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
#   BATCH 3 (2):
#     roundtrip-toplevel-val -> toplevelValVar  (#195: reference KLIB surfaces a field-backed top-level val/var)
#     roundtrip-nothing -> crossModuleNothingBranchMerge  (#197: the Nothing value merge is well-typed IL, so the
#                                                          case no longer needs a lane that skips ilverify)
# The remaining sections below stay in this shell lane pending later increments (suspend/coharness, negative
# compile-fail and dual-emit-path cases).
# generic-hof and receiver-lambda are now formally clean after low-arity delegate ABI unification; they remain here
# only because their migration to the in-process ProjectReference lane has not been done yet.
# roundtrip-comparable remains a direct reference-KLIB projection check; its broader ProjectReference twin also
# lives in the in-process lane.

# ---- section EVIDENCE: what an XFAIL's documented failure SHAPE is matched against ------------------
# A section verdict is an stdout comparison, so on its own it cannot tell WHY a section failed — a missing
# assembly, an unrelated compiler break, a different exception and a plain wrong value all produce the same
# "not equal". That makes a name-only XFAIL a hole: it stays green while the documented cause is replaced by
# some other one. So every compiler / emitter diagnostic and the app's stderr + exit status are accumulated
# here, and RT_XFAIL_SHAPE (below) pins a substring each listed entry's evidence must contain. This is the
# discipline tests/compile-fail/run.sh and tests/run-ilverify.sh already apply to their own baselines.
RT_EVIDENCE=""
evidence_reset() { RT_EVIDENCE=""; }
evidence_add() { [[ -n "${1:-}" ]] && RT_EVIDENCE+="$1"$'\n'; return 0; }

# ---- section result collection (no section may abort the script) -----------------------------------
declare -a SUMMARY=() NEW_FAILS=()
# section_result <name> <ok 0|1> <pass-descr> [fail-detail]
# PASS / FAIL(+detail, reddens) / XFAIL(reason, green) / FIXED(xfail now passing, green).
# A listed entry that fails for a shape its RT_XFAIL_SHAPE does not describe is NOT absorbed: it reddens as
# an XFAIL SHAPE MISMATCH, because the baseline's claim about that section has stopped being true.
section_result() {
	local name="$1" ok="$2" descr="$3" detail="${4:-}" line shape
	if (( ok )); then
		if [[ -v RT_XFAIL[$name] ]]; then
			line="FIXED $name — fixed; remove it from the RT_XFAIL baseline"
		else
			line="PASS  $name ($descr)"
		fi
	elif [[ -v RT_XFAIL[$name] ]]; then
		shape="${RT_XFAIL_SHAPE[$name]:-}"
		if [[ -n "$shape" && "$RT_EVIDENCE" != *"$shape"* ]]; then
			line="FAIL  $name — XFAIL SHAPE MISMATCH: the documented failure no longer describes this section"
			detail="$(printf -- '--- documented shape (RT_XFAIL_SHAPE) ---\n%s\n--- observed evidence ---\n%s\n--- stdout diff ---\n%s' \
				"$shape" "$RT_EVIDENCE" "$detail")"
			NEW_FAILS+=("$name")
		else
			line="XFAIL $name (${RT_XFAIL[$name]})"
		fi
	else
		line="FAIL  $name"
		detail="$(printf -- '%s\n--- evidence ---\n%s' "$detail" "$RT_EVIDENCE")"
		NEW_FAILS+=("$name")
	fi
	echo "$line"
	if [[ "$line" == FAIL* && -n "$detail" ]]; then printf '%s\n' "$detail"; fi
	SUMMARY+=("$line")
	# A verdict closes a section, so it is also the evidence boundary: whatever accumulates next belongs to
	# the next section. Resetting HERE rather than at each section's top means no section can forget to.
	evidence_reset
}
# check_output <name> <expected> <actual> <pass-descr> — the common expected==actual section verdict.
# The ACTUAL output joins the evidence: a section can fail by producing a silently WRONG VALUE, with no
# diagnostic and no exception anywhere, and then the observed value is the only thing a documented shape can
# be matched against.
check_output() {
	local ok=0
	if [[ "$3" == "$2" ]]; then ok=1; fi
	evidence_add "(stdout) $3"
	section_result "$1" "$ok" "$4" "$(printf -- '--- expected ---\n%s\n--- actual ---\n%s' "$2" "$3")"
}
# run_app <outvar> <dll> — capture stdout of a possibly-crashing app. The suspend-stub abort exits 134
# (SIGABRT) INSIDE the command substitution; naked `x="$(...)"` would kill the whole gate under set -e,
# so the assignment runs as an `if` condition (errexit-exempt) and the crash is folded into the output.
# stderr is NOT discarded: it is the only place the exception type appears, and that type is what an
# RT_XFAIL_SHAPE matches. The section VERDICT is still stdout-only, so nothing about it changes here.
run_app() {
	local -n _out="$1"
	local err rc=0
	err="$(mktemp)"
	if _out="$(dotnet "$2" 2>"$err")"; then :; else
		rc=$?
		_out+="${_out:+$'\n'}(app crashed: exit $rc)"
	fi
	evidence_add "$(cat "$err")"
	evidence_add "(app exit $rc)"
	rm -f "$err"
}

# compile_kt <srcdir> <bir-outdir> <classpath> — kotc, with its diagnostics kept as section evidence. A
# consumer that fails to COMPILE emits no assembly, and the section then fails on empty stdout; without the
# diagnostic there is nothing to tell that apart from a crash, so an XFAIL could not name either.
compile_kt() {
	evidence_add "$("$LAUNCHER" "$1" -no-stdlib -classpath "$3" -d "$2" 2>&1 || true)"
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
# (DotKt.Stdlib's kotlin.coroutines.Continuation) in its emitted CPS signatures, so retarget/dll2klib must be
# able to LOAD it to walk KLib's type surface (else dll2klib skips every seed type -> empty meta -> the
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
# runtime (`kotlin.coroutines.Continuation`, synthesized in a suspend fun's CPS signature by bir2cir's suspend
# lowering); and the rt dll is dropped beside the emitted assembly so the run resolves it (mirrors verify-tests).
emit_il() {
	local out="$1" asm="$2"; shift 2
	local refs=() birs=() usrrefs=()
	while (( $# )); do
		# A user `--ref X` (a retargeted DotKt library) goes to ilemit AND — A2 (#61) — to bir2cir, which RESOLVES
		# the reference-KLIB-projected owner FQN against it to bind the .NET call SHAPE (clrStatic/clrInstance/…). Mirrors
		# The compiler-test emit path uses the RUNTIME stdlib only for ilemit (bir2cir reads the REFERENCE stdlib).
		if [[ "$1" == --ref ]]; then refs+=("$2"); usrrefs+=("$2"); shift 2; else birs+=("$1"); shift; fi
	done
	[[ -f "$STDLIB_RT_DLL" ]] && refs+=("$STDLIB_RT_DLL")
	local cir="$out.cir"; rm -rf "$cir"; mkdir -p "$cir"
	# bir2cir reads the REFERENCE stdlib ONLY (DotKt.Private.Stdlib). A consumed cross-module DotKt library references
	# the RUNTIME stdlib (DotKt.Stdlib) in its `[kotlin.clr.*]` round-trip metadata, but bir2cir's ManagedReferenceCatalog
	# ALIASES that reference to the ref twin (same type shapes) — so the runtime stdlib is NOT on --compile-refs here.
	local compile_refs; compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$(refset_join "${usrrefs[@]}")")"
	# Both stages tolerate failure, but their diagnostics are KEPT as section evidence: an emit that aborts
	# (`ilemit: … cannot resolve .NET type X`) produces no assembly, and the section then fails on empty
	# stdout — indistinguishable from a crash unless the abort message survives.
	evidence_add "$(dotnet "$BIR2CIR_DLL" "$cir" --compile-refs "$compile_refs" "${birs[@]}" 2>&1 || true)"
	evidence_add "$(dotnet "$ILEMIT_DLL" "$out" "$asm" --runtime-refs "$(refset_join "${refs[@]}")" "$cir"/*.cir.json 2>&1 || true)"
	[[ -f "$STDLIB_RT_DLL" ]] && cp "$STDLIB_RT_DLL" "$out/" 2>/dev/null || true
}

# write_coharness <appDir> — drop the coroutine TEST HARNESS (dotkt.support.blockOn) beside a suspend-consuming
# app so it co-compiles. `blockOn` was DROPPED from kotlin.clr (docs/design-coroutine-cold-core-task-bridge.md §13);
# it is a kotlinx/Track-2 primitive, re-implemented HERE in pure Kotlin over the PUBLIC stdlib primitives
# (startCoroutine/Continuation) + System.Threading.Monitor (dll2klib-seeded), with ZERO compiler special-casing.
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
# A `fun interface`, sealed class/interface, and enum carry the Kotlin declaration flags needed by a consumer.
# dll2klib writes those facts into standard reference-KLIB metadata.
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
# their members. REGRESSION TARGET for #73: a real MSBuild consumer puts BOTH stdlib twins on dll2klib's compile set
# — the REFERENCE twin `DotKt.Private.Stdlib` (what a ref-reader reads) AND the RUNTIME twin `DotKt.Stdlib` (which the
# consumed lib was emitted against, copy-local). So THIS section's dll2klib call passes BOTH twins (unlike the other
# sections, which pass only the runtime twin). Pre-fix (#35/#37): every `kotlin.*` type resolved to TWO defining
# assemblies -> dll2klib's use-site duplicate-definition check threw -> EmitOneType skipped each atomic type -> the
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

# ----- SUSPEND `Nothing` return round-trip (#135/#151): `suspend fun f(): Nothing` -----
# The dll2klib READER reads [KotlinNothing] before the Task unwrap; bir2cir's SuspendColdLowering.BuildBridge stamps
# retNothing on the Task<Nothing> bridge return (#151), so RoundtripMetadata emits [KotlinNothing] and dll2klib
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
# 2. dll2klib records the attributes in the reference KLIB.
project_reference_klib "$R/libil/KLib.dll" "$R/KLib.klib"
write_coharness "$R/app"
# 3. compile the consumer with the generated reference KLIB.
"$LAUNCHER" "$R/app" -no-stdlib -classpath "$CP$KLIB_CP_SEP$R/KLib.klib" -d "$R/appbir" >/dev/null 2>&1 || true
emit_il "$R/appil" KApp --ref "$R/libil/KLib.dll" "$R/appbir"/*.bir.json
cp "$R/libil/KLib.dll" "$R/appil/" 2>/dev/null || true

expected="$(printf '11\n(4, 6)\nHi, Vec\n42\n(3, 6)')"
run_app actual "$R/appil/KApp.dll"
check_output roundtrip "$expected" "$actual" "infix / operator / suspend / top-level restored from a DotKt assembly"

# ----- GENERIC round-trip, COMBINED with every other round-tripping feature, consumed as Kotlin -----
# Exercises user generics in every POSITION (class type param, member, return, parameter, two type params, generic
# method on a generic class) AND combined with each restored modifier (operator, infix, extension, extension operator,
# top-level suspend, nullable, default arg, vararg). Guards the complete KLIB generic declaration surface:
#   - dll2klib preserves root-package generic names and every generic type appearing in a signature.
#   - ilemit: a generic type was named `Box` without the CLR `Box`1` arity (cross-assembly `GetType` missed it); a
#     generic EXTENSION call omitted the `__self` receiver shape; a generic fn with a DEFAULT arg had fewer shapes than
#     the single .NET method's params (now tolerated + default-filled).
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
# A top-level `T?` PARAMETER kept as bare `T` so it can survive the dll2klib round-trip is unsound at VALUE-TYPE
# instantiations (a bare struct T cannot hold null). Every existing gate exercises this family only at T=String —
# roundtrip-generic drives `orDefault<String>` (a reference type, where bare-T is trivially sound) and the MSBuild
# nullable-generic sample consumes `holderOf<String>` — so a regression in bare-T handling at a value type would be
# INVISIBLE. This section closes the cross-module axis; the SAME-MODULE axis is the roundtrip-nullable-vt-generic-local-*
# sections below, which measure that the fault does not need a module boundary at all.
# A lib declares a nullable-value-type generic METHOD param (`firstOr<T>(x: T?, d: T)`) and CTOR param
# (`NBox<T>(value: T?)`),
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

# ===== #86 nullable-generic VALUE axis ===============================================================
# One app per OBSERVABLE. A section's verdict is a single stdout comparison, so an app driving several faulty
# shapes reports one result and the FIRST fault hides the rest — a `main` that dies of InvalidProgramException
# never reaches the TypeLoadException line. Bundling would therefore let one entry keep claiming three causes
# long after two were fixed. Each section below has exactly one thing that can fail, and each RT_XFAIL entry
# pins the shape (exception type / diagnostic) its evidence must contain.
#
# The GREEN control sections are load-bearing, not decoration: they run the identical shape at a REFERENCE
# instantiation (and, for the override axis, at a NON-nullable slot) through the identical pipeline. They are
# what makes "the value axis is the subject" a measurement rather than a claim — if the whole family broke,
# they would redden as NEW-FAILs rather than being absorbed by a sibling's XFAIL.

# ng_local <name> <expected-stdout> <descr>   (Kotlin source on stdin)
# A SAME-MODULE case: one compilation, no library, no metadata round trip — the control that separates a
# CARRIER defect (a shape lost across the module boundary) from a REPRESENTATION defect (a shape that never
# worked). Without it a reader of the cross-module sections cannot tell the two apart.
ng_local() {
	local name="$1" expected="$2" descr="$3"
	local d="$ROOT/build/$name"; rm -rf "$d"; mkdir -p "$d/app" "$d/bir" "$d/il"
	cat > "$d/app/app.kt"
	compile_kt "$d/app" "$d/bir" "$CP"
	emit_il "$d/il" NgApp "$d/bir"/*.bir.json
	local out; run_app out "$d/il/NgApp.dll"
	check_output "$name" "$expected" "$out" "$descr"
}

# ng_lib <workdir> <asm>   (Kotlin library source on stdin)
# Builds one section-GROUP's library: compile, emit, retarget, project to a reference KLIB. Sections in a
# group share it deliberately — the library is not the subject, one consumer's use of one slot is.
ng_lib() {
	local d="$1" asm="$2"; rm -rf "$d"; mkdir -p "$d/lib" "$d/libbir" "$d/libil"
	cat > "$d/lib/lib.kt"
	compile_kt "$d/lib" "$d/libbir" "$CP"
	emit_il "$d/libil" "$asm" "$d/libbir"/*.bir.json
	dotnet "$RETARGET_DLL" "$d/libil/$asm.dll" --compile-refs "$REFS" >/dev/null 2>&1 || true
	project_reference_klib "$d/libil/$asm.dll" "$d/$asm.klib"
}

# ng_app <workdir> <libasm> <name> <expected-stdout> <descr>   (Kotlin consumer source on stdin)
# One CONSUMER per observable against a group's library. A consumer that fails to COMPILE, or whose emit
# aborts, produces no assembly and the section fails on empty stdout — which is exactly why the diagnostics
# are kept as evidence: the RT_XFAIL_SHAPE is matched against them, not against the empty output.
ng_app() {
	local d="$1" asm="$2" name="$3" expected="$4" descr="$5"
	local a="$d/$name"; rm -rf "$a"; mkdir -p "$a/app" "$a/bir" "$a/il"
	cat > "$a/app/app.kt"
	compile_kt "$a/app" "$a/bir" "$CP$KLIB_CP_SEP$d/$asm.klib"
	emit_il "$a/il" NgApp --ref "$d/libil/$asm.dll" "$a/bir"/*.bir.json
	cp "$d/libil/$asm.dll" "$a/il/" 2>/dev/null || true
	local out; run_app out "$a/il/NgApp.dll"
	check_output "$name" "$expected" "$out" "$descr"
}

# ----- SAME-MODULE: the representation, with no boundary and no metadata in play (#86) ------------------
ng_local roundtrip-nullable-vt-generic-local-param '7' \
	'same-module: a null through a top-level T? PARAM at T=Int (#86)' <<'EOF'
fun <T> pickOr(x: T?, d: T): T = x ?: d
fun main() {
    println(pickOr<Int>(null, 7))          // 7   null through a top-level T? param at T=Int
}
EOF

# The COST of `Array<X?>` = `object[]` (#86 D2), and the one shape it is not paid in silently. `Int?` now has TWO
# physical forms depending on POSITION — `object` as an array element, `Nullable<int32>` as an ordinary type argument —
# so a generic that carries the element from one into the other cannot produce both. Driven same-module: no boundary,
# no metadata, so this is the representation itself and not a carrier defect.
ng_local roundtrip-nullable-vt-generic-array-to-collection '3/1' \
	'same-module: an Array<Int?> element carried into a List<Int?> by a generic Array<T> extension (#86 D2)' <<'EOF'
fun toL(xs: Array<Int?>): List<Int?> = xs.toList()   // T binds to Int?: object[] receiver, List<Nullable<int32>> result
fun main() {
    val a = arrayOfNulls<Int>(3)
    a[0] = 1
    val l = toL(a)
    println("${l.size}/${l[0]}")           // 3/1
}
EOF

# The same split in the OTHER direction: a `Collection<Int?>` (whose element IS `Nullable<int32>`) handed to a generic
# whose RESULT is an `Array<T>` (whose element must be `object`). One `T`, two required answers.
ng_local roundtrip-nullable-vt-generic-collection-to-array '3/3' \
	'same-module: a List<Int?> element carried into an Array<Int?> by a generic Array<T> extension (#86 D2)' <<'EOF'
fun main() {
    val p = arrayOfNulls<Int>(2).plus(listOf<Int?>(3))   // receiver element object, Collection element Nullable<int32>
    println("${p.size}/${p[2]}")           // 3/3
}
EOF

ng_local roundtrip-nullable-vt-generic-local-ctor '9' \
	'same-module: a T? CTOR PARAM plus its backing field on a generic owner at T=Int (#86)' <<'EOF'
class Cell<T>(private val slot: T?) {   // T? ctor param + backing field, on a generic owner
    val stored: T? get() = slot
    fun orElse(d: T): T = slot ?: d
}
fun main() {
    println(Cell<Int>(null).orElse(9))     // 9   null through the T? ctor param
}
EOF

ng_local roundtrip-nullable-vt-generic-local-override 'none' \
	'same-module: an override narrowing a base T? slot to a concrete Int? (#86 D3)' <<'EOF'
interface Sink<T> { fun accept(x: T?): String }      // the base slot an override narrows
class IntSink : Sink<Int> { override fun accept(x: Int?): String = x?.toString() ?: "none" }
fun main() {
    val s: Sink<Int> = IntSink()
    println(s.accept(null))                // none  the narrowed override, reached through the base slot
}
EOF

# The reference-type control for all three shapes above: identical declarations, T instantiated with a
# reference type. It must stay GREEN — a bare `T?` slot is trivially sound there — which is what makes the
# three XFAILs above statements about the VALUE axis rather than about nullable generics in general.
ng_local roundtrip-nullable-vt-generic-local-reference "$(printf 'x\ns\nnone')" \
	'same-module control: the same three T? shapes at a REFERENCE instantiation (#86)' <<'EOF'
fun <T> pickOr(x: T?, d: T): T = x ?: d
class Cell<T>(private val slot: T?) { fun orElse(d: T): T = slot ?: d }
interface Sink<T> { fun accept(x: T?): String }
class TextSink : Sink<String> { override fun accept(x: String?): String = x ?: "none" }
fun main() {
    println(pickOr<String>(null, "x"))                 // x     top-level T? param
    println(Cell<String>(null).orElse("s"))            // s     T? ctor param + field
    println((TextSink() as Sink<String>).accept(null)) // none  override through the base slot
}
EOF

# ----- the stdlib idioms whose VALUE instantiations do not resolve (#86) -------------------------------
# Their reference-element and eager twins are green in the NUnit lane (tests/basic/fixtures:
# CollectionOperationsTests.filterNotNullTo at a String element, Iterable.mapNotNull at Int/Boolean), which
# is what makes each of these a measurement: everything around them works at a value element, so a fix that
# only keeps the green set green has not touched the gap.
ng_local roundtrip-nullable-vt-generic-filternotnullto '1,3,5' \
	'List<Int?>.filterNotNullTo at a VALUE element (#86)' <<'EOF'
fun main() {
    val vs: List<Int?> = listOf(1, null, 3, null, 5)
    val dest = mutableListOf<Int>()
    vs.filterNotNullTo(dest)
    println(dest.joinToString(","))        // 1,3,5
}
EOF

ng_local roundtrip-nullable-vt-generic-seq-mapnotnull '20,40' \
	'Sequence.mapNotNull at a VALUE element (#86)' <<'EOF'
fun main() {
    val xs = listOf(1, 2, 3, 4, 5, 6)      // List.asSequence() only — #284 covers sequenceOf/Array.asSequence
    println(xs.asSequence().mapNotNull { if (it % 2 == 0 && it < 5) it * 10 else null }
        .toList().joinToString(","))       // 20,40
}
EOF

# ----- CROSS-MODULE: a top-level `T?` RETURN (#86) ----------------------------------------------------
# The RETURN axis is a DIFFERENT defect from the param axis, and worse. The param axis keeps a bare `T` slot
# plus an NRT byte, so the consumer compiles and only faults at run time. A top-level `T?` return keeps
# nothing: it is object-erased at the declaration, the carrier recorder skips a top-level Nullable(Tv), and
# the NRT byte walk runs AFTER the erasure so it sees `object` and stamps no `2`. The slot re-imports as a
# non-null `Any` and the failure is at the consumer's COMPILE, before any IL exists — invisible to every
# runtime-shaped gate. Note the third section: unlike the param axis, this one is NOT confined to value
# types, which is why there is no green reference control here.
NR="$ROOT/build/roundtrip-nullable-vt-generic-ret-group"
ng_lib "$NR" NrLib <<'EOF'
fun <T> pick(x: T, use: Boolean): T? = if (use) x else null   // top-level T? RETURN
class Picker<T>(private val held: T) {
    fun get(use: Boolean): T? = if (use) held else null        // the same return position on a generic OWNER
}
EOF

ng_app "$NR" NrLib roundtrip-nullable-vt-generic-ret '-1' \
	'cross-module: a top-level T? RETURN bound to an Int? slot (#86)' <<'EOF'
fun main() {
    val absent: Int? = pick(5, false)      // the re-imported return must bind to an Int? slot
    println(absent ?: -1)                  // -1
}
EOF

ng_app "$NR" NrLib roundtrip-nullable-vt-generic-ret-member '-1' \
	'cross-module: a generic OWNER member T? RETURN bound to an Int? slot (#86)' <<'EOF'
fun main() {
    val member: Int? = Picker(8).get(false)
    println(member ?: -1)                  // -1
}
EOF

ng_app "$NR" NrLib roundtrip-nullable-vt-generic-ret-reference 'none' \
	'cross-module: the same T? RETURN at a REFERENCE instantiation (#86)' <<'EOF'
fun main() {
    val text: String? = pick("a", false)   // the return axis is NOT confined to value types
    println(text ?: "none")                // none
}
EOF

# ----- CROSS-MODULE: `Array<Int?>` in both directions (#86 D2) ----------------------------------------
# A nullable VALUE element array is the one position where the erasure cannot be transparent: `object[]` and
# `Nullable<int32>[]` are unrelated CLR types (array compatibility requires reference-compatible elements),
# so the two representations that coexist today cannot meet. A same-compilation fixture never notices — the
# producer and consumer of the array are lowered together and agree by construction — so only a separately
# compiled consumer forces it. The `Array<String?>` control must keep its `string[]` representation.
NA="$ROOT/build/roundtrip-nullable-vt-generic-array-group"
ng_lib "$NA" NaLib <<'EOF'
fun boxedPair(n: Int): Array<Int?> {        // Array<Int?> RETURN across the boundary
    val a = arrayOfNulls<Int>(3)
    a[0] = n
    a[2] = n * 2
    return a
}
fun sumPresent(xs: Array<Int?>): Int {      // Array<Int?> PARAM across the boundary
    var s = 0
    for (x in xs) if (x != null) s += x
    return s
}
fun joinPresent(xs: Array<String?>): String = xs.filterNotNull().joinToString(",")
EOF

ng_app "$NA" NaLib roundtrip-nullable-vt-generic-array-param '0' \
	'cross-module: an Array<Int?> PARAM built by the consumer (#86 D2)' <<'EOF'
fun main() {
    println(sumPresent(arrayOfNulls<Int>(2)))   // 0   an all-null array built by the CONSUMER
}
EOF

# The RETURN position fails DIFFERENTLY from the param position, and far worse: the consumer compiles, runs,
# and prints garbage. The element type re-imports as a NON-NULL `Int`, so the consumer indexes a
# `Nullable<int32>[]` as an `int32[]` and reads the LAYOUT WORDS as elements — for an array of 4/null/8 it
# reports `3/1/4/0`: the hasValue flag, then the value, then the null element's zeroed flag.
# Read index by index and printed as ONE line: no elvis (kotc folds it away once the element is non-null), no
# generic-array stdlib extension (`Array<Int>.joinToString` has an overload-resolution gap of its own, which
# would make this section measure a different defect), and one line so the observed value is a single
# matchable shape.
ng_app "$NA" NaLib roundtrip-nullable-vt-generic-array-ret '3/4/null/8' \
	'cross-module: an Array<Int?> RETURN read index by index (#86 D2)' <<'EOF'
fun main() {
    val a = boxedPair(4)
    println("${a.size}/${a[0]}/${a[1]}/${a[2]}")   // 3/4/null/8   the null element survives the boundary
}
EOF

ng_app "$NA" NaLib roundtrip-nullable-vt-generic-array-reference 'a,b' \
	'cross-module control: an Array<String?> PARAM keeps its string[] representation (#86 D2)' <<'EOF'
fun main() {
    println(joinPresent(arrayOf("a", null, "b")))  // a,b
}
EOF

# ----- CROSS-MODULE: an override NARROWING a base `T?` slot (#86 D3) ----------------------------------
# Erasure propagates from the OVERRIDDEN slot, not from syntax: the derived declaration holds a concrete
# `Int?`, not `Nullable(Tv)`, so a syntactic sweep cannot see it while the base slot erases to `object`. The
# two controls below are the discrimination, kept in the lane rather than as a one-off probe: the SAME
# interface at a REFERENCE instantiation, and the SAME value instantiation with a NON-nullable slot. Each
# gets its own library so the three cases differ in exactly one thing.
NO="$ROOT/build/roundtrip-nullable-vt-generic-override-group"
ng_lib "$NO" NoLib <<'EOF'
interface Sink<T> { fun accept(x: T?): String }    // the base slot: Nullable(Tv) -> object
class IntSink : Sink<Int> { override fun accept(x: Int?): String = x?.toString() ?: "none" }
EOF

ng_app "$NO" NoLib roundtrip-nullable-vt-generic-override 'none' \
	'cross-module: an override narrowing a base T? slot to a concrete Int? (#86 D3)' <<'EOF'
fun main() {
    val s: Sink<Int> = IntSink()
    println(s.accept(null))                // none  through the INTERFACE-typed receiver
}
EOF

# The same override reached through its OWN declared type rather than the base slot. It is a SEPARATE observable
# from its sibling above, and the one that says which declaration the erasure moved: the interface slot has to be
# `accept(object)` or the method goes unimplemented and the type never loads, while a consumer type-checks against
# the re-imported `accept(x: Int?)`. Both are live only when the override KEEPS its own physical signature and a
# private bridge fills the erased slot — move the declaration instead and this section resolves a member that does
# not exist, however the argument is derived.
ng_app "$NO" NoLib roundtrip-nullable-vt-generic-override-direct 'none/3' \
	'cross-module: the same narrowed override called through its OWN type, not the base slot (#86 D3)' <<'EOF'
fun main() {
    println(IntSink().accept(null) + "/" + IntSink().accept(3))   // none/3  the DECLARED type, null and a value
}
EOF

NOR="$ROOT/build/roundtrip-nullable-vt-generic-override-ref-group"
ng_lib "$NOR" NorLib <<'EOF'
interface Sink<T> { fun accept(x: T?): String }
class TextSink : Sink<String> { override fun accept(x: String?): String = x ?: "none" }
EOF

ng_app "$NOR" NorLib roundtrip-nullable-vt-generic-override-reference 'none' \
	'cross-module control: the same T? override slot at a REFERENCE instantiation (#86 D3)' <<'EOF'
fun main() {
    val s: Sink<String> = TextSink()
    println(s.accept(null))                // none
}
EOF

# The same narrowing over a base CLASS rather than an interface, and its own two entry points. A class slot is
# wired by a DIFFERENT piece of CLR metadata than an interface slot — a MethodImpl against the constructed base
# instead of against the constructed interface — so an override bridge that only covered interfaces would leave
# this one a new overload, and the abstract slot unimplemented.
NOC="$ROOT/build/roundtrip-nullable-vt-generic-override-class-group"
ng_lib "$NOC" NocLib <<'EOF'
abstract class Holder<T> { abstract fun take(x: T?): String }   // an abstract base-CLASS slot: Nullable(Tv) -> object
class IntHolder : Holder<Int>() { override fun take(x: Int?): String = x?.toString() ?: "none" }
EOF

ng_app "$NOC" NocLib roundtrip-nullable-vt-generic-override-class 'none/5' \
	'cross-module: an override narrowing a base CLASS T? slot, through the base slot and its own type (#86 D3)' <<'EOF'
fun main() {
    val h: Holder<Int> = IntHolder()
    println(h.take(null) + "/" + IntHolder().take(5))   // none/5  the base slot, then the DECLARED type
}
EOF

# The same narrowing on a `T?` RETURN, three levels deep. The return axis is where TWO bridge synthesizers can see
# the divergence — the covariant-return one and this erasure's — and only the erasure's forwards virtually, so
# `SubSrc`'s override is reachable through the erased interface slot exactly when one bridge owns it and it is that
# one. Cross-module because a consumer binds the re-imported surface rather than the emitted member it wraps.
NOS="$ROOT/build/roundtrip-nullable-vt-generic-override-ret-group"
ng_lib "$NOS" NosLib <<'EOF'
interface Src<T> { fun get(): T?; val v: T? }        // a T? RETURN slot, method and property
open class BaseSrc : Src<Int> { override fun get(): Int? = 4; override val v: Int? = 40 }
class SubSrc : BaseSrc() { override fun get(): Int? = 9; override val v: Int? get() = 90 }
EOF

ng_app "$NOS" NosLib roundtrip-nullable-vt-generic-override-ret '9/90/4/40' \
	'cross-module: a narrowed T? RETURN dispatched through the erased base slot, three levels deep (#86 D3)' <<'EOF'
fun main() {
    val s: Src<Int> = SubSrc()
    val b: Src<Int> = BaseSrc()
    println("" + s.get() + "/" + s.v + "/" + b.get() + "/" + b.v)   // 9/90/4/40
}
EOF

# The same return slot with the DERIVATION in the consumer, which is the only place the virtual-forward property is
# observable. Derived in the SAME module self-heals: it gets a bridge of its own, and the most-derived MethodImpl wins
# whichever way the library's bridge forwards. Across the boundary `SubSrc` gets NO bridge — it is the referenced-base
# gap below, and it is exactly why this shape is the discriminating one — so dispatch falls back to the LIBRARY's
# MethodImpl, and the override is only reached because that bridge forwards VIRTUALLY. Bridged non-virtually (which is
# what the covariant-return synthesizer does, and what it did to this slot before the two passes were made exclusive),
# every one of these calls answers with the library's 4.
NOV="$ROOT/build/roundtrip-nullable-vt-generic-override-virtual-group"
ng_lib "$NOV" NovLib <<'EOF'
interface Src<T> { fun get(): T? }
open class IntSrc : Src<Int> { override fun get(): Int? = 4 }
EOF

ng_app "$NOV" NovLib roundtrip-nullable-vt-generic-override-virtual-forward '9/9/9' \
	'cross-module: a consumer-side override reached through the LIBRARY bridge, which must forward virtually (#86 D3)' <<'EOF'
class SubSrc : IntSrc() { override fun get(): Int? = 9 }
fun main() {
    val s: Src<Int> = SubSrc()
    val b: IntSrc = SubSrc()
    println("" + SubSrc().get() + "/" + s.get() + "/" + b.get())   // 9/9/9
}
EOF

# ----- CROSS-MODULE: a base declared in a REFERENCED assembly (#86 D3, documented red) ------------------
# The supertype graph the bridge walks is the CURRENT compilation's, so a class whose base interface lives in a
# REFERENCED DotKt assembly gets no bridge and the erased slot goes unfilled. This is the same cross-module reader
# gap that keeps the other referenced-declaration derivations out, and it predates the bridge — but nothing measured
# it, and both the pass comment and docs/dotkt-semantics.md now scope their claim to same-module supertypes because
# of this section. The IMPLEMENTER lives in the consumer, so the interface is genuinely across the boundary from it.
NOX="$ROOT/build/roundtrip-nullable-vt-generic-override-xbase-group"
ng_lib "$NOX" NoxLib <<'EOF'
interface XSink<T> { fun accept(x: T?): String }     // the base slot, in its OWN assembly
EOF

ng_app "$NOX" NoxLib roundtrip-nullable-vt-generic-override-crossmodule-base 'none/3' \
	'cross-module: an override whose base interface lives in a REFERENCED assembly (#86 D3)' <<'EOF'
class XIntSink : XSink<Int> { override fun accept(x: Int?): String = x?.toString() ?: "none" }
fun main() {
    val s: XSink<Int> = XIntSink()
    println(s.accept(null) + "/" + XIntSink().accept(3))   // none/3
}
EOF

# ----- CROSS-MODULE: `T?` through the SUSPEND channels (#86) -------------------------------------------
# A suspend declaration's Kotlin result does not ride `ret` — it rides `suspendRet`, and the public ABI is a
# Task bridge CONSTRUCTED fresh, so it inherits nothing from the declaration it replaces. A `suspend (…) -> T?`
# VALUE is not a delegate either: it erases to `object` and its shape rides a DEDICATED suspend-function
# carrier, which is built after the erasure and so recorded the erased shape. Both re-imported as `Any` /
# `suspend () -> object` and the consumer did not type-check — a COMPILE failure, invisible to every
# runtime-shaped gate, which is why this section's assertion is that the consumer builds and runs at all.
NS="$ROOT/build/roundtrip-nullable-vt-generic-suspend-group"
ng_lib "$NS" NsLib <<'EOF'
suspend fun <T> nullableSuspend(x: T?): T? = x                     // the suspend DECLARATION return
fun <T> takesSuspendFn(f: suspend () -> T?): suspend () -> T? = f  // a suspend FUNCTION-TYPE slot
class SBox<T>(private val held: T?) {
    suspend fun get(): T? = held                                   // the same return on a generic OWNER
}
EOF

ng_app "$NS" NsLib roundtrip-nullable-vt-generic-suspend 'ok' \
	'cross-module: a T? through a suspend RETURN and a suspend FUNCTION-TYPE slot, at T=Int (#86)' <<'EOF'
fun mkFn(): suspend () -> Int? = { 5 }
// Each of these names `Int?` at a slot the library declared as `T?`, so a re-import as `Any` (the suspend
// declaration return) or `suspend () -> object` (the function-type slot) fails to compile right here — which is
// the whole failure mode: a CONSUMER compile error that no runtime-shaped gate can see.
suspend fun useRet(): Int? = nullableSuspend<Int>(null)
suspend fun useBox(): Int? = SBox(3).get()
fun main() {
    val g: suspend () -> Int? = takesSuspendFn(mkFn())
    println(if (g !== null && ::useRet !== null && ::useBox !== null) "ok" else "no")
}
EOF

NOP="$ROOT/build/roundtrip-nullable-vt-generic-override-plain-group"
ng_lib "$NOP" NopLib <<'EOF'
interface Plain<T> { fun accept(x: T): String }    // the SAME shape with a NON-nullable slot
class IntPlain : Plain<Int> { override fun accept(x: Int): String = x.toString() }
EOF

ng_app "$NOP" NopLib roundtrip-nullable-vt-generic-override-nonnull '7' \
	'cross-module control: the same VALUE instantiation with a NON-nullable override slot (#86 D3)' <<'EOF'
fun main() {
    val s: Plain<Int> = IntPlain()
    println(s.accept(7))                   // 7
}
EOF

# ----- CROSS-MODULE: a NESTED `Slot<T?>` declaration slot at a VALUE instantiation (#18/#86) ------------
# #147's carrier covers the NESTED Nullable(Tv) positions — a `Slot<T?>` param / property / member return —
# and the in-process consumer already drives them, but only at T=String. There the erased `Slot<object>` and
# the restored `Slot<string>` are reference-compatible, so the mismatch was formal-only. At T=Int the same
# mismatch is `Slot<object>` against `Slot<Nullable<int32>>`, which are unrelated invariant reified generics
# that no cast reconciles — and the split here is NOT param-vs-property-vs-return, which is what a bundled app
# made it look like. Measured per line, the axis was PRESENT-vs-NULL: carrying a null through any of the three
# positions always worked, while carrying a VALUE corrupted memory in `CastHelpers.Unbox_Nullable` (at
# T=Boolean the same shape surfaced one step earlier as a NullReferenceException).
#
# All three are GREEN now: bir2cir reads `[KotlinNullableGeneric]` off the REFERENCED library and types the
# consumer's use as `Subst(Erase(declared))`, so the construction is BUILT as `Slot<object>` instead of being
# built wrongly and then not convertible. They stay here rather than moving to the in-process lane because that
# is the lane that cannot host them: an AccessViolationException takes the test host down before any assertion
# runs, so a regression would report zero tests instead of one failure.
NG="$ROOT/build/roundtrip-nullable-generic-slot-group"
ng_lib "$NG" NgSlotLib <<'EOF'
class Slot<T>(val value: T)
class Vault<T>(private val fill: T) {
    fun cell(): Slot<T?> = Slot(null)            // member return whose type arg is the OWNER's Nullable(Tv)
}
class SlotHolder<T>(private val initial: Slot<T?>) {
    val slot: Slot<T?> get() = initial           // property whose type arg is Nullable(Tv)
}
fun <T> vaultOf(fill: T): Vault<T> = Vault(fill)
fun <T> unwrapSlot(slot: Slot<T?>): T? = slot.value   // PARAM whose type arg is Nullable(Tv)
EOF

ng_app "$NG" NgSlotLib roundtrip-nullable-generic-slot-param-value '5' \
	'cross-module: a VALUE carried through a nested Slot<T?> PARAM at T=Int (#18/#86)' <<'EOF'
fun main() {
    println(unwrapSlot(Slot<Int?>(5)) ?: -1)          // 5   a value through a Slot<T?> param
}
EOF

ng_app "$NG" NgSlotLib roundtrip-nullable-generic-slot-property-value '7' \
	'cross-module: a VALUE read back from a nested Slot<T?> PROPERTY at T=Int (#18/#86)' <<'EOF'
fun main() {
    println(SlotHolder(Slot<Int?>(7)).slot.value ?: -1)   // 7
}
EOF

ng_app "$NG" NgSlotLib roundtrip-nullable-generic-slot-param-bool 'False' \
	'cross-module: a VALUE carried through a nested Slot<T?> PARAM at T=Boolean (#18/#86)' <<'EOF'
fun main() {
    println(unwrapSlot(Slot<Boolean?>(false)) ?: true)    // False
}
EOF

# The NULL half of the same three positions, and the reference instantiation. All GREEN today, and they are
# the reason the entries above can claim the PRESENT axis specifically: if the whole nested-carrier family
# broke, these would redden as NEW-FAILs rather than being absorbed by a sibling's XFAIL.
ng_app "$NG" NgSlotLib roundtrip-nullable-generic-slot-null "$(printf -- '-1\n-1\n-1\nTrue')" \
	'cross-module control: a NULL through the nested Slot<T?> param / property / member return at T=Int (#18/#86)' <<'EOF'
fun main() {
    println(unwrapSlot(Slot<Int?>(null)) ?: -1)           // -1  PARAM
    println(SlotHolder(Slot<Int?>(null)).slot.value ?: -1)  // -1  PROPERTY
    println(vaultOf(2).cell().value ?: -1)                // -1  member RETURN (owner Nullable(Tv))
    println(unwrapSlot(Slot<Boolean?>(null)) ?: true)     // True
}
EOF

ng_app "$NG" NgSlotLib roundtrip-nullable-generic-slot-reference 'param' \
	'cross-module control: the same nested Slot<T?> PARAM at a REFERENCE instantiation (#18/#86)' <<'EOF'
fun main() {
    println(unwrapSlot(Slot<String?>("param")))       // param
}
EOF

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
# bare `[KotlinExtensionFunctionType]` on the delegate param; dll2klib moves the delegate's first arg back into the
# fn receiver; KLIB deserialization restores `Panel.() -> Unit` (an ExtensionFunctionType cone) so the consumer's lambda
# gets `this: Panel`. Without the metadata the projected param degrades to a receiver-less `(Panel)->Unit` and the
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
# Member extension property (`class C { val T.p }`): represented in KLIB metadata as a member property with an
# extension receiver; read/write inside `with(c)` routes
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
# ilemit stamps it dumbly; dll2klib reads the `fn` node back and KLIB deserialization restores `block` as a suspend
# function type (`kotlin.coroutines.SuspendFunction0<Int>`). PROOF that suspend survives: the
# consumer's `runBlock { addAsync(...) }` lambda BODY calls `addAsync` (itself a suspend fun) — which only compiles
# if `block` is a suspend function type (else "suspend function called from non-suspend context"), and only runs if
# the suspend lambda is driven as a state machine. (A suspend fn-type in a RETURN/property/field position is wired in
# dll2klib too, but blocked E2E on a separate suspend-lambda-VALUE emit limitation — `expr suspendLambdaNew`.)
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
#     `suspend () -> Int` return type comes back via dll2klib's structured meta — a `fn` node with suspend:true) and DRIVES it.
#   - PROPERTY + FIELD positions are proven by the LIB storing the suspend lambda in a top-level `val` and an
#     instance `val`, then DRIVING each via `runBlock` inside restorable functions `runProp()`/`runField()`
#     the app invokes. (kotc emits a top-level `val` as a plain static FIELD, which dll2klib does not restore
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
# A projected external owner must preserve virtual dispatch. This section guards both the normal clrInstance
# binding and the raw callInstance fallback when bir2cir is deliberately not given the DotKt reference.
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
#                                    #47 combined suspend+extension cone) is separately asserted by grepping the dll2klib
#                                    meta for ext's `fn` node carrying `recv`. (ext is NOT driven at runtime: driving a
#                                    suspend EXTENSION lambda VALUE via the receiver-form startCoroutine hits a pre-existing
#                                    bir2cir coroutine-lowering gap — reproducible SAME-module, unrelated to this
#                                    symbol-surface fix — so the restored TYPE is asserted by compile-dependency + meta.)
# bir2cir: RoundtripMetadata StampProps emits [Nullable] (from DeclNullableFlags) + [KotlinSuspendFunctionType];
# kotc BirEmitterTypes keeps recv on a suspend ext fn; dll2klib PropTypeN reads the suspend carrier + ApplyNrt the
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
# kotc flattens an interface companion to the interface's static fields and methods. dll2klib must project those
# statics as companion members so `Greeter.Companion.DEFAULT` and `Greeter.Companion.greet` resolve cross-module.
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
# A Kotlin `class C : Comparable<C>` lowers to the CLR `System.IComparable<C>.CompareTo` slot. The reference KLIB
# must restore lowercase operator `compareTo` and the `kotlin.Comparable<C>` supertype so comparison operators and
# `sorted()` resolve cross-module.
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

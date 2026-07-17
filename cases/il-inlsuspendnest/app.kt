// #75 Batch B — the REAL kotlinx.coroutines flow shape the prior 2A fix MISSED: a §4.4ii-materialized SUSPEND
// carrier whose body itself NESTS a `newSuspendLambda`, under a NON-IDENTITY (multi-scope) enclosing tv remap.
// This is the `unsafeFlow { … collectInner { … } }` / combine / zip family (Zip/Combine/FlowCoroutine). The former
// F3 guard `remapShiftsIndex && HasNode(newSuspendLambda)` fail-loud'd on EXACTLY this — ANY carrier nesting a suspend
// lambda under a shifting remap — so the whole flow subsystem could not compile even though the pieces were sound.
// The fix SHIELDS a nested `newSuspendLambda`'s own tv frame (body/params/suspendRet/typeParams) from the outer
// carrier's CollectTvKeys/RenumberTvs — exactly like a `synthClass` — so the nested SM's positional tv resolution stays
// intact while the outer SM binds the enclosing tvs from the nested captures' types. Two narrow invariant guards replace
// the blanket refusal (nested-sm-nonprefix / nested-sm-bare-tv-capture-shift) to keep a genuinely-unsound future shape loud.
//
// This case pins the bir2cir shield: (a) `block` materializes into a real cold SM value stored in an `object : Src<R>`
// (non-invoke); (b) its body passes a NESTED suspend lambda `{ x }` to a non-inline suspend fn — that nested lambda
// becomes a `newSuspendLambda` INSIDE the materialized carrier, capturing the enclosing R-typed local `x`/`y` (tv{method,0})
// and returning R. The carrier's key set is the MULTI-scope {(method,0),(type,0)} (the `emit` member-sig carries the
// receiver's tv{type,0}), so `remapShiftsIndex` is true and a nested SM is present — the precise pair the old guard rejected.
// (The nested lambda captures a plain enclosing LOCAL, not the block receiver, to isolate the bir2cir shield from the
// separate kotc nested-receiver-capture naming seam.) Exercised top-level, from a generic METHOD, and a generic CLASS
// member; each drives end-to-end via blockOn and the summed value MUST be correct.
import dotkt.support.blockOn

interface Sink<T> { suspend fun emit(value: T) }
interface Src<T> { suspend fun drain(s: Sink<T>) }

class ListSink<T> : Sink<T> {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

// a non-inline suspend fn taking a suspend lambda — the lambda literal at the call site becomes a NESTED
// `newSuspendLambda` INSIDE the materialized carrier body (capturing an enclosing local, returning R).
suspend fun <T> produceOne(block: suspend () -> T): T = block()

// THE materialized suspend carrier: `block` captured into an `object : Src<T>` (non-invoke) -> §4.4ii MaterializeSuspendCarrier.
inline fun <T> mkFlow(crossinline block: suspend Sink<T>.() -> Unit): Src<T> = object : Src<T> {
    override suspend fun drain(s: Sink<T>) { s.block() }
}

// (b) generic METHOD: R (tv{method,0}) survives as a method-scope free var captured into the nested suspend lambda.
// (A generic-CLASS member whose nested lambda captures a BARE type-scope tv shifts to a non-0 dense slot and is a genuine
// nested-SM reified-type miscompile — refused LOUD by MSC:nested-sm-bare-tv-capture-shift; none of the rc6 flow carriers
// hit it. Its clean support is the #74/#46 recursive resolved-identity follow-up, out of scope for this blocker.)
fun <R> makeSrc(x: R, y: R): Src<R> = mkFlow<R> {
    emit(produceOne { x })
    emit(produceOne { y })
}

suspend fun runSrc(src: Src<Int>): Int {
    val sink = ListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

fun main() {
    val c = 20; val dd = 22
    val s1 = mkFlow<Int> { emit(produceOne { c }); emit(produceOne { dd }) }
    println(blockOn { runSrc(s1) })       // 42
    println(blockOn { runSrc(makeSrc(30, 12)) })   // 42
}

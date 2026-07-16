// BATCH B (#75 holistic) — the FORMER SILENT-MISCOMPILE cell. A `collectWhile`-shaped inline fn: a `crossinline`
// SUSPEND lambda captured by an `object :` literal inside the inline body. kotc lifts the `object :` to a local
// class whose ctor takes the captured suspend lambda as a value arg (the capture rides OUTSIDE the typeDef, in the
// `new` args) and whose suspend member invokes it as a suspend VALUE. When `makeAndDrive` is spliced the captured
// crossinline `predicate` survives in a non-invoke position (the `new`'s ctor arg); before Batch B MaterializeCarrier
// minted a PLAIN newClosure for that `{t:fn,suspend:true}` carrier — a plain delegate where the SM /
// startSuspendUninterceptedOrReturn protocol expects a suspend lambda -> SILENT MISCOMPILE. Now the suspend arm mints
// a real newSuspendLambda VALUE, so the object's suspend `accept` drives it correctly. The predicate genuinely
// suspends (it calls the real suspend fn `isBelow`), and the value MUST be correct (true/false).
import dotkt.support.blockOn

interface SuspendSink {
    suspend fun accept(value: Int): Boolean
}

suspend fun isBelow(v: Int, limit: Int): Boolean = v < limit

// non-inline driver: invokes the sink's SUSPEND member.
suspend fun driveSink(sink: SuspendSink, v: Int): Boolean = sink.accept(v)

// inline fn: builds an `object :` capturing the crossinline SUSPEND `predicate`, drives via a non-inline suspend fn.
inline fun makeAndDrive(v: Int, crossinline predicate: suspend (Int) -> Boolean): Boolean {
    val sink = object : SuspendSink {
        override suspend fun accept(value: Int): Boolean = predicate(value)
    }
    return blockOn { driveSink(sink, v) }
}

fun main() {
    println(makeAndDrive(41) { isBelow(it, 42) })   // true
    println(makeAndDrive(42) { isBelow(it, 42) })   // false
    println(makeAndDrive(0) { isBelow(it, 42) })    // true
}

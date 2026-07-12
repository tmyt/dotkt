// #123: constructing an EXTERNAL generic instantiated over a FREE type variable, as a ctor arg to a
// same-assembly generic. `new AtomicReference<T>(v)` (T = the enclosing fn's method type-var) is a
// TypeBuilderInstantiation, so ilemit must resolve its ctor on the open definition and re-anchor via
// TypeBuilder.GetConstructor (mirroring EmitClrNew) instead of calling `.GetConstructors()` on it, which
// throws "TypeBuilder generic instantiation does not support resolving members". Regression guard for the
// kotlinx.coroutines CLR port's atomic() helper (which had to use a construct-at-concrete + cast workaround).
@file:OptIn(ExperimentalAtomicApi::class)

import kotlin.concurrent.atomics.AtomicReference
import kotlin.concurrent.atomics.ExperimentalAtomicApi

class AtomicRef<T>(val a: AtomicReference<T>)

fun <T> atomic(v: T): AtomicRef<T> = AtomicRef(AtomicReference(v))

// A DIRECT free-T external new (no same-assembly wrapper) — exercises the external branch alone.
fun <T> boxed(v: T): AtomicReference<T> = AtomicReference(v)

fun main() {
    println(atomic(5).a.load())            // 5   (value-type free T through the wrapper)
    println(atomic("hi").a.load())         // hi  (ref-type free T through the wrapper)
    println(boxed(42).load())              // 42  (bare external new over free T)
    println(boxed("yo").load())            // yo
}

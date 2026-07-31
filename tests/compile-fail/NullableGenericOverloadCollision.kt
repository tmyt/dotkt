// #86 §5.3 — the overload collision the object-erasure CREATES.
//
// `T?` over an unconstrained type parameter is emitted as `System.Object`, and `Any?` reaches the same CLR type by
// the ordinary reference-nullable strip. So these two members — which the Kotlin frontend accepts as distinct, and
// between which Kotlin's own resolution picks `f(T?)` for `c.f(3)` at `Coll<Int>` — become ONE slot: whichever the
// emitter binds wins every call and the other is unreachable.
//
// That is the one outcome a program with no valid CIL lowering must not get: it used to compile, run, and silently
// take the wrong branch (`c.f(3)` printed "any"). The refusal names both source signatures instead.
//
// Note what does NOT collide, and must keep compiling: generic ARITY is part of the CLI signature (ECMA-335
// I.8.6.1.6), so a `fun <T> g(x: T?)` beside a non-generic `fun g(x: Any?)` is two slots, not one.
class Coll<T> {
    fun f(x: T?): String = "tv"
    fun f(x: Any?): String = "any"
}

fun main() {
    val c = Coll<Int>()
    println(c.f(3))
    println(c.f("s"))
}

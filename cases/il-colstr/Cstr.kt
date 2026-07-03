// A collection/Map operand prints Kotlin-style (`[a, b]` / `{a=1, b=2}`), not the raw .NET
// `System.Collections.Generic.Dictionary`2[...]` / `List`1[...]` type name, in EVERY stringify context —
// not just `println(x)`. kotc routes a `List`/`Set`/`Collection`/`Map`-typed operand to the stdlib
// clrCollToString/clrMapToString helper at the STATIC type level (a runtime `is Map<*,*>` is unreliable
// for @ClrTypeAlias-lowered BCL collections), now shared across the string-template, explicit-`toString()`,
// and string-`plus`-concat paths (bundle-6 FIX 1).
fun main() {
    val m = mapOf("a" to 1, "b" to 2)
    val l = listOf(1, 2, 3)
    println("m=$m")            // string template, Map          -> m={a=1, b=2}
    println("l=$l")            // string template, List         -> l=[1, 2, 3]
    println("x=" + m)          // string `+` concat, Map        -> x={a=1, b=2}
    println("" + l)            // string `+` concat, List       -> [1, 2, 3]
    println(l.toString())      // explicit toString(), List     -> [1, 2, 3]
    println(m.toString())      // explicit toString(), Map      -> {a=1, b=2}
    // NOTE: a Set (setOf -> concrete HashSet) routes the same way at runtime (prints `[7, 8]`), but is omitted
    // here — the HashSet<T> -> Set<T> interface-arg widening trips ilverify (ilemit doesn't emit the widening
    // cast; a pre-existing formal-only gap shared by `println(setOf(...))`, orthogonal to this fix).
}

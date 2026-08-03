// #220 — the CANONICAL wide function-type ABI, producer side. Kotlin arities 17..22 exceed System.Func/Action's
// 16-parameter ceiling and lower to DotKt.Runtime.CompilerServices.KAction`17..22 / KFunc`18..23, which are defined
// ONCE, in the stdlib. That is what makes them legal in a PUBLIC signature: a per-assembly definition would be a
// distinct nominal type on each side of the module boundary.
//
// Every position a delegate can occupy is declared here, because they fail differently: a PARAMETER used to be
// papered over by a call-site rewrap, a RETURN produced an unverifiable callvirt through the consumer's own copy,
// and one NESTED IN A GENERIC (`List<(...)->R>`) aborted ilemit outright. Both edges of the range (17 and 22) are
// declared, and both in ONE assembly — the arity-clash re-import path only bites when a single producer carries
// two arities of the same family. `paramExt17` pins that an extension receiver counts toward the arity.
package roundtrip.wide

fun param17(f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int = f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

fun ret17(): (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p1 + p17 }

fun nested17(): List<(Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int> = listOf({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p1 * p17 })

fun applyNested17(fs: List<(Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int>): Int = fs[0](1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

fun action17(a: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Unit) { a(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17) }

fun param22(f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int = f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22)

fun ret22(): (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22 -> p1 + p22 }

fun nested22(): List<(Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int> = listOf({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22 -> p1 * p22 })

fun applyNested22(fs: List<(Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int>): Int = fs[0](1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22)

fun action22(a: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Unit) { a(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22) }

// `Int.(16 params) -> Int` — the extension receiver occupies a delegate slot, so this is arity 17.
fun paramExt17(f: Int.(Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int = 1.f(2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

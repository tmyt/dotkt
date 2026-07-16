// BATCH-C regression lock (holistic inline-splice pass, item 21). A labeled `return@lambda <value-type-nullable
// Int?/UInt?>` inside a kotc-SPLICED inline lambda routes through the splice result-local (`__inlRetN`, typed by the
// lambda's return type). The value-type-nullable (`Nullable<T>`) must reach the bare-value slot already UNWRAPPED to
// `Nullable<T>.Value` — a raw struct would have its `HasValue` bit read as the value.
//
// The unwrap is done at the LEAF (expr()'s narrowed-IrGetValue / IMPLICIT_CAST arms), NOT at the splice return site:
// a `return@f Int?` into an `Int` slot is well-typed only via a smart-cast, which Fir2Ir always materializes as a
// narrowed read or an IMPLICIT_CAST. So the spliced return arms intentionally do NOT mirror #32's non-spliced
// return-site coerceValue/wrapReturnNonNull (a verified no-op — see BirEmitterStatements/Expressions). This case
// locks that leaf coverage in across the value-nullable / smart-cast / generic shapes.

inline fun <R> runIt(block: () -> R): R = block()
class Box(val p: Int?)
fun src(): Int? = 7

// param smart-cast return (bare local)
fun paramSc(q: Int?): Int = runIt { if (q != null) return@runIt q; -1 }
// stable-property smart-cast return (NON-bare-local: a getter call, the IMPLICIT_CAST-arm path)
fun propSc(b: Box): Int = runIt { if (b.p != null) return@runIt b.p; -1 }
// local-val smart-cast return
fun localSc(): Int = runIt { val g: Int? = src(); if (g != null) return@runIt g; -1 }
// elvis returned (result is non-null Int)
fun elvis(q: Int?): Int = runIt { return@runIt (q ?: -1) }
// UInt? smart-cast return (unsigned value type)
fun uintSc(u: UInt?): UInt = runIt { if (u != null) return@runIt u; 0u }
// generic T=Int inline splice via lambda (owned by bir2cir concretization; must stay green)
fun <T : Any> pick(x: T?, d: T): T = runIt { if (x != null) return@runIt x; d }

fun main() {
    println(paramSc(5))        // 5
    println(paramSc(null))     // -1
    println(propSc(Box(3)))    // 3
    println(propSc(Box(null))) // -1
    println(localSc())         // 7
    println(elvis(9))          // 9
    println(elvis(null))       // -1
    println(uintSc(11u))       // 11
    println(uintSc(null))      // 0
    println(pick(4, -1))       // 4
}

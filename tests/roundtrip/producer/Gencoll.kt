// #86 — CARRIER-ARGUMENT ERASURE across a MODULE BOUNDARY, at a VALUE argument.
//
// A same-compilation fixture cannot witness this: producer and consumer are lowered together and agree by
// construction. Only a separately compiled consumer forces the question, because the physical slot
// (`IReadOnlyList<object>`, `Bin<object>`, `Func<object, string>`) and the Kotlin surface (`List<Int?>`,
// `Bin<Int?>`, `(Int?) -> String`) are re-derived independently on the far side — the slot from the emitted
// signature, the surface from the `[KotlinNullableGeneric]` carrier that rides it.
//
// One declaration per way a nullable value argument can reach the boundary: a read-only collection in each
// direction, a MUTABLE one (whose writes the caller must observe), a map VALUE, a USER generic, a NESTED argument,
// a DELEGATE component, and the OPEN `List<T?>` at a value instantiation. `List<String?>` is the control: a
// reference argument keeps its element type and must not move.
package gencoll

class Bin<T>(val item: T)

fun boxedInts(n: Int): List<Int?> = listOf(n, null, n * 2)      // List<Int?> RETURN

fun sumPresent(xs: List<Int?>): Int {                           // List<Int?> PARAM
    var s = 0
    for (x in xs) if (x != null) s += x
    return s
}

fun appendPresent(xs: MutableList<Int?>, v: Int): Int {         // MutableList<Int?> PARAM — writes the caller sees
    xs.add(v)
    xs.add(null)
    var n = 0
    for (x in xs) if (x != null) n++
    return n
}

fun joinPresent(xs: List<String?>): String {                    // the REFERENCE control: still IReadOnlyList<string>
    var s = ""
    for (x in xs) if (x != null) s = if (s == "") x else s + "," + x
    return s
}

fun lookup(m: Map<String, Int?>, k: String): Int? = m[k]        // a nullable value at a map VALUE argument

fun binValue(b: Bin<Int?>): Int? = b.item                       // a USER generic at Int?

fun newBin(v: Int?): Bin<Int?> = Bin(v)                         // …and its RETURN, constructed on this side

fun nestedCount(xss: List<List<Int?>>): Int {                   // a NESTED argument
    var n = 0
    for (xs in xss) n += xs.size
    return n
}

fun describe(x: Int?, f: (Int?) -> String): String = f(x)       // a DELEGATE PARAMETER component

fun <T> firstPresent(xs: List<T?>): T? {                        // the OPEN form of the same slot
    for (x in xs) if (x != null) return x
    return null
}

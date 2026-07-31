// #86 D2 — `Array<X?>` across a MODULE BOUNDARY, at a VALUE element.
//
// A same-compilation fixture cannot witness this: producer and consumer are lowered together and agree by
// construction. Only a separately compiled consumer forces the question, because the physical slot (`object[]`) and
// the Kotlin surface (`Array<Int?>`) are re-derived independently on the far side — the slot from the emitted
// signature, the surface from the `[KotlinNullableGeneric]` carrier that rides it.
//
// Four shapes, one per way the array can reach the boundary: a CONCRETE `Array<Int?>` in each direction, the OPEN
// `Array<T?>` at a value instantiation, and a `Cargo<Array<T?>, U>` where the array is NESTED inside another generic
// (the carrier's erasure lands under an array under a type argument — the composition the reader used to refuse).
// `Array<String?>` is the control: a reference element keeps its `string[]` and must not move.
package genarr

class Cargo<A, B>(val payload: A, val tag: B)

fun boxedTriple(n: Int): Array<Int?> {          // Array<Int?> RETURN
    val a = arrayOfNulls<Int>(3)
    a[0] = n
    a[2] = n * 2
    return a
}

fun sumPresent(xs: Array<Int?>): Int {          // Array<Int?> PARAM
    var s = 0
    for (x in xs) if (x != null) s += x
    return s
}

fun joinPresent(xs: Array<String?>): String {   // the REFERENCE control: still a string[]
    var s = ""
    for (x in xs) if (x != null) s = if (s == "") x else s + "," + x
    return s
}

fun <T> firstPresent(xs: Array<T?>): T? {       // the OPEN form of the same slot
    for (x in xs) if (x != null) return x
    return null
}

fun <T> firstTwo(xs: Array<T?>): Array<T?> = xs.copyOf(2)   // an OPEN Array<T?> RETURN, allocated on this side

fun <T, U> crate(xs: Array<T?>, tag: U): Cargo<Array<T?>, U> = Cargo(xs, tag)

// #153: primitive-array-receiver top-level stdlib extension calls (`intArrayOf(1,2).toList()`) must resolve at
// app level. kotc emits `callStatic owner:null method:toList sig:[kotlin.IntArray]`; bir2cir attributes the file-class
// owner off the ref.dll by receiver-key. A specialized primitive-array Fqn now canonicalizes to "[]" (like the ref-side
// RecvKey of a real int[]), pinning the `ArraysKt` owner — previously only the generic `Array<T>` receiver keyed as "[]",
// so the IntArray/CharArray/... variants dead-ended at ilemit FindStatic. Also auto-recovers #97 copyInto / #128 copyOf.
fun main() {
    println(intArrayOf(1, 2).toList())          // [1, 2]
    println(charArrayOf('a', 'b').toList())     // [a, b]
    println(longArrayOf(1L, 2L, 3L).toList())   // [1, 2, 3]
    println(doubleArrayOf(1.5, 2.5).toList())   // [1.5, 2.5]

    // #128 primitive copyOf(newSize): grows with the element default (int 0).
    val grown = intArrayOf(1, 2).copyOf(4)
    println(grown.toList())                     // [1, 2, 0, 0]

    // #97 primitive copyInto: mutates the destination array.
    val dst = IntArray(3)
    intArrayOf(7, 8).copyInto(dst)
    println(dst.toList())                       // [7, 8, 0]

    // Unsigned specialized arrays collapse to "[]" too — the fine first-param key must pin UArraysKt (not the signed
    // _ArraysKt generic toList<T>, which would bind uninstantiated and crash at runtime).
    println(ubyteArrayOf(1u, 2u).toList())      // [1, 2]
    println(uintArrayOf(3u, 4u).toList())       // [3, 4]

    // NULLABLE-receiver extension (`fun UByteArray?.contentToString()`): the fine key is receiver-nullability-insensitive
    // so it still pins UArraysKt (was miscompiling onto the signed generic -> runtime "not fully instantiated").
    println(ubyteArrayOf(9u, 8u).contentToString())  // [9, 8]
    println(intArrayOf(5, 6).contentToString())      // [5, 6]
}

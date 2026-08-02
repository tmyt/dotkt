// Generics battery (feature fixture) — migrates a pair of generic-instantiation cases/il-* onto the in-process NUnit suite.
// Each old case's `main` + stdout-golden diff becomes one @TestAttribute method with typed asserts; the old
// `println(list)` becomes an assert on the list's `.toString()` (byte-identical to the old golden). Top-level
// declarations are `GenericInstantiation`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-geninlinearg -> genericInstantiation_geninlinearg  #122 nested generic G<T> passed INLINE as a call/newobj arg, T = enclosing
//                                        fun's free type param (vararg splat at the correct type-var scope)
//   il-cwindowedv   -> genericInstantiation_cwindowedv    CharSequence.windowed with a VALUE-TYPE transform result (R = Int/Char): the
//                                        synthetic `<>dotkt_CharSequence` `it` must not collapse to String (W4-B guard)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-geninlinearg : #122 nested generic passed inline as a ctor/newobj argument ------------------------------
class GenericInstantiationHolder<T>(val list: MutableList<T>)
fun <T> genericInstantiationMkHolder(x: T): GenericInstantiationHolder<T> = GenericInstantiationHolder(mutableListOf(x))          // ctor inline arg
fun <T> genericInstantiationSizeOf(x: T): Int = ArrayList(mutableListOf(x)).size              // nested newobj inline arg

class GenericInstantiationTests {
    @TestAttribute
    fun geninlinearg() {
        assertEquals("[7]", genericInstantiationMkHolder(7).list.toString())     // value T       -> [7]
        assertEquals("[x]", genericInstantiationMkHolder("x").list.toString())   // reference T   -> [x]
        assertEquals(1, genericInstantiationSizeOf(7))                           // 1
    }

    @TestAttribute
    fun windowedValueTransform() {
        assertEquals("[2, 2, 2]", "abcd".windowed(2) { it.length }.toString())        // [2, 2, 2]  (Int transform)
        assertEquals("[a, b, c]", "abcd".windowed(2) { it[0] }.toString())            // [a, b, c]  (Char transform)
        assertEquals("[3, 3, 3]", "abcde".windowed(3) { it.length }.toString())       // [3, 3, 3]
    }
}

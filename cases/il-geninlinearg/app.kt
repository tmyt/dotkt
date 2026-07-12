// Nested generic G<T> passed INLINE (not via a local) as a call/newobj argument,
// where T is the enclosing fun's free type parameter (task #122). The inline factory
// call `mutableListOf(x)` must splat its vararg into the newList elements at the
// correct type-var scope; a scope mismatch (bir2cir MapVarianceRealign) previously
// left the vararg T[] un-splatted -> Add(T[]) -> InvalidProgramException.
class Holder<T>(val list: MutableList<T>)
fun <T> mkHolder(x: T): Holder<T> = Holder(mutableListOf(x))          // ctor inline arg
fun <T> sizeOf(x: T): Int = ArrayList(mutableListOf(x)).size          // nested newobj inline arg
fun main() {
    println(mkHolder(7).list)        // value T   -> [7]
    println(mkHolder("x").list)      // reference T -> [x]
    println(sizeOf(7))               // -> 1
}

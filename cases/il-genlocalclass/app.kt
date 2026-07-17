// #69: a function-local class that CAPTURES an enclosing TYPE PARAMETER. On the CLR generics are reified, so the
// lifted local class must be GENERIC over the captured param (liftLocalClass runs typeDef's captureEnclosingGenerics
// scan); ownerSpec/birType then name the CONSTRUCTED `L<T>` at the `new` site, the `val l: L` slot, AND member access
// `l.x` — otherwise a `tv`-typed field read hits an open generic ("not fully instantiated"). Was a whole-compile abort.
fun <T> firstBox(t: T): T {
    class L(val label: String) { val x: T = t }
    val l = L("box")                 // denotable local var of a generic-capturing local class
    return l.x                       // member access must name L<T>
}

fun <T> roundTrip(a: T, b: T): String {
    class Cell {                     // captures `a` (T-typed) + references T in a member signature
        var value: T = a
        fun swap(n: T): T { val old = value; value = n; return old }
    }
    val c = Cell()
    val old = c.swap(b)
    return "$old->${c.value}"
}

fun main() {
    println(firstBox(42))            // 42   (T = Int, a value type)
    println(firstBox("hi"))          // hi   (T = String, a ref type)
    println(roundTrip(1, 2))         // 1->2
    println(roundTrip("a", "b"))     // a->b
}

// #149: a bound callable-reference over a VALUE-TYPE (.NET struct) receiver. The delegate ctor's first
// arg is `object` and ldftn/ldvirtftn dispatch on an object reference, so the struct receiver must be
// BOXED before binding (ilemit newBoundClrDelegate). Covers both the non-virtual (ldftn) and the
// virtual (ldvirtftn) target over a value-type receiver.
import System.TimeSpan

fun main() {
    val a = TimeSpan(0, 0, 5)
    val b = TimeSpan(0, 0, 9)
    val cmp: (TimeSpan) -> Int = a::CompareTo   // non-virtual struct method -> box + ldftn
    println(cmp(b))                             // -1
    val g: () -> String = a::ToString           // virtual (Object.ToString override) -> box + dup + ldvirtftn
    println(g())                                // 00:00:05
}

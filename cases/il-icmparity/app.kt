// #129: an arity-clash .NET interface FAMILY (System.IComparable + System.IComparable`1). A Kotlin classifier cannot be
// arity-overloaded (K2 hard limit, dotkt-semantics §8d), so facadegen names the GENERIC member `IComparable1<T>` (the
// non-generic keeps the plain `IComparable`). Implementing the generic uses the VERBATIM .NET member surface —
// `CompareTo(other: Ver?)` — not the Kotlin operator `compareTo`. Direct call + upcast-to-interface dispatch.
import System.IComparable1

class Ver(val n: Int) : IComparable1<Ver> {
    override fun CompareTo(other: Ver?): Int = n - (other?.n ?: 0)
}

fun main() {
    println(Ver(3).CompareTo(Ver(5)))
    val c: IComparable1<Ver> = Ver(10)
    println(c.CompareTo(Ver(4)))
}

// #128: a Kotlin class IMPLEMENTING a facadegen-injected .NET GENERIC interface instantiated with a VALUE-TYPE arg
// (IComparer<Int>/IEquatable<Int>), the value-type sibling of il-clrifaceimpl (which uses the reference-type
// IComparer<String>). The injected member surfaces its unconstrained T as `T?`, so the override is `Compare(Int?,Int?)`
// -> post-lowering `Compare(Nullable<int32>,…)`. But the CONSTRUCTED CLR slot `IComparer<int32>.Compare` wants BARE
// `int32`; without a bridge the DefineMethodOverride sig-mismatch throws TypeLoadException at type load. bir2cir's
// ValueTypeIfaceSlotBridge synthesizes a bare-value-signature bridge forwarding to the Nullable method, so the slot
// binds and a direct call, an interface-typed upcast dispatch, AND a BCL consumer all dispatch into the override.
import System.IEquatable
import System.Collections.Generic.IComparer
import System.Collections.Generic.List

class IntCmp : IComparer<Int> {
    override fun Compare(x: Int?, y: Int?): Int = (x ?: 0) - (y ?: 0)
}

class Box(val v: Int) : IEquatable<Int> {
    override fun Equals(other: Int?): Boolean = v == (other ?: 0)
}

fun main() {
    val c = IntCmp()
    println(c.Compare(3, 1))               // 2    direct call on the implementing class
    val i: IComparer<Int> = IntCmp()       // upcast to the injected .NET interface type
    println(i.Compare(1, 3))               // -2   dispatched through the value-type interface slot

    val b = Box(5)
    println(b.Equals(5))                   // true
    val ie: IEquatable<Int> = b            // upcast to IEquatable<Int>
    println(ie.Equals(2))                  // false

    // The BCL itself dispatches into our value-type override: List<Int>.Sort(IComparer<Int>).
    val xs = List<Int>()
    xs.Add(3); xs.Add(1); xs.Add(2)
    xs.Sort(c)
    println("" + xs[0] + xs[1] + xs[2])    // 123
}

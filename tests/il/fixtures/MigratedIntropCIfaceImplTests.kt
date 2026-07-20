// CLR-interop interface-implementation battery (batch IntropC) — migrates the facadegen `import System.*` cases where a
// Kotlin class IMPLEMENTS a .NET interface (not merely extends a base class). Each old case's `main` + stdout-golden
// becomes one @TestAttribute method asserting the value directly (every `il_check_imports` value preserved 1:1, see the
// `// <expected>` comments). The tests/il .ktproj runs the facadegen scan-imports pipeline, so `import
// System.Collections.Generic.IComparer` etc. inject the CLR interface at compile.
//
// Coverage preserved (old case -> method):
//   il-clrifaceimpl   -> clrifaceimpl_referenceTypeIfaceImpl        a class implements System.Collections.Generic.IComparer<String>
//                                                                   (reference-type arg); bir2cir.DeclarationRename re-stamps the
//                                                                   override off the injected member + fills the slot, so direct /
//                                                                   interface-upcast / BCL-consumer (List<T>.Sort) all dispatch in.
//   il-clrifaceimplvt -> clrifaceimplvt_valueTypeIfaceSlotBridge    #128 the VALUE-TYPE sibling — IComparer<Int>/IEquatable<Int>.
//                                                                   The injected `T?` override lowers to Nullable<int32> params but
//                                                                   the constructed slot wants BARE int32; bir2cir's
//                                                                   ValueTypeIfaceSlotBridge synthesizes a bare-signature bridge
//                                                                   forwarding to the Nullable method (else TypeLoadException).
//   il-icmparity      -> icmparity_arityClashInterfaceFamily        #129 an arity-clash .NET interface FAMILY
//                                                                   (System.IComparable + System.IComparable`1). Kotlin cannot
//                                                                   arity-overload a classifier, so facadegen names the generic
//                                                                   member `IComparable1<T>`; implementing it uses the VERBATIM
//                                                                   .NET member `CompareTo(other: Ver?)`, not the Kotlin operator.
//
// Top-level names are family-prefixed with `IntropC` (one assembly = one namespace) to avoid clashing with sibling
// batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import System.IEquatable
import System.IComparable1
import System.Collections.Generic.IComparer
import System.Collections.Generic.List

// il-clrifaceimpl: implement System.Collections.Generic.IComparer<T> (a facadegen-injected .NET generic interface). The
// injected Compare surfaces its unconstrained T params as nullable (`String?`), so the override matches that signature.
class IntropCLenCmp : IComparer<String> {
    override fun Compare(x: String?, y: String?): Int = (x ?: "").length - (y ?: "").length
}

// il-clrifaceimplvt: the value-type variant — IComparer<Int> (Compare on a Nullable<int32>-lowered override) and
// IEquatable<Int> (Equals). Both need the ValueTypeIfaceSlotBridge to bind the bare-int32 constructed slot.
class IntropCIntCmp : IComparer<Int> {
    override fun Compare(x: Int?, y: Int?): Int = (x ?: 0) - (y ?: 0)
}

class IntropCBox(val v: Int) : IEquatable<Int> {
    override fun Equals(other: Int?): Boolean = v == (other ?: 0)
}

// il-icmparity: implement the GENERIC arm of the arity-clash family. facadegen renamed `System.IComparable`1` to
// `IComparable1`; the override uses the verbatim .NET member `CompareTo(other: Ver?)`.
class IntropCVer(val n: Int) : IComparable1<IntropCVer> {
    override fun CompareTo(other: IntropCVer?): Int = n - (other?.n ?: 0)
}

class MigratedIntropCIfaceImplTests {
    // il-clrifaceimpl: direct call on the implementing class, an interface-typed upcast dispatch, and a BCL consumer
    // (List<T>.Sort(IComparer<T>)) all dispatch into the override.
    @TestAttribute
    fun clrifaceimpl_referenceTypeIfaceImpl() {
        val c = IntropCLenCmp()
        assertEquals(1, c.Compare("ab", "z"))            // 1   direct call on the implementing class
        val i: IComparer<String> = IntropCLenCmp()       // upcast to the injected .NET interface type
        assertEquals(-3, i.Compare("z", "abcd"))         // -3  dispatched through the interface slot

        // The BCL itself dispatches into our override: List<T>.Sort(IComparer<T>).
        val xs = List<String>()
        xs.Add("abcd"); xs.Add("z"); xs.Add("bb")
        xs.Sort(c)
        assertEquals("z,bb,abcd", xs[0] + "," + xs[1] + "," + xs[2])   // z,bb,abcd
    }

    // il-clrifaceimplvt (#128): value-type interface slot bridge — direct call, interface-typed upcast, IEquatable
    // upcast, and a BCL List<Int>.Sort consumer all dispatch into the bare-value override.
    @TestAttribute
    fun clrifaceimplvt_valueTypeIfaceSlotBridge() {
        val c = IntropCIntCmp()
        assertEquals(2, c.Compare(3, 1))                 // 2    direct call on the implementing class
        val i: IComparer<Int> = IntropCIntCmp()          // upcast to the injected .NET interface type
        assertEquals(-2, i.Compare(1, 3))                // -2   dispatched through the value-type interface slot

        val b = IntropCBox(5)
        assertTrue(b.Equals(5))                          // true
        val ie: IEquatable<Int> = b                      // upcast to IEquatable<Int>
        assertFalse(ie.Equals(2))                        // false

        // The BCL itself dispatches into our value-type override: List<Int>.Sort(IComparer<Int>).
        val xs = List<Int>()
        xs.Add(3); xs.Add(1); xs.Add(2)
        xs.Sort(c)
        assertEquals("123", "" + xs[0] + xs[1] + xs[2])  // 123
    }

    // il-icmparity (#129): implementing the GENERIC arm of a same-name .NET arity family uses the verbatim member
    // surface; direct call + upcast-to-interface dispatch.
    @TestAttribute
    fun icmparity_arityClashInterfaceFamily() {
        assertEquals(-2, IntropCVer(3).CompareTo(IntropCVer(5)))   // -2
        val c: IComparable1<IntropCVer> = IntropCVer(10)
        assertEquals(6, c.CompareTo(IntropCVer(4)))                // 6
    }
}

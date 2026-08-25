// CLR-interop interface-implementation battery (feature fixture): a Kotlin class implements a .NET interface
// projected through a reference KLIB, rather than merely extending a base class.
//
// Coverage preserved (old case -> method):
//   il-clrifaceimpl   -> clrifaceimpl_referenceTypeIfaceImpl        a class implements System.Collections.Generic.IComparer<String>
//                                                                   (reference-type arg); bir2cir.DeclarationRename re-stamps the
//                                                                   override off the projected member + fills the slot, so direct /
//                                                                   interface-upcast / BCL-consumer (List<T>.Sort) all dispatch in.
//   il-clrifaceimplvt -> clrifaceimplvt_valueTypeIfaceSlotBridge    #128 the VALUE-TYPE sibling — IComparer<Int>/IEquatable<Int>.
//                                                                   The projected `T?` override lowers to Nullable<int32> params but
//                                                                   the constructed slot wants BARE int32; bir2cir's
//                                                                   KotlinOverrideSlotBridge synthesizes a bare-signature bridge
//                                                                   forwarding to the Nullable method (else TypeLoadException).
//   il-icmparity      -> icmparity_arityClashInterfaceFamily        #129 an arity-clash .NET interface FAMILY
//                                                                   (System.IComparable + System.IComparable`1). Kotlin cannot
//                                                                   arity-overload a classifier, so dll2klib names the generic
//                                                                   member `IComparable1<T>`; implementing it uses the VERBATIM
//                                                                   .NET member `CompareTo(other: Ver?)`, not the Kotlin operator.
//
// Top-level names are family-prefixed with `BclInterfaceImplementation` (one assembly = one namespace) to avoid clashing with sibling
// batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import System.IEquatable
import System.IComparable1
import System.Collections.Generic.IComparer
import System.Collections.Generic.List
import SlotTableInterop.IOverloaded

// il-clrifaceimpl: implement System.Collections.Generic.IComparer<T> (a reference-KLIB-projected .NET generic interface). The
// projected Compare surfaces its unconstrained T params as nullable (`String?`), so the override matches that signature.
class BclInterfaceImplementationLenCmp : IComparer<String> {
    override fun Compare(x: String?, y: String?): Int = (x ?: "").length - (y ?: "").length
}

// il-clrifaceimplvt: the value-type variant — IComparer<Int> (Compare on a Nullable<int32>-lowered override) and
// IEquatable<Int> (Equals). Both need the unified override slot table to bind the bare-int32 constructed slot.
class BclInterfaceImplementationIntCmp : IComparer<Int> {
    override fun Compare(x: Int?, y: Int?): Int = (x ?: 0) - (y ?: 0)
}

class BclInterfaceImplementationBox(val v: Int) : IEquatable<Int> {
    override fun Equals(other: Int?): Boolean = v == (other ?: 0)
}

// il-icmparity: implement the GENERIC arm of the arity-clash family. dll2klib renamed `System.IComparable`1` to
// `IComparable1`; the override uses the verbatim .NET member `CompareTo(other: Ver?)`.
class BclInterfaceImplementationVer(val n: Int) : IComparable1<BclInterfaceImplementationVer> {
    override fun CompareTo(other: BclInterfaceImplementationVer?): Int = n - (other?.n ?: 0)
}

class BclInterfaceImplementationOverloaded : IOverloaded<Int> {
    override fun Measure(value: String?): Int = value?.length ?: -1
    override fun Measure(value: Int?): Int = value ?: -2
}

class BclInterfaceImplementationTests {
    // il-clrifaceimpl: direct call on the implementing class, an interface-typed upcast dispatch, and a BCL consumer
    // (List<T>.Sort(IComparer<T>)) all dispatch into the override.
    @TestAttribute
    fun referenceTypeIfaceImpl() {
        val c = BclInterfaceImplementationLenCmp()
        assertEquals(1, c.Compare("ab", "z"))            // 1   direct call on the implementing class
        val i: IComparer<String> = BclInterfaceImplementationLenCmp()       // upcast to the projected .NET interface type
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
    fun valueTypeIfaceSlotBridge() {
        val c = BclInterfaceImplementationIntCmp()
        assertEquals(2, c.Compare(3, 1))                 // 2    direct call on the implementing class
        val i: IComparer<Int> = BclInterfaceImplementationIntCmp()          // upcast to the projected .NET interface type
        assertEquals(-2, i.Compare(1, 3))                // -2   dispatched through the value-type interface slot

        val b = BclInterfaceImplementationBox(5)
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
    fun arityClashInterfaceFamily() {
        assertEquals(-2, BclInterfaceImplementationVer(3).CompareTo(BclInterfaceImplementationVer(5)))   // -2
        val c: IComparable1<BclInterfaceImplementationVer> = BclInterfaceImplementationVer(10)
        assertEquals(6, c.CompareTo(BclInterfaceImplementationVer(4)))                // 6
    }

    // #355: the CLR slot is selected by its complete constructed signature, never the first same-name/same-count
    // reflection result. The String overload is deliberately declared before T in the C# producer.
    @TestAttribute
    fun overloadedValueTypeInterfaceSlots() {
        val slot: IOverloaded<Int> = BclInterfaceImplementationOverloaded()
        assertEquals(3, slot.Measure("abc"))
        assertEquals(7, slot.Measure(7))
    }
}

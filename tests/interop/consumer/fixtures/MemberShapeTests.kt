// C#-producer roundtrip consumer battery B — .NET MEMBER-shape interop (custom-named indexer, value-type property
// mutation, self-referential generic, transitive generic-member projection).
//   ixname   <- il-ixname    a custom-named indexer [IndexerName("Cell")] binds `g[i]`/`g[i]=v` to get_Cell/set_Cell
//   vtprop   <- il-vtprop    mutating a MUTABLE property/field on a .NET value-type (struct) receiver via ldloca
//   selfref  <- il-selfref   Money : IComparable<Money> passed where IComparable<Money> is expected
//   transinj <- il-transinj  constructed-generic members (IList/IReadOnlyList/Dictionary/IEnumerable) + 2-hop
//                            transitive projection (Gadget/Sprocket are reached through signatures)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
// il-ixname
import PIx.Grid
// il-vtprop
import Probe.Box
// il-selfref
import SelfRef.Money
import SelfRef.Cmp
// il-transinj
import TX.Panel
import TX.Widget

class MemberShapeTests {
    @TestAttribute
    fun ixname() {
        val g = Grid()
        assertEquals(10, g[0])   // 10 — get_Cell(0)
        assertEquals(30, g[2])   // 30 — get_Cell(2)
        g[1] = 99                // set_Cell(1, 99)
        assertEquals(99, g[1])   // 99 — get_Cell(1)
    }

    @TestAttribute
    fun vtprop() {
        val b = Box(3)           // V=3, F=3
        b.V = 10                 // clrPropSet -> set_V on the struct's address
        b.F = 20                 // clrPropSet field-store -> stfld on the struct's address
        assertEquals(10, b.V)    // 10 (was 3 before the fix: setter mutated a copy)
        assertEquals(20, b.F)    // 20
        assertEquals(30, b.Sum())// 30
    }

    @TestAttribute
    fun selfref() {
        val a = Money(7); val b = Money(3)
        assertEquals(4, Cmp().Test(a, b))  // 4 — 7 - 3
    }

    @TestAttribute
    fun transinj() {
        val panel = Panel()
        val w = Widget("w1")
        panel.Children.Add(w)
        assertEquals(1, panel.Children.Count)      // 1
        assertEquals("w1", panel.Children[0].Name) // w1
        assertEquals(1, panel.View.Count)          // 1
        assertEquals("w1", panel.View[0].Name)     // w1
        assertEquals("w1!", w.Make().Tag)          // w1!
        assertEquals(3, w.Make().Core().Size)      // 3 — "w1!".length
        panel.Index.Add("k", w)
        assertEquals("w1", panel.Index["k"].Name)  // w1
        val names = mutableListOf<String>()
        for (n in panel.Names()) names.add(n)
        assertEquals(1, names.size)                // one child
        assertEquals("w1.", names[0])              // w1. — Names() yields Name + "."
    }
}

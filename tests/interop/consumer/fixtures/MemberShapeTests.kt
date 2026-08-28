// C#-producer roundtrip consumer battery B — .NET MEMBER-shape interop (custom-named indexer, value-type property
// mutation, self-referential generic, transitive generic-member projection).
//   ixname   <- il-ixname    a custom-named indexer [IndexerName("Cell")] binds `g[i]`/`g[i]=v` to get_Cell/set_Cell
//   vtprop   <- il-vtprop    mutating a MUTABLE property/field on a .NET value-type (struct) receiver via ldloca
//   selfref  <- il-selfref   Money : IComparable<Money> passed where IComparable<Money> is expected
//   transinj <- il-transinj  constructed-generic members (IList/IReadOnlyList/Dictionary/IEnumerable) + 2-hop
//                            transitive projection (Gadget/Sprocket are reached through signatures)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
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
import CompilerGeneratedApi.Surface
import VolatileInterop.Fields

inline fun inlineTryVtProp437(block: () -> Int): Int = try { block() } catch (e: Exception) { -1 }
fun loggedVtPropInt437(log: MutableList<String>, label: String, value: Int): Int {
    log.add(label)
    return value
}

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

        // A C# volatile field is projected as a Kotlin property, but its field access must retain the CLR
        // IsVolatile fact through NetInteropBinding's clrPropGet/clrPropSet reshape.
        val volatile = Fields()
        volatile.Value = 41
        assertEquals(41, volatile.Value)
        Fields.StaticValue = 42
        assertEquals(42, Fields.StaticValue)
    }

    @TestAttribute
    fun inlineTryValuePreservesStructReceiverLocation() {
        val log = mutableListOf<String>()
        val boxes = arrayOf(Box(3))
        boxes[loggedVtPropInt437(log, "index", 0)].V = inlineTryVtProp437 {
            loggedVtPropInt437(log, "value", 17)
        }
        assertEquals(17, boxes[0].V)
        assertEquals(listOf("index", "value"), log)
    }

    // Keep each volatile shape isolated so the IL gate proves all four lowering paths independently.
    fun volatileInstanceGet(fields: Fields): Int = fields.Value
    fun volatileInstanceSet(fields: Fields, value: Int) { fields.Value = value }
    fun volatileStaticGet(): Int = Fields.StaticValue
    fun volatileStaticSet(value: Int) { Fields.StaticValue = value }

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

    @TestAttribute
    fun compilerGeneratedExternalApiIsPreserved() {
        assertEquals(31, Surface().Value())
        assertEquals(37, Surface.Nested().Value())
    }
}

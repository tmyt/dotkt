// C#-producer roundtrip consumer battery — consumes the producer's public .NET API FAÇADE-FREE (facadegen
// re-imported the plain C# dll's types from `import <Ns>.<Type>`) and asserts each migrated CLR-interop case's
// golden values 1:1. NONE of the producer's source is in this compilation — every symbol resolves from the built
// InteropProducer.dll (the DLL-not-source invariant; the producer is C# in a SIBLING dir, so `**/*.kt` can't
// capture it). Each C# runtime.cs was given its OWN namespace so the colliding simple names (Item/Bag) coexist.
//
// First migrated batch (6 `cases/il-*` runtime.cs-inject cases -> 6 @TestAttribute methods; golden values from
// scripts/verify-il.sh preserved 1:1 as `// <expected>` trailing comments, per design D1 value asserts):
//   clrasm   <- il-clrasm    List<Item> assignable to every generic interface (IEnumerable/ICollection/IList)
//   clriface <- il-clriface  a property typed as a generic INTERFACE (IList<Item>); .Add via inherited ICollection<T>
//   clrimpl  <- il-clrimpl   a C# class implementing a C# interface is assignable to the interface-typed param
//   geninj   <- il-geninj    a constructed-generic member (List<Item>) resolves to injected open List<T> over Item
//   genim    <- il-genim     a generic method declared ON an interface; the impl class assignable to the interface
//   inherit  <- il-inherit   subclass a C# base + override its PROTECTED VIRTUAL; subtype assignability
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// il-clrasm: colliding `Item`/`Bag` disambiguated per-namespace via aliased imports (the scan resolves the FQN).
import ClrAsm.Bag as ClrAsmBag
import ClrAsm.Item as ClrAsmItem
import ClrAsm.Sink as ClrAsmSink
// il-clriface
import ClrIface.Doc as ClrIfaceDoc
import ClrIface.Item as ClrIfaceItem
// il-clrimpl
import ClrImpl.IShape
import ClrImpl.Circle
import ClrImpl.Square
import ClrImpl.Drawer
// il-geninj
import GenInj.Bag as GenInjBag
import GenInj.Item as GenInjItem
// il-genim
import GenIm.Conv
import GenIm.IConv
// il-inherit
import Inherit.Base
import Inherit.Widget
import Inherit.Button
import Inherit.Host

// il-inherit top-level helper: subclass an injected .NET class and override its PROTECTED VIRTUAL (the WinUI
// App.OnLaunched pattern). Unique name so it can't collide with another battery's top-level decl.
class InheritMyApp : Base() {
    override fun Tag(): String = "derived"
}

// il-genim top-level helper: call the generic interface method through the IConv interface TYPE.
fun genimViaIface(c: IConv): String = c.Convert<String>("hello")

class InteropTests {
    @TestAttribute
    fun clrasm() {
        val b = ClrAsmBag(); b.Items.Add(ClrAsmItem("a")); b.Items.Add(ClrAsmItem("b"))
        val s = ClrAsmSink()
        assertEquals(2, s.CountE(b.Items))   // 2 — as IEnumerable<Item>
        assertEquals(2, s.CountC(b.Items))   // 2 — as ICollection<Item>
        assertEquals(2, s.CountL(b.Items))   // 2 — as IList<Item>
    }

    @TestAttribute
    fun clriface() {
        val d = ClrIfaceDoc()
        d.Items.Add(ClrIfaceItem("a"))        // Add inherited from ICollection<Item> via IList<Item> supertype chain
        d.Items.Add(ClrIfaceItem("b"))
        assertEquals(2, d.Items.Count)        // 2 — Count (property) from ICollection<Item>
        assertEquals("a", d.Items.get(0).Name) // a — indexer get(Int): Item from IList<Item>
    }

    @TestAttribute
    fun clrimpl() {
        val d = Drawer()
        assertEquals("draw:circle", d.Draw(Circle()))  // draw:circle
        assertEquals("draw:square", d.Draw(Square()))  // draw:square
        val s: IShape = Circle()                       // upcast to the interface type
        assertEquals("circle", s.Describe())           // circle
    }

    @TestAttribute
    fun geninj() {
        val bag = GenInjBag()
        bag.Items.Add(GenInjItem("a"))
        bag.Items.Add(GenInjItem("b"))
        assertEquals(2, bag.Items.Count)           // 2 — ICollection-style Count through List<Item>
        assertEquals("a", bag.Items.get(0).Name)   // a — indexer get(Int): Item, then Item.Name
    }

    @TestAttribute
    fun genim() {
        val c = Conv()
        assertEquals("hello", genimViaIface(c))         // hello — through interface type
        assertEquals("world", c.Convert<String>("world")) // world — Conv assignable to IConv usage
    }

    @TestAttribute
    fun inherit() {
        assertEquals("run:derived", InheritMyApp().Run())  // run:derived — Base.Run() dispatches to the override
        val host = Host()
        assertEquals("show:button", host.Show(Button()))   // show:button — Button assignable to the Widget param
        val w: Widget = Button()                           // upcast holds at the type level
        assertEquals("button", w.Name())                   // button — virtual dispatch through the injected hierarchy
    }
}

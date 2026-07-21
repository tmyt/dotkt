// C#-producer roundtrip consumer battery (batch A — FIR-injection / subclass / interface-impl interop). Each producer
// runtime.cs has its OWN namespace; top-level Kotlin helper decls are case-prefixed to avoid cross-battery collisions.
//
//   firgap      <- il-firgap      FIR injection of cross-.NET-type members (makeWidget -> Widget) + array members
//   injbase     <- il-injbase     assignability survives a non-constructible base (TextBox -> Frame -> Element)
//   injfqn      <- il-injfqn       two same-simple-name `Args` in different namespaces; the override binds the exact one
//   fieldvis    <- il-fieldvis    a .NET host reflects the emitted Kotlin type -> honored CLR accessor visibility
//   ifacechainvt<- il-ifacechainvt#129: implement IMid<Int> where IMid<T> : IBase<T> — value-type slots across the chain
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import Firgap.Engine
import Firgap.Widget
import Firgap.Arr
import Injbase.Element
import Injbase.TextBox
import Injbase.Api
import InjfqnAaa.Args
import InjfqnApp.Base
import Fieldvis.Refl
import Ifacechainvt.IMid

// il-fieldvis top-level helper: a Kotlin type whose property accessors carry Kotlin visibility (private/internal/
// public), reflected by the .NET host. Property NAMES (balance/owner) are what Refl reads — the class name is free.
class FieldvisAccount(initial: Int) {
    private var balance: Int = initial          // -> private property (private get_/set_)
    internal val tag: String = "acct"           // -> internal (assembly) property
    val owner: String = "me"                    // -> public property
    fun deposit(n: Int) { balance = balance + n }
    fun show(): Int = balance                   // same-class read of the private property
}

// il-injfqn top-level helper: override Base.handle whose param type must resolve to the EXACT InjfqnAaa.Args.
class InjfqnMy : Base() {
    override fun handle(x: Args): Int = 42       // must override Base.handle(InjfqnAaa.Args)
}

// il-ifacechainvt top-level helper: implement IMid<Int> (IMid<T> : IBase<T>) with value-type (int32) slots.
class IfacechainvtCell(val v: Int) : IMid<Int> {
    override fun Get(): Int = v                  // inherited through IBase<Int> — value-type slot
    override fun Rank(v: Int): Int = v * 2       // declared on IMid<Int> — value-type slot
}

class InteropAInjectTests {
    @TestAttribute
    fun firgap() {
        assertEquals(42, Engine().makeWidget().value())  // 42  (cross-type return resolves to the injected Widget)
        assertEquals(60, Arr.sumArr(Arr.range3()))       // 60  (array param + array return)
        assertEquals(3, Arr.words().size)                // 3   (string[] -> Array<String>)
        assertEquals(20, Arr.range3()[1])                // 20  (array return is indexable)
    }

    @TestAttribute
    fun injbase() {
        val tb = TextBox()
        assertEquals("placed:0", Api.place(tb))          // placed:0  (TextBox passed where Element is expected)
    }

    @TestAttribute
    fun injfqn() {
        assertEquals(42, InjfqnMy().handle(Args()))      // 42
    }

    @TestAttribute
    fun fieldvis() {
        val a = FieldvisAccount(100)
        a.deposit(50)
        val refl = Refl()
        assertEquals(150, a.show())                      // 150  (private property works within the class)
        assertEquals("me", a.owner)                      // me
        assertEquals("Private", refl.MemberVis(a, "balance"))  // Private (Kotlin visibility honored on the CLR accessor)
        assertEquals("Public", refl.MemberVis(a, "owner"))     // Public
    }

    @TestAttribute
    fun ifacechainvt() {
        val c = IfacechainvtCell(21)
        assertEquals(21, c.Get())
        assertEquals(10, c.Rank(5))
        val m: IMid<Int> = c
        assertEquals(23, m.Get() + m.Rank(1))            // 21 + 2 = 23
    }
}

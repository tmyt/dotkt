import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private open class CovariantBase(val value: Int)
private class CovariantDerived(value: Int) : CovariantBase(value)
private interface CovariantBox<out T> {
    val item: T
}
private class CovariantBoxImpl<out T>(override val item: T) : CovariantBox<T>
private interface CovariantSlot {
    val boxed: CovariantBox<CovariantBase>
}
private class CovariantSlotImpl : CovariantSlot {
    override val boxed: CovariantBox<CovariantDerived>
        get() = CovariantBoxImpl(CovariantDerived(42))
}

private interface CovariantGenericConstraint
private class CovariantGenericReceiver<T>(val value: T)
private interface CovariantGenericExtensionSlot {
    val <T : CovariantGenericConstraint> CovariantGenericReceiver<T>.genericBoxed: CovariantBox<CovariantBase>
}
private class CovariantGenericExtensionSlotImpl : CovariantGenericExtensionSlot {
    override val <T : CovariantGenericConstraint> CovariantGenericReceiver<T>.genericBoxed: CovariantBox<CovariantDerived>
        get() = CovariantBoxImpl(CovariantDerived(43))
    fun marker(): Int = 43
}

private interface CovariantGenericDefaultBase {
    fun <T> create(value: T): CovariantBase
}
private interface CovariantGenericDefaultDerived : CovariantGenericDefaultBase {
    override fun <T> create(value: T): CovariantDerived = CovariantDerived(48)
}
private class CovariantGenericDefaultImpl : CovariantGenericDefaultDerived

// A covariant override whose return type is `Nothing` (#197). Legal Kotlin — `Nothing` is below every type, so it
// satisfies any slot — and it is the LATE-SYNTHESIZED instance of the Nothing-value defect: the bridge above is
// built by bir2cir out of the target declaration's return type, so it mints a fresh `Nothing`-typed call inside a
// method that did not exist when the per-file termination sweep ran. Its erased `object` then met the slot's exact
// return at the bridge's own `ret` (ilverify: found object, expected CovariantBox<CovariantBase>), which is why the
// termination runs a second time after bridge synthesis. Both member kinds are covered because the bridge is
// synthesized from the interface SLOT, and a property slot and a function slot reach it by different routes.
private interface CovariantMaker {
    fun make(): CovariantBox<CovariantBase>
}
private class CovariantNothingSlot : CovariantSlot {
    override val boxed: Nothing get() = throw IllegalStateException("no box")
}
private class CovariantNothingMaker : CovariantMaker {
    override fun make(): Nothing = throw IllegalStateException("no maker")
}

class CovariantInterfaceReturnTests {
    @TestAttribute
    fun nestedCovariantReturnUsesExactClrSlotBridge() {
        val concrete: CovariantBox<CovariantDerived> = CovariantSlotImpl().boxed
        assertEquals(42, concrete.item.value)

        val value: CovariantSlot = CovariantSlotImpl()
        assertEquals(42, value.boxed.item.value)

        // Constructing the class forces CoreCLR to validate the method-generic MethodImpl row. Invocation of a
        // method-generic member-extension property is a separate call-site type-argument concern, not this slot test.
        val genericExtension = CovariantGenericExtensionSlotImpl()
        assertEquals(43, genericExtension.marker())

        val genericDefault: CovariantGenericDefaultBase = CovariantGenericDefaultImpl()
        // A generic DIM needs the same exact-return MethodImpl as a class implementation; otherwise the eventual
        // concrete class has no implementation of the base interface's broad-returning slot.
        assertEquals(48, genericDefault.create("x").value)

    }

    // The formal half (ilverify over this assembly) is what regresses first: the bridge is only ever entered to
    // throw, so these assertions passed while the bridge was ill-typed. Read a green run with a clean ilverify.
    @TestAttribute
    fun covariantOverrideReturningNothing() {
        val slot: CovariantSlot = CovariantNothingSlot()
        // Through the INTERFACE slot — the synthesized bridge — and through the declared member directly.
        assertEquals("no box", try { slot.boxed.item.value.toString() } catch (e: IllegalStateException) { e.message })
        assertEquals("no box", try { CovariantNothingSlot().boxed } catch (e: IllegalStateException) { e.message })
        val maker: CovariantMaker = CovariantNothingMaker()
        assertEquals("no maker", try { maker.make().item.value.toString() } catch (e: IllegalStateException) { e.message })
        assertEquals("no maker", try { CovariantNothingMaker().make() } catch (e: IllegalStateException) { e.message })
    }
}

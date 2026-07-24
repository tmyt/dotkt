import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

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

class CovariantInterfaceReturnTests {
    @TestAttribute
    fun nestedCovariantReturnUsesExactClrSlotBridge() {
        val concrete: CovariantBox<CovariantDerived> = CovariantSlotImpl().boxed
        assertEquals(42, concrete.item.value)

        val value: CovariantSlot = CovariantSlotImpl()
        assertEquals(42, value.boxed.item.value)
    }
}

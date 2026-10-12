package roundtrip.uncheckedslot

import NUnit.Framework.TestAttribute

class UncheckedSlotViewTests {
    @TestAttribute
    fun boundSlotCallsIgnoreUncheckedConcreteArgumentsAcrossDlls() {
        val owner = ConcreteOwner()
        check(owner.allocate())
        check(owner.allocations == 1)
        check(owner.free() == 1)
        check(exactSlot().allocate(owner))
        check(owner.allocations == 2)
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun storedUncheckedViewKeepsIdentityAndDispatch() {
        val owner = ConcreteOwner()
        val raw = rawSlot()
        val slot = raw as Slot<Any>
        check(slot === raw)
        check(slot.allocate(owner))
        check(slot.free(owner) == 1)
        check(owner.allocations == 1)
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun safeUncheckedViewChecksTheRawClassifier() {
        val owner = ConcreteOwner()
        val raw = rawSlot()
        val slot = raw as? Slot<Any>
        check(slot != null)
        check(slot === raw)
        check(slot.allocate(owner))
        check(slot.free(owner) == 1)
        val unrelated: Any = owner
        check((unrelated as? Slot<Any>) == null)
    }
}

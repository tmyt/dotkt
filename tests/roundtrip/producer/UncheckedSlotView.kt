package roundtrip.uncheckedslot

abstract class Slot<F> {
    abstract fun allocate(owner: F): Boolean
    abstract fun free(owner: F): Int
}

abstract class Owner<S : Slot<*>> {
    protected abstract fun currentSlot(): S

    @Suppress("UNCHECKED_CAST")
    fun allocate(): Boolean = (currentSlot() as Slot<Any>).allocate(this)

    @Suppress("UNCHECKED_CAST")
    fun free(): Int = (currentSlot() as Slot<Any>).free(this)
}

class ConcreteSlot : Slot<ConcreteOwner>() {
    override fun allocate(owner: ConcreteOwner): Boolean {
        owner.allocations++
        return true
    }

    override fun free(owner: ConcreteOwner): Int = owner.allocations
}

class ConcreteOwner : Owner<ConcreteSlot>() {
    var allocations = 0
    private val slot = ConcreteSlot()
    override fun currentSlot(): ConcreteSlot = slot
}

fun rawSlot(): Any = ConcreteSlot()
fun exactSlot(): Slot<ConcreteOwner> = ConcreteSlot() as Slot<ConcreteOwner>

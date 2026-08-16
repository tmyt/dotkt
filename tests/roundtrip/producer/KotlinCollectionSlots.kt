// #400 cross-module half: a Kotlin implementer of MutableCollection defined in a SEPARATE assembly.
//
// Its Kotlin-only members (removeAll/retainAll/addAll) have no slot on the BCL `ICollection<E>` face, so bir2cir
// gives this class the compiler-owned `DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots` interface plus an
// exact MethodImpl per member. That physical fact must survive into this assembly's metadata and be honoured by a
// CONSUMER compiled against the projected reference KLIB: the consumer emits only a call to the stdlib dispatcher,
// which tests for the interface at run time and reaches the override declared here.
package roundtrip.collslots

class TrackedBag<E> : MutableCollection<E> {
    private val backing = ArrayList<E>()
    var removeAllCalls: Int = 0
    var retainAllCalls: Int = 0
    var addAllCalls: Int = 0

    override val size: Int get() = backing.size
    override fun isEmpty(): Boolean = backing.size == 0
    override fun contains(element: E): Boolean = backing.contains(element)
    override fun containsAll(elements: Collection<E>): Boolean {
        for (e in elements) if (!backing.contains(e)) return false
        return true
    }
    override fun iterator(): MutableIterator<E> = backing.iterator()
    override fun add(element: E): Boolean { backing.add(element); return true }
    override fun remove(element: E): Boolean = backing.remove(element)
    override fun clear() { backing.clear() }

    override fun addAll(elements: Collection<E>): Boolean {
        addAllCalls++
        var changed = false
        for (e in elements) { backing.add(e); changed = true }
        return changed
    }
    override fun removeAll(elements: Collection<E>): Boolean {
        removeAllCalls++
        var changed = false
        for (e in elements) if (backing.remove(e)) changed = true
        return changed
    }
    override fun retainAll(elements: Collection<E>): Boolean {
        retainAllCalls++
        var changed = false
        var i = 0
        while (i < backing.size) {
            if (!elements.contains(backing[i])) { backing.removeAt(i); changed = true } else i++
        }
        return changed
    }

    fun render(): String {
        val sb = StringBuilder()
        sb.append("[")
        var first = true
        for (e in backing) { if (!first) sb.append(", "); sb.append(e.toString()); first = false }
        sb.append("]")
        return sb.toString()
    }
}

fun makeTrackedBag(): TrackedBag<Int> {
    val b = TrackedBag<Int>()
    b.add(1); b.add(2); b.add(3); b.add(4)
    return b
}

// Kotlin-only mutable-collection members (#400): removeAll / retainAll / addAll(elements) / addAll(index, elements).
//
// `MutableCollection<E>` IS `System.Collections.Generic.ICollection<E>` and `MutableList<E>` IS `IList<E>`, and those
// BCL interfaces carry no slot for any of the four. Before this battery existed the compiler emitted
// `recv.GetType().GetMethod(name).Invoke(recv, args)` for `removeAll`/`retainAll`, which returned null (an opaque
// NullReferenceException) on any BCL-backed receiver, and routed `addAll` unconditionally to a static helper, which
// silently bypassed a Kotlin implementer's override. The contract now is:
//
//   * every call goes to a `kotlin.collections.ClrCollectionDefaults` dispatcher;
//   * a Kotlin implementer carries `DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots` /
//     `KotlinMutableListSlots` with an exact MethodImpl per member, so its OVERRIDE is reached by virtual dispatch;
//   * a BCL-backed receiver runs the default, written only over slots that physically exist.
//
// Every override case asserts by SIDE EFFECT (a call counter), not by result value: a result can coincide with what
// the default would compute, a counter cannot.
//
// Assembly-wide collision rule: every top-level helper here is `CollectionKotlinSlot`-prefixed.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

// A Kotlin class implementing MutableCollection DIRECTLY and overriding all three collection-level members.
class CollectionKotlinSlotCounting<E> : MutableCollection<E> {
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
    fun snapshot(): List<E> {
        val out = ArrayList<E>()
        for (e in backing) out.add(e)
        return out
    }
}

// A Kotlin class implementing MutableList, so the INDEXED addAll slot is exercised too.
class CollectionKotlinSlotCountingList : MutableList<Int> {
    private val backing = ArrayList<Int>()
    var addAllAtCalls: Int = 0
    var removeAllCalls: Int = 0

    override val size: Int get() = backing.size
    override fun isEmpty(): Boolean = backing.size == 0
    override fun contains(element: Int): Boolean = backing.contains(element)
    override fun containsAll(elements: Collection<Int>): Boolean {
        for (e in elements) if (!backing.contains(e)) return false
        return true
    }
    override fun iterator(): MutableIterator<Int> = backing.iterator()
    override fun listIterator(): MutableListIterator<Int> = backing.listIterator()
    override fun listIterator(index: Int): MutableListIterator<Int> = backing.listIterator(index)
    override fun subList(fromIndex: Int, toIndex: Int): MutableList<Int> = backing.subList(fromIndex, toIndex)
    override fun get(index: Int): Int = backing[index]
    override fun set(index: Int, element: Int): Int = backing.set(index, element)
    override fun indexOf(element: Int): Int = backing.indexOf(element)
    override fun lastIndexOf(element: Int): Int = backing.lastIndexOf(element)
    override fun add(element: Int): Boolean { backing.add(element); return true }
    override fun add(index: Int, element: Int) { backing.add(index, element) }
    override fun remove(element: Int): Boolean = backing.remove(element)
    override fun removeAt(index: Int): Int = backing.removeAt(index)
    override fun clear() { backing.clear() }
    override fun addAll(elements: Collection<Int>): Boolean {
        var changed = false
        for (e in elements) { backing.add(e); changed = true }
        return changed
    }
    override fun addAll(index: Int, elements: Collection<Int>): Boolean {
        addAllAtCalls++
        var at = index
        var changed = false
        for (e in elements) { backing.add(at, e); at++; changed = true }
        return changed
    }
    override fun removeAll(elements: Collection<Int>): Boolean {
        removeAllCalls++
        var changed = false
        for (e in elements) if (backing.remove(e)) changed = true
        return changed
    }
    override fun retainAll(elements: Collection<Int>): Boolean {
        var changed = false
        var i = 0
        while (i < backing.size) {
            if (!elements.contains(backing[i])) { backing.removeAt(i); changed = true } else i++
        }
        return changed
    }
    fun snapshot(): List<Int> {
        val out = ArrayList<Int>()
        for (e in backing) out.add(e)
        return out
    }
}

// A class-delegation forwarder over a BCL-backed delegate: `MutableList<T> by backing`. The compiler generates
// forwarders for every member including the four here, and the delegate is a plain `System.Collections.Generic.List`.
class CollectionKotlinSlotDelegating(backing: MutableList<Int>) : MutableList<Int> by backing

class CollectionKotlinSlotTests {

    // ---- BCL-backed receivers: no Kotlin body exists at all, so the default must run ----------------------------

    @TestAttribute
    fun bclListThroughMutableCollection() {
        // mutableListOf -> System.Collections.Generic.List<Int>, seen through the Kotlin interface.
        val c: MutableCollection<Int> = mutableListOf(1, 2, 3, 4, 2)
        assertTrue(c.removeAll(listOf(2, 4)))
        // Kotlin removes EVERY element also contained in the argument, so the duplicate 2 must not survive.
        assertEquals("[1, 3]", c.toString())

        val c2: MutableCollection<Int> = mutableListOf(1, 2, 3, 4)
        assertTrue(c2.retainAll(listOf(2, 3)))
        assertEquals("[2, 3]", c2.toString())

        val c3: MutableCollection<Int> = mutableListOf(1)
        assertTrue(c3.addAll(listOf(7, 8)))
        assertEquals("[1, 7, 8]", c3.toString())
    }

    @TestAttribute
    fun bclListThroughMutableList() {
        val l: MutableList<Int> = mutableListOf(1, 4)
        // The INDEXED addAll has no IList slot either.
        assertTrue(l.addAll(1, listOf(2, 3)))
        assertEquals("[1, 2, 3, 4]", l.toString())
        assertFalse(l.addAll(1, listOf()))
        assertEquals("[1, 2, 3, 4]", l.toString())
    }

    @TestAttribute
    fun bclSetThroughMutableCollection() {
        // HashSet() is the BCL System.Collections.Generic.HashSet<Int>.
        val h: MutableCollection<Int> = HashSet<Int>()
        h.add(1); h.add(2); h.add(3); h.add(4)
        assertTrue(h.removeAll(listOf(2, 4)))
        assertEquals(2, h.size)
        assertTrue(h.contains(1))
        assertFalse(h.contains(2))

        val h2: MutableCollection<Int> = HashSet<Int>()
        h2.add(1); h2.add(2); h2.add(3)
        assertTrue(h2.retainAll(listOf(2)))
        assertEquals(1, h2.size)
        assertTrue(h2.contains(2))
    }

    @TestAttribute
    fun noOpsReportUnchanged() {
        val c: MutableCollection<Int> = mutableListOf(1, 2, 3)
        assertFalse(c.removeAll(listOf(9)))
        assertEquals("[1, 2, 3]", c.toString())
        assertFalse(c.retainAll(listOf(1, 2, 3, 9)))
        assertEquals("[1, 2, 3]", c.toString())
        assertFalse(c.addAll(listOf()))
        assertEquals("[1, 2, 3]", c.toString())
    }

    // ---- self-aliasing: the receiver IS the argument -------------------------------------------------------------
    // Kotlin's KDoc does not name these forms, so the compiler fixes them: each default snapshots before mutating,
    // which gives the same answers as the reference implementation on the JVM rather than throwing out of an
    // invalidated BCL enumerator. Recorded in docs/dotkt-semantics.md.

    @TestAttribute
    fun selfAliasingIsDefined() {
        val a: MutableCollection<Int> = mutableListOf(1, 2, 3)
        assertTrue(a.removeAll(a))
        assertEquals("[]", a.toString())

        val b: MutableCollection<Int> = mutableListOf(1, 2, 3)
        assertFalse(b.retainAll(b))
        assertEquals("[1, 2, 3]", b.toString())

        val c: MutableCollection<Int> = mutableListOf(1, 2)
        assertTrue(c.addAll(c))
        assertEquals("[1, 2, 1, 2]", c.toString())

        val d: MutableList<Int> = mutableListOf(1, 2)
        assertTrue(d.addAll(1, d))
        assertEquals("[1, 1, 2, 2]", d.toString())
    }

    @TestAttribute
    fun duplicatesAreFullyRemoved() {
        val dup: MutableCollection<Int> = mutableListOf(1, 2, 2, 3, 2)
        assertTrue(dup.removeAll(listOf(2)))
        assertEquals("[1, 3]", dup.toString())

        val keep: MutableCollection<Int> = mutableListOf(1, 2, 2, 3)
        assertTrue(keep.retainAll(listOf(2)))
        assertEquals("[2, 2]", keep.toString())
    }

    // ---- Kotlin implementers: the OVERRIDE must be the thing that runs -------------------------------------------

    @TestAttribute
    fun directOverrideIsInvokedThroughTheKotlinInterface() {
        val k = CollectionKotlinSlotCounting<Int>()
        k.add(1); k.add(2); k.add(3); k.add(4)

        // through the concrete class first (an ordinary callvirt), as the control
        assertTrue(k.removeAll(listOf(4)))
        assertEquals(1, k.removeAllCalls)

        // and now through the Kotlin interface, whose BCL face has no slot for any of these
        val c: MutableCollection<Int> = k
        assertTrue(c.removeAll(listOf(3)))
        assertEquals(2, k.removeAllCalls)
        assertTrue(c.retainAll(listOf(1)))
        assertEquals(1, k.retainAllCalls)
        assertTrue(c.addAll(listOf(9)))
        assertEquals(1, k.addAllCalls)

        assertEquals("[1, 9]", k.snapshot().toString())
    }

    @TestAttribute
    fun indexedAddAllOverrideIsInvoked() {
        val k = CollectionKotlinSlotCountingList()
        k.add(1); k.add(4)
        val l: MutableList<Int> = k
        assertTrue(l.addAll(1, listOf(2, 3)))
        assertEquals(1, k.addAllAtCalls)
        assertEquals("[1, 2, 3, 4]", k.snapshot().toString())
        assertTrue(l.removeAll(listOf(2, 3)))
        assertEquals(1, k.removeAllCalls)
        assertEquals("[1, 4]", k.snapshot().toString())
    }

    @TestAttribute
    fun inheritedKotlinBodyIsReachedThroughTheInterface() {
        // LinkedHashSet is a real Kotlin class in the CLR stdlib (there is no insertion-ordered generic BCL set),
        // so its own removeAll/retainAll bodies must be what runs when it is seen as a MutableCollection.
        // Asserted by MEMBERSHIP, not iteration order: LinkedHashSet's ordering after a removal is a separate
        // subject (its backing insertion-ordered map), and this battery is about which body runs.
        val s: MutableCollection<Int> = mutableSetOf(1, 2, 3, 4)
        assertTrue(s.removeAll(listOf(2, 4)))
        assertEquals(2, s.size)
        assertTrue(s.contains(1)); assertTrue(s.contains(3))
        assertTrue(s.retainAll(listOf(3)))
        assertEquals(1, s.size)
        assertTrue(s.contains(3))
        assertTrue(s.addAll(listOf(5, 3)))
        assertEquals(2, s.size)
        assertTrue(s.contains(3)); assertTrue(s.contains(5))
    }

    // ---- class delegation over a BCL-backed delegate --------------------------------------------------------------
    // The generated forwarders call the four members on the delegate field, whose static type is the Kotlin interface
    // and whose runtime value is a plain BCL List. This shape already existed in the corpus but was never CALLED.

    @TestAttribute
    fun delegationOverBclDelegate() {
        // Kotlin class delegation does not forward the `Any` members, so render the contents explicitly rather
        // than through toString().
        fun render(l: MutableList<Int>): String {
            val sb = StringBuilder()
            sb.append("[")
            var first = true
            for (e in l) { if (!first) sb.append(", "); sb.append(e.toString()); first = false }
            sb.append("]")
            return sb.toString()
        }
        val d = CollectionKotlinSlotDelegating(mutableListOf(1, 2, 3, 4))
        assertTrue(d.removeAll(listOf(2)))
        assertEquals("[1, 3, 4]", render(d))
        assertTrue(d.retainAll(listOf(1, 4)))
        assertEquals("[1, 4]", render(d))
        assertTrue(d.addAll(listOf(7)))
        assertEquals("[1, 4, 7]", render(d))
        assertTrue(d.addAll(1, listOf(9)))
        assertEquals("[1, 9, 4, 7]", render(d))
    }

    // ---- element types that get erased --------------------------------------------------------------------------
    // The capability test is deliberately NON-generic. If it were `is KotlinMutableCollectionSlots<E>` this case
    // would silently take the BCL default instead of the override, because the element type reaches the dispatcher
    // erased in exactly these shapes.

    @TestAttribute
    fun overrideSurvivesElementErasure() {
        val nullable = CollectionKotlinSlotCounting<String?>()
        nullable.add("a"); nullable.add(null); nullable.add("b")
        val cn: MutableCollection<String?> = nullable
        assertTrue(cn.removeAll(listOf<String?>(null)))
        assertEquals(1, nullable.removeAllCalls)
        assertEquals("[a, b]", nullable.snapshot().toString())

        // a VALUE-type element through a star-projected/Any-typed view
        val boxed = CollectionKotlinSlotCounting<Int>()
        boxed.add(1); boxed.add(2)
        val any: MutableCollection<Int> = boxed
        assertTrue(any.retainAll(listOf(2)))
        assertEquals(1, boxed.retainAllCalls)
        assertEquals("[2]", boxed.snapshot().toString())
    }
}

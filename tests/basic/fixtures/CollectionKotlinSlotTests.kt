// Kotlin-only mutable-collection members (#400/#590): mutable iterator / removeAll / retainAll / addAll(elements) /
// addAll(index, elements).
//
// `MutableCollection<E>` IS `System.Collections.Generic.ICollection<E>` and `MutableList<E>` IS `IList<E>`, and those
// BCL interfaces carry no slot for any of those Kotlin members. Before this battery existed the compiler emitted
// `recv.GetType().GetMethod(name).Invoke(recv, args)` for `removeAll`/`retainAll`, which returned null (an opaque
// NullReferenceException) on any BCL-backed receiver, and routed `addAll` unconditionally to a static helper, which
// silently bypassed a Kotlin implementer's override. The contract now is:
//
//   * every call goes to a `kotlin.collections.ClrCollectionDefaults` dispatcher;
//   * a Kotlin implementer carries `DotKt.Runtime.CompilerServices.KotlinMutableIteratorSlots` /
//     `KotlinMutableCollectionSlots` / `KotlinMutableListSlots` with an exact MethodImpl per member, so its OVERRIDE
//     is reached by virtual dispatch;
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
import System.Collections.Generic.LinkedList as ClrLinkedList

// A Kotlin class implementing MutableCollection DIRECTLY and overriding all three collection-level members.
open class CollectionKotlinSlotCounting<E> : MutableCollection<E> {
    private val backing = ArrayList<E>()
    var isEmptyCalls: Int = 0
    var containsCalls: Int = 0
    var containsAllCalls: Int = 0
    var removeAllCalls: Int = 0
    var retainAllCalls: Int = 0
    var addAllCalls: Int = 0

    override val size: Int get() = backing.size
    override fun isEmpty(): Boolean { isEmptyCalls++; return backing.size == 0 }
    override fun contains(element: E): Boolean { containsCalls++; return backing.contains(element) }
    override fun containsAll(elements: Collection<E>): Boolean {
        containsAllCalls++
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
    open override fun removeAll(elements: Collection<E>): Boolean {
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
    var iteratorCalls: Int = 0
    var isEmptyCalls: Int = 0
    var containsCalls: Int = 0
    var containsAllCalls: Int = 0
    var indexOfCalls: Int = 0
    var lastIndexOfCalls: Int = 0
    var listIteratorCalls: Int = 0
    var listIteratorAtCalls: Int = 0
    var subListCalls: Int = 0

    override val size: Int get() = backing.size
    override fun isEmpty(): Boolean { isEmptyCalls++; return backing.size == 0 }
    override fun contains(element: Int): Boolean { containsCalls++; return backing.contains(element) }
    override fun containsAll(elements: Collection<Int>): Boolean {
        containsAllCalls++
        for (e in elements) if (!backing.contains(e)) return false
        return true
    }
    override fun iterator(): MutableIterator<Int> {
        iteratorCalls++
        return backing.iterator()
    }
    override fun listIterator(): MutableListIterator<Int> {
        listIteratorCalls++
        return backing.listIterator()
    }
    override fun listIterator(index: Int): MutableListIterator<Int> {
        listIteratorAtCalls++
        return backing.listIterator(index)
    }
    override fun subList(fromIndex: Int, toIndex: Int): MutableList<Int> {
        subListCalls++
        return backing.subList(fromIndex, toIndex)
    }
    override fun get(index: Int): Int = backing[index]
    override fun set(index: Int, element: Int): Int = backing.set(index, element)
    override fun indexOf(element: Int): Int { indexOfCalls++; return backing.indexOf(element) }
    override fun lastIndexOf(element: Int): Int { lastIndexOfCalls++; return backing.lastIndexOf(element) }
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
class CollectionKotlinSlotDelegatingSet<T>(backing: MutableSet<T>) : MutableSet<T> by backing
class CollectionKotlinSlotDelegatingCollection<T>(backing: MutableCollection<T>) : MutableCollection<T> by backing
class CollectionKotlinSlotDelegatingIterable<T>(backing: MutableIterable<T>) : MutableIterable<T> by backing
open class CollectionKotlinSlotAnimal
class CollectionKotlinSlotDog : CollectionKotlinSlotAnimal()
class CollectionKotlinSlotEqualValue(val id: Int) {
    override fun equals(other: Any?): Boolean = other is CollectionKotlinSlotEqualValue
    override fun hashCode(): Int = 0
}


// A subclass of a slot-holding implementer: it must inherit the slot interface and receive no bridge of its own,
// and the base's bridge must still reach THIS override. Deliberately does not call `super.` — a `super` call from a
// subclass of a same-assembly GENERIC base emits an un-substituted owner and is broken independently of collections
// (witnessed by a plain `open class GenBase<E>` / `class Sub : GenBase<Int>()` probe), so using it here would pin an
// unrelated defect into this battery.
class CollectionKotlinSlotSubclass : CollectionKotlinSlotCounting<Int>() {
    var subclassRemoveAllCalls: Int = 0
    // Records the call and does nothing else ON PURPOSE. The point of this fixture is WHICH BODY the inherited slot
    // reaches; calling an inherited member of the generic base from this non-generic subclass would drag in an
    // unrelated defect (an inherited-owner call from a non-generic subclass of a same-assembly generic base emits an
    // un-substituted owner — the same family as `super.` into a generic base).
    override fun removeAll(elements: Collection<Int>): Boolean {
        subclassRemoveAllCalls++
        return false
    }
}

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

    @TestAttribute
    fun mutableIteratorDelegationKeepsMutableSlot() {
        // Pure-Kotlin LinkedHashSet carries the compiler-authored slot, so its exact override wins.
        val kotlinSet = CollectionKotlinSlotDelegatingSet(mutableSetOf(1, 2))
        val kotlinIterator: MutableIterator<Int> = kotlinSet.iterator()
        assertEquals(1, kotlinIterator.next())
        kotlinIterator.remove()
        assertFalse(kotlinSet.contains(1))

        // BCL HashSet cannot carry the slot; its fallback snapshots enumeration and removes the returned unique key.
        val hashSet = HashSet<Int>()
        hashSet.add(10)
        hashSet.add(20)
        val bclSet = CollectionKotlinSlotDelegatingSet(hashSet)
        val bclIterator = bclSet.iterator()
        val removed = bclIterator.next()
        bclIterator.remove()
        assertFalse(bclSet.contains(removed))

        // A MutableCollection-typed BCL list still takes the indexed adapter. Removing the second of two equal but
        // distinguishable values must not remove the first equal value through ICollection.Remove(value).
        val first = CollectionKotlinSlotEqualValue(1)
        val second = CollectionKotlinSlotEqualValue(2)
        val third = CollectionKotlinSlotEqualValue(3)
        val list = mutableListOf(first, second, third)
        val collectionFace: MutableCollection<CollectionKotlinSlotEqualValue> = list
        val collection = CollectionKotlinSlotDelegatingCollection(collectionFace)
        val collectionIterator = collection.iterator()
        assertTrue(collectionIterator.next() === first)
        assertTrue(collectionIterator.next() === second)
        collectionIterator.remove()
        assertTrue(list[0] === first)
        assertTrue(list[1] === third)

        // LinkedList is a duplicate-permitting ICollection but not an IList. Remove(value) would delete the first
        // equal node; the erased fallback must instead remove the exact second occurrence returned by the iterator.
        val linked = ClrLinkedList<CollectionKotlinSlotEqualValue>()
        linked.AddLast(first)
        linked.AddLast(second)
        linked.AddLast(third)
        val linkedFace = linked as MutableCollection<CollectionKotlinSlotEqualValue>
        val linkedIterator = linkedFace.iterator()
        assertTrue(linkedIterator.next() === first)
        assertTrue(linkedIterator.next() === second)
        linkedIterator.remove()
        val linkedRemaining = ArrayList<CollectionKotlinSlotEqualValue>()
        for (element in linked) linkedRemaining.add(element)
        assertTrue(linkedRemaining[0] === first)
        assertTrue(linkedRemaining[1] === third)

        // MutableIterable has only IEnumerable as its operational face, so capability dispatch is its sole route to
        // the implementer's remove-capable iterator.
        val iterableBacking = mutableSetOf(30, 40)
        val iterableFace: MutableIterable<Int> = iterableBacking
        val iterable = CollectionKotlinSlotDelegatingIterable(iterableFace)
        val iterableIterator = iterable.iterator()
        val iterableRemoved = iterableIterator.next()
        iterableIterator.remove()
        assertFalse(iterableBacking.contains(iterableRemoved))

        // MutableList is the same semantic slot: a Kotlin override must win over the indexed BCL fallback.
        val customList: MutableList<Int> = CollectionKotlinSlotCountingList()
        customList.add(50)
        val customListIterator = customList.iterator()
        assertEquals(50, customListIterator.next())
        assertEquals(1, (customList as CollectionKotlinSlotCountingList).iteratorCalls)

        // MutableIterable is covariant. A BCL List<Dog> viewed as MutableIterable<Animal> must use an erased physical
        // list face rather than attempting the impossible invariant IList<Animal> cast.
        val dogs: MutableList<CollectionKotlinSlotDog> = mutableListOf(CollectionKotlinSlotDog())
        val animals: MutableIterable<CollectionKotlinSlotAnimal> = dogs
        val animalIterator = animals.iterator()
        assertTrue(animalIterator.next() is CollectionKotlinSlotDog)
        animalIterator.remove()
        assertTrue(dogs.isEmpty())

        val dogSet: MutableSet<CollectionKotlinSlotDog> = HashSet<CollectionKotlinSlotDog>()
        dogSet.add(CollectionKotlinSlotDog())
        val animalSetView: MutableIterable<CollectionKotlinSlotAnimal> = dogSet
        val animalSetIterator = animalSetView.iterator()
        assertTrue(animalSetIterator.next() is CollectionKotlinSlotDog)
        animalSetIterator.remove()
        assertTrue(dogSet.isEmpty())

        val ints: MutableList<Int> = mutableListOf(55)
        val widenedInts: MutableIterable<Any?> = ints
        val widenedIntIterator = widenedInts.iterator()
        assertEquals(55, widenedIntIterator.next())
        widenedIntIterator.remove()
        assertTrue(ints.isEmpty())

        // Erasing the helper parameter must not erase the explicit star-cast check itself. A set is enumerable, so
        // passing the pre-cast object directly to the helper would incorrectly let this MutableList cast succeed.
        var invalidMutableListCastRejected = false
        try {
            val notAList: Any = mutableSetOf(56)
            (notAList as MutableList<*>).iterator()
        } catch (e: Exception) {
            invalidMutableListCastRejected = true
        }
        assertTrue(invalidMutableListCastRejected)

        // Star projection also retains MutableIterator.remove rather than falling through the read-only raw adapter.
        val unknown: Any = CollectionKotlinSlotDelegatingSet(mutableSetOf(60, 70))
        if (unknown is MutableSet<*>) {
            val starIterator = unknown.iterator()
            starIterator.next()
            starIterator.remove()
            assertEquals(1, unknown.size)
        } else {
            assertTrue(false)
        }

        val unknownBclSet: Any = HashSet<Int>().also { it.add(80); it.add(90) }
        if (unknownBclSet is MutableSet<*>) {
            val starBclIterator = unknownBclSet.iterator()
            starBclIterator.next()
            starBclIterator.remove()
            assertEquals(1, unknownBclSet.size)
        } else {
            assertTrue(false)
        }
    }

    // ---- element types the dispatcher is instantiated at ----------------------------------------------------------
    // NOT a witness of erasure: both call sites below instantiate the dispatcher at the concrete element
    // (`clrCollRemoveAll<System.String>` / `clrCollRetainAll<System.Int32>`), because the dispatchers take an
    // INVARIANT `ICollection<T>` receiver. What these pin is that a nullable reference element and a value-type
    // element both reach the override — the shapes where a constructed capability test would have had to be
    // re-argued. See the note on KotlinCollectionSlots.kt for why the slot surface is erased anyway.


    @TestAttribute
    fun oneGenericImplementerAtTwoInstantiations() {
        // Each bridge castclasses the erased argument back to ITS OWN element instantiation; two live
        // instantiations of the same implementer in one program must not confuse that.
        val ints = CollectionKotlinSlotCounting<Int>()
        ints.add(1); ints.add(2)
        val strs = CollectionKotlinSlotCounting<String>()
        strs.add("a"); strs.add("b")

        val ic: MutableCollection<Int> = ints
        val sc: MutableCollection<String> = strs
        assertTrue(ic.removeAll(listOf(1)))
        assertTrue(sc.removeAll(listOf("a")))
        assertEquals(1, ints.removeAllCalls)
        assertEquals(1, strs.removeAllCalls)
        assertEquals("[2]", ints.snapshot().toString())
        assertEquals("[b]", strs.snapshot().toString())
    }

    @TestAttribute
    fun selfAliasOnASlotHoldingReceiverUsesTheOverride() {
        // The self-aliasing forms are defined by the BCL default only for a BCL-backed receiver. On a receiver that
        // carries the Kotlin slot the OVERRIDE decides, exactly as on any other platform.
        // Asserted on the CALL COUNTERS only: once the override runs, the resulting contents are the override's own
        // business (this one iterates the argument directly), and the compiler-defined snapshot semantics of the BCL
        // default deliberately do NOT apply.
        val k = CollectionKotlinSlotCounting<Int>()
        k.add(1); k.add(2)
        val c: MutableCollection<Int> = k
        c.removeAll(c)
        assertEquals(1, k.removeAllCalls)

        val r = CollectionKotlinSlotCounting<Int>()
        r.add(1); r.add(2)
        val rc: MutableCollection<Int> = r
        rc.retainAll(rc)
        assertEquals(1, r.retainAllCalls)
    }

    @TestAttribute
    fun inheritedSlotReachesASubclassOverride() {
        // A subclass gets NO bridge of its own: the base's bridge forwards virtually, so the most-derived override
        // must still win. This is what lets the pass skip every class whose base already carries the interface.
        val s = CollectionKotlinSlotSubclass()
        val c: MutableCollection<Int> = s
        assertFalse(c.removeAll(listOf(2)))         // the subclass override's own answer, not the BCL default's
        assertEquals(1, s.subclassRemoveAllCalls)
        assertEquals(0, s.removeAllCalls)            // the BASE override must NOT have run
    }

    @TestAttribute
    fun overrideReachedAtNullableAndValueElements() {
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

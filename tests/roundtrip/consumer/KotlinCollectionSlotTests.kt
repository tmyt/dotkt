// #400 cross-module half: the Kotlin-only mutable-collection members across an assembly boundary.
//
// `roundtrip.collslots.TrackedBag` is compiled into the producer library and consumed HERE through its projected
// reference KLIB. Its `removeAll`/`retainAll`/`addAll` overrides have no slot on the BCL `ICollection<E>` face; they
// are reachable only because the producer assembly carries the compiler-authored
// `DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots` interface with an exact MethodImpl per member, and the
// consumer emits nothing but a call to the stdlib dispatcher that tests for it.
//
// The assertions are on the producer's CALL COUNTERS, not on results: a result can coincide with what the BCL default
// would compute, a counter cannot.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.collslots.TrackedBag
import roundtrip.collslots.makeTrackedBag

class KotlinCollectionSlotTests {
    @TestAttribute
    fun crossModuleOverrideIsReachedThroughTheKotlinInterface() {
        val b: TrackedBag<Int> = makeTrackedBag()
        val c: MutableCollection<Int> = b

        assertTrue(c.removeAll(listOf(2, 4)))
        assertEquals(1, b.removeAllCalls)
        assertEquals("[1, 3]", b.render())

        assertTrue(c.retainAll(listOf(3)))
        assertEquals(1, b.retainAllCalls)
        assertEquals("[3]", b.render())

        assertTrue(c.addAll(listOf(8, 9)))
        assertEquals(1, b.addAllCalls)
        assertEquals("[3, 8, 9]", b.render())
    }

    // The reverse enumerator bridge across the boundary (#400): the producer's `GetEnumerator` and its module-private
    // `dotkt$EnumeratorOverKotlinIterator` adapter live in the PRODUCER assembly, and this consumer reaches them
    // through the ordinary CLR enumerable face — a `for`-in here, and a compiled stdlib body reached through the
    // read-only `Collection<E>` slot.
    @TestAttribute
    fun crossModuleEnumerationGoesThroughTheProducersGetEnumerator() {
        val b: TrackedBag<Int> = makeTrackedBag()
        var sum = 0
        for (e in b) sum += e
        assertEquals(10, sum)
        val readOnly: Collection<Int> = b
        assertEquals(4, readOnly.count())
        assertEquals("1,2,3,4", readOnly.joinToString(","))
    }

    @TestAttribute
    fun crossModuleConcreteCallStillDispatchesToTheSameBody() {
        val b: TrackedBag<Int> = makeTrackedBag()
        // The concrete static type resolves the member exactly (no dispatcher involved); it must reach the same body.
        assertTrue(b.removeAll(listOf(1)))
        assertEquals(1, b.removeAllCalls)
        assertEquals("[2, 3, 4]", b.render())
    }
}

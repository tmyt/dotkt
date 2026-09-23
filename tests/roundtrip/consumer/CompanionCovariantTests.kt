package roundtriptests.companioncovariant

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.covariantreference.*
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext

private class StringFactory : ReferencedCompanionCovariantSlot<String> {
    override fun storage(): Array<String?> = arrayOf(null)
    override fun make(): ReferencedNarrowCovariantValue = ReferencedNarrowCovariantValue(41)
}

private class GenericFactory<A, T>(val seed: Array<T?>) : ReferencedCompanionCovariantSlot<T> {
    override fun storage(): Array<T?> = seed
    override fun make(): ReferencedNarrowCovariantValue = ReferencedNarrowCovariantValue(42)
}

private class CovariantIteratorCollection : AbstractMutableCollection<Int>() {
    private val elements = mutableListOf(43)
    override val size: Int get() = elements.size
    override fun add(element: Int): Boolean = elements.add(element)
    override fun iterator(): MutableIterator<Int> = elements.iterator()
}

class CompanionCovariantTests {
    @TestAttribute
    fun importedCovariantContextPreservesItsSourceTypeAndInterfaceSlot() {
        val integers = ReferencedCovariantCompletion<Int>()
        val strings = ReferencedCovariantCompletion<String>()
        val narrowed: EmptyCoroutineContext = integers.context
        check(narrowed === EmptyCoroutineContext)
        check(strings.context === EmptyCoroutineContext)
        val integerInterface: Continuation<Int> = integers
        val stringInterface: Continuation<String> = strings
        check(integerInterface.context === EmptyCoroutineContext)
        check(stringInterface.context === EmptyCoroutineContext)
        val derived: Continuation<Int> = ReferencedDerivedCovariantCompletion()
        check(derived.context === EmptyCoroutineContext)
        derived.resumeWith(Result.success(11))
    }

    @TestAttribute
    fun importedCovariantSlotsCloseTheDeclarationAndCallerFrames() {
        val strings: ReferencedCompanionCovariantSlot<String> = StringFactory()
        assertEquals(41, strings.make().value)
        assertEquals(null, strings.storage()[0])
        val integers: ReferencedCompanionCovariantSlot<Int> =
            GenericFactory<String, Int>(arrayOf<Int?>(7, null))
        assertEquals(42, integers.make().value)
        assertEquals(7, integers.storage()[0])
        assertEquals(null, integers.storage()[1])
        // Collection.iterator is Kotlin vocabulary, not a CLR IReadOnlyCollection.iterator slot.
        val collection: Collection<Int> = CovariantIteratorCollection()
        assertEquals(43, collection.iterator().next())
    }
}

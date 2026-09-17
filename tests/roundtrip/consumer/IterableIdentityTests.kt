package roundtriptests.iterableidentity

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.iterableidentity.*

private class ImportedIterableChild<T>(value: T) : ReadOnlyIterable<T>(value)
private class ImportedForeignCollectionChild : ForeignCollectionWithIterable<Int>()
private interface LocalTaggedIterable<T> : Iterable<T>
private class LocalForeignCollection : System.Collections.ObjectModel.Collection<Int>(), LocalTaggedIterable<Int> {
    override fun iterator(): Iterator<Int> = emptyList<Int>().iterator()
}

class IterableIdentityTests {
    @TestAttribute
    fun foreignBaseCollectionFacesSurviveKotlinIterableIdentity() {
        val values = arrayOf<Any>(System.Collections.ObjectModel.Collection<Int>(),
            LocalForeignCollection(), ForeignCollectionWithIterable<Int>(), ImportedForeignCollectionChild())
        for (value in values) {
            assertTrue(value is Iterable<*> && value is MutableIterable<*>)
            assertTrue(value is Collection<*> && value is MutableCollection<*>)
            assertTrue(value is List<*> && value is MutableList<*>)
            assertTrue(value as? MutableList<*> === value)
            assertTrue(value as MutableCollection<*> === value)
            assertTrue(iterableIs<MutableIterable<*>>(value))
            assertTrue(iterableIs<MutableList<*>>(value))
            assertTrue(iterableSafeCast<MutableIterable<*>>(value) === value)
            assertTrue(iterableCheckedCast<MutableList<*>>(value) === value)
        }
    }

    @TestAttribute
    fun readOnlyIterableRejectsMutableClassifiersAndCasts() {
        val value: Any = ReadOnlyIterable("value")
        assertTrue(value is Iterable<*>)
        assertTrue(value !is MutableIterable<*>)
        assertTrue(value !is Collection<*>)
        assertTrue(value as? MutableIterable<*> == null)
        assertTrue(value as Iterable<*> === value)
        var rejected = false
        try { value as MutableIterable<*> } catch (_: ClassCastException) { rejected = true }
        assertTrue(rejected)
    }

    @TestAttribute
    fun iterationMutabilityDoesNotGrantCollectionMutability() {
        val iterable: Any = MutableIterableOnly(mutableListOf("value"))
        assertTrue(iterable is Iterable<*> && iterable is MutableIterable<*>)
        assertTrue(iterable !is Collection<*> && iterable !is MutableCollection<*>)
        assertTrue(iterable as MutableIterable<*> === iterable)
        val mixed: Any = ReadOnlyCollectionWithMutableIteration(mutableListOf("value"))
        assertTrue(mixed is Iterable<*> && mixed is MutableIterable<*>)
        assertTrue(mixed is Collection<*> && mixed !is MutableCollection<*>)
        val bclBacked: Any = mutableListOf("value")
        assertTrue(bclBacked is Iterable<*> && bclBacked is MutableIterable<*>)
    }

    @TestAttribute
    fun importedReifiedOperationsPreserveIterableIdentityAndNullability() {
        val value: Any = ReadOnlyIterable("value")
        assertTrue(iterableIs<Iterable<*>>(value), "reified readonly positive")
        assertTrue(!iterableIs<MutableIterable<*>>(value), "reified mutable negative")
        assertTrue(!capturedIterableIs<MutableIterable<*>>()(value), "captured mutable negative")
        assertTrue(iterableSafeCast<MutableIterable<*>>(value) == null, "reified safe cast")
        assertTrue(iterableCheckedCast<Iterable<*>>(value) === value, "reified identity")
        var rejected = false
        try { iterableCheckedCast<MutableIterable<*>>(value) } catch (_: ClassCastException) { rejected = true }
        assertTrue(rejected)
        assertTrue(!iterableIs<MutableIterable<*>>(null), "nonnull reified null test")
        assertTrue(iterableIs<MutableIterable<*>?>(null), "nullable reified null test")
        assertTrue(iterableCheckedCast<MutableIterable<*>?>(null) == null, "nullable reified null cast")
        assertTrue(!iterableIs<MutableIterable<*>?>(value), "nullable reified mutable negative")
        assertTrue(!iterableIs<Iterable<*>>(42), "non-iterable reified negative")
        assertTrue(!iterableIs<MutableIterable<*>?>(42), "nullable non-iterable negative")
        assertTrue(iterableSafeCast<MutableIterable<*>>(42) == null, "non-iterable safe cast")
        rejected = false
        try { iterableCheckedCast<Iterable<*>>(42) } catch (_: ClassCastException) { rejected = true }
        assertTrue(rejected, "non-iterable checked cast")
        val bclBacked: Any = mutableListOf("value")
        assertTrue(iterableIs<Iterable<*>>(bclBacked), "foreign reified iterable positive")
        assertTrue(iterableIs<MutableIterable<*>>(bclBacked), "foreign reified mutable positive")
        assertTrue(iterableSafeCast<MutableIterable<*>>(bclBacked) === bclBacked,
            "foreign reified safe cast preserves identity")
    }

    @TestAttribute
    fun classifierGuardsEvaluateOperandsExactlyOnce() {
        val source = IterableEvaluation(ReadOnlyIterable("value"))
        assertTrue(source.next() !is MutableIterable<*>)
        assertTrue(source.count == 1)
        assertTrue(source.next() as? MutableIterable<*> == null)
        assertTrue(source.count == 2)
        assertTrue(!iterableIs<MutableIterable<*>?>(source.next()))
        assertTrue(source.count == 3)
        assertTrue(iterableSafeCast<MutableIterable<*>>(source.next()) == null)
        assertTrue(source.count == 4)
        var rejected = false
        try { iterableCheckedCast<MutableIterable<*>>(source.next()) }
        catch (_: ClassCastException) { rejected = true }
        assertTrue(rejected && source.count == 5)
    }

    @TestAttribute
    fun importedGenericBaseRetainsReadOnlyIterableIdentity() {
        val value: Any = ImportedIterableChild(23)
        assertTrue(value is Iterable<*> && value !is MutableIterable<*>)
        assertTrue(iterableCheckedCast<Iterable<Int>>(value).iterator().next() == 23)
        assertTrue(iterableSafeCast<MutableIterable<Int>>(value) == null)
    }
}

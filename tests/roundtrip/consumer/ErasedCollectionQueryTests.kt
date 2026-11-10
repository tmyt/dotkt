import NUnit.Framework.TestAttribute
import roundtrip.erasedqueries.QuerySet
import roundtrip.erasedqueries.CountingQuerySet
import roundtrip.erasedqueries.IntegerQueries
import roundtrip.erasedqueries.NullableQueries
import roundtrip.erasedqueries.ItemQueries
import roundtrip.erasedqueries.QueryItem
import roundtrip.erasedqueries.BoundedQuerySet
import roundtrip.erasedqueries.NativeBackedQuerySet
import roundtrip.erasedqueries.CountingQueryMap
import roundtrip.erasedqueries.BoundedQueryMap
import roundtrip.erasedqueries.NullableBoundedQueryMap
import roundtrip.erasedqueries.OrdinaryMapQueries

private class ImportedErasedQuerySet<E>(values: List<E>) : QuerySet<E>(values)
private class ImportedIntegerQueries : CountingQuerySet<Int>(listOf(1)), IntegerQueries
private class ImportedNullableQueries : CountingQuerySet<String?>(listOf(null, "x")), NullableQueries
private class ImportedItemQueries(item: QueryItem) : BoundedQuerySet<QueryItem>(listOf(item)), ItemQueries
private class ImportedCountingMap : CountingQueryMap<Int, Int>()
private class ImportedBoundedMap : BoundedQueryMap<QueryItem>()
private class ImportedNullableBoundedMap : NullableBoundedQueryMap<QueryItem>()
private class ImportedOrdinaryMap<K, V>(data: Map<K, V>) : OrdinaryMapQueries<K, V>(data), Map<K, V>

private class ImportedErasedQueryOverride : QuerySet<Int>(listOf(1)) {
    var calls = 0
    override fun containsAll(elements: Collection<Int>): Boolean { calls++; return true }
}

class CrossModuleErasedCollectionQueryTests {
    @TestAttribute
    fun importedMapQueriesPreserveEffectsThroughPrivateGenericCaptures() {
        val source = ImportedCountingMap()
        val query: Map<*, *> = source
        var expectedCalls = 0
        for (key in listOf<Any?>(1, "x", null)) {
            check(!query.containsKey(key))
            expectedCalls++
            check(source.calls == expectedCalls && source.lastKey == key)
        }
        val failure = IllegalStateException("authored Map query across DLL")
        source.failure = failure
        try {
            query.containsKey("throwing key")
            error("imported Map exception was suppressed")
        } catch (actual: IllegalStateException) { check(actual === failure && source.calls == 4) }
        val bounded = ImportedBoundedMap()
        val boundView: Map<*, *> = bounded
        check(!boundView.containsKey(null) && !boundView.containsKey("wrong") && bounded.calls == 0)
        check(!boundView.containsKey(roundtrip.erasedqueries.QueryBound()) && bounded.calls == 1)
        val nullableBounded = ImportedNullableBoundedMap()
        val nullableBoundView: Map<*, *> = nullableBounded
        check(!nullableBoundView.containsKey("wrong") && nullableBounded.calls == 0)
        check(!nullableBoundView.containsKey(null) && nullableBounded.calls == 1)
        val ordinary = ImportedOrdinaryMap(mapOf<Int?, String>(null to "present"))
        val ordinaryView: Map<*, *> = ordinary
        check(ordinaryView.containsKey(null) && ordinary.calls == 1)
        check(!ordinaryView.containsKey("wrong") && ordinary.calls == 2)
    }

    @TestAttribute
    fun importedGenericBasePreservesErasedQueriesAndVirtualOverrides() {
        val integers = ImportedErasedQuerySet(listOf(1))
        check(integers.equals(setOf(1)))
        check(!integers.equals(setOf("x")))
        check(!integers.equals(setOf<Int?>(null)))
        val erased: Collection<*> = integers
        check(!erased.containsAll(listOf("x")) && erased.containsAll(listOf(1)))
        val nullable = ImportedErasedQuerySet(listOf<Int?>(null, 1))
        check(nullable.equals(setOf(null, 1)) && !nullable.equals(setOf("x", 1)))
        val strings = ImportedErasedQuerySet(listOf("x"))
        check(strings.equals(setOf("x")) && !strings.equals(setOf(1)))
        val counted = CountingQuerySet(listOf(1))
        check(!counted.equals(setOf("x")) && counted.calls == 1)
        check(!counted.equals(setOf<Int?>(null)) && counted.calls == 2)
        val failure = IllegalStateException("authored across DLL")
        counted.failure = failure
        try {
            (counted as Collection<*>).containsAll(listOf("x"))
            error("imported contains override exception was suppressed")
        } catch (actual: IllegalStateException) { check(actual === failure && counted.calls == 3) }
        @Suppress("UNCHECKED_CAST")
        val unrelated = listOf("x") as Collection<Int>
        check(!integers.matchesAll(unrelated) && integers.matchesAll(listOf(1)))
        val override = ImportedErasedQueryOverride()
        check(override.equals(setOf("x")) && override.calls == 1)
        val inherited = ImportedIntegerQueries()
        check((inherited as IntegerQueries).contains(1) && inherited.calls == 1)
        check(!(inherited as IntegerQueries).contains(2) && inherited.calls == 2)
        val inheritedNullable = ImportedNullableQueries()
        check((inheritedNullable as NullableQueries).contains(null) && inheritedNullable.calls == 1)
        check((inheritedNullable as NullableQueries).contains("x") && inheritedNullable.calls == 2)
        val item = QueryItem()
        val inheritedBounded = ImportedItemQueries(item)
        check((inheritedBounded as ItemQueries).contains(item) && inheritedBounded.calls == 1)
        check(!(inheritedBounded as ItemQueries).contains(QueryItem()) && inheritedBounded.calls == 2)
        val nativeBacked = NativeBackedQuerySet(arrayListOf(1))
        check(!nativeBacked.equals(setOf("x")) && nativeBacked.calls == 1)
        check(!nativeBacked.equals(setOf<Int?>(null)) && nativeBacked.calls == 2)
        check(nativeBacked.equals(setOf(1)) && nativeBacked.calls == 3)
    }
}

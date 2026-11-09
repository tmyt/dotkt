import NUnit.Framework.TestAttribute
import roundtrip.erasedqueries.QuerySet
import roundtrip.erasedqueries.CountingQuerySet
import roundtrip.erasedqueries.IntegerQueries
import roundtrip.erasedqueries.NullableQueries
import roundtrip.erasedqueries.ItemQueries
import roundtrip.erasedqueries.QueryItem
import roundtrip.erasedqueries.BoundedQuerySet

private class ImportedErasedQuerySet<E>(values: List<E>) : QuerySet<E>(values)
private class ImportedIntegerQueries : CountingQuerySet<Int>(listOf(1)), IntegerQueries
private class ImportedNullableQueries : CountingQuerySet<String?>(listOf(null, "x")), NullableQueries
private class ImportedItemQueries(item: QueryItem) : BoundedQuerySet<QueryItem>(listOf(item)), ItemQueries

private class ImportedErasedQueryOverride : QuerySet<Int>(listOf(1)) {
    var calls = 0
    override fun containsAll(elements: Collection<Int>): Boolean { calls++; return true }
}

class CrossModuleErasedCollectionQueryTests {
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
    }
}

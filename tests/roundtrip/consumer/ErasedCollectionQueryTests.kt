import NUnit.Framework.TestAttribute
import roundtrip.erasedqueries.QuerySet

private class ImportedErasedQuerySet<E>(values: List<E>) : QuerySet<E>(values)

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
        @Suppress("UNCHECKED_CAST")
        val unrelated = listOf("x") as Collection<Int>
        check(!integers.matchesAll(unrelated) && integers.matchesAll(listOf(1)))
        val override = ImportedErasedQueryOverride()
        check(override.equals(setOf("x")) && override.calls == 1)
    }
}

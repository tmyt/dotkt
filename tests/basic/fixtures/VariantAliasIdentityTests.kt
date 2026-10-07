import NUnit.Framework.TestAttribute

class VariantAliasIdentityTests {
    private fun <T> collectionIdentity(value: Collection<T>): Collection<T> = value
    private fun <T> sequenceIdentity(value: Sequence<T>): Sequence<T> = value

    private fun <T> smartCastSize(value: Iterable<T>): Int =
        if (value is Collection<T>) value.size else -1

    private fun <T> smartCastFirst(value: Iterable<T>): T? =
        if (value is List<T>) value[0] else null

    @TestAttribute
    fun widenedCollectionSmartCastsPreserveStorage() {
        val original = listOf(3, 5)
        val widened: Iterable<Any?> = original
        check(smartCastSize(widened) == 2)
        check(smartCastFirst(widened) == 3)
        check(widened.count() == 2)
        check(widened.elementAt(1) == 5)
        check(widened.firstOrNull() == 3)
        check(widened.lastOrNull() == 5)
    }

    @TestAttribute
    fun widenedCollectionCopyPreservesElementsAndIndependence() {
        val original = mutableListOf(3, 5)
        val widened: Collection<Any?> = original
        val copy = ArrayList(widened)
        check(copy.size == 2)
        check(copy[0] == 3 && copy[1] == 5)
        copy.add("tail")
        check(original.size == 2)
        check(widened.toMutableList() == listOf(3, 5))
        check((widened + listOf<Any?>(7)).toList() == listOf(3, 5, 7))
        check(arrayOf(3, 5).toMutableList() == listOf(3, 5))
    }

    @TestAttribute
    fun valueElementWideningPreservesReference() {
        val original = listOf(3, 5)
        val widened: List<Any?> = original
        check(widened === original)
        check(widened[0] == 3)
        val projected: Collection<*> = original
        check(collectionIdentity(projected) === original)
        check(collectionIdentity(projected).size == 2)
    }

    @TestAttribute
    fun emptyCollectionWideningPreservesReference() {
        val bottomList: List<Nothing> = emptyList()
        val ints: List<Int> = bottomList
        check(ints === bottomList)
        check(ints.isEmpty())
        val bottomSet: Set<Nothing> = emptySet()
        val strings: Set<String> = bottomSet
        check(strings === bottomSet)
        check(strings.isEmpty())
    }

    @TestAttribute
    fun sequenceWideningPreservesReference() {
        val original = sequenceOf(7, 11)
        val widened: Sequence<Any?> = original
        check(widened === original)
        val projected: Sequence<*> = original
        check(sequenceIdentity(projected) === original)
        check(widened.toList() == listOf(7, 11))
        val bottom: Sequence<Nothing> = emptySequence()
        val ints: Sequence<Int> = bottom
        check(ints === bottom)
        check(ints.none())
        check(ints.drop(1).none())
        check(ints.take(1).none())
    }
}

import NUnit.Framework.TestAttribute

private fun <T> projectedCollectionReturnIdentity(value: T): T = value
private fun projectedCollectionReturnConsume(items: List<Any>): Int = items.size
private fun projectedCollectionReturnWiden(items: List<Any>): List<Any> = items
private class ProjectedCollectionReturnBox<T>(val value: T)
private fun <E, F : List<E>> projectedCollectionBoundSize(items: F): Int = items.size

class ProjectedCollectionReturnTests {
    @TestAttribute
    fun nestedProjectedGenericReturnWidensWithoutChangingTheValue() {
        check(projectedCollectionReturnConsume(
            projectedCollectionReturnIdentity<List<Comparable<*>>>(listOf(7, "x"))) == 2)
        check(projectedCollectionBoundSize<Int, List<Int>>(listOf(7, 8)) == 2)
        check(projectedCollectionBoundSize<String, List<String>>(listOf("a", "b")) == 2)
    }

    @TestAttribute
    fun valueElementsKeepIdentityAndAllSubsequentMutations() {
        val integers = mutableListOf(7, 8)
        val widened = projectedCollectionReturnWiden(
            projectedCollectionReturnIdentity<List<Comparable<*>>>(integers))
        check(widened === integers)
        integers[0] = 9
        check(widened[0] == 9)
        integers.add(10)
        check(widened.size == 3)
        check(widened[2] == 10)
        integers.removeAt(0)
        check(widened.size == 2)
        integers.add(11)
        check(widened.size == 3)
        check(widened[2] == 11)
        integers.clear()
        check(widened.isEmpty())
    }

    @TestAttribute
    fun referenceElementsKeepIdentityAndLiveStorage() {
        val strings = mutableListOf("first", "second")
        val widened = projectedCollectionReturnWiden(
            projectedCollectionReturnIdentity<List<Comparable<*>>>(strings))
        check(widened === strings)
        strings[0] = "changed"
        check(widened[0] == "changed")
        strings.add("third")
        check(widened.size == 3)
        strings.removeAt(0)
        check(widened.size == 2)
        check(widened[0] == "second")
        strings.clear()
        check(widened.isEmpty())
    }

    @TestAttribute
    fun nestedProjectionInOrdinaryGenericStorageKeepsTheOriginalReference() {
        val integers = mutableListOf(7, 8)
        val box = ProjectedCollectionReturnBox<List<Comparable<*>>>(integers)
        val widened = projectedCollectionReturnWiden(box.value)
        check(widened === integers)
        integers[0] = 9
        integers.add(10)
        check(widened[0] == 9)
        check(widened.size == 3)
    }
}

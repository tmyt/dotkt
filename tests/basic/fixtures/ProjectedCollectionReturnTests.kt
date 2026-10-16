import NUnit.Framework.TestAttribute

private fun <T> projectedCollectionReturnIdentity(value: T): T = value
private fun projectedCollectionReturnConsume(items: List<Any>): Int = items.size
private fun projectedCollectionReturnWiden(items: List<Any>): List<Any> = items
private class ProjectedCollectionReturnBox<T>(val value: T)
private fun <E, F : List<E>> projectedCollectionBoundSize(items: F): Int = items.size
private fun projectedCollectionSame(left: Any, right: Any): Boolean = left === right
private class ProjectedEnumeratorReturn<T>(private val source: ClrEnumerable<T>) {
    fun enumerator(): ClrEnumerator<T> = source.GetEnumerator()
}
private open class ProjectedConstructorReturn<T> {
    val result: String
    constructor(items: List<T>) { result = "list:" + items.size }
    constructor(value: Any) { result = "any:" + value }
    constructor(items: List<T>, tag: String) : this(items) { check(tag == "delegate") }
}
private class ProjectedDerivedConstructor(items: List<Int>) : ProjectedConstructorReturn<Int>(items)
private class ProjectedNullableConstructor {
    val result: String
    constructor(items: List<Int?>) { result = "ints:" + items.size }
    constructor(items: List<Long?>) { result = "longs:" + items.size }
}

class ProjectedCollectionReturnTests {
    @TestAttribute
    fun nestedProjectedGenericReturnWidensWithoutChangingTheValue() {
        check(projectedCollectionReturnConsume(
            projectedCollectionReturnIdentity<List<Comparable<*>>>(listOf(7, "x"))) == 2)
        check(projectedCollectionBoundSize<Int, List<Int>>(listOf(7, 8)) == 2)
        check(projectedCollectionBoundSize<String, List<String>>(listOf("a", "b")) == 2)
        val source = mutableListOf(7, 8) as ClrEnumerable<Int>
        val widened: ClrEnumerable<Any> = source
        val iterator = widened.GetEnumerator()
        check(iterator.MoveNext())
        check(iterator.current() == 7)
        val generic = ProjectedEnumeratorReturn<Int>(source).enumerator()
        check(generic.MoveNext())
        check(generic.current() == 7)
        check(ProjectedConstructorReturn<Int>(listOf(7)).result == "list:1")
        check(ProjectedConstructorReturn<Int>(7 as Any).result == "any:7")
        check(ProjectedConstructorReturn<Int>(listOf(7), "delegate").result == "list:1")
        check(ProjectedDerivedConstructor(listOf(7)).result == "list:1")
        check(ProjectedNullableConstructor(listOf<Int?>(7, null)).result == "ints:2")
        check(ProjectedNullableConstructor(listOf<Long?>(7L, null)).result == "longs:2")
    }

    @TestAttribute
    fun valueElementsKeepIdentityAndAllSubsequentMutations() {
        val integers = mutableListOf(7, 8)
        val widened = projectedCollectionReturnWiden(
            projectedCollectionReturnIdentity<List<Comparable<*>>>(integers))
        check(projectedCollectionSame(widened, integers))
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
        check(projectedCollectionSame(widened, strings))
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
        check(projectedCollectionSame(widened, integers))
        integers[0] = 9
        integers.add(10)
        check(widened[0] == 9)
        check(widened.size == 3)
    }
}

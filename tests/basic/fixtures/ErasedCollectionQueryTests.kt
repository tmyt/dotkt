import NUnit.Framework.TestAttribute

private open class ErasedCollectionQuerySet<E>(private val values: List<E>) : AbstractSet<E>() {
    override val size: Int get() = values.size
    override fun iterator(): Iterator<E> = values.iterator()
}

private class ErasedCollectionQueryOverride : ErasedCollectionQuerySet<Int>(listOf(1)) {
    var calls = 0
    var result = true
    override fun containsAll(elements: Collection<Int>): Boolean { calls++; return result }
}

private open class ErasedCountingQueries<E>(values: List<E>) : ErasedCollectionQuerySet<E>(values) {
    var calls = 0
    var failure: IllegalStateException? = null
    override fun contains(element: E): Boolean {
        calls++
        val pending = failure
        if (pending != null) throw pending
        return super.contains(element)
    }
}

private class ErasedTypedQuery : ErasedCollectionQuerySet<Int>(listOf(1)) {
    var calls = 0
    override fun contains(element: Int): Boolean { calls++; return super.contains(element) }
}

private open class ErasedQueryBound
private class ErasedQueryItem : ErasedQueryBound()
private open class ErasedBoundedQueries<E : ErasedQueryBound>(private val values: List<E>) : AbstractSet<E>() {
    var calls = 0
    override val size: Int get() = values.size
    override fun iterator(): Iterator<E> = values.iterator()
    override fun contains(element: E): Boolean { calls++; return super.contains(element) }
    fun contains(element: String): Boolean = error("unrelated overload was selected")
}

private interface ErasedIntegerQueries : Collection<Int>
private interface ErasedDeclaredQueries : Collection<Int> { override fun contains(element: Int): Boolean }
private interface ErasedNullableQueries : Collection<String?>
private interface ErasedItemQueries : Collection<ErasedQueryItem>
private class ErasedInheritedIntegerQueries : ErasedCountingQueries<Int>(listOf(1)), ErasedIntegerQueries
private class ErasedInheritedDeclaredQueries : ErasedCountingQueries<Int>(listOf(1)), ErasedDeclaredQueries
private class ErasedInheritedNullableQueries : ErasedCountingQueries<String?>(listOf(null, "x")), ErasedNullableQueries
private class ErasedInheritedItemQueries(item: ErasedQueryItem) :
    ErasedBoundedQueries<ErasedQueryItem>(listOf(item)), ErasedItemQueries

private class ErasedOuterQuery<A> {
    inner class Values<B>(private val values: List<B>) : AbstractSet<B>() {
        override val size: Int get() = values.size
        override fun iterator(): Iterator<B> = values.iterator()
    }
}

private class ErasedQueryCursor<T>(private val value: T) : ListIterator<T> {
    var position = 0
    val exhausted = NoSuchElementException("end")
    override fun hasNext(): Boolean = position == 0
    override fun hasPrevious(): Boolean = position == 1
    override fun next(): T { if (!hasNext()) throw exhausted; position++; return value }
    override fun previous(): T { if (!hasPrevious()) throw exhausted; position--; return value }
    override fun nextIndex(): Int = position
    override fun previousIndex(): Int = position - 1
}

private fun <T> erasedQueryNext(receiver: () -> Iterator<T>): Any? = receiver().next()
private fun <T> erasedQueryPrevious(receiver: ListIterator<T>): Any? = receiver.previous()

private fun <T> erasedCollectionQueryLoop(target: Collection<T>, values: Collection<T>): Boolean {
    for (value in values) {
        val alias = value
        if (!target.contains(alias)) return false
    }
    return true
}

private fun <T> erasedCollectionQueryConsumer(values: Collection<T>, consume: (T) -> String): String {
    val value = values.iterator().next()
    return consume(value)
}

class ErasedCollectionQueryTests {
    @TestAttribute
    fun genericQueryValuesRemainErasedUntilAnActualTypedConsumer() {
        val integers = ErasedCollectionQuerySet(listOf(1))
        check(integers.equals(setOf(1)))
        check(!integers.equals(setOf("x")))
        check(!integers.equals(setOf<Int?>(null)))
        val erased: Collection<*> = integers
        check(!erased.containsAll(listOf("x")) && !erased.containsAll(listOf(null)))
        check(erased.containsAll(listOf(1)) && erased.containsAll(emptyList<Any?>()))

        val strings = ErasedCollectionQuerySet(listOf("x"))
        check(strings.equals(setOf("x")) && !strings.equals(setOf(1)))
        check(!strings.equals(setOf<String?>(null)))
        val nullable = ErasedCollectionQuerySet(listOf<Int?>(null, 1))
        check(nullable.equals(setOf(null, 1)))
        check(!nullable.equals(setOf("x", 1)))
        check(ErasedCollectionQuerySet(emptyList<Int>()).equals(emptySet<String>()))

        val foreign = System.Collections.Generic.HashSet<String>()
        foreign.Add("x")
        check(!integers.equals(foreign as Set<*>))
        check(strings.equals(foreign as Set<*>))
        val reference = Any()
        check(ErasedCollectionQuerySet(listOf(reference)).equals(setOf(reference)))
        check(!ErasedCollectionQuerySet(listOf(reference)).equals(setOf(Any())))
        check(!integers.equals(strings) && !strings.equals(integers))

        val counted = ErasedCountingQueries(listOf(1))
        check(!counted.equals(setOf("x")) && counted.calls == 1)
        check(!counted.equals(setOf<Int?>(null)) && counted.calls == 2)
        val failure = IllegalStateException("authored")
        counted.failure = failure
        try {
            (counted as Collection<*>).containsAll(listOf("x"))
            error("contains override exception was suppressed")
        } catch (actual: IllegalStateException) { check(actual === failure && counted.calls == 3) }
        val typed = ErasedTypedQuery()
        check(!typed.equals(setOf("x")) && typed.calls == 0)
        check(typed.equals(setOf(1)) && typed.calls == 1)
        val boundedValue = ErasedQueryBound()
        val bounded = ErasedBoundedQueries(listOf(boundedValue))
        check(!bounded.equals(setOf("x")) && bounded.calls == 0)
        check(bounded.equals(setOf(boundedValue)) && bounded.calls == 1)
        val inherited = ErasedInheritedIntegerQueries()
        check((inherited as ErasedIntegerQueries).contains(1) && inherited.calls == 1)
        check(!(inherited as ErasedIntegerQueries).contains(2) && inherited.calls == 2)
        val declared = ErasedInheritedDeclaredQueries()
        check((declared as ErasedDeclaredQueries).contains(1) && declared.calls == 1)
        val inheritedNullable = ErasedInheritedNullableQueries()
        check((inheritedNullable as ErasedNullableQueries).contains(null) && inheritedNullable.calls == 1)
        check((inheritedNullable as ErasedNullableQueries).contains("x") && inheritedNullable.calls == 2)
        val item = ErasedQueryItem()
        val inheritedBounded = ErasedInheritedItemQueries(item)
        check((inheritedBounded as ErasedItemQueries).contains(item) && inheritedBounded.calls == 1)
        check(!(inheritedBounded as ErasedItemQueries).contains(ErasedQueryItem()) && inheritedBounded.calls == 2)
        check(ErasedOuterQuery<String>().Values(listOf(1)).contains(1))

        val cursor = ErasedQueryCursor(reference)
        var receiverCalls = 0
        check(erasedQueryNext { receiverCalls++; cursor } === reference && receiverCalls == 1)
        check(erasedQueryPrevious(cursor) === reference && cursor.position == 0)
        check(erasedQueryNext { receiverCalls++; cursor } === reference && receiverCalls == 2)
        try {
            erasedQueryNext { receiverCalls++; cursor }
            error("iterator exception was suppressed")
        } catch (actual: NoSuchElementException) { check(actual === cursor.exhausted && receiverCalls == 3) }

        val overridden = ErasedCollectionQueryOverride()
        check(overridden.equals(setOf("x")) && overridden.calls == 1)
        overridden.result = false
        check(!overridden.equals(setOf(1)) && overridden.calls == 2)

        @Suppress("UNCHECKED_CAST")
        val unrelated = listOf("x") as Collection<Int>
        check(!erasedCollectionQueryLoop(integers, unrelated))
        check(erasedCollectionQueryLoop(integers, listOf(1)))
        check(erasedCollectionQueryConsumer(listOf(7)) { "number=$it" } == "number=7")
        try {
            erasedCollectionQueryConsumer(unrelated) { "number=${it + 1}" }
            error("a concrete Int consumer accepted a String")
        } catch (expected: ClassCastException) {
            // The typed consumer, not a generic iterator or alias local, owns this extraction.
        }
    }
}

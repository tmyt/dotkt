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

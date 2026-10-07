import NUnit.Framework.TestAttribute

class ErasedCollectionIterationTests {
    private fun <T, C : Iterable<T>> visit(source: C, action: (Int, T) -> Unit): C =
        source.onEachIndexed(action)

    @TestAttribute
    fun genericIterableReceiverPreservesIdentityAndElements() {
        val source = listOf(4, 7)
        var total = 0
        val result = visit(source) { index, value -> total += index + value }
        check(result === source)
        check(total == 12)
    }

    private fun sum(collection: Collection<*>): Int {
        var total = 0
        for (element in collection) total += element as Int
        return total
    }

    @TestAttribute
    fun starProjectedValueElementsAndEmptyCollection() {
        check(sum(listOf(1, 2, 3)) == 6)
        check(sum(setOf(4, 5)) == 9)
        check(sum(emptyList<Int>()) == 0)
    }

    @TestAttribute
    fun sequenceParameterAndLoopControl() {
        fun collect(source: Sequence<Int>): Int {
            var total = 0
            outer@ for (element in source) {
                if (element == 2) continue@outer
                if (element == 4) break@outer
                total += element
            }
            return total
        }
        check(collect(sequenceOf(1, 2, 3, 4, 5)) == 4)
    }

    @TestAttribute
    fun sequenceSourceIsEvaluatedOnce() {
        var evaluations = 0
        fun source(): Sequence<Int> {
            evaluations += 1
            return sequenceOf(2, 3)
        }
        var total = 0
        for (element in source()) total += element
        check(evaluations == 1)
        check(total == 5)
    }
}

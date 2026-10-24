package roundtrip.operatorresults

open class MutationListBase<T>(
    private val backing: MutableList<T>,
    private val setResult: T,
    private val removeResult: T,
) : MutableList<T> by backing {
    var writes = 0
    override fun set(index: Int, element: T): T {
        writes++
        backing[index] = element
        return setResult
    }
    override fun removeAt(index: Int): T {
        backing.removeAt(index)
        return removeResult
    }
}

open class ResultIndex<T>(initial: T, private val result: T) {
    var value: T = initial
    var writes = 0
    open operator fun set(index: Int, element: T): T {
        check(index == 0)
        writes++
        value = element
        return result
    }
}

package roundtriptests.sequenceresults

internal class LocalSequenceResult<T>(val item: T, val source: CharSequence?) {
    fun read(): CharSequence? = source
    fun pick(x: Int, y: Int = x): CharSequence? = if (x == y) source else null
    fun lengthOf(value: CharSequence?) = value?.length ?: -1
    companion {
        fun <U> text(value: U): CharSequence? = if (value == null) null else "hello"
    }
}

package roundtriptests.sequenceresults

internal class LocalSequenceResult<T>(val item: T, val source: CharSequence?) {
    fun read(): CharSequence? = source
}

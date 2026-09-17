package roundtrip.iteratorproviders
interface DefaultIterable<T> : Iterable<T> {
    override fun iterator(): Iterator<T> = emptyList<T>().iterator()
}

package roundtrip.indirectcollection

interface CollectionContract<T> : MutableCollection<T> {
    val values: MutableList<T>
    var adds: Int
    override fun add(element: T): Boolean {
        adds++
        return values.add(element)
    }
}

interface ListContract<T> : MutableList<T>

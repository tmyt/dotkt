package roundtrip.covariantreference

open class ReferencedCovariantValue(val value: Int)

class ReferencedNarrowCovariantValue(value: Int) : ReferencedCovariantValue(value)

interface ReferencedCovariantRoot<T> {
    val item: T
    fun make(): T
    fun makeWith(seed: Int): T
    fun <X> makeFrom(seed: X): T
}

interface ReferencedCovariantSlot : ReferencedCovariantRoot<ReferencedCovariantValue>

interface ReferencedConstrainedCovariantRoot<T, U> {
    fun <X : T> makeConstrained(seed: X): ReferencedCovariantValue
}

interface ReferencedSuspendCovariantControl {
    suspend fun load(): ReferencedCovariantValue
}

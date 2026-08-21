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

// Both declarations lower to the same broad signature. A downstream covariant DIM may use one exact bridge body for
// both MethodImpl rows; the consumer fixture pins that CoreCLR-valid inherited-interface shape.
interface ReferencedRedeclaredCovariantRoot {
    fun makeRedeclared(): ReferencedCovariantValue
}

interface ReferencedRedeclaredCovariantSlot : ReferencedRedeclaredCovariantRoot {
    override fun makeRedeclared(): ReferencedCovariantValue
}

interface ReferencedConstrainedCovariantRoot<T, U> {
    fun <X : T> makeConstrained(seed: X): ReferencedCovariantValue
}

interface ReferencedSuspendCovariantControl {
    suspend fun load(): ReferencedCovariantValue
}

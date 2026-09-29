package roundtrip.nonnullbounds

interface BoundFactory {
    fun <U : Any> make(value: U): Any
}

interface BoundOrdinarySlot<T> {
    fun <U : Any> read(value: U): T
}

interface BoundSlot<T> {
    fun <U : Any> read(value: U): T
    suspend fun <U : Any> delayed(value: U, pause: suspend () -> Unit): T
}

open class BoundBase<T>(val result: T) : BoundSlot<T> {
    override fun <U : Any> read(value: U): T = result
    override suspend fun <U : Any> delayed(value: U, pause: suspend () -> Unit): T {
        pause()
        return result
    }
}

class NonNullBox<T : Any>(val value: T)
class NullableBox<T : Any?>(val value: T)
fun <T : Any> nonNull(value: T): T = value
fun <T : Any?> nullable(value: T): T = value
fun <T> unconstrained(value: T): T = value

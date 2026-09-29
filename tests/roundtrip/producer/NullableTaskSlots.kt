package roundtrip.nullabletaskslots

interface NullableTaskSlot<T> {
    suspend fun <U> read(marker: U, pause: suspend () -> Unit): T
    suspend fun <U> read(marker: U, text: String): T
}

abstract class NullableTaskBase<T> {
    abstract suspend fun <U> read(marker: U, pause: suspend () -> Unit): T
}

interface NullableDeclaredTaskSlot<T> {
    suspend fun <U> read(marker: U, pause: suspend () -> Unit): T?
}

class ProducerTaskBase(private val result: Long?) : NullableTaskBase<Long?>() {
    override suspend fun <U> read(marker: U, pause: suspend () -> Unit): Long? {
        pause()
        return result
    }
}

open class FinalTaskBody(private val result: Long?) {
    suspend fun <U> read(marker: U, pause: suspend () -> Unit): Long? {
        pause()
        return result
    }
    suspend fun <U> read(marker: U, text: String): Long? = result
}

interface OrdinaryCallbackSlot<T> {
    fun <U> read(marker: U, pause: suspend () -> Unit): T
}
open class OrdinaryCallbackBody(private val result: Long?) {
    open fun <U> read(marker: U, pause: suspend () -> Unit): Long? = result
}

class ProducerNullableTask(private val result: Int?) : NullableTaskSlot<Int?> {
    override suspend fun <U> read(marker: U, pause: suspend () -> Unit): Int? {
        pause()
        return result
    }
    override suspend fun <U> read(marker: U, text: String): Int? = result
}

open class ProducerTaskBody(private val result: Long?) {
    open suspend fun <U> read(marker: U, pause: suspend () -> Unit): Long? {
        pause()
        return result
    }
    open suspend fun <U> read(marker: U, text: String): Long? = result
}

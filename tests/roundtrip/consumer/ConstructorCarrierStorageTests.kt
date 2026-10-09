@file:Suppress("UNCHECKED_CAST")
package roundtriptests.constructorcarrier

import NUnit.Framework.TestAttribute
import roundtrip.constructorcarrier.*

abstract class Linked<S : Linked<S>>(val prev: S?)
class Segment<E>(prev: Segment<E>?, val owner: Buffer<E>?) : Linked<Segment<E>>(prev)
private val sentinel = Segment<Any?>(null, null)
class Buffer<E>(empty: Boolean) {
    val storage: Storage<Segment<E>>
    init {
        val first = Segment(null, this)
        storage = store(if (empty) (sentinel as Segment<E>) else first)
    }
}

class PlainSegment<E>
private val plainSentinel = PlainSegment<Any?>()
class PlainBuffer<E> {
    val storage: Storage<PlainSegment<E>> = store(plainSentinel as PlainSegment<E>)
}
fun <T> localStore(value: T): Storage<T> = Storage(value)
class LocalBuffer<E> {
    val storage: Storage<PlainSegment<E>> = localStore(plainSentinel as PlainSegment<E>)
}

private fun <E> verifySeparateSlots() {
    val raw = tokenSentinelObject() as Token<E>
    val actual = Token<E>("actual")
    val result = pair<Token<E>, Token<E>>(raw, actual)
    check(result.first === raw)
    check(result.second === actual)
}

class ConstructorCarrierStorageTests {
    @TestAttribute
    fun genericConstructorPreservesBothBranches() {
        val empty = Buffer<String>(true)
        val full = Buffer<String>(false)
        check(empty.storage.value === sentinel)
        check(full.storage.value.owner === full)
    }

    @TestAttribute
    fun directArgumentDoesNotRequireAnFBound() {
        check(PlainBuffer<String>().storage.value === plainSentinel)
    }

    @TestAttribute
    fun localGenericFactoryUsesTheSameValueFrame() {
        check(LocalBuffer<String>().storage.value === plainSentinel)
    }

    @TestAttribute
    fun projectedStorageReadsAcrossDllBoundaries() {
        val imported = ImportedBuffer<String>()
        check(imported.storage.value === tokenSentinelObject())
        check(imported.storage.value.tag == "sentinel")
    }

    @TestAttribute
    fun equalSourceTypesDoNotMergeSeparateMethodSlots() {
        verifySeparateSlots<String>()
    }
}

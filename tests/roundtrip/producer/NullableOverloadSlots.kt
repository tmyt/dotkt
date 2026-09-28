import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.CoroutineContext

interface OverloadSlotMarker : CoroutineContext.Element {
    companion object Key : CoroutineContext.Key<OverloadSlotMarker>
}
class OverloadSlotElement : AbstractCoroutineContextElement(OverloadSlotMarker), OverloadSlotMarker
class OverloadSlotResult<T>(val marker: OverloadSlotMarker?, val selected: String)

fun <T> remoteOverloadSlot(value: T): OverloadSlotResult<T> = OverloadSlotResult(null, "value")
fun <T> remoteOverloadSlot(marker: OverloadSlotMarker? = null): OverloadSlotResult<T> =
    OverloadSlotResult(marker, "marker")

class RemoteOverloadSlotFactory<T>(val owner: T) {
    fun <M> select(value: M): OverloadSlotResult<T> = OverloadSlotResult(null, "value")
    fun <M> select(marker: OverloadSlotMarker?): OverloadSlotResult<T> = OverloadSlotResult(marker, "marker")
}

open class RemoteInheritedSlotBase<T>(val owner: T) {
    fun select(marker: OverloadSlotMarker?): OverloadSlotResult<T> = OverloadSlotResult(marker, "marker")
}
class RemoteInheritedSlotDerived<T>(owner: T) : RemoteInheritedSlotBase<T>(owner) {
    fun select(value: String): String = value
}
open class RemoteInheritedOverloadBase<T>(val owner: T) {
    fun <M> select(value: M): OverloadSlotResult<T> = OverloadSlotResult(null, "value")
    fun <M> select(marker: OverloadSlotMarker?): OverloadSlotResult<T> = OverloadSlotResult(marker, "marker")
}
class RemoteInheritedOverloadDerived : RemoteInheritedOverloadBase<Int>(31)

suspend fun <T> remoteSuspendingOverload(value: T): OverloadSlotResult<T> = OverloadSlotResult(null, "value")
suspend fun <T> remoteSuspendingOverload(marker: OverloadSlotMarker?): OverloadSlotResult<T> =
    OverloadSlotResult(marker, "marker")

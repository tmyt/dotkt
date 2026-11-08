package dotkt.support.events

import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

open class FunctionHandlerBase {
    open val Changed: ClrEvent<(Int) -> Unit> by clrEvent()
}

class InferredHandlerSource : FunctionHandlerBase() {
    override val Changed by clrEvent()
}

fun localInferredHandler(): ClrEvent<Nothing> = InferredHandlerSource().Changed

class NullableHandlerSource {
    val Changed: ClrEvent<System.Action?> by clrEvent()
}

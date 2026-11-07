import CustomPolicyPayloads.SpanHandler
import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

class SpanPolicy : ClrEventPolicy<SpanHandler>() {
    override fun add(handler: SpanHandler) {}
    override fun remove(handler: SpanHandler) {}
    override fun invoke(vararg args: Any?) {}
}
class SpanPolicySource {
    val Changed: ClrEvent<SpanHandler> by clrEvent(SpanPolicy())
}

import CustomPolicyPayloads.RefHandler
import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

class RefPolicy : ClrEventPolicy<RefHandler>() {
    override fun add(handler: RefHandler) {}
    override fun remove(handler: RefHandler) {}
    override fun invoke(vararg args: Any?) {}
}
class RefPolicySource {
    val Changed: ClrEvent<RefHandler> by clrEvent(RefPolicy())
}

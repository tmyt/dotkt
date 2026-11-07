import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

class TopLevelPolicy : ClrEventPolicy<System.Action>() {
    override fun add(handler: System.Action) {}
    override fun remove(handler: System.Action) {}
    override fun invoke(vararg args: Any?) {}
}
val Changed: ClrEvent<System.Action> by clrEvent(TopLevelPolicy())

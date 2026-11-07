import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

class StaticPolicy : ClrEventPolicy<System.Action>() {
    override fun add(handler: System.Action) {}
    override fun remove(handler: System.Action) {}
    override fun invoke(vararg args: Any?) {}
}
class GenericStaticPolicyOwner<T> {
    companion { val Changed: ClrEvent<System.Action> by clrEvent(StaticPolicy()) }
}

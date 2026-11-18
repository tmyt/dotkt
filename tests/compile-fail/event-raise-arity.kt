import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

class Emitter {
    val Changed: ClrEvent<(Int) -> Unit> by clrEvent()
    fun fire() { Changed() }
}

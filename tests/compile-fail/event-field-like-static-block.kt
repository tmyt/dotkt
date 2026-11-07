import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

class GenericStaticFieldLikeOwner<T> {
    companion { val Changed: ClrEvent<System.Action> by clrEvent() }
}

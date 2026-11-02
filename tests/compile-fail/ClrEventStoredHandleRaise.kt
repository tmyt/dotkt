import kotlin.clr.ClrEvent

class StoredEventHandle(val event: ClrEvent<(Int) -> Unit>)

fun raiseStoredHandle(box: StoredEventHandle) {
    box.event(1)
}

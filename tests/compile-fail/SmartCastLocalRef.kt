import kotlin.clr.ClrRef
import kotlin.clr.byref

private fun replace(slot: ClrRef<String>) { slot.value = "after" }

fun test(input: Any): Any {
    var local = input
    if (local is String) replace(byref(local))
    return local
}

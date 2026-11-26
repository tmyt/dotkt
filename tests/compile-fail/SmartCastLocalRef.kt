import kotlin.clr.ClrRef
import kotlin.clr.byref

private fun replace(slot: ClrRef<String>) { slot.value = "after" }

fun test(input: Any): Any {
    var local = input
    if (local is String) replace(byref(local))
    return local
}

@Suppress("UNCHECKED_CAST")
fun arrayView(input: Array<out Any>) {
    fun replace(slot: ClrRef<Array<String>>) { slot.value = arrayOf("after") }
    var elements = input
    elements as Array<String>
    replace(byref(elements))
}

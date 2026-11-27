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

private fun replaceAny(slot: ClrRef<Any>) { slot.value = "not a Number" }
private fun replaceList(slot: ClrRef<List<Any>>) { slot.value = listOf(42) }
private fun clear(slot: ClrRef<String?>) { slot.value = null }

fun <T : Number> widerBorrows(input: T, strings: List<String>, nonNull: String) {
    var number: Number = input
    var generic = input
    var list = strings
    replaceAny(byref<Any>(number))
    replaceAny(byref(generic))
    replaceList(byref(list))
    var text = nonNull
    clear(byref(text))
}

fun <T> definitelyNonNull(input: T & Any, replacement: T) {
    fun put(slot: ClrRef<T>) { slot.value = replacement }
    var nonNull: T & Any = input
    put(byref<T>(nonNull))
}

fun aliasedLocal() {
    var live by byref<String>(System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference<String>(arrayOf("before")))
    clear(byref<String?>(live))
}

fun resizeNonNullLocal() {
    var values = arrayOf("before")
    System.Array.Resize(byref(values), 4)
}

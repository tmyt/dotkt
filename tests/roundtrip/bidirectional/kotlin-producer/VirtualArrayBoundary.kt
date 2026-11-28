import kotlin.clr.ClrRef
import kotlin.clr.byref

private class InspectedBox<T>(val value: T)

interface RefArrayInspection {
    fun inspectRef(value: ClrRef<Any>): Any?
}

open class KotlinRefInspection : RefArrayInspection {
    override fun inspectRef(value: ClrRef<Any>): Any? = (value.value as Array<*>)[0]
}

fun inspectRefThroughKotlin(view: RefArrayInspection, compatible: Boolean) = inspectGenericRef(view, compatible)

@Suppress("UNCHECKED_CAST")
private fun <S : RefArrayInspection> inspectGenericRef(view: S, compatible: Boolean): Boolean {
    val first = InspectedBox("initial")
    val expected = if (compatible) first else InspectedBox(42) as InspectedBox<String>
    val array = arrayOf(first)
    array[0] = expected
    var slot: Any = array
    val observed = view.inspectRef(byref(slot))
    return if (observed === array) slot is String else observed === expected && slot === array
}

@Suppress("UNCHECKED_CAST")
fun inspectThroughKotlin(view: ArrayInspection, bound: Boolean, compatible: Boolean): Boolean {
    val first = InspectedBox("original")
    val array = arrayOf(first)
    val value = if (compatible) first else InspectedBox(42) as InspectedBox<String>
    array[0] = value
    val result = if (bound) {
        val call = view::inspect
        call(array)
    } else view.inspect(array)
    // A native implementation returns the vector; the Kotlin implementation reads the element.
    return result === array || result === value
}

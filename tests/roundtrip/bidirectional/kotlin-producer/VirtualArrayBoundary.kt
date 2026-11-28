interface ArrayInspection {
    fun inspect(value: Any): Any?
}

open class KotlinArrayInspection : ArrayInspection {
    override fun inspect(value: Any): Any? = (value as Array<*>)[0]
}

private class InspectedBox<T>(val value: T)

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

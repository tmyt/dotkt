package roundtrip.arrayviews

fun <T> viewOf(values: Array<out T>): List<T> = values.asList()
inline fun <T> inlineViewOf(values: Array<out T>, before: () -> Unit): List<T> {
    before()
    return values.asList()
}
fun intViewOf(values: IntArray): List<Int> = values.asList()

class SharedArrayViews {
    val values = arrayOf<Int?>(null, 2, null)
    val view: List<Int?> = values.asList()
    fun replace(value: Int?) { values[0] = value; values[2] = value }
}

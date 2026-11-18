package roundtrip.interpolation

fun <T> renderValue(value: T): String = "[$value]"
@Suppress("NOTHING_TO_INLINE")
inline fun <T> renderInline(value: T): String = "[$value]"
fun <T> renderNullable(value: T?): String = "[$value]"
fun renderText(value: String?): String = "[$value]"

class RenderSource<T>(val value: T) {
    fun renderWith(next: () -> T): String = "[$value:${next()}]"
}

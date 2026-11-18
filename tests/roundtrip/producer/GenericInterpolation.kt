package roundtrip.interpolation

fun <T> renderValue(value: T): String = "[$value]"
fun <T> renderNullable(value: T?): String = "[$value]"
fun renderText(value: String?): String = "[$value]"

class RenderSource<T>(val value: T) {
    fun renderWith(next: () -> T): String = "[$value:${next()}]"
}

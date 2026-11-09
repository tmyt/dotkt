package wrapper.producer

enum class WireValue(val code: Int) {
    FIRST(7), SECOND(11) { override fun label(): String = "second" };
    open fun label(): String = "first"
}

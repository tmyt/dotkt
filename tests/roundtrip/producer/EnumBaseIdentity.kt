package roundtrip.enumidentity

enum class BaseIdentity(val code: Int) {
    FIRST(7), SECOND(7), ENTRY(7) { override fun label(): String = "entry" };

    open fun label(): String = "plain"
    fun baseEquals(other: Any?): Boolean = super.equals(other)
    fun baseHash(): Int = super.hashCode()
}

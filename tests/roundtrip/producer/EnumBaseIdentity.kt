package roundtrip.enumidentity

enum class BaseIdentity(val code: Int) {
    FIRST(7), SECOND(7), ENTRY(7) {
        override fun label(): String = "entry"
        override fun baseEquals(other: Any?): Boolean = super.equals(other)
        override fun baseHash(): Int = super.hashCode()
    };

    open fun label(): String = "plain"
    open fun baseEquals(other: Any?): Boolean = super.equals(other)
    open fun baseHash(): Int = super.hashCode()
}

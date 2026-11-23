interface PrivateHelperContract {
    private fun toString(value: Int): Int = value
    fun answer(): Int = toString(17)
}

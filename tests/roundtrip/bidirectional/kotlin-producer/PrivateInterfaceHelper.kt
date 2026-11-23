interface PrivateHelperContract {
    private fun hidden(): Int = 17
    fun answer(): Int = hidden()
}

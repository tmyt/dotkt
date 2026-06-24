// IL parity: visibility modifiers -> CLR access flags.
class Account(private val balance: Int) {
	private fun fee(): Int = 2
	fun net(): Int = balance - fee()       // private method used within the class
	internal fun tag(): String = "acct"
	protected open fun kind(): String = "base"
}
private fun secret(): Int = 99
fun main() {
	val a = Account(100)
	println(a.net())
	println(a.tag())
	println(secret())
}

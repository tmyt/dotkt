interface Greeter {
	fun greet(): String
}
class English : Greeter {
	override fun greet(): String = "Hello"
}
class Japanese : Greeter {
	override fun greet(): String = "Konnichiwa"
}
fun shout(g: Greeter): String = g.greet()
fun main() {
	println(shout(English()))
	println(shout(Japanese()))
}

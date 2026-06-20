interface Greeter { fun greet(): String }
fun make(): Greeter = object : Greeter {
	override fun greet(): String = "hello from anon"
}
interface Op { fun apply(x: Int): Int }
fun adder(): Op = object : Op {
	override fun apply(x: Int): Int = x + 100
}
fun main() {
	println(make().greet())
	println(adder().apply(5))
}

// B: kotlin.math.* -> System.Math, kotlin.text String ops -> .NET String methods.
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.sqrt
fun main() {
	println(abs(-5))
	println(max(3, 8))
	println(sqrt(16.0))
	val s = "Hello, World"
	println(s.uppercase())
	println(s.substring(0, 5))
	println(s.replace("World", "CLR"))
	println(s.startsWith("Hello"))
}

fun main() {
	val g = Greeter("Visual Studio")
	println(g.greet())
	var total = 0
	var i = 1
	while (i <= 5) { total = total + i; i = i + 1 }
	println("sum 1..5 = $total")
}

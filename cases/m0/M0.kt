fun main() {
	println("sum = ${sum(2, 3)}")
	var i = 0
	while (i < 3) {
		println(fizz(i))
		i = i + 1
	}
}

fun sum(a: Int, b: Int): Int = a + b

fun fizz(n: Int): String = if (n == 0) "zero" else "n=$n"

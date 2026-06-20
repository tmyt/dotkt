// IL parity: user-defined operator overloading.
class Vec(val x: Int, val y: Int) {
	operator fun plus(o: Vec) = Vec(x + o.x, y + o.y)
	operator fun minus(o: Vec) = Vec(x - o.x, y - o.y)
	operator fun times(k: Int) = Vec(x * k, y * k)
	operator fun unaryMinus() = Vec(-x, -y)
	operator fun get(i: Int): Int = if (i == 0) x else y
	operator fun compareTo(o: Vec): Int = (x * x + y * y) - (o.x * o.x + o.y * o.y)
	operator fun contains(v: Int): Boolean = v == x || v == y
	operator fun invoke(): Int = x + y
	override fun toString(): String = "($x, $y)"
}

class Box(var v: Int) {
	operator fun get(i: Int): Int = v + i
	operator fun set(i: Int, value: Int) { v = value + i }
}

fun main() {
	val a = Vec(3, 4)
	val b = Vec(1, 2)
	println(a + b)
	println(a - b)
	println(a * 2)
	println(-a)
	println(a[0])
	println(a[1])
	println(a > b)
	println(b < a)
	println(2 in a)
	println(3 in a)
	println(a())

	val box = Box(0)
	box[5] = 10
	println(box[0])
}

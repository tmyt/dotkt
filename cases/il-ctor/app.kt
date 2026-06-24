// IL parity: secondary constructors + init blocks, delegating via this(...).
class Rect(val w: Int, val h: Int) {
	var area: Int = 0
	init { area = w * h }
	constructor(side: Int) : this(side, side)
}

class Labeled {
	val label: String
	val n: Int
	constructor(label: String, n: Int) {
		this.label = label
		this.n = n
	}
	constructor(label: String) : this(label, 0)
}

fun main() {
	val r = Rect(3, 4)
	println(r.area)
	val sq = Rect(5)
	println(sq.area)
	println("${sq.w}x${sq.h}")
	val a = Labeled("hi", 7)
	println("${a.label}=${a.n}")
	val b = Labeled("solo")
	println("${b.label}=${b.n}")
}

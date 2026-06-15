import clr.DotNetList

fun main() {
	val list = DotNetList<Int>()
	list.add(10)
	list.add(20)
	list.add(30)
	println("count = ${list.count}")
	println("first = ${list[0]}, last = ${list[2]}")

	list[1] = 99
	var sum = 0
	var i = 0
	while (i < list.count) {
		sum = sum + list[i]
		i = i + 1
	}
	println("sum after set = $sum")
}

private fun Collection<Int>.samePhysicalName(): Int = 1
private fun Collection<Int>?.samePhysicalName(): Int = 2

fun main() = println(listOf(1).samePhysicalName())

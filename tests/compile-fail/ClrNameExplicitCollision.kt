@kotlin.clr.ClrName("samePhysicalName")
private fun Collection<Int>.left(): Int = 1

@kotlin.clr.ClrName("samePhysicalName")
private fun Collection<Int>.right(): Int = 2

fun main() = println(listOf(1).left())

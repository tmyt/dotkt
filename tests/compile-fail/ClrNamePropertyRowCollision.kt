private val Collection<Int>.firstProperty: Int get() = 1

@get:kotlin.clr.ClrName("otherProperty")
private val Set<Int>.firstProperty: Int get() = 2

private val Collection<Int>.otherProperty: Int get() = 3

fun main() = println(listOf(1).firstProperty)

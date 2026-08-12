open class ClrNameOpenBase {
    @kotlin.clr.ClrName("renamed")
    open fun member(): Int = 1
}

fun main() = println(ClrNameOpenBase().member())

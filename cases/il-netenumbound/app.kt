import System.DayOfWeek
fun <T : Enum<T>> nameOf(e: T): String = e.name
fun main() {
    val d: DayOfWeek = DayOfWeek.Friday
    println(nameOf(d))                                 // Friday
    println(enumValues<DayOfWeek>().size)              // 7
    println(enumValueOf<DayOfWeek>("Monday").ordinal)  // 1
}

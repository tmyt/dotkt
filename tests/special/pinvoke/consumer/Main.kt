import kotlin.clr.byref

fun main() {
    check(add(20, 22) == 42)
    var value = 9
    increment(byref(value))
    check(value == 10)
    check(none(7) == 7)
    check(ansi(8) == 8)
    check(auto(9) == 9)
    println("P/Invoke direct + dll2klib round-trip: OK")
}

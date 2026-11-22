import kotlin.clr.byref

fun main() {
    check(add(20, 22) == 42)
    var value = 9
    increment(byref(value))
    check(value == 10)
    check(none(7) == 7)
    check(ansi(8) == 8)
    check(auto(9) == 9)
    check(observedLastError(1234) == 1234)
    check(modeEcho(Mode.One) == Mode.One)
    check(checkUtf16("日😀") == 1)
    check(checkUtf16(null) == -1)
    check(checkUtf8("日😀") == 1)
    println("P/Invoke direct + dll2klib round-trip: OK")
}

// >16-arg function values: System.Func/Action top out at 16 value parameters (Func`17 = 16 args +
// TResult), so ilemit synthesizes module-local delegate types KFunc`18 / KAction`17 for these
// shapes. This structural source drives the adjacent run.sh through the real pipeline
// (kotc -> bir2cir -> ilemit); the script additionally asserts the synthesized delegate type names
// exist in the dll and that facadegen restores `accept` with the full 17-arg Kotlin function type.
fun accept(cb: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int =
    cb(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)

fun main() {
    val f: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int =
        { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p17 }
    val a: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Unit =
        { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> println(p17) }
    println(f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
    a(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
    println(accept(f))
}

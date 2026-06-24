// Callable references -> a delegate. `::foo` binds a top-level (static) function; `obj::method` binds an
// instance method to its receiver.
fun isEven(n: Int): Boolean = n % 2 == 0
fun square(n: Int): Int = n * n
fun greet(name: String): String = "Hi, $name"

class Calc(val base: Int) {
    fun addTo(x: Int): Int = base + x
    open fun label(): String = "calc$base"
}
fun apply2(f: (Int) -> Int, v: Int): Int = f(v)
fun applyTo(f: (Calc, Int) -> Int, c: Calc, v: Int): Int = f(c, v)

fun main() {
    val xs = listOf(1, 2, 3, 4, 5, 6)
    println(xs.filter(::isEven).joinToString(","))   // 2,4,6
    println(xs.map(::square).joinToString(","))      // 1,4,9,16,25,36

    // A top-level reference stored in a function-typed val, then invoked.
    val f: (String) -> String = ::greet
    println(f("Kotlin"))                             // Hi, Kotlin

    // Bound instance references `obj::method`.
    val c = Calc(100)
    val bound = c::addTo
    println(bound(5))                                // 105
    println(apply2(c::addTo, 7))                     // 107
    val lbl: () -> String = c::label                 // bound ref to an open method (ldvirtftn)
    println(lbl())                                   // calc100

    // Unbound `Class::method` — the receiver becomes the first parameter.
    val unb = Calc::addTo                             // (Calc, Int) -> Int
    println(unb(Calc(200), 3))                        // 203
    println(applyTo(Calc::addTo, Calc(40), 2))        // 42
}

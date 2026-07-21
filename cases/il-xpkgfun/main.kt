import a.foo as aFoo
import b.foo as bFoo

fun main() {
    val x = aFoo()
    val y = bFoo()
    println("a.foo=$x b.foo=$y")
    if (x != 1 || y != 2) { println("FAIL: expected a.foo=1 b.foo=2, got a.foo=$x b.foo=$y"); return }
    println("PASS")
}

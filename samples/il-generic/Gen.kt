// G-1: user-defined generic class + generic function, emitted as real .NET generics.
class Box<T>(val value: T) {
    fun get(): T = value
}

fun <T> identity(x: T): T = x

class Pair2<A, B>(val first: A, val second: B) {
    fun firstOne(): A = first
    fun secondOne(): B = second
}

fun main() {
    val bi = Box(42)
    println(bi.get())          // 42
    println(bi.value)          // 42
    val bs = Box("hello")
    println(bs.get())          // hello

    println(identity(7))       // 7
    println(identity("world")) // world

    val p = Pair2(3, "three")
    println(p.firstOne())      // 3
    println(p.secondOne())     // three
}

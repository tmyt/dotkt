package mylib
class Box<T>(val v: T) { fun get(): T = v }
class Plain(val n: Int)
fun <T> boxed(x: T): Box<T> = Box(x)              // top-level GENERIC function (was dropped: no [KotlinFile])
fun plain(n: Int): Int = n                         // top-level function
infix fun Int.times2(o: Int): Int = this * o * 2   // top-level extension INFIX (needs [KotlinFunction] round-trip)

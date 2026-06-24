import mylib.Box
import mylib.Plain
import mylib.boxed
import mylib.plain
import mylib.times2
fun main() {
    println(Plain(7).n)        // 7   (class member)
    println(Box(5).get())      // 5   (generic class)
    println(boxed("hi").get()) // hi  (top-level generic fn restored from [KotlinFile])
    println(plain(3))          // 3   (top-level fn)
    println(4 times2 5)        // 40  (top-level extension infix restored from [KotlinFunction])
}

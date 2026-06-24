// The original motivation: inherit a GENERIC .NET base class, façade-free.
import System.Collections.ObjectModel.Collection

class IntColl : Collection<Int>() {
    fun addAll(vararg xs: Int) { for (x in xs) Add(x) }
}

fun main() {
    val c = IntColl()
    c.addAll(5, 7, 9)
    println(c.Count)        // 3
    println(c.Contains(7))  // True
    println(c.IndexOf(9))   // 2
}

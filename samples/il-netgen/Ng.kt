// Generic FIR injection: use a generic .NET type (System.Collections.ObjectModel.Collection<T>) façade-free.
import System.Collections.ObjectModel.Collection

fun main() {
    val c = Collection<Int>()
    c.Add(10)
    c.Add(20)
    c.Add(30)
    println(c.Count)        // 3
    println(c.Contains(20)) // True
    println(c.IndexOf(30))  // 2
}

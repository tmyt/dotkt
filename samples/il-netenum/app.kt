// `for (x in <.NET IEnumerable<T>>)`: a raw .NET enumerable (not a Kotlin collection). facadegen injects a
// frontend-only `operator fun iterator(): Iterator<T>` (so the for-loop resolves unambiguously); the backend
// bypasses it and enumerates via GetEnumerator/MoveNext/Current.
import Kfc.Nums
import Kfc.Words
fun main() {
    var sum = 0
    for (a in Nums()) { sum += a }
    println(sum)                 // 60
    var total = 0
    for (w in Words()) { total += w.length }
    println(total)               // 1+2+3 = 6
    for (w in Words()) { print(w) }
    println()                    // abbccc
}

// for-in over a real .NET IEnumerable<T> (here System.Collections.Generic.List<Int>) — GetEnumerator loop.
// The .NET type comes from facadegen's `import System.X` scan (no hand-written façade); `for (x in list)`
// is lowered by the reverse bridge to GetEnumerator/MoveNext/Current.
import System.Collections.Generic.List

fun main() {
    val l = List<Int>()
    l.Add(10); l.Add(20); l.Add(30)

    var sum = 0
    for (x in l) sum += x
    println(sum)            // 60

    var joined = ""
    for (x in l) joined += "$x,"
    println(joined)         // 10,20,30,
    println(l.Count)        // 3
}

// for-in over a real .NET IEnumerable<T> (here System.Collections.Generic.List<Int>) — GetEnumerator loop.
import clr.NetList

fun main() {
    val l = NetList<Int>()
    l.add(10); l.add(20); l.add(30)

    var sum = 0
    for (x in l) sum += x
    println(sum)            // 60

    var joined = ""
    for (x in l) joined += "$x,"
    println(joined)         // 10,20,30,
    println(l.count)        // 3
}

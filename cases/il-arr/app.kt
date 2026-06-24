// IL parity: arrays — factory, indexing get/set, .size, indexed + for-in iteration.
fun main() {
    val a = intArrayOf(10, 20, 30)
    println(a[0])
    println(a[2])
    a[1] = 99
    println(a[1])
    println(a.size)
    var sum = 0
    var i = 0
    while (i < a.size) { sum = sum + a[i]; i = i + 1 }
    println(sum)
    var fsum = 0
    for (x in a) fsum = fsum + x
    println(fsum)
}

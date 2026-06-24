// heap ref-cell: a captured-and-mutated outer `var` is promoted to a shared cell so the write is visible.
fun main() {
    var counter = 0
    val inc = { counter++ }                       // non-inline lambda mutates a captured var
    inc(); inc(); inc()
    println(counter)                              // 3

    var total = 0
    val adder = object { fun add(n: Int) { total += n } }   // object expression mutates a captured var
    adder.add(10); adder.add(20)
    println(total)                                // 30

    var log = ""
    class Logger { fun put(s: String) { log += s } }        // local class mutates a captured var
    val l = Logger()
    l.put("a"); l.put("b")
    println(log)                                  // ab

    var sum = 0
    listOf(1, 2, 3, 4).forEach { sum += it }      // (inline) forEach over the same cell
    println(sum)                                  // 10
}

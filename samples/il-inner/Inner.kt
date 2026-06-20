// inner class: captures the enclosing instance (this@Outer) and can read its members.
class Outer(val base: Int) {
    private val tag = "T"

    inner class Counter(val step: Int) {
        var n = 0
        fun tick(): Int {
            n = n + 1
            return base + n * step      // base is the outer's property
        }
        fun label(): String = tag + n   // tag is the outer's private property
    }

    fun newCounter(step: Int): Counter = Counter(step)
}

fun main() {
    val o = Outer(100)
    val c = o.newCounter(10)
    println(c.tick())   // 110
    println(c.tick())   // 120
    println(c.label())  // T2

    // Construct an inner instance directly off an Outer receiver.
    val d = Outer(0).Counter(5)
    println(d.tick())   // 5
}

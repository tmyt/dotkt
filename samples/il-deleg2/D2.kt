import kotlin.properties.Delegates

class C {
    var v: Int by Delegates.observable(0) { _, old, new -> println("$old -> $new") }
    var pos: Int by Delegates.vetoable(0) { _, _, n -> n >= 0 }
    var late: String by Delegates.notNull()
}

fun main() {
    val c = C()
    c.v = 1
    c.v = 2
    c.pos = 5
    c.pos = -3       // vetoed
    println(c.pos)   // 5
    c.late = "hi"
    println(c.late)  // hi
}

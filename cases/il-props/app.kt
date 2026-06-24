// Custom property accessors (get()/set() with the `field` backing identifier) + proper `lateinit` semantics.
class Box(v: Int) {
    var x: Int = v
        get() = field * 2
        set(value) { field = value + 1 }
    val doubled: Int get() = x + x          // computed property (no backing field)
}

class Svc { lateinit var name: String }

fun main() {
    val b = Box(10)
    println(b.x)                            // field 10, get *2 = 20
    b.x = 3                                 // set: field = 3+1 = 4
    println(b.x)                            // get: 4*2 = 8
    println(b.doubled)                      // x + x = 16

    val s = Svc()
    try { println(s.name) } catch (e: Exception) { println("not initialized") }  // lateinit access throws
    s.name = "ready"
    println(s.name)                         // ready
}

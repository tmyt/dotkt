// #68: a function-local class / object expression that WRITES a captured outer `var` shares a heap ref-cell with the
// enclosing frame (the same box mechanism the closure path uses) — so the write is visible after the class/object
// runs. `computeRefCells` promotes the mutated captured `var` to a `dotkt$Ref<T>` BEFORE the lift. Was a whole-compile
// abort for the mutating (write-through) capture; reading such a capture already worked.
fun counterViaClass(): Int {
    var n = 0
    class Bump { fun go() { n++ } }
    val b = Bump()
    b.go(); b.go(); b.go()
    return n
}

fun counterViaObject(): Int {
    var m = 10
    val o = object { fun go() { m += 5 } }
    o.go(); o.go()
    return m
}

fun main() {
    println(counterViaClass())        // 3
    println(counterViaObject())       // 20
}

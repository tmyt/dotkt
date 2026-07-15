// #31 (kotc): an EXPRESSION-position `return unitFn()` in an inline fn body must EVALUATE the Unit-typed call for its
// side effect. The old expr-position IrReturn arm hit `isUnit -> {"k":"returnExpr"}` and DROPPED the call (a silent
// miscompile). Covers the elvis-RHS and if-as-value expression-position return shapes.
var counter = 0
fun bump() { counter++ }

inline fun elvisUnit(input: String?, block: () -> Unit) {
    val x: String = input ?: return bump()    // expression-position elvis RHS, Unit-typed return
    block()
    println("elvis-body $x")
}

inline fun ifUnit(c: Boolean, block: () -> Unit) {
    val x: Int = if (c) 1 else return bump()  // expression-position if-as-value, Unit-typed return
    block()
    println("if-body $x")
}

fun main() {
    elvisUnit(null) { println("blk") }    // early -> bump(); counter 0->1
    println("counter=$counter")
    elvisUnit("hi") { println("blk2") }   // fall-through
    println("counter=$counter")
    ifUnit(false) { println("blk3") }     // early -> bump(); counter 1->2
    println("counter=$counter")
    ifUnit(true) { println("blk4") }      // fall-through
    println("counter=$counter")
}

// P2-1: receiver lambdas (Scope.() -> Unit) + nested receiver lambdas + capture across them — the Compose-style
// `Column { Text(); Row { ... } }` DSL shape. The implicit receiver ($this$column/$this$row) and the captured
// outer `prefix` must all resolve.
class Col {
    var s = ""
    fun text(t: String) { s = s + t }
    fun row(block: Col.() -> Unit) { val c = Col(); c.block(); s = s + "[" + c.s + "]" }
}
fun column(block: Col.() -> Unit): Col { val c = Col(); c.block(); return c }

fun main() {
    val prefix = "P"
    val r = column {
        text("a")
        row {
            text(prefix)   // captures the outer `prefix` across a nested receiver lambda
            text("b")
        }
        text("c")
    }
    println(r.s)           // a[Pb]c
}

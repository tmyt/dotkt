// Expression-body functions whose body expression is Unit-typed (a side-effecting call). The IrReturn(<Unit expr>)
// must EVALUATE the expression then return — previously it emitted a bare `return`, silently dropping the call
// (so `fun main() = winUiApp { … }` launched nothing).
fun app(block: () -> Unit) { block() }
fun cleanup() { println("cleanup") }
fun greet() = println("greet")                 // expr-body, direct Unit call
fun viaLambda() = app { println("viaLambda") } // expr-body, Unit call taking a lambda
fun cond(x: Int) { if (x < 0) return cleanup(); println("pos") }  // explicit `return <Unit expr>`
fun main() = run {
    greet()
    viaLambda()
    cond(-1)
    cond(1)
}
fun run(block: () -> Unit) = block()

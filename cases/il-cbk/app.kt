// P1-3: a lambda binds to a .NET delegate parameter (custom delegate + BCL Action), façade-free.
import P.Engine

fun main() {
    val e = Engine()
    println(e.Apply(21) { x -> "v" + (x * 2) })   // =v42 — lambda -> custom delegate Transform
    e.Run { println("ran") }                       // ran  — lambda -> System.Action
}

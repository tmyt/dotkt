// `x::class` (runtime class of an instance) -> x.GetType(); `.simpleName` -> Type.Name.
// (Types whose CLR name matches Kotlin's simpleName are used, so the JVM differential agrees.)
class Widget

fun describe(x: Any): String = x::class.simpleName ?: "?"

fun main() {
    println("hi"::class.simpleName)        // String
    val w = Widget()
    println(w::class.simpleName)           // Widget
    println(describe(w))                    // Widget  (w passed as Any, runtime class recovered)
    println(describe("text"))              // String
}

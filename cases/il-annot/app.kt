// User-defined annotations are emitted as .NET custom attributes (: System.Attribute) and applied on the targets,
// so reflection / reverse interop sees them. (Visibility verified via C# reflection: [Tag("entity",(Int32)3)].)
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION)
annotation class Tag(val name: String, val level: Int, val active: Boolean)

@Tag("entity", 3, true)
class Widget(val id: Int) { fun show() = "widget#$id" }

@Tag("helper", 1, false)
fun helper(n: Int) = n * 2

fun main() {
    println(Widget(7).show())   // widget#7
    println(helper(21))         // 42
}

// #184: a .NET attribute whose ONLY constructor is `params object[]` can be applied bare (zero args) from Kotlin.
// The vararg parameter must be surfaced as vararg in the injected annotation class, not as a required argument.
import P.TagAttribute

@TagAttribute
class Widget(val id: Int) { fun show() = "widget#$id" }

@TagAttribute("helper", 1)
fun helper(n: Int) = n * 2

fun main() {
    println(Widget(7).show())   // widget#7
    println(helper(21))         // 42
}

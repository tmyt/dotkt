// Reverse attribute interop: an existing .NET attribute (P.LabelAttribute, deriving from System.Attribute) is
// surfaced as a Kotlin annotation and applied on Kotlin declarations; the backend re-applies the real .NET
// attribute via SetCustomAttribute (verified by reflection: [P.LabelAttribute("entity",(Int32)5)]). (#54)
import P.LabelAttribute

@LabelAttribute("entity", 5)
class Widget(val id: Int) { fun show() = "widget#$id" }

@LabelAttribute("helper", 1)
fun helper(n: Int) = n * 2

fun main() {
    println(Widget(7).show())   // widget#7
    println(helper(21))         // 42
}

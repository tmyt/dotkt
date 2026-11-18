package fixture

import InheritedStatics.Leaf
import InheritedStatics.Middle

fun original(): String = Leaf.Method()
fun replacement(): String = Middle.Method()
fun main() {
    check(pluginprobe.api.render(content = { "source" }) == 73)
    check(pluginprobe.api.render(content = 19) == 19)
    check(pluginprobe.api.Scope<Int>().render(3) { it.toString() } == 73)
    check(construct()().value == "closed")
    check(constructLocal(41) == "local")
    check(constructInBody<Int>()())
    check(original() == "middle method")
    println(answer())
}

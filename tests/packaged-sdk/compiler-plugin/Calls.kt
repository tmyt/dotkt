package fixture

import InheritedStatics.Leaf
import InheritedStatics.Middle
import pluginprobe.api.Tag
import pluginprobe.api.marked

fun original(): String = Leaf.Method()
fun replacement(): String = Middle.Method()
fun main() {
    check(pluginprobe.api.render(content = { "source" }) == 73)
    check(pluginprobe.api.render(content = 19) == 19)
    check(Tag.marked { 1 } == 73)
    check(pluginprobe.api.Scope.Nested<Int>().render(3) { it.toString() } == 73)
    check(construct()().value == "closed")
    check(constructLocal(41) == "local")
    check(constructInBody<Int>()())
    check(original() == "middle method")
    println(answer())
}

package fixture

import InheritedStatics.Leaf
import InheritedStatics.Middle
import pluginprobe.api.Tag
import pluginprobe.api.marked
import kotlin.clr.byref

fun original(): String = Leaf.Method()
fun replacement(): String = Middle.Method()
private fun compositeDiscard(value: Int): Any = value
private fun observe(value: Int) { check(value == 0) }
private fun withCompositeLocal() {
    var slot = pluginprobe.api.CompositeBox("initial")
    observe(0)
    check(FixtureNative.CompositeRef.Replace(byref(slot), byref(slot)))
    check(slot.value == "updated")
}
fun main() {
    withCompositeLocal()
    var evaluations = 0
    check(compositeDiscard(++evaluations) === Unit && evaluations == 1)
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

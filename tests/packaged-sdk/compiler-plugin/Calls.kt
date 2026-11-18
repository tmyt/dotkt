package fixture

import InheritedStatics.Leaf
import InheritedStatics.Middle

fun original(): String = Leaf.Method()
fun replacement(): String = Middle.Method()
fun main() {
    check(construct()().value == "closed")
    check(constructLocal(41) == "local")
    check(constructInBody<Int>()())
    check(original() == "middle method")
    println(answer())
}

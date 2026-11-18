package fixture

import InheritedStatics.Leaf
import InheritedStatics.Middle

fun original(): String = Leaf.Method()
fun replacement(): String = Middle.Method()
fun main() {
    check(original() == "middle method")
    println(answer())
}

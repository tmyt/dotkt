import roundtrip.dataidentity.*

var reads = 0
fun nextPoint(): Point { reads++; return Point(8) }

fun main() {
    val point = Point()
    point.bodyState = 99
    val changed = point.copy(label = "changed")
    check(changed.x == 3 && changed.label == "changed" && changed.bodyState == 17)
    val (x, label) = changed
    check(x == 3 && label == "changed")
    check(Point("four") == Point(4, "four"))
    check(point.copy("x") == 7)
    check(nextPoint().copy(label = "once").x == 8 && reads == 1)
    val generic = Box(23).copy(tag = "changed")
    check(generic.value == 23 && generic.tag == "changed")
    check(Hidden.make(6).copy().value == 6)
    check(Secret(12).copy().reveal() == 12)
    check(FieldData(14).copy().value == 14)
    check(Named(16).copy().value == 16)
    check(Container.Nested(5).copy().value == 5)
    check(Token.toString() == "Token")
    check(Ordinary(9).copy().component1() == 9)
    check(Pair(4, "x").copy(second = "y") == Pair(4, "y"))
    println("data identity roundtrip: OK")
}

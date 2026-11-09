import wrapper.producer.WireValue

fun main() {
    val values = WireValue.values()
    check(values.size == 2)
    check(values[0].code == 7 && values[0].label() == "first")
    check(values[1].code == 11 && values[1].label() == "second")
    println("wrapper-enum-ok")
}

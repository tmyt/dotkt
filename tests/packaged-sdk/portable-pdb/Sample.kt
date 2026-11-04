fun main() {
    val input = "41".toInt()
    val result = input + 1
    println(result)
}

fun documents(): Int {
    val seed = 1
    val result = withSeed { n -> n + seed }
    return result
}

fun controlFlow(): Int {
    var n = 0
    while (n < 3) {
        n++
    }
    return n
}

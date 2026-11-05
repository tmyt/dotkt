fun documents(): Int {
    val seed = 1
    val result = withSeed { n ->
        n + seed
    }
    return result
}

fun controlFlow(): Int {
    var n = 0
    while (n < 3) {
        n++
    }
    return n
}

fun branch(flag: Boolean): Int = if (flag)
    17
else
    23

class SourceInitializer {
    val value = "42".toInt()
}

val topLevel = "42".toInt()

fun exerciseSourceCases() {
    check(documents() == 42 && controlFlow() == 3 && branch(true) == 17 && branch(false) == 23)
    check(SourceInitializer().value == 42 && topLevel == 42 && loweredEvaluations() == 42 && supplied() == 42)
}

fun loweredEvaluations(): Int {
    var result = 0
    when (readNumber()) {
        21 -> result = 20
        else -> result = 1
    }
    val (left, right) = makePair()
    nullableString()?.toString()
    return result + left + right
}

private fun readNumber() = 21
private fun makePair() = Pair(20, 2)
private fun nullableString(): String? = "value"

fun supplied(value: Int = if (readNumber() > 0) 42 else 0): Int = value

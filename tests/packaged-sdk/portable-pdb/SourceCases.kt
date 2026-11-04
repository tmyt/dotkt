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
    check(SourceInitializer().value == 42 && topLevel == 42)
}

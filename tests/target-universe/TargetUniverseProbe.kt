import System.Collections.Generic.List

enum class TargetEnum { FIRST, SECOND }

class TargetList<T> : List<T>()

class TargetResource : AutoCloseable {
    override fun close() {}
}

open class TargetBase(val number: Int)
class TargetDerived(number: Int) : TargetBase(number)

fun primitive(value: Int): Int = value

private fun six(a: String, b: String, c: String, d: String, e: String, f: String): Int =
    a.length + b.length + c.length + d.length + e.length + f.length

fun trimmedBranchDepth(text: String?, early: Boolean): Int {
    val kept = text ?: if (early) return 11 else return 13
    return six(kept, kept, kept, kept, kept, kept)
}

fun nullableShape(values: List<String?>): String? = values[0]

fun carry(
    values: List<String>,
    transform: (String) -> String,
): List<String> {
    transform("target")
    return values
}

fun main() {
    val values = TargetList<String>()
    if (TargetEnum.FIRST.ordinal != 0 || values.Count != 0) error("target shape")
    TargetResource().close()
    println("target-universe")
}

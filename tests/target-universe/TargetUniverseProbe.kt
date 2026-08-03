import System.Collections.Generic.List

enum class TargetEnum { FIRST, SECOND }

class TargetList<T> : List<T>()

class TargetResource : AutoCloseable {
    override fun close() {}
}

open class TargetBase(val number: Int)
class TargetDerived(number: Int) : TargetBase(number)

fun primitive(value: Int): Int = value

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

import System.Collections.Generic.List

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
    TargetResource().close()
    println("target-universe")
}

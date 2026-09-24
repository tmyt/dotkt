package roundtrip.heterogeneousidentity

open class Node {
    override fun equals(other: Any?): Boolean = other is Node
    override fun hashCode(): Int = 1
}
class Child : Node()

fun <T, U : T> pairSame(a: T, b: U): Boolean = a === b
fun <T, U : T> pairReversed(a: T, b: U): Boolean = b === a
fun <T, U : T> pairDifferent(a: T, b: U): Boolean = a !== b
fun <T : Node> boundSame(a: T, b: Node): Boolean = a === b
fun <T : Node> boundReversed(a: T, b: Node): Boolean = b === a
fun nullablePrimitive(a: Any?, b: Int?): Boolean = a === b
fun nullableReversed(a: Int?, b: Any?): Boolean = a === b
fun <T> homogeneous(a: T, b: T): Boolean = a === b
inline fun <T, U : T> inlinePair(a: T, b: U): Boolean = a === b
inline fun <T, U : T> splicedPair(a: T, b: U, before: () -> Unit): Boolean {
    before()
    return a === b
}
class PairHolder<T, U : T>(val left: T, val right: U) {
    fun same(): Boolean = left === right
    fun reversed(): Boolean = right === left
}

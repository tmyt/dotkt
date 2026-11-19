import EqualityBoundary.*

fun narrowed(left: Left, other: Any): Int = if (left == other) other.Number else 0
fun branch(left: Left?, other: Any?): Int = when (left) { other -> other.BranchNumber; else -> 0 }
fun ambiguous(left: Left, right: Right): Boolean = left == right
fun inaccessible(left: Left): Int = left and null
fun incompatible(order: Order): Int = order.compareTo(null)

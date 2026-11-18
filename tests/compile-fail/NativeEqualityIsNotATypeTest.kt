import EqualityBoundary.Left
import EqualityBoundary.Right

fun narrowed(left: Left, other: Any): Int = if (left == other) other.Number else 0
fun ambiguous(left: Left, right: Right): Boolean = left == right

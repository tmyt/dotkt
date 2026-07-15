// GitHub #28: a nullable generic element is represented as object at the declaration boundary.
// Calls on the returned collection must use that same erased interface instantiation; otherwise
// IReadOnlyCollection<T>.Count / IEnumerable<T>.GetEnumerator have no entry point on List<object>.
fun <T> boxes(x: T): List<T?> = listOf(x, null)
fun <T> plainBoxes(x: T): List<T> = listOf(x)

fun main() {
    val strings = boxes("a")
    println(strings.size)
    println(strings[0])
    for (value in strings) println(value)

    val ints = boxes(7)
    println(ints.size)
    println(ints[0])
    for (value in ints) println(value)

    // Controls: neither a plain generic element nor a concrete nullable element needs object erasure.
    println(plainBoxes("b").size)
    println(listOf<String?>("c", null).size)
}

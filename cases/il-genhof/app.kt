// A generic function iterating List<T> and applying a (T) -> Unit lambda. Regressed: the for-loop lowering
// (forEachInline) resolved GetEnumerator/get_Current on IEnumerable<T>/IEnumerator<T> via plain .GetMethod, but
// when T is the enclosing function's type parameter those are TypeBuilderInstantiations -> NotSupportedException.
fun <T> each(xs: List<T>, f: (T) -> Unit) { for (x in xs) f(x) }
fun main() { each(listOf(1, 2, 3)) { println(it) } }

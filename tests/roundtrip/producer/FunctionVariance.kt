package roundtrip.functionvariance

fun increment(): (Int) -> Int = { it + 1 }
fun <T, R> keep(callback: (T) -> R): (T) -> R = callback
class FunctionHolder(val callback: (Int) -> Int)
fun invokeDefault(value: Int, callback: (Int) -> Int = increment()): Int = callback(value)

fun optional(present: Boolean): ((String?) -> String?)? = if (present) ({ it }) else null
fun nested(): (String?) -> ((Int) -> String?) = { text -> { count -> if (count > 0) text else null } }
fun extension(): String?.(Int) -> String? = { count -> if (count > 0) this else null }
class Context(val text: String?)
fun contextual(): context(Context) (Int) -> String? = { count -> if (count > 0) contextOf<Context>().text else null }
class Box<T>(val value: T)
fun boxed(): Box<(Int) -> Int> = Box(increment())
inline fun inlineIncrement(value: Int, callback: (Int) -> Int = { it + 1 }): Int = callback(value)

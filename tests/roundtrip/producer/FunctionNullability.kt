package roundtrip.functionnullability

fun predicate(): (Any?) -> Boolean = { it == null }
fun identity(): (Any?) -> Any? = { it }
fun optional(present: Boolean): ((String?) -> String?)? = if (present) ({ it }) else null
fun nested(): (String?) -> ((Any?) -> String?) = { text -> { value -> if (value == null) text else value.toString() } }
fun <T> genericIdentity(): (T?) -> T? = { it }
fun extension(): String?.(Any?) -> String? = { if (it == null) this else it.toString() }
class Context(val text: String?)
fun contextual(): context(Context) (Any?) -> String? = { if (it == null) contextOf<Context>().text else it.toString() }
class Holder<T>(val value: T)
fun boxed(): Holder<(Any?) -> Any?> = Holder { it }
class Callbacks(var callback: (Any?) -> Any?, val optional: ((String?) -> String?)?)
class GenericCallbacks<T>(var callback: (T?) -> T?)

fun verifyLocalFunctionNullability() {
    check(predicate()(null))
    check(identity()(null) == null)
    check(optional(false) == null)
    check(optional(true)!!(null) == null)
    check(nested()(null)(null) == null)
    check(genericIdentity<String>()(null) == null)
    check(extension()(null, null) == null)
    with(Context(null)) { check(contextual()(null) == null) }
    check(boxed().value(null) == null)
    check(Callbacks({ it }, { it }).callback(null) == null)
    check(GenericCallbacks<String> { it }.callback(null) == null)
}

package roundtrip.delegateargumentconversion

enum class Marker { Value }
class Token
value class NumberValue(val raw: Int)

fun objectPredicate(): (Any?) -> Boolean = { it is Int }
fun objectIdentity(): (Any?) -> Any? = { it }
fun referenceIdentity(): (Token) -> Token = { it }
fun intIdentity(): (Int) -> Int = { it }
fun extensionPredicate(): Any?.(Any?) -> Boolean = { this is Int && it is String }
fun <T> throughObject(value: T): Any? = objectIdentity()(value)
fun <T : Any> throughTyped(value: T?, callback: (T) -> T): T = callback(value!!)
fun nullableIntIdentity(): (Int?) -> Int? = { it }
fun zeroArguments(): () -> Int = { 51 }
fun contextIdentity(): context(Token) (Any?) -> Any? = { it }

private var invocationTrace = ""
private fun orderedFactory(): (Any?, Any?) -> Boolean {
    invocationTrace += "receiver;"
    return { a, b -> invocationTrace += "invoke;"; a is Int && b is Long }
}
private fun firstArgument(): Int { invocationTrace += "first;"; return 42 }
private fun secondArgument(): Long { invocationTrace += "second;"; return 43L }

fun verifyLocalDelegateArguments() {
    check(objectPredicate()(42))
    check(!objectPredicate()("not an int"))
    check(!objectPredicate()(null))
    check(objectIdentity()(42) as Int == 42)
    check(objectIdentity()(42L) as Long == 42L)
    check(objectIdentity()(true) as Boolean)
    val present: Int? = 7
    val absent: Int? = null
    check(objectIdentity()(present) as Int == 7)
    check(objectIdentity()(absent) == null)
    check(objectIdentity()(Marker.Value) == Marker.Value)
    val token = Token()
    check(objectIdentity()(token) === token)
    check(referenceIdentity()(token) === token)
    check(intIdentity()(17) == 17)
    check(nullableIntIdentity()(17) == 17)
    check(nullableIntIdentity()(null) == null)
    check(zeroArguments()() == 51)
    check(throughObject(23) as Int == 23)
    check(throughObject(token) === token)
    check(throughTyped(23, intIdentity()) == 23)
    check(throughTyped(token, referenceIdentity()) === token)
    val boxed: Any = 42
    check(objectIdentity()(boxed) === boxed)
    check((objectIdentity()(NumberValue(9)) as NumberValue).raw == 9)
    with(token) { check(contextIdentity()(42) as Int == 42) }
    invocationTrace = ""
    check(orderedFactory()(firstArgument(), secondArgument()))
    check(invocationTrace == "receiver;first;second;invoke;")
    check(extensionPredicate()(42, "value"))
    var observed: Any? = null
    val action: (Any?) -> Unit = { observed = it }
    action(29)
    check(observed as Int == 29)
    action(null)
    check(observed == null)
}

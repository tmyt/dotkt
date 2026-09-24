package roundtrip.delegateargumentconversion

enum class Marker { Value }
class Token

fun objectPredicate(): (Any?) -> Boolean = { it is Int }
fun objectIdentity(): (Any?) -> Any? = { it }
fun referenceIdentity(): (Token) -> Token = { it }
fun intIdentity(): (Int) -> Int = { it }
fun extensionPredicate(): Any?.(Any?) -> Boolean = { this is Int && it is String }
fun <T> throughObject(value: T): Any? = objectIdentity()(value)

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
    check(throughObject(23) as Int == 23)
    check(throughObject(token) === token)
    check(extensionPredicate()(42, "value"))
    var observed: Any? = null
    val action: (Any?) -> Unit = { observed = it }
    action(29)
    check(observed as Int == 29)
    action(null)
    check(observed == null)
}

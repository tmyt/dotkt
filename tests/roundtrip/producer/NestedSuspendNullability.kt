package roundtrip.nestedsuspendnullability

class InvariantBox<T>(val value: T)
class TwoSlots<A, B>(val first: A, val second: B)

suspend fun nullableBox(): InvariantBox<String?>? = InvariantBox(null)
suspend fun nonNullBox(): InvariantBox<String?> = InvariantBox(null)
suspend fun nullableArray(): Array<String?>? = arrayOf(null, "value")
suspend fun nestedArray(): InvariantBox<Array<String?>?> = InvariantBox(arrayOf(null))
suspend fun nestedUnit(): InvariantBox<InvariantBox<Unit?>?> = InvariantBox(InvariantBox(null))
suspend fun nonNullControl(): InvariantBox<String> = InvariantBox("value")
fun ordinaryBox(): InvariantBox<String?> = InvariantBox(null)
fun ordinaryFunction(): ((String?) -> String?)? = null
fun ordinaryUnitBox(): InvariantBox<Unit?>? = null

suspend fun valueSegment(): System.ArraySegment<String?> = System.ArraySegment<String?>(arrayOf(null))
suspend fun functionResult(): ((String?) -> String?)? = { it }
suspend fun actionResult(): ((String?) -> Unit)? = { }
suspend fun receiverResult(): (String?.(String) -> String?)? = { this }
suspend fun unitFunctionResult(): (() -> Unit?)? = { null }
suspend fun suspendFunctionResult(): (suspend () -> String?)? = null
suspend fun collapsedResult(): TwoSlots<Pair<String, String>?, String?> = TwoSlots(null, null)
suspend fun lateCollapsedResult(): TwoSlots<Comparable<Any?>?, String> = TwoSlots(null, "value")
suspend fun lateCollapsedNullableResult(): TwoSlots<Comparable<Any?>?, String?> = TwoSlots(null, null)
suspend fun starComparableResult(): TwoSlots<Comparable<*>?, String> = TwoSlots(null, "value")
suspend fun enumResult(): TwoSlots<Enum<*>?, String> = TwoSlots(null, "value")
suspend fun primitiveResult(): TwoSlots<Int, String?> = TwoSlots(1, null)

interface NestedDefault {
    suspend fun read(): InvariantBox<String?>? = InvariantBox(null)
}
interface NestedAbstract {
    suspend fun read(): InvariantBox<Array<String?>?>
}

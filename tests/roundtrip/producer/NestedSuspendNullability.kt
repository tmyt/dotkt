package roundtrip.nestedsuspendnullability

class InvariantBox<T>(val value: T)

suspend fun nullableBox(): InvariantBox<String?>? = InvariantBox(null)
suspend fun nonNullBox(): InvariantBox<String?> = InvariantBox(null)
suspend fun nullableArray(): Array<String?>? = arrayOf(null, "value")
suspend fun nestedArray(): InvariantBox<Array<String?>?> = InvariantBox(arrayOf(null))
suspend fun nestedUnit(): InvariantBox<InvariantBox<Unit?>?> = InvariantBox(InvariantBox(null))
suspend fun nonNullControl(): InvariantBox<String> = InvariantBox("value")
fun ordinaryBox(): InvariantBox<String?> = InvariantBox(null)

interface NestedDefault {
    suspend fun read(): InvariantBox<String?>? = InvariantBox(null)
}
interface NestedAbstract {
    suspend fun read(): InvariantBox<Array<String?>?>
}

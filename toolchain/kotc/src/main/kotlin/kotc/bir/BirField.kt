package kotc.bir

// Presence is not nullability: an uninitialized local may carry explicit null,
// while another producer has not authored its initializer field at all.
sealed interface BirField<out T> {
    data object Absent : BirField<Nothing>
    data object Null : BirField<Nothing>
    data class Value<T>(val value: T) : BirField<T>
}

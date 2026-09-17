package roundtrip.nullablesam

fun interface Slot<T> { fun inspect(value: T): Boolean }
fun interface NullableSlot<T> { fun inspect(value: T?): Boolean }
fun interface Supplier<T> { fun get(): T }

fun <T> nullableSam(): Slot<T?> = Slot { it == null }
fun <T> declaredNullableSam(): NullableSlot<T> = NullableSlot { it == null }
fun <T> nullableSupplier(value: T?): Supplier<T?> = Supplier { value }

class AuthoredNullableSlot<T> : Slot<T?> {
    override fun inspect(value: T?): Boolean = value == null
}

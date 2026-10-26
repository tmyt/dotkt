package roundtrip.invariantstorage

import kotlin.clr.ClrField
import kotlin.clr.ClrRef
import kotlin.clr.byref
import roundtrip.genericvalueinterop.Box

interface StoredView<T> { fun code(value: T): Int }

object UniversalStoredView : StoredView<Any> {
    override fun code(value: Any): Int = 7
}

@Suppress("UNCHECKED_CAST")
fun <T> storedView(): StoredView<T> = UniversalStoredView as StoredView<T>

class StoredHolder<T>(val view: StoredView<T>)
class StoredReferenceField { val view: StoredView<String> = storedView() }

fun storedSupplier(): () -> StoredView<Int> = { storedView() }
fun storedList(): List<StoredView<Int>> = listOf(storedView())
fun storedPair(): Pair<StoredView<Int>, Int> = storedView<Int>() to 1

fun replaceProjectedSlot(slot: ClrRef<Box<*>>, replacement: Box<*>) { slot.value = replacement }

class ProjectedReferenceField {
    var value: Box<*> = Box(1)
    fun replace(replacement: Box<*>) { replaceProjectedSlot(byref(value), replacement) }
}

class StoredVariant<out T>(val value: T)
fun replaceVariant(slot: ClrRef<StoredVariant<Any>>, replacement: StoredVariant<Int>) { slot.value = replacement }
fun replaceSourceList(slot: ClrRef<List<Any>>, replacement: List<Int>) { slot.value = replacement }
fun replaceVariantArray(slot: ClrRef<Array<StoredVariant<String>>>, replacement: Array<StoredVariant<String>>) {
    slot.value = replacement
}
fun <T> touchReference(slot: ClrRef<T>) { slot.value = slot.value }

class NativeLocationField(initial: Box<String>) {
    @ClrField var value: Box<String> = initial
}

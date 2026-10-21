package fieldprojection

import kotlin.clr.ClrField

open class FieldBox<T>(seed: T) { @ClrField var value: T = seed }
class FieldMiddle<A, B>(seed: B) : FieldBox<B>(seed)
class ReadonlyFieldBox<T>(seed: T) { @ClrField val value: T = seed }
class CovariantFieldBox<out T>(seed: T) { @ClrField val value: T = seed }
class RestrictedFieldBox<T>(seed: T) {
    @ClrField var value: T = seed
        internal set
}

fun readField(box: FieldBox<*>): Any? = box.value
fun stringFieldBox(): Any = FieldBox("native")
fun intFieldBox(): Any = FieldBox(42)
fun nullableFieldBox(): Any = FieldBox<Int?>(null)
fun writeRestrictedField(box: RestrictedFieldBox<in String>, value: String) { box.value = value }

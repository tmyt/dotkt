// ktproj-genq (#18/#147): generic types re-imported cross-module. Nested `Nullable(Tv)` declaration-slot shapes that
// bir2cir's NullableGenericErasure object-erases to `<object>` and that dll2klib would otherwise degrade to `Any?`
// (hiding members and breaking generic inference):
//   * the FACTORY `holderOf(): Holder<T?>`  — a top-level generic function: `tv(method, 0)`.
//   * the MEMBER `Holder<T>.cell(): Ref<T?>` — a method of a generic class over the class's OWN type param:
//     `tv(type, 0)` — the exact `AtomicArray<T>.get(): AtomicRef<T?>` shape on the kotlinx.coroutines path.
//   * #147's parameter / constructor / property / raw-field slots below.
// [KotlinNullableGeneric] records each pre-erasure slot so dll2klib restores its Kotlin shape.
package genq

// `Slot`/`Vault` keep this fixture readable. The same-simple-name carrier case is tested separately with two namespaces
// in roundtrip-nullable-generic-slots, so this producer test stays focused on the declaration-slot/runtime surface.
class Slot<T>(val value: T)

class Vault<T>(val size: Int, private val fill: T) {
    operator fun get(index: Int): T = fill
    fun cell(): Slot<T?> = Slot(null)
}

fun <T> holderOf(n: Int): Vault<T?> = Vault(n, null)

annotation class ClrField

class GenericSlots<T>(initial: Slot<T?>) {
    @ClrField val fieldSlot: Slot<T?> = initial
    val propertySlot: Slot<T?> get() = fieldSlot
}

fun <T> unwrapSlot(slot: Slot<T?>): T? = slot.value

class FunctionSlots<T>(initial: (T?) -> String) {
    @ClrField val functionField: (T?) -> String = initial
    val functionProperty: (T?) -> String get() = functionField
}

fun <T> invokeNullable(block: (T?) -> String): String = block(null)
fun <T> invokeNullableValue(value: T?, block: (T?) -> String): String = block(value)
fun renderNullableInt(value: Int?): String = "top=${value ?: -1}"
class NullableIntRenderer(private val prefix: String) {
    fun render(value: Int?): String = "$prefix=${value ?: -1}"
}

// #147 late-synthesis regression: this hierarchy makes bir2cir materialize a public forwarding slot on SlotDerived.
// The bridge is created after nullable-generic erasure and must inherit the interface parameter's pre-erasure carrier.
interface SlotConsumer<T> { fun accept(slot: Slot<T?>): String }
open class SlotBase<T> { fun accept(slot: Slot<T?>): String = slot.value?.toString() ?: "bridge-null" }
class SlotDerived<T> : SlotBase<T>(), SlotConsumer<T>

// The consumer reaches this declaration through InheritedNullableMiddle<T>. The round trip keeps the nullable generic
// slot callable through that intermediate base; the separate lowering fixture pins owner/declarer edge projection.
abstract class InheritedNullableBase<T> { abstract fun take(value: T?): String }
abstract class InheritedNullableMiddle<T> : InheritedNullableBase<T>()

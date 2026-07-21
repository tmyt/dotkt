// ktproj-genq (#18): generic types re-imported cross-module. TWO nested-`Nullable(Tv)` return shapes that bir2cir's
// NullableGenericReturnErasure object-erases to `<object>` and that facadegen would otherwise degrade to `Any?`
// (hiding every member of the generic result):
//   * the FACTORY `holderOf(): Holder<T?>`  — a top-level generic function: `tv(method, 0)`.
//   * the MEMBER `Holder<T>.cell(): Ref<T?>` — a method of a generic class over the class's OWN type param:
//     `tv(type, 0)` — the exact `AtomicArray<T>.get(): AtomicRef<T?>` shape on the kotlinx.coroutines path.
// The [KotlinNullableGeneric] round-trip attribute records the pre-erasure node so facadegen restores `Holder<T?>` /
// `Ref<T?>` instead of collapsing to `Any?`.
package genq

// `Slot`/`Vault` (not `Ref`/`Holder`) so the simple names are UNIQUE across this shared producer assembly — a
// same-simple-name collision with another package's generic type broke facadegen's restoration of the
// nested-nullable-generic factory return (`holderOf(): Vault<T?>` degraded to `Any?`). The case tests the
// [KotlinNullableGeneric] round-trip (#18), not the names.
class Slot<T>(val value: T)

class Vault<T>(val size: Int, private val fill: T) {
    operator fun get(index: Int): T = fill
    fun cell(): Slot<T?> = Slot(null)
}

fun <T> holderOf(n: Int): Vault<T?> = Vault(n, null)

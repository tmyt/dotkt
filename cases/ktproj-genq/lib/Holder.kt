// #18: generic types re-imported cross-module through a <ProjectReference>. TWO nested-`Nullable(Tv)` return shapes
// that bir2cir's NullableGenericReturnErasure object-erases to `<object>` and that facadegen would otherwise degrade
// to `Any?` (hiding every member of the generic result):
//   * the FACTORY `holderOf(): Holder<T?>`  — a top-level generic function: `tv(method, 0)`.
//   * the MEMBER `Holder<T>.cell(): Ref<T?>` — a method of a generic class over the class's OWN type param:
//     `tv(type, 0)` — the exact `AtomicArray<T>.get(): AtomicRef<T?>` shape on the kotlinx.coroutines critical path.
// The [KotlinNullableGeneric] round-trip attribute records the pre-erasure node so facadegen restores `Holder<T?>` /
// `Ref<T?>` instead of collapsing to `Any?`.
package genq

class Ref<T>(val value: T)

class Holder<T>(val size: Int, private val fill: T) {
    operator fun get(index: Int): T = fill
    fun cell(): Ref<T?> = Ref(null)
}

fun <T> holderOf(n: Int): Holder<T?> = Holder(n, null)

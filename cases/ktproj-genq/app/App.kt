// #18 APP: consumes the re-imported generics `genq.Holder` / `genq.Ref` + the `holderOf<T>(): Holder<T?>` factory
// through a <ProjectReference>. Before the fix `val h = holderOf<String>(3)` degraded to `Any?` (the factory return
// `Holder<object>` was unreadable), so `h.size`, `h[0]`, and `h.cell()` were all unresolved and the app FAILED TO
// COMPILE. With the [KotlinNullableGeneric] round-trip, `h` is `Holder<String?>` (factory: method-scope tv) and
// `h.cell()` is `Ref<String?>` (member of a generic class: type-scope tv) — both member sets resolve.
import genq.Holder
import genq.Ref
import genq.holderOf

fun main() {
    val h = holderOf<String>(3)          // Holder<String?>       (factory tv(method,0))
    println(h.size)                      // 3   (member surfaces only when h is NOT Any?)
    val e: String? = h[0]                // null  (the get indexer on the generic result)
    println(e ?: "empty")                // empty
    val c: Ref<String?> = h.cell()       // Ref<String?>          (member tv(type,0) restored)
    println(c.value ?: "cell-null")      // cell-null
}

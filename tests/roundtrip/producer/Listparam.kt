// ktproj-listparam (#27): a Kotlin LIBRARY exercising kotlin.collections.* PARAM/return types across the module
// boundary. Each collection param compiles to its BCL @ClrTypeAlias interface in the emitted dll (List ->
// IReadOnlyList, MutableList -> IList, Map -> IDictionary); dll2klib must reverse-map those BACK to
// kotlin.collections.* when the consumer re-imports this dll's signatures, so a consumer's listOf(...)/mapOf(...)
// value unifies with the param. (Package renamed from the case's `mylib` to `listparam` so it coexists with the
// nestedlist producer's own types in this single producer assembly.)
package listparam

// List<String> param -> IReadOnlyList<String> in metadata; called with listOf(...) from the consumer.
fun takesList(xs: List<String>): Int = xs.size

// MutableList<Int> param -> IList<Int>; called with mutableListOf(...).
fun takesMutable(xs: MutableList<Int>): Int {
    xs.add(99)
    return xs.size
}

// Map<String,Int> param -> IDictionary<String,Int>; called with mapOf(...).
fun takesMap(m: Map<String, Int>): Int = m.size

// A generic class with a List<T> constructor-val property -> the getter returns IReadOnlyList<T>; the consumer reads
// h.items.size (element-member resolution through the reverse-mapped property type).
class Holder<T>(val items: List<T>)

// A generic top-level fun with a List<T> param -> the consumer relies on generic inference (T from listOf("x","y")).
fun <T> makeHolder(items: List<T>): Holder<T> = Holder(items)

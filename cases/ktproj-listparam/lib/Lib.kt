// A Kotlin LIBRARY exercising kotlin.collections.* PARAM/return types across a <ProjectReference> (#27).
// Each collection param compiles to its BCL @ClrTypeAlias interface in the emitted dll (List -> IReadOnlyList,
// MutableList -> IList, Map -> IDictionary); facadegen must reverse-map those BACK to kotlin.collections.* when the
// app re-imports this dll's signatures, so a consumer's listOf(...)/mapOf(...) value unifies with the param.
package mylib

// List<String> param -> IReadOnlyList<String> in metadata; called with listOf(...) from the app.
fun takesList(xs: List<String>): Int = xs.size

// MutableList<Int> param -> IList<Int>; called with mutableListOf(...).
fun takesMutable(xs: MutableList<Int>): Int {
    xs.add(99)
    return xs.size
}

// Map<String,Int> param -> IDictionary<String,Int>; called with mapOf(...).
fun takesMap(m: Map<String, Int>): Int = m.size

// A generic class with a List<T> constructor-val property -> the getter returns IReadOnlyList<T>; the app reads
// h.items.size (element-member resolution through the reverse-mapped property type).
class Holder<T>(val items: List<T>)

// A generic top-level fun with a List<T> param -> the app relies on generic inference (T from listOf("x","y")).
fun <T> makeHolder(items: List<T>): Holder<T> = Holder(items)

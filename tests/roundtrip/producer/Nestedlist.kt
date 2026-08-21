// ktproj-nestedlist (#29): a cross-module Kotlin LIBRARY whose public API nests a kotlin.collections.* collection
// INSIDE a user-defined generic (`Box<List<T>>`, `State<List<T>>`). At generic-arg depth >= 1 bir2cir's Root-V
// variance collapse lowers the read-only `List<T>` to its INVARIANT CLR sibling `IList<T>` (load-bearing for
// reified-generic inhabitance — a single `T := List<Int>` must have ONE context-independent CLR lowering). That
// collapse can collide with `MutableList`'s own `IList` alias; dll2klib must not reverse-map the nested `IList` back
// to `MutableList` and surface `Box<MutableList<T>>` — the consumer's `Box<List<String>>` value was then REJECTED.
// bir2cir now stamps [KotlinCollectionIdentity] (the pre-collapse Kotlin type) on each affected slot; dll2klib
// restores `List` vs `MutableList` at every nested position from that stamp. (Package renamed from `mylib` to
// `nestedlist` to coexist with the listparam producer in this single assembly.)
package nestedlist

// `Crate`/`Store` (not `Box`/`State`) so the simple names are UNIQUE across this shared producer assembly — a
// same-simple-name collision with another package's generic type broke dll2klib's re-import of the generic member
// (`Crate<X>.v` went unresolved). The case tests the Root-V nested-collection collapse round-trip (#29), not the names.
class Crate<X>(val v: X)
class Store<S>(val value: S)

// nested read-only List inside a user generic — the headline #29 slot (param + return).
fun <T> useNested(s: Crate<List<T>>): Int = s.v.size
fun <T> boxOfList(items: List<T>): Crate<List<T>> = Crate(items)

// a DEEPER user-generic nest (Store) — same read-only identity must round-trip.
fun <T> stateOfList(items: List<T>): Store<List<T>> = Store(items)

// a nested MUTABLE list inside the SAME user generic — must STILL surface as MutableList (the read/write split
// survives: MutableList lowers to IList via its own alias, is NOT stamped, and dll2klib reverse-maps it correctly).
fun <T> boxOfMutable(items: MutableList<T>): Crate<MutableList<T>> = Crate(items)
fun <T> useNestedMutable(s: Crate<MutableList<T>>): Int { s.v.add(s.v[0]); return s.v.size }

// A member carrier cannot restore these positions: the Kotlin collection identity is part of the declaration edge
// itself. All three type-level positions share [KotlinSupertypes] with nullable-generic erasure.
open class CollectionEdge<T>(val value: T)
class ReadonlyCollectionBase : CollectionEdge<List<String>>(listOf("base edge"))

interface CollectionContract<T>
class ReadonlyCollectionInterface : CollectionContract<List<String>>

class CollectionBound<T : CollectionEdge<List<String>>>(val value: T)

// Both producers move this one edge. Nullable-generic recording runs first and owns the less-erased `T?`; the later
// collection producer must merge without overwriting that source truth with its already-erased view.
open class MixedCollectionEdge<T>
class MixedCollectionDerived<T> : MixedCollectionEdge<List<T?>>()

class NullableCollectionBase : CollectionEdge<List<String?>>(listOf<String?>(null))
class StarCollectionBase : CollectionEdge<List<*>>(listOf<Any?>("star edge"))

interface NullableEdge<T>
interface ReadonlyEdge<T>
class MixedInterfaceEdges<T> : NullableEdge<T?>, ReadonlyEdge<List<T>>

class CollectionOuter<A> {
    inner class BoundInner<B : CollectionEdge<List<String>>>(val value: B)
    open inner class ApplicationInner<B>(val value: B)
}

class InnerApplicationBound<T : CollectionOuter<List<String>>.ApplicationInner<Int>>(val value: T)

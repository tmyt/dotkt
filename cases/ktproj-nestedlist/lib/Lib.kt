package mylib

// #29: a cross-module Kotlin LIBRARY whose public API nests a kotlin.collections.* collection INSIDE a user-defined
// generic (`Box<List<T>>`, `State<List<T>>`). At generic-arg depth >= 1 bir2cir's Root-V variance collapse lowers the
// read-only `List<T>` to its INVARIANT CLR sibling `IList<T>` (load-bearing for reified-generic inhabitance — a single
// `T := List<Int>` must have ONE context-independent CLR lowering). That collapse collided with `MutableList`'s own
// `IList` alias, so facadegen used to reverse-map the nested `IList` back to `MutableList` and surface
// `Box<MutableList<T>>` — the app's `Box<List<String>>` value was then REJECTED. bir2cir now stamps
// [KotlinCollectionIdentity] (the pre-collapse Kotlin type) on each affected slot; facadegen restores `List` vs
// `MutableList` at every nested position from that stamp. Regression guard for #29.

class Box<X>(val v: X)
class State<S>(val value: S)

// nested read-only List inside a user generic — the headline #29 slot (param + return).
fun <T> useNested(s: Box<List<T>>): Int = s.v.size
fun <T> boxOfList(items: List<T>): Box<List<T>> = Box(items)

// a DEEPER user-generic nest (State) — same read-only identity must round-trip.
fun <T> stateOfList(items: List<T>): State<List<T>> = State(items)

// a nested MUTABLE list inside the SAME user generic — must STILL surface as MutableList (the read/write split
// survives: MutableList lowers to IList via its own alias, is NOT stamped, and facadegen reverse-maps it correctly).
fun <T> boxOfMutable(items: MutableList<T>): Box<MutableList<T>> = Box(items)
fun <T> useNestedMutable(s: Box<MutableList<T>>): Int { s.v.add(s.v[0]); return s.v.size }

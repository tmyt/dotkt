# CLR collection binding — the iterator bridge (design)

Status: **design (Codex-assisted, 2026-06-28)**. The HARD fork of the @Clr stdlib binding ([[clr-binding-actuals]]):
the collection hierarchy, where Kotlin `Iterator.hasNext()/next()` ≠ BCL `IEnumerator.MoveNext()/Current`.

## Decision summary

Bind the collection **interfaces** to their BCL surface, but bridge the iterator semantic mismatch with compiler
lowerings + Kotlin-level adapters (NO hand-written CIL — @ClrAsm is gone). Key rule: **do NOT bind Kotlin
`Iterator<T>` to CLR `IEnumerator<T>` directly**; bind `Iterable<T>`→`IEnumerable<T>` and bridge at the two protocol
boundaries.

Type map:
| Kotlin | BCL |
|---|---|
| `Iterable<out T>` | `IEnumerable<T>` |
| `Collection<out T>` | `IReadOnlyCollection<T>` (size→Count) |
| `List<out T>` | `IReadOnlyList<T>` (get→get_Item) |
| `MutableCollection<T>` | `ICollection<T>` |
| `MutableList<T>` | `IList<T>` |
| `Set<out T>` | `IReadOnlySet<T>` |
| `MutableSet<T>` | `ISet<T>` |
| `Map<out K,out V>` | `IReadOnlyDictionary<K,V>` |
| `MutableMap<K,V>` | `IDictionary<K,V>` |
| `listOf/mutableListOf/arrayListOf` | `System.Collections.Generic.List<T>` (returned as the read-only/mutable iface) |

Three mechanisms: (1) CLR-bound type identity (@Clr), (2) **for-loop lowering** to GetEnumerator/MoveNext/Current when
the iterated type is BCL-bound IEnumerable, (3) **semantic adapters**: explicit `iterator()` → `KotlinIteratorOver
Enumerator<T>`; a Kotlin `Iterable` implementor gets a generated `GetEnumerator` via `EnumeratorOverKotlinIterator<T>`.

---

## Full Codex consultation (verbatim)

Yes: make the bridge a compiler feature, not a library trick. Treat Kotlin collection interfaces annotated with `@Clr` as having **CLR surface identity**, but preserve Kotlin semantics through targeted lowerings and generated adapters.

**Opinionated Design**

1. **For-Loops**

Lower `for (x in xs)` directly to the BCL foreach pattern when the iterated type is CLR-bound to `IEnumerable<T>` or compatible:

```kotlin
for (x in xs) body
```

becomes approximately:

```cil
var e = xs.GetEnumerator()
try {
    while (e.MoveNext()) {
        var x = e.Current
        body
    }
}
finally {
    (e as IDisposable)?.Dispose()
}
```

This is clean and desirable.

Do not lower BCL-bound `Iterable<T>` loops through Kotlin `Iterator<T>`. That would allocate an adapter and obscure the native CLR shape.

Compiler rule:

```text
if expression type is @Clr-bound Iterable<T> / IEnumerable<T>:
    emit GetEnumerator / MoveNext / Current
else:
    emit Kotlin iterator / hasNext / next protocol
```

You may also support pattern-based enumeration later, but first bind to known BCL collection interfaces.

2. **Explicit Kotlin Iterator Use**

For explicit calls:

```kotlin
val it = xs.iterator()
it.hasNext()
it.next()
```

you need a real Kotlin `Iterator<T>` view.

Provide this in the Kotlin stdlib as normal Kotlin code:

```kotlin
package kotlin.collections

internal class KotlinIteratorOverEnumerator<T>(
    private val e: System.Collections.Generic.IEnumerator<T>
) : Iterator<T> {
    private var state: Int = 0
    // 0 = unknown, 1 = has current buffered, 2 = done

    override fun hasNext(): Boolean {
        if (state == 1) return true
        if (state == 2) return false

        return if (e.MoveNext()) {
            state = 1
            true
        } else {
            state = 2
            false
        }
    }

    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        state = 0
        return e.Current
    }
}
```

Then define the Kotlin-level adapter:

```kotlin
@PublishedApi
internal fun <T> iteratorOverEnumerable(
    self: System.Collections.Generic.IEnumerable<T>
): Iterator<T> =
    KotlinIteratorOverEnumerator(self.GetEnumerator())
```

For `Iterable<T>.iterator()` on the CLR-bound stdlib declaration, do **not** bind it directly to `GetEnumerator`, because the return type and semantics differ.

Instead:

```kotlin
@Clr("System.Collections.Generic.IEnumerable`1")
interface Iterable<out T> {
    fun iterator(): Iterator<T> =
        iteratorOverEnumerable(this)
}
```

Backend behavior:

```text
Iterable<T>.iterator() has a Kotlin body
=> hoist to static helper
=> call helper with receiver as __self
```

That matches your existing model perfectly.

Important: `hasNext()` must buffer by calling `MoveNext()` at most once per element. `next()` must consume the buffered state and return `Current`.

Also implement disposal if useful:

```kotlin
if (!hasNext()) {
    (e as? System.IDisposable)?.Dispose()
}
```

But be careful: Kotlin `Iterator` has no close/dispose protocol, so disposal is best-effort only. Compiler-lowered `for` should always dispose.

3. **Kotlin Classes Implementing Iterable / Iterator**

Separate two cases.

**A Kotlin class implements `Iterable<T>`**

Example:

```kotlin
class MyRange : Iterable<Int> {
    override fun iterator(): Iterator<Int> = ...
}
```

Because `kotlin.collections.Iterable<T>` is CLR-bound to `IEnumerable<T>`, the emitted class must implement:

```csharp
System.Collections.Generic.IEnumerable<T>
System.Collections.IEnumerable
```

Generate methods:

```kotlin
public fun GetEnumerator(): System.Collections.Generic.IEnumerator<T> =
    EnumeratorOverKotlinIterator(this.iterator())

public fun System.Collections.IEnumerable.GetEnumerator(): System.Collections.IEnumerator =
    this.GetEnumerator()
```

Adapter:

```kotlin
internal class EnumeratorOverKotlinIterator<T>(
    private val it: Iterator<T>
) : System.Collections.Generic.IEnumerator<T> {
    private var currentValue: T = defaultValue()

    override fun MoveNext(): Boolean {
        if (!it.hasNext()) return false
        currentValue = it.next()
        return true
    }

    override val Current: T
        get() = currentValue

    override fun Reset() {
        throw System.NotSupportedException()
    }

    override fun Dispose() {}
}
```

For non-nullable/value types, `defaultValue()` needs backend support or a generated backing field initialized to CLR default.

**A Kotlin class implements `Iterator<T>`**

Do **not** make every Kotlin `Iterator<T>` directly implement `IEnumerator<T>` unless `Iterator` itself is CLR-bound to `IEnumerator<T>`, which I would avoid.

Reason: the semantics are not isomorphic. Kotlin `Iterator.next()` advances; CLR `IEnumerator.Current` does not. Binding `Iterator` directly to `IEnumerator` will make normal calls ambiguous and bug-prone.

Instead:

```text
Kotlin Iterator<T> remains a Kotlin interface.
Iterable<T> is CLR-bound to IEnumerable<T>.
The compiler generates IEnumerator adapters where CLR requires them.
```

If a Kotlin type explicitly needs to be passed as `IEnumerator<T>`, require/generated adapter:

```kotlin
asClrEnumerator(kotlinIterator)
```

or compiler-inserted conversion only at known CLR interop boundaries.

4. **Concrete Collection Factories**

Yes: return real BCL collections.

Use:

```text
listOf<T>()              -> System.Array.Empty<T>() or cached empty IReadOnlyList<T>
listOf<T>(one)           -> small immutable list if you have one, otherwise List<T>
listOf<T>(vararg items)  -> System.Collections.Generic.List<T>
mutableListOf<T>()       -> System.Collections.Generic.List<T>
arrayListOf<T>()         -> System.Collections.Generic.List<T>
setOf<T>()               -> HashSet<T> or read-only wrapper, depending on immutability policy
mutableSetOf<T>()        -> HashSet<T>
mapOf<K,V>()             -> Dictionary<K,V> or read-only wrapper
mutableMapOf<K,V>()      -> Dictionary<K,V>
```

But be precise about Kotlin read-only vs immutable:

```text
Kotlin List<T> is read-only view, not necessarily immutable.
```

So this is acceptable:

```kotlin
fun <T> listOf(vararg xs: T): List<T>
```

emits a `System.Collections.Generic.List<T>` instance returned as `IReadOnlyList<T>`.

Recommended bindings:

```text
Iterable<out T>      -> IEnumerable<T>
Collection<out T>    -> IReadOnlyCollection<T>
List<out T>          -> IReadOnlyList<T>
MutableIterable<T>   -> IEnumerable<T> plus Kotlin mutation members where needed
MutableCollection<T> -> ICollection<T>
MutableList<T>       -> IList<T>
Set<out T>           -> IReadOnlySet<T> if targeting modern .NET, otherwise IReadOnlyCollection<T>
MutableSet<T>        -> ISet<T>
Map<out K, out V>    -> IReadOnlyDictionary<K,V>
MutableMap<K,V>      -> IDictionary<K,V>
```

For `List.get(index)` bind to:

```text
IReadOnlyList<T>.get_Item(int)
```

For `size` bind to:

```text
IReadOnlyCollection<T>.Count
```

For `MutableList.add`, `removeAt`, `set`, bind to `IList<T>` members.

Variance:

```text
Kotlin List<out E>        -> IReadOnlyList<E>
Kotlin Collection<out E>  -> IReadOnlyCollection<E>
Kotlin Iterable<out E>    -> IEnumerable<E>
```

This matches CLR covariance because these BCL interfaces are covariant in `out T`.

For mutable collections:

```text
MutableList<E> -> IList<E>
MutableCollection<E> -> ICollection<E>
MutableSet<E> -> ISet<E>
```

These must be invariant. Do not try to preserve Kotlin-style variance on mutable BCL interfaces.

**Backend Rules To Implement**

Use three distinct mechanisms:

```text
1. CLR-bound type identity
   kotlin.collections.Iterable<T> is emitted/referenced as IEnumerable<T>.

2. Special for-loop lowering
   BCL-bound Iterable<T> uses GetEnumerator / MoveNext / Current directly.

3. Semantic adapters
   Explicit iterator() calls use KotlinIteratorOverEnumerator<T>.
   Kotlin Iterable implementors get generated GetEnumerator methods using EnumeratorOverKotlinIterator<T>.
```

The key rule is: **do not bind Kotlin `Iterator<T>` to CLR `IEnumerator<T>` directly**. Bind `Iterable<T>` to `IEnumerable<T>`, then bridge the iterator semantic mismatch at the two protocol boundaries.
6,166
Yes: make the bridge a compiler feature, not a library trick. Treat Kotlin collection interfaces annotated with `@Clr` as having **CLR surface identity**, but preserve Kotlin semantics through targeted lowerings and generated adapters.
Yes: make the bridge a compiler feature, not a library trick. Treat Kotlin collection interfaces annotated with `@Clr` as having **CLR surface identity**, but preserve Kotlin semantics through targeted lowerings and generated adapters.

**Opinionated Design**

1. **For-Loops**

Lower `for (x in xs)` directly to the BCL foreach pattern when the iterated type is CLR-bound to `IEnumerable<T>` or compatible:

```kotlin
for (x in xs) body
```

becomes approximately:

```cil
var e = xs.GetEnumerator()
try {
    while (e.MoveNext()) {
        var x = e.Current
        body
    }
}
finally {
    (e as IDisposable)?.Dispose()
}
```

This is clean and desirable.

Do not lower BCL-bound `Iterable<T>` loops through Kotlin `Iterator<T>`. That would allocate an adapter and obscure the native CLR shape.

Compiler rule:

```text
if expression type is @Clr-bound Iterable<T> / IEnumerable<T>:
    emit GetEnumerator / MoveNext / Current
else:
    emit Kotlin iterator / hasNext / next protocol
```

You may also support pattern-based enumeration later, but first bind to known BCL collection interfaces.

2. **Explicit Kotlin Iterator Use**

For explicit calls:

```kotlin
val it = xs.iterator()
it.hasNext()
it.next()
```

you need a real Kotlin `Iterator<T>` view.

Provide this in the Kotlin stdlib as normal Kotlin code:

```kotlin
package kotlin.collections

internal class KotlinIteratorOverEnumerator<T>(
    private val e: System.Collections.Generic.IEnumerator<T>
) : Iterator<T> {
    private var state: Int = 0
    // 0 = unknown, 1 = has current buffered, 2 = done

    override fun hasNext(): Boolean {
        if (state == 1) return true
        if (state == 2) return false

        return if (e.MoveNext()) {
            state = 1
            true
        } else {
            state = 2
            false
        }
    }

    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        state = 0
        return e.Current
    }
}
```

Then define the Kotlin-level adapter:

```kotlin
@PublishedApi
internal fun <T> iteratorOverEnumerable(
    self: System.Collections.Generic.IEnumerable<T>
): Iterator<T> =
    KotlinIteratorOverEnumerator(self.GetEnumerator())
```

For `Iterable<T>.iterator()` on the CLR-bound stdlib declaration, do **not** bind it directly to `GetEnumerator`, because the return type and semantics differ.

Instead:

```kotlin
@Clr("System.Collections.Generic.IEnumerable`1")
interface Iterable<out T> {
    fun iterator(): Iterator<T> =
        iteratorOverEnumerable(this)
}
```

Backend behavior:

```text
Iterable<T>.iterator() has a Kotlin body
=> hoist to static helper
=> call helper with receiver as __self
```

That matches your existing model perfectly.

Important: `hasNext()` must buffer by calling `MoveNext()` at most once per element. `next()` must consume the buffered state and return `Current`.

Also implement disposal if useful:

```kotlin
if (!hasNext()) {
    (e as? System.IDisposable)?.Dispose()
}
```

But be careful: Kotlin `Iterator` has no close/dispose protocol, so disposal is best-effort only. Compiler-lowered `for` should always dispose.

3. **Kotlin Classes Implementing Iterable / Iterator**

Separate two cases.

**A Kotlin class implements `Iterable<T>`**

Example:

```kotlin
class MyRange : Iterable<Int> {
    override fun iterator(): Iterator<Int> = ...
}
```

Because `kotlin.collections.Iterable<T>` is CLR-bound to `IEnumerable<T>`, the emitted class must implement:

```csharp
System.Collections.Generic.IEnumerable<T>
System.Collections.IEnumerable
```

Generate methods:

```kotlin
public fun GetEnumerator(): System.Collections.Generic.IEnumerator<T> =
    EnumeratorOverKotlinIterator(this.iterator())

public fun System.Collections.IEnumerable.GetEnumerator(): System.Collections.IEnumerator =
    this.GetEnumerator()
```

Adapter:

```kotlin
internal class EnumeratorOverKotlinIterator<T>(
    private val it: Iterator<T>
) : System.Collections.Generic.IEnumerator<T> {
    private var currentValue: T = defaultValue()

    override fun MoveNext(): Boolean {
        if (!it.hasNext()) return false
        currentValue = it.next()
        return true
    }

    override val Current: T
        get() = currentValue

    override fun Reset() {
        throw System.NotSupportedException()
    }

    override fun Dispose() {}
}
```

For non-nullable/value types, `defaultValue()` needs backend support or a generated backing field initialized to CLR default.

**A Kotlin class implements `Iterator<T>`**

Do **not** make every Kotlin `Iterator<T>` directly implement `IEnumerator<T>` unless `Iterator` itself is CLR-bound to `IEnumerator<T>`, which I would avoid.

Reason: the semantics are not isomorphic. Kotlin `Iterator.next()` advances; CLR `IEnumerator.Current` does not. Binding `Iterator` directly to `IEnumerator` will make normal calls ambiguous and bug-prone.

Instead:

```text
Kotlin Iterator<T> remains a Kotlin interface.
Iterable<T> is CLR-bound to IEnumerable<T>.
The compiler generates IEnumerator adapters where CLR requires them.
```

If a Kotlin type explicitly needs to be passed as `IEnumerator<T>`, require/generated adapter:

```kotlin
asClrEnumerator(kotlinIterator)
```

or compiler-inserted conversion only at known CLR interop boundaries.

4. **Concrete Collection Factories**

Yes: return real BCL collections.

Use:

```text
listOf<T>()              -> System.Array.Empty<T>() or cached empty IReadOnlyList<T>
listOf<T>(one)           -> small immutable list if you have one, otherwise List<T>
listOf<T>(vararg items)  -> System.Collections.Generic.List<T>
mutableListOf<T>()       -> System.Collections.Generic.List<T>
arrayListOf<T>()         -> System.Collections.Generic.List<T>
setOf<T>()               -> HashSet<T> or read-only wrapper, depending on immutability policy
mutableSetOf<T>()        -> HashSet<T>
mapOf<K,V>()             -> Dictionary<K,V> or read-only wrapper
mutableMapOf<K,V>()      -> Dictionary<K,V>
```

But be precise about Kotlin read-only vs immutable:

```text
Kotlin List<T> is read-only view, not necessarily immutable.
```

So this is acceptable:

```kotlin
fun <T> listOf(vararg xs: T): List<T>
```

emits a `System.Collections.Generic.List<T>` instance returned as `IReadOnlyList<T>`.

Recommended bindings:

```text
Iterable<out T>      -> IEnumerable<T>
Collection<out T>    -> IReadOnlyCollection<T>
List<out T>          -> IReadOnlyList<T>
MutableIterable<T>   -> IEnumerable<T> plus Kotlin mutation members where needed
MutableCollection<T> -> ICollection<T>
MutableList<T>       -> IList<T>
Set<out T>           -> IReadOnlySet<T> if targeting modern .NET, otherwise IReadOnlyCollection<T>
MutableSet<T>        -> ISet<T>
Map<out K, out V>    -> IReadOnlyDictionary<K,V>
MutableMap<K,V>      -> IDictionary<K,V>
```

For `List.get(index)` bind to:

```text
IReadOnlyList<T>.get_Item(int)
```

For `size` bind to:

```text
IReadOnlyCollection<T>.Count
```

For `MutableList.add`, `removeAt`, `set`, bind to `IList<T>` members.

Variance:

```text
Kotlin List<out E>        -> IReadOnlyList<E>
Kotlin Collection<out E>  -> IReadOnlyCollection<E>
Kotlin Iterable<out E>    -> IEnumerable<E>
```

This matches CLR covariance because these BCL interfaces are covariant in `out T`.

For mutable collections:

```text
MutableList<E> -> IList<E>
MutableCollection<E> -> ICollection<E>
MutableSet<E> -> ISet<E>
```

These must be invariant. Do not try to preserve Kotlin-style variance on mutable BCL interfaces.

**Backend Rules To Implement**

Use three distinct mechanisms:

```text
1. CLR-bound type identity
   kotlin.collections.Iterable<T> is emitted/referenced as IEnumerable<T>.

2. Special for-loop lowering
   BCL-bound Iterable<T> uses GetEnumerator / MoveNext / Current directly.

3. Semantic adapters
   Explicit iterator() calls use KotlinIteratorOverEnumerator<T>.
   Kotlin Iterable implementors get generated GetEnumerator methods using EnumeratorOverKotlinIterator<T>.
```

The key rule is: **do not bind Kotlin `Iterator<T>` to CLR `IEnumerator<T>` directly**. Bind `Iterable<T>` to `IEnumerable<T>`, then bridge the iterator semantic mismatch at the two protocol boundaries.

---

## VALIDATED reference pattern (2026-06-28) — the full for-loop bridge WORKS

Proven end-to-end in scratchpad (`for (x in @ClrList) -> 1/2/3`). The mechanism, confirmed:

```kotlin
@Clr("System.Collections.Generic.IEnumerator")
internal interface ClrEnumerator<out T> { fun MoveNext(): Boolean; @Clr("get_Current") fun current(): T }
@Clr("System.Collections.Generic.IEnumerable")
internal interface ClrEnumerable<out T> { fun GetEnumerator(): ClrEnumerator<T> }   // INTERFACE GetEnumerator -> IEnumerator<T> (not the List<T>.Enumerator struct)

internal class KotlinIteratorOverEnumerator<out T>(private val e: ClrEnumerator<T>) : Iterator<T> { ... MoveNext/Current -> hasNext/next, buffer one ... }
internal fun <T> iteratorOverEnumerable(self: ClrEnumerable<T>): Iterator<T> = KotlinIteratorOverEnumerator(self.GetEnumerator())

@Clr("System.Collections.Generic.IEnumerable")
interface Iterable<out T> {
    operator fun iterator(): Iterator<T> = iteratorOverEnumerable(this as ClrEnumerable<T>)   // default body -> RULE 3 hoist; cast Iterable(=IEnumerable) -> ClrEnumerable(=IEnumerable) is identity at runtime
}
```

Key facts that made it work:
1. **No special for-loop lowering.** `for (x in xs)` desugars to `xs.iterator()`/hasNext/next in FIR; `Iterable.iterator()`
   has a Kotlin default body, so **rule 3 hoists it to a static helper** automatically.
2. **`this as ClrEnumerable<T>`** bridges the Kotlin-type gap (Iterable and ClrEnumerable are distinct Kotlin types, same
   BCL IEnumerable) — an identity cast at runtime. Avoids adding GetEnumerator to Iterable (which would break USER Iterable
   implementors — the reverse direction, EnumeratorOverKotlinIterator, still TODO).
3. Required an ilemit fix (committed): generic self-calls now reference the self-instantiation, not the open type def
   (the adapter is a generic class calling its own methods).
4. The adapter MUST be `out T` (covariant) to satisfy `Iterable<out T>.iterator(): Iterator<out T>`.

## Remaining wiring (the large, coupled follow-up)
The mechanism is proven; wiring the real hierarchy is mechanical-but-large and CANNOT be tested incrementally (one
for-loop needs Iterable+List+listOf together):
- `Iterable`→IEnumerable (iterator() bridge above), `Collection`→IReadOnlyCollection (size→@Clr get_Count; isEmpty/
  contains/containsAll have NO 1:1 BCL member → bodied actuals, rule-3-hoisted, implemented via Count/enumeration),
  `List`→IReadOnlyList (get→@Clr get_Item; indexOf/lastIndexOf/subList/listIterator → bodied actuals), `MutableCollection`
  →ICollection, `MutableList`→IList.
- `ArrayList`→@Clr `System.Collections.Generic.List` (Add/etc.); it must IMPLEMENT every abstract member of MutableList
  (@Clr each 1:1 one with a TODO body, leave the rest bodied for rule 3).
- `listOf`/`mutableListOf`/`emptyList` factories → create an @Clr List.
Each rule-3-hoisted member needs a REAL Kotlin body (the current stubs are `TODO()`, which would throw). Iterate with
`scripts/run-clr-sample.sh` once enough of the chain is in to make `for (x in listOf(1,2,3))` resolvable.

## WIRING BLOCKER found (2026-06-28): expect/actual forces `iterator()` abstract

Attempting the real-stdlib wiring (Iterable/Collection/List @Clr + `iterator()` default bridge + `Array.asList()` =
`this as List`) hit a hard expect/actual constraint:
- The common `expect interface Iterable { operator fun iterator() }` declares `iterator()` **abstract**. Giving the CLR
  `actual` a DEFAULT BODY (the rule-3 bridge) fails: *"modality is different — expect abstract, actual open."*
- Removing members the expect declares (the `iterator()`/`size`/etc. overrides on Collection/List) fails:
  *"some expected members have no actual ones."* So the actual must keep every member, all abstract.

=> The rule-3-hoist-of-`iterator()`-default-body approach (which worked in scratchpad with NON-expect interfaces) does
NOT apply to the real stdlib collection interfaces. The bridge must instead be a **compiler lowering**: when `recv.iterator()`
is called and `recv`'s static type is a BCL-bound (`@Clr`) `kotlin.collections.Iterable` subtype, kotc emits a call to the
stdlib bridge `iteratorOverEnumerable(recv)` (a generic top-level fun in ClrIteratorBridge.kt) instead of routing to a
non-existent BCL `iterator` member. (`for` desugars to `iterator()`/hasNext/next in FIR, and hasNext/next then run on the
adapter — a real Kotlin Iterator — so only `iterator()` needs interception.)

OPEN DESIGN POINT: this is a compiler-knows-a-stdlib-function coupling (like the existing array-iteration / .size
intrinsics). Either accept it as a foundational intrinsic (resolve the bridge fun's file class by name), or find a
cleaner registration. The @Clr annotations on the interfaces themselves (NOT the default body / member removal) are
believed compatible with expect/actual — to be confirmed — so the type identity (Iterable=IEnumerable, size=Count,
get=get_Item) can land independently of the iterator() lowering.

## C3 (the reverse direction) is FORCED, not deferrable (2026-06-28)

The "concrete classes are @Clr->BCL, so no Kotlin implementors of @Clr collection interfaces" idea is INCOMPLETE: the
stdlib has concrete Kotlin classes implementing the collection interfaces that are NOT BCL collections and can't be
@Clr-bound — the **unsigned arrays** (`UByteArray`/`UIntArray`/... : `Collection<UByte>`), `EmptyList`, ranges, etc.
So @Clr-binding Collection/List immediately aborts ilemit on THOSE classes' `clrOverride` against the generic @Clr base.

=> A Kotlin class IMPLEMENTING a @Clr collection interface (C3 / the reverse direction) IS required for the bootstrap.
It has two parts:
- **C3a (member naming):** an override of a @Clr interface member must emit with the BCL name — `size` -> `get_Count`,
  `get` -> `get_Item`, `contains` -> `Contains`. (kotc: a method/accessor overriding a @Clr-annotated interface member
  inherits that member's @Clr name; ilemit clrOverride must resolve the CONSTRUCTED generic base, not the bare name.)
- **C3b (GetEnumerator):** the class's Kotlin `iterator()` must ALSO be exposed as a BCL `GetEnumerator(): IEnumerator<T>`
  — a generated method wrapping the Kotlin iterator in an `EnumeratorOverKotlinIterator<T>` adapter (the mirror of
  KotlinIteratorOverEnumerator). This is the genuinely large piece.

STATUS: the FORWARD mechanism is fully validated (bridge, @Clr type+member binding, abstract-leave-abstract, iterator()
compiler lowering — committed, inert until the interfaces are @Clr-bound). C3 (reverse) is the forced, large remaining
core, blocked behind it. Order: C3a (member naming + clrOverride generic-base) -> C3b (generated GetEnumerator) -> then
land the interface/concrete @Clr + asList + factories -> `for (x in listOf(1,2,3))`.

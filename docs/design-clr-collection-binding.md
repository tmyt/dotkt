# CLR collection binding

Status: implemented design record. This document records the collection identities and iterator boundary that remain part of the current architecture.

## Public type mapping

| Kotlin type | CLR operational interface |
|---|---|
| `Iterable<T>` | `IEnumerable<T>` |
| `Collection<T>` | `IReadOnlyCollection<T>` |
| `MutableCollection<T>` | `ICollection<T>` |
| `List<T>` | `IReadOnlyList<T>` |
| `MutableList<T>` | `IList<T>` |
| `Set<T>` | `IReadOnlyCollection<T>` plus Kotlin Set identity |
| `MutableSet<T>` | `ICollection<T>` plus Kotlin MutableSet identity |
| `Map<K,V>` | `IDictionary<K,V>` plus Kotlin read-only map behavior |
| `MutableMap<K,V>` | `IDictionary<K,V>` |

These are exact declaration, implementation and native interop interfaces. Ordinary Kotlin collection-interface
values use `object`, retaining their Kotlin source types in metadata, so unchecked casts do not demand a different
invariant CLR construction. This includes invariant mutable interfaces as well as declaration-variant aliases.
Factories return ordinary BCL implementations;
widening passes the same reference without requiring CLR variance between value-element instantiations.
The mapping is declared by stdlib `@ClrTypeAlias` and `@ClrIntrinsic` metadata and applied by bir2cir; kotc does not hard-code it.

Emitted Kotlin Set implementations carry the nominal Kotlin Set classifier; foreign Sets are recognized through
their actual `IReadOnlySet<T>` / `ISet<T>` interfaces. A shared Collection storage interface alone does not identify
a Set, and an unrelated List implemented by the same object does not supply that Set's Count or enumeration.

### Static operation views

Opaque values retain the raw reference, not a dynamically remembered originating view. Each operation selects
from its static Kotlin view: a matching implemented closed preference wins, otherwise exactly one eligible actual
family is required. A star supplies no closed preference. Merely transporting an ambiguous value does not open
its family; null passes through unchanged.

Direct Set Count, queries and iteration select the same actual Set family and its Collection/Enumerable parents,
excluding unrelated List and raw enumeration. The preferred element can match a read-only or mutable Set root;
mutable-only Sets remain readable, and Kotlin-authored default-member overrides remain authoritative. Widening
to Collection or Iterable deliberately drops the originating Set family: the operation uses that wider static
view and can therefore be ambiguous even when the Set operation was unique. The same object entering the same
static parameter has the same behavior regardless of its earlier casts or generic storage. No collection adapter,
per-object provenance registry or identity normalization participates in this policy.

Ordinary invariant mutable operations test for their exact eligible native view and call the selected CLR member
directly when it exists. The same runtime selector verifies eligibility, including dictionary-storage exclusions;
its immutable type/view decisions use weak keys, not collection-instance history. A missing exact view keeps the
existing erased path. Value-returning Kotlin mutation slots take precedence over native old-value emulation.

Projected mutable List append uses the same selected `IList<T>` family as indexed insertion. Its native `Add` and
before/after `Count` come from that List's own `ICollection<T>` parent, not from unrelated collection faces on the
receiver. An exact source preference wins when implemented; otherwise genuine ambiguity remains an error. A raw
`IList` is used only when no eligible generic List exists and the raw face is not map storage.

List Count, indexed reads, queries and iteration likewise share the selected List family and its actual parents.
The explicit `listIterator` and `subList` operations retain their requested backing view in the newly created
iterator or live slice; this is operation state, not originating-view metadata on the transported collection.

## Why Kotlin `Iterator<T>` is not simply `IEnumerator<T>`

Kotlin and CLR expose different protocols:

| Kotlin | CLR |
|---|---|
| `hasNext(): Boolean` | `MoveNext(): Boolean` advances the cursor |
| `next(): T` | `Current` reads the current element |
| no disposal contract | `IDisposable.Dispose()` |

Binding the identities directly would make `hasNext()` consume an element or require hidden buffering in every caller. DotKt therefore uses explicit adapters at the protocol boundary.

### CLR enumeration consumed as Kotlin

`KotlinIteratorOverEnumerator<T>` wraps an `IEnumerator<T>`. It buffers the result of `MoveNext()` so repeated `hasNext()` calls are stable, returns the buffered `Current` from `next()`, and disposes the enumerator on completion.

### Kotlin iterable exposed as CLR

`dotkt$EnumeratorOverKotlinIterator<T>` wraps a Kotlin iterator as an `IEnumerator<T>`. Generated `GetEnumerator()` bridges let a Kotlin `Iterable<T>` implementation satisfy CLR enumeration consumers without changing Kotlin's iterator contract.

bir2cir authors both halves as ordinary CIR — the adapter's TypeDef, fields, constructor, method bodies, and the `GetEnumerator()`/`dotkt$NonGenericGetEnumerator()` pair on each qualifying class, each carrying the exact MethodImpl descriptor of the slot it fills. The adapter cannot be written in Kotlin, because `IEnumerator<T>` and the non-generic `IEnumerator` declare two `Current` slots that differ only in return type; it is emitted once per module, since its CLR identity never appears in a signature.

Inherited interfaces read from reference metadata resolve through the same alias declarations as local interfaces;
CLR arity in a referenced owner's spelling must not hide its native enumeration obligation.

## Member behavior

Members with direct BCL equivalents are substituted from stdlib metadata, for example `size` to `Count` and indexed access to the CLR indexer. Members without a one-to-one BCL operation retain real Kotlin bodies that use bound primitive members. This keeps stdlib policy in the stdlib rather than adding symbol recognition to the compiler.

A declared Kotlin `iterator()` keeps its virtual slot whether abstract or concrete, in the current module or a
reference assembly, including a slot inherited from a base declaration. Early binding follows source hierarchy
metadata; after frame materialization the physical hierarchy is authoritative. Only an owner without that declared
protocol uses the native enumeration fallback. A read-only
enumeration adapter must not replace an authored mutable iterator or its removal contract.

Native `ICollection<T>.Contains` on a Kotlin read-only collection uses the same declaration-owned membership
slot as Kotlin callers, including a native dictionary's checked Keys/Values views. Default membership bodies perform
their own search rather than reentering that dispatcher;
foreign collection calls retain their static operation view.

Kotlin `MutableList.set` and `removeAt` dispatch through their value-returning Kotlin slots without an extra getter.
Each operation has its own capability so unrelated list defaults do not acquire new implementation obligations.
Only foreign native lists emulate the result with a read followed by the void CLR mutation; native `IList` signatures stay unchanged.

Authored `Map` and `MutableMap` implementations carry independent read and mutation capabilities selected by
Kotlin override identity. Map dispatchers preserve their original getters, views, and overrides, including through
erased or inherited references. One canonical stdlib carrier, also declared in its reference twin,
fulfills all native `IDictionary<K,V>`,
`ICollection<KeyValuePair<K,V>>`, and generic/non-generic enumeration slots. It translates missing-key and mutation
return conventions, rejects mutation of read-only maps, and exposes checked live native key/value views without
replacing the Kotlin views. Native enumeration constructs real CLR pairs from Kotlin entries; the two entry types
are not interchangeable.

Existential `MutableList` Count, indexed reads, and the default `isEmpty` use the eligible `IList<T>` family
and its actual `ICollection<T>` parent. An unrelated readonly List is not a candidate for these mutable-view
operations, even when it uses the same element type. Ordinary `List` operations retain their readonly preference.
Raw `IList` remains a fallback only when no eligible generic mutable List exists; it must not hide genuine
generic ambiguity or a getter exception. Map-owned List storage is excluded, and source-authored exact
interface witnesses and Kotlin emptiness overrides remain authoritative.

Projected indexed insertion, replacement and removal carry the source-selected exact List preference through
the complete native operation, including the getter used to emulate a mutation's result. An absent preference
still requires one eligible actual List; the inserted value does not select a view.

For-loops over opaque collection values consume the authored iterator operation and its static family selection.
Exact BCL enumerable sources can use the CLR enumeration protocol directly. Kotlin iterator consumers receive
the protocol adapter because they expect `hasNext()` and `next()`; this adapter does not replace the collection value.

## Layer ownership

- The stdlib declares collection identities, bound members, and real fallback bodies.
- bir2cir applies type/member substitutions and chooses the appropriate iteration protocol.
- ilemit emits the resolved CIR, adapter type included; it synthesizes no member and recognizes no Kotlin collection symbol.
- dll2klib maps CLR collection signatures back to their Kotlin-facing types for source consumption.

These responsibilities follow [architecture.md](architecture.md). User-visible equality, mutability, and representation rules are documented in [dotkt-semantics.md](dotkt-semantics.md).

## Required properties

Collection changes must preserve:

- lazy, single-pass enumeration without skipped or duplicated elements;
- stable repeated `hasNext()` calls;
- disposal of CLR enumerators;
- structural Kotlin equality and stringification where specified;
- read-only versus mutable interface distinction;
- generic value-type support without boxing-driven signature changes;
- implementation by user Kotlin classes and consumption by ordinary .NET APIs.

Regression coverage lives in the collection fixtures under `tests/basic/` and the interop fixtures under `tests/interop/`.

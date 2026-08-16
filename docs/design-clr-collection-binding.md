# CLR collection binding

Status: implemented design record. This document records the collection identities and iterator boundary that remain part of the current architecture.

## Public type mapping

| Kotlin type | CLR representation |
|---|---|
| `Iterable<T>` | `IEnumerable<T>` |
| `Collection<T>` | `IReadOnlyCollection<T>` |
| `MutableCollection<T>` | `ICollection<T>` |
| `List<T>` | `IReadOnlyList<T>` |
| `MutableList<T>` | `IList<T>` |
| `Set<T>` | `IReadOnlySet<T>` |
| `MutableSet<T>` | `ISet<T>` |
| `Map<K,V>` | `IReadOnlyDictionary<K,V>` |
| `MutableMap<K,V>` | `IDictionary<K,V>` |

Factories such as `listOf`, `mutableListOf`, and `mapOf` return ordinary BCL implementations through these interface views. The mapping is declared by stdlib `@ClrTypeAlias` and `@ClrIntrinsic` metadata and applied by bir2cir; kotc does not hard-code it.

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

## Member behavior

Members with direct BCL equivalents are substituted from stdlib metadata, for example `size` to `Count` and indexed access to the CLR indexer. Members without a one-to-one BCL operation retain real Kotlin bodies that use bound primitive members. This keeps stdlib policy in the stdlib rather than adding symbol recognition to the compiler.

For-loops over BCL-bound collections lower to the CLR enumeration protocol. An explicit Kotlin `iterator()` call still receives the Kotlin adapter, because its caller expects `hasNext()` and `next()`.

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

# Compiling the real Kotlin standard library for the CLR (Path B)

Status: **design + spike done; groundwork landed (the `<DotKtKotcOptions>` flag channel).**

## Goal

Stop hand-lowering stdlib functions one at a time (the `COLLECTION_OPS` catalog in `BirMappings.kt` — ~50 functions,
hitting its ceiling: ranges aren't iterable, mutable ops missing, `Map` iteration missing). Instead **compile the real
`kotlin-stdlib` source through DotKt** — the same "compile, don't reimplement" strategy used for the kotlinx libraries
(see memory `dotkt-compile-kotlin-libraries`, `dotktx-coroutines-path-b`). Hundreds of stdlib functions then arrive with
correct Kotlin semantics, for free.

## The key finding (from the spike)

**The stdlib is not one thing — it is `common` + a platform `actual` layer.** The common source declares ~**184
`expect`** types/functions (`Int`, `String`, `Array`, `ArrayList`, `Comparator`, `NoSuchElementException`, …). Each
platform supplies the `actual`s: Kotlin/JVM does it with `actual typealias ArrayList<E> = java.util.ArrayList<E>` etc.
**DotKt must supply the CLR `actual`s — binding to BCL** (`ArrayList → System.Collections.Generic.List`,
`Comparator → IComparer`, `Int → System.Int32`, `String → System.String`, …). DotKt **already** provides many of these
implicitly in the compiler (`Int→int`, `String→string`, `Array→T[]`); the work is the rest (collections, comparator,
exceptions) plus a few intrinsics. This is exactly the "type-binding table" keystone — and it turns out to be Kotlin's
own `expect`/`actual` mechanism.

So compiling the stdlib means: **compile the common source + provide a CLR platform-actuals source set + exclude the
JVM platform files + a handful of intrinsics + the bootstrap compiler flags.** It is a *finite, mapped* task, not an
open-ended slog.

## Evidence: the error funnel

Naively building `runtime/stdlib/stdlib.ktproj` produced ~4002 errors. They triage almost entirely to configuration:

| step | errors | what it was |
|---|---|---|
| naive build (source was for Kotlin 2.3/2.4) | 4002 | version skew vs the embedded 2.2.0 compiler |
| → pin source to **2.2.0** | 1738 | matched the compiler |
| → `-Xallow-kotlin-package` | (incl. above) | the `kotlin` package guard (the stdlib IS allowed) |
| → `-opt-in=kotlin.contracts.ExperimentalContracts -opt-in=kotlin.ExperimentalMultiplatform` | 1283 | the stdlib opts into its own internal APIs |
| → `-Xcontext-parameters` | 1283 | context parameters in some declarations |
| → **with `-classpath kotlin-stdlib.jar`** (current MSBuild build) | **280** | the jar resolves the platform layer (a bootstrap crutch) |

After the flags, **none** of the remaining errors are package-guard / annotation-applicability / opt-in noise — they are
the real work: the platform `actual` layer, a few intrinsics, and Enum bootstrap.

The residual ~280 (with the jar) / ~1283 (standalone) break down as:
- **platform `actual` layer** — `ArrayList`/`LinkedHashMap`/`Comparator`/`NoSuchElementException`, `kotlin.jvm.*` /
  `java.*` references (45 JVM-coupled files to exclude). → provide CLR actuals / bind to BCL.
- **intrinsics** — `copyInto`, `concatToString`, `contentToString`, `not`, `shl`, … → provide as DotKt intrinsics.
- **Enum bootstrap** — `no value passed for parameter 'name'/'ordinal'`: DotKt injects `name`/`ordinal` into enums, which
  collides with the stdlib's own `Enum` definition. → reconcile.
- **multiplatform** — `… can only be used in common module sources`: expect/actual split. → file-set / flags.
- `internal` access conflicts (with the jar on classpath) are an artifact of the jar crutch — they vanish once we drop
  the jar and provide CLR actuals.

## Groundwork that landed

- **`<DotKtKotcOptions>`** — a project may pass raw kotc flags through the shared pipeline
  (`packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`, appended to the compile command). `stdlib.ktproj` uses it
  for the bootstrap flags above. Verified: it collapses the config-noise band to zero.
- **`runtime/stdlib/stdlib.ktproj`** builds with the **in-repo toolchain** (`Sdk="Microsoft.NET.Sdk"` +
  `<Import ../../cases/KotlinClr.targets>`), not a published `DotKt.Sdk` package — the stdlib is a first-party repo
  component; using the published SDK would be circular and fight the NuGet same-version cache. It sets
  `<KotlinClrRuntimeRef>false</KotlinClrRuntimeRef>` (the stdlib is more fundamental than DotKt.Runtime).

## Plan (sequencing)

1. ✅ Bootstrap flag channel (`<DotKtKotcOptions>`) + 2.2.0-pinned source.
2. **Drop the `-classpath kotlin-stdlib.jar` crutch for this project** so the platform-layer gaps are explicit (a way to
   suppress the default stdlib classpath for the stdlib project itself — a small targets knob).
3. **CLR platform-actuals source set** — the 184-`expect` binding table: enumerate the expects, mark which the compiler
   already binds vs which need a new `actual` (collections → BCL, `Comparator → IComparer`, exceptions → BCL). This is
   the keystone.
4. **Exclude the 45 JVM-coupled files** (`kotlin/jvm/**`, `java.*` users).
5. **Intrinsics** — `copyInto`/`concatToString`/`not`/`shl`/… as DotKt lowerings.
6. **Enum bootstrap** reconciliation (`name`/`ordinal`).
7. Retire the `COLLECTION_OPS` hand-catalog as the compiled functions take over (keep a fast-path only where it pays).

## How the compiled stdlib is consumed

`DotKt.Stdlib.dll` (the compiled `src` common + `clr` actuals) is **auto-referenced by every `.ktproj`** via the SDK
— exactly the role DotKt.Runtime plays today. A user's `listOf(1).map { it*2 }` then emits **calls into
DotKt.Stdlib's compiled methods** instead of the hand-lowering catalog; the collection TYPES are `@Clr`-bound to BCL
(below), so values flow as BCL types and the compiled functions operate on them.

Frontend resolution falls out for free: **DotKt.Stdlib is self-describing by construction.** Because it is built with
DotKt's OWN compiler, the round-trip metadata (`[KotlinFunction]` / `[KotlinFileClass]` / NRT, embedded per assembly —
memory `metadata-attrs-embedded-nrt-nullability`) is emitted automatically, so kotc resolves `kotlin.*` against
DotKt.Stdlib via facadegen (the round-trip path, memory `interop-surface-complete`) — **no kotlin-stdlib.jar in the
consumer**. The model is simply **"forward `kotlin.*` to DotKt.Stdlib"**: the compiler's existing `kotlin.*`
special-casing flips from "hand-lower to LINQ/intrinsic" to "resolve + call the compiled stdlib method."

The jar survives only as a **bootstrap crutch for compiling the stdlib ITSELF** (and even that goes away once the `clr`
actuals provide the platform layer, so `src + clr` compiles standalone). Consuming the stdlib never needs it.

Because the stdlib exercises the *entire* language surface (generics, extensions, operators, inline, …), it is the
ultimate stress test of the round-trip metadata — expect `interop-surface-complete` / `kotlin-modifier-roundtrip` gaps
to surface here.

**Hybrid:** the hot ops (`map`/`filter` → LINQ) stay hand-lowered as a fast-path (inlined, faster); the long tail comes
from DotKt.Stdlib. So `COLLECTION_OPS` is demoted to an optimization, not fully retired.

## Binding actuals: the `@Clr*` attribute family (stub types)

The `clr` actuals are **stub types** — ambient/external Kotlin declarations bound to a BCL type, resolved by the
frontend but NOT emitted (codegen redirects to the real .NET type). **This stub mechanism already exists**: a
`@Clr("System.X")` source class is filtered out of emission (`BirEmitter.kt`, `clrName(it) == null` guard) and
references to it map to `clr:System.X`. So a hand-written `@Clr` stub works today — the injected facadegen types use the
same path.

**Member-name lowering already works — no new attribute is needed.** `@Clr` is already applicable to FUNCTION and
PROPERTY (`@Target(CLASS, FUNCTION, PROPERTY)`), and the call emitter already uses `clrName(callee) ?: name` /
`clrName(prop) ?: name` (`BirEmitter.kt:3075/3090/3103`). So `@Clr("Add")` on a member lowers the call to the .NET name.
**Verified end-to-end**: a hand-written `@Clr("System.Text.StringBuilder") class Buf { @Clr("Append") fun add(s); @Clr("ToString") fun render() }`
runs (`b.add("hi").add("!"); b.render()` → "hi!"). So the `clr` actuals are written with `@Clr` alone:

```kotlin
@Clr("System.Collections.Generic.List`1")   // class: stub, not emitted, redirected to the BCL type
actual class ArrayList<T> {
    @Clr("Add")   actual fun add(e: T)        // member: call lowers to List.Add
    @Clr("Count") actual val size: Int        // property: lowers to List.Count
}
```

Operators already map too (`op_*` handling at the call site). The remaining `@Clr*` ideas (`@ClrIndexer` for Kotlin
`get`/`set` ↔ .NET indexer, `@ClrCtor` for ctor-signature selection) are refinements to add only where a stdlib member
needs them — not prerequisites. This is a GENERAL idiomatic-.NET-binding mechanism; the stdlib is its first big consumer.

## The `clr` actual worklist (from the vendored src)

The vendored `src` declares **65 `expect`s**. Classified:

- **~25 primitive / builtin** (`Int`/`Long`/…/`Array`/`*Array`/`Comparable`/`CharSequence`/`Any`/`Nothing`/`Unit`/
  `Number`/`String`) — the compiler already binds these. **Exclude their src files** (ktproj `KotlinCompile` glob); the
  compiler builtin satisfies references.
- **~40 library expects = the `clr` actuals to write**, prioritized:
  1. **Collections (the keystone, ~14)** — `Iterable`/`Iterator`/`Collection`/`List`/`ListIterator`/`Set`/`Map` +
     the `Mutable*` variants. Bind to BCL: `Iterable→IEnumerable`, `Iterator→IEnumerator`, `List→IReadOnlyList`,
     `MutableList→IList`, `Set→IReadOnlySet`, `Map→IReadOnlyDictionary`, `MutableMap→IDictionary`, etc. This is the
     user's original pain (mutable collections, `Map` iteration) and unblocks most of `_Collections.kt`/`_Maps.kt`.
  2. **Text (~2)** — `Appendable`/`StringBuilder` → `System.Text.StringBuilder` (resolves the `append`/`appendTwoDigits`
     unresolved refs too).
  3. **Map onto existing DotKt types** — `AtomicBoolean/Int/Long/Reference` → `DotKtx.Atomicfu.*`; `SafeContinuation` →
     `DotKt.Coroutines`; `AutoCloseable` → `System.IDisposable`.
  4. **Defer / stub** — the reflection cluster `KClass`/`KCallable`/`KFunction`/`KProperty*`/`KType` (~11): a separable
     sub-area, low initial value; minimal stubs or exclude until reflection is needed.
  5. **Misc, case by case** — `Annotation`, `MonotonicTimeSource`, `PlatformSpecific`, `EnumEntriesSerializationProxy`,
     `ReadObjectParameterType`, `ValueTimeMarkReading`.

Each actual is `@Clr("<BCL type>") actual <class|fun interface> X { @Clr("<BCL member>") actual ... }` — the verified
stub+rename mechanism. Layout: `runtime/stdlib/clr/<X>.kt` (platform fragment), `src/**` marked common via
`-Xcommon-sources`, the build standalone (drop the `kotlin-stdlib.jar` crutch once the actuals cover the platform layer).

## Decide before the collections actuals: the iterator protocol

Not every collection type is a clean `@Clr`-to-BCL bind. `expect interface Iterator<out T>` is Kotlin's protocol
(`next(): T` + `hasNext(): Boolean`), which does NOT match .NET `IEnumerator` (`MoveNext(): bool` + `Current`) — the
shapes differ, so a name-map isn't enough. **DotKt already represents the Kotlin iterator protocol** via the
monomorphized synthetic `@KIterator_<elem>` / `@KIterable_<elem>` interfaces (`birType`'s `iteratorElemIface` /
`iterableElemIface`, and the `for (x in xs)` lowering). So the `Iterator`/`Iterable` actuals should bind to / reuse that
machinery (a DotKt-side `IKIterator`-style interface), NOT raw `IEnumerable`/`IEnumerator`. Concrete collections
(`List`→`IReadOnlyList`, `MutableList`→`IList`, `Map`→`IReadOnlyDictionary`) bind more directly (members map by
`@Clr` name) but must still yield Kotlin iterators. This protocol reconciliation is the first design decision of the
collections actuals — resolve it before writing them.

## Build attempt — empirical results and the remaining blocker

A focused build attempt established the working configuration and the precise blocker.

**Working flag set** (collapses all config noise to zero): `-no-stdlib -Xallow-kotlin-package
-opt-in=kotlin.contracts.ExperimentalContracts -opt-in=kotlin.ExperimentalMultiplatform -Xcontext-parameters
-Xbuiltins-from-sources`. The stdlib must compile **as one module from source** (both the jar-resolution and
builtins-from-sources routes confirm this): with the jar on the classpath, source files can't access the jar's
`internal` members ("cannot access X: it is internal in file"); standalone, the builtins must come from source — hence
`-Xbuiltins-from-sources`, which is the right mode (`Int`/`String`/… then resolve from `Primitives.kt`/`String.kt`).

**Funnel on a 28-file builtin closure** (core types + annotations + experimental, excluding the Kotlin/Native files):
4002 (version skew) → 1283 (flags) → **10 errors**, all of ONE kind: `no value passed for parameter 'name'/'ordinal'`
at enum entries (`DeprecationLevel`, `AnnotationTarget`, `AnnotationRetention`, `OptIn.Level`).

**The blocker — the Kotlin builtins bootstrap.** Under `-Xbuiltins-from-sources`, the standard FIR frontend's enum-entry
→ `Enum(name, ordinal)` super-call synthesis does NOT fire for the SOURCE `expect class Enum` (it special-cases the
*builtin* Enum), so the entries are flagged as missing `name`/`ordinal`. This is standard Kotlin frontend behavior (kotc
reuses `JvmFrontendPipelinePhase` verbatim — `ClrCliPipeline` swaps only the backend), not a kotc-specific bug. The real
Kotlin stdlib build sidesteps it by **serializing the builtins** (`Any`/`Int`/`Enum`/… → `.kotlin_builtins`) in a
separate pre-pass and compiling the rest against those — it does NOT run `-Xbuiltins-from-sources` over the whole stdlib.

So completing the build needs one of:
1. **Replicate the builtins-serialization bootstrap** — compile the builtin closure to serialized builtins first, then
   the rest against them. The proper path; substantial (mirrors the Kotlin build's builtins pipeline).
2. **A kotc FIR fix** — make the enum-entry synthesis recognize the source `Enum` under `-Xbuiltins-from-sources` (or
   suppress the spurious diagnostic, since DotKt's backend synthesizes `__name`/`__ordinal` itself from the entry index
   and never uses the source super-call). A FIR additional-checkers / synthesis extension in `ClrCompilerPluginRegistrar`.

Beyond the builtin closure, the full stdlib still needs the platform `actual` layer (the ~40 clr actuals) and the
collections iterator-protocol reconciliation — the build is gated FIRST on the builtins bootstrap above.

## Open questions

- **Builtins boundary**: which `expect`s are "compiler builtins" (Int/String/Array — already bound, exclude the source
  file) vs "library actuals we author" (collections/comparator)? The 184-expect enumeration (step 3) answers this.
- **One assembly or split?** The stdlib could be one `DotKt.Stdlib` assembly; the kotlinx libraries stay their own
  `dotktx.*` (memory `refactor-stdlib-types-out-of-coroutines`). The metadata attrs are already embedded per-assembly
  (memory `metadata-attrs-embedded-nrt-nullability`), and `Fmt` was removed — DotKt.Runtime keeps shrinking toward
  "compiler runtime-support only," with `DotKt.Stdlib` as the user-facing standard library.

# Compiling the real Kotlin standard library for the CLR (Path B)

Status: **design + spike done; groundwork landed (the `<DotKtKotcOptions>` flag channel).**

## THE CANONICAL ROADMAP (design owner, 2026-06-25) — read this first

The end state: **this becomes a normal Kotlin compiler that happens to ship a CLR version of the stdlib.** The compiler
does NOT special-case stdlib types/ops. Getting there has a strict order — and the cardinal rule
([[stdlib-compile-retires-lowerings-never-adds]]): **the fix for "the stdlib won't compile/emit" is ALWAYS on the
stdlib side, NEVER a new compiler lowering / denylist / ilemit stub** (the whole point of this work is to RETIRE the
compiler's filter-lowerings, not add more).

1. **Assemble the CLR stub `actual`s, in Kotlin, inside the stdlib.** Every platform `expect` and every JVM/runtime type
   the common stdlib source references (`Random`, `Grouping`, `StringBuilder`, ranges, the array/collection helpers) gets
   a Kotlin declaration in the CLR source set (`runtime/stdlib/clr/`) — a stub body (`= TODO()`) is fine at this step.
   This makes the library syntactically whole so it compiles + emits.
2. **Fill in the annotations that lower each stub `actual` to its CLR type.** Annotate the stubs (`@ClrIntrinsic("System.Text.StringBuilder")`,
   …) so the compiler's EXISTING @ClrIntrinsic/injection lowering turns them into the BCL type/call. This is purely stdlib-side
   work; when it's done **the stdlib actually works end to end** (no TODO throws left on the hot paths).
3. **Reverse direction — FIR injection read-as.** When a .NET type arrives FROM the CLR and is injected into FIR, read it
   AS the corresponding Kotlin stdlib type (the `IEnumerable<T>`→`Iterator<T>`/`Iterable` reading already partly done).
   Now CLR collections flow into Kotlin code as the Kotlin types.
4. **Result:** the compiler is free to just compile Kotlin against this CLR stdlib — a normal Kotlin compiler with a CLR
   stdlib. The `COLLECTION_OPS` / type special-cases get retired, not extended.

What I must NOT do (and tried, wrongly — all reverted): a compiler `UNMAPPED_STDLIB_TYPES` denylist, a stdlib-op skip,
an ilemit partial-IL stub-on-failure. Those add compiler 固有実装 for the STDLIB's own behaviour types/ops — exactly the
thing the stdlib is meant to eliminate.

### The layers (where each kind of mapping legitimately lives)

The design owner (2026-06-25) drew the line: "最終的に一部の java.* package みたいなやつはコンパイラ側で固定の lowering を
持たないといけない気がするけど、それは stdlib とはまた違うレイヤーの話".

- **Stdlib layer (Kotlin, in `runtime/stdlib/clr/`):** the `kotlin.*` expect classes get a CLR `actual` — and the
  idiomatic form is exactly JVM's: an **`actual typealias`** to the underlying platform type, e.g.
  `actual typealias HashSet<E> = java.util.HashSet<E>` (mirrors `TypeAliasesJVM.kt`). `kotlin.*` behaviour types with no
  platform analogue (`Grouping`) are just their real common source, emitted.
- **Compiler `java.*` layer (legitimate, FIXED, separate from the stdlib):** a small fixed set of `java.*` → BCL
  lowerings — `java.util.HashSet`/`LinkedHashSet` → `System.Collections.Generic.HashSet`, `java.util.HashMap`/… →
  `Dictionary`, `java.lang.StringBuilder` → `System.Text.StringBuilder`, etc. These are NOT the stdlib's job (the stdlib
  typealiases its `kotlin.*` names *to* these `java.*` names; the compiler then lowers the `java.*` names to the BCL).
  This is the SAME role as the existing foundational `kotlin.collections.List/Set/Map → BCL` mapping — a fixed interop
  layer, not a per-op 固有実装 that grows. So `isSetType += java.util.HashSet` is correct *as a java.\* lowering*; a
  denylist or a `kotlin.collections.HashSet` special-case is not.
- **@ClrIntrinsic / FIR-injection layer:** @ClrIntrinsic-annotated declarations + the reverse read-as (CLR `IEnumerable<T>` injected →
  `Iterator`/`Iterable`), so .NET types flow in as the Kotlin stdlib types.

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

## How the compiled stdlib is consumed — the pipeline's three references

The pipeline is **4 layers** (kotc → bir2cir → ilemit), and the stdlib is consumed through **three distinct
references**, one per stage (ship-tasks.md §0):

- **kotc → `stdlib.jar`** (the frontend jar built from the CLR stdlib sources). kotc resolves `kotlin.*` symbols against
  it — Kotlin space only, **no CLR knowledge**, and no JVM `kotlin-stdlib.jar` leak. A user's `listOf(1).map { it*2 }`
  resolves to the real stdlib functions here.
- **bir2cir → `DotKt.Private.Stdlib.dll`** (the *ref* dll: pure `kotlin.*` with **every `@ClrIntrinsic` attribute
  preserved**). bir2cir reads the `@ClrIntrinsic` labels and **substitutes the BCL call into CIR**; the collection TYPES
  are `@ClrIntrinsic`-bound to BCL (below), so values flow as BCL types and the compiled functions operate on them.
  This `@ClrIntrinsic` substitution is **bir2cir's responsibility (currently in BirEmitter, being migrated** —
  ship-tasks.md §6 tracks it as a current layer violation).
- **ilemit → `DotKt.Stdlib.dll`** (the *rt* dll: the emitted implementation). ilemit is **Kotlin-free** — it never sees
  an `@ClrIntrinsic` label.

Frontend resolution falls out for free: **the stdlib is self-describing by construction.** Because it is built with
DotKt's OWN compiler, the round-trip metadata (`[KotlinFunction]` / `[KotlinFileClass]` / NRT, embedded per assembly —
memory `metadata-attrs-embedded-nrt-nullability`) is emitted automatically, so facadegen recovers the Kotlin semantics
from the ref dll (the round-trip path, memory `interop-surface-complete`). The model is **"resolve `kotlin.*` against the
stdlib jar, then substitute `@ClrIntrinsic` → BCL at bir2cir"** — replacing the old "hand-lower to LINQ/intrinsic in the
compiler."

Because the stdlib exercises the *entire* language surface (generics, extensions, operators, inline, …), it is the
ultimate stress test of the round-trip metadata — expect `interop-surface-complete` / `kotlin-modifier-roundtrip` gaps
to surface here.

## Binding actuals: the `@ClrIntrinsic*` attribute family (stub types)

The `clr` actuals are **stub types** — ambient/external Kotlin declarations bound to a BCL type, resolved by the
frontend but NOT emitted (codegen redirects to the real .NET type). **This stub mechanism already exists**: a
`@ClrIntrinsic("System.X")` source class is filtered out of emission (`BirEmitter.kt`, `clrName(it) == null` guard) and
references to it map to `clr:System.X`. So a hand-written `@ClrIntrinsic` stub works today — the injected facadegen types use the
same path.

**Member-name lowering already works — no new attribute is needed.** `@ClrIntrinsic` is already applicable to FUNCTION and
PROPERTY (`@Target(CLASS, FUNCTION, PROPERTY)`), and the call emitter already uses `clrName(callee) ?: name` /
`clrName(prop) ?: name` (`BirEmitter.kt:3075/3090/3103`). So `@ClrIntrinsic("Add")` on a member lowers the call to the .NET name.
**Verified end-to-end**: a hand-written `@ClrIntrinsic("System.Text.StringBuilder") class Buf { @ClrIntrinsic("Append") fun add(s); @ClrIntrinsic("ToString") fun render() }`
runs (`b.add("hi").add("!"); b.render()` → "hi!"). So the `clr` actuals are written with `@ClrIntrinsic` alone:

```kotlin
@ClrIntrinsic("System.Collections.Generic.List`1")   // class: stub, not emitted, redirected to the BCL type
actual class ArrayList<T> {
    @ClrIntrinsic("Add")   actual fun add(e: T)        // member: call lowers to List.Add
    @ClrIntrinsic("Count") actual val size: Int        // property: lowers to List.Count
}
```

Operators already map too (`op_*` handling at the call site). The remaining `@ClrIntrinsic*` ideas (`@ClrIndexer` for Kotlin
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

Each actual is `@ClrIntrinsic("<BCL type>") actual <class|fun interface> X { @ClrIntrinsic("<BCL member>") actual ... }` — the verified
stub+rename mechanism. Layout: `runtime/stdlib/clr/<X>.kt` (platform fragment), `src/**` marked common via
`-Xcommon-sources`, the build standalone (drop the `kotlin-stdlib.jar` crutch once the actuals cover the platform layer).

## Decide before the collections actuals: the iterator protocol

Not every collection type is a clean `@ClrIntrinsic`-to-BCL bind. `expect interface Iterator<out T>` is Kotlin's protocol
(`next(): T` + `hasNext(): Boolean`), which does NOT match .NET `IEnumerator` (`MoveNext(): bool` + `Current`) — the
shapes differ, so a name-map isn't enough. **DotKt already represents the Kotlin iterator protocol** via the
monomorphized synthetic `@KIterator_<elem>` / `@KIterable_<elem>` interfaces (`birType`'s `iteratorElemIface` /
`iterableElemIface`, and the `for (x in xs)` lowering). So the `Iterator`/`Iterable` actuals should bind to / reuse that
machinery (a DotKt-side `IKIterator`-style interface), NOT raw `IEnumerable`/`IEnumerator`. Concrete collections
(`List`→`IReadOnlyList`, `MutableList`→`IList`, `Map`→`IReadOnlyDictionary`) bind more directly (members map by
`@ClrIntrinsic` name) but must still yield Kotlin iterators. This protocol reconciliation is the first design decision of the
collections actuals — resolve it before writing them.

## Build status (RESOLVED) — the stdlib builds clean

The stdlib now builds via the **jar-frontend route**: **ref + rt + frontend jar all build with 0 errors** (the
Milestone-0 emit crash is resolved). kotc resolves the builtins/platform layer against the CLR-built `stdlib.jar`,
which sidesteps the `-Xbuiltins-from-sources` bootstrap entirely.

> **History (no longer on the critical path).** An earlier standalone route (`-Xbuiltins-from-sources`, one module from
> source) hit the Kotlin **builtins-serialization bootstrap**: the FIR enum-entry → `Enum(name, ordinal)` super-call
> synthesis does NOT fire for a SOURCE `expect class Enum`, so the builtin closure failed with `no value passed for
> parameter 'name'/'ordinal'` (`DeprecationLevel`/`AnnotationTarget`/…). Diagnostic suppression was a dead-end (each
> suppressed layer surfaced the next — `multiPlatform` then demanded actuals for `expect class Throwable`/`Unit`/…), and
> the two complementary walls (`-Xbuiltins-from-sources` enum synthesis vs. `-classpath kotlin-stdlib.jar` cross-module
> `internal`-access errors) made "one module, from source" look mandatory. The proper long-term shape — a two-pass
> builtins-serialization build (pass 1 → a `DotKt.Builtins` assembly, pass 2 against it) — remains the cleaner
> direction, but the jar-frontend route unblocks the build without it.

## Realized: a first DotKt.Stdlib.dll slice (the growth approach)

A **working `DotKt.Stdlib.dll` is built from real vendored Kotlin stdlib source** by `scripts/build-stdlib.sh` — the
first step of Path B. It compiles the stdlib files whose dependency closure is entirely builtins + .NET-mappable
(`kotlin.collections.IndexedValue<T>`, `kotlin.KotlinVersion`, `kotlin.experimental.BitwiseOperations`, the annotation
classes, …) using the **jar for frontend resolution** (no `-Xbuiltins-from-sources`, sidestepping the multi-layer
builtins bootstrap), then ilemit emits the assembly. The DLL loads in .NET, carries real `kotlin.*` types, and is
self-describing (embedded round-trip metadata).

This is deliberately a SLICE. The goal isn't a single-shot full-stdlib compile — it's to **progressively replace the
compiler's hand-written lowerings (the `COLLECTION_OPS` catalog, the builtin mappings) with the real Kotlin
implementation, removing each lowering as its source compiles in.** Growing the slice = (1) pull in more stdlib files,
(2) make them compile by binding the platform layer (the `clr` actuals) and/or adjusting the compiler (the builtins
bootstrap, intrinsics — compiler changes are EXPECTED here, not avoided), (3) retire the matching hand-lowering. The
builtins bootstrap (above) is the gating compiler work for the deepest layer; the self-contained leaf files compile
today without it.

## Proven: retiring a hand-written collection lowering for real Kotlin (the migration seam)

The end-to-end mechanism for moving a collection op off its `COLLECTION_OPS` LINQ lowering onto real Kotlin source is
**proven working** (op: `getOrElse`):

1. Compile the real Kotlin op into a DotKt library: `public inline fun <T> List<T>.getOrElse(index, defaultValue) =
   if (index >= 0 && index <= size - 1) this[index] else defaultValue(index)`. ilemit stamps it with `[KotlinInline]`
   **carrying the BIR body** (verified — the attribute's ctor arg is the BIR JSON), and facadegen reads it back into the
   injection metadata as `tlfun getOrElse … inline,ext`.
2. Drop `getOrElse` from `COLLECTION_OPS` (`BirMappings.kt`).
3. The backend's "unsupported stdlib function" guard (`BirEmitter.kt`, ~3485) now **defers to the round-trip registry**:
   if `ClrTopLevelRegistry.lookup(fqn) != null` (the op is provided by a referenced DotKt.Stdlib) it does NOT error, and
   falls through to the round-trip path (~3494) which emits a `clrGenericStatic` call to (or inline-splices) the real
   body. **This guard change is the committed enabler** — a safe no-op until a stdlib op is actually injected.

Result (throwaway harness): user `listOf(10,20,30).getOrElse(1){…}` / `getOrElse(5){…}` compiled with `Enumerable`
(LINQ) count = 0, two `clrGenericStatic` calls into `kotlin.collections.CollKt`, and ran `20` / `500`. The LINQ lowering
was genuinely replaced by the real Kotlin implementation.

**Why this is `[KotlinInline]`-powered (per the design owner): `[KotlinInline]` injects the body in BIR form, so the
current compiler splices inline functions originating from a CLR DLL correctly. Removing `COLLECTION_OPS` entries one at
a time therefore routes each call to the real stdlib source.**

### Remaining productionization (all-or-nothing per op)
Removing an op from `COLLECTION_OPS` breaks every build that doesn't reference the stdlib (the guard fires). So the
op's migration must land together with the **three pipeline references** wired through every `.ktproj` and the verify
harnesses:
- kotc resolves the op against **`stdlib.jar`** (the frontend jar),
- bir2cir reads the op's `@ClrIntrinsic` labels from **`DotKt.Private.Stdlib.dll`** (the ref dll) and substitutes the
  BCL calls,
- ilemit emits against **`DotKt.Stdlib.dll`** (the rt dll),

then the `COLLECTION_OPS` removal + a regression case land. (The untracked vendored `runtime/stdlib/src` is the SOURCE we
copy ops from as we migrate.)

Random-access ops (`first`/`last`/`getOrElse`/`get`/`indexOf`/`isEmpty`/`single`/…) are migratable now (no iteration).
Iteration ops (`map`/`filter`/`fold`/…) additionally need the `Iterable`→`IEnumerable` reconciliation (today Kotlin
`Iterable` is the synthetic monomorphized `<>dotkt_KIterable`).

## Compiling the REAL generated `_Collections.kt` — the recipe (2026-06-25)

The user supplied the generated stdlib source under `runtime/stdlib/common/src/generated/` (`_Collections.kt` = 3793
lines, `_Maps.kt`, `_Sequences.kt`, …). Hand-writing the ops is OUT (guessed signatures are wrong — `reduce` is
`<S, T : S>`, `count` has a `Collection<T>` overload, …). Compiling the real source needs its dependency closure + the
right module flags. Established recipe (errors 119 → 15):

1. **Include the internal-helper + internal-annotation source** (not just `_Collections.kt`): `kotlin/internal/*.kt`
   (gives `@InlineOnly`/`@OnlyInputTypes`/…), all of `kotlin/collections/*.kt` (gives `collectionSizeOrDefault`,
   `checkIndexOverflow`, `mapCapacity`, `optimizeReadOnlyList`, …), `IndexedValue.kt`, `SlidingWindow.kt`. **Source
   shadows the JAR** — an `internal` member resolves to the in-module source copy, so cross-module "internal in file"
   errors clear when the defining file is in the set.
2. **Mark ALL the included files common** with `-Xcommon-sources=<every file>`. This is the crux: marking only SOME
   common splits the module and re-breaks internal access (InlineOnly resorts to the JAR); marking EVERYTHING common
   puts the generated common files AND their internal helpers in one module, so both internal access AND the
   `@JvmName`/`@JvmMultifileClass` `@OptionalExpectation` annotations resolve. (In upstream the internal helpers are
   common too.)
3. **opt-ins:** `-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi`
   plus `-Xallow-kotlin-package`, classpath = the kotlin-stdlib.jar (for the builtins only).

The remaining **15 errors are all JVM-platform-specific** — the CLR platform-actual layer to provide:
`collectionToArray`/`copyToArrayOfAny`/`arrayOfNulls(reference,size)` (java array helpers → CLR arrays),
`java.io.Serializable` (drop / empty marker), `toSingletonMap`/`appendElement` (small helpers),
`ConstrainedOnceSequence` (include `SequencesH.kt`), and `@Volatile` in `AbstractMap.kt` (needs
`kotlin.concurrent.Volatile` for multiplatform). These are the "細かな Primitive" to bind on CLR. After the frontend
compiles, the BACKEND (ilemit) pass over the full `_Collections.kt` bodies is the next phase (the ops call one another
+ internal helpers — each must lower/route/emit). See [[stdlib-use-real-generated-source]].

### The platform-actual layer (the remaining frontend work)

Past the recipe above, `_Collections.kt` (119 → ~10) needs CLR `actual`s for the multiplatform `expect`s the common
collection source declares. Found + written so far (`runtime/stdlib/clr/`):
- factories `listOf(e)`/`setOf(e)`/`mapOf(pair)`, builders `buildList/Map/SetInternal`, `Array.asArrayList`
- internal helpers `checkIndexOverflow`/`checkCountOverflow`/`mapCapacity`, `MutableList.reverse`
- array bridges `collectionToArray`(×2)/`terminateCollectionToArray`/`copyToArrayOfAny`/`arrayOfNulls(ref,size)`
- `kotlin.internal` serialization stubs (`throwReadObjectNotSupported`/`wrapAsDeserializationException`/`ReadObjectParameterType`)
- `kotlin.io.Serializable` (empty marker — JVM `java.io.Serializable` has no CLR equivalent)
- `kotlin.sequences.ConstrainedOnceSequence`

KEY structural rule: actuals go in the PLATFORM source set (NOT in `-Xcommon-sources`), matching the package of the
`expect` exactly (`ConstrainedOnceSequence` → `kotlin.sequences`, the serialization stubs → `kotlin.internal`,
`Serializable` → `kotlin.io`). Helpers whose `expect` file is NOT pulled in resolve via a plain `internal fun` that
shadows the JAR.

NOT yet done — **`CollectionsH.kt`'s full platform surface (~25 `expect`s)**: `RandomAccess`, `toTypedArray`, `sort`,
`sortWith`, `shuffle`/`shuffled`, `fill`, `orEmpty`, `binarySearch`, … Each needs a CLR `actual` (mostly array/list ops
over the BCL). Once the frontend is at 0, the BACKEND (ilemit) pass over the full `_Collections.kt` bodies is the final
phase (every op must lower/route/emit; the inter-op calls + internal helpers surface there). This is a large but
well-defined layer — the path to full `_Collections.kt` compilation is concrete. See [[stdlib-use-real-generated-source]].

### MILESTONE: the real `_Collections.kt` FRONTEND compiles (BIR emitted)

With the platform-actual layer written (`runtime/stdlib/clr/`), the real generated `_Collections.kt` now compiles all
the way through the frontend — **30 BIR files emitted, 0 frontend errors**. The full actual set:
- `kotlin.collections` (PlatformClr.kt, PLATFORM): `listOf(e)`/`setOf(e)`/`mapOf(pair)`, builders ×2 each (with/without
  capacity), `asArrayList`, `checkIndexOverflow`/`checkCountOverflow`/`mapCapacity`, `reverse`, `collectionToArray`×2/
  `terminateCollectionToArray`/`arrayOfNulls(ref,size)`/`copyToArrayOfAny`, `toSingletonMap`/`toSingletonMapOrSelf`, and
  the CollectionsH surface `RandomAccess`/`orEmpty`/`toTypedArray`/`fill`/`sort`/`sortWith`/`shuffle`/`shuffled`/`eachCount`.
- `kotlin.internal` (SerializationClr.kt, PLATFORM): serialization stubs.
- `kotlin.sequences` (SequencesClr.kt, PLATFORM): `ConstrainedOnceSequence`.
- `kotlin.io.Serializable` + `kotlin.text.appendElement` (COMMON shadowing — they're USED by common files, and common
  can't see platform, so a regular `internal` decl in the common set shadows the jar).

Rule of thumb learned: an `actual` for an `expect` goes in the PLATFORM set; a plain shadowing decl that COMMON code
calls goes in the COMMON set.

### Remaining: the BACKEND expect/actual call resolution

The 139 remaining errors are ALL the backend guard ("the .NET backend does not support the Kotlin stdlib function X"):
`_Collections.kt`'s bodies call `reverse`/`sort`/`sortWith`/`asList`/… which resolve to the `expect` (body == null), so
the guard (BirEmitter ~3522, meant for EXTERNAL unsupported stdlib fns) fires — even though the `actual` is in THIS
compilation. When compiling the stdlib ITSELF, these calls must emit a normal static call to the local actual, not hit
the guard. The fix is backend expect→actual resolution (or: don't fire the guard for a callee whose actual is being
compiled in-module). Plus a few more actuals (`asList`, …). This is the final phase; the frontend is done.

### Backend (ilemit) emission of the real `_Collections.kt` — the final remaining layer

kotc now compiles the real `_Collections.kt` end-to-end (frontend 0 errors → 30 BIR files, kotc finishes OK), thanks to
two committed toolchain fixes: the `DOTKT_STDLIB_COMPILE` stub-on-unsupported flag (27 backend-gap ops emit a throwing
`[DOTKT-STDLIB]` stub + stay on their COLLECTION_OPS lowering) and the NaN/±Infinity JSON-string encoding.

The JVM types the bodies reference (`java.lang.Appendable`/`java.lang.StringBuilder`, `kotlin.random.Random`, …) must
**NOT** become ilemit type maps. ilemit is **Kotlin-free** by the layer rule, and adding such a map violates the
cardinal rule (the fix is ALWAYS stdlib-side, NEVER a new compiler map/lowering). The correct fix is stdlib-side: the
platform `actual` carries an `@ClrIntrinsic` to the BCL type (`StringBuilder`, `System.Random`), and bir2cir consumes
that label to substitute the BCL call — no ilemit map.

What legitimately remains in ilemit is structural CLR codegen only (no Kotlin knowledge):
- **The Abstract* class hierarchy** (`AbstractCollection`/`AbstractList`/`AbstractMutableList`/`ArrayDeque`/…) emits with
  cross-file refs (resolve only when emitted together) and hits an ilemit "unresolved generic type parameter E/T" in
  generic CLASS emission (distinct from the method-level GenericMethod fix — class bodies need the same TypeBuilder
  generic-param registration).
- These Abstract classes are runtime impls the OPS don't all need; the minimal set is `_Collections` + the platform
  actuals + the internal-helper FUNCTIONS (`collectionSizeOrDefault` from Iterables.kt, `checkIndexOverflow` from
  Collections.kt) — but helpers and Abstract classes share files, so a clean split needs care.

See [[stdlib-platform-actuals-as-bcl-lowering]] and [[compiler-layer-responsibilities]].

## Open questions

- **Builtins boundary**: which `expect`s are "compiler builtins" (Int/String/Array — already bound, exclude the source
  file) vs "library actuals we author" (collections/comparator)? The 184-expect enumeration (step 3) answers this.
- **One assembly or split? RESOLVED — split into a ref dll + an rt dll.** The stdlib ships as
  **`DotKt.Private.Stdlib.dll`** (the *ref* dll: pure `kotlin.*` + `@ClrIntrinsic` metadata, consumed by bir2cir) plus
  **`DotKt.Stdlib.dll`** (the *rt* dll: the emitted implementation, referenced by ilemit). The kotlinx libraries stay
  their own `dotktx.*` (memory `refactor-stdlib-types-out-of-coroutines`). The metadata attrs are already embedded
  per-assembly (memory `metadata-attrs-embedded-nrt-nullability`), and `Fmt` was removed — DotKt.Runtime keeps shrinking
  toward "compiler runtime-support only," with the stdlib ref/rt pair as the user-facing standard library.

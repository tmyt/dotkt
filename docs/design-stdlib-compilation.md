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

The ONE new feature needed is **member-name lowering** (`add` → `Add`): currently a stub's members must use the .NET
name. So the `clr` actuals look like:

```kotlin
@Clr("System.Collections.Generic.List`1")          // existing: stub, not emitted, redirected to the BCL type
actual class ArrayList<T> {
    @ClrName("Add")   actual fun add(e: T)          // NEW: backend emits clrInstance("Add"), not "add"
    @ClrName("Count") actual val size: Int
}
```

The `@Clr*` family to design (the "various features"): `@Clr` type bind (have), **`@ClrName`** member rename (the
keystone), property⇔method, `@ClrIndexer` (Kotlin `get`/`set` ↔ .NET indexer), `@ClrStatic`, `@ClrOperator`, `@ClrCtor`.
This is a GENERAL idiomatic-.NET-binding mechanism; the stdlib is just its first big consumer.

Implementation of `@ClrName` (the minimal first step):
- Define `clr.ClrName(name: String)` alongside `clr.Clr` (the façade `_Clr.kt` that facadegen generates, + the per-case
  `facade.kt` pattern).
- Add `clrMemberName(callee): String` in BirEmitter (mirror `clrName` at `BirEmitter.kt:3582`, reading `clr.ClrName`,
  default = the Kotlin name).
- Use it where the .NET member name is currently the Kotlin name — the `clrInstance`/`clrStatic`/`clrPropGet`/
  `clrPropSet` emit sites (the resolved callee carries the annotation; swap the emit name only, like `@JvmName`).
- Spike: one `@Clr` + `@ClrName` stub, verify the call lowers to the mapped CLR name and runs.

## Open questions

- **Builtins boundary**: which `expect`s are "compiler builtins" (Int/String/Array — already bound, exclude the source
  file) vs "library actuals we author" (collections/comparator)? The 184-expect enumeration (step 3) answers this.
- **One assembly or split?** The stdlib could be one `DotKt.Stdlib` assembly; the kotlinx libraries stay their own
  `dotktx.*` (memory `refactor-stdlib-types-out-of-coroutines`). The metadata attrs are already embedded per-assembly
  (memory `metadata-attrs-embedded-nrt-nullability`), and `Fmt` was removed — DotKt.Runtime keeps shrinking toward
  "compiler runtime-support only," with `DotKt.Stdlib` as the user-facing standard library.

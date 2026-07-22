# DotKt — Kotlin → .NET (CLR) compiler

A compiler that runs **Kotlin on .NET**. It reuses the official Kotlin frontend
(`kotlin-compiler-embeddable` 2.4.0 — Configuration / FIR / Fir2Ir, so resolution and type
checking are the real thing) and replaces only the backend, lowering **Kotlin IR → CIL** that
runs on `dotnet`.

DotKt ships the Kotlin standard library compiled for the CLR (`DotKt.Stdlib`); you reference real
.NET assemblies and call their types directly from Kotlin.

## New here? (user documentation)

- **[Getting started](docs/user/getting-started.md)** — install the NuGet packages, `dotnet new
  dotkt-cli`, hello world, `dotnet build`/`run`.
- **[Using .NET from Kotlin](docs/user/using-dotnet-from-kotlin.md)** — `import System.X`
  façade-free interop: statics, events, delegates, `out`/`ref`, operators, enums.
- **[Kotlin on the CLR — what's different](docs/user/kotlin-on-clr-differences.md)** — the
  readable tour of deliberate deviations from Kotlin/JVM (`True`/`4` printing,
  `CharSequence`=`string`, `suspend`=`Task<T>`, …).
- **[Supported features](docs/user/supported-features.md)** — the scannable ✅/🚧/❌ matrix.
  Full doc index: [`docs/README.md`](docs/README.md).

## Backend

Direct IL, one unflagged path:

```
Kotlin IR → BirEmitter → BIR (JSON) → bir2cir → CIR (JSON) → ilemit → CIL
```

Each stage owns one concern: **kotc** (the Kotlin frontend, no CLR knowledge) emits BIR;
**bir2cir** owns the Kotlin↔CLR relation (it reads the stdlib reference dll's
`@ClrTypeAlias`/`@ClrIntrinsic` bindings and lowers Kotlin types/calls to their BCL forms);
**ilemit** is pure CLR codegen via `Reflection.Emit` (no Kotlin knowledge); **facadegen** turns
.NET metadata into FIR-injection metadata for façade-free `import System.X`. (An earlier
Kotlin-IR→C#-text oracle backend, and the interim `--compat-bir`/`--native-cir` dual-track, were
both retired and removed.)

## The stdlib is real Kotlin, compiled for the CLR

`libraries/stdlib/` holds the Kotlin standard library built as a genuine CLR assembly — not a
hand-written compiler mapping. It ships as three artifacts: a **frontend klib**
(`kotlin-stdlib-clr-frontend.klib`, what kotc resolves `kotlin.*` against), a **reference dll**
(`DotKt.Private.Stdlib.dll`, compile-time metadata carrying the `@Clr*` bindings), and the
shipping **runtime dll** (`DotKt.Stdlib.dll`). Where a Kotlin type *is* a BCL type
(`List`→BCL list interfaces, `Map`→`IDictionary`, `StringBuilder`, exceptions, …) the binding is
declared **in the stdlib source** as `@ClrTypeAlias`/`@ClrIntrinsic` metadata and applied by
bir2cir at app-emit — the compiler itself stays generic.

## What works today

The canonical gate is `make verify`: categorized NUnit suites compile related tests into shared
assemblies, execute them in-process, apply the ILVerify baseline, then run emitted-IR, stateful MSBuild,
round-trip, wide-delegate, and packaged-SDK scenarios. Test entry points live beside their fixtures under
`tests/`; reusable build and validation tools live under `scripts/`.

**Language**
- Top-level functions; primitives; arithmetic, comparison, bitwise; control flow (`if`/`when`
  with subjects/`is`/ranges, loops, labeled `break`/`continue`); string templates and raw strings
- Functions: default args (incl. cross-module, two-tier rule), `vararg`, local functions +
  closures, extension functions, `inline` (incl. cross-module non-local return), **`reified`**,
  `tailrec`, function references, generic functions/classes end-to-end
- Classes: properties (every Kotlin property is a real CLR property), primary/secondary
  constructors + `init`, inheritance, interfaces, visibility → real CLR access, `companion
  object`, nested classes, object expressions, `data class`, `value class`, `typealias`,
  `lateinit`, delegation (`by lazy`, `by map`, `ReadWriteProperty`), sealed hierarchies
- **Enums**: basic → real CLR enums; rich (ctor params/methods/bodies) → singleton classes
- Null safety: `?.`, `?:`, `!!`, smart casts, `Int?` → `System.Nullable<T>`, NRT interop
- Exceptions: `try`/`catch`/`throw` (+ as expression), `require`/`check`/`error`; Kotlin
  exception types are `@ClrTypeAlias`-bound to `System.*`

**Stdlib**
- Collections (`listOf`/`mapOf`/…, `map`/`filter`/`fold`/`groupBy`/`joinToString`/`sorted*`/…),
  strings + `Regex`, `kotlin.math`, ranges, `Pair`/`Triple`, scope functions, destructuring,
  unsigned types, `Array<T>` ops, `Result`/`runCatching`, atomics — the real stdlib bodies run
  on the CLR (lazy `Sequence`/coroutine builders are the in-progress tail)

**Kotlin ↔ .NET interop**
- `import System.X` façade-free (+ transitive closure injection of everything reachable);
  statics via `.Companion`; events; lambdas → any delegate type (incl. custom generic
  delegates); `out`/`ref` via `byref()`; nullable value types; .NET enums; C# operator
  overloads + extension methods; inherit .NET bases / implement .NET interfaces
- Reverse interop: the emitted assembly is plain public IL — C# consumes it via
  `<ProjectReference>`; re-consuming it **as Kotlin** restores `infix`/`operator`/`suspend`/
  inline/nullability/sealed/bounds via `[Kotlin*]` attributes (see
  `docs/dotkt-semantics.md` §6/§10)

Still in progress: coroutines/`Sequence` builders (the `suspend`⇔`Task<T>` ABI is settled;
`docs/coroutine-stdlib-port-plan.md` is the live plan) — see `docs/master-task-inventory.md`
(the canonical task ledger) and `docs/remaining-tasks.md` (the 1.0 ship checklist).

## Quick start (repo)

Prereqs: the repo's Gradle auto-provisions a JDK; you need the **.NET SDK 10**.

```bash
make help                          # all targets
make all                           # toolchain + stdlib artifacts
make dev SRC=path/to/Foo.kt        # compile + run one file (wraps scripts/dotkt.sh --run)
make verify                        # all compiler, IR, MSBuild, roundtrip, and package gates
make pack                          # NuGet packages into a local feed
```

Or focused suites directly:

```bash
./tests/run-nunit-tests.sh                         # categorized NUnit suites + ILVerify
./tests/msbuild/run.sh                             # stateful MSBuild behavior
./tests/roundtrip/scenarios/run.sh                 # irreducible consume-as-Kotlin scenarios
./scripts/dotkt.sh --run path/Foo.kt   # one-shot compile + run (-h for options)
```

Building the CLR stdlib (three artifacts — see `CLAUDE.md` for details):

```bash
./scripts/build-stdlib-ref.sh --emit   # reference dll (DotKt.Private.Stdlib.dll)
./scripts/build-stdlib-rt.sh --emit    # runtime dll  (DotKt.Stdlib.dll)
./scripts/build-stdlib-klib.sh         # frontend klib (kotlin-stdlib-clr-frontend.klib)
```

### Build a project with MSBuild / `.ktproj`

A DotKt project builds with plain `dotnet build` / `dotnet run` (and thus in Visual Studio):

```xml
<Project Sdk="DotKt.Sdk/0.9.6-rc7">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

Every `.kt` under the project is compiled; `import System.X` in source injects .NET types
automatically (an explicit `<DotKtImport Include="..." />` item does the same from MSBuild).
See `docs/user/getting-started.md`.

## Layout

| Path | Role |
|------|------|
| `toolchain/kotc/` | the Kotlin→BIR compiler frontend (Kotlin/JVM gradle module; source package `kotc.*`) |
| `toolchain/bir2cir/` | **BIR (JSON) → CIR (JSON)**: the Kotlin↔CLR lowering (reads the stdlib ref.dll bindings) |
| `toolchain/ilemit/` | **CIR (JSON) → CIL** via `System.Reflection.Emit` |
| `toolchain/facadegen/` | .NET metadata → FIR-injection metadata (façade-free `import System.X`) |
| `toolchain/retarget/` | repoint emitted BCL refs so a C# project can `<Reference>` the dll at compile time |
| `libraries/stdlib/` | the **CLR Kotlin stdlib** sources (common Kotlin + `clr/` actuals + `@Clr*` bindings) |
| `packaging/` | NuGet packages: `DotKt.Sdk`, `DotKt.Toolchain`, `DotKt.Stdlib`, `DotKt.Templates` |
| `tests/` | categorized NUnit projects plus the few shell scenarios that require external process/build state |
| `eng/` | in-repo development build integration (`KotlinClr.targets`) |
| `scripts/` | reusable build/validation tools, `dotkt.sh`, and the three `build-stdlib-*.sh` helpers |
| `docs/user/` | **user-facing docs** (getting started / .NET interop / CLR differences) |
| `docs/dotkt-semantics.md` | **canonical**: how Kotlin maps to the CLR + deliberate deviations from Kotlin/JVM |
| `docs/design-fir-bir-cir-il.md` | backend layer contract (kotc / bir2cir / ilemit responsibilities) |
| `docs/master-task-inventory.md` | the canonical "what's left" task ledger |
| `docs/remaining-tasks.md` | the 1.0 ship checklist |
| `docs/archive/` | historical design/plan docs (superseded; kept for rationale) |

## How it works (design)

The frontend is the **stock JVM pipeline** (Configuration → FIR → Fir2Ir); we own only the final
backend phase, so resolution against the (CLR-built) Kotlin stdlib is correct. `kotc` serializes
Kotlin IR to a compact JSON "BIR" with **no CLR knowledge**; `bir2cir` applies the Kotlin↔CLR
relation (type lowering, `@ClrTypeAlias`/`@ClrIntrinsic` substitution from the stdlib reference
dll, the String↔CharSequence bridge, default-argument splicing); `ilemit` emits verifiable CIL.
Keeping lowering before IL emission is what makes control flow, generics-shaped overloads,
nullable value types, etc. tractable. Reified CLR generics carry Kotlin's type arguments for
real, so `T::class` / `is T` need no inlining tricks.

## Toolchain / caveats

- JDK auto-provisioned by Gradle (foojay); **.NET SDK 10** required.
- Kotlin/IR APIs are **version-pinned to 2.4.0** (internal, unstable — intentionally not tracking
  newer versions; bumped from 2.2.0 in #111 — see `docs/kotlin-frontend-bump-playbook.md`). Some
  round-trip limits stem from this pin (`docs/dotkt-semantics.md` §10).
- WPF/WinUI samples build on Windows only; **Avalonia** windowing runs cross-platform (incl. WSLg).

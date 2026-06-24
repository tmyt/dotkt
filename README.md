# kotlin/clr — Kotlin → .NET (CLR) compiler

A compiler that runs **Kotlin on .NET**. It reuses the official Kotlin frontend
(`kotlin-compiler-embeddable` 2.2.0 — Configuration / FIR / Fir2Ir, so resolution and type
checking are the real thing) and replaces only the backend, lowering **Kotlin IR → CIL** that
runs on `dotnet`.

Long-term goal: production grade. Mid-term goal: CLR windowing (drive Avalonia/WPF/WinUI from
Kotlin via their real .NET types).

> **Pure .NET binding — no bundled libraries.** kotlin/clr is *only* a Kotlin→.NET binding; it
> ships no library of its own. You reference real .NET assemblies and call their types directly
> from Kotlin. A Kotlin-idiomatic UI DSL would be a separate downstream product.

## Backend

Direct IL, one path: **Kotlin IR → `BirEmitter` → `BIR` (JSON) → `ilemit` → CIL**. The output is
[`ilverify`](https://github.com/dotnet/runtime/tree/main/src/coreclr/tools/ILVerify)-clean. (An earlier
Kotlin-IR→C#-text oracle backend was retired and removed; BIR→ilemit is the sole backend.)

## What works today

The direct-IL backend runs a broad, practical subset end-to-end (every item below is exercised by
`scripts/verify-il.sh` — 35 samples, all run-correct **and** `ilverify`-clean).

**Language**
- Top-level functions; primitives (`Int`/`Long`/`Double`/`Float`/`Short`/`Byte`/`Boolean`/`Char`/`String`); arithmetic, comparison, bitwise (`and`/`or`/`xor`/`shl`/`shr`/`ushr`/`inv`)
- Control flow: `if`/`when` (subject, `is`, ranges, multi-value branches), `while`, `do-while`, `for` (ranges, arrays, collections), labeled `break`/`continue`
- Strings: templates, multi-line/raw `"""…"""`, escapes
- Functions: default args, `vararg`, local functions + closures, extension functions, `inline`, **`reified`** (`T::class`, `x is T`, `x as? T`), `tailrec`
- Classes: properties, primary **and secondary constructors + `init {}`**, inheritance, `override`/`virtual`/`abstract`, interfaces, **visibility** (`private`/`internal`/`protected`/`public` → real CLR access), `companion object`, **nested classes**, **object expressions** (anonymous, non-capturing), `data class` (`toString`/`equals`/`hashCode`/`componentN`/`copy`), `typealias`, `lateinit`
- **Operator overloading** (`plus`/`minus`/`times`/`get`/`set`/`invoke`/`compareTo`/`contains`/`unaryMinus`); structural `==` (value/reference/`Nullable<T>`)
- **Enums**: simple enums → real CLR enums (for .NET interop); **rich enums** (constructor params, methods, `name`/`ordinal`/`values()`/`valueOf`) → singleton classes
- Null safety: `?.`, `?:`, `!!`, smart casts, `as?`/`is`, and **nullable value types** (`Int?` → `System.Nullable<T>`)
- Exceptions: `try`/`catch`/`throw`, `throw` as an expression, `require`/`check`/`error`/`TODO`/`requireNotNull`/`checkNotNull`; Kotlin exception types → .NET (`IllegalStateException` → `InvalidOperationException`, …)

**Stdlib (mapped to the BCL)**
- Collections: `listOf`/`mutableListOf`/`setOf`/`mapOf` → `List`/`HashSet`/`Dictionary`; **30+ operations** (`map`/`filter`/`fold`/`reduce`/`sumOf`/`groupBy`/`associate*`/`zip`/`sorted*`/`max*`/`min*`/`joinToString`/…) → LINQ
- Strings: `uppercase`/`lowercase`/`trim*`/`substring`/`replace`/`split`/`startsWith`/`contains`/`padStart`/… → `System.String`
- `kotlin.math.*` → `System.Math`; `Pair`/`Triple`/`to` → `ValueTuple`; scope functions `let`/`run`/`with`/`apply`/`also`; destructuring

**Kotlin ↔ .NET interop**
- Call real .NET types: static calls, `new`, instance methods + chaining, properties, generics (`List<T>`), indexers (`list[i]`)
- **Inherit .NET base classes**, **implement .NET interfaces**, **subscribe to .NET events** (`+=`)
- Kotlin lambdas → CLR delegates (`Action`/`Func<T>`); `use`/`AutoCloseable` → `IDisposable`; .NET enums
- Reverse interop: the generated assembly is plain public IL, consumable from C# (`ProjectReference`)

See `docs/remaining-tasks.md` for the full 1.0 checklist and what is still in progress (e.g. per-entry
enum bodies, the iterator operator / `Sequence`, `by lazy`, coroutines).

## Quick start

Prereqs: the repo's Gradle auto-provisions a JDK; you need the **.NET SDK 10**.

### Run the full IL test suite (compile → IL → run → assert → `ilverify`)

```bash
./scripts/verify-il.sh
```

### Compile and run one file through the IL backend

```bash
# 0. build the BIR→CIL tool once
dotnet build toolchain/ilemit -c Release -o build/ilemit-bin

# 1. Kotlin → BIR (JSON).  One run also writes Foo.cs (the C# oracle) + KIR@Raw.txt (IR dump)
STDLIB=$(find ~/.gradle/caches -name 'kotlin-stdlib-2.2.0.jar' | head -1)
./gradlew :kotc:run --args="$PWD/samples/m0/M0.kt -no-stdlib -classpath $STDLIB -d $PWD/build/out"

# 2. BIR → CIL assembly
dotnet build/ilemit-bin/ilemit.dll build/out M0Kt build/out/*.bir.json

# 3. run it
dotnet build/out/M0Kt.dll          # -> sum = 5 / zero / n=1 / n=2
```

### Build a project with MSBuild / `.ktproj`

A Kotlin.NET project builds with plain `dotnet build` / `dotnet run` (and thus in Visual Studio):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <StartupObject>AppKt</StartupObject>
    <!-- 'cs' (default, via C# source) or 'il' (direct CIL) -->
    <KotlinClrBackend>il</KotlinClrBackend>
  </PropertyGroup>
  <!-- expose .NET types to Kotlin: <KotlinClrType> injects them façade-free,
       <KotlinClrFacade> generates façades at build time -->
  <ItemGroup><KotlinClrType Include="System.Text.StringBuilder" /></ItemGroup>
  <Import Project="path/to/msbuild/KotlinClr.targets" />
</Project>
```

`dotnet build foo.ktproj` runs the kotlin/clr compiler on the `.kt` files, then finishes the
assembly via the chosen backend. See `samples/ktproj/` (C# path), `samples/ktproj-il/` (IL path),
`samples/ktproj-ref/` and `samples/ktproj-inject/` (.NET type injection).

```bash
./scripts/verify-il.sh      # the shipping IL backend over the sample corpus + ilverify
./scripts/verify-ktproj.sh  # MSBuild/.ktproj end-to-end (forward + bidirectional ProjectReference)
```

## Layout

| Path | Role |
|------|------|
| `toolchain/kotc/` | the Kotlin→BIR compiler frontend (Kotlin/JVM gradle module; source package `kotc.*`) |
| `toolchain/kotc/.../kotc/pipeline/ClrCliPipeline.kt` | driver: stock JVM phases + our backend phase |
| `toolchain/kotc/.../kotc/backend/BirEmitter.kt` (+ `BirEmitterExpressions/Statements`, `BirMappings`) | **Kotlin IR → BIR** (all lowering lives here) |
| `toolchain/ilemit/` | **BIR (JSON) → CIL** via `System.Reflection.Emit` (split: `Emitter.Expressions/Coroutines/Statements/Metadata`) |
| `toolchain/facadegen/` | .NET metadata → FIR-injection metadata (façade-free `import System.X`) |
| `toolchain/retarget/` | repoint emitted BCL refs so a C# project can `<Reference>` the dll at compile time |
| `runtime/DotKt.Runtime/` | .NET runtime helpers + the `[Kotlin*]` round-trip metadata attributes |
| `packaging/` | NuGet packages: `DotKt.Sdk` (thin), `DotKt.Toolchain` (tools + the build pipeline), `DotKt.Runtime` |
| `samples/` | `il-*` (IL-backend samples), `m-*` (language/interop), `ktproj-*` (MSBuild) |
| `scripts/verify-il.sh` | IL differential + `ilverify` gate |
| `scripts/verify-ktproj.sh` | MSBuild/.ktproj integration (IL backend) |
| `docs/dotkt-semantics.md` | **how Kotlin maps to the CLR + where DotKt deliberately differs from Kotlin/JVM** |
| `docs/remaining-tasks.md` | the 1.0 ship checklist |

## How it works (design)

The frontend is the **stock JVM pipeline** (Configuration → FIR → Fir2Ir); we own only the final
backend phase, so resolution against the real `kotlin-stdlib` is correct. Lowering is concentrated
in `BirEmitter` (Kotlin IR → a compact JSON "BIR"); `ilemit` stays thin and just emits CIL
from BIR. Keeping lowering in BIR (rather than emitting IL straight from the structured AST) is what
makes control flow, generics-shaped overloads, nullable value types, etc. tractable.

stdlib is mapped to the BCL at codegen (Kotlin's `inline` stdlib bodies are not present in our IR),
e.g. collection ops → LINQ, `kotlin.math` → `System.Math`. Reified generics are handled by a
targeted inline expansion at the call site (the type argument is substituted, so `T::class` and
`x is T` materialize).

## Toolchain / caveats

- JDK auto-provisioned by Gradle (foojay); **.NET SDK 10** required.
- Kotlin/IR APIs are **version-pinned to 2.2.0** (internal, unstable — intentionally not tracking
  newer versions).
- WPF/WinUI samples build on Windows only; **Avalonia** windowing runs cross-platform (incl. WSLg).

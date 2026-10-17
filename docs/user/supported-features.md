# Supported features — the scannable matrix

Status legend: ✅ works today (exercised by the test gates) · 🚧 in progress · ❌ not supported (see
[kotlin-on-clr-differences.md](kotlin-on-clr-differences.md) for the "why").

## Kotlin language

| Feature | Status |
|---|---|
| Functions, default args (incl. cross-module), `vararg`, extensions, local functions/closures | ✅ |
| `inline` / `reified` / non-local return (incl. cross-module) | ✅ |
| Classes, inheritance, interfaces, visibility, `companion object`, nested classes, object expressions | ✅ |
| `data class` (all generated members), `value class` (as a real class), `sealed`, `typealias`, `lateinit` | ✅ |
| Properties (real CLR properties), delegation: `by lazy`, `by map`, `ReadWriteProperty` | ✅ |
| Enums (basic → CLR enum, rich → singleton class) | ✅ |
| Generics: classes/functions/constraints/variance — real CLR reified generics | ✅ |
| Null safety: `?.` `?:` `!!`, smart casts, `Int?` → `Nullable<int>`, NRT interop | ✅ |
| Control flow, `when`, ranges, labeled jumps, `try`/`catch`, string templates | ✅ |
| Operator overloading, `infix`, destructuring | ✅ |
| `suspend` functions (`Task<T>` ABI), continuations, coroutine context, Task/ValueTask await | ✅ |
| `Sequence` / `iterator` builders, `yield` / `yieldAll` | ✅ |
| Structured concurrency (`Job`, `CoroutineScope`, `launch`, `async`, Flow, channels) | Available through the separate `dotktx.coroutines` library; see [external libraries](#external-kotlinx-libraries) |
| Context parameters (`context(s: S) fun f()`), incl. cross-module | ✅ (a leading CLR parameter; see the differences doc) |

## Standard library

| Area | Status |
|---|---|
| Collections (`listOf`/`mapOf`/…, `map`/`filter`/`fold`/`groupBy`/`joinToString`/`sorted*`/…) | ✅ |
| Strings (`trim`/`padStart`/`split`/templates/… — a few ops still route through interim lowerings) | ✅ |
| `Regex` | ✅ Matching (including anchored `matchEntire`/`matches`), groups, replacement, splitting, `findAll`, `splitToSequence`, and `options`. The CLR engine and [option mapping](../dotkt-semantics.md#5b-quater-regexoptions-decodes-the-compiled-regexoptions-three-regexoptions-have-no-clr-bit-deliberate-clr-choice) have documented differences. |
| `kotlin.math`, ranges, `Pair`/`Triple`, scope functions, unsigned types, `Array<T>` ops | ✅ |
| `Result`/`runCatching`, atomics, exceptions (bound to `System.*`) | ✅ |
| Lazy `Sequence` chains (`asSequence().map{…}`, filtering, short-circuiting terminals) | ✅ |
| kotlinx.coroutines / kotlinx.atomicfu CLR ports | Available as separate libraries: `dotktx.coroutines` / `dotktx.atomicfu` |
| Other kotlinx libraries (for example serialization) | No general support claim; each needs its own CLR port and validation |

## .NET interop

| Feature | Status |
|---|---|
| `import System.X` façade-free (+ transitive closure), import aliases | ✅ |
| Constructors, methods, properties, indexers, projected overloads, generics (`List<T>`, `Dictionary<K,V>`) | ✅ |
| Statics — direct `Type.member` (no synthetic `.Companion`) | ✅ |
| Events (`X.subscribe(handler)` returning a closeable subscription), lambdas → any delegate (incl. custom generic delegates) | ✅ |
| `out`/`ref` via `byref()`; user Kotlin `ClrRef<T>` parameters as CLR `ref T`; nullable value types (`int?`); .NET enums (including typed `[Flags]` operations) | ✅ |
| Native P/Invoke via top-level `external fun` + CLR `DllImportAttribute` (initial blittable/`IntPtr`/`ClrRef<T>` subset) | ✅ |
| Plain value consumption of a generic method's ref return | 🚧 [Current generic-call emission bug](using-dotnet-from-kotlin.md#4-out--ref-parameters--byref) |
| Imported BCL signatures containing native-integer `nint`/`nuint` codes | 🚧 [Current projection bug](using-dotnet-from-kotlin.md#imported-native-sized-integers) |
| C# operator overloads and extension methods | ✅ |
| Inherit .NET base classes / implement .NET interfaces | ✅ |
| Consume the output from C# (`ProjectReference`, NRT, `Task<T>`) | ✅ |
| Re-consume a DotKt dll **as Kotlin** through a reference KLIB | ✅ ([projection limits](../dotkt-semantics.md#10-round-trip-fidelity)) |

Gates backing this table: `tests/run-nunit-tests.sh` (compile → NUnit assertions → `ilverify`, including
ProjectReference consume-as-Kotlin coverage) and `tests/msbuild/run.sh` (stateful MSBuild). `make verify` runs the
complete set.

Sequence and Regex coverage lives in `tests/basic/fixtures/SequenceOperationTests.kt`,
`tests/basic/fixtures/RegexTests.kt`, and `tests/coroutines/fixtures/CoroutineSequenceBuilderTests.kt`.

## External kotlinx libraries

DotKt's standard library supplies `kotlin.coroutines` primitives, suspend lowering, Sequence builders,
and the .NET await bridge. Higher-level coroutine APIs are provided by the separate CLR port
`dotktx.coroutines`: import `kotlinx.coroutines`, `kotlinx.coroutines.flow`, or
`kotlinx.coroutines.channels` as usual. The separate `dotktx.atomicfu` port supplies
`kotlinx.atomicfu` APIs. These libraries are ordinary Kotlin multiplatform libraries built with
`DotKt.Sdk.Mpp`; the compiler does not special-case their builders or library names.

Use library builds matching the compiler/toolchain being consumed. When working on compiler HEAD,
rebuild dependent Kotlin libraries with that toolchain; a released port's existence does not promise
binary compatibility with a newer compiler's internal ABI. Support for these two ports is not a claim
that every kotlinx library or platform module has a CLR implementation.

Their library APIs and tests live in the separate `dotktx.coroutines` and `dotktx.atomicfu` projects.
The compiler's NUnit coroutine suite exercises the core lowering and interop contracts; it is not a
replacement for those libraries' runtime validation.

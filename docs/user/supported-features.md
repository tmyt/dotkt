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
| Structured concurrency (`Job`, `CoroutineScope`, `launch`) | ❌ (later track) |
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
| kotlinx libraries (coroutines-core, serialization, …) | ❌ (stdlib only; separate future track) |

## .NET interop

| Feature | Status |
|---|---|
| `import System.X` façade-free (+ transitive closure), import aliases | ✅ |
| Constructors, methods, properties, indexers, all overloads, generics (`List<T>`, `Dictionary<K,V>`) | ✅ |
| Statics — direct `Type.member` (no synthetic `.Companion`) | ✅ |
| Events (`add_X`/`remove_X`), lambdas → any delegate (incl. custom generic delegates) | ✅ |
| `out`/`ref` via `byref()`; user Kotlin `ClrRef<T>` parameters as CLR `ref T`; nullable value types (`int?`); .NET enums (including typed `[Flags]` operations) | ✅ |
| Native P/Invoke via top-level `external fun` + CLR `DllImportAttribute` (initial blittable/`IntPtr`/`ClrRef<T>` subset) | ✅ |
| C# operator overloads and extension methods | ✅ |
| Inherit .NET base classes / implement .NET interfaces | ✅ |
| Consume the output from C# (`ProjectReference`, NRT, `Task<T>`) | ✅ |
| Re-consume a DotKt dll **as Kotlin** through a reference KLIB | ✅ ([projection limits](../dotkt-semantics.md#10-round-trip-fidelity)) |

Gates backing this table: `tests/run-nunit-tests.sh` (compile → NUnit assertions → `ilverify`, including
ProjectReference consume-as-Kotlin coverage) and `tests/msbuild/run.sh` (stateful MSBuild). `make verify` runs the
complete set.

Sequence and Regex coverage lives in `tests/basic/fixtures/SequenceOperationTests.kt`,
`tests/basic/fixtures/RegexTests.kt`, and `tests/coroutines/fixtures/CoroutineSequenceBuilderTests.kt`.

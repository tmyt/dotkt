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
| `suspend` functions (`Task<T>` ABI) / `Sequence` builders / `yield` | 🚧 (ABI settled; runtime port in progress) |
| Structured concurrency (`Job`, `CoroutineScope`, `launch`) | ❌ (later track) |
| Context parameters/receivers | ❌ |

## Standard library

| Area | Status |
|---|---|
| Collections (`listOf`/`mapOf`/…, `map`/`filter`/`fold`/`groupBy`/`joinToString`/`sorted*`/…) | ✅ |
| Strings & `Regex` (a few ops like `trim`/`padStart` still route through interim lowerings) | ✅ |
| `kotlin.math`, ranges, `Pair`/`Triple`, scope functions, unsigned types, `Array<T>` ops | ✅ |
| `Result`/`runCatching`, atomics, exceptions (bound to `System.*`) | ✅ |
| Lazy `Sequence` chains (`asSequence().map{…}`) | 🚧 (coroutine-machinery-gated) |
| kotlinx libraries (coroutines-core, serialization, …) | ❌ (stdlib only; separate future track) |

## .NET interop

| Feature | Status |
|---|---|
| `import System.X` façade-free (+ transitive closure), import aliases | ✅ |
| Constructors, methods, properties, indexers, all overloads, generics (`List<T>`, `Dictionary<K,V>`) | ✅ |
| Statics via `.Companion` (the qualifier is required) | ✅ |
| Events (`add_X`/`remove_X`), lambdas → any delegate (incl. custom generic delegates) | ✅ |
| `out`/`ref` via `byref()`, nullable value types (`int?`), .NET enums | ✅ |
| C# operator overloads and extension methods | ✅ |
| Inherit .NET base classes / implement .NET interfaces | ✅ |
| Consume the output from C# (`ProjectReference`, NRT, `Task<T>`) | ✅ |
| Re-consume a DotKt dll **as Kotlin** (`infix`/`operator`/`suspend`/`inline`/`sealed`/bounds restored) | ✅ (with documented losses: enum-class-ness, `object` sugar, SAM-lambda — [details](../dotkt-semantics.md)) |

Gates backing this table: `scripts/verify-il.sh` (compile → run → assert → `ilverify`),
`scripts/verify-ktproj.sh` (MSBuild end-to-end), `scripts/verify-roundtrip.sh` (consume-as-Kotlin).

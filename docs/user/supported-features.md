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
| Context parameters (`context(s: S) fun f()`), incl. cross-module | ✅ (a leading CLR parameter; see the differences doc) |

## Standard library

| Area | Status |
|---|---|
| Collections (`listOf`/`mapOf`/…, `map`/`filter`/`fold`/`groupBy`/`joinToString`/`sorted*`/…) | ✅ |
| Strings (`trim`/`padStart`/`split`/templates/… — a few ops still route through interim lowerings) | ✅ |
| `Regex` | 🚧 `find`/`matchEntire`/`matches`/`matchAt`/`matchesAt`/`containsMatchIn`/`replace`/`replaceFirst`/`split`/`escape`/`escapeReplacement`/`fromLiteral` + the `MatchResult`/group surface (`value`/`range`/`groups`/`groupValues`, by-index & by-name groups) all work (gates: `il-regex`, `il-regexgroups`, `il-regexreplace`). `findAll`/`splitToSequence`/`options` are still pending (the first two are gated on the lazy-`Sequence` runtime). `matchEntire` does not re-anchor an alternation (`a\|ab` over `"ab"`), a deliberate CLR deviation being tightened in #162. |
| `kotlin.math`, ranges, `Pair`/`Triple`, scope functions, unsigned types, `Array<T>` ops | ✅ |
| `Result`/`runCatching`, atomics, exceptions (bound to `System.*`) | ✅ |
| Lazy `Sequence` chains (`asSequence().map{…}`) | 🚧 (coroutine-machinery-gated) |
| kotlinx libraries (coroutines-core, serialization, …) | ❌ (stdlib only; separate future track) |

## .NET interop

| Feature | Status |
|---|---|
| `import System.X` façade-free (+ transitive closure), import aliases | ✅ |
| Constructors, methods, properties, indexers, all overloads, generics (`List<T>`, `Dictionary<K,V>`) | ✅ |
| Statics — implicit `Type.member` (`.Companion` also accepted) | ✅ |
| Events (`add_X`/`remove_X`), lambdas → any delegate (incl. custom generic delegates) | ✅ |
| `out`/`ref` via `byref()`, nullable value types (`int?`), .NET enums | ✅ |
| C# operator overloads and extension methods | ✅ |
| Inherit .NET base classes / implement .NET interfaces | ✅ |
| Consume the output from C# (`ProjectReference`, NRT, `Task<T>`) | ✅ |
| Re-consume a DotKt dll **as Kotlin** through a reference KLIB | ✅ ([projection limits](../dotkt-semantics.md#10-round-trip-fidelity)) |

Gates backing this table: `tests/run-nunit-tests.sh` (compile → NUnit assertions → `ilverify`, including
ProjectReference consume-as-Kotlin coverage) and `tests/msbuild/run.sh` (stateful MSBuild). `make verify` runs the
complete set.

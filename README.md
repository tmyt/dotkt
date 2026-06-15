# kotlin/clr — Kotlin → .NET (CLR) backend

Reuses the official Kotlin frontend (`kotlin-compiler-embeddable` 2.2.0: Configuration / FIR / Fir2Ir)
and replaces only the backend, lowering **Kotlin IR → C# source** which then runs on `dotnet`.

Long-term goal: production grade. Mid-term goal: CLR windowing.

## Status

**Language (M0):** top-level functions, primitives, arithmetic/comparison, `if`/`when`, `while`,
calls, string templates, `println`, lambdas.

**Kotlin ↔ CLR interop (usable):** via a `@Clr("System.X")` façade, codegen maps to real .NET:
- static calls (`System.Math.Max`), instance construction (`new`), instance methods + chaining
- properties (get/set), generics (`new List<int>()`), indexers (`list[i]`)
- Kotlin lambdas → CLR delegates (`Action`, `Func<T>`)

**Windowing:** a real window, **built in Kotlin**, rendered via Avalonia on WSLg.

```bash
./scripts/verify-all.sh        # compile+run+assert all console samples (m0, m2, m-i1, m-i3)
./scripts/run-window.sh 15     # launch the Kotlin-driven Avalonia window (auto-close 15s)
```

Pure-Kotlin window (`samples/win-kotlin/app.kt`) — the `Window`/`TextBlock` are constructed in
Kotlin; only the Avalonia app lifecycle stays in C# (`runtime/csharp/KfcUi/UiRuntime.cs`).
A WPF variant for Windows is in `samples/win-wpf/` (same Kotlin source, Windows-only).

## Layout

| Path | Role |
|------|------|
| `compiler/` | the compiler (Kotlin/JVM) |
| `compiler/.../clrc/pipeline/ClrCliPipeline.kt` | driver: stock JVM phases + our backend phase |
| `compiler/.../clrc/backend/ClrBackendPhase.kt` | owns the backend; dumps IR + runs codegen |
| `compiler/.../clrc/backend/CSharpCodegen.kt` | Kotlin IR → C# |
| `samples/m0/` | example source + `runner.csproj` |
| `scripts/run-m0.sh` | end-to-end check |

## Manual invocation

```bash
STDLIB=$(find ~/.gradle/caches -name 'kotlin-stdlib-2.2.0.jar' | head -1)
./gradlew :compiler:run --args="$PWD/samples/m0/M0.kt -no-stdlib -classpath $STDLIB -d $PWD/build/clr-out"
# emits build/clr-out/M0.cs (+ KIR@Raw.txt for debugging)
```

## Toolchain

JDK auto-provisioned by Gradle (foojay); .NET SDK 10. Kotlin/IR APIs are version-pinned to 2.2.0
(internal, unstable — intentionally not tracking newer versions).

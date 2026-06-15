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

**Classes (multi-file):** Kotlin classes → C# classes with fields, constructors, inheritance,
`override`/`virtual`, objects → singletons. Multiple `.kt` files cross-reference each other.

**MSBuild / `.ktproj`:** a Kotlin.NET project builds with plain `dotnet build` / `dotnet run`
(and therefore in Visual Studio, which uses MSBuild):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <StartupObject>AppKt</StartupObject>
  </PropertyGroup>
  <!-- expose .NET types to Kotlin; façades auto-generated at build time -->
  <ItemGroup><KotlinClrFacade Include="System.Text.StringBuilder" /></ItemGroup>
  <Import Project="path/to/msbuild/KotlinClr.targets" />
</Project>
```
`dotnet build foo.ktproj` runs the kotlin/clr compiler on the `.kt` files, then the C# toolchain
finishes the assembly. See `samples/ktproj/` and `samples/ktproj-ref/`.

**Windowing:** a real window, **built in Kotlin**, rendered via Avalonia on WSLg.

```bash
./scripts/verify-all.sh        # compile+run+assert all console + .ktproj samples
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

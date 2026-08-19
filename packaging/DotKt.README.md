# DotKt — Kotlin → .NET (CLR)

DotKt compiles **Kotlin to a normal .NET assembly**. It reuses the official Kotlin frontend
(`kotlin-compiler-embeddable` — Configuration / FIR / Fir2Ir, so resolution and type checking are
the real thing) and replaces only the backend, lowering Kotlin IR → CIL that runs on `dotnet`.

## The packages

| Package | Role |
|---|---|
| `DotKt.Sdk` | The MSBuild SDK: build Kotlin (`.kt`) projects to .NET assemblies. Pulls in the toolchain (build-only) and the runtime stdlib. |
| `DotKt.Sdk.Mpp` | The SDK for multiplatform projects (common/actual `expect`/`actual` in one CLR compilation). |
| `DotKt.Toolchain` | The compiler toolchain (`kotc`, `bir2cir`, `ilemit`, `dll2klib`) + the frontend stdlib klib + the compile-time stdlib reference assembly. Build-only. |
| `DotKt.Stdlib` | The Kotlin standard library **runtime** assembly (`DotKt.Stdlib.dll`) apps link and ship against. |
| `DotKt.Templates` | `dotnet new` project templates (`dotkt-cli`). |

## Getting started

```
dotnet new install DotKt.Templates
dotnet new dotkt-cli -o hello
cd hello
dotnet run
```

## Provenance & licensing

- Licensed under **Apache-2.0**. See the repository `LICENSE`.
- Source & issues: <https://github.com/tmyt/dotkt>
- `DotKt.Toolchain` redistributes third-party components (the Kotlin compiler/runtime,
  kotlinx-coroutines, JetBrains annotations, `System.Reflection.MetadataLoadContext`).
  Their licenses are listed in `THIRD-PARTY-NOTICES.md`, shipped in the `DotKt.Toolchain` package.

Full documentation, the supported-feature matrix, and the "Kotlin on the CLR — what's different"
guide live in the repository: <https://github.com/tmyt/dotkt>.

## MSBuild integration contract

Editor and analysis integrations can depend on the public `DotKtResolveKlibReferences` target. Once it completes:

- `@(DotKtResolvedKlibReference)` contains every generated reference KLIB. Its item identity is the absolute KLIB path;
  `SourceAssembly` is the MSBuild-selected source DLL, and `TargetFramework` / `RuntimeIdentifier` identify the
  inner build that resolved it. `RuntimeIdentifier` is empty for framework-dependent builds.
- On a project declaring `TargetFrameworks`, invoking the target without an explicit `TargetFramework` dispatches
  to every inner build and returns their combined, TFM-specific items.
- `$(DotKtStdlib)` is the evaluation-time path to DotKt's embedded frontend standard-library KLIB; the target does
  not produce or mutate it.
- `$(DotKtKotlinVersion)` identifies the embedded Kotlin toolchain.

The item contains only the KLIB set projected from references selected by MSBuild for each TFM. It deliberately does
not duplicate `$(DotKtStdlib)` inside the item or synthesize a second "complete frontend input set" contract. A
consumer that needs kotc's complete frontend classpath composes `$(DotKtStdlib)` with the returned item set.

Names beginning with `_DotKt` and the layout below `$(DotKtToolchainDir)` remain private implementation details.

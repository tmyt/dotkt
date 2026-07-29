# DotKt — Kotlin → .NET (CLR)

DotKt compiles **Kotlin to a normal .NET assembly**. It reuses the official Kotlin frontend
(`kotlin-compiler-embeddable` — Configuration / FIR / Fir2Ir, so resolution and type checking are
the real thing) and replaces only the backend, lowering Kotlin IR → CIL that runs on `dotnet`.

## The packages

| Package | Role |
|---|---|
| `DotKt.Sdk` | The MSBuild SDK: build Kotlin (`.kt`) projects to .NET assemblies. Pulls in the toolchain (build-only) and the runtime stdlib. |
| `DotKt.Sdk.Mpp` | The SDK for multiplatform projects (common/actual `expect`/`actual` in one CLR compilation). |
| `DotKt.Toolchain` | The compiler toolchain (`kotc`, `bir2cir`, `ilemit`, `dll2klib`, `retarget`) + the frontend stdlib klib + the compile-time stdlib reference assembly. Build-only. |
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
  kotlinx-coroutines, JetBrains annotations, Mono.Cecil, `System.Reflection.MetadataLoadContext`).
  Their licenses are listed in `THIRD-PARTY-NOTICES.md`, shipped in the `DotKt.Toolchain` package.

Full documentation, the supported-feature matrix, and the "Kotlin on the CLR — what's different"
guide live in the repository: <https://github.com/tmyt/dotkt>.

# Getting started with DotKt (Kotlin on .NET)

DotKt compiles **Kotlin to a normal .NET assembly**. You write ordinary Kotlin, build with
`dotnet build`, and get a dll/exe that runs on the CLR and can be referenced from C#.

Next steps after this page:

- [Using .NET from Kotlin](using-dotnet-from-kotlin.md) — `import System.X` and everything interop.
- [Kotlin on the CLR — what's different](kotlin-on-clr-differences.md) — the friendly tour of
  deviations from Kotlin/JVM.
- [Supported features](supported-features.md) — the scannable ✅/🚧/❌ matrix.

## 1. Prerequisites

- **.NET SDK 10**. The packaged SDK targets the `net10.0` family, including platform-qualified targets.
- **A Java runtime on your `PATH`** — a JDK or JRE, **version 21 or newer** (`java -version`). The
  DotKt Kotlin compiler front-end (`kotc`) runs on the JVM, so a Java runtime is required to *build*
  a DotKt project. (Nothing Java is needed to *run* the emitted assembly — it is plain .NET IL.)

## 2. Install

DotKt ships as five NuGet packages:

| Package | What it is |
|---|---|
| `DotKt.Sdk` | the MSBuild project SDK — `<Project Sdk="DotKt.Sdk">` is all a project needs |
| `DotKt.Sdk.Mpp` | the SDK for multiplatform projects using `expect`/`actual` in one CLR compilation |
| `DotKt.Toolchain` | the compiler pipeline (build-time only; pulled in automatically by the Sdk) |
| `DotKt.Stdlib` | the Kotlin standard library compiled for the CLR |
| `DotKt.Templates` | `dotnet new` project templates |

For the examples below, create a workspace directory and save this `NuGet.Config` at its root.
The projects created inside it inherit the package sources. For an existing solution, add the
DotKt source to its NuGet configuration without removing sources it already needs.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="dotkt" value="https://dotkt.utatane.dev/nuget/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="dotkt">
      <package pattern="DotKt.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

From that workspace, install the templates. **Pin the version** to select a known toolchain
across feed updates:

```bash
dotnet new install DotKt.Templates@0.9.21 --nuget-source https://dotkt.utatane.dev/nuget/index.json
```

The template install's source argument does not configure package restore for generated projects;
keep the `NuGet.Config` above in place.

## 3. Hello world

```bash
dotnet new dotkt-cli -o hello
cd hello
dotnet run
# Hello, World, from DotKt — Kotlin on .NET!
```

The template generates two files: `hello.csproj` and `Program.kt`. The project uses
`DotKt.Sdk`, so the `.csproj` extension does not make it a C# compilation. The project file:

```xml
<Project Sdk="DotKt.Sdk/0.9.21">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

And `Program.kt`:

```kotlin
fun main(args: Array<String>) {
    val who = args.firstOrNull() ?: "World"
    println("Hello, $who, from DotKt — Kotlin on .NET!")
}
```

Every `.kt` file under the project directory is compiled automatically (like `.cs` in a C#
project). There is no required Kotlin-specific configuration.

### Multiplatform (`expect`/`actual`)

From the workspace root again, use `dotkt-mpp` for a Kotlin multiplatform project with a common
`expect` fragment under `common/` and a CLR `actual` fragment under `clr/`:

```bash
dotnet new dotkt-mpp -o hello-mpp
cd hello-mpp
dotnet run
# Hello, World, from a DotKt multiplatform app on .NET!
```

It uses `<Project Sdk="DotKt.Sdk.Mpp">` and ships a `global.json` next to the project. The
`global.json` is **required**: the MPP SDK imports the base `DotKt.Sdk` through a version-less
nested import, and the NuGet MSBuild-SDK resolver reads that nested SDK's version *only* from
`global.json`'s `msbuild-sdks` map — so both `DotKt.Sdk.Mpp` and `DotKt.Sdk` are pinned there. The
template generates it for you, so the project builds out of the box.

## 4. Build and run

```bash
dotnet build        # compile only
dotnet run          # compile + run
```

It is a normal MSBuild project, so it also builds inside **Visual Studio / Rider / VS Code**
(F5/Ctrl+B work; `.kt` edits are tracked by the fast up-to-date check). The output assembly is
plain public IL — a C# project can `<ProjectReference>` it directly.

`Optimize` controls the emitted assembly's JIT optimization policy. The normal SDK defaults are
`false` for Debug and `true` for Release; explicit overrides such as
`dotnet build -c Release -p:Optimize=false` are honored. Unoptimized output carries
`DebuggableAttribute` with JIT tracking and optimization suppression, without advertising Edit and
Continue. `DebugSymbols` and `DebugType` do not choose the optimization policy.

Starting with 0.9.20, the SDK normally emits a Portable PDB next to the Kotlin DLL. Use the
matching pair and the original `.kt` files to set a line breakpoint on an executable Kotlin statement in a .NET
debugger. Set `DebugType=none` to omit the PDB; `DebugType=portable` also works with
`Optimize=true`. The SDK supports external Portable PDBs, not embedded PDBs.

Basic breakpoint binding and step-over were tested with NetCoreDbg 3.2.0-1092 on Linux and
.NET 10.0.12. Optimized code may step differently. Inlined code within one build retains its
original document, but breakpoint binding and stepping around multi-document methods depend on
the debugger. Code inlined from another assembly does not carry the producer's source paths into
the consumer's PDB. This does not provide Kotlin local-variable inspection or expression evaluation,
coroutine logical stepping, SourceLink, or Edit and Continue.

### Reference assemblies and native resources

Starting with 0.9.20, set `ProduceReferenceAssembly=true` to also
emit a declaration-only reference DLL for project-reference compilation. It preserves signatures,
generic constraints and Kotlin metadata so C# and Kotlin consumers can compile against it. The
implementation DLL is still used at runtime; the reference DLL is not a runnable artifact and
does not have its own PDB. The default remains implementation-only.

See [Windows application manifests](../application-manifest.md) for `ApplicationManifest`, native
resources and assembly-version settings.

## 5. Working in the DotKt repository itself (contributors)

If you have the source repo rather than the NuGet packages:

```bash
make help                      # list all targets
make all                       # build the full toolchain + stdlib
make dev SRC=path/to/Foo.kt RUN=1 # compile + run one Kotlin file through the pipeline
make verify                    # run all compiler, IR, MSBuild, roundtrip, and package gates
make pack                      # produce the NuGet packages into a local feed
```

(`./scripts/dotkt.sh --run path/to/Foo.kt` is the underlying one-shot wrapper. Omit `RUN=1` from
`make dev` to compile without running.)

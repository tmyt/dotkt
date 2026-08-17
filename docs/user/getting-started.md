# Getting started with DotKt (Kotlin on .NET)

DotKt compiles **Kotlin to a normal .NET assembly**. You write ordinary Kotlin, build with
`dotnet build`, and get a dll/exe that runs on the CLR and can be referenced from C#.

Next steps after this page:

- [Using .NET from Kotlin](using-dotnet-from-kotlin.md) — `import System.X` and everything interop.
- [Kotlin on the CLR — what's different](kotlin-on-clr-differences.md) — the friendly tour of
  deviations from Kotlin/JVM.
- [Supported features](supported-features.md) — the scannable ✅/🚧/❌ matrix.

## 1. Prerequisites

- **.NET SDK 10** (`dotnet --version` ≥ 10).
- **A Java runtime on your `PATH`** — a JDK or JRE, **version 21 or newer** (`java -version`). The
  DotKt Kotlin compiler front-end (`kotc`) runs on the JVM, so a Java runtime is required to *build*
  a DotKt project. (Nothing Java is needed to *run* the emitted assembly — it is plain .NET IL.)

## 2. Install

DotKt ships as four NuGet packages:

| Package | What it is |
|---|---|
| `DotKt.Sdk` | the MSBuild project SDK — `<Project Sdk="DotKt.Sdk">` is all a project needs |
| `DotKt.Toolchain` | the compiler pipeline (build-time only; pulled in automatically by the Sdk) |
| `DotKt.Stdlib` | the Kotlin standard library compiled for the CLR |
| `DotKt.Templates` | `dotnet new` project templates |

Install the templates once. **Pin the version** for reproducible builds — DotKt is pre-1.0; the feed's
current release is `0.9.9`, and pinning keeps a project on a known toolchain across feed updates:

```bash
dotnet new install DotKt.Templates::0.9.9
```

(Check `packaging/DotKt.Versions.props` in the repo for the current shipping version if this page
is out of date — `DotKtVersionPrefix` + `DotKtVersionSuffix`.)

## 3. Hello world

```bash
dotnet new dotkt-cli -o hello
cd hello
dotnet run
# Hello, World, from DotKt — Kotlin on .NET!
```

That template is just two files. The project file:

```xml
<Project Sdk="DotKt.Sdk/0.9.9">
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

For a Kotlin multiplatform project (a common `expect` fragment under `common/` + a CLR `actual`
fragment) use the `dotkt-mpp` template:

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

## 5. Working in the DotKt repository itself (contributors)

If you have the source repo rather than the NuGet packages:

```bash
make help                      # list all targets
make all                       # build the full toolchain + stdlib
make dev SRC=path/to/Foo.kt    # compile + run one Kotlin file through the pipeline
make verify                    # run all compiler, IR, MSBuild, roundtrip, and package gates
make pack                      # produce the NuGet packages into a local feed
```

(`./scripts/dotkt.sh --run path/to/Foo.kt` is the underlying one-shot wrapper.)

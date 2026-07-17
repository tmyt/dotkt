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

Install the templates once:

```bash
dotnet new install DotKt.Templates
```

## 3. Hello world

```bash
dotnet new dotkt-cli -o hello
cd hello
dotnet run
# Hello, World, from DotKt — Kotlin on .NET!
```

That template is just two files. The project file:

```xml
<Project Sdk="DotKt.Sdk/0.9.6-rc7">
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
make verify                    # run the test gates (verify-il / verify-ktproj)
make pack                      # produce the NuGet packages into a local feed
```

(`./scripts/dotkt.sh --run path/to/Foo.kt` is the underlying one-shot wrapper.)

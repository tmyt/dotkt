# Third-party notices

DotKt (Kotlin → .NET/CLR) is licensed under **Apache-2.0** (see `LICENSE`).

The `DotKt.Toolchain` package redistributes the third-party components listed below. Each remains
under its own license; the notices here are provided for attribution. Versions track the toolchain's
declared dependencies — see `packaging/DotKt.Versions.props` (embedded Kotlin version),
`toolchain/kotc/build.gradle.kts`, and the `toolchain/*/*.csproj` `PackageReference`s for the exact
pins in a given build.

| Component | Used by | License | Project |
|---|---|---|---|
| Kotlin compiler (`kotlin-compiler-embeddable`) and the bundled Kotlin runtime/stdlib/reflection it carries | `kotc` (the Kotlin frontend) | Apache-2.0 | https://github.com/JetBrains/kotlin |
| Kotlin standard library (`kotlin-stdlib`, vendored jar) | `kotc` | Apache-2.0 | https://github.com/JetBrains/kotlin |
| kotlinx.coroutines (bundled transitively with the Kotlin compiler) | `kotc` | Apache-2.0 | https://github.com/Kotlin/kotlinx.coroutines |
| JetBrains Java annotations (`org.jetbrains:annotations`, bundled with the Kotlin compiler) | `kotc` | Apache-2.0 | https://github.com/JetBrains/java-annotations |
| System.Reflection.MetadataLoadContext | `bir2cir`, `ilemit` | MIT (.NET Foundation) | https://github.com/dotnet/runtime |

The full text of the Apache License 2.0 governing DotKt itself is in `LICENSE`. The Apache-2.0 and
MIT licenses of the redistributed components are available from their respective project pages above.

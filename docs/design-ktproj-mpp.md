# First-class ktproj MPP: common `expect` + CLR `actual` in one compilation

This is the design of record for the CLR MPP source-set split. See [architecture.md](architecture.md)
for binding layers and the special stdlib artifacts.

## 1. The model — one compilation, two source kinds, one dll

DotKt is a **single-target** toolchain: the CLR is the only platform. Kotlin Multiplatform (MPP) on DotKt is therefore
NOT "compile once per target and ship an expect-only klib for downstream targets to actualize." It is one compilation of
one project that happens to hold **two kinds of source**:

- **common** — the `-Xcommon-sources` set: platform-agnostic code plus `expect` declarations with no body. Platform-neutral.
- **actual** — the default `.kt` glob = **the sole CLR platform implementation**: the `actual` declarations that satisfy
  the `expect` contracts, plus ordinary platform code and the entry point.

These lower through **one** kotc invocation to **one fully-actualized dll**. There is no second target, no per-target
artifact, no klib to hand off.

**Authorship of common is irrelevant.** The common set may be the project's own `expect`/platform-agnostic code, OR the
physically-present `commonMain` `.kt` of a ported library (e.g. kotlinx.coroutines' commonMain vendored into the tree).
Either way it is just "the source files tagged common in THIS compilation." There is deliberately **no** user-facing
notion of:
- an "upstream" project,
- "consuming a common klib,"
- an "expect-only klib" that a later build actualizes,
- a `Clr` (or any platform) qualifier on the user surface.

Because there is exactly one target, `commonMain` vs `clrMain` collapses to "which files carry the `expect`s" vs "which
files carry the `actual`s" — a source-set tag, not a compilation boundary or a shipped artifact.

## 2. The mechanism — `metadataCompilationMode = !hasCommonSources`

The whole feature is one parameterized literal in kotc's app frontend.

The stock public API `prepareMetadataSessions` hardcodes `metadataCompilationMode = true`, which forces
`SessionConstructionUtils.prepareSessions` down its `createSingleSession` branch. In that branch common + platform
sources collapse into ONE `FirModuleData`, so an `expect` shares a module with its `actual` and the frontend rejects it:
*"expect and corresponding actual are declared in the same module."* Before #119 the common→platform module split existed
ONLY in the stdlib frontend pipeline (`prepareNativeSessions`, `metadataCompilationMode = false`) — never in the app
pipeline.

`ClrAppFrontendPipelinePhase` (#119, `b793c0f`) fixes this by **inlining** `prepareMetadataSessions`' body — the same
"fork the thin CLI glue, not core logic" pattern the phase already uses to open its pre-resolution session window — and
driving the sole `metadataCompilationMode` literal off the source set:

```
hasCommonSources = ktFiles.any { isCommonSourceForPsi(it) }   // set by -Xcommon-sources
metadataCompilationMode = !hasCommonSources
```

- **No common sources** → `hasCommonSources = false` → `metadataCompilationMode = true` → `createSingleSession` → the
  compile is **byte-identical** to the pre-#119 single-session app compile. Every existing non-MPP sample (the ~150
  ordinary `.ktproj` projects) is unaffected — this is the load-bearing safety property.
- **Common sources present** (`-Xcommon-sources=...`) → `hasCommonSources = true` → `metadataCompilationMode = false`.
  Since `-Xmulti-platform` is always on and no `-Xfragments`/HMPP structure is passed, the legacy-MPP split runs: a
  common module + a platform module that depends on it, so `expect`/`actual` match across the module boundary.

The relevant flags on the kotc invocation are `-Xmulti-platform -Xexpect-actual-classes -Xcommon-sources="<CSV>"`. All
sources are still passed **positionally**; `-Xcommon-sources` only *tags which of them are common*.

Downstream is unchanged: the multi-session Fir2Ir actualization + BIR emit already handle a two-session frontend — the
stdlib self-build drives the same two tail phases (`ClrCommonFir2Ir` then `ClrBackend`). MPP reuses that machinery; it
adds no new backend path.

## 3. The artifact model — one fully actualized implementation

A user MPP library has one fully actualized implementation DLL. It does not publish an
expect-only KLIB for consumers to actualize again.

### Reference assembly versus stdlib binding metadata

`DotKt.Private.Stdlib.dll` exists to solve the stdlib's *dual-representation boundary*: the stdlib types
must present a pure-Kotlin compile-time face (`List : Collection : Iterable`, `kotlin.Char : Comparable`) AND a
BCL-bound runtime face (`IReadOnlyList`, `System.Char`). The ref.dll is the pure-Kotlin **metadata surface** carrying
`@ClrTypeAlias`/`@ClrIntrinsic` labels that **bir2cir substitutes at app-emit time**
(see [architecture.md](architecture.md)).

A user library has no such bootstrap binding assembly. Like `DotKt.Sdk`, the MPP SDK can
emit a normal declaration-only CLR reference assembly with `ProduceReferenceAssembly=true`.
This optional image preserves the actualized signatures and Kotlin metadata for consumers,
but never replaces the runtime DLL. See [SDK build outputs](user/getting-started.md#reference-assemblies-and-native-resources).

### No emitted frontend klib
The stdlib ships a frontend klib (`kotlin-stdlib-clr-frontend.klib`) because kotc needs a common/metadata `-classpath`
input carrying the actualized stdlib's `expect`/`@Clr*` metadata to compile *apps against it*. A user MPP library needs
no such emitted klib because **expect/actual is a within-library contract**: every CLR artifact a user library produces
is **fully actualized** — there is NO "this library ships unresolved `expect`s for another library to actualize" case on
a single-target toolchain. Library→library dependencies resolve **dll→dll** through the `[Kotlin*]` round-trip metadata
and ordinary .NET API metadata (the path exercised by the roundtrip producer/consumer tests), not through a shared
expect-only klib.

User MPP projects share the normal DotKt/.NET implementation and reference assembly paths.
The separate stdlib binding assembly and frontend KLIB remain stdlib-bootstrap artifacts.

## 4. The packaging — shared targets and composition SDK

The MPP source-set split is opt-in via a property on the shared build
pipeline, `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`:

```xml
<PropertyGroup>
  <DotKtMultiplatform>true</DotKtMultiplatform>
</PropertyGroup>
```

When set, the shared targets glob `common/**/*.kt` into the `DotKtCommon` item (Kotlin's `commonMain` convention; a
project may instead list them explicitly via `<DotKtCommon Include="..."/>`, which takes precedence), and — only when
`DotKtCommon` is non-empty — append `-Xmulti-platform -Xexpect-actual-classes -Xcommon-sources="<CSV>"` to the compile.
With no `DotKtCommon` items, the MPP flags are not added. The roundtrip and packaged-SDK gates cover
both the ordinary and MPP source paths.

The distinct `DotKt.Sdk.Mpp` SDK selected with
`<Project Sdk="DotKt.Sdk.Mpp">` imports the base `DotKt.Sdk` and layers MPP on top, setting
`DotKtMultiplatform=true` — the same composition relationship Microsoft ships between `Microsoft.NET.Sdk` and
`Microsoft.NET.Sdk.Web`. See §5 for the SDK's structure and its verification.

## 5. Distinct `DotKt.Sdk.Mpp` SDK

`packaging/DotKt.Sdk.Mpp/` is a **thin composition SDK**: `Sdk="DotKt.Sdk.Mpp"` == `Sdk="DotKt.Sdk"` + MPP on. Four
small files, mirroring the base SDK's layout:

- `Sdk/Sdk.props` — sets `<DotKtMultiplatform>true</DotKtMultiplatform>` (its whole value-add) and `<Import
  Project="Sdk.props" Sdk="DotKt.Sdk" />` to inherit all the kt→BIR→CIL orchestration + implicit Toolchain/Stdlib refs.
  It also defaults `$(DotKtVersion)` **before** importing the base, so the base's implicit `DotKt.Toolchain`/`DotKt.Stdlib`
  PackageReferences resolve at the same release version. The release pack checks that the SDK defaults agree.
- `Sdk/Sdk.targets` — `<Import Project="Sdk.targets" Sdk="DotKt.Sdk" />`.
- `DotKt.Sdk.Mpp.nuspec` — `packageType MSBuildSdk`, packs `Sdk/**`, and declares a **hard `<dependency>` on
  `DotKt.Sdk`** (same version) so restore fetches the base package.
- `DotKt.Sdk.Mpp.pack.csproj` — mirrors the base's pack project (version single-sourced from `DotKt.Versions.props`).

`scripts/pack-nuget.sh` packs it as the **fifth** package into `build/nuget-feed`.

**The one consumer-facing requirement — `global.json` pins the base.** The NuGet MSBuild-SDK resolver reads a
**nested** SDK import's version ONLY from `global.json`'s `msbuild-sdks` — it ignores an inline `Sdk="Name/Version"` on
`<Import>` *and* the nuspec `<dependency>` version. So the `Sdk.props`/`Sdk.targets` imports of the base are
**version-less**, and a consuming project pins both SDKs in `global.json` (the idiomatic pinning any custom MSBuild SDK
uses):

```json
{ "msbuild-sdks": { "DotKt.Sdk.Mpp": "x.y.z", "DotKt.Sdk": "x.y.z" } }
```

**Automated coverage:** `tests/packaged-sdk/run.sh` is the standing local-feed-restore gate. It
packs the 5 nupkgs to `build/nuget-feed` and, from that feed only (isolated `globalPackagesFolder` + a local-only
`nuget.config`, so a stale published version can't mask the fresh pack), builds `Sdk="DotKt.Sdk.Mpp"` (transitively
`Sdk="DotKt.Sdk"`, pinned via `global.json`) with a `common/` `expect` + `clr/` `actual`+entry — **restores → builds →
runs → asserts `Hello from the CLR actual`** — alongside a plain `Sdk="DotKt.Sdk"` Exe and a Library that
PackageReferences a second DotKt library. The MPP template case also checks the two SDK pins.
The property-gated slice (`tests/roundtrip/producer-mpp/`) exercises cross-assembly consumption.

## 6. First real consumer — the kotlinx.coroutines port

The dotktx.coroutines port compiles upstream kotlinx.coroutines' `commonMain` sources together
with CLR actuals into the `dotktx.coroutines` implementation DLL. It uses the
[same continuation core and Task bridge](design-coroutine-cold-core-task-bridge.md) as ordinary
Kotlin suspend code.

## 7. Limits

The app SDK supports a flat common/platform source split through `-Xcommon-sources`, not an
`-Xfragments` graph with intermediate common source sets. A common `expect` with no corresponding
CLR `actual` is a frontend compilation error.

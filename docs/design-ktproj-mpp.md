# First-class ktproj MPP: common `expect` + CLR `actual` in one compilation

> **状態 (2026-07-13, #125)**: 能力は**出荷済み** — kotc のアプリパイプライン（`ClrAppFrontendPipelinePhase`）が
> common→platform のモジュール分割を行い（`b793c0f`, #119）、`.ktproj` は `<DotKtMultiplatform>true</DotKtMultiplatform>`
> でオプトインする（`017a85c`, #119、`packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`）。動作サンプルは
> `tests/roundtrip/producer-mpp/`。本ドキュメントはその**確定した設計**の
> 正典。パッケージングは property-gated 方式（0.9.5 のメカニズム）に加え、独立した合成 SDK `DotKt.Sdk.Mpp`
> が **出荷済み**（`packaging/DotKt.Sdk.Mpp/`、`scripts/pack-nuget.sh` で 5 番目のパッケージとして pack、
> ローカルフィード restore による E2E スモークテスト済み — 本文 §5）。

Status: **shipped capability + design of record (2026-07-13).** See [architecture.md](architecture.md)
for the binding layers and ref/runtime artifact split, which deliberately does not apply to user libraries.

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

## 3. The artifact model — a user MPP library emits exactly ONE fully-actualized rt.dll

A user MPP library emits **one** fully-actualized runtime dll. It emits **no** `ref.dll` and **no** frontend klib.
Both of those are **stdlib-bootstrap-specific**, and understanding *why* is the core of the design:

### No ref.dll
`ref.dll` (`DotKt.Private.Stdlib.dll`) exists to solve the stdlib's *dual-representation boundary*: the stdlib types
must present a pure-Kotlin compile-time face (`List : Collection : Iterable`, `kotlin.Char : Comparable`) AND a
BCL-bound runtime face (`IReadOnlyList`, `System.Char`). The ref.dll is the pure-Kotlin **metadata surface** carrying
`@ClrTypeAlias`/`@ClrIntrinsic` labels that **bir2cir substitutes at app-emit time**
(see [architecture.md](architecture.md)).

A user library has **no** such dual representation. Its types are ordinary Kotlin types that emit as ordinary CLR types.
`@ClrTypeAlias`/`@ClrIntrinsic` are **stdlib-only** bindings (bir2cir reads them from the stdlib ref.dll; a user library
never authors them — MEMORY `clrtypealias-stdlib-only-apps-use-dll2klib`). A user library's `.NET` interop goes through
**dll2klib** (`import System.X` → concrete .NET types resolved against the reference assemblies at that library's OWN
emit), not through a ref-metadata surface that a downstream build re-substitutes. So there is nothing for a user ref.dll
to carry — it would be an empty indirection.

### No emitted frontend klib
The stdlib ships a frontend klib (`kotlin-stdlib-clr-frontend.klib`) because kotc needs a common/metadata `-classpath`
input carrying the actualized stdlib's `expect`/`@Clr*` metadata to compile *apps against it*. A user MPP library needs
no such emitted klib because **expect/actual is a within-library contract**: every CLR artifact a user library produces
is **fully actualized** — there is NO "this library ships unresolved `expect`s for another library to actualize" case on
a single-target toolchain. Library→library dependencies resolve **dll→dll** through the `[Kotlin*]` round-trip metadata
and ordinary .NET API metadata (the path exercised by the roundtrip producer/consumer tests), not through a shared
expect-only klib.

Net: **user MPP library = one rt.dll, fully actualized, consumed downstream as an ordinary DotKt/.NET assembly.** The
ref.dll + frontend-klib machinery stays a stdlib-bootstrap concern.

## 4. The packaging — property-gated shared targets + a distinct composition SDK (both shipped)

**Current (shipped, 0.9.5 mechanism):** the MPP source-set split is opt-in via a property on the *shared* build
pipeline, `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`:

```xml
<PropertyGroup>
  <DotKtMultiplatform>true</DotKtMultiplatform>
</PropertyGroup>
```

When set, the shared targets glob `common/**/*.kt` into the `DotKtCommon` item (Kotlin's `commonMain` convention; a
project may instead list them explicitly via `<DotKtCommon Include="..."/>`, which takes precedence), and — only when
`DotKtCommon` is non-empty — append `-Xmulti-platform -Xexpect-actual-classes -Xcommon-sources="<CSV>"` to the compile.
With the property unset, `DotKtCommon` is empty, the MPP flags are empty, and the compile is byte-identical to a non-MPP
project. Every ordinary `.ktproj` is inert under this change; the roundtrip and packaged-SDK gates cover both modes.

This property-gated slice lives in the **shared** pipeline precisely because it is inert for every existing project — it
carries zero risk to the non-MPP path.

**Shipped (productization):** a distinct `DotKt.Sdk.Mpp` SDK that a project selects with
`<Project Sdk="DotKt.Sdk.Mpp">`, which **imports the base `DotKt.Sdk`** and layers MPP on top — auto-setting
`DotKtMultiplatform=true` — the same composition relationship Microsoft ships between `Microsoft.NET.Sdk` and
`Microsoft.NET.Sdk.Web`. See §5 for the SDK's structure and its verification.

## 5. Distinct `DotKt.Sdk.Mpp` SDK — shipped

`packaging/DotKt.Sdk.Mpp/` is a **thin composition SDK**: `Sdk="DotKt.Sdk.Mpp"` == `Sdk="DotKt.Sdk"` + MPP on. Four
small files, mirroring the base SDK's layout:

- `Sdk/Sdk.props` — sets `<DotKtMultiplatform>true</DotKtMultiplatform>` (its whole value-add) and `<Import
  Project="Sdk.props" Sdk="DotKt.Sdk" />` to inherit all the kt→BIR→CIL orchestration + implicit Toolchain/Stdlib refs.
  It also defaults `$(DotKtVersion)` **before** importing the base, so the base's implicit `DotKt.Toolchain`/`DotKt.Stdlib`
  PackageReferences resolve at the current DotKt version rather than the base SDK's own stale default.
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

**Verification — end-to-end local-feed smoke test (done).** Against `build/nuget-feed`, a test
project `<Project Sdk="DotKt.Sdk.Mpp">` with a `common/` `expect` + `clr/` `actual`+entry, pinned via `global.json`,
**restores → builds → runs → prints `Hello from the CLR actual`**, with bir2cir reporting the two-fragment split
(`lowered 2 BIR file(s)`). This is the packaged-SDK layer's first real end-to-end coverage (the base `DotKt.Sdk` had
none — it was exercised only at pack time).

**Automated coverage:** `tests/packaged-sdk/run.sh` is the standing local-feed-restore gate. It
packs the 5 nupkgs to `build/nuget-feed` and, from that feed only (isolated `globalPackagesFolder` + a local-only
`nuget.config`, so a stale published version can't mask the fresh pack), builds `Sdk="DotKt.Sdk.Mpp"` (transitively
`Sdk="DotKt.Sdk"`, pinned via `global.json`) with a `common/` `expect` + `clr/` `actual`+entry — **restores → builds →
runs → asserts `Hello from the CLR actual`** — alongside a plain `Sdk="DotKt.Sdk"` Exe and a Library that
PackageReferences a second DotKt library. This is **both** packaged SDKs' first automated end-to-end coverage; the
property-gated slice (`tests/roundtrip/producer-mpp/`) remains the gated proof of
the MPP *mechanism*.

## 6. First real consumer — the kotlinx.coroutines port

The dotktx.coroutines port is the first production consumer of user-app MPP: vendor kotlinx.coroutines' `commonMain`
`.kt` as the **common** set + write CLR **actual**s (over the cold-Continuation/Task-bridge coroutine runtime,
[design-coroutine-cold-core-task-bridge.md](design-coroutine-cold-core-task-bridge.md)) → **one** `dotktx.coroutines`
rt.dll. This is exactly the "authorship of common is irrelevant" case from §1: the common `.kt` is upstream's, physically
present in the tree, tagged common in this one compilation. The backend blockers that gated it (#122 collection-factory
splat, #123 external-generic `new` over a free type-var) are fixed.

## 7. Known follow-ups

- **HMPP / `-Xfragments` app builds are not wired.** The app pipeline switches on `-Xcommon-sources` (a flat common vs
  platform split). A project passing an `-Xfragments` HMPP module structure (multiple intermediate source sets) falls to
  the single-session path. Not needed for the CLR-only single-platform model; would matter only for a multi-fragment
  common graph. (In-code follow-up note on `ClrAppFrontendPipelinePhase`.)
- **`expect`-without-`actual` diagnostic quality.** A common `expect` with no CLR `actual` surfaces as a frontend
  actualization error; the message/position quality for the user-app case is a diagnostics-polish follow-up (aligns with
  the #84 diagnostics work).
- **#129 — dll2klib generic-interface import edges.** Some `.NET` generic-interface imports from a common fragment hit
  dll2klib edges; tracked separately.

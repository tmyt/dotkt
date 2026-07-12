# First-class ktproj MPP: common `expect` + CLR `actual` in one compilation

> **状態 (2026-07-13, #125)**: 能力は**出荷済み** — kotc のアプリパイプライン（`ClrAppFrontendPipelinePhase`）が
> common→platform のモジュール分割を行い（`b793c0f`, #119）、`.ktproj` は `<DotKtMultiplatform>true</DotKtMultiplatform>`
> でオプトインする（`017a85c`, #119、`packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`）。動作サンプルは
> `cases/ktproj-mpp/`（verify-ktproj 全通過）、最小再現は `experiments/mpp-greeter/`。本ドキュメントはその**確定した設計**の
> 正典。パッケージングは現状 property-gated 方式（0.9.5 のメカニズム）、独立 SDK (`DotKt.Sdk.Mpp`) は追跡中の
> productization フォローアップ（本文 §5）。

Status: **shipped capability + design of record (2026-07-13).** Cross-reference:
[docs/design-clr-stdlib-ref-runtime-split.md](design-clr-stdlib-ref-runtime-split.md) (the ref/runtime artifact split
this reuses and deliberately does NOT apply to user libraries), [docs/ship-tasks.md](ship-tasks.md) §0 (the binding
layer architecture).

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
  `cases/il-*` + the plain `.ktproj`) is unaffected — this is the load-bearing safety property.
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
(see [design-clr-stdlib-ref-runtime-split.md](design-clr-stdlib-ref-runtime-split.md)).

A user library has **no** such dual representation. Its types are ordinary Kotlin types that emit as ordinary CLR types.
`@ClrTypeAlias`/`@ClrIntrinsic` are **stdlib-only** bindings (bir2cir reads them from the stdlib ref.dll; a user library
never authors them — MEMORY `clrtypealias-stdlib-only-apps-use-facadegen`). A user library's `.NET` interop goes through
**facadegen** (`import System.X` → concrete .NET types resolved against the reference assemblies at that library's OWN
emit), not through a ref-metadata surface that a downstream build re-substitutes. So there is nothing for a user ref.dll
to carry — it would be an empty indirection.

### No emitted frontend klib
The stdlib ships a frontend klib (`kotlin-stdlib-clr-frontend.klib`) because kotc needs a common/metadata `-classpath`
input carrying the actualized stdlib's `expect`/`@Clr*` metadata to compile *apps against it*. A user MPP library needs
no such emitted klib because **expect/actual is a within-library contract**: every CLR artifact a user library produces
is **fully actualized** — there is NO "this library ships unresolved `expect`s for another library to actualize" case on
a single-target toolchain. Library→library dependencies resolve **dll→dll** through the `[Kotlin*]` round-trip metadata
and ordinary .NET API metadata (the same path `cases/ktproj-roundtrip`/`ktproj-applib` exercise), not through a shared
expect-only klib.

Net: **user MPP library = one rt.dll, fully actualized, consumed downstream as an ordinary DotKt/.NET assembly.** The
ref.dll + frontend-klib machinery stays a stdlib-bootstrap concern.

## 4. The packaging — property-gated shared targets (current), distinct SDK (intended)

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
project. Every existing `.ktproj` is inert under this change (verify-ktproj all-pass).

This property-gated slice lives in the **shared** pipeline precisely because it is inert for every existing project — it
carries zero risk to the non-MPP path.

**Intended (productization, tracked follow-up):** a distinct `DotKt.Sdk.Mpp` SDK that a project selects with
`<Project Sdk="DotKt.Sdk.Mpp/x.y.z">`, which **imports the base `DotKt.Sdk`** and layers MPP on top — auto-setting
`DotKtMultiplatform=true` and surfacing a first-class `<DotKtCommon>` item — the same composition relationship
Microsoft ships between `Microsoft.NET.Sdk` and `Microsoft.NET.Sdk.Web`. See §5 for the status and the reason it is
deferred rather than done in this change.

## 5. Distinct `DotKt.Sdk.Mpp` SDK — status: tracked productization follow-up (NOT in 0.9.5)

**Disposition: documented as a tracked follow-up; the property-gated approach is the 0.9.5 mechanism.**

The *files* for a distinct SDK are small (a `Sdk/Sdk.props` importing `DotKt.Sdk` and setting `DotKtMultiplatform=true`,
a `Sdk/Sdk.targets`, a `.nuspec`, a `.pack.csproj`, and a pack entry). The **verification story is not**:

- The packaged SDK layer has **no gate coverage today.** Every `verify-ktproj` sample — including the MPP sample
  `cases/ktproj-mpp/` — builds through the **in-repo dev entry** (`<Import ../KotlinClr.targets>` + explicit `$(DotKt*)`
  tool paths), NOT through a NuGet-resolved `Sdk="DotKt.Sdk"`. The base `DotKt.Sdk` package itself is exercised only by
  `pack-nuget.sh` (build-time) and as `DotKt.Templates` content — never by an end-to-end gate.
- SDK-to-SDK composition (`<Import Project="Sdk.props" Sdk="DotKt.Sdk" />`) requires NuGet MSBuild-SDK **resolution**
  against a feed. Verifying `Sdk="DotKt.Sdk.Mpp"` end-to-end therefore requires standing up a **local-feed restore
  integration harness that does not exist** — a genuinely new pack/test pipeline, not a thin file addition.

Adding an unverifiable SDK package would ship a productization surface with no gate behind it, against the project's
"no half-baked public state" rule. The clean, gated slice is the property mechanism, which the MPP sample proves through
the same pipeline every other `.ktproj` uses. So the distinct SDK is deferred **with a concrete technical blocker** (no
packaged-SDK gate harness), not on a vibe.

**To productize later** (the follow-up): (a) `packaging/DotKt.Sdk.Mpp/` with the four small files above; (b) a fifth
`dotnet pack` entry in `scripts/pack-nuget.sh` + the Makefile's "4 packages" comment bumped to 5; (c) — the real work —
a local-feed-restore gate that builds a `Sdk="DotKt.Sdk.Mpp"` project against `build/nuget-feed`, giving the packaged
SDK layer (base AND MPP) its first end-to-end coverage.

## 6. First real consumer — the kotlinx.coroutines port

The dotktx.coroutines port is the first production consumer of user-app MPP: vendor kotlinx.coroutines' `commonMain`
`.kt` as the **common** set + write CLR **actual**s (over the cold-Continuation/Task-bridge coroutine runtime,
[design-coroutine-cold-core-task-bridge.md](design-coroutine-cold-core-task-bridge.md)) → **one** `dotktx.coroutines`
rt.dll. This is exactly the "authorship of common is irrelevant" case from §1: the common `.kt` is upstream's, physically
present in the tree, tagged common in this one compilation. The backend blockers that gated it (#122 collection-factory
splat, #123 external-generic `new` over a free type-var) are fixed. See
[design-kotlinx-coroutines-port.md](design-kotlinx-coroutines-port.md).

## 7. Known follow-ups

- **HMPP / `-Xfragments` app builds are not wired.** The app pipeline switches on `-Xcommon-sources` (a flat common vs
  platform split). A project passing an `-Xfragments` HMPP module structure (multiple intermediate source sets) falls to
  the single-session path. Not needed for the CLR-only single-platform model; would matter only for a multi-fragment
  common graph. (In-code follow-up note on `ClrAppFrontendPipelinePhase`.)
- **`expect`-without-`actual` diagnostic quality.** A common `expect` with no CLR `actual` surfaces as a frontend
  actualization error; the message/position quality for the user-app case is a diagnostics-polish follow-up (aligns with
  the #84 diagnostics work).
- **#129 — facadegen generic-interface import edges.** Some `.NET` generic-interface imports from a common fragment hit
  facadegen edges; tracked separately.
- **Distinct `DotKt.Sdk.Mpp` SDK + its local-feed gate** (§5).

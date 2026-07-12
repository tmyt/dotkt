# Kotlin frontend bump 2.2.0 → 2.4.0 — delta-analysis + migration plan (#111)

> Status (2026-07-12): PLAN/SCOPING (Fable, verified against the `v2.4.0` JetBrains/kotlin tag + the local
> `upstream/` checkout — NOT from memory). Estimate ~3-5 focused days. Bulk is mechanical; the const-value
> serializer (§4) is the one gating risk.

## 1. Where 2.2.0 is pinned (bump all together)
- `toolchain/kotc/build.gradle.kts:2` (`kotlin("jvm") version`) + `:13` (`kotlin-compiler-embeddable`) — the only compiler artifact.
- **`scripts/verify-differential.sh:26-29`** — the JVM-oracle jars (kotlin-stdlib / compiler-embeddable / script-runtime 2.2.0). EASY TO MISS; bump in the SAME change or the oracle speaks 2.2 while kotc speaks 2.4.
- `upstream/` reference checkout → re-checkout at `v2.4.0`.
- Doc claims of the pin: `CLAUDE.md:3,152`, `README.md:4,175`, + docs/remaining-tasks, master-task-inventory, dotkt-semantics, design-stdlib-compilation, bir-coverage, user/kotlin-on-clr-differences.
- Coordinates are unchanged at 2.4.0. **PREFLIGHT (abort trigger):** unzip `kotlin-compiler-embeddable-2.4.0.jar` and confirm `org/jetbrains/kotlin/cli/pipeline/metadata/` + `cli/metadata` are bundled (upstream moved metadata CLI into `compiler/cli/cli-metadata/`). If absent → extra artifact / vendoring; the plan changes.

## 2. Internal-API footprint — CERTAIN BREAKS (verified vs v2.4.0)
kotc's Kotlin surface is ~7,900 lines / 19 files, but ~600 lines of pipeline glue carry the risk; the ~6,000-line BirEmitter sits on the stable IR tree (near-zero expected breakage). Concentrated in 5 files.
1. **`FirResult` gone** → `AllModulesFrontendOutput` (value class over `List<SingleModuleFrontendOutput>`); `convertToIrAndActualize` now an extension on it, **same 9 params (name+order) + 2 new optional** — verified. Breaks `ClrStdlibFrontendPipelinePhase.kt:87`, `ClrAppFrontendPipelinePhase.kt:135`, `ClrCliPipeline.kt:108`, `ClrMetadataKlibPipeline.kt:61`. Mechanical.
2. **`MetadataFrontendPipelineArtifact` reshaped** `(frontendOutput, configuration, sourceFiles)`; `diagnosticCollector`/`metadataVersion` → `configuration.diagnosticsCollector` / `configuration.klibMetadataVersionOrDefault()`. Mechanical.
3. **`ConfigurationPipelineArtifact`** lost diagnostics: `(configuration, rootDisposable)` (was 3-way). Breaks `ClrStdlibFrontendPipelinePhase.kt:43`, `ClrAppFrontendPipelinePhase.kt:62`. Mechanical.
4. **`K2MetadataCompilerArguments.metadataKlib` REMOVED — polarity flipped**: metadata-klib output is now the DEFAULT; new opt-out `legacyMetadataJar`. Delete `Main.kt:27` (`arguments.metadataKlib = true`). GOOD: **every `-X` flag survives** on CommonCompilerArguments at 2.4.0 (allowKotlinPackage/commonSources/contextParameters/expectActualClasses/fragments/multiPlatform/renderInternalDiagnosticNames/stdlibCompilation/optIn); `removedMetadataCompilerArguments.kt` is empty at v2.4.0.
5. **`constValueProvider` DEAD** — `FirKLibSerializerExtension` ctor lost it; `ConstValueProviderImpl` gone. Breaks `ClrMetadataKlibPipeline.kt:15,95,120-125`. **The one break needing reasoning — §4.**
6. **`serializeSingleFirFile`** (same package) lost constValueProvider, gained `produceHeaderKlib=false`; `SerializedMetadata` gained a 4th ctor arg (`metadataVersion.toArray()`) — breaks `ClrMetadataKlibPipeline.kt:147`.
7. **`DefaultImportProvider`→`DefaultImportsProvider`**, `FirDefaultImportProviderHolder`→`FirDefaultImportsProviderHolder` (plural). Breaks all of `ClrDefaultImports.kt`. Mechanical rename; re-verify the register-overwrite-before-resolution trick still lands.
8. **`FirExtensionRegistrar.getInstances(project)` gone** → `configuration.getCompilerExtensions(FirExtensionRegistrar)`. Breaks `ClrStdlibFrontendPipelinePhase.kt:63`, `ClrAppFrontendPipelinePhase.kt:98`. `ClrTypeInjection.kt:1070-1071` registration matches 2.4.0 — but this is the load-bearing façade-free `import System.*` wiring; verify the registrar reaches the session.
9. **klib dep resolution**: `prepareMetadataSessions` `resolvedLibraries` now `List<KotlinLibrary>`; use `loadMetadataKlibs(...).all` — the KT-63573 workaround (`ClrAppFrontendPipelinePhase.kt:82-86`) is now upstream's normal path (its own comment predicted this).

### Survivors (low risk, verified)
Driver skeleton (`ClrCliPipeline.kt:133-163`) ports intact (PipelinePhase/AbstractCliPipeline/ConfigurationUpdater/then). **The `-Xfragments`-rejection quirk SURVIVES verbatim** (`MetadataConfigurationPipelinePhase.kt:72`) → kotc's config-updater fork (`ClrCliPipeline.kt:59-94`) STAYS required, do not un-fork. `Fir2IrConfiguration.forKlibCompilation`, `prepareMetadataSessions`, `resolveAndCheckFir`/`buildFirFromKtFiles` (moved to `firUtils.kt`), `Fir2IrLazyProperty.fir` public, `FirDefaultProperty{Getter,Setter}` (so `BirEmitterCalls.kt:1369-1398` default-accessor discrimination survives), `IrFileEntry`/`MessageCollector`, `actualizedExpectDeclarations`. `PerformanceNotifications.KlibWritingStarted/Finished` now exist (update the `ClrMetadataKlibPipeline.kt:78-81` workaround comment).

## 3. Behavioral watch-list (compiles, miscompiles quietly) — ranked
1. **Const-value baking into the metadata klib (#80 mechanism)** — at 2.2.0 const serialization used FIR's `evaluatedInitializer` + the IR-interpreter `constValueProvider`; at 2.4.0 the IR fallback is DELETED, FIR's `FirExpressionEvaluator` is the ONLY source. If it doesn't cover the fragment-actualized `const val`s (`Int.MIN_VALUE`, `Double.POSITIVE_INFINITY` were historical failures), values silently vanish → downstream `InterpreterMethodNotFoundError`. Detection: rebuild klib, compile an app using those consts, `verify-il.sh`.
2. **Fir2Ir internals rewritten** (per-session Fir2IrComponentsStorage, SpecialFakeOverrideSymbolsResolver) — kotc leans on `resolveFakeOverride` (9 files) + fake-override/default-accessor discrimination. Fake-override linking changes = classic wrong-dispatch miscompiles. Watch BIR override/dispatch shape.
3. **New `ConstInliner` IR pass** — BirEmitter may see `IrConst` where it saw `IrGetField`/getter `IrCall`. Probably benign; differential gate shows it.
4. **Default language version 2.4** — new default features change FIR/IR shape. Mitigation: STAGE — compile at `-language-version 2.2` first (isolate API breakage from semantics), gate, then lift to default and gate again.
5. **Inline pre-serialization lowering** (KT-64570) — kotc consumes Fir2Ir directly so likely dodges it, but diff inline-heavy cases first.
6. **HMPP session construction grew a branch** — the stdlib fragment expect/actual actualization (`ClrMetadataKlibPipeline.kt:97-110`) depends on session/module ordering. Watch `kotlin.String` resolving to the unactualized expect again.
7. **Stdlib source vintage** — 2.2.0-vintage `libraries/stdlib` under a 2.4.0 `-Xstdlib-compilation` may want newer builtins (fails loudly; budget for a possible stdlib-source refresh).
Detection instruments: `verify-differential.sh` (after oracle bump) + BIR-corpus diffing (BIR is deterministic JSON — compile `cases/` before/after and diff). Grep upstream `ChangeLog.md` at v2.4.0 for fir2ir/klib/metadata/const/inline/expect-actual/fake-override/context-parameters before Phase 5.

## 4. The klib const-serializer (highest-reasoning item) — gates the bump
Structurally the klib pipeline survives; what died is the const-provider. Two options:
- **Option A (minimal):** drop the `constValueProvider` arg, add the `SerializedMetadata` metadataVersion arg, rely on FIR `evaluatedInitializer`; then EMPIRICALLY verify const values landed (klib dump + the #80 symptom test).
- **Option B (adopt upstream, RECOMMENDED):** 2.4.0's `Fir2KlibMetadataSerializer` (`fir/pipeline/Fir2KlibMetadataSerializer.kt`) is now EXACTLY the HMPP-aware, actualization-aware serializer kotc hand-rolled (takes `fir2IrActualizedResult`, extracts `actualizedExpectDeclarations`). kotc's phase collapses to Fir2Ir → Fir2KlibMetadataSerializer → header/fragment assembly → `buildKotlinMetadataLibrary`. Per no-dual-track/cleanest-design, B wins if reachable from the embeddable jar; A is the fallback.
**The const-coverage question is EMPIRICAL and gates the whole bump** — if FIR's 2.4.0 evaluator doesn't cover the stdlib cases, there's no IR fallback anymore → a real design problem, and where deep reasoning is spent.

## 5. Migration plan
- **P0 Preflight (hours):** unzip embeddable jar → confirm cli-metadata bundled (abort trigger). Re-checkout `upstream/` @ v2.4.0. Grep ChangeLog for watch-list keywords.
- **P1 Bump (mins):** build.gradle.kts:2,13 → 2.4.0; `installDist` compile loop.
- **P2 Compile fixes inside-out (~1-2 days grind):** Main.kt args → ClrCliPipeline artifacts/phases → both frontend phases (destructuring, loadMetadataKlibs, getCompilerExtensions, artifact ctor) → ClrDefaultImports renames → **ClrMetadataKlibPipeline: make the A/B decision (§4) first** → ClrTypeInjection plugin DSL → BirEmitter* residue (small).
- **P3 Rebuild:** `make toolchain && make stdlib`; fix build-stdlib fallout; **verify const values in the fresh klib IMMEDIATELY** (don't wait for the gate).
- **P4 Gate:** bump oracle jars (`verify-differential.sh:26-29`), `make verify`; reconcile xfail (expect FIXED prints + possibly new legit fails).
- **P5 Behavioral hunt:** triage reds vs the watch-list; BIR-corpus diff over `cases/`; optionally run twice (`-language-version 2.2` vs default) to bisect API-vs-semantics.
- **P6 Doc/comment sweep (same change):** CLAUDE.md:3,152 / README.md:4,175 / the 6 docs / now-false in-code comments (ClrMetadataKlibPipeline.kt:78-81, ClrAppFrontendPipelinePhase.kt:52-55, the #80 rationale block if Option B lands).

## 6. Effort / Fable-vs-grind split
**~3-5 focused days.** Stretches toward a week only if (i) embeddable jar dropped cli-metadata, (ii) FIR const evaluation misses the stdlib cases, (iii) the 2.4 frontend forces a stdlib-source refresh.
- **Mechanical Opus grind ~70%:** P1-P3 renames/reshapes, gate-and-fix, xfail reconciliation, doc sweep (each has a compiler error pointing at it).
- **High-leverage reasoning ~30%:** (1) §4 serializer decision + const-coverage verification (the only place it can get STUCK); (2) behavioral-regression root-cause (fake-override/ConstInliner/inline-lowering — symptom-moving whack-a-mole = escalation-rule territory); (3) the language-version staging call. Much of the usual API-delta triage is ALREADY DONE above (verified, not speculative).

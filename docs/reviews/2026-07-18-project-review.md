# Kotlin/CLR project review — 2026-07-18

**Reviewed revision:** `3983a5503418`  
**Scope:** `kotc` → BIR → `bir2cir` → CIR → `ilemit`, CLR stdlib, MSBuild SDK, NuGet packaging, tests/CI, documentation, security/robustness, and maintainability.  
**Assessment:** promising compiler architecture with unusually broad end-to-end coverage, but not yet release-ready. The highest risks are gaps in the CI release path, stale MSBuild state, one reproduced Kotlin semantic mismatch, and verification scripts that can accept a non-zero program exit as a pass.

## Executive summary

The three-layer compiler split is sound. `kotc` owns Kotlin frontend/IR emission, `bir2cir` owns Kotlin-to-CLR semantic lowering and reference metadata, and `ilemit` owns CLR emission. The reference/runtime stdlib split and the façade-free .NET metadata injector are also coherent. The repository has a large behavioral corpus (423 immediate case directories), differential testing against Kotlin/JVM, IL verification, round-trip testing, and a packaged-SDK test.

However, the default GitHub workflow does not run all gates that the Makefile calls canonical, including the only real NuGet-consumption gate. The shipped MSBuild targets fail to invalidate injected CLR metadata when `DotKtImport` changes and do not clean that metadata. `Regex.matchEntire` and `Regex.matches` currently disagree with Kotlin/JVM for ordinary alternation. Several primary shell gates compare stdout while discarding process exit status, allowing post-output crashes to pass. These are release-confidence defects, not just missing features.

### Priority summary

| ID | Severity | Perspective | Finding |
|---|---|---|---|
| F1 | High | CI / release | CI omits the packaged-SDK, schema, and IR-sanity gates and tests only Linux |
| F2 | High | MSBuild correctness | `DotKtImport` changes leave stale injection metadata; `dotnet clean` does not repair it |
| F3 | High | Kotlin semantics | `Regex.matchEntire`/`matches` reject valid full matches after a shorter alternation wins first |
| F4 | High | Test integrity | Multiple gates discard compile/run exit codes and can report a false green |
| F5 | Medium | Build robustness/performance | Intermediate BIR/CIR/type state is shared across configurations and downstream lowering always reruns |
| F6 | Medium | Packaging freshness | The packaged-SDK gate can package stale stdlib binaries when invoked on its own |
| F7 | Medium | Product documentation | The README and support matrix contradict the current implementation and each other |
| F8 | Medium | Maintainability | There are no toolchain unit tests; several semantic passes are very large |
| F9 | Low–Medium | Distribution / supply chain | NuGet packages lack package-level license, repository, readme, and third-party notice metadata |

## Findings

### F1 — High — CI does not exercise the complete release surface

**Evidence**

- `Makefile:98-113` defines `verify-schema`, `verify-sanity`, and `verify-packaged-sdk` as part of `make verify`.
- `.github/workflows/verify.yml:20-83` runs IL, differential, ktproj, round-trip, and wide-delegate tests, but none of those three gates.
- `scripts/verify-packaged-sdk.sh:2-19` explicitly calls itself the only gate that restores and consumes the five real nupkgs. Its comments record two prior releases broken by packaging-only defects.
- The workflow has one `ubuntu-latest` job (`.github/workflows/verify.yml:21-22`). It does not exercise the shipped `kotc.bat`, Windows path/quoting behavior, Visual Studio/MSBuild behavior, WPF, or WinUI.

**Impact**

A pull request can be green while breaking the published SDK graph, template version substitution, packaged reference/runtime selection, BIR/CIR schema, or offline semantic invariants. Linux-only validation is especially weak for a CLR compiler whose README advertises Visual Studio and whose mid-term goals include Windows-only UI stacks.

**Recommendation**

Run the complete canonical gate set in CI, preferably by invoking one authoritative aggregate rather than copying its list into YAML. Make packaged-SDK validation a distinct release-blocking job using artifacts from the same build. Add at least a Windows job for compiler installation, package restore, a `.ktproj` build/run, reverse C# interop, and template creation. Keep WPF/WinUI tests conditional to Windows rather than excluding the platform entirely.

### F2 — High — explicit `DotKtImport` is absent from incremental inputs and custom state survives clean

**Evidence**

- `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets:72-85` declares `DotKtInjectTypes` as incremental.
- Its command consumes `@(DotKtImport)` at line 85, but its `Inputs` at line 74 contain only Kotlin sources, resolved references, and the compiler.
- `KotlinCompile` consumes the resulting metadata at lines 110-126, so an unchanged stale metadata file also preserves stale BIR.
- The custom files/directories are rooted in `BaseIntermediateOutputPath` at lines 7-14, but the targets do not add them to `FileWrites` and define no clean target. Consequently `dotnet clean` leaves them behind.

**Reproduction**

1. Build a project whose source calls `System.Math.Abs` and whose project contains `<DotKtImport Include="System.Math" />`; it builds and prints `7`.
2. Remove only the `DotKtImport` item and rebuild. The build still succeeds and the previous output still runs.
3. Run `dotnet clean` and rebuild. It still succeeds because `obj/dotkt-clrtypes.meta` survives clean.
4. Change only a Kotlin source comment and rebuild. Metadata is finally regenerated and compilation fails with `unresolved reference 'System'`, which is the correct result without the explicit import.

A second metadata-only probe added and removed `System.Guid`: after removing the item, the metadata hash remained `857deaa1…` and still contained `System.Guid`; a Kotlin source change regenerated it to `0f7a922e…` and removed the stale type.

**Impact**

Adding an explicit import may appear to do nothing, while removing one can leave unauthorized/stale CLR surface available. Clean builds are not actually clean, making failures machine-state-dependent and particularly difficult to reproduce in IDEs and CI caches.

**Recommendation**

Materialize the ordered `@(DotKtImport)` values into a `WriteOnlyWhenDifferent` manifest, as already done for compile options, and make that file an input of `DotKtInjectTypes`. Register all generated files/directories for `Clean` (or add a dedicated `BeforeTargets="CoreClean"` target). Add an integration regression covering add, remove, no-op rebuild, and `dotnet clean`.

### F3 — High — `Regex.matchEntire` and `Regex.matches` have incorrect alternation semantics

**Evidence**

- `libraries/stdlib/clr/kotlin/text/regex/RegexClr.kt:83-86` implements `matches` through `matchEntire`.
- `RegexClr.kt:103-112` calls ordinary .NET `Regex.Match`, then accepts the result only if that first leftmost match spans the input.
- The source comment at lines 104-105 acknowledges that `a|ab` over `ab` returns no match, even though an anchored full match exists.
- This deviation is not listed in `docs/dotkt-semantics.md` or the user-facing CLR differences guide.

**Reproduction**

```kotlin
val re = Regex("a|ab")
println(re.matchEntire("ab")?.value)
println(re.matches("ab"))
```

Kotlin/CLR produced:

```text
null
False
```

Kotlin/JVM 2.4.0 produced:

```text
ab
true
```

**Impact**

Valid Kotlin programs silently choose a different result on CLR. This is broader than one alternation: lazy quantifiers and other patterns whose first search result is shorter than an available full-input result can be affected. Existing `il-regex` coverage uses patterns whose first match already consumes the whole input and therefore misses this class.

**Recommendation**

Perform an anchored regex-engine match, rather than filtering the result of a search. For .NET this can be implemented by a native helper using `\A(?:pattern)\z` while preserving options and capture behavior, or an equivalent binding that asks the engine to match the full region. Add JVM-differential cases for alternation, lazy quantifiers, empty input, anchors, and capturing groups.

### F4 — High — verification gates can accept programs that exit non-zero

**Evidence**

- `scripts/verify-differential.sh:118-128` appends `|| true` to JVM compilation/execution and every CLR stage. Lines 129-134 compare only non-empty normalized stdout; no exit code is retained.
- `scripts/verify-ktproj.sh:25-35` similarly pipes `dotnet run` through a filter and `|| true`, then compares only stdout.
- `scripts/verify-packaged-sdk.sh:140-157`, `278-280`, and `312-314` use the same pattern for executable, MPP, and template cases.
- The otherwise stronger primary IL gate has the same hole in reverse interop at `scripts/verify-il.sh:1141-1159`.

**Impact**

A program that prints all expected output and then throws, fails an assertion, or returns a non-zero code is reported as passing. Differential tests can also declare a match when both runtimes emit the same prefix and then fail. This weakens several of the gates used to justify semantic and packaging readiness.

**Recommendation**

Capture stdout and status independently for every compile/lower/emit/run stage. Require status zero before comparing output, and include stderr plus the failing stage in the result record. Add a harness self-test whose sample prints the expected text and then exits non-zero; every affected gate must reject it.

### F5 — Medium — project intermediate state is not configuration-isolated, and CIR/emission reruns on no-op builds

**Evidence**

- `DotKt.Toolchain.targets:7-14` places BIR, CIR, type metadata, import lists, and option manifests directly under `BaseIntermediateOutputPath`, normally the shared `obj/` directory. It does not include configuration, TFM, RID, or `IntermediateOutputPath`.
- `DotKtBir2Cir` at lines 144-153 has no `Inputs`/`Outputs`; every build removes and recreates the shared CIR directory.
- `DotKtIlEmit` and `DotKtRetarget` at lines 158-187 likewise execute after every normal C# placeholder compile.

**Impact**

Concurrent Debug/Release, RID, or multi-target builds of one project can delete or consume each other’s BIR/CIR/type state. Even a single no-op build repeats lowering, IL emission, and retargeting, reducing IDE/build responsiveness and increasing the window for races.

**Recommendation**

Root all custom state in `IntermediateOutputPath` (or explicitly include configuration/TFM/RID dimensions). Give BIR→CIR, emit, and retarget precise incremental inputs/outputs or stamps. Add parallel Debug/Release and repeated no-op build tests that verify output identity and that no compiler stages rerun unnecessarily.

### F6 — Medium — the packaged-SDK test can package stale stdlib binaries when run alone

**Evidence**

- `scripts/verify-packaged-sdk.sh:52-56` says it packs fresh nupkgs but invokes `pack-nuget.sh` directly.
- `scripts/pack-nuget.sh:68-79` rebuilds the frontend KLIB and stdlib DLLs only when the artifacts are missing. It does not use the source/tool fingerprints implemented in `scripts/lib.sh:110-152`.
- `make pack` has correct source prerequisites (`Makefile:65-93`), but neither direct `pack-nuget.sh` nor standalone `verify-packaged-sdk.sh` goes through those prerequisites.

**Impact**

After changing stdlib sources or a compiler stage that bakes the stdlib, a developer can run the package gate and successfully test nupkgs containing an older stdlib. The gate proves NuGet wiring but not necessarily the current revision’s packaged payload.

**Recommendation**

Make package assembly use the same fingerprint-aware `need_*` functions, or split “build current artifacts” from “pack exactly these artifacts” and require an explicit immutable staging directory. Ensure the package gate validates hashes/version metadata for all payloads produced from the reviewed commit.

### F7 — Medium — user documentation contradicts current behavior

**Evidence**

- `README.md:11-13` says the product has “no bundled libraries” and “ships no library of its own.”
- `README.md:43-52` then documents and ships a real Kotlin CLR stdlib; the NuGet layout also contains `DotKt.Stdlib`.
- `docs/user/supported-features.md:30` marks all Regex support unavailable and claims `find`, `matchEntire`, `containsMatchIn`, `replace`, `split`, and all match/group accessors throw. Current `il-regex`, `il-regexgroups`, and `il-regexreplace` cases exercise many of those APIs successfully, while `RegexClr.kt` shows a mixed implemented/TODO surface.
- `README.md:56-58` gives hard-coded corpus and pass counts that are already inconsistent with the larger current tree.

**Impact**

Users cannot reliably decide whether the compiler fits their project. The “no library” wording also obscures runtime deployment and licensing expectations. The stale Regex claim hides both working functionality and the real semantic defect in F3.

**Recommendation**

Clarify that the product bundles no UI/framework abstraction but does ship its CLR Kotlin stdlib. Generate the support matrix and gate counts from a machine-readable capability inventory where possible. List partial Regex support method-by-method and document any deliberate semantic deviations explicitly.

### F8 — Medium — semantic logic has no isolated unit-test layer

**Evidence**

- No test source or test project exists under `toolchain/` (`src/test`, `test`, and `*Tests.csproj` all return zero files).
- `toolchain/kotc/build.gradle.kts:15,29-31` configures a test dependency/platform but has no tests to execute.
- Several high-risk semantic components are large: `SuspendColdLowering.cs` is about 3,383 lines, `InlineSplice.cs` about 2,820, `facadegen/Program.cs` about 2,326, and `BirEmitterCalls.kt` about 1,698.

**Impact**

The 423-case integration corpus is valuable, but every diagnosis crosses process and serialization boundaries. Small parser, type-system, reference-selection, or lowering regressions are slower to localize, and a harness defect such as F4 undermines confidence across the entire suite.

**Recommendation**

Add focused tests at each stable boundary: TypeNode/BIR/CIR serialization, IR sanity, reference conflict resolution, CLR metadata injection, overload selection, individual lowering passes, and IL signature generation. Retain end-to-end cases as acceptance tests. Split the largest passes by transformation responsibility after characterization tests exist.

### F9 — Low–Medium — NuGet provenance and license metadata are incomplete

**Evidence**

- The four nuspec files contain ID/version/author/description/tags, but no package license, repository URL/commit, project URL, or package readme (`packaging/DotKt.*/*.nuspec:3-13`).
- The template project similarly lacks these fields (`packaging/DotKt.Templates/DotKt.Templates.csproj:3-15`).
- `DotKt.Toolchain` redistributes Kotlin compiler/runtime jars, kotlinx-coroutines, annotations, Mono.Cecil, and MetadataLoadContext binaries. No package-level license or third-party notice file was found in the staged toolchain.

**Impact**

Consumers and package scanners receive little provenance or licensing context, and maintainers cannot trace a nupkg to a source revision from NuGet metadata. This is a distribution-governance risk. This review does not make a legal-compliance determination; embedded upstream artifacts may contain some license material, but the package-level record is absent.

**Recommendation**

Add SPDX-compatible package license metadata, repository URL and commit, project URL, and a packaged readme to all five packages. Generate and include a third-party notices/SBOM artifact with component versions and licenses, and validate it in the package gate.

## Existing residual risks

These are known baselines rather than newly discovered defects, but they affect release posture:

- Runtime and Kotlin/JVM differential baselines are empty, which is a strong signal.
- Six assemblies remain in `XFAIL_ILVERIFY`: `coctxkey`, `cointercept`, `genbaseext`, `awaitintercept`, `sort`, and `del2`. They are documented as runtime-safe representation mismatches, but formally invalid IL remains a compatibility and ahead-of-time/runtime portability risk.
- Coroutine/Sequence functionality remains explicitly incomplete. The support matrix should continue to present it as experimental until runtime and round-trip coverage are complete.

## What is working well

- The BIR/CIR separation makes semantic ownership visible and supports schema/sanity validation.
- Compile-time and runtime reference sets are deliberately distinct in the MSBuild targets, avoiding reference-assembly execution mistakes.
- The stdlib has a clear frontend-KLIB/reference-DLL/runtime-DLL split, and binding metadata is held in stdlib sources rather than hard-coded throughout the emitter.
- Exact reference identity/type conflict guards, CLR-to-Kotlin round-trip metadata, differential JVM testing, ILVerify, and isolated local-feed package testing are all strong foundations.
- The empty runtime, differential, round-trip, and packaged-SDK XFAIL maps set an appropriately strict direction; the remaining issue is ensuring every gate observes process status and runs in CI.

## Verification performed

| Check | Result |
|---|---|
| `./gradlew :kotc:compileKotlin --stacktrace` | Passed; emitted a Gradle 8.14 deprecation warning for future Kotlin 2.5 compatibility |
| Focused Kotlin/CLR Regex probe | Reproduced `null` / `False` for `Regex("a|ab")` over `"ab"` |
| Same source on Kotlin/JVM 2.4.0 | Produced `ab` / `true` |
| Focused `.ktproj` `DotKtImport` probe | Reproduced stale metadata across rebuild and `dotnet clean`; source edit forced correct failure |
| `scripts/verify-il.sh` | Green: 367/367 runtime cases passed; 282 ILVerify passes and the six documented XFAILs |
| `scripts/verify-schema.sh` | Green: 1,741 BIR/CIR files, 94 node kinds, 0 violations |
| `scripts/verify-sanity.sh` | Green: 1,059 CIR files, 0 violations |
| `scripts/verify-packaged-sdk.sh` | Green: executable, library, MPP SDK, and installed-template package cases passed |

## Recommended remediation order

1. Fix F4 first so gate results are trustworthy.
2. Add the missing release gates to CI (F1), including Windows coverage.
3. Fix and regression-test MSBuild import invalidation/clean behavior (F2) and intermediate isolation (F5).
4. Correct Regex full-match semantics and add JVM-differential coverage (F3).
5. Make packaged inputs freshness-safe (F6).
6. Reconcile the user documentation (F7), then build unit coverage around the most volatile passes (F8).
7. Complete package provenance, licensing metadata, and SBOM/notice generation before public distribution (F9).

# Design: NUnit in-process test harness — migrating off the per-case bash gate

**Status:** design + working pilot (this doc's companion: `tests/nunit-pilot/`, `tests/nunit-roundtrip/`).
**Motivation:** `docs/reviews/2026-07-19-cases-test-design-audit.md` — the `cases/` gate grew to 384 `il-*`
directories, each a separate `kotc → bir2cir → ilemit → dotnet-run → ilverify` **process**, driving the full
gate to ~45 min. The audit's structural remedies (its items #6, #8, #12–16) and its dedup/battery remedies
(#1–5, #11) are realized by moving to an **in-process NUnit suite**: Kotlin `@TestAttribute` methods,
batch-compiled by the DotKt MSBuild SDK, discovered and run in-process/parallel by `dotnet test`.

This is not "improve the bash scripts". It **replaces** `verify-il.sh`, `verify-roundtrip.sh` (1000+ lines) and
`verify-ktproj.sh` with one model, leaving only a short, irreducible shell lane (see §7).

---

## 1. The unified model

Every current gate flavor collapses into **one** artifact type: an NUnit test project built by the DotKt SDK.

| Current gate | Current unit | Becomes |
|---|---|---|
| `verify-il.sh` (381 samples) | one `cases/il-*/app.kt` → own dll, stdout-diff, per-dll ilverify | a `@TestAttribute fun` asserting the **value**; related cases grouped into one fixture **battery** = one assembly |
| `verify-roundtrip.sh` | heredoc lib compiled, consumed as Kotlin, stdout-diff | an NUnit project that **`<ProjectReference>`s** a DotKt library project and asserts the consumed API |
| `verify-ktproj.sh` | MSBuild `.ktproj` end-to-end | the same: reference the sample project, assert — the SDK build graph *is* the orchestration |

The key realization: **the MSBuild build graph already does the "compile lib → produce dll → make it available
to the consumer" step** that `verify-roundtrip.sh`/`verify-ktproj.sh` hand-roll. And a DotKt project referencing
another DotKt dll **is** the facadegen re-import round-trip. So il + roundtrip + ktproj are the same thing:
NUnit test projects that either contain DotKt test code directly (il) or `<ProjectReference>` a DotKt lib
(roundtrip/ktproj), all run by `dotnet test`.

### Case granularity ≠ execution granularity (audit #15)

A scenario is a **method**; a compiler invocation is an **assembly**. The old gate fused them
(1 scenario = 1 directory = 1 kotc = 1 bir2cir = 1 ilemit = 1 dotnet = 1 ilverify). The battery model separates
them: many independent scenarios → **batch-compiled per environment** → the runner reports each by name.
`il-generic`..`il-generic6` (6 permanent processes, audit §5) become 6 methods in **one** `GenericsTests`
fixture, compiled once. `il-cwindowed`/`il-cwindowedv`, `il-indices`/`il-indicesv`, the 36 duplicate
`harness.kt` copies (audit §13) — all collapse to methods + **one** shared harness file.

---

## 2. Locked design decisions

### D1 — il case → `@TestAttribute` asserting the VALUE (not stdout)

Each `cases/il-<x>/app.kt` (today: `println` compared to a hardcoded expected string) becomes a
`@TestAttribute fun` with `ClassicAssert.AreEqual(expected, actual)` — asserting the computed value directly.

**Strictly stronger and self-documenting.** Stdout-diff only proves "the whole concatenated text matched";
a value assert pins each contract to its own method, so a regression fails *exactly* the broken contract with a
typed expected/actual diff. Codex confirmed: value asserts dominate stdout-diff except where the contract *is*
textual — and even then you assert `.toString()` explicitly (this **preserves** the exact textual check the
stdout encoded, e.g. `ClassicAssert.AreEqual("P(x=1, y=20, z=3)", p.copy(y = 20).toString())`), which is still
scoped to one method. `null` slots become `IsNull` (a wrong non-null can't alias to the string `"null"`).
Booleans become `IsTrue`/`IsFalse` (can't alias to `"True"`).

**Where stdout is genuinely the contract** (ordering of interleaved side effects, a program's literal console
protocol): keep it as an explicit `capture-stdout` assertion inside the test, or leave it in the small shell
lane (§7). These are rare; the pilot found none in its slice.

### D2 — ilverify: once per assembly, machine-readable XFAIL baseline

Keep formal verification, but run it **once over the built test assembly**, not per case. ilverify only reports
on the target assembly's own methods; the `-r` sets are resolution scopes (shared framework + the assembly's
output dir, which already holds NUnit/stdlib/producer dlls). `tests/run-ilverify.sh` implements this with an
`ILVERIFY_XFAIL` map (substring → tracking issue), mirroring `verify-il.sh`'s `XFAIL_ILVERIFY` discipline:
**green iff every finding is baseline-listed; any finding outside it is a NEW-FAIL.** 382 per-dll ilverify runs
→ one per battery assembly (~16).

### D3 — JVM oracle (differential): shared sources, backend-selected assertions — NOT re-authored

Do **not** re-author the 175/203 differential cases. Codex's recommendation (validated): factor the assertion
surface behind a tiny alias so the **same** Kotlin test body compiles under two backends:

- CLR: `Test` → NUnit `TestAttribute`, `assertEq` → `ClassicAssert.AreEqual` (this harness).
- JVM: `Test` → JUnit-5 `@Test`, `assertEq` → `kotlin.test`/JUnit assertions (a `kotlinc` + JUnit oracle project).

Both run the identical scenario bodies; no double authoring, no golden files (goldens add serialization +
stale-file + review cost — reserve them for very large structured results). Apply the oracle **only where
JVM-equivalence is the specific claim**; per project doctrine JVM is a *reference, not a compat target*, so most
contracts are asserted directly (D1) and the JVM oracle shrinks to the handful of "same observable result as
Kotlin/JVM" cases. A JVM NUnit shim is possible but rejected (it would couple the oracle to emulating NUnit's
overload/equality semantics).

### D4 — local repo SDK (not a published nuget)

The pilot pins the **published** `DotKt.Sdk/0.9.6-rc7` (present in the local nuget cache) to prove the concept
offline. The repo gate must instead consume the **locally-built** SDK from `make pack` → `build/nuget-feed`.
Mechanism (Codex-validated; **NuGet SDK resolver**, *not* `MSBuildSDKsPath` — the latter bypasses the packaged
resolution path we want to test): a repo/test-level `nuget.config` with a local feed + package-source mapping,
restoring through an **isolated `globalPackagesFolder`** — exactly the model `scripts/verify-packaged-sdk.sh`
already uses. Template:

```xml
<configuration>
  <config>
    <add key="globalPackagesFolder" value="build/test-package-cache" />   <!-- recreated per gate -->
  </config>
  <packageSources>
    <clear />
    <add key="dotkt-local" value="build/nuget-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="dotkt-local"><package pattern="DotKt.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Then either `<Project Sdk="DotKt.Sdk/$(version)">` or a central `global.json` `msbuild-sdks` entry with
`<Project Sdk="DotKt.Sdk">`. **Caveat (Codex):** NuGet prefers an existing exact-version package in the cache —
never overwrite the same version in a persistent cache; recreate the isolated `globalPackagesFolder` per gate
(as `verify-packaged-sdk.sh` does) or stamp a unique prerelease version across all DotKt packages. Note
`+build-metadata` is **not** a distinct NuGet identity (`packaging/DotKt.Versions.props`).

### D5 — negative / multi-module handled by dedicated small mechanisms

- **Compile-fail + diagnostic-text** cases genuinely cannot be runtime NUnit tests (the code must *not*
  compile). They stay in a small shell lane that invokes `kotc` and asserts the diagnostic text — the only
  irreducible non-NUnit il-lane.
- **Multi-module / roundtrip** = separate test projects `<ProjectReference>`-ing produced dlls (§3). Fully in
  the NUnit model, no bash.

---

## 3. Round-trip via `<ProjectReference>` — the DLL-not-source invariant

**Structure (two projects, producer built to a dll):**

```
tests/nunit-roundtrip/
  producer/  RoundtripProducer.ktproj   (OutputType=Library) + Api/Money/Shapes/Async.kt
        ↓ <ProjectReference>  (consumed via the BUILT dll)
  consumer/  RoundtripConsumer.Tests.ktproj  (IsTestProject) + RoundtripTests.kt + harness/
```

**CRITICAL INVARIANT:** the consumer must consume the producer's **built dll through facadegen re-import** —
never the producer's Kotlin **source**. If producer `.kt` and the test end up in one compilation, it is not a
round-trip. The producer lives in a **sibling** directory so the consumer's `**/*.kt` glob cannot capture it.

**Verified in the pilot:** the consumer's `obj/dotkt-bir/` contained only `RoundtripTests.bir.json` +
`Coroutines.bir.json` (its own two files) — no `ApiKt`/`ShapesKt` BIR. The `roundtrip.api.*` symbols appear in
the consumer BIR **only as references** (call targets, type tokens), and the API resolves from the built
`RoundtripProducer.dll` on the ReferencePath. That is the facadegen re-import path: `[KotlinFunction]` /
`[KotlinFileClass]` / operator / infix / inline / suspend metadata stamped by ilemit, read back by facadegen,
restored on the synthesized FIR (`docs/design-kotlin-metadata-attributes.md`). The pilot round-trips:
top-level fn+prop, overloads, default args, extension, inline, operator `+`, infix, interface default method +
inheritance + virtual dispatch, generics, nullable reference/return, and a cross-module `suspend` call.

**Known re-import gap (mapped, not exercised):** a cross-module **nullable value-type generic** `T?` in
param/field position re-imports as bare non-null `T` (`verify-roundtrip.sh`'s `RT_XFAIL`
`roundtrip-nullable-vt-generic`, #109/#86, +#147/#127 restricts the nullable-generic carrier to method-RETURN).
The pilot uses nullable-value-type **returns** (which work) and nullable **reference** params, and documents the
param/field value-type axis as the one known gap.

---

## 4. Battery layout (audit #6/#13/#15/#16)

- Group the ~382 il cases into **8–16 battery assemblies of ~25–50 related cases** (Codex: NUnit's meaningful
  process-isolation boundary is the **assembly**, not the fixture). Suggested batteries: plain-JVM-compatible,
  plain-CLR-specific, generics, collections, nullable/NRT, coroutine (+ the one shared harness), System-imports
  / BCL-interop, injected-runtime interop, cross-file, cross-module/roundtrip.
- **One shared `harness/Coroutines.kt`** (`dotkt.support.blockOn`) per coroutine assembly replaces the 36
  duplicated copies (audit §13). `blockOn` is a blocking call returning the coroutine result, so it asserts as
  an ordinary value.
- The audit's `Task.Delay(1)` non-determinism (§10) is a **separate** battery using a `TaskCompletionSource` +
  explicit barrier to force the slow-path (await-before-complete) deterministically — orthogonal to the harness
  shape and done once, not per case.
- A **single manifest** drives registration (audit #8/#9/#14): the fixtures *are* the manifest (discovered by
  `dotnet test`); the dual `il_check`/`PURE` hand-lists disappear. A machine-readable coverage inventory
  (feature id, layer, JVM-comparable?, cost class, supersedes) can be attributes on fixtures/methods, queryable
  by reflection.

---

## 5. Batching risks & mitigations (Codex)

| Risk | Mitigation |
|---|---|
| One compile error fails the whole battery | Batteries are moderate (25–50); a broken case is isolated by rebuilding only its shard |
| `TypeLoadException` / bad metadata / fixture static-init breaks **discovery** | Avoid fixture/static initialization; **enforce an expected discovered/executed test count** — zero or missing tests **fails** the gate |
| `Environment.Exit`, stack overflow, native fault kills the test host → lost results | Keep process-exit / known-crash-sensitive / console-protocol cases in the small process-isolated shell lane |
| Static state / culture / cwd / console leaks between tests | Disable parallelism for process-global tests; reset mutable state explicitly; default NUnit is sequential within an assembly |
| Former standalone cases collide via top-level names | Unique top-level names per battery (the pilot uses `Shape2`/`Rect2` etc. to avoid clashes); one battery = one namespace |
| A hard crash takes down remaining results | ~8–16 shards bound the blast radius to one shard; if truly needed, run each fixture via a separate `dotnet test --filter` |

Order: **build → ilverify (`--no-build`) → `dotnet test --no-build`**, with the discovered-count assertion.

---

## 6. Bugs surfaced by the pilot (value-assert batteries catch real defects)

1. **Grandchild override of an interface DEFAULT method is miscompiled.** `Shape { fun describe() = ... }`,
   `Rect : Shape` (does **not** override `describe`), `Square : Rect` (**overrides** `describe`) — calling
   `describe()` on a `Square` through a `Shape` reference dispatches to the **interface default**, not
   `Square`'s override (got `"shape area=16"`, expected `"square area=16"`). **Boundary pinned in-process**
   (`tests/nunit-pilot/fixtures/InterfaceDispatchTests.kt`): a **direct** child override of an interface default
   works (`Circle : Shape` overriding `describe` → correct); only the **grandchild** override through a
   non-overriding intermediate fails. This is a **general compiler bug** (reproduces in-process, not a re-import
   gap) — the audit's 384-case corpus does not cover this exact shape. Should be filed as a GitHub issue.
2. **`joinToString{}` synthetic-delegate `DelegateCtor` ilverify finding** — the known runtime-safe formal-only
   `#170/#150` (the `verify-il.sh` `[defargs]` XFAIL); reproduced here and baseline-listed in
   `tests/run-ilverify.sh`. Confirms the harness reproduces the existing formal-verification coverage.

---

## 7. What stays in the shell lane (short, irreducible)

- **Compile-fail + diagnostic-text** assertions (the code must not compile).
- **ilverify** invocation itself (over the built assemblies) — thin wrapper `tests/run-ilverify.sh`.
- **Strict metadata inspection** (attribute blobs / exact CLR signatures) — **NB most of this can be done by
  reflection from *inside* an NUnit test**, so it need not be shell.
- **C#→Kotlin / Kotlin→C# cross-language ABI** checks.
- **Mutually-incompatible build modes** (MPP, special MSBuild props) needing separate project configs.
- **NuGet package restore itself** (the packaged-SDK release gate, `verify-packaged-sdk.sh`).

Everything else in `verify-il.sh` / `verify-roundtrip.sh` / `verify-ktproj.sh` is replaced.

---

## 8. Measured pilot numbers

Slice: **18 `il-*` cases → 27 `@TestAttribute` methods** in one assembly (plain / generics / collections /
nullable / .NET-interop / coroutine), plus a **producer→consumer roundtrip** (10 methods, 2 assemblies).
(Dev box, published `0.9.6-rc7` from the local cache.)

| Measurement | Value |
|---|---|
| IL pilot — clean `dotnet test` (restore + kotc + bir2cir + ilemit + retarget + run) | **16.1 s** |
| IL pilot — warm `dotnet test` (no source change) | **6.9 s** |
| IL pilot — NUnit **execution** phase (27 tests) | **42 ms** |
| Roundtrip — clean `dotnet test` (producer + consumer, 2 assemblies) | **24.2 s** |
| Roundtrip — NUnit execution (10 tests) | **13 ms** |
| ilverify — 3 emitted assemblies (once each) | **0.37 s total** |
| **Single il case** as its own DotKt Exe (build) — the per-case unit the bash gate pays | **~8.0 s** |
| Single il case — `dotnet run` | **0.036 s** |

**Interpretation.** The dominant cost is the **compiler invocation** (~8 s: msbuild + kotc JVM start + tool
JIT). The battery model pays it **once per ~25–50 cases** instead of once per case:

- 18 cases as **one** battery = 16 s clean / 6.9 s warm.
- The same 18 cases as 18 separate DotKt builds ≈ 18 × 8 s ≈ **144 s** → the tiny slice is already ~**9×**
  faster, and the ratio grows with battery size.
- **Compiler invocations: 382 → ~16 (≈ 24× fewer).** **ilverify: 382 per-dll → ~16** (0.12 s each measured).
  **Run: 382 process starts → in-process** (27 tests = 42 ms; extrapolates to well under 1 s for ~2,000
  assertions).

**Full-suite extrapolation (382 cases as ~16 battery assemblies):** compile is the whole budget — ~16
assemblies × ~20–30 s build, MSBuild-parallel across projects on the 24-core box (4–8 concurrent) →
**~3–7 min wall**, with the run + ilverify phases adding seconds. This clears the audit's **< 15 min** target
(#15) with margin, versus the current ~45 min.

---

## 9. Go / No-go

**GO on full migration.** Evidence:

- **Model proven end-to-end green.** IL battery (27 tests) and a real producer→consumer round-trip (10 tests)
  both pass `dotnet test`; ilverify runs once per assembly and is green against a machine-readable XFAIL
  baseline; the DLL-not-source invariant is verified.
- **Every audit remedy has a concrete mechanism** (§1–§7): dedup→batteries, one shared harness, single
  manifest, once-per-assembly ilverify, scenario/execution separation, roundtrip/ktproj with zero bash.
- **The performance target is met with margin** (§8): ~24× fewer compiler invocations, ~3–7 min projected.
- **It already earns its keep**: the value-assert battery surfaced a **real, previously-uncovered compiler
  bug** (§6.1) that the 384-case stdout corpus missed.

**Cleanly-mapping vs hard parts.**

- *Cleanly maps:* plain, generics, collections, nullable, BCL-interop (`import System.X`), coroutine (shared
  harness), roundtrip/ktproj via `<ProjectReference>`. These are the large majority.
- *Hard parts + plan:* (a) **injected-runtime interop** cases that ship a `runtime.cs` (e.g. `il-transinj`)
  need the C# helper as a `<ProjectReference>`'d csproj — mechanically identical to the roundtrip producer, one
  extra project per interop battery. (b) **compile-fail/diagnostic** cases stay in a small `kotc`-invoking
  shell lane (§5/§7). (c) **JVM oracle** needs the dual-backend alias + a `kotlinc`+JUnit project (§D3), scoped
  to the few genuine JVM-equivalence claims. (d) **local-SDK feed** wiring (§D4) is designed and matches
  `verify-packaged-sdk.sh`; the pilot used published rc7 to stay offline-reproducible.

**Recommended sequencing:** stand up the battery skeleton + the local-SDK `nuget.config` + the discovered-count
guard; migrate batteries family-by-family (each migration deletes the superseded `cases/il-*` dirs and their
`il_check`/`PURE` lines in the **same** change, per the audit's #14); convert roundtrip/ktproj; add the JVM
oracle alias last. Keep the bash gate running until each family is proven migrated (do not delete `cases/` or
`scripts/verify-*.sh` wholesale up front).

# NUnit test harness — pilot + production migration

Migrating the per-case bash gate (`cases/il-*` + `scripts/verify-il.sh` / `verify-roundtrip.sh` /
`verify-ktproj.sh`) to an **in-process NUnit suite**: Kotlin `@TestAttribute` methods, batch-compiled by the
DotKt MSBuild SDK, discovered and run in-process by `dotnet test`.

Design + decisions + measured numbers + go/no-go: **`docs/design-nunit-test-harness.md`**.
Per-family migration steps: **`docs/nunit-migration-playbook.md`**.
Motivation: **`docs/reviews/2026-07-19-cases-test-design-audit.md`**.

## Production suite (local-SDK) — the real migration target

- `il/` — `DotKt.Tests.Il.ktproj`, the production IL-battery suite where migrated families land (one `.kt` per
  family under `il/fixtures/`). **First migrated family:** `il/fixtures/GenericsTests.kt` (6 `@TestAttribute`
  methods replacing `cases/il-generic .. il-generic6`).
- `nuget.config` — **active**; routes every `DotKt.*` package for the test projects to the LOCALLY-BUILT SDK
  feed (`make pack` → `build/nuget-feed`) with an isolated `globalPackagesFolder`, so the suite tests the
  compiler in THIS working tree (design D4). Copied from `nuget.config.local-sdk.template`.
- `run-nunit-il.sh` — the gate driver: builds each battery against the local feed, runs `dotnet test` with a
  TRX logger, asserts the **discovered test count** equals its `EXPECTED` manifest (a dropped/added method or a
  0-test discovery failure reddens the gate), then runs `run-ilverify.sh` once per emitted assembly.

Run it (needs `make pack` first to populate `build/nuget-feed`):

```bash
make pack                 # build+pack the local DotKt SDK -> build/nuget-feed
bash tests/run-nunit-il.sh   # local-SDK build + dotnet test + discovered-count guard + ilverify
```

## Pilot (published-SDK, offline)

The `nunit-pilot/` and `nunit-roundtrip/` projects pin the **published** `DotKt.Sdk/0.9.6-rc7` (present in the
local NuGet cache) as the offline-reproducible proof of the model. NOTE: with `tests/nuget.config` active, a
`dotnet test` run of these projects will also resolve `DotKt.Sdk` from the local feed (same version) — harmless,
since the local feed is built from the same tree.

## Layout

- `nunit-pilot/` — one battery assembly migrating 18 `il-*` cases → 27 `@TestAttribute` methods
  (plain / generics / collections / nullable / .NET-interop / coroutine). Each former stdout-diff case asserts
  the **value** directly (`ClassicAssert.AreEqual` / `IsNull` / `IsTrue`). Includes a shared
  `harness/Coroutines.kt` (`dotkt.support.blockOn`) replacing the 36 duplicated `cases/*/harness.kt` copies.
- `nunit-roundtrip/` — a producer→consumer round-trip via `<ProjectReference>`: `producer/` is a DotKt
  **library** consumed by `consumer/` (an NUnit project) through its **built dll** (facadegen re-import),
  **never its source** (producer is a sibling dir so the consumer `**/*.kt` glob can't capture it).
- `run-ilverify.sh` — runs ilverify **once** over each emitted test assembly, with a machine-readable
  `ILVERIFY_XFAIL` baseline (mirrors `verify-il.sh`'s `XFAIL_ILVERIFY`).
- `nuget.config.local-sdk.template` — how the repo gate consumes the **locally-built** SDK
  (`make pack` → `build/nuget-feed`) instead of a published version (design D4).

## Run the pilot

```bash
# IL battery (27 tests)
( cd tests/nunit-pilot && dotnet test )
# Round-trip through a ProjectReference (10 tests)
( cd tests/nunit-roundtrip/consumer && dotnet test )
# Formal IL verification, once per emitted assembly
bash tests/run-ilverify.sh \
  tests/nunit-pilot/bin/Debug/net10.0/DotKt.Nunit.Pilot.dll \
  tests/nunit-roundtrip/consumer/bin/Debug/net10.0/RoundtripConsumer.Tests.dll \
  tests/nunit-roundtrip/consumer/bin/Debug/net10.0/RoundtripProducer.dll
```

The pilot pins the **published** `DotKt.Sdk/0.9.6-rc7` (present in the local NuGet cache) so it reproduces
offline; the repo gate should swap in the local-SDK feed (see the template + design D4).

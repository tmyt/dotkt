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

## Supporting files

- `run-ilverify.sh` — runs ilverify **once** over each emitted test assembly, with a machine-readable
  `ILVERIFY_XFAIL` baseline (mirrors `verify-il.sh`'s `XFAIL_ILVERIFY`); driven by `run-nunit-il.sh`.
- `nuget.config.local-sdk.template` — how the gate consumes the **locally-built** SDK
  (`make pack` → `build/nuget-feed`) instead of a published version (design D4).

# NUnit test-harness pilot

Working pilot for migrating the per-case bash gate (`cases/il-*` + `scripts/verify-il.sh` /
`verify-roundtrip.sh` / `verify-ktproj.sh`) to an **in-process NUnit suite**: Kotlin `@TestAttribute` methods,
batch-compiled by the DotKt MSBuild SDK, discovered and run in-process by `dotnet test`.

Design + decisions + measured numbers + go/no-go: **`docs/design-nunit-test-harness.md`**.
Motivation: **`docs/reviews/2026-07-19-cases-test-design-audit.md`**.

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

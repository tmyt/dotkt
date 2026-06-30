# Prioritized task list

> A **priority-ordered backlog**, NOT a ship checklist (no release is committed at any point here — see
> MEMORY `release-flow-0.9.4-accumulate`). Last updated 2026-07-01.

## Main sequence (priority order)

1. **rt-green** — drive the rt stdlib build (`build-clr-stdlib-runtime.sh --emit`) to completion. *(in progress)*
2. **exception map → @ClrTypeAlias** — retire kotc's `BirMappings.NET_EXCEPTIONS` hardcoded `kotlin.*Exception→System.*`
   map (`@ClrTypeAlias` the stdlib exception classes + bir2cir substitutes + delete the kotc map). Plus quarantine the
   33 old `clr.Clr` samples. (MEMORY `exception-map-to-clrtypealias`)
3. **implicit ref-passing** — `@ClrRefArgument` byref for stdlib methods; unblocks atomics (Interlocked), TryParse,
   DivRem. Kotlin has no ref/out syntax → binding-metadata-driven. (MEMORY `implicit-ref-passing-to-stdlib-methods`)
4. **facadegen app .NET interop** — operators (`op_*`), C#-origin extension methods, static `.Companion` routing,
   dual-rep collision (`import System.Text.StringBuilder` vs stdlib alias).
5. **netType→bir2cir migration completion** — finish removing kotc's CLR knowledge (the `kotlin.*` half of the maps;
   the `java.*` half is removed).
6. **coroutine lowering layer** — deferred design (Task-based). (MEMORY `coroutine-lowering-layer-deferred`)

## App / MSBuild / round-trip (added 2026-07-01; cluster around #4/#5)

7. **MSBuild app + lib** — build BOTH an app and a library with MSBuild, and reference the lib from the app via
   `<ProjectReference>`.
8. **Round-trip comprehensive review** — audit for any Kotlin semantics the Roundtrip attributes CANNOT restore
   (find the gaps, not just the known ones).
9. **MSBuild practical cases** — implement a variety of practical sample cases and confirm they build AND run via MSBuild.

## Cross-cutting categories (not in the linear sequence)

### A. Known bugs (MEMORY known-bugs)
- cross-module default-args (frontend jar drops default VALUES → IrErrorExpression; ~20 samples)
- generic ext-property getter typeargs (`List.last()`/`lastIndex` "not fully instantiated")
- dual-representation open cases (Comparable-self-ref / `use{}`)
- `@InlineOnly` drops `@ClrIntrinsic` cross-module (direct `s[i]=c`)

### B. Layer-purity follow-ups + performance
- kotc "reads NEITHER annotation" final form — move `substitutedAway` (type-strip) + rule-3 helper-emission to bir2cir
- `stackBuffer`/`Span` `FqName.ROOT` → `kotlin.clr.*` (§6)
- **static-helper (rule-3) performance review** — audit the stdlib pieces implemented as static helpers for perf
  problems; reimplement them a better way where found. *(added 2026-07-01)*

### C. rt-green internals (part of #1)
- unsigned value-class conversions — FIXED via the inline-class `.data` erasure collapse
- BLOCKED stdlib bindings (unsigned `Div_Un` etc., awaiting ilemit ops)

### D. Hygiene / recording
- quarantine/remove the 33 old `clr.Clr` samples (testing a removed feature)
- `docs/dotkt-semantics.md` — record this session's behavioral deltas
- `CHANGELOG` `## Unreleased` — accumulate this session's fixes (per `release-flow-0.9.4-accumulate`)
- **`scripts/` cleanup** — retire/consolidate old scripts (retired-backend leftovers, stale stdlib builders).
  *(added 2026-07-01)*

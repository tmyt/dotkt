# docs/ index — where to read what

## Start here (users)

| Doc | What it answers |
|---|---|
| [user/getting-started.md](user/getting-started.md) | install, `dotnet new dotkt-cli`, hello world, build & run |
| [user/using-dotnet-from-kotlin.md](user/using-dotnet-from-kotlin.md) | `import System.X`, statics/events/delegates/`byref`, the dual-view rule |
| [user/kotlin-on-clr-differences.md](user/kotlin-on-clr-differences.md) | the readable tour of deviations from Kotlin/JVM |
| [user/supported-features.md](user/supported-features.md) | the scannable supported / not-yet matrix |

## Canonical references

| Doc | Role |
|---|---|
| [dotkt-semantics.md](dotkt-semantics.md) | **THE** JVM-deviation reference — every deliberate behavioral difference, with round-trip audit (§10) |
| [ship-tasks.md](ship-tasks.md) **§0** | the **binding** layer architecture (facadegen / kotc / bir2cir / ilemit reference-artifact split + invariants) |
| [design-fir-bir-cir-il.md](design-fir-bir-cir-il.md) | the backend Layer Contract (BIR/CIR shapes, responsibilities) |

## Current tasks / status

| Doc | Role |
|---|---|
| [master-task-inventory.md](master-task-inventory.md) | **the canonical "what's left" ledger** (de-duplicated; wins over all other task docs) |
| [remaining-tasks.md](remaining-tasks.md) | the 1.0 ship checklist (definition of done) |
| [coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md) | the LIVE plan for the coroutine bundle (【6】) |

## Design records (one per living decision)

- [design-charsequence-clr-string.md](design-charsequence-clr-string.md) — `CharSequence` = `string` (3-point model)
- [design-clr-collection-binding.md](design-clr-collection-binding.md) — collections → BCL interfaces + iterator bridge
- [design-clr-property-model.md](design-clr-property-model.md) — every Kotlin property = a real CLR property
- [design-clr-stdlib-ref-runtime-split.md](design-clr-stdlib-ref-runtime-split.md) — the jar / ref.dll / rt.dll artifact split
- [design-compiler-modes.md](design-compiler-modes.md) — per-stage modes (ref / rt / app) + attribute emission
- [design-primitive-dual-representation.md](design-primitive-dual-representation.md) — bare `Int` = `System.Int32`, type-arg = `kotlin.*`
- [design-kotlin-metadata-attributes.md](design-kotlin-metadata-attributes.md) — the `[Kotlin*]` round-trip attribute set
- [design-stdlib-compilation.md](design-stdlib-compilation.md) — compiling the real stdlib (cardinal rule: fix stdlib-side)
- [coroutine-abi.md](coroutine-abi.md) — the `suspend` ⇔ `Task<T>` ABI contract
- [design-coroutines-clr.md](design-coroutines-clr.md) / [coroutine-il.md](coroutine-il.md) — coroutine design + IL strategy records
- [design-il-generics.md](design-il-generics.md) / [design-il-cfg.md](design-il-cfg.md) — Reflection.Emit generics gotchas / CFG lowering (as-built)
- [csharp-retirement-design.md](csharp-retirement-design.md) — the C#-backend retirement (as-built record)

## Audit / generated

- [clr-stdlib-intrinsic-audit.md](clr-stdlib-intrinsic-audit.md) — the `@ClrIntrinsic` binding model (three rules) + per-area decisions
- [clr-stdlib-actual-index.md](clr-stdlib-actual-index.md) — GENERATED (`scripts/gen-stdlib-actual-index.py`); do not hand-edit
- [bir-coverage.md](bir-coverage.md) — which IR nodes the backend lowers

## archive/

Superseded/historical docs, each stamped with a `HISTORICAL — superseded by …` header naming its successor. They are
kept for rationale only — **never work from an archived doc**. Policy: when a doc's content is fully absorbed by a
successor (usually `master-task-inventory.md`), move it here, stamp the header, and repoint inbound links.

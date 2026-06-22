# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## 0.9.1 — 2026-06-23

Language/stdlib long-tail completion + a type-emission correctness refactor. The
direct-IL backend, coroutine surface, generics, and forward interop were already
complete in 0.9.0; this release closes the remaining A (language) / B (stdlib) gaps
so the A/B checklists in `docs/remaining-tasks.md` have **zero** open items.

### Added
- **Regex `matches` / `find`** — full-input match + `MatchResult?` (via `DotKt.Text.Regexes`
  shims), `MatchResult.value` → `Match.Value`. (`il-regex`)
- **`return` as an expression** — `val x = if (c) a else return b` (new `returnExpr`
  lowering, `tryStack`-aware). (`il-langtail`)
- **enum per-entry bodies** — `enum class Op { PLUS { override fun apply(…)=… }; abstract
  fun apply(…) }`: the base enum becomes abstract and each body entry is emitted as a
  subclass `<>Enum_NAME : Enum`. (`il-enumbody`)
- **Field-level visibility** — a property's visibility is honored on its backing field:
  `private` → true `FieldAttributes.Private`, `internal` → `Assembly`, `protected` →
  `FamORAssem`. (`il-fieldvis`)

### Changed
- **Inner / nested classes are now emitted as true CLR nested types** (`Outer+Inner`)
  instead of being flattened to separate top-level types. Nested types retain Kotlin's
  legal access to the enclosing type's `private` members, which is what makes true
  `private` field visibility correct. `inner` classes still capture `__outer`.

### Fixed
- **Compound-condition smart-cast** — `if (x is Int && x > 10)` no longer mis-takes the
  then-branch (the `>` operand stayed boxed as `Any`); `bin` now coerces a boxed operand
  to the other operand's primitive type, and `IrGetValue` honors a narrowed smart-cast.

### Notes
- Verified working & locked by samples this release: `lateinit` (uninitialized read
  throws), `field` in custom accessors, `when`+type smart-cast.
- Full IL suite green + JVM differential ALL MATCH + ilverify-clean.
- Known residue (unchanged, tracked in `docs/remaining-tasks.md` §F / §R): packaged-SDK
  end-to-end consumption still has MSBuild SDK-resolution plumbing to finish (F-308);
  reverse-interop cosmetic naming/`[Nullable]` is gated behind R-1.

## 0.9.0

Initial pre-1.0 line: direct-IL backend (C# codegen retired), CLR-native coroutines
(`suspend` ⇔ `Task<T>` / `IAsyncEnumerable`), user generics, forward .NET interop
(import-driven, façade-free), and the 3-package distribution (Sdk / Toolchain / Runtime
+ Templates).

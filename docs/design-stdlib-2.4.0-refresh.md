# DotKt stdlib SOURCE refresh v2.2.0 → v2.4.0 (#111, part 2)

> Status (2026-07-12): SCOPING (Fable, verified vs upstream v2.2.0=build-2.2.0-292=631e9fdbe59c and
> v2.4.0=add726ca8c82). Runs AFTER the compiler-bump green checkpoint. The Fable-design is captured here; execution
> is mechanical (~1 day). New-actual work is near-ZERO (3 trivial one-liners), contrary to the initial fear.

## Delta + two corrections
- Upstream delta over `libraries/stdlib/{common/src, src/kotlin, unsigned/src}`: **103 files, +7290/−1111**, but ~80%
  inert (docs, 443 `@IgnorableReturnValue` applications, Duration parser rewrite +897, Uuid refactor +484, new
  pure-common fns).
- **Correction 1 — our tree is NOT pure 2.2.0-vintage.** Already carries 2.4 backports: the Atomics
  `update/fetchAndUpdate/updateAndFetch` expect family + CLR actuals (`clr/kotlin/concurrent/atomics/AtomicsClr.kt`,
  `AtomicArraysClr.kt`), `wrapAsDeserializationException` (+ actual `clr/kotlin/internal/serializationUtilClr.kt:14`),
  `ExperimentalContextParameters.kt`, `ReturnValue.kt` annotation classes, and 11 files already byte-identical to v2.4.0.
- **Correction 2 — local edits are SEMANTIC, not comments.** ~24 files carry CLR rewrites woven into common source that
  MUST survive: the #76 unsigned-array `Collection`-supertype removal (`unsigned/src/kotlin/U*Array.kt` +
  `common/src/generated/_UArrays.kt` appended block), `Strings.kt` trim/pad/isBlank rewrites (CharSequence-cast +
  char-default-arg ilemit workarounds), `Comparisons.kt` comparator-object rewrite, `_Collections.kt` `sortedWith`
  toTypedArray fast-path removal, the `COROUTINE_SUSPENDED` boxed-once cache (`coroutines/intrinsics/Intrinsics.kt`), and
  a **locally-added expect** `clrRenderTupleElement` (`src/kotlin/util/Tuples.kt`, Pair/Triple toString routing).

## Mechanics: per-file 3-way merge (NOT clean re-vendor)
Clean re-vendor+patch is ruled out (the local patch is semantic, spread over ~24 files, includes DELETIONS a naive
v2.4.0 copy would resurrect, and a local expect whose loss orphans a clr actual). Use `git merge-file` with base =
v2.2.0 blob (both parent blobs available from `upstream/.git`). Per file in union(upstream-changed, we-changed) ≈130:
- ours == v2.4.0 → skip (11 files).
- ours == v2.2.0 (we didn't touch) → copy `git --git-dir=upstream/.git show v2.4.0:F` verbatim (~62 files, the bulk).
- upstream unchanged 2.2→2.4 → keep ours (the #76 supertype removal, SUSPENDED cache survive for free).
- both changed → 3-way `git merge-file` — exactly **18 files**: `_UArrays.kt`, `_Collections.kt`, `MathH.kt`,
  `src/kotlin/Collections.kt`, `collections/{Collections,Maps,Sets}.kt`, `comparisons/Comparisons.kt`,
  `concurrent/atomics/AtomicArrays.common.kt`, `contracts/Effect.kt`, `random/XorWowRandom.kt`, `text/Strings.kt`,
  `util/{Standard,Tuples}.kt`, `unsigned/src/kotlin/{UByte,UShort,UInt,ULong}.kt`.
Realistic conflicts: **5–10 files** (Strings.kt, _UArrays.kt, Comparisons.kt, MathH.kt, the 4 U*.kt), each resolvable by
"take upstream text, re-apply our local block" — every local edit carries a `// NOTE (CLR)` / `// #76` marker; use those
as the resolution guide. Sources are auto-globbed (`stdlib.ktproj`/`build-stdlib-klib.sh`) → new files need no wiring.

## New-expect → new-actual (the "real work" — it is TINY: 3 trivial one-liners, all in `clr/kotlin/uuid/UuidClr.kt`)
| New expect (v2.4.0) | CLR actual | Difficulty |
|---|---|---|
| `internal expect fun secureRandomBytes(destination: ByteArray)` (uuid/Uuid.kt) | `kotlin.random.Random.Default.nextBytes(destination)` (port the crypto-RNG caveat comment from UuidClr.kt:8-12 — BCL RandomNumberGenerator.Fill doesn't bind to signed ByteArray) | trivial |
| `internal expect fun uuidParseHexOrNull(hexString): Uuid?` | 1-line delegate to `uuidParseHexOrNullCommonImpl` | trivial |
| `internal expect fun uuidParseHexDashOrNull(hexDashString): Uuid?` | 1-line delegate to `uuidParseHexDashOrNullCommonImpl` | trivial |
| `expect annotation class JsSymbol/JsNoRuntime/ObjCEnum` | NONE — `@OptionalExpectation` | none |
| Atomics `update*`×18, `wrapAsDeserializationException` | already have expect+actual | done |
Zero BCL-binding, zero `@ClrIntrinsic`/`@ClrTypeAlias`. The 18 "new StringBuilder-looking expect lines" in the raw diff
are annotation-only rewraps of pre-existing expects.

## Removed/changed decls (build-break risks)
- `internal expect fun secureRandomUuid(): Uuid` REMOVED (becomes plain common calling secureRandomBytes) → our actual
  `UuidClr.kt:13` orphans → **delete it proactively** in the same change (the file's `@file:Suppress("ACTUAL_WITHOUT_EXPECT")`
  may mask it rather than error — don't rely on the gate).
- `internal expect annotation class ActualizeByJvmBuiltinProvider` removed → OptionalExpectation, 0 clr refs → non-event.
- All common helpers our clr actuals call verified present at v2.4.0 (`{get,set}LongAtCommonImpl`, `formatBytesIntoCommonImpl`,
  `uuidFromRandomBytes`, `uuidParseHex{,Dash}CommonImpl` (keep 1-arg forms), `systemClockNow`). No other clr-referenced
  decl removed/re-signatured.
- `clrRenderTupleElement` (our local expect in Tuples.kt) MUST survive the Tuples merge (upstream change there is 2 annotation
  lines; verify post-merge both the expect + toString bodies survived).

## Annotation deltas: inert (one caveat)
kotc passes EVERY annotation to BIR verbatim (`BirEmitterDeclarations.kt:480-505`, no-filter policy). `IgnorableReturnValue`/
`MustUseReturnValue` already exist as annotation classes in `src/kotlin/annotations/ReturnValue.kt` → the 443 new applications
emit like existing `@SinceKotlin` → inert for bir2cir/ilemit. CAVEAT: our tree has ZERO applications today, so the first
ref-build after the merge is the proving run; if ilemit chokes on attribute volume, the fix is bir2cir/ilemit-side, NOT a kotc
filter. `@MustUseReturnValue`/`@SubclassOptInRequired` have 0 applications — nothing to assess.

## Construct novelty: `val _ = expr` unnamed locals (11 uses / 7 files)
`Atomics.common.kt`×4, `Sequences.kt`×2, `_Collections.kt`/`ReversedViews.kt`/`Base64.kt`/`XorWowRandom.kt`/`TimeSources.kt`.
kotc→bir2cir has never seen this. **Add a tiny `cases/` test first** — the only construct-level unknown.

## New files
- `ExperimentalObjCEnum.kt` — MUST vendor (a plain `annotation class` referenced by the new `@ObjCEnum` expect in
  NativeAnnotations.kt; else the merged file won't compile).
- `VersionOverloads.kt` — plain annotation classes, 0 applications; vendor for parity, no compiler support needed.

## Phased plan (~1 day mechanical, AFTER the compiler-bump green checkpoint)
1. Spot-check `val _` (one case through kotc→bir2cir→ilemit→run) — the only construct unknown, do first.
2. Merge (§1): copy/merge loop, vendor 2 new files, resolve 5–10 conflicts via the marker comments.
3. Actuals (§2/§3): in UuidClr.kt — delete secureRandomUuid, add secureRandomBytes + 2 parse-OrNull delegates.
4. `make stdlib-klib` (surfaces NO_ACTUAL_FOR_EXPECT / merge damage) → `make stdlib-ref` (proves the 443 @IgnorableReturnValue
   + Duration/Uuid rewrites through bir2cir) → `make stdlib-rt && make verify`.
5. Post-merge audit: grep every `// NOTE (CLR)`/`#76` block survived (list in §Correction-2).

Fable-leverage (done here): the 3-way classification + conflict guide, the secureRandomUuid→secureRandomBytes migration,
the clrRenderTupleElement preservation, the `val _` risk call. Mechanical (Opus-safe): the copy/merge loop, conflict
resolution against markers, the 3 one-line actuals, build/gate.

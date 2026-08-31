# Kotlin frontend bump playbook (version-independent)

> How to bump the pinned Kotlin frontend (kotc reuses the stock Kotlin Configuration→FIR→Fir2Ir; the backend is
> ours). **The 2.4.0 bump was this playbook's TestFlight** — the first real exercise that validated these steps;
> its detailed execution record remains in Git history. For the next bump (e.g. 2.5.0), re-run the 7 steps
> below — the steps are version-independent; only the delta is version-specific.

## The 7 steps

### 0. Preflight — the go/no-go (hours)
Confirm the target `kotlin-compiler-embeddable-<X>.jar` exists on Maven Central AND **bundles the cli-metadata
module** (`unzip -l | grep 'org/jetbrains/kotlin/cli/metadata'`). If it doesn't bundle it, the metadata-klib pipeline
needs an extra artifact / vendoring — the plan changes. (2.4.0: bundled, 38 classes — preflight passed.)

### 1. Scope against the upstream tag — NOT from memory (the highest-leverage step)
Point `upstream/` at the target release tag (`git --git-dir=upstream/.git`; note `vX` and `build-X-*` can co-locate on
one commit). **Read the real vX source** (a read-only `Plan`/`Explore` fan-out is the cheap way) and produce: (a) the
**CERTAIN-BREAKS** list — every internal/unstable API kotc uses that renamed/reshaped, with file:line; (b) the
**behavioral watch-list** — changes that *compile* but could silently miscompile. This front-loads the reasoning; the
rest is mostly mechanical. A source-verified delta beats speculation — 2.4.0 had ~9 certain breaks in ~5 pipeline
files, all found this way.

### 2. Bump the dependencies (minutes)
- `toolchain/kotc/build.gradle.kts` (kotlin plugin + `kotlin-compiler-embeddable`).
- Any test fixture or packaged metadata that embeds the Kotlin compiler version.
- `upstream/` checkout at the tag; the doc/pin references (step 7).

### 3. Compile-fix inside-out (the bulk — mechanical grind, ~1-2 days)
Fix the CERTAIN-BREAKS in dependency order: `Main.kt` (removed args) → `ClrCliPipeline.kt` (pipeline artifacts/phases)
→ frontend phases (artifact ctors, `getCompilerExtensions`, klib loading) → `ClrDefaultImports.kt` (renames) →
reference-KLIB loading →
`BirEmitter*` residue (expected small — it sits on the stable IR tree). Each break is a compiler error pointing at it.

### 4. The metadata-klib serializer — the ONE recurring gating risk
The const-value serializer is where a bump gets *stuck*, not just delayed. Kotlin keeps evolving how const values bake
into the metadata klib (2.4.0 deleted `constValueProvider`/the IR fallback). **Prefer adopting upstream's own
serializer** (2.4.0: `fir/pipeline/Fir2KlibMetadataSerializer.kt` — the HMPP/actualization-aware one kotc hand-rolled)
over hand-patching. Then **EMPIRICALLY verify const baking**: rebuild the klib, compile an app using `Int.MIN_VALUE`,
`Double.POSITIVE_INFINITY` (the historical failures), run it. If they vanish → a real design problem: root-cause it
holistically, do not patch the symptom. Do NOT trust the gate to find this indirectly — check the klib directly.

### 5. Fragile watch-points — verify empirically, never assume "unchanged"
kotc pokes several **internal/unstable FIR surfaces**; a bump can silently break any of them:
- **CLR intrinsic declarations** (`libraries/stdlib/clr/kotlin/clr/CompilerIntrinsics.kt`) — verify that the frontend
  KLIB continues to expose the fixed `byref` / `stackBuffer` / `clrEvent` vocabulary without a compiler plugin.
  CLR reference declarations and their direct static members are loaded from reference KLIBs.
- **fake-override linking** (`resolveFakeOverride`, default-accessor discrimination) — the classic wrong-dispatch
  miscompile source when Fir2Ir internals get rewritten.
- **default-import synthesis** — 2.4.0 made `FirDefaultImportsProviderHolder` composable, so `register` *composed*
  instead of *overwriting*; the `kotlin.jvm.*` install silently no-op'd. Fixed by forcing the flat-overwrite overload.
- **language tightening breaks TEST SAMPLES, not the compiler** — 2.4.0 made a statically-false `is` check an error
  (`5 is Collection<*>`); fix the *sample* (use an `Any` operand), never special-case the compiler (cardinal rule).
- **@-annotation materialization** — 2.4.0 stopped materializing `@JvmInline` in non-JVM sessions; kotc had to carry
  the value-class fact via `mods.value` + bir2cir a `[KotlinValueAttribute]` roundtrip marker. New bumps may drop
  other `expect`/`OptionalExpectation` annotations kotc relied on — re-check what the IR still carries.

### 6. Gate to the PRE-BUMP baseline = behavior-preserving
The compiler half is done when `make verify` returns to the pre-bump result with no lost or disabled tests.
A new compile, runtime, schema, or ILVerify regression is a real frontend behavioral change — root-cause it with
a focused source reproducer and, when useful, a side-by-side deterministic BIR diff. Every regression that
"symptom-moves" is escalation-rule territory. The 2.4.0 bump hit exactly 3
distinct root causes behind the ~22 reds (an identity-cast `unbox.any`, a double-`nullableValue`, the language `is`
tightening) — bisect, don't lump.

### 7. Stdlib-source refresh + doc/pin sweep (the second half)
A *full* bump = compiler + the matching stdlib SOURCE. Replace `libraries/stdlib/common` verbatim from upstream vX;
do not 3-way-merge CLR adaptations into that subtree. Regenerate
`tests/stdlib-common-upstream/upstream-vX.sha256`, update its recorded upstream tag/commit and the gate's
`EXPECTED_VERSION`, then run `make verify-stdlib-upstream`. Refresh `libraries/stdlib/{src,unsigned/src}` by per-file
3-way merge (base = the old tag's blob), preserving genuine Kotlin-semantic changes. CLR physical names, markers, and
platform bodies live in `libraries/stdlib/clr/common` and `libraries/stdlib/clr/stdlib-bindings.json` instead.

After the frontend BIR build, re-resolve every sidecar entry from the target declaration's `declarationId`, source
name, complete parameter/return signature, and implementation identity. The overlay fails closed on stale IDs, but
the opaque hashes still need deliberate regeneration from `build/clr-stdlib/bir/*.bir.json`; never guess an ID or
match only by name. Enumerate genuinely new `expect`s by signature and add only their required CLR actuals. Then do
the P6 pin/doc sweep. Do the doc sweep only after green (writing "2.X" while the gate is red is premature).

## Reasoning-vs-grind split (plan the budget this way)
- **Reasoning-heavy (front-load; high-leverage):** step 1 (the verified delta + watch-list), step 4 (the
  const-serializer choice + coverage), step 5/6 (behavioral-regression root-cause), step 7 (the 3-way classification +
  new-actual enumeration). Do these FIRST and carefully; they decide how mechanical the rest is.
- **Mechanical grind (parallelizable across worktree-isolated specialists):** step 3 (the rename/reshape fix loop),
  step 7's merge + actuals, the gate-and-fix cycles, the doc sweep.

## Effort
2.4.0 (the TestFlight): compiler half ~3-5 days (mostly grind + the one const-serializer decision); stdlib refresh ~1
day mechanical. A subsequent bump over a similar delta should be comparable or faster, since the playbook + the fragile
watch-points are now known and the serializer pattern is established.

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

### 1. Scope with Fable against the upstream tag — NOT from memory (the highest-leverage step)
Point `upstream/` at the target release tag (`git --git-dir=upstream/.git`; note `vX` and `build-X-*` can co-locate on
one commit). Have **Fable read the real vX source** and produce: (a) the **CERTAIN-BREAKS** list — every internal/
unstable API kotc uses that renamed/reshaped, with file:line; (b) the **behavioral watch-list** — changes that
*compile* but could silently miscompile. This front-loads the reasoning; the rest is mostly mechanical. Fable's
verified delta beats speculation — 2.4.0 had ~9 certain breaks in ~5 pipeline files, all found this way.

### 2. Bump the dependencies (minutes)
- `toolchain/kotc/build.gradle.kts` (kotlin plugin + `kotlin-compiler-embeddable`).
- Any test fixture or packaged metadata that embeds the Kotlin compiler version.
- `upstream/` checkout at the tag; the doc/pin references (step 7).

### 3. Compile-fix inside-out (the bulk — mechanical Opus grind, ~1-2 days)
Fix the CERTAIN-BREAKS in dependency order: `Main.kt` (removed args) → `ClrCliPipeline.kt` (pipeline artifacts/phases)
→ frontend phases (artifact ctors, `getCompilerExtensions`, klib loading) → `ClrDefaultImports.kt` (renames) →
`ClrMetadataKlibPipeline.kt` (**stop here — step 4 first**) → `ClrTypeInjection.kt` (plugin/registrar DSL) →
`BirEmitter*` residue (expected small — it sits on the stable IR tree). Each break is a compiler error pointing at it.

### 4. The metadata-klib serializer — the ONE recurring gating risk (Fable)
The const-value serializer is where a bump gets *stuck*, not just delayed. Kotlin keeps evolving how const values bake
into the metadata klib (2.4.0 deleted `constValueProvider`/the IR fallback). **Prefer adopting upstream's own
serializer** (2.4.0: `fir/pipeline/Fir2KlibMetadataSerializer.kt` — the HMPP/actualization-aware one kotc hand-rolled)
over hand-patching. Then **EMPIRICALLY verify const baking**: rebuild the klib, compile an app using `Int.MIN_VALUE`,
`Double.POSITIVE_INFINITY` (the historical failures), run it. If they vanish → a real design problem (Fable). Do NOT
trust the gate to find this indirectly — check the klib directly.

### 5. Fragile watch-points — verify empirically, never assume "unchanged"
kotc pokes several **internal/unstable FIR surfaces**; a bump can silently break any of them:
- **`FirInternals.java` companion shim** (`ownerGenerator` / `replaceCompanionObjectSymbol` / `FirGeneratedScopes`
  early-return) — the implicit-companion mechanism (§8c). 2.4.0: survived UNCHANGED; the green `il-injstatic` sample
  is what *proved* it (a behavior-preserving gate is exactly how you check a fragile internal dependency didn't break).
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
A *full* bump = compiler + the matching stdlib SOURCE. Refresh `libraries/stdlib/{common/src,src,unsigned/src}` from
upstream vX by **per-file 3-way merge** (base = the OLD tag's blob), preserving our local CLR-semantic edits (guided by
their `// NOTE (CLR)` / `// #76` markers) + the `clr/` actuals; write CLR actuals only for the **genuinely-new
`expect`s** (2.4.0: just 3 trivial one-liners — the "new actuals" fear is usually overblown; enumerate by signature).
Then the P6 sweep: `CLAUDE.md` / `README.md` / the docs' "pinned to X" lines. Do the doc sweep only AFTER green (writing
"2.X" while the gate is red is premature).

## Fable-vs-Opus split (plan the budget this way)
- **Fable (front-load; high-leverage):** step 1 (the verified delta + watch-list), step 4 (the const-serializer choice
  + coverage), step 5/6 (behavioral-regression root-cause), step 7 (the 3-way classification + new-actual enumeration).
- **Opus (mechanical grind):** step 3 (the rename/reshape fix loop), step 7's merge + actuals, the gate-and-fix cycles,
  the doc sweep. These need no Fable and can run after Fable is unavailable.

## Effort
2.4.0 (the TestFlight): compiler half ~3-5 days (mostly grind + the one const-serializer decision); stdlib refresh ~1
day mechanical. A subsequent bump over a similar delta should be comparable or faster, since the playbook + the fragile
watch-points are now known and the `FirInternals`/serializer patterns are established.

# CLAUDE.md

> kotlin/clr — a compiler that runs **Kotlin on .NET (CLR)**. Reuses the stock Kotlin 2.2.0
> frontend (Configuration → FIR → Fir2Ir) and replaces only the backend:
> **Kotlin IR → BirEmitter → BIR(json) → bir2cir → CIR(json) → ilemit → CIL**.
> Full overview, layout table, and design notes live in **`README.md`** — read it first.
>
> **Current ship scope + confirmed architecture: [`docs/ship-tasks.md`](docs/ship-tasks.md)** — the
> single source of truth for what to work on now (8 goals) and the confirmed layer architecture.
> Its **§0 invariants are binding: an implementation that violates them is a bug.**

# Ground Rules

- **Think in English; write the final report to the user in Japanese.** Internal reasoning, code,
  comments, identifiers, commit messages, **and subagent (Agent tool) prompts** stay in English —
  they are instructions to agents, not the user-facing report; only the user-facing answer (the final
  report) is Japanese.
- **The answer is usually already written down — this repo over-documents.** Before changing
  anything non-trivial, use the **Task → doc map** below and read the matching doc. Do not
  re-derive what a design doc already settled.
- **But docs and scripts go stale.** A `docs/` file explains *rationale*; it may lag the code. For
  *current commands, paths, and canonical workflow*, **this file and the actual scripts win** — when
  a doc disagrees with them, trust this file, verify against the code, and flag the stale doc rather
  than following it. (E.g. the canonical stdlib build is the three `build-stdlib-*.sh` scripts
  below, regardless of what older docs/scripts imply.)
- **Durable rules live in *this file*, not in auto-memory.** CLAUDE.md is loaded in full every
  session and is authoritative. Auto-memory is a *recall-gated side-store*: its file bodies are not
  in context unless the harness happens to surface them, and even then they're flagged as
  background that may be stale. So if something must *always* be obeyed, it belongs **here** — when
  the user says "remember this, it's important" and it's a rule, add it to CLAUDE.md, not just memory.
- **Decide and implement — do NOT bounce A/B questions back.** Once the goal is clear, pick the
  option the project's own rules + docs already imply and carry it to completion. Never pause
  mid-task to ask the user to choose between options the architecture has already settled — that
  reads as not having looked. Asking is for genuinely-open design forks, not for questions a rule
  in this file or MEMORY already answers.
  - **Layer placement is a lookup, never a question:** any fix that reads .NET/CLR metadata (a ref
    dll, `@Clr`/`@ClrIntrinsic` labels, BCL shapes) belongs in **bir2cir** (the Kotlin↔CLR layer),
    **never** in kotc/`BirEmitter` — `compiler-layer-responsibilities` decides it. So "wire it into
    BirEmitter or move it to bir2cir?" is not a real fork: it's bir2cir.
- This project is **unpublished**: prefer the cleanest design over backward compatibility. Break freely.
- **NO compat shims. NO dual-track. Delete the legacy path in the SAME change (2026-06-30, binding, user-directed).**
  Maintaining two systems behind a `compat` flag — e.g. `--compat-bir` kept alongside `--native-cir` — is **the
  root cause** of the blurred layer boundaries: as long as the old path survives, CLR knowledge keeps lingering in
  kotc/ilemit instead of being forced into bir2cir. So, from this moment: **never keep old code behind a
  `compat`/legacy flag or a "both paths" switch.** When a layer is being moved to the 4-layer architecture
  (facadegen / kotc / bir2cir / ilemit), **delete the legacy code as part of that change** — do not preserve it "just
  in case". The clean 4-layer is the ONLY path; the `--compat-bir`/`--native-cir` output-selection flags (and the
  CompatBir verbatim-copy mode / the native-CIR envelope) were **removed** (2026-06-30), leaving a single unflagged
  bir2cir type-lowering path. Always choose a clean rebuild over an incremental compat shim, even when the rebuild is larger.
- **Clean as you go — the "same change" rule extends to COMMENTS and DOCS, not just code (2026-07-08, user-directed).**
  When you relocate/delete logic, in the SAME change delete the comments, dead helpers, and stale doc lines it
  leaves behind — otherwise debt-repayment mass-produces incorrect comments/docs/dead-code (a whole cleanup pass had to
  reconcile them). And a stale **false** claim must be **DELETED or replaced with the current truth, NEVER annotated**
  ("this used to be true but…") — an annotation still carries the false claim. State what the code does NOW.
- **Prefer dedicated subagents for tasks, and actively use Codex.** Delegate substantive work to dedicated
  (specialized) subagents rather than doing it inline — the coordinator orchestrates and integrates. Use **Codex**
  for design and investigation, and **instruct every subagent to USE Codex** (not merely note it's "available") —
  both the coordinator and subagents should consult it. (User-directed.) **The canonical invocation is**
  `codex exec -s read-only --skip-git-repo-check "<question in English>" </dev/null` — the **`</dev/null` is
  MANDATORY in this harness**: even with a prompt argument, codex reads stdin to EOF ("stdin is appended as a
  `<stdin>` block"), and the harness keeps stdin open → an un-redirected call hangs forever (the repeated
  "Codex stalled on stdin" incidents). Also beware: the CLI can block on an interactive self-update prompt on the
  user's terminal — if Codex goes silent across agents, ask the user to check it, and fall back to empirical
  verification meanwhile.

# Build & test (do NOT guess commands)

The build is a multi-stage native pipeline, not a single `gradle build`. Use these:

| Goal | Command |
|------|---------|
| **Run the IL test gate** (compile → IL → run → assert → `ilverify`) | `./scripts/verify-il.sh` |
| MSBuild / `.ktproj` end-to-end | `./scripts/verify-ktproj.sh` |
| Kotlin↔CLR round-trip (consume a DotKt dll as Kotlin) | `./scripts/verify-roundtrip.sh` |
| **One-shot: compile + run a single `.kt`** | `./scripts/dotkt.sh --run path/to/Foo.kt` |

`verify-il.sh` is the **canonical gate** — a change is not "done" without a run. The truthful fail-set
baseline is **machine-readable, not prose**: the `XFAIL_RUN` / `XFAIL_ILVERIFY` maps at the top of
`scripts/verify-il.sh` (one reason per name). The gate **exits 0 iff every actual fail is XFAIL-listed**
and prints a `NEW-FAIL` / `FIXED` diff against that baseline either way; an XFAIL entry that starts
passing prints "FIXED — remove it from the xfail list" **without** reddening the gate (prune the entry in
the same change). So never copy fail counts/names into docs — run the gate and read its diff. (Each
sample writes an atomic result record under `build/verify-il/`; the old stdout race that dropped FAIL
lines is dead.) `verify-roundtrip.sh` follows the same discipline (`RT_XFAIL`: the suspend sections are
expected-fail until the coroutine lowering lands — it is the coroutine bundle's E2E gate), and so does
`verify-differential.sh` (`XFAIL_DIFF`). `dotkt.sh` is
the fast dev wrapper over the same pipeline (`-h` for options: `--exe`, `--no-stdlib`, `--retarget`,
`--ref <dll>`).

**Building the CLR stdlib** — the real pure-Kotlin stdlib under `libraries/stdlib/`. **These THREE
scripts are the current, canonical build** (other stdlib scripts are STALE — see the warning):

- `./scripts/build-stdlib-ref.sh --emit` — the **reference** assembly (`DotKt.Private.Stdlib.dll`;
  compile-time only, keeps `@Clr` metadata, substituted away at app-emit).
- `./scripts/build-stdlib-rt.sh --emit` — the shipping **runtime** assembly (`DotKt.Stdlib.dll`).
- `./scripts/build-stdlib-jar.sh` — the **frontend jar** (`kotlin-stdlib-clr-frontend.jar`)
  that replaces `kotlin-stdlib.jar` as kotc's `-classpath` input, killing the `java.util.*` typealias
  leak. It generates all 8 `.kotlin_builtins` **from our own sources** via `-Xoutput-builtins-metadata`
  (the old `jar uf` injection of a JVM kotlin-stdlib's `.kotlin_builtins` is GONE — it dragged JVM
  semantics into the frontend). The `kotlin.coroutines` package-fragment marker
  `libraries/stdlib/clr/builtins/Coroutines.kt` is what keeps that flag from crashing ("builtins must
  span ALL builtin pkgs"). Backs up nothing — it `rm -rf`s its output dir, so back up the working jar first.
- `--emit` makes the first two actually run `ilemit` (without it: frontend + BIR only, for fast triage).
- Why the split: the **ref/runtime split** — `docs/design-clr-stdlib-ref-runtime-split.md`,
  MEMORY `clr-stdlib-ref-runtime-split`; the frontend jar — MEMORY `frontend-stdlib-jar-plan`.

> ✅ **REMOVED (2026-07-02):** the stale `scripts/build-dotkt-stdlib.sh` and `scripts/build-stdlib.sh` (they built the
> OLD stdlib and referenced the now-deleted `DotKt.Runtime`) are DELETED. The canonical stdlib build is the three
> `build-stdlib-*.sh` scripts above; there is no longer a dangerous "rm the cached dll" footgun to avoid.

Toolchain: JDK is auto-provisioned by Gradle; **.NET SDK 10 required**. Kotlin/IR APIs are
**pinned to 2.2.0** (internal/unstable — intentionally not tracking newer versions).

# Toolchain responsibility (respect the layer boundaries)

The pipeline is split so each stage owns exactly one concern. **Put new logic in the layer that owns
it** — do not smear CLR knowledge into the frontend or Kotlin knowledge into the emitter.

The **authoritative** layer table — including the reference artifact each stage reads
(facadegen ← CLR dll, kotc ← stdlib.jar, bir2cir ← stdlib.ref.dll, ilemit ← stdlib.rt.dll) and the
**`@ClrIntrinsic` invariant** (sourced from ref.dll, consumed by bir2cir, **never passed to ilemit**)
— is **`docs/ship-tasks.md` §0**. The summary below must not drift from it.

> ### Who references the .NET Reference Assemblies (the binding layer is bir2cir)
> - **facadegen** reads them → to GENERATE Kotlin FIR-injection metadata (.NET metadata → Kotlin).
> - **bir2cir** reads them → to RESOLVE cross-assembly types/references — **this is where .NET binding belongs.**
> - **ilemit** ideally does NOT (it emits IL from CIR; it historically referenced them only because bir2cir+ilemit
>   were once one process — that merge is why some .NET-resolution is still scattered upward).
> - **runtime** references them → for execution.
>
> So **kotc must be .NET-AGNOSTIC**: a facadegen-injected library is, to kotc, just "a weird Kotlin library with
> PascalCase packages." kotc emits a plain `callStatic`/`callInstance` by the FQN identity and does NOT decide the
> .NET call shape; **bir2cir** resolves the owner FQN against the Reference Assemblies and binds it
> (`clrStatic`/`clrInstance`/`clrPropGet`…) — the SAME "emit the identity, bind in bir2cir" pattern as stdlib, one
> axis over. kotc's current clr*-shape emission for facadegen-injected owners is a DEVIATION to restore (task #61 /
> MEMORY `a2-restore-bir2cir-net-binding`), not the design.

> ### BINDING INVARIANT — `kotlin.*` comes from the JAR, never from facadegen
> kotc resolves the **entire stdlib (`kotlin.*`)** from the frontend **jar** (`-classpath`), which
> preserves full Kotlin semantics. facadegen handles the **.NET space ONLY** (`System.*` *and any
> referenced .NET assembly* — not just System). **NEVER feed the stdlib assembly to
> `facadegen --scan-asm`.** Use ONE source (the jar) for two reasons: (1) a facadegen scan of the stdlib
> would *duplicate* the jar's `kotlin.*` and the two **conflict** (this session: a non-reified `arrayOf`
> from facadegen collided with the jar's reified `arrayOf` → `overload resolution ambiguity`); (2) it is
> **slower** — facadegen re-generating the whole stdlib every build vs reading the prebuilt jar. The fix
> for any "stdlib symbol missing/ambiguous in an app build"
> is **the jar**, never a facadegen scan or a `kotlin.*` guard inside facadegen (that's treating the
> symptom — the root error is passing stdlib.dll to facadegen at all). Removed from the production
> path `packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets` + `scripts/dotkt.sh` (commit
> `522bdc8`) **and from `scripts/verify-il.sh` + `scripts/verify-differential.sh` (DONE — no stdlib
> `--scan-asm` remains; kotlin.* is on the frontend jar `-classpath`).**

| Module | Owns | Must NOT contain |
|--------|------|------------------|
| `toolchain/kotc/` | the **Kotlin frontend** (PSI/FIR/IR → BIR) | CLR/BCL knowledge |
| `toolchain/bir2cir/` | the **Kotlin ↔ CLR relation** (lowering BIR → CIR) | — |
| `toolchain/ilemit/` | **CLR codegen** (CIR-json → CIL via Reflection.Emit) | Kotlin-language knowledge |
| `toolchain/facadegen/` | .NET metadata → FIR-injection metadata (façade-free `import System.X`) | |
| `toolchain/retarget/` | repoint emitted BCL refs so C# can `<Reference>` the dll | |

The bulk of that migration is DONE (2026-07-03: kotc reads neither CLR annotation; Math/Char/String/Console/
NUMBER_PARSE/exception hardcodes retired; bir2cir `MemberCallSubstitution` is the sole substituter). Residual
CLR-specific lowering in `BirEmitter` (genuine primitive IL ops + the deferred delegate/closure + coroutine
families) follows the same rule: when you touch it, move it toward the boundary above, don't entrench it.
(MEMORY `compiler-layer-responsibilities`; status in `docs/master-task-inventory.md` 【1】; the old 6-wave
taxonomy is archived at `docs/archive/bir2cir-migration-inventory.md`.)

> ### kotc reads NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias` — the substitution is bir2cir's (2026-06-30, user, foundational)
> `BirEmitter.clrName()` (it reads `@ClrIntrinsic` to do member call-substitution) is **legacy that must be
> REMOVED**: kotc must not read either CLR-binding annotation. It emits pure Kotlin — a plain `kotlin.String.length`
> member call, the bare `kotlin.String` owner — and nothing more. **bir2cir reads the ref.dll** and treats a class
> carrying `@ClrTypeAlias` as a **CLR-bound owner**, and its members carrying `@ClrIntrinsic` (and rule-3 bodies) as the
> substitution targets — rewriting `kotlin.String.length` → `System.String.get_Length`, etc. **This bir2cir
> reference-metadata substitution is the CORE of the 4-layer migration and is MANDATORY — not a follow-up.** (A
> stdlib member binding does nothing until bir2cir consumes it from the ref.dll.)

> ### BIR type tokens are pure Kotlin FQN identities — kotc emits NO CLR-resolution marker (2026-06-30, user-confirmed)
> The BIR's `@Name` (this-assembly-emitted → ilemit `_types`), `clr:Name` (a referenced .NET type),
> `clrg:Name[args]` (a referenced .NET *generic* type), and the primitive **shorthand** (`int`/`long`/
> `bool`/`char`/`void`/`object`/`string`/…) prefixes ALL encode a **CLR-resolution decision** — *where*
> a type lives (local vs referenced) and *what kind* it is (primitive / generic / value). That is CLR
> knowledge, so it must NOT be produced by kotc. **kotc emits ONLY the type's FQN identity** — `kotlin.Int`,
> `kotlin.collections.List`, `System.Exception` — and nothing else. **bir2cir / ilemit DERIVE the
> resolution** from that FQN: substitute a stdlib type to its CLR form (gated — see below), resolve a
> referenced .NET type, select the primitive IL opcode, construct the generic, look up an in-assembly
> emitted type. The whole `@`/`clr:`/`clrg:`/shorthand vocabulary lives **below** the kotc boundary.
> **Primitive substitution is mode-gated and owned by bir2cir:** in the **reference** build
> (`DOTKT_STDLIB_COMPILE=1`, no `SUBSTITUTE`) a primitive STAYS `kotlin.Int` (the ref is pure-Kotlin
> metadata; its method bodies are meant to be squashed to `throw NotImplementedException`, so a bare-value
> `kotlin.Int` never reaches arithmetic/box IL); in **every other** build (rt, app — anything non-ref)
> `kotlin.Int` lowers to the CLR primitive. The CompatBir/`--native-cir` dual-track is **removed** (2026-06-30):
> bir2cir owns this on a single path, env-gated by `refBuild`. Both formerly-coupled pieces have **landed** (2026-07):
> (a) kotc emits `kotlin.*` FQN identities (freeze m1 TYPE flip, `#37`) — not the CLR shorthand; and (b) the ref-build
> **body-squash IS implemented** (`RefBodySquash`, bir2cir `Program.cs`, gated on `refBuild`): every ref-stdlib
> declaration body becomes a single `throw NotImplementedException()`, so a bare-value `kotlin.Int` never reaches
> arithmetic/box IL, AND a ref method that leaks into runtime fails loud (the sentinel). bir2cir's primitive lowering
> is therefore now **fully active**, not a no-op. This is what makes the ref.dll a pure **annotation surface**:
> every stdlib member carries its `@Clr*` binding metadata with a throw-only body, and bir2cir reads that metadata by
> CLR reflection to transform BIR→CIR — the mechanism `#52` (kotc-purity) exists to finally use for ALL recognition.

# The cardinal rule: do NOT special-case the compiler

There is now a real CLR stdlib (`libraries/stdlib/`). The whole point of compiling it is to **retire**
the compiler's hand-written stdlib lowerings — so:

- **NEVER** add compiler special-casing (denylist / type-map / `ilemit` stub) to force a stdlib
  function to work. The fix is **always stdlib-side**: emit the real type, or add an `actual`/stub in
  `libraries/stdlib/clr/`. (MEMORY `stdlib-compile-retires-lowerings-never-adds`.)
- **Prefer `@ClrIntrinsic` bindings over compiler lowerings.** Bind named BCL methods
  (`String.format` → `System.String.Format`) as `@ClrIntrinsic` metadata in the stdlib. Only genuine
  primitive IL ops stay compiler-lowered. (MEMORY `intrinsic-over-compiler-lowering`,
  `four-layer-purpose-retire-intrinsics`.)
- **Source analysis uses a real parser/lexer (Kotlin PSI), never regex/heuristics.** (MEMORY
  `prefer-parser-over-regex`.)

If a stdlib function "needs" a compiler hack to work, that is a signal the stdlib binding is wrong —
fix the binding, not the compiler.

# Task → doc map (read BEFORE you act, not after)

"Read the docs" is too vague to act on, so here are the concrete triggers. Before you start the task
on the left, open the doc on the right:

| If you are about to… | Read first |
|----------------------|-----------|
| **pick up work / know what's left** | **`docs/master-task-inventory.md`** (THE de-duplicated remaining-work ledger; bundles 1-5 CLOSED 2026-07-03) — `docs/ship-tasks.md` §0 stays the binding architecture reference |
| change the backend pipeline (BIR/CIR/IL, layer boundaries) | `docs/design-fir-bir-cir-il.md` + MEMORY `compiler-layer-responsibilities` |
| touch stdlib bindings / `@Clr*` / lowerings | `docs/clr-stdlib-intrinsic-audit.md`, `docs/design-clr-stdlib-ref-runtime-split.md` |
| retire / migrate an intrinsic | `docs/master-task-inventory.md` 【1】 (the old 6-wave plan is `docs/archive/bir2cir-migration-inventory.md`) |
| ask "how does Kotlin map to the CLR, or why does it differ?" | `docs/dotkt-semantics.md` (canonical) |
| check what is left for 1.0 | `docs/remaining-tasks.md` |
| **record a new behavioral difference** from Kotlin/JVM | write it **into** `docs/dotkt-semantics.md` (not a code comment) |
| log a fix | add it under `## Unreleased` in `CHANGELOG.md` |

For everything else, **`README.md`** has the layout table, quick-start, and "what works today".
**MEMORY** holds dated decisions and `KNOWN BUG` warnings — its index is auto-loaded, but treat
entries as background that may be stale: if one names a file/flag/number, verify it still exists
before relying on it.

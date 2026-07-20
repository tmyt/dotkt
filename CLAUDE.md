# CLAUDE.md

> kotlin/clr — a compiler that runs **Kotlin on .NET (CLR)**. Reuses the stock Kotlin 2.4.0
> frontend (Configuration → FIR → Fir2Ir) and replaces only the backend:
> **Kotlin IR → BirEmitter → BIR(json) → bir2cir → CIR(json) → ilemit → CIL**.
> Full overview, layout table, and design notes: **`README.md`**.
>
> Authority order: (1) the user's request → (2) this file's rules → (3) the actual code + canonical
> scripts for current behavior → (4) **`docs/ship-tasks.md` §0** for architecture (**its invariants
> are binding: an implementation that violates them is a bug**) → (5) **`docs/master-task-inventory.md`**
> for what remains to do → (6) other docs for rationale (they may lag the code — verify, and flag
> stale docs rather than following them).

# Ground Rules

## Language & turn discipline
- **Think in English; write the user-facing report in Japanese.** Reasoning, code, comments,
  identifiers, commit messages, and subagent (Agent tool) prompts stay English — they are
  instructions to agents; only the final report to the user is Japanese.
- **Tool turns: work FIRST, report LAST — never announce-then-stop (2026-07-17, supersedes the
  2026-07-14 HARD no-prose rule).** A prose-only message ENDS the turn, so a message that says
  "I will now do X" and stops means X never happens. Run the tool calls first; the user-facing
  report (Japanese) is the FINAL, tool-free message of the turn — never a preamble, never a
  promise of pending work.
  - **On Opus 4.8 only, keep tool-bearing turns 100% prose-free:** on that model, prose sharing
    a turn with a tool call intermittently CORRUPTS the call (stray tokens, dropped `antml:`
    prefix → malformed, wasted turns). Incident record: MEMORY
    `respond-in-english-when-tool-calling`.
  - **On Fable 5 (current default) and other models:** the corruption has not been reproduced;
    a one-line pre-tool status note is allowed when it helps. If call corruption is EVER observed
    on a model, treat that model as Opus-4.8-class: go fully prose-free there and record the
    model name here.

## Use what's already written
- **The answer is usually already written down — this repo over-documents.** Before any non-trivial
  change, use the **Task → doc map** at the bottom and read the matching doc. Do not re-derive what
  a design doc already settled.
- **Durable rules live in THIS file, not auto-memory.** CLAUDE.md loads in full every session and is
  authoritative; memory is a recall-gated side-store that may not surface. When the user says
  "remember this, it's important" about a rule, add it HERE, not just to memory.
- **Decide and implement — do NOT bounce A/B questions back.** Once the goal is clear, pick the
  option the project's rules + docs already imply and carry it to completion. Asking is for
  genuinely-open design forks only — never for questions a rule here or in MEMORY already answers.
  - **Layer placement is a lookup, never a question:** a fix that reads .NET/CLR metadata (a ref
    dll, `@Clr*` labels, BCL shapes) belongs in **bir2cir**, never in kotc/`BirEmitter`; Kotlin
    semantics never go in ilemit. "Which layer?" is not a real fork — see **Layer boundaries** below.

## Design doctrine
- **The acceptance test for behavior choices is "consistent, documented, convincingly explainable" — JVM is a reader reference, not a compat target (2026-07-18/19, user-directed).**
  Resolution order: ① where the Kotlin spec/KDoc contract fixes behavior, honor it by default (frame it
  "Kotlin contract"); ② where Kotlin leaves it unspecified, take the CLR-native form (frame it
  "deliberate CLR choice (reason)"); ③ where CLR/interop consistency convincingly outweighs the KDoc
  letter, deviate even from the contract (frame it "interop-first deviation" — exemplar:
  `"ß".uppercase()` stays `"ß"`, not the KDoc/Unicode `"SS"`, because one-to-one case mapping is the
  general mscorlib behavior). Every deviation must pass all three test conditions and be recorded in
  `docs/dotkt-semantics.md`. NEVER hand-force a JVM value/behavior and NEVER cite "matches JVM" as a
  correctness claim — "the JVM does it" passes none of the three conditions.
- **Unpublished project: cleanest design over backward compatibility. Break freely.**
- **NO compat shims. NO dual-track. Delete the legacy path in the SAME change (2026-06-30, binding, user-directed).**
  Keeping an old path alive behind a `compat`/legacy flag is the proven root cause of blurred layer
  boundaries (the removed `--compat-bir`/`--native-cir` lesson). When moving logic to the 4-layer
  architecture, delete the legacy code in that same change; always choose a clean rebuild over an
  incremental shim, even when the rebuild is larger.
- **Clean as you go — the same-change rule covers COMMENTS and DOCS too (2026-07-08, user-directed).**
  Relocating/deleting logic deletes its comments, dead helpers, and stale doc lines in the SAME
  change. A stale FALSE claim is DELETED or replaced with the current truth — NEVER annotated
  ("this used to be true but…"); state what the code does NOW.
- **Author for AGENT cognition — one pass/concern per file, each small enough to read WHOLE (2026-07-11, user-directed).**
  This codebase is ~100% agent-implemented; an agent editing a monolith works with partial vision
  (grep + a read window) and misses cross-cutting invariants — the trap that regrew
  bir2cir/Program.cs 5740→7007 lines (unwound in #41). When a pass or family grows, give it its OWN
  file in the SAME change, following the established per-file patterns (bir2cir per-pass `*.cs`,
  ilemit `Emitter.*.cs` partial-class parts, kotc `BirEmitter*.kt` extension files);
  verify-by-refactor (output byte-identical). A driver/entry file stays; an outgrown concern does not.

## Working the repo (git, agents, gates)
- **NEVER run a destructive git op on a file carrying uncommitted work you did not verify is disposable (2026-07-11, user-directed, after a real loss).**
  `git checkout/restore <file>`, `git stash`, `git reset --hard` silently destroy working-tree
  changes — and this tree usually holds several agents' uncommitted edits at once. To undo ONE hunk,
  use `Edit` to write just that hunk back — never a whole-file checkout. If you truly must
  checkout/restore: first capture the complete state you are about to touch (staged + unstaged +
  untracked; e.g. `git diff` AND `git status --porcelain` for the untracked list), and re-check
  `git status` before and after.
- **Sub-agents MUST NEVER touch the `main` working tree — parallel work goes in an ISOLATED WORKTREE, no exceptions (2026-07-13, user-directed after repeated data-loss).**
  Any parallel file-mutating agent (or any concurrent agent that COMPILES — builds contend on the
  tree's shared `build/`) gets `isolation: "worktree"` and this non-negotiable block prepended to its
  brief, verbatim intent: *"You run in your OWN isolated git worktree on your OWN branch. NEVER
  touch/edit/build/gate the main working tree; NEVER `git checkout/switch main`; NEVER `cd` out of
  your worktree. Do ALL work (edits, installDist, dotnet build, gates, commits) INSIDE your worktree
  ONLY. Commit to YOUR branch; the COORDINATOR integrates into main — you MUST NOT. If any step seems
  to require touching main, STOP and report."* **Only the coordinator integrates**: merge branches
  into main ONE at a time, resolve conflicts with `Edit` (never whole-file checkout), then run ONE
  integrated gate. Cut worktree branches from a HEAD that already contains any in-flight main-direct
  work. Serialize (one mutating agent owns main+gate) only when truly unsplittable; **default =
  worktree-isolated parallelism.** (MEMORY `parallel-agents-isolate-or-serialize`.)
- **Prefer dedicated subagents for tasks, and actively use Codex (user-directed).** The coordinator
  orchestrates and integrates; substantive work goes to specialist subagents. Use Codex for design
  and investigation, and **instruct every subagent to USE it** (not merely note it's available).
  Canonical invocation:
  `codex exec -s read-only --skip-git-repo-check "<question in English>" </dev/null`
  — the **`</dev/null` is MANDATORY**: the harness keeps stdin open and codex reads it to EOF, so an
  un-redirected call hangs forever. If Codex goes silent across agents it may be blocked on an
  interactive self-update prompt on the user's terminal — ask the user to check, and fall back to
  empirical verification meanwhile.
- **When ≥3 specialist round-trips fail to resolve ONE problem, escalate to a holistic Fable+Opus root-cause pass (2026-07-12, user-directed).**
  The tell is whack-a-mole: the same fault class keeps moving (a new IL offset, a new pass/store
  site) instead of closing — the failure mode of per-layer one-symptom-at-a-time fixing. STOP
  dispatching per-layer specialists and mount a **Fable** read-only cross-layer design pass that
  (a) enumerates the COMPLETE manifestation set, (b) rules root-vs-band-aid, (c) weighs a
  design-level fix that dissolves the whole family, (d) specs ONE unified fix — then **Opus**
  (coordinator + specialists) implements and gates it once. Run the Fable pass in parallel with any
  in-flight specialist attempt (read-only → no build collision). **3 is a ceiling, not a quota** —
  escalate the moment the pattern is clear. This is a STRUCTURAL escalation (holistic vs per-layer),
  not a model upgrade. (Origin: the #75 covariance-erasure ilverify loop.)

# Build & test (do NOT guess commands)

The build is a multi-stage native pipeline, not a single `gradle build`:

| Goal | Command |
|------|---------|
| **Run the IL test gate** (compile → IL → run → assert → `ilverify`) | `./scripts/verify-il.sh` |
| MSBuild / `.ktproj` end-to-end | `./scripts/verify-ktproj.sh` |
| Kotlin↔CLR round-trip (consume a DotKt dll as Kotlin) | `./scripts/verify-roundtrip.sh` |
| **One-shot: compile + run a single `.kt`** | `./scripts/dotkt.sh --run path/to/Foo.kt` |

`verify-il.sh` is the **canonical gate** — a behavior-affecting change (compiler, stdlib, scripts,
packaging) is not "done" without a run. The truthful fail-set baseline is **machine-readable, not
prose**: the `XFAIL_RUN` / `XFAIL_ILVERIFY` maps at the top of the script (one reason per name). The
gate **exits 0 iff every actual fail is XFAIL-listed** and prints a `NEW-FAIL` / `FIXED` diff either
way; an XFAIL entry that starts passing prints "FIXED — remove it" (prune it in the same change).
Never copy fail counts/names into docs — run the gate and read its diff. Same discipline: `RT_XFAIL`
in `verify-roundtrip.sh`, `XFAIL_DIFF` in `verify-differential.sh`. `dotkt.sh` is the fast dev
wrapper over the same pipeline (`-h` for `--exe`, `--no-stdlib`, `--retarget`, `--ref <dll>`).

**Building the CLR stdlib** (`libraries/stdlib/`) — `make stdlib` runs the THREE canonical scripts
in order; other stdlib scripts are stale or experimental:

- `./scripts/build-stdlib-klib.sh` — the **frontend KLIB** (`kotlin-stdlib-clr-frontend.klib`),
  kotc's `-classpath` input. Built by kotc's own `DOTKT_BUILD_KLIB=1` metadata pipeline over the
  actualized stdlib sources, so it carries real `@ClrTypeAlias`/`@ClrIntrinsic` metadata and
  compiled const values. (Superseded the retired JVM frontend jar — #67/#80.)
- `./scripts/build-stdlib-ref.sh --emit` — the **reference** assembly (`DotKt.Private.Stdlib.dll`;
  compile-time only, carries `@Clr*` metadata, substituted away at app-emit).
- `./scripts/build-stdlib-rt.sh --emit` — the shipping **runtime** assembly (`DotKt.Stdlib.dll`).
- `--emit` makes ref/rt actually run `ilemit` (without it: frontend + BIR only, for fast triage).
  Why the ref/runtime split: `docs/design-clr-stdlib-ref-runtime-split.md`.
- ⚠️ `scripts/build-stdlib.sh` (a #66 shared-BIR experiment) exists but is NOT canonical and is not
  in the Makefile; consolidating onto it is an open follow-up.

Toolchain: JDK auto-provisioned by Gradle; **.NET SDK 10 required**. Kotlin/IR APIs **pinned to
2.4.0** (internal/unstable; bump procedure: `docs/kotlin-frontend-bump-playbook.md`).

# Layer boundaries (put logic in the layer that owns it)

The authoritative layer table — including the reference artifact each stage reads (facadegen ← CLR
dll, kotc ← stdlib.klib, bir2cir ← stdlib.ref.dll, ilemit ← stdlib.rt.dll) — is
**`docs/ship-tasks.md` §0**. This summary must not drift from it.

| Module | Owns | Must NOT contain |
|--------|------|------------------|
| `toolchain/kotc/` | the **Kotlin frontend** (PSI/FIR/IR → BIR) | CLR/BCL knowledge |
| `toolchain/bir2cir/` | the **Kotlin ↔ CLR relation** (lowering BIR → CIR) | — |
| `toolchain/ilemit/` | **CLR codegen** (CIR-json → CIL via Reflection.Emit) | Kotlin-language knowledge |
| `toolchain/facadegen/` | .NET metadata → FIR-injection metadata (façade-free `import System.X`) | |
| `toolchain/retarget/` | repoint emitted BCL refs so C# can `<Reference>` the dll | |

**The binding layer is bir2cir.** The invariants (2026-06-30, user, foundational — all realized;
when you find residual code violating them, fixing it is in scope, not optional):

- **kotc INTERPRETS no CLR metadata.** It reads neither `@ClrIntrinsic` nor `@ClrTypeAlias`
  (realized by #52 kotc-purity: kotc recognizes zero operators and reads no `@Clr*`; it only carries
  annotations as opaque metadata when serializing the klib). kotc emits pure Kotlin — a plain
  `callStatic`/`callInstance` by FQN identity, the bare `kotlin.String` owner — and does NOT decide
  the .NET call shape. To kotc, a facadegen-injected library is just "a weird Kotlin library with
  PascalCase packages". (Realized for .NET interop by bir2cir `NetInteropBinding` — A2/#61: kotc
  emits the plain call, bir2cir reflects the owner against the reference assemblies and binds
  `clrStatic`/`clrInstance`/`clrPropGet`/…. Exception by design: CLR-only vocabulary with no
  plain-Kotlin form — `.NET events`, `byref`/`ClrRef<T>` — is lowered directly by kotc as
  facadegen-injected CLR vocab.)
- **BIR type tokens are pure Kotlin FQN identities.** The `@Name` / `clr:Name` / `clrg:Name[args]` /
  primitive-shorthand vocabulary encodes CLR-resolution decisions (local vs referenced, primitive vs
  generic) and lives **below** the kotc boundary: kotc emits only `kotlin.Int`,
  `kotlin.collections.List`, `System.Exception`; bir2cir/ilemit derive the resolution from the FQN.
- **bir2cir reads the ref.dll** and treats a `@ClrTypeAlias` class as a CLR-bound owner and its
  `@ClrIntrinsic` members (and rule-3 bodies) as substitution targets — rewriting
  `kotlin.String.length` → `System.String.get_Length`, etc. (`MemberCallSubstitution`). This
  reference-metadata substitution is the CORE of the 4-layer design; it is what makes the ref.dll a
  pure **annotation surface**. `@ClrIntrinsic` is consumed HERE as a "what to substitute" label and
  emitted as a plain BCL call — **ilemit never interprets it as binding semantics** (in the
  ref-stdlib build the label rides through as an ordinary CIR attribute, nothing more).
- **Primitive substitution is mode-gated, owned by bir2cir:** the **ref** build
  (`--build-stdlib=metadata`) keeps `kotlin.Int` un-lowered — its bodies are squashed to
  `throw NotImplementedException()` (`RefBodySquash`), so a bare `kotlin.Int` never reaches
  arithmetic IL and a ref method leaking into runtime fails loud. **Every other build** (rt, app)
  lowers `kotlin.Int` → the CLR primitive. Single unflagged path (the compat dual-track is removed).
- **ilemit knows no Kotlin** and ideally does not read the Reference Assemblies — residual .NET
  resolution above bir2cir is historical debt: when you touch it, move it toward the boundary, don't
  entrench it. (Status ledger: `docs/master-task-inventory.md` 【1】.)

> ### BINDING INVARIANT — `kotlin.*` comes from the KLIB, never from facadegen
> kotc resolves the **entire stdlib (`kotlin.*`)** from the frontend **KLIB** (`-classpath`), which
> preserves full Kotlin semantics. facadegen **generates the .NET space ONLY** (`System.*` and any
> referenced .NET assembly) and must **NEVER generate/inject `kotlin.*` facades** — it cannot
> restore Kotlin semantics (inline/reified/operator…), and a facadegen copy of `kotlin.*` conflicts
> with the klib's (seen live: non-reified vs reified `arrayOf` → `overload resolution ambiguity`)
> besides being slower than the prebuilt klib. The fix for any "stdlib symbol missing/ambiguous" is
> **the klib** — never a facadegen scan of the stdlib or a `kotlin.*` guard inside facadegen
 (symptom-patching; the root error is asking facadegen for stdlib symbols at all).
>
> ### BINDING INVARIANT — facadegen must never SURFACE the stdlib (resolver-scope OK, surface-set banned) (2026-07-21, user)
> **facadegen is the process that PROJECTS a FOREIGN CLR assembly into the Kotlin dialect** (so
> `import System.X` / a C# `<ProjectReference>` works). `kotlin.*` **IS** the Kotlin dialect, supplied by
> the **KLIB** — so facadegen must **never generate/surface a `kotlin.*` type from the stdlib**;
> `--import-list` keeps generation .NET-only. But the stdlib legitimately (and for the DotKt-library
> **roundtrip** lane, NECESSARILY) sits in facadegen's `--compile-refs` **resolver**: it is needed to
> materialize a consumed DotKt lib's `[kotlin.clr.*]` round-trip attributes (`ManagedReferenceCatalog`
> aliases the runtime twin to the reference twin for exactly this — `verify-roundtrip.sh` depends on it).
> So the correct invariant is **resolver-scope OK, SURFACE-set banned** — enforced by separating the
> loadable set from the surfaceable set (facadegen's `Resolve`/`TypesInNamespace`/`ResolveTopLevelFacade`/
> `GetAwaiterExtIndex`/`HasArityClash` iterate the resolver universe indiscriminately; the fix routes the
> *surfacing* sites through a stdlib-excluded index — the ~30-line "Option B" separation).
> ⚠️ The earlier "handing facadegen the stdlib degrades a plain-C# `List<T>` facade" claim was a
> **PHANTOM** — it does NOT reproduce (tests/interop is green 21/21); the reverse-`@ClrTypeAlias` restore
> is gated on `IsDotKtEmittedAssembly`, which a plain-C# producer isn't. (MEMORY
> `facadegen-never-gets-stdlib-in-compile-refs`.)

# The cardinal rule: do NOT special-case the compiler

There is a real CLR stdlib (`libraries/stdlib/`). The point of compiling it is to **retire** the
compiler's hand-written stdlib lowerings — so:

- **NEVER** add compiler special-casing (denylist / type-map / `ilemit` stub) to force a stdlib
  function to work. The fix is **always stdlib-side**: emit the real type, or add an `actual`/stub
  in `libraries/stdlib/clr/`. (MEMORY `stdlib-compile-retires-lowerings-never-adds`.)
- **Prefer `@ClrIntrinsic` bindings over compiler lowerings.** Bind named BCL methods
  (`String.format` → `System.String.Format`) as stdlib metadata; only genuine primitive IL ops stay
  compiler-lowered. (MEMORY `intrinsic-over-compiler-lowering`.)
- **Source analysis uses a real parser/lexer (Kotlin PSI), never regex/heuristics.** (MEMORY
  `prefer-parser-over-regex`.)

If a stdlib function "needs" a compiler hack to work, the stdlib binding is wrong — fix the binding,
not the compiler.

# Task → doc map (read BEFORE you act, not after)

| If you are about to… | Read first |
|----------------------|-----------|
| **pick up work / know what's left** | **`docs/master-task-inventory.md`** (THE remaining-work ledger); `docs/ship-tasks.md` §0 stays the binding architecture reference |
| change the backend pipeline (BIR/CIR/IL, layer boundaries) | `docs/design-fir-bir-cir-il.md` + MEMORY `compiler-layer-responsibilities` |
| touch stdlib bindings / `@Clr*` / lowerings | `docs/clr-stdlib-intrinsic-audit.md`, `docs/design-clr-stdlib-ref-runtime-split.md` |
| retire / migrate an intrinsic | `docs/master-task-inventory.md` 【1】 (archived 6-wave plan: `docs/archive/bir2cir-migration-inventory.md`) |
| ask "how does Kotlin map to the CLR, or why does it differ?" | `docs/dotkt-semantics.md` (canonical) |
| check what is left for 1.0 | `docs/remaining-tasks.md` |
| **record a new behavioral difference** from Kotlin/JVM | write it **into** `docs/dotkt-semantics.md` (not a code comment) |
| log a fix | add it under `## Unreleased` in `CHANGELOG.md` |

For everything else, **`README.md`** has the layout table, quick-start, and "what works today".
**MEMORY** holds dated decisions and process gotchas — its index auto-loads, but treat entries as
background that may be stale: if one names a file/flag/number, verify it still exists before relying
on it. Do not copy volatile completion status into this file — verify names, flags, and task status
against the current tree.

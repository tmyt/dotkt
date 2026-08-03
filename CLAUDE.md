# CLAUDE.md

kotlin/clr — a compiler that runs **Kotlin on .NET (CLR)**. It reuses the stock Kotlin 2.4.0 frontend
(Configuration → FIR → Fir2Ir) and replaces only the backend:

    Kotlin IR → BirEmitter → BIR(json) → bir2cir → CIR(json) → ilemit → CIL

`README.md` has the layout table and quick start. `docs/architecture.md` is the binding architecture
reference — its invariants are not advisory; an implementation that violates one is a bug. The GitHub issue
tracker is the only record of what remains to do. Other docs explain rationale and may lag the code; when one
names a file, flag or number, check it still exists and flag the drift rather than following it.

When these disagree, the order is: what you asked for, then this file, then the code and canonical scripts
for what actually happens today, then `docs/architecture.md`, then the tracker, then everything else.

## Principles

The layers exist so that no layer has to guess. These are contracts, not preferences — a change that
breaks one is wrong even when the tests pass, and "it works" is not a defence.

They still sit below your instructions. If you tell me to break one for a specific change — most often the
source-compatibility principle, when a dotkt-side source break is worth it — that is the decision, and I do
it without relitigating; I will say what it breaks, not argue the principle back at you. What is out of
bounds is *me* deciding a principle doesn't apply this time.

- **kotc projects Kotlin IR into BIR in Kotlin vocabulary.** It carries Kotlin identities and frontend facts
  across. It decides no CLR member, owner, or call shape.
- **bir2cir fixes the physical CLR representation of Kotlin meaning.** Every "what does this Kotlin thing
  become on the CLR" decision is made here, and only here.
- **ilemit emits CIR one-to-one as CIL and re-infers nothing** — not an overload, not a stdlib ABI, not a
  member kind. If ilemit cannot resolve something, an earlier layer dropped it: fix the drop rather than
  adding a resolver. ilemit is a projection, not a second compiler.
- **A reference assembly may lose bodies, never declarations.** Signatures, generic constraints and Kotlin
  metadata all survive body-stripping. A ref dll missing one of those is broken, not merely reduced.
- **The common layer of stdlib, coroutines and atomicfu tracks upstream.** Adapt the CLR platform actuals,
  not the shared sources — divergence there is debt that compounds at every upstream bump.
- **Kotlin source compatibility is what we owe; internal compatibility is not.** As long as Kotlin source
  keeps compiling and meaning the same thing, any internal shape — ABI, metadata, BIR/CIR vocabulary, pass
  structure — is fair to break outright.
- **Solve for arbitrary Kotlin source, not for the library in front of you.** A fix keyed to a particular
  library, type or function name is a symptom patch; the rule has to hold for source nobody has written yet.

## Where the project is

Getting the compiler from nothing to mostly-working is done. This is the last stretch — closing the gap
between "mostly works" and "shippable" — and it changes what good work looks like here.

Earlier the bottleneck was coverage, so the right instinct was to move fast, decide alone, and start the
next thing. It isn't any more. The bar for a change is now "would I ship this", not "does it move forward":
depth over breadth, one thing finished properly over three started, and a green gate is the floor for a
change rather than the finish line. Where speed and rigor pull against each other, rigor wins.

This is about how carefully work is verified, not about becoming conservative with the design. Breaking an
internal shape for a cleaner one is still the right instinct — that is the source-compatibility principle
above, and it does not soften here. What must not happen is a regression: something that works today quietly
stopping.

## Working agreement

These are about who does what, not about how to write code.

- Reports to me are in Japanese. Everything else — code, comments, identifiers, commit messages, subagent
  prompts — is English.
- Do the work, then report. A message that only says what you are about to do ends the turn without doing it.
- **The main working tree is mine.** Don't edit, build, gate or run probes there. Work in a worktree cut from
  `origin/main`, and give every file-mutating or compiling subagent its own.
- **I do the merging.** Not `git merge` into main, not `gh pr merge`. Your deliverable is an opened PR, one
  per scope, and the turn ends there.
- **The issue tracker is mine.** Don't create, close or re-milestone issues unless I ask.
- Before a destructive git operation, capture the full state (`git diff` and `git status --porcelain`) and
  confirm what you are about to lose. To undo one hunk, edit it back.

## Scope

An issue names a user-visible behavior; that behavior is the scope, not the file the bug happens to live in
or the example in the issue body. If another path breaks the same behavior, it belongs in the same fix — a
tracker with one issue per code site is useless. Conversely, don't widen into a different subject.

"It was already broken", "that's a different layer", "that needs another mechanism" are not reasons to defer.
If something genuinely blocks you, stop and name the blocker rather than shipping around it.

When the frontend has resolved a program, a valid CIL lowering exists. A backend abort on accepted IR is a
bug; the only legitimate throw is an assert that cannot fire on valid IR.

## Design stance

- The bar for a behavior choice is that it is consistent, documented, and convincingly explainable. The
  JVM is a reader reference, not a compat target — "matches JVM" is not an argument. Honor the Kotlin
  spec/KDoc contract; where Kotlin leaves something unspecified, take the CLR-native form; deviate from the
  contract only where interop consistency clearly outweighs it. Record deviations in
  `docs/dotkt-semantics.md`.
- Break internal shapes freely, per the source-compatibility principle above: a redesign that costs more now
  beats a shape you would regret, and nothing has shipped as 1.0.0 to hold you back. No compat shims and no
  dual-track paths — when you replace something, delete what it replaced in the same change rather than
  keeping it behind a flag. Dual-track is what blurred the layer boundaries before.
- Relocating or deleting logic takes its comments and doc lines with it. State what the code does now; don't
  annotate a stale claim as formerly-true.
- This codebase is written almost entirely by agents working through grep and a read window, so a file that
  can't be read whole tends to lose its invariants. When a concern outgrows its file, split it out in the
  same change and verify the output is byte-identical.

## How I like work done

- Check the premise before acting on it, and prefer a question to a wrong autonomous change. Open the file
  before citing a doc; read a closed issue's body before filing something related; re-read your own rewrite
  before building on it. Asking used to be the expensive option here; it isn't now.
- Finish one thing before starting the next. A few concurrent streams that each get verified beat many that
  each get a glance — and gates that run at the same time can produce false failures, so a result you did
  not watch is not a result.
- Substantive work goes to a subagent in its own worktree. Brief it with the issue, the worktree and the
  acceptance test — a plain restatement of the issue's symptom has to become true — and say nothing about
  which files it may touch. The layer table tells you where code *belongs*; it is not a permission boundary,
  and a root cause does not pick its layer to suit whoever is fixing it.
- Use Codex for design and investigation, and tell subagents to use it too:
  `codex exec -s read-only --skip-git-repo-check "<question>" </dev/null`. The `</dev/null` is required or it
  hangs. If it goes silent it may be stuck on an interactive update prompt — ask me.
- Before reporting done or treating a PR as ready, complete the independent local review contract in `AGENTS.md`:
  separate fresh Claude and Codex processes, both read-only and given the exact diff or commit range. Give
  them the task, applicable invariants, and the honest remainder — known limitations, open questions and weak
  points — but none of the implementation conversation or reasoning that would turn an independent read into
  a confirmation pass. Validate both review results before handoff. For this review contract, `AGENTS.md` is
  authoritative if this summary ever differs from it.
- If the same fault keeps reappearing somewhere new instead of closing, stop fixing symptoms per layer and do
  one read-only pass that enumerates every manifestation and specs a single fix.

## Build and test

Not a single `gradle build` — a multi-stage native pipeline. Don't guess these.

| Goal | Command |
|------|---------|
| Canonical gate (a behavior-affecting change isn't done without it) | `make verify` |
| Compiler behavior only (NUnit + `ilverify`) | `make verify-tests` |
| Compile and run one file | `./scripts/dotkt.sh --run path/to/Foo.kt` |
| The CLR stdlib: frontend KLIB, then reference dll, then runtime dll | `make stdlib` |

Validation cadence is part of the review contract in `AGENTS.md`: iterate with the narrowest focused check,
review the stable focused-green diff, then run the canonical full gate once. If that gate fails, iterate on its
failing stage; if the fix changes the reviewed artifact, repeat focused validation and independent review before
rerunning the whole gate. A draft PR may expose honest work in progress, but it must state which reviews or
checks remain and cannot be treated as ready for handoff.

The truthful fail-sets are machine-readable, never prose: `ILVERIFY_XFAIL` in `tests/run-ilverify.sh`,
`XFAIL_PKG` in `tests/packaged-sdk/run.sh`. A gate exits 0 iff every actual failure is listed, and reports what
is new or newly fixed. Read that diff; don't copy counts into docs.

Two traps worth knowing: the toolchain binaries are only checked for existence, so after changing
bir2cir/ilemit/dll2klib you need `rm -rf build/*-bin` (plus `build/clr-stdlib*` for stdlib-affecting work)
before a gate result means anything; and after any kotc change, `./gradlew :kotc:installDist`, or a stale
launcher fails the gate for the wrong reason.

Toolchain: JDK auto-provisioned by Gradle, .NET SDK 10 required, Kotlin/IR APIs pinned to 2.4.0 (bump
procedure in `docs/kotlin-frontend-bump-playbook.md`).

## Layers

The three principles above fix kotc, bir2cir and ilemit. `dll2klib` projects each resolved .NET reference assembly
into a standard metadata-only KLIB. `docs/architecture.md` owns the full table, including which reference artifact
each stage reads; raw ilemit output is already consumable by ordinary CLR tooling.

So "which layer?" is a lookup, not a design question — a fix that consults a ref dll, `@Clr*` labels or BCL
shapes goes in bir2cir. Residual .NET resolution still living in ilemit is a principle violation carrying
interest: move it toward the boundary when you touch it, never entrench it.

Two invariants that are easy to violate by accident:

- `kotlin.*` comes from the dedicated frontend KLIB on kotc's `-classpath`. dll2klib ignores the CLR
  stdlib twins, so a projected copy cannot collide with that authoritative Kotlin surface.
- Every resolved non-stdlib reference assembly is projected independently and completely. Source imports do
  not select the projected type set, and reference KLIBs do not embed assembly dependency graphs.

## The cardinal rule

There is a real CLR stdlib in `libraries/stdlib/`, and the point of compiling it is to retire the compiler's
hand-written lowerings. So a stdlib function that doesn't work is fixed stdlib-side — emit the real type, or
add an `actual` in `libraries/stdlib/clr/` — never with a denylist, type-map or ilemit stub. Prefer binding a
named BCL method as `@ClrIntrinsic` metadata over a compiler lowering; only genuine primitive IL ops stay
lowered. If a stdlib function seems to need a compiler hack, the binding is wrong.

Source analysis uses the Kotlin PSI, not regex.

## Where to read first

| Before you… | Read |
|---|---|
| pick up work | the GitHub issue tracker |
| change the backend pipeline or a layer boundary | `docs/architecture.md` |
| ask how Kotlin maps to the CLR, or why it differs | `docs/dotkt-semantics.md` |
| record a new behavioral difference | write it into `docs/dotkt-semantics.md` |
| log a fix | `CHANGELOG.md`, under `## Unreleased` |

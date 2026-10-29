# Project Principles

The compiler pipeline must preserve strict ownership of meaning. Each layer must
operate only on the facts assigned to it and must not infer semantics owned by
another layer.

- `kotc` must project Kotlin IR into BIR using Kotlin vocabulary and semantics.
  It must not decide the CLR representation of those semantics.
- `bir2cir` must resolve Kotlin semantics into their concrete CLR physical
  representation.
- `ilemit` must emit CIR to CIL one-to-one. It must not re-resolve overloads,
  reconstruct Kotlin semantics, or infer the standard-library ABI.
- Stripping method bodies from reference assemblies must not strip declaration
  signatures, generic constraints, or Kotlin metadata.
- The common source layers of the standard library, coroutines, and atomicfu
  must remain aligned with their upstream projects.
- Internal compatibility may be broken deliberately when doing so enables a
  correct design without breaking Kotlin source compatibility.
- Compiler-produced BIR, CIR, metadata carriers, and DLLs are internal ABI.
  Do not add compatibility fallbacks for artifacts produced by older compiler
  versions, and do not reconstruct missing facts from legacy names, physical
  layout, or method bodies. Artifacts produced by an older compiler are
  unsupported; arbitrary compiler or linker failure is an acceptable breaking
  change, and no dedicated compatibility diagnostic is required.
- Fixes must express general rules that produce valid CLR binaries from
  arbitrary Kotlin source. Do not introduce local special cases tied to a
  particular library or function name.

# User-Program Undefined Behavior

Project-specific undefined behavior is limited to the cases listed in this
section. Do not infer new undefined behavior from an implementation limitation,
an unsupported feature, or a compiler bug. Except for these cases, Kotlin
source retains Kotlin semantics together with the documented CLR interop and
platform deviations.

- The binding annotations that define the compiler-provided stdlib's CLR
  representation are trusted stdlib/compiler inputs. User-authored use of
  `ClrTypeAlias`, `ClrIntrinsic`, `ClrProperty`, `ClrConv`,
  `ClrRefArgument`, `ClrCollectionFactory`, or
  `ClrArrayFactory` is undefined behavior. User-authored use of the compiler
  metadata carriers `ClrExternal`, `ClrAwaitBridge`,
  `ClrFlagsOperation`, `KotlinDeclarationIdentity`, and `KotlinDefault` is also
  undefined behavior.
  This does not apply to ordinary use of stdlib declarations carrying those
  annotations, nor to the supported user-facing `ClrName` and `ClrField`
  annotations.
- `ClrRef<T>` is compiler vocabulary for a CLR managed reference. Its supported
  user-program forms are passing `byref(x)` directly to a projected CLR
  `ref`/`out` parameter, declaring it as a parameter of a non-suspend Kotlin
  function and accessing that parameter through `.value`, and using
  `var x by byref(refReturningCall())` as the documented live-reference
  delegate. Such a Kotlin parameter is emitted as a real CLR `ref` parameter;
  capturing or storing it is undefined behavior. User-authored return types,
  properties, fields, stored values, or other ordinary uses of `ClrRef<T>`
  remain undefined behavior.
- `StackBuffer<T>` is compiler vocabulary scoped to the literal block of
  `stackBuffer`. The block parameter may be used only through the supported
  stack-buffer operations in that block. It must not be returned, stored,
  captured, passed as an ordinary value, or otherwise escape. A `Span<T>`
  derived from it may be consumed inside the block but must not escape the
  block's dynamic extent. Violating these lifetime rules is undefined
  behavior.
- In the current implementation, user-authored `ClrEvent<T>` usage is defined
  only for an event property implemented with `by clrEvent()`. Subscription is
  supported on such a property and on a compiler-projected CLR event; raising is
  supported only on a Kotlin-implemented event that has the synthesized raise
  accessor. Subclassing `ClrEvent<T>`, implementing custom add/remove behavior,
  or materializing the event handle as an ordinary parameter, return value,
  local, field, stored value, or captured value is currently undefined
  behavior. Future versions may define additional `ClrEvent<T>` forms.

These mechanisms are intentionally not gated on a stdlib or user build mode.
Undefined forms may therefore happen to compile and work. Do not add validation,
diagnostics, build-mode branches, compatibility fallbacks, or special cases
solely to accept or reject them. Do not preserve or deliberately break their
accidental behavior, and do not add fixtures that turn it into a compatibility
requirement. Tests may exercise the supported forms and the compiler-provided
stdlib's use of these mechanisms, but must not assert a particular compiler,
linker, generated-binary, or runtime result for the undefined forms.

# Pull Request Workflow

- Begin each issue in a dedicated Git worktree rather than changing the primary
  working tree.
- Open a draft pull request once the focused tests for the changed behavior are
  green. State every outstanding review and validation step in its description.
- Run the independent Claude review read-only with network access outside the
  sandbox. Do not impose a short API timeout or force a model override; allow
  20--30 minutes for a long review, and follow the review budget below.
- Mark the draft pull request ready for review only after the independent
  reviews and the canonical full gate (`make verify` for behavior-affecting
  changes) have completed successfully.
- Treat CI failures and Copilot review findings as work to investigate and
  resolve before merge. Validate a finding before changing the implementation.
- Copilot automatically reviews the initial ready-for-review revision, but a
  later push does not itself request another review. Request every subsequent
  Copilot review explicitly when one is required.
- Request another Copilot review after a push when the preceding Copilot review
  reported anything other than no comments, or when the new work crosses a
  semantic milestone. Do not request it for an ordinary push that satisfies
  neither condition.

# Implementation Review

During implementation, use the narrowest focused checks that exercise the
changed behavior. Once those checks pass and the diff is stable enough to
review, run the independent local reviews in separate processes when their
CLIs are available. Reviews are a fixed budget, not a loop:

- Claude reviews once per pull request iteration: one review of the stable
  diff before handoff. Fixes made in response to findings get focused
  validation and the final gate, not another review round; only substantial
  new work pushed to the same pull request afterwards constitutes a new
  iteration carrying one further Claude review.
- Codex reviews once per semantic milestone of the issue — a design boundary
  such as "the new contract is established", "consumers are switched to the
  new path", "the old path is deleted" — never per diff revision, and never
  for mechanical follow-ups such as comment fixes or one-line corrections.
- The review requirement rides with the gate: a change is reviewed exactly
  when it needs the canonical full gate. A change in the gate-exempt class —
  documentation or comment-only edits to files not executed or consumed by
  the build or gates — is review-exempt as well, and a pull request claiming
  that exemption names it in its description.

This budget is deliberate: unbounded
re-review rounds inflated lead time, and out-of-scope findings folded in
blindly drifted pull requests away from their issue. Do not consider the
change ready for handoff or ready-for-review until the budgeted reviews have
completed, their findings have been validated, and every required final gate
is green on the final diff. A draft pull request may be opened earlier when
its outstanding review and validation status is stated explicitly in the pull
request description.

- Run the reviewers read-only; they must not edit the worktree. Explicitly name
  the artifact each reviewer must inspect: the full staged and unstaged diff
  plus every in-scope untracked file for a dirty worktree (stage intended new
  files first or name them explicitly), or the relevant commit range for
  committed work.
- Give the reviewers the task scope and these project principles, but do not
  pass the implementing agent's conversation context or reasoning. Include all
  known limitations, unresolved questions, and suspected weak points rather
  than only a favorable summary. Run every review from a fresh process with no
  inherited implementation conversation.
- Ask for concrete, evidence-backed findings about correctness, layer ownership,
  generality, binary validity, and missing test coverage, and instruct the
  reviewers to reject weak or speculative hypotheses explicitly.
- Independently validate every finding before changing the implementation; do
  not apply reviewer suggestions blindly. A validated finding outside the pull
  request's declared scope is reported for the issue tracker, not folded into
  the pull request.
- After addressing material findings, rerun the focused checks — the review
  budget for this iteration is spent, so do not open another review round. Run
  the canonical full gate (`make verify` for behavior-affecting changes) once
  the diff is stable, rather than after each implementation iteration.
- If the full gate fails, reproduce and iterate with the failing stage or
  focused check before running the full gate again; the fix is validated by
  those focused checks, not by a new review round. A green full-gate result
  must be repeated only when a subsequent code, build, or packaging change
  can invalidate it; review-only discussion
  and documentation/comment-only edits to files not executed or consumed by
  the build or gates do not require another full run.
- If a budgeted review cannot be run, report that explicitly rather than
  silently skipping it.

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

# Implementation Review

During implementation, use the narrowest focused checks that exercise the
changed behavior. Once those checks pass and the diff is stable enough to
review, run the independent local reviews in separate processes when their
CLIs are available. Reviews are a fixed budget, not a loop:

- Claude reviews once per pull-request iteration: one review of the stable
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

This budget is deliberate, adopted after its trial on issue #370: unbounded
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

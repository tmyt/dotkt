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
- Fixes must express general rules that produce valid CLR binaries from
  arbitrary Kotlin source. Do not introduce local special cases tied to a
  particular library or function name.

# Implementation Review

After implementing and locally validating a change with the applicable project
checks (`make verify` for behavior-affecting changes), complete independent
local reviews from both Claude and Codex in separate processes when their CLIs
are available. Do not consider the change ready for handoff or a pull request
until both reviews have completed and their findings have been validated.

- Run the reviewers read-only; they must not edit the worktree. Explicitly name
  the artifact each reviewer must inspect: the full staged and unstaged diff
  plus every in-scope untracked file for a dirty worktree (stage intended new
  files first or name them explicitly), or the relevant commit range for
  committed work.
- Give the reviewers the task scope and these project principles, but do not
  pass the implementing agent's conversation context or reasoning. Include all
  known limitations, unresolved questions, and suspected weak points rather
  than only a favorable summary. Run both reviews from fresh processes with no
  inherited implementation conversation.
- Ask for concrete, evidence-backed findings about correctness, layer ownership,
  generality, binary validity, and missing test coverage, and instruct the
  reviewers to reject weak or speculative hypotheses explicitly.
- Independently validate every finding before changing the implementation; do
  not apply reviewer suggestions blindly.
- After addressing material findings, rerun the relevant validation and repeat
  independent review when the resulting change is substantial.
- If either reviewer cannot be run, report that explicitly rather than silently
  skipping it.

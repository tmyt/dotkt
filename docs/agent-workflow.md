# Agent workflow

[AGENTS.md](../AGENTS.md) defines the project principles and review budget. This
runbook supplies the operational steps for one task. It does not grant authority
to pick unrelated issues, repeat a milestone loop, merge, or publish a release;
use the scope authorized by the current user request.

## 1. Establish scope and a worktree

Read the applicable instructions, current issue, and current implementation.
Record a narrow acceptance condition and exclusions. An old issue's diagnosis
may no longer describe the code. A milestone goal is not a single PR's scope.
Give new issues labels and the user-agreed milestone; leave PR milestones unset.
Track validated unrelated findings separately rather than growing the diff.

From the primary checkout, use a dedicated branch and worktree based on the
current remote base. Substitute task-specific names in these examples:

```sh
repo_root=$(git rev-parse --show-toplevel)
branch_name=issue/NUMBER-short-name
worktree_dir="$repo_root/build/agent-worktrees/issue-NUMBER"
git fetch origin main
git worktree add -b "$branch_name" "$worktree_dir" origin/main
```

Do not reset, clean, or edit the primary checkout or another task's worktree.
If resuming an existing branch, inspect its state instead of recreating it.
Respect sandbox permissions for Git metadata and network access; use the
environment's approval mechanism where required.

While a PR waits for CI, work on an independent issue in another worktree when
authorized. Normally keep one CI-waiting task and one active implementation,
with separate scope, artifacts, and checkpoints. Dependent changes must wait
for their prerequisites. When the base advances, inspect relevant code, build,
and test changes and integrate them when necessary; the updated head needs its
own CI result. Unconsumed documentation-only base changes do not inherently
invalidate implementation tests, but never claim unequal trees are identical
and always obey repository up-to-date requirements.

## 2. Implement, focus-test, and create a draft

Implement the general rule at its owning compiler layer. Keep upstream common
sources aligned and do not add legacy-artifact fallbacks. Use the narrowest
checks that exercise the change; inspect existing coverage before adding tests
that duplicate an existing contract. Avoid issue-number-only code comments.

Once focused checks pass, commit, push the task branch, and create the draft:

```sh
gh pr create --draft --base main --head "$branch_name" \
  --title 'Concise outcome' --body-file /absolute/path/to/pr-body.md
```

The description contains the problem, bounded solution, issue link, focused
results, and outstanding reviews/full gate. Update pending steps as they finish,
but do not accumulate a chronological status diary or large status tables.

Documentation/comment-only changes to files not executed or consumed by the
build or gates are exempt from both the full gate and independent reviews.
Name that exemption in the PR and check the actual changed paths: some Markdown
files are package-version or schema gate inputs. Use `git diff --check`, link
checks, and the selector's dry run as appropriate. For example:

```sh
bash scripts/gate.sh --dry-run AGENTS.md docs/agent-workflow.md docs/README.md
```

## 3. Review a stable, explicit artifact

For behavior-affecting changes, follow the exact budget in AGENTS.md: Claude
once per stable PR iteration; Codex once per semantic milestone, not per push.
Ordinary fixes to findings do not buy another round. Substantial new work is a
new iteration/milestone only when it genuinely crosses that boundary.

Prepare a review packet naming the task scope, acceptance conditions, exclusions,
project principles, known limitations, and suspected weak points. Identify either
the exact base/head commit range or the full staged and unstaged diff plus every
intended new file. Stage intended new files or name them explicitly; a plain
`git diff` silently omits untracked files. Review the source as well as the diff.
Keep each reviewer's report separate from the other reviewer's input.

Start fresh processes without inherited implementation conversation. For example,
after preparing the packet and existing output directories:

```sh
claude -p --permission-mode plan --tools 'Read,Grep,Glob' \
  --no-session-persistence < "$review_dir/prompt.txt" \
  > "$evidence_dir/claude-review.log" 2>&1

codex exec --ephemeral --sandbox read-only --cd "$review_dir" \
  --output-last-message "$evidence_dir/codex-review-final.md" - \
  < "$review_dir/prompt.txt" > "$evidence_dir/codex-review.log" 2>&1
```

These are separate independent invocations; they may overlap when resources
allow. Define `review_dir` and `evidence_dir` as absolute task-specific paths.
The packet must identify the source worktree and the exact artifact to inspect.
Check the installed CLI's help if its options differ. Run Claude with network
access outside the network-restricted sandbox, using the environment's authorized
execution mechanism, while keeping its review tools read-only. Do not force a
model, set `API_TIMEOUT_MS`, or use a short process timeout. A long review may
take 20--30 minutes; allow it to finish naturally rather than terminating it at
an observation timeout. Do not deliberately delay a review that finishes earlier.

A review prompt should request a completed, evidence-backed report, not a plan:

> Inspect the named artifact and source read-only, within the stated scope and
> project principles. Report concrete correctness, layer-ownership, generality,
> binary-validity, or coverage problems, with file/line evidence and impact.
> Explicitly reject weak or speculative hypotheses. Do not edit files, run builds
> or tests, mutate Git/GitHub, or delegate work. Do not read another reviewer's
> report. Separate validated out-of-scope concerns from in-scope findings. State
> explicitly when there are no actionable findings.

Independently reproduce or substantiate each finding before changing code.
Respond to non-adopted findings with evidence. Put validated out-of-scope work
in the issue tracker. If a CLI or service cannot complete its budgeted review,
record it as not performed and report the blocker; do not imply approval.

## 4. Final gate, ready, CI, and Copilot

After material fixes, rerun focused checks. Push the stable final diff and use
the complete CI full gate on that immutable PR head as the final validation.
Local focused checks and independent reviews remain required; a duplicate
local `make verify` is not routine. Keep it available as a diagnostic equivalent,
including during CI outages, without bypassing repository-required checks.
A change-aware subset is not a replacement for the full CI gate.

For the [verify workflow](../.github/workflows/verify.yml), confirm success of
`prerequisites`, all four `verify-core` shards (`test-corpus`, `compile-fail`,
`lowering`, `integration`), `packaged-sdk`, and the final `verify` aggregate.
Inspect individual jobs: a skipped, cancelled, incomplete, or partial run is
not a passing full gate. Check other repository-required checks too. Record
the PR head SHA, run URL/ID, run head SHA, and job conclusions; they must refer
to the final head, not an earlier successful push. For example:

```sh
gh pr view NUMBER --json headRefOid
gh run view RUN_ID --json headSha,status,conclusion,jobs,url
gh pr checks NUMBER
```

If CI fails, reproduce with its failing stage/focused test locally and iterate
there before pushing the fix. Every new behavior-affecting head needs its own
successful full CI gate, not another local review round. Review discussion does
not invalidate checks. Non-consumed documentation/comment-only edits retain
their gate/review exemption; repository protections still apply.

When the budgeted reviews and required validation are complete, update the
concise PR description and mark it ready:

```sh
gh pr ready NUMBER
gh pr checks NUMBER
gh pr view NUMBER --json headRefOid,isDraft,mergeStateStatus,reviews,url
```

The initial ready-for-review revision normally triggers Copilot. Allow several
minutes, inspecting reviews and review threads while CI runs. No response yet
does not mean "no comments". After later pushes, request Copilot explicitly if
its preceding review had anything other than no comments, or the push crosses
a semantic milestone. Ordinary pushes satisfying neither condition need no
new request. Verify that a required re-review actually arrived for the revision.
Use the PR's reviewer-request/re-request control (or the corresponding GitHub
API); pushing again or merely mentioning Copilot is not a review request.

Investigate CI failures and validate Copilot findings before making changes.
Explain justified non-adoption in the thread and resolve it when authorized.
Apply the same focused/final-gate invalidation rules to review-driven fixes;
do not restart local reviewers for mechanical corrections.

For an outage or quota limit, report the unavailable check accurately and seek
a current, scoped user decision if the task cannot proceed without it. An
explicit exception is not "no comments", must not spread to other checks, and
does not authorize changing protections, models, or administrator bypasses.
Do not bake a temporary monthly quota exception into this runbook.

## 5. Merge, preserve evidence, and clean up

When merge is authorized, confirm the actual final head, CI for that head,
review dispositions, and any explicit exceptions. Use normal repository rules
and a head match so an intervening push cannot silently change what is merged:

```sh
gh pr merge NUMBER --squash --match-head-commit FINAL_HEAD_SHA
```

Confirm GitHub reports the PR merged and check the linked issue's disposition.
Do not describe an open or merely mergeable PR as merged, and do not leave an
authorized merge implicitly to the user. If rules still block it, report the
specific blocker rather than bypassing them.

Before removing the task worktree, preserve a compact receipt outside it: base,
tested head and merged commit/tree, PR URL, focused/full-gate results and logs,
review reports and budget used, CI links, and exceptions. Keep necessary repros,
not a copy of the entire worktree or multi-GB disposable caches. Check tracked,
untracked, and ignored task outputs for anything that must be retained. Inspect
the exact path and `git status --short --untracked-files=all`, then remove only
the task's clean worktree with `git worktree remove /absolute/task/worktree`.
Do not force removal when it reports uncommitted work.

Remove used task-owned probes and temporary builds once no process needs them.
Never recursively delete broad directories or another task's work. Report
material removal and what is preserved or reproducible. Do not accumulate
`archived-worktree` copies.

## Waiting and resuming

Keep a compact current checkpoint outside the disposable worktree: scope,
issue/PR, worktree/branch, base/head, validation status with log paths, consumed
review budget, live process/session handles, exceptions, and next action.
Completed history belongs in receipts, not the active to-do list.

Use resumable process handles and durable logs for long checks. Poll the same
process at sensible intervals; an observation timeout, empty output, or missing
report is not an exit status. Do not launch duplicates or repeatedly reanalyze
unchanged state to fill waiting time. Give concise progress updates without
claiming unobserved success. After interruption/restart, reconcile the checkpoint
with actual processes, Git, PR, and CI state before continuing. Do not reread
completed historical PRs or rerun spent reviews simply because context compacted.
Use an independent task's worktree to overlap useful implementation with CI
waiting, within the bounded concurrency described in section 1.

## Requested releases

Release only when requested, using the existing
[release workflow](../.github/workflows/release.yml), not a parallel publishing
path. Preserve required PDBs, resources, references, and validation; do not save
time by weakening the release contract or redoing already valid checks.

Before reporting completion, confirm all of the following:

- The requested version in `packaging/DotKt.Versions.props` is merged, with
  valid final-gate/review/CI evidence. Reconcile the tested and merged trees;
  do not tag an untested integration result.
- An annotated matching `vVERSION` tag points to that release commit and has
  been pushed. A version bump or local package build is not publication.
- The tag's release workflow succeeded and every expected package (currently
  five) is available at the public feed with the intended version and source
  provenance. Partial upload is not completion.
- A GitHub Release exists for that tag. The workflow's package push alone does
  not create this release record.
- Fresh CLI and MPP template projects restore, build, and run using only the
  public packages in isolated package caches, with no local feed/cache masking
  missing or stale publication. Preserve the public-source configuration and
  logs as evidence.

Do not rebuild downstream atomicfu, coroutines, or UI repositories solely as a
release ritual unless the user requests that work. If publication or public
smoke is blocked, report the exact incomplete step instead of calling it done.

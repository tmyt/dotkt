# verify-schema / verify-sanity self-tests

Both gates validate whatever BIR/CIR is on disk, so a check that silently stopped checking would look exactly
like a clean corpus. These synthetic documents pin the checks that have no natural negative in the emitted
corpus, and the legitimate shape next door for each — without the positive half, a validator that rejected
everything would pass the negative half.

The **file extension picks the lane**, because the two validators have different scopes (§2.7 plan vocabulary
is BIR-only; the sanity invariants are post-lowering CIR):

| files | driver | validator |
|---|---|---|
| `*.bir.json` | `tests/ir/run-schema.sh` | `scripts/verify-schema.py` |
| `*.cir.json` | `tests/ir/run-sanity.sh` | `scripts/verify-sanity.py` |

- `reject-*` — the validator MUST reject each, and its message must contain the file's expected substring
  (`reject-<name>.expected`).
- `accept-*` — the validator MUST accept each.

Each runner runs its own half before the real corpus.

## What is pinned today

**Schema (`*.bir.json`).**

- The §2.7 **nesting rule** (`plan_scope`) — `reject-dangling-bindref`, `reject-forward-bindref`,
  `reject-nested-plan-unknown-id`, `accept-nested-plans`.
- The §2.7 **granularity rule**: a plan exists only where a value can acquire a SECOND reader, so a call whose
  operand subtree merely *suspends* — `h(f(), 1)` with `f` suspending — is plain BIR with no `callEval` around
  it (`accept-unplanned-suspension-operand`). Where a suspension is planned, and by whom, is bir2cir's
  decision; a BIR-side "every suspension-bearing operand must be planned" rule would make the emitter's own
  legal output illegal, and this fixture reddens if one is added.

**Sanity (`*.cir.json`).**

- The **suspension-lowered** invariant (check 6) — `reject-unlowered-suspension` is a `suspendCall:true` left
  in an ordinary method body, which ilemit would emit as a plain invocation with no resume point.
  `accept-lowered-suspension` holds both legitimate neighbours: the cold-lowered `f$dotkt_suspend` call that
  carries no tag, and the coroutine PRIMITIVE that bir2cir deliberately leaves un-lowered — still
  `mods.suspend`, so ilemit stubs or refuses it and never walks its body. The exemption has no negative in the
  corpus either, so it is pinned here rather than left to a comment.

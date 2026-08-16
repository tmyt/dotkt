# verify-schema / verify-sanity self-tests

Both gates validate whatever BIR/CIR is on disk, so a check that silently stopped checking would look exactly
like a clean corpus. These synthetic documents pin the checks that have no natural negative in the emitted
corpus, and the legitimate shape next door for each — without the positive half, a validator that rejected
everything would pass the negative half.

In **this** directory the **file extension picks the lane**, because the two validators have different scopes
(§2.7 plan vocabulary is BIR-only; the sanity invariants are post-lowering CIR):

| files | driver | validator |
|---|---|---|
| `tests/ir/selftest/*.bir.json` | `tests/ir/run-schema.sh` | `scripts/verify-schema.py` |
| `tests/ir/selftest/*.cir.json` | `tests/ir/run-sanity.sh` | `scripts/verify-sanity.py` + C# `IrSanity` |
| `tests/ir/selftest-schema/*.cir.json` | `tests/ir/run-schema.sh` | `scripts/verify-schema.py` |

The third row exists because the schema validator also has CIR-only rules — a shape whose *well-formed* half
only exists after lowering, such as the resolved #370 `memberRef`. Those fixtures cannot sit here: the
extension is already spoken for by the sanity lane, which would hand a schema-only refusal to a validator that
has no opinion about it and (correctly) accept it. So the schema lane's CIR fixtures get their own directory,
and the rule becomes *directory + extension* picks the lane. Both lanes run the same reject/accept contract
and the same "one of each or the lane asserts nothing" requirement.

- `reject-*` — the validator MUST reject each, and its message must contain the file's expected substring
  (`reject-<name>.expected`).
- `accept-*` — the validator MUST accept each.

Each runner runs its own half before the real corpus, and fails if it discovered no fixture of either
kind — a lane that asserted nothing is indistinguishable from a lane that passed.

The sanity lane asserts **both** implementations on every fixture: `scripts/verify-sanity.py` is only the corpus
net, and the normative checker is the C# `IrSanity` compiled into ilemit, which is what actually stops a bad
build. ilemit cannot fully emit a synthetic fixture — there are no references to resolve its types against — so
the accept side asserts that no sanity diagnostic is raised rather than a zero exit; the sanity gate runs at the
head of `EmitAssembly`, ahead of any resolution.

## What is pinned today

**Schema (`*.bir.json`).**

- The §2.7 **nesting rule** (`plan_scope`) — `reject-dangling-bindref`, `reject-forward-bindref`,
  `reject-nested-plan-unknown-id`, `accept-nested-plans`.
- The **CIR-only vocabulary that must not appear in kotc BIR** — `reject-cir-only-signature-carrier` (a
  pointer type), `reject-cir-only-array-rank` (a multi-dimensional array rank) and `reject-memberref-in-bir`
  (a resolved #370 member identity). Kotlin source cannot spell any of the three, so one in BIR means the
  frontend projection started deciding physical CLR shape — the layer inversion, not a typo.
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
- The **stamp-agreement** invariant (check 7, spec §2.7). Four refusals, one per way the relation can refute:
  `reject-stale-sty` is the shape that motivated the check — a call retyped to `List<object>` whose frontend `sty`
  still claims `List<Int?>`, two unrelated invariant reified generics, so a slot declared from the stamp is invalid
  IL; `reject-stale-sty-scalar` is a `dynRet` whose head name simply differs; `reject-stale-sty-under-nullable` and
  `reject-stale-sty-array-element` are what hold the wrapper-stripping and the two array spellings in place, since
  those two arms exist to make a refutation REACHABLE (delete either and its fixture is silently accepted).
  `accept-sty-stamp-equivalences` is the other direction — one method per documented equivalence, so the check is
  RED the day it starts reddening on a legitimate pair. Read it as a guard against a future arm that over-refutes
  rather than as a pin on each present arm: several of its cases (a type variable, an absent stamp, a shape of
  unlike arity) are accepted by the relation's catch-all, so they would still pass with the arm that names them
  deleted. The arms a fixture genuinely pins are the vocabulary table, `kotlin.Nothing`, and the two above.
  Note the CHOKEPOINT for this invariant is not here — `sty` is stripped on the way to CIR, so bir2cir checks it on
  the pre-lowering BIR and `tests/ir/lowering/reject-stale-sty-after-passes` is what pins that call.
- The **collection-view completeness** invariant (check 8) — `reject-missing-readonly-collection-view` states
  `IList<String>` and nothing else, which is the CIR of a type whose Kotlin read-only view has no CLR face to land
  on, so a caller passing it into a `List<String>` slot would fail at an `InvalidCastException` far from here.
  `accept-readonly-collection-view` is the same type with both faces stated, plus a map face (BCL
  `IDictionary<K,V>` has no read-only twin in the lattice DotKt uses) that must stay untouched. Neither half has a
  natural witness: bir2cir states the sibling on every type that owes one, so the refusal never occurs in the
  corpus, and a check that had stopped checking would leave the acceptance green.
- The **width of that exemption**, which is the easy thing to get wrong — `reject-unlowered-suspension-in-ctor`
  and `reject-unlowered-suspension-in-static-init` carry `mods.suspend` on the constructor and on the
  containing type, and must STILL be refused. ilemit's suspend guard lives in `EmitMethodBody` alone: it emits
  a constructor body, and builds a type initializer from the fields, without ever consulting the flag. An
  exemption derived from the scope's declaration rather than from its KIND lets a suspension through exactly
  there, and these two are what say so.

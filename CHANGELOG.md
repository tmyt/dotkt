# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Added

- **A suspension that escapes the cold lowering is now caught at the CIR boundary (area:bir2cir, area:ilemit).**
  `suspendCall:true` is kotc's frontend fact that a call site suspends, and bir2cir's `SuspendColdLowering` is its
  only consumer — it rebuilds each suspending call as a resume label plus the callee's cold-shape call (a
  `$dotkt_suspend` cold entry, or the awaiter sequence for a `.await()` CLR bridge), out of fresh nodes that carry
  no tag. Until now nothing said so: a suspension that slipped through reached ilemit as an ordinary invocation, so
  the caller read the raw `Task`/`COROUTINE_SUSPENDED` sentinel where the awaited value belonged and the state
  machine got no resume point — an `InvalidCastException` far from its cause, or a silently wrong value. The shared
  `IrSanity` check set (run in-process by both bir2cir and ilemit, mirrored offline by `scripts/verify-sanity.py`
  for `make verify-sanity`) gained the invariant, and the message names the declaration. A METHOD that still
  carries `mods.suspend` is exempt, because ilemit's guard on that exact flag returns before it reaches the
  statement walk; such a survivor is stdlib-only, since the self-build deliberately retains the original beside its
  cold entry and the admit gate excludes the inline coroutine primitives, where an app build removes the original
  outright. Only a method scope can be exempt: ilemit emits a constructor body, and builds a type initializer from
  the fields, without consulting the flag at all, so a ctor and a static-initializer group are always checked —
  deriving the exemption from the scope's declaration instead of its kind would have let a suspension through a
  `.cctor` under a type that carried the modifier. Calibrated against the current corpus: all seven survivors in
  the runtime stdlib CIR sit in exempt declarations, no emitted body carries one, and the app corpus has none.
- **The IR sanity gate gained a self-test lane, and the schema gate a granularity fixture
  (`tests/ir/selftest/`).** Both gates validate whatever is on disk, so one that stopped checking would look
  exactly like a clean corpus. `tests/ir/run-sanity.sh` now runs the directory's `*.cir.json` half first
  (`run-schema.sh` keeps the `*.bir.json` half), pinning both the suspension invariant above and the
  `mods.suspend` exemption it is calibrated against — neither has a natural negative in the corpus. On the schema
  side, `accept-unplanned-suspension-operand.bir.json` pins the §2.7 granularity rule for the shape the
  suspension work touches: a call whose operand merely suspends acquires no second reader, so `h(f(), 1)` is
  plain BIR with no `callEval` around it. Where a suspension is planned is bir2cir's decision, and a BIR-side
  rule requiring one would make the emitter's own legal output illegal — the shape is ordinary emitter output,
  present in the hundreds across the stdlib and coroutine corpora, so such a rule would redden real builds and
  not just the fixture.
  The sanity lane asserts each fixture against BOTH implementations, not only the offline mirror: the normative
  checker is the C# `IrSanity` compiled into ilemit, and until now nothing exercised it — a check deleted there
  would have left every gate green. Both lanes also now fail when they discover no fixture of either kind, and
  when a `reject-*` case has no expectation text (an empty one matched any message, degrading the assertion to
  "exited non-zero").

- **Cross-target (target RID != host RID) reference-asset selection is now covered by the gate
  ([tmyt/dotkt#192], area:ilemit, area:packaging).** `tests/msbuild/run.sh` gained
  `ktproj-crosstarget-rid-assets`: it builds a throwaway RID-implementation package from
  `tests/msbuild/rid-probe/` and runs a real `dotnet build -r <rid>` for a RID derived to differ from the host,
  asserting that ilemit loads the `runtimes/<rid>/lib` asset of the TARGET RID — on an exact-RID hit and through
  the RID fallback chain (`win-x64` to `win`, `linux-x64` to `unix`), under both the portable RID graph and the
  built-in chain. The package's RID-neutral placeholder omits a member its compile surface declares, so a wrong
  selection is a red build rather than a subtly different program; the scenario additionally replays the emit at
  the host RID and requires that replay to fail, so the assertion cannot pass vacuously. Previously the RID flow
  was only exercised at the host RID and cross-target selection had been confirmed by hand.
- **A negative-compile gate (`tests/compile-fail/`, wired into `make verify-tests`).** Some behavior is only
  expressible as a REFUSAL — source the compiler must reject, with the message it owes the author. Each case is a
  `.kt` plus an `.expected` list of substrings the diagnostic must contain; the lane is green iff every failing
  case is listed in its (currently empty) `CF_XFAIL` baseline, and reports NEW-FAIL/FIXED like the other gates.
  It opens with the eight byref-like storage refusals below.

### Fixed

- **`x is T?` answers TRUE for null again, and with it every `joinToString` over a null element (area:bir2cir,
  area:ilemit).** Kotlin's `is` against a NULLABLE type operand accepts null — `null is String?`, `null is Int?`,
  `null is Any?` are all true — and the frontend DEPENDS on it: the `else` branch of `when { x is T? -> … }` is
  reachable only for a non-null `x`, so it carries a smart-cast, and `x.toString()` there resolves to the
  `kotlin.Any` MEMBER rather than the null-safe `Any?.toString()` extension. kotc emitted the type operand's `?`
  faithfully, but nothing downstream read it: type lowering erases nullability from every reference type (every CLR
  reference is nullable, so the lowered type cannot carry the signal), leaving a bare `isinst`, which matches no
  null. The test went false for null and the smart-cast the frontend had already granted dereferenced one. The
  stdlib's `appendElement` is exactly that shape — `element is CharSequence?`, else `element.toString()` — so
  `arrayOfNulls<String>(2).joinToString()` threw a `NullReferenceException` inside `AppendableKt.appendElement`
  instead of rendering `null, null`, on every join receiver (array, list, sequence, `joinTo`), with or without a
  transform, and at any null position. bir2cir's new `NullableIsInstMatch` marks the node `nullMatches` while the
  `?` is still on it, and ilemit projects the one extra `dup; brtrue` that answers true for null — the operand is
  still evaluated exactly once, and a non-nullable type operand is untouched. (#287)
- **bir2cir (area:bir2cir): a nullable-generic return that was object-erased no longer crosses a suspension under
  its PRE-erasure type.** `fun <T> f(x: T): List<T?>` has its `Nullable(T)` erased to `object` on the declaration
  side, so the emitted method returns `List<object>`, while the call site — emitted with `T` already substituted —
  is stamped `List<Nullable<Int>>`. `NullableTvErasureCallRealign` realigned the call's `ret`/`dynRet` to the erased
  form but left the frontend `sty` stamp behind, and the deriver reads `sty` first. `List<object>` and
  `List<Nullable<int32>>` are UNRELATED invariant reified generics — the very reason that pass exists — so any slot
  declared from the stale stamp is invalid IL rather than a diagnosable drop. A suspension is what makes such a slot
  exist, in two shapes: the erased call sitting LEFT of a suspending operand, where stage 0 declares the plan's
  spill local from the stamp, and the erased call BEING the suspension, where the awaited state-machine field is.
  Both produced an ilverify `StackUnexpected` (`IReadOnlyList<object>` where `IReadOnlyList<Nullable<int32>>` was
  expected, and the reverse at the read). The pass now restamps `sty` at each of its four result-retype sites, per
  the spec §2.7 invariant. The corpus had never composed nullable-generic erasure with a suspension, which is why
  no gate saw it; `SuspendResultTypePrecedenceTests` composes both shapes now.
- **kotc (area:kotc): a `suspend fun interface`'s SAM shim carries its Kotlin RESULT TYPE, not just the suspend
  modifier.** `suspendRet` rides alongside `mods.suspend` on every declaration — the modifier is the fact, the slot
  is the type — and the SAM lift (`BirEmitterLifts`, the shim behind `FlowCollector { … }` and every other suspend
  `fun interface` lambda) emitted the modifier alone. bir2cir's cold registry reads the slot, so the shim's awaited
  values were typed `kotlin.Any`: boxed on the way into the state machine and unboxed on the way out, at every
  suspension inside a suspend SAM body. Found by the refusal that replaced that `kotlin.Any` (see §7b), which is
  what a fallback hides — the drop had been silent since the shim was introduced.
- **bir2cir (area:bir2cir): a suspension inside a suspending call's own operand list no longer hangs forever
  ([tmyt/dotkt#272]), and an operand that CONTAINS a suspension is no longer evaluated after the operand to its
  right ([tmyt/dotkt#286]).** `corAdd(x, corTick(1))` never completed: the outer call wrote its resume label, the
  inner suspension overwrote it, and every resume jumped back into the inner state. `h(f()) + g()` traced F,G,H
  where Kotlin requires F,H,G: the suspension's segments were appended and the residual `h(<awaited>)` was left
  in its slot, so it ran after the NEXT operand's suspension. Both are now unreachable by construction. A new
  STAGE 0 of the suspend lowering (`toolchain/bir2cir/SuspendOperandPlan.cs`) runs before any state machine
  exists and, for every node the shared operand descriptor recognises, wraps the operands in a call-evaluation
  plan (spec §2.7) forced by POSITION: every operand left of the last suspension-bearing one becomes a `var`
  ahead of the node, and — when the node is itself a suspend call — so does that operand, which lifts the nested
  suspension out of the argument list entirely. Everything to the right stays in its slot and is still evaluated
  after the resume. `tests/coroutines/fixtures/SuspendOperandOrderTests.kt` pins the issue's own repro plus the
  instance, receiver-position, generic and four cross-module `clr*` arms of the same shape — one of which
  (`clrGenericInstance`) the descriptor only listed for a call that WAS the suspension, never one that merely
  contained it. The issue's string-valued sibling (`wrap(f()) + g()`, and the `"…${f()}…${g()}…"` template that
  means the same thing) is covered too: both are a `concat` by the time the plan is made, which the descriptor
  had never named.
- **bir2cir (area:bir2cir): a .NET property read, a delegate invoke, an `Any`-slot method or an interface call on
  a type-parameter receiver, sitting left of a suspending operand, now observes the state BEFORE the suspension.**
  `sb.Length + susp()` read the length the suspending callee had already changed. The retired rule decided which
  operands were evaluated before a suspension by KIND — a subtree free of calls, allocations, assignments and
  control transfers was judged safe to defer past the resume — and that set never closed: a raw field read and an
  array element had been added, `clrPropGet`/`delegateInvoke`/`objMethod`/`constrainedCall` never were. Stage 0
  answers by position instead, so the whole family is covered without naming a kind, and the predicate, the
  eval-order rewrite it fed and the liveness analysis's model of that rewrite are all deleted.
- **bir2cir (area:bir2cir): an argument that never returns, left of a suspension in a suspending call, compiles
  and runs instead of being refused.** `sum(run { throw … }, relay())` was a compile-time refusal, because the
  cold-call builder met it half-way through assembling a suspension point it then had no way to elide. Stage 0
  sees the same shape before any state machine exists, so the node and every operand right of the terminal one
  are simply dropped and the throw is the expression's value. `tests/compile-fail/` loses that case (its runtime
  twin is in `SuspendOperandOrderTests.kt`); the neighbouring arrangements it contrasted with are unchanged.
- **bir2cir (area:bir2cir): a `!!`, an elvis or a safe call in an argument to the LEFT of a suspending argument
  no longer aborts the compile.** `h(x!!, susp())` was REJECTED — "the operand … carries no static type" — on
  source the frontend had accepted. kotc lowers `x!!` to `{ var __nn = x; if (__nn != null) __nn else throw }`
  and stamps a type on none of the three nodes, so the deriver that has to type the operand's evaluation-order
  spill slot had nothing to read. It now reads a `cond` through its LIVE branch — a branch that never returns
  says nothing about the type of the value the other branch produces, so a `throw` arm cannot answer while the
  other arm can — and a `local` through the `var` the block itself declares. The value-nullable arms
  (`nullableWrap`/`nullableValue`/`safeCastValue`) also read their `elem` instead of a `type` slot no producer
  writes on them, so `n!!`, `n ?: 0` and `b?.size()` in the same position resolve too. Five shapes that were
  compile aborts are pinned as running tests.
- **bir2cir (area:bir2cir): a side-effecting operand to the left of an operand-position `try` no longer faults
  at runtime.** `f() + try { … } catch { … }` spills `f()` to a temp so it keeps evaluating before the hoisted
  try, and that temp's declared type was copied from whichever of `type`/`ret`/`dynRet` the node carried — a
  call node carries none, so the spill was declared `kotlin.Any`, and the emitted unbox read a value that was
  never boxed (`AccessViolationException`, process abort). The hoist now threads the lexical scope and derives
  the type the way every other spill site does.

### Changed

- **bir2cir (area:bir2cir): the three result-type stamps have ONE precedence — `sty`, then `ret`, then `dynRet` —
  stated once in `bir-common/NodeType.cs`.** `sty` is the frontend's INSTANTIATED static type, stamped per call
  site; `ret` is emitted only when the callee or its owner is GENERIC, which is exactly where it may name the
  UNinstantiated declared type. Reading `ret` first therefore typed a generic-owner call by its declaration
  instead of by its use, and four separate restatements of the order had accumulated — the core, an explicit
  "deliberately not unified yet" override on `StaticType.Surface`'s call/field arm, the awaited-value read in
  `EmitSuspensionPoint`, and `IsSuspendFunctionValue`'s own copy. The core is flipped and the other three are
  gone: the call/field/`bindRef` family needs no arm at all now that `sty` wins everywhere, and the two suspend
  readers ask the shared deriver. The order rests on an invariant now written into `docs/bir-cir-spec.md` §2.7
  and cited at the flip site: **a pass that changes a node's result type rewrites or deletes its `sty`** — a
  stale stamp on a retyped node is a bug in that pass, not a reason to demote the stamp. Byte-identical CIR
  across the reference and runtime stdlib corpora (497 files) and across the test corpora.
- **bir2cir (area:bir2cir): a slot whose type cannot be derived is a REFUSAL, not a `kotlin.Any` box.**
  `kotlin.Any` in a declared slot is never neutral — it boxes a value type, it makes the read unverifiable
  without an unbox, and it turns an earlier layer's dropped stamp into a runtime `InvalidCastException` far from
  the cause. Four fallbacks are retired. The suspend lowering's conditional temporary is now typed from the
  conditional's LIVE branch, which is what made every value-type `x?.suspendFoo()` crossing a suspension box and
  unbox; `TryValueOperandHoist`'s spill type (formerly `GuessType`, carried as a KNOWN GAP) refuses instead of
  boxing, since a null from the shared deriver there is a hole in the DERIVER; and the two method-return indexes
  the stage-0 typer consults refuse a declaration carrying neither `suspendRet` nor `ret` — surveyed as
  impossible by construction (0 of 7308 stdlib declarations, and every kotc method emitter writes `ret`
  unconditionally). What remains is ABI rather than fallback (the cold entry's `Any?` return, `Continuation<Any>`,
  `Result<Any?>`, the non-generic `IEnumerable` element, an undeclared catch filter), and the site-by-site triage
  — LEGITIMATE-ABI / RETIRED / FOLLOW-UP with its precondition — is `docs/dotkt-semantics.md` §7b.
  These refusals cannot fire on frontend-produced BIR, so `tests/ir/run-lowering.sh` gained a `reject-*` half
  (synthetic BIR that bir2cir must refuse, with the wording pinned) to keep them from being silently defeated;
  all four fixtures are calibrated — the previous binary accepted each one.
- **bir2cir (area:bir2cir): `StaticType.Surface` is founded on the shared node-local deriver
  (`bir-common/NodeType.cs`) instead of restating it.** The two derivations had drifted into disagreeing about
  five kinds — the nullable wrap/unwrap slots, an untyped `cond`, `Nothing`, the `&&`/`||` result, and the two
  spellings of an array type — and a kind classified one way for a spill slot and another way for an operand
  classifier is exactly the drift the shared file exists to prevent. Each disagreement is now either fixed in
  the core (so both consumers inherit it) or one named adapter (`ArrayAsFqn`: the core answers structurally,
  this reader's classifiers are name-keyed). `Surface` keeps only the arms the core cannot answer — the ones
  needing the enclosing lexical scope — and delegates the rest, including the whole call/field family once the
  stamp precedence was unified (see the `sty`-first entry above).
- **A call value NOTHING reads is now evaluated unless evaluating it is genuinely unobservable
  (area:bir2cir, area:kotc; recorded as `docs/dotkt-semantics.md` §7a).** Kotlin evaluates a call's receiver and
  every supplied argument whether the emitted CLR shape has a slot for the value or not; the backend was skipping
  the evaluation for anything that *looked* like a pure load. Two of those are not: a **static-field** read (and an
  **enum-value** read with it) runs the declaring type's initializer — which on this backend is where a top-level
  property initializer and an `object`'s body live, so it can print, throw or mutate — and an **instance-field**
  read dereferences, so it throws on a null receiver, which a platform type makes reachable (§9a). `IsDroppable`
  (Q2 of the value questions) is narrowed to the loads whose evaluation genuinely cannot be detected: `const`,
  `this`, `local`, `bindRef`, `default`, `classRef`. Anything else with no reader becomes a local nobody reads —
  at most one local, and only at a call site that supplies a value the emitted shape cannot place. The
  zero-reader case is now decided by that question ALONE: a `stable` binding used to vanish the same way through
  the inline path without ever being asked whether its evaluation mattered ("may be read twice" was standing in
  for "may be read zero times"). `KClassMemberBinding`'s `value::class` const-fold, which asks the same question
  about the receiver it folds away, delegates to `IsDroppable` instead of restating a narrower ad-hoc set —
  widening the fold to a `this`/plan-read receiver of a known-final builtin type, while explicitly rejecting a
  `classRef` receiver: the DOUBLE class literal `(Int::class)::class` reflects the `KClass` VALUE, and a
  `classRef`'s type slot names the type it REFERS to rather than the type it IS, so folding it would answer "Int"
  for a receiver that is not an `Int`.
  The one shape that relied on the old, too-generous answer is gone at the producer: a plain `companion object` is
  flattened onto its enclosing class and a projected .NET static holder has no instance either, so **kotc no longer
  mints a call-evaluation-plan binding for a receiver naming one** — at any receiver site, ordinary or inline. It
  was a binding nothing could read, holding a read of an `INSTANCE` field this representation never emits (the
  inline emitter already named it a "dangling token"), surviving only because the drop hid it; there was never a
  value there to evaluate. A REAL `object` and a super-typed companion are the opposite case and keep their
  binding: their `INSTANCE` exists and loading it runs the object's own body, so it is an observable evaluation
  that Kotlin orders BEFORE every argument — without the binding, `O.f(side())` let `side()` run first.
- **bir2cir (area:bir2cir): the by-reference argument's location pins ask their own question.**
  `CallEvalLowering`'s `LocationHasPinWork`/`PinLocationOperands` shared Q2's implementation, but they decide
  whether a node is a link of an addressable location's own path — pinning one into a local would take the address
  of a COPY for a value type, so a callee writing through the `byref` would miss the real storage. That is storage
  identity, not side effects; it is now `StaysInLocation`, next to the code that asks it, and the Q2 narrowing
  above leaves it untouched.
- **bir2cir (area:bir2cir): every "is this value pure / stable" predicate is now named after the question it
  answers, and each question has exactly one home.** Five different questions were being asked under three
  interchangeable-sounding names, which invited the assumption that a kind classified one way in one of them was
  a bug in another. `bir-common/BindingStability.cs` becomes `ValueStability.cs` and heads the roster;
  `IsStable` becomes `IsReReadable` (Q1 — may this value be read more than once, with other evaluation in
  between) and `IsTriviallyPure` becomes `IsDroppable` (Q2 — is evaluating it unobservable, so a binding nothing
  reads may be skipped); `TryValueOperandHoist`'s `PureKinds`/`IsPure` become `StackNeutralKinds`/`IsStackNeutral`
  (Q4 — may it stay in its slot when a later sibling hoists out of the protected region); `CallEvalLowering`'s
  `IsLvalueFormer` gains the question it answers (Q5). Naming Q3 — the suspend lowering's resume-stability set —
  is what made it answerable, and the operand-plan entry above then RETIRED it outright, so the roster ships with
  FOUR questions and no resume-stability predicate at all. The control-transfer kind set that the suspend
  lowering stated twice — once inside its impure set, once inside `EscapesExpression` — is now one named constant
  (and the impure set that was its other reader is gone with Q3). Two kind sets lost entries no producer mints at the point they are consulted: `TryValueOperandHoist`
  (`param`, `constNull`, `null` — no producer anywhere) and `CallEvalLowering.EagerKinds` (`newList`, `newSet`,
  `newMap`, `clrPropGet`, `clrPropSet` — minted by passes that run hundreds of lines after it). Pure refactor:
  the emitted CIR corpus is byte-identical.

### Removed

- **facadegen (area:toolchain): remove the retired CLR-to-FIR injection tool.** The production frontend now
  consumes one standard metadata-only KLIB per resolved CLR reference assembly through `dll2klib`, so the old
  import-seeded JSON projection has no build, package, test, or developer entry point. The `facadegen` project,
  `make facades`, and `scripts/gen-facades.sh` are deleted; `make toolchain` now builds only the shipping tools.

### Changed

- **kotc (area:kotc): one home per stability question in the BIR emitter.** `bindOnce` — the splice-once binder
  behind a when-subject, a safe call and a range membership — carried a byte-identical inline copy of the
  call-evaluation plan's `isStableValue` predicate, so the two could drift apart while both claimed to answer
  "is this value free to be re-read and to move past another value". It now calls `isStableValue`, which is the
  single implementation. `isStableAddress` is renamed `isStableLocation`, because it answers a DIFFERENT question
  — whether an argument's *address* may be taken twice and moved, which holds for a mutable `var` too — and a
  name that echoed the value predicate invited the immutability clause to be "restored" into it; its doc now
  states the question and why that clause is deliberately absent. The `BirEmitterCallPlan` granularity note
  pointed at a `BirEmitter.callNeedsPlan` that does not exist and never did; it now describes what really gates a
  `callEval` — a plan scope installed around every call that costs nothing when empty, and, inside it, the
  `planNeeded` tests of `filledArgs`/`filledExternalArgs` (§2.7 triggers (a)-(c)) together with the unconditional
  binding a `callInline` performs (trigger (d)), which the old note did not mention at all. Behavior-preserving:
  emitted BIR and CIR are byte-identical over the full stdlib corpus (ref + runtime, 1001 files) and a
  198-source fixture sweep.

### Fixed
- **bir2cir (area:bir2cir): `x in a..b` now evaluates `a`, `b` and `x` exactly once each, in that order.** Range
  membership is `(a..b).contains(x)`: the range is constructed first, so BOTH bounds always run, left to right, and
  the subject is read after them. The short-circuit fast path (`x >= a && x <op> b`) put the upper-bound test inside
  the lower-bound test's `then`, so a subject below `a` never ran `b` — `0 in lo()..hi()` silently dropped `hi()`'s
  side effect — and it read the subject before either bound, so a subject a bound assigns (`var x = -1;
  x in run { x = 5; 0 }..50`) compared the stale value and answered `false` where Kotlin answers `true`. The three
  operands are now bound to temps up front in Kotlin's order and the comparison legs read the temps. An operand is
  still spliced in place when re-reading it is free — `ValueStability.IsReReadable`, the one answer to that question
  (Q1), replaces a local "stable" set that had wrongly accepted any `local`, mutable or not — so an all-constant
  membership (`5 in 1..10`) still lowers to bare comparisons with no temp at all. The fix covers every form the fast
  path handles: `..`, `..<`, the `until` extension, `!in`, and the `Int`/`Long`/`Char` element types.
  `StringCharSequenceBridge`'s copy of that same "stable" set — harmless, because nothing evaluates between its two
  reads — is gone the same way, so there is no second answer left to drift.
- **bir2cir (area:bir2cir): an argument that never returns, to the left of a suspending one, is no longer treated
  as a value to carry across the suspension.** `pair(run { throw IllegalStateException() }, later())` refused to
  compile — the evaluation-order spill wanted a type for an operand that has no value — and the shapes it stood for
  (`run { return … }`, `error(…)`, any `Nothing`-typed call) were the same. Kotlin evaluates such an operand and then
  nothing else in the expression, *including* the suspension: it is the expression's value, not something to store
  and reload after a resume, and the operands to its right are unreachable. The suspend lowering now says so — the
  terminal operand becomes the value, the rest is dropped, and no spill slot is minted (one would also have left the
  state machine's receiver on the stack when control left through the throw). `bir-common/NodeType.cs` answers
  `Nothing` for an expression-position `throw`/`return`, which is what lets "this has no value" be told apart from
  "I could not derive its type" — the first is ordinary code, the second the dropped type the spill reports.
  The rule applies exactly where it is needed: only an operand with a suspension to its RIGHT is treated specially,
  because that is the only arrangement the spill would have got wrong. `pair(later(), run { throw … })` still
  suspends, resumes and only then leaves, and an argument that never returns in a SUSPENDING call's own list
  (`one(run { throw … })`, `sum(relay(), run { throw … })`) lowers as it always did. The one arrangement that
  remains refused is a never-returning argument to the LEFT of a nested suspension in a suspending call's own list —
  a suspension point that would have to be elided, which the state machine cannot express; the refusal names the
  shape and the workaround (`tests/compile-fail/SuspendTerminalArgumentBeforeSuspension.kt`).
- **kotc/bir2cir (area:kotc, area:bir2cir): an INLINE call's arguments now follow the same evaluation plan as every
  other call's — one evaluation each, in Kotlin's order, whatever the spliced body does with them.** `InlineSplice`
  bound each parameter to a temp in PARAMETER order, so a call that also filled a default ran the default in its own
  slot: `inline fun f(a: Int = t("A"), b: Int, c: Int = t("C"), block: (Int) -> Int)` called `f(b = t("B"))` ran
  A, B, C where Kotlin runs B, A, C — the inline half of the ordering defect the plan fixed for ordinary calls.
  kotc now emits a plan for a `callInline` too (`docs/bir-cir-spec.md` §2.7, granularity trigger (d)): the dispatch
  receiver, the extension receiver and every supplied argument become bindings, in that order. A spliced lambda is not
  a value and is not bound — a literal carrier and a by-name forward of the enclosing inline fn's own lambda parameter
  are the body being spliced — so a lambda-only inline call (`run { … }`) supplies nothing, binds nothing, and still
  emits no plan, exactly as the granularity rule says. `InlineSplice` consumes the bindings instead of minting a temp per parameter, so a body
  that reads a parameter twice, in a loop, or not at all no longer costs a redundant local; a filled default becomes a
  local of the spliced block, which is where Kotlin evaluates a default (in the callee's scope, after every supplied
  value). Consequences:
  - a binding is inlined back into its reader only when that reader sits on the node's EAGER SPINE. A read inside a
    spliced body's statements, a conditional branch, a loop or a closure happens at a different time, a different
    number of times, or not at all, so the value is materialised — which is what "evaluate it at the call, exactly
    once" means once the callee's body is the reader;
  - every value an inline call binds, and every default it fills, carries a type. That closes the staged hole the
    liveness work had to leave open: an untyped local, and an untyped evaluation-order spill in the suspend lowering,
    are now errors that name the lowering which dropped the type, not a `kotlin.Any` slot that silently boxes a value
    type behind a warning;
  - the passes that run between the splice and the plan lowering peel a `callEval` exactly as they peel a
    `valueBlock` when they ask what an expression produces, so a covariant construction spliced under a plan
    (`xs.partition { … }`'s `Pair`) is still widened to its declared slot; and the plan lowering folds the layer it
    creates — a block whose result is a block is one block — so a lambda body ending in a nested inline call
    (`xs.map { s -> s.let { it.trim() } }`) keeps the single layer the splice's own flatten used to guarantee.
- **kotc/bir2cir (area:kotc, area:bir2cir): a call's values are now ONE ordered evaluation plan, so a filled default
  can no longer duplicate a value, reorder a call, or be traded away for storage.** A Kotlin call evaluates its
  receiver, then each supplied argument, then the callee's omitted defaults, each exactly once — but on the CLR those
  values had TWO representations: expressions substituted into filled defaults, and independently hoisted `var`s
  sorted ahead of the call. Whenever a hoisted value turned out to be unholdable one of the two had to be abandoned,
  and every possible choice lost single evaluation, Kotlin order, or legal storage. That is why the same defect kept
  reappearing: three successive attempts each fixed one invariant by breaking another (the enumeration is at commit
  `cb4ff8d`, reachable via `refs/pull/270/head`).
  kotc now emits an ordered **call-evaluation plan** wherever a fill can give a value a second reader
  (`docs/bir-cir-spec.md` §2.7): a `callEval` node carrying the call's bindings in Kotlin evaluation order, with every
  reader — the call's own slot, a spliced same-module default, a reconstructed cross-module data-class `copy` field, a
  `@KotlinDefault` carrier's `{this}` / `{defaultArgParam n}` token — a `bindRef`, a pure read. A constructor
  delegation's plan rides the declaration as `delegationBindings`. bir2cir's `DefaultArgSplice` shrinks to
  materialise-and-reference (it fills the reserved binding and clones only reads), and the new
  `toolchain/bir2cir/CallEvalLowering.cs` turns each plan into locals once every splice has finished: a single-reader
  binding straight back into its slot, a shared one into a `var`, a delegation's into `preStmts` that ilemit emits
  ahead of the base call. Storage remains a separate decision, made later from liveness, and its refusals now name the
  value's source role ("the receiver of `copy`") instead of a minted id.
  Behavioural fixes that follow:
  - a cross-module data-class `copy` evaluates its receiver ONCE — `nextTriple().copy(second = 9)` ran the receiver
    once per omitted field ON TOP of the call's own use of it (three times for `Triple`, four when every field was
    omitted), and re-rendering it also put a receiver evaluation AFTER the argument;
  - a filled default no longer runs BEFORE the values the call supplies: `host().f()` logged the default before the
    receiver, `host().g(arg())` before both, and `host().h(c = arg())` before the argument;
  - an EXTENSION call site ran its whole default-filling pass twice, so a default that another default reads was
    rendered — and evaluated — twice (`"s".ext()` against `fun String.ext(a: Int = bump(), b: Int = a * 10)` called
    `bump()` twice where the non-extension form called it once);
  - a call to a facadegen-injected TOP-LEVEL function bound none of its values, the same fault as the `copy`
    receiver and unfired only because the splice happened to hoist values itself;
  - a byref-like argument at a call with defaults keeps Kotlin's order instead of being jumped by the fill's
    temporary (the coverage arrives here inverted: the fixture as written on the branch it comes from asserted the
    reverse, `"dT"`, while its own comment stated Kotlin required `"Td"` — the compromise it recorded was a property
    of the old shape, not of the CLR);
  - a filled default whose SLOT precedes a slot the call supplies no longer runs first: `f(a: Int = mk(), c: Int)`
    called `f(c = arg())` ran `mk()` before `arg()`, because the positional argument array's order is not Kotlin's
    when an omitted default sits to the left of a supplied one;
  - a generic base class's chained constructor defaults no longer produce an `InvalidProgramException` in a derived
    class with a different type-parameter frame — a delegation's owner instantiation is read from the call's type
    arguments, since a `: super(…)` is a statement whose own type is `Unit`;
  - the synthetic data-class `copy` is selected by its generated SIGNATURE (parameter names AND types mirroring the
    primary constructor), not by the name alone, so a user-declared `copy` overload cannot be mistaken for it.
  - a default that reads its RECEIVER in a generic callee no longer produces an `InvalidProgramException` at load:
    `class G<T>(val v: T) { fun one(a: T = v) }` left `G`'s positional type variable in a caller frame that has no
    such slot. Splicing a default now closes the callee's WHOLE type frame against the call site — everything a
    default may read (the receiver's property, a member call on it, the receiver inside a generic constructor's
    default, an extension receiver, the callee's own type parameter beside the owner's), not just the omitted
    parameter's own type;
  - a by-reference argument at a call with defaults keeps its position: an address is not a value, so the impure
    values its location is computed from (`byref(mk().f)`, `byref(a[i()])`) are evaluated at the argument's own
    position and the address is taken off those; when the location's root is a CALL, the invocation IS the evaluation,
    so the whole location moves there — into a `ref T` local when its declared type is a byref, else into a plain `T`
    local whose address the slot takes. A value read THROUGH a managed pointer now answers `EmitAddr` with that
    pointer rather than with the address of a copy, which fixes passing a `var x by byref(m())` delegate on to another
    `ref` parameter: `c.Swap(byref(a), byref(b))` swapped two temporaries and dropped both writes — verifiable IL and
    a silently wrong program;
  - a default's TYPE frame is closed whatever its expression reads. The closure was installed only for a default that
    splices one of the call's values, so a default mentioning the callee's type parameters only through TYPES
    (`fun <U> f(xs: List<Pair<T, U>> = emptyList())` in a `class C<T>`) rendered them open in the caller's frame —
    an `EntryPointNotFoundException` at the first call. Constructor delegations and enum entries reached it the same
    way;
  - nested defaults compose their type frames instead of replacing them, so a default filling a default filling a
    default closes every open type variable against the OUTERMOST call site, at any depth;
  - a GENERIC callee's non-constant default no longer arrives with the callee's type parameters open. The
    `@KotlinDefault` carrier holds the default as the callee wrote it, so its type parameters ride it as positional
    type variables; the splice now substitutes the CALL's type arguments into the materialized carrier before binding
    its value tokens, the way an inline body's splice already does. `fun <T> f(xs: MutableList<T> = mutableListOf())`
    omitted as `f<String>()` built a `MutableList<Any>` holding the right values — an `EntryPointNotFoundException`
    where the erased object met the declared slot, and unverifiable IL where it did not;
  - a stack-buffer slot taken BY REFERENCE evaluates its index once. The bounds check and the address computation are
    one access behind a single helper the read and the write share; as two independent pieces they each evaluated the
    index, so `Swap(byref(b[i++]), byref(b[i++]))` incremented `i` four times and swapped the wrong slots;
  - a local pinned out of a by-reference argument's location is always typed. `bir-common/NodeType.cs` is the one
    node-local "what type does this expression produce" derivation, shared with the suspend lowering's spill typing;
    a `kotlin.Any` fallback would box a value type and hide a type the CLR refuses, so an underivable node is
    reported as a hole in the deriver.
  The negative lane gains the shape where the plan genuinely has no CLR form — a byref-like argument at a call whose
  LATER value suspends — and its refusal names the value's source role rather than the minted binding id.
- **bir2cir (area:bir2cir): a suspend function no longer promotes EVERY local to a state-machine field — storage
  is now decided by real liveness, behind one gate, and a value the CLR cannot put in a field is refused at
  compile time instead of crashing at run time.** `CollectVarFields` spilled every `var` in a suspend body into an
  instance field of the generated state-machine class. For a byref-like (`ref struct`) value — `System.Span<T>`,
  `kotlin.clr.Span<T>` from `stackBuffer { … }.asSpan()`, any `ref struct` from a referenced assembly — the CLR
  refuses such a field, so the state machine failed to load with `TypeLoadException` even when the value never
  spanned a suspension, and a byref-like value captured by an ordinary lambda produced the same TypeLoad from the
  closure class. A byref-like PARAMETER failed either way, in a shape-dependent form: `TypeLoadException` when the
  body suspends (the state machine's parameter field), `InvalidProgramException` at the generated cold entry when
  it does not (no state machine is built, so there is no field to reject).
  New `toolchain/bir2cir/SuspendLiveness.cs` runs a precise backward liveness over the normalized body (an
  evaluation-order walk into use/def/susp/label/goto/brIf events, then a worklist solve over the induced CFG,
  with every point in a protected region reaching each catch entry and the region exit). A local needs a field
  iff it is live at a suspension point; everything else stays a `MoveNext` local. It is liveness, not a lexical
  interval: a value created and consumed inside each iteration of a loop whose body also suspends is accepted,
  as C# accepts it, while the same value carried across the loop back edge is refused. The walk also models the
  emitter reordering that makes an unspilled operand read at the RESUME point (`acc + f()` reads `acc` after `f`
  resumes), so an accumulator still gets its field.
  Every field the machine mints — spilled locals, parameters, captures, `$this`/`label`, and the synthesized
  `__aw$`/`__ord$`/`__cond$`/`__awaiter$` temporaries — now goes through a single `FieldStorage` gate, and a
  byref-like or `ref T` type reaching it is a diagnostic naming the declaration, the storage role, the type and
  the suspending callee it lives across. The suspend ABI (parameters, result, suspend-lambda captures) is checked
  unconditionally, since none of it has a "dead across" escape. The same legality oracle
  (`toolchain/bir-common/FieldLegality.cs`) serves the third minting site, `ClosureSynthesis`, so a byref-like
  closure capture is refused too. The three diagnostics mirror C# CS4007, CS4012 and CS8352 and are documented in
  `docs/dotkt-semantics.md` §4d. Two smaller drops closed on the way: an evaluation-order spill whose operand
  type could not be read was silently typed `kotlin.Any` — the spill now types every node kind that carries a
  type, and where a type is genuinely absent it warns, naming the function and the node kind, before falling back
  to `kotlin.Any` as before (the remaining source of untyped operands is InlineSplice, which mints its
  `valueBlock`s without a `type` slot; erroring instead of warning waits on closing that) — and a conditional
  lowered only because a branch ESCAPES no longer mints a field for its result.
- **kotc/bir2cir/facadegen (area:kotc, area:bir2cir, area:facadegen): a declaration with a Kotlin `context`
  parameter is now compiled correctly — previously every such call miscompiled.** Context parameters need no
  opt-in at language version 2.4, so this was reachable from ordinary user source. kotc emitted the context
  parameter into the declaration's parameter list but counted only the `Regular` parameters when building the
  call's argument list, the `sig`/`paramSig` overload key, the inline payload's `pc`, the local-function lift and
  the lambda/delegate parameter projection — so a call passed a SHORT argument list against a longer method. The
  observed failures were `System.InvalidProgramException` at run for `with(c) { g(5) }` (no diagnostic at compile
  time), a silent `null` argument when the context type was generic, `bir2cir: sanity: 'local' references
  undeclared local 'c'` when an omitted default read the context parameter together with a value parameter, and
  `inline splice: cannot splice … no [KotlinInline] payload found` for an `inline` context function. The fix is one
  rule applied at every site: **`Context` and `Regular` parameters are one positional sequence — the emitted
  parameter list, the argument list, the overload key and the `@KotlinDefault` index all count
  `[__self?] + contexts + regulars`** (`docs/dotkt-semantics.md` §5i). The previous refusal for an omitted default
  that reads a context parameter is deleted rather than widened: the default is now bound by symbol to this call's
  context argument, exactly as an earlier value parameter or a receiver already was.
  Fixed across functions (top-level, member, extension, member-extension, companion, interface/`override`,
  `suspend`, `inline`, generic, `vararg`/overloaded, local), properties (top-level, extension, member, companion,
  `var` get+set), multiple context parameters, defaults reading a context parameter alone or together with a value
  parameter and a receiver, lambdas capturing a context parameter, the stdlib's own `contextOf<T>()`, and
  **context function types** (`context(A) (B) -> C` — the lambda's context parameter is a physical delegate
  argument and was dropped from the lifted method while the invoke passed it, a silently wrong result).
  Cross-module, each context slot now carries a `[KotlinContextParameter]` marker (kotc `mods.context` ->
  bir2cir -> facadegen -> the FIR injector), so a consuming Kotlin module restores it AS a context parameter and
  keeps writing `with(scale) { scaled(5) }`; without the marker the same physical method surfaced as a plain
  leading value parameter, a Kotlin SOURCE break at the module boundary. `docs/user/kotlin-on-clr-differences.md`
  and `docs/user/supported-features.md` claimed context parameters were rejected outright; both are corrected.
  A context FUNCTION TYPE (`context(A) B.(D) -> E`) round-trips too, which needed its own carrier: fir2ir ERASES
  which of a function type's leading arguments were contexts (at IR level `context(A) B.(D) -> E` is *identical* to
  `B.(A, D) -> E`), so bir2cir stamped `[KotlinExtensionFunctionType]` and facadegen promoted the delegate's first
  argument — the CONTEXT — to the restored receiver. A consumer's `evaluate { this.n }` then read the context's field
  instead of the receiver's and returned the wrong number with no diagnostic. kotc now captures the arity from FIR
  before it is dropped (`kotc.frontend.ClrContextFnTypes`) and carries it as the slot fact `ctxFnType`/`retCtxFnType`
  -> `[KotlinContextFunctionType(N)]` -> facadegen's context split -> the injector's `ContextFunctionTypeParams` cone
  attribute. A lambda LITERAL of such a type needed one more fix: the lift had no binding for the lambda's own
  receiver, so the body's `this` fell through to `{k:this}` — the enclosing instance, or nothing in a static lift. It
  now mints a name for the receiver parameter and binds `this` to it, as the inline splice carrier already did. That
  ALSO closes the context-free sibling, where the same gap made `val f: Int.(Int) -> Int = { d -> this + d }` throw a
  NullReferenceException; both lift shapes (non-capturing static and capturing closure) are covered.
  The arity is keyed by the slot's file path and its END source offset — the one offset FIR and IR always agree on —
  recorded from a DECLARATION-only walk (never a body, initializer or default value, because a callable nested in an
  expression body ends where its enclosing declaration ends and the two arities would land on one key). Carried for
  the stdlib's own `kotlin.context(...)` family too, which compiles through the other frontend phase. The table is
  cleared once per pipeline execution: it hangs off an object, so a HOSTED kotc could otherwise read a previous
  compilation's entry — a latent hazard closed by construction, not a bug that was reproducing (today's launcher
  execs a fresh JVM per invocation and each pipeline runs once).
  Also fixed in the same family: a cross-module MEMBER (and member-extension) call never emitted a positional
  `defaultArg` placeholder for an omitted default — it built `args`/`argTypes` from the expressions that happened to
  be present, so the omitted slot was DELETED and a later provided argument slid into it (`h.pick(b = 3)` bound `3`
  to `a` and zero-filled `b`). Those paths now use the same positional filler and carry `sig`, and the
  `@KotlinDefault` metadata lookup — previously constructors and top-level functions only — covers members.
  Regression coverage: `tests/basic/fixtures/ContextParameterTests.kt`,
  `tests/coroutines/fixtures/SuspendContextParameterTests.kt`, and `crossModuleContextParameters` in
  `tests/roundtrip/consumer/KotlinMetadataRoundtripTests.kt` over `tests/roundtrip/producer/Ctxparams.kt`.
- **kotc/bir2cir/ilemit ([tmyt/dotkt#68], area:kotc, area:bir2cir, area:ilemit): a captured `var` written from a
  local class, an object expression, a lambda or a local `fun` is now heap ref-celled under EVERY emission root and
  for every one of those boundaries — previously only a function body, and never a local `fun`.** The promotion of a
  captured-and-mutated local to a shared `dotkt$Ref<T>` was decided per emission root, and only two roots decided it —
  `method()` and `topLevelAccessorMethod()`. Everywhere else the emitter ran with an empty set, so a constructor body,
  an `init` block, a property or field initializer, a member accessor, a default interface method, a static-field
  initializer, a rich-enum entry argument and a `@KotlinDefault` default-value expression each aborted the compile
  ("does not support an object expression / a local class that writes to a captured outer variable") — while the same
  shape written with a LAMBDA hit no guard at all and emitted a `setLocal` against a local that does not exist in the
  lifted closure, which bir2cir then rejected as an undeclared local. Needing a cell is a property of the VARIABLE,
  not of the frame emitting it, so the set is now computed ONCE per module (`BirEmitter.initRefCells`, driven by
  `ClrBackendPhase`) and is identity-keyed; the per-root save/restore is gone with it. This also makes the two frames
  that emit ONE default-argument expression — the callee's `@KotlinDefault` carrier and the omitting call site —
  agree. The two guards that used to reject the shape now report a broken emitter invariant instead of an unsupported
  language construct (they can only fire on a mutated capture that is not a `var` local, which valid frontend IR
  cannot produce).

  A local `fun` is now a capture boundary too. It lifts to a static method whose captures are BY-VALUE parameters, so
  `fun f(): Int { var n = 0; fun bump() { n++ }; bump(); bump(); return n }` compiled clean and returned 0 instead of
  2 — the only boundary whose missing cell lost the write with no diagnostic. Reaching a capturing local fun from
  another boundary failed loud, since the lift supplies its captures at the CALL SITE. Closing both halves took: one
  capture scan shared by the lambda/local-fun and object/local-class walks, which follows a call INTO a local fun and
  a CONSTRUCTION of a local class (cycle-guarded), so a lambda, object expression or local class that merely calls
  `bump()` captures `bump`'s `n` too; a local declaration recognized by the frontend's own `Local` visibility rather
  than by probing its IR parent, which is exact for a class as well and needs no `init { }` special case; both lifts
  RESTORING the enclosing frame's capture binding instead of dropping it, so a local fun or local class declared
  inside a closure/object/local-class member no longer leaves that frame reading a bare local it does not have (in an
  `inner class` member, dropping it left every LATER member reading the enclosing instance off the inner one); a
  capture keeping its own name unless something else already owns it in the same namespace — the lifted class's own
  fields and constructor parameters, the lifted local fun's own value parameters, or an earlier capture — in which
  case it moves into a `cap$` prefix; the lift also generic over the type operands in its BODY, like the lambda lift,
  and over the BOUNDS of the type parameters it re-declares; kotc recording on the lifted method which enclosing type
  variable each of its own type params re-declares (`_syntheticTypeArgs`, the same key and meaning ClosureSynthesis
  already derives for a lifted closure CLASS) and bir2cir's `SharedSyntheticSynthesis` applying its synthetic-frame
  remap to METHODS as well, so a generic cell used inside the lift is constructed in the method's own parameter space
  and a lifted synthetic can supply the parameter constraints when the celled `var` is declared inside it; that binder
  running collect/bind/construct in separate passes, so its result no longer depends on declaration order; and
  ilemit's `setField`/`staticFieldSet` taking their owner through `ParseOwnerSlot` like the field READ paths beside
  them instead of collapsing a constructed-generic owner to its open name — writing a `Cell<T>` field from a generic
  static method emitted `Cell<!0>::v` where the frame has only `!!0`, which the runtime rejected as a bad image.

  A lifted class also keeps the BOUNDS of the enclosing type parameters it re-declares (they were emitted as bare
  names, so a member needing one had no constraint to dispatch through — wrong metadata, no diagnostic). A local class
  INHERITING from a capturing local class, and a `this(...)` delegation between two constructors of one, both forward
  the captures ahead of the source-level arguments. Ref-cell identity keys on the bounds of the variables its element
  mentions, not the printed element alone, so two same-file generic classes no longer share one cell and one of them
  its constraint. `::localFun` lowers through the lifted static — a plain delegate with no captures, and a closure
  over the captured values when there are some, including the enclosing instance and enclosing generic parameters.
  Callable references participate in the same transitive declaration-reachability scan as calls and constructions;
  a capturing local-class constructor reference binds the hidden lifted-ctor arguments in a closure while preserving
  its Kotlin-visible arity. Finally, every function-local variable is assigned a BIR frame slot by IR declaration
  identity rather than source spelling, so shadowed locals and two distinct captures with the same name cannot alias
  in ilemit's flat local table; capture names (including the preferred `__outer`) are collision-free bindings rather
  than downstream semantic markers. Suspend-lambda captures likewise use compiler-only descriptor bindings and carry
  their enclosing-frame value explicitly, so the body and construction site never depend on sharing a source name.
  With declaration identity preserved in BIR, bir2cir's former `DisambiguateShadowedVars` JSON scope reconstruction
  has been deleted; coroutine lowering now only spills the slots the frontend actually declared.

  Regressions: `tests/basic/fixtures/CapturedVarRefCellTests.kt` and the asynchronous shadowed-slot case in
  `tests/coroutines/fixtures/SuspendCaptureTests.kt`; the function-body root stays pinned by `LambdaTests.kt`'s
  `localClassObject`. The two `tests/known-fail/localfun-capture-write*` reproductions this replaced are deleted.

- **kotc (area:kotc): a default argument that reads the DISPATCH receiver of a member EXTENSION function no
  longer reads it off the extension receiver.** `filledArgs` collapsed the call's receivers to one expression
  (`extensionReceiver ?: dispatchReceiver`) and bound *every* receiver parameter to it, so a callee with two
  receivers got the wrong one: `class Host(val k: Int) { fun Int.scaled(f: Int = k) = this * f }` emitted the
  filled default as `Host.get_k()` on the extension receiver's value — `"recv": {"k":"const","type":"kotlin.Int",
  "value":3}` in the BIR — which bir2cir and ilemit projected faithfully into a `Host` member call on an `Int`:
  a `NullReferenceException` at runtime with nothing loud at compile time. Each receiver parameter now binds to
  the call's receiver of its own kind, and the enclosing-`this` chain hangs off the dispatch receiver rather than
  the collapsed one (an inner class's member extension reading `this@Outer` failed identically). Only a receiver
  the default actually READS is emitted, so an unread receiver's lifted lambdas cannot leak into the file class.
  This is the same-module substitution path only: the cross-module `@KotlinDefault` carrier still refuses a default
  that reads the DISPATCH or an enclosing-instance receiver (a pure extension-receiver default is carried and
  filled), and a cross-module data-class `copy` still evaluates a non-stable receiver once per omitted field —
  both unchanged here. The value-param arm (`f: Int = base`) and the single-receiver top-level
  extension arm (`fun Int.f(x: Int = this)`) are unchanged and pinned. Covered by
  `tests/basic/fixtures/DefaultArgumentTests.kt` `defargsReceiverKind`.
- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir, area:ilemit): calls into referenced Kotlin helpers
  now carry and link the physical declaration signature instead of falling back to a same-name/same-arity method.**
  bir2cir preserves the frontend Kotlin descriptor before nullable-generic erasure, resolves the referenced
  declaration after owner attribution, then carries that declaration through actual/type-alias lowering; synthesized
  alias helpers likewise carry their flattened method type-variable scope. ilemit consumes that physical signature
  exactly. In particular,
  `Collection<T>.maxOrNull()` from inside a generic function keeps its `IEnumerable<T>` descriptor and cannot
  silently bind the neighboring `IEnumerable<Double>` overload. A missing descriptor match is now a link-time ABI
  error; the former standalone known-failure is an in-process NUnit regression covering concrete, value-generic,
  and reference-generic calls.

- **bir2cir ([tmyt/dotkt#251], area:bir2cir): constructor parameters now carry their `[Nullable]` annotation, so a
  nullable ctor parameter stays nullable across a module boundary.** The declaration-position NRT walk
  (`toolchain/bir2cir/DeclNullableFlags.cs`) visited methods, fields, properties and nested types but never `ctors`,
  while the stamp that turns those bytes into `[Nullable]` already walked constructor parameters — so every
  constructor parameter was emitted unannotated. A Kotlin consumer of a DotKt library therefore saw `C(val s:
  String?)` as taking a non-null `String` and `C(null)` failed to compile with "null cannot be a value of a non-null
  type"; a C# consumer lost the annotation outright. The walk now covers constructor parameters, matching the
  declaration kinds the stamp traverses. Regressions:
  `tests/roundtrip/producer/Nrt.kt` + `tests/roundtrip/consumer/KotlinMetadataRoundtripTests.kt`
  (`nullableConstructorParams`, cross-module Kotlin) and
  `tests/roundtrip/bidirectional/consumer/BidirectionalTests.cs`
  (`KotlinNullableConstructorParameterCarriesNullableAttribute`, the C# metadata assert).

- **bir2cir ([tmyt/dotkt#228], area:bir2cir): an auto-property no longer emits a same-named field, so
  reflection-driven .NET libraries (JSON.NET) can serialize a Kotlin type again.** An accessor-routed property
  emitted its backing field under the property's own name, so the CLR type carried `Value` as BOTH a property and a
  field; Newtonsoft groups candidate members by name and could not resolve the pair — `JsonConvert.SerializeObject`
  silently produced `{}` and deserializing back threw on the null constructor argument. The backing field is now
  emitted as `<Value>k__BackingField` (the C# auto-property convention) with
  `[System.Runtime.CompilerServices.CompilerGenerated]`. The name is unwritable in Kotlin — even backtick-quoted, the
  frontend rejects it with "name contains illegal characters: <>" — so it can never collide with a user declaration.
  The rename lives in bir2cir (`toolchain/bir2cir/BackingFieldRename.cs`), which owns the Kotlin↔CLR representation;
  kotc keeps emitting the pure Kotlin identity. Only accessor-routed properties are affected (including one with a
  custom accessor over a backing field) — `lateinit var`, `const`, a delegated `p$delegate`, companion/top-level
  statics and the `@ClrField` opt-out emit no CLR property and keep their plain field name. An `@JvmInline value
  class`'s erased-value getter is now read off the property that owns the single instance field instead of being
  spelled from that field's name. Documented in `docs/dotkt-semantics.md` §5h and
  `docs/design-clr-property-model.md`; regression in
  `tests/interop/consumer/fixtures/AutoPropertyBackingFieldTests.kt`.

- **bir2cir/facadegen ([tmyt/dotkt#147], area:bir2cir): nested nullable-generic shapes now round-trip through every
  declaration slot.** `[KotlinNullableGeneric]` records the pre-erasure Kotlin `Holder<T?>` shape on method and
  constructor parameters, fields, and properties as well as returns; facadegen restores that shape before NRT
  composition, including ordinary function-type trees and public interface bridges synthesized after erasure. Restored
  user types keep their namespace-qualified identity. A separately compiled Kotlin consumer now retains generic
  inference and member types instead of seeing `Any?`, while metadata regressions cover raw fields and same-simple-name
  types directly. Bare top-level `T?` remains the distinct dual-representation work tracked by #86.
  
 - **packaged-SDK gate ([tmyt/dotkt#250], area:packaging): each `dotnet new` template case now gets its own scratch
  hive, so concurrent worktree gates no longer false-RED each other.** `tests/packaged-sdk/run.sh` isolated NuGet
  state but installed/uninstalled `DotKt.Templates` in the machine-global template store under `$HOME`. Two
  `make verify` runs in different worktrees carry the same package id at the same version, so one run's `--force`
  reinstall or its cleanup uninstall landed between the other's install and its scaffold — reported as
  `ThrowMoreThanOneMatchException` / "Could not find the template package containing template 'DotKt.Templates.Cli'"
  on a gate that had nothing wrong with it. Every `dotnet new` invocation now goes through a `dotnet_new` helper that
  passes `--debug:custom-hive` pointing at the case's own hive inside the run's scratch workspace, which is wiped at
  the start of each run: nothing is installed into or uninstalled from the machine-global store any more, and the
  uninstall trap that existed only to keep it clean is gone with it. The hive is per case rather than per run because
  installing a package a hive already carries is either a hard error or, with `--force`, a second registration for the
  same id that makes every later scaffold ambiguous with the very same error — which the old cross-case uninstall had
  been hiding. `--force` goes with it, since a fresh hive never has anything to force over. Each case also asserts
  that the packed nupkg really landed in its own hive, so an SDK that silently stopped honouring the switch cannot
  restore the race behind a green gate.

- **kotc ([tmyt/dotkt#235], area:kotc): a constructor call may omit a default argument whose value reads an earlier
  constructor parameter — `class Rect(val w: Int, val h: Int = w * 2)` + `Rect(3)` compiles and yields `h == 6`.**
  kotc carried two default-argument filling passes: the one behind ordinary calls already inlined such a default with
  each referenced parameter rewritten to that call's filled argument, while the one behind `new` / array constructors /
  a lifted local class's `new` refused the identical shape. The constructor path is folded onto that pass, so one
  implementation now serves both. Folding takes the UNION of the two passes' behaviors: constructor arguments gain the
  call path's arg-slot coercions (byref shaping, `Nullable<T>` unwrap, boxed-`Any` narrowing), calls gain the
  constructor path's facadegen constant-default fill (#134) and its loud refusal when an unfillable cross-module
  default is omitted with a LATER argument provided (that slot-shift was previously a silent miscompile on the call
  path). The remaining constructor call sites are routed through the same pass with it: a `: this(…)` / `: super(…)`
  **delegation** and an **enum entry**'s `NAME(args)` (including a per-entry body's base call) dropped an omitted
  default's slot outright, sliding every later argument one place down — `class D(val a: Int, val b: Int = a * 2) {
  constructor() : this(3) }` produced an unloadable `D..ctor()`. A constructor default that reads an **enclosing
  instance** (`inner class In(val x: Int = outerProp)`, and the same default on a member of an inner class) now lowers
  too: the enclosing `this` binds to the call's dispatch receiver, a member call and each further level through the
  `__outer` capture field (it previously emitted the *caller's* `this` and threw `InvalidProgramException`). Every
  binding is now made BY SYMBOL rather than by rewriting the emitted `this` token, which also fixes an argument that
  reads `this` being re-pointed at the call's receiver — `class D { val k = 5; val c = C(7); fun f() = c.m(k) }`, with
  `C.m(a: Int, b: Int = a * 10)`, filled `b` from `c.k` by emitting `D::get_k` against a `C` receiver. Filling saves
  and restores the substitutions it installs, so a closure that captured the callee's own parameter keeps its binding
  across a recursive omitting call; a delegation's args read the leading capture PARAMS, which (unlike the capture
  fields) are already live before the constructor body.
  **A value a same-module filled default splices is now evaluated exactly ONCE**: the receiver (or enclosing instance) a
  `= this` / `= outerProp` default reads, and the earlier argument a `= a * 10` default reads, are bound to a call-site
  temporary in a wrapping `valueBlock`, so `mkOuter().In()` runs `mkOuter()` once — the constructor and its default now
  see the SAME instance — and `f(next())` calls `next()` once however many defaults read it. A stable value (a literal
  or an immutable local/parameter read) still splices directly, so an ordinary call emits no temporary, and every
  non-stable value to the left of a bound one is bound with it so the evaluation order stays Kotlin's. This holds at
  EVERY site that fills a default: a constructor delegation and an enum entry ride a declaration rather than an
  expression, so their temporaries are declared by the first argument. **Cross-module omissions bind too**
  (area:bir2cir): `DefaultArgSplice` used to deep-clone the call's receiver/argument into the default it filled, so
  `sideEffect().substringAfter(".")` ran `sideEffect()` twice and a value two defaults read ran four times — it now
  hoists as it fills, including a filled default a later default reads (`chain(a, b = bump(), c = b * 10)` calls
  `bump()` once). Working on emitted JSON it cannot tell a `val` from a `var`, so there only a literal or `this` stays
  spliced in place (`docs/dotkt-semantics.md` §7).
  **Cross-module too** (area:bir2cir with it): a CONSTRUCTOR now carries the same `@kotlin.clr.KotlinDefault` stamp a
  function does, facadegen surfaces its non-constant defaulted parameter OPTIONAL, and `bir2cir.DefaultArgSplice` fills
  the `{"k":"new"}` placeholder from the reference dll — so a consumer of a DotKt library can write `Rect(3)` against
  `class Rect(val w: Int, val h: Int = w * 2)` and get `h == 6`, where it previously failed the frontend with *no value
  passed for parameter 'h'*. The splice keys a constructor as `<type>|.ctor|<declared parameter count>`
  (`ReferenceMetadataIndex` now scans `GetConstructors` alongside `GetMethods`), and the stamped index is the parameter's
  position in the emitted constructor's own parameter list. Both constructors and methods are additionally keyed by the
  declared parameter VECTOR, so two same-arity overloads carrying different defaults resolve their own instead of the
  arity key serving whichever declaration the metadata scan reached first; a pair the key cannot separate is refused. A
  cross-module `: super(…)` is filled too — its arguments ride the constructor declaration rather than a call node, so
  they were previously left unfilled and aborted the build; lacking a signature vector there, the target constructor is
  identified by arity alone (a base with same-arity constructor overloads whose defaults disagree is refused).
  Lower slots fill first, so a chain fills too
  (`class Tri(a, b = a + 1, c = a * 100 + b)` consumed as `Tri(2)` gives `c == 203`). A metadata-representable (Tier-1)
  constant still fills from the facadegen metadata and never becomes a placeholder; a ctor default reading an enclosing
  instance is still refused at stamp time. Covered by `tests/basic/fixtures/DefaultArgumentTests.kt` (`defargsCtor`,
  `defargsCtorDelegation`, `defargsCtorEnclosingInstance`, `defargsSingleEval`, `defargsEvalOrder`) and by the round-trip
  lane's `nonConstDefaultArgs` (`tests/roundtrip/producer/Nonconst.kt` + `tests/roundtrip/consumer/`).

## 0.9.7 (2026-07-22)

### Added

- **CLR event model — a Kotlin class can now IMPLEMENT and RAISE a .NET interface event ([tmyt/dotkt#187],
  [tmyt/dotkt#113], area:kotc, area:bir2cir, area:ilemit): first-class WPF/Avalonia/WinUI MVVM
  (`INotifyPropertyChanged`) on Kotlin-on-CLR.** `class ViewModelBase : INotifyPropertyChanged { override val
  PropertyChanged by clrEvent() }` now compiles to a loadable type: kotc synthesizes `add_/remove_/raise_<E>` accessors
  + a backing delegate field; bir2cir (`ClrEventImplBinding`) resolves the concrete `EventHandlerType` delegate off the
  ref.dll and lowers the accessor bodies; ilemit emits the C# field-like-event shape (a lock-free
  `Delegate.Combine/Remove` + `Interlocked.CompareExchange<D>` CAS loop for add/remove, `field?.Invoke(args)` for raise)
  + the `.event` metadata, and the synthesized accessors satisfy the interface `add_/remove_` slots. `kotlin.clr.ClrEvent<T>`
  is now an **abstract covariant marker with a private ctor** (non-constructable, non-subclassable); the interface event
  member is emitted OPEN, so a missing `by clrEvent()` on a direct interface implementer is a real compile diagnostic (a
  kotc emission-time check — an abstract member is unsatisfiable when a .NET base explicitly implements the event with a
  different-signature same-name public event, so it would wrongly break the `class MyApp : Avalonia.Application` ELIDE
  case). **RAISE-from-outside** is a deliberate CLR-native deviation (interop-first): `vm.E.invoke(sender, args)`
  is legal from any type via a public synthesized `raise_<E>` (`docs/dotkt-semantics.md` §8d). #113: all event emit routes
  through guarded resolution — a missing/value-type/constructed-generic event owner gives a legible `ilemit:`/`bir2cir:`
  breadcrumb instead of an opaque NRE. Design: `docs/design-clr-event-model.md`. (Class-delegation event forwarding #186
  is deferred to 0.9.8.)

### Fixed

- **bir2cir (area:bir2cir): `dotkt` and `dotkt.*` are ordinary user namespaces again.** Reference resolution still
  skipped the namespace once used by the retired pre-stdlib compiler-intrinsics runtime, even though only the
  unspeakable `dotkt$...` generated-type prefix remains compiler-owned. A cross-module Kotlin library under
  `dotkt.foo.bar` could therefore compile cleanly but be mis-bound at runtime. `ResolveNetType`/`ResolveRefType` now
  reserve only `dotkt$...`; the round-trip regression captures and mutates a referenced generic value from the exact
  `dotkt.*` namespace through a stored delegate.

- **bir2cir ([tmyt/dotkt#140], area:bir2cir): an asynchronously faulted or canceled `suspend fun main` now surfaces
  the raw await exception instead of `AggregateException`.** The synthesized blocking entry point drains its root
  `Task<Unit>` with `GetAwaiter().GetResult()` rather than `Task.Wait()`, matching the coroutine bridge design and
  normal .NET await semantics. A process-level regression verifies the asynchronous fault path.

- **bir2cir ([tmyt/dotkt#125], area:bir2cir): non-segmentable suspend lambdas now fail loud at invocation instead of
  emitting invalid IL.** `newSuspendLambda` uses the same structural classifier as named suspend functions; a
  suspension in `finally`, a suspending `catch` paired with `finally`, or a nested suspending `try` produces a valid
  `SuspendLambda` state machine whose `invokeSuspend` throws an explanatory `NotSupportedException`. The normal
  capture and `create()` protocol remains intact. Coroutine regressions drive all three shapes and require the
  call-time diagnostic rather than `InvalidProgramException`.

- **bir2cir ([tmyt/dotkt#98], area:bir2cir): a counted range loop now resumes inside every iteration instead of
  hoisting its suspension before the loop.** SuspendColdLowering flattens app-build `for (i in a..b)` and
  `for (i in a downTo b)` nodes into state-machine CFG before segmentation, spilling the counter as an SM field and
  preserving `break`/`continue` targets. This fixes both the silent “suspend once, execute the body N times” result
  and the unresolved loop variable failure when the resumed expression reads `i`. Coroutine regressions cover
  suspension count, ascending ranges, descending ranges, runtime results, and ILVerify.

- **facadegen ([tmyt/dotkt#205], area:facadegen): a generic .NET interface that derives a member-bearing
  NON-generic base interface now surfaces the base as a supertype, so its INHERITED members resolve and the
  generic interface stays assignable to the base.** `interface IReader<T> : IPingable` used to surface `IReader`1`
  with NO super edge (`InterfaceSuperTypes` emitted only GENERIC direct supers), dropping `IPingable.Ping` — a
  consumer's `reader.Ping()` gave `unresolved reference 'Ping'` and `IReader<Doc>` was not assignable to
  `IPingable`. Fix: `InterfaceSuperTypes` now also emits the namespace-qualified (#199 reference-token rule)
  non-generic direct super edge, dropping only the legacy same-simple-name generic shadow (`IEnumerable` beside
  `IEnumerable<T>`), mirroring the class path's `genericNames` guard. General to any generic BCL/user interface.
  New regression: `tests/interop` `ifacebasegen`. This is the narrow genuine follow-up found during #205; the issue's
  broader provenance false-positive was fixed separately by PR #201.

- **compiler ([tmyt/dotkt#203], area:kotc/ilemit): callable references now bind same-owner overloads by resolved
  parameter signature.** `calleeOwner` fixed package selection for `::foo`, but `newDelegate` and
  `newBoundDelegate` still performed a name-only `ldftn` lookup inside the selected file class / declaring class, so
  `(Int) -> String` and `(String) -> String` references could bind the same overload. kotc now carries the same
  structured `sig` used by ordinary calls on top-level and bound references (and on the inner call of an unbound member
  forwarder); ilemit consumes it in both normal and event-handler delegate construction. The signature contains type
  facts only, so bir2cir's declaration/slot name rewrites cannot stale it. Regression coverage exercises top-level,
  bound-member, and unbound-member overload pairs.
- **facadegen ([tmyt/dotkt#202], area:facadegen): generic method overrides no longer make
  `MetadataLoadContext.GetMethods` skip otherwise valid types and awaitable candidates.** The runtime's inherited-member
  suppression can call `GetGenericTypeDefinition()` on a generic parameter and throw for a non-generic derived type,
  producing 90 duplicate `skipped type` / `skipped awaitable` warnings in the NUnit-backed interop build. facadegen now
  falls back to derived-first, declared-only hierarchy enumeration for that reflection failure. A C# generic-override
  producer and Kotlin consumer call cover both metadata injection and dispatch; the same clean build now emits zero
  warnings.
- **bir2cir ([tmyt/dotkt#200], area:bir2cir): nested suspend lambdas inside a materialized inline carrier now retain
  their transitive captures.** A deep inline `Flow.transform`/`filter` chain could leave the nested lambda's
  `predicate` capture off the enclosing suspend state machine; synthesized-name ordering then decided whether ilemit
  saw a valid SM field or an unspilled local. InlineSplice now promotes current-frame nested-SM capture dependencies
  into the outer carrier, making spill ownership independent of declaration names. The coroutine flow-transform
  fixture uses the formerly failing prefixed declaration shape as the regression guard.
- **facadegen / roundtrip ([tmyt/dotkt#205], area:bir2cir/ilemit/facadegen): DotKt assembly provenance is now
  explicit instead of inferred from a namespace name.** DotKt output carries assembly-level
  `[AssemblyMetadata("DotKt.Compiler", "metadata-v1")]`, and every embedded
  `DotKt.Runtime.CompilerServices.*Attribute` definition is also `[CompilerGenerated]`; facadegen requires both before
  reading Kotlin metadata or applying collection reverse maps.
  An ordinary C# assembly containing a same-full-name lookalike therefore stays ordinary C#. The `il-tloverload`
  regression now uses two real Kotlin producer files and round-trips their genuine file facades, replacing the C#
  stand-in attribute that caused PR #201's shared producer DLL to be misclassified.
- **compiler ([tmyt/dotkt#199], area:kotc/bir2cir/ilemit): two same-simple-name top-level functions in DIFFERENT
  packages (`a.foo`/`b.foo`) now dispatch to their OWN package's body instead of a global first-match. Root: the
  `callStatic.owner` slot overloads two concepts — `owner:null` is the load-bearing "top-level call" axis that ~12
  bir2cir recognizers key on (`@ClrIntrinsic`/collection/array-factory substitution, Precondition/Repeat/Enum/ForIn/
  CharSeq lowerings), so the earlier fix of stamping the file-class on `owner` silently disabled substitution and
  broke `make stdlib` (`clrTimestamp` et al. reached ilemit unresolved). Fix (Design B): split the axes — `owner`
  keeps its meaning (`null` = top-level, UNTOUCHED) and a NEW advisory `calleeOwner` carries the FIR-resolved callee
  file-class DISPATCH hint (mirrors `sty`); the bir2cir owner-null machinery ignores it, only ilemit's `callStatic`
  dispatch consults it (falling back to the global `FindStatic` on a hint miss). Covers the non-suspend path, the
  suspend cold-lowering (the hint rides the rewrite + synthesized cold-entry/Task-bridge calls stamp it), and the
  top-level extension-property accessor. Regression fixtures: tests/il `XPkgSameNameFunTests`, tests/coroutines
  `SameNameAcrossPackagesTests`.**
- **compiler ([tmyt/dotkt#199], area:kotc/ilemit): a `::foo` function-REFERENCE delegate over a top-level fun now
  dispatches to its OWN package's body — the delegate analogue of the same-simple-name call bug above. kotc emitted
  the bare-name `newDelegate method:foo` (dropping the FIR-resolved callee file-class) and ilemit bound it by global
  first-match `FindStatic`, so two same-simple-name top-level funcs across packages (`a.foo`/`b.foo`) both bound to
  the first. Fix: extend Design B's `calleeOwner` DISPATCH hint to `newDelegate` — kotc stamps the callee file-class
  when the target is a top-level fun (`owner` stays absent, the substitution axis unchanged), and both ilemit
  newDelegate binding sites resolve `FindMethod(calleeOwner, name) ?? FindStatic(name)` (global fallback on a hint
  miss). Lifted `__lambdaN`/`__ctorref`/`__mref`/adapter forwarders (unique names, no `calleeOwner`) keep the plain
  `FindStatic` path, unchanged. Regression fixture: tests/il `XPkgSameNameDelegTests`.**
- **compiler ([tmyt/dotkt#199], area:kotc): the Design-B `calleeOwner` dispatch hint is now also stamped on a LIFTED
  LOCAL function call — completing the rule that EVERY `owner:null` `callStatic` carries its FIR-resolved dispatch
  owner.** A local `fun` is lifted to a static `__local<n>_<fn>` in the current file's file class; its call site emitted
  `owner:null` with no dispatch hint, leaving ilemit to resolve via global first-match `FindStatic`. It now carries
  `calleeOwner = <current file class>` (used directly, since a local fn's parent is the enclosing function, not an
  `IrFile`, so the `calleeOwnerTag` gate excludes it). This is a **defensive** hardening, not a reproducible-bug fix:
  `__local<n>`'s `<n>` is `scopeCounter`, which is monotonic across all files in one kotc invocation, and every
  canonical build is one invocation per assembly — so two `__local<n>` with the same `<n>` never coexist in an assembly
  and the global `FindStatic` resolves correctly today. A mis-dispatch is reachable only by linking BIR from two
  SEPARATE kotc invocations into one assembly (no canonical path does this). It is the method-dispatch analog of the
  `synthScope` per-file prefix already applied to synthetic closure TYPE names. No fixture is added — a single-`.ktproj`
  fixture compiles in one invocation, gets unique `__local<n>` names, and would pass with AND without the fix (a fake
  guard).**
- **facadegen ([tmyt/dotkt#199], area:facadegen): a re-imported/injected type REFERENCE to another type that shares
  its simple name with a type in a DIFFERENT namespace now carries the NAMESPACE-QUALIFIED name, so the injector
  resolves the EXACT type instead of the by-simple-name last-wins collision.** Two symptom families are fixed: ① a
  GENERIC reference (a factory RETURN `a.State<T>`, a `var` PROPERTY type, a generic supertype) was emitted as the bare
  `State` — collapsing `a.State<T>` and `b.State<T>` to one, so a factory's return / a var's type resolved to the WRONG
  package's type (var mutability + members degraded); the generic-reference paths (`CrossTypeT`/`CrossTypeTN`), the
  self-reference short-circuits, and the enum self-type now qualify with the namespace. ② a NON-generic base class and
  interface supertype was emitted as the bare simple name, so a subclass of `Inherit.Widget` could bind to a
  same-named `Ext.Widget` whose missing no-arg ctor CRASHED kotc's `generateConstructors`
  (`No arguments constructor for class Ext/Widget not found`); `SuperTypes`/`ClassInterfaceSuperTypes` now emit the
  qualified name. Nested base types key on `namespace + simpleName` (matching the injector's `+`-stripped ClassId), so
  the nested edge resolves too. Regression fixtures: `tests/roundtrip/producer/Genclash{A,B}.kt` (two same-simple-name
  generic `Cell<T>` across packages) and `tests/interop/producer/Extlib.cs` (`Ext.Widget` restored to collide with
  `Inherit.Widget`).
- **bir2cir ([tmyt/dotkt#138], area:bir2cir): `KClass.simpleName`/`qualifiedName` now report the KOTLIN name for a
  statically-known `::class`, not the .NET reflection name.** `1::class.simpleName` was `"Int32"` and
  `.qualifiedName` `"System.Int32"`; `"x"::class.qualifiedName` was `"System.String"` — the accessors were wired
  straight through to `System.Type.Name`/`.FullName`. `KClassMemberBinding` runs before `BirTypeLowering`, where the
  receiver's type slot is still a pure Kotlin FQN, so it now const-folds the accessor to the Kotlin name
  (`qualifiedName` = the FQN, `simpleName` = its last `.`-segment) for both an unbound `Int::class`/`Foo::class`
  (`classRef`) and a bound `1::class`/`"x"::class` on a known-final builtin (`getType` — a final type's runtime class
  == its static type). Fixes the primitive tower + String; a genuinely-dynamic `x::class` on an open/interface static
  type keeps the run-time read (a sequenced stdlib follow-up — see `docs/dotkt-semantics.md` §5g).
- **kotc ([tmyt/dotkt#184], area:kotc): a .NET attribute with a `params` (varargs) constructor parameter can now be
  applied bare (zero args) from Kotlin.** The injected annotation class constructor was not marking `params array`
  parameters as vararg — `ClrTypeInjection.generateConstructors` iterated all params with a plain `valueParameter`
  call that ignored `p.vararg`, so a `params object[]` ctor parameter surfaced as REQUIRED rather than omittable.
  The same `if (p.vararg) … isVararg = true` branch already used for injected method parameters is now applied to
  constructor parameters too. `@TestFixtureAttribute` (and any attribute whose sole ctor is `params T[]`) can now
  be applied bare (`@TestFixtureAttribute` with no args → empty array) or with arguments, matching C#/CLR semantics.
  Gated by `il-netattr-vararg`.
- **bir2cir ([tmyt/dotkt#189], area:bir2cir): a nullable-REFERENCE-returning lambda bound into a delegate
  (`Api.RunNullable(Func<string?>) { null }`) is now ilverify-clean.** `NullableFuncReturnErasure` erased EVERY
  nullable func-return `(…) -> R?` to `object`, so the lifted lambda's ret became `object` while the concrete delegate
  slot's `Invoke` returns `string` — `object` is not assignable-to `string`, so `newobj Func<string>::.ctor(ldftn object …)`
  failed ilverify `DelegateCtor` (runtime-safe; the ctor is not JIT-verified). The erasure to `object` is only needed
  when a plain reference cannot carry the null — a VALUE-type inner (`Int?`) or an unconstrained open generic (`T?`);
  a REFERENCE inner (`String?`) already carries null and now stays the (nullable-stripped) reference type, keeping the
  lifted return covariantly-assignable to the delegate slot (ECMA-335: the ldftn target's return must be
  assignable-TO the delegate `Invoke` return). Gated by `isValueFqn`; distinct axis from #170's String->CharSequence
  delegate-return bridge. Prunes the `il-delegnull` `XFAIL_ILVERIFY` entry.
- **toolchain ([tmyt/dotkt#51], area:ilemit, area:packaging): reference-asset selection now keys off the
  TARGET RID, not the build HOST RID — cross-target builds pick the right `runtimes/<rid>/lib` asset.** The core
  slice made `ManagedReferenceCatalog` rank RID-impl assets against the target RID's portable-RID-graph `#import`
  closure (NuGet `RuntimeGraph.ExpandRuntime`), but nothing passed the target RID *in*, so ilemit still selected on
  the host RID. This completes the wiring: ilemit gained `--target-rid` / `--rid-graph-path`, `RuntimeReferences.Load`
  forwards both to `ManagedReferenceCatalog.Create`, and the shipped MSBuild pipeline
  (`DotKt.Toolchain.targets`, imported by both the packaged SDK and the in-repo `cases/KotlinClr.targets`) passes
  `$(RuntimeIdentifier)` + `$(RuntimeIdentifierGraphPath)` on the ilemit `Exec`. ilemit is the sole consumer that
  reaches RID-asset selection (bir2cir/facadegen/retarget read RAR's RID-neutral compile set — a repeated simple name
  there still throws); an empty `$(RuntimeIdentifier)` (framework-dependent, no-RID build) degrades to the host RID.
  `scripts/dotkt.sh` gained a matching `--target-rid` passthrough for direct cross-target dev runs. Verified: on a
  linux-x64 host, `--target-rid win-x64` selects the `runtimes/win-x64/lib` asset (was the host linux-x64 asset).

- **stdlib/bir2cir ([tmyt/dotkt#56], area:bir2cir): high-arity (17–22) function-type declarations no longer
  silently dropped during the stdlib build.** bir2cir's `HighArityFunctionFilter` dropped (with only a stderr
  warning) any stdlib declaration whose signature mentioned a function type with >16 params — a silent-drop
  landmine (its "context() overloads are arity 17-22" premise was stale; those overloads are arity 1-6, so the
  filter guarded nothing that exists). Deleted the filter entirely: ilemit's module-local `KFunc`N`/`KAction`N`
  delegate synthesis is arity-driven and mode-INDEPENDENT, so the stdlib build now emits wide function-type
  declarations exactly like an app build (verified: ref+rt builds emit `KFunc`23`/`KAction`22` for a 22-arg
  probe). The Func/Action 16-param cap is now owned solely by ilemit's `BuildFuncType`; no bir2cir arity filter
  remains. Aligns with the cardinal rule (never drop/special-case a stdlib decl) and the frontend-resolved ⇒
  backend-must-compile invariant.

### Changed

- **bir2cir consumes the frontend-resolved operand static type instead of re-deriving it ([tmyt/dotkt#122],
  [tmyt/dotkt#48], area:bir2cir, area:kotc): the no-re-resolution-downstream invariant, realized on the type-contract
  surface.** kotc now stamps each value node's instantiated static type as a structured `sty` slot at its `expr()`
  chokepoint; bir2cir's `StaticType` reads that stamp (carried onto the clr* nodes MemberCallSubstitution/NetInteropBinding
  synthesize, stripped before CIR in BirTypeLowering) rather than re-doing overload return-type resolution against the
  ref.dll. The ~27KB `StaticTypeResolver` re-inference — `ResolveCallReturn`/`ResolveFieldType`/`LocalMemberType`/
  `TryGlobalTopLevel`/`SubstMemberTv` + the cross-file `GlobalTypes` aggregation + `ReferenceMetadataIndex.TryFieldType` —
  is DELETED; the lexical `BirScope` (declared var/param types) stays for bir2cir-synthesized locals. Stdlib CIR is
  byte-identical (only recognition moved). Also closes **#48 residual 1**: the last bare-string `ownerType` sites
  (top-level file-class calls, `__mref` forwarders, class-delegation forwarders) now emit a structured `{t:"fqn"}` node,
  so `ownerType` is removed from the `verify-schema` `STR_OK` allow-list. **#48 residual 2** (primitive-shorthand leaf
  `int`/`void`/`object`) is confirmed a sanctioned below-kotc CLR-resolution vocabulary (ilemit normalizes toward it),
  not a value-slot string — no change. Two follow-on consistency fixes complete the change: (a) `CharSeqStringLowering`
  now collapses a stale `dotkt$CharSequence` `sty` on a declaration-read (`local`/`field`/`lateinitGet`/`staticField`)
  in lockstep with the decl it already retypes to `System.String`, so the sty-first `StaticType.Surface` no longer
  shadows the CharSequence→String model and the `StringCharSequenceBridge` correctly adapter-wraps a String flowing into
  an un-rebuilt stdlib `dotkt$CharSequence` arg slot (`il-regexreplace` ilverify StackUnexpected fixed — was a raw
  `string` reaching a `dotkt$CharSequence` param); (b) the user-delegate `getValue`/`setValue` owner emit site (the
  member-property `by` arm) was the last bare-string `ownerType` and now emits a `{t:"fqn"}` node like its two sibling
  delegate sites, so `verify-schema` `STR_OK` genuinely holds zero `ownerType` (`il-deleg`/`il-rwp`). #122 closes; #48 closes.
- **bir2cir/ilemit ([tmyt/dotkt#48], area:bir2cir, area:ilemit): the legacy string-token type grammar is DELETED —
  structured `TypeNode` only, matching the frozen #37 schema; no dual-protocol.** The wire was already structured
  (`docs/bir-cir.schema.json` `$defs/type`), but the CODE still PARSED/EMITTED the retired `clr:` / `clrg:Name[..]` /
  `@Name` / primitive-shorthand / `func:`/`sfunc:` / `nullable:`/`array:`/`byref:`/`gp:` grammar. **S4 — owner islands →
  `TypeNode`:** every bir2cir-side owner slot (`ownerType`/`accessOwner`/`clrOverride`/clr\* `type`, and
  SuspendColdLowering's coroutine owners) is now a structured `{t:fqn}` node; the applied-attribute owner is a bare FQN
  + an `attrExternal` bool (`AttrExternalNormalize` strips kotc's `clr:`-imported prefix), retiring ilemit's
  `attr.StartsWith("clr:")` branches; `scripts/verify-schema.py` `STR_OK` shrinks (`ownerType`/`clrOverride`/
  `accessOwner`/`recv0` dropped) so any regression reds the gate. **S5 — sig-token island → one structural comparator:**
  ilemit's overload resolution no longer renders a `TypeNode` to a legacy token string and re-parses it — `SigTokenOf`/
  `SigTokenMatches`/`SigTokenMatchesOpen`/`SkipTypeToken`/`FuncType(string)`/`FuncRetEnd`/`NormalizeGpNames`/
  `FindByNormalizedSig`/`StripSigPrefixes` are replaced by a structural `Matches(TypeNode, System.Type)` and the `sig`
  parameter threads as `TypeNode[]` (the `MethodsBySig` dictionary keeps a `SigCanon` hash key — an internal encoding,
  never a wire spelling). ilemit's `MapType(string)`/`ClrRef(string)`/`NativeType(string)`/`ParseOwner` prefix parsers
  and bir2cir's `LowerTypeString` grammar-construction are deleted; bir2cir emits bare BCL FQNs. Gate: full
  `gate.sh --full` GREEN (il/schema/sanity/ktproj/roundtrip/differential-all-MATCH/widedelegates).

- **kotc ([tmyt/dotkt#48], area:kotc: CLOSES #48 — the LAST string-token type grammar is deleted EVERYWHERE; kotc emits
  structured `TypeNode` only.** Completes the kotc-owned residual the bir2cir/ilemit slice left open. (1) `callInline`'s
  `owner`/`callee` are structured `{t:fqn}` identity nodes (owner may be JSON-null for the owner-less stdlib scope-fn
  arm); bir2cir `InlineSplice` reads them via `TypeJson.OwnerName`. (2) An applied attribute's `attr` type is a
  `{t:fqn}` node; kotc flags an imported .NET attribute with `"attrClr":true` (a frontend origin fact — no `clr:`
  prefix), which bir2cir `AttrExternalNormalize` consumes into the `attrExternal` bool. (3) the `stackptr` pseudo-FQN
  is renamed to the canonical synthetic identity `dotkt$stackptr`. `scripts/verify-schema.py` `STR_OK` drops
  `owner`/`callee`/`attr` — every remaining entry is a genuine non-type string (member/accessor NAME islands, the
  `ownerType` owner island, CFG/opaque payloads). Dead-data cleanup: `BirTypeLowering.LowerLeaf`'s `@`-decorated
  dual-representation branch and `MemberCallSubstitution.WrapByref`'s `byref:` string form (both producer-dead). Gate:
  full `gate.sh --full` GREEN; `verify-schema` 0 violations; differential MATCH unchanged.

### Fixed

- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir, area:ilemit): CLOSED — ilemit re-resolves NOTHING on any clr*
  member axis (W1-S5 finishes the arc).** W1-S5 carries the `newBoundClrDelegate` target (Site 3 — a bound
  `netObj::method` reference) via a new `ResolveBoundClrDelegate` + `memberSig`, and deletes ilemit's LAST name-only
  first-pick (`Emitter.Expressions.cs`'s `type.GetMethod(name, argTypes) ?? type.GetMethod(name)` → the consume-only
  `LinkClrMethod`). After the full arc — S1 (#44, generic calls) → S2 (plain calls / ctors / dispatch) → S3
  (properties / fields / events) → S4 (override base-slot) → S5 (bound delegate) — ilemit's only remaining
  `GetMethod`/`ResolveMethod` are fixed BCL gets, the by-design Site-2 `callInstance` linker for MLC-unresolvable
  local-emitted owners (instrumented: 0 arbitrary-overload / dynamic-escape firings), and the deterministic
  `InterfaceMethodOn` single-abstract-method SAM lookup — none is an overload/arity/name first-pick. `MATCH 188 / DIFF 0`.
- **bir2cir/ilemit ([tmyt/dotkt#46], [tmyt/dotkt#183], area:bir2cir, area:ilemit): W1-S4 — declaration-side override
  base-slot memberRef carry.** A method DECLARATION overriding a .NET base-CLASS virtual (a property accessor such as
  `override val message` -> System.Exception.get_Message; the coroutine SM `create`/`invokeSuspend` overrides of
  `BaseContinuationImpl`) carries `clrOverride` (the base owner FQN). bir2cir (`ClrMemberResolution.OverrideBase.cs`,
  a partial of the S2 pass, running last on the fully-lowered tree) now resolves the EXACT base virtual off the ref.dll
  (MetadataLoadContext) and stamps its DECLARED param signature as `clrOverrideSig` (positional-`tv` for a generic
  base). ilemit's `LinkOverrideBase` (`Emitter.ClrInterop.cs`) links the UNIQUE base slot (0 = hard ABI error, >1 =
  malformed) and `DefineMethodOverride`s it — deleting the former `baseT.GetMethod(name, ps) ?? baseT.GetMethod(name)`
  NAME-ONLY first-pick fallback (`Emitter.Assembly.cs`). The match is STRUCTURAL identity (an override's params ARE the
  base slot's), NOT call-side applicability — so `BaseContinuationImpl.create(Any,Cont)` and `create(Any[],Cont)` stay
  distinguishable (a scalar arg no longer matches the array param via the object-downcast rule), with Kotlin `Any` ==
  `System.Object` as the only leaf normalization. Gated by `il-overridemsg`/`il-supercall`/`il-superobj`/`il-supernet`
  + every coroutine case (`il-corestrict`, `il-seqforin`, `il-inlsuspend*`, …). The `callInstance` `ResolveMethod` site
  (#183 Site 2) stays a LINKER consuming kotc's FIR-resolved `sig` (the empirical arbitrary-overload first-pick and the
  BCL-interface dynamic escape both fire 0× across the stdlib self-build + all app cases); its owners are either
  local-emitted (MLC-unresolvable by definition — the S2 local-`new` SelectCtor residual parallel) or referenced
  kotlin.* slots already structurally linked by the carried sig. #46 #183
- **ref-common ([tmyt/dotkt#51], area:packaging): reference-asset selection uses the TARGET RID, not the host RID,
  and consults the real .NET/NuGet portable RID fallback graph instead of a hand-rolled family table.**
  `ManagedReferenceCatalog.Create` gained `targetRid` / `ridGraphPath` parameters: `SelectRuntimeAsset` ranks
  `runtimes/<rid>/lib` assets against the TARGET RID's fallback chain (the transitive `#import` closure of
  `PortableRuntimeIdentifierGraph.json`, expanded breadth-first exactly like NuGet's `RuntimeGraph.ExpandRuntime`),
  so cross-target compilation (e.g. Linux host → `win-x64`) now selects the correct RID-impl asset instead of the
  host's — previously a Linux build targeting Windows picked the RID-neutral PlatformNotSupported placeholder and the
  special-RID fallback was input-order dependent. The graph path is MSBuild's `$(RuntimeIdentifierGraphPath)` when
  passed, else auto-discovered from the running SDK; `targetRid` defaults to the host RID when unset (correct for a
  host-targeted or direct-script run), so existing host-target builds are unchanged. The hand-rolled family chain
  survives only as a last resort when no portable graph is found. Wiring MSBuild's `$(RuntimeIdentifier)` /
  `$(RuntimeIdentifierGraphPath)` through `ilemit --runtime-refs` into `Create` is a follow-up (ilemit + targets). #51

- **bir2cir/ilemit ([tmyt/dotkt#46], [tmyt/dotkt#121], area:bir2cir): property / field / event memberRef carry —
  ilemit is a pure linker for every clr* member-ACCESS axis: calls, ctors, properties, fields, events, dispatch
  (W1-S3, closes #121). #46 stays OPEN for W1-S4 — two ilemit resolution sites remain (NOT in #121's enumerated
  use-sites): the declaration-side override base-slot link (`Emitter.Assembly.cs`, still `GetMethod(name, ps)` + a
  name-only fallback for every method override incl. ToString/Equals) and the `@Clr`/`@ClrIntrinsicAsDynamic`
  `callInstance` resolution (`Emitter.Expressions.cs`).** The remaining un-carried axes
  followed the S2 plan: bir2cir (`ClrMemberResolution.PropFieldEvent.cs`, a partial of the S2 pass, running last)
  now resolves `clrPropGet`/`clrPropSet`, `clrEventAdd`/`clrEventRemove`, and an external `field`/`setFieldExpr`/
  `setField` against the ref.dll (MetadataLoadContext), stamping a `member` discriminator (`accessor`|`field`), the
  resolved accessor NAME, `memberSig`, and `dispatch`. ilemit consumes them via the shared `LinkClrMethod` +
  `EmitClrDispatch` — no property-vs-`get_`-method-vs-field reclassify, no external-field→accessor reinterpret, no
  unchecked `GetEvent` (a missing event is now a hard ABI error, hardening #113), and no `call`/`callvirt`/
  `constrained` derivation from the reflected accessor. A generic base-interface accessor (`IReadOnlyCollection<T>.
  get_Count` on `IReadOnlyList<T>`) retargets the owner to the constructed base interface (the resolved twin of the
  deleted `PropAccessor`'s `SubstituteIfaceArgs` re-anchor). Deleted `PropAccessor`, `ExternalPropAccessor`,
  `EmitInstanceCall`, `PropList` (the KIND-derivation / first-pick helpers). A LOCAL emitted owner (ref.dll returns
  null) keeps its direct backing-field access. Gated by `il-extprop`/`il-vtprop`/`il-event`/`il-eventext`/
  `il-ifaceevent` + `roundtrip-property-type`. #46 #121

- **bir2cir ([tmyt/dotkt#157], area:bir2cir): general cross-module top-level `val` accessor resolution; delete
  the `COROUTINE_SUSPENDED` band-aid.** A cross-module top-level `val` read is kotc-emitted (post-#89) as
  `callStatic owner:null … prop:get`; bir2cir already reconstructs the `get_<name>` accessor and resolves it via
  `TryResolveTopLevelStatic` — and the ref-scan already indexes property accessors (`get_X`/`set_X` — file-class
  statics with `intrinsic==null`, no `IsSpecialName` exclusion). The prior `COROUTINE_SUSPENDED`-specific
  owner-rebind in `MemberCallSubstitution` was therefore redundant (post-#89 the "already-owner'd" shape it also
  covered no longer occurs — both reads now arrive owner:null); removed it (no-band-aid rule), so every
  cross-module top-level val resolves through the ONE general path. Byte-identical CIR before/after on
  `il-suspendintrinsicowned`; the non-coroutine sibling of the same path is gated by `il-extprop` (extension-property
  getters). NB the facadegen-consumed top-level val is a distinct owner-ful `staticField` shape (gated separately by
  `roundtrip-toplevel-val`), and the owner:null path is klib-package-fragment-only. #157

- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir): plain-call / ctor / dispatch memberRef carry — ilemit purified
  to a linker (W1-S2; the generic-call dual S1 was #44).** bir2cir now resolves `clrStatic`/`clrInstance`/`newClr`
  against the ref.dll (MetadataLoadContext), structurally matches the winning member, and carries its declared param
  signature as `memberSig` (+ `dispatch` for `clrInstance`); ilemit consumes it purely as a linker (structural match
  to exactly one handle — 0 = hard ABI error, >1 = malformed, no first-pick). Deletes ilemit's
  PickCtorByAssignable / PickClrCtor / ParamAcceptsArg / ResolveInheritedIfaceMethod / the EmitClrCall resolution
  cascade / the implicit EmitDynamicCall downgrade / dispatch derivation (new pass `ClrMemberResolution.cs`); an
  interface owner with no matching member becomes an explicit `clrDynInstance`, not a silent downgrade. #24
  override-dispatch preserved (`MATCH 203 / DIFF 0`). Remaining for W1-S3 (so #46 stays open): properties / fields /
  events + the declaration side (`clrPropGet`/`clrPropSet` still route through `EmitInstanceCall`); the local-`new`
  `SelectCtor` + referenced `kotlin.*`-helper arity-probe axes are MLC-unresolvable and stay by design.
- **bir2cir ([tmyt/dotkt#153], area:bir2cir): primitive-array-receiver top-level stdlib extensions resolve at app
  level.** `intArrayOf(1,2).toList()` failed with ilemit `static method not found` — `RecvKeyOf` keyed the
  primitive-array Fqn as `kotlin.IntArray` while the ref side collapses `int[]` to `[]`, so owner attribution missed.
  A shared `RecvKeyOfFqn` now maps every specialized-array Fqn (signed + unsigned) to `[]`; because that key is lossy
  (generic `Array<T>` + all primitives share it), a fine first-param `ParamKey` narrows the overload so generic
  `Array<out T>.toList` no longer erases the element to `object` and `ubyteArrayOf(..).toList()` binds the
  instantiated helper. Auto-recovers the app path for #97 (primitive `copyInto`) and #128 (`copyOf(newSize)`). Gate:
  `il-intarraytolist`.
- **packaging ([tmyt/dotkt#133], area:packaging): the MPP SDK (`DotKt.Sdk.Mpp`) builds out of the box — a new
  `dotkt-mpp` `dotnet new` template.** `Sdk="DotKt.Sdk.Mpp"` needs a `global.json` pinning both `DotKt.Sdk.Mpp`
  and the nested `DotKt.Sdk` (the NuGet resolver reads a nested SDK's version *only* from `global.json`), and
  nothing scaffolded it. The `dotkt-mpp` template now ships that `global.json` (both pins substituted to the
  release version at pack) alongside a common `expect` / CLR `actual` sample, so `dotnet new dotkt-mpp && dotnet
  run` works with no hand-written boilerplate. New gate case: `verify-packaged-sdk.sh` `mpp-template`.
- **packaging ([tmyt/dotkt#134], area:packaging): the DotKt build back-half is now incremental — a no-op build
  skips it.** `DotKtBir2Cir`/`DotKtIlEmit`/`DotKtRetarget` had no `Inputs`/`Outputs`, so every `dotnet build`
  re-lowered, re-emitted and re-retargeted, rewriting the output dll's timestamp and forcing every downstream C#
  `ProjectReference` to rebuild. Each target now keys `Inputs`/`Outputs` off a stable `.stamp` (the compile's
  `$(DotKtOut)/.stamp` cascades through `$(DotKtCirOut)/.stamp` to the emitted dll and a retarget stamp), and the
  `_DotKtPlaceholder.cs` write became `WriteOnlyWhenDifferent` (it was bumping its mtime every build and forcing
  `CoreCompile` to recompile). A no-op build now converges.
- **packaging ([tmyt/dotkt#135], area:packaging): the Windows compiler launcher is selected by OS.**
  `$(DotKtCompiler)` was hardcoded to the extension-less UNIX `kotc` script, leaving Windows to rely on cmd.exe's
  PATHEXT resolving a pathed extension-less command. It now selects the shipped `kotc.bat` when `$(OS)` is
  `Windows_NT` (both launchers ride in the package from the Gradle `installDist`), falling back to `kotc` elsewhere.
- **packaging ([tmyt/dotkt#151], area:packaging): corrected the `DotKt.Sdk` `Sdk.props` guard comment.** It said
  the pack guard compares the `DotKtVersion` default to `DotKtVersionPrefix`; it actually compares to the version
  CORE (prefix, plus `-suffix` when pre-release, e.g. `0.9.6-rc7`). Following the old comment during an RC would
  trip the (fail-safe) guard.

- **stdlib ([tmyt/dotkt#104], area:stdlib): `Regex.findAll`/`splitToSequence` and the `Regex.options` getter no longer
  throw `NotImplementedError`.** All three shipped as `TODO()` runtime stubs. Now implemented in pure Kotlin over the
  existing bindings: `findAll` = `generateSequence` over `find()`/`MatchResult.next()` (every non-overlapping match,
  left-to-right, `startIndex`-honored, via ordinary `Sequence` machinery — no coroutine `sequence{}` builder needed);
  `splitToSequence` = `split(input, limit).asSequence()`; `options` decodes the compiled `System...RegexOptions`
  `[Flags]` bitmask (`IgnoreCase`/`Multiline`/`Singleline`/`IgnorePatternWhitespace` → the matching `RegexOption`;
  `LITERAL`/`UNIX_LINES`/`CANON_EQ` have no .NET bit). Gate: `il-regexseq`.
- **facadegen ([tmyt/dotkt#132], area:facadegen): interface-companion statics survive the round-trip.** kotc flattens an
  interface's plain `companion object` to the interface's OWN static fields/methods (the `SharingStarted.Eagerly` #83
  path), but facadegen's interface branch enumerated only `Public|Instance` members and dropped every flattened static —
  so a consumer re-importing the DotKt library could not resolve `I.X`/`I.f()`. facadegen now surfaces an interface's
  `Public|Static` fields/props/methods/events as companion members (`staticProps`/`staticFuns`/`staticEvents`), reached
  via `I.Companion`; a C#11 static-abstract/static-virtual interface member (invokable only through a constrained type
  parameter) is excluded so no uncallable companion slot is advertised. Gate: `roundtrip-iface-companion`.
- **facadegen ([tmyt/dotkt#146], area:facadegen): `KotlinFun()` no longer silently demotes infix/operator/suspend.** The
  blanket `catch` around the `[KotlinFunction]` read erased a method's Kotlin vocabulary whenever an UNRELATED user
  attribute referenced a type outside the resolver set (materializing one attribute forces the whole set). The read is
  now guarded per-attribute (a bad sibling never blocks `[KotlinFunction]`); a genuine enumeration failure on an
  already-DotKt-classified assembly is surfaced LOUD instead of swallowed. The unconditional `if (name=="compareTo")
  op=true` hack — which force-flagged ANY method named `compareTo` and masked a genuinely-missing operator flag — is
  removed; kotc stamps the real `isOperator` (inherited by keyword-less overrides). Gate: `roundtrip-operator-flag`.
- **facadegen ([tmyt/dotkt#179], area:facadegen): a re-consumed `class C : Comparable<C>` regains its Kotlin operator
  surface.** At lib emit a Kotlin `class C : Comparable<C>` lowers `compareTo` to the PascalCase
  `System.IComparable<C>.CompareTo` slot and its supertype to `System.IComparable<C>` (+ a non-generic bridge), so on
  re-import facadegen surfaced neither the lowercase `operator fun compareTo` nor the `Comparable<C>` supertype — a
  consumer's `c1 < c2` / `sorted()` was unresolved. facadegen now (a) restores the `System.IComparable<X>` supertype as
  fully-qualified `kotlin.Comparable<X>` (dropping the non-generic bridge) so the type is seen as `Comparable` and
  `sorted()`'s constraint holds, and (b) renames the DotKt `IComparable<X>`-self-slot `CompareTo` to the lowercase
  `compareTo` + forces the `operator` flag so the FRONTEND resolves `<`/`>`/`<=`/`>=`. A genuine .NET `IComparable`
  keeps its verbatim PascalCase `CompareTo` (`il-icmparity`). The residual bir2cir call-binding half — `NetInteropBinding`
  rebinds the Kotlin `compareTo` call to the DotKt owner's PascalCase `CompareTo` slot when the owner implements generic
  `IComparable<T>` — landed too, so the end-to-end `<`/`sorted()` run passes. Gates: `roundtrip-comparable-meta` (surface)
  + `roundtrip-comparable` (end-to-end; its RT_XFAIL pruned). #179 fully closed.
- **bir2cir ([tmyt/dotkt#178], area:bir2cir): `Regex(pattern, Set<RegexOption>)` / `Regex(pattern, RegexOption)` ctors
  work.** The options-taking Regex constructors threw `InvalidProgramException` — the `Set<RegexOption>`/`RegexOption`
  → `System...RegexOptions` ctor-arg conversion was unwired. `NetInteropBinding.Reshape` now synthesizes the
  `RegexOptions` `[Flags]` bitmask (`IGNORE_CASE`→1, `MULTILINE`→2, `DOT_MATCHES_ALL`→16, `COMMENTS`→32; the three
  no-.NET-bit options drop to 0) at the `newClr` site and retypes the arg so `ClrMemberResolution` binds the BCL
  `Regex(String, RegexOptions)`. Gate: `il-regexopts`. Encode-side deviation recorded in `docs/dotkt-semantics.md` §5b-quater.
- **bir2cir ([tmyt/dotkt#180], area:bir2cir): direct/mixed nullable `Double?`/`Float?` `==` is verifiable IL.** The
  `ieee754equals` arm lowered a nullable-float `==` to a raw `Ceq` over `Nullable<T>` structs (unverifiable IL /
  `InvalidProgram`, latent). It now emits null-safe shaping (`null==null`→true, one-null→false, both-present→IEEE `==`
  on the values) — direct `==` stays IEEE per #95 (`-0.0 == 0.0` true, `NaN == NaN` false), distinct from #152's
  structural bit-equality. Nullness is read from `StaticType.Surface` (so an explicit `x as Double?` is caught too).
  Gate: `il-floateqnull`. Follow-up: #181 (safe-call `obj?.d == y` operand, same class).
- **bir2cir ([tmyt/dotkt#152], area:bir2cir): nullable `Double?`/`Float?` structural equality uses total-order
  bit-equality, not boxed `Double.Equals`.** A data-class / structural `==` over a `Double?`/`Float?` field fell
  through to a boxed `System.Double.Equals` (IEEE: `-0.0 == 0.0` true), violating the total-order equals/hashCode
  contract #95 adopted for the non-null case. The `EQEQ` lowering now, before the `objEq` fallback, emits null-safe
  bit-equality (`clrDoubleEquals`/`clrFloatEquals`) so `D(-0.0) != D(0.0)`, `D(NaN) == D(NaN)`, and hashSet
  membership is consistent; direct operator `==` stays IEEE per #95. Gate: `il-structfloateqnull`. Follow-up: #180
  (direct/mixed nullable `ieee754equals`).
- **kotc ([tmyt/dotkt#177], area:kotc): a `companion object` extension fun passes its extension receiver.** A
  `fun Receiver.ext()` declared inside a `companion object` lowered to a static with a leading `__self` param, but the
  call site emitted only the regular args — dropping the receiver → an arity miscompile. The companion-extension emit
  now prepends the extension receiver as the first arg (consistent with member/top-level extension emit). Gate:
  `il-companionext`.

- **stdlib ([tmyt/dotkt#141], area:stdlib): `hypot`/`expm1`/`ln1p` (Double & Float) bind the numerically-correct
  net10 BCL primitives.** The old bodies (`sqrt(x*x+y*y)`, `exp(x)-1`, `ln(1+x)`) overflowed for large magnitudes
  (`hypot(1e308,1e308)` → `Infinity`) and lost all precision to cancellation near 0. Now bound as `@ClrIntrinsic`
  to `System.Double.Hypot`/`ExpM1`/`LogP1` and `System.Single.Hypot`/`ExpM1`/`LogP1`. Gate: `il-mathnumerics`.
- **stdlib ([tmyt/dotkt#143], area:stdlib): `decodeToString`/`encodeToByteArray` honor `throwOnInvalidSequence=true`.**
  The 3-arg overloads previously ignored the flag and silently substituted U+FFFD. They now transcode through a
  throwing `UTF8Encoding(false, true)` and surface a `CharacterCodingException` (Kotlin contract) on malformed
  UTF-8 / unpaired surrogates; the default (`false`) path keeps replacement. Gate: `il-utf8throw`.
- **stdlib ([tmyt/dotkt#144], area:stdlib): `String`/`Char` `uppercase()`/`lowercase()` documented as CLR-native
  1:1 mapping — NOT a JVM one-to-many bug.** `#144` was re-triaged (not a defect): kotlin/clr has no binary interop
  with other Kotlin backends, so string-value parity (`"ß".uppercase() == "SS"`) has no functional value, and .NET's
  deliberate 1:1 no-expansion (`ToUpperInvariant`/`ToLowerInvariant`) is a valid platform choice. The public forms
  bind directly to `System.String.ToUpperInvariant`/`ToLowerInvariant` (`@ClrIntrinsic`); `"ß".uppercase() == "ß"`.
  The deliberate deviation from Kotlin/JVM/Native/JS one-to-many expansion is recorded in `docs/dotkt-semantics.md`
  §5g. Gate: `il-caseinvariant`.
- **stdlib ([tmyt/dotkt#145], area:stdlib): array `fill(element, fromIndex, toIndex)` validates its range.**
  A `fromIndex > toIndex` call silently no-op'd; the generic + all 8 primitive `fill` actuals now throw
  `IllegalArgumentException` on an inverted range and `IndexOutOfBoundsException` out of bounds (Kotlin contract).
  Gate: `il-fillrange` (generic path; the primitive actuals carry the identical guard but remain blocked from app
  calls by the pre-existing primitive-array-receiver resolution gap).
- **stdlib ([tmyt/dotkt#129]/[tmyt/dotkt#130]/[tmyt/dotkt#142], area:stdlib): concurrency-correctness in the atomics + coroutine primitives.**
  Three memory-model/locking defects fixed stdlib-side (CLR-native Interlocked/Volatile/Monitor bindings, no compiler
  special-casing). **#129**: the `AtomicIntArray`/`AtomicLongArray`/`AtomicArray` element ops did a bare
  `monitorEnter/…/monitorExit` around `array[index]`, whose bounds check throws mid-critical-section and leaked the
  monitor (a reentrant lock the throwing thread never notices but every OTHER thread on that instance deadlocks on);
  each section is now wrapped in `try { … } finally { monitorExit(lock) }`. **#130**: scalar `AtomicInt`/`AtomicLong`/
  `AtomicBoolean`/`AtomicReference` `load()`/`store()` were plain field access outside the memory model. The lock-free
  scalars (`AtomicInt`/`AtomicLong`) now bind `System.Threading.Volatile.Read/Write(ref …)` (byref, ordered and
  non-tearing for `long` on every platform); the monitor-backed `AtomicBoolean`/`AtomicReference` keep a `@Volatile`
  field for the unlocked acquire `load()` but route `store()` through the SAME monitor as their RMW ops — a lock-free
  store would slip inside the monitor's read-modify-write gap and be lost (non-linearizable). `toString()` now reads
  via `load()` so `AtomicLong` cannot tear. Separately, the `AtomicIntArray`/`AtomicLongArray`/`AtomicArray`
  array-adopting constructors now defensively `copyOf()` the argument (per the expect KDoc; aliasing left an
  unsynchronized side door into the monitor-guarded storage). **#142**:
  `SafeContinuation`'s `UNDECIDED→result` / `UNDECIDED→COROUTINE_SUSPENDED` state transition was a non-atomic
  check-then-store that races under a multithreaded dispatcher; it is now a lock-free CAS loop over a `@Volatile`
  field via `Interlocked.CompareExchange(ref object,…)`, faithful to the JVM `AtomicReferenceFieldUpdater` version.
  New gate cases `il-atomicarraytry` (cross-thread lock-release), `il-volatileatomic` (volatile round-trip), and
  `il-safecontresume` (async cross-thread `suspendCoroutine` resume).
### Changed

- **gates/tests (area:gates): NUnit migration foundation + first family (generics battery) migrated off the
  per-case bash gate.** Stood up the production in-process NUnit suite (`docs/design-nunit-test-harness.md`):
  `tests/il/DotKt.Tests.Il.ktproj` resolves the LOCALLY-BUILT
  DotKt SDK from `build/nuget-feed` (`make pack`) via an active `tests/nuget.config` (isolated
  `globalPackagesFolder`, package-source mapping `DotKt.*`→local feed) — so the suite tests the compiler in the
  working tree, not a published nuget. `tests/run-nunit-il.sh` drives it and enforces a **discovered-count
  guard** (asserts `dotnet test` discovered exactly the expected number of methods — a dropped/added method or a
  0-test discovery failure reddens the gate) plus once-per-assembly ilverify. Migrated `cases/il-generic ..
  il-generic6` (the G-1..G-6 progressive-milestone cases the cases-test-design audit condemns as 6 permanent
  compiler processes) → one `GenericsTests` fixture, 6 `@TestAttribute` methods asserting the SAME values via
  `assertEquals` (aliased from `ClassicAssert.Companion.AreEqual`); the 6 case dirs, their `verify-il.sh`
  `il_check` lines, and their `verify-differential.sh` `PURE` entries were deleted in the SAME change (audit
  必須是正条件 #14). `dotnet test` runs the battery in ~17 s clean / ~3.6 s warm against the local SDK.

- **docs/process (area:semantics): the behavior-choice acceptance test is now stated as "consistent, documented,
  convincingly explainable"** (CLAUDE.md Design doctrine + `docs/dotkt-semantics.md` guiding principle): ① Kotlin
  contract by default, ② CLR-native where unspecified, ③ *interop-first deviation* may override even the KDoc letter
  when CLR/mscorlib consistency convincingly wins. Recorded the #144 case-mapping deviation as `docs/dotkt-semantics.md`
  §5b-ter (`"ß".uppercase()` stays `"ß"`, no Unicode one-to-many expansion — previously only a `CharClr.kt` comment).
  A new PostToolUse hook (`scripts/hooks/check-jvm-emulation.sh`, wired in `.claude/settings.json`) auto-injects this
  self-check whenever newly-written toolchain/stdlib text pattern-matches JVM-emulation intent ("matches JVM",
  "JVM parity", the hashCode 31-polynomial), so agents re-verify the judgment at write time instead of after review.

- **gates ([tmyt/dotkt#107]/[tmyt/dotkt#108]/[tmyt/dotkt#99]/[tmyt/dotkt#109], area:gates): hardened the verification harness.**
  `verify-il.sh` now (#107) FAILS LOUD when the ilverify lane cannot run (ILVerify.dll absent / runtime ref dir
  missing) instead of silently reporting green with zero IL coverage and printing spurious `FIXED` for every real
  XFAIL; (#108) wraps every per-sample run in a `timeout` (default 60s, `DOTKT_RUN_TIMEOUT`) so a coroutine
  resume/pulse-drop deadlock surfaces as a distinct `run timeout` FAIL instead of wedging the whole gate; and (#99)
  DERIVES the ilverify assembly set from the run set (each sample records its emitted assembly name) rather than a
  hand-maintained map that had drifted — closing the 78+ run-only-sample formal-coverage gap permanently, with a
  single explicit `ILVERIFY_EXCLUDE` (stackalloc's by-design-unverifiable `localloc`) printed loudly, no silent gaps.
  This exposed six pre-existing formal-only ilverify findings (all RUN-green, runtime-safe): `boxgen` (#62/#46
  compare-SAM boxing), `classdeleg` ([tmyt/dotkt#174], new — class-delegation forwarder narrows the MutableList
  iterator return), `copyofnull` (#127/#86 nullable-value-type array object-erasure), and `defargs`/`delegnull`/
  `linkedorder` (#170/#150 DelegateCtor) — each XFAIL_ILVERIFY-listed with a concrete reason. `verify-roundtrip.sh`
  adds (#109) a cross-module nullable VALUE-TYPE generic case (`T?` param+field instantiated at `T=Int`), which
  documents the #86/#147 cross-module gap as an RT_XFAIL (the consumer fails to compile because the `T?` restores as
  bare non-null `T`) — an axis every other gate missed by driving only `T=String`.
- **stdlib ([tmyt/dotkt#167]/[tmyt/dotkt#168], area:stdlib): String/Float/Double `hashCode()` bind to CLR-native `GetHashCode`.**
  Removed the hand-rolled JVM-forced hash bodies — String's `s[0]*31^(n-1)+…` polynomial and Float/Double's
  `toBits()` bit-hash. The Kotlin `hashCode` contract requires only within-run consistency + equals-consistency
  ("need not remain consistent from one execution to another"), not a specific value or across-run determinism;
  kotlin/clr consumes no JVM artifacts, so no interop needs the JVM value. `System.String/Single/Double.GetHashCode`
  already satisfy the contract (per-process consistent, NaN/zero normalized to be equals-consistent with the
  total-order structural equality). String binds via `@ClrIntrinsic("GetHashCode")` (falls through kotc's
  universal-method routing to the BCL slot); Float/Double drop the declaration entirely and inherit the `kotlin.Any`
  slot like Int/Long (routing to the native value-type `GetHashCode`). The `il-strhash`/`il-pairtostr` gate cases now
  assert equals-consistency + hash-set membership instead of a pinned integer.
- **CI: run the COMPLETE canonical gate set + a distinct packaged-SDK job + Windows coverage ([tmyt/dotkt#160], area:packaging).**
  `.github/workflows/verify.yml` previously ran only IL/differential/ktproj/round-trip/wide-delegate on a
  single `ubuntu-latest` job — it silently skipped `verify-schema`, `verify-sanity`, and (release-critically)
  `verify-packaged-sdk`, the only gate that restores + consumes the 5 real nupkgs. The workflow now invokes the
  Makefile aggregates (gate list single-sourced there, not copied into YAML): a `verify` job runs
  `make verify-core` (the canonical set), a distinct release-blocking `packaged-sdk` job runs
  `make verify-packaged-sdk`, and a `windows` job covers the Windows surface (kotc.bat install, nupkg restore,
  packaged build/run, `verify-ktproj`, `dotnet new` template creation). New `make verify-core` target =
  `make verify` minus the packaged-SDK gate.
- **NuGet packages carry provenance metadata + third-party notices ([tmyt/dotkt#166], area:packaging).**
  All 5 packages now declare an SPDX `Apache-2.0` license, `projectUrl`, and a `<repository>` with the source
  commit (stamped by `pack-nuget.sh`), and ship a packaged readme (`packaging/DotKt.README.md`). `DotKt.Toolchain`
  additionally ships `THIRD-PARTY-NOTICES.md` listing the redistributed components (Kotlin compiler/runtime,
  kotlinx-coroutines, JetBrains annotations, Mono.Cecil, `System.Reflection.MetadataLoadContext`) and their licenses.
- **docs: README + support matrix reconciled with actual behavior; JVM-framing cleanup ([tmyt/dotkt#164], area:docs).**
  The README "no bundled libraries" line now states DotKt ships no UI/framework abstraction but DOES ship its CLR
  Kotlin stdlib; the hardcoded corpus/pass counts are softened to point at the gates' XFAIL maps. The
  `supported-features.md` Regex row is regenerated method-by-method (`find`/`matchEntire`/`matches`/`replace`/`split`/
  group accessors work; `findAll`/`splitToSequence`/`options` pending). Recorded the correctness bar in
  `docs/dotkt-semantics.md` and `CLAUDE.md`: the bar is the Kotlin spec/KDoc contract, JVM is a reader reference
  (not a compat target), unspecified behavior takes the CLR-native form.
### Fixed

- **bir2cir/ilemit ([tmyt/dotkt#169], area:backend): the concrete `LinkedHashSet` (a #169 side-effect) emitted invalid
  IL for `setOf`/`distinct()`/`toMutableSet()`/`retainAll` — `InvalidProgramException` at runtime.** Making
  `LinkedHashSet` a real generic Kotlin class (was a `@ClrTypeAlias`) exposed three CLR-codegen bugs, all fixed while
  keeping the #169 insertion-order contract: (1) ilemit's `SelectCtor` picked a ctor by ARITY only, so
  `new LinkedHashSet(collection)` resolved to the arity-colliding `(Int)` ctor instead of `(Collection<E>)` — now it
  signature-matches the `new` node's declared `argTypes` (falling back to first-arity when absent/unreadable); (2)
  `CollectionBclSlotSynthesis` emitted its synthesized `ICollection.Contains`/`IList.IndexOf` self-forward against the
  OPEN generic self (`LinkedHashSet\`1`) instead of the constructed `LinkedHashSet<!0>` (the pass runs after
  GenericSelfInstantiation); (3) `MemberCallSubstitution` rerouted EVERY `.iterator()` on an emitted `kotlin.collections.*`
  non-alias type to the base-`Iterator` bridge, but the concrete `LinkedHashSet` declares its own `MutableIterator`-returning
  `iterator()` — the reroute is now suppressed for any type (local OR ref.dll) that declares a concrete `iterator()`, so
  an app's `linkedSetOf(..).iterator().remove()` binds the real slot (was `EntryPointNotFound` on `remove()`). Regression
  case `cases/il-linkedset`.
- **stdlib ([tmyt/dotkt#162]/[tmyt/dotkt#169], area:stdlib): two Kotlin-contract fixes in text/collections.**
  - **#162 `Regex.matchEntire`/`matches` now do a TRUE anchored full match.** The old path ran a leftmost
    `System...Regex.Match` (a SEARCH) and accepted it only if the first result spanned the input — so a shorter
    alternation branch winning the search (`Regex("a|ab").matchEntire("ab")` → `a` found first) returned `null`, and
    lazy quantifiers hit the same class. `matchEntire` now anchors the engine: it re-matches the pattern wrapped as
    `\A(?:<pattern>)\z` (the non-capturing group scopes a top-level alternation and preserves the user's capture-group
    NUMBERS) with the instance's OWN compiled options (read via a new `nativeOptions`/`ClrRegexOptions` binding and fed
    to the static `Regex.Match(string,string,RegexOptions)` overload), so the engine backtracks to a full-input match
    when one exists. `matches` delegates unchanged. Regression case: `cases/il-regexanchor`.
  - **#169 `LinkedHashMap`/`LinkedHashSet` (and `mapOf`/`setOf`) now preserve insertion order across removals.** They
    were aliased to `Dictionary`/`HashSet`, which only preserve insertion order incidentally and LOSE it after a
    removal — violating the Kotlin iteration-order contract. `LinkedHashMap` is now `@ClrTypeAlias`-bound to the
    insertion-ordered `System.Collections.Generic.OrderedDictionary<K,V>` (.NET 9+; a pure alias swap — it exposes the
    same non-generic `IDictionary`/`ICollection` facades and intrinsic members the map-defaults helpers rely on).
    `LinkedHashSet` — .NET has no ordered generic set — is now a REAL pure-Kotlin `MutableSet` backed by that
    `LinkedHashMap` (exactly as Kotlin/JVM backs it with a `LinkedHashMap`), so it gets the `CollectionBclSlotSynthesis`
    ICollection slots + the reverse `GetEnumerator` bridge. Plain `HashMap`/`HashSet` stay unordered (per contract).
    Regression case: `cases/il-linkedorder`.
- **packaging ([tmyt/dotkt#161]/[tmyt/dotkt#106], area:packaging): MSBuild-SDK + pack staleness fixes.**
  - **#161 stale injection metadata across a `<DotKtImport>` change / `dotnet clean`.** `DotKtInjectTypes` consumed
    `@(DotKtImport)` but did not track it as an Input (a non-file item cannot be a target Input), so removing/adding an
    import left the previous `obj/dotkt-clrtypes.meta` in place and the build kept succeeding against a dropped .NET
    type until an unrelated `.kt` edit forced a recompile; and none of the generated DotKt state under
    `$(BaseIntermediateOutputPath)` was tracked for `Clean`, so `dotnet clean` did not repair it. The ordered
    `@(DotKtImport)` set is now materialized into a `WriteOnlyWhenDifferent` manifest (`dotkt-clrimports-explicit.txt`)
    by a new `DotKtComputeImportManifest` target and added as an Input of `DotKtInjectTypes`, so add/remove/reorder
    flips a timestamp and re-runs injection (no-op rebuild stays byte/timestamp-stable); a new `DotKtClean`
    (`BeforeTargets="CoreClean"`) wipes the BIR/CIR dirs + the meta/import-list/options/import-manifest files.
  - **#106 pack could ship a STALE stdlib/klib.** `scripts/pack-nuget.sh` rebuilt the frontend klib and the stdlib
    ref/rt dlls only when MISSING, so a `pack-nuget.sh` run (directly or via `verify-packaged-sdk.sh`) could package a
    klib/stdlib baked by an older toolchain against freshly-built tools. It now uses the fingerprint-aware
    `need_fe_klib`/`need_stdlib_ref`/`need_stdlib_rt` builders (`scripts/lib.sh`), which rebuild on toolchain
    fingerprint mismatch OR absence.
- **ilemit ([tmyt/dotkt#91]/[tmyt/dotkt#92], area:ilemit): generic-field token anchoring + the abstract-slot body invariant.**
  - **#91 generic FIELD token anchoring** — a raw `@ClrField` access whose owner is a GENERIC type emitted a bare
    `C`1::f` operand ("not fully instantiated": `ResolveField`'s `TypeBuilder.GetField(constructed, fb)` threw
    `field must be declared on a generic type definition`, and ilverify crashed with an `IndexOutOfRange` in
    `get_GenericParameters`). `ResolveField` now mirrors the #84-I METHOD-side anchoring, FIELD side: an inherited
    generic-base field is re-anchored onto the owner's CONSTRUCTED base instantiation via a new
    `AnchorInheritedFieldOnBase` — for a non-generic subclass (`constructed == null`), a constructed generic-subclass
    receiver, and a self-instantiated `this` inside a generic method alike. Suspend-free; pure Reflection.Emit
    mechanics (the kotlinx port hit it at `JobSupport.kt ResumeAwaitOnCompletion`1.invoke`). Regression case:
    `cases/il-genfield`.
  - **#92 abstract-slot body invariant** — `EmitMethodBody` now skips any MethodBuilder DECLARED `Abstract`
    (`mb.IsAbstract`, the single source of truth) rather than re-deriving abstractness from the CIR `abstract` flag,
    making the `Method body should not exist` emit-crash impossible while WARNING (naming the def) when the skip is
    unexpected — so an upstream defect (a body written onto an abstract slot) stays visible. The dup-`$dupN` counter now
    runs for class abstract slots too, keeping the body phase in lockstep with declare.
- **kotc ([tmyt/dotkt#57]/[tmyt/dotkt#89]/[tmyt/dotkt#40], area:kotc): three frontend symbol-resolution fixes.**
  - **#57 the `length`-reference deferral is OWNER-keyed, not override-chain-keyed.** A property reference to
    `length` on a USER class implementing `CharSequence` now lifts faithfully — its accessor resolves on the
    class's OWN emitted `get_length` slot — for a DIRECT override AND one INHERITED through an intermediate
    (`B : A`, `A : CharSequence`). The retired override-chain walk over-deferred the direct case (a compile
    error on a liftable reference) while missing the indirect one (both should behave alike). The deferral now
    keys on the accessor's RESOLVED declaring owner (`getterFn.parent`): only a .NET-mapped CharSequence owner
    (`String`/`StringBuilder`/the polymorphic `kotlin.CharSequence`, whose slot bir2cir renames/collapses) stays
    deferred. (`BirEmitterLifts.kt`)
  - **#89 a CROSS-MODULE top-level `val` read is attributed `owner:null`, not the READING file's class.** A
    computed top-level val deserialized from the frontend metadata klib is PACKAGE-keyed (its parent is a package
    fragment, not an `IrFile`), so kotc cannot name its declaring file class and no longer mis-owns it to
    `<ReaderFile>Kt` (the #80 `COROUTINE_SUSPENDED` root). It emits the same "unresolved owner" fact it already
    emits for a cross-module top-level FUNCTION; bir2cir binds the true declaring file class off the ref.dll.
    (`BirEmitterCalls.kt`)
  - **#40 verified already-resolved on current main; regression guard added.** A cross-module `@InlineOnly` +
    `@ClrIntrinsic` stdlib function keeps its `@ClrIntrinsic` binding across the assembly boundary — kotc carries
    the annotation as UNCONDITIONAL, opaque ref.dll metadata (`attrsJson` is not gated on `@InlineOnly`), and
    bir2cir substitutes the plain call to the bound BCL member. No code change.
  - Regression cases: `cases/il-charseqlenref`, `cases/il-xmodtopval`, `cases/il-inlonlyintr`.
- **bir2cir/ilemit ([tmyt/dotkt#93]/[tmyt/dotkt#71]/[tmyt/dotkt#94]/[tmyt/dotkt#95], area:bir2cir/ilemit): a family of numeric/equality miscompiles.**
  - **#93 numeric widening** — `Byte`/`Short`/`UByte`/`UShort` arithmetic (and `inc`/`dec`/`unaryMinus`) dropped the
    operator's DECLARED return type, so the value truncated to the narrow left operand on box/narrow-store
    (`(100.toByte())+(100.toByte())` → `-56` not `200`; `(255u as UByte).inc()` → `256` not `0`).
    `PrimitiveOperatorLowering` now wraps the lowered bin/unary/inc op in a `conv` to the frontend-resolved return
    type (`dynRet`) for the narrow/char owners — generalizing the pre-existing `Char` precedent (`Byte`/`Short` → `Int`,
    `UByte`/`UShort` → `UInt`). Full-width owners stay bare.
  - **#71 ilemit unsigned conv arms** — `EmitConv` gained the `Conv_U1`/`U2`/`U4`/`U8` arms for `UByte`/`UShort`/`UInt`/`ULong`
    targets (previously a `default:` throw that aborted the whole compile); required by the #93 widening and by explicit
    `.toUByte()`/`.toUInt()`/… conversions.
  - **#94 unsigned shr** — `UInt`/`ULong` `shr` now lowers to `>>>` (ilemit `Shr_Un`, zero-filling) instead of the
    sign-propagating `>>` (`UInt.MAX_VALUE shr 1` → `2147483647` not `4294967295`). `shl` is bit-identical and unchanged.
  - **#95 structural float equality** — a STRUCTURAL `==` over two `Double`/`Float` (data-class `equals`/`hashCode`) now
    routes to the total-order helper (`clrDoubleEquals`/`clrFloatEquals`: `NaN == NaN` true, `+0.0 != -0.0`) instead of
    IEEE `ceq`, restoring the equals/hashCode contract. A DIRECT `a == b` stays IEEE (`ieee754equals`) — unchanged.
  - Regression cases: `cases/il-bytewiden`, `cases/il-unsignedshr`, `cases/il-structfloateq`.
- **stdlib: `copyInto` is now overlap-safe (#97).** All nine `copyInto` actuals (generic `Array<T>` +
  the 8 primitive arrays) bind to `System.Array.Copy` (memmove) instead of a naive forward element
  loop, which clobbered source slots on an overlapping self-copy with `destinationOffset > startIndex`.
  This silently corrupted `ArrayDeque.add(index, elem)` (an in-place right shift). (`_ArraysClr.kt`)
- **stdlib: `Double/Float.roundToInt`/`roundToLong` round half-up toward +inf (#103).** They now
  implement `floor(x + 0.5)` (ties: `2.5→3`, `-2.5→-2`, `0.5→1`, `-0.5→0`) instead of delegating to
  `kotlin.math.round` (banker's ties-to-even). NaN throws `IllegalArgumentException`; out-of-range
  saturates to `Int`/`Long` `MIN`/`MAX`. `kotlin.math.round` itself stays ties-to-even. (`MathClr.kt`)
- **stdlib: `CharArray.copyOf(newSize)` zero-fills grown slots with the null char `'\u0000'` (#128),**
  not a space (`U+0020`) — the Kotlin contract fills grown slots with the element type's default
  value (the null char for `Char`). (`_ArraysClr.kt`)
- **kotc ([tmyt/dotkt#66]/[#67]/[#68]/[#69]/[#70], umbrella [#72], area:kotc): lower five fail-loud
  callable-reference / capture / delegate shapes the frontend accepts (stop aborting the compile).**
  Each was a whole-compile abort on frontend-accepted IR; all now lower to pure Kotlin BIR facts (bir2cir
  owns any CLR/coroutine transform). (#66) a callable reference to a `lateinit var` / `@ClrField` property
  (`b::name`, `Box::name`) — the lifted `KProperty` class now reads/writes the plain backing field
  (`lateinitGet`/`field`/`setFieldExpr`) instead of a non-existent `get_/set_` accessor slot. (#67) a
  reference to a `suspend` function (`::work`, `d::apply`) is emitted as a `newSuspendLambda` adapter (the
  suspend lambda `{ a -> target(a) }` with a `suspendCall`-tagged body; bir2cir builds the `SuspendLambda`
  SM), and `kotlin.reflect.KSuspendFunctionN` now erases to a suspend `fn` type like `KFunctionN` — a plain
  suspend `newDelegate` had no cold-suspend lowering and the reflect type-token leaked to ilemit. (#68) a
  local class / object expression that WRITES a captured outer `var` now shares the enclosing frame's heap
  ref-cell (the mutated capture is promoted by `computeRefCells` before the lift). (#69) a local class
  capturing an enclosing TYPE PARAMETER is lifted GENERICALLY (reified CLR generics) — the object-literal
  generic-capture scan is reused, and a local class being DENOTABLE (`val l: L`, member access `l.x`),
  `ownerSpec`/birType now name the constructed `L<T>`. (#70) a TOP-LEVEL delegated property with an
  arbitrary `getValue`/`setValue` provider (`val x by Provider()`) routes through the static
  `x$delegate.getValue/setValue` with a null thisRef (only member/local delegated properties were routed
  before). Regression cases: `cases/il-{lateinitref,suspendref,writecapture,genlocalclass,topdeleg}`.

## 0.9.6-rc7 (2026-07-18)

A large compiler-correctness release. The kotlinx.coroutines CLR port now compiles through the
Kotlin frontend + the entire bir2cir layer (cold-core suspend lowering fires; all 108 CIR files
emit) and advances into ilemit; the remaining ilemit-stage work to make it fully compile+run
(abstract/interface/cross-member suspend cold-lowering completion + the covariance/variance-erasure
representation) is tracked under #85 and moved to 0.9.7. Highlights of what landed: the inline-splice
family (Set A #60–#63, the §4.4ii suspend-carrier + cold-SM nested-closure capture families, member
inline fake-override splicing #87); suspend cold-lowering (Defect A/B, #78/#80/#82, catch-hoist,
COROUTINE_SUSPENDED + coroutineContext binding, splice-local spill); #73 atomic-wrapper cross-module
re-import; #76 generic-base type-arg carriage; #77 concrete-collection loadability (ArrayDeque et al.);
#81 class delegation `$$delegate_0`; #83 interface companion members; #24/#36/#44 correctness; plus
packaging/docs (#50/#53/#54). The nullable value-type generic representation design is settled in #86
(object-erasure) for 0.9.7.

### Fixed

- **bir2cir ([tmyt/dotkt#80] residual, area:bir2cir): an ALREADY-OWNER'd `COROUTINE_SUSPENDED` read now canonicalizes.**
  The #80 fix rebinds the top-level val `COROUTINE_SUSPENDED` (`kotlin.coroutines.intrinsics`) to its declaring
  `IntrinsicsKt` owner, but only handled the OWNER-NULL emission. The real kotlinx.coroutines port surfaced a variant it
  missed: a NON-suspend reader (`DispatchedCoroutine.getResult(): Any?`) emits the read ALREADY-OWNER'd —
  `callStatic owner=kotlinx.coroutines.Builders_commonKt method=COROUTINE_SUSPENDED prop:get args:[]` (kotc stamps the
  reader's own file class, not owner-null) — so `MemberCallSubstitution`'s owner-null-only rewrite slipped it through and
  the owner-ful non-CLR path merely renamed the accessor, leaving ilemit with `kotlinx.coroutines.Builders_commonKt.
  get_COROUTINE_SUSPENDED not found` (15 sibling nodes normalized correctly). The COROUTINE_SUSPENDED canonicalization is
  now hoisted ahead of the owner-dependent branches and rebinds BOTH shapes (owner-null and already-owner'd) to
  `IntrinsicsKt.get_COROUTINE_SUSPENDED`, static + argless-guarded, regardless of the owner kotc stamped. Non-suspend
  readers never reach SuspendColdLowering's SM-body canonicalization, so this is their only rebind site.
  Gate: `cases/il-suspendintrinsicowned` (a non-suspend `getResult`-shape member reading the intrinsic val).

- **kotc ([tmyt/dotkt#88], area:kotc/area:bir2cir): splicing an inherited member `inline fun` on a GENERIC owner.**
  When an inherited member `inline fun` is spliced (a lambda arg → the same-module splice path) and its OWNER class is
  GENERIC — `IntBox : Container<E>` calling `Container.transform` — kotc's F2A guard omitted the owner's type args because
  the dispatch receiver's static class (`IntBox`) is not the owning class (`Container`). The spliced body's
  `tv{scope:type,0}` (the owner's `E`) then stayed OPEN, so ilemit typed the dispatch temp as the bare open generic →
  `BadImageFormatException`. kotc's F2A now carries the owner's args from the CORRESPONDING-SUPERTYPE instantiation
  (`Container<Int>` seen through `IntBox`), computed substitution-aware + transitively via
  `AbstractTypeChecker.findCorrespondingSupertypes` (`BirEmitter` gains `irBuiltIns` for the type-system context); the
  bir2cir F2B consumer (`recvs.dispatchTypeArgs`) was already implemented. The payload's `tv{scope:type,i}` now
  concretizes to the real call-site type. A TYPE-PARAMETER receiver whose bound fixes the owner (`T : Container<Int>`)
  is handled the same way. When the supertype instantiation CAPTURES a projected/star owner arg (`S : Slot<*>`) it is
  OMITTED (kept at the pre-#88 positional bind / ilemit object-fallback) rather than carried as a misleading
  `Base<Any>`. Gate: `cases/il-inheritedgenericinline` (value-type `Container<Int>`, reference-type `Container<String>`,
  and a `T : Container<Int>`-bound receiver; the value-type path being the one that BadImageFormats).

- **kotc ([tmyt/dotkt#87], area:kotc/area:bir2cir): an INHERITED member `inline fun` with a lambda arg now splices.**
  A member `inline fun` called through a SUBCLASS receiver — e.g. kotlinx.coroutines
  `ConcurrentLinkedListNode<N>.nextOrIfClosed`, a non-local-return-lambda inline fn invoked on a `Segment<S : Segment<S>>`
  — resolves in IR to a FAKE OVERRIDE whose `parent` is the subclass and whose `body` is `null`. kotc's inline-call
  emitter (`emitOwnerfulInlineNode`) took the `callInline` `owner` from `callee.parent` verbatim, so it named the
  SUBCLASS; but bir2cir's InlineSplice keys the `[KotlinInline]` payload under the REAL declaring class (`InlineBirStash`),
  so the lookup missed and the port build broke with `bir2cir: inline splice: cannot splice
  kotlinx.coroutines.internal.Segment.nextOrIfClosed (pc=1 ga=0): no [KotlinInline] payload found`. A fake override also
  has a `null` body, so the same-module splice-routing gate (`callee.body != null`) misrouted the call to the cross-module
  path. Now kotc resolves the fake override (`resolveFakeOverride`, the same normalization the ordinary member-call owner
  path already did at three sites but the inline path had omitted) for the callInline owner + all declaration facts, and
  routes the splice on the resolved declaration's body. The port now advances past bir2cir InlineSplice into the
  suspend-lowering + ilemit stages. Gate: `cases/il-inlineinherit` (a member inline fn with a non-local-return lambda,
  inherited through both a plain subclass and a self-bounded generic `Seg<S : Seg<S>>`, spliced at the subclass call site).

- **bir2cir ([tmyt/dotkt#78], area:bir2cir): a suspend call INSIDE a catch handler now lowers (catch-hoist).**
  Resuming into a CLR `catch` clause is illegal IL, so `SuspendColdLowering` used to refuse any suspend fun with a
  suspension in a catch/finally handler (`SuspensionsSupported`'s `inHandler` gate) — and, because the cold-entry ABI is
  coupled to body transformability, ONE such refusal (`SelectImplementation.processResultAndInvokeBlockRecoveringException`,
  a `catch (e) { recoverAndThrow(e) }`, kotlinx `Select.kt:723`) cascaded to the entire `select` family. bir2cir's new
  `HoistSuspendingCatches` (`toolchain/bir2cir/SuspendColdLowering.Normalize.cs`) lifts a suspending catch handler OUT of
  the CLR clause: the real catch only records the exception into an SM-field-backed capture, and the handler body runs as
  gated straight-line code (`if (__exc$N != null) { … }`) after the try, where the state machine segments its suspension
  normally. Finally-free trys only (hoisting past a finally would flip Kotlin's run-after-handler ordering). Gated in
  lockstep in `SuspensionsSupported`. Also fixes a pre-existing latent bug the newly-lowered value-returning try/catch
  exposed: an init-less value-type SM `var` (kotc's `tryExpr` value var) emitted a null-Int32 const; it now default-inits.

- **bir2cir ([tmyt/dotkt#80], area:bir2cir): `COROUTINE_SUSPENDED` intrinsic reads resolve everywhere.** The top-level
  val `kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED` was mis-owned by `MemberCallSubstitution` to the ENCLOSING file
  class (it is a val, absent from the top-level-fun index), so a bare `<FileClass>.get_COROUTINE_SUSPENDED` reached ilemit
  unresolved. Now bound to the canonical `IntrinsicsKt` owner at substitution time — covering EVERY reader, including the
  port's NON-suspend readers (`getResult(): Any?` in `CancellableContinuationImpl`/`Builders`) that never reach the SM
  transform. The former F2-only `SubstBlock` canonicalization is lifted into `Rewrite`/`RewriteNoSpill` so every SM-body
  path (incl. a direct user `suspendCoroutineUninterceptedOrReturn { … COROUTINE_SUSPENDED }`) normalizes to the SM's own
  `Suspended()` marker.

- **bir2cir ([tmyt/dotkt#82], area:bir2cir): a structured collection loop whose body spans a suspension now lowers
  (loop-flatten).** A `forArray` (`for (x in array)`) or `forEachInline` (inline `Iterable.forEach`) loop whose body
  contains a suspension carries implicit loop machinery (array + index; or an IEnumerator) and an element local that cross
  the resume point — but the straight-line SM cannot segment a structured loop, so a splice-generated element local
  reached ilemit as `load unknown var __inlsN$element`. bir2cir's new `FlattenSuspendingLoops`
  (`toolchain/bir2cir/SuspendColdLowering.Normalize.cs`) desugars such a loop to flat `label`/`brIf`/`goto` CFG with its
  loop temps made explicit `{k:var}`, so `CollectVarFields` spills them into SM fields and the resume re-enters across the
  back-edge. `forEachInline` uses a NON-generic `IEnumerator` (unconditional `viaNonGeneric`) so an open generic-param
  element never mints a broken `IEnumerable<!!T>` TypeBuilder token. A post-Build tripwire (`AssertLocalsResolved`) now
  converts any residual unspilled SM local into a loud bir2cir error instead of a distant ilemit `load unknown var`.

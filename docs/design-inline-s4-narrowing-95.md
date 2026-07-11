# #75 S4 × #95 — retire kotc mechanism-1, narrowed by escape analysis (Fable design 2026-07-11)

Final inline slice, two changes as ONE: (i) retire kotc's same-module inliner (mechanism-1
`inlineCall`/`spliceLambdaCall`, BirEmitterInline.kt), (ii) the #95 narrowing — source-inline ONLY when a
lambda arg has a NON-LOCAL EXIT (or a caller-inherited suspension); everything else emits a PLAIN call to
the inline fun as a real generic method taking a delegate (the Enumerable.Select/Where model — CLR
generics are reified, the JIT inlines small delegates, so splice only where semantics demand it).
S1(143ae02)/S2(3f6770c)/S3(d1b8c0f) landed; the bir2cir InlineSplice engine is battle-tested on the
cross-module shape. Re-anchor by name.

## 1. The escape predicate `lambdaNeedsSplice` (kotc, pure Kotlin-language analysis)
`hasEarlyReturn` is NOT reusable (it keys `==` on ONE target = the callee's own, special-cases the tail,
tracks no loops/boundaries). Write a fresh ~50-line IrVisitorVoid walker; keep hasEarlyReturn as the
carrier producer's helper. TRUE iff compiling `lambda` as a separate CLR method (a delegate) would change
semantics. Scan the lambda's expansion REGION = its own body + (recursively) the bodies of literal lambdas
that are non-noinline/non-crossinline args to NESTED INLINE calls (they expand with it); STOP at every
other function boundary (non-inline-arg lambda / crossinline / noinline / local fun / object literal —
their exits are their own by frontend rules). Three escape arms:
- (a) IrReturn whose `returnTargetSymbol` ∉ the region's collected function-symbol set (seeded with the
  lambda itself → `return@own` is LOCAL, an outer named fn / outer label = ESCAPE).
- (b) IrBreak/IrContinue whose `loop` ∉ the region's collected loop set (add loops BEFORE descending so
  breaks inside see them) — non-local break/continue (stable in the pinned 2.2.0).
- (c) a suspend CALL while the ROOT lambda is NOT suspend-typed (a suspend call in a non-suspend inline-arg
  lambda is legal only because inline expansion puts it in the caller's suspend frame; the delegate path
  would trap it in a non-suspend closure → miscompile). If the lambda IS suspend-typed, its suspensions
  are its own state machine's → delegate path correct → arm (c) disabled for that scan.
Conservative: any uncertain shape (e.g. IrReturnableBlock target) = ESCAPE (a false-positive costs code
size; a false-negative is a silent miscompile).

### Worked cases (the predicate composes per-level; residue is re-gated when the enclosing lambda compiles)
1. `a { b { return } }` (THE failure mode): scanning `a`'s lambda La, `b` is inline so its literal arg Lb
   is descended; the bare `return` targets the CALLER — ∉ {La,Lb} → ESCAPE → `a` splices; the fixpoint
   then splices `b`. A direct-arg-only predicate sees no return in La's own statements → delegate-compiles
   `a` → the return targets a frame no longer enclosing it → silently dropped. This is the proof the
   region must descend through inner inline args.
2. `f outer@ { g { return@outer } }`: for `f`, `return@outer` targets La (the seed) → LOCAL → `f` delegate.
   When La compiles as the closure invoke, `g` is re-gated: `return@outer` ∉ Lg → `g` splices INTO the
   invoke; the return becomes the invoke method's own return. Correct at both levels.
3. `for (x in xs) { ys.forEach { if (p) break } }`: forEach's `break` targets a loop ∉ localLoops → ESCAPE
   → forEach splices; the break's goto resolves against the caller's cfgLoopStack label (needs §4.1).
4. `xs.forEach { if (it==0) return@forEach; … }`: targets the seed → LOCAL → delegate path (the whole
   point of #95); the closure's own ret = continue-like. NOT an escape.
5. `suspend fun f() { listOf(1).let { delay(1) } }`: arm (c) → let splices, delay stays in f's SM. But
   `sequence { yield(1) }`: lambda is suspend-TYPED → arm (c) off → delegate (newSuspendLambda), unchanged.

### The call-site gate
`callNeedsSplice(call) = call.symbol.owner.isInline && regularParams(callee).zip(regularArgs(call)).any {
(p,a) -> a is IrFunctionExpression && !p.isNoinline && !p.isCrossinline && lambdaNeedsSplice(a) }`.
crossinline/noinline args never trigger a splice (frontend forbids their NLR) — they emit as plain closure
exprs in callInline.args, the engine binds a delegate temp.

## 2. kotc gate rewrites + delete list
| Site | Now | S4 end-state |
|---|---|---|
| Calls.kt:386 (same-module) | `-> inlineCall` | `callNeedsSplice ? inlineSpliceCallSameModule : FALL THROUGH to the plain call` (callee is a real emitted generic method; lambda args = newClosure/newDelegate) |
| Calls.kt:371-373 (invoke splice) | spliceLambdaCall | DELETE — inside a real-method inline fun, `action(x)` is the ordinary callInstance invoke |
| Calls.kt:441 (repeat) | inlineRepeat splicer | DELETE (S4b) — escaping repeat rides owner-less callInline (payload kotlin.repeat off ref.dll); non-escaping → the plain call → bir2cir RepeatInlineLowering delegate counter loop (exists) |
| Calls.kt:1154 (facadegen) | isInline && hasLambdaArg && extRecv==null | `&& callNeedsSplice` (keep extRecv gate) |
| Calls.kt:1168 (owner-less) | isInline && hasLambdaArg && isInlineOnly | `isInline && callNeedsSplice` — DROP @InlineOnly restriction: ANY body==null inline+lambda callee (forEach/map/filter) with an escaping arg emits owner-less callInline. CLOSES today's silent app-side `xs.forEach{return}` NLR drop. Non-escaping majority → plain call = status quo. |

`inlineSpliceCallSameModule`: the owner-less shape with `owner` from kotc's OWN naming (fileClassOf(callee)
for top-level — cross-FILE same-module works, stash spans all files; enclosing type name for a member) +
`recvs.dispatch` (new: expr(dispatchReceiver)). NO fallback slot.
**DELETE (kotc):** inlineCall, spliceLambdaCall, the :371-373 dispatch, inlineRepeat, inlineLambdas,
inlineLambdaTypeScopes, withTypeArgScope, the suspendIntrinsic stamp (:293-301), hasLambdaArg's IrGetValue
arm + inlineLambdas arm, the mechanism-1 typeArgSubst save/restore choreography. **KEEP:**
emitInlineLambdaCarrier, spliceBodyWithReturns + hasEarlyReturn + inlineReturnSubst (carrier producer),
selfSubst, spliceBody, emitStackBuffer.

## 3. Delegate path for non-escaping calls (all verified present)
The inline fun is a real generic method (same-module decls emit normally; rt.dll carries real bodies incl.
@InlineOnly; reified T free). Lambda args → lambda() → newDelegate/newClosure+synthClass; mutable captured
vars ref-cell-boxed (computeRefCells — write-through correct, VERIFIED not a blocker). Suspend-typed lambda
→ newSuspendLambda → SuspendLambdaLowering. `suspendCoroutineUninterceptedOrReturn{c->…}`: kotc emits the
plain callStatic … suspendCall:true in BOTH builds; SuspendColdLowering's existing F2 recognizer (by NAME
+ owner-null-or-stdlib + suspendCall) lowers it.

## 4. Engine (bir2cir InlineSplice) S4 hardening
1. **Hygiene fix (MANDATORY, silent-miscompile class):** CollectIds must collect ONLY `label`-node ids;
   ApplyIds remaps goto/brIf only when mapped. Today it collects goto/brIf ids too → a carrier body with a
   non-local `goto <caller-loop-label>` (arm-b break/continue, new #95 traffic) gets remapped to a DANGLING
   id. Invisible in the S3 corpus (payload bodies self-contained); carriers are not. FIX FIRST.
2. **Overload-key widening (D6):** `owner|name|pc|ga` collides across forEach/count receiver families →
   poison → error once fallback dies. Widen to `owner|name|pc|ga|recv0` (recv0 = first param's type FQN,
   "-" when none — a pure Kotlin FQN, layer-clean); kotc carries recv0 on callInline; stash + index key
   alike; post-widening collisions still poison → fail-loud.
3. **Dispatch receiver** (fix #3): kotc carries recvs.dispatch; stash recv=="dispatch" stops falling back;
   bind __inlsN$this temp, rewrite payload {k:this}; descend into newClosure captures VALUES but NOT into
   synthClass bodies/nested typeDefs (their `this` is their own).
4. **Forwarding + capture materialization** (fix #4): a lambda-param ref not a direct invoke — (i) arg of a
   NESTED callInline → substitute the carrier node itself (fixes stdlib map→mapTo forwarding,
   _Collections.kt:1559); (ii) in a newClosure/other non-invoke position → materialize the carrier as a
   newClosure (built from carrier params+body; legal — InlineSplice runs BEFORE ClosureSynthesis) bound to
   a temp. D3 shrinks to a hard error for the remainder (aliased `val f = action` re-invoke — not in stdlib).
5. **Fallback → fail-loud; DELETE the fallback slot.** Under #95 kotc emits callInline ONLY when the splice
   is REQUIRED, so every fallback is a miscompile. Delete the fallback field + swap plumbing; the engine
   throws with owner/name/pc/ga + reason. No dual-track.
6. **newDelegate-in-payload guard:** a payload body carrying newDelegate (a capture-less nested lambda
   lifted to the ORIGIN file class __lambdaN) dangles when spliced into another file. Guard: error unless
   origin file == consuming file (extend the :112 guard). If the stdlib flip hits it, engine-side synthesis
   (rebuild as a zero-capture newClosure), not a kotc special case.
7. Keep returnExpr guard :115 (audit hits during S4b; implement expr-position routing or restructure the
   stdlib member — never a compiler special case).

## 5. Where each mechanism-1 fix lands
1 crossinline/noinline→delegate: kotc callNeedsSplice (never trigger) + carrier emission. 2 positional type
subst / self-star→object: ALREADY engine-owned (SubstTv positional, name-capture-immune; kotc keeps only
`?: OBJ`). 3 dispatch-recv: engine §4.3. 4 nestedCapturesValue: engine §4.4. 5
suspendCoroutineUninterceptedOrReturn: bir2cir SuspendColdLowering F2 by FQN on the plain call, kotc stamp
deleted.

## 6. Payload narrowing
isInlineWithLambda (BirEmitterDeclarations.kt:864) gains `&& !it.isCrossinline` on the param predicate:
`mods.inline` = "∃ a splice-eligible lambda param" (Fn ∧ ¬noinline ∧ ¬crossinline). InlineBirStash keys
off mods.inline unchanged → crossinline-only/noinline-only inline funs stop carrying inlineBir → ref.dll
shrinks (nothing lost: such funs can never need a splice). [KotlinFunction] source-inline FLAG fidelity is
a SEPARATE channel (RoundtripMetadata), untouched. Measure ref.dll before/after.

## 7. Staging — S4a / S4b, each a fully-green deletion (no compat flag)
**S4a — narrow cross-module gates + harden engine (NO mechanism-1 deletion yet):** kotc add
lambdaNeedsSplice/callNeedsSplice; narrow :1154; rewrite :1168 to isInline && callNeedsSplice (drop
@InlineOnly — the forEach-NLR hole closes here); DELETE the fallback slot from both cross-module emitters.
engine: §4.1 hygiene, §4.2 key widen, §4.3 dispatch recv, §4.4 forwarding+capture, §4.5 fallback→error,
§4.6 newDelegate guard. Land the FULL §8 sample matrix. Green: verify-il (il-scope/il-use flip to delegate
calls — behavior identical, IL shape changes), roundtrip (forEach3 NLR via the widened owner-less gate),
differential. Deletes real surface (@InlineOnly restriction + fallback), duplicates nothing (mechanism-1
still owns same-module — same split as today, narrowed).
**S4b — retire mechanism-1 + stdlib flip:** kotc gate :386 → callNeedsSplice ? inlineSpliceCallSameModule
: fall-through; the §2 DELETE list incl. inlineRepeat + suspendIntrinsic stamp; payload narrowing (§6).
bir2cir: confirm F2 covers the self-build intrinsic shape, delete IsSuspendIntrinsicBlock's flag+string
arms in the same change. Verify: branch-local rt-stdlib IL/BIR diff old-vs-new (never a committed dual
track); make stdlib + full gates + roundtrip (suspend = canary) + differential; il-co*/il-collmore/il-coll*
(Duration/toComponents = dispatch-recv member inline).

## 8. Required gate samples (new cases/, green before the S4b deletion)
1 `il-inline-nested-nlr` — a{b{return}}: caller returns (print-after must NOT run). THE predicate trap.
2 `il-inline-outerlabel` — f outer@{ g{ return@outer } }: outer delegate + inner splice; post-label runs.
3 `il-inline-nlbreak` — for + forEach{break}/continue to outer loop; exercises §4.1 through a carrier.
4 `il-inline-ownlabel` — forEach{ return@forEach } + trailing accumulation: MUST take delegate path.
5 `il-inline-mutcapture` — var sum; xs.forEach{ sum += it }: ref-cell write-through on delegate path.
6 `il-inline-forward` — xs.map{ if(it==0) return emptyList(); it*2 }: escaping + payload forwarding (§4.4i).
7 `il-inline-suspend` — suspend fun f(){ repeat(2){ delay(1) }; listOf(1).let{ delay(1) } }: arm (c) splice
  into SM (+ il-suspendco/il-co* as the intrinsic gates).
8 Keep il-inline/il-inline2/il-xinline/il-scope/il-use green throughout (il-xinline pins fix #1).

## 9. Risks
1 **Predicate = correctness crux** — false-negative = silent NLR drop (the a{b{return}} class). Mitigation:
§8 matrix, the compositionality argument (§1), conservative-on-uncertainty, Codex review vs §1's cases.
2 **rt-stdlib self-build, zero XFAIL cushion** — the flip converts most same-module stdlib inline to
delegate calls + routes the escaping remainder through same-module-new engine paths. Mitigation: S4a
battle-tests every engine feature cross-module first; S4b stdlib IL/BIR diff reviewed before gates.
3 **Suspend × intrinsic convergence** — F2 must catch every shape the mechanism-1 stamp marked; a miss =
far-downstream coroutine miscompile. Mitigation: il-co* + roundtrip suspend canary; delete legacy arms
only after green.
4 **Carrier hygiene (§4.1)** — pre-existing latent bug made live by arm (b); fix FIRST in S4a.
5 **Perf deviation (record, not a bug):** a non-escaping inline lambda in a hot loop is now a per-element
delegate Invoke (the LINQ model). Record in docs/dotkt-semantics.md (a numbered section) with a measured
micro-benchmark at S4b; do NOT re-add splicing for perf (that recreates mechanism-1).

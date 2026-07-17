# #75 inline unification — the landed 2-axis splice rule (supersedes the #95 escape-narrowing design)

> **Status (2026-07-12): the escape-narrowing design this file once described was ABANDONED. #95 is
> SUPERSEDED.** What landed (`#75 S4b`, commit `3f33e51`) is the simpler **2-axis rule** below. This file is
> kept as the design record; the historical escape-analysis proposal is retired, not implemented.

## What landed

kotc's THREE historical inline mechanisms — `inlineCall` (body-visible same-module), the cross-module
`[KotlinInline]` splice (body carried on the ref.dll), and the `SCOPE_FUNCTIONS`/`inlineUse` hardcode
(`@InlineOnly` scope-fns whose body is absent at kotc's stage) — collapse into **ONE downstream bir2cir
splice engine**. kotc emits a plain `callInline` by identity for every `inline fun`; bir2cir owns the whole
splice.

**AXIS ① — does the call splice?**  `callNeedsSplice = callee.isInline && hasLambdaArg(call) &&
!isSuspendCoroutineIntrinsic(callee)`, where `hasLambdaArg` is true iff some argument is an
`IrFunctionExpression` passed to a **function-typed** (`TypeNode.Fn`) parameter. Rationale: an `inline fun`
with **no** lambda argument is a JVM local-optimization or a `reified`-carrier — on the CLR (reified
generics, a JIT that inlines small calls) it is pointless, so it emits a **plain call**. The two
`suspendCoroutineUninterceptedOrReturn`-family intrinsics are carved out (they must keep the plain-call
shape `SuspendColdLowering` keys on).

**AXIS ② — per lambda argument, carrier vs. real delegate.**  A normal or `crossinline` lambda becomes an
inline **carrier** (`emitInlineLambdaCarrier`, spliced by bir2cir). A `noinline` lambda becomes a **real
delegate** (emitted as an ordinary function value). `crossinline` is treated exactly like a normal inline
lambda here — the CLR has no "the lambda must not escape the caller frame" concern that `crossinline`
exists to police on the JVM.

`@InlineOnly` is NOT a splice trigger — on the CLR it is at most a courtesy
`[MethodImpl(AggressiveInlining)]` on a real method (tracked as #98); the body still routes through the one
splice path when (and only when) AXIS ① fires.

## Why the #95 escape-narrowing was dropped

The retired proposal spliced ONLY when a lambda argument had a **non-local exit** (`return@outer`, a
non-local `break`/`continue`, or a caller-inherited suspension) and otherwise emitted a plain call passing
the lambda as a **real generic delegate** (the `Enumerable.Select`/`Where` model). It was abandoned because
**delegating that much surface re-exposed the delegate path's latent bugs**: generic-closure IL, value-type
generic boxing, and the covariance-erasure family — the exact seams `#75` then had to close in the splice
engine anyway. Splicing *whenever a lambda is present* (AXIS ①) sidesteps the whole delegate-path cascade:
the lambda body is inlined into the caller frame, so non-local exits, captured mutation, and inherited
suspension are all naturally in-frame with no per-shape escape predicate to get wrong. The escape analysis
was strictly more machinery for strictly more bug surface — the 2-axis rule is both simpler and sounder.

## The splice engine (bir2cir)

The engine that consumes the `callInline`/carrier emission is `InlineSplice.cs` (+ `InlineBirStash.cs` for
same-module stash, `ReferenceMetadataIndex.cs` for the cross-module `[KotlinInline]` payload,
`ClosureSynthesis.cs`, `SuspendColdLowering.cs`): overload selection by full param-signature, capture /
`__outer` / `__self` receiver materialization, closure synthesis, `tv{scope:type}` substitution from the
`dispatchTypeArgs` carry, and a fail-loud guard on every descriptor-skew path (no silent fallback slot).

## §4.x section map (the code's `§4.x` labels have no other textual definition)

`InlineSplice.cs` comments key on a `§4.x` numbering carried over from the retired #95 spec; this is the
authoritative map:

- **§4.2 — overload disambiguation.** `owner|name|pc|ga` selects a candidate LIST; the unique candidate
  whose `params[i].type` DeepEquals the node's `paramSig[i]` wins (`ResolveInlinePayload` / `paramSigOf`).
- **§4.3 — dispatch (member) receiver bind.** For a payload classified `recv=="dispatch"`, `recvs.dispatch`
  binds to a fresh `<prefix>this` temp and `RewriteThis` rebinds the payload's own `{k:this}` (not descending
  into nested closures/type-defs — their `this` is their own).
- **§4.4i — lambda-arg forwarding.** A lambda param passed BY NAME into a nested stdlib inline call is
  converted to a nested `callInline` (`ForwardLambdaArgs` / `TryForwardCall`) and spliced at the fixpoint.
- **§4.4ii — carrier materialization.** A lambda param surviving in a non-invoke position (stored, returned,
  captured) is materialized into a real closure/SM value; a carrier that a fail-loud invariant forbids
  materializing (non-local-return, unlisted capture) refuses loudly.
- **§4.4iii — dead carrier-capture retire.** A nested carrier's capture descriptor whose lambda-param is no
  longer referenced is pruned before it mints a dangling ctor arg.
- **§4.6 — cross-module `newDelegate` refusal.** A cross-module payload carrying an origin-file lifted
  `__lambdaN` (`newDelegate`) is producer-private → fail loud (the W3 hidden-ABI export dissolves this).

## Cross-module inline MEMBER (#60 / W1) — kotc emits `callInline` unconditionally, bir2cir owns eligibility

kotc is **body-blind** at a cross-module member call: the klib is metadata-only, so it cannot inspect the
callee body to decide splice-eligibility, and a body-blind gate MISS is SILENT (the lambda becomes a real
delegate and its non-local `return` returns from the delegate frame). So the call-site gate in
`BirEmitterCalls.kt` emits an **owner-ful `callInline` for EVERY** cross-module inline member with a lambda
arg (`callee.body == null && callNeedsSplice(call) && dispatchReceiver(call) != null` → `emitOwnerfulInlineNode`)
— a facadegen-injected DotKt member AND a klib-stdlib member alike (the former `clrInjectedDotNetName != null`
and `extensionReceiver == null` gate conditions are DELETED). All remaining eligibility decisions move to
bir2cir's `RewriteGeneric`, which holds the `[KotlinInline]` payload and always **splices or fail-louds**
(`AssertNoUnsplicedInline`) — converting every residual gate-shape hole from silent-wrong to loud.

- **klib-stdlib member (dispatch receiver, `recv=="dispatch"`)** splices via §4.3 (gate case
  `il-inline-klibmember-nlr`: `Duration.toComponents { … return … }` returns from the caller).
- **member-EXTENSION dual-receiver (#23, `recv=="extensionParam"`)** rides through too. `InlineBirStash`'s
  single-valued `recv` lets `__self` SHADOW dispatch, so §4.3 never binds the dispatch and STEP 5 binds only
  the extension. A **pure-extension** body (reads only the extension `this` = `__self`, never `{k:this}`)
  splices soundly. A body that READS the dispatch receiver leaves a payload-frame `{k:this}` that would
  rebind to the caller's `this` — a silent miscompile — so `RewriteGeneric` **fails loud** when
  `payloadExt && recvs.dispatch != null && HasPayloadFrameThis(pBody)` (a `{k:this}` detector that mirrors
  `RewriteThis`'s descent: skips `typeDef`/`newSuspendLambda` whole and the `synthClass` key). Co-binding
  BOTH receivers (making #23 splice instead of refuse) is **W2**, out of scope for W1.

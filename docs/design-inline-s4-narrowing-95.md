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

# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

- **Stdlib correctness (bundle-6 ④): number-conversion parsing fixed to match JVM (culture + exception type).**
  - `String.toInt()/toLong()/toByte()/toShort()` now delegate to the base-10 radix implementation instead of the
    culture-sensitive `System.<T>.Parse`. They are strict base-10 (no whitespace/group-separator leniency) and, crucially,
    throw the **real Kotlin `NumberFormatException`** — so `"abc".toInt()` is catchable as `NumberFormatException` (and as
    its `IllegalArgumentException` supertype), instead of aborting the process with an uncaught `System.FormatException`.
  - `String.toDouble()/toFloat()` now parse with `CultureInfo.InvariantCulture` and reject the group separator `,`, so
    `"3,14".toDouble()` throws `NumberFormatException` (previously it silently parsed to `314.0` under a comma-decimal
    locale / `AllowThousands`). Failures surface as `NumberFormatException`. New il-gate case `il-strnum`; deviation
    recorded in `docs/dotkt-semantics.md`.
- **Stdlib correctness (bundle-6 ④): `Throwable.printStackTrace()` on a `Throwable`-typed receiver no longer NREs.**
  The JVM `java.lang.Throwable.printStackTrace` member is mapped onto `kotlin.Throwable` and shadows the stdlib
  extension, so app calls resolved to a member that (on the substituted `System.Exception`) dynamic-dispatched to a
  missing method → `NullReferenceException`. Declared a real `printStackTrace()` member (rule-3 body writing
  `stackTraceToString()` to `Console.Error`) on the Throwable class; a `Throwable`-typed receiver now routes correctly.
  (Subclass-typed receivers still need a bir2cir override-chain rule-3 resolution — reported.)
- **Gate XFAIL audit (bundle-6): restore verify-roundtrip + verify-differential baselines to accurate "Expected" state.**
  The coroutine machinery landed but two gate scripts still carried stale XFAIL reasons and one had a broken oracle.
  - **verify-differential — JVM-oracle startup crash (whole gate was red).** `kotlin-compiler-embeddable` 2.2.0 has an
    external runtime dep on `kotlinx-coroutines-core` (its IntelliJ `CoreApplicationEnvironment` references
    `kotlinx.coroutines.CoroutineScope`, not shaded under `org.jetbrains.kotlin.*`). The hardcoded oracle classpath
    omitted it, so `K2JVMCompiler` died at startup with `NoClassDefFoundError: kotlinx/coroutines/CoroutineScope`
    BEFORE compiling any sample → every JVM oracle output was empty → the gate reddened wholesale. A latent classpath
    gap, NOT a dangling kotlinx ref from the P1b purge (the samples are pure Kotlin). Fix: add the cached
    `kotlinx-coroutines-core-jvm` jar to `CCP`. Oracle runs; gate GREEN, DIFF set = {il-seq, il-collops2, m-b6, m-b9,
    m-b10}, all XFAIL-listed. Also re-attributed the stale `il-seq`/`il-collops2` reasons ("coroutine/SequenceScope-
    deferred", now false) to the true blockers mirroring verify-il (value-typed sequence nullability drop; cross-module
    default-arg drop on `windowed(3)`).
  - **verify-roundtrip — wire the runtime stdlib into the suspend sections + re-attribute the 3 RT_XFAILs.** `emit_il`
    fed ilemit no `--ref DotKt.Stdlib.dll`, so a suspend fun's CPS signature (`kotlin.coroutines.Continuation`, injected
    by bir2cir's suspend lowering) could not resolve; and facadegen/retarget could not LOAD the now-coroutine-referencing
    DotKt library to walk its type surface (empty meta → consumer unresolved). Fixed both (rt stdlib joins the ilemit
    `--ref` set + `$REFS`, and is dropped beside the emitted assembly). With the emit crash cleared, the 3 suspend
    sections surface their TRUE remaining cross-module gaps: `roundtrip`/`roundtrip-generic` = cross-module suspend
    cold-entry returns a `Task<T>` neither the caller SM nor `blockOn` awaits (runtime `InvalidCastException Task<T>→T`);
    `roundtrip-memext2` = ilemit `NotSupportedException` on a suspending call inside a `with` scope-function sub-expression.
    Reasons re-attributed; gate stays GREEN.

- **ilemit (bundle-6 P5 Phase-B): delete the dead coroutine/sequence CODEGEN — ilemit is now coroutine-free.**
  After the A2 ignition + the kotc CPS-engine deletion, NOTHING produces the old CPS/sequence CIR any more (the
  cold-core lowering in bir2cir synthesizes the state machine as ordinary `ContinuationImpl` CIR classes + a public
  `Task<T>` bridge carrying `suspendBridge`). ilemit's old state-machine codegen was therefore unreachable dead code.
  Removed `Emitter.Coroutines.cs` in full (~782 lines: `EmitCoroutine`/`steps`, `EmitCoroutineClass`/`coClass`,
  `EmitSequenceSm`/`sequenceNew`, `EmitCoSuspend`/`EmitCoSuspendClass`/`EmitCoSuspendIntrinsicClass`/`EmitCoTryEnd`
  and the `SmField`/`SmCtor`/`GenM`/`CtorOf` helpers) — relocating the still-live `DefineParamNames` to
  `Emitter.Metadata.cs`. In `Program.cs`: deleted the `Co*` type consts (Continuation/Context/Intrinsics/TypedCont/
  Builders/CancellableCont/ISeqStep/Seq), the `suspend fun -> Task<T>` KICKOFF signature rewrite (the bridge now
  arrives from bir2cir already `Task<T>`-typed), the `coClass`/`steps`/`sequenceNew` body-emit dispatch, and the
  now-orphaned SM-context fields (`_coFields`/`_coThis`/`_seqCounter`/`_smCounter`/`_coTryDepth`/`_coExit`) with
  their always-null read guards in the `this`/`local`/`var`/`setLocal` cases. In `Emitter.Expressions.cs`: deleted
  the `coSuspendedSentinel`/`sequenceNew`/`coSelfCont`/`coContext`/`coSelfCancellable` expression cases. KEPT: the
  `[KotlinFunction(Suspend)]` flag stamp (triggered by `suspendBridge` AND by ref-build `"suspend":true` decls;
  round-trip restore needs it) and the suspend THROW-STUB (a leftover `"suspend":true` method — the ref build, or an
  app/rt shape the cold lowering left untouched — emits a throwing stub). Pure dead-code deletion: stdlib
  jar/ref/rt all emit clean (ref suspend fns still stub), IL gate byte-identical GREEN (zero sample changes),
  verify-ktproj 9/9. Completes the design's "ilemit becomes coroutine-free" column
  (`docs/design-coroutine-cold-core-task-bridge.md` §8, §11 supersession).

- **kotc (bundle-6 P5 Phase-B): delete the dead CPS coroutine engine — kotc withdraws ALL coroutine lowering.**
  After the A2 ignition removed the `kotlin.sequences.sequence` special-case (its only caller), the entire
  `emitCoroutineBody` CPS state-machine family in `BirEmitter.kt` was unreachable dead code. Removed
  `emitCoroutineBody` + the `CoroutineBody` holder and every CPS helper (`emitCps`/`emitCpsValue`/`emitCpsBlock`/
  `emitScopeCps`/`emitWhenCps`/`emitTryCps`/`emitWhileCps`/`emitSuspend`/`emitSuspendIntrinsic`/`spillExpr`/
  `store0Local`/`coAwaitable`/`collectCpsVars`/`coStmtsOf`/`coReturnJson`/`coFresh`/`coUnsupported`/
  `isSuspendIntrinsic`/`scopeSuspendCall`) plus the CPS mutable state (`coState`/`coLabelN`/`coFields`/`coSpill`/
  `coSpillFields`) and the now-dead `coSpill` residual-render hook in `expr()`. ~337 net lines removed. kotc now
  holds ZERO coroutine LOWERING: the only coroutine code left is FACT emission — `"suspend":true`+resultType on
  decls, `"suspendCall":true` at call sites, `suspendLambdaNew` for suspend lambdas — driven by the kept
  `containsSuspend`/`isSuspensionCall`/`isAwaitIntrinsic` helpers, which bir2cir consumes to build the
  `ContinuationImpl` state machine + public `Task<T>` bridge. Pure dead-code deletion: IL gate byte-identical
  green (zero sample changes), verify-ktproj 9/9. Completes the design's kotc-column goal
  (`docs/design-coroutine-cold-core-task-bridge.md` §8).

- **bir2cir (bundle-6 P5 BUG A): make a lifted GENERIC anon-object reach its enclosing class's privates and
  self-instantiate.** A Kotlin `object : I { … }` inside a class body (e.g. `FilteringSequence.iterator()`'s anon
  `Iterator`) is emitted by kotc as a SEPARATE top-level CLR class (`<>dotkt_obj*`) that captures the enclosing
  instance as `__outer` — but on the CLR a separate top-level class cannot reach another class's `private` members
  (legal on the JVM via nesting) → `System.MethodAccessException` on `FilteringSequence.get_sequence()`. Two new
  GLOBAL bir2cir passes, run in non-ref builds after the suspend passes and before type lowering:
  (1) `CrossClassPrivateWidening` — for every local type, collect the members it reaches cross-class
  (callInstance/callStatic/field/setField whose owner names a DIFFERENT local type) and widen any matching PRIVATE
  member (method / field / property get_/set_ accessor) to `internal` (valid Kotlin can never author a cross-class
  private access, so every such access is compiler-lifted → widening exactly those is minimal and correct;
  generalizes `SuspendColdLowering.WidenPrivatesAccessedBySm`).
  (2) `GenericSelfInstantiation` — a lifted GENERIC anon-object emits its SELF instance accesses with the BARE type
  name (`<>dotkt_obj144`, no type args) → runtime "method/type not fully instantiated"; derive the constructed self
  `<>dotkt_obj144[gp:T]` for those executable instance accesses (a NORMAL generic class already emits the
  instantiated token — kotc emits the FQN identity, bir2cir derives the CLR instantiation). Lazy `Sequence`
  pipelines over REFERENCE element types now construct + iterate; the IL gate stays green (il-seq now passes formal
  ilverify). VALUE-typed sequences still fail on a separate kotc `T?`→`T` nullability drop (see below).
- **kotc (bundle-6 P5): fix the object-expression generic-capture OVER-capture regression that broke the rt-stdlib build.**
  The `object : Box<T>` generic-capture support (`typeDef` capture-augmentation) computed a lifted class's captured
  enclosing type params by REGEX-scanning the rendered member JSON for `gp:` tokens, for EVERY class. Two defects:
  (1) it ran for normal named declarations too, and (2) a `gp:T` that appears only inside a call node's `sig`
  metadata (e.g. `ArrayList<E>.addAll` calling the inline helper `clrCollAddAll<T>`) was mis-read as an enclosing
  capture. Result: `kotlin.collections.ArrayList`/`HashSet`/`LinkedHashSet`/`AbstractCollection`/`AbstractMap`/
  `ArrayDeque`/`ClrMapSnapshotSet` emitted a spurious extra type param (arity 2) → bir2cir lowered `ArrayList<E>` to
  `List<E,T>` → ilemit `cannot resolve System.Collections.Generic.List\`2` → rt-stdlib build failed. The detection is
  now gated to the lifted object-literal path only (`typeDef(..., liftedAnon = true)`) and computed STRUCTURALLY from
  the class's real type positions (supertypes, own type-param bounds, captured-var field types, ctor/member parameter +
  return + `is`/`as` body-operand types) rendered through `birType` — never from call-node metadata; a member's own
  generic params are excluded. `ArrayList` etc. are back to arity-1 and the rt build emits clean.
- **kotc (bundle-6 P5): save/restore `captureSubst` in the object-literal lift so a nested capture of the same outer
  var is not clobbered.** When a generic-capturing `object { ... }` is nested inside a capturing closure that captures
  the SAME outer variable (e.g. `element` in the `Sequence`/`asSequence` builders), `blockExpr` blindly `remove`d the
  captured decl's substitution after lifting the object, dropping the enclosing closure's `this.element` binding — so
  the capture VALUE at the `new` site mis-rendered as a bare `local element` (ilemit `load unknown var element`). It
  now saves the prior binding and restores it, mirroring the closure path. This unblocked the rt stdlib
  `Sequences`/`SequencesKt` emit.
- **bir2cir (bundle-6 P5): scope-aware SM-field spill for shadowed same-name locals of DIFFERENT types.**
  A coroutine body may declare the same `var` name in DISJOINT scopes with DIFFERENT types — e.g.
  `SlidingWindow.windowedIterator`'s `var buffer = ArrayList<T>()` in the `gap >= 0` branch vs
  `var buffer = RingBuffer<T>(...)` in the `else` branch. `SuspendColdLowering.CollectVarFields` keyed the
  state-machine field by NAME, so it collapsed the two declarations to a SINGLE field of ONE type; the other
  branch's members (`buffer.expanded()/isFull()/removeFirst()`) then resolved against the wrong-typed field
  (`ilverify` `StackUnexpected: found RingBuffer<T0> expected List<T0>` and its inverse; runtime
  "Iterator has failed" on the windowed path). A new `DisambiguateShadowedVars` pre-pass (runs in `FunGen.Build`
  before field collection) alpha-renames the shadowing declarations (`buffer` / `buffer$2`) so each
  distinct-typed declaration gets its OWN correctly-typed SM field, binding every `local`/`setLocal` reference to
  the declaration lexically IN SCOPE via a scope-frame stack (one frame per `block`/`valueBlock`/`try`
  body/catch/finally, resolved innermost-first; a `var` `init` is bound in the outer scope; nested
  lambda/closure and `suspendCoroutine`-intrinsic subtrees are skipped as they own their own scope). Only names
  whose declarations disagree on type are touched (the common case is byte-identical), and the pass returns the
  input untouched — operating on a `DeepClone` only on an actual clash — so the retained rt-stdlib original body
  is never mutated. The `windowedIterator` SM now emits `buffer : List<T>` + `buffer$2 : RingBuffer<T>`; the three
  RingBuffer↔List `StackUnexpected` findings are eliminated. (General shadowed-same-name-locals correctness fix,
  common in generated/inlined stdlib code; exposed by the windowed sequence path. `chunk`/`collops2` remain
  XFAIL on SEPARATE pre-existing blockers — the SequenceBuilder cold-resume "Iterator has failed" machinery and
  an earlier `collops2` op — not the shadowed var.)
- **kotc (bundle-6 P5): object expressions / anonymous objects that CAPTURE an enclosing generic type parameter.**
  An `object : Box<T> { … }` (or an inlined object whose supertype/captures resolve to the enclosing `T`) is
  flattened to a top-level synthetic class; on the CLR generics are reified, so that class must itself be GENERIC
  over the captured `T` and be instantiated with the enclosing arg at the `new` site — exactly as the closure/SAM
  paths (`closureNew`/`samNew`) already do. Previously kotc threw `unsupported("an object expression that captures
  an enclosing generic type parameter")`, so the real stdlib `sequence()`/`asSequence()`/`asIterable()`/…
  (`Sequence { iterator(block) }` = a generic-capturing anon object after SAM lowering) emitted throw-stubs.
  Fix: `typeDef` now derives a lifted class's captured type params from the `gp:` tokens `birType` actually
  rendered into its members (interfaces/fields/ctors/methods) minus the class's own params — a single-render,
  substitution-robust detection: an inline param monomorphized to a concrete type leaves no `gp:` token (so it is
  dropped, e.g. `inline fun <T> Iterable(...)` inlined into `ByteArray.asIterable`), while one remapped to another
  `gp:X` yields `X`. The object-literal construction site (`blockExpr`) brackets those tokens onto the constructed
  type. Non-generic object expressions and closure/SAM are unchanged. Repro: `cases/il-objgen`.
- **bir2cir (bundle-6 P5 Phase-A3): `sequence{}` cold path drive-to-green — captured extension receiver + nested field-assignment.**
  Three fixes that unblock the rt-stdlib build from EMITTING the real `SequenceBuilderIterator` cold code
  (it was aborting at `ilemit`):
  - **Captured extension receiver (`__self`/`__outer`).** A `sequence{}` inside an extension fun
    (`fun <T> Sequence<T>.ifEmpty(...) = sequence{…}`) captures the enclosing receiver. kotc names the capture
    `__outer` (its `<this>` capture-field convention) yet, in the lambda body, reads that receiver as `local __self`
    (the enclosing static extension's receiver-param name). `SuspendLambdaLowering` now (a) at the SM-construction
    site sources the `__outer` capture value from the enclosing method's `__self` param when it is a static
    extension (an instance method still uses `this`), and (b) `SuspendColdLowering` rewrites a body `local __self`
    with no `__self` field to the `__outer` capture FIELD read — reconciling the two names to one captured value
    (was: `load unknown var __self` at ilemit).
  - **Nested field-assignment redirect.** A `setLocal`/`var` that assigns a SPILLED SM variable but sits INSIDE an
    expression subtree (e.g. the `index++` post-increment lowered to `valueBlock { var <unary> = index; index =
    index+1; <unary> }`) is reached via `Rewrite`, not the statement-level `EmitStmt`, so its field-assignment was
    left as a bare `setLocal` to an SM field (was: `store unknown var index` at ilemit). `Rewrite`/`RewriteNoSpill`
    now redirect a nested `setLocal`/`var` of a spilled name to `setField`.
  - **`resumeWith` Result accessor erasure.** The `Continuation<object>` ABI erasure retypes
    `resumeWith(result: Result<Unit>)` to `Result<object>`, but the body's `result.getOrThrow()` stayed
    `getOrThrow<Unit>` — an invariant `Result<Unit>` receiver mismatching the `Result<object>` we pass
    (InvalidProgramException). `ContinuationErasure` now re-instantiates a Result-accessor whose extension
    receiver is the erased `result` local at `object`, and promotes a stale `void`/Unit retType so the discarded
    `getOrThrow` result is popped.
- **kotc (bundle-6 P5 Phase-A2 IGNITION): `sequence{}`/`iterator{}`/`yield` are now ORDINARY library code.**
  Deleted every trace of the `sequence`/`yield` builder from the frontend: (1) the
  `kotlin.sequences.sequence` special-case in `BirEmitter.call()` (which pulled the block, rejected
  captures, ran the CPS engine, and emitted a `sequenceNew` CLR-sink node); (2) `isYield`/`isYieldAll`
  and their `emitSuspend` `coYield`/`coYieldAll` branches; (3) the `@RestrictsSuspension` exclusion in
  `suspendLambda()`. `sequence(block)` now resolves as a normal stdlib call over the real cold core
  (`SequenceBuilderIterator`); `{ yield(...) }` emits `suspendLambdaNew` and flows through bir2cir's
  `RestrictedSuspendLambda` state machine; `yield`/`yieldAll` become ordinary virtual suspend member
  calls. kotc holds ZERO knowledge of the `sequence`/`yield`/`yieldAll` symbols (the only residual FQN
  reference is in the now-unreferenced CPS engine, deferred to Phase-B). Downstream follow-up: bir2cir
  `SuspendLambdaLowering` must supply `{"k":"this"}` (not `{"k":"local","name":"__outer"}`) for the
  enclosing-`this`/extension-receiver capture at SM construction — the rt-stdlib build's internal
  capturing `sequence{}`s (`ifEmpty`/`shuffled`/`runningFold`/…) block on it.
- **ilemit (bundle-6 P5): `class D<T> : Base<T>()` — generic base instantiated over the derived's OWN type param.**
  A generic class whose base is a generic type constructed over its own type parameter (the
  `SequenceBuilderIterator<T> : SequenceScope<T>()` shape) crashed at load/JIT with a "not fully instantiated" /
  `InvalidProgramException`. Two sites emitted the OPEN base definition where the CONSTRUCTED base was required:
  - **Base-ctor call (`EmitCtorBody`).** The local-base `: base(...)` branch called `SelectCtor(...)` — the
    `Base<>` open `ConstructorBuilder` — yielding `call Base``1::.ctor` while the class `extends Base``1<!T>`. It now
    anchors the open ctor onto the constructed parent (`ti.TB.BaseType`) via `TypeBuilder.GetConstructor` when the
    base is a generic instantiation, mirroring `closureNew`.
  - **Inherited generic-base member calls (`ResolveMethod`).** A `callInstance` to a member declared on the generic
    base (`d.x` -> `Base<>::get_x`, both the `D<int>` non-self and the `D<T>` self case) fell through the
    `TypeBuilder.GetMethod` `ArgumentException` catch to the OPEN `MethodBuilder`. New `AnchorInheritedOnBase` walks
    the constructed receiver's base-CLASS chain for the instantiation whose generic def is the member's declaring
    type and re-anchors via `TypeBuilder.GetMethod` (interface members, absent from the class chain, keep the prior
    open fallback). Unblocks the cold-core `sequence{}` runtime. New gate sample `cases/il-genbase`.

- **bir2cir (bundle-6 P5 Phase-A1b): cold-transform `yield`/`yieldAll` — generic-class ABSTRACT/OVERRIDE suspend
  members.** `SuspendColdLowering.IsMemberShapeEligible` previously deferred (a) suspending members of a GENERIC
  enclosing class and (b) abstract/open/override/virtual members. It now ADMITS them, closing the last capability gap
  before kotc ignites the cold-core sequence path. Three pieces:
  - **Generic-class instance-member SM.** The SM is now generic over the enclosing class's type params (plus the
    member's own): `$this` is typed as the CONSTRUCTED self (`SequenceBuilderIterator[gp:T]`), the SM's fields/label
    are typed in `T`, and the cold entry constructs `new <SM>[gp:T](this, …)`. `_smAllTps` (owner ++ method params)
    drives both the SM's declared type-params and every `_smTypeInst` reference; `_selfType` is the constructed self.
  - **Virtual/abstract/override cold-entry lockstep.** An ABSTRACT member (`SequenceScope.yield`) emits an ABSTRACT
    cold entry `yield$dotkt_suspend` (Virtual|Abstract, no SM). An OVERRIDE (`SequenceBuilderIterator.yield`) emits
    `yield$dotkt_suspend` marked `override:true` (Virtual, reuses the base slot by name+sig — no explicit
    `DefineMethodOverride`, Codex-confirmed) + its SM. So a virtual `scope.yield(x)` cold call dispatches to the
    iterator's override at runtime. The public Task bridge is suppressed for these internal members.
  - **Overload disambiguation.** `FunKey` gains a param-signature component (`SigOf`) so `SequenceScope`'s THREE
    `yieldAll` overloads (Iterator/Iterable/Sequence, all arity-1) each register + get a UNIQUE SM class name
    (`SequenceScope_yieldAll_Iterable$sm` / `_Sequence$sm`, from the param simple-names); the cold-entry NAME stays
    `yieldAll$dotkt_suspend` (IL overloads resolved by param type). `IsResolvable` matches suspend calls by
    (owner, name) since the call site carries no resolved overload signature.
  - **Additive in the rt-STDLIB build.** kotc's pre-ignition `@RestrictsSuspension` builder path (`sequence{}` /
    `iterator{}`, e.g. `SlidingWindow.windowedIterator`, BirEmitter.kt:2169-2173) still calls the Task-shaped
    `SequenceScope.yield`/`yieldAll` BY NAME. So in the rt-stdlib build (`baseIsLocal`) the original suspend method is
    RETAINED alongside the added cold entry (removing it would break the stdlib build); an APP build keeps the
    replace-with-bridge behavior. The public Task bridge is also skipped in the stdlib build (its RootContinuation/TCS
    sinks are the coroutine primitives being DEFINED there, not external refs). ref/rt stay symmetric on the Task
    `yield`; rt additionally carries the cold entry a consumer resolves via the ref.dll Suspend flag. The kotc-ignition
    handoff (delete BirEmitter.kt `:3329` sequence special-case + `isYield`/`isYieldAll` `:1640-1657`) retires the old
    path; the retained Task originals then go dead and a follow-up drops them. Still DEFERRED (reported): a member that
    is BOTH generically own-parameterized AND on a generic class (`DeepRecursiveScope<T,R>`'s
    `<U,S> DeepRecursiveFunction<U,S>.callRecursive`) — an untested type-param-union combination not needed for the
    sequence path. Verified: `make stdlib-ref stdlib-rt` clean, `yield`/`yieldAll` emit abstract + override cold
    entries + generic SMs; gate GREEN (existing suspend samples unchanged; `chunk`/`seq`/`collops2` stay XFAIL — kotc
    not yet ignited).

- **bir2cir (bundle-6 P5 Phase-A capability 1): lower the inline `suspendCoroutineUninterceptedOrReturn { c -> … }`
  intrinsic to a real cold suspension point.** kotc's IR inliner leaves the `@InlineOnly` intrinsic as a `valueBlock`
  whose result is the fake `throw NotImplementedError("Implementation of suspendCoroutineUninterceptedOrReturn is
  intrinsic")`, with its `{ c -> … }` block materialized as a separate closure class (captured into a dead
  `var __inlN`). `SuspendColdLowering` now recognizes that marker (the NotImplementedError message — the only stable
  discriminator, since the frontend never invokes `block` so no `suspendCall` tag exists), resolves the closure
  class's `invoke` body, and INLINES it into the state machine: the closure's captures rewrite to `$this`, the block's
  `c`/continuation param binds to the SM itself (a new `smSelf` node that survives the `this`->`$this` member rewrite —
  the SM IS a `Continuation`), and the block's tail value becomes the suspension result (`COROUTINE_SUSPENDED` ->
  return SUSPENDED with a synchronous-value fast path). This is kotc's live `emitSuspendIntrinsic`/`coSelfCont`
  (BirEmitter.kt:1669-1688) re-expressed over the cold SM. Verified on a fixture: a `yield`-shaped body
  (`suspendCoroutineUninterceptedOrReturn { c -> nextStep = c; COROUTINE_SUSPENDED }`) lowers to
  `$this.nextStep = this; label = 1; return COROUTINE_SUSPENDED`. Capability only — dormant until kotc stops routing
  `sequence{}` through its CPS engine.

- **bir2cir (bundle-6 P5 Phase-A capability 2): target `RestrictedSuspendLambda` for `@RestrictsSuspension`
  blocks.** `SuspendLambdaLowering` previously hardcoded the SM base to `SuspendLambda`. It now reads
  `@kotlin.coroutines.RestrictsSuspension` off the ref.dll (a new `ReferenceMetadataIndex.HasRestrictsSuspension`,
  scanning the BINARY-retained attribute) and, when a suspend lambda's RECEIVER (its create()-bound param) is such a
  scope — `sequence{}`'s `SequenceScope` — emits the SM base as `RestrictedSuspendLambda`
  (`ContinuationImpl.kt:131`) instead. Both bases share the 2-arg `(arity, completion)` ctor + `create()` protocol;
  the restricted base pins `EmptyCoroutineContext`. Verified on a fixture: a lambda with a `SequenceScope` receiver
  gets `RestrictedSuspendLambda`; a plain-typed receiver keeps `SuspendLambda`. Dormant until kotc emits
  `suspendLambdaNew`.

- **bir2cir (bundle-6 P5 Phase-A capability 3): run the suspend cold transform in the rt-stdlib build.**
  `SuspendColdLowering`/`SuspendLambdaLowering` were app-build-only (gated off in the rt-stdlib self-build). They now
  run in the rt build too (still skipped in the REFERENCE build — metadata-only), so genuine cold coroutine bodies in
  the stdlib can cold-transform. The rt-stdlib's CLR-interop suspend fns (`kotlin.clr.await`/`delay`, file-class
  `kotlin.clr.CoroutinesKt`) are NOT cold bodies — `await` is a facadegen call-site marker whose DEFINITION stays a
  plain suspend declaration for ref/rt signature symmetry — so they are excluded from the transform inside
  `ApplyAll` (Codex-confirmed rt-gate decision). `SequenceBuilderIterator.yield`/`yieldAll` remain correctly deferred
  by the v1 shape gate (generic enclosing class + virtual/override cold-entry lockstep) — making them actually
  cold-transform is a follow-on shape-gate expansion, NOT this gate change. Verified: rt-stdlib rebuilds clean;
  await/delay/yield/yieldAll definitions unchanged.

- **bir2cir/diagnosis (bundle-6 P4 genuine-async): root-caused `il-cobuild` printing `0` instead of `25` to a
  compiler bug OUTSIDE bir2cir — boxed Kotlin `enum` entries lose reference identity, breaking the
  `COROUTINE_SUSPENDED` sentinel.** The bir2cir cold-core transform is verified correct: dumping cobuild's CIR shows
  `compute$sm`/`total$sm`/the `blockOn` lambda SM each return the right boxed value (9, 16 → 25), the `Task.await()`
  awaiter dance (GetAwaiter/IsCompleted/OnCompleted/GetResult) is well-formed, and the suspend-call chain wires up
  correctly. The failure is that `kotlin.coroutines.intrinsics.CoroutineSingletons` is emitted as a **.NET value-type
  `enum`** and `get_COROUTINE_SUSPENDED()` returns `System.Object`, so it **re-boxes a fresh instance every call**.
  The stdlib's own `outcome === COROUTINE_SUSPENDED` reference-equality checks (`BaseContinuationImpl.resumeWith`,
  `SafeContinuation`, `RootContinuation`) are therefore **always false** once a body genuinely suspends: the suspended
  coroutine is mistaken for completed, the boxed marker (ordinal `0`) is propagated to the `blockOn` sink, and
  `sink.value as Int` unboxes it to `0` (blockOn returns immediately; the async resume never runs — hence
  `f()`'s post-await code never executes). The fast path (already-completed task, `il-taskawait`) passes because it
  never returns `SUSPENDED`. Proven minimally: `E.A === E.A` through `Any` is `False` in our compiler while an
  `object` singleton is `True`; a throwaway stdlib patch caching the boxed sentinel (a stored `val`) makes
  cobuild/f7/f7d print `25`/`7`/`7` with `il-taskawait` unregressed. **Fix belongs in ilemit** (preserve reference
  identity for boxed Kotlin enum entries — the general Kotlin-enum `===` correctness fix) **or narrowly in the stdlib**
  (cache the `COROUTINE_SUSPENDED` box / make `CoroutineSingletons` a reference singleton); `il-cobuild` stays
  run-XFAIL until that lands. `scripts/verify-il.sh` `XFAIL_RUN[cobuild]` reason updated to this root cause (the prior
  "blockOn drain returns immediately" note was a symptom, not the cause — the Monitor drain is correct).

- **ilemit: emit a cross-assembly call to a STATIC method on an EXTERNAL generic type against the correct constructed
  instantiation (bundle-6 P4 blocker (1); general codegen fix).** `AnchorOpenGenericOwnerStatic` previously anchored
  only LOCAL `MethodBuilder` statics (onto the `object`-instantiation); an EXTERNAL reflection static resolved on the
  open generic type DEFINITION (`kotlin.Result\`1::success`, from the referenced `DotKt.Stdlib.dll`) was emitted with
  its parent scoped to that open typedef — an invalid memberref that JIT-loaded as
  `TypeLoadException: Could not load type 'kotlin.Result\`1' from assembly '<app>'` at runtime. It now mirrors the
  local path for a reflection static whose declaring type is a generic type definition: construct
  `C\`1<object>` (a Kotlin companion static cannot reference the enclosing class's type params, so every
  instantiation is signature-identical and `object` is canonical — matching the stdlib's OWN emitted
  `call C\`1<object>::success<…>(…)`) and re-anchor the member by `(module, metadata token)`; `ApplyTypeArgs` then
  `MakeGenericMethod`s it with the call's own type args (reading the concrete return/param signature straight off the
  reflection instantiation so value-arg boxing stays correct). Any cross-assembly `Result.success`/`failure` (and any
  static on a generic stdlib type) now emits verifiably. Verified by **`cases/il-genstatic`** (new): `Result.success`/
  `Result.failure` called from the app → `42`/`True`/`True`/`boom`/`hi`, `ilverify`-clean. Unblocks
  `il-cobuild`'s genuine-async resume callback (`Result.success` wake token no longer TypeLoad-crashes; cobuild now
  runs to value `0`, its remaining run-XFAIL being the stdlib `blockOn` drain, blocker (2)).
- **bir2cir: lower `Task.await()` to the cold-core awaiter suspension point — the `.NET Task ⇒ Kotlin suspend`
  REVERSE bridge (bundle-6 P4; design §4/§5). Completes bidirectional coroutine interop.** `SuspendColdLowering`
  now consumes the facadegen-injected await marker (`k == clrStatic`/`clrGenericStatic`, `suspendCall`,
  `type == kotlin.clr.CoroutinesKt`, `method == await`) inside its state-machine segmentation and emits, in place
  of the marker, the awaiter dance: `this.<aw> = ((Task<T>)task).GetAwaiter()` (a `TaskAwaiter[<T>]` /
  non-generic `TaskAwaiter` STRUCT spilled into an SM field); `if (this.<aw>.IsCompleted) goto L_state` (sync fast
  path — no suspension); else `this.label = state; this.<aw>.OnCompleted(<Action>); return COROUTINE_SUSPENDED`;
  `L_state: <value> = this.<aw>.GetResult()` (generic → the value field; non-generic void → Unit). The `OnCompleted`
  Action is a synthesized SM instance method (`$awaitOnDone$state`, bound via `boundDelegateNew`) that re-drives THIS
  SM with `this.resumeWith(Result.success(null))` — a wake TOKEN (Codex-verified Option B: the resumed `result` is
  discarded, the real value/fault comes from `GetResult()` at the resume label, a faulted task rethrowing there and
  routing through `BaseContinuationImpl.resumeWith`'s catch to the completion — matching JVM `Task.await` semantics).
  `OnCompleted` (not `UnsafeOnCompleted`) flows `ExecutionContext`. APP-BUILD ONLY (rides SuspendColdLowering's
  `!RefBuild && attributeTopLevelOwner` gate — the stdlib's own `await` bodies stay TODO placeholders; ref/rt are a
  symmetric no-op). Verified E2E by **`cases/il-taskawait`** (new): the SYNC FAST PATH — generic `Task<Int>.await()`
  and non-generic `Task.await(): Unit` on already-completed tasks — prints `43` / `7`, exercising the marker
  lowering, the TaskAwaiter struct calls (`GetAwaiter`/`IsCompleted`/`GetResult`), and the generic result read-back.
  The genuine-ASYNC path (`OnCompleted` callback fires — confirmed at runtime) is BLOCKED on two cross-layer gaps
  OUTSIDE bir2cir, tracked in `il-cobuild`'s `XFAIL_RUN` reason: (1) **ilemit** cannot emit a cross-assembly call to
  `kotlin.Result.success` (a public static on the generic `Result\`1`) — `FindMethod`/`AnchorOpenGenericOwnerStatic`
  only anchor LOCAL `MethodBuilder`s, so the external static-on-generic emits a bad-scoped memberref →
  `TypeLoadException`; (2) the await SLOW PATH does not yet drive a genuine cross-thread suspension — for
  `Task.Delay(1).await()` the coroutine completes SYNCHRONOUSLY during `startCoroutine` (bir2cir's suspend counter
  reports `0 await`; instrumentation confirms `blockOn` finds `sink.done == true` before it can Wait), so `blockOn`
  faithfully returns the (default `0`) synchronous result. This is NOT a `blockOn` drain defect — see the drain
  verification bullet below. `il-cobuild` is rewritten to the honest `Task.Delay(1).await()` form (the retired
  `kotlin.clr.delay` crutch dropped) and stays XFAIL until those two land.
- **stdlib: `kotlin.clr.blockOn`'s Monitor Wait/Pulse drain VERIFIED correct — no fix needed; the immediate
  `0` return is the await synchronous-completion path above, not the drain.** The drain logic is textbook: the
  waiter does `Enter(sink)/while(!sink.done) Wait(sink)/Exit(sink)` and the completer (`BlockOnSink.resumeWith`)
  does `Enter(this)/value=…/done=true/Pulse(this)/Exit(this)` on the SAME `BlockOnSink` monitor — the `while(!done)`
  guard is robust against a lost pre-Wait Pulse. Confirmed three ways: (a) the rt-build CIR shows the four
  `@ClrIntrinsic` bindings substitute exactly to `System.Threading.Monitor.Enter/Wait/Exit(sink)` and
  `Enter/Pulse/Exit(this)` on the same object (owner `System.Threading.Monitor`, correct one-arg overloads);
  (b) an instrumented `blockOn` shows both the sync (`lam1`) and would-be-async (`cobuild`) paths reach
  `sink.done == true` immediately after `startCoroutine` — the coroutine completes synchronously, so Wait is never
  entered (the drain is never the cause of `0`); (c) **new `cases/il-monitordrain`** exercises those exact four
  Monitor primitives with a GENUINE cross-thread hand-off (a worker thread sleeps, then sets the value + Pulses
  under the monitor) and the main thread BLOCKS in `Wait` until woken — printing `99`, which is only observable
  after the cross-thread Pulse. This locks the drain mechanism `blockOn` is built on; the end-to-end
  `blockOn`-waits proof only awaits the await slow-path suspension landing.
- **bir2cir + ilemit: synthesize the public `Task<R>` BRIDGE for exported suspend funs (bundle-6 P4 — the hot
  CLR ABI; design §11).** `SuspendColdLowering` now emits, next to each transformable suspend fun's cold entry
  `f$dotkt_suspend`, a public `Task<R> f(args)` bridge that C#/F# callers consume as a normal hot `Task`:
  `tcs = new TaskCompletionSource<R>(); root = new RootContinuation<R>(tcs); try { r = f$dotkt_suspend(args, root);
  if (r !== COROUTINE_SUSPENDED) tcs.TrySetResult((R)r); } catch (e) { tcs.TrySetException(e); } return tcs.Task;`.
  Sync/async completions are mutually exclusive by the coroutine contract (a non-SUSPENDED cold return completes
  the TCS here; a SUSPENDED one is completed later by `RootContinuation.resumeWith`); a synchronous throw faults
  the TCS. The Task-family BCL owners are resolved from the **ref.dll `@ClrTypeAlias` index** (`kotlin.clr.Task`
  → `System.Threading.Tasks.Task`, `kotlin.clr.TaskCompletionSource` → `…TaskCompletionSource`) — bridge skipped
  (cold entry still emitted) when a build's stdlib predates the taskinterop set. `R = Unit`/`void` folds to
  `kotlin.Unit` uniformly (`TaskCompletionSource<Unit>` / `Task<Unit>`; the cold entry returns null for a Unit
  body and `(Unit)null` matches the async path). Generic funs get a generic bridge (`Task<T> f<T>()`, TCS/root
  threaded with the method type param); instance members get an instance bridge on the owner. APP-BUILD ONLY
  (rides SuspendColdLowering's `!RefBuild && attributeTopLevelOwner` gate — the stdlib's own suspend funs get no
  bridge, keeping ref/rt symmetric). `main` is excluded (it is drained by the synthesized plain `main`). ilemit:
  a 1-line clause treats the bridge's `suspendBridge:true` as the `[KotlinFunction(Suspend)]` trigger so a
  round-tripping consumer restores `suspend fun f(…)` (its suspend CALLS then lower back to the cold entry). The
  16 bridges across coldcf/coldgen/coldinst (Int/String/generic/instance/generic-class shapes) emit + run
  unchanged. NOTE: the Task-drain E2E (a C# consumer awaiting the bridge) belongs to the ktproj-bidir harness —
  a Kotlin caller can never reach the bridge (kotc forbids calling a suspend fun from a non-suspend context, and
  a suspend caller hits the cold entry directly).
- **facadegen: inject the `kotlin.clr.await` CLR async-boundary suspend extension when the BCL Task family is
  surfaced (bundle-6 P4 — the frontend surfacing half).** `toolchain/facadegen/Program.cs` now emits, whenever the
  injection closure reaches `System.Threading.Tasks.Task` and/or `Task`1` (an `import System.Threading.Tasks.Task`,
  or any .NET API returning a Task), a top-level `[KotlinFile]` section `file kotlin.clr kotlin.clr.CoroutinesKt`
  with `tlfun await ... ,ext,suspend` extensions: `suspend fun <T> Task<T>.await(): T` (receiver `generic:Task1[T]`)
  and `suspend fun Task.await(): Unit` (receiver `Task`). This is the SOLE frontend surfacing of the extension —
  it is deliberately EXCLUDED from the frontend stdlib jar (design-coroutine-cold-core-task-bridge.md §5/§12), so
  `import System.Threading.Tasks.Task; import kotlin.clr.await; task.await()` now RESOLVES at the kotc frontend on
  the ONE facadegen-surfaced Task (design §12 "removes the two Tasks"). facadegen only SURFACES the symbol and binds
  NO intrinsic; the non-generic await emits as `{"k":"clrStatic","type":"kotlin.clr.CoroutinesKt","method":"await",
  …,"suspendCall":true}` and the generic as `clrGenericStatic` — the marker (type == `kotlin.clr.CoroutinesKt`,
  method == `await`) bir2cir keys on to lower the call site to the TaskAwaiter + Continuation bridge (P4 follow-up).
  Existing façade Task samples (`taskfam` runs `plain=True`/`generic=42`) are unaffected — the injected extension is
  inert until `.await()` is actually called.
- **bir2cir: erase the coroutine ABI to a MONOMORPHIC `Continuation<object>` (bundle-6 §11, bug #5 ROOT — the
  `blockOn { 42 }` payoff RUNS).** New pass `ContinuationErasure` (`toolchain/bir2cir/ContinuationErasure.cs`, run in
  ALL builds before `BirTypeLowering`) rewrites EVERY `kotlin.coroutines.Continuation[X]` type token —
  params/returns/fields/base-args/`sig`/`funcType`/star-projection bare tokens — to `Continuation[kotlin.Any]`
  (→ `Continuation<object>` in rt/app, kept verbatim in ref). Rationale: CLR interface contravariance (`in T`) does
  not lift value types (`Continuation<object>` is not a `Continuation<int>`) AND the declared `in T` is illegal
  anyway (`T` sits inside the invariant `Result<T>`), so ilemit emits Continuation INVARIANT — uniform erasure +
  boundary boxing (JVM-equivalent) is the only shape that composes. The `resumeWith(Result<T>)` boundary uses
  Option A (Codex-verified): keep `resumeWith(Result<object>)` uniformly (the cold-core bases already hand-declare
  `Result<Any?>`), and — SCOPED to the resume protocol only (user `Result<X>` in `runCatching`/`il-result`
  untouched) — erase every `Result[X]` in a `resumeWith` method (decl + body `result.get_value`/`exceptionOrNull`
  owners) and every `Result.success/failure` construction feeding a `resumeWith` call to `Result<object>`, so the
  invariant reference-class instances all match the slot. Now the `BlockOnSink → startCoroutine →
  createCoroutineUnintercepted → SM → resumeWith` chain type-checks and dispatches; `cases/il-lam1`
  (`blockOn { 42 }`) prints **42** and `cases/il-lam2` (a capturing suspend lambda with a real `h()` suspend call)
  prints **15** — the cold-core suspend-lambda E2E milestone. ref/rt symmetric (Continuation/ContinuationImpl/
  SafeContinuation/RootContinuation all re-emit on `Continuation<object>`); gate GREEN, ktproj 9/9.
- **bir2cir: type a suspend-LAMBDA's awaited value with the callee's real return type (bundle-6, bug #6 — the
  lam2 half).** `SuspendColdLowering.ApplyAll` now RETURNS its callee-return-type map (cold-entry name → Kotlin
  resultType) and `SuspendLambdaLowering`/`BuildLambdaSm` thread it into the lambda SM's `FunGen` — previously a
  lambda SM got an EMPTY map, so a lambda's `h()` await fell back to `kotlin.Any` and the spilled value was never
  unboxed, emitting `object + int` (`h() + n`) → runtime corruption. `toolchain/bir2cir/{SuspendColdLowering,
  SuspendLambdaLowering,Program}.cs`.
- **ilemit: substitute interface type-args THROUGH nested (value-class) method-param types when matching the override
  body, so a generic-interface method with a value-class-generic param binds its `.override` at a CONCRETE
  instantiation (bundle-6 coroutine, general bug #5 — the ilemit half).** The `_types` interface-wiring loop
  (`Program.cs`, the `iface.Def` methods pass) built the body-lookup signature with a WHOLE-STRING dictionary lookup
  (`ifSubst.TryGetValue(paramType)`), which only substitutes a BARE `gp:T` param — a NESTED param like
  `@kotlin.Result[gp:T]` (a value class over the type param, e.g. `Continuation.resumeWith(Result<T>)`) never matched
  a key, so for an implementer of `Continuation[object]` (`BaseContinuationImpl`/`BlockOnSink`) the substituted sig
  stayed `resumeWith(@kotlin.Result[gp:T])` and missed the body's `resumeWith(@kotlin.Result[object])` → the
  `DefineMethodOverride` was silently skipped (only the trivial `gp:T`→`gp:T` self-generic case bound). Now uses
  `SubstSig` (nested string replace, exactly like the sibling covariant-return wiring one line below), so
  `Continuation`1<object>`/`<Unit>`/`<R>`::resumeWith` now emit proper MethodImpls. General fix (any emitted generic
  interface method with a value-class-generic param + concrete-instantiation override), not coroutine-specific. Gate
  neutral (145 run-pass / 7 run-fail, all XFAIL-listed; ilverify clean). NOTE: this is NOT the `blockOn { 42 }`
  runtime blocker — see below. `toolchain/ilemit/Program.cs`.
  - **The remaining `blockOn { 42 }` blocker is UPSTREAM, not ilemit: `Continuation<in T>` loses its CLR
    contravariance.** With the override now wired, `cases/il-lam1`/`il-lam2` STILL fail identically
    (`EntryPointNotFoundException` at `Continuation`1.resumeWith` during `resume<Int>`). Root cause: `blockOn<T=Int>`
    passes a `Continuation<Any?>` sink (`BlockOnSink : Continuation<object>`) to `startCoroutine<Int>`/`resume<Int>`,
    which `callvirt`s `Continuation`1<int>::resumeWith`; but the emitted `Continuation`1<T>` interface is INVARIANT
    (`<T>`, no `-T`), so `Continuation<object>` is not a `Continuation<Int>` and the interface dispatch resolves to
    no slot. kotc drops `in`/contravariant declaration-site variance from BIR entirely (0× `"variance":"in"` vs 56×
    `"out"`), so `interface Continuation<in T>` emits invariant. AND naively emitting `in` would not suffice: with
    `Result<T>` an INVARIANT reference class, `resumeWith(Result<T>)` makes a contravariant `T` invalid → the CLR
    loader would throw `TypeLoadException` (confirmed). This is a kotc/bir2cir/stdlib coroutine-ABI DESIGN issue
    (erase the Continuation boundary to `Continuation<object>` / `Result<object>`, matching JVM erasure — or lower
    the contravariant assignment via an explicit adapter), OUT of ilemit's scope. `il-lam1`/`il-lam2` stay XFAIL.

- **kotc: emit `retNullable:true` on ABSTRACT/interface methods with a nullable type-parameter return, matching the
  concrete-impl path (bundle-6 coroutine, general bug #4).** `BirEmitter.ifaceMethod()` (the interface-member emission
  path) never emitted `retNullable`, while the concrete `method()` path did — so an interface `fun <E> get(key): E?`
  emitted `ret=gp:E, retNullable=None` but its override emitted `ret=gp:E, retNullable=True`. bir2cir's
  `NullableGenericReturnErasure` then erased only the override to `object get(...)`, leaving the interface slot as
  `E get(...)`, so the CLR method-impl link had a signature mismatch → `TypeLoadException` (first hit:
  `kotlin.coroutines.EmptyCoroutineContext` overriding `CoroutineContext.get`). Now both emit `retNullable:true`
  symmetrically. General fix at the root (the retNullable computation), not a coroutine special-case — any interface
  `fun <E> foo(): E?` + impl is fixed. Unblocks `EmptyCoroutineContext`/`CombinedContext`/`CoroutineContext.Element`
  type-load in the `blockOn { .. }` path; the next (downstream) coroutine blocker is `Continuation.resumeWith`.
- **bir2cir: erase the `sfunc:` suspend-fn TYPE token to `object`, not `Func` — a suspend-lambda VALUE flows as its
  SuspendLambda state machine (bundle-6 P3 wave-2b FINAL).** The Part-A fold `sfunc:`→`func:` made a
  `suspend () -> T` param/receiver a CLR `Func<T>` delegate — but a suspend-lambda VALUE is a SuspendLambda SM
  (a `BaseContinuationImpl`-derived OBJECT), and a `Func` is not a `BaseContinuationImpl`, so the coroutine
  intrinsics' `this as? BaseContinuationImpl` returned null and threw "not a state machine". Now `sfunc:` erases to
  `object` (System.Object) consistently in EVERY build (stdlib ref/rt AND app): the SM passes anywhere a
  `suspend () -> T` is expected and satisfies `as? BaseContinuationImpl`; the stdlib intrinsics
  (`createCoroutineUnintercepted`/`startCoroutine`/`blockOn`) already cast their object-typed suspend value, so no
  stdlib change was needed. The change touches the SAME spots the fold did — `LowerTypeString`, `NormalizeType`,
  `ParamKey` (→`obj`), the `StatusFor` note — with ONE exception: the `funcType` node key (closureNew/delegateNew/
  delegateInvoke) keeps `sfunc:`→`func:` because it names a real CLR DELEGATE to construct (the pre-P3
  `iterator{}`/`sequence{}` closure path), never an SM value slot. `blockOn { 42 }` now drives the SM as `object`
  all the way through `blockOn(object)` → `startCoroutine(object)` → `createCoroutineUnintercepted(object)` →
  `create()` → the SM ctor (the prior `create()` return-type blocker is gone); it is now blocked ONLY by a
  DOWNSTREAM stdlib emit bug — `EmptyCoroutineContext.get` has its `E?` return retNullable-erased to `object` while
  the `CoroutineContext.get` interface declaration stays `gp:E` (a kotc `retNullable` asymmetry between the
  abstract decl and the concrete override), so the method-override link mismatches → `TypeLoadException` when the
  SM ctor reads `completion.context`. `cases/il-lam1`/`il-lam2` stay XFAIL pending that separate follow-up. Gate
  neutral (145 run-pass / 7 run-fail, all XFAIL-listed). `toolchain/bir2cir/Program.cs`.

- **kotc: emit `sfunc:` + `suspendLambdaNew` — ACTIVATE the SuspendLambda pipeline (bundle-6 P3 wave-2b STEP 2).**
  The producer half of STEP 1's dormant bir2cir consumer. **Part 1:** a `suspend (P..) -> R` function TYPE now
  emits the `sfunc:<ret>:<args>` token (split out of the shared `func:` erasure) at the two folded positions —
  `funcTypeOf` (lambda/delegate funcType) and `birType`'s function-type-value form — carrying the suspend FACT the
  lambda node needs. Detection is by the `kotlin.coroutines.SuspendFunction*` classifier; `clrMethodShape`'s shape
  token is left `func:` (bir2cir does not fold the `shapes` array, and a .NET generic method never takes a
  suspend-fn param). bir2cir folds `sfunc:`→`func:` in every build (type keys + the `sig` path), so ilemit never
  sees `sfunc:` and every existing delegate/lambda sample is unchanged. **Part 2:** a `suspend { }` /
  `suspend (..) -> R` lambda LITERAL now emits a `suspendLambdaNew` node (instead of delegateNew/closureNew),
  reusing the existing closure machinery (`capturedVars(includeThis=true)` / `captureFieldName` /
  `captureFieldType` for captures, `lambdaParamsJson` for own params, the statements-with-`suspendCall`-tags body,
  bare enclosing type-param names for `typeArgs`); the body is emitted WITHOUT `captureSubst` so bir2cir's FunGen
  spills captured-var reads into SM fields. v1 exclusions fall through to the plain closure path (arity ≥ 2;
  `@RestrictsSuspension` receiver-scope builders like `sequence{}`). Verified: `blockOn { 42 }` / capture +
  suspend-call / nested arity-1 all emit the correct node and bir2cir builds the SuspendLambda SM. The end-to-end
  run is blocked ONLY by a bir2cir SM `create()` return-type bug (returns `Continuation<object>`; the stdlib base
  `BaseContinuationImpl.create` returns `Continuation<Unit>` → `TypeLoadException` at class load) — `cases/il-lam1`
  / `il-lam2` are XFAIL pending that bir2cir follow-up. `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitter.kt`.
- **bir2cir: `sfunc:` suspend-fn-type token + `suspendLambdaNew` → SuspendLambda state-machine builder (bundle-6
  P3 wave-2b STEP 1, DORMANT consumer).** Lands the bir2cir *consumer* BEFORE kotc emits either (a deliberate
  dormant-first ordering — an unrecognized `sfunc:` prefix or unknown node would otherwise break ilemit), so both
  changes are a verified no-op against current input. **Part A:** `sfunc:<ret>:<args>` (the suspend function type,
  mirroring `func:` receiver-first) is folded to `func:` at the two type-token funnels (`LowerTypeString`,
  `NormalizeType`) + `ParamKey`/`StatusFor`/`PrefixLength`, so the delegate-shape lowering treats it exactly like
  `func:` and ilemit NEVER receives `sfunc:` (its suspend-ness is consumed by Part B, not the delegate path).
  **Part B:** the new `suspendLambdaNew` BIR node (the cold suspend-lambda value) is lowered by a new
  `SuspendLambdaLowering` pass (app-build only, after the cold lowering, before type lowering) to
  `new <mangled>_lambdaN$sm(captures…, null)` + a synthesized `<mangled>_lambdaN$sm : SuspendLambda` state machine
  built from `SuspendColdLowering`'s FunGen (the shared invokeSuspend/label/spill/field machinery). The SM carries
  the create(completion)/create(value, completion) override protocol (arities 0/1; ≥2 refused) matching
  `BaseContinuationImpl.create`'s erased CLR ABI so ilemit's clrOverride binds the base slot. Fixture-tested
  (hand-crafted arity-0 + arity-1 lambdas): bir2cir → ilemit → ilverify clean.
  `toolchain/bir2cir/{Program.cs,SuspendColdLowering.cs,SuspendLambdaLowering.cs}`.
- **stdlib: `kotlin.clr.blockOn` / `kotlin.clr.delay` are now FRONTEND-RESOLVABLE via `expect`/`actual` (bundle-6
  P4 symbol-surfacing, user-directed).** `import kotlin.clr.blockOn` / `import kotlin.clr.delay` now type-check at
  the kotc frontend with ZERO compiler special-casing. Their signatures are CLR-free, so they split into an
  `expect` in the jar-INCLUDED common set (`libraries/stdlib/common/src/kotlin/clr/CoroutinesH.kt`) plus two
  actuals across the two separate K2 compilations: `build-stdlib-jar.sh` stages a throwing STUB actual
  (`BlockOnStubActual.kt` — the frontend jar is a never-executed classpath; exact precedent = the
  `@OptionalExpectation` JvmName/JvmInline stub actuals), while `build-stdlib-{ref,rt}.sh` compile the REAL
  `actual` bodies in `libraries/stdlib/clr/taskinterop/kotlin/clr/Coroutines.kt` (Monitor-drain / `Task.Delay`).
  `await` is unchanged (its signature names `Task` → facadegen-surfaced, not expect/actual). This retires the
  planned "kotc kotlin.clr coroutine injection seam" — kotc cares about ZERO coroutine symbols. The
  `verify-il` `cobuild` and `verify-roundtrip` suspend sections now pass the frontend and fail LATER at ilemit
  (the blockOn suspend-lambda SM + suspend-fun cold-entry/Task-bridge are the remaining wave-2b work); their
  XFAIL reasons were updated to the new stage.
- **kotc: `override val`/`override var` accessors now fill the base CLASS abstract vtable slot (bundle-6 P3, general
  override-property fix).** An `override` property whose accessor overrides a base **class** (or per-entry enum)
  accessor was emitted as `override:false, virtual:true` — a fresh `NewSlot` — instead of reusing the base's virtual
  slot. Consequence: a concrete subclass that does NOT re-override the property left the base's abstract `get_<X>`
  slot unfilled and the type failed to load (`TypeLoadException: 'get_X' … does not have an implementation`). This
  mismatched the METHOD path, which already correctly stamps a class override with `override:true` (reuse slot,
  no `NewSlot`). Fix in `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitter.kt` `accessorMethod()`: compute
  `isOverrideClass` from the accessor's own `overriddenSymbols` (mirroring `method()`'s `isOverride`) and emit
  `override: clrIface || isOverrideClass`. Interface-member accessors are unchanged (they bind via ilemit's
  `DefineMethodOverride` pass, so a `NewSlot` is correct there); a setter that merely ADDS to a base `val` still
  gets a fresh slot (its own `overriddenSymbols` is empty). This was the root cause of the cold-core
  `ContinuationImpl.get_context` / `RestrictedContinuationImpl.get_context` mis-emission that bundle-6 P2 worked
  around by re-overriding `get_context` in every synthesized state-machine class; the workaround is now redundant.
  New E2E sample `cases/il-overrideprop` (interface `val` + abstract-class override + non-re-overriding concrete
  subclass) added to `verify-il.sh` — TypeLoad-fails before the fix, runs after.
- **kotlinx purged — BREAKING (bundle-6 P1b, user-directed deliberate break).** The historical `kotlinx.coroutines`
  intermixing (a pre-stdlib coroutine stopgap) is removed from the repo: no `kotlinx` names remain outside Track-2
  design notes, `docs/archive/`, and this changelog's history. The cold-core coroutine surface
  `kotlin.clr.blockOn` / `kotlin.clr.delay` / `Task.await()` replaces `kotlinx.coroutines.runBlocking` / `.delay`.
  Concretely: the `il-cobuild` sample and the three suspend-consuming `verify-roundtrip` sections were rewritten
  `kotlinx.coroutines.{runBlocking,delay}` → `kotlin.clr.{blockOn,delay}`; the `kotlinx-coroutines-core-jvm` jar was
  dropped from `verify-il.sh` / `verify-roundtrip.sh` (clr side) and `verify-differential.sh` / `dotkt.sh`; kotc lost
  its `kotlinx.coroutines.runBlocking` trivial-block passthrough recognizer and its
  `kotlinx.coroutines.suspendCancellableCoroutine` (`isSuspendCancellable`) intrinsic branch (cleanly detachable —
  `sequence{}`'s CPS engine relies on `yield`/`yieldAll`, not this). `kotlin.clr.blockOn`/`delay` are not yet
  frontend-resolvable (they live in the jar-excluded taskinterop source set; their FIR injection is cold-core P4), so
  `cobuild` and the roundtrip suspend sections stay XFAIL at the frontend stage with updated reason strings. The
  ilemit `CoCancellableCont` constant is intentionally left for P6 cleanup.
- **Coroutine bundle-6 P2: `SuspendColdLowering` v1 (bir2cir) — the cold-core suspend → state-machine
  transform.** New pass `toolchain/bir2cir/SuspendColdLowering.cs`, wired after `MemberCallSubstitution` and
  before `BirTypeLowering` (app + rt-stdlib builds; skipped in the ref build). It lowers a straight-line
  top-level `suspend fun f(args): R` into the cold Continuation shape per
  `docs/design-coroutine-cold-core-task-bridge.md §11`: a plain-CIR state-machine class
  `<FileClass>_f$sm : kotlin.coroutines.clr.internal.ContinuationImpl` whose `invokeSuspend(result): Any?`
  does the label dispatch (`label`/`brIf`/`goto`) + segmented body, a cold entry
  `f$dotkt_suspend(args, completion): Any?`, and (for `suspend fun main`) a synthesized plain draining
  `fun main()`. Suspend calls are segmented at each `COROUTINE_SUSPENDED` check; locals crossing a
  suspension + sub-expression spill temps become SM fields (the kotc `collectCpsVars`/`spillExpr` algorithm,
  re-implemented over BIR JSON). `COROUTINE_SUSPENDED` is referenced as the real stdlib getter
  `IntrinsicsKt.get_COROUTINE_SUSPENDED()` (the stale ilemit `coSuspendedSentinel` field node is bypassed).
  Rungs verified end-to-end (`dotkt.sh --run`): `suspend fun f()=42` drained via a suspend `main` prints 42;
  params+locals (`add`), sequential awaits (`two`), and sub-expression spill (`f()+g()`) all correct. v1 scope
  is straight-line, non-generic, static top-level funs whose suspend calls target local transformable funs;
  every other shape is LEFT UNTOUCHED (keeps `"suspend":true` for the existing ilemit throw-stub path — zero
  regression, and a verified no-op in the rt-stdlib build). No ilemit/stdlib changes. Control flow / try /
  suspend lambdas / generics / `Task.await` / the public Task bridge / async-resume exception propagation are
  P3-P4.
- **Coroutine bundle-6 P3: `SuspendColdLowering` — control flow / try / generics / extensions (bir2cir).**
  Lifts the P2 straight-line transform to the full non-lambda control-flow surface, all as plain CIR (no
  ilemit change). Key observation: kotc already FLATTENS `while`/`for`/`do-while` into structured
  `block`/`label`/`brIf`/`goto` BIR, so loops need no re-segmentation — only `if`/`when` survive as `cond`
  (ternary) expressions, which the pass lowers to `label`/`brIf`/`goto` control flow (a result field, only
  the taken branch's suspension runs) when they contain a suspension. Locals crossing a suspension (loop
  induction/accumulator vars, the `for` `<iterator>`) become SM fields. **`throwOnFailure(result)` prologue**
  now emitted at every resume merge point (the `kotlin.coroutines.clr.internal.throwOnFailure` rethrow that
  surfaces a failed async resume — the CLR analog of the JVM SM's `ResultKt.throwOnFailure($result)`).
  **try/catch** with the suspension in the try BODY works via a two-level dispatch (the outer method-top
  dispatch enters the try at a pre-try label; the try body begins with an inner dispatch that branches to the
  actual resume point inside the protected region — both branches stay in-region, legal IL); the SUSPENDED
  exit is emitted INLINE (`if (result===SUSPENDED) return SUSPENDED`) so a suspension inside a `.try` returns
  via ilemit's structured-try `leave` with no cross-region branch. **Generic suspend funs**
  (`suspend fun <T> f(x): T`) lower to a generic SM `<file>_f$sm<T>` with T-typed fields + a generic cold
  entry; invokeSuspend returns `object` (boxing a value T), an awaited T is read back via `unbox.any !T` — the
  **generic-SM spike is GREEN and ilverify-clean, confirming plain CIR fully expresses generic state machines
  with no ilemit gap.** **Extension** suspend funs come free (kotc lowers the receiver to a `__self` param).
  New gate samples `il-coldcf` (if/when/while/for + try/catch + extension) and `il-coldgen` (the generic
  spike), both run-correct and ilverify-clean. LEFT UNTOUCHED (P3-wave2/P4, ride the ilemit throw-stub, zero
  regression): suspend lambdas / closures / the inline `suspendCoroutine{}` intrinsic (emits a `closureNew`),
  member/cross-assembly suspend calls (owner'd `callStatic` / `callInstance suspendCall`), instance suspend
  MEMBERS (`static==false`, live in `types`), suspension inside a catch/finally, a nested suspending try. The
  pass stays gated to app builds (skipped in ref AND rt-stdlib), so stdlib ref/rt symmetry is unchanged.
- **Coroutine bundle-6 P3 wave-2a: `SuspendColdLowering` — INSTANCE suspend members + MEMBER/cross-file/
  cross-assembly suspend CALLS + the `get_context` workaround removal (bir2cir).** Retires three of the P3
  policy-passthrough items in one pass.
  - **Instance suspend members** (`class C { suspend fun m(args): R }`, `static==false`, living inside a
    `types[]` class): the pass now iterates each file's `types[]` and transforms suspend methods inside each
    class. The SM carries a `$this` field of type `C`; the method body's `this`/implicit-receiver reads become
    `SM.$this` reads. The cold entry `m$dotkt_suspend` is an **INSTANCE** method on `C` (so a
    direct/no-suspension member body keeps `this` verbatim — the cold entry itself is the CLR virtual-dispatch
    boundary; per Codex, the static-explicit-receiver alternative would have to reimplement virtual resolution
    by hand). A generic class combines cleanly for a direct member (`Box<T>.get(): T` inherits the class type
    param); a suspending member of a generic class, and OPEN/overridden members (which would need a per-override
    virtual/override cold entry in lockstep), are deferred (policy-stub, kept `suspend:true`).
  - **Member + cross-file/cross-assembly suspend CALLS**: a `callInstance suspendCall` (`x.g()`) and a same-
    assembly cross-file top-level suspend call (kotc emits it with `owner:null`, identical to same-file) are
    rewritten to the callee's `<name>$dotkt_suspend` cold shape on the correct receiver. Because a cross-file
    callee's cold entry may live in another file, the whole analysis is now **GLOBAL** across the compilation's
    files (`ApplyAll` — Program.cs `TransformFiles` split into a stage-1 per-file transform / stage-1.5 global
    suspend lowering / stage-2 per-file type lowering); the transformability fixpoint spans every input file.
    Cross-assembly callees resolve via the ref.dll `MemberBinding.Suspend` flag (`ReferenceMetadataIndex.
    HasSuspendMember`) + the naming convention; an unresolvable suspend call makes its caller non-transformable
    (policy-stub). A call site's instantiated `ownerType` (`Box[kotlin.Int]`) is stripped to its bare class key
    for the registry/ref.dll lookup.
  - **`get_context` workaround REMOVED**: the P2/P3 SM classes re-overrode `ContinuationImpl.get_context` to fill
    `BaseContinuationImpl`'s abstract slot. kotc commit `a65b44d` now emits `ContinuationImpl.get_context` as a
    proper base-slot override, so a synthesized SM subclass no longer needs the re-override — deleted. Verified:
    after rebuilding the stdlib rt so the fixed `ContinuationImpl` is in the dll, the coldcf/coldgen/coldinst SMs
    still class-load and run.
  - New gate sample `il-coldinst` (INST1 `Counter.bump` instance member, INST2 `Svc.chain` → `this.helper()`,
    INSTGEN generic `Box<T>.get`, MCALL1 top-level → `c.bump()`, MCALL2 a suspend fun in a second source file),
    run-correct (`11 12 10 42 hi 101 7`) and ilverify-clean. `make verify-il` GREEN (PASS(run) 145, no NEW-FAIL
    vs the `bymap/chunk/cobuild/collops2/seq` XFAIL baseline). App-build-gated as before, so ref/rt symmetry is
    structurally preserved. LEFT UNTOUCHED (wave-2b/P4): suspend LAMBDAS / `sfunc:`, open/overridden and
    generic-class suspending members, the public `Task<T>` bridge / `Task.await` / real `blockOn` drain.
- **Gate hardening (pre-coroutine batch C1-C3): machine-readable XFAIL baselines + abort-proof harnesses.**
  *C1 (verify-il)* — the known-fail baseline moved from prose/flat name lists to `XFAIL_RUN` / `XFAIL_ILVERIFY`
  associative arrays (fail name → reason) diffed by the new shared `lib.sh xfail_diff`: exit 0 iff every actual
  fail is XFAIL-listed; any other name prints `NEW-FAIL` and exits 1; an XFAIL entry that starts passing prints
  `FIXED — remove it from the xfail list` without reddening the gate. CLAUDE.md's gate paragraph now points at
  the mechanism instead of prose numbers. RECORDED (not fixed): `bymap` regressed with the stdlib subtree bump
  (cde8afd) — the rt `clrMapGet` throws `EntryPointNotFoundException` on `IDictionary.ContainsKey`; XFAILed with
  an explicit REGRESSION reason, owned by the Map/MutableMap dual-rep sub-track.
  *C2 (verify-roundtrip)* — the gate used to die silently mid-script (SIGABRT 134 inside a `$(...)` under
  `set -e`) at the FIRST suspend-stub crash, so the 5 sections after it never ran and piping through `tail`
  masked the exit to 0. Now every section runs to completion (crash-safe captures via the `if var="$(cmd)"`
  errexit-exempt pattern; every pipeline step tolerates failure so it surfaces as its section's verdict), the 3
  suspend-consuming sections (`roundtrip`, `roundtrip-generic`, `roundtrip-memext2`) are `RT_XFAIL`-listed
  ("coroutine lowering deferred (bundle 6)"), and the final summary prints per-section PASS/FAIL/XFAIL with
  exit 0 iff no unexpected outcome. This script is the coroutine bundle's E2E gate: the suspend sections
  flipping to PASS surface as "FIXED — remove it from the RT_XFAIL baseline" lines.
  *C3a (verify-wide-delegates)* — the hand-written 17-arg `.bir.json` fixture, fed STRAIGHT to ilemit
  (bypassing kotc + bir2cir — a single-path violation whose hand-maintained expr vocabulary rotted twice), is
  DELETED. The gate now drives a real Kotlin source (`cases/il-widedeleg/wide.kt`: 17-arg function values +
  a wide-typed parameter) through the canonical kotc → bir2cir → ilemit pipeline and keeps all three
  assertions: run output, KFunc`18/KAction`17 synthesis in the dll, facadegen restoring the wide Kotlin
  function type (`rg` → `grep` for CI portability).
  *C3 (verify-differential)* — same `XFAIL_DIFF` mechanism: the 2 coroutine DIFFs (`il-seq`/`il-collops2`,
  mirroring verify-il's run-XFAILs) plus 3 RECORDED regressions from the 2026-07-02 stdlib subtree bump
  (cde8afd): `m-b6` (ilemit aborts on the rt's Double-specialized `maxOrNull` — "not a
  GenericMethodDefinition"), `m-b9` (`sumOf {}` returns 0 on CLR), `m-b10` (`groupBy` → `clrMapGet`
  `EntryPointNotFoundException`, the same Map dual-rep family as verify-il's `bymap`). These 4 stdlib-bump
  regressions (incl. bymap) are stdlib-side work, NOT gate bugs — the XFAIL entries carry the full symptom
  so the owning track can pick them up.
  *C3b (CI)* — `.github/workflows/verify.yml`: an explicit `make stdlib` step (now that `libraries/stdlib`
  is tracked, a stdlib-build failure is attributed to its own step instead of a silenced lazy `need_*()`
  call inside the first verify script), a `verify-widedelegates` step, and the XFAIL exit semantics
  documented in the header — the workflow consumes the gates' exit codes directly, so it is expected GREEN;
  a red run means a real regression, not a known gap. Locally `make verify` (all 5 gates) is exit 0.
- **ilemit hardening: 5 codegen defect fixes (pre-coroutine batch A1-A4+B4).**
  *A1 nested-try return* — a `return` inside nested try/finally emitted the pending `ret` INSIDE the outer
  protected region (ilverify ReturnFromTry / InvalidProgramException); the pending return now propagates
  per level (store to the outer frame's result local + `leave` to its retLabel), only the outermost `ret`s.
  *A2 return coercion inside try* — the return-inside-try store (and both `returnExpr` twins) skipped the
  Nullable-wrap/object-box coercion the plain return applies (`fun f(): Int? { try { return 1 } finally {} }`
  printed 0); one shared `EmitReturnCoerced` now runs at all four return sites.
  *A3 store coercion asymmetry* — only `var`-init boxed a value/generic RHS into a reference slot; setLocal
  (local+arg), the cps-field stores, setField/setFieldExpr (setter+field) and staticFieldSet emitted the raw
  RHS (`var a: Any = "x"; a = 42` NRE'd); one shared `EmitStoreCoerced` (Nullable wrap + box) now runs at
  every store site.
  *A4 float NaN `<=`/`>=`* — lowered with the signed-inverted compare, so `NaN <= 1.0`/`NaN >= 1.0` were TRUE;
  float/double now emit C#'s unordered-inverted forms (`cgt.un`/`clt.un` + invert); integer paths unchanged.
  *B4 kotlin.time surface (ilemit portions)* — ResolveType resolves CLR NESTED type names (last `.` → `+`
  probing: `kotlin.time.Clock.System` → `Clock+System`; fixes `Clock.System.now()`); 4 unchecked resolver
  derefs now throw legible NotSupportedException instead of NRE/ArgumentNullException; and an emitted
  interface with an external (clr:/clrg:) base now wires its bodied DIM to the base slot via a private FINAL
  bridge + MethodImpl (C#'s explicit-impl-in-interface shape) — without it every implementer of
  `ComparableTimeMark : IComparable<CTM>` failed to LOAD (`ValueTimeMark`/`LongTimeMark`/`DoubleTimeMark`
  TypeLoadException; unblocks `measureTime` type loading). New gate cases: il-nestedtry, il-trynullable,
  il-setlocalbox, il-nancmp. Remaining kotlin.time breakage is kotc-side (companion-extension-property
  receiver drop `2.seconds`; value-class operator mislowering in longSaturatedMath) — producer defects, not
  emitter ones.
- **bir2cir: generic-token fixes for CONCRETE generic alias receivers + map defaults (pre-coroutine hardening A6).**
  *(a)* `Rule3HelperCall` now instantiates the hoisted `<>dotkt_ClrH_*` helper with the receiver's class args
  (class-first, then method args — the `MergeTypeParams` order), carries the INSTANTIATED receiver token in the
  call `sig` and substitutes class `gp:` names positionally (was: an open-generic `callStatic` + the degenerate
  non-generic `clr:Dictionary` owner → `HashMap<String,Int>().put(..)` = InvalidProgramException).
  `IteratorConsumerNormalization` additionally treats the substituted helper owner (`<>dotkt_ClrH_kotlin_*`) as an
  rt-stdlib iterator source, so `for (x in ArrayList<Int>())` re-points hasNext/next at the real
  `kotlin.collections.Iterator` (was EntryPointNotFound). Stdlib companion: HashMap/LinkedHashMap `put`/`remove`
  restructured to the containsKey-guarded shape (never hold a `V?` in a bare `gp:V` local — the documented
  ClrMapDefaults null-unbox landmine).
  *(b)* `MapDefaultCall`/`Rule3HelperCall` carry the call's `retType` (guarded against bare `gp:` tokens) so a
  bare-V-returning helper (`Map`/`MutableMap.getOrDefault` → `clrMapGetOrDefault`) boxes the CONCRETE
  instantiation — previously the emitted `box !!1` used the callee's own method-generic token inside non-generic
  `main()` = invalid metadata → BadImageFormatException. New gate case `il-mapgen` covers both.
- **facadegen/kotc: `Task`/`Task<T>` (same-name .NET arity families) now coexist — the silent last-wins
  ClassId overwrite is fixed.** facadegen's meta emitted a generic definition under an arity-LESS .NET-name
  token (`class Task System.Threading.Tasks.Task open TResult` vs the non-generic `class Task
  System.Threading.Tasks.Task open`), and the kotc injector keyed `byClassId` by simple name — BFS order
  decided which `Task` survived, and `generic:Task[T]` cross-refs could bind against the non-generic one
  (directly under the coming suspend=`Task<T>` ABI). Now: (1) the meta's .NET-name token is the TRUE CLR name
  (`` System.Threading.Tasks.Task`1 ``); (2) a generic definition in a multi-member `(namespace, simpleName)`
  family gets an arity-suffixed KOTLIN name (`Task1<TResult>`, `Func2<T,R>` — the `kotlin.Function1`
  precedent; singleton families like `` List`1 `` keep the plain name; family computed against the loaded
  reference universe, so names are import-set-stable); (3) cross-refs (`generic:Task1[...]`) and supertype
  refs agree; (4) `import ...Task` seeds the WHOLE arity family (and `import ...Task1` maps the digits back
  to the backtick arity); (5) the injector strips the backtick when registering the backend name (the
  `clrg:<open>[args]` + ilemit arity-append contract is unchanged — no bir2cir/ilemit change). Sibling fix:
  NESTED generic definitions (`` List`1+Enumerator ``) previously injected under a nonexistent FQN
  (`System.Collections.Generic.Enumerator`); they are now excluded (`ShouldInject`) and references degrade to
  `Any?`. K2 cannot host two same-name classifiers in one package (one classifier per ClassId), hence the
  naming projection; documented in `docs/dotkt-semantics.md` §8d.

- **kotc: kotlin.time (B4) enablement — five root causes fixed; `2.seconds + 3.seconds` runs end-to-end.**
  (1) A companion EXTENSION property accessor (`val Int.seconds` on `Duration.Companion`) dropped its
  receiver: the getter emitted `get_<name>` with `"args":[]` and the cross-module backing-field probe
  degraded `2.seconds` to a bare `staticField Duration.seconds`. Now mirrors the top-level-property
  branch: static `get_/set_<name>(__self, …)` with the receiver as the leading arg + `sig` for overload
  resolution (`get_seconds(Int|Long|Double)`). (2) The BINARY/UNARY/inc/dec operator lowering gated only
  on "no extension receiver", so a `kotlin.*` VALUE-CLASS member operator (`Duration.plus/unaryMinus`)
  became a raw CIL `bin +`/`un -` (InvalidProgram inside the rt, `saturatingFiniteDiff`); the gate now
  also requires primitive operand types (`PRIMITIVE_OP_FQ` = signed/bool/char + unsigned, which the raw
  lowering legitimately serves) — anything else is a real method call. (3) A `private` TOP-LEVEL fun is
  FILE-private in Kotlin, but the file splits across CLR types, so CLR `private` broke same-file class
  access (`Duration..cctor` → `DurationKt.durationOfMillis` MethodAccessException); top-level private
  funs now emit `internal`. (4) An EARLY `return` inside a spliced inline body emitted a raw method
  return (void caller + Int32 on the stack = invalid IL, `indexOfLast` in `appendFractional`); spliced
  returns now route through a result local + end label (`spliceBodyWithReturns`). (5) A MEMBER inline
  fun's DISPATCH receiver was never bound — the spliced body's `this` fell through to the CALLER's this
  (`absoluteValue.toComponents{}` read the negative outer duration: `-1s` printed `--1s`); now bound to
  a temp like the extension receiver. New gate case: `il-duration`.
- **kotc: removed the dead `kotlinx.coroutines.delay` → `Task.Delay` lowering from `coAwaitable`.**
  Pre-stdlib bespoke kotlinx legacy (kotlinx.* is not the stdlib); it was unreachable on the current
  pipeline — unrestricted suspend fns emit plainly with `"suspendCall":true`, and the only `coAwaitable`
  caller is the `sequence{}` restricted-suspension CPS path where `delay` cannot appear. Deleted (not
  aliased) so the Task-based coroutine lowering (bundle 6) does not inherit it as a load-bearing hack;
  `il-cobuild` BIR and run behavior are unchanged (the legible deferred-coroutine stub).
- **kotc: exactly-once evaluation for when-subject / safe-call receiver / range-membership operand.**
  `BirEmitter.blockExpr` stored the RENDERED initializer JSON in `valSubst`, so every `IrGetValue` of the
  subject re-spliced — and re-EVALUATED — it: a when-subject call ran once per branch test, a safe-call
  receiver ran twice, and `x in a..b` rendered `x` into both comparison legs. Worse, a safe call on a
  nullable VALUE-type receiver (`g(): Char?; g()?.code`) spliced the raw `Nullable<char>` under `conv int`
  → `System.InvalidProgramException` at run. Fixed with the ELVIS temp-local pattern (`bindOnce`): the
  subject is bound once into a `valueBlock` temp (stable const/immutable-local reads still splice
  directly), and a nullable-VALUE receiver is HasValue-gated with the member seeing the unwrapped
  `.Value`. A nullable generic-param subject (`x as? T`) object-erases its temp (a `gp:T` local can't
  hold the `isinst` REF result — unverifiable). New gate cases: `il-whensubj`, `il-safecallnv`,
  `il-rangein`.
- **`scripts/` overhaul: one naming scheme + shared internal conventions + two harness bug fixes.**
  *Naming* — normalized to `<verb>-<noun>[-qualifier].sh`, aligned with the make target names (targets unchanged):
  `build-clr-stdlib.sh`→`build-stdlib-ref.sh`, `build-clr-stdlib-runtime.sh`→`build-stdlib-rt.sh`,
  `build-clr-stdlib-frontend.sh`→`build-stdlib-jar.sh`, `pack-dotkt.sh`→`pack-nuget.sh`,
  `verify-ilemit-wide-delegates.sh`→`verify-wide-delegates.sh`, `gen-clr-stdlib-actual-index.py`→
  `gen-stdlib-actual-index.py`; `run-clr-sample.sh` DELETED (pre-dated and duplicated `dotkt.sh`/`make dev`).
  All live references updated; `docs/archive/**` and released CHANGELOG entries intentionally keep the old names.
  *Conventions* — new `scripts/lib.sh` sourced by every script: strict mode (`set -euo pipefail`; tolerated
  failures are explicit `|| true`), `ROOT`, the tool/artifact paths as a single source, `info`/`warn`/`die`,
  a `usage()`/`-h` convention, lazy `need_*()` builders vs the UNCONDITIONAL `build_tool` the verify gates use.
  *Bug fix 1 (the rt grep-exit-1 footgun)* — `build-stdlib-rt.sh` ended with an error-grep that exited 1 exactly
  when the build was CLEAN; both stdlib build scripts now exit 0 on success / nonzero on real failure, and the
  compensating `|| true` in the Makefile and pack-nuget.sh is gone.
  *Bug fix 2 (the verify-il dropped-FAIL-line / stdout race)* — a crashing sample died before printing its FAIL
  line (`set -e` killed the parallel subshell) and concurrent output interleaved → false-pass headlines. Every
  sample now writes ONE atomic result record (`build/verify-il/run-<name>`, guaranteed by an EXIT trap),
  aggregated after `wait`. The 4 coroutine-deferred crashers (`chunk`/`cobuild`/`collops2`/`seq`) that used to
  drop their lines now PRINT as FAILs; the script encodes the known-fail baseline (`KNOWN_RUN_FAIL`/
  `KNOWN_ILVERIFY_FAIL`) and exits 0 iff there is no NEW fail name — green is machine-checked. Truthful baseline:
  **PASS(run) 132 / run-FAIL = exactly those 4 / 6 known ilverify-formal-only names**.
  Also un-broke `verify-wide-delegates.sh` (pre-existing: its hand-written BIR fixture still used the retired
  `k:"console"` expr; now the current `clrStatic` form — the gate passes again).
- **kotc: implicit companion access for injected .NET statics — `.Companion` no longer required.**
  `Application.Start(...)` / `App.Count` now resolve directly; previously only `App.Companion.Start(...)` worked
  (the old form stays supported — both forms emit byte-identical BIR). Root cause was a wiring gap, not a K2 limit:
  stock FIR only links `companionObjectSymbol` for source/deserialized classes (`FirCompanionGenerationProcessor`
  walks FirFiles only), so a fully-generated owner never got the link the implicit-qualifier path consults
  (`typeForQualifierByDeclaration` → `canBeValue`). Fix: `ClrTypeInjector` eagerly creates + links the companion for
  injected classes with statics and sets the FIR-internal `ownerGenerator` attribute via a bytecode-public Java shim
  (`kotc/frontend/FirInternals.java`) — required because the eager link makes the framework's only nested assignment
  site unreachable (`FirGeneratedScopes.kt:245-255`) and generated-origin member lookup dies on `ownerGenerator!!`
  (`:290`). `il-injstatic` now exercises both forms.
- **docs: overhaul** — 7 superseded docs archived to `docs/archive/` (HISTORICAL headers); `dotkt-semantics.md` gains a TOC + suspend-hot/Appendable/enum/value-class/.Companion sections; new user-facing set `docs/user/` (getting-started / using-dotnet-from-kotlin / kotlin-on-clr-differences / supported-features) + `docs/README.md` index; `README.md` refreshed to the single-path 4-layer reality.
### Added
- **Unified build interface (`Makefile`)** — a thin orchestrator over the canonical scripts, with incremental
  file targets for the whole artifact DAG (kotc → the 4 .NET tools → stdlib jar/ref/rt → pack). `make help`
  self-documents; key targets: `all` (toolchain → stdlib → pack), `toolchain`, `stdlib{,-jar,-ref,-rt}`, `pack`,
  `verify{,-il,-ktproj,-roundtrip,-differential,-widedelegates}` (the gate scripts are called verbatim),
  `dev SRC=… [RUN=1 …]` (wraps `dotkt.sh`), `facades`, `clean{,-tools,-stdlib,-pack}`. `make -j` builds the
  independent tools in parallel. The load-bearing output paths (`build/<tool>-bin`, `build/clr-stdlib*/dll`,
  `build/clr-stdlib-frontend-jvm`) are unchanged.
- **4-package NuGet structure + the stdlib packaging gap fixed** — `pack-dotkt.sh` shipped NO stdlib dlls while
  the shipped `DotKt.Toolchain.targets` needs both; the packed SDK could not actually compile. Now exactly four
  packages: **DotKt.Sdk** (MSBuild SDK; implicit refs to Toolchain + Stdlib), **DotKt.Toolchain** (kotc + bir2cir +
  ilemit + facadegen + retarget + `kotlin-stdlib-clr-frontend.jar` + the COMPILE-TIME reference stdlib
  `tools/stdlib/DotKt.Private.Stdlib.dll`, exposed as `$(DotKtStdlibRefAsm)` → a non-copy `<Reference>` in
  Sdk.props), **DotKt.Stdlib** (NEW: the RUNTIME stdlib `lib/net10.0/DotKt.Stdlib.dll`, copy-local via the SDK's
  implicit PackageReference; opt out with `<KotlinClrStdlibRef>false</KotlinClrStdlibRef>`), **DotKt.Templates**
  (unchanged). `DotKt.Runtime` stays retired — nothing creates or references it. Verified end-to-end: a fresh
  `.ktproj` consumer restored from the local feed alone builds and runs stdlib calls, with `DotKt.Stdlib.dll`
  copy-local and the reference face absent from output. Package version is single-sourced from
  `packaging/DotKt.Versions.props` (both `*.pack.csproj` now import it; the dead `VER` in `pack-dotkt.sh` is gone).

### Removed
- **`scripts/run-m0.sh`** — drove the retired C# backend (`kotc` → C# → dotnet); the IL pipeline gates
  (`verify-il.sh` / `make verify-il`) are the canonical entry.

### Fixed
- **Injected .NET static-companion members (`il-injstatic`)** — `App.Companion.start(cb)` on a facadegen-injected
  C# host type crashed with `unresolved method: <>dotkt_ClrH_Kfc_App.start`: kotc's Rule-3 hoist classifier
  ("no interop marker + concrete → its body was hoisted to the `<>dotkt_ClrH_<owner>` static helper") misfired on
  the synthesized companion's static method — an injected member naturally has no marker (it isn't a stdlib
  binding), and no helper exists for an external .NET type (the hoist is only for @Clr classes with hoisted Kotlin
  bodies). The hoist is now gated on the injected `ClrTypeRegistry`: an owner (or a companion's host class)
  registered there routes to the direct .NET member shapes (`clrStatic Kfc.App::start` etc.), never the hoist —
  generalizing (and subsuming) the narrower `ClrEventRegistry` gate from the event-accessor fix `32a1da6`. This was
  the last run-FAIL: verify-il PASS(run) 131 → 132, fail-names 7 → 6 (all remaining are the documented ilverify
  set: chunk/collops2/collrealkt/gen3/iter/iterable), verify-ktproj 9/9.
- **User `Comparable<T>` sorting (`il-comparable`)** — `listOf(v1,v2,v3).sorted()` over a user `class Ver :
  Comparable<Ver>` crashed silently (rt `sorted[T]` invoked an OPEN-generic `Array.Sort[T]` → "not fully
  instantiated"). Three coordinated fixes: (1) **bir2cir** no longer lets the name-only top-level `@ClrIntrinsic`
  fallback capture a call that has a REAL-BODIED (non-intrinsic) top-level sibling — `sort`'s 8 primitive-array
  intrinsics all bind "System.Array.Sort" (so the name wasn't "ambiguous"), yet `MutableList<T>.sort()` is a real
  Kotlin body; such names now substitute only on a sig-exact intrinsic match. (2) **bir2cir** new
  `ComparableBridgeSynthesis` pass: every emitted class implementing the generic `System.IComparable<X>` also gets
  the NON-generic `System.IComparable` + a `CompareTo(object)` forward bridge — the BCL convention the CLR-side
  natural-ordering dispatch (compareValues / the sortWith SAM shim's constrained fallback) depends on.
  (3) **ilemit** `clr:`/`clrg:` interface-slot wiring now disambiguates same-name body OVERLOADS by the slot's
  substituted param types instead of the name-keyed pick (which mis-wired the new CompareTo pair → TypeLoad).
- **`kotlin.Result` / `runCatching` (`il-result`)** — crashed silently (InvalidProgram inside the rt's
  `runCatching[R]`). Four coordinated fixes around a GENERIC class's companion statics: (1) **ilemit** anchors a
  static method of a generic emitted class (`Result<T>`'s companion `fun <T> success`) onto an `object`-instantiated
  owner (`TypeBuilder.GetMethod`) — the previous open-typedef parent token is invalid IL at a foreign call site;
  (2) **kotc** companion-member `callStatic` now carries the call's type args (`typeArgsJson`) so the anchored
  method is `MakeGenericMethod`'d; (3) **kotc** `ownerSpec` renders a STAR-projection type arg as `object` instead
  of dropping it (a dropped star collapsed `Result<*>.throwOnFailure`'s receiver owner to the bare open generic);
  (4) **ilemit** `new` ctor args now BOX to the ctor's declared `argTypes` (a bare `!!T` flowed unboxed into
  `Result(object)` — InvalidProgram at value instantiations).
- **Map delegation `val name by data` (`il-bymap`)** — three coordinated fixes across the layers it crosses:
  (1) **kotc** routes a delegated property whose convention resolved to a TOP-LEVEL extension (the stdlib
  `kotlin.collections.getValue/setValue`, MapAccessors.kt) by re-emitting the accessor body's RESOLVED call at the
  access site as the plain owner-null static call (receiver-first args + declared sig + typeArgs) — previously this
  fell to "unsupported delegated property". (2) **ilemit** canonicalizes `<>dotkt_KProperty`/`<>dotkt_KPropertyImpl`
  (added to `CanonicalSynthetics`): the synthetic is MONOMORPHIC (one get_name/ctor(string) shape everywhere, unlike
  KIterator_*), and a per-assembly copy made the rt's `MapAccessorsKt.getValue(map, thisRef, KProperty)` fail
  `EntryPointNotFound` on `get_name` when handed the APP's KPropertyImpl — apps now reference the rt dll's single
  copy (self-correcting: a --no-stdlib build still emits it locally). (3) **stdlib** `MapAccessors.kt` pins
  `getOrImplicitDefault`'s K to String via `(this as Map<String, V>)`: on the projected receiver `Map<in String, V>`
  the frontend approximates the captured K to Any — fine under JVM erasure, but reified CLR generics then dispatch
  `IDictionary<object,V>.ContainsKey` on a `Dictionary<string,V>` → EntryPointNotFound (a variance JVM-ism, discarded).
- **Generic method on a generic class, called with a CONCRETE owner instantiation (`il-generic4`)** — `Holder<int>.pairWith<string>()`
  threw `InvalidOperationException: … not fully instantiated` at runtime: ilemit's `ApplyTypeArgs` replaced the
  `TypeBuilder.GetMethod`-anchored member with the OPEN method's instantiation (`Holder`1::pairWith<string>`), losing the
  container's `<int>`. Fix (ilemit): when the constructed owner carries NO generic-parameter args, keep the anchored
  `MethodOnTypeBuilderInstantiation` and `MakeGenericMethod` it directly (the documented GetMethod→MakeGenericMethod
  order; verified supported on .NET 10 persisted emit). The erased-context path (owner constructed with enclosing
  generic params — the rt-stdlib self-instantiation case that broke a previous naive fix) is gated out unchanged.
- **Unsigned division/remainder/`toString(radix)` (bundle 【2】b-A)** — the 6 `UnsignedClr.kt` TODO stubs
  (`uintDivide`/`uintRemainder`/`ulongDivide`/`ulongRemainder`/`uintToString(base)`/`ulongToString(base)`) now have
  **real pure-Kotlin bodies** (JVM-actual ports; ULong via the Guava UnsignedLongs algorithm; radix `toString` via a
  self-contained digit loop — NOT `Long.toString(radix)`, whose call sites still lower to `Convert.ToString`,
  bases 2/8/10/16 only). **Zero compiler change**: direct `a / b` on UInt/ULong was already frontend-lowered to a raw
  `bin /` whose unsigned CLR operand type selects `div.un`/`rem.un` in ilemit (no BCL bind exists — `op_Division` on
  `UInt32`/`UInt64` is an explicit-interface generic-math impl, not a callable static). Fixes
  `UInt/ULong.toString(radix)` (previously threw `NotImplementedException`); verified incl. `2^63.toString(7)`,
  `ULong.MAX_VALUE.toString(10/16/36)`, unsigned div/rem edges (`2^63` divisor, `MAX/MAX`); `il-unsigned` unchanged.
- **Enum reflection `enumValues<T>()`/`enumValueOf<T>(name)`/`enumEntries<T>()`/`enumEntriesIntrinsic<T>()`
  (bundle 【2】b-B)** — kotc lowers the top-level reified intrinsics at the CALL SITE like `T.values()`/`T.valueOf()`
  (`ENUM_REIFIED_INTRINSICS`): a **rich** enum type arg → the synthesized static `values()`/`valueOf()`; a **basic**
  enum / generic-param type arg → the semantic `enumValues`/`enumParse` BIR nodes (`System.Enum.GetValues/Parse` in
  ilemit; an unknown name surfaces as `ArgumentException`, the CLR face of `IllegalArgumentException`). Previously
  every such call threw (`VerificationException`: the cross-module generic call's `T : kotlin.Enum<T>` constraint is
  unsatisfiable for a basic enum, which derives `System.Enum`). The entries family is not intercepted under
  `stdlibCompile` (the rt `enumEntries<T>` body would return `T[]` where `EnumEntries<T>` is declared — invalid IL).
  KNOWN GAPS (documented in the stubs): a RICH enum through a **non-inlined generic** context is invisible to
  `System.Enum` reflection; user-defined `inline fun <reified T : Enum<T>>` helpers still hit the pre-existing
  `kotlin.Enum<T>`-constraint emission issue (orthogonal — any Enum-bounded generic call, not enum reflection).
  Gates kept green: `il-enum`/`il-enumbody`/`il-enumrich`.
- **Generic `Array<T>` ops bound with real stdlib bodies (bundle 【2】a): `copyOf(newSize)`, `copyOfRange`,
  `plus(element)`, `plus(Array)`, `plus(Collection)`, `plusElement`, `orEmpty()`, `arrayOfNulls(reference, size)`**
  — all pure Kotlin in `runtime/stdlib/clr` (allocate via `arrayOfNulls<T>(n)` → generic `newarr !T`, reified-on-CLR;
  `TYPE_PARAMETER_AS_REIFIED` suppressed deliberately) mirroring the primitive-array siblings; **zero new
  `@ClrIntrinsic`/compiler special-casing**. Three compiler *wrong-code* fixes were required to make them behave:
  - ilemit `arraySet`/`clr.stelem`: don't `box` a value stored into a GENERIC-PARAM-element array (`stelem !T` with a
    boxed ref corrupts value-type instantiations — printed pointer bits); same guard as the local/field/coroutine box
    sites.
  - ilemit `FindReflectedMethodBySig`: STRUCTURAL matching for sig tokens `MapType` can't resolve at a cross-module
    call site (`gp:T`/`array:gp:T`/`clrg:X[gp:T]`), so `copyOf(array:gp:T,int)` selects the generic `copyOf<T>(T[],int)`
    over same-arity concrete siblings (previously: arity-pick chose `copyOf(sbyte[],int)` → short/sbyte reinterpretation
    garbage) and the three generic `plus` overloads stay distinguishable.
  - kotc BINARY operator lowering gated on "callee has NO extension receiver": `Array<Int> + 4` / `Array<String> + "d"`
    were lowered to a raw CIL `add` on the array REFERENCE (garbage/crash). Primitive operators are members and the IR
    compare intrinsics are top-level with plain params, so both still lower; stdlib `plus`/`minus` EXTENSIONS now emit
    real calls.
  - KNOWN GAP (pre-existing, unchanged): element reads of an `Array<Int?>` (e.g. the result of `Array<Int>.copyOf(n)`)
    emit `ldelem Nullable<int32>` against a runtime `int[]` — the nullable-primitive-array dual-representation is
    unresolved; reference-type `T` is fully correct. `enumValues`/`enumValueOf` skipped (need reified-enum lowering /
    typeArgs on `clrStatic` — compiler-side, follow-up).
- **facadegen interop bundle 【3】b closed — alias imports, op_* battery, C# extensions, dual-rep rule, I4 remnants
  (all verification + rule-setting; no compiler changes needed).**
  - **(5) aliased import**: `import System.Text.StringBuilder as SB` works end-to-end (the PSI import scan already
    canonicalizes the alias; Kotlin's import machinery binds it) — new gate `cases/il-alias`. A no-match .NET import
    warns in facadegen and errors at the frontend (nothing silent).
  - **`op_*` operators / C#-origin `[Extension]` methods**: full battery verified on a C# struct
    (`+ - * / unary-` + int/string extension receivers) — `cases/il-c1net` extended. `op_Equality`/`op_Inequality`
    deliberately unmapped (Kotlin `==` → `Equals(Any?)`); `op_Implicit`/`op_Explicit` skipped (no Kotlin analog).
  - **Dual-representation rule (DECIDED)**: an imported BCL type (`System.Text.StringBuilder`) and its stdlib alias
    (`kotlin.text.StringBuilder`) are TWO TYPED VIEWS of one CLR type — coexist, never unified; mixing is a clear
    frontend type error; explicit cast is the escape hatch. `docs/dotkt-semantics.md` §8b; gate `cases/il-dualrep`.
  - **I4 remnants assessed, all working**: .NET enum import (read/pass/`==`/`when`), generic delegates
    (`Func<int,int>` + custom `Mapper<T>`), nullable value types (`int?` both directions), `out`/`ref` (il-outref) —
    new gate `cases/il-netinterop` locks enum+delegate+nullable.
- **Collection/sequence + language-feature 4-bug batch: `il-sort`/`il-collmore`/`il-regex`/`il-langf` all green
  (run-correct AND ilverify-clean); verify-il fail-names 18 → 9, PASS(run) 121 → 124, ktproj 9/9.**
  - `sorted`/`sortedDescending`/`sortedBy`: three JVM erasure-isms fixed stdlib-side —
    `naturalOrder()`/`reverseOrder()` singleton-cast (now genuinely generic comparator classes), `sortedWith`'s
    `toTypedArray as Array<T>` fast-path (now the `toMutableList` branch), and `compareValues`' `as
    Comparable<Any>` cast (now dispatched through the NON-generic `System.IComparable` via the internal
    `ClrRawComparable` binding; ilemit's `cast` boxes a value/generic source before `castclass`).
  - RC2 transform side: a `(T) -> R?` function slot preserves its return nullability
    (kotc `func:nullable:gp:R:...`) and bir2cir's new `NullableFuncReturnErasure` lowers every nullable-marked
    func return to `Func<…, object>` uniformly (backing lambda rets erased + local dataflow repaired), fixing
    the delegate-reinterpretation crashes (`mapNotNull` InvalidProgram, `sortedBy` AccessViolation).
  - kotc inline-splice type-arg substitution re-keyed by `IrTypeParameter` SYMBOL (a name-keyed map erased a
    caller's same-named generic to `object` and cross-captured outer params: `mapNotNullTo`→`forEach`,
    `let<T,R:=Unit>`).
  - `MutableCollection.add`/`addAll` calls route to new `clrCollAdd`/`clrCollAddAll` stdlib defaults
    (`ICollection<T>.Add` is void vs Kotlin's changed-Boolean; `addAll` has no BCL slot).
  - Rule-3 helper calls carry their receiver-first `sig` so the String→CharSequence bridge wraps raw-string
    args (`Regex.matches`/`find` ilverify StackUnexpected).
  - kotc no longer emits class-inherited fake-override property accessors as empty-bodied methods (ilverify
    ReturnMissing on every derived class of a property-carrying base) — also greened
    `netbase`/`netbase2`/`netgen2`/`customexc`/`mc1`; abstract interface-only fake-overrides are kept (CLR
    re-declaration requirement). ilemit base-chain resolution handles the inner-generic `base[gp:E]` encoding
    (`BareTypeKey`) and probes interface tokens best-effort.

### Added
- **facadegen interop gaps (3)+(6) closed and gate-covered: constructed-generic member types + transitive
  injection.** Verified end-to-end and hardened: a .NET member typed as a constructed generic
  (`IList<Widget>`, `IReadOnlyList<Widget>`, `Dictionary<String,Widget>`, `IEnumerable<String>`) resolves as the
  real generic type (not `Any?`), and types appearing only in member signatures (never imported) are injected
  transitively by the facadegen reachable-closure BFS — full closure with a 5000-type cap, NOT depth-limited, so
  a 2-hop chain (`w.Make(): Gadget` → `g.Core(): Sprocket`) works with zero extra imports. New fix on top: for-in
  over an **interface-typed** receiver (`for (n in panel.Names())` where `Names(): IEnumerable<String>`) — the
  frontend-only `iterator` marker is now emitted on the injected `IEnumerable<T>` interface itself (abstract
  member; derived interfaces `IList<T>`/`ICollection<T>`/`IReadOnlyList<T>` inherit it through the generic super
  chain, one declaration point → no duplicate-member clash with a concrete class's own marker). New gate sample
  `cases/il-transinj`; 15 existing injection samples re-verified green. `docs/dotkt-interop-feedback.md` (3)/(6)
  and `docs/future-work-interop.md` #4 marked RESOLVED.
- **`Map`/`MutableMap` → `IDictionary<K,V>` dual-rep (Track B) — real Kotlin maps run on BCL dictionaries.** BOTH
  interfaces are `@ClrTypeAlias("System.Collections.Generic.IDictionary")` — deliberately NOT the List-style
  read-only/mutable split (IDictionary does not extend IReadOnlyDictionary, so a split breaks `MutableMap : Map`
  verifiability on the hot path; both-IDictionary mirrors Kotlin/JVM's java.util.Map erasure — see
  `docs/dotkt-semantics.md §5c`). Kotlin-semantic members route through the new rt `kotlin.collections.ClrMapDefaults`
  via bir2cir **Rule 5m** (2-type-arg `MapDefaultCall`): null-on-missing `get` (= `ContainsKey` + raw `get_Item`),
  previous-value-returning `put`/`remove`, `putAll`/`getOrDefault`/`isEmpty`/`containsValue`, and the
  `keys`/`values`/`entries` views (pure-Kotlin snapshot Sets; entry values live). `size`/`containsKey`/`clear` and
  `MutableMap.keys`/`values` bind 1:1 (`Count`/`ContainsKey`/`Clear`/`Keys`/`Values`). `il-collrealkt` and `il-mapdes`
  now run correct end-to-end (`mapOf`/`mutableMapOf`/`associate`/`for ((k,v) in m)`); `il-collops2`'s partition/
  associate/withIndex/scan/runningFold/getOrElse lines all pass (blocked only by the separate `windowed` gap).

### Fixed
- **kotc: rich-enum user properties now follow the CLR property model** (`il-enumbody`/`il-enumrich` greened).
  `richEnumDef` emitted a ctor-val property (`enum class Op(val sym: String)`) as a bare public FIELD while the
  general access site emits `callInstance get_<name>` → ilemit crashed `Op.get_sym not found`. The lowering now
  mirrors `typeDef`: internal backing field + real `get_`/`set_` accessor methods + a `properties` entry.
- **frontend jar: `@JvmInline` platform actual** (`il-valclass` greened). `kotlin.jvm.JvmInline` existed only as the
  `@OptionalExpectation` common `expect`, so any app `@JvmInline value class` failed the frontend ("can only be used
  in common module sources"). `build-clr-stdlib-frontend.sh` now stages a `JvmInlineActual.kt` (exactly the existing
  `JvmName` precedent). A `value class` lowers to a real wrapper class — see `docs/dotkt-semantics.md` §10.3.
- **ilemit: arity-changing constructed base-interface member/property resolution.** `PropAccessor` and
  `ResolveInheritedIfaceMethod` only walked SHARED-arity interface chains; `IDictionary<K,V>.Count`/`Clear` live on
  `ICollection<KeyValuePair<K,V>>` (2→1, constructed arg). New `SubstituteIfaceArgs` substitutes the open definition's
  type parameters positionally through the (possibly nested-constructed) base reference — a strict generalization.
- **ilemit: duplicate `(name, params)` method defs no longer merge into one MethodBuilder.** Kotlin overload pairs
  distinguished only by receiver types that COLLAPSE under an alias (`Map.iterator()`/`MutableMap.iterator()` both →
  `IDictionary<K,V>`) had both bodies written into a single builder (concatenated IL → `BadImageFormatException`, one
  body-less method). The second-and-later defs now get deterministic `$dupN` names; the first keeps the clean name.
- **kotc: deleted the legacy `Map.Entry.component1/2` → `KeyValuePair.Key/.Value` lowering** (CLR knowledge in kotc;
  it read the new ref-object entries as a struct → garbage values in `for ((k,v) in map)`). The components now emit as
  plain Kotlin extension calls resolved via the rt stdlib; bir2cir `RecvKey` learned to normalize NESTED ref-type
  names (`kotlin.collections.Map`2+Map$Entry`2` → `kotlin.collections.Map$Entry`) so the attribution matches.
- **bir2cir: `IteratorConsumerNormalization` generalized to rt-returned iterators.** Iterator-typed for-loop vars
  initialized from a `kotlin.*` owner (Set.iterator(), MapsKt.iterator(map)) and `<>dotkt_KIterable_*` synthetic
  consumers with rt receivers (`xs.withIndex()` loops) are re-pointed at the real `kotlin.collections.Iterator[E]` /
  the ClrIteratorBridge. Receiver-gated: app-emitted synthetic producers (il-iter/il-iterable) are untouched.
- **stdlib: `emptyMap()` returns a Dictionary-backed map** — the pure-Kotlin `EmptyMap` singleton cannot satisfy the
  IDictionary surface under the alias (its type fails to load). Read-only-ness stays frontend-enforced.
- **`String.format` as CLR platform API — .NET composite format, bound to `System.String.Format` (fixes `il-fmt` +
  `il-bmore` frontend failures).** Kotlin/JVM's `format` is JVM-only platform API (Native/JS have none); DotKt now
  provides its own: `fun String.Companion.format(format, vararg args)` + `fun String.format(vararg args)` in the CLR
  stdlib (`runtime/stdlib/clr/kotlin/text/StringsClr.kt`), delegating to a private `@ClrIntrinsic("System.String.Format")`
  helper — the format string is the **.NET composite format** (`"{0} items"`, `"{0:D5}"`, `"{0,-4}"`), NOT Java printf
  (`"%d"`), per the host-conventions rule (recorded in `docs/dotkt-semantics.md §5`). No compiler special-case: the
  binding is pure stdlib metadata. One general bir2cir rule landed with it: a **companion `INSTANCE` load on a
  CLR-bound owner** (`String.Companion` as the receiver arg of a companion-extension call) lowers to a null `object`
  const — the substituted BCL type (`System.String`) has no companion singleton and the flattened-companion `__self`
  param is never read. This makes companion-extension bindings (`Double.Companion.fromBits`, `CASE_INSENSITIVE_ORDER`)
  callable from apps in general, not just `format`.
- **`CharSequence` is `System.String` on the CLR — app-own declarations (the 3-point model, points ①/②).** A
  JVM-shaped `kotlin.CharSequence` has no faithful .NET equivalent, so DotKt models it as `string` (an immutable
  snapshot). New bir2cir pass `CharSeqStringLowering` (app build, no user `class S : CharSequence`): a CharSequence-typed
  param/return/local/field → `System.String`; member reads (`length`/`get`/`subSequence`) → `System.String.Length`/
  `get_Chars`/`Substring(a, b-a)`; a non-`String` value (a `StringBuilder`) flowing into a now-`string` slot is snapshot
  with an implicit `.toString()` (a `String` flows directly). Composes with the existing `StringCharSequenceBridge` (a
  now-`string` value into an un-rebuilt stdlib CharSequence-extension is still adapter-wrapped). Sample: `il-charseqs`.
  The synthetic `<>dotkt_CharSequence` is RETAINED for a user `class S : CharSequence` supertype (sealed `System.String`
  can't be subclassed) — an assembly declaring one keeps `CharSequence` polymorphic assembly-wide (`il-charseq`/
  `il-charseqx` unchanged). Snapshot-not-live-view deviation recorded in `docs/dotkt-semantics.md §5b`; design +
  landed/deferred split in `docs/design-charsequence-clr-string.md`. DEFERRED (needs a stdlib rebuild): lowering the
  stdlib's OWN CharSequence-extension signatures to `string` — the change that would retire the 5 still-lowered String
  ops (`trim`/`reversed`/`padStart`/`replace(S,S)`/`isBlank`).

### Fixed
- **`StringBuilder` → `Appendable` dual-rep: `joinToString`/`joinTo` now run (bundle 4-C RC1 blocker (1)).** `Appendable`
  is a JVM-shaped abstraction with no distinct .NET representation — the only CLR appendable char sink is
  `System.Text.StringBuilder` (its sole CLR implementer) — so, mirroring the `CharSequence`→`System.String` collapse,
  it is now `@ClrTypeAlias("System.Text.StringBuilder")` (stdlib). bir2cir lowers every `Appendable` token from the
  ref.dll, so the generic bound `A : Appendable` on `joinTo` becomes the satisfiable `A : System.Text.StringBuilder`
  (was: `VerificationException` "type argument System.Text.StringBuilder violates the constraint of A"). Three supporting
  codegen fixes make the joinTo/appendElement body run: (a) **ilemit** — the name+arity overload FALLBACK could pick a
  BCL overload the arg is NOT assignable to (a `<>dotkt_CharSequence` into `StringBuilder.Append(String)` reinterpreted
  the object as a string → memory corruption "Destination is too short"); it now keeps only overloads whose params
  ACCEPT the resolved arg, preferring the most-specific — a real `String` binds `Append(String)`, a synthetic ref binds
  `Append(object)` (which ToStrings it); (b) **ilemit** — `x is T` / `x as? T` on a value-type / generic-param receiver
  emitted `isinst` on an UNBOXED value → NRE; it now boxes a value-type/gp receiver first (as C# does for `element is X`
  on a generic `T`), exposed by `appendElement`'s `element is CharSequence?`/`element is Char`; (c) **bir2cir** — the
  `<>dotkt_StringCharSequence` adapter gained a `ToString()` override returning its backing string, so
  `Append(object).ToString()` materializes the real content. Greens `il-mapfilter`/`il-coll2`/`il-mutcoll`/`il-arrops`;
  unblocks `il-collrealkt` up to `Map.get` (the separate Map/MutableMap dual-rep track).
- **Cross-module default arguments via a 2-tier rule (bundle 4-C RC1).** kotc emits only the args a caller wrote
  (correct); the frontend jar drops a callee's default VALUES (`IrErrorExpression`), so an OMITTED cross-module default
  is filled by one of two per-parameter mechanisms, chosen by "can the param's own CLR type carry the default as a
  `[DefaultParameterValue]`?": **Tier 1** (a primitive/String/null const on a matching param) → native `[Optional]`+
  `[DefaultParameterValue]` (C#-consumable, unchanged); **Tier 2** (a String const on a `CharSequence`/interface param —
  a string constant can't sit on an interface-typed param — or ANY non-constant default) → the param is emitted REQUIRED
  and its default EXPRESSION is carried as embedded BIR on the new `@kotlin.clr.KotlinDefault(index, bir)` attribute
  (ref.dll-only, mirroring `[KotlinInline]`); bir2cir's `DefaultArgSplice` pass reads it and splices the expression as
  the omitted arg (before the CharSequence bridge + type lowering, so a String default is coerced/lowered exactly like an
  explicit arg — and callee-scope evaluation now handles a param-referencing default). A Tier-2-carrying function stamps
  `@KotlinDefault` on ALL its defaulted params (uniform contiguous splice source). `listOf(1,2,3).joinToString("-")` now
  fills all 7 args and dispatches correctly (the prior stack-underflow / `InvalidProgramException` is gone). NOTE: the
  `joinToString` SAMPLE remains blocked DOWNSTREAM by a separate pre-existing dual-rep bug (`joinTo`'s `A : Appendable`
  constraint unsatisfiable by the BCL-aliased `StringBuilder`), tracked in `docs/master-task-inventory.md §4-C RC1`.
- **Value-type nullable generic return (`T?`) now round-trips as `System.Nullable<T>` (bundle 4-C RC2).** A Kotlin
  `fun <T> …(): T?` has its nullability erased by kotc to a bare `gp:T` return (`Nullable<T>` is inexpressible for an
  unconstrained T), with the null case emitted as `ldnull`. That is correct for a reference T, but for a VALUE T
  `ldnull; ret !!T` collapses to `default(T)=0` — null-ness was LOST: `listOf(10,20).firstOrNull()` returned `0` (not
  `10`), and the result stored into a `Nullable<int>` slot corrupted (`ilverify: found Int32, expected Nullable<int32>`).
  The CLR-faithful representation of a generic `T?` is `System.Object` (the boxed/erased nullable form, which carries a
  real null for a value T): `bir2cir.NullableGenericReturnErasure` (all builds, so ref.dll + rt.dll signatures agree)
  rewrites a `ret=gp:X` + `retNullable=true` method to return `object`; ilemit boxes value/gp returns and, at the call
  boundary (`CoerceReturn`), converts the `object` actual to the caller's `Nullable<V>` (`unbox.any`) or reference type
  (`castclass`). Reference-type nullable returns keep working. Now `listOf(10,20).firstOrNull()`=10,
  `listOf<Int>().firstOrNull()`=null, `lastOrNull` correct. (`mapNotNull`'s transform-side `R?` is a separate,
  kotc-gated case — the delegate-return nullability is not preserved in the BIR func token.)
- **ilemit — a duplicate-emitted reflected overload resolves to the first exact match.** `FindReflectedMethodBySig`
  returned null on a SECOND exact-signature match ("ambiguous"), but two methods matching the same sig token have
  identical parameter types — so a second match can only be a DUPLICATE method emission (the stdlib expect/actual
  fileClass merge emits some top-level fns twice; `_ArraysKt.sum(int[])` carries two distinct method tokens). The null
  dropped to the arity-only fallback, which picked the wrong same-arity overload: `arrayOf(3,1,4,1,5).sum()` bound to
  `sum(sbyte[])` and read the int[] as bytes → `4` instead of `14`. Now keeps the first exact-sig match.
- **ilemit resolves members/fields on referenced generic Kotlin types (bundle 4-C RC3+RC4).** An APP that links the rt
  stdlib via `--ref` and touches a REFERENCED generic Kotlin type absent from this assembly's `_types` crashed at emit.
  **RC3:** a call on an un-substituted generic Kotlin interface owner — `kotlin.collections.Iterator[gp:T]`.hasNext/next
  (the `ClrIteratorBridge` rewrite of `for (x in genericIterable)`) or `kotlin.collections.Map[gp:K,gp:V]`.get — NRE'd at
  `ResolveMethod` because `FindMethod` returned 0 candidates: `ParseOwner` strips the `[gp:..]` args off, leaving the BARE
  open name, but reflection knows a generic interface only by its arity suffix (`Iterator`1`/`Map`2`). `FindMethod`'s
  external branch now probes `typeName`+backtick-N (N=1..8) and takes the unique resolvable open definition;
  `ResolveMethod`'s existing `TypeBuilder.GetMethod` re-anchors it onto the constructed instantiation. **RC4:**
  `kotlin.Pair`.first/.second (a destructuring `component1()`/`component2()` that kotc lowers to a `field` access) hit
  `FindField` KeyNotFound on the external `kotlin.Pair`; once resolved, a direct `Ldfld` of the PRIVATE backing field
  threw `FieldAccessException` cross-assembly (the CLR property model gives every Kotlin property a private backing field
  + public accessors). `FindField`/`ResolveField` gain the same external-type reflection fallback (incl. the arity probe),
  and a new `ExternalPropAccessor` routes an external type's `field` read/write through the public `get_`/`set_<name>`
  accessor (falling back to the field for a public `@ClrField`). Greens `il-genclosure`/`il-genhof`/`il-pair` (verify-il
  run-FAIL 15 → 8) and advances `il-collops2`/`il-collrealkt`/`il-mutcoll` past emit. The three §4-C target samples don't
  fully green yet — each also calls `joinToString` (blocked by the separate rt-baked `StringBuilder`→`Appendable`
  dual-rep) and `collrealkt`/`collops2` additionally hit the Map/MutableMap dual-rep (`mapOf`/`associate` return a BCL
  `Dictionary` that doesn't implement `kotlin.collections.Map`) — separate dual-rep tracks, tracked in
  `docs/master-task-inventory.md §4-C`.

### Changed
- **Retired the clean kotc String-op lowerings (bundle 4-B) — now that CharSequence is canonical.** Building on 4-A,
  the hardcoded `kotlin.text` String lowerings in kotc (`STRING_OPS` + the `BirEmitter` emit sites) are RETIRED for the
  ops whose real stdlib `CharSequence`-extension bodies now run cross-assembly: `contains`, `indexOf`, `startsWith`,
  `endsWith`, `split`, `substring(2-arg)`, `isEmpty`/`isNotEmpty` (joining the earlier uppercase/lowercase/
  substring(1)/NUMBER_PARSE). kotc emits a PLAIN call; bir2cir attributes it to `StringsKt` and the CharSequence bridge
  coerces the `String` receiver/args → the real Kotlin body runs. Two supporting fixes made this work:
  - **ilemit — sig-aware overload resolution on a referenced file-class.** `FindMethod`→`FindReflectedMethod` on an
    EXTERNAL file-class (the rt `StringsKt`) disambiguated only by arity, so a String-face `substring(String,int,int)`
    vs a CharSequence-face `substring(<>dotkt_CharSequence,int,int)` was an arbitrary pick → the wrong body ran
    (`EntryPointNotFound`). New `FindReflectedMethodBySig` matches each `sig` token's mapped `Type` to the reflected
    parameters (the same signature keying the in-`_types` path already does via `MethodsBySig`); falls back to the
    arity pick on any miss — purely additive.
  - **bir2cir — the StringCharSequenceBridge now runs on the RT stdlib self-build too** (gate widened from
    `attributeTopLevelOwner` to `!RefBuild`). The stdlib's own `CharSequence`-extension bodies widen a `String` into a
    `<>dotkt_CharSequence` slot INTERNALLY (`CharSequence.indexOf(string: String)` → the private
    `indexOf(other: CharSequence)`), which the compiled rt.dll body left as a raw String passed where the interface is
    required → `InvalidProgram`/`EntryPointNotFound` at run. The bridge now materializes those internal coercions and
    injects the adapter once into the rt assembly (implementing the RT's canonical `<>dotkt_CharSequence`). Ref build
    still skipped (its bodies are squashed to `throw`).

  Gate-neutral to gate-improving: the verify-il run-fail set is IDENTICAL, and the ilverify set improved 21→20
  (`il-tryexpr` is now fully green — the rt.dll's fixed internal coercions). `il-str`/`il-substr`/`il-char`/`il-charseq`/
  `il-charseqx` all pass. STILL LOWERED (each a DISTINCT deeper stdlib-body bug — a follow-up, no longer dual-rep):
  `trim`/`trimStart`/`trimEnd` (`Char::isWhitespace` method-ref not lowered + un-wrapped inlined `as CharSequence`),
  `reversed` (`StringBuilder(CharSequence)` has no .NET ctor), `padStart`/`padEnd` (StringBuilder append/capacity
  mis-bind), `replace(String,String)` (StringBuilder `append(seq,start,END)`→`Append(str,start,COUNT)`),
  `isBlank`/`isNotBlank` (`all { isWhitespace }` → CharSequence iteration `Iterator.hasNext` not found).
- **Retired the kotc String-indexer lowering `s[i]`→`get_Chars` (bundle 4-B).** kotc no longer hardcodes
  `String s[i]` → `System.String.get_Chars`; `kotlin.String.get(index)` carries `@ClrIntrinsic("get_Chars")`, so kotc
  emits the plain operator-`get` member call and bir2cir's `MemberCallSubstitution` rewrites it to
  `clrInstance System.String.get_Chars` off the ref.dll. Gate-neutral (run-fail set + ilverify set identical);
  `il-charseq` (a user `class S : CharSequence` that indexes `s[index]`) still passes.
- **Retired the kotc Regex lowering (bundle 4-B).** kotc no longer hardcodes `"p".toRegex()`→`new Regex`,
  `r.containsMatchIn(s)`→`IsMatch`, `r.replace(...)`→`Replace`. `kotlin.text.Regex` is
  `@ClrTypeAlias("System.Text.RegularExpressions.Regex")` with `containsMatchIn`/`replace` bound
  `@ClrIntrinsic("IsMatch")`/`("Replace")` and real Kotlin bodies for `matches`/`find`/`split`/`.value` (over the
  `ClrMatch`/`ClrMatchResult` adapters). kotc emits plain calls; bir2cir substitutes the ctor + members off the ref.dll
  and runs the real bodies. `il-regex` RUN-passes; gate-neutral (run-fail set + ilverify set identical — `il-regex`
  stays run-pass / ilverify-fail exactly as in the baseline). NB retiring did NOT clear the `il-regex` ilverify FAIL:
  the `@ClrIntrinsic("IsMatch")`/`("Replace")` bindings sit on a `CharSequence` param but the BCL method takes `string`,
  so the substituted call carries the Kotlin `<>dotkt_CharSequence` argType while a raw `string` is pushed
  (`StackUnexpected`); and the `find`/`.value` bodies (`ClrMatchResult : MatchResult`, `ClrMatchGroupCollection :
  AbstractCollection`) have their own verify noise. Both are stdlib-body/binding follow-ups (materialize the
  `CharSequence` via `toString()` behind a `nativeIsMatch(String)`/`nativeReplace(String,String)` helper, mirroring the
  existing `nativeMatch`/`nativeReplaceFirst` pattern), not kotc lowerings. The kotc TYPE token map
  `kotlin.text.Regex`→`clr:System...Regex` (`BirEmitter.kt`) is left in place (a type-token concern like the `netType`
  maps, separate from the call lowering).

### Added
- **CharSequence synthetic CANONICALIZATION (bundle 4-A) — cross-assembly CharSequence now works.** The synthetic
  interface `<>dotkt_CharSequence` (kotc emits it for `kotlin.CharSequence`, which has no faithful BCL equivalent) is
  now emitted ONCE, publicly, in the rt stdlib dll and REFERENCED by app assemblies instead of re-synthesized
  per-assembly. Previously every dll emitted its OWN copy — a DISTINCT CLR type — so a value crossing the app↔rt
  boundary (a stdlib `CharSequence`-extension called with an app value) threw `EntryPointNotFoundException`
  (`<>dotkt_CharSequence.get_length` not found on the rt-dll copy). ilemit now (1) SKIPS the local definition of a
  canonical synthetic when it already resolves in a `--ref`'d assembly (self-correcting: a `--no-stdlib` build, or the
  stdlib's own ref/rt build — which passes ilemit no `--ref` — still emits it locally), and (2) binds a user
  `class S : CharSequence` (and bir2cir's injected foundation-A `<>dotkt_StringCharSequence` adapter) to the EXTERNAL
  canonical interface by reflection (the existing `clr:` MethodImpl path). Reference/method resolution already routed a
  non-`_types` `@<>dotkt_X` through `ResolveType`/`FindMethod`→reflection, so no call-site changes were needed. Scoped
  to `CharSequence` — the other shared synthetics (`Result`/`KProperty`/`KIterator_*`/`RWProperty_*`) still re-emit
  per-assembly until each is verified cross-assembly. This UNBLOCKS retiring the remaining `STRING_OPS` + the `s[i]`
  indexer + Regex (their stdlib bodies are `CharSequence` extensions). New sample `il-charseqx`:
  `S("hello").hasSurrogatePairAt(0)` (user CharSequence → stdlib ext) and `"hi".hasSurrogatePairAt(0)` (String →
  foundation-A adapter → stdlib ext) both run. verify-il gate-neutral (36-fail set identical; PASS +1 for il-charseqx),
  verify-ktproj 9/9. kotc/bir2cir unchanged; ilemit only (CLR codegen reading .NET metadata — the layer that owns type
  resolution). NB `il:injstatic` is a SEPARATE root cause (rule-3 misrouting of an app facadegen-injected static member
  into the non-existent stdlib `<>dotkt_ClrH_<Type>` body-hoist helper), NOT this per-assembly-duplication pattern.
- **String → CharSequence adapter bridge (bundle 4-A FOUNDATION).** A bare `System.String` flowing into a
  `kotlin.CharSequence` slot now works polymorphically (`val cs: CharSequence = "abc"; cs.length` → `3`, `cs[1]` →
  `'b'`; a `String` literal passed to a `CharSequence`-typed function). `kotlin.String` is `@ClrTypeAlias("System.String")`
  — a **sealed** BCL type that cannot implement the synthetic `<>dotkt_CharSequence` interface kotc emits for
  `kotlin.CharSequence` — so bir2cir now MATERIALIZES the coercion: a new `StringCharSequenceBridge` pass detects a
  statically-`String` value flowing into a `<>dotkt_CharSequence` slot (a call's CharSequence-typed arg / extension
  receiver, a `CharSequence` return, a `CharSequence`-local store, an `as CharSequence` cast) and wraps it in
  `new <>dotkt_StringCharSequence(str)` — an **app-local** adapter class the pass injects (String-backed
  `length`/`get`/`subSequence`, modeled on the verified user-`class S : CharSequence` shape). App-local because the
  synthetic interface is emitted per-assembly: a stdlib adapter would implement the rt-dll copy, unreachable by the
  app's interface dispatch. Purely additive — wraps ONLY positively-`String` values, never an already-`CharSequence`
  one — so kotc's `STRING_OPS` (statically-`String`-receiver ops) and every passing sample are untouched. APP builds
  only (ref/rt stdlib self-builds byte-identical). kotc unchanged; ilemit emits the injected type as ordinary CLR.
  verify-il gate-neutral. NOTE: this unblocks intra-assembly CharSequence polymorphism, but calling a *stdlib*
  CharSequence-extension with an app value crosses the app↔rt synthetic-interface boundary — a separate, deeper blocker
  for the String-op retire (B) / Regex follow-ups (see `docs/master-task-inventory.md` 【4-A】).

### Changed
- **Layer-purity: retired kotc's hardcoded `kotlin.math.* → System.Math` lowering (the pilot of the "retire a kotc
  hardcoded CLR lowering" pattern).** kotc no longer rewrites `kotlin.math` calls into `clrStatic System.Math` nodes
  (`MATH_FUNCS` + the `BirEmitter` emit site are gone); it emits a plain call and bir2cir's `MemberCallSubstitution`
  substitutes it from `MathClr.kt`'s existing `@ClrIntrinsic` bindings on the ref.dll (no stdlib change needed). Also
  fixed a latent bir2cir bug this exposed: the top-level `@ClrIntrinsic` index was keyed by function NAME only, so
  arg-type-discriminated overloads collided — `sqrt`/`abs`/`pow`/… and `isNaN`/`isInfinite`/`isFinite` silently used
  the `System.Math`/`System.Double` overload for Float args instead of `System.MathF`/`System.Single`. Now resolved by
  the exact call signature (Float math correctly hits `System.MathF`). verify-il gate-neutral (fail-set identical).
- **Layer-purity: retired kotc's hardcoded `String`/`Char` CLR lowerings (bundle 1, batch 2).** Following the Math
  pilot's recipe, kotc no longer hardcodes the *cleanly-substitutable* `kotlin.text` ops — it emits a plain call and the
  already-built bir2cir `MemberCallSubstitution` consumes the stdlib's `@ClrIntrinsic` bindings off the ref.dll:
  - **String family:** `uppercase`/`lowercase` (→ `@ClrIntrinsic` `ToUpperInvariant`/`ToLowerInvariant`),
    `substring(startIndex)` (→ `Substring`), `"42".toInt()`/`toLong`/`toDouble`/`toFloat`/`toShort`/`toByte` (→
    `System.X.Parse`; the `NUMBER_PARSE` map deleted), and `repeat(n)` (the real StringBuilder body). `String.format`
    deleted as **dead code** — a `java.util.Formatter` JVM-ism the CLR frontend jar has no symbol for (unresolved
    before the backend runs); making it work is a stdlib `String.Companion.format` `@ClrIntrinsic` binding, not a kotc
    lowering.
  - **Char family:** `isDigit`/`isLetter`/`isWhitespace`/`isLetterOrDigit`/`uppercaseChar`/`lowercaseChar`/
    `isUpperCase`/`isLowerCase` (the `CHAR_OPS` map deleted) → `CharClr.kt`'s `@ClrIntrinsic("System.Char.*")` FQ
    bindings, substituted to `clrStatic System.Char.*`.
  - **Reusable bir2cir fix:** the bare-`@ClrIntrinsic` extension-fun index was keyed by `name|recvKey`, so a same-name/
    same-receiver overload of a **different arity** collided — `substring(String,Int)` captured the 3-arg
    `substring(String,Int,Int)` call and emitted `Substring(start,end)` with `end` read as a LENGTH. Now keyed by
    `name|recvKey|paramCount` (the sibling of the Math pilot's full-signature keying).
  - **DELIBERATELY KEPT lowered (blocked, not retired):** `trim`/`contains`/`startsWith`/`endsWith`/`replace`/
    `indexOf`/`padStart`/`padEnd`/`split`/`reversed`/`substring(start,end)`/`isEmpty`/`isBlank` — their stdlib bodies
    are `CharSequence` extensions, so a `System.String` receiver hits the known String/CharSequence
    **dual-representation** crash (InvalidProgram / EntryPointNotFound). And **`Int/Long.toString(radix)`** (the
    `System.Convert.ToString` lowering) — bir2cir attributes it correctly but the stdlib digit-loop body miscompiles
    cross-module (base-2 OK, but `255.toString(16)` → `"ffffffff"`), so retiring would ship a correctness regression.
    Both retire only once the underlying stdlib/emit bugs are fixed. verify-il gate-neutral (il fail-set + full FAIL
    list identical before/after).
- **Layer-purity: retired kotc's hardcoded `System.Console` (`println`/`print`) + `readLine` CLR lowering (bundle 1,
  batch 3).** kotc no longer emits the hardcoded `{"k":"console"}` node — `println`/`print` are emitted as PLAIN
  top-level fun calls and bir2cir's `MemberCallSubstitution` substitutes them to `System.Console.Write`/`WriteLine`
  from `ConsoleClr.kt`'s existing `@ClrIntrinsic` bindings (top-level-intrinsic-by-name path; both are unambiguous, so
  no stdlib or bir2cir change was needed). Value-type args box via ilemit's `EmitArg` (`object` param); the Kotlin
  collection→`clrCollToString` toString adapter is KEPT (Kotlin semantics — it calls a stdlib helper, not a CLR
  member). Deleted the now-dead ilemit `case "console"` consumer. `readLine()` deleted as **dead code** (like
  `String.format`): the CLR frontend jar has no `kotlin.io.readLine` symbol — the CLR I/O API is `readln()`/
  `readlnOrNull()` (the latter `@ClrIntrinsic`-bound to `System.Console.ReadLine`). verify-il gate-neutral (fail-set
  identical, 36). This closes the mechanically-retirable part of bundle 1; the rest of the batch-3 families are
  **BLOCKED on the dual-rep/collection-bridge (bundle 4) or the deferred delegate/coroutine layers, NOT retired:**
  - `use{}`/`IDisposable.Dispose` — a structural `try/finally` inline desugar; `close→Dispose` is a `clrName`
    member-rename (shared with the class-emit path), not an `@ClrIntrinsic` call-substitution.
  - `by lazy`/`System.Lazy<T>` — structural delegate construction (`new Lazy<T>(Func<T>)` + `Value`); `kotlin.Lazy`
    is a Kotlin interface with Kotlin implementors and there is no `@ClrIntrinsic` factory to substitute.
  - `compareTo`/`IComparable.CompareTo` — the primitive path is the primitive dual-rep and the user-`Comparable<T>`
    path is a `constrained.` callvirt (structural CLR lowering); `il-comparable`'s open Comparable-self dual-rep bug.
  - indexer `get_/set_Item` — `String s[i]`→`get_Chars` is the String/CharSequence dual-rep (same class as the
    batch-2-blocked String ops), and the injected-`.NET`-indexer arm is per-sample facadegen metadata (NOT stdlib
    ref.dll), so bir2cir cannot substitute it.
  - `listOf`/`setOf`/`mapOf`→`listNew`/`setNew`/`mapNew` — structural collection-literal factories that must retire
    together with the `COLLECTION_MEMBER`/`COLLECTION_OPS` clrName table (the collection-bridge, bundle 4).
  - `Regex` — CharSequence dual-rep + `MatchResult` adapters (`find`/`value`). `Task.Delay` — SKIPPED (inside
    `coAwaitable`, the coroutine await machinery; the coroutine layer is deferred to bundle 6).
- **Round-trip carrier attributes for Kotlin class-nature (`sealed` fully; `fun interface` nature) — re-consuming a
  DotKt `.dll` as Kotlin restores more of the original surface (round-trip gaps ③ + ⑤).** A `fun interface` (SAM) and a
  `sealed` class/interface lower to a plain CLR interface / abstract-class, dropping the Kotlin nature. Now: kotc emits
  `isFun` (from `IrClass.isFun`) and `isSealed` (from `Modality.SEALED`) BIR flags; ilemit synthesizes + stamps two new
  embedded metadata attributes `[KotlinFunInterface]` / `[KotlinSealed]` (the same self-embedded model as
  `[KotlinFunction]`/`[KotlinFileClass]`, stripped in the runtime build); facadegen reads them back as `funinterface` /
  `sealed` meta lines; and `ClrTypeInjection` restores `status.isFun` / `Modality.SEALED` on the re-consumer's FIR.
  - **`sealed` (⑤) round-trips fully:** the modality, **cross-module inheritance enforcement** (a rogue subclass in
    another module is rejected), AND **exhaustive `when` with no `else`** — the closed inheritor set is rediscovered
    because the sealed type's subtypes are themselves injected into the consumer's session via their `super` edges.
  - **`fun interface` (③) restores the NATURE, not the lambda:** a consumer sees a functional interface and can
    implement it (incl. anonymous `object : Handler { … }`), but a bare **lambda** still won't SAM-convert — the pinned
    Kotlin 2.2.0 `FirSamResolver.computeSamCandidateNames` scans `FirRegularClass.declarations` directly, which a
    `FirDeclarationGenerationExtension`-injected interface leaves empty (members are scope-served). Documented as a
    pinned-compiler limitation (same basis as `object`/companion).
  - **`enum class` (④) NOT restored:** blocked at the injection layer — a `FirDeclarationGenerationExtension` (2.2.0)
    cannot synthesize real `FirEnumEntry` declarations (FIR's exhaustiveness checker requires them; the plugin API has
    no entry hook), so no `[KotlinEnum]` carrier is emitted. A basic enum still round-trips as an `object` of `val`s
    (value access works). Flagged in `docs/dotkt-semantics.md` §10.2/§10.4.
  - Covered by a new `roundtrip-markers` section in `scripts/verify-roundtrip.sh` (a `fun interface` + `sealed`
    hierarchy + `enum` library, re-consumed: anonymous-object handler runs, exhaustive `when` compiles, a rogue
    sealed subclass is rejected). `docs/dotkt-semantics.md` §10 updated.
- **kotc→bir2cir `clrName` migration, Step 3: the bir2cir compensation for removing `annClr` (member-strip + flags +
  setter markers), verified byte-identical.** With the ilemit overload-attribute fix in place (ref.dll now carries every
  overload's `@ClrIntrinsic`), bir2cir gained the machinery to reproduce what kotc's `annClr`/`clrIfaceMemberName` does,
  so kotc can stop reading `@ClrIntrinsic`: (1) a `MemberStrip` pass (before `AliasHelperHoist`) that drops
  `@ClrIntrinsic`-bound stub declarations by FULL SIGNATURE (`IsBoundStub` + a `ParamKey` canonicalizer over the new
  `MemberBinding.ParamTypes`, so `StringBuilder.append(Char)` is dropped while `append(CharSequence?)` is kept; an
  alias-class member that merely OVERRIDES a `@ClrIntrinsic` member is dropped too; INTERFACE members are never stripped
  — they declare the CLR slot); (2) `DeclarationRename` restores the `override:true`/`vis:public` flags exactly when a
  CLASS member's rename fires (kotc's `clrIfaceName`-driven `isOvr`/vis — never inside an interface); (3) kotc's
  `overridesJson` now derives an accessor's marker from the PROPERTY's override closure (so a `var size` setter
  overriding a `val size` still renames `set_size`→`set_Count`), and `ResolveSlot` looks the intrinsic up on the
  `get_<name>` accessor for both getter and setter. All verified **byte-identical with annClr active** (idempotent
  no-ops). This drops the annClr-OFF diff from 71 → 6; the actual `annClr` deletion awaits those last 6 (see
  prioritized-tasks): top-level `sort`/`append` signature-strip (array-class param canonicalization), a call-side
  `clrPropGet` vs `clrInstance get_X` routing edge, and 3 helper/closure body diffs.

### Fixed
- **App-consume of collection-BUILDING ops (`map`/`filter`/`toList`/`toMutableList`/`reversed`) now works — two general
  codegen fixes (NOT a stdlib special-case; the mutable-collection actuals were already `@ClrTypeAlias`/`@ClrIntrinsic`
  bound and direct `ArrayList().add(...)` worked).** (1) **Generic-parameter-receiver constrained dispatch:** the real
  stdlib `mapTo`/`filterTo`/`toCollection` do `destination.add(x)` where `destination: C` and `C : MutableCollection<R>`.
  bir2cir lowered this to a plain `callvirt` on the `ICollection<object>` owner (the alias padded the missing type args
  with `object`), which mis-dispatches at runtime — a `List<R>` implements `ICollection<R>`, not `<object>`, so the JIT
  found no slot and threw `EntryPointNotFoundException`. bir2cir's `MemberCallSubstitution` now threads a lexical
  type-parameter/param environment (`SubstCtx`) and, when a CLR-aliased-interface member is invoked on a
  generic-parameter receiver, emits a `constrainedCall` node (`recvType=gp:C`, `iface=ICollection<R>` from the
  receiver's constraint); ilemit's `constrainedCall` handler gained an N-arg form that emits `constrained. !!C ; callvirt
  ICollection<R>::Add`. (2) **Ctor overload argType precision:** `ArrayList(collection)` (used by `toMutableList`/`toList`/
  `reversed`) lowered to `new List<T>(...)` with argType `object` (bir2cir dropped kotc's declared ctor param type and
  re-inferred `object` from the bare local), so ilemit couldn't disambiguate `List(int capacity)` from
  `List(IEnumerable<T>)` and picked the wrong one → `InvalidProgramException`. bir2cir's `TransformNew` now instantiates
  the ctor's declared param types by substituting the class type params with the `new`'s type args (`ArrayList[Int]` ⇒
  `E:=Int`, via a new ref.dll type-param-name index) — yielding a precise `IReadOnlyCollection<int>` overload key; ilemit
  falls back to assignability (`PickCtorByAssignable`) when the exact ctor misses (`IReadOnlyCollection<int>` IS
  `IEnumerable<int>`). Residuals (separate pre-existing bugs, unchanged): `sorted()` on a `Collection` (value-type
  `toTypedArray`/`Array.Sort`), `mapNotNull` (nullable-generic `?.let`), and `for (x in this: Iterable<T>)` over a
  generic receiver.
- **Round-trip gap ①: generic CONSTRAINTS and declaration-site VARIANCE now survive re-consuming a DotKt assembly as
  Kotlin.** `ilemit` already wrote the CLR constraints (`SetBaseTypeConstraint`/`SetInterfaceConstraints`) and interface
  variance (`GenericParameterAttributes.Covariant/Contravariant`), but `facadegen` emitted only the bare type-param NAME
  and the FIR injector hard-coded `Variance.INVARIANT` with no bounds — so a consumer saw an unconstrained, invariant
  `T`. `facadegen` now reads `GetGenericParameterConstraints()` / `GenericParameterAttributes` and emits them as
  backward-compatible metadata lines (`tvariance`/`tbound` for a class/interface type param, `mbound` for a method type
  param; a Kotlin `Comparable<T>` bound is reversed from the CLR `System.IComparable<T>` it lowers to), and
  `ClrTypeInjection` restores them on the synthesized FIR (`out`/`in` variance + upper bounds via lazy lookup-tag cones,
  self-referential-safe for the curiously-recurring BCL numeric tower reachable from a `System.*` closure, and fail-soft
  so a pathological bound degrades to an unconstrained `T` rather than crashing). A round-trip of `interface P<out T>` /
  `interface C<in T>` / `class SortedPair<T : Comparable<T>>` / `fun <T : Comparable<T>> maxOf2` now restores the
  variance (covariant/contravariant assignability compiles) and bounds cross-module. (docs/dotkt-semantics.md §10.)
- **`il:regex` restored after the DotKt.Runtime retirement — `matches`/`find` now run on the real stdlib bodies (no
  shim).** Removed two stale kotc CLR-lowerings the retirement missed: the `kotlin.text.MatchResult`→`System...Match`
  type alias (which made `ClrMatchResult : MatchResult` implement a CLASS as an interface → `TypeLoadException`) and the
  `MatchResult.value`→`Match.Value` call lowering. Stdlib-side, `matchEntire`/`matchAt`/`matchesAt` materialize the
  `CharSequence` input to a `String` before reading `.length` (System.String does not implement the synthetic
  `<>dotkt_CharSequence`), and `ClrMatchResult.groups` became a lazy getter (no eager `AbstractCollection` load).
  kotc now OMITS a cross-module default arg whose value deserialized as an `IrErrorExpression` so ilemit fills it from
  `[DefaultParameterValue]` metadata (fixes `Regex.find(input)` with `startIndex` omitted); `ilemit.EmitCallArgs` fills
  omitted trailing defaults on the callStatic/callInstance path.
- **`kotlin.Result` (and other pure-Kotlin, non-`@ClrTypeAlias` stdlib types) resolve as REFERENCED types cross-module.**
  ilemit `MapType`/`ParseOwner`/`ResolveMethod` resolve a `@Name`/`Name[args]` token absent from this assembly's
  `_types` as a referenced .NET type/generic (arity-suffixed), and resolve instance members on the reflection-constructed
  instantiation; bir2cir attributes a multi-overload top-level fun (e.g. `runCatching`) to its shared file-class owner
  when the receiver key doesn't disambiguate. `il:result` no longer crashes at emit (KeyNotFound gone); it now fully
  resolves. **Residual (scoped follow-up):** `getOrNull(): T?` for a value-type `T` returns bare `Int32` where the call
  site needs `Nullable<Int32>` (the pre-existing primitive-dual-representation gap) — `il:result` does not yet pass.
- **ilemit: `@ClrIntrinsic` (and every user annotation) dropped from all-but-last overload in the ref build.** The
  user-annotation → `.NET` custom-attribute application (`Program.cs`) resolved the target `MethodBuilder` by NAME
  only (`ti.Methods[name]`), which is last-declared-wins for overloads — so for an overloaded intrinsic function
  (`sin(Double)`+`sin(Float)`, `sort(IntArray/…)`, `append(…)`, `println(…)`) every def's attrs landed on the single
  last-declared builder while the earlier overloads got NONE. In `DotKt.Private.Stdlib.dll` this left `sin(Double)`
  with `intr=[]` and doubled `sin(Float)` to `["System.Math.Sin","System.MathF.Sin"]`. Since the ref.dll is bir2cir's
  binding source, the intrinsic was invisible for those overloads (blocked the `clrName`/annClr removal and mis-bound
  cross-module calls). Fix: resolve by SIGNATURE first (`MethodsBySig[SigKey(name, m)]`), name-only fallback —
  mirroring the Kotlin-metadata path. Verified 1:1: 262 ref.dll methods carry `@ClrIntrinsic` = 262 CIR method-defs
  (was fewer, with doubled values). rt build unaffected (metadata stripped there).

### Changed
- **kotc→bir2cir `clrName` migration, Step 3 part 2: CLR-property-entry slot rename.** kotc tags each emitted
  `properties:[{name,get,set}]` record with the getter's `overrides` marker, and bir2cir's `DeclarationRename` renames
  the record's `get`/`set` accessor references (`get_size`→`get_Count`, `set_size`→`set_Count`) via a new
  `ResolveBareIntrinsic` (the @ClrIntrinsic lives on the `get_<name>` accessor in the ref.dll; the bare value is the BCL
  property name, applied to both accessors). The record's `name` stays the Kotlin property name (matching annClr).
  Verified rt CIR byte-identical with annClr active (idempotent); an annClr-off probe confirms it FIRES (the property
  records emit `get_Count`). **Newly surfaced remainder for the annClr removal** (beyond the member-strip + SAM): the
  `override`/`virtual`/`vis` FLAGS are also computed via `clrIfaceMemberName` (an interface-override method's
  `override:true` depends on it) — these must move to a pure-Kotlin signal (`overridesIface`) or bir2cir; and the
  member-strip needs full-SIGNATURE (param-type) matching, not just name+arity (StringBuilder.append has same-arity
  @ClrIntrinsic + rule-3 overloads), and must run BEFORE AliasHelperHoist (else the rule-3 helper over-hoists).
- **kotc→bir2cir `clrName` migration, Step 3 part 1: CALL-SITE slot rename.** kotc now emits the same pure-Kotlin
  `overrides` marker on the `callInstance` nodes whose member name `clrIfaceMemberName` resolves via `@ClrIntrinsic`
  (the property-accessor and method-call paths), and bir2cir's `DeclarationRename` is now a recursive walk that renames
  a CALL's `method` (not just a declaration's `name`) from that marker + the ref.dll — so an implementor-side call
  `AbstractList.get_size` tracks its renamed declaration `get_Count`. The pass moved to run BEFORE
  `MemberCallSubstitution` (so a now-`get_Count` call on a CLR-bound owner still lowers to `clrPropGet`). Verified rt CIR
  byte-identical with annClr active (idempotent); an annClr-off probe confirms it FIRES — the call side now compensates
  (probe diff 71→46 files, the `AbstractList.get_size not found` failure gone). **Remaining for annClr removal**: the
  `@ClrIntrinsic`-bodyless member-strip (bir2cir, the member mirror of the @ClrTypeAlias type-strip), the
  `properties:[{get,set}]` entry rename, the fun-interface SAM rewrite — then kotc plain-naming + delete `annClr`.
- **kotc→bir2cir `clrName` migration, Step 2a FIX: the declaration-rename was inert; now functional.** Step 2a's
  `DeclarationRename` (552261e) was a verified no-op — it looked the property `@ClrIntrinsic` up by the property NAME
  (`size`), but in the ref.dll that attribute lives on the ACCESSOR METHOD (`get_size@ClrIntrinsic("Count")`, the
  intrinsic value being the BCL property name), so `ResolveSlot` always returned null and kotc's annClr name was simply
  kept (still byte-identical, but the rename did nothing). Fixed `ResolveSlot` to look up the accessor method
  (`get_`/`set_`+name) by exact arity and prefix the result; removed the dead `GetProperties()` scan + unused
  `TryMemberIntrinsicByName`. Verified: rt CIR still byte-identical with annClr active (idempotent), and an annClr-off
  probe now correctly renames `AbstractCollection.get_size`→`get_Count`. This makes the Step-3 prerequisite real (the
  rename actually compensates when annClr is removed).
- **kotc→bir2cir `clrName` migration, Step 2a (IDEMPOTENT declaration-rename, byte-identical): bir2cir now owns the BCL
  slot-name derivation.** Two bir2cir additions consume the Step-1 `overrides` markers to reproduce what kotc's
  `clrName`/`annClr` does for declaration naming: (1) `ScanSubstitutionMetadata` now also reads `GetProperties()`, so a
  property's `@ClrIntrinsic` (`Collection.size`→`"Count"`, `CharSequence.length`→`"Length"`) — which lives on the
  property, invisible to the `GetMethods()` scan — enters `MemberBindings`; (2) a new `DeclarationRename` pass (gated to
  NON-ref builds, runs before the marker is stripped) renames an emitted method/accessor to its BCL slot from the FIRST
  overridden member carrying an `@ClrIntrinsic` in the ref.dll (a `size` getter override → `get_Count`, `resumeWith` →
  `ResumeWith`). Method overloads match by EXACT arity (a new `TryMemberIntrinsicExact` — so `add(element)`→`Add` does
  NOT fall through to `add(index,element)`→`Insert`); property accessors match by name (`TryMemberIntrinsicByName`).
  With `annClr` STILL running in kotc the pass is **idempotent** → verified **rt CIR byte-identical** (0 diff) and ref
  💮 (`kotlin.Int : Comparable<kotlin.Int>`) intact. This moves the slot-name LOGIC to bir2cir without yet removing the
  kotc annotation read. **Remaining for Step 3** (the actual `annClr` removal, deferred — proven not single-pass-safe):
  add `fn`-self to the marker (a method with its OWN `@ClrIntrinsic`; harmless/idempotent today, a byte-identity
  prerequisite once annClr is gone), rename the `properties:[{get,set}]` entries, the `@ClrIntrinsic`-bound member-strip,
  the fun-interface SAM rewrite, then switch kotc's decl-name sites to plain Kotlin names and delete `annClr`.
- **kotc→bir2cir `clrName` migration, Step 1 (NEUTRAL groundwork): pure-Kotlin override markers.** Toward "kotc reads
  NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias`", kotc now emits an `overrides:[{owner,member,kind,arity}]` marker on each
  instance method / interface method / property accessor — the transitive closure of the interface/base members it
  overrides, in **pure Kotlin terms** (FQN + Kotlin member name + getter/setter/method + arity; NO `@ClrIntrinsic` read,
  NO BCL name). bir2cir **strips** the marker in `BirTypeLowering` so it never reaches the CIR/ilemit. **Behavior-neutral
  and verified CIR byte-identical** (rt stdlib: 0 differing/new/removed files vs the prior build; 95 BIR files carry the
  marker, 0 leak to CIR). The marker is the handshake a future Step 2 consumes — bir2cir resolves the BCL slot name from
  the ref.dll `@ClrIntrinsic` (`TryMemberIntrinsic`) instead of kotc's `clrName`/`annClr`: validated that e.g.
  `AbstractCollection.get_Count` ← `Collection.size`(getter) → ref.dll `@ClrIntrinsic("Count")`, and `String.get_Length`
  ← `CharSequence.length`(getter) → `@ClrIntrinsic("Length")` reproduce exactly. **Remaining** (Step 2/3, deferred — a
  large coordinated change proven not single-pass-safe by a 72-file/ilemit-crash probe): a bir2cir declaration-rename
  pass (markers + ref.dll) + the `@ClrIntrinsic`-bound-member DROP (member-strip, the `clrName(it)==null` emission
  filters) + the fun-interface SAM rewrite (Comparator→IComparer), then switch kotc decl-name sites to plain names and
  remove `annClr`. Also pending markers on the `properties` get/set entries + SAM methods + `clrAccessorMethod`.
- **`@ClrTypeAlias` type-STRIP moved kotc → bir2cir (layer-purity).** kotc no longer reads `@ClrTypeAlias` to strip a
  CLR-bound type from emission: `substitutedAway` / `hasClrTypeAlias` / `hasHoistableBody` and the `aliasPlainTypes` +
  "alias-only file" branches are **deleted**. kotc now emits EVERY type as ordinary Kotlin (a primitive `kotlin.Int`,
  the `kotlin.collections.List` interface, `kotlin.text.StringBuilder`, …); bir2cir's `AliasHelperHoist` DROPS each
  alias type def — hoisting a class's rule-3 members into the `<>dotkt_ClrH_*` helper, and dropping an interface/object
  alias with NO helper (a new `kind == "class"` guard, so a ref.dll default-interface-method can't false-positive into a
  bogus interface helper). The rt-stdlib emit is unchanged in IL (still 14 helpers; the only CIR deltas are internal
  label-id renumbering from the new type-emission order, a now-defined `<>dotkt_CharSequence` that `kotlin.String`'s
  helper already referenced, and the removal of 4 pointless **empty** file-classes — Primitives/Comparable/Any/MathH —
  which bir2cir now skips when an alias-only file lowers to nothing). The reference build is untouched (the strip was
  always a no-op there: `clrName` is null in the ref, so the old `substitutedAway` never fired). Drives kotc toward
  "reads NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias`": the `@ClrTypeAlias` read is now gone except the fun-interface-SAM
  alias lookup; what remains is the `clrName`/`netType` member-call + type maps.
- **Rule-3 static-helper SYNTHESIS moved kotc → bir2cir (layer-purity, MIXED-file hoist).** kotc no longer synthesizes
  the `<>dotkt_ClrH_<owner>` helper for a CLR-bound (`@ClrTypeAlias`) class: `clrHelperClassJson`/`clrHelperMethod`/
  `clrHelperMembers` (which read `@ClrIntrinsic`) are **deleted**. kotc now emits EVERY bound alias class with hoistable
  bodies as a PLAIN BIR type — the alias-only files (String/Char/Boolean) AND the previously kotc-synthesized MIXED
  files (StringBuilder/collections/Regex/unsigned) alike — gated by the pure-Kotlin `hasHoistableBody` (no annotation
  read); bir2cir's existing `AliasHelperHoist` (the single home of rule-3 synthesis) hoists their members and drops the
  type. bir2cir gained two fixes for the now-bir2cir-owned MIXED set: (a) a GENERIC alias owner types `__self` as the
  constructed `kotlin.collections.ArrayList[gp:E]` (lowers to `clrg:…List[gp:E]`, was a non-generic `clr:…List` that
  ilemit could not resolve); (b) an `@JvmInline` value-class alias (UInt/UByte/…) does NOT hoist its `Equals`/
  `GetHashCode`/`ToString` overrides (they read the erased `.data` field → an unresolvable `<self>.data` on the `ubyte`
  shorthand; they defer to the BCL primitive instead). The emitted rt-stdlib helper set is byte-identical to before (14
  `<>dotkt_ClrH_*`), with kotc now producing zero of them. Remaining for the "kotc reads NEITHER annotation" goal: the
  `substitutedAway` strip-routing (still reads `@ClrTypeAlias`/`@ClrIntrinsic`) and the `clrName`/`netType` member-call +
  type maps.

### Fixed
- **App-consume of the rt stdlib: `for (x in list)` now iterates a referenced collection.** kotc desugars the loop to
  a `<iterator>` var initialized by the rt bridge `ClrIteratorBridgeKt.iteratorOverEnumerable` (which returns the real
  generic `kotlin.collections.Iterator<E>`) and routes `hasNext`/`next` to a synthetic monomorphized
  `<>dotkt_KIterator_*` interface — a legacy "IL can't define a generic interface" workaround that KeyNotFounds in an
  app build (the synthetic + the `@kotlin.collections.Iterator` var type are referenced, not emitted). A new bir2cir
  pass (`IteratorConsumerNormalization`, app build only) retypes the var to `clrg:kotlin.collections.Iterator[E]` and
  converts the synthetic `hasNext`/`next` `callInstance` to a `clrInstance` on the real referenced interface (the
  `EmitClrCall` path the substituted IReadOnlyList already uses), in a single document-order walk so sibling/nested
  for-loops reusing the `<iterator>` name bind to their own element type. The rt stdlib bridge
  `iteratorOverEnumerable` (+ its two `@ClrTypeAlias` interface types) was made `public` (was `internal` →
  `MethodAccessException` from an app).
- **App-consume of the rt stdlib: referenced top-level stdlib funs now resolve.** A top-level stdlib function called
  from an app (`xs.getOrElse(i){…}`, `xs.first()`, …) is emitted by kotc as `callStatic owner=null`; ilemit's
  `FindStatic` only searches THIS assembly's file-classes, so it threw `static method not found`. bir2cir now reads
  the ref.dll for non-intrinsic file-class statics and, in an **app** build (`DOTKT_STDLIB_COMPILE` unset),
  attributes such an owner-less call to the file-class it actually lives in (`kotlin.collections._CollectionsKt`),
  disambiguated by the call's receiver type when overloaded across file-classes (CollectionsKt vs ArraysKt vs MapsKt).
  ilemit's owner-present `FindMethod` then resolves it by reflection against the runtime stdlib (the same path the
  iterator bridge already uses). Gated off for the stdlib self-build (the fun is local there) and when the name is
  locally defined; the rt/ref stdlib CIR is byte-identical after the change. New sample `cases/ktproj-coll` builds and
  runs a practical collections app (List local + `first`/`getOrElse`/`contains`/`indexOf`/`count`/`isEmpty`/`take`) via
  MSBuild `dotnet build`/`dotnet run`; wired into `verify-ktproj.sh`.
- **ilemit picks the arity-matching overload of a referenced file-class static.** The reflected-method lookup used an
  unconstrained `GetMethod(name)` that threw `AmbiguousMatchException` and fell back to an arbitrary pick, emitting a
  stack-mismatched call (`InvalidProgramException` at run) for e.g. `_CollectionsKt.first(List<T>)` vs
  `first(Iterable<T>, predicate)`. It now prefers the overload whose parameter count matches the call's `sig`.

### Changed
- **verify-il routes the migrated `m2`/`mi1`/`c1net` samples through the facadegen import path.** `m2`/`mi1` consume
  BCL types via `import System.X` (System.Math, StringBuilder) but ran under a bare `il_check` that injects nothing —
  moved to a new `il_check_imports` (scan-imports + facadegen `--meta`, no `runtime.cs`). `c1net` consumes its own
  `runtime.cs` types via `import Probe.X` — moved off `il_check_ref` (no import scan, the dead `@Clr`-facade path) onto
  `il_check_inject` (build runtime + scan imports + `--ref`). `il_check_ref` stays for the coroutine samples that ship
  a `runtime.cs` but import nothing.
- **bir2cir is now the single-path owner of Kotlin→CLR type substitution.** The `CompatBir` verbatim-copy mode and
  the `--compat-bir`/`--native-cir` output-selection flags are gone — there is one path: a real type-lowering pass
  rewrites the Kotlin type vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a
  BIR-shaped CIR (same node shape; only type strings change, so ilemit needs no shape change). The lowering is
  build-gated by env (not a flag): the pure-Kotlin **reference** stdlib surface (`DOTKT_STDLIB_COMPILE` set,
  `DOTKT_STDLIB_SUBSTITUTE` unset) keeps `kotlin.*` tokens verbatim; **every other** build (the runtime stdlib and
  all app builds) lowers a bare `kotlin.*` primitive to its CLR token (`kotlin.Int` → `int`, …). kotc still emits the
  CLR shorthand today, so the rewrite is a verified no-op against current output (it activates once kotc is switched
  to emit `kotlin.*` symbols). `scripts/dotkt.sh` drops its `--native-cir` flag accordingly.

### Removed
- **Namespace projection** (`[DotKtNamespaceProjection]` / `ilemit --ns-projection` / the `nsproj` meta line). The
  assembly-level Kotlin-package ↔ .NET-namespace remap (e.g. consuming a `DotKt.Coroutines` library as
  `import kotlinx.coroutines.*`) had no real use — a DotKt assembly's types are seen 1:1 at their actual .NET
  namespace as the Kotlin package, and a library that wants a `kotlinx.*` package simply declares `package kotlinx.*`.
  Removed across kotc/ilemit/bir2cir/facadegen, the `DotKtNamespaceProjectionAttribute` runtime type, the MSBuild
  `<DotKtNamespaceProjection>` item, and the `roundtrip-nsproj` test.

### Added
- **`DotKt.Stdlib` — a tracked first-party library of real-Kotlin stdlib ops**, compiled by DotKt's own toolchain
  (`runtime/DotKt.Stdlib/`, built by `scripts/build-dotkt-stdlib.sh`). It holds standard-library operations migrated
  off the compiler's hand-written `COLLECTION_OPS` LINQ lowerings onto their real Kotlin source. Auto-referenced by the
  verify harnesses (and intended for every `.ktproj`); a call to a migrated op routes to the real body via the
  round-trip registry. First migrated op: **`List.getOrElse`** (random-access, runs directly on the BCL `List<T>`).
  Validated against the Kotlin/JVM oracle (verify-differential) — the real-Kotlin reimplementation matches JVM semantics.
- **`facadegen --scan-asm <dll>`** — inject ALL `[KotlinFileClass]` facades from a referenced DotKt library wholesale
  (auto-imported stdlib functions never appear in the `--import-list`), so DotKt.Stdlib's ops are visible to the FIR
  injector without naming each one.
- **`<DotKtKotcOptions>` MSBuild property** — pass raw kotc flags through to the compile step (appended verbatim, e.g.
  `-Xallow-kotlin-package`, `-opt-in=...`, `-Xcontext-parameters`). Needed to compile the Kotlin standard library itself
  (see `docs/design-stdlib-compilation.md`); useful for any advanced compiler option.

- **Kotlin `Iterable<T>` (as a parameter/receiver type) lowers to `IEnumerable<T>`.** The broadest read-only iteration
  interface — `List<T>`, `HashSet<T>`, and any CLR `IEnumerable<T>` all bind, so a real-Kotlin `Iterable<T>.map(...)` in
  DotKt.Stdlib accepts them all and `for (x in this)` enumerates via `GetEnumerator`/`MoveNext`/`Current`. As a user
  class SUPERTYPE, `Iterable`/`Iterator` stay the synthetic monomorphized interface (implementing `IEnumerable<T>` would
  need a synthesized `GetEnumerator` — the producing-side bridge, separate work), so user iterables are unaffected.
- **Collection ops migrated off the LINQ lowering onto real Kotlin.** A `List`/`Collection`/`Set`/`Iterable` receiver
  routes these ops to the real Kotlin body shipped in DotKt.Stdlib (iterate + build an `ArrayList`), matching Kotlin/JVM
  (verify-differential): **`map`, `filter`, `forEach`, `count`, `fold`, `any`, `none`, `all`, `toList`, `toMutableList`**
  (plus the random-access `getOrElse`). `Array`/`Sequence` receivers keep the LINQ lowering (DotKt.Stdlib ships only the
  `Iterable` overload). The skip is gated on the op being registered from a referenced DotKt.Stdlib, so it composes with
  the lowering-retirement seam. New verify-il case `mapfilter`.
- **Mutable collections + the real-stdlib `map`/`filter` shape now compile.** `ArrayList<R>()` (the JVM
  `java.util.ArrayList` typealias) lowers to `new System.Collections.Generic.List<R>()`, and the `MutableList`/
  `MutableCollection` mutation members (`add`/`remove`/`clear`/`removeAt`) bind to the BCL `List<T>` methods — so
  `mutableListOf(...).add(x)` etc. work (they previously hit an unsupported-owner gap), and a real-Kotlin
  `Iterable<T>.mapTo(ArrayList()) { … }` iterating + `.add(...)` runs on the BCL list. ilemit's `clrNew` resolves the
  ctor of a `List<R>` whose `R` is the enclosing generic function's type parameter (a `TypeBuilderInstantiation`) via
  `TypeBuilder.GetConstructor`. This unblocks migrating the iteration collection ops (`map`/`filter`/`fold`/…) off the
  LINQ lowering onto real Kotlin source. New verify-il case `mutcoll`.

### Fixed
- **`(P..) -> Unit` lambda shape matches `Action<P..>` for migrated/round-trip generic calls.** `clrMethodShape`
  counted the trailing `Unit` (`(T)->Unit` → `func:2`), but such a type lowers to `Action<T>` (one generic arg, no
  return slot) which ilemit shapes `func:1` — the mismatch made the generic-method shape lookup find 0 candidates
  (`Sequence contains no elements`). Now the trailing `Unit` is dropped from the count. (Surfaced migrating `forEach`.)
- **Injected stdlib top-level functions no longer re-emitted as broken stubs.** A consuming module's FIR holds the
  plugin-injected stdlib ops (restored from DotKt.Stdlib in the synthetic `__GENERATED DECLARATIONS__` file); the BIR
  emitter was emitting them as local top-level methods with no real body (invalid IL — `ReturnMissing` under ilverify).
  Now filtered to origin `DEFINED` (user code only), mirroring the existing filter for injected top-level properties.
- **Generic collection member access (`List<T>`/`MutableList<T>`/`Map<K,V>` indexers + size) inside a generic function.**
  `fun <T> List<T>.first(): T = this[0]` and friends now emit: when the element type is the enclosing generic function's
  own type parameter, `List<T>`/`Dictionary<K,V>` are `TypeBuilderInstantiation`s whose plain reflection `.GetMethod`
  throws (`TypeBuilder generic instantiation does not support resolving members`). ilemit now routes the `listGet`/
  `listSet`/`mapGet`/`mapSet`/`mapSize` member lookups through `TypeBuilder.GetMethod` (the existing `GenericMethod`
  helper). This unblocks compiling real Kotlin stdlib collection extensions to run on the BCL collections DotKt maps
  `kotlin.collections.*` to — the first step of moving random-access collection ops off the hand-written LINQ lowering
  onto real Kotlin source (see `docs/design-stdlib-compilation.md`).

### Changed
- **`String.format` binds directly to .NET `String.Format` — use .NET composite format strings, not Java printf.**
  `"{0:F2}".format(x)` / `String.format("{0:D5}-{1:x}", a, b)` now lower straight to `System.String.Format` with the
  format string passed through verbatim. DotKt no longer reproduces `java.util.Formatter` (the printf→composite
  translation and the `DotKt.Fmt` runtime helper are removed) — `String.format` is JVM-only in Kotlin (Kotlin/Native and
  Kotlin/JS don't have it), so binding it to the CLR's own formatter is the natural CLR-native choice and slims the
  runtime by one type. **Breaking:** a Java printf string like `"%.2f".format(x)` is no longer translated — it is passed
  to `String.Format`, which treats `%.2f` as literal text. Use `"{0:F2}"`, or string interpolation for the common case.

## 0.9.3 — 2026-06-24

Round-trip interop: a DotKt-compiled assembly can now be consumed **as Kotlin** by another
`.ktproj` (the basis for shipping compiled kotlinx-* libraries for the CLR), plus bidirectional
compile-time `ProjectReference` between C# and Kotlin projects.

### Added
- **Reference-type nullability via .NET NRT + platform types.** A reference-type `String?` now rides .NET's own
  nullable-reference metadata (`[Nullable]`/`[NullableContext]`) instead of a bespoke attribute: ilemit stamps
  `[NullableContext(1)]` per type and `[Nullable(2)]` on each nullable reference return/parameter, so a **C# consumer
  also sees** DotKt's `String?` as nullable. facadegen reads NRT uniformly for every assembly, which closes a soundness
  hole — a reference type from any non-DotKt assembly was previously injected as strictly non-null. A reference type from
  an assembly built without `<Nullable>enable</Nullable>` (oblivious) now injects as a Kotlin **platform type** `T!`
  (`ConeFlexibleType(T, T?)`, à la Kotlin/JVM's treatment of un-annotated Java), instead of lying "non-null". The old
  `[KotlinNullable]` attribute is retired. See `docs/dotkt-semantics.md` §9.
- **Round-trip metadata attributes are compiler-embedded per assembly.** The `[Kotlin*]` attributes moved to namespace
  `DotKt.Runtime.CompilerServices` and are now defined as internal types inside each emitted assembly (the csc model for
  its own `NullableAttribute`/`IsReadOnlyAttribute`) rather than referenced from `DotKt.Runtime`. They are metadata-only,
  so this makes each assembly self-contained and removes the "ilemit needs `--ref DotKt.Runtime` to stamp" coupling.
  (`[DotKtNamespaceProjection]` stays a referenced type — it is assembly-level, which PersistedAssemblyBuilder can't
  embed.) `DotKt.Runtime` now carries only executed code plus that one attribute.
- **Consume a DotKt assembly AS KOTLIN — Kotlin-modifier round-trip.** Kotlin-language facts with no native .NET
  representation now survive compilation and are restored on a consuming module's FIR, so a `.ktproj` can use
  another DotKt-compiled assembly with idiomatic Kotlin syntax (the basis for shipping compiled kotlinx-* libraries
  for the CLR). Embedded `DotKt.Runtime.CompilerServices` attributes (`[KotlinFunction(Infix|Operator|Suspend)]`, `[KotlinFileClass]`) are
  stamped onto the IL by ilemit, read back by `facadegen --meta`, and restored by the FIR injector:
  - `infix fun` / `operator fun` — restored as `status { isInfix/isOperator }` (call notation + operator resolution).
  - `suspend fun` — emitted as `Task<T>`; restored as `suspend fun(): T` (the Task is unwrapped and re-awaited by the
    coroutine machinery), for both members and top-level functions.
  - top-level functions — a `<File>Kt` facade carries `[KotlinFileClass]`; its statics restore as top-level package
    functions, called via a new `ClrTopLevelRegistry` as a static call on the file class. **Generic** top-level
    functions are restored with their type parameters and called via `clrGenericStatic`, so a cross-module
    `inline fun <reified T>` is consumed as a generic method (`f<Int>()`) — CLR generics are reified, so no inlining
    or carried body is needed. (The only cross-module inline case that can't degrade — a lambda with a non-local
    `return` — fails with a clean compile error; see docs/design-kotlin-metadata-attributes.md.)
  - `final`/`open`/`abstract`, visibility, and **`reified`** need no attribute — they ride plain .NET metadata (CLR
    generics are reified, so `inline fun <reified T>` is just a generic method).
  - **`inline` (with a lambda) — cross-module non-local `return`.** DotKt inlines at EMIT time (BirEmitter, no JVM
    `FunctionInlining` lowering), so a cross-module inline call to a body-less injected stub can't be inlined — which
    means a non-local `return` through the lambda (the one inline case that can't degrade to a regular call) was a
    compile error. Now: `ilemit` stamps `[KotlinInline(birJson)]` with the function's own BIR body; the injector
    marks it `inline`; and the consumer's `ilemit` reads that body from the referenced assembly and splices it at the
    call site (param + lambda-body substitution), so the lambda's `return` becomes the caller's `return`. Lighter than
    JVM's `@Metadata` (BIR, emit-time, no IR deserializer). Verified by `scripts/verify-roundtrip.sh`.

- **Bidirectional `ProjectReference` (R-1, reverse interop)** — a C# project can now
  `<ProjectReference>`/`<Reference>` a Kotlin `.ktproj` at **compile time** (not just
  reflection-load), so a Visual Studio solution can split code across C# and Kotlin
  projects that reference each other. New build-time tool **`tools/retarget`**
  (Mono.Cecil) repoints the emitted assembly's BCL `TypeRef`s off the single
  `System.Private.CoreLib` onto the real contract assemblies (`Object`/`Task` →
  `System.Runtime`, `List`/`Dictionary` → `System.Collections`, …) — the type→contract
  map is the forward path's machinery in reverse (the ref pack via `MetadataLoadContext`).
  This is pure post-emit metadata surgery, so it sidesteps the Reflection.Emit/MLC
  generic-instantiation limits that sank the two earlier attempts; `List`/`Dictionary`
  and `suspend fun` → `Task<T>` all consume cleanly from C#. New sample
  **`samples/ktproj-bidir`** (cslib.csproj ← klib.ktproj ← app.csproj: forward + reverse
  in one graph) is green in `verify-ktproj.sh`. Default ON; opt out with
  `<KotlinClrRetarget>false</KotlinClrRetarget>` / `<DotKtRetarget>false</DotKtRetarget>`.

### Fixed
- **A closure/local function capturing an enclosing generic type parameter crashed ilemit.** A lambda or local
  function inside a generic function that captured a value whose type involves the enclosing `T` (a `T` value, a
  `(T)->Unit`, a `List<T>`) threw `NotSupportedException: unresolved generic type parameter T` — the synthesized closure
  class / lifted method wasn't generic over `T` (reified CLR generics need it). The closure class is now generic over the
  captured type parameters and instantiated with the enclosing ones at the capture site; a captured local function is
  lifted to a generic static method. (An object expression or local *class* that captures an enclosing type parameter is
  not yet supported and now fails with a clear compile error instead of crashing.)
- **Cross-file / namespaced interface polymorphism crashed ilemit.** A class in a Kotlin `package` implementing an
  interface from another file threw `KeyNotFoundException` during the interface-link pass — `FindMethod` was keyed by the
  TypeBuilder's simple name while `_types` is keyed by the BIR full name. Now keyed consistently.
- **A generic function applying `(T) -> Unit` to a `List<T>` crashed ilemit.** `for (x in xs) f(x)` inside
  `fun <T> each(xs: List<T>, f: (T) -> Unit)` threw `NotSupportedException` (TypeBuilder generic instantiation doesn't
  resolve members) — the `forEach` lowering called `.GetMethod` on `IEnumerable<T>` directly instead of via
  `TypeBuilder.GetMethod`.
- **Assigning a Boolean to a .NET `bool?` property failed the frontend.** facadegen mapped a nullable value type
  `Nullable<X>` to the literal generic `Nullable<X>` (a distinct type) instead of Kotlin's `X?`, so e.g.
  `checkBox.IsChecked = true` reported an assignment type mismatch. `System.Nullable<X>` now maps to `X?`.
- **Kotlin → Kotlin `ProjectReference` round-trip — a library's top-level functions vanished.** A `.ktproj` consuming
  another `.ktproj` as Kotlin got `unresolved reference` on the library's top-level functions (`import mylib.boxed`),
  while classes resolved fine. The MSBuild `ilemit` step built its `--ref` list from `@(ReferenceCopyLocalPaths)`, which
  doesn't contain `DotKt.Runtime` (a compile reference, not copy-local) — so ilemit couldn't resolve the metadata
  attribute types and **silently skipped stamping** `[KotlinFileClass]`/`[KotlinFunction]`. The file facade then looked
  like a plain class to the consumer, which finds top-level functions only on `[KotlinFileClass]`-marked classes. ilemit
  is now passed `DotKt.Runtime` from `@(ReferencePath)` (SDK + in-repo targets). New regression test
  `samples/ktproj-roundtrip` (this Kotlin→Kotlin `ProjectReference` path had no coverage before).
- Renamed the metadata attribute `[KotlinFile]` → **`[KotlinFileClass]`** (clearer: it marks the `<File>Kt` *class* that
  holds a file's top-level declarations). Pre-1.0, no compat shim.
- **Omitting a non-constant default argument is a clean compile error instead of a backend crash.** A default that reads
  the callee's own parameters/receiver (`b: Int = a * 10`, or a data class `copy`'s `x = this.x`) can't be filled by
  inlining it at the call site (`a`/`this` aren't in scope there) — it needs callee-side evaluation (Kotlin/JVM's
  `$default`), not yet implemented on the .NET backend. Such an omission previously crashed ilemit with
  `InvalidProgram`/`NotSupported`; it now reports a source-located error at the omitting call. Detected at the call site,
  not the declaration, so a data class whose `copy` is never arg-omitted still compiles.
- **Kotlin packages are now projected to .NET namespaces** (`package geom; class Vec` → `.NET geom.Vec`, file facade
  `geom.LibKt`). Previously every type was flattened to the **root** namespace — a correctness bug: two classes with
  the same simple name in different packages (e.g. `alpha.Box` + `beta.Box`) both emitted as `.NET Box` and **collided**
  (ilemit crash), and a packaged library couldn't be consumed across an assembly boundary (`import geom.Vec` resolved
  nothing). `BirEmitter` now qualifies top-level classes/interfaces/enums and the file facade with `packageFqName`
  (nested types stay simple-named — their outer carries the namespace; root-package code is unchanged by construction).
  This unblocks consuming a packaged DotKt library via MSBuild, including its top-level functions (`import geom.greet`).
- **Member `suspend fun` returning a user type** crashed ilemit (`AsyncTaskMethodBuilder<T>`/`Task<T>`/`TaskAwaiter<T>`
  are TypeBuilder instantiations whose `GetMethod` throws). A `GenM` helper re-anchors those members via
  `TypeBuilder.GetMethod`, and `EmitClrCall` now substitutes the open return type (`TaskAwaiter`1<!0>`) from the BIR
  `ret` hint so the await temp is typed correctly. Works through both a `suspend fun` and a `runBlocking { … }` lambda.
- **Parameter names** weren't emitted into the IL (ilemit defined methods by type only), so cross-assembly callers
  couldn't use named arguments. ilemit now writes them via `DefineParameter` (the names were always in the BIR).
- **Forward `ProjectReference`/`PackageReference` under the IL backend** — the dev-path
  `msbuild/KotlinClr.targets` never passed copy-local references to `ilemit`, so a
  `.ktproj` consuming a referenced non-BCL .NET type (e.g. a C# project's `Theme.Palette`,
  `Ext.Widget`) crashed at emit on the default IL backend (`ktproj-extlib` was broken).
  ilemit now receives `@(ReferenceCopyLocalPaths)` as `--ref`, matching the packaged SDK.
- **`ProduceReferenceAssembly` for `.ktproj`** — the SDK built its `obj/ref` reference
  assembly from our placeholder `.cs` (which holds no Kotlin types), so a downstream C#
  `<ProjectReference>` bound the empty ref assembly (CS0246). Disabled for `.ktproj` so
  consumers reference the real, retargeted output.

### Added (round-trip interop — consume a DotKt assembly AS KOTLIN)
All identified round-trip gaps resolved; guarded by `scripts/verify-roundtrip.sh` (roundtrip-pkg), each kept verify-il green.
- **Properties** (`val`/`var`/custom getters) — facadegen surfaces public instance fields and non-special `get_`/`set_`
  methods as Kotlin `prop`s; ilemit's `clrPropGet/Set` falls back to a field then a `get_`/`set_` method. This also makes
  **data classes** consumable (property access + already-round-tripping `componentN` operators + `equals`/`toString`).
- **Asymmetric visibility** (`val`, `var ... private set`) — a not-publicly-settable property's backing field is stamped
  `[KotlinReadOnly]`; the consumer restores it read-only (rejecting external writes). Fixes `val x` being exposed writable.
- **Extension functions, extension properties & top-level extension operators** — an extension's `__self` receiver is
  marked and restored as an extension receiver; `operator fun Vec.plus` is usable as `a + b`; `val T.p` round-trips as an
  extension property (BirEmitter emits its `get_/set_(__self)` statics; the backend routes `x.p` to them). Also fixed
  `isBuiltin` defaulting top-level functions to "builtin", which had lowered a restored `Vec + Vec` to a primitive `bin`.
- **vararg** — ilemit stamps `[ParamArray]`, facadegen encodes `vararg:<elem>`, the injector restores `isVararg`; `f(1,2,3)`
  and empty `f()` both work.
- **Default arguments** (constant, trailing) — restored @JvmOverloads-style (one overload per trailing default omitted);
  ilemit stamps `[DefaultParameterValue]` so the omitted args are filled at the call site.
- **Nullable types** — a `[KotlinNullable]` bitmask carries the signature's nullability; the consumer restores `T?`
  (type-level: passing null to a non-null parameter is rejected).
- Named-argument calls also work (ilemit emits parameter names). New metadata attributes: `[KotlinNullable]`, `[KotlinReadOnly]`.
  Remaining known limits (not round-trip blockers): object singletons — see docs/future-work-interop.md §5.
- **Default arguments — omit ANYWHERE (named-middle, reordered), on functions AND constructors.** Previously a restored
  default arg was @JvmOverloads-style (one positional overload per *trailing* default omitted), so a **named middle
  omission** — skip a middle default but provide a later one (`box(1, c = 9)`, `greet("C", punct = "?")`, `Pt(y = 4)`) —
  matched no overload and failed. The restored param now carries a **real constant default**: facadegen encodes the
  value in the metadata token (`opt:Int=2`, spaces escaped), and the injector builds a `FirLiteralExpression` and
  `replaceDefaultValue`s it (fir2ir then inlines the constant for any omitted arg, which `filledArgExprs` fills at the
  call site). Constructor parameter **names** are now emitted too (`DefineParamNames` for ctors), so named-arg ctor calls
  work. A .NET BCL method with a non-constant default (an enum/struct, e.g. `NumberStyles = 7`) keeps the @JvmOverloads
  trailing-overload fallback — the two strategies can't mix on one function (a bare `hasDefaultValue` flag with no literal
  crashes fir2ir). Guarded by `scripts/verify-roundtrip.sh` (roundtrip-defargs).
- **Generic round-trip** — user generics now consume from another `.ktproj` as Kotlin in **every position** and
  **combined with every other restored feature**: a generic user **class** (`class Box<T>`, with `operator`/`infix`
  members and a generic method `fun <R> mapTo(f)`), **two type parameters** (`Holder<A, B>`), generic user types in
  **return** and **parameter** position (`fun <T> wrap(x: T): Box<T>`, `fun <T> unwrap(b: Box<T>): T`), generic
  **extension** functions and **extension operators** on a generic type (`fun <T> Box<T>.twice()`), generic **top-level
  `suspend`** (`echoAsync`), and generics combined with **nullable** / **default-arg** / **vararg**. (Reified generics
  already worked — a generic method with no carried type.) The coordinated fixes:
  - **facadegen** — a root-namespace generic type's open .NET name was `.Box` (a leading dot: `Type.Namespace` is null at
    the root); now `OpenName` omits it. `Supported`/`CrossType` dropped a generic user type appearing in a signature
    (`Box<T>` → `Any?`), so the whole function silently vanished from the metadata; both now keep it (`generic:Box:T`).
  - **ilemit** — a generic type was emitted as `Box` without the CLR ``Box`1`` arity suffix, so a cross-assembly
    `GetType("Box`1")` missed it (same-assembly use resolves through the `_types` registry by BIR name, so it never
    surfaced); the metadata name now carries the arity, the registry key stays bare. A generic **extension** call omitted
    the `__self` receiver's shape (so overload resolution saw 0 params); it's now included. A generic fn with a
    **default arg** supplies fewer shapes than the single .NET method's params — `ResolveGenericMethod` now tolerates the
    trailing optional params and the emit path default-fills them.
  - **injector** — `coneOf` lost the method type variable nested inside a `generic:Box:T` argument (resolved `T` → `Any?`
    with a null owner, so a returned `Box<T>` became `Box<object>` and corrupted the call site); a type-variable resolver
    is now threaded through every recursion. The generic top-level path also ignored the extension receiver / `inline` /
    `infix` / `operator` / `vararg` / default-arg overloads — unified into the one path the ordinary case already used.
  - Guarded by `scripts/verify-roundtrip.sh` (roundtrip-generic). Known limitation (NOT a round-trip regression — it
    fails the same way in a single module): a `suspend` member of a generic class (`class Box<T> { suspend fun f(): T }`)
    is a separate pre-existing coroutine×generics gap, tracked in docs/future-work-interop.md.
- **Higher-order generics — a generic user type nested in a lambda parameter.** A function-type parameter whose argument
  or return is a generic user type (`fun <U,V> apply2(f: (Box<U>) -> Box<V>, …)`) now round-trips, in every position
  (top-level / member / extension / `infix` / `operator` / `inline`). Root cause: the internal metadata **type grammar
  was flat** (`func:<ret>:<args>` / `generic:<Open>:<args>`, colon/comma-delimited), so a `generic:` couldn't nest
  inside a `func:` — facadegen deliberately dropped such a lambda to `Any?`, which erased the type variable and made it
  uninferable at the call site. The grammar is now **recursive (bracketed)**: `generic:Box[V]`, `func:[ret,a,b]` — a
  compound child keeps its own commas, the injector splits at bracket depth 0, and `(Box<U>)->Box<V>` survives as
  `func:[generic:Box[V],generic:Box[U]]`. Guarded by `scripts/verify-roundtrip.sh` (roundtrip-generic-hof).
- **Member-declared extension functions** (`class C { fun T.f() }`) now round-trip — plain, `infix`, `operator`,
  `inline`+generic-method, and `protected` — consumed as Kotlin via `with(c) { x.f() }`. This also fixes a **pre-existing
  single-module bug**: a member extension's two implicit receivers (the dispatch `this` and the extension `__self`, both
  named `<this>` in IR) were name-keyed and got swapped, producing wrong results; they're now substituted by symbol
  identity, and a member-extension call dispatches on the enclosing instance with the extension receiver prepended.
  facadegen stamps `,ext`/`,inline` on the member `fun` line; the injector restores the extension receiver on the member
  path (the `fun`-line parser had also been dropping `,ext`/`,inline`). Guarded by `scripts/verify-roundtrip.sh`
  (roundtrip-memext).
- **Member-declared extension properties** (`class C { val T.p }`, `var` too) now round-trip — public + protected. A new
  `memextprop` metadata line carries the `get_p(__self)`/`set_p(__self, v)` member accessors; the injector restores a
  member property with an extension receiver, and a `x.p` read/write inside `with(c)` routes to C's `get_`/`set_` method
  with the extension receiver prepended.
- **Suspend member extensions** (`class C { suspend fun T.f() }`) — public + protected, consumed via the natural
  `with(c) { x.f() }`. Two general coroutine fixes enable it: (1) a `suspend fun`'s state machine was a top-level type
  and so threw `MethodAccessException` when its body touched a `protected`/`private` member of the owner — the SM is now
  **nested in its owner** (non-generic owners), which can reach those members; (2) a **suspending call inside an inline
  scope function** (`with(x){ f() }`, `run`/`let`/`apply`/`also`) is now **CPS-linearized through the state machine**
  instead of emitting an un-awaited `Task` (was a silent `InvalidProgram`). The scope function's receiver is bound to a
  state-machine field, `this`/`it` is substituted, and the lambda body's suspensions become real await points (handles
  nested scope functions, suspending args, and multi-statement bodies). Guarded by `scripts/verify-roundtrip.sh`
  (roundtrip-memext2). Remaining edge: a scope function used as a **sub-expression** (`c.apply{ f() }.x`) is a clean
  compile error — bind it to a `val` first.
- **Namespace projection** (`[assembly: DotKtNamespaceProjection(kotlinPrefix, dotNetPrefix)]`) — a DotKt library whose
  types live in one .NET namespace (e.g. `DotKt.Coroutines`) can be consumed under a different Kotlin package (e.g.
  `import kotlinx.coroutines.*`). The producer stamps it via `ilemit --ns-projection k=d` (SDK: a `<DotKtNamespaceProjection>`
  item); the consumer's facadegen reverse-projects each import to the real .NET type and the FIR injector forward-projects
  the .NET namespace to the Kotlin package, so types resolve under the imported package while the backend calls the real
  type. Prefix-based (sub-packages follow). The import scanner no longer drops `kotlinx.*` (external libs, not stdlib);
  only `kotlin.*` is filtered. Verified by `scripts/verify-roundtrip.sh` (roundtrip-nsproj).

### Removed
- **C# backend regression suite (`scripts/verify-all.sh`)** — the C# backend was retired
  in 0.x (2026-06-18); regression-testing a backend we no longer ship has no value, and the
  harness had rotted (the generated C#/façade path no longer compiles). The valuable
  MSBuild/.ktproj end-to-end coverage it carried moved to the new **`scripts/verify-ktproj.sh`**,
  which runs those samples on the shipping **IL backend** (and adds `ktproj-bidir`). CI runs
  `verify-il` + `verify-differential` + `verify-ktproj`.

## 0.9.2 — 2026-06-23

Interop/primitive bug fixes, most surfaced building a real WinUI app from Kotlin.

### Fixed
- **Signed `Byte` / `Short`** as parameters, locals, fields, and constant args threw
  `InvalidProgramException` (or crashed ilemit). They were omitted from the primitive
  paths (Int/Long/unsigned were present): `birType` fell to the user-type fallback
  `@Byte`/`@Short`, and ilemit `EmitConst` had no `byte`/`short` case so a `const byte`
  pushed `null`. Kotlin `Byte` = signed `sbyte`, `Short` = `Int16` (UByte stays
  unsigned). Fixes `MemoryStream().WriteByte(65)` too. (`il-bytearg`)
- **Lambda passed to a .NET constructor's delegate parameter** (`new Thread({ … })`)
  crashed ilemit with a `NullReferenceException` (`EmitClrNew`): the façade erases the
  delegate param, so the exact-type ctor lookup found nothing. `EmitClrNew` now selects
  the ctor by arity (preferring delegate-param/lambda-arity matches) and builds the
  specific delegate. (`il-delegatearg`)
- **`for (x in <.NET IEnumerable<T>>)`** over a raw .NET enumerable (not a Kotlin
  collection) failed to compile: `iterator()` was ambiguous (only the clashing stdlib
  extension `iterator()`s applied). facadegen now injects a frontend-only
  `operator fun iterator(): Iterator<T>` for any type implementing `IEnumerable<T>`;
  the backend bypasses it and enumerates via GetEnumerator/MoveNext/Current
  (forEachInline). (`il-netenum`)
- **User class implementing Kotlin `Iterable<T>`** (`class R : Iterable<T>`) crashed
  ilemit (`KeyNotFoundException 'Iterable'`): `Iterator<T>` had a monomorphized
  synthetic interface but `Iterable<T>` did not. Added `KIterable_<elem>`
  (`operator fun iterator(): KIterator_<elem>`), parallel to the existing
  `KIterator_<elem>`; both the `for` loop and explicit `.iterator()` now work. (`il-iterable`)
- **User class implementing/extending a .NET-mapped Kotlin stdlib supertype** crashed
  ilemit (`KeyNotFound`) — the supertype emission didn't route these through their
  .NET mapping. A whole cluster:
  - **Custom exceptions** `class E(msg) : Exception(msg)` / `RuntimeException` -> a CLR
    class `: System.Exception` (ctor chains to `System.Exception(string)`, `.message`/
    `.cause` -> `.Message`/`.InnerException`, catchable by base type). (`il-customexc`)
  - **`Comparator<T>`** -> `IComparer<T>` (`compare` -> `Compare`). (`il-comparator`)
  - **`AutoCloseable`/`Closeable`** -> `IDisposable` (`close` -> `Dispose`).
  Mechanism: supertype base/interface emission now routes through `birType` when it
  maps to a `clr:`/`clrg:` spec; `clrIfaceMemberName` renames the overridden members;
  the `catch` clause types via `birType` (a user exception catches as its own type, not
  `object`); `MapType` resolves bare .NET FQNs. (Comparable<T> as a self-referential
  generic supertype is now handled too — see below.)
- **`use {}`** (Closeable/AutoCloseable) now lowers to `try { block(it) } finally { close()/Dispose() }`
  returning the block value — the CLR analogue of C# `using`. (`il-use`)
- **`Comparable<T>`** (`class V : Comparable<V>`) — the self-referential generic interface
  `IComparable<V>` (V the emitted type) made ilemit call `.GetMethods()` on a
  TypeBuilderInstantiation (throws). Interface-impl linking now enumerates the OPEN
  generic definition and re-anchors each method via `TypeBuilder.GetMethod` (same
  pattern as the self-ref base ctor). `<`/`>`/`<=`/`compareTo`/`sorted()` all work. (`il-comparable`)
- **`class S : CharSequence`** -> a synthetic `<>dotkt_CharSequence` interface (length
  getter + get(i) operator + subSequence); no faithful BCL equivalent exists. (`il-charseq`)
- **`String.substring(start, end)`** used .NET `Substring(start, LENGTH)` directly, but
  Kotlin's `end` is an EXCLUSIVE INDEX -> the 2-arg form now converts `end -> end - start`
  (`"hello".substring(1,4)` = "ell", was "ello"). (`il-substr`)
- **Type-injector metadata** (façade generation), found building a WinUI-on-Kotlin library:
  - Assignability edge no longer dropped for a non-constructible base (WinRT `UIElement`,
    `SafeHandle`): the supertype edge is emitted for is-a regardless of a base no-arg ctor;
    a `basector none` marker suppresses the synthesized `: super()` only. (`il-injbase`)
  - Member signature types now use the FULLY-QUALIFIED name, so a same-simple-name type from
    another namespace (`Microsoft.UI.Xaml.LaunchActivatedEventArgs` vs the UWP one) no longer
    shadows the right one — fixes overrides that "override nothing". (`il-injfqn`)
  - Public **static members of a normal class** (one with instance members too) are now
    injected — they were dropped, so `Application.Start(cb)` / `Application.Current` were
    unresolved. Surfaced on a synthesized companion: facadegen emits `sfun`/`sprop`, the
    injector generates the companion, the backend emits .NET static calls (lambda args bind
    to the .NET delegate). Accessed via `App.Companion.Start(cb)` / `App.Companion.Current`
    (`il-injstatic`). NOTE: the bare `App.Start` form is NOT supported — the current
    compiler doesn't resolve the implicit companion of a plugin-generated class, so the
    `.Companion` qualifier is required (accepted rule).
  - A .NET **FIELD surfaced as a Kotlin property** (facadegen records static/const fields
    and public instance fields as `sprop`) crashed ilemit with a `NullReferenceException`
    (later a 0xC0000005 access-violation via MSBuild) — `clrPropGet`/`clrPropSet` only looked
    up a property accessor. They now fall back to `ldfld`/`ldsfld` / `stfld`/`stsfld` — and a `const`/literal field is
    INLINED (its value pushed, as C# does, since a literal has no storage and can't be
    `ldsfld`'d) — otherwise an actionable "no property OR field" error. Verified via
    `il-injstatic` (`App.Companion.Answer`=99 static readonly; `App.Companion.Magic`=123 const).
  - `ilemit` gained an `ILEMIT_TRACE` env switch that prints each emission step (ref load,
    parents, signatures, bodies, createType, save) flushed to stderr — so a Reflection.Emit
    hard-crash (uncatchable AV, exit 0xC0000005) can be localized to the culprit type/method.
- **Per-file lifted state leaked across files (multi-file)** — one `BirEmitter` instance
  processes every file, but its per-file lifted collections (`liftedMethods`/`liftedTypes`/
  synthesized delegate classes/ref cells/iterator+property+CharSequence+KProperty synthetics)
  were never reset, so each file's BIR ACCUMULATED the prior files' lifted lambdas/types —
  duplicating e.g. `App.kt`'s `__lambda*` into ControlsKt/DslKt/LayoutKt/ReactiveKt. The
  `<>dotkt_*` types are de-duplicated by ilemit, but lifted `__lambdaN` are file-class methods
  that are not, so this was real metadata bloat (and a corruption hazard surfaced building a
  multi-file WinUI app). `emitFile` now resets all per-file lifted state up front. (`il-mflambda`)
- **Overloaded user functions resolved to the wrong method** — ilemit keyed methods by NAME
  only, so `f(String)` and `f(() -> String)` collided in one dictionary: the last-declared
  overwrote, a body was emitted into the wrong overload's `MethodBuilder`, and calls picked
  the wrong target. Manifested as a WinUI crash — the DSL's `text(String)` / `text(() -> String)`
  caused `text(() -> String)` to run `tb.Text = <the Func itself>` (the String overload's body),
  so CsWinRT marshaled a `Func` object as a string (`WindowsCreateStringReference` AV / OOM).
  ilemit now keys methods by name + parameter-type signature (`MethodsBySig`); BirEmitter emits
  that signature on each call (callStatic/callInstance, incl. extension and companion calls) so
  body emission AND call resolution pick the right overload. Covers top-level and member
  overloads, by arity and by parameter type. (`il-overload`)
- **Expression-body function with a Unit-typed body dropped the call** — `IrReturn(<expr>)`
  emitted a bare `{"k":"return"}` when the value's type was `Unit`, discarding the
  expression. So `fun main() = winUiApp { … }` (and `fun f() = sideEffect()`, or an explicit
  `return doCleanup()`) launched/ran NOTHING. A Unit-typed return value is now EVALUATED
  (`exprStmt`) before the bare return; only a plain Unit reference (`return`/`return Unit`)
  stays a bare return. (`il-exprbody`)
- **Unsigned .NET parameter types weren't mapped to Kotlin unsigned types** — facadegen's
  primitive map had `System.Int32→Int` etc. but no `System.UInt32`/`UInt64`/`UInt16`, so a
  `uint` parameter surfaced as the bare name `UInt32`, which doesn't unify with `kotlin.UInt`
  ("argument type mismatch: actual 'UInt', expected 'UInt32'") — hit calling WinUI's
  `Bootstrap.Initialize(uint majorMinorVersion)`. Added `UInt32→UInt`, `UInt64→ULong`,
  `UInt16→UShort`, `SByte→Byte`. (`il-injuint`)
- **Synthetic type names collided across files in a multi-file assembly** — every file's
  `BirEmitter` used a fresh counter, so `<>dotkt_Closure0…`, `<>dotkt_Ref_<elem>`, and
  `<>dotkt_Seq…` repeated across files. Linking all BIR into one assembly overwrote them in
  ilemit's `_types`, orphaning a `TypeBuilder` that was never `CreateType()`'d →
  `NotSupportedException` ("not supported before the type is created") at `Save`, or a
  `0xC0000005` via MSBuild. (Single-file samples never hit it.) BirEmitter now prefixes these
  per-file-DISTINCT synthetics with the file class (`<>dotkt_<FileKt>_Closure0`); ilemit
  de-dups per-file-IDENTICAL shared synthetics (`<>dotkt_Result`/`KProperty`/`KIterator_*`/…)
  by name; and `Ordered()`/a pre-Save sweep make every defined TypeBuilder get created.
  (`il-mfclosure` — two files, capturing closures + ref cells.) Found building a WinUI app
  whose `.ktproj` source-includes the whole library.

## 0.9.1 — 2026-06-23

Language/stdlib long-tail completion + a type-emission correctness refactor. The
direct-IL backend, coroutine surface, generics, and forward interop were already
complete in 0.9.0; this release closes the remaining A (language) / B (stdlib) gaps
so the A/B checklists in `docs/remaining-tasks.md` have **zero** open items.

### Added
- **Regex `matches` / `find`** — full-input match + `MatchResult?` (via `DotKt.Text.Regexes`
  shims), `MatchResult.value` → `Match.Value`. (`il-regex`)
- **`return` as an expression** — `val x = if (c) a else return b` (new `returnExpr`
  lowering, `tryStack`-aware). (`il-langtail`)
- **enum per-entry bodies** — `enum class Op { PLUS { override fun apply(…)=… }; abstract
  fun apply(…) }`: the base enum becomes abstract and each body entry is emitted as a
  subclass `<>Enum_NAME : Enum`. (`il-enumbody`)
- **Field-level visibility** — a property's visibility is honored on its backing field:
  `private` → true `FieldAttributes.Private`, `internal` → `Assembly`, `protected` →
  `FamORAssem`. (`il-fieldvis`)

### Changed
- **Inner / nested classes are now emitted as true CLR nested types** (`Outer+Inner`)
  instead of being flattened to separate top-level types. Nested types retain Kotlin's
  legal access to the enclosing type's `private` members, which is what makes true
  `private` field visibility correct. `inner` classes still capture `__outer`.

### Fixed
- **Compound-condition smart-cast** — `if (x is Int && x > 10)` no longer mis-takes the
  then-branch (the `>` operand stayed boxed as `Any`); `bin` now coerces a boxed operand
  to the other operand's primitive type, and `IrGetValue` honors a narrowed smart-cast.

### Notes
- Verified working & locked by samples this release: `lateinit` (uninitialized read
  throws), `field` in custom accessors, `when`+type smart-cast.
- Full IL suite green + JVM differential ALL MATCH + ilverify-clean.
- Known residue (unchanged, tracked in `docs/remaining-tasks.md` §F / §R): packaged-SDK
  end-to-end consumption still has MSBuild SDK-resolution plumbing to finish (F-308);
  reverse-interop cosmetic naming/`[Nullable]` is gated behind R-1.

## 0.9.0

Initial pre-1.0 line: direct-IL backend (C# codegen retired), CLR-native coroutines
(`suspend` ⇔ `Task<T>` / `IAsyncEnumerable`), user generics, forward .NET interop
(import-driven, façade-free), and the 3-package distribution (Sdk / Toolchain / Runtime
+ Templates).

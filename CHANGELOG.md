# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Fixed

- **bir2cir (#25): a cross-module GENERIC top-level fun among a same-name OVERLOAD SET now binds the correct
  overload.** A generic top-level function consumed cross-module from a re-imported `kotlinx.*`/DotKt Kotlin library
  (facadegen ProjectReference round-trip) lowered to a `callStatic` carrying `typeArgs`+`shapeTypes` but NO resolved
  `sig`/`argTypes` (kotc emits the pure-Kotlin overload-matching *shape* for a generic call instead of the concrete
  `sig` a non-generic call gets). Because the owner FQN is `kotlinx.*`, `NetInteropBinding` leaves it a plain
  `callStatic` (never a `clrGeneric*` node), so ilemit's `callStatic` overload resolution — which selects via `sig`
  (`FindReflectedMethodBySig`) *before* `MakeGenericMethod` — dropped to a name-only arity pick and MIS-BOUND: e.g. the
  atomicfu-shaped `atomic<String?>(null)` bound to its arity-2 defaulted sibling `atomic(T, trace: TraceBase = None)`
  (the non-const default `None` then passed as null → `NullReferenceException`), and the sole-generic
  `arrOf<T>(n)`/`atomicArrayOfNulls<T>(size)` reported `ilemit: static method not found`. Fix (bir2cir-side, general —
  no atomicfu special-casing): when `MemberCallSubstitution` owner-attributes such a generic top-level call, it now
  promotes `shapeTypes`→`sig` (kept OPEN — a method type-var stays `gp:T` so it matches the OPEN generic method def)
  and stamps the concrete `argTypes` (the call's `typeArgs` substituted for the method type-vars), exactly parallel to
  the non-generic path — so ilemit selects the arity-1 generic overload and finds the sole-generic factory. Unblocks
  the kotlinx-atomicfu CLR port. Gate: `ktproj-genov` in `verify-ktproj.sh`.

## 0.9.6-rc2 — 2026-07-15

### Fixed

- **bir2cir (#22): an inline-splice carrier whose body NESTS a lambda capturing an enclosing binding now
  materializes.** `MaterializeCarrier` (§4.4ii, `InlineSplice.cs`) refused any carrier containing a nested closure and
  failed loud — but a `suspend inline fun` with a `crossinline` block that nests a lambda (the
  `suspendCancellableCoroutine { cont -> cont.invokeOnCancellation { … } }` shape every cancellable-coroutine block
  uses) always hit that refusal, blocking the kotlinx.coroutines-core port. The blanket `HasNestedClosure` guard is
  replaced by `HasUnmaterializableNested` (refuses only a nested `newSuspendLambda`/`newDelegate`/un-spliced
  `inlineLambda`), and the four sibling scans (capture-rewrite, stray-local, this-guard, tv-collection) now DESCEND into
  a nested closure's CAPTURE VALUES while skipping its own `synthClass` frame — so a nested closure capturing the
  block's invoke param (`cont`), a carrier capture (rewritten to `this.<field>`), or a carrier local is bound
  correctly, and a genuinely-unlisted capture still fails loud. Gate: `il-suspendnestedcapture` in `verify-il.sh`.
- **kotc (#20): an inline MEMBER-extension fn called with a lambda now splices.** kotc rejected any call to an
  `inline fun` that is BOTH a member (e.g. of a companion) AND an extension — `class Queue { companion object { inline
  fun <T> Long.withState(block) {…} } }` called via `state.withState { … }` — because such a callee carries BOTH a
  dispatch (enclosing-class) and an extension receiver, and `inlineSpliceCallSameModule` blanket-failed the shape. But
  the extension receiver already rides the leading `__self` param (`InlineBirStash` classifies it `recv=extensionParam`),
  so the splice binds it exactly like a top-level extension; the ONLY `{k:this}` a spliced member-ext body can carry is
  the dispatch receiver (the extension `this` renders as `{k:local,name:__self}` via `selfSubst`). The blanket guard is
  narrowed to fail loud ONLY when the callee body actually references the dispatch receiver (new `bodyReferencesDispatch`
  IR scan) — the pure-extension idiom (a `Long`-decoder that never touches the enclosing class) splices, keeping the
  non-local `return` through the lambda inline. Co-binding both receivers when the companion IS used is a deferred
  bir2cir follow-up. Unblocks the kotlinx.coroutines CLR port (`LockFreeTaskQueueCore.withState`, 7 call sites, all
  companion-unused). Gate: `il-memberextinline` in `verify-il.sh`.
- **kotc/bir2cir/ilemit (#14): `super.X()` to a CLR-bound base is now a non-virtual `call` to the base slot (no more
  infinite recursion).** The #14 core already made a user-class `super.X()` non-virtual (`virtual:false`), but when the
  super-call's base is CLR-bound the non-virtual intent was LOST downstream: bir2cir reshapes the `callInstance` into a
  `clrInstance`/`clrPropGet` (dropping `virtual`) and ilemit `callvirt`s an unconditional virtual on the reference
  receiver — so `super.toString()`/`equals()`/`hashCode()` (→ `System.Object`), a `super.<m>()` to a facadegen-injected
  .NET base, and a `super.<m>()` to a `@ClrTypeAlias` stdlib base all re-dispatched by the receiver's runtime type back
  to THIS class's override → stack overflow. Fix (one contract across three layers): kotc stamps a `"super":true` marker
  on the `super`-qualified `callInstance` (new `superTag`); bir2cir propagates it onto the produced `clrInstance`/
  `clrPropGet`/`clrPropSet`/`clrGenericInstance` (both the `@ClrTypeAlias` `MemberCallSubstitution` route and the
  facadegen `NetInteropBinding` route); ilemit's `EmitInstanceCall`/`EmitClrCall`/`EmitClrPropGet`/`EmitClrPropSet`
  (and the generic-instance path) honor it, emitting `OpCodes.Call` for a reference owner — exactly C#'s `base.M()` IL.
  Gates: `il-superobj` (R1: `super.toString()`/`hashCode()`/`equals()` → the `System.Object` slot) and the new
  `il-supernet` (R2: `super.Next()` to facadegen-injected `System.Random`) in `verify-il.sh`; the `superobj` `XFAIL_RUN`
  entry is pruned. `il-supercall` (user-class super) stays green.
- **kotc (#16): top-level functions in a dotted-filename file class (`api.common.kt` → `Api_commonKt`) now round-trip
  cross-module.** `BirEmitter.fileClassName` derived the file-facade class from the raw filename stem without sanitizing
  it, so `api.common.kt` became file class `demo.Api.commonKt` — and ilemit's `DefineType` reads that dot as a namespace
  separator (real type: Namespace=`demo.Api` / Name=`commonKt`), so facadegen scanning package `demo` never surfaced its
  top-level functions and a cross-module consumer got `unresolved reference` (top-level *classes* round-tripped either
  way — they carry their own type name). The stem's non-identifier characters are now replaced with `_` before
  capitalize+`Kt` (`Api_commonKt`), exactly as stock Kotlin derives the file class (`AtomicFU.common.kt` →
  `AtomicFU_commonKt`). Surfaced by the kotlinx-atomicfu CLR port (`update`/`getAndUpdate`/`loop` in `AtomicFU.common.kt`
  were invisible). Gate: `roundtrip-dotfile` in `verify-roundtrip.sh`.
- **kotc (#21): a bound reference to a top-level extension property (`this::extProp`) now lowers.** kotc rejected any
  property reference whose accessor carried an extension receiver with "an extension-receiver property reference
  (KProperty2) has no supported lowering yet"; only the property *value* (`this.extProp`) worked. A top-level extension
  property reference is now lowered like the member-property reference path: a `KProperty0`/`KProperty1` lift whose
  `get`/`set`/`invoke` invoke the static extension accessor with the ext receiver — captured in a `__recv` field for a
  BOUND ref (`obj::extProp` → `KProperty0<V>`), or passed as the leading param for an UNBOUND ref (`Type::extProp` →
  `KProperty1<T,V>`). Only a genuine `KProperty2` (a member extension property with both a dispatch AND an extension
  receiver, inexpressible as a plain Kotlin callable) stays unsupported. Blocked the kotlinx.coroutines CLR port
  (`LockFreeLinkedList.toString()` uses `this::classSimpleName`). Gate: `cases/il-extpropref`.
- **kotc (#19): a bare lambda `{ … }` into a .NET member overloaded on delegate-typed params resolves again — no
  overload-resolution ambiguity.** `Thread({ … })` (`ThreadStart` / `ParameterizedThreadStart`) and `Task.Run({ … })`
  (`Action` / `Func<T>`) regressed to an ambiguity once the delegate types surfaced faithfully (`() -> Unit` vs
  `(Any?) -> Unit` / `() -> T`), because a no-arrow lambda's arity/return is unspecified and matched both candidates.
  facadegen's `MarkLowPriorityDelegateOverloads` marks the Pareto-dominated (wider-arity / value-returning) sibling
  `lowPriority`; kotc's `ClrTypeInjection` now maps that marker onto the synthesized ctor/member (and companion-static)
  FIR declaration as `@kotlin.internal.LowPriorityInOverloadResolution` (read by the `CheckLowPriorityInOverloadResolution`
  stage for both `FirSimpleFunction` and `FirConstructor`). So a bare `Thread({ … })` binds `ThreadStart` and
  `Task.Run({ … })` binds `Action`, while an explicit `{ x -> … }` / a method reference still reaches the wider sibling.
  The `cases/il-monitordrain` `Thread({ -> … })` arity-pin band-aid is removed (natural `Thread({ … })` now resolves).
  Gate: `cases/il-threadlambda`. Semantics: `docs/dotkt-semantics.md` §8e.
- **bir2cir (#22): a `suspend inline fun` with a `crossinline` lambda (the `suspendCancellableCoroutine` shape) is now
  cold-lowered instead of reaching ilemit un-lowered / failing the inline splice.** The crossinline `block` is invoked
  INSIDE the `suspendCoroutineUninterceptedOrReturn { … }` intrinsic's block, so InlineSplice materializes the carrier
  as a `newClosure` VALUE the intrinsic block captures (`§4.4ii`) — but four interacting gaps then blocked the cold
  core: (1) `SuspendColdLowering` excluded ALL `inline` suspend funs, so the wrapper's standalone body reached ilemit
  un-lowered — the exclusion is now lifted in APP builds only (a stdlib build's only inline suspend funs are the
  `suspendCoroutine`/`suspendCoroutineUninterceptedOrReturn` primitives, which stay stubbed); (2) the shape gate refused
  a body holding any `newClosure`, so the non-inline caller whose spliced body carries the materialized closure was
  never lowered — a PLAIN closure VALUE (no suspension inside) is now admitted as a spillable local; (3) the intrinsic-
  block closure class the cold lowering reconstructs INLINE was still emitted (dead) and mis-resolved a direct
  `COROUTINE_SUSPENDED` block-return to the enclosing file class — the dead class is now pruned and the sentinel
  canonicalized to the SM's `Suspended()` marker; (4) the unintercepted form passed the SM itself as the raw
  continuation, so a synchronous `cont.resume(v)` re-entered before the state label was armed and recursed unboundedly —
  the label is now set BEFORE the block (mirroring the JVM CPS), so the re-entry lands at the resume point. The
  `contract { callsInPlace(block, …) }` needs no handling: Fir2Ir drops `FirContractCallBlock`, so it never reaches BIR.
  Gate: `cases/il-coinline` (two sync-resume suspensions sequenced in one `suspend fun main` SM → `42`). Unblocks
  emitting kotlinx.coroutines-core's `suspendCancellableCoroutine` family.
- **bir2cir/facadegen (#18): a cross-module generic factory `fun <T> holderOf(): Holder<T?>` no longer degrades to
  `Any?` on re-import — every member of the generic result (the `size` property, the `get` indexer) now surfaces.**
  bir2cir's `NullableGenericReturnErasure` object-erases the nested `Nullable(Tv)` of such a return to the one uniform
  CLR carrier (`Holder<object>`, #142-mandatory), but facadegen then could not read the erased arg back and collapsed
  the whole re-imported return to `Any?` — so `val h = holderOf<String>(3)` was typed `Any?` and `h.size` / `h[0]`
  were unresolved (`h[0]` even mis-resolved to `MatchGroupCollection.get` → `MatchGroup?`). The erasure now RECORDS the
  pre-erasure return TypeNode (`Holder<T?>`) as a `nullableGenericRet` fact; `RoundtripMetadata` carrier-encodes it into
  a new `[KotlinNullableGeneric]` return attribute (same round-trip channel as `[KotlinSuspendFunctionType]` /
  `[KotlinNothing]`); and facadegen restores `Holder<T?>` from it — keeping its own open-name derivation for the outer
  type and only re-injecting the recorded `Nullable(Tv)` where `object` was erased. The erasure itself is unchanged
  (only the erased fact is saved and restored). Unblocks the kotlinx.coroutines CLR port (`atomicArrayOfNulls<T>():
  AtomicArray<T?>`). New gate case `cases/ktproj-genq` (registered in `verify-ktproj.sh`).

- **kotc (#14): a `super.X()` call from an override no longer infinite-recurses — it now emits a NON-virtual `call` to
  the resolved base slot.** kotc never read `IrCall.superQualifierSymbol`, so it emitted `super.greet()` identically to
  `this.greet()` — a `callInstance` with `virtual:true` → ilemit `callvirt Base::greet`, which re-dispatches by the
  receiver's runtime type back to the override → stack overflow. A shared `isVirtualInstanceCall(call, callee)` helper
  now folds `call.superQualifierSymbol == null` into every instance-call virtual-flag computation (the primary
  instance-method, member-extension, property get/set accessor, .NET-interop, and index get/set sites), so a
  super-qualified call is a plain `call` to the base member — exactly like C#'s `base.M()`. A normal virtual dispatch
  through a base-typed variable is unchanged (still `callvirt` to the override). Also fixes `super.<prop>`, N-level
  `super` chains (A/B/C), `super` to a user base's `toString()`, and `super<IFace>.foo()` to a Kotlin interface default
  (DIM). Gate: `cases/il-supercall`. **Known residual (`cases/il-superobj`, XFAIL):** `super.toString()`/`hashCode()`/
  `equals()` whose immediate super is `kotlin.Any` still re-dispatches — kotc's BIR is faithful (`callInstance
  kotlin.Any virtual:false anySlot:true`), but bir2cir substitutes the `@ClrTypeAlias(System.Object)` owner to a
  `clrInstance System.Object::ToString` and drops the non-virtual intent, and ilemit's `EmitInstanceCall` emits an
  unconditional `callvirt` for a reference owner; the same drop affects a super call to any facadegen-injected .NET base
  or `@ClrTypeAlias`-bound stdlib base — a bir2cir+ilemit follow-up. Semantics: `docs/dotkt-semantics.md`.
- **kotc (#15): a declaration whose identity is BOTH declared in the compiled source AND facadegen-injected from a
  `<ProjectReference>`'d assembly is no longer materialized TWICE.** When an app's `**/*.kt` glob reaches a referenced
  library's *own source files*, the app compiled `class Plain` / `fun hello()` from source while facadegen *also*
  injected `demo.Plain` / `demo.hello` from the referenced dll — the FIR injection extension produced a second,
  identical copy, so *using* (not merely referencing) the name gave `overload resolution ambiguity` at the call site and
  `conflicting overloads/declarations` at the source decl site (only TYPES/ctors and TOP-LEVEL functions doubled). The
  injector (`ClrTypeInjection.kt`) now consults the SOURCE FIR provider (`session.firProvider`, non-recursive) and
  SUPPRESSES any injection whose ClassId/CallableId the compiled source already declares — the source declaration wins
  and emits as a plain local type/call (the backend accessors exclude the shadowed identity so it is never clr-routed to
  the referenced dll). Suppression is per (package, name) and per kind; a source overload of a different signature still
  shadows the referenced same-name one (a loud unresolved-reference — the real remedy is to not compile the referenced
  library's source into the app). Gate: `cases/il-injectdedup` (source + injection of the same `demo.Plain`/`demo.hello`
  via the metadata alone) in `scripts/verify-il.sh`. Semantics: `docs/dotkt-semantics.md` §8f.
- **bir2cir (#15): a type declared in THIS compilation now WINS over a referenced dll of the same identity — the
  emit half of the #15 ProjectReference-source-glob layout.** After the frontend "source wins" fix, the app compiles
  `demo.Plain`/`demo.hello` into a LOCAL BIR type, but bir2cir still resolved `demo.Plain` against the referenced
  `Demo.dll` and emitted `newClr`/`clr*` — so the app both emitted `demo.Plain` locally *and* `newClr`'d the ref's
  copy, and ilemit errored. `ReferenceMetadataIndex.ResolveNetType` now refuses to bind a locally-emitted FQN to the
  refs (a new `_localEmittedTypes` set, the union of every input file's BIR `types`, populated before the transform
  loop) — the single chokepoint through which `TransformNew`'s injected-owner fallback and `NetInteropBinding`'s
  call/field/bound-delegate reshapes all resolve. A local `new demo.Plain` (this-assembly-emitted) and a local
  `hello()` result; a type present ONLY in the ref is unchanged. This mirrors the frontend "source wins" and makes
  the long-standing "ResolveNetType skips every local type" comments true. Gate: `cases/ktproj-injectemit` (the app's
  recursive glob pulls in a nested ProjectReference lib's source AND references its dll) in `scripts/verify-ktproj.sh`.

- **bir2cir (#17): a direct property get/set on a re-imported cross-module Kotlin type now lowers to the
  `get_<p>`/`set_<p>` accessor call.** A `--ref` Kotlin assembly whose type FQN starts with `kotlin.`/`kotlinx.`/
  `dotkt` (e.g. a `kotlinx.atomicfu.AtomicInt` port) is deliberately SKIPPED by `NetInteropBinding.ResolveNetType`
  (that prefix is reserved for stdlib binding), so its property access never reached the `clrPropGet`/`clrPropSet`
  reshape. `MemberCallSubstitution` then returned the node untouched — leaving kotc's bare `callInstance{method:"value",
  prop:"get"}` marker for ilemit, whose external-owner `ResolveMethod` looked for a literal method `value` and failed
  (`method kotlinx.atomicfu.AtomicInt.value() not found`). The non-CLR-bound reconstruction (which already rebuilt the
  `get_`/`set_<name>` accessor on the STATIC axis) now covers the INSTANCE axis too, so the call resolves against the
  referenced dll's public `get_value`/`set_value` accessor. Regression gate: `cases/ktproj-reprop` (a `kotlinx.cell`
  Library re-imported via `<ProjectReference>` with a `var value` read AND written).
- **facadegen (#19, half): a .NET member overloaded on delegate params of adjacent arity / Unit-vs-value return now
  stamps a `lowPriority` marker on the deprioritized overload, so a bare Kotlin lambda disambiguates.** Passing `{ ... }`
  to `Thread(ThreadStart)` / `Thread(ParameterizedThreadStart)` (or `Task.Run(Action)` / `Task.Run(Func<T>)`) regressed
  to an overload-resolution ambiguity after the #1 delegate-faithfulness fix (the object-param delegate now surfaces as
  `(Any?) -> Unit` instead of collapsing to `Any?`, so a no-arrow lambda — arity unspecified — matched both candidates).
  The delegate types are already faithful, so the ambiguity is inherent to Kotlin overload resolution and cannot be
  fixed by shape. facadegen (the only layer that sees the full .NET overload group) now runs a per-group Pareto analysis
  (`MarkLowPriorityDelegateOverloads`) and marks the less-preferred sibling — the higher-arity delegate (`Thread`) or the
  value-returning `Func` (`Task.Run`). **Cross-layer, not yet end-to-end:** the marker is inert until kotc's
  `ClrTypeInjection` maps it to the FIR `kotlin.internal.LowPriorityInOverloadResolution` annotation (reported to the
  coordinator for the kotc half + the `il-monitordrain` band-aid removal + `docs/dotkt-semantics.md` §8e rewrite).

## 0.9.6-rc1 — 2026-07-14

### Fixed

- **packaging (#131 durable): the `DotKt.Sdk` / `DotKt.Sdk.Mpp` `Sdk.props` `DotKtVersion` default is now guarded at
  pack time.** That default is copied verbatim into the SDK package (the nuspec `$version$` never reaches it) and pins
  the implicit `DotKt.Toolchain` / `DotKt.Stdlib` PackageReferences — a stale value silently pulls an OLD toolchain
  (the 0.9.5 SDK shipped pinned to 0.9.3, pulling the pre-2.4.0 compiler). `scripts/pack-nuget.sh` now refuses to pack
  when the `Sdk.props` default drifts from `DotKtVersionPrefix`, so a stale pin can never ship again.
- **bir2cir (#11): a nullable/`null` source written into a value-type platform slot is now coerced at the `clrPropSet`
  boundary — the WRITE twin of #8's oblivious read.** A facadegen-injected value-type platform property (e.g.
  `ThreadLocal<Int>.Value`, a bare `int32` setter) assigned a genuine Kotlin `Int?` (`ti.Value = someIntQ`) previously
  produced an `InvalidProgramException` — a `Nullable<Int32>` value flowed unchanged into the bare `int32` slot. New pass
  `ValueSlotNullableWrite` (runs right after `NetInteropBinding`, non-ref builds) reflects the setter slot; when it is a
  bare (non-`Nullable<>`) value type it **unwraps** a `Nullable<V>` source via the existing `nullableValue`
  (`Nullable<V>.get_Value()`), and it **fails loud** at emit time on a literal-`null` write (`ti.Value = null`) — a CLR
  value type has no null representation, so a silent `default(V)` would mask a user bug. A genuine `Nullable<V>` .NET
  property, a `ThreadLocal<Int?>` slot, and reference slots (`ThreadLocal<String>`) are untouched. Gate: the extended
  `cases/il-tlvalint` (write `5`, write `Int? = 7` → coerced to 7, and the `String?` reference-slot twin). Semantics:
  `docs/dotkt-semantics.md` §9a-bis.
- **gate infra (#13): the cached CLR stdlib artifacts now rebuild on a TOOLCHAIN-FINGERPRINT mismatch, not only
  when missing — killing a class of false-RED and (worse) silent stale-GREEN.** `scripts/lib.sh`'s
  `need_fe_klib`/`need_stdlib_ref`/`need_stdlib_rt` used to reuse an existing
  `kotlin-stdlib-clr-frontend.klib` / `DotKt.Private.Stdlib.dll` / `DotKt.Stdlib.dll` unconditionally, so an artifact
  baked by an OLDER toolchain (or left by another branch's build) was silently consumed by a NEWER compile — the source
  of an intermittent `AbstractCoroutineContextElement` methodimpl false-RED, and the more dangerous silent stale-GREEN
  (a case passing against a stale bake, regressing on the next fresh one). Each artifact is now stamped with a
  fingerprint (a `mtime+size+path` hash over its build inputs) in a sidecar `<artifact>.toolstamp`, and `need_*`
  rebuilds when the artifact is missing OR the sidecar is missing OR the recomputed fingerprint differs. Inputs per
  artifact: **klib** = installed kotc + `libraries/stdlib/` (a klib has no IL, so the tool dlls are irrelevant);
  **ref** = kotc + `bir2cir.dll` + `ilemit.dll` + `retarget.dll` + sources; **rt** = kotc + `bir2cir.dll` +
  `ilemit.dll` + the ref dll it consumes + sources. An unchanged toolchain leaves every input untouched, so the stamp
  matches and the build-only-if-needed fast path is preserved (no per-gate rebuild). Also hardened
  `scripts/build-stdlib-rt.sh` to `die` (was: silently proceed with no `--ref`) when the reference dll is absent.

- **facadegen/bir2cir (#10): `await` generalized from Task-only to the .NET AWAITABLE PATTERN (GetAwaiter) — Task,
  ValueTask, WinRT `IAsyncOperation<T>`, and custom awaitables, with zero per-type compiler support.** Previously
  facadegen injected `.await()` only for the BCL Task family and bir2cir hardcoded the `TaskAwaiter` dance. Now a type is
  awaitable IFF it has a conforming `GetAwaiter()` — a public parameterless instance MEMBER **or** a referenced
  `[Extension] static GetAwaiter(this X)` — returning an awaiter with `bool IsCompleted`, `T GetResult()`, and
  `INotifyCompletion`. **facadegen** (`EmitAwaitables`) pattern-detects every surfaced .NET awaitable and injects
  `suspend fun X.await(): <Result>` (result = the awaiter's `GetResult()` return, `void`→`Unit`; the `captureContext`
  opt-out param only for a Task-like awaitable exposing `ConfigureAwait(bool)`). **bir2cir**
  (`SuspendColdLowering.EmitAwaitPoint` via the new `ReferenceMetadataIndex.ResolveAwaitable`) discovers the awaiter type
  + members from ref metadata and emits the SAME awaiter dance generically — a member GetAwaiter as `clrInstance`, a
  GENERIC extension GetAwaiter (the WinRT shape) as `MyExt.GetAwaiter<TResult>(x)` (`clrGenericStatic`, the method type
  arg unified from the concrete receiver). ilemit gains no await knowledge. The Task/`ConfigureAwait` (§4a #3/#7) paths
  are preserved as one instance of the pattern; `OnCompleted` (INotifyCompletion) is bound rather than
  `UnsafeOnCompleted` (the cold core flows ExecutionContext via OnCompleted). New gate cases `cases/il-valueawait`
  (ValueTask<T>, member GetAwaiter) and `cases/il-extawait` (a generic-extension custom awaitable — sync fast path +
  genuine suspend/resume). Contract in `docs/dotkt-semantics.md §4c`.

- **bir2cir/stdlib (#7): `Task.await()` resume now honors a `ContinuationInterceptor` — interceptor > captured
  SynchronizationContext > inline.** Part B of the await-resume precedence work (Part A was #3's
  `await(captureContext)` SyncContext capture). The stdlib cold-core `ContinuationImpl.intercepted()` (formerly a v1
  identity stub) now implements the real JVM protocol: it consults `context[ContinuationInterceptor]` and wraps `this`
  via `interceptContinuation` (cached per SM), and `BaseContinuationImpl.resumeWith` calls the new
  `releaseIntercepted()` on state-machine termination. bir2cir's `SuspendColdLowering` routes the await-point
  `OnCompleted` callback through `this.intercepted().resumeWith(...)`, so an installed interceptor (a Kotlin dispatcher,
  e.g. a UI dispatcher) OWNS the resume dispatch and takes precedence over the raw SynchronizationContext capture;
  absent an interceptor the #3 captured-SyncContext (or `captureContext=false` inline) fallback is unchanged. Also
  fixed a context-propagation gap: a named-fun cold-entry state machine now threads `_context = completion?.context`
  (via the 1-arg `ContinuationImpl(completion)` base ctor, replacing a pinned-`null` 2-arg form that made every
  named-fun SM's context `EmptyCoroutineContext`), so an interceptor at the coroutine root is honored at a nested-fun
  await too. New gate case `cases/il-awaitintercept` (interceptor precedence + the two non-interceptor fallbacks);
  contract in `docs/dotkt-semantics.md §4a`. (The interceptor impl's `get_key()` carries the pre-existing GitHub #2
  formal-only `Key<Element>` covariance ilverify finding — runtime-safe, the run lane passes.)

- **bir2cir/kotc (#8): an oblivious VALUE-type platform member no longer collapses to `Nullable<T>`.** A
  facadegen-injected `[MaybeNull]` value getter (`ThreadLocal<Int>.Value`) is a platform type `Int!`
  (`ConeFlexibleType(Int, Int?)`), but on Kotlin 2.4.0 the `@kotlin.internal.ir.FlexibleNullability` IR marker only
  survives when Fir2Ir is given a non-null `specialAnnotationsProvider` — kotc passed `null`, so the flexible type
  collapsed to a plain `Int?` indistinguishable from a genuine user `Int?` and lowered to `System.Nullable<Int32>`
  (wrong: reading unset garbage / invalid IL). kotc now installs `JvmIrSpecialAnnotationSymbolProvider` on the app
  Fir2Ir path so the marker rides the flexible IR type; `BirEmitterTypes.birType` reads it to emit `{t:oblivious}`
  (and excludes it from the `Nullable<T>.Value` unwrap); and `bir2cir`'s re-landed `TypeNode.Oblivious` case lowers it
  to the BARE inner (value `Int!` → `int32` default `0`; reference `String!` → a bare NRT-oblivious ref) in every
  build. So `ThreadLocal<Int>().Value` reads `0` when unset (a value-type platform default — see
  `docs/dotkt-semantics.md §9a-bis`), `== null` is statically false, and the reference twin (`ThreadLocal<String>`,
  #143) is unchanged. The klib metadata path keeps `specialAnnotationsProvider = null` (no interop markers there).

### Packaging

- **Any non-stdlib DotKt reference now reaches bir2cir/ilemit in a `Library` project** (#132-general), and a new
  gate covers the packaged nupkg-resolution path (#130). The shipped `DotKt.Toolchain.targets` fed both bir2cir and
  ilemit from `@(ReferenceCopyLocalPaths)` (the copy-local runtime set) plus a hard-coded special-case for the two
  stdlib dlls. For `<OutputType>Library</OutputType>` a `PackageReference`'s runtime dll is NOT copy-local (it is
  transitive — the consuming app pulls it), so any OTHER DotKt package/project reference (an MPP/kotlinx-style
  library, a .NET package) was absent from the copy-local set and never reached bir2cir/ilemit → the emit failed
  with unresolved types. (`Exe` output masked it: package refs are copy-local for an Exe.) The two layers are now
  each sourced for what they actually need:
  - **bir2cir** reads compile-time METADATA (a metadata-only reflection load), so it takes EVERY non-framework
    reference from `@(ReferencePath)` — the @Clr-metadata reference stdlib, the runtime stdlib, and every user
    package/project/explicit reference (a package's method-body-less `ref/` assembly is fine for metadata).
    Framework/targeting-pack assemblies are excluded by `%(ReferencePath.FrameworkReferenceName) == ''`
    (empirically the reliable .NET 10 marker — `ReferenceSourceTarget` is `ResolveAssemblyReference` for BOTH
    framework and package refs, so it does NOT discriminate).
  - **ilemit** emits real CIL and must load each type's RUNTIME (implementation) assembly — a package's `ref/`
    assembly has no method bodies and deriving a class from one fails ("does not have an implementation"). It keeps
    taking `@(ReferenceCopyLocalPaths)` (the runtime set; framework assemblies and the `Private=false` reference
    stdlib are never copy-local), and the targets now set `CopyLocalLockFileAssemblies=true` so a Library's
    transitive package runtime dlls join that set — reaching ilemit for a Library exactly as they already did for
    an Exe.

  The copy-local glob's per-name stdlib special-cases are removed. New gate `scripts/verify-packaged-sdk.sh` packs
  the 5 nupkgs to a local feed (isolated `globalPackagesFolder`, local-only source — no cache masking) and drives
  three throwaway projects through real nupkg resolution: an `Exe` (build+run), a `Library` consuming a second
  DotKt library via `PackageReference` (the #132-general reproducer — RED before the fix, GREEN after), and a
  `DotKt.Sdk.Mpp` multiplatform Exe (build+run).

### Fixed

- **Reading a value-type nullable array element back across a generic class boundary no longer fails ilverify
  (#4).** A generic `class Box<T>` whose field is `Array<Ref<T?>>` (with `class Ref<X>(val v: X)`) has its
  `Nullable(Tv)` element erased to `Ref<object>` on the declaration side (`object` is the only uniform CLR storage
  carrying a real null for both a reference and a value `T` — the #142 design). But kotc stamps a CALL across the
  boundary (`Box<Int>().a[0]`) with `T` already substituted → `Ref<Nullable(Int)>`, which lowered to the
  irreconcilable `Ref<Nullable<int32>>` where the member actually returns `Ref<object>` (the two are unrelated
  invariant reified generics — no `castclass` reconciles them), so the element read / slot store failed with
  `ilverify StackUnexpected [found ref 'Ref`1<object>'][expected ref 'Ref`1<Nullable`1<int32>>']`. A new bir2cir
  pass (`NullableTvErasureCallRealign`) re-derives each such call's return by substituting the owner's type-args
  into the `EraseNullableTv`-applied declaration — gated to the exact object-erasure boundary, so a directly-written
  `Ref<Int?> = Ref(7)` (whose `Ref` declaration has no `Nullable(Tv)`) is untouched — and flows the corrected
  `Ref<object>` receiver through a per-method type-env so a chained `…[i].v` re-stamps `get_v`'s owner (`Ref<object>`)
  and return (`object`) too; a value-typed consumer (`val x: Int? = …[i].v`) gets an `unbox.any` `cast` back to
  `Nullable<int32>`. Part of the value-type-array-nullability read family (#113/#117/#120/#142). Gate:
  `cases/il-genarrlam` extended to read the element value for both a value-type (`Box<Int>`) and a reference
  (`Box<String>`) element.
- **Overriding a .NET virtual whose delegate parameter has an `object`/`Any?` `Invoke` no longer fails with
  `overrides nothing` (#1).** facadegen mapped a delegate whose `Invoke` takes/returns `object` (e.g.
  `SendOrPostCallback.Invoke(object)`) to a bare `Any?` — so a natural Kotlin override
  (`class MyCtx : SynchronizationContext() { override fun Post(cb: (Any?) -> Unit, state: Any?) }`) could not match the
  injected member. The delegate now faithfully surfaces as a **function type** `(Any?) -> Unit`. Behavioral note: a .NET
  member overloaded on two adjacent-arity delegates (`Thread(ThreadStart)` = `() -> Unit` /
  `Thread(ParameterizedThreadStart)` = `(Any?) -> Unit`) now needs an explicit lambda arity — `Thread({ -> … })` vs
  `Thread({ x -> … })` — since a no-arrow `{ … }` matches both (docs/dotkt-semantics.md §8e). Gate: `cases/il-delegobj`.
- **`Task<T>.await(captureContext: Boolean = true)` — opt out of `SynchronizationContext` capture (#3, Part A).**
  `await(captureContext = false)` now lowers to `task.ConfigureAwait(false).GetAwaiter()` (the
  `ConfiguredTaskAwaitable[`1].ConfiguredTaskAwaiter` awaiter, whose `OnCompleted` does NOT capture the
  `SynchronizationContext`); `await()` / `await(captureContext = true)` keep the historical capturing `TaskAwaiter` path.
  facadegen surfaces the const-default param on both await overloads; bir2cir's `EmitAwaitPoint` reads the literal and
  selects the awaiter family (a dynamic, non-const `captureContext` is refused loudly). Gate: `cases/il-cfgawait`
  (non-generic, sync fast path).
- **The GENERIC `Task<T>.await(captureContext = false)` path now resolves its nested-generic awaiter (#3).** The
  ConfigureAwait(false) awaiter for a `Task<T>` is the nested struct
  `System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter`, whose generic arity backtick rides
  the OUTER type — so the FQN already carries a `` ` ``. ilemit's `ConstructGeneric` unconditionally appended a SECOND
  arity suffix, yielding `…ConfiguredTaskAwaiter`1`, which `ResolveType` could not find (`cannot resolve .NET type`).
  It now skips the append when the name already contains a backtick (the name is already arity-complete). Gate:
  `cases/il-cfgawaitgen` (generic `Task<Int>.await(captureContext = false)`, sync fast path).
- **A star projection `Key<*>` of a self-ref-bounded generic (`interface Key<E : Element>`) no longer lowers to the
  constraint-violating `Key<System.Object>` (#2).** kotc's star-projection rule discards the bound (`at == null ->
  OBJ`, `kotlin.Any`), so `Key<*>` reached bir2cir as `Key<kotlin.Any>` → `Key<System.Object>` — but `System.Object`
  does not satisfy `E : Element`, an illegal reified CLR instantiation (the stdlib `get_key(): Key<*>` methodimpl no
  longer matched its interface declaration when an app subclassed `AbstractCoroutineContextElement` and forced the
  loader; an app `override val key: CoroutineContext.Key<*>` emitted the constraint-violating `Key<object>` directly).
  A new bir2cir pass `StarProjectionBoundLowering` (pre-BirTypeLowering, ALL builds) reads the type-param CONSTRAINT
  metadata — bir2cir's lane — and repoints the objectish arg to the type-param BOUND: `Key<*>` -> `Key<Element>`. It
  resolves the bound for a REFERENCED owner via `ReferenceMetadataIndex.TvBound` (the ref.dll generic-parameter
  constraints, newly captured and keyed by the dotted FQN with nested `+`->`.` normalization) AND for the stdlib's own
  in-assembly owner via the type declarations' `typeParams[].constraints` (collected across all staged BIR roots).
  `Key<Any>` is never valid Kotlin on a bounded param, so an objectish arg there unambiguously came from a star
  projection — the rewrite is safe (an unconstrained `List<Any>` is untouched). A self-referential F-bound
  (`E : Enum<E>`) is left unsubstituted (no valid closed generic; finite by construction). This makes the ref.dll,
  rt.dll, and app views of the signature agree.
- **ilemit now forwards/implements a GENERIC default-interface-method without erasing its method type parameter, so
  the coroutine-context hierarchy loads and dispatches (#2 part-2).** The inherited generic DIM
  `get<E : Element>(key: Key<E>): E?` was handled three ways that each broke a `CoroutineContext` subclass/impl, all
  fixed in the CLR codegen lane:
  - The class DIM-forward bridge (`TryEmitDimForwardBridge`) emitted a NON-generic body (E erased to `object` →
    `Key<object>` + a generic-arity methodimpl mismatch → `TypeLoadException`). It now emits a GENERIC bridge
    (`DefineGenericParameters` mirroring the DIM's arity + constraints, `MakeGenericMethod` on the forwarded target),
    and skips the redundant self-forwarder when the found default IS the very slot being filled (which would recurse).
  - An emitted interface whose DIM overrides an EMITTED base-interface method (`ContinuationInterceptor.get` over
    `Element::get`/`CoroutineContext::get`) carried no methodimpl on the base slots, so every implementer failed to
    load (`Method 'get' … does not have an implementation`). A new pass (`Emitter.DimImpl.cs`) emits a private-final
    methodimpl bridge for each inherited emitted-base slot (transitively; signature sourced from the BASE decl so a
    covariant/constrained override stays legal), mirroring the existing external-base path.
  - A `callInstance` to an interface method inherited through an emitted class's EXTERNAL base
    (`e[key]` → `AbstractCoroutineContextElement`'s `get<E>`) was unresolved because the reflected lookup searched
    only a class's own members, not its implemented interfaces. `FindReflectedMethod` now falls back to the
    transitively-implemented interfaces for a class receiver too. Gates: `cases/il-coctxkey`, `cases/il-cointercept`.
- **`Array(size){ mk<T?>(null) }` inside a generic class is now ilverify-clean — no spurious `[DelegateCtor]
  Unrecognized arguments` (#142).** When an `Array(size){…}` init-lambda inside a generic class returns a
  CONSTRUCTED-generic whose type-arg is a nullable type-var (`Ref<T?>`), bir2cir's `NullableGenericReturnErasure`
  erases `Nullable(Tv)` to `object` in the method-return and array-element positions — but `EraseNullableTv`'s
  `Fn` arm passed the function-type RETURN (`fn.Ret`) VERBATIM, an over-broad carve-out meant only for the
  top-level `(...)->T?` hand-off to `NullableFuncReturnErasure`. A nested `Ref<Nullable(Tv)>` return is an `Fqn`,
  so it survived that carve-out ONLY in the `newDelegate.funcType.ret` position; `ReferenceNullableStrip` then
  stripped the surviving `Nullable(Tv)` to a bare `!T`, leaving the delegate funcType.ret `Ref<!T>` internally
  contradictory with the `__lambda0` ldftn-target signature `Ref<object>` → ilverify `[DelegateCtor]` in the
  generic class ctor. The carve-out is now narrowed to a TOP-LEVEL `Nullable(Tv)` return only, so a nested
  constructed-generic return erases to `Ref<object>` consistently and funcType / method-signature / array-element
  agree end-to-end. New gate case `il-genarrlam` (run + ilverify).
- **A value-returning infinite loop (`fun f(): Int { while(true){ … return x } }`) is now ilverify-clean — no
  spurious `ReturnMissing` (#141).** bir2cir CFG-lowers `while(true)` to a `brfalse end` on a constant-true
  condition, so the loop-exit label stays STATICALLY reachable. ilemit's method-body emitter unconditionally
  appended a bare trailing `ret` at method end — valid for a `void` method and for a genuinely-unreachable
  non-void tail (dead code, ilverify skips it), but the reachable-yet-never-taken infinite-loop tail with an
  empty stack in a non-void method tripped ilverify `ReturnMissing` (the JIT ran it fine). `EmitMethodBody` now
  appends `default(ret); ret` (a new `EmitTrailingRet`: `ldloca/initobj/ldloc` for value types and generic
  params, `ldnull` for reference types — the same split as `case "default"` and the `unbox.any` rule) so the
  unreachable terminator is stack-valid whether reachable or not; it never actually executes. Seen in atomicfu
  `loop`/`getAndUpdate`/`updateAndFetch` and a `NativeMutex` inner loop. New gate case `il-infloopret` (run +
  ilverify).
- **A `suspend fun f(): Nothing` now round-trips its Nothing return through a re-consumed DotKt assembly (#151).**
  bir2cir's `SuspendColdLowering.BuildBridge()` builds the `Task<Nothing>` async bridge from `suspendRet` (= `kotlin.Nothing`),
  but the inner Nothing erases to `object` (Nothing has no CLR analog), so `BirTypeLowering`'s own bare-Fqn `IsNothingRet`
  check could not see it on the `Task<...>` return and `RoundtripMetadata` never stamped `[KotlinNothing]`. `BuildBridge`
  now carries the pre-erasure `retNothing` fact onto the bridge return (both the abstract-member signature and the concrete
  TCS-driven body), so `RoundtripMetadata` emits `[KotlinNothing]` and the merged facadegen reader (#135) restores the
  `suspend fun f(): Nothing` return on re-consumption — `blockOn { if (c) x else sfail() }` keeps the lambda `suspend () -> Int`
  instead of widening to `Any?`. (`verify-roundtrip.sh` section `roundtrip-nothing-suspend`, previously RT_XFAIL, now GREEN.)

- **A genuinely-nullable `String?` value passed unwrapped into a `CharSequence?`-receiver slot is now ilverify-clean (#156).**
  On the STRICT nullable-slot path, bir2cir's `StringCharSequenceBridge` deliberately left a `String? = null` value RAW to
  preserve null (`z.isNullOrEmpty()`), but a raw `String` assigned into a `dotkt$CharSequence` interface slot is
  ilverify-unsound (String does not implement the synthetic adapter interface) — it only ran because a null short-circuits
  `isNullOrEmpty`. The bridge now emits a runtime-conditional wrap on that path — `v == null ? (dotkt$CharSequence)null :
  new dotkt$StringCharSequence(v)` (bindOnce: a side-effecting subject is bound to a temp so it runs exactly once) — so the
  slot receives a genuine adapter or a typed null: ilverify-clean and null-preserving. (Gate: `cases/il-nullcs`.)

- **A generic .NET extension over a value-type constructed-generic receiver miscompiled the receiver (#157).** `class Cell<T>`
  + `Peek(this Cell<int>)` called on an inferred `val c = Interop.Cell(40)` returned garbage (2) instead of 41. Root cause:
  facadegen faithfully types the un-annotated ctor param `Cell(T v)` as an oblivious type-variable (`T!`), which kotc's
  `ClrTypeInjection.coneOf` mapped to a `ConeFlexibleType(T, T?)`; a flexible PARAM biases inference of `Cell(40)` toward
  the strict nullable upper bound `Cell<Int?>` (the `@FlexibleNullability` erased before the backend — it never reaches
  BIR), so the receiver was constructed as `Cell<Nullable<int32>>` while the extension parameter is the layout-distinct
  `Cell<int32>` — an unverifiable, type-unsafe call reading garbage field bytes. Fix (kotc, `ClrTypeInjection`): an
  oblivious type-VARIABLE in an INPUT/value-parameter position resolves to the BARE type variable (`Cell(40)` -> `Cell<Int>`,
  the value arg reified invariantly, matching the .NET member slot). The resolution is position-scoped: a `[MaybeNull] T`
  in an OUTPUT (return/getter) position stays flexible, so platform-type null-checkability is preserved (#143,
  `ThreadLocal<T>.Value`). `oblivious` remains frontend-only — kotc never emits a `TypeNode.Oblivious` to BIR, so the
  earlier speculative bir2cir `BirTypeLowering` Oblivious->bare-inner lowering was removed as dead. Gate: `cases/il-genextval`.

- **A delegate type-arg's nullability now survives into the injected Kotlin lambda param/return (#150).** facadegen
  built a delegate's `fn` node (`Action<T>`/`Func<T>`) with a plain `MapT` per type arg — no access to the member's
  flattened `[Nullable]` byte array — so an `Action<string?>` param surfaced its lambda arg as forced non-null and a
  `Func<string?>`-returning method surfaced its lambda return as non-null (a lambda returning null would not compile).
  This is the contravariant sibling of #143 (`ThreadLocal<T>.Value`, a covariant/return position). facadegen now
  threads the member's `[Nullable]` byte array with a preorder POSITION cursor (`MapTN`) that matches Roslyn's exact
  flattening — reference/type-param/array/generic-struct positions consume a slot, simple value types and `Nullable<T>`
  do not — and applies the tri-state NRT wrapper (`T`/`T?`/platform `T!`) to each `Action`n`/`Func`n` type arg, at any
  nesting depth (`Func<Func<string?>>`, `Func<string?,int,string>`). An unannotated arg in an oblivious assembly
  surfaces platform `T!`; a `[Nullable]=2` arg surfaces `T?`. Non-delegate positions keep their existing bare
  structure (the outer NRT is still folded by the caller's `ApplyNrt`), so the change is confined to delegate
  internals. New gate `il-delegnull` (`il_check_inject_nrt` builds the sample's runtime.cs with C# NRT enabled).
- **The `System.IComparable` arity clash + base-interface-chain value-type slots (#129) are confirmed by-design and
  now gated + documented.** Investigation (paired with the design reviewer) established there is no facadegen (or kotc)
  code fix warranted: a Kotlin classifier cannot be arity-overloaded (K2 hard limit, `docs/dotkt-semantics.md` §8d),
  so `import System.IComparable` + the natural `IComparable<Ver>` spelling resolving to the non-generic (arity-0) member
  is the documented projection, not a bug. Implementing the generic member uses the arity-qualified `IComparable1<T>`
  with the VERBATIM .NET surface (`override fun CompareTo(other: Ver?): Int`); for Kotlin comparability the idiom is
  `kotlin.Comparable<T>` (it emits both CLR IComparable faces via bir2cir's ComparableBridgeSynthesis). The
  base-interface-chain value-type case (`class Cell : IMid<Int>` inheriting `Get(): Int` through `IBase<T>`) already
  works via #128's value-type-slot bridge across the transitively-inherited link. Two new gates lock both paths
  (`il-icmparity`: the arity-family generic-interface implement + upcast dispatch; `il-ifacechainvt`: the value-type
  base-interface chain); `docs/dotkt-semantics.md` §8d gains the implement-an-injected-interface guidance.
- **Two same-name/same-arity top-level extensions on DIFFERENT receiver types (parallel `*Extensions` static classes in
  one namespace) now each bind to their OWN receiver's class — no silent mis-bind to the first candidate (#144).** A
  facadegen-injected C#-origin `[Extension]` method surfaces as a Kotlin top-level extension fun keyed by
  `CallableId(package, name)`. Two static classes in one namespace declaring a same-name, same-arity extension on
  different receiver types (`FooExt.Tag(this Foo)` + `BarExt.Tag(this Bar)`, or `System.Linq` `Enumerable.Where` vs
  `Queryable.Where`) both injected that one CallableId; kotc's `clrInjectedTopLevelFileClass` disambiguated candidates by
  VALUE-PARAM ARITY ONLY, so a same-arity collision arbitrary-picked the first candidate → `bar.Tag()` silently bound to
  `FooExt` (wrong static, wrong result). The disambiguation now keys on the resolved callee's extension-RECEIVER type,
  identified by its classifier **ClassId** (`TopLevelSig.receiverKey` on the metadata side via `receiverClassifierClassId`,
  the resolved `IrType.classId` on the backend side) — the same ClassId `coneOf` produced from the metadata, so the two
  match by construction across facadegen's name vocabulary (a bare `String`, a namespace-less generic `Box`, a
  primitive-array element — where a raw type-name compare would diverge). Receivers that share a classifier but differ
  only in type args, or an `Any`/`ClrRef`/type-variable receiver, degrade to the pre-existing arity match (never a wrong
  pick). New gate case `cases/il-csextrecv` (plain-class + primitive `this string`/`this int` receivers).

- **An `open`/`override` instance method of a DotKt library consumed AS KOTLIN now dispatches virtually — no
  `KeyNotFoundException` / mis-dispatch when the call reaches ilemit un-reshaped (#139).** kotc's .NET-interop
  `callInstance` path (a facadegen-reinjected owner) emitted NO `virtual` flag. When bir2cir resolves the owner off
  the `--ref` DotKt assembly it reshapes the node to a `clrInstance` (where `virtual` is moot), which masked the gap
  in the round-trip gate. But when bir2cir CANNOT resolve the owner (an asymmetry: kotc's `clrName` resolved it from
  the facadegen injection metadata, bir2cir's `ResolveNetType` did not), the RAW `callInstance` reaches ilemit, which
  read `virtual` UNCONDITIONALLY → `KeyNotFoundException`; and even null-tolerant, a defaulted non-virtual `call` on
  an `open`/`override` member mis-dispatches (e.g. `d.sound()` on a base-typed reference printed the base result).
  kotc now stamps `virtual` (`modality != FINAL || overrides`) on every `.NET`-interop `callInstance` — matching the
  plain Kotlin member-call path — and ilemit reads it null-tolerantly (`IsVirtual`, defaulting FALSE) at the three
  `callInstance`/`newBoundDelegate`/`newBoundClrDelegate` sites. New gate section `roundtrip-virtual-dispatch` covers
  the BIR (`virtual` present), the reshaped `clrInstance` path, and the raw-`callInstance`-into-ilemit fallback +
  ilverify.

- **`import System.X` BCL-injection coverage + NRT fidelity: `ThreadLocal<T>.Value` now surfaces as a platform type,
  and static `RuntimeHelpers.GetHashCode(object)` injects (#143).** (facadegen) Two gaps the coroutines/atomicfu port
  hit. (a) NRT fidelity: `ThreadLocal<T>.Value` was injected as a non-null `T` even though its getter carries
  `[MaybeNull]` (it returns `default(T)`=null when unset) — a consumer's `if (x == null)` was flagged 'always false',
  a latent NPE. facadegen now folds an OUTPUT-position `[MaybeNull]`/`[NotNull]` flow contract: `[MaybeNull]` demotes a
  non-null node to a platform type `T!` (value-type-safe — `ThreadLocal<Int>.Value` is `default(int)`=0, never null —
  where a forced `T?` would be wrong), read from BOTH the property and its getter's return parameter (the BCL uses both
  placements); the contract is applied at return/property positions ONLY, never flipping a param's input type. (b) The
  over-broad `OBJECT_MEMBERS` name-skip dropped `RuntimeHelpers.GetHashCode(object)` — a distinct public STATIC method —
  because its name matches Kotlin `Any`'s instance member; the skip now applies to instance members only (a static
  method never collides with `Any`), with the inherited `System.Object` statics still dropped by declaring type, and the
  same narrowing mirrored into the FIR-injection closure walk so a newly-surfaced static's referenced types are enqueued.
- **A companion-static `fun f(): Nothing` return now round-trips (does not widen to `Any?`) when consuming a DotKt
  assembly as Kotlin (#135, extends #133).** (facadegen) The `[KotlinNothing]` reader covered only the plain
  method/getter return; the companion-static loop read returns via raw `MapRetT` (`kotlin.Nothing` erased to `object` →
  `Any?`), so a consumer's `val r: String = if (c) x else Boom.boom()` widened to `Any?` and failed to compile. The
  companion-static loop now routes returns through the same `RetTypeSfxN` reader (restoring `Nothing` + folding NRT) the
  instance/interface/top-level loops use, and `SuspendRetNode` is extended to read the marker before the `Task` unwrap
  (the reader half of the suspend-return path; the suspend-return end-to-end round-trip additionally needs the bir2cir
  producer to stamp `[KotlinNothing]` on the suspend Task-bridge — tracked as the `roundtrip-nothing-suspend` XFAIL).

- **A NON-CONSTANT default parameter (`= {}` / a simple expression) is now preserved across the DotKt-as-Kotlin
  cross-module round-trip (#146, Avalonia report E(a); extends #134 to non-const defaults).** Consuming a DotKt
  library as Kotlin, a library function with an empty-lambda default — `fun column(configure: Panel.() -> Unit = {},
  build: Panel.() -> Unit)` (THE Avalonia DSL idiom, composed with #145's receiver lambda) — called `column(build =
  {…})` failed the consumer's kotc frontend with `no value passed for parameter 'configure'`: #134 carried only a
  CONSTANT default value, so a `= {}` / `= emptyList()` default surfaced as a required param. Now the SAME
  `@KotlinDefault` mechanism carries a non-const default as a CLOSED BIR sub-tree — a non-capturing lambda's lifted
  method rides a `defaultCarrier` envelope so it is self-contained cross-module; facadegen marks the injected param
  OPTIONAL (a `nonConst` default) so the frontend accepts the omission; and bir2cir's `DefaultArgSplice` now runs at
  PHASE 1 (right after `InlineSplice`, before owner attribution/type lowering) and fills the omitted slot ownerlessly
  (by name+arity), RE-HOISTING a carried lambda into the consumer's file class under a fresh name so ilemit's
  assembly-local `ldftn` resolves it and it re-lowers in the app's context. The empty-lambda default fills to `{}`.
  Covered: an empty receiver/plain lambda `= {}`, a simple-expression default (`= emptyList()`); a capturing / SAM /
  suspend-lambda default is refused loudly (a `defaultUnsupported` poison carrier) rather than miscompiled. New gate
  section `roundtrip-nonconst-default`.

- **`String.split`/`replace`/`substring` on a COMPUTED (non-const/local) receiver no longer crashes with
  `EntryPointNotFoundException` at `dotkt$CharSequence.subSequence` (#148, the residual of #92).** A `kotlin.text`
  CharSequence extension (`split`/`replace`/`substring`/…) on a `String` receiver requires bir2cir to adapter-wrap
  the receiver into the synthetic `dotkt$StringCharSequence` (a real `subSequence`/`get_length` body); an UNwrapped
  String reaches the `dotkt$CharSequence` slot raw and the extension's virtual interface call hits the body-less
  synthetic method → `EntryPointNotFound` at runtime. The `StringCharSequenceBridge`'s static-String detector only
  recognized const/local/param + a `ret`-carrying call, so a String from a **property getter** (`cfg.body`), an
  **app top-level fun result** (`load()`), a **`!!`/elvis** result, or a **map indexer** (`m[k]!!`) — none of which
  carry a `ret` on their BIR node — was left unwrapped (a literal/local receiver was fine, which is why it read as
  "literal vs BCL-origin"; the true trigger is const/local vs any computed expression). Fixed by routing the detector
  through the shared `StaticType.Surface` static-type resolver (#59), so every String origin is classified uniformly
  (a ret-less `callInstance`/`callStatic` resolves its return type from the ref.dll or the THIS-file file class; a
  `!!`/elvis `valueBlock` resolves through its result; a nullable-String value into a NON-nullable CharSeq slot —
  frontend-guaranteed non-null — is peeled and wrapped). Gate: `cases/il-charseqbcl` (property-read / app-fun-result /
  `!!` / `StringBuilder.toString()` receivers into split/replace/substring — the BCL-origin path #92 left un-gated).
- **Four more CharSequence wrap-site residuals no longer crash with `EntryPointNotFoundException` (#149, the coverage
  #148 explicitly left open).** After #148 routed the static-String detector through `StaticType.Surface`, four
  receiver shapes still reached the body-less `dotkt$CharSequence` slot unwrapped:
  - **A CROSS-FILE receiver** — a `String`-typed user-class property (`c.body`) or top-level fun (`banner()`) declared
    in a SIBLING `.kt` of the SAME assembly. `StaticType.LocalTypes` is PER-FILE, so such a receiver resolved in
    neither the current file's types nor the ref.dll → unwrapped. bir2cir now aggregates EVERY input file's declared
    types + file classes ONCE before Phase 1 (`StaticType.GlobalTypes`/`GlobalFileClasses`), and `LocalMemberType`
    consults it as an assembly-wide fallback (a cross-file member return / owner=null top-level fun now resolves).
  - **A StringBuilder receiver** (`sb.split(...)`) — a non-String `CharSequence` that does NOT implement the synthetic
    interface: it is snapshot to a `String` via the null-safe `kotlin.LibraryKt.toString` and then adapter-wrapped.
  - **A String BRANCH of a polymorphic `CharSequence` if/else** (`(if (c) "a-b" else cs).split(...)`) — the whole
    `cond` unifies to `CharSequence`, so it was not itself wrapped; the coercion now DESCENDS into `then`/`else` and
    wraps each String branch while leaving a genuine-CharSequence branch.
  - **`x!!.isNullOrEmpty()`** — a nullable `CharSequence?` slot with a `!!` value stayed on the strict (unwrapped)
    path. A `!!` non-null assertion is now recognized STRUCTURALLY (its `then` reads the same local the condition
    null-checks) and is peel-safe even into a nullable slot (it is provably non-null or throws).
  Gates: `cases/il-charseqxfile` (a MULTI-FILE case — cross-file property + top-level-fun receivers into split) and
  `cases/il-charseqmore` (the cond-branch, StringBuilder, and `!!.isNullOrEmpty()` residuals).
- **Deeply-nested inlined lambdas/blocks in one function no longer crash the pipeline with a JSON depth error**
  (#147). A function with enough nested inline lambdas/blocks produces a BIR (and derived CIR) whose method-body
  JSON nests deeper than System.Text.Json's default `MaxDepth` of 64 — legal Kotlin that hard-crashed bir2cir with
  `maximum configured depth of 64 has been exceeded`. Raised the bound to 1024 on EVERY BIR/CIR read AND
  full-document write site via a shared `DotKt.Bir.BirJson` helper: bir2cir's BIR readers (`Program.cs` main +
  merge loop, `DefaultArgSplice`) and its indented CIR writer, ilemit's CIR readers + the file-class merge
  re-parse/re-serialize (`Program.cs`), and the shared `TypeNode`/`BirCarrier` parse path in `bir-common`.
  (Verified: a 20-deep `run { }` nest yields a 109-level BIR JSON — `JsonDocument.Parse` at the default throws at
  exactly that input, at 1024 it parses and the whole pipeline compiles+runs.)

- **A Kotlin lambda passed to a GENERIC BCL delegate ctor param over a user type now materializes as the target
  delegate** (#140, report P3). `System.Threading.ThreadLocal<Box>({ Box(42) })` — where `Box` is an app-emitted
  class — passes the lambda to `System.Func`1<Box>`, but the value on the stack stayed the internal
  `DotKt.Runtime.CompilerServices.KFunc`1<Box>` -> `ilverify StackUnexpected`. Root cause: because `Box` is a same-
  assembly TypeBuilder, the constructed `ThreadLocal<Box>` is a TypeBuilderInstantiation whose ctor ilemit resolves on
  the OPEN definition, then emitted the delegate arg against the OPEN param `Func<T>`; a `want` still mentioning an
  open generic parameter skips the lambda->delegate rewrap (there is no concrete ctor to bind), so the lambda self-
  built as its own `KFunc`1`. Fixed by substituting the instantiation's concrete type args (`T -> Box`) onto the open
  ctor's param types before emitting the args, so the delegate target is the closed `Func`1<Box>` (a TypeBuilder CLASS
  arg, still rewrappable via `TypeBuilder.GetX`) and the rewrap fires. Covers the whole generic-BCL-delegate family
  (`Func<T>`, `Action<T>`, and multi-arg shapes) since the substitution is arity-generic; the non-generic and BCL-typed-
  arg cases were already correct (no TypeBuilderInstantiation, so the closed ctor param was used directly). New
  `il-gendelegate` gate sample (ThreadLocal = Func<T>, Progress = Action<T>): run + ilverify-clean.

- **A receiver-lambda parameter `P.() -> Unit` keeps its receiver when a DotKt library is consumed AS KOTLIN
  cross-module** (#145, Avalonia report E(b)). A `fun apply1(block: Panel.() -> Unit)` param is a Kotlin RECEIVER
  function type — the lambda body gets an implicit `this: Panel`, so `apply1 { margin = 4 }` resolves `margin` to
  `Panel.margin`. Kotlin lowers `P.() -> Unit` to `Function1<P,Unit>` carrying the `kotlin.ExtensionFunctionType`
  annotation, then flattens the receiver to the delegate's first CLR arg (`KAction`1[Panel]`) — erasing the
  "was a receiver" bit, so a re-consuming assembly saw a receiver-less `(Panel) -> Unit` and the lambda body failed
  with `unresolved reference 'margin'`. Fixed end-to-end by carrying the bit the same way the suspend-fn-type carrier
  (#28) does: kotc records it in the BIR `fn.recv` (detected off the IR `kotlin.ExtensionFunctionType` annotation,
  non-suspend only); bir2cir stamps a bare `[KotlinExtensionFunctionType]` marker on the delegate in every position
  (param/return/field/property) — the delegate itself is NOT erased (the receiver rides as its first CLR type arg, so
  no shape is carried, unlike suspend); facadegen moves the delegate's first arg back into `fn.recv` at each read site
  (param / getter-return / field / the shared property loops); and `ClrTypeInjection` restores
  `P.() -> R` as an `ExtensionFunctionType` cone so the consumer's lambda regains `this: P`. ilemit's delegate-shape
  readers (FuncType/SigToken/arity/mentions-tv) and the three pre-lowering bir2cir walkers (inline-splice HasTvType,
  value-type collection/array/nullable, CharSequence) now read the shared `TypeNode.Fn.DelegateParams` (receiver
  prepended) so the emitted delegate + overload token stay identical whether the receiver is kept in `recv` (a restored
  param type) or flat in `params` (a lambda-value closure) — the stdlib `apply`/`run`/`with` inline hot path is
  covered. New `roundtrip-receiver-lambda` gate section (top-level + member + multi-param mix).

- **A native/unmanaged `.dll` in the `--ref` set no longer aborts the bir2cir build** (#138, Avalonia report D).
  When an app copy-locals an unmanaged PE (Avalonia's `libSkiaSharp.dll` / `av_libglesv2.dll` /
  `libHarfBuzzSharp.dll` under `runtimes/<rid>/native/`), bir2cir's reference loader called
  `AssemblyName.GetAssemblyName` on it at the top level and hard-crashed the whole build with
  `bir2cir: PE image does not have metadata.`. The loader (`ReferenceMetadataIndex.Build`) now pre-checks each
  `--ref` for a CLI/CorHeader via `PEReader` (`IsManagedAssembly`) and SKIPs a non-managed PE with a one-line
  `bir2cir: skipping non-managed --ref <name>` diagnostic — a native dll carries no managed types, so skipping is
  always correct. ilemit's `--ref` load was already tolerant (`Assembly.LoadFrom` in try/catch). The packaging
  targets additionally keep native runtime assets off the `--ref` command line (`%(AssetType) != 'native'` plus a
  `/native/` path filter on both the bir2cir and ilemit ref item groups). A native-ref guard in `verify-il.sh`
  asserts the loader skips a real native PE and lowers successfully.
- **facadegen now surfaces C#-origin `[Extension]` methods as top-level Kotlin extension functions** (#137,
  Avalonia report B). A C# extension method (`public static int Twice(this W w)` in a static class `NS.Ext`)
  was only reachable via a per-member import (`import NS.Ext.Twice`, the `using static` analog); a
  namespace-style `import NS.*` (the Kotlin analog of C# `using NS;` — how Avalonia's fluent
  `UsePlatformDetect()`/`StartWithClassicDesktopLifetime()`/… startup surface is used) saw nothing, so
  `w.Twice()` failed with `unresolved reference`. facadegen now emits each `[Extension]` static ADDITIONALLY
  as a top-level extension fun in a `file` decl (pkg = the static class's namespace, fileClass = its FullName),
  binding through the same path DotKt `[KotlinFileClass]` top-level extensions already use — no bir2cir change
  needed (a `[Extension]` call is a plain static `NS.Ext.M(recv, …)` at the IL level). Non-generic, extra-param,
  and generic (`fun <T> Box<T>.Echo(): T`) receivers all resolve, run, and are ilverify-clean. Also fixes a
  latent crash: a generic `[Extension]` reused one `typeParams` JSON node across two emissions
  (`The node already has a parent`), silently dropping the whole static class. Gated by `verify-il.sh` `csext`.

- **Consume-as-Kotlin round-trip: an omitted cross-module DEFAULT argument is now filled from the facadegen
  metadata** (#134). Consuming a DotKt library as Kotlin, omitting a defaulted parameter anywhere but the
  trailing tail — a constructor `Pt(y = 4)` (omitting `x`) or a named-middle call `box(1, c = 9)` (omitting
  `b`) — dropped the omitted slot, so a later provided arg slid into the wrong parameter (or ilemit reported
  `no matching constructor … with 1 arg(s)`). fir2ir converts a facadegen-injected (bodies-skipped dependency)
  declaration's default value to an `IrErrorExpression`, so kotc had no value to fill. It now reads the real
  constant default from the facadegen metadata (`ClrMetadataHolder`, the same source the FIR injection's
  `applyDefaults` reads) and synthesizes an `IrConst` at the call site — for both constructors and top-level
  functions, keyed by resolved IR identity (owner `ClassId` / `CallableId` + regular-param count). A trailing
  omission still falls back to ilemit's `[DefaultParameterValue]` backfill. Gated by `verify-roundtrip.sh`
  `roundtrip-defargs` (now PASS: `greet`/`box`/`flags` named-middle + reordered, and `Pt(y=4)`/`Pt(x=7)`).

### Added

- **Non-null CONTRACTS on the public surface — parameter PRECONDITIONS + return POSTCONDITIONS** (#6). kotc now
  synthesizes JVM-Kotlin-style design-by-contract checks — reproducing `Intrinsics.checkNotNullParameter`, which is a
  JVM-BACKEND lowering (`JvmArgumentNullabilityAssertionsLowering`) absent from the Configuration→FIR→Fir2Ir pipeline
  kotc runs, so the IR carried none.
  - **Preconditions:** every PUBLIC or PROTECTED member (top-level fun, member fun, constructor, property setter,
    default interface method, `@PublishedApi internal`) checks each NON-NULL REFERENCE value parameter at entry:
    `if (p == null) throw NullPointerException("Parameter specified as non-null is null: <owner>.<method>, parameter
    <p>")`. A null crossing a boundary (a platform `T!`, an unsound cast, reflection ignoring NRT) fails fast at the
    entry instead of propagating to a later, mis-sited `NullReferenceException`.
  - **Postconditions (a DotKt addition beyond JVM Kotlin):** a NON-NULL REFERENCE return of a public/protected member
    (fun / getter / default interface method) is bind-checked at each `return` and throws if null — guarding a null
    leaking OUT via a platform type / unsound generic. Suspend fns are excluded (their body drives bir2cir's
    Continuation state machine).
  - Both are emitted as ordinary BIR (an `if`/`objEq`/`throw`, or a bind-check-throw `valueBlock` — the same null-check
    shape as `x!!`); `kotlin.NullPointerException` resolves to the BCL exception via the existing `@ClrTypeAlias` path
    (no CLR knowledge in kotc). Value types / nullable / `Unit`/`Nothing` / type-parameter-typed / receivers /
    private/internal/local/inline are skipped (see `docs/dotkt-semantics.md` §9c for the discriminator and the
    deliberate JVM deviations). Gated by `verify-il.sh` `nncontract`.

- **Consume-as-Kotlin round-trip: three generic-fidelity gaps fixed** (#133, surfaced by the atomicfu CLR port).
  In all three the facadegen symbol surface was already correct; the loss was DOWNSTREAM, each in one owning layer.
  Reproduced + gated by `verify-roundtrip.sh` `roundtrip-generic-inline-ext` / `roundtrip-generic-operator` /
  `roundtrip-nothing-return` (now PASS).
  - **kotc: a generic inline extension on a generic receiver** (`inline fun <T> Cell<T>.update(fn:(T)->T)` called
    `c.update { it + 1 }`) now splices cross-module. `BirEmitterInline.inlineSpliceCall` threads the extension
    receiver in `recvs.extension` (the same shape the owner-less splice uses); the owner stays the facadegen file
    class so bir2cir's owner-ful `ResolveInlinePayload` binds the `[KotlinInline]` body. Removed the
    `BirEmitterCalls` refusal of a facadegen inline call carrying a lambda AND an extension receiver.
  - **bir2cir: a Kotlin `operator get`/`set` on a generic DotKt type** (`class Arr<T> { operator fun get/set }`,
    used `r[1]` / `r2[0] = x`) now binds to the plain `get`/`set` method the Kotlin type emitted, instead of the
    BCL `get_Item`/`set_Item` indexer accessor the emitted type lacks. `NetInteropBinding` keeps the emitted method
    when the owner declares it and has no .NET indexer property.
  - **bir2cir + kotc: a Kotlin `Nothing` return** (`fun fail(): Nothing`) now round-trips. bir2cir records the
    pre-erasure fact (`BirTypeLowering`, alongside the `Nothing`→`object` erasure) and stamps a `[KotlinNothing]`
    return marker (`RoundtripMetadata`); kotc's `coneOf` resolves the restored bare `Nothing` node to
    `bt.nothingType`. So a consumer's `val y: String = if (c) "ok" else fail(...)` keeps `String` instead of
    widening to `Any?`. (facadegen's `RetTypeSfxN` reader landed earlier in #133.)

## 0.9.5 — 2026-07-13

0.9.5 bumps the Kotlin frontend to **2.4.0** (compiler + stdlib, behavior-preserving), lands **first-class
user-app multiplatform (`expect`/`actual`) support** — a `common`+`actual` project compiles to ONE
fully-actualized CLR dll through the app pipeline, opt-in via `<DotKtMultiplatform>true</DotKtMultiplatform>`
(design: `docs/design-ktproj-mpp.md`) — and sweeps a broad set of correctness bugs surfaced by the
kotlinx.coroutines CLR port and gate-coverage work: the **value-type-array-nullability family**
(`arrayOfNulls`/`copyOfRange`/`plus`/`plusElement`/`copyOf` now allocate `Nullable<T>[]` / runtime-element-type
arrays correctly), the **unsigned-value-type nullable family** (`!!` / `as?` / `if-else`-join), **inline
nested-generic arguments** that emitted invalid IL, `new` of an external generic over a free type-variable, a
**cross-module custom-accessor property that silently returned the raw field**, coroutine cancellation
fidelity, a bound `CharSequence` extension reference, a facadegen-injected `.NET` enum under a `T : Enum<T>`
bound, and a Kotlin class implementing a `.NET` generic interface with a value-type argument. Diagnostics now
point at `File.kt:line` with a shared IR-sanity gate. Every verify gate is green: verify-il 276/242,
verify-ktproj all-pass (incl. the new MPP sample), verify-schema clean, and the 2.4.0 bump matched the
JVM-oracle differential.

### Added

- **`DotKt.Sdk.Mpp` MSBuild SDK** (#125) — a thin composition SDK for Kotlin-Multiplatform projects:
  `Sdk="DotKt.Sdk.Mpp"` imports the base `DotKt.Sdk` and turns on `DotKtMultiplatform=true`, so a `common/`
  (`expect`) + `clr/` (`actual`) project compiles to one fully-actualized CLR dll without hand-setting the property.
  Packed as the fifth package by `scripts/pack-nuget.sh`; declares a NuGet dependency on `DotKt.Sdk` (same version).
  Consumers pin the base in `global.json` `msbuild-sdks` (the resolver reads nested-SDK versions only from there).
  Verified end-to-end against a local `build/nuget-feed` (restore → build → run → "Hello from the CLR actual").
  See `docs/design-ktproj-mpp.md` §5.

### Packaging

- **`DotKt.Sdk` now pins the CURRENT toolchain version** (`#131` hotfix). The base SDK's `Sdk.props` hardcoded
  `<DotKtVersion>0.9.3</DotKtVersion>` (drives the implicit `DotKt.Toolchain` + `DotKt.Stdlib` PackageReferences)
  and was never bumped, so `Sdk="DotKt.Sdk/0.9.5"` silently pulled the **0.9.3** compiler (pre-2.4.0, pre-§8c
  implicit-companion) — a user project calling a `.NET` static (e.g. `Application.Start`) failed with
  `unresolved reference`. Bumped to 0.9.5; the `dotkt-cli` template's `Sdk="DotKt.Sdk/0.9.3"` was likewise
  corrected. (`#131` tracks stamping it from `DotKt.Versions.props` at pack time so it can never go stale again.)

### Toolchain

- **`try { value } catch { null }` in VALUE position on a value-type result now materializes its temp as
  `Nullable<T>` on the null branch (`#127`, symmetric hardening of `#126`'s ternary value+null-branch join,
  kotc).** `tryExpr` builds the value-position `try` as a shared temp assigned in each branch, so the temp's
  DECLARED type IS the join type — the try analogue of `ternary()`'s `cond` type. It now mirrors `#126`: when
  `birType(node.type)` resolves to a bare non-nullable value Fqn (`isPrimitiveOrUnsigned`, signed + unsigned
  identically — a join-SHAPE gap) AND a branch (the try body or a catch) yields a bare `null`, the temp is typed
  `Nullable<T>` so the null branch becomes `HasValue=false` instead of assigning `null` into a bare `int` slot (the
  raw-`Nullable<T>`/InvalidProgram class). Under the codebase's uniform nullability a genuine null branch already
  keeps `node.type` nullable (bir2cir preserves the `?` through inline-splice substitution on the stashed JSON), so
  the gate only arms on the substituted-generic drop-`?` shape and no live repro exists in the current corpus — this
  is the correct forward-looking mirror (like `#126`'s if/else-join). New `cases/il-tryval` gates the value-type
  `try{v}catch{null}` join for `Int?`/`Long?`/`Double?` plus the stdlib `toFloatOrNull`/`toDoubleOrNull` bindings
  (value present + null). Layer-pure: kotc emits the `kotlin.*` FQN + existing node kinds; the `Nullable<T>` CLR
  representation stays bir2cir/ilemit.

- **Emit diagnostics now point at the originating `File.kt:line`, and a shared IR-sanity gate runs at both the
  bir2cir/CIR boundary and ilemit (`#112`, diagnostics-quality follow-ups of `#84`).** Two additive,
  no-codegen-change pieces:
  - **Phase 2 — decl-level source position.** kotc now stamps an optional `pos {f,l,c}` (1-based `File.kt:line`)
    on each method/type declaration (via the existing `BirEmitter.locationOf`), threaded BIR → CIR (bir2cir's
    DOM passes preserve the unknown key). ilemit reads it into its `#84` failure breadcrumb, so an emit throw now
    reads `ilemit: File.kt:42: Foo.bar [node]: <message>` instead of the bare `Foo.bar` decl name. The field is
    numeric `l`/`c` (only `pos.f` is a string, allow-listed in `verify-schema.py` STR_OK) so the `#37` types-are-nodes
    freeze is untouched (`additionalProperties:true` headroom + the one STR_OK edit; `pos` added to
    `docs/bir-cir.schema.json`). Optional — a synthetic decl with no source omits it (absent = pre-`#112` behavior).
  - **Phase 4 — shared IR-sanity in bir-common + an offline checker.** The `#84` CIR-sanity invariants (undeclared
    `local`, dangling `goto`/`brIf`, missing `field` owner, malformed `binOp`/`cond`, bad `for` cmp) moved from the
    ilemit-local `Emitter.Sanity.cs` into a shared `toolchain/bir-common/IrSanity.cs` (compile-Included by BOTH
    bir2cir and ilemit); ilemit's file is now a thin adapter. **bir2cir now runs the same checks on the CIR it
    produces** — the earliest catch, at the bir2cir/CIR boundary — surfacing a malformed CIR as
    `bir2cir: File.kt:42: Foo.bar: sanity: <invariant>`. A new offline `scripts/verify-sanity.py` (+ `verify-sanity.sh`
    wrapper, `make verify-sanity`, wired into `make verify`) mirrors the invariants over the post-lowering CIR
    corpus build-free, exiting non-zero with a `File.kt:line`-attributed message. (Layer: bir-common owns the layer-agnostic
    invariants; kotc emits the pos; ilemit/bir2cir consume — no valid-sample emission changes.)

- **A Kotlin class implementing a facadegen-injected .NET generic interface instantiated with a VALUE-TYPE arg no
  longer miscompiles to a `TypeLoadException` (`#128`, real miscompile surfaced by `#79`'s coverage).** A
  `class C : IComparer<Int>` fails at type load with `TypeLoadException: Signature of the body and declaration in a
  method implementation do not match`, while the reference-type sibling (`IComparer<String>`, `cases/il-clrifaceimpl`)
  works. Root cause: the injected interface member surfaces its unconstrained `T` as `T?`, so the override is written
  `Compare(x: Int?, y: Int?)` and bir2cir lowers it to params `Nullable<System.Int32>` — but the CONSTRUCTED CLR slot
  `IComparer<int32>.Compare` uses BARE `int32` (a value type substituted into a .NET generic parameter is bare, never
  `Nullable<>`; reference types work because `Nullable<String>` == `String` on the CLR). ilemit binds the override to
  the slot via `DefineMethodOverride` and the `Nullable<int32>` vs `int32` mismatch throws at load. Fix (bir2cir, the
  Kotlin↔CLR relation): a new pass `ValueTypeIfaceSlotBridge` synthesizes a bridge with the slot's bare-value signature
  that forwards to the Nullable-param method (ilemit re-wraps each bare arg into `Nullable<T>` at the forwarding call;
  its interface-slot overload disambiguation then wires the BRIDGE to the slot, leaving the Nullable method as the
  plain overload the direct call still resolves to — the JVM/Java bridge-method idiom). Tightly scoped: a param/return
  is bare-ified ONLY when the override declares `Nullable<V>` (post `ReferenceNullableStrip`, a surviving `Nullable<>`
  implies V is a value type) AND the corresponding .NET slot position is the interface's own unconstrained generic
  parameter (read off the ref.dll) — so the deliberate value-type-in-type-arg boxing of `Comparable<Int>`/`List<Int>`/
  `sorted` is untouched, and a genuinely-`int?` slot param stays nullable. New GREEN gate sample
  `cases/il-clrifaceimplvt` (direct call + interface upcast + BCL `List<Int>.Sort(IComparer<Int>)` dispatch, plus
  `IEquatable<Int>`). (Layer: bir2cir — override→.NET-slot binding is the Kotlin↔CLR relation's job.)

- **Gate coverage for two correct-by-construction-but-uncovered .NET-interop paths (`#79`).** Two new IL samples,
  both GREEN, close gaps the existing interop-override samples never exercised: (1) `cases/il-clrifaceimpl` — a
  Kotlin class IMPLEMENTING a facadegen-injected .NET generic interface (`System.Collections.Generic.IComparer<String>`);
  the prior samples only EXTEND a base class (kotc's own `isOverride` stamps `override:true`), whereas the
  interface-impl path is re-stamped `override:true`/`vis:public` by bir2cir's `DeclarationRename` and its slot filled,
  so a direct call, an interface-typed upcast dispatch, AND a BCL consumer (`List<T>.Sort(IComparer<T>)`) all dispatch
  into the override. (2) `cases/il-ixname` — a .NET type with a CUSTOM-NAMED indexer via `[IndexerName("Cell")]`;
  `g[i]`/`g[i] = v` must bind to `get_Cell`/`set_Cell` (read from the type's `DefaultMemberAttribute` by
  `bir2cir.NetInteropBinding.DefaultIndexerAccessor`), not the hardcoded `get_Item`/`set_Item`. (Layer: coverage only —
  no production-code change; both paths already worked, they were just gate-blind.)

- **ilemit `callInstance` now guards a value-type-receiver contract violation instead of emitting unverifiable IL (`#108`, defensive).**
  The `callInstance` emit path pushes the receiver as a plain value/reference (`EmitExpr(recv)`) then emits
  `call`/`callvirt` on the resolved method directly — per ECMA-335 that is verifiable IL only when the method's
  declaring type is a reference type (a value-type receiver's `this` is a managed pointer, needing an
  address/`unbox` + `constrained.`, which is the separate `constrainedCall` path). bir2cir lowers every value-type
  instance call to `constrainedCall`, so nothing valid reaches this path with a value-type declaring type; the guard
  converts a future bir2cir mis-lowering into a precise ilemit `CirEmitException` breadcrumb (the `#84` style) naming
  the method + declaring type, rather than a silent miscompile / `BadImageFormat`. Inert on all valid CIR (verify-il
  unchanged). (Layer: ilemit — a pure CIL-verifiability invariant, no Kotlin knowledge.)

- **`as?` (SAFE_CAST) and the if/else null-branch JOIN on an UNSIGNED value type now route the value-type nullable path (`#126`).**
  Two syntaxes still mis-materialized an unsigned nullable as a boxed reference (`Nullable<uint>` via `isInstRef`)
  instead of the value-type `Nullable<uint>` path — the two sites `#118` did not reach: `x as? UInt` fell into
  kotc's reference `isInstRef` branch (signed `Int?` takes `safeCastValue`), and the ternary null-branch join
  tagged an unsigned value+`null` join with a bare non-nullable type. Both gated on the signed-only
  `isValuePrimitive()`; they now gate on `isPrimitiveOrUnsigned()` (mirroring `#56`/`#118`), so an unsigned `as?`
  emits `safeCastValue` and the join is tagged `nullable:<elem>` exactly like a signed `Int`. The now-unused
  `isValuePrimitive()` helper was deleted. `cases/il-nullbang` extended with the unsigned `as?` and if/else-join
  cases. (Layer: kotc — unsigned-is-a-value-type is a Kotlin type fact; the `Nullable<T>` representation stays
  bir2cir/ilemit.)

- **`!!` (and `requireNotNull`/`checkNotNull`) on an UNSIGNED nullable now unwraps `Nullable<T>.Value` (`#118`).**
  A not-null assertion on `UInt?`/`UByte?`/`UShort?`/`ULong?` yielded a raw `Nullable<uint>` STRUCT at a use
  site that consumes the bare value (`u!! + 1u` → `InvalidProgramException`) — the eager null-throw from `#115`
  worked, but the `.Value` unwrap was missing. Root cause: kotc's `nullableElem` gated on `isPrimitiveType()`,
  which EXCLUDES the unsigned inline-classes, so an unsigned `!!` (CHECK_NOT_NULL) fell into the reference objEq
  branch instead of the value-type HasValue/Value branch that signed `Int?` takes (post-`#56`). Unsigned IS a
  value type on the CLR (`Nullable<uint>`, `#76` native-unsigned), so `nullableElem` now gates on
  `isPrimitiveOrUnsigned()` and the `kotlin.UInt` elem lowers to `System.UInt32` downstream exactly as
  `kotlin.Int` → `System.Int32`. The same parity gap in bir2cir's `PreconditionLowering` (its `ValueTypes` set)
  is fixed in the same change so `requireNotNull(u)`/`checkNotNull(u)` on an unsigned nullable also unwrap.
  `cases/il-nullbang` extended with the unsigned cases.

- **User-app MPP: a common `expect` + a CLR `actual` now compile through the APP frontend pipeline (`#119`).**
  A user project with `-Xcommon-sources` (marking the common/expect sources) + `-Xmulti-platform` previously
  failed at kotc with `expect and corresponding actual are declared in the same module`: the app frontend
  (`ClrAppFrontendPipelinePhase`) called the stock `prepareMetadataSessions`, which hardcodes
  `metadataCompilationMode = true` and so forces `SessionConstructionUtils.prepareSessions` down the
  single-session branch — common + platform sources collapse into ONE `FirModuleData`, so an `expect` and its
  `actual` share a module. kotc now inlines that public-API body (the same "fork the thin CLI glue" pattern the
  phase already used) and drives `metadataCompilationMode` off whether any common source is present: with NO
  common sources the flag stays `true` and the path is byte-identical to the prior single-session app compile
  (the non-MPP `cases/il-*` samples are unaffected — verify-il green, no NEW-FAIL); with common sources present it
  flips to `false`, taking the legacy-MPP split (a common module + a platform module that refines it) so
  expect/actual matches across the boundary. Downstream Fir2Ir actualization + BIR emit already handled the
  multi-session output — the stdlib self-build uses the same two tail phases. MPP smoke test:
  `bash experiments/mpp-greeter/build.sh` → `Hello from the CLR actual`.

- **A real `.ktproj` can now build a multiplatform (common `expect` + CLR `actual`) project (`#119`, MSBuild half).**
  Opt in with `<DotKtMultiplatform>true</DotKtMultiplatform>`: the shared pipeline
  (`packaging/DotKt.Toolchain/build/DotKt.Toolchain.targets`) then tags sources under a `common/` directory as the
  common source set — matching both the greeter experiment's layout and Kotlin's `commonMain` convention (or list them
  explicitly via `<DotKtCommon Include="…"/>`) — and adds `-Xcommon-sources="…" -Xmulti-platform -Xexpect-actual-classes`
  to the kotc invocation (the exact flags `experiments/mpp-greeter/build.sh` proved). All sources still pass
  positionally; `-Xcommon-sources` only marks which are common. With the property unset `DotKtCommon` is empty and the
  compile is byte-identical to a non-MPP project, so existing `.ktproj` are unaffected. New gate sample
  `cases/ktproj-mpp/` (common `expect class Greeter` + clr `actual` + entry) wired into `scripts/verify-ktproj.sh`,
  asserting `Hello from the CLR actual`. Productization follow-up: package a distinct `DotKt.Sdk.Mpp` SDK that imports
  the base SDK and layers this on (composition, like `Microsoft.NET.Sdk.Web`); this property-gated slice ships it
  inertly in the shared targets meanwhile.

- **Constructing an external generic type over a FREE type variable now emits via `TypeBuilder.GetConstructor`
  instead of crashing at emit (`#123`).** `AtomicRef(AtomicReference(v))` in `fun <T> atomic(v: T): AtomicRef<T>`
  — where `AtomicReference<T>` is a stdlib `kotlin.*` generic instantiated over the enclosing function's free
  method type-var — failed with `ilemit: … [new]: TypeBuilder generic instantiation does not support resolving
  members`. Such an instantiation is a `TypeBuilderInstantiation`, on which `.GetConstructors()` throws; the
  external branch of the `new` emitter called it directly, unlike `EmitClrNew` (which already re-anchored). The
  branch now resolves the constructor on the open generic definition and re-anchors it onto the instantiation via
  the static `TypeBuilder.GetConstructor` (mirroring `EmitClrNew`'s `IsTbInstantiation` handling), including the
  nested case where a constructor argument is itself a generic instantiated over the same free `T`. Surfaced by
  the kotlinx.coroutines CLR port's `atomic()` helper (previously worked around with a construct-at-concrete +
  cast). Regression sample: `cases/il-genextnew`.

- **An inline collection-factory argument of a `new` inside a generic function now emits `List.Add(T)`, not
  `List.Add(T[])` (`#122`).** `Holder(mutableListOf(x))` in `fun <T> mkHolder(x: T): Holder<T>` (and
  `ArrayList(mutableListOf(x)).size`) threw `InvalidProgramException` at runtime — the vararg array was left
  unsplatted. `MapVarianceRealign.RealignFactoryCtorArgTypes` stamped the factory call's `typeArgs` with the
  constructor's DECLARED param generic arg read RAW (class-scope `tv{type,i}`) instead of instantiating it through
  the `new` node's OWN type arguments (the method-scope binding), so the downstream splat guard — keyed on tv scope
  — mismatched and skipped the vararg splat. The declared arg is now substituted through the `new` binding
  (`tv{type,i}` → `newNode.type.Args[i]`, recursive through nested generics), making the rewrite a correct no-op
  when the factory already carries the right method-scope tv; an unavailable binding skips the rewrite rather than
  stamping the unbound class-scope token.

- **`Array<T>.plus`/`.plusElement` on a value-type element now returns the right values (`#120`).** The 3rd
  manifestation of the value-type-array-nullability family (after `#113` `arrayOfNulls` and `#117` `copyOfRange`).
  The pure-Kotlin body `val result = arrayOfNulls<T>(size+1); result[i]=this[i]; result as Array<T>` had its
  body-local `var result: Array<T?>` slot object-erased to `object[]` while the allocation stayed `newarr !T` and the
  element stelem became `object` — an `object[]`-typed local over a reified `T[]` whose value slots read back as
  garbage (`a.plus(4)` on `arrayOf(1,2,3)` printed random ints). `NullableGenericReturnErasure` now collapses the ONE
  fresh-local "reify-back" chain — `val result = arrayOfNulls<T>(n); ...; return result as Array<T>` — to bare `!T`
  (the var slot + its `arraySet`/`arrayGet` elems), so var slot / newarr / stelem / ldelem / cast all agree. The gate
  is PRODUCER + CONSUMER driven (chain consistency), NOT node-kind: a var whose init is not a direct fresh allocation
  (`RingBuffer.toArray`'s `cond`) or which is not consumed by a bare `Array<T>` cast (`copyOf(newSize)`'s `return
  result` into its object-erased `Array<T?>`) still object-erases — keeping `!T` there would `stelem !T` over an
  `object[]`. General body-local reified-array fix, not per-op; rt.dll ILVerify error-set byte-identical to the
  pre-`#120` baseline. New `il-arrplus` gate (value + reference T).

- **`Array<T>.copyOf(newSize)` on a value-type element now returns the right values (`#124`).** The 4th (and final)
  manifestation of the value-type-array-nullability family — the case `#120` explicitly EXCLUDED, because copyOf(newSize)
  HONESTLY returns `Array<T?>` (a growing array's extra slots are `null`), not the bare `Array<T>` the plus/copyOfRange
  reify-back siblings produce. The old body `val result = arrayOfNulls<T>(newSize); result[i]=this[i]; return result`
  allocated `newarr !T` (an `int[]`) in the generic body — bir2cir's `ReferenceNullableStrip` strips `Nullable(Tv)` on an
  OPEN type-variable (load-bearing for plus/toTypedArray) — while the return/consumer treats it as `Array<T?>` =
  `Nullable<int>[]`: `arrayOf(1,2,3).copyOf(5)` read back garbage and `.toList()` threw InvalidCast. Since a generic IL
  body cannot allocate `Nullable<!!T>[]` statically (no `T : struct` constraint) — and neither could a hypothetical
  compiler primitive, which would have to emit the same reflection — the fix is stdlib-side (`libraries/stdlib/clr/
  generated/_ArraysClr.kt`, the value-type sibling of `#117` copyOfRange): copyOf now builds the result by RUNTIME
  reflection on the receiver's element type via private `@ClrTypeAlias("System.Type")`/`("System.Array")` surfaces —
  `Nullable<elem>[]` for a value-type elem (`typeof(Nullable<>).MakeGenericType`), `elem[]` for a reference elem —
  then per-element `Array.SetValue` copies the prefix (CLR nullable boxing lifts a boxed T into a `Nullable<T>` slot;
  `Array.Copy` does not lift) with a `null` tail from `CreateInstance`'s zero-fill. A `GetUnderlyingType` guard avoids a
  `Nullable<Nullable<T>>` double-wrap when the receiver is already `Nullable<T>[]`. Exact for Int/Long/Double/Char AND
  reference/nullable T. New `il-copyofnull` gate (grow null-tail / shrink / prefix read-back; value + reference +
  already-nullable T).

- **`RootContinuation.trySetCanceled()` now forwards the originating `CancellationToken` (`#116`).** When a suspend
  body's `Task<T>` bridge cancels (an `OperationCanceledException` reaches `RootContinuation.resumeWith`), it completed
  the TCS via `trySetCanceled()` with no argument, dropping the token — whereas .NET's `AsyncTaskMethodBuilder` passes
  `TrySetCanceled(oce.CancellationToken)` so the canceled Task carries the token that raised it. The stdlib Task bridge
  now binds `System.OperationCanceledException.CancellationToken` (a new value-type `CancellationToken` alias) and the
  `TaskCompletionSource<T>.TrySetCanceled(CancellationToken)` overload, and forwards the token. Token-fidelity polish
  only — `IsCanceled`/Cancel semantics (`#86`/`#109`) are unchanged; covered by the existing `il-cocancel` gate.

- **Kotlin frontend bumped 2.2.0 → 2.4.0** (`#111`), behavior-preserving: `verify-il` 265/239,
  `verify-ktproj` 13/13, `verify-differential` MATCH 203 / DIFF 0 — identical to the pre-bump baseline.
  Both halves landed: the compiler dependency (`kotlin-compiler-embeddable` 2.4.0, adopting upstream's
  `Fir2KlibMetadataSerializer` for const baking now that `constValueProvider` is gone; value-class fact
  carried via a `mods.value` BIR flag + `[KotlinValueAttribute]` roundtrip marker since 2.4.0 stopped
  materializing `@JvmInline` in non-JVM sessions; an identity-cast / double-`nullableValue` ilemit fix)
  AND the matching stdlib SOURCE refresh (per-file 3-way merge to v2.4.0, 3 new Uuid actuals). The
  reusable procedure is `docs/kotlin-frontend-bump-playbook.md`.
- **Fixed `arrayOfNulls<value-type>` dropping the element nullability (`#113`).** `arrayOfNulls<Int>(3)` returns
  Kotlin `Array<Int?>`, which on the CLR is `Nullable<int32>[]`, but in chained/inline contexts (e.g. inside the
  stdlib `copyOf`/`toList` bodies) it allocated a native `int32[]` — the value-type type-argument's nullability was
  dropped, so a later `copyOf() as Array<Int?>` threw `InvalidCastException`. bir2cir now nullable-wraps the element
  at the `@ClrArrayFactory` "sized" substitution site (the semantic source: `arrayOfNulls<T>` → `Array<T?>`), so
  `Nullable<T>[]` is allocated uniformly for Int/Long/Double/Char while reference/`String` T stays a bare array. The
  2.4.0 `Array<out T>.toList()` stdlib workaround (element-wise `toMutableList()`) is reverted to upstream
  `copyOf().asList()`. Gate-covered by `cases/il-arrnull`. (`Array<T>.slice`/`take`/`takeLast` were fixed in `#117`.)
- **Fixed `Array<value-type-element>.slice`/`take`/`takeLast` producing garbage (`#117`).** All three route through the
  stdlib `Array<T>.copyOfRange(from,to): Array<T>`, which was a pure-Kotlin `arrayOfNulls<T>(n) as Array<T>`: for a
  value-type T that allocates a `Nullable<int>[]` (inline call site) or an object-erased slot (non-inline generic body)
  and REINTERPRET-casts it to a non-null `int[]` → garbage / InvalidCast. A non-null `Array<T>` whose runtime element
  type equals the receiver's cannot be produced by a `newarr !T`-style Kotlin allocation, so — like the no-arg `copyOf()`
  (`System.Array.Clone`) — `copyOfRange` is now a runtime-element-type-preserving native intrinsic: it reflects on the
  receiver's actual array type via `System.Array.CreateInstanceFromArrayType(this.GetType(), length)` + `System.Array.Copy`
  (net10.0), exact for Int/Long/Double/Char AND reference T, no per-element-type special-casing. Gate-covered by the new
  `cases/il-arrslice`. (The sibling `Array<T>.plus`/`plusElement` share the same underlying bir2cir cross-pass erasure
  bug — object[]-erased local vs `newarr !T` alloc — and are reported for a compiler-side fix, out of this stdlib change.)
- **Fixed reference-type `x!!` not throwing eagerly (`#115`).** Kotlin's `x!!` throws `NullPointerException`
  IMMEDIATELY when `x` is null, regardless of how the result is used. For a reference operand, kotc emitted a
  bare pass-through, so a null only surfaced as a later `NullReferenceException` at a subsequent dereference
  (wrong exception type + site) and NEVER threw at all when the result was stored (`val y: String = x!!`) or
  discarded (`x!!` as a statement). kotc now binds the operand to a temp ONCE and null-tests it (objEq-null,
  mirroring the already-correct value-type-nullable path and bir2cir's `PreconditionLowering` reference shape),
  throwing `kotlin.NullPointerException` on null — an eager throw is a Kotlin-language fact, not CLR knowledge, so
  the fix stays in kotc. Gate-covered by the extended `cases/il-nullbang`.
- **Fixed a cross-module silent miscompile of field-backed properties with a custom accessor (`#103`).**
  A top-level `val x = 41; get() = field + 1` (or a `var` with a custom `set`) consumed from ANOTHER DotKt
  assembly silently returned the raw backing field (41) instead of invoking the getter (42) — the accessor
  was bypassed. `#89` fixed the same-module shape; this is its cross-module twin. facadegen now detects the
  field + `get_`/`set_<name>` pairing and marks the top-level `prop` `customGet`/`customSet` (dropping the
  loose accessor `fun`); kotc restores the flags and routes the cross-module read/write through the accessor
  instead of the static field. Covered by the new `roundtrip-customprop` gate section (top-level + companion
  + member, independent get/set customness).

- **A facadegen-injected .NET enum now satisfies Kotlin's `T : Enum<T>` bound (`#107`).** Code using
  `enumValues<TheNetEnum>()`, `enumValueOf<TheNetEnum>()`, or a `<T : Enum<T>>` generic function over an
  imported .NET enum (e.g. `import System.DayOfWeek`) now compiles AND runs. facadegen declares the
  self-referential `kotlin.Enum<Self>` supertype on an injected .NET `enum` so the frontend accepts the
  bound; bir2cir (`NetInteropBinding`) binds the inherited Kotlin `Enum` contract on a concrete .NET-enum
  receiver to the CLR enum semantics — `.name` → `ToString()`, `.ordinal` → the declaration index via
  `Array.IndexOf(Enum.GetValues(t), value)` (Kotlin-faithful for a sparse/negative/aliased .NET enum, not
  just the underlying int). The generic-receiver `e.name` is handled by the existing `EnumMemberBinding`.
  General, metadata-driven — no per-enum special-casing. Covered by the new `cases/il-netenumbound` gate.

## 0.9.4 — 2026-07-12

0.9.4 carries the 4-layer compiler migration to completion, lands a full coroutine engine,
and turns hundreds of mid-migration fixes into a coherent release. Headlines: `suspend` /
`sequence{}` / `Task.await()` run end-to-end; the compiler's hand-written stdlib lowerings are
retired into a real pure-Kotlin standard library; and every verify gate is XFAIL-zero.

### Coroutines

- **Full `suspend` support end-to-end via a cold-core state machine + `Task<T>` bridge.** A
  `suspend fun` lowers (in bir2cir) to a plain-CIR `ContinuationImpl` state machine plus a public
  `Task<R>` bridge that C#/F# callers consume as a normal hot `Task` — the bidirectional CLR async
  model in `docs/design-coroutine-cold-core-task-bridge.md`. Covers straight-line bodies, all control
  flow (`if`/`when`/`while`/`for`), `try`/`catch`/`finally` across a suspension, generic suspend funs,
  extension + instance + interface + abstract/override members, and cross-module suspend calls
  (consuming a `suspend` fun from another DotKt assembly). A `suspend fun main` is drained correctly
  whether it completes synchronously or genuinely suspends.
- **`sequence{}` / `iterator{}` / `yield` / `yieldAll` are now ordinary library code** over the shared
  cold core (`SequenceBuilderIterator`), for reference and value element types alike. The compiler
  holds ZERO knowledge of these symbols — no CPS engine, no `sequence`/`yield` special-case.
- **`suspendCoroutine{}` / `suspendCoroutineUninterceptedOrReturn` / `@RestrictsSuspension`** lower to
  real suspension points, including cross-module `suspendCoroutine{}` (the wrapper is reconstructed in
  the caller's state machine through a `SafeContinuation`).
- **`Task.await()` — the `.NET Task ⇒ Kotlin suspend` reverse bridge.** `import kotlin.clr.await;
  task.await()` suspends on a `TaskAwaiter`, resuming on completion (sync fast path + genuine async).
  `Task.WhenAll(vararg Task<T>)` / `WhenAny` and generic static factories (`Task.FromResult<T>`)
  resolve and run, so Kotlin can both consume and build a `Task<T>`.
- **`kotlin.clr` coroutine surface = `await` only** (the genuine CLR async boundary, facadegen-injected).
  `blockOn`/`delay` are NOT stdlib — they are re-implemented in a pure-Kotlin test harness over the
  public primitives (`startCoroutine`/`Continuation`/`Monitor`), a living proof that `runBlocking` is
  ordinary library code over the shared core.
- **kotlinx.coroutines purged (BREAKING).** The pre-stdlib `kotlinx.coroutines` stopgap is removed; use
  `kotlin.clr.await` (and a harness `blockOn`) in place of `kotlinx.coroutines.runBlocking`/`delay`.
- **The compiler back half is coroutine-free.** All coroutine lowering lives in bir2cir; kotc's CPS
  engine and ilemit's state-machine codegen are DELETED. The coroutine ABI is monomorphic
  (`Continuation<object>` / `Result<object>`, matching JVM erasure).
- **Coroutine correctness:** suspension-crossing evaluation honors Kotlin's strict left-to-right order —
  impure operands (property / field / array-element reads) left of a suspend call are spilled into a
  state-machine field before the suspension; a `try`/`finally` across a suspension runs its `finally`
  exactly once (not early + twice); shadowed same-name locals of different types get distinct SM fields;
  exceptions propagate across a suspended `Task` boundary.
- **A suspend call inside an INLINE scope function used as a sub-expression lowers.** An expression body
  `suspend fun doFetch(lib, b) = with(lib){ b.fetch() }` (or `c.apply{ s() }.x`) no longer refuses at
  compile time: kotc inlines the scope function to a `valueBlock` verbatim (holding NO coroutine
  knowledge), and bir2cir's `SuspendColdLowering` flattens the value-block — emitting its stmts as
  ordinary statements and segmenting the suspend call in its result as a normal suspension point.
- **Interface `suspend fun` bridge is verifiable IL.** An interface member `suspend fun` (kotc emits it
  `virtual` but without an `abstract` flag, unlike an abstract-class member) is now recognized by bir2cir
  as abstract — its cold entry AND `Task<R>` bridge are emitted abstract (no body), mirroring the
  abstract-class shape — so the synthesized bridge no longer does an unverifiable non-virtual `call` on
  the abstract cold entry (`ilverify CallAbstract`). `cases/il-ifacesuspend` is now ilverify-gated.
- **A `suspend (…) -> T` function type now round-trips across DotKt assemblies (H2).** When a public API
  takes/returns/holds a suspend function type — `fun runBlock(block: suspend () -> Int)` — bir2cir erases
  the CLR signature slot to `object` (a suspend-lambda value is a Continuation state machine, not a `Func`),
  which previously destroyed the suspend origin: a re-consuming module saw a plain `Any?`/`Func` and could
  not pass a lambda that calls a suspend function. ilemit now stamps `[KotlinSuspendFunctionType(shape)]` on
  the param/return/property/field (carrying the pre-erasure `sfunc:<ret>:<args>` shape), facadegen reads it
  back into an `sfunc:[ret,args]` injection-meta token, and kotc's `ClrTypeInjection` restores it to
  `kotlin.coroutines.SuspendFunctionN` — so the parameter is once again a *suspend* function type and a
  passed lambda re-binds as a suspend lambda. Gated by `cases`-style `roundtrip-suspendfn` in
  `verify-roundtrip.sh`.
- **A suspend lambda used as a VALUE (returned / stored) now lowers end-to-end (H2 residual, #33).**
  bir2cir's `SuspendLambdaLowering` previously walked only method/ctor/property-accessor bodies, so a
  `suspendLambdaNew` node in a static field initializer — a suspend lambda RETURNED from a function or
  STORED in a top-level/object property or an instance field — reached ilemit un-lowered and crashed with
  `NotSupportedException: expr suspendLambdaNew`. It now walks `fields[].init` (file-level and type-level)
  too, lowering a value-position `suspendLambdaNew` to a `new <SuspendLambda SM>` value in ANY position.
  Return + property + field are proven cross-module by the new `roundtrip-suspendfn-ret` section.
- **A suspend lambda that captures its ENCLOSING instance now resolves the capture correctly (#34a).**
  When a suspend lambda closes over its enclosing instance — `class Box(val n:Int){ fun make(): suspend
  ()->Int = { addA(n, 5) } }` — bir2cir's `SuspendLambdaLowering` captures that instance as the state
  machine's `__outer` field, but the `invokeSuspend` body rewrite left a bare `this` (kotc emits the
  member read `n` as `this.get_n()`) pointing at the SM instance itself, so `this.n` read garbage
  (a non-deterministic value, not 42). `SuspendColdLowering` now redirects a lambda-body `this` to read
  the captured `this.__outer` field (a suspend lambda has no `this` of its own — its receiver, if any,
  rides a create()-set param field — so every bare `this` denotes the captured enclosing instance;
  synthesized SM-self nodes use the `smSelf` marker and are unaffected). Correct now in every
  construction position — value / call-argument / via a member method / object receiver / nested lambda —
  while a local-capture lambda stays correct. Gated by the new `cases/il-suspendcapture` in `verify-il.sh`.
- **Invoking a suspend functional VALUE `b()` now lowers instead of aborting (#36, GAP 1).** A call to a
  `suspend (…) -> T` value — a param/local/field, e.g. `suspend fun run1(b: suspend () -> Int) = b()` or
  the higher-order `suspend fun times(n, block) { repeat(n){ block() } }` idiom — has no named
  `<name>$dotkt_suspend` cold entry: the value at runtime is a `SuspendLambda` state machine. kotc emits
  it as a `SuspendFunctionN.invoke()` suspend call, which bir2cir previously could not resolve to a cold
  entry, dropped the enclosing fun from the cold-transform set, and then ABORTED at ilemit (`suspend method
  reached codegen un-lowered`). `SuspendColdLowering` now recognizes a `SuspendFunctionN.invoke` suspend
  call and drives it at the suspension point through the stdlib cold-invoke helper
  `startSuspendUninterceptedOrReturn(fn, [receiver,] completion)` (= `create(completion).invokeSuspend()`) —
  the same label/`COROUTINE_SUSPENDED`/resume machinery as a named cold call, only the "start" is the helper.
- **A suspend member/fn that BUILDS a capturing suspend lambda and drives it now lowers (#36, GAP 2).** A
  `class Box(val n:Int){ suspend fun go() = run1 { addA(n, 5) } }` — a suspend fun whose body constructs a
  `this`-capturing `suspendLambdaNew` and passes it to a suspend-value-invoking fn — previously reached
  ilemit un-lowered (a `suspendLambdaNew` in a suspend body disqualified the enclosing fun). The lambda is
  now treated as an OPAQUE value inside the cold state machine: its own body is left for
  `SuspendLambdaLowering`, and each capture's construction value is resolved into the SM's vocabulary
  (`__outer` → the member SM's `$this`, a spilled local → its SM field) and threaded as `capValues`.
- **Invoking a suspend functional VALUE of arity ≥ 2 now lowers (#38).** #36 covered arity 0/1 (the fixed
  `create(completion)` / `create(value, completion)` continuation slots) and refused arity ≥ 2. The
  cold core now carries a GENERAL N-arg protocol: `BaseContinuationImpl.create(args: Array<Any?>,
  completion)` (a new open slot the JVM lacks — there arity 2+ routes through the generated
  `FunctionN.invoke`) plus `startSuspendUninterceptedOrReturnN(fn, args, completion)`. bir2cir boxes the N
  invoke args into an `Array<Any?>` and drives the value through that helper, and the generated N-ary
  suspend-lambda SM overrides `create(args, completion)` — allocating the SM bound to the completion and
  unpacking `args[i]` into its param fields (the same `object → param` unbox/cast the arity-1 path uses).
  kotc no longer gates `newSuspendLambda` on arity, so a `suspend (Int,Int) -> Int` / arity-3 capturing
  lambda emits the pure facts for any N. Covered by the new `cases/il-suspendval2` (arity-2 param/local
  values + an arity-3 capturing lambda, all → 42).
- **A discarded generic `Unit`-returning call no longer strands a `kotlin.Unit` on the stack.** A generic
  method `<T> f(): T` instantiated with `T = kotlin.Unit` (e.g. a discarded `blockOn { …Unit… }`) genuinely
  pushes a `kotlin.Unit`, but the statement-context call carries `retType:"void"`; ilemit's `RetOr` trusted
  that and skipped the pop, leaving the value on the stack (`ilverify ReturnVoid`). ilemit now keeps the
  resolved method's real non-void return so the caller pops/uses it. Covered by the new
  `cases/il-suspendvalue` in `verify-il.sh` (invoke a suspend param value, the `times`/repeat idiom, a
  suspend value in a local, and the GAP-2 member shape — all → 42).

### Language & correctness

- **Two residual generics-covariance seams documented as durable/accepted (#102).** After the #75/#100
  Root-V nested-collection collapse, two covariance gaps are deliberately left as accepted limitations and
  recorded in `docs/dotkt-semantics.md` §5c-ter: (1) the `Map<out K, V>` **key-covariance** ("Root-K") seam —
  reachable only when a user genuinely widens a map's KEY type across a `putAll`/`plus`/copy-ctor boundary
  (same-key merges and value widening are already verifiable via `MapVarianceRealign`); CLR `IDictionary` is
  key-invariant so there is no covariant sibling to collapse to. (2) the ~46 internal `IList`↔`IReadOnlyList`
  view seams inside `DotKt.Stdlib.dll` — reconciled at emit by ilemit's bidirectional `IsCollectionViewSeam`
  `castclass` and **not user-observable** (stdlib/BCL collections implement every face). Documentation-only; no
  code change.
- **Top-level `lateinit var` of a reference type no longer crashes ilemit (#104).** A top-level
  `lateinit var s: String` maps to an initializer-less static field, whose CIR carries `"init": null`
  (key present, JSON-null value). ilemit's static-field-initializer pass (`.cctor`) matched the key's
  mere presence and fed the JSON-null element into the store-coercion path, aborting the emit. The pass
  now skips a null `init`: an init-less static field needs no `.cctor` store (it defaults to null, and a
  read routes through the existing `lateinitGet` not-initialized check, throwing before assignment).
  Only a member `lateinit var` had worked before. (`cases/il-toplateinit`.)
- **`@kotlin.internal.InlineOnly` functions are stamped `[MethodImpl(AggressiveInlining)]` on the emitted method (#98).**
  An `@InlineOnly` fn (the scope functions `let`/`run`/`with`/`apply`/`also`/`takeIf`, `TODO`, `repeat`, …)
  is still emitted as a real method; on the CLR the closest translation of "inline this" is the JIT
  `AggressiveInlining` hint. kotc reads the `kotlin.internal.InlineOnly` annotation and emits a new
  `mods.inlineOnly` flag (a pure annotation read-translation, SEPARATE from `mods.inline`, which stays the
  narrow cross-module `[KotlinInline]` splice signal); ilemit stamps the implementation-flag off it —
  exactly what C# emits. Pure metadata, no behavior change (the JIT ignores the hint for a too-large method);
  a plain non-`@InlineOnly` fn (e.g. `joinToString`) is not stamped.

- **`expr::extFn` — a BOUND extension-function reference now compiles (#91).** `val f = "hi"::shout; f()`
  previously raised a clean "not supported" error: a closed static delegate over the ext forwarder is not
  ilverify-clean (ECMA-335 wants `ldnull` as a static-method delegate target). kotc now lifts a CAPTURE CLASS,
  exactly a capturing lambda `{ args -> expr.extFn(args) }`: a synth closure with a `__recv` field holding the
  receiver (evaluated ONCE, eagerly, at reference-creation time), whose INSTANCE `invoke(args)` forwards to
  `extFn(__recv, args)`. It reuses the existing `newClosure` path, so the delegate binds over the closure's
  instance method (`ldftn instance` + `newobj` — verifiable). Works for receiver-only refs, refs with extra
  args, `Unit`-returning refs, and referenced-assembly facade exts. (`cases/il-boundextref`.)

- **Top-level / companion `val`/`var` with a custom accessor + backing field now invokes the accessor (#89).**
  A top-level or companion property that had BOTH a backing field (an initializer) AND a custom
  `get()`/`set()` was read/written as a raw static-field load/store, silently skipping the accessor
  (`val topProp = 41; get() = field + 1` read 41 instead of 42; a companion `get() = field + 100` read 7
  instead of 107). kotc now gates the static-field shortcut on the accessor being DEFAULT (the trivial
  `field` passthrough) — a custom getter routes the read through `get_<name>` and a custom setter routes the
  write through `set_<name>`, decided independently per accessor (a `var` may pair a default getter with a
  custom setter). The property's custom accessors are now emitted for backing-field top-level/companion
  properties too (previously only computed ones got them), and their `field` reference lowers to a
  `staticField`/`staticFieldSet` of the owning file/enclosing class. Plain `object` properties already
  honored their accessors and are unchanged.

- **Inline unification (#75) is complete — one splice engine for every `inline fun`.** kotc's three
  historical inline mechanisms (`inlineCall` body-visible, the cross-module `[KotlinInline]` splice, and the
  `SCOPE_FUNCTIONS`/`inlineUse` hardcode) collapse into a **single downstream bir2cir splice**. kotc now emits a
  plain `callInline` by identity for every inline fn under a clean **2-axis rule** — splice ⟺ `isInline &&
  hasLambdaArg (a non-`noinline` function-typed argument) && !suspendCoroutineIntrinsic`; a `noinline` lambda
  becomes a real delegate; a lambda-less inline is a plain call (the JIT inlines it). bir2cir owns the whole
  splice (overload selection by full param-sig, capture/`__outer`/`__self` materialization, closure synthesis,
  `tv{scope:type}` substitution from a new `dispatchTypeArgs` carry, fail-loud guards on every descriptor-skew
  path). `SCOPE_FUNCTIONS`/`inlineScope`/`inlineUse` are removed.
- **bir2cir: nested collection type-arguments collapse to their invariant CLR sibling (the Root-V fix).**
  At generic-argument depth ≥ 1, `kotlin.collections.List`→`IList`, `Collection`/`Set`→`ICollection` (the head
  keeps the covariant read-only alias) — so a concrete `Dictionary<K, List<V>>` (e.g. a `groupBy` result)
  inhabits a `Map<K, List<V>>` slot, which the previous read-only-sibling lowering made an uninhabitable
  invariant slot (`mapValues`/`chunk`/`collops2` `ilverify` `StackUnexpected`). Extends §5c's head Map-collapse
  to the value/element positions; see `docs/dotkt-semantics.md` §5c-bis for the deliberate verify-only gaps.

- **ilemit: a mutable-collection interface value flowing into its read-only sibling slot is reconciled
  with a `castclass`.** After bir2cir's arg-position variance collapse (at generic-argument depth ≥ 1,
  Kotlin `List`→`IList`, `Collection`/`Set`→`ICollection`), a value whose static face is `IList<T>` /
  `ICollection<T>` can reach a slot typed as the read-only sibling `IReadOnlyList<T>` / `IReadOnlyCollection<T>`
  (same element `T`) — the two do not derive from each other in the CLR, so the raw flow failed `ilverify`
  (`StackUnexpected`). ilemit now emits the runtime-checked `castclass` at every value-source/consumer site of
  this exact family (call-return, method-return, local/field store, and argument), which always verifies (a
  closed interface cast) and succeeds at runtime because the concrete value implements all faces. Fixes the
  `chunk` / `collops2` / `mapvalues` head-position seams (`for`/destructuring element stores, `Pair.componentN()`
  / `Map.Entry.value` reads typed as the read-only view). A user class implementing `MutableList` /
  `MutableCollection` / `MutableSet` now also lists the read-only sibling interface so such a value can be
  passed into a read-only slot. Purely CLR structural reconciliation — no Kotlin knowledge in ilemit.

- **ilemit: a value-position `when`/ternary (`cond`) typed `kotlin.Unit` whose arms produce `void`
  no longer emits invalid IL.** When one arm of a Unit-typed conditional yields a value (a `valueBlock`
  loading a Unit local) while sibling arms are void (`x.close()`, `println(...)`, a value-producing
  `try` over void arms), the void arms pushed nothing — so the branches merged at the cond-end with
  inconsistent eval-stack depth (ilverify `PathStackDepth` / `StackUnderflow`, `InvalidProgramException`
  at JIT). `EmitBranchCoerced` now reconciles a void arm to the cond's result type (reference → `ldnull`;
  value/generic-param → `default(T)`) so every path leaves exactly one value. Pure CIL stack-depth
  reconciliation; surfaced by the `use{}`/`closeFinally` inline splice but independent of scope functions.

- **#73 Wave 9 (G8): unbound extension-function callable references (`String::isNotBlank`,
  `String::indentWidth`, `Type::extFn`) now work.** Previously ANY extension-function reference fell
  through to `unsupportedExpr` in kotc's `functionRef`; the stdlib `Indent.kt` masked this by lambda-wrapping
  (`{ it.isNotBlank() }`). kotc now lifts a static forwarder whose BODY is the faithful extension call
  (`callStatic owner:null method:<name> args:[__self, …]`), then binds a `newDelegate` over it — bir2cir
  then substitutes/binds that inner call like any other (so `String::isNotBlank` resolves to the BCL
  @ClrIntrinsic). The forwarder's param types + the delegate funcType are derived from the CALL-SITE-resolved
  `KFunctionN` type (`birType(node.type)`), not the declared receiver, so a `String::isNotBlank` reference
  binds `Func<string,bool>` even though `isNotBlank` is declared on `CharSequence`. `Indent.kt` is reverted to
  the natural `.filter(String::isNotBlank)` / `.map(String::indentWidth)` (upstream-identical), proving the
  gap is closed. Also fixes a latent `propertyRef` miscompile: the extension-receiver guard tested the bound
  ARGUMENT (null for an unbound top-level ext-property ref), letting `String::extProp` slip past all guards and
  emit a param-count-mismatched 0-arg accessor override; the guard now tests the accessor's parameter SHAPE.
  Bound extension-fn refs, KProperty2 (member-ext), lateinit/@ClrField/CharSequence.length property refs stay
  clean deferrals (a closed static-forwarder delegate is not ilverify-clean per ECMA-335 II.14.6; the rest have
  no in-tree demand). New sample `cases/il-extfunref`.
- **#73 Wave 8: the System.Object slot names (M5) and the bound .NET method-ref delegate (M4.4) moved out of
  kotc into bir2cir — kotc now emits ZERO BCL member names.** kotc stopped baking the CLR slot names
  `ToString`/`GetHashCode`/`Equals` (its `objectMethodName` helper became the pure-Kotlin boolean predicate
  `isAnySlotMethod`); it emits the Kotlin names `toString`/`hashCode`/`equals` plus two pure-Kotlin facts — the
  existing `objectOverride:true` on a `kotlin.Any`-override declaration, and a new `anySlot:true` on a CALL whose
  callee is such an override. A single new bir2cir pass, `ObjectSlotRename`, runs FIRST in the per-file loop and
  UNCONDITIONALLY (ref + rt + app — matching kotc's former unconditional rename, so the ref.dll's decl names and
  the emitted-name-keyed member index stay byte-identical), mapping the Kotlin name → the BCL slot on decls
  (keyed on `objectOverride`), on `objMethod` nodes (by bare name), and on any call carrying `anySlot` (keyed on
  the flag, kind-agnostic). Placing it first keeps every downstream pass (FaithfulHintRecognition's
  collection-`ToString` recognition, EnumMemberBinding, MemberStrip's bound-stub match, NetInteropBinding,
  DeclarationRename, ilemit) byte-identical. This also fixed two latent, previously-broken sites — a Kotlin-owner
  bound/unbound method reference to an override (`obj::toString` / `UserClass::toString`) had emitted the lowercase
  name against a decl slot renamed to the BCL name (a FindMethod miss); tagging them with `anySlot` renames both in
  lockstep. **M4.4:** a BOUND method reference on a facadegen-injected .NET owner (`netObj::m`) — kotc's last
  .NET-shape decision for method refs — now emits the neutral `newBoundDelegate` carrying the owner identity;
  bir2cir's `NetInteropBinding.ReshapeBoundDelegate` shapes it to the CLR `newBoundClrDelegate` when the owner
  resolves to a .NET type off the refs (a Kotlin/local owner stays a plain `newBoundDelegate`). (The unbound
  `NetType::m` lift was already reshaped by #61's NetInteropBinding.)
- **#73 Wave 1: three more CLR-representation decisions moved out of kotc into bir2cir.** Each deletes a
  kotc branch that baked a CLR fact and re-homes the derivation in the Kotlin↔CLR layer:
  - **`kotlin.clr.Span<T>` (M11).** kotc's `birType` no longer maps it to the literal `System.Span` (the last
    naked `System.*` name in kotc); it emits the faithful `kotlin.clr.Span<T>` identity and bir2cir's
    `BirTypeLowering.LowerType` substitutes it to `System.Span<T>` in every build (placed before the
    ref-build passthrough so the substitution is uniform), like every other alias.
  - **`ieee754equals` (M8).** kotc emits the faithful `kotlin.internal.ir`-owned intrinsic call (a sibling of
    `EQEQ`/`less`/…), and bir2cir's `PrimitiveOperatorLowering.LowerIntrinsic` re-emits the ordered IEEE-754
    `binOp ==` — previously kotc lowered it to `binOp ==` directly while the rest of the family had already moved.
  - **`UByteArray.toByteArray()` / `ByteArray.toUByteArray()` reinterpret (M9, #76 residue).** kotc emits the
    faithful top-level extension call; bir2cir's `FaithfulHintRecognition` recognizes it off the receiver's
    recovered static type and re-emits the same reinterpret `cast` (a VIEW, not a copy). The "UByteArray IS
    byte[]" fact no longer lives in kotc.
  - **`String.reversed()` (M10) stays lowered this wave** — a concrete blocker: the real stdlib path
    `CharSequence.reversed() = StringBuilder(this).reverse()` needs a `StringBuilder(CharSequence)` ctor, but
    `System.Text.StringBuilder` has no ctor accepting a CharSequence (only `String`/`Int32`, Codex-confirmed),
    so the @ClrTypeAlias ctor cannot be bound 1:1 and the compiled rt body throws `InvalidProgram`. Closing it
    needs a separate bir2cir CharSequence→String ctor-arg coercion feature.

- **#73 Wave 6: one MOVE (M13) after empirical investigation + three verify-by-deletion dead-code removals
  (D1/D2/D3) from the kotc residual audit.** Full stdlib BIR byte-diff + verify-il/differential/roundtrip/ktproj
  all green:
  - **M13 Pair/Triple/IndexedValue `.first`/`.second`/`.third`/`.index`/`.value` (MOVED).** kotc emitted these as
    raw `field` reads, baking a false "these props are plain fields" layout assumption. Investigation: those stdlib
    backing fields are emitted `internal` (accessor-routed), so a cross-assembly raw field read never binds — the read
    only worked because ilemit's field handler re-routes an external-owner field to its `get_<name>` accessor. Deleted
    the special-case → the ordinary member-property read emits the faithful `get_first`/`get_index`/… call (`call`
    instead of `callvirt` on the final getter; ilverify-clean, byte-better).
  - **M13 `EnumEntries.size` → `arrayLen` (reclassified GENUINE, audit G9).** NOT a layout hack: `EnumClass.entries`
    is lowered by kotc to an `enumValues` node = a real `E[]` CLR array, so `.size` MUST be `arrayLen`. Coupled to the
    still-in-kotc direct-entries producer (the un-landed half of M3); moves with it, not before.
  - **D1 `clrIfaceMemberName` (DELETED).** It only ever returned `"get_length"` for kotlin.CharSequence.length —
    identical to the `get_`+name default at every consumer. Deleting it flips only `override:true→false` on the
    CharSequence.length accessor (virtual/vis preserved; the `overrides` marker + ilemit `DefineMethodOverride` bind the
    interface slot by name/signature), verified inert. The `method()`/`samConversion`/method-call-path consumers were
    dead (accessors never reach them); the propertyRef `MyCs::length` deferral was a live behavior gate and is preserved
    inline. Deleted the stale `resumeWith→ResumeWith` comments (the function never did that).
  - **D2 `charSeqIface` (DELETED).** An identity map `kotlin.CharSequence`→same FQN. All consumers fall through to the
    general path, which returns the same bare `kotlin.CharSequence` (non-generic, no clrName). Stdlib BIR byte-diff = zero.
  - **D3 propertyRef `get_annotations` (CONSOLIDATED).** The lifted `KProperty0/1` class now extends the real stdlib
    `kotlin.reflect.ClrPropertyStub<V>(name)`, inheriting `name`+`annotations` instead of hand-rolling a bare-name
    `emptyList` call. `ClrPropertyStub` is made `open` (the lift's base; zero IL diff — ilemit does not seal
    Kotlin-final) with a KDoc that documents both its uses. Verified across bound/mutable/unbound + value/reference/
    app-class/generic-`tv` `V`; `cases/il-propref` gains generic-context + app-class references to guard the
    base-with-baseArgs shape.

- **#73 Wave 3: the for-in `forEachInline` gate (M1) moved from kotc into bir2cir — the last CLR-representation
  decision in kotc's loop family.** kotc's for-in emission carried a residual `forInEnumerable` gate: it chose the
  `forEachInline` (GetEnumerator) loop shape when the source's static type was a facadegen-injected .NET type
  (`clrName(src) != null`) OR exactly `kotlin.sequences.Sequence` — a CLR-representation decision keyed on
  `@Clr`/.NET-type knowledge. kotc now emits ONE faithful `forIn{src,srcType,elem,body,fallback}` for EVERY
  non-array source (no `clrName`/`Sequence` classification leaves kotc), and bir2cir's `ForInLowering` gained the
  dispatch arm: a `forIn` whose `srcType` is `kotlin.sequences.Sequence` OR resolves to a referenced .NET type
  (`ReferenceMetadataIndex.ResolveNetType != null` — the faithful equivalent of the old `clrName` test, since it
  returns null for every `kotlin.*`/`kotlinx.*`/`dotkt*`/app-local FQN and non-null exactly for a reachable .NET
  type) becomes a `forEachInline`, in ALL builds. Without it a Sequence/.NET source would fall to the Kotlin
  iterator protocol and a consumer would hit `EntryPointNotFound`. The kotc-emitted-`forEachInline` handling in
  `ForInLowering` (and its `srcType`-strip) is deleted — kotc no longer produces `forEachInline` at all. Proven
  transparent on the stdlib self-build: the rt-build CIR is byte-identical to before modulo global compiler-counter
  renumbering (the faithful `forIn` now carries the FIR-desugared `fallback`, whose desugaring bumps the shared
  `__inl`/`__inlRet`/`__lam`/label counters that bir2cir then discards when it selects `forEachInline`) — every
  `forEachInline`/`forRange`/iterator site preserved in count and structure.

- **#73 Wave 2: two more kotc CLR/stdlib decisions moved into bir2cir.**
  - **Direct enum `values()`/`entries`/`valueOf()` (M3) — kills the last banned `@Name` type-token in kotc.**
    kotc's direct (non-reified) enum-intrinsic path emitted the legacy `"@Color"` type-token STRING in its
    `enumValues`/`enumParse` nodes (`val et = "@" + ec.name`); it now emits the FAITHFUL structured FQN identity,
    exactly like the reified `enumValues<T>()` path (bir2cir's `EnumIntrinsicLowering`). No `"@" +` type-token
    construction remains anywhere in kotc. bir2cir's `StaticTypeResolver` dropped its now-dead `@Name`-string
    fallback (both producers emit the structured Type).
  - **Range membership `x in a..b` (M2) — a live user-type miscompile fix + a move.** kotc lowered `x in a..b` /
    `x in a until b` to `>=`/`<`/`<=` comparisons keyed on the BARE names `contains`+`rangeTo`/`until`/`rangeUntil`
    with NO FQN gate, so a USER type with `operator fun rangeTo`+`contains` was MISCOMPILED to primitive
    comparisons. kotc now emits the faithful `contains` member call by identity; the new bir2cir
    `RangeMembershipLowering` re-emits the short-circuit `(x >= a && x <op> b)` fast path FQN-keyed — only for a
    stdlib primitive range (`kotlin.ranges.{Int,Long,Char}Range` contains over an un-materialized
    `rangeTo`/`until`/`rangeUntil`) — binding the subject once so a side-effecting operand runs a single time. A
    user rangeTo/contains type now dispatches its real `contains()` (new gate `cases/il-userrange`, also in the
    differential oracle).

- **`@JvmInline value class` (and any other `kotlin.jvm.*` name used unqualified) resolves again
  after the klib migration (#80).** Switching kotc's app/stdlib frontends from the JVM-platform
  pipeline to the Common/Native-platform metadata pipeline (`MetadataFrontendPipelinePhase` /
  `prepareNativeSessions`) dropped `kotlin.jvm.*` from the FIR session's default imports — only the
  JVM platform's `DefaultImportProvider` adds it, and Common/Native's don't need it upstream (no
  `kotlin.jvm.*` on those real platforms). kotc now re-registers the FIR session's
  `FirDefaultImportProviderHolder` with a provider that adds `kotlin.jvm.*` on top of the
  platform's own default imports, right after each session is built and before any FIR resolution
  reads it (`kotc.pipeline.installKotlinJvmDefaultImport`, wired into both
  `ClrStdlibFrontendPipelinePhase` and the new `ClrAppFrontendPipelinePhase`, a fork of stock
  `MetadataFrontendPipelinePhase` needed only to get hold of the `FirSession` before resolution runs).
  Gate: `cases/il-valclass` (`@JvmInline value class Money(val cents: Int)`, verify-il `valcls`).
- **A star-projected collection over a value-type element (`is Map<*,*>` / `List<*>` / `Iterable<*>` / `Collection<*>`
  on a `Dictionary<int,int>` / `List<int>`) no longer throws `InvalidCastException` (#60).** After `if (g is Map<*,*>)`,
  the smart-cast `g` erased to `Map<Any?,Any?>` — the CLR generic `IDictionary<object,object>`; because CLR reified
  generics are INVARIANT, a `Dictionary<int,int>` does NOT implement it, so the `castclass` threw (the JVM erases both
  to `Map`, hiding it). bir2cir now lowers a star-projected/`Any`-erased collection **cast** to the NON-generic BCL
  interface (`System.Collections.IDictionary`/`IList`/`ICollection`/`IEnumerable`), which every value-type-arg BCL
  collection implements — mirroring the existing `is`-test lowering. `println` of such an erased value routes to the
  stdlib's `clrElemToString(Any?)`, which renders `{1=2}` / `[10, 20, 30]` via the non-generic facades; `.size`
  re-points onto the non-generic `ICollection.Count` and `[i]` onto `IList.get_Item`. Gate: `cases/il-starproj`
  (ilverify-clean). All lowering is in bir2cir (the Kotlin↔CLR layer); no kotc/ilemit change.
- **A basic (non-rich) enum's top-level reified `enumValues<T>()`/`.entries` for-loop no longer crashes ilemit
  (#77).** `for (x in enumValues<Color>())` / `for (c in Color.entries)` wraps the `enumValues` node in a `forArray`
  with no `elem` — bir2cir's static-type recovery (`StaticType.Surface`) had a case for a singular `enumValue` but
  none for the plural array-producing `enumValues`, so `elem` derivation returned null and ilemit's `forArray`
  emission KeyNotFound'd on the missing property. Added the `enumValues` case (handling both the structured
  top-level-intrinsic encoding and kotc's legacy `"@Name"` string form for a direct `Color.values()`/`.entries`
  member read), and reordered `EnumIntrinsicLowering` to run BEFORE `ArrayConstructionLowering` so the reified
  top-level intrinsics are already in their final semantic shape when element-derivation runs. Gate:
  `cases/il-enumintr` (index/`.size`/`enumValueOf`/for-loop/reified-inline-fn instantiation).
- **A basic (non-rich) enum's `.toString()`/`.hashCode()`/`.equals()` no longer crash ilemit (#90).** A BASIC
  `enum class` (constants only) lowers to a CLR value-type `enum` that INHERITS `ToString`/`GetHashCode`/`Equals`
  from `System.Enum` and declares none of its own. kotc emits `callInstance ownerType=E method=toString anySlot:true`
  (static receiver = the concrete enum); `ObjectSlotRename` renames the method to `ToString` but keeps owner `E`, so
  ilemit's `FindMethod("E","ToString")` dead-ended ("method E.ToString not found"). bir2cir's `EnumMemberBinding` now
  collects locally-declared value-type enums (`kind:"enum"`) module-wide (across every `.bir.json`, so a call site in a
  different file from the `enum class` declaration is covered) and rebinds each such Object-slot call to an `objMethod`
  (box the value-type receiver + `callvirt` the `System.Object` virtual slot; `System.Enum`'s override supplies the
  constant name / value-equality / hash), moving `Equals`'s argument into the `arg` slot ilemit reads. This shares
  the same box-then-Object-slot mechanism as the pre-existing generic-`Enum<T>`-receiver path. Gate:
  `cases/il-enumtostr` (`.toString()`, `println(Any?)`, string concat, `==`, `.equals()`, `.compareTo()`).
- **CROSS-ASSEMBLY basic-enum inherited members (#105) confirmed already closed — regression guard added, no code
  change.** A basic `enum class` declared in a REFERENCED DotKt assembly and `.toString()`/`==`/`.hashCode()`'d by a
  consumer does NOT hit the #90 `callInstance` gap: kotc emits the inherited-member call by FQN identity
  (`callInstance owner=palette.Color`), and bir2cir's `NetInteropBinding` resolves that owner off the `--ref` DotKt
  assembly (A2/#61) one pass BEFORE `EnumMemberBinding`, binding it to a `clrInstance`; ilemit's
  `EmitClrCall`/`EmitInstanceCall` take the value-type receiver by address and emit `constrained. <Color>; callvirt
  object::ToString` — valid, ilverify-clean (verified end-to-end: `RED` / `False` / `0`). A klib-external `kotlin.*`
  enum arrives from kotc already as an `objMethod`, so it too skips the local gap. A candidate
  `ReferenceMetadataIndex.TypeKinds`-"enum" union into `EnumMemberBinding`'s set was investigated and REJECTED as
  unreachable dead code (its owner universe is the same MetadataLoadContext `NetInteropBinding` already rebinds). New
  `verify-roundtrip.sh` `roundtrip-enum` section pins the facadegen-injected-enum -> `NetInteropBinding` `clrInstance`
  -> `constrained. callvirt` path as a regression guard.
- **`Map<*,*>`'s `get`/`containsKey` (and any `Collection<*>`/`Map<*,*>` for-loop / explicit `.iterator()`) no
  longer throws `InvalidCastException`/`EntryPointNotFoundException` on a star-projected receiver (#74).**
  `m[key]`/`m.containsKey(k)` on a `Map<*,*>` resolves (Kotlin `@OnlyInputTypes` overload rule) to the stdlib's
  cross-module `Maps.kt` extension — not the `Map` interface member — and since that extension is `@InlineOnly`
  but not actually inlined cross-module, it arrives at bir2cir as a generic top-level call instantiated
  `K=V=object`; its generic `IDictionary<object,object>` call-boundary param rejects the receiver's real
  (invariant, reified) runtime type before the extension's own covariance-safe body ever runs. bir2cir now
  recognizes this exact shape and emits the non-generic `IDictionary.get_Item`/`.Contains` call directly (its
  indexer is null-on-missing, matching Kotlin `Map.get` exactly) — same fix applied to `StarProjectionLowering`'s
  member-call routing for completeness. Separately, a `for`/explicit-`.iterator()` over an `(x as Collection<*>)`
  cast dispatched a typed `IEnumerable<object>`/generic `Iterator<object>` a value-type-element runtime collection
  doesn't implement; `.iterator()` now routes through a new rt bridge (`iteratorOverRawEnumerable`, a
  `KotlinIteratorOverEnumerator` twin over the non-generic `IEnumerator`) that produces a genuine `Iterator<Any?>`,
  and `SequenceForEachLowering`'s non-generic `IEnumerable`/`IEnumerator` for-loop rewrite now also fires for a
  star-projected/erased collection cast (previously Sequence-only). All bir2cir-side; one new rt stdlib helper.
- **`!!` on a value-type nullable (`Int?`/`Long?`/`Double?`/`Byte?`…) now emits verifiable IL and throws on null (#56).**
  kotc lowered the `CHECK_NOT_NULL` intrinsic (`v!!`) to a bare pass-through, leaving the `System.Nullable<X>` **struct**
  on the stack where the use site consumes the bare value: `n!! + 1` produced an `InvalidProgramException`, `n!!.toLong()`
  read garbage, and `null!!` silently failed to throw. kotc now lowers `v!!` on a value-type nullable to a
  `Nullable<X>.HasValue` test — throw `NullPointerException` on empty, else unwrap `Nullable<X>.Value` — reusing the
  same `nullableHasValue`/`nullableValue` nodes as the #15 smart-cast unwrap. Reference-type `!!` is unchanged. Gate:
  `cases/il-nullbang` (int/long/double/byte, non-null + null-throws-NPE; ilverify-clean).
- **`tailrec` is now tail-call optimized — deep tail recursion runs in constant stack (§2b deviation CLOSED).**
  Our pipeline runs Fir2Ir straight into the backend, skipping the JVM lowerings, so a `tailrec` self-call stayed
  ordinary recursion and `sumTo(1_000_000, 0)` overflowed the CLR stack where kotlinc/JVM loops. kotc now reapplies
  the frontend's own tail-call transform: a self-tail-call (identified by `collectTailRecursionCalls`) is rewritten
  to a back-jump to the method entry — evaluate the args into temporaries (so `sumTo(n-1, acc+n)` isn't corrupted by
  the reassignment), reassign the parameters, `goto` the loop head. Covered for self / multi-branch-`when` /
  extension-receiver / member tailrec. Gate: `cases/il-tailrec` (verify-il + JVM-oracle differential).
- **Partial `Pair`/`Triple.copy(field = x)` no longer misplaces the argument (kcc review C3).**
  `(1 to 2).copy(second = 20)` returned `(20, 0)` (the named arg fell into the wrong slot) because a data-class
  copy's per-field default is `this.<field>`, a non-constant default the frontend jar drops cross-module
  (IrErrorExpression) — so the omitted field was silently dropped. kotc now reconstructs each omitted copy field as
  a receiver field read at the instantiated call site (`(1 to 2).copy(second=20)` → `(1, 20)`,
  `Triple(1,2,3).copy(second=9)` → `(1, 9, 3)`), the stdlib-data-class analogue of the same-module user path. Gate:
  `cases/il-copydef`.
- **An explicit `.equals()` follows Kotlin's total-order / structural equality (§5a edge CLOSED).**
  The `==` operator already routed a boxed `Double`/`Float` to the total-order helper and a collection to the
  structural helper, but an explicit `x.equals(y)` still hit `Object.Equals` (IEEE `-0.0 == 0.0` / reference
  identity). kotc now routes the explicit call through the same helpers: `(-0.0).equals(0.0)` → `false`,
  `listOf(1,2).equals(listOf(1,2))` → `true`; a plain object stays reference identity, String keeps its own
  value-equality binding. Gate: `cases/il-equalscall`.
- **`CharSequence.windowed(size){ value-type R }` no longer garbles its elements (#25 / W4-B).**
  `"abcd".windowed(2){ it.length }` returned pointer garbage instead of `[2, 2, 2]` (a reference-type `R`
  like `{ it.toString() }` was fine). Root: the pure-app `CharSequence`→`System.String` lowering
  (`CharSeqStringLowering`, bir2cir) collapsed the transform LAMBDA's `it: CharSequence` param to `string`
  and its member reads to `System.String.get_Length`/`get_Chars` — but that lambda is a `delegateNew` target
  whose `funcType` KEEPS the synthetic `<>dotkt_CharSequence` (it must match the stdlib's `Func<CharSequence,R>`
  generic sig), and the stdlib `windowed` passes a genuine `<>dotkt_CharSequence` (its `subSequence` result)
  into the delegate. Reading `String.Length` off a non-String object then reinterpreted pointer bits as an
  `Int`; a reference `R` masked it because `toString()` is a virtual `objMethod`. Fix: exempt any lambda used
  as a `delegateNew`/`delegateInvoke` target with a `<>dotkt_CharSequence` param from the lowering, so its
  param stays synthetic and its member reads stay virtual interface calls. Regression case `il-cwindowedv`
  (JVM-oracle PURE).
- **`Double`/`Float` boxed structural equality and `compareTo` now follow Kotlin's total order (C14).**
  Kotlin gives floating types a total order in the boxed/`compareTo`/structural-`equals` path (distinct from the
  primitive IEEE operators): `-0.0 != 0.0`, `NaN` is the largest value, `NaN == NaN` and `NaN.compareTo(NaN) == 0`.
  On the CLR `kotlin.Double` IS `System.Double`, whose `Object.Equals`/`CompareTo` do not match that order. kotc now
  routes a BOXED `==` on a floating value to the stdlib total-order helper `clrDoubleEquals`/`clrFloatEquals`
  (`toBits()` bit-compare) and a direct `Double`/`Float.compareTo` to `clrDoubleCompare`/`clrFloatCompare` (JDK
  total-order algorithm). Primitive `==`/`<`/`>` stay IEEE (`-0.0 == 0.0` true, `NaN == NaN` false; `il-nancmp`-green).
  `(-0.0 as Any) == (0.0 as Any)` → `false`, `(-0.0).compareTo(0.0)` → `-1`. Gate: `cases/il-negzero` (JVM-oracle PURE),
  `docs/dotkt-semantics.md §5a` (was a documented deviation, now removed).
- **Collection `==` is now STRUCTURAL, not reference identity.** Kotlin `==` on a `List`/`Set`/`Map` compares elements
  (`AbstractList/Set/Map.equals`), but the CLR-lowered BCL collections use reference `Object.Equals`, so
  `listOf(7,8) == listOf(7,8)` returned `false`. kotc now routes a collection `==`/`!=` (static-type-driven off both
  operands, mirroring `collToStringRoute`) to the stdlib structural helpers `clrCollStructEquals` (List/ordered),
  `clrSetStructEquals` (unordered), `clrMapStructEquals` (entrywise). `listOf(1)==setOf(1)` stays `false` (kind
  mismatch → reference), and non-collection reference `==` is unchanged. Gate: `cases/il-listeq` (JVM-oracle PURE).
- **`for (i in coll.indices)` / `"s".indices` now iterates in APP builds.** A for-loop over a non-literal `IntRange`
  obtained from `.indices` fell to the iterator protocol and hit an unresolved `IntIterator.hasNext` (emit-time
  crash). kotc now counter-lowers a `for` over an IntRange VALUE in app builds too: it spills the range once and reads
  `first`/`last` off the referenced type (an IntRange is always step-1 ascending). Gate: `cases/il-indices` (JVM-oracle
  PURE). (A value-type-element list still crashes in the `.indices` getter itself — the pre-existing
  `generic-ext-property-getter-typeargs` bug, separate from the loop.)
- **Same-module default argument referencing another value parameter (C3 residual).** A default like
  `fun f(a: Int, b: Int = a * 10)` called `f(5)` was rejected (`omitting a non-constant default argument`). kotc's
  positional-fill now inlines such a default with each referenced value parameter rewritten to THIS call's filled arg
  (via captureSubst, the twin of the `= this` receiver case). The cross-module `@KotlinDefault` BIR now encodes a
  value-param read as a `{param N}` token and bir2cir's `DefaultArgSplice` substitutes it (peer of its `{this}`
  substitution) — latent until `@KotlinDefault` param attributes are encoded into the ref.dll (see Known issues).
  Gate: `cases/il-defargs2` (JVM-oracle PURE).
- **`generateSequence(seed){ next }` now drives correctly for value AND reference elements (C13a).**
  Two ilemit codegen bugs in the cold-sequence path are fixed: (1) a generic capturing closure passed as a
  DELEGATE argument (the `{ seed }` closure into `GeneratorSequence`'s `Function0` ctor param) had its
  `newobj` emitted with an OPEN generic operand (`Closure`1::.ctor(!0)`) — a `TypeLoadException` at run;
  the delegate-arg binding path now instantiates the closure generic (shared with the main `closureNew`
  emit via `ResolveClosure`). (2) The `GeneratorSequence` iterator's `delegateInvoke` passed a boxed `T?`
  to a `Func<T,object>::Invoke(!0)` slot with no unbox — tolerated for a reference element (the object IS a
  valid reference) but an `InvalidProgramException` for a value element; delegateInvoke now coerces each arg
  to the delegate's declared param type (`unbox.any` — unbox a value param, castclass a reference one).
  `generateSequence(1){ it*2 }.take(3).toList()` == `[1, 2, 4]`. (`cases/il-genseq2`.)
- **`break`/`continue` in expression position now lowers (C13b).** A `break`/`continue` used as an
  `if`/`when` branch VALUE (`val end = if (…) x else break`) — Kotlin-typed `Nothing` — previously hit
  `the .NET backend does not support this expression yet: IrBreakImpl`. kotc now emits the same control
  transfer inside a `valueBlock` with an unreachable `throw` result, so it never falls through to the
  surrounding merge (mirrors the existing `throwExpr`/`returnExpr`-in-expression handling). Unblocks
  `CharSequence.windowed(size)` (`"abcd".windowed(2)` → `[ab, bc, cd]`), whose stdlib body uses the
  construct. New PURE case `il-cwindowed`.
- **`Grouping.eachCount()` (regression guard, C13c).** `listOf("a","ab","b").groupingBy { it.first() }
  .eachCount()` → `{a=2, b=1}`. Its body reads a value-type-nullable smart-cast (`Int?`) in arithmetic
  (`count + 1`) — already correct via the C1 value-slot-unwrap; locked with new PURE case `il-eachcount`.
- **Default arguments now fill positionally — an omitted middle default no longer shifts a later
  argument's slot (C3).** The kcc-review C3 family is fixed in kotc + bir2cir:
  - `list.joinToString("-") { "x$it" }` prints `x1-x2-x3` (was `System.Func…1-2-3`: the transform lambda
    had leaked into the `prefix` slot because the four omitted middle defaults were dropped, sliding the
    lambda up the argument list).
  - `str.substringAfter("=")` / `substringBefore` (default `missingDelimiterValue = this`) return the
    right value (was `InvalidProgramException`).
  - `dataInstance.copy(field = x)` compiles and runs, same-module and cross-module (the generated
    `copy`'s self-referential `y = this.y` default was previously refused with "omitting a non-constant
    default argument").
  - kotc `filledArgs` emits a positional `{"k":"defaultArg"}` placeholder for each omitted arg of a
    `@KotlinDefault`-carrying cross-module callee, and inlines a same-module receiver-referencing default
    with `this` rewritten to the call's receiver; bir2cir's `DefaultArgSplice` replaces each placeholder
    in place (by array index, matching the `@KotlinDefault` stamp) and rewrites a `{"k":"this"}` default
    to the call's receiver. See `docs/dotkt-semantics.md §7`/§10 (default omission now works everywhere —
    trailing, named-middle, reordered, and mixed with a trailing lambda). A same-module default that
    reads another VALUE parameter (`b = a * 10`) still needs a `$default` synthetic (documented follow-up).

- **Boxed-primitive dual-representation through generics no longer crashes or loses data (C2).** A family
  of value-type-via-generic-`T`/`V` miscompiles is fixed in bir2cir + ilemit:
  - `MutableMap<K, primitive>.getOrPut(k){…}` no longer silently returns `0` and skips the insert. The
    inlined `get()`'s erased-nullable (`object`) result was stored raw into the `gp:V` local, so
    `value == null` never saw the `null`; the local is now object-typed and the `else` branch unbox.any's
    back to `V`.
  - `Map<K, primitive>.getOrElse(presentKey){…}` returns the real value instead of garbage (the `object`
    `else`-branch of the result `cond` is now unbox.any'd to `V`).
  - `compareBy`/`compareValuesBy`/`sortedBy` with a primitive selector no longer NREs: a `Comparable<*>`
    selector return lowers to the NON-generic `System.IComparable` (a boxed `Int` is `IComparable`, never
    the contravariant `IComparable<object>`), and a value returned where a reference is declared now boxes.
  - `Array<Int?>` (= `Nullable<int>[]`) element access no longer SIGSEGVs: `arrayOf(1, null, 3)` /
    `arrayOfNulls<Int>(3).also{ it[0]=5 }` wrap each element into `Nullable<int>` (or `default`) at
    `stelem`, and the array creation allocates the correct `Nullable<int>[]`.
  - `fun <T : Enum<T>> …(e.name)` no longer throws a VerificationException: the self-referential
    `Enum<T>` bound lowers to the CLR `System.Enum` constraint, and `e.name` on a generic enum receiver
    binds to `System.Enum.ToString()`.
  - Covered by the JVM-oracle differential case `cases/il-boxgen`.
- **`Int`/`Long`.`toString(radix)` renders sign + arbitrary base, not two's-complement (C4).** kotc's
  legacy `System.Convert.ToString(value, base)` special-case (a BCL name in the frontend — a layer
  violation) was both wrong and crash-prone: `(-255).toString(16)` gave `ffffff01` instead of `-ff`,
  `Int.MIN_VALUE.toString(16)` dropped its sign, and any base outside `{2,8,10,16}` (`35.toString(36)`)
  threw `ArgumentException: Invalid Base`. The special-case is deleted; kotc now emits the plain
  `kotlin.text` extension call and bir2cir attributes it to the stdlib `StringNumberConversionsKt` body,
  which produces `-ff` / `-80000000` / `z`. Covered by `cases/il-radix` (JVM-oracle differential).
- **Deterministic `String`/`Double`/`Float` `hashCode()` (C5).** kotc's universal-method intercept
  unconditionally rewrote every `.hashCode()`/`.toString()`/`.equals()` on a `kotlin.*` receiver to the
  `System.Object` slot (`GetHashCode`/`ToString`/`Equals`), which shadowed the stdlib's declared
  overrides — so `"Aa".hashCode()` returned .NET's per-process-randomized hash instead of Kotlin's
  deterministic polynomial `2112`, `""`.hashCode() was non-zero, and `(-0.0).hashCode()` was not
  `Int.MIN_VALUE`. The intercept is now GATED: it falls through to the real declared member when the
  receiver TYPE declares its own override (String's polynomial hash, Double/Float's deterministic
  bit-hash — routed to the stdlib body; String's `@ClrIntrinsic` toString/equals — to their BCL slot),
  and keeps the `System.Object` slot only for a genuine universal call on a type with NO override (an
  inherited `kotlin.Any` member) and for primitive value types' bodyless `toString`/`equals`
  (`Int`/`Long`/`Char`/`Boolean` — the BCL slot is correct there). This also resolves the layer-review
  M2-vs-C5 tension (the routing is kept exactly where it is still correct). Covered by `cases/il-strhash`
  and `cases/il-pairtostr`.
- **Cross-module top-level extension-property getters no longer crash (C7).** A `val List<T>.lastIndex`,
  `val Int.absoluteValue`, `val CharSequence.lastIndex` (a top-level extension property with no
  declaring class) fell to a current-file-class static-field read that dropped the receiver entirely —
  `NotSupportedException: field <AppKt>.lastIndex not found` at emit. kotc now routes an extension
  property to `callStatic owner=null get_<name>(receiver)` (mirroring the top-level extension-FUNCTION
  path, so bir2cir attributes it to the ref.dll file class), carrying the resolved type args for a
  GENERIC getter (`get_lastIndex[T]`) so ilemit instantiates it. Covered by `cases/il-extprop`.
- **Value-type nullable smart-cast reads the value, not `HasValue` (C1).** An `Int?`/`Long?`/`Double?`
  (a CLR `Nullable<T>`) narrowed by `if (n != null)` and then read as its non-null `T` — an assignment
  (`val z: Int = n`), an arithmetic/comparison operand (`n + 1`, `n > 5`), a function argument, or a
  `return` — now UNWRAPS `Nullable<T>.Value` instead of loading the raw struct. Previously the raw
  `Nullable<T>` slot flowed into an `int`/`long`/`double` context, giving garbage (`1` for `7`), an
  `InvalidProgramException`, a SIGSEGV in arithmetic, or a wrong branch (`n > 5` taking the else). kotc
  now emits the unwrap at each JVM-style coercion slot (the smart-cast carries no IR cast node, mirroring
  the JVM's implicit `Integer.intValue()` coercion). Covered by `cases/il-nullableprim` in the JVM-oracle
  differential.
- **Value-type nullable generics (`T?`) round-trip correctly.** A generic `T?` erases to `System.Object`
  (the only CLR rep that carries a real null for a value `T`), so `listOf(10,20).firstOrNull()` returns
  `10`/`null` (not `0`), and value-type `sequence{}` / `asSequence().filter{}` / `List<Int?>.filterNotNull()`
  run to completion instead of NRE/InvalidProgram.
- **Generic collection dispatch on BCL-aliased types.** Kotlin's use-site `in`/`out` variance (a JVM
  erasure-ism) is realigned to the CLR's invariant generics, so `val x by map` delegation,
  `groupBy`/`associate*`, `.map`/`.filter`/`.add`/`.size`, and a mutable-map `for ((k,v) in m)` dispatch
  the right slot instead of `EntryPointNotFound`.
- **Null renders as `"null"` consistently.** `println(null)`/`print(null)`, a null operand in `"$x"` /
  `"" + x`, and `x.toString()` all render the literal `"null"` (Kotlin semantics) rather than an empty
  string; nested collections/maps stringify Kotlin-style (`{a=[1, 2]}`) instead of raw .NET type names,
  recursively.
- **Evaluation-order fixes:** a value-producing `try` in an operand slot (`1 + try{…}`) is hoisted to a
  preceding temp; a `when`-subject / safe-call receiver / `x in a..b` operand evaluates exactly once;
  strict left-to-right operand order is preserved.
- **~55 further correctness fixes**, including: `Char - Char → Int` and `Char + Int → Char`;
  `Char.digitToIntOrNull()` value+null join; ordinal `String.compareTo`; `MutableList.set`/`removeAt`
  return the old element; `catch (IndexOutOfBoundsException)` catches both .NET out-of-range types;
  `x is Collection<*>` / `is Map<*,*>` holds for value-type collections; `HashSet`/`HashMap(capacity,
  loadFactor)` construct; float `NaN <=`/`>=`; `return` inside nested `try`/`finally`; store/return
  coercion into reference/nullable slots; `printStackTrace()` on any `Throwable`-typed receiver.
- **Number parsing matches JVM** (deviation, recorded in `docs/dotkt-semantics.md`): `String.toInt()`/
  `toLong()`/… are strict base-10 and throw a real catchable `NumberFormatException`; `toDouble()`/
  `toFloat()` parse invariant-culture and reject the group separator (`"3,14".toDouble()` throws).
- **`kotlin.time`:** `2.seconds + 3.seconds` and the `Duration` value-class arithmetic/formatting run
  end-to-end.
- **Unsigned `UInt`/`ULong` division, remainder, and `toString(radix)`** now have real pure-Kotlin bodies
  (previously threw); **enum reflection** `enumValues<T>()` / `enumValueOf<T>()` / `enumEntries<T>()` work
  (documented gaps for non-inlined generic contexts).
- **Generic `Array<T>` ops** (`copyOf`, `copyOfRange`, `plus`, `plusElement`, `orEmpty`, `arrayOfNulls`)
  run pure-Kotlin (generic `newarr !T`, reified on the CLR).
- **`kotlin.Result` / `runCatching`, user `Comparable<T>` sorting, `Map` property delegation, and
  cross-module default arguments** (a 2-tier `[DefaultParameterValue]` / embedded-BIR-splice rule) all run.
- **`@kotlin.concurrent.Volatile` is now a real CLR volatile field** (`modreq(IsVolatile)` + the
  `volatile.` prefix on backing-field access) — it was previously a silent no-op.

### .NET interop

- **Wide synthesized delegates restore as Kotlin function types (#97, facadegen).** For function values
  wider than `System.Func`/`Action` (which cap at 16 value params + `TResult`), ilemit synthesizes module-local
  `DotKt.Runtime.CompilerServices.KFunc`N`/`KAction`N` delegates. facadegen already restores a member typed by
  such a wide delegate as a Kotlin function type `(I1..In)->R`: its `MapT` delegate→`fn` path reads the
  delegate's `Invoke` signature directly with no arity cap and independent of the `[CompilerGenerated]` stamp
  (`IsDelegate` walks the base chain to `System.MulticastDelegate`), so `KFunc`18` maps to `(Int×17)->Int`
  correctly. Fixed `scripts/verify-wide-delegates.sh`: its restore assertion was a stale grep for the retired
  pre-#37-m4 text meta grammar (`tlfun accept … cb:func:[…]`) and now checks the current structured-JSON `fn`
  node — closing the facadegen half of #97 (the bir2cir app-throw half landed in `0df45d9`).

- **Strict unsigned-byte mapping: `System.Byte` ⇔ `kotlin.UByte`, `System.Byte[]` ⇔ `UByteArray` (#53).**
  `System.Byte` is unsigned, so it now maps to Kotlin's unsigned `UByte` (and `byte[]` to the specialized
  native `UByteArray`), consistent with the wider unsigned widths (`UInt16↔UShort`, `UInt32↔UInt`,
  `UInt64↔ULong`) and with the forward direction; `kotlin.Byte` (signed) stays `System.SByte`. This ends the
  old lossy collapse where a .NET byte `200` re-consumed as a signed `Byte -56`, and makes `UByte`/`UByteArray`
  round-trip faithfully. `UByteArray` is a native `System.Byte[]` (not a wrapper, not `Array<UByte>`); its
  `ubyteArrayOf`/indexing/`size` are native array ops, and `UByteArray.toByteArray()` / `ByteArray.toUByteArray()`
  reinterpret between the runtime-interchangeable `System.Byte[]`/`System.SByte[]` (a view, not a copy). Also
  completes bir2cir's `PrimitiveBirName` (the missing `sbyte` + unsigned family) and resolves injected unsigned
  return types in the frontend (they previously degraded to `Any?` in a return position). See
  `docs/dotkt-semantics.md` §9b.
- **De-invert the internal 8-bit shorthand tokens to match .NET (#54).** The compiler's internal
  primitive shorthand `"byte"` used to mean SIGNED (`kotlin.Byte` = `System.SByte`) and `"ubyte"` UNSIGNED
  (`System.Byte`) — inverted vs .NET/CIL/C#, where `byte` is unsigned and `sbyte` is signed. `int`/`short`/
  `long` already agreed with .NET; `byte` was the lone outlier that followed Kotlin's naming, leaving an
  active semantic inversion in the CLR-facing bir2cir↔ilemit layer. The 8-bit tokens are now .NET-aligned:
  the token `"sbyte"` is SIGNED (`kotlin.Byte`→`typeof(sbyte)`) and `"byte"` is UNSIGNED
  (`kotlin.UByte`→`typeof(byte)`), uniform with `int`→`Int32`/`short`→`Int16`/`long`→`Int64`. Purely an
  internal token rename across bir2cir (producer) and ilemit (consumer) — the CIR `{t:fqn,name:…}` wire value
  changes spelling (`byte`→`sbyte` for signed, `ubyte`→`byte` for unsigned) but byte VALUES are identical; the
  JSON schema/validator treat `fqn.name` as an opaque identity string, so the frozen contract is untouched.
- **Idiomatic .NET events: `w.Changed += handler` / `-= handler`.** A .NET event surfaces as a
  `ClrEvent<T>` member with `+=`/`-=` operators (replacing the `add_`/`remove_` accessor stopgap), for
  instance, static, and interface events. The event Kotlin↔CLR relation now lives entirely in bir2cir.
- **Interop without static registries (internal, A2).** All four process-global name-keyed side-tables in
  kotc were replaced by pure projections of facadegen metadata keyed on the resolved IR `ClassId`/
  `CallableId`; the emitted BIR is byte-identical. User-visible consequence: same-name top-level overloads
  across different DotKt file facades now route 1:1 (previously they collided last-wins).
- **facadegen symbol-surface completions:** constructed-generic member types (`IList<Widget>`,
  `Dictionary<String,Widget>`), transitive (reachable-closure) injection, aliased imports (`import … as
  SB`), operators on generic .NET types, C#-origin `[Extension]` methods, generic constraints +
  declaration-site variance round-trip, and same-name arity families (`Task` vs `Task<T>`).
- **facadegen surfaces a library's top-level `val`/`var` (#34b, facadegen side).** A top-level Kotlin
  `val greeting = "hi"` compiles to a plain `Public|Static` FIELD on the file class (no `get_`/`set_`
  accessor — only backing-field-LESS props, i.e. extension/computed props, get accessors), so a second
  module consuming the DotKt `.dll` could not `import somelib.greeting`. facadegen now emits a
  `tlprop <name> <type> <ro|rw>` meta token per such field (`EmitKotlinFileClass`), mirroring the
  `tlfun`/`tlextprop` top-level path; the .NET file-class FQN rides the enclosing `file` line, and `val`
  vs `var` is read from `[KotlinReadOnly]`/`InitOnly`. The consuming-side restore (kotc `ClrTypeInjection`
  parsing `tlprop` + `BirEmitter` routing the read/write to `staticField`/`staticFieldSet` on the
  referenced file class, plus a `readOnly` flag on top-level `val` fields for the val/var distinction) is
  routed to kotc; the round-trip case `roundtrip-toplevel-val` (reads a library top-level property
  DIRECTLY, no function workaround) is `RT_XFAIL` until that lands, then flips to FIXED.
- **A referenced DotKt library's top-level `val`/`var` is now read DIRECTLY cross-module (#34b, kotc
  side).** kotc consumes facadegen's `tlprop` token: `ClrTypeInjection` restores a NON-extension
  top-level property (`createTopLevelProperty` with no `extensionReceiverType`; `val`/`var` from the
  `ro`/`rw` flag), and `BirEmitter` routes its read/write to `staticField`/`staticFieldSet` on the
  referenced .NET file class (not the wrong `fileClassOf(p)`/`get_`/`set_` path — a plain top-level
  val/var is a static field with no accessor). The producer side stamps a `readOnly` flag on top-level
  `val` static fields so the val/var distinction survives the round-trip. `import somelib.greeting`
  works with no function workaround; `roundtrip-toplevel-val` is now GREEN (its `RT_XFAIL` pruned),
  closing #34b end-to-end (a top-level val fully round-trips).
- **Round-trip carriers:** re-consuming a DotKt `.dll` as Kotlin now restores `sealed` (modality +
  cross-module inheritance enforcement + exhaustive `when` with no `else`) and `fun interface` nature.
  (Deviations, `docs/dotkt-semantics.md` §10: a `fun interface` restores the nature but a bare lambda
  still won't SAM-convert; an `enum class` re-consumes as an `object` of `val`s — both pinned-compiler
  limits.)
- **`CharSequence` is `System.String`** and **`Appendable` is `System.Text.StringBuilder`** on the CLR
  (each a JVM abstraction with a single faithful CLR representation), so `joinToString`/`joinTo` and
  CharSequence polymorphism run. `CharSequence` is an immutable snapshot, not a live view (deviation,
  §5b); a user `class S : CharSequence` keeps a synthetic polymorphic interface.
- **Suspend function-type POSITIONS now carry round-trip metadata (H2).** A `suspend (…) -> T` in a
  parameter / return / property / field position has its type slot erased to `object` (a suspend-lambda
  value is a `Continuation`-based state-machine object, not a `Func` delegate), which previously destroyed
  the suspend origin AND its arg/return shape — `fun run(block: suspend () -> T)` was indistinguishable
  from a plain function-typed one in the emitted metadata. bir2cir now records the pre-erasure
  `sfunc:<ret>:<args>` shape as a positional fact (`suspendFnType`/`retSuspendFnType`) and ilemit stamps
  it as an embedded `[KotlinSuspendFunctionType(shape)]` at every such position (mirroring the
  `[Nullable]`/`[KotlinInline]` metadata-carrier model — a SHAPE string, not a bare flag, since the CLR
  type is `object`). Verified applied+reflectable on the stdlib coroutine intrinsics at all four position
  kinds (`createCoroutine`/`startCoroutine` receivers, `suspend()`'s return, `DeepRecursiveFunction.block`
  property). NOTE: the metadata now SURVIVES emission, but facadegen does not yet reconstruct the
  `suspend (…) -> T` type on re-consumption — that final restore hop requires a kotc `ClrTypeInjection`
  change (an `sfunc:` case in `coneOf` building `kotlin.coroutines.SuspendFunctionN`), tracked separately.

### Standard library

- **The compiler's hand-written stdlib lowerings are retired into a real pure-Kotlin CLR stdlib.**
  `kotlin.math`, `String`/`Char` ops, `trim`/`pad*`/`replace` (STRING_OPS), `coerceIn`/`coerceAtMost`/
  `coerceAtLeast`, `isBlank`, `println`/`print`, `Regex`, `AutoCloseable`/`use{}`, `Lazy`/`by lazy`,
  `Throwable.message`/`cause`/`printStackTrace`, the collection and `StringBuilder` member slots, and
  `Int/Long.toString(radix)` now run their real Kotlin bodies (bound via `@ClrTypeAlias`/`@ClrIntrinsic`
  on the reference stdlib and substituted by bir2cir). This is the cardinal-rule payoff: correctness
  fixes land stdlib-side, never as compiler special-cases.
- **`Regex`** runs on real bodies: `matches`/`find`/`replace`/`replaceFirst`/`split`/`.value`/`.pattern`/
  `groupValues`, plus named + indexed groups (`replaceFirst` no longer corrupts memory).
- **`lazy {}`** is pure-Kotlin and thread-safe by default (`SynchronizedLazyImpl`) with a lock-free
  double-checked-locking fast read (one volatile load on the hot path), backed by the now-real `@Volatile`.
- **`Map`/`MutableMap` → `IDictionary<K,V>`** (both — deliberately NOT a read-only/mutable split, §5c) with
  Kotlin-semantic members via `ClrMapDefaults`; core collection ops (`map`/`filter`/`fold`/`toList`/…) run
  on real Kotlin bodies over BCL collections.
- **`MutableMap.merge(key, value) { old, new -> … }`** now works (C2). On Kotlin/JVM `merge` is the
  `java.util.Map.merge` member (a `java.util.function.BiFunction` overload); on the CLR that erased SAM
  materialized the Kotlin lambda as `Func<V,V,object>` and then `castclass`-ed it to the `? super V`-erased
  `Func<object,object,object>` → `InvalidCastException`. `merge` is now declared on the `MutableMap` builtin
  with a Kotlin function-type parameter (the frontend binds to THIS overload, so no cast), routed to
  `ClrMapDefaults.clrMapMerge` for BCL-aliased receivers. Semantics mirror `java.util.Map.merge`
  (absent → insert; present → remap; null result → remove).
- **`groupBy {}` read surface is covariance-safe (C2).** `listOf(1,2,3,4).groupBy { it % 2 }` returns a
  `Map<K, List<V>>` (`IDictionary<K, IReadOnlyList<V>>`) but the runtime object is the `Dictionary<K, MutableList<V>>`
  (`IDictionary<K, IList<V>>`) that `groupByTo` built and mutated — and CLR `IDictionary<,>` is INVARIANT in the value,
  so the runtime map is not assignable to the read interface: reading it (`toString`/`m[k]`/`for ((k,v) in m)`/`.entries`/
  `.keys`/`.values`) threw `EntryPointNotFound`/`InvalidCastException` through the mismatched generic slot. The
  `ClrMapDefaults` READ helpers now route through the NON-GENERIC `System.Collections.IDictionary` (implemented by every
  `Dictionary<K,V>` regardless of V) via `IDictionaryEnumerator` + `get_Item(object)` — the read-side mirror of bir2cir's
  write-side `MapVarianceRealign`. Regular `mapOf`/`mutableMapOf` read/iterate/`toString` are unaffected. Verified against
  the JVM oracle (`cases/il-groupby2`, added to `verify-differential`).
- **`groupBy {}.mapValues {}` and a direct `m.size`/`m.containsKey` on a groupBy result are covariance-safe (#29).**
  `size` and `containsKey` are now UNBOUND on the `Map`/`MutableMap` interface (their `@ClrIntrinsic("Count")`/
  `("ContainsKey")` bindings, which read through the INVARIANT generic `IDictionary<K,V>`, are removed); bir2cir Rule 5m
  routes `get_size`/`containsKey` on a `Map`/`MutableMap` owner to the covariance-safe `ClrMapDefaults.clrMapSize`/
  `clrMapContainsKey` (non-generic `ICollection.Count` / `IDictionary.Contains`), exactly as `get`/`get_keys`/`get_values`
  already route. This also makes `mapValues`' transitive `mapCapacity(this.size)` pre-size covariance-safe, so
  `listOf(1,2,3,4).groupBy { it % 2 }.mapValues { it.value.size }` no longer throws `EntryPointNotFound`. Normal
  `mapOf`/`mutableMapOf` `size`/`containsKey` stay correct. Verified against the JVM oracle (`cases/il-mapvalues`).
- **A value-element collection's `.indices`/`.lastIndex` are covariance-safe (#30).** `Collection<*>.indices` used a
  star projection whose receiver lowered to the reified `IReadOnlyCollection<object>`; a value-element runtime list
  (`ArrayList<int> : IReadOnlyCollection<int>`) does not implement it — CLR generic covariance excludes value-type
  args — so reading `size` (`get_Count`) threw `EntryPointNotFound`. Genericized to `Collection<T>.indices` (a
  source-compatible generalization) so the receiver stays `IReadOnlyCollection<T>` and the size read is covariance-safe
  for value elements — the same shape as the already-working `List<T>.lastIndex`. `listOf(1,2,3).indices` and
  `.lastIndex` now work for `Int`/`Double` elements; reference-element collections stay green. Verified against the
  JVM oracle (`cases/il-indicesv`).
- **Nested collections/maps inside `Pair`/`Triple.toString()`** render Kotlin-style (C11):
  `(listOf(1, 2) to listOf(3, 4)).toString()` is `([1, 2], [3, 4])`, not the raw
  `(System.Collections.Generic.List\`1[System.Int32], …)`. A tuple component's erased generic static type
  used to reach .NET's `Object.ToString()`; components now route through the runtime collection-aware
  stringifier (`clrRenderTupleElement` → `clrElemToString`), matching `println(list)`.
- **`@ClrProperty`** explicit accessor binding (READ/WRITE) replaces the fragile `get_`/`set_`
  intrinsic-string prefix sniff.
- **`String.format`** binds to .NET `String.Format` — use .NET composite format (`"{0:F2}"`), NOT Java
  printf (`"%.2f"`) (BREAKING deviation, §5).
- **`abs(Int)`/`abs(Long)` now WRAP at `MIN_VALUE`** (matching Kotlin's unchecked negation:
  `abs(Int.MIN_VALUE) == Int.MIN_VALUE`) instead of throwing `OverflowException`. The `@ClrIntrinsic("System.Math.Abs")`
  binding — whose checked overload throws at `MIN` — is dropped for the integer overloads in favor of the
  pure-Kotlin body `if (n < 0) -n else n`; the `Float`/`Double` overloads keep their `System.Math.Abs`
  binding. Verified against the JVM oracle (`cases/il-mathabs`, added to `verify-differential`).
- **Deterministic `String`/`Double`/`Float` `hashCode()` bodies added** (polynomial hash for `String`,
  bit-based for `Double`/`Float`) replacing reliance on .NET's randomized/native `GetHashCode`. The
  correct stdlib bodies now ship, but they are still SHADOWED at the call site by kotc's universal-method
  intercept (`BirEmitter.kt` `isBuiltin && name=="hashCode"` → `objMethod GetHashCode`), so `"Aa".hashCode()`
  remains non-deterministic until that intercept is gated to fall through when the receiver type declares its
  own `hashCode` — a compiler-layer follow-up, not a stdlib change.

### Compiler architecture (4-layer / layer purity)

- **kotc `clrName()` split into an origin-gate `isExternalNetType()` + the FQN emitter `clrName()` (#93).** The one
  accessor served two purposes — a boolean "is this a facadegen-injected .NET type?" gate (~20 call sites that only
  tested truthiness) and .NET-FQN identity emission (~10 sites that consumed the returned string). The truthiness sites
  now call the intent-named `isExternalNetType()` (defined as `clrName(decl) != null`); `clrName()` is reserved for the
  FQN-emission sites. Pure clarity refactor, verified byte-identical: the full stdlib BIR corpus (251 files) is
  unchanged by the split.

- **All Kotlin round-trip metadata GENERATION moved from ilemit into bir2cir; ilemit is now Kotlin-metadata-FREE (#71
  S2).** ilemit no longer synthesizes the embedded attribute classes or DECIDES which Kotlin modifier maps to which
  attribute — a new bir2cir pass `RoundtripMetadata` GENERATES every `[KotlinFunction]`/`[KotlinFileClass]`/
  `[KotlinFunInterface]`/`[KotlinSealed]`/`[KotlinReadOnly]`/`[KotlinInline]`/`[KotlinSuspendFunctionType]` and the
  standard `[Nullable]`/`[NullableContext]` NRT attributes as ordinary CIR `attrs`/`retAttrs` entries, plus the 9
  attribute-class definitions (`internal sealed : System.Attribute`) as ordinary CIR type decls in a dedicated synthetic
  file. ilemit only STAMPS them dumbly through its generic `BuildCab`/`ConstArgValue` path (extended with a `bytes`
  base64 arg kind and an exact-ctor-type pick that disambiguates the dual-ctor `NullableAttribute`). Deleted from ilemit:
  `Emitter.CompilerServices.cs` (EnsureKotlinAttrs / DefineEmbeddedAttr), every `ApplyKotlin*`/`ApplyNullable*`/
  `ApplySuspendFnType`, `DecodeCarrier`/`ReadByteArrayArg`/`ReadNullableFlags`, and the `_stripMetadata` runtime-strip
  gate — bir2cir now both GENERATES the round-trip metadata (ref + app builds) and STRIPS kotc's verbatim user
  annotations (runtime build, `RoundtripMetadata.StripRuntimeAttrs`), so a runtime-build CIR reaches ilemit already
  attribute-free. Behavior-neutral for the metadata surface: an attribute dump of `DotKt.Private.Stdlib.dll` (the
  reference assembly facadegen reads) and a sample user dll is byte-identical before/after. The runtime assembly
  `DotKt.Stdlib.dll` is now genuinely round-trip-metadata-free as designed (the old build leaked `[Nullable]`/
  `[KotlinReadOnly]`/`[KotlinSuspendFunctionType]` via un-gated declare-time stamps — never consumed, now gone).

- **Cross-module inline splicing re-homed from ilemit to bir2cir, on a RAW-BIR `[KotlinInline]` carrier (#71/#75 S1).**
  ilemit no longer splices inline bodies and no longer builds the `[KotlinInline]` payload from post-lowering CIR. A new
  bir2cir pass `InlineBirStash` runs FIRST (before any lowering) and captures every `inline` method's RAW pre-lowering
  facts `{v,fqn,owner,recv,typeParams,params,ret,body}` into one opaque base64 string `inlineBir`; ilemit stamps that
  verbatim as the `[KotlinInline(version,bytes)]` carrier (`Emitter.Metadata.ApplyKotlinInline` / `Emitter.Assembly.cs`).
  kotc emits a generic `callInline` node (replacing `inlineSplice`) carrying the call's bindings + a `fallback` plain
  call; bir2cir `InlineSplice` resolves the callee's raw body (cross-module from `[KotlinInline]` off the `--ref`'d
  assembly via `ReferenceMetadataIndex.TryReadInlineBir`, same-module from `InlineBirStash`'s index) and SPLICES it at
  BIR level — positional type-param substitution (`tv{scope:method,i}` → the call's `typeArgs[i]`), receiver/value
  params bound to fresh temps, each lambda-param `invoke` replaced by the carried caller-scope lambda body (freshened per
  invocation), origin returns routed to a result-local + end-label, a bare non-local `return` kept as the caller's
  return — into a value-producing `valueBlock`. Because `InlineSplice` runs before all lowering, the spliced raw body
  re-lowers IN THE APP's context (`@ClrIntrinsic` binds against the app ref.dll, generics resolve with call-site type
  args, reified is free on CLR) — the fix the old post-lowering ilemit splice could never do. Deleted from ilemit:
  `Emitter.InlineSplice.cs` (`EmitInlineSplice`/`EmitSplicedStmts`) + its 4 touchpoints + the `inlineSplice` CIR node.
- **Scope functions (`let`/`run`/`with`/`apply`/`also`) and `use{}` retired from kotc hardcodes into the generic
  `callInline` → bir2cir `InlineSplice` engine (#71/#75 S3).** kotc's mechanism-3 — the `SCOPE_FUNCTIONS` FQN set +
  `inlineScope`/`inlineUse`/`scopeCall` hand-inliners (and the now-dead `containsSuspend`/`isSuspensionCall`) — is
  DELETED. A call to an `@kotlin.internal.InlineOnly` cross-module inline fn taking a lambda now emits an OWNER-LESS
  `callInline` (kotc cannot name the stdlib file class — the whole stdlib rides the klib, facadegen supplies no
  `kotlin.*` metadata); bir2cir resolves the hosting file class from the ref.dll (`TryResolveInlineOwner`, a
  name|pc|ga → owner reverse index over the `[KotlinInline]` payloads, poisoned on collision) and splices the real
  ref.dll body — so `use{}` gets its genuine `try/finally` + `closeFinally` fidelity, and `with(x){ this.f() }` binds
  the lambda's extension receiver (kotc now LIFTS a `T.()->R` lambda's receiver as a leading carrier param; the splice
  binds it positionally, since the stdlib `block.invoke(recv)` passes the receiver as arg[0]). kotc's `visOf` promotes a
  `@PublishedApi internal` decl to `public` (matching Kotlin/JVM) so the spliced `use{}` body's cross-assembly
  `closeFinally` call binds; ilemit gains two IL-correctness fixes the spliced `use{}` needs — a `goto` that exits a
  protected region now emits `leave` (runs the intervening `finally`), and the return-scan descends into expression
  positions (a non-local `return` nested in a spliced `valueBlock`). bir2cir `InlineSplice` fails loud rather than
  silently dropping a `suspendCall` carried in a discarded lambda body on fallback.
- **Inline splicing narrowed by escape analysis + the cross-module engine hardened (#75 S4a / #95).** kotc gains a fresh
  pure-IR predicate `lambdaNeedsSplice`/`callNeedsSplice` (`BirEmitterInline.kt`): a lambda arg is source-inlined ONLY
  when compiling it as a separate CLR delegate would change semantics — a non-local `return` whose target is outside the
  expansion region, a non-local `break`/`continue` to an outer loop, or a suspend call inherited into a non-suspend
  inline lambda (arm c). The region descends through the literal-lambda args of NESTED inline calls (so `a { b { return
  } }` correctly splices both — a direct-arg-only predicate would delegate-compile `a` and silently DROP the caller
  return) and stops at every other function boundary; conservative on any uncertain shape. The two cross-module gates are
  narrowed to `callNeedsSplice`: the facadegen path keeps its `extRecv == null` guard (and now fails loud on an escaping
  receiver call rather than dropping it), and the owner-less path DROPS the `@InlineOnly` restriction — closing today's
  silent app-side `xs.forEach{ return }` non-local-return drop (any body-less inline+lambda callee with an escaping arg
  now emits an owner-less `callInline`; the non-escaping majority stays a plain delegate call, the LINQ model). The
  `fallback` slot is DELETED from both cross-module emitters — under #95 a `callInline` is emitted only when a splice is
  REQUIRED, so a fallback would be a miscompile; the engine now FAILS LOUD (no dual-track). bir2cir `InlineSplice`
  hardening: (§4.1) a carrier-hygiene fix — `CollectIds` remints only DECLARED-label ids, so a non-local
  `goto <caller-loop-label>` inside a carrier is left resolving against the caller's live label instead of being
  dangled/mis-remapped (a silent-miscompile class made live by non-local break/continue); (§4.2) the `[KotlinInline]`
  overload key widened to `owner|name|pc|ga|recv0` (recv0 = first param's type FQN — splits `Iterable.forEach` from
  `IntArray.forEach`, computed identically by kotc, `InlineBirStash`, and the ref.dll reader); (§4.3) dispatch-receiver
  binding for member inline funs; (§4.4) lambda-forwarding — a lambda param passed BY NAME into a nested stdlib-inline
  call (`filter`→`filterTo(dest, predicate)`) is converted from a plain `callStatic` into a `callInline` carrying the
  caller's carrier, so the escaping lambda splices where the inner op invokes it; (§4.5) the fallback path replaced by a
  fail-loud with the full overload key; (§4.6) a cross-module `newDelegate`-in-payload guard. Nine new gate samples
  (`cases/il-inline-*`) pin the matrix — nested NLR, outer-label delegate+inner splice, non-local break through a
  carrier, own-label continue on the delegate path, mutable-capture write-through, `filter`→`filterTo` forwarding, and
  arm-c suspension spliced into a state machine.
- **kotc `BirEmitter.kt` decomposed into 6 cohesive sibling files (#41) — a purely mechanical, verify-by-refactor
  carve-out.** The 4633-line `BirEmitter.kt` is now a ~547-line core (class decl + ctor, the whole run-scoped
  mutable-state block, diagnostics, the type-naming quartet, ref-cell machinery, `scopeCall`, `newExc`/`throwExpr`,
  and `emitFile`); the rest moved verbatim into six `internal fun BirEmitter.<name>(…)` extension siblings in
  `kotc.backend` — `BirEmitterTypes` / `BirEmitterControlFlow` / `BirEmitterDeclarations` / `BirEmitterLifts` /
  `BirEmitterInline` / `BirEmitterCalls` — mirroring the existing `BirEmitterExpressions`/`Statements` split. No
  behavior change: every function moved whole (`call()` and its ~970 lines intact — never split), all shared state
  stays on the class reached via the receiver (no counter reset/param-ification), the nine `private` members handled
  per plan (five widened `private`→`internal` around `call()`, the rest file-private in their destination). Gated per
  batch by a byte-identical BIR-corpus `diff` over the stdlib emit + 154 verify-il inputs (1495 `*.bir.json`, empty
  diff every batch) and end-to-end by the full gate (verify-il 251/0 / -differential ALL-MATCH / -roundtrip / -ktproj /
  -schema). Design: `docs/design-kotc-decompose-41.md`.
- **ilemit `Program.cs` decomposed into 8 cohesive sibling files (#41, the last half) — a purely mechanical,
  verify-by-refactor carve-out.** The 4226-line `Program.cs` is now a ~172-line driver + core-state file (`static
  class IlEmit` = `Main`/`MergeByFileClass`/`LoadInputDocument`, plus the `Emitter` overview comment, all core
  instance fields, `Trace`/`T`, `BuildStdlibMode`, the ctor and `EffectiveTps`); the rest moved verbatim into eight
  `sealed partial class Emitter` siblings — `Emitter.Types` / `Emitter.Delegates` / `Emitter.Resolve` /
  `Emitter.ClrInterop` / `Emitter.Operators` / `Emitter.InlineSplice` / `Emitter.Bodies` / `Emitter.Assembly` —
  joining the existing `Expressions`/`Statements`/`Metadata`/`CompilerServices`/`ReverseBridge` split. No behavior
  change: every member moved whole (never split), all shared state reachable within the one partial class (fields
  stay in `Program.cs` except the five per-cluster sets the plan relocates), no overload dropped (MapType×3,
  FuncType×2, ResolveMethod×2, StampCompilerGenerated×2 all preserved). The cross-module inline-splice path
  (`EmitInlineSplice`/`EmitSplicedStmts` + its four `_inline*` fields) is quarantined into `Emitter.InlineSplice.cs`
  with a header enumerating the four external touchpoints #75 step-3 deletes with it. IL/PE bytes are not diffable,
  so verification is behavioral: gated per batch by verify-il (the inline-splice batch also by verify-roundtrip) and
  end-to-end by the full gate (verify-il 251/0 / -differential ALL-MATCH / -roundtrip / -ktproj / -schema). Design:
  `docs/design-ilemit-decompose-41.md`.
- **bir2cir `Program.cs` decomposed into 21 per-pass files (#41) — a purely mechanical, verify-by-refactor
  carve-out.** The 7007-line `Program.cs` is now a ~705-line driver-only file (`Bir2Cir`/`Main`, the `Pipeline`
  pass driver with its per-pass ordering + call-site gate comments, `BuildStdlibMode`/`DriverOptions`/`BirFile`/
  `CirFile`/`JsonOptions`/`UsageException`); each pass and the shared `ReferenceMetadataIndex`/`BirTypeLowering`
  infrastructure moved verbatim (header comment + run-scoped static state intact) into a same-named sibling file,
  mirroring the 33 already-extracted passes. No behavior change: every class moved whole (no class split, no
  static field param-ified), gated per batch by a byte-identical CIR-corpus `diff` over the stdlib (both ref and
  runtime modes) plus 254 app BIR inputs, and end-to-end by the full gate (verify-il / -differential / -roundtrip /
  -ktproj / -schema). — non-local `return` through `repeat(n){}` restored via a spliced inline
  body.** kotc now recognizes a literal-lambda `repeat(n){ … }`, splices the lambda body UN-CLOSURED into a new
  `callInline` BIR node (carried in the caller's scope, so a bare `return` inside stays a plain `return` = the
  caller's return, and a `return@repeat` routes to a `goto` = `continue`), and bir2cir's new `InlineSplice` pass wraps
  that body in the counted loop (`repeatInline`) with the body SPLICED rather than delegate-invoked. This restores the
  non-local return that #73 M7's delegate-invoke loop dropped (recorded then as a limitation): `repeat` is an `inline`
  fun, so a non-local return from its lambda is legal Kotlin. Gate: `cases/il-repeatnlr` (non-local return +
  `return@repeat` + capture + implicit `it`). The `callInline` node + `InlineSplice` pass are the reusable
  infrastructure the remaining #75 stages (scope functions / `use{}` / same- and cross-module inline) extend.

- **The A2 tail — the remaining .NET SHAPE decisions for facadegen-injected owners moved from kotc to bir2cir (#73,
  audit item M4: newClr / field-property / clrOverride), plus the `strReversed` stdlib fix (M10).** #61 moved the
  .NET CALL family; M4 completes it for the SHAPES it left behind. kotc now emits the plain Kotlin-identity node — a
  `new` (not `newClr`), a `field`/`setField` (not `clrPropGet`/`clrPropSet`), and a plain override accessor carrying
  only its `overrides` marker (not a `clrOverride` field) — keeping the .NET-FQN identity `clrName()` yields in the
  type/owner slot (never the Kotlin ClassId name, which diverges from the .NET name for arity-qualified/nested
  injections). bir2cir derives the .NET shape off the loaded reference assemblies: `TransformNew` reshapes a `new` on
  an injected owner (resolved via `ResolveNetType`) to `newClr`; `NetInteropBinding.ReshapeField` binds a `field`/
  `setField` on a .NET owner to `clrPropGet`/`clrPropSet` (ilemit's `EmitClrPropGet/Set` is struct-receiver-safe and
  const-field-inlining, unlike the plain-field external Ldfld route); `DeclarationRename` stamps `clrOverride` when an
  accessor's `overrides` marker resolves to a real .NET base CLASS (this also RETIRES `clrAccessorMethod`, whose
  output duplicated the `userAccessors` accessor — the latent double `get_Message` in `il-netbase2` is now a single
  method). `clrOverride`/`newBoundClrDelegate`/`clrEventGet` stay as ilemit/bir2cir vocabulary (dedicated node kinds,
  not demoted). **M10:** kotc's `strReversed` lowering is deleted; `"x".reversed()` now runs the real stdlib
  `CharSequence.reversed() = StringBuilder(this).reverse()` (a pure-Kotlin index-swap) — a new `TransformNew`
  coercion wraps a CharSequence ctor argument in the null-safe `kotlin.LibraryKt.toString(object)` so
  `StringBuilder(CharSequence)` binds to the BCL `StringBuilder(String)` ctor (System.Text.StringBuilder has no
  CharSequence ctor). Gate green: verify-il 249/0, differential 196/0, roundtrip + ktproj all pass.
- **The precondition/error helper family and the top-level `repeat(n){}` inline loop no longer bake their lowering in
  kotc — the recognition + synthesis moved to bir2cir (#73, audit items M6/M7).** kotc emitted the throw/condition for
  `require`/`check`/`error`/`TODO`/`requireNotNull`/`checkNotNull` (and the `kotlin.internal.ir.noWhenBranchMatchedException`
  intrinsic), and the counter loop for `repeat`, directly at the call site — stdlib-symbol semantics baked into the
  frontend. kotc now emits the FAITHFUL call (`callStatic owner:null method:<name>`, the intrinsic re-emitted like
  `ieee754equals`) and two new bir2cir passes synthesize the semantics FQN-keyed: `PreconditionLowering` re-emits the
  exact `cond`/`throwExpr`/`valueBlock` CIR (bare Kotlin exception FQNs; the BCL mapping stays downstream off the ref.dll
  `@ClrTypeAlias`; `requireNotNull`/`checkNotNull` keep the value-nullable `Nullable<T>` vs objEq split via the call's
  `typeArgs`), and `RepeatInlineLowering` re-emits the counted `repeatInline` (n once, index 0..n-1) invoking the action
  delegate — shape-agnostic over the lambda's `newClosure`/`newDelegate` form. These are @InlineOnly helpers with no
  rt.dll body, so bir2cir is the layer that must synthesize. Both passes run before `ClosureSynthesis`, gate on a Boolean
  first param / function-typed action, and skip user top-level shadows (app build) — a user `require`/`repeat` is never
  miscompiled. New gate sample `cases/il-precond`.
- **Companion & top-level EXTENSION/COMPUTED property accessors no longer bake the `get_`/`set_` slot name at the
  cross-module CALL site (#81, audit item M12).** Extending the #78 convention (bare property IDENTITY +
  `"prop":"get"/"set"` marker) to its three sibling sites — companion extension property, top-level extension property
  (C7), and top-level plain computed property — kotc now emits the faithful Kotlin identity and bir2cir shapes the
  accessor (or substitutes an `@ClrProperty`/`@ClrIntrinsic` binding off the ref.dll). bir2cir's `ClrPropNode` is now
  argcount-aware on the static axis: the marker fixes the get/set direction and a leading `__self` extension-receiver
  arg (`get` = `[__self]`, `set` = `[__self, value]`) becomes the .NET receiver, with the WRITE value taken past it.
  A ref-build-only `PropertyMarkerReconstruct` pass reconstructs the marker (the ref surface has no bindings to
  substitute), and `CharCodeInvokeLowering` matches the new `code`/`prop:get` shape for `Char.code`.
- **A computed companion property whose cross-module deserialized stub carries a phantom backing field no longer
  misroutes to a static-field access (#82).** `KTypeProjection.Companion.STAR` (`val STAR get() = star`) reported a
  spurious `backingField != null` (the FIR `hasBackingField` rule fires for any bodyless custom getter), so kotc read
  it as `staticField STAR` → ilemit "static field STAR not found". kotc now discriminates via the deserialized FIR
  accessor kind (`Fir2IrLazyProperty.fir.getter is FirDefaultPropertyGetter`) — reliable where the getter origin
  cannot (a metadata stub keeps `IR_EXTERNAL_DECLARATION_STUB` on both default and custom accessors) — gated at the
  companion computed call site only (`statFields` sees source IR, ground truth). Gate case `cases/il-kstar`.
- **kotc is now fully build-mode-agnostic: the `DOTKT_STDLIB_COMPILE` env var and the `stdlibCompile` field are
  DELETED, and the last three stdlib-only decisions moved DOWN to bir2cir (#72).** kotc used to branch on
  `DOTKT_STDLIB_COMPILE` in six places; each was either a CLR-representation decision that belonged in the
  Kotlin↔CLR layer or a build-mode gate that is properly keyed off `-Xstdlib-compilation`. Now: (1) a for-loop over a
  stdlib collection is emitted by kotc as a faithful `forIn` (source + `srcType` + element type + iterator `fallback`)
  and **bir2cir's `ForInLowering` turns it into a `forEachInline` (GetEnumerator)** in a stdlib self-build — the
  supertype walk kotc did over the IR hierarchy is reconstructed from the BIR type defs (so `ArrayList : MutableList`
  still matches); without it the rt.dll collection ops would enumerate via `iterator()`/`hasNext` and an app call would
  hit `EntryPointNotFound`. (2) `for (i in a downTo b)` is likewise classified in `ForInLowering` — FQN-keyed off the
  progression `srcType` + the stdlib `downTo` call identity (a user `infix downTo` no longer miscompiles) — and lowered
  to a counted `for` with side-effect-safe temp bounds (fixing the per-iteration re-eval of the `to` bound). (3) a decl
  whose signature mentions a >16-parameter function type (the `context()` overloads in package `kotlin`) has no
  `System.Func`/`Action`, so a new bir2cir `HighArityFunctionFilter` drops it (stdlib) / rejects it (app), running
  before `ClosureSynthesis` so no orphan closure types are synthesized. Property-accessor annotations are now emitted
  unconditionally (the same no-filter pass-through as plain methods); `unsupported()` is a uniform hard error (the two
  `.NET`-method callable-reference stubs in `Indent.kt` were rewritten as lambdas); and the frontend-pipeline select in
  `ClrCliPipeline` re-keys off `arguments.stdlibCompilation`. `DOTKT_STDLIB_COMPILE` is gone from every tool and script.
- **The stdlib-build mode is now ONE CLI flag `--build-stdlib=metadata|runtime`, not an env-var soup (#69).**
  bir2cir and ilemit each used to read a tangle of environment variables (`DOTKT_STDLIB_COMPILE` +
  `DOTKT_STDLIB_SUBSTITUTE` + `DOTKT_STRIP_METADATA`) to select the reference/runtime/app build. Both now take a single
  `--build-stdlib` flag (absent = an app build); bir2cir maps it to `DriverOptions.StdlibMode` and ilemit to
  `Emitter.BuildStdlibMode` (`_stdlibStub = mode != App`, `_stripMetadata = mode == Runtime`). The three env vars are
  RETIRED from both tools (kotc kept its own `DOTKT_STDLIB_COMPILE` gate at the time; that too is now gone — see #72). Pure
  flag-source swap — every branch, mode value, and emitted byte is unchanged, so `DotKt.Private.Stdlib.dll` +
  `DotKt.Stdlib.dll` are byte-identical (modulo the non-deterministic PE timestamp + MVID GUID). The build scripts
  (`build-stdlib.sh` + `build-stdlib-{ref,rt}.sh`) pass the flag to both tools instead of exporting the env vars.
- **kotc no longer decides the .NET call SHAPE for facadegen-injected interop — bir2cir binds it off the
  Reference Assemblies (#61 / A2).** kotc's backend used to read facadegen's `.NET`-marking injection metadata and
  emit the CLR call shape itself (`clrStatic`/`clrInstance`/`clrPropGet`/`clrPropSet`/`clrGeneric*`/indexer/`op_`)
  for an `import System.X` / referenced-`.NET`-library member call. That is CLR knowledge in the frontend — a
  deviation from the confirmed layer table (facadegen reads the Reference Assemblies to inject FIR metadata;
  **bir2cir** reads them to RESOLVE cross-assembly types = where `.NET` binding belongs). Now kotc is
  **.NET-agnostic**: to it a facadegen-injected library is "a weird Kotlin library with PascalCase packages", so it
  emits a PLAIN `callStatic`/`callInstance` by the owner's FQN identity carrying only frontend FACTS (static-ness,
  the `get_X`/`set_X` accessor name, `typeArgs`+`shapeTypes`, the `op_` name with receiver prepended, the extension
  `__self` prepend, the constructed-generic owner identity). A new bir2cir pass **`NetInteropBinding`** (the 3rd
  instance of the `ClrEventOperatorBinding`/`KClassMemberBinding` reflect-and-rewrite pattern) resolves the owner
  FQN against the loaded `.NET` reference assemblies — a long-lived `MetadataLoadContext` on `ReferenceMetadataIndex`
  with `ResolveNetType(fqn)` (`System.*` resolves from the running framework's reference dir; a user `.NET` lib from
  its `--ref`) — and REFLECTS the member to bind the CLR shape (a `get_X`/`set_X` over a `.NET` property OR field →
  `clrPropGet`/`clrPropSet`; an indexer/`op_`/method → `clrStatic`/`clrInstance`; a `typeArgs`-bearing call →
  `clrGeneric*`). This is the SAME "emit the identity, bind in bir2cir" pattern #52 established for the stdlib off the
  ref.dll, one axis over (user `.NET` refs instead of the stdlib ref.dll). The CIR is byte-identical (the shape
  decision merely moved down a layer; il-injstatic verified byte-for-byte). After this, `clrPropGet`/`clrPropSet` are
  **100% bir2cir-produced** (a real `.NET` property/field). The `.NET` event READ `w.Changed` — CLR-only vocabulary
  with no plain-Kotlin call form (it exposes `add_`/`remove_`, never a `get_`), so, like `byref`/`ClrRef<T>`, a
  facadegen-injected synthetic absent from every reference assembly that can't be "resolved + bound" — is lowered by
  kotc to its OWN dedicated dialect node **`clrEventGet`** (the ClrEvent<T> handle, NOT the shared `clrPropGet`); it
  exists only to feed a `+=`/`-=`, which bir2cir's `ClrEventOperatorBinding` binds into `add_X`/`remove_X`, so it never
  reaches ilemit. kotc's BIR thus emits ZERO shared `clr*`-shape nodes — only plain `callStatic`/`callInstance` plus
  the genuine CLR-only-vocab dialect forms (`@ClrRefArgument` byref annotation, ref-local, `clrEventGet`). ilemit keeps
  its `--ref` (runtime `Assembly.LoadFrom` for Reflection.Emit token resolution). `NetInteropBinding`'s owner
  resolution peels `Nullable`/`Oblivious`/`ByRef` wrappers off the owner slot (a `List<Item>?` receiver's owner is
  `nullable(fqn List<Item>)`) AND accepts a legacy STRING owner token (a referenced file class `LibKt`; the
  constructed-generic owner) — the original wrapped/string node is preserved verbatim in the `type` slot (byte-identical
  to the old kotc, which emitted a nullable `type` / a string `type` for a file class). The `Task.await()` marker
  (`kotlin.clr.CoroutinesKt.await`, a `kotlin.*` owner) is SKIPPED by `NetInteropBinding` (a stdlib owner) and reaches
  bir2cir's `SuspendColdLowering` as a plain `callStatic`/`callInstance` carrying `suspendCall`+(`typeArgs` for the
  generic form); the P4 await-marker detection now matches that plain shape BEFORE the generic-suspend-call path (else
  it mis-routed to a bogus same-assembly cold entry `await$dotkt_suspend`), keying generic-ness on `typeArgs` presence.
  `scripts/verify-roundtrip.sh`'s `emit_il` forwards the user `--ref` DotKt library to bir2cir too (mirroring
  `verify-il`'s `il_emit`), so `NetInteropBinding` can resolve the retargeted-library owners.
- **kotc no longer authors the `<>dotkt_` compiler-generated-name convention (#68).** The `<>` prefix is a
  C#/CLR codegen convention (`<>c__DisplayClass`, `<>d__`); kotc emitting it meant the Kotlin frontend knew a CLR
  naming rule. Now: (a) synthetic type definitions (capturing-lambda closures, heap ref-cells, `KProperty(Impl)`,
  the monomorphic `CharSequence` interface, lifted anon-object/local classes, the `StringCharSequence` adapter, and
  the rule-3 `ClrH` helpers) carry a structural **`generated:true`** flag; **ilemit** stamps the standard
  `[System.Runtime.CompilerServices.CompilerGenerated]` from that flag (replacing every `name.StartsWith("<>dotkt_")`
  prefix-sniff — a #37-freeze structured-flag-over-string win) and stamps its OWN internal synthetics too — the
  reverse-enumerator adapter `dotkt$EnumeratorOverKotlinIterator` and the variance/DIM method bridges
  (`dotkt$covar$…`/`dotkt$dimimpl$…`/`dotkt$dimfwd$…`) — which ALSO drop `<>` for the same `$` spelling, so ONE
  consistent marker spans the whole toolchain and the only `<>` left in emitted metadata is the CLR-mandated set
  (`<Module>`, `.ctor`); (b) the
  unspeakable names now use Kotlin's own `$` marker (`dotkt$Closure0` / `dotkt$Ref$…` / `dotkt$CharSequence` /
  `dotkt$KProperty`) — `$` is the string-template char, unspeakable in normal Kotlin source, so it is a frontend-legit
  collision guard, not a CLR-ism; a single canonical spelling flows through every layer. (c) **CharSequence** is
  emitted by kotc as the plain `kotlin.CharSequence` identity and **substituted** to the synthetic interface in
  bir2cir (same machinery as `kotlin.String`→`System.String`), so kotc knows nothing of the synthetic. (d)
  **facadegen** skips compiler-generated types by reading the `[CompilerGenerated]` attribute, never by
  `<>dotkt_` name-sniffing. kotc now authors ZERO `<>dotkt_*` names and ZERO `<>` knowledge. Additive to the frozen
  #37 schema (a `generated` boolean on type-decl nodes; verify-schema stays green).

- **kotc is now SUBSTITUTE-INDEPENDENT — the stdlib REFERENCE and RUNTIME builds share ONE frontend run and
  a BIT-IDENTICAL BIR (#66).** `BirEmitter` used to read `DOTKT_STDLIB_SUBSTITUTE` / `DOTKT_STRIP_METADATA`,
  so the ref-build and rt-build BIR diverged — a residual layer leak (kotc knowing about BCL substitution).
  kotc now emits ONE pure-Kotlin BIR; ALL ref/rt divergence is bir2cir's + ilemit's. Proven: running the
  frontend under the ref env (`DOTKT_STDLIB_COMPILE=1`) and the rt env
  (`DOTKT_STDLIB_COMPILE=1 DOTKT_STDLIB_SUBSTITUTE=1 DOTKT_STRIP_METADATA=1`) and `diff -rq`-ing the
  `*.bir.json` is byte-identical. Five sites moved: (1) the roundtrip-metadata / accessor / `@KotlinDefault`
  attrs are always emitted (the rt strip is ilemit's `_stripMetadata`, `Program.cs:626`); (2) the
  `kotlin.Comparable` upper-bound drop and (3) the `in` declaration-site variance drop moved to bir2cir
  (`StdlibSubstituteTypeParams.cs`, rt-build only, before `BirTypeLowering`); (4) the
  `for`-over-`kotlin.collections`→`forEachInline` recognition is gated on `stdlibCompile` alone (ref emits it
  too, its body squashed by `RefBodySquash`); (5) `clrName`'s ref-build early-return became substitute-free.
  The two `build-stdlib-{ref,rt}.sh` scripts are unified into `scripts/build-stdlib.sh` (ONE kotc run → a
  shared, cacheable BIR → ref emit + rt emit): a build speedup, and the emitted `DotKt.Private.Stdlib.dll` +
  `DotKt.Stdlib.dll` are byte-identical (modulo the non-deterministic PE timestamp + MVID) between the
  two-script and unified paths, with the rt.dll unchanged from before the change.

- **kotc's vestigial `<>dotkt_ClrH_` rule-3 routing arm deleted (verify-by-deletion).** The member-call
  emitter's `if (clrType != null)` interop block carried a dead "Rule 3" arm that routed a concrete member to
  a `<>dotkt_ClrH_<Class>` static hoist helper via `clrHelperName`. Since kotc reads **no** `@Clr` annotation,
  `clrType != null` requires a facadegen-injected .NET owner (which has no Kotlin bodies to hoist), and stdlib
  `@ClrTypeAlias` classes — the real source of rule-3 members — resolve to `clrInteropName == null` and fall
  through to the plain `kotlin.*` member-call path, where bir2cir's `AliasHelperHoist` synthesizes and routes
  the ClrH helper entirely on its own. The arm therefore had no reachable trigger. Removed the arm, its
  now-unused `injectedOwner` gate, and `clrHelperName` + doc-comment; nothing else in kotc references either.
  Sanity-verified: il-injstatic, a pure `System.*` call, and a stdlib StringBuilder `append`/`reverse`/`length`
  sample all emit **zero** kotc `<>dotkt_ClrH_` (StringBuilder members route as plain `callInstance`).
- **kotc's last operator/faithful-hint TYPE HINTS retired — bir2cir recovers operand static types itself
  (#59, the final #52 purity step).** kotc used to attach a per-site operand-static-type HINT so bir2cir
  could re-derive the collection/Double/Float/nullable Kotlin-semantic split it could not read off a bare
  operand node: `argTypes`+`argValueTypes` on the `EQEQ` intrinsic, `partTypes` on `String.plus`/the string
  template `concat`, `argTypes` on `println`/`print`, and `recvType`/`argType` on `objMethod ToString`/
  `Equals`. Those hints are **deleted** — kotc now emits ONLY the faithful op + the faithful operand
  expression nodes. bir2cir gains a single uniform static-type recovery (`toolchain/bir2cir/StaticTypeResolver.cs`:
  `BirScope` — a local/param type environment built by extending each declaration scope, the early-pass twin
  of `MemberCallSubstitution`'s `SubstCtx.VarTypes`; and `StaticType.Surface`/`.Value` — reading the operand's
  type off the node itself: `cast`→its target / peeled underlying, `local`→the scope, `const`→its type,
  `call*`→`ret`, `conv`→`to`, a LOWERED `binOp`/`unaryOp`→its result type, `arrayGet`→`elem`, `cond`(elvis/
  if-expr)→its branch, `concat`→`String`, `nullableValue`/`safeCastValue`→the value type, …). No new BIR node
  was needed — the smart-cast refined type was ALREADY a first-class BIR fact: a smart-cast USE emits
  `{k:cast,type:<refined>,…}` on the operand (and member calls carry the frontend-resolved `ownerType`), so
  the refined type reaches every consumer through the ONE `StaticType` path, closing the ad-hoc-per-consumer
  gap. `PrimitiveOperatorLowering` (EQEQ) + `FaithfulHintRecognition` (concat/println/ToString/Equals/
  compareTo) reproduce the EXACT SAME helper `callStatic` nodes (`clrCollStructEquals`/`clrCollToString`/
  `clr{Double,Float}Equals`/`LibraryKt.toString`/…) off the recovered types; ilemit is unchanged and the CIR
  is byte-identical (verify-schema 0 violations on the 250-file stdlib corpus + apps; ilverify-clean). Two
  subtleties the recovery handles: (a) `BirScope` records a `var` **lexically** (in scope for the subsequent
  siblings only), so two sibling `for ((k,v) in …)` loops whose `v` differs (List<Int> vs List<String>) don't
  collide into one flat last-wins dict — the collision would `clrCollToString<String>` an Int list → InvalidCast;
  (b) a call whose BIR node lacks a `ret` (kotc emits `ret` only for a GENERIC call) is resolved from the ref.dll
  — `MemberBinding` now carries the callee's structured return `TypeNode` (built by `TypeNodeOf`), and
  `StaticType` resolves a `callStatic owner=null` / member call / field read's type via
  `TryTopLevelReturn`/`TryMemberReturn`, so a non-generic collection-returning stdlib call (`"abcd".windowed(2)`)
  still stringifies Kotlin-style (`[ab, bc, cd]`) and no operand is left silently `Any`.
- **The synthetic CLR-representation TYPE *definitions* moved kotc → bir2cir — kotc emits the FACT, bir2cir
  synthesizes the TYPE (#52, final purity step).** kotc used to hand-build four families of `<>dotkt_*`
  CLR-representation types directly into its BIR `types`: the capturing-lambda **closure class**
  (`<>dotkt_<scope>_Closure<N>`), the **`<>dotkt_CharSequence`** monomorphic interface, the
  **`<>dotkt_KProperty`/`KPropertyImpl`** reflection stub, and the monomorphized **heap ref-cell**
  (`<>dotkt_<scope>_Ref_<elem>`) for a captured-and-mutated `var`. These are CLR-representation inventions (no such
  type exists in the Kotlin source), so — like every other #52 recognition — the *synthesis* belongs in the
  Kotlin↔CLR layer, not the frontend. kotc now emits only the structural FACTS and bir2cir assembles the type defs:
  - **closure** — kotc's `newClosure` node carries a transient `synthClass` ingredient bag (capture fields
    `{name,type}`, invoke params/ret/body, generic `typeParams`); the new bir2cir `ClosureSynthesis` pass builds the
    class (class/base/interfaces wrapper + the ctor field-init body) and strips `synthClass`, leaving the lean
    `newClosure` ilemit already lowers to `new`. Runs first in the phase-1 loop, before `SuspendColdLowering` reads
    the closure defs from `types` to inline a `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic.
  - **CharSequence / KProperty** — kotc emits only the use-site references; the new bir2cir
    `SharedSyntheticSynthesis` pass injects each fixed-shape def into any file that references the identity (ilemit
    still dedups per assembly + canonicalizes to the rt stdlib's copy when it resolves externally).
  - **ref-cell** — kotc emits a file-level `refTypes` registry ({name, element type} — the element type is
    unrecoverable from the bare `field .v` use-sites); `SharedSyntheticSynthesis` assembles each `{ var v }` cell
    from it and drops the registry.
  Output is byte-identical (the synthesized defs match kotc's retired `charSeqIfaceDefs`/`kPropertyDefs`/`refDefs`
  and the closure `liftedTypes.add` verbatim); behavior + ilverify unchanged. kotc's `usesCharSeq`/`needsKProperty`
  flags and the four producer functions are deleted. (The SAM shim + lifted local-class/anon-object types stay in
  kotc — they are lifts of user-authored declarations, not pure synthetics; an analogous follow-up, not in scope.)
- **The `<>dotkt_KIterator_<elem>`/`<>dotkt_KIterable_<elem>` iterator-protocol monomorphization is retired — user
  `Iterable`/`Iterator` value-type-element classes use the real generic (#58).** kotc used to compiler-synthesize a
  per-concrete-element non-generic interface (`<>dotkt_KIterator_int`, `<>dotkt_KIterable_int`) for a user
  `class R : Iterable<Int>`/`Iterator<Int>`, on the false premise "IL can't define a generic interface". It can (the
  BCL is full of generic interfaces; ilemit emits the stdlib's own; the substitute stdlib build already used the real
  generic here). Retired, mirroring #57's `ReadWriteProperty` retirement: a user `class R : Iterable<Int>` now links
  the **real generic `kotlin.collections.Iterable<Int>`** (bir2cir `@ClrTypeAlias`-lowered to
  `System.Collections.Generic.IEnumerable<int>`), `Iterator<Int>` the real emitted stdlib `kotlin.collections.Iterator<Int>`,
  and every `for (x in r)` / `it.hasNext()`/`it.next()` dispatches on that real generic. Three supporting pieces:
  (a) kotc deletes `iteratorElemIface`/`iterableElemIface`/`kIteratorName`/`kIterableName`/`iterableIfaces` + the
  synthetic-def emission + all three consumer sites; (b) **ilemit**'s reverse GetEnumerator bridge, previously able to
  synthesize `GetEnumerator` only when `kotlin.collections.Iterator` was emitted in the SAME assembly (the stdlib rt
  build), now also resolves the shared public adapter `<>dotkt_EnumeratorOverKotlinIterator\`1` from the **referenced**
  `DotKt.Stdlib.dll`, so an app class implementing `IEnumerable<int>` gets its `GetEnumerator` — else `TypeLoadException`;
  (c) **bir2cir**'s `IteratorConsumerNormalization` now normalizes a `hasNext`/`next` dispatch on the real
  `kotlin.collections.(Mutable)Iterator<E>` owner (not only the legacy synthetic) to a `clrInstance` on the base
  `Iterator<E>` where both members are DECLARED — required because `MutableIterator` only ADDS `remove`, so its
  inherited `hasNext`/`next` resolve nowhere as a `callInstance` (every `for (x in aMutableList)` hits this). The
  `<>dotkt_CharSequence` adapter is untouched — `kotlin.CharSequence` has no faithful BCL equivalent, a genuine
  reason distinct from the false generic-interface premise. Output runs correctly and is ilverify-clean.
- **Dead-code sweep after the #57/#58 synthetic retirements + facadegen's unused `--scan-asm` option (#62,
  verify-by-deletion).** Removals that #58 left as follow-up cleanup, plus a long-dead facadegen option: (a)
  **facadegen** loses the `--scan-asm <dll>` option and its `ScanAsmKotlinTypes` helper — no script/target/ktproj ever
  invoked it (it was the "inject stdlib facades from `DotKt.Stdlib`" mechanism, killed by the "never `--scan-asm` the
  stdlib; `kotlin.*` comes from the frontend jar" invariant); the general `IsKotlinStdlibSymbol` defense-in-depth guard
  is KEPT (it also protects the production `--meta`/`--import-list` and `--refs` DotKt-library paths, not just
  `--scan-asm`). (b) **bir2cir** deletes the now-inert `SyntheticIteratorUnification` pass (dead once #58 stopped kotc
  emitting the `<>dotkt_KIterator_`/`KIterable_` synthetic) and the unreachable `<>dotkt_KIterable_`/`SynthPrefix`
  branches in `IteratorConsumerNormalization`/`IteratorDispatchElem`; with the synthetic-owner dispatch gone, the pass's
  now-write-only `map` name→element bookkeeping is dropped (each `hasNext`/`next` reads its element straight off the real
  iterator owner's type arg). No behavior change — the sole remaining producers of those tokens were the retired paths.
- **`Delegates.observable`/`vetoable`/`notNull` now resolve to the real stdlib — kotc's `synthDelegate`
  vestige + the `ReadWriteProperty` monomorphization are deleted (#57).** kotc used to intercept
  `kotlin.properties.Delegates.observable/vetoable/notNull` and compiler-synthesize a per-value-type delegate
  class (`<>dotkt_*Delegate_<V>`) implementing a monomorphized `<>dotkt_RWProperty_<V>` interface — a workaround
  from before the CLR stdlib shipped `ObservableProperty`/`Delegates`/`NotNullVar`. The stdlib now emits those
  as real types into `DotKt.Stdlib.dll`, so the interception and `synthDelegate` are removed: `by
  Delegates.observable(…)` resolves to the real stdlib `Delegates.observable` (returns a real
  `ReadWriteProperty<Any?,V>`), and the delegate-access sites dispatch getValue/setValue on the **real generic
  `kotlin.properties.ReadWriteProperty<Any?,V>`** interface — exactly as `by lazy` dispatches on the real generic
  `kotlin.Lazy<T>`. The `<>dotkt_RWProperty_<V>`/`<>dotkt_ROProperty_<V>` monomorphization (`propIface`/`propIface0`/
  `propIfaceDefs`) is fully retired: `birType` and user delegate-class supertypes emit the real generic interface,
  so the delegate field type, the `observable(…)` value, and the dispatch owner all share one type identity
  (previously they diverged — a latent ilverify `StackUnexpected` that the monomorphization masked by keeping
  everything app-local). Output is byte-identical and every case is ilverify-clean; the synthetic
  `<>dotkt_KProperty`/`KPropertyImpl` property-reference pair stays (KProperty is a pure binding with no BCL
  equivalent). The monomorphization was a pre-generic-interface assumption, disproven by generic `kotlin.Lazy<T>`
  already working with a value-type `V`.
- **The range for-loop's CLR accessor knowledge moved out of kotc into bir2cir — kotc now emits ZERO CLR
  recognition (#52 Phase 5, the "range partial").** kotc's range for-loop lowering used to leak CLR accessor
  names: the stdlib-build `forRange` node carried `accessOwner="kotlin.ranges.IntProgression"` +
  `firstM/lastM/stepM="get_first"/"get_last"/"get_step"`, and the app-build counter loop emitted `callInstance`
  nodes to those getters — the CLR IntProgression realization living in the Kotlin frontend (the standing
  `TODO(refactor, per user 2026-06-28)`). kotc now emits a FAITHFUL `forRange` carrying only the range VALUE
  expr, the loop var, and the range's own pure-Kotlin type (`rangeType`); a new bir2cir pass `RangeForLowering`
  DERIVES the accessor access and picks the realization by build mode — the stdlib build keeps `forRange` and
  injects `accessOwner`/`get_first`/`get_last`/`get_step` (ilemit resolves them off `_types` generically), the
  app build rewrites to `block{ var __rng = range; for(i = __rng.get_first(); i <= __rng.get_last(); i += 1) }`
  with cross-module getters. It runs FIRST in the pipeline so the produced nodes flow through every downstream
  pass exactly as the old kotc-emitted forms did — byte-identical IL. `INT_PROGRESSION_FQ` stays in kotc ONLY as
  a pure-Kotlin recognition gate ("these Kotlin types are counted ranges"). **`grep get_first|get_last|get_step|
  IntProgression toolchain/kotc/src` → the CLR accessor names are gone from kotc. With the operator and range
  relocations complete, kotc is a pure faithful IR→BIR transcriber holding ZERO CLR recognition.**
- **Primitive OPERATOR recognition moved out of kotc into bir2cir (#52 Phase 5).** kotc no longer
  recognizes a primitive's operators: it emits the FAITHFUL member call (`callInstance kotlin.Int.plus`,
  `kotlin.Char.unaryMinus`) with its recv/args value-shaped (nullable-unwrap + boxed-Any cast), and a new
  bir2cir pass `PrimitiveOperatorLowering` re-emits the SAME `binOp`/`unaryOp` (+ the Char-arith `conv`)
  node the retired kotc `BINARY`/`UNARY` tables produced — so ilemit is UNCHANGED and the CIR is
  byte-identical. The primitive-op gate (`PRIMITIVE_OP_FQ`) and IL-op selection are CLR-relation knowledge
  and now live in bir2cir, keyed off the pure-Kotlin owner FQN. The pass runs unconditionally (reference +
  app builds) at the very start of the pipeline, restoring the exact tree shape every downstream pass (ref
  body-squash, type lowering, suspend) expects — and, crucially, a reference-build ctor field-init / base-arg
  (which is not body-squashed) carries a raw IL op rather than an unresolvable call to the bodyless builtin
  `kotlin.Int.inv`. _Class 1 (arithmetic `plus`/`minus`/`times`/`div`/`rem`, bitwise `and`/`or`/`xor`/`shl`/
  `shr`/`ushr`, unary `unaryMinus`/`unaryPlus`/`not`/`inv`) done._ _Class 2 (`inc`/`dec`, the `i++`/`i--`
  desugaring) done — the `const 1:kotlin.Int` literal moved to bir2cir with it._ _Class 3 (comparison
  `less`/`lessOrEqual`/`greater`/`greaterOrEqual`, the `<`/`<=`/`>`/`>=` `kotlin.internal.ir` intrinsics) done:
  kotc emits the intrinsic faithfully as a `callStatic owner=kotlin.internal.ir` (the owner marker makes the
  bir2cir match collision-safe vs a user top-level `less`), operands shaped exactly as the retired binOp._
  _Class 4 (equality `EQEQ`/`EQEQEQ`) done: kotc emits the faithful `kotlin.internal.ir` intrinsic — EQEQ
  carrying the operands' IR-derived `argTypes` — off which bir2cir re-derives the reference-vs-primitive split
  (both argTypes primitive-eq → `binOp ==`/ceq, else the null-safe `objEq`/Object.Equals); `===` → identity
  `binOp ==`. The Kotlin-semantic structural routings (boxed Double/Float total-order `==`, collection
  structural `==`) STAY in kotc (Phase-4 GENUINE-GAP), checked FIRST to preserve the exact ordering (the
  primitive fast-path must precede the float total-order route). **kotc now recognizes ZERO operators —
  arithmetic/bitwise/unary/inc/dec/comparison/equality all synthesize their binOp/unaryOp/conv/objEq in
  bir2cir's `PrimitiveOperatorLowering`; kotc emits only the faithful IR.**_
- **`String.plus` (member concat) recognition moved out of kotc into bir2cir (#52 Phase 5 — the last
  operator-recognition residual).** kotc no longer recognizes `"a" + b` (`kotlin.String.plus`) as a `concat`:
  it emits the FAITHFUL `callInstance kotlin.String.plus(a, b)` (a plain 2-operand member call) carrying the
  same cast-stripped `partTypes` hint the string-template path already carries (the stripped static operand
  types — List / nullable — are not recoverable from the declared param type `Any?`, so the hint is genuine).
  `PrimitiveOperatorLowering.Lower` recognizes `ownerType==kotlin.String && method=="plus" && args==1` and
  re-emits the identical 2-part `concat` node; `FaithfulHintRecognition` then consumes `partTypes` exactly as
  for a template concat (collection part → `clrCollToString`, nullable part → `LibraryKt.toString`). The
  string-TEMPLATE path (`IrStringConcatenation` → `concat`) is untouched — emitting a concat from the template
  IR is faithful transcription (concat IS the template's meaning), not member recognition. Byte-identical:
  nested `"a" + "b" + "c"` lowers bottom-up, and `"x" + listOf(1,2,3)` still renders `[1, 2, 3]`. **kotc now
  recognizes ZERO operators — member operators (`String.plus`) included.**
- **The last kotc CLR recognition — collection/Map `toString`, structural `==`, Double/Float total-order,
  null-safe stringify — moved to bir2cir (#52 Phase 4b).** kotc used to pattern-match the operand STATIC
  TYPE (through IR casts) to route these Kotlin-SEMANTIC operations to stdlib helpers by hardcoded FQN
  (`ClrCollectionDefaultsKt`/`ClrMapDefaultsKt`/`NumbersKt`/`LibraryKt`). It now emits the FAITHFUL op
  (`objMethod ToString`/`Equals`, `concat` with parts, `callStatic EQEQ`, `println`/`print`, `callInstance
  compareTo`) carrying a TRANSIENT, IR-derived, cast-stripped static-TYPE hint (`recvType`/`argType`/
  `argValueTypes`/`argTypes`/`partTypes` — faithful type transcription, NOT a helper name). bir2cir does
  ALL the recognition off those hints — a new pass `FaithfulHintRecognition` plus an extended
  `PrimitiveOperatorLowering` EQEQ arm — reproducing the EXACT SAME helper `callStatic` node kotc used to
  synthesize, then STRIPPING every consumed hint so the CIR is clean. The helper bodies are unchanged; only
  the RECOGNITION moved (relocation, mechanism-(b)). Collection/Map `toString` → `clr{Coll,Map}ToString`
  (`[a, b]`/`{a=1, b=2}`, vs raw .NET type-name ToString); structural `==` on List/Set/Map →
  `clr{Coll,Set,Map}StructEquals` (vs BCL reference `Object.Equals`); Double/Float `compareTo`/boxed `==` →
  `NumbersKt.clr{Double,Float}{Compare,Equals}` (Kotlin total order `-0.0<0.0`/`NaN==NaN`, vs IEEE
  `System.Double`); null-safe template/`+` operand → `LibraryKt.toString` (null → `"null"`). The EQEQ split
  keeps its exact precedence: primitive fast-path (surface `argTypes`) first, then collection struct-eq /
  float total-order (cast-stripped `argValueTypes`), then reference `objEq`. Byte-identical (verify-il
  243/0, all gates green). **kotc now recognizes ZERO CLR-shaped stdlib symbols — it is a pure faithful
  IR→BIR transcriber.**
- **Numeric conversions are metadata-driven — `@ClrConv` replaces kotc's `NUMBER_CONV` name-heuristic
  (#52 Phase 0/1).** kotc no longer *recognizes* a numeric conversion: it emits the faithful IR call
  `callInstance kotlin.Double.toInt` and nothing more. A new stdlib marker `@kotlin.clr.ClrConv` (no
  argument) annotates the 7 conversions (`toByte`/`toShort`/`toInt`/`toLong`/`toFloat`/`toDouble`/`toChar`)
  on each signed primitive (Byte/Short/Int/Long/Float/Double/Char). bir2cir reads it off the ref.dll and
  emits `{k:conv, to:<callee return type>, e:<recv>}` — the SAME `conv` node kotc used to synthesize, so
  ilemit is untouched (it still selects `conv.i4`/`conv.i8`/`conv.r8`). The `conv` stays a genuine
  primitive IL op; only the *recognition* moved to the layer that owns CLR knowledge, keyed on the exact
  stdlib symbol (precise) instead of a `name`+numeric-receiver guess (which could misfire). This
  establishes the ref.dll-metadata → bir2cir pattern for the remaining recognition sites (factories,
  `Pair`/`componentN`). The `NUMBER_CONV`/`NUMERIC_FQ` tables + the conv site's `fqnJson("kotlin.Int")`
  literals are gone from kotc. Also deletes the dead `COLLECTION_OPS` table (Phase 0 — no live consumer).
- **Collection & array factories are metadata-driven — `@ClrCollectionFactory`/`@ClrArrayFactory` replace
  kotc's `LIST_FACTORIES`/`SET_FACTORIES`/`MAP_FACTORIES`/`ARRAY_FACTORY_NAMES` recognition (#52 Phase 2).**
  kotc no longer *recognizes* a factory call: it emits the plain top-level call
  (`callStatic kotlin.collections.listOf(...)`, the vararg argument itself riding as a `newArray` node). Two
  new stdlib markers — `@kotlin.clr.ClrCollectionFactory(kind = "list"/"set"/"map")` on
  `listOf`/`mutableListOf`/`arrayListOf`/`emptyList` + the `set`/`map` families, and
  `@kotlin.clr.ClrArrayFactory(kind = "vararg"/"sized")` on `arrayOf`/`intArrayOf`/… + `arrayOfNulls` — are
  read off the ref.dll by bir2cir, which re-emits the SAME `{k:newList/newSet/newMap/newArray/newArraySized}`
  construction node (element/key/value types from the call's `typeArgs`; elements from the vararg argument),
  so ilemit is untouched. The `mapOf(a to b, …)` literal-split moved to bir2cir intact, **keeping its guard**:
  a non-literal Pair argument (`mapOf(pairVariable)`) is *not* force-split — it stays a plain call to the real
  `mapOf` body. The four recognition tables + their `BirEmitter` sites are deleted from kotc. (The factory
  markers live in the COMMON stdlib source set — `libraries/stdlib/src/kotlin/clr/Factories.kt` — because they
  annotate factory bodies in common sources that cannot reference a platform-only `kotlin.clr` binding.)
- **`to` / `Pair`·`Triple`·`IndexedValue` `componentN` are no longer recognized by kotc — the real stdlib
  types resolve them (#52 Phase 3).** kotc used to intercept the `a to b` infix call (synthesizing
  `new kotlin.Pair`) and the `component1()`/`component2()`/`component3()` operator calls on
  `Pair`/`Triple`/`IndexedValue` (synthesizing a `first`/`second`/`third`/`index`/`value` field read). Both
  intercepts are DELETED: unlike the conv/factory families (which synthesize CLR-shaped nodes and so need a
  ref.dll marker), these are **real emitted stdlib types with real members** — the infix `to`
  (`= Pair(this, that)`) and the data-class `componentN()` operators are materialized IR declarations already
  emitted onto the stdlib surface. kotc now emits the plain call (`5 to 6`, `val (a, b) = pair`,
  `t.component1()`) and it resolves against that surface with **no marker and no bir2cir change**. NOTE: the
  explicit `.first`/`.second`/`.third`/`.index`/`.value` property read (a separate site) stays a direct field
  read; only the `to`/`componentN` recognitions moved. One coupled follow-on in bir2cir: the Phase-2
  `mapOf(a to b, …)` literal-split recognized only a `new kotlin.Pair` node, which was the shape kotc used to
  emit for `to`; now that `to` is a plain call, the split also decomposes a `callStatic .to(k, v)` element
  (whose stdlib body is `Pair(this, that)`). Without this, `mapOf("a" to 1, "b" to 2)` fell to the real `mapOf`
  body, which builds a `Pair<K,V>[]` vararg array and `ArrayTypeMismatch`-crashed under reified generics when
  the elements are more-specifically typed (`Pair<String,String>` stored into `Pair<String,Any>[]`).
- **Collection-interface member routing is owned by bir2cir alone — kotc's dead duplicate is DELETED
  (#52 Phase 4).** kotc had a second copy of the routing that rewrites a `kotlin.collections` interface member
  whose substituted BCL face lacks the slot — `iterator()`/`isEmpty`/`contains`/`containsAll`/`indexOf`/
  `lastIndexOf`/`subList`/`listIterator()` — into the rt `ClrIteratorBridge`/`ClrCollectionDefaults` helper
  statics. bir2cir Rule 5 (`Program.cs`) already performs this routing off the ref.dll `@ClrTypeAlias` metadata;
  kotc's copies were gated on `clrName(declaringClass) != null`, which is **null** for the jar-sourced stdlib
  collection interfaces (they are not facadegen-injected), so once #5 stopped kotc reading `@ClrTypeAlias` the
  kotc sites became unreachable dead code. They are removed (three call-site blocks + the lifted
  `Iterable::iterator` method-reference special-case), which purges the `kotlin.collections.ClrIteratorBridgeKt`
  owner literal from kotc entirely and drops the `clrListListIterator`/collection-default uses of
  `ClrCollectionDefaultsKt`. kotc now emits the plain member call; bir2cir derives the CLR-gap routing.
  Behavior-preserving (the `coll`/`coll2`/`coll3`/`iter`/`iterable`/`sort` samples are the safety net).
  The Kotlin-**semantic** helper routings that a plain op genuinely CANNOT resolve stay in the frontend as a
  documented BCL gap (audit §3.3): collection/Map Kotlin-style `toString` (`[a, b]`/`{k=v}` vs the raw .NET
  type name), structural `==` on List/Set/Map (the substituted BCL collection's `Object.Equals` is reference
  identity), `Double`/`Float` total-order `compareTo`/`==` (differs from `System.Double.CompareTo`/`Equals`),
  and the null-safe `Any?.toString()` string-template stringifier — the runtime object is a raw BCL type with
  no Kotlin override (or a CLR primitive with different semantics), so no real stdlib default body dispatches
  on it; the helper is the only home.
- **Primitive Kotlin↔CLR mapping is metadata-driven — bir2cir's hardcoded `KotlinToClr` map is DELETED
  (#55).** The stdlib primitives already carry their CLR identity as metadata: `@ClrTypeAlias("System.Int32")
  class Int`, `@ClrTypeAlias("System.SByte") class Byte`, … (signed/unsigned split per #53/#54), and bir2cir
  already folds every such alias off the ref.dll into its `_aliases` index. The `KotlinToClr` dictionary
  (`kotlin.Int` → `"int"`, `kotlin.String` → `"string"`, …) was pure redundancy — it merely *shadowed* the
  alias the compiler already reads. It is removed: a primitive now lowers to its `@ClrTypeAlias` BCL form
  (`System.Int32`/`System.SByte`/…) via the same `AliasBcl` path as every other CLR-bound type. ilemit's
  `MapType` resolves `System.Int32` to `typeof(int)` identically to the old shorthand, so type resolution,
  value-type detection, boxing, arithmetic, arrays and generic construction are unchanged; the three name-keyed
  opcode switches (`EmitConst`/`EmitConv`/`ConstArgValue`) gained a `PrimShorthandName` normalizer that maps the
  alias spelling back to the opcode alphabet (`System.SByte` → `sbyte` signed, `System.Byte` → `byte` unsigned).
  `kotlin.Nothing` — the one primitive-ish token with no CLR value — gained an explicit
  `@ClrTypeAlias("System.Object")` in the stdlib (it erases to `object`, mirroring `kotlin.Any`) so it too
  resolves from metadata rather than a hardcode. The attribute-blob force map (`KotlinAllToClr`) STAYS: a
  custom-attribute blob needs a concrete `System.*` even in the reference build, which has no ref.dll to read.
  This is a behavior-preserving reroute — emitted primitive values are byte-identical. (One decoupled follow-up
  left out of scope: facadegen's reverse `System.*`→`kotlin.*` map.)
- **Generic-overload `shapes` are metadata-driven — kotc's `clrMethodShape` .NET-name matcher is DELETED
  (#55 §4).** The `clrGenericStatic`/`clrGenericInstance` nodes carry a `shapes` array (the SIG-KEY reflection
  island) that ilemit matches against reflected `MethodInfo` parameter shapes to pick the exact generic-method
  overload. kotc used to compute those tokens itself in `clrMethodShape(IrType)` — including the .NET SIMPLE
  NAMES (`kotlin.Long` → `"Int64"`, `kotlin.Byte` → `"SByte"`, `kotlin.Float` → `"Single"`, …), a keystone CLR-
  knowledge leak in the frontend (kotc is the only layer coupled to the Kotlin IR API, so this blocked the
  2.4 bump). kotc now emits only the DECLARED parameter types as pure-Kotlin `birType` identities in a transient
  `shapeTypes` array; a new bir2cir pass (`ShapeSynthesis`) derives the `shapes` tokens off the `@ClrTypeAlias`
  index (`kotlin.Long` → `System.Int64` → `"Int64"`, the signed/unsigned split per #53/#54) and drops
  `shapeTypes` before ilemit. The structural tokens (`gp`/`array`/`generic`/`ienum`/`func:N`/`string`/`char`/
  `int`) are derived from the `TypeNode` shape; ilemit is unchanged. Behavior-preserving — generic overload
  resolution is byte-identical (the `gen*`/`sort`/`taskwhen`/`netgen` samples are the safety net).
- **kotc `BirMappings.kt` shed of dead + vestigial tables (#40).** Post-#37-freeze audit of the
  Kotlin→BIR name/shape tables removed four entries: `COLLECTION_MEMBER` and `PRIMITIVE_SHORTHANDS`
  (both ref-count 0 — dead) and `VALUE_PRIM_BIR` (a `kotlin.Int`→`kotlin.Int` identity map whose only
  consumer was `PRIMITIVE_SHORTHANDS`, so dead once it went). The JVM-ism guard `SEQUENCED_COLLECTION_LEAK`
  (a filter dropping the `getFirst`/`addFirst`/`removeFirst`/… members that `java.util.SequencedCollection`
  (JDK21) leaked onto `List`/`MutableList` when kotc read the **JVM** kotlin-stdlib builtins) plus its
  single consumer in `BirEmitter` are deleted: kotc now reads the **self-host** frontend jar (built from
  our CLR stdlib sources) whose `kotlin/collections` builtins carry no `java.util.*` and no
  SequencedCollection members, so the filter was a verified no-op. The legitimate operator/factory/
  primitive-IL-op tables are kept (genuine Kotlin-frontend logic; primitive ops stay compiler-lowered per
  the cardinal rule).
- **BIR/CIR freeze — the versioned carrier envelope (#37 m6).** The two metadata-carrier attributes that
  ride cross-module BIR on the emitted assembly — `[KotlinInline]` (an inline+lambda fn's body, for splice)
  and `[KotlinSuspendFunctionType]` (a `suspend (…) -> T` position's pre-erasure `fn` shape) — now stamp a
  **versioned, codec-agnostic `(string version, byte[] content)`** envelope instead of a bare `(string)`.
  `version` = `"bir-json/1"` today (`content` = `UTF8(json)`); a future `"bir-msgpack/1"` swaps the physical
  codec without touching the logical node, and a schema bump is `"bir-json/2"`. A SINGLE
  `BirCarrier.EncodeBody(version,node)` / `DecodeBody(version,byte[])` pair dispatches on `version`, and an
  **unknown version is REJECTED** (a loud `NotSupportedException`, never a silent mis-decode) — mirroring the
  `NullableAttribute` scalar/array dual-ctor shape. The old `(string body)` ctors are DELETED (no dual-track);
  producers (`ilemit` `ApplyKotlinInline` / `ApplySuspendFnType`) and consumers (`ilemit`'s cross-module splice,
  `facadegen`'s `KotlinInlineBody` / `SuspendFnNode`) all route through the one codec. This is the groundwork
  for a binary (MessagePack) body codec: the version tag decouples the logical body from its physical encoding.
  Spec §0 updated.
- **BIR/CIR freeze — the schema VALIDATOR wired into the gate (#37 m6, the enforcer).** A structural validator
  (`scripts/verify-schema.py`, gate `scripts/verify-schema.sh`, Make target `verify-schema`, folded into the
  `verify` aggregate after `verify-il`) walks the freshly-emitted BIR + CIR (the whole stdlib corpus + every app
  sample) and reddens on any drift from the frozen contract: a document type slot that is a bare string instead of
  a `{t:…}` node (types-are-nodes — enforced by an inverse allow-list that fails closed across the tree), an
  unknown/typo'd/retired node kind `{k}` or type tag `{t}`, a malformed Type, or an unknown `mods`/`vis` value.
  Verified to fire: injecting a bare-string type slot, a retired `k` (`bin`), or an unknown tag (`clrg`) all red.
  Landing it clean surfaced and structuralized the last bir2cir/kotc-injected string type slots — `conv.to`, the
  synthetic `<>dotkt_KProperty` interface refs, the `StringCharSequenceBridge` adapter literal + `WrapAdapter`, a
  lifted-local-call `gp:T` typeArgs, the `Cast<object>` typeArgs, the CharSequence→String arg-coercion argType, and
  a `suspendLambdaNew` field renamed `typeArgs`→`typeParams` (a type-param NAME-decl list, not a type-usage slot).
  The two documented string islands (owner-FQN, sig-key reflection) stay out of scope. Spec §7 + schema updated.
- **BIR/CIR freeze — `funcType` structuralized: the last string type slot is gone (#37 #49).** The
  delegate-view function type on `closureNew`/`delegateNew`/`samNew`/`suspendLambdaNew`/`boundDelegateNew`/
  `delegateInvoke` was the final stringly-typed type slot (`func:<ret>:<args>` / `sfunc:<ret>:<args>`). kotc
  now emits it as the structured `fn` node UNIFORMLY (the emitted BIR carries 0 `func:`/`sfunc:` strings and
  ~5000 `{t:fn}` nodes), bir2cir lowers the node via `LowerFnDelegate`, and ilemit derives the CLR delegate
  from the node (`MapType(Fn)`→`FuncType(Fn)`; `FuncArityOf`/`FuncRetType`/`FuncArgTypes` read it). The now-dead
  `func:`/`sfunc:` STRING scanners are deleted — kotc's uncalled `synthLambda`; bir2cir's `LowerFuncString`/
  `FuncRetEnd`/`SkipTypeToken`/`PrefixLength`/`FoldSFuncToFunc` + the `func:`/`sfunc:` branches of
  `LowerTypeString`; ilemit's `FuncArity(string)` + `FuncArityOf`'s string path — plus the fully-dead
  `TypeSiteAnalyzer`/`TypeSiteAnalysis`/`TypeSite` (a `BirFile.Types` report no consumer read). The two
  intentional string islands — the owner-FQN `_types` index (`ParseOwner`) and the sig-key reflection matcher
  (`SigTokenOf`/`SigTokenMatches`, bir2cir `ParamKey`), which render a structured `Type`→string only to compare
  against a reflected `--ref` `MethodInfo` — are documented KEEPs (spec §2.2.1). Spec §1/§2 updated.
- **BIR/CIR freeze — tri-state nullability unification (#37 #48).** kotc now emits the `?` on the **Type
  node** (`{t:nullable,of:T}`) UNIFORMLY for value, reference, and type-variable positions — the duplicate
  decl-level scalar `nullable`/`retNullable` flags are RETIRED. The value-vs-reference split is derived
  below the kotc boundary: bir2cir's `DeclNullableFlags` walks each decl slot's type and emits the flattened
  `NullableAttribute` byte array (`nullableFlags` / `retNullableFlags`), then `ReferenceNullableStrip`
  removes every reference `{t:nullable,of:<ref>}` in ANY position (decl slots, owner generic args,
  `argTypes`/`typeArgs`, cast/expression types) while KEEPING a value `{t:nullable,of:<value>}` as the
  structural `System.Nullable<T>`; ilemit's `MapNullable` realizes value→`Nullable<T>` (via
  `TypeBuilder.GetConstructor` for an emitted value type) / reference→bare + the stamped NRT byte. As part of
  landing this, six object-erasure and variance-approximation predicates that pattern-matched a **bare** node
  learned to see through the new nullability wrapper — a nullable map/collection receiver kept its concrete
  type-args instead of collapsing to `IDictionary<object,object>`/`ICollection<object>` (fixing
  `EntryPointNotFound` in structural-`==` and `groupBy`/`mapValues`); an unconstrained `T?` accumulator, a
  `delegateInvoke` result temp, and a `forEach` loop-var over an `Iterable<T?>` source erase to `object`
  (fixing `NullReferenceException`/`InvalidProgram` in `merge`/`mapNotNull`/`filterNotNull` on value elements);
  and a top-level generic `T?` **param** is kept as the bare `T` + its NRT byte so facadegen round-trips the
  type-param identity (`orDefault<T>(x: T?)` stays inferable, not a `T`-less `Any?`). Spec §1 + schema updated.
- **BIR/CIR freeze — kotc producer flip fix: lifted-anon captured type-params (#37 m1).** A hoisted
  `object : Sequence<T>` (an object literal inside a `fun <T>`) flattens to a standalone generic class,
  but under the structured-`Type` producer flip its members referenced the captured `T` as the ENCLOSING
  function's `{t:tv,scope:"method",i:0}` — unresolvable once the anon is a class of its own (it declared
  `typeParams:["T"]` yet no member pointed at that slot). `BirEmitter.typeDef` now hoists the capture
  scan+install ABOVE member rendering: it collects the captured `IrTypeParameter`s and installs a
  `typeArgSubst` remapping each onto THIS class's own generic space (`scope:"type"`, flattened index after
  the anon's own params), so member bodies render resolvable `{t:tv,scope:"type",i}`. The construction
  (`new`) site instantiates the flattened type with those params rendered in the enclosing scope, and its
  leftover legacy `<>dotkt_objN[gp:T]` string token is flipped to a structured `{t:fqn,…,args:[{t:tv,…}]}`.

- **BIR/CIR freeze — the shared `TypeNode` model (#37 phase 1b, additive).** The frozen Type contract
  (`docs/bir-cir-spec.md` §1) is now concrete and compilable in BOTH languages, ahead of wiring the
  emit/consume paths to it. A single sealed/record hierarchy — `fqn`/`tv`/`fn`/`nullable`/`array`/`byref`
  — replaces the stringly-typed compound type tokens (`func:`/`sfunc:`/`nullable:`/`array:`/`byref:`/
  `gp:`/`clr:`/`clrg:`/`@`/primitive-shorthand). A Type is ALWAYS a `{ "t": … }` JSON object; readers
  dispatch on `t` and never split a string. C# side: `toolchain/bir-common/TypeNode.cs`
  (namespace `DotKt.Bir`), `<Compile Link/>`-shared into bir2cir/ilemit/facadegen (a linked file, not a
  project — no build-order dependency) with `TypeNode.Read(JsonElement)` / `Write(TypeNode)→JsonNode` and
  a `BirCarrier` codec skeleton (`EncodeBody`/`DecodeBody`, `bir-json/1` = UTF8↔JSON, msgpack a future
  `NotSupported` stub). Kotlin side: `toolchain/kotc/src/main/kotlin/kotc/bir/TypeNode.kt` with a matching
  compact `toJson()` and a real recursive-descent `parse()`. Both carry a round-trip self-test over a
  SHARED cross-language fixture (spec §1 examples), proving the two implementations agree byte-for-byte on
  the JSON shape. NOT yet wired to emit/consume (phases 2-5) — all gates stay XFAIL-zero.

- **ilemit: `@kotlin.clr.KotlinDefault` custom attributes now encode (#23b).** The ref-stdlib emit was
  skipping ~172 `@KotlinDefault(index, bir)` applications with `ArgumentException: Parameter count does not
  match`. Root: `BuildCab` stamps a param/method attribute during pass-3 member declaration, but a
  `@KotlinDefault` on an EARLIER type's parameter reached `BuildCab` before `kotlin.clr.KotlinDefault`'s own
  `(int, string)` ctor was defined (pass 3 declares types one at a time) — the old
  `ti.Ctors[0] ?? DefineDefaultConstructor()` then minted a bogus parameterless ctor per application and every
  stamp failed the arity check. Fix: `EnsureCtorsDefined(ti)` defines a type's ctors from its CIR on demand
  (idempotent, guarded), pulled early by `BuildCab`, which now also picks the ctor whose parameter count
  matches the applied argument count. bir2cir reads these attributes from the reference assembly to splice a
  callee's omitted non-constant (`CharSequence`/object) default at a cross-module call — so a Tier-2
  default-omitted call (`listOf(1,2,3).joinToString()`, `separator`/`prefix`/`postfix` `CharSequence`
  defaults) now fills correctly instead of crashing. (Pre-existing `@Deprecated`/`@OptIn`/`@WasExperimental`
  skips — Kotlin optional-param / `KClass`-arg annotations `CustomAttributeBuilder` can't encode — are
  unchanged and out of scope.)

- **ilemit dead-code sweep (M1).** Removed producer-zero legacy CIR handling now that bir2cir emits
  the plain BCL-call / collection-factory vocabulary: the 21 unreachable retire-list `EmitExpr` cases
  (`nullableOf`/`strRepeat`/`split`/`associateWith`/`associateBy`/`groupBy`/`linq*`/`listGet`/`listSet`/
  `mapGet`/`mapSet`/`mapSize`/`tupleNew`/`tupleItem`), the standalone native-CIR `clr.*` handlers
  (`clr.newobj`/`clr.call`/`clr.ldfld`/`clr.ldsfld`/`clr.stfld`/`clr.stsfld`/`clr.isinst`/`clr.isinst.ref`/
  `clr.castclass`) and their 6 dead-only helpers (`EmitNativeClrNewObj`/`Call`/`FieldGet`/`FieldSet`/
  `IsInst`/`CastClass`) plus 2 exclusive sub-helpers. The live computed-kind factories
  (`listNew`/`setNew`/`mapNew`/`strReversed`) and the 11 shared `EmitNativeClr*` helpers stay.
- **Make-it-loud: an unresolved CLR member no longer silently degrades to a runtime NRE.** bir2cir Rule-4
  used to emit a `clrInstance` for ANY member it could not resolve; ilemit's `clrInstance` fallback then
  reflected (`recv.GetType().GetMethod(name)`, no signature match) → `null` → an opaque `NullReferenceException`.
  Now: (1) bir2cir refuses, at compile time, a lowercase-camelCase member on a CLR-bound NON-interface owner
  (naming `owner.member`) — a BCL member is PascalCase, so such a member is an unbound routing MISS; (2)
  ilemit's `clrInstance`→dynamic-dispatch fallback is gated to INTERFACE owners (the clrInstance analog of the
  `callInstance` path's `OwnerHasClrInterface` gate), so a miss on a concrete BCL owner throws at EMIT. The
  intended dynamic dispatch (`MutableCollection.addAll/removeAll/retainAll` via `ICollection<T>`) is preserved.
- **bir2cir emits a suspend-lowering diagnostic when it drops a fun from the cold-transform set** — a
  shape-eligible suspend fun with an unresolvable suspend call (no same-assembly cold entry, no ref.dll
  Suspend-flagged member) now names the fun and the offending call on stderr, instead of silently surviving
  to trip the distant "suspend method reached codegen un-lowered" error at the ilemit boundary.
- **kotc reads NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias` and emits pure Kotlin.** All Kotlin↔CLR
  substitution is bir2cir's, sourced from the reference stdlib dll: kotc emits `kotlin.Unit` (bir2cir
  derives `void`), the Kotlin exception FQN (bir2cir substitutes the `System.*` type), a plain
  `annotation` flag (bir2cir derives the `: System.Attribute` base), the `kotlin.reflect.KClass` member
  (bir2cir derives `System.Type.Name`/`.FullName`), and plain member calls (bir2cir renames the BCL slot).
  The `clrName`/`annClr` side-tables and the `System.Math`/`System.Console`/exception/collection/
  StringBuilder/Regex/Closeable hardcodes are gone.
- **Deleted the `kotlin.String.length` → `System.String.Length` hardcode in kotc (M2).** It was redundant
  CLR knowledge: the stdlib's `@ClrIntrinsic("Length")` binding + bir2cir's `MemberCallSubstitution` already
  rewrite the plain `kotlin.String.length` member read (the sibling `String.get` → `get_Chars` was cleaned the
  same way). `"abc".length` stays `3`.
- **kotc stamps a stable `suspendIntrinsic:true` marker on the lowered `suspendCoroutineUninterceptedOrReturn`
  block (L1).** bir2cir's cold-suspension recognizer already prefers this flag over sniffing the intrinsic's
  fake `throw` message string, so the fragile string-match path becomes dead weight (its removal is a bir2cir
  follow-up). suspend samples unchanged.
- **The primitive/`Comparable` `compareTo` lowering moved to bir2cir** — the last kotc CLR-knowledge leak
  of its class. kotc emits a plain `callInstance` (`kotlin.Int.compareTo` / `kotlin.Comparable.compareTo`);
  bir2cir derives a primitive `System.<Prim>.CompareTo` and a `constrained. System.IComparable<T>::CompareTo`
  (its `Constrainify` pass now recovers the receiver static type from a `callInstance` return / `arrayGet`
  element and builds `IComparable<recvType>` directly, so a `Comparator.compare` override — whose `T` lives on
  an outer scope — still constrains). The runtime stdlib emits byte-behavior-identical constrained IL.
- **Removed the dead `Assembly.LoadFrom` ref-scan in bir2cir** — it always threw `TypeLoadException` on the
  metadata-only reference stdlib (surfacing a spurious `metadata scan failed: … 'kotlin.String'` warning once
  ref-scan diagnostics started reaching stderr) and its `Members`/`Types`/`Functions` output fed only
  callerless resolution helpers. The live `@ClrTypeAlias`/`@ClrIntrinsic`/rule-3 substitution reads solely from
  the `MetadataLoadContext` scan (loads per-type cleanly); genuine ref-scan failures still surface loud.
- **Single type-lowering path.** The `CompatBir` verbatim-copy mode and the `--compat-bir`/`--native-cir`
  flags are removed — one env-gated bir2cir pass rewrites the Kotlin type vocabulary into the CLR-codegen
  vocabulary ilemit consumes.
- **Namespace projection removed** (`[DotKtNamespaceProjection]` and the associated flag/meta/MSBuild item)
  — a DotKt assembly's types are seen 1:1 at their .NET namespace as the Kotlin package.
- **Pruned stale tombstone comments** across kotc / bir2cir / ilemit / facadegen / stdlib / scripts —
  dead-symbol references (`ClrTypeRegistry`/`ClrTopLevelRegistry`/`ClrEventRegistry`, `netType`,
  `NET_EXCEPTIONS`, `--compat-bir`/`--native-cir`, the retired `add_`/`remove_` event model) and
  `(RETIRED)`/`is GONE` archaeology left by the migration deletions are trimmed to present-tense layer
  guards or removed; genuine "why" rationale is preserved. Comment-only, no behavior change.
- **facadegen enforces the `kotlin.*` BINDING invariant in-layer (M3, defense-in-depth).** The rule
  "`kotlin.*` comes from the frontend JAR, never from facadegen" is now guaranteed by the owning layer:
  a `kotlin.*` symbol is short-circuited in BOTH the seed resolution AND `ShouldInject` (new
  `IsKotlinStdlibSymbol` predicate), so facadegen can never inject a stdlib symbol (which would be
  semantically degraded and would collide with the JAR's). The deliberate `kotlin.clr.await` CLR-async
  bridge is whitelisted — it is surfaced textually by `EmitTaskAwait`, never through the injection
  closure. Output-neutral (the closure never reached a `kotlin.*` type under the existing "don't
  `--scan-asm` the stdlib" discipline); the guarantee previously lived only downstream
  (`ClrTypeInjection.kt`, injected classes/interfaces — not top-level functions) plus that discipline.
  Same sweep: `System.Nullable\`1` added to `NO_INJECT` (a value-type `X?` is projected to Kotlin `X?`
  by `Map`, never the literal `Nullable<X>` — its open-definition injection was a stray dead type,
  mirroring `Span\`1`); a member signature type that degrades to `Any?` now emits a deduped `note:` to
  stderr (a silent `Any?` weakens the injected overload); the retarget `System.Runtime` fallback ref
  now carries the well-known ECMA PublicKeyToken `b03f5f7f11d50a3a` (a PKT-less ref failed a C#
  `<Reference>` bind); and two stale facadegen comments (`clrgen` package, `func:<ret>:<arg>` grammar)
  are corrected. No metadata-output change beyond dropping the dead `Nullable\`1` injection.
- **A companion computed property's .NET accessor shape moves from kotc to bir2cir's stdlib
  `@ClrProperty`/`@ClrIntrinsic` path (#78, A2-adjacent).** A companion object's computed property
  (`val X.Companion.foo: T get() = ...`, no backing field) used to have its accessor call baked as a
  literal `"get_"+name`/`"set_"+name` string at the kotc call site — a CLR-shape decision on the
  stdlib `owner`-keyed axis (`MemberCallSubstitution`, distinct from the facadegen `ownerType`-keyed
  `NetInteropBinding` axis A2 already covers). Baking the slot name also made the property
  UNBINDABLE: bir2cir's Rule 2p (`@ClrProperty`) lookup is keyed on the member's bare Kotlin name and
  was gated to the instance axis only, so a future `@ClrProperty`/`@ClrIntrinsic`-bound companion
  static property could never route to its real .NET member. kotc now emits the plain identity (bare
  property name) plus a `"prop":"get"/"set"` marker, mirroring the A2 convention; bir2cir's Rule 2p is
  extended to the static axis (keyed purely by owner+name+argcount, with no instance/static
  distinction of its own), followed by a bare `@ClrIntrinsic` probe under the same name, and — when
  neither binds — a fallback reconstructs kotc's own `get_`/`set_`+name declaration-side convention
  (the CLR property model: every emitted property accessor is CIL-named that way regardless of
  CLR-boundness), so the byte-identical output is preserved for the common (unbound) case. Verified via
  a companion computed val/var (get_/set_ fallback CIR unchanged) and the schema gate.
- **`kotlin.reflect.KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1` are now REAL emitted
  stdlib interfaces, not the compiler's `dotkt$KProperty(+Impl)` synthetic (#70).** The klib migration
  made these interfaces (`KPropertyClr.kt`) real CLR types in `DotKt.Stdlib.dll`, leaving the old
  synthetic a redundant parallel identity. A genuine callable reference (`::x`, `obj::p`, `Type::p`) now
  lowers (kotc's `propertyRef`) to a lifted class — mirroring `samConversion` — that implements the REAL
  `KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1` (including the interface's own
  fake-overridden `invoke()`, so `(::x)()` and passing `::x` to a `KProperty0<T>`-typed parameter both
  work); v1 covers a top-level property, and a member property either BOUND (`obj::p`, receiver captured
  in a field) or UNBOUND (`Type::p`, receiver is the `get`/`set`'s own leading param) — an
  extension-receiver property reference (`KProperty2`) is a clean deferral. A delegated property's
  compiler-synthesized `getValue`/`setValue`/`provideDelegate` argument — which only ever needs `.name`
  — now materializes a real (but minimal) `kotlin.reflect.ClrPropertyStub` instead. `dotkt$KProperty` /
  `dotkt$KPropertyImpl` are deleted from all three layers (kotc's `birType`/`birTypeDeleg` KProperty
  special-casing, bir2cir's `SharedSyntheticSynthesis`, ilemit's `CanonicalSynthetics`) in the same
  change — no dual-track.

### Tooling, build & gates

- **ilemit failure diagnostics name the declaration (#84 Phase 1).** ilemit had no error boundary — any
  emit failure surfaced as a raw unhandled .NET stack trace with no indication of *which* declaration was
  being emitted. Each method/ctor body emit now carries a breadcrumb (`Type.method [node]`) and is guarded,
  so a throw is re-tagged and `IlEmit.Main` prints a clean one-line `ilemit: <Type>.<method>: <message>`
  (returning 1) instead of a stack dump; the full stack stays available behind `ILEMIT_TRACE`. Pure
  error-path plumbing — a successful emit is byte-identical.
- **In-process CIR sanity gate before emit (#84 Phase 4).** The offline schema validator checks CIR
  *shape*; a new in-process pass in ilemit (`Emitter.Sanity.cs`, run at the head of `EmitAssembly` before
  any codegen) checks *meaning*, so malformed CIR fails LOUD with a precise `ilemit: <Type>.<method>:
  sanity: <invariant>` message (routed through the Phase-1 diagnostic) instead of a cryptic
  Reflection.Emit crash / silent `BadImageFormat`. Invariants: every `local`/`setLocal`/`byref` resolves
  to a declared var/param; every `goto`/`brIf` has a matching `label` (and no duplicate label id);
  `binOp` has both operands and `cond` has `cond`/`then`/`else`; field-family nodes carry a non-null
  `ownerType`; a `for` loop's `cmp` is one of `<=`/`<`/`>=` (an unknown one silently miscompiled to an
  infinite loop). Deliberately conservative — calibrated to never false-positive on the verify-il corpus
  or the 251-file stdlib rt build; ambiguous invariants (callStatic owner, args-vs-argTypes arity) were
  dropped rather than risk a false reject. (Shared bir-common home + bir2cir-side call + offline
  `verify-sanity.py` are noted Phase-4 follow-ups.)
- **`Makefile` orchestrator** over the canonical scripts (incremental targets `all` / `toolchain` /
  `stdlib{,-jar,-ref,-rt}` / `pack` / `verify*` / `dev`), and a **4-package NuGet structure** (Sdk /
  Toolchain / Stdlib / Templates) that fixes the packaging gap where the shipped SDK carried no stdlib
  DLLs and could not actually compile a consumer.
- **`scripts/` overhaul:** one `<verb>-<noun>` naming scheme aligned with the make targets, a shared
  `scripts/lib.sh` (strict mode, common tool/artifact paths, `need_*`/`build_tool`), and two harness bug
  fixes (the rt grep-exit-1 footgun; the verify-il dropped-FAIL-line race — now one atomic result record
  per sample).
- **Every gate is XFAIL-zero** (verify-il, verify-differential, verify-roundtrip, verify-ktproj). The
  known-fail baselines are machine-readable `XFAIL_*` maps diffed on each run (printing `NEW-FAIL`/`FIXED`),
  replacing prose fail-counts.
- **Failure posture is loud, not silent:** ambiguous `@Clr*` overloads, an un-lowered `suspend` fn reaching
  ilemit in an app build, ref.dll-scan diagnostics, and per-file stdlib-emit crashes now fail or warn
  explicitly instead of silently dropping work.
- **frontend stdlib jar** (`kotlin-stdlib-clr-frontend.jar`) replaces the JVM `kotlin-stdlib.jar` as kotc's
  `-classpath`, killing the `java.util.*` typealias leak; its `.kotlin_builtins` are generated from our own
  sources.
- **Build adoption for the klib stdlib-frontend migration (#67, follow-up to #80):** the frontend jar above
  is itself now retired — `scripts/build-stdlib-jar.sh` deleted, and the `FE_JAR`/`need_fe_jar`
  backward-compat aliasing in `scripts/lib.sh` removed in favor of naming the klib directly
  (`FE_KLIB`/`need_fe_klib`) everywhere it's consumed (`dotkt.sh`, `verify-il.sh`, `verify-differential.sh`,
  `verify-roundtrip.sh`, `verify-wide-delegates.sh`). The Makefile's `stdlib-jar` compatibility-alias target
  is gone too, and `clean-stdlib` now wipes the klib output dir instead of the old jar one. Canonical stdlib
  build is `build-stdlib-{klib,ref,rt}.sh`, in that order (`make stdlib` runs all three). Also deleted
  `scripts/dotkt-keep.sh`, a tracked-but-unreferenced duplicate of `dotkt.sh` (same file, minus its
  `mktemp` cleanup trap) dating back to `dotkt.sh`'s introduction.
- **Gate-hygiene fixes (final-review 2026-07-05):** closed the `verify-differential` `empty==empty`
  false-MATCH hole (a MATCH now requires BOTH the jvm oracle and the clr side to have produced real,
  non-empty output — two compile/run failures no longer silently pass as a MATCH); removed a stale
  `verify-il` comment referencing the retired `XFAIL_RUN[cobuild]` and a duplicate `comaindrain`
  invocation; and **wired 44 run-only cases into the `verify-il` ilverify pass** (they were run-checked
  but had no formal-verification coverage). Two cases are documented-excluded: `stackalloc`
  (`localloc` is unverifiable by ECMA-335), plus `ifacesuspend` which runs correctly but emits a
  genuinely-unverifiable `CallAbstract` in the interface-suspend bridge — surfaced as a real latent
  finding, not XFAIL-hidden. (`strops` was the third; its primitive-array `StackUnexpected` is now
  fixed in ilemit and it is wired into the ilverify pass — see below.)
- **`verify-differential` coverage expanded to the JVM oracle (COV1, kcc review §2B) — the structural
  fix.** The differential gate (the ONLY gate that checks against real Kotlin/JVM semantics) validated
  only ~43 samples; the other ~120 pure-Kotlin `il-*` samples self-scored against DotKt-captured fixed
  strings in `verify-il`, so a Kotlin-INCORRECT mapping could pass green forever. The JVM-runnable
  pure-Kotlin `il-*` subset (string / collection / math / regex / unsigned / enum / data-class /
  generics / delegates / lazy / …) is now promoted into the `PURE` list, so each runs on BOTH the
  kotlin/jvm oracle and the shipping CLR backend and must match — **163 samples, ALL MATCH**.
  CLR-specific-by-design samples are excluded with a per-sample reason: `il-bmore`/`il-fmt` (`.format`
  uses .NET composite format strings, literal text on the JVM), `il-reified` (`Int::class.simpleName`
  is the CLR name `Int32` vs the JVM's `Int`); the coroutine cold-core family and all interop
  (`il_check_imports`/`il_check_inject`) samples stay out (not JVM-runnable). Two harness bugs found and
  fixed along the way: a `package`-declared sample ran `java <Class>` without the FQN (empty JVM output →
  false DIFF — now prefixes the package), and the parallel result echoes shared one redirected stdout
  offset and clobbered each other under a warm cache (the same race `verify-il` already retired — now one
  atomic result record per sample). This makes the C1–C11-class regressions the review found redden the
  gate instead of passing green.
- **`il-strops` ilverify finding FIXED (2026-07-05) — the last ilverify-dirty finding.** The 3×
  `[StackUnexpected][found Char]` in `main` was the `String.trim(vararg chars: Char)` call site building
  a `char[]`, where `ilemit` emitted the generic token opcode `stelem <System.Char>` instead of the
  specialized `stelem.i2`. ECMA-335 requires the specialized `stelem`/`ldelem` opcode for a PRIMITIVE
  element type; the token form is unverifiable for primitives (`stelem <char>` → `[found Char]`,
  `ldelem <char>` → `[found Short]`; `stelem.i2`/`ldelem.u2` verify clean). Fixed with a shared
  `EmitStelem`/`EmitLdelem` helper (`Program.cs`) that selects the specialized opcode for a BCL
  primitive element (char→`stelem.i2`/`ldelem.u2`, int→`stelem.i4`, …), `stelem.ref`/`ldelem.ref` for a
  reference element, and keeps the TOKEN form ONLY for a generic-parameter (`!T`/`!!T`) or non-primitive
  struct element (specializing a generic-param element would be wrong for a value-type instantiation).
  Wired into all five array store/load sites (`EmitNewArray`, `newArrayInit`, `arrayGet`/`arraySet`,
  for-in-over-array). `il-strops` now RUNS correct and verifies clean, and is wired into the `verify-il`
  ilverify pass — leaving `verify-il`/`differential`/`ktproj`/`roundtrip` + ilverify all XFAIL-zero.
- **Repo hygiene (kcc review §X1/§L2, 2026-07-06):** untracked the 90 compiled DLLs (+87
  `runtimeconfig.json`, ~3.1M) under `dotkt-out/` — the `dotkt.sh`/`dotkt-keep.sh` default output dir,
  pure build artifacts, never fixtures — and added `dotkt-out/` to `.gitignore` (it no longer dirties
  every build, pollutes diffs, or masks stdlib regressions). Pruned a cluster of stale
  comments/dead references that were pure archaeology: the dead `steps`/`coClass` node-kind entries in
  bir2cir `SuspendColdLowering` `LambdaKinds` (the `sequenceNew` producer is gone; the surviving
  `steps`/`coClass` method-property guards are a separate mechanism, kept), the retired `delay`/`blockOn`
  reference in the `InteropBridgeFileClass` comment, ilemit's `cps-field` store-target comment (CPS is
  gone), and kotc's `native-cir`/`compat-passthrough` comment (the dual-track was removed 2026-06-30).
  Behavior-neutral: every gate stays XFAIL-zero.
- **Residual coverage gaps closed (COV2–COV6, kcc review §2B, 2026-07-06).** Added three gate cases,
  all JVM-oracle-verified in the differential gate: **`il-atomics`** — `kotlin.concurrent.atomics`
  `AtomicInt`/`AtomicLong` (`incrementAndFetch`/`fetchAndAdd`/`addAndFetch`/`compareAndSet`/`exchange`/
  `compareAndExchange`), exercising the `@ClrRefArgument` Interlocked byref binding that previously had
  ZERO coverage (COV2); **`il-typealias`** — a `typealias` over a stdlib generic / function type / user
  class, used across a function boundary (COV3); **`il-triple`** — `Triple` construction / destructuring /
  `componentN` / full-arg `copy` / `toString` (COV4). Wired into both `verify-il` (with ilverify formal
  coverage) and the differential `PURE` list. Two ktproj-level gates were also added and three unwired
  fixtures reconciled (see below).
- **`tailrec` deep-recursion deviation documented (COV5).** A new deep-recursion probe (`cases/il-tailrec`,
  `sumTo(1_000_000)`) empirically showed our compiler emits **no tail-call optimization** for `tailrec`:
  deep tail recursion **overflows the CLR stack** where kotlin/jvm rewrites it into a loop. Recorded as a
  known deviation in `docs/dotkt-semantics.md §2b` (routed fix = tail-call lowering in kotc/bir2cir); the
  reproducer is intentionally left OUT of every gate so they stay XFAIL-zero.
- **`ifacesuspend` ilverify code/comment contradiction reconciled (COV6).** `verify-il.sh` had
  `ifacesuspend` in the ilverify `ASMS` set (verifying clean) while a stale comment still claimed it was
  "deliberately NOT in ASMS — a REAL latent finding"; the CallAbstract bridge finding had in fact been
  fixed. Comment corrected to match the code.
- **Dead/unwired fixtures dispositioned (COV6).** Wired the README-advertised **`ktproj-il`** (pure-IL
  starter project, previously ungated → could rot) plus **`ktproj-import`** (bare `import System.X`
  resolution), **`ktproj-refrt`** and **`ktproj-refrt-pr`** (the `<KotlinClrRefRt>` ref→rt property, standalone
  and across a Kotlin→Kotlin `ProjectReference`) into `verify-ktproj`. **Removed** 13 genuinely-dead
  fixtures: the 12 retired C#-backend runners (`m-c2`..`m-c8`, `m-i3`..`m-i5`, `m-s4`, `m-s5` — their
  `runner.csproj` includes the never-generated `build/clr-m*/` C# output) and **`ktproj-ref`** (the retired
  `import clr.X` + `<KotlinClrFacade>` positional-facade path — facadegen now supports `--meta` mode ONLY,
  so it no longer builds). The now-orphaned `KotlinClrFacades` target in `cases/KotlinClr.targets` is left
  in place but has no remaining consumer.

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

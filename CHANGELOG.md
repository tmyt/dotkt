# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Fixed

- **stdlib: `copyInto` is now overlap-safe (#97).** All nine `copyInto` actuals (generic `Array<T>` +
  the 8 primitive arrays) bind to `System.Array.Copy` (memmove) instead of a naive forward element
  loop, which clobbered source slots on an overlapping self-copy with `destinationOffset > startIndex`.
  This silently corrupted `ArrayDeque.add(index, elem)` (an in-place right shift). (`_ArraysClr.kt`)
- **stdlib: `Double/Float.roundToInt`/`roundToLong` round half-up toward +inf (#103).** They now
  implement `floor(x + 0.5)` (ties: `2.5→3`, `-2.5→-2`, `0.5→1`, `-0.5→0`) instead of delegating to
  `kotlin.math.round` (banker's ties-to-even). NaN throws `IllegalArgumentException`; out-of-range
  saturates to `Int`/`Long` `MIN`/`MAX`. `kotlin.math.round` itself stays ties-to-even. (`MathClr.kt`)
- **stdlib: `CharArray.copyOf(newSize)` zero-fills grown slots with the null char `'\u0000'` (#128),**
  not a space (`U+0020`), matching Kotlin/JVM. (`_ArraysClr.kt`)

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

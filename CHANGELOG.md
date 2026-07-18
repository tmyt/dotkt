# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Fixed

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
  not a space (`U+0020`), matching Kotlin/JVM. (`_ArraysClr.kt`)
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

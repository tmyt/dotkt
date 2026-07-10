# Decomposition plan: `toolchain/kotc/.../backend/BirEmitter.kt` (task #41, Fable design 2026-07-11)

Target: split the ~4633-line `BirEmitter.kt` into cohesive sibling files mirroring the existing
`BirEmitterExpressions.kt` / `BirEmitterStatements.kt` split, **byte-identical BIR output**, no behavior
change. Re-locate by function NAME — line anchors drift as batches land. Companion of
`docs/design-bir2cir-decompose-41.md` + `docs/design-ilemit-decompose-41.md`.

## 1. The split mechanism (verified against the two existing siblings)
`BirEmitter` is a plain `class BirEmitter(private val messageCollector: MessageCollector? = null)`
(no object, no context receivers). The established split = **top-level `internal` extension functions
on `BirEmitter`** in the same package `kotc.backend`, same module:
`internal fun BirEmitter.expr(node): String` (BirEmitterExpressions.kt), `internal fun BirEmitter.stmt(node)`
(BirEmitterStatements.kt). All shared state stays as `internal` members on the class; extensions reach
it via the implicit receiver — NO state threaded as parameters. Same-package/same-module means no
imports needed for cross-file calls; copy BirEmitter.kt's import block verbatim (unused imports are
warnings — the existing siblings keep them; no import golf). A helper used only inside one new file
becomes a top-level `private fun BirEmitter.xxx(...)` (file-private).

**Kotlin private-member constraint.** ~98% of members are already `internal` (for the existing split).
Nine are `private`; extraction handles them (each behavior-neutral — visibility is compile-time-only,
BirEmitter is never reflected/serialized, no statement changes):
- `messageCollector` ctor param → widen to `internal val` (batch 6); `hadError private set` → `internal set` (batch 6); `clrEventReceiverOk` → `internal`; `asClrEventReceiver` (private inline) → `internal inline` (an internal inline fun may only touch internal members — hence widening the two above together).
- `hasRealStaticField` (only call()) → file-private in BirEmitterCalls.kt; `freeTypeParams`/`suspendLambda` → file-private in BirEmitterLifts.kt; `bodyTypeOperands` (typeDef + lifts) → widen to `internal`, home in BirEmitterLifts.kt; `defaultArgPlaceholder`/`defaultArgThisToken` → top-level `private val` in BirEmitterCalls.kt; `argType` (only birType) → file-private in BirEmitterTypes.kt.

## 2. Target file set (6 new files, all in kotc/backend/)
| File | Moves (≈size) |
|---|---|
| **BirEmitter.kt (STAYS)** | class decl + ctor; diagnostics (locationOf/unsupported); the ENTIRE mutable-state block (incl. relocating `cfgLoopStack` + `TailrecCtx`/`tailrecCtx` UP into it); type-naming quartet (typeName/emittedNestedParent/superTypedCompanion/companionObjectTypeName); ref-cell machinery; scopeCall/SCOPE_FUNCTIONS; newExc/throwExpr; **emitFile**. ≈550 |
| **BirEmitterTypes.kt** | birType + argType(file-private), tvOf, hasTv, containsTv, mangle, OBJ (→extension val), fqnJson, argElemNullable, constJson, isArrayType, IrType.isValuePrimitive/isPrimitiveOrUnsigned (→plain top-level IrType extensions), arrayElemType, nullableElem, nullableValueUnwrapElem, coerceValue, isPreUnwrappedRead, visOf, ownerSpec, clrName, str(TypeNode)+str(String). ≈400 |
| **BirEmitterDeclarations.kt** | interfaceDef, enum family (enumDef/isRichEnum/richEnumDef/enumSuperArgs/enumEntrySubclass), nested collectors, innerClassDef, property gates (isClrField/isVolatile/volatileFieldFlag/isClrEventProperty), overridesJson, accessors (topLevelAccessorMethod/accessorMethod), annotationDef, attrsJson, typeDef, ctor, method, funModsJson/resultTypeJson/classModsJson, isInlineWithLambda, suspend-FACT helpers (isAwaitIntrinsic/isSuspensionCall/containsSuspend — NO CPS engine here; SM is bir2cir's), typeParamsJson, params machinery, isAnySlotMethod. ≈1000 |
| **BirEmitterControlFlow.kt** | labelJson, loopBody, breakContinueExpr, tailrecJump, cfgWhile, cfgDoWhile, birForLoop, tryStmt, bodyStmts, tryExpr, bodyStmtsAssign, cfgWhen, bindOnce, blockExpr, ternary, isEmittedNullConst. ≈360 |
| **BirEmitterLifts.kt** | freeTypeParams(file-private), bodyTypeOperands(internal), suspendLambda(file-private), lambda, samConversion, functionRef, kPropertyStub, propertyRef, capturedVars(+ForObject), mutatedIn, captureFieldName, capValueExpr, orderedLambdaParams, funcTypeOf, funcRetTypeOf, birTypeDeleg, lambdaParamsJson, liftLocalFn, liftLocalClass. ≈720 |
| **BirEmitterInline.kt** | inlineScope, inlineRepeat, inlineUse, hasLambdaArg, nestedCapturesValue, bodyStatements, inlineCall, withTypeArgScope (qualifies BirEmitter.TypeArgScope), inlineSpliceCall, spliceLambdaCall, hasEarlyReturn, spliceBodyWithReturns, spliceBody, emitStackBuffer, emitStackBufferOp (qualifies BirEmitter.StackBufInfo). ≈490 |
| **BirEmitterCalls.kt** | top-level private val defaultArgPlaceholder/defaultArgThisToken, filledArgs, filledArgExprs, refsAny, regularArgs, dispatchReceiver, regularParams, extensionReceiver, propRefDispatchReceiver, **call** (whole, ~975 lines — NEVER split), retHint, retHintStr, suspendCallTag, typeArgsJson, byrefMarker, isClrRefArgument, argExpr, recvExpr, byrefBackingField, clrCallArgs, hasRealStaticField(file-private). ≈1300 |

Each new file: `package kotc.backend` + BirEmitter.kt's import block copied verbatim.

## 3. Shared-state home
EVERYTHING mutable stays on the `BirEmitter` class in BirEmitter.kt; extensions reach it via the
receiver, exactly as expr/stmt do. ONE BirEmitter instance serves the WHOLE module (ClrBackendPhase.kt).
emitFile resets ONLY liftedMethods/liftedTypes/refTypes per file; cfgLabelN/lambdaCounter/closureCounter/
inlCounter/scopeCounter are deliberately run-global (label/name uniqueness across files). NEVER
param-ify / move / reset-differently any state — any change = a name/label diff in the BIR. Relocate (not
extract) `cfgLoopStack` + `TailrecCtx`/`tailrecCtx` up into the state block (pure member-order move).

## 4. Extraction order — 6 batches, one BIR-byte-identity gate each
Per batch: move functions verbatim (with doc comments), delete from BirEmitter.kt, rebuild kotc
(`./gradlew :kotc:installDist`), re-run the BIR corpus, `diff -r` — MUST be empty; commit.
1. **BirEmitterTypes.kt** (leaf; only shape changes OBJ→extension val + the two IrType member-exts→top-level).
2. **BirEmitterControlFlow.kt** (first relocate cfgLoopStack/TailrecCtx/tailrecCtx into the state block, then extract).
3. **BirEmitterDeclarations.kt** (widen bodyTypeOperands private→internal in this batch; still a member).
4. **BirEmitterLifts.kt** (freeTypeParams/suspendLambda file-private; bodyTypeOperands moves here as internal).
5. **BirEmitterInline.kt**.
6. **BirEmitterCalls.kt** (carries the widenings: messageCollector→internal val, hadError internal set, clrEventReceiverOk→internal, asClrEventReceiver→internal inline; hasRealStaticField file-private).

### The gate: byte-identical BIR
1. Baseline once (before batch 1): build kotc; run verify-il.sh + `make stdlib`, archive every emitted `*.bir.json` → baseline-bir/ (preserve relative paths).
2. Per batch: rebuild kotc, re-run the same invocations → after-bir/, `diff -r baseline-bir/ after-bir/` — must be empty (kotc is deterministic per input).
3. End: verify-il.sh green (same XFAIL diff) + verify-roundtrip.sh. NOTE: since kotc changes affect BIR, the authoritative end-gate is gate-fast (rebuilds stdlib from the new kotc BIR).
The gate is BIR-output identity, NOT kotc-jar identity (class-file order permutes — irrelevant).

## 5. Risks
1. **Run-scoped mutable state on the single per-module instance** — counters never reset across files; liftedMethods/liftedTypes/refTypes reset per emitFile only. Any param-ification / var→local / moved reset = BIR name/label diff. Whole-function verbatim moves + state-stays-on-class preserve all of it. TOP scope-creep trap.
2. **5 private→internal widenings** (all in batch 6 + bodyTypeOperands in batch 3) — behavior-neutral (compile-time-only). `asClrEventReceiver` + `clrEventReceiverOk` MUST widen together (internal inline can't touch private).
3. **Member-vs-extension shadowing**: if a moved function is left in the class, the member silently wins over the new extension → build green with dead code; the byte gate can't catch it. Always delete in the same change; grep the name per batch.
4. IrType.isValuePrimitive/isPrimitiveOrUnsigned are member-extensions using no instance state → become plain top-level IrType extensions; call sites (incl. BirEmitterExpressions.kt) compile unchanged.
5. Nested classes stay nested (TypeArgScope/StackBufInfo/TailrecCtx keep BirEmitter. FQ names); extracted signatures qualify them.
6. **NEVER split call()** (~975 lines) or any single function; a function moves whole or not at all. Carving call()'s intrinsic arms is a separate task, out of #41 scope.
7. File-private placement is load-bearing (freeTypeParams/suspendLambda in Lifts, hasRealStaticField/defaultArg* in Calls, argType in Types) — only private because all callers land in the same file. A missed caller → widen to internal, do not re-home the caller.
8. Comment relocation (same-change rule): function comments move with them; state-block comments + the BIT-IDENTICAL-BIR contract comment stay in core.

## Summary
6 new files; BirEmitter.kt 4633 → ~550. Mechanism: `internal fun BirEmitter.<name>()` top-level
extensions in kotc.backend, exactly like BirEmitterExpressions/Statements.kt. 6 batches (Types →
ControlFlow → Declarations → Lifts → Inline → Calls), each gated by a BIR-corpus diff -r + gate-fast at
the end. Top risks: run-scoped counters/state (forbid param-ification), the 5 behavior-neutral
private→internal widenings around call(), member-shadowing from incomplete deletes. No CPS engine in
this file — kotc emits suspend FACTS only.

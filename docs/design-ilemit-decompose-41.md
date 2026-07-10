# #41 ilemit decomposition — split Program.cs (Fable design, 2026-07-11)

Split the ~4226-line `toolchain/ilemit/Program.cs` into cohesive files WITHOUT changing behavior.
Verification is BEHAVIORAL (IL/PE bytes are not diffable like CIR json): each batch keeps
`./scripts/verify-il.sh` green; the batch touching the inline-splice path additionally runs
`./scripts/verify-roundtrip.sh` (the ONLY gate exercising `EmitInlineSplice`). Re-locate members by
NAME — line numbers drift as batches land.

## 1. The split mechanism (verified)
One class, C# **partial**: `sealed partial class Emitter`, declared in Program.cs and re-opened in
each sibling `Emitter.*.cs` (Emitter.Expressions.cs, Emitter.Statements.cs, Emitter.Metadata.cs,
Emitter.CompilerServices.cs, Emitter.ReverseBridge.cs — there is NO Emitter.Coroutines.cs; coroutine
lowering lives in bir2cir). `TypeInfo` is already a separate `sealed class` (TypeInfo.cs). Program.cs
also holds `static class IlEmit` (Main/driver). SDK-style csproj globs `*.cs` → new files need NO
csproj edit. Extraction = pure textual move of members between parts of one class; every field/method
stays reachable with zero signature changes. New files: sibling header + same `using` block +
`sealed partial class Emitter` (include `sealed` to match the majority).

## 2. Target file set (8 new + Program.cs remainder ~170 lines)
| File | Contents | ~lines |
|---|---|---|
| **Program.cs (STAYS)** | `static class IlEmit` (Main/CLI/MergeByFileClass/LoadInputDocument) + the Emitter overview comment + ALL core instance fields (except the per-cluster ones below) + Trace/T + BuildStdlibMode + ctor + EffectiveTps | ~170 |
| **Emitter.Assembly.cs** | EmitAssembly (passes 1–6 DefineType/bases+ifaces/signatures/bodies/.cctor/entry/bake, metadata-strip gating), Ordered, AccessOf, _methodTypeParams, _bodyDupSeen, DeclareMethod, EnsureCtorsDefined, TpName(s)/MentionsTv/ApplyConstraints/OwnerOpen, EmitCovariantBridge/TryEmitDimForwardBridge/_covarBridge, Save | ~900 |
| **Emitter.Bodies.cs** | EmitCtorBody/SelectCtor, EmitMethodBody, PrescanCfgLabels, EmitLdcI4, BeginMethod, Stmts*Return, LoopLabel, EmitForEachOf, EmitStoreCoerced, SetterValueType, ReadFqn/NeedsBoxToRef/EmitStelem/EmitLdelem/Subst, EmitThrowStub, EmitArgsTyped/EmitNewArgs/CtorArgTarget/RetOr/CoerceReturn/InterfaceMethodOn, EmitAddr (peeks _inlineThis), EmitStackBounds/EmitStackAddr, EmitInstanceCall, EmitArgs/EmitDefaultArg/EmitArgs2/EmitArg/EmitReturnCoerced/EmitCallArgs | ~650 |
| **Emitter.Resolve.cs** | ResolveField/ResolveMethod/AnchorInheritedOnBase/ExternalPropAccessor/IsSelfInstantiation/IsTbInstantiation/GenericCtor/GenericMethod/ResolveInheritedIfaceMethod/…, ApplyTypeArgs, FindField, sig-key machinery (BareTypeKey/SigKey/…/FindMethod/FindReflectedMethod*/…), FindStatic/ContainsTypeBuilder/IsTypeBuilderBackedGeneric, CanonicalSynthetics/StampCompilerGenerated/ResolvesExternally/_typeCache/ResolveType, PropAccessor/PropList, ResolveGenericMethod/Shape | ~950 |
| **Emitter.Operators.cs** | EmitConst, EmitLiteralValue, NumRank/NumericCommon/ConvTo, EmitBin/EmitDivRemGuarded/EmitUn/EmitConv, EmitNewArray/EmitArrayElemCoerced, EmitConcat, EmitNullableCoerced/EmitBranchCoerced/EmitCond, EmitObjMethod/EmitObjEq | ~350 |
| **Emitter.ClrInterop.cs** | EmitNativeClr* (safeCast/nullable×4/typeOf/getType/enum×4), EmitClrNew + ctor pickers + FuncArityOf, ParamAcceptsArg, EmitClrCall, native-spec resolution, EmitClrPropGet/Set, EmitClrEvent, EmitHandlerAsDelegate | ~650 |
| **Emitter.Delegates.cs** | DelegateCtor/IsGenericInst/InvokeOf/FuncRetType/FuncArgTypes/FuncArgSpecs, FuncType(string)+Synthetic*+FuncRetEnd/SkipTypeToken, + _syntheticDelegates/_syntheticDelegateCtors/_syntheticDelegateInvokes fields | ~200 |
| **Emitter.Types.cs** | SlotName/PrimShorthandName, ClrRef/PrimShorthand/NativeArrayOwnerAlias/NativeArrayOwner/MapArg/GenericType/SplitTopLevel, MapType×3/MapNullable/ConstructGeneric/ResolveTv/FuncType(Fn)/BuildFuncType/TryMapEmittedType | ~250 |
| **Emitter.InlineSplice.cs** | EmitSplicedStmts + EmitInlineSplice **plus** the 4 fields _inlineSubst/_inlineLambdas/_inlineDocs/_inlineThis — QUARANTINED for #75/#71 step-3 deletion | ~90 |

Judgment: EmitNativeClr* → ClrInterop (BCL-intrinsic handlers) not Operators; ResolveType/_typeCache
→ Resolve (lookup) while ClrRef/MapType → Types (spec mapping). Alternative: fold Operators into
Emitter.Expressions.cs (same partial) — kept separate only to bound file size.

## 3. Shared-state home
Trivial by construction (one partial class): every _il/_types/_locals/_curTypeParams access compiles
unchanged. All instance fields STAY in Program.cs EXCEPT: the _inline* quartet → InlineSplice.cs;
_syntheticDelegates* → Delegates.cs; _methodTypeParams/_bodyDupSeen/_covarBridge → Assembly.cs;
_typeCache/CanonicalSynthetics → Resolve.cs; PrimShorthand/NativeArrayOwnerAlias → Types.cs. All field
initializers are independent literals (none reads another Emitter field) — so cross-file
initializer order is a non-issue; do NOT introduce initializer cross-references during the move.
Keeping the overview comment + core fields in Program.cs keeps the existing "see Program.cs for the
overview" sibling headers true.

## 4. Extraction order (5 batches; each: move members verbatim, dotnet build toolchain/ilemit, verify-il, commit)
1. **Leaf helpers:** Emitter.Types.cs + Emitter.Delegates.cs (pure spec/TypeNode functions).
2. **Resolution:** Emitter.Resolve.cs (biggest; leaf-ish, calls only Types).
3. **CLR interop + operators:** Emitter.ClrInterop.cs + Emitter.Operators.cs.
4. **Inline-splice quarantine + bodies:** Emitter.InlineSplice.cs (with its 4 fields) + Emitter.Bodies.cs. Gate: verify-il AND verify-roundtrip (the only exerciser of EmitInlineSplice).
5. **Assembly passes:** Emitter.Assembly.cs (EmitAssembly/DeclareMethod/bridges/Save). Program.cs is now driver + core state only. Gate: verify-il, then full make verify.

## 5. Risks
1. **Field-initializer order across partial files** is C#-unspecified — currently all independent
   literals; keep instance fields consolidated in Program.cs except the per-cluster ones. Trace (the
   only env-dependent static) stays in Program.cs.
2. **Inline-splice quarantine (#71/#75):** the path is slated for retirement (#75 step 3 deletes kotc
   inlineSpliceCall + ilemit EmitInlineSplice). The new file's header MUST LIST the 4 external
   touchpoints step-3 deletes with it: Emitter.Expressions.cs (`_inlineSubst` local-subst;
   `_inlineLambdas` delegateInvoke splice via EmitSplicedStmts; `case "inlineSplice"`) and the
   `_inlineThis` peek in EmitAddr (moves to Emitter.Bodies.cs). DecodeCarrier STAYS in
   Emitter.CompilerServices.cs (shared with metadata reading) — not quarantined. Update
   design-inline-unification-75.md's now-stale Program.cs line citations in the same batch.
3. **Behavioral-only gate won't catch a silently-dropped overload** (3× MapType, 2× FuncType, 2×
   ResolveMethod, 2× StampCompilerGenerated) — a caller may pick another overload and still compile.
   Move whole contiguous ranges, `grep -c` member counts pre/post, rely on the gate.
4. No behavior knobs: MergeByFileClass order, pass 1–6 sequencing, Ordered() base-before-derived bake
   are all inside single methods that move as units.

## Summary
8 new files + Program.cs shrinks to ~170 (IlEmit + Emitter core state/ctor). 5 batches, each
verify-il (batch 4 adds verify-roundtrip; batch 5 ends with full make verify). Top risks: partial
field-initializer order (keep independent), the inline-splice quarantine's 4 touchpoints (list in the
file header for #75), behavioral gate blind to dropped overloads (diff member counts).

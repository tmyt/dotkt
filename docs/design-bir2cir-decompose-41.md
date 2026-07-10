# Decomposition plan: `toolchain/bir2cir/Program.cs` (task #41, Fable audit 2026-07-11)

Target: split the 7007-line `Program.cs` into per-pass files mirroring the 33 existing siblings, **byte-identical CIR output**, no behavior change. Anchors drift as batches land — re-locate by class name, never by raw line.

## 0. Ground facts that make this a low-risk carve-out
- Every construct is a **top-level class/record/enum in the global namespace** (no `namespace`, no `partial`, no `file` types) — identical to every sibling. Cross-class access is `internal`/`public` static calls, identical across files in the same assembly. **Extraction = verbatim text move + a per-file `using` header.**
- `bir2cir.csproj` is SDK-style (implicit `**/*.cs` glob). New files auto-picked-up; **no csproj edit**.
- Sibling header: `using System; using System.Collections.Generic; using System.Linq; using System.Text.Json.Nodes; using DotKt.Bir;` (add `System.Reflection` + `System.Runtime.InteropServices` for the metadata index; `System.Text.Json` where `JsonSerializerOptions` appears).
- The gate is **CIR-output byte identity**, NOT bir2cir.dll byte identity (file order permutes metadata tokens — irrelevant; behavior is what must match, and whole-class moves cannot change it).

## 1. Driver cluster — STAYS in Program.cs
`Bir2Cir` (Main), `Pipeline` (the pass driver: Run / LoadBirFiles / SubstituteCharSeqIdentity / TransformFiles + the per-pass ordering & gate comments + local-fact pre-scans / CollectLocalValueTypes / IsEmptyCir / DeclaresCharSeqImplementer / WriteCirFiles / OutputNameFor), `enum BuildStdlibMode`, `DriverOptions`, `BirFile`/`CirFile` records, `JsonOptions`, `UsageException`. ≈ 700 lines after.

## 2. Target file set (21 new files, all in toolchain/bir2cir/, global namespace)
| New file | Moves |
|---|---|
| `ReferenceMetadataIndex.cs` | `ReferenceMetadataIndex`, `ReferenceAssembly`, `ReferenceDotKtMetadata`, `MemberBinding` |
| `CallSiteAnalyzer.cs` | `CallSiteAnalyzer`, `CallSiteAnalysis`, `CallSite` |
| `SuspendShapeAnalyzer.cs` | `SuspendShapeAnalyzer`, `SuspendShapeAnalysis`, `SuspendFunctionShape` |
| `BirTypeLowering.cs` | `BirTypeLowering` |
| `IteratorConsumerNormalization.cs` | `IteratorConsumerNormalization` |
| `DefaultArgSplice.cs` | `DefaultArgSplice` |
| `CharSeqStringLowering.cs` | `CharSeqStringLowering` (+ nested Env) |
| `StringCharSequenceBridge.cs` | `StringCharSequenceBridge` (+ nested Env) |
| `ComparableBridgeSynthesis.cs` | `ComparableBridgeSynthesis` |
| `NullableGenericReturnErasure.cs` | `NullableGenericReturnErasure` |
| `StarProjectionLowering.cs` | `StarProjectionLowering` |
| `CatchClauseWidening.cs` | `CatchClauseWidening` |
| `NullableFuncReturnErasure.cs` | `NullableFuncReturnErasure` |
| `ClrEventOperatorBinding.cs` | `ClrEventOperatorBinding` |
| `NetInteropBinding.cs` | `NetInteropBinding` |
| `KClassMemberBinding.cs` | `KClassMemberBinding` |
| `MemberCallSubstitution.cs` | `MemberCallSubstitution` (+ nested SubstCtx) |
| `RefBodySquash.cs` | `RefBodySquash` |
| `DeclarationRename.cs` | `DeclarationRename` |
| `MemberStrip.cs` | `MemberStrip` |
| `AliasHelperHoist.cs` | `AliasHelperHoist` |

## 3. Shared-helper home
- `ReferenceMetadataIndex.cs` IS the shared-helper home (`BareOwnerFqn`, `ParamKey`×3, `HelperTypeName` stay on the index class). Do NOT create Common.cs/Utils.cs.
- `TypeJson` (TypeJsonUtil.cs), `StaticType`/`BirScope` (StaticTypeResolver.cs) are the already-extracted shared homes; nothing moves in.
- `BirTypeLowering.PrimArrayElem` (used by ArrayConstructionLowering + MemberCallSubstitution) STAYS on BirTypeLowering.
- Deliberately-duplicated private helpers (`Str` in 8 classes, `SplitTopLevel` in 3, `Bare`/`IsStringTok*` in the two CharSequence passes) stay duplicated by design. Do NOT unify (optional follow-up only).
- Dependency direction (acyclic): passes → {ReferenceMetadataIndex, TypeJson, StaticType, BirTypeLowering.PrimArrayElem}; DeclarationRename → NetInteropBinding (2 internal statics); MemberStrip → DeclarationRename.ResolveSlot; Pipeline → everything.

## 4. Extraction order — 6 gate-sized batches
Each: move classes verbatim (WITH their header comment blocks), add the `using` header, delete moved text from Program.cs, `dotnet build`, CIR byte-identity diff, commit.
- **Batch 1 — leaf micro-passes (7 files ~800L):** IteratorConsumerNormalization, DefaultArgSplice, ComparableBridgeSynthesis, StarProjectionLowering, CatchClauseWidening, ClrEventOperatorBinding, KClassMemberBinding. Zero intra-Program deps.
- **Batch 2 — CharSequence + nullable-erasure (4 files ~1050L):** CharSeqStringLowering, StringCharSequenceBridge, NullableGenericReturnErasure, NullableFuncReturnErasure. Whole-class moves keep static run-scoped state intact.
- **Batch 3 — diagnostics analyzers (2 files ~280L):** CallSiteAnalyzer.cs, SuspendShapeAnalyzer.cs.
- **Batch 4 — .NET-binding / declaration-shaping quintet (5 files ~950L):** NetInteropBinding, DeclarationRename, MemberStrip, AliasHelperHoist, RefBodySquash (move the NetInteropBinding←DeclarationRename←MemberStrip internal-helper chain in the SAME batch for reviewer context).
- **Batch 5 — the substituter (1 file ~1460L):** MemberCallSubstitution.cs. Isolated so the gate isolates any slip.
- **Batch 6 — shared infra (2 files ~1710L):** BirTypeLowering.cs, then ReferenceMetadataIndex.cs (+3 records). Last (most-referenced); Program.cs is now driver-only.

## 5. Gate: byte-identical CIR per batch
1. Baseline once (before batch 1): build bir2cir; capture a CIR corpus — stdlib both modes (`--build-stdlib=metadata` and `=runtime` over the stdlib BIR into baseline-ref/ / baseline-rt/) + a spread of existing `build/bir-*/**.bir.json` app inputs (see `scripts/dotkt.sh:98` for the CLI).
2. Per batch: rebuild, rerun the same invocations into after/, `diff -r baseline/ after/` — MUST be empty. Any diff = transcription error.
3. End: `./scripts/verify-il.sh` (or gate-fast) + a full stdlib rebuild to confirm end-to-end.

## 6. Risks
1. **Static mutable, run-scoped state — never split a class, never param-ify a static field.** `StringCharSequenceBridge._adapterEmitted` (once-per-RUN adapter dedup across files), `CharSeqStringLowering._subSeqTmp` (process-global temp counter), `MemberCallSubstitution._localTopLevelFns`/`_attributeTopLevelOwner`, `NetInteropBinding._refs`, `BirTypeLowering._aliases`/`_isValueFqn` (set once per Apply/Lower, read by deep static helpers). Param-ifying any → temp-naming/dedup change → CIR diff. Whole-class verbatim moves preserve all of it. TOP scope-creep trap.
2. **Comment relocation:** each class's header comment moves WITH the class; each *call-site* gate comment in `Pipeline.TransformFiles` STAYS in Program.cs (they document pipeline ordering, not pass internals).
3. **Cross-class internal-helper coupling:** DeclarationRename→NetInteropBinding.MemberIsPropertyOrField/DeclaresPublicMethodNamed; MemberStrip→DeclarationRename.ResolveSlot; MemberCallSubstitution→BirTypeLowering.PrimArrayElem; CallSiteAnalyzer→ReferenceMetadataIndex.ParamKey. All internal/public statics → extraction order can't break the build.
4. Do NOT touch the 17 sibling files' comment references to MemberCallSubstitution/BirTypeLowering — ordering prose, not callers.
5. Compiled-binary nondeterminism is a red herring: the gate is CIR output, which file layout can't affect.

## Summary
21 new files; Program.cs 7007 → ~700 (Main + Pipeline + DriverOptions + records + JsonOptions + UsageException). 6 batches, each gated by CIR-corpus `diff -r` + verify-il/gate-fast at the end. Top risks: run-scoped static state (forbid param-ification), comment relocation, duplicated-helper scope creep.

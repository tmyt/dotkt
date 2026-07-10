# #73 — kotc residual CLR/stdlib special-casing: COMPREHENSIVE inventory (Fable audit, 2026-07-10)

Currency-corrected successor to `docs/kotc-recognition-audit.md` (2026-07-08); everything that doc marks ✅ DONE was re-verified gone (#52/#57/#61/#66/#68/#70/#72/#76/#77/#78/#80 all landed). This doc lists ONLY what REMAINS in `toolchain/kotc/src` (all anchors = `backend/BirEmitter.kt` unless noted).

> Anchors are as of the audit (post-#72, commit a2e5309). Line numbers drift as items land — re-locate by pattern, never by raw line number.

## Classification table

### MOVE-TO-bir2cir (13 families)

| # | Residual | Anchor | CLR knowledge encoded | Contract (kotc emits X / bir2cir derives Y) | Risk |
|---|---|---|---|---|---|
| M1 | for-in `forEachInline` gate | 1664-1668 | "a facadegen-injected type OR literal `kotlin.sequences.Sequence` enumerates via GetEnumerator" — a `clrName()` read + a hardcoded stdlib FQN deciding loop shape | kotc emits `forIn{src,srcType,elem,fallback}` for ALL non-array sources (protocol already exists, 1674-1675); bir2cir `ForInLowering` (which ALREADY dispatches forIn→forRange/counted-for/forEachInline/fallback, ForInLowering.cs:6-29) adds "srcType resolves to a referenced .NET enumerable / Sequence → forEachInline". Its own comment (:38-40) acknowledges the kotc-classified forEachInline as residue | **HIGHEST of loop family** — same EntryPointNotFound hazard the #72 doc flagged for item-7; prove stdlib CIR byte-identical before deleting the gate |
| M2 | range membership `x in a..b` | 3345-3358 | Lowers to `>=`/`<`/`<=` cond keyed on **bare names** `contains` + `rangeTo`/`until`/`rangeUntil` — NO FQN gate. A user type with `operator rangeTo`+`contains` is **miscompiled** to primitive comparisons (the exact 3-strike pattern of #72's `downTo`, which was moved) | kotc emits the faithful `contains` member call; bir2cir lowers it FQN-keyed (`kotlin.ranges.*` owner / primitive receiver) beside `RangeConstructionLowering`, keeping bindOnce single-eval semantics via a temp | MED-HIGH; includes a live-miscompile fix — add an il-case for a user rangeTo/contains type |
| M3 | direct enum `values()`/`entries`/`valueOf` | 3360-3372, esp. **3362 `val et = "@" + ec.name`** | Emits the **banned legacy `@Name` type-token** (the `@`/`clr:`/shorthand vocabulary CLAUDE.md forbids kotc to produce) + the System.Enum-reflection-shaped `enumValues`/`enumParse` nodes | kotc emits the faithful member/intrinsic call (as the reified path already does, 3373-3380); bir2cir `EnumIntrinsicLowering` — which already handles BOTH the structured form and "kotc's legacy `@Name` string form" (#77) — becomes the sole producer; delete the `@` token | MED; kills the last `@`-marker in kotc |
| M4 | A2 tail: clr\*-dialect nodes still emitted for injected owners | `newClr`: Expressions 166-177 + 1951-1963; `clrPropGet/Set` on IrGet/SetField: Expressions 127-143, Statements 112-120; `clrOverride`: 1437-1457; .NET method-ref shapes `newBoundClrDelegate`/mref lift: 2000-2027; (`clrEventGet` 3556-3566 documented-deliberate, may stay) | kotc still decides the .NET **shape** (ctor=newClr, field=clrPropGet, base-property-override=clrOverride, delegate=newBoundClrDelegate) for facadegen-injected owners — the exact deviation CLAUDE.md's A2 note says bir2cir owns; #61 moved only the CALL family | kotc emits plain `new`/`field`/`setField`/override-marker by FQN identity; bir2cir `NetInteropBinding`/`TransformNew`/`DeclarationRename` resolve the owner off the refs and shape it (TransformNew already does this for @ClrTypeAlias exceptions) | MED per sub-family; sequence: newClr → clrPropGet/Set → clrOverride → mref shapes |
| M5 | System.Object slot names | `objectMethodName` 4304-4319; `objMethod GetHashCode/ToString/Equals` call sites 3860-3891; decl `emitName` 1272; richEnum `ToString` 669; used in mrefs 2006/3503/3573 | **BCL member names hardcoded in kotc** (`ToString`/`GetHashCode`/`Equals`) — same species as the get_size→get_Count rename already moved to bir2cir `DeclarationRename` | kotc emits the Kotlin names + the existing `overrides`(kotlin.Any) fact; bir2cir renames decl slots and re-emits `objMethod` at call sites (StaticType machinery already recovers receiver types there) | **HIGHEST overall** — pervasive (equality, toString, enum, functionRef); do LAST |
| M6 | precondition/error family | 3957-3987 (`TODO`/`error`/`require`/`check`/`requireNotNull`/`checkNotNull`, `noWhenBranchMatched…`/`throwUninitialized…`) | stdlib-symbol semantics baked in kotc (FQN-keyed throw/cond synthesis). Exception FQNs are pure Kotlin (layer-ok); the *recognition* is misplaced | kotc emits faithful calls; bir2cir lowers FQN-keyed (mechanism-(b) — the @InlineOnly bodies don't exist in the rt.dll, so a layer must lower; that layer is bir2cir) | MED-LOW |
| M7 | `kotlin.repeat` inline loop | 3995-4004 | stdlib-fn recognition → `repeatInline` counter loop | faithful call; bir2cir re-emits `repeatInline` (or splices) | LOW |
| M8 | `ieee754equals` | 3969-3972 | lowered to `binOp ==` in kotc while its whole family (EQEQ/EQEQEQ/less…) was moved to bir2cir via the `kotlin.internal.ir` owner marker | emit the same faithful `kotlin.internal.ir` call; PrimitiveOperatorLowering adds the arm | LOW |
| M9 | `toByteArray`/`toUByteArray` reinterpret | 3312-3319 | "UByteArray IS Byte[]" reinterpret-cast fact (#76 residue), receiver-FQN-keyed | faithful extension call; bir2cir rewrites keyed on the resolved `kotlin.UByteArray`/`kotlin.ByteArray` receiver | LOW |
| M10 | `strReversed` | 4016-4019 | `new string(Reverse(s).ToArray())` semantic node; self-documented as pending a stdlib `StringBuilder(CharSequence)` fix | fix the stdlib ctor, delete the branch (cardinal rule: stdlib-side fix) | LOW |
| M11 | `System.Span` literal | 4391-4392 | a literal BCL FQN in `birType` (`kotlin.clr.Span` → `"System.Span"`) — the only naked `System.*` type name left in kotc | emit the `kotlin.clr.Span` identity; bir2cir substitutes (it owns every other alias) | LOW (trivial) |
| M12 | **= task #81**: baked `get_`/`set_` at cross-module accessor CALL sites | companion EXT prop 3620-3625; top-level EXT prop 3693-3703; top-level computed 3709-3713; propertyRef `accessorCall` 2103-2140 | the CLR accessor-NAME convention baked at call sites where the callee may be a stdlib member bir2cir must substitute (@ClrIntrinsic on the accessor) — the #78 fix (identity + `"prop":"get"/"set"` marker, 3633-3639) covers only companion COMPUTED props | emit bare property identity + `prop` kind marker (the #78 convention); bir2cir shapes get_/set_ or substitutes the intrinsic. (In-kotc-emitted user classes at 3837-3838 are self-consistent producer+consumer — fine) | MED |
| M13 | Pair/Triple/IndexedValue `.first/.second/.third/.index/.value` → raw `field` reads; `EnumEntries.size` → arrayLen | 3726-3741; 3746-3749 | stdlib-type LAYOUT assumptions ("these props are plain fields") — suspicious: kotc's own stdlib emission makes those backing fields `internal` + accessor-routed (1050-1051), so a cross-assembly field read shouldn't even bind; needs empirical why | faithful property calls; stdlib/bir2cir resolve. Investigate before moving | LOW-MED (investigate first) |

### GENUINE (stays in kotc — justified)

| # | Item | Anchor | Why not CLR knowledge / why sanctioned |
|---|---|---|---|
| G1 | `clrName()` as injected-type IDENTITY read | 4142-4151 + `frontend/ClrTypeInjection.kt:131,292` | The sanctioned A2 stage-1 read: facadegen metadata keyed by resolved IR ClassId, TYPE identity only (member slots gone since #61 step 5). Its ~25 origin-test call sites answer "is this ours to emit / injected", which is legitimate. The clr\*-SHAPE consumers of the read are M4, not the read itself |
| G2 | Structural lowerings | closures/SAM/anon-objects/local fns/ref-cell facts (registry 324-331)/propertyRef lift/inner-class flatten/tailrec 1578-1601/inline splice 2275-2543; scope-fns 358+3283-3288 and `use` 3289-3295; `close()` emitted by pure `kotlin.AutoCloseable` identity, 2220 | Kotlin-language shape, no CLR names; prior audit verdict (c) reconfirmed |
| G3 | Enum rich/basic split + richEnumDef synthesis | 580-705 | class-KIND language fact (IR-kind-gated, no FQN table) — except M3's tokens |
| G4 | Value-nullable `Nullable<T>` unwrap family | 4268-4293, Expressions 96-106/206-219, CHECK_NOT_NULL 3326-3335 | Per CLAUDE.md ("value-type primitives stay compiler-intrinsic") + the frozen BIR vocabulary (`nullableHasValue`/`nullableValue`/`nullableWrap`). **Honest tension flagged:** this IS CLR representation ("Int? is a struct"); relocating is the same cost-class as the deferred operator bucket — a future dedicated mega-task, never piecemeal |
| G5 | `kotlin.clr.*` intrinsics + annotation-flag reads | byref 4155-4156, stackBuffer 3127+2570-2604 (`stackptr` token 2583 = cosmetic vocabulary wart), ClrEvent 3136-3142/131-139, @ClrAwait 1336-1337, @ClrRefArgument 4161-4162, @ClrField 805, @Volatile 812 | The sanctioned interop-surface reads; ClrEvent's `clrEventGet` is documented CLR-only vocabulary with no plain-Kotlin form |
| G6 | @InlineOnly-convention reconstruction | Lazy 3157-3163/3766-3772; ReadWriteProperty 3173-3186/3787-3801; map-delegate 3802-3818; `kPropertyStub` 2041-2042 | Reconstructs absent inline bodies using REAL stdlib identities (get_value on kotlin.Lazy, real generic interfaces) — no BCL names; FIR-resolution-driven |
| G7 | Pipeline/session plumbing | `ClrDefaultImports.kt`, `ClrCliPipeline.kt:143-155` mode select, `ImportScan.kt:48` kotlin./java. filter, `fileClassName` Clr-strip 190-198 (mirror: ClrTypeInjection `stripClrFileClass` 327-330 — keep in sync), ClrTypeInjection metadata materializer (UNSIGNED_KOTLIN_TYPES:82, builtinBoundOpen:873 are frontend-resolution tables) | build/session concerns, the designed facadegen channel |
| G8 | **GENUINE-GAP (fix in kotc, NEW TASK)**: callable-reference completion | `functionRef` gating 1969-1983 → `unsupported` 2029-2030 (ANY extension-function reference — `::isNotBlank`, `String::indentWidth` — fails; the #72 Indent.kt `.filter { it.isNotBlank() }` lambda-wraps merely MASK it); `propertyRef` deferrals 2064-2075 (KProperty2 / lateinit / @ClrField / .NET-iface-override) | Not a move: the fix is a kotc structural lift (a static forwarder whose BODY is the faithful call — bir2cir then substitutes it like any call). This is the seeded "@ClrTypeAlias-receiver callable-ref gap" + "method-reference-to-.NET gap" |

### DEAD / NEAR-DEAD

| # | Item | Anchor | Note |
|---|---|---|---|
| D1 | `clrIfaceMemberName` | 829-833 (consumers 505, 881, 893, 1120-1121, 1239, 1271, 1830, 1903, 2073, 4098) | Returns `"get_length"` for kotlin.CharSequence.length — **identical to the default `get_`+name fallback at every consumer**; only side effect is forcing override/virtual/public flags (interface members bind by name/signature per 898-899, so likely redundant). Verify BIR byte-diff, then delete (fold flags if load-bearing) |
| D2 | `charSeqIface` | 308-309 (+ ownerSpec 1405) | Identity mapping FQN→same FQN (#52/#68 made it vestigial). ownerSpec:1405 drops type args vs. the general path — verify, then inline/delete |
| D3 | propertyRef `get_annotations` body calling bare-name `emptyList` | 2142 | name-synthesized stdlib call; duplicate of what `ClrPropertyStub` already provides — consolidate |

## #74 / #81 / #82 — subsumed or separate?

- **#74** (erased/star collection member access): **LANDED** (CHANGELOG "#74", commit `b229795`) — bir2cir-side; NOT a kotc residual; outside this inventory.
- **#81**: **subsumed** — it is exactly M12.
- **#82** (KTypeProjection.STAR staticField misroute): **separate** — a routing-correctness bug in the companion-property family (companion `staticField` route 3633-3640 + the vis-less companion statFields 1058-1063), adjacent to M12 but not fixed by it. Keep as its own task.
- **NEW tasks needed:** (a) G8 callable-reference completion; (b) M4 A2-tail; (c) M5 Object-slot renaming; (d) M1-M3+M6-M13 bundled as "#73 execution" waves. The residual set is **substantially bigger** than {#74,#81,#82}.

## Recommended execution order (gate each with `./scripts/verify-il.sh` + differential/roundtrip)

1. **Trivial batch:** M11 (System.Span), M8 (ieee754equals), M9 (toByteArray), M10 (strReversed stdlib fix) — independent.
2. **M3** (enum `@`-token — kills the last banned type-token vocabulary; consumer already dual-path).
3. **M2** (range `contains` — live miscompile fix; add the user-rangeTo il-case).
4. **M1** (forIn gate — highest loop-family risk; prove stdlib CIR byte-identical; forIn fallback protocol is the safety net).
5. **M12 (#81)**, then **#82**.
6. **M6 + M7** (preconditions/repeat).
7. **M13** (after the internal-field empirical investigation).
8. **M4** (A2 tail, sub-family sequence newClr → clrPropGet/Set → clrOverride → mref shapes).
9. **M5** (Object slots — last; pervasive).
- **G8** (callable refs) is orthogonal — can run any time in parallel-ish (separate files).
- D1/D2/D3 opportunistically with adjacent steps (byte-diff-verified).

## Summary

- **Counts:** MOVE-TO-bir2cir **13 families** (M1-M13), GENUINE **8 families** (incl. G8 genuine-GAP needing a new kotc task), DEAD/near-dead **3** (D1-D3).
- **Top 3 value/risk moves:** (1) M5 Object slot names (pervasive BCL naming, do last); (2) M1 the forEachInline gate (highest loop-family risk, mechanism proven by #72); (3) M4 the A2 tail (direct continuation of #61). Honorable mention: M2 range-`contains` is a live user-type miscompile.
- **Residual set vs. the 3 known tasks:** bigger. #74 landed, #81 = M12, #82 separate — leaving ~11 MOVE families + G8 uncovered.
- **First step:** trivial batch (M11+M8) to warm the mechanism, then M2 (first substantive move + miscompile fix).

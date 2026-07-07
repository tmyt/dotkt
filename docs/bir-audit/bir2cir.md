# bir2cir — BIR/CIR vocabulary audit (for the BIR/CIR freeze, ship-task #37)

READ-ONLY audit of `toolchain/bir2cir/`. Catalogs every BIR node kind (`"k"`) bir2cir CONSUMES
(reads from kotc's BIR), every node kind it PRODUCES into the CIR ilemit consumes, every
type-token parser/splitter/normalizer, the `@ClrIntrinsic` substitution vocabulary, and the
embedded-BIR-json re-parse ("splice") sites.

Files in scope (all `.cs`, `Program.cs` = 5740 lines):
`Program.cs`, `SuspendColdLowering.cs` (3023), `SuspendLambdaLowering.cs` (253),
`MapVarianceRealign.cs`, `NestedCollectionCountLowering.cs`, `ArrayNullableElemRealign.cs`,
`EnumMemberBinding.cs`, `GenericSelfInstantiation.cs`, `CrossClassPrivateWidening.cs`,
`ContinuationErasure.cs`, `TryValueOperandHoist.cs`, `ValueTypeNullableCollectionArg.cs`.

This is the drift epicenter: many agents added independent lowering passes here, each with its
OWN copy of the top-level comma splitter and its own ad-hoc prefix scanning. The freeze should
name one canonical token grammar + one shared parser module and forbid new private copies.

---

## 1. NODE KINDS CONSUMED (matched/read from incoming BIR)

The main member-call substitution dispatch (`MemberCallSubstitution.Transform`, `Program.cs:4372`)
switches on only 4 kinds; everything else is consumed by the many satellite passes. Full set of
node kinds bir2cir reads (via `Str(o["k"])` equality, `case` labels, or `HashSet.Contains`):

| `k` value | Primary read sites | Consumed for |
|-----------|--------------------|--------------|
| `callStatic` | `Program.cs:4378` (Transform), `:2842`,`:3144`, `SuspendColdLowering.cs:1362/1454/1491`, `CrossClassPrivateWidening.cs:77`, `IteratorConsumerNormalization` `:2175` | member-call substitution; suspend cold-call detection; CharSequence rewrite; private-widening |
| `callInstance` | `Program.cs:4377`, `:3145`, `GenericSelfInstantiation.cs:57`, `CrossClassPrivateWidening.cs:73`, `SuspendColdLowering.cs:1362/1500`, `EnumMemberBinding` | member-call substitution; self-instantiation; enum member binding |
| `new` | `Program.cs:4376` (TransformNew), `SuspendColdLowering.cs:1488` | construction → `newClr` on CLR-bound owner |
| `staticField` | `Program.cs:4379` (TransformStaticField), `:1384`, `SuspendColdLowering.cs:1456/1506` | companion INSTANCE load → null const; impurity/type-of-expr |
| `field` | `Program.cs:1383`,`:3637`, `GenericSelfInstantiation.cs:58`, `CrossClassPrivateWidening.cs:81`, `SuspendColdLowering.cs:1456/1506` | self-instantiation; SM field lowering; impurity |
| `setField` | `GenericSelfInstantiation.cs:59`, `CrossClassPrivateWidening.cs:82`, `TryValueOperandHoist.cs:96` | self-instantiation; operand hoist |
| `setFieldExpr` | `Program.cs:1385`,`:5689` | call-site analysis |
| `staticFieldSet` | `Program.cs:1386`,`:5689` | call-site analysis |
| `var` | `Program.cs:2835`,`:3148`, `ArrayNullableElemRealign.cs:40`, `IteratorConsumerNormalization:2174`, `SuspendColdLowering.cs:1039/1115/1488`, `TryValueOperandHoist.cs:91` | array-elem realign; iterator var retype; SM var lowering; operand hoist |
| `setLocal` | `SuspendColdLowering.cs:1051/1124`, `TryValueOperandHoist.cs:92` | SM rewrite; operand hoist |
| `local` | `SuspendColdLowering.cs:1048`,`:1311`, `MapVarianceRealign.cs:311`, `Program.cs:2976/3221` | var-type lookup; string-detect; SM |
| `return` | `Program.cs:2845`,`:3151`, `SuspendColdLowering.cs:1132`, `TryValueOperandHoist.cs:93` | decl-sig lowering; SM; operand hoist |
| `throw` | `TryValueOperandHoist.cs:94` | operand hoist |
| `throwExpr` | `SuspendColdLowering.cs:1455` (ImpureKinds) | impurity classify |
| `exprStmt` | `Program.cs` (walk), `SuspendColdLowering.cs:1138`, `TryValueOperandHoist.cs:95` | SM rewrite; operand hoist |
| `cast` | `Program.cs:2849/2977/3154/3222`, `SuspendColdLowering.cs:1488` | string-detect; type-of-expr |
| `const` | `Program.cs:2975/3220`, `SuspendColdLowering.cs:1488` | string-detect; type-of-expr |
| `bin` | `Program.cs:4921` (approx), `SuspendColdLowering.cs:1521` | type-of-expr (op → Boolean/left) |
| `concat` | `Program.cs:2978/3223` | string-detect (always string) |
| `this` | `Program.cs:2979`,`:3223` | string-detect (never string) |
| `block` | `SuspendColdLowering.cs:1055/1141` | SM flatten |
| `valueBlock` | `Program.cs` (CharSeq), `SuspendColdLowering.cs:1056/1488` | type-of-expr; SM |
| `try` | `SuspendColdLowering.cs:1063/1157`, `MapVarianceRealign` | SM try lowering |
| `label`/`goto`/`brIf` | `SuspendColdLowering.cs:1144/1145/1148` | SM control-flow (consumed AND produced) |
| `lateinitGet` | `SuspendColdLowering.cs:1456/1506` | impurity; type-of-expr |
| `arrayGet` / `clr.ldelem` | `SuspendColdLowering.cs:1457/1516` | impurity; element type-of-expr |
| `isinst` | `Program.cs` (walk) | (type-token bearing) |
| `cond` | `Program.cs` (walk) | (type-token bearing) |
| `objEq` | produced; also walked | SM |
| `newSuspendLambda` | `SuspendLambdaLowering.cs:129/151/161`, `SuspendColdLowering.cs:88/570`, `Program.cs:286` | suspend-lambda → `new <SM>` |
| `newClosure` | `Program.cs:1891/2742/4038`, `SuspendColdLowering.cs:83/155/176` | delegate/CharSeq; suspend cold detection; LambdaKinds refusal |
| `newDelegate` | `Program.cs:2742/4036`, `SuspendColdLowering.cs:83/155` | CharSeq delegate targets; suspend detection |
| `delegateInvoke` | `Program.cs:2742/4044` | CharSeq / nullable-func erasure |
| `forEachInline` | `Program.cs:3637`, `SuspendColdLowering.cs:83` | nullable-generic loop repair; LambdaKinds refusal |
| `repeatInline` / `lambda` | `SuspendColdLowering.cs:83` | LambdaKinds refusal |
| `defaultArg` / `defaultArgParam` | `Program.cs` (KotlinDefault splice) | cross-module default-arg fill |
| `smSelf` | `SuspendColdLowering.cs:1246/1284/1331/1836` | INTERNAL only (rewritten to `this` before emit) |
| `dynCall` | `SuspendColdLowering.cs:1455` (ImpureKinds) | impurity |

`CallSiteAnalyzer.InterestingKinds` (`Program.cs:1378`) additionally enumerates the CLR-side kinds
it walks for analysis: `clrStatic clrGenericStatic clrInstance clrGenericInstance newClr clrPropGet
clrPropSet clrStaticField` — these are normally PRODUCED by bir2cir but are also re-read when a
pass runs after substitution.

---

## 2. NODE KINDS PRODUCED (emitted into CIR)

Grouped by producing pass. All are `["k"] = "…"` object constructions or `obj["k"] = …` mutations.

### 2a. Member-call substitution (`Program.cs`, MemberCallSubstitution)
| `k` produced | Site | Meaning |
|--------------|------|---------|
| `clrInstance` | `:2278`,`:2414`,`:4638`,`:5072`,`:5147`, ClrCallNode | instance BCL call `System.X.Method` (Rule 1c/2/4) |
| `clrStatic` | `:5147` (ClrCallNode, `instance?…:clrStatic`) | static BCL call |
| `newClr` | `:4443` (TransformNew), `:5364` | `new System.X(..)` on CLR-bound reference owner (Rule 1) |
| `clrPropGet` | `:2904`,`:3911/3917`,`:4247`,`:5104` (ClrPropNode `write?…`) | property read `get_X` → `System.X.Prop` |
| `clrPropSet` | `:5104` | property write |
| `constrainedCall` | `:4822` (Constrainify) | `constrained.` virtual dispatch on a `gp:T` receiver over a CLR-bound interface (IComparable / MutableCollection.add) |
| `constrained-`fields | `recvType`,`iface`=`clrg:Owner[args]`,`method`,`recv`,`args`,`argTypes`,`ret` | (constrainedCall payload) |
| `conv` | `:4590` | @JvmInline backing-field getter collapse `get_x()` → `conv(recv)` |
| `const` (`object` null) | `:4394` (TransformStaticField), `:3918` | erased companion INSTANCE / count-zero literal |
| `callStatic` | `:2218`,`:2258`,`:4964`,`:4982`,`:5198`, Rule3HelperCall `:5198`, CollDefaultCall/MapDefaultCall | Rule 3 helper (`<>dotkt_ClrH_*`), collection/map default helpers (`ClrCollectionDefaultsKt.clrCollAdd`, `ClrMapDefaultsKt.clrMapGet`, …), toplevel intrinsic |
| `cast` | `:3464`,`:3669`,`:4052/4058` | object-box / value-type coercion inserted around args & inits |
| `bin` (`==`) | `:3916`,`:2940` | isEmpty→Count==0 lowering; substring arithmetic |
| `new` | `:3206` | adapter type instantiation |
| `throw` | `:5361` | (with nested `newClr`) |
| `valueBlock` | `:2926` | CharSequence.subSequence temp-spill block |
| `clrGenericStatic` | `ValueTypeNullableCollectionArg.cs:65` | value-type nullable-collection arg wrap |
| `clrMapSize` etc. | `:4690` **(NOTE: METHOD-name string, NOT a `k` value)** | routed as `method` on a callStatic to `ClrMapDefaultsKt` |

### 2b. Suspend cold lowering (`SuspendColdLowering.cs`) — synthesizes the state machine
Produces: `callStatic` (cold entry / bridge / newSafeContinuation / throwOnFailure / getCompleted),
`callInstance` (resumeWith / MoveNext), `clrInstance` (GetAwaiter/OnCompleted/GetResult/Wait/
GetCompleted), `clrPropGet` (IsCompleted / `Task` / `type=tcsType`), `newClr` (TaskCompletionSource /
RootContinuation), `new` (`<SM>` construct), `field`/`setField` (SM slots, `recv:{k:smSelf}`),
`var`/`setLocal`/`return`/`exprStmt`/`try`/`block`/`label`/`goto`/`brIf`/`objEq`/`bin`/`cast`/`const`/
`local`/`this`, and the INTERNAL `smSelf` marker (`:1727`,`:1836`) that it later rewrites to `this`
(`:1246`,`:1284`).

### 2c. Other passes
- `SuspendLambdaLowering.cs:229` — `new` (`<SM>`), `local`/`this`/`const` args.
- `EnumMemberBinding.cs:50` — `objMethod` (boxes value-type `gp:T` receiver, calls System.Enum override).
- `NestedCollectionCountLowering.cs:28` — `cast` to `clr:System.Collections.ICollection`.
- `TryValueOperandHoist.cs:189/190` — `var` (spill temp) + `local` (reference to it).

### 2d. Producer / consumer spelling cross-check vs ilemit
Verified every produced CLR `k` against `toolchain/ilemit/` consumption:

| produced `k` | ilemit consumes? | verdict |
|--------------|------------------|---------|
| clrInstance, clrStatic, clrPropGet, clrPropSet, newClr, clrGenericStatic, clrGenericInstance, constrainedCall, newBoundDelegate, objMethod, valueBlock, staticField, staticFieldSet, clr.ldelem, lateinitGet | YES | spelling matches — no drift |
| **`smSelf`** | ilemit occurrences = **0** | **OK — internal-only**: SuspendColdLowering rewrites every `smSelf` → `this` (`:1246`,`:1284`) before emit; never reaches ilemit. |
| **`clrMapSize`** | ilemit occurrences = **0** | **OK — NOT a node kind**: it is a HELPER METHOD NAME (`method` field of a callStatic to `ClrMapDefaultsKt`), one of the `clrMap*` family (Get/Size/ContainsKey/…). Do not confuse with a `k`. |

No genuine producer/consumer `k`-spelling mismatch found. The two zero-hits are both explained.

---

## 3. TOKEN PARSERS / SPLITTERS — the core drift risk

The BIR type-token grammar (informal): prefixes `clrg:` / `clr:` / `gp:` / `@` (owner/leaf);
modifiers `array:` / `nullable:` / `byref:` (recurse into element); function types
`func:<ret>:<args,…>` and `sfunc:<ret>:<args,…>` (suspend); generic args `Owner[a,b,…]`;
CLR primitive shorthand (`int`/`long`/`bool`/`char`/`void`/`object`/`string`/`i8`/`i16`/…).

### 3a. Canonical parsers (should be the ONLY ones)
| Function | Site | Handles | Rule |
|----------|------|---------|------|
| `SkipTypeToken(value,i)` | `Program.cs:2066` | ALL: recurses `array:/nullable:/byref:` element, `func:/sfunc:` ret+comma-args, scans `clrg:/clr:/gp:`/leaf to top-level `:`,`,`,`]` with `[]` depth | the reference grammar walker |
| `FuncRetEnd(value)` | `Program.cs:2048` | `func:`/`sfunc:` BODY ret/args separator | **delegates to SkipTypeToken for a nested-func ret**, else `PrefixLength` + depth-0 `:` scan |
| `PrefixLength(value)` | `Program.cs:2096` | one leading prefix of `{clrg:,clr:,array:,nullable:,sfunc:,func:,gp:,byref:}` | returns prefix length |
| `LowerFuncString(t)` | `Program.cs:2011` | `func:<ret>:<args>` lowering | splits at `FuncRetEnd`, lowers ret via `LowerReturnSlot`, args via `SplitTopLevel` |
| `LowerTypeString(t)` | `Program.cs:1949` | the MAIN recursive type-token lowerer | dispatches on `sfunc:`(→object) / `gp:`(keep) / `clr:`(keep) / `func:`(→LowerFuncString) / `[` generic / `clrg:`(keep) / leaf-primitive rewrite |
| `ParamKey(t)` | `Program.cs:649` | signature-match canonicalizer | unwrap `byref:/array:/nullable:`, drop `clrg:/clr:/@`, `sfunc:`→`obj`, `func:`→`func`, strip `[..]`, `gp:`→`gp`, fold all primitive spellings to `i8/i16/i32/i64/f32/f64/bool/char/str/void/obj`, IntArray→`array:i32` etc. |
| `BareOwnerFqn(token)` | `Program.cs:555` | owner-token → bare Kotlin FQN | TrimStart `@`, strip `clrg:`/`clr:`, drop `[..]` |
| `StripGenericArity` | `Program.cs:1155` | drop backtick arity | |
| `NormalizeTypeName` | `Program.cs:1339` | classify already-CLR (`clr:/clrg:/array:/func:/sfunc:/gp:` → "already-clr") | |

### 3b. DUPLICATION — where drift enters (ranked worst-first)

1. **`SplitTopLevel` (top-level comma split respecting `[]`) — SEVEN independent copies.**
   `Program.cs:1541`, `:2104`, `:2617`, `:2985`, `:3244`, `:5271`; plus `MapVarianceRealign.cs:326`
   (`SplitTop`), `NestedCollectionCountLowering.cs:52` (`SplitTop`), `SuspendColdLowering.cs:2903`
   (`SplitTopLevelArgs`). **Nine copies of the same 12-line loop.** They agree today, but any grammar
   change (e.g. `<…>` angle nesting, or `{…}` in an embedded default-arg BIR) must edit all nine.
   FREEZE ACTION: extract one `BirTokens.SplitTopLevel` and delete the eight private copies.

2. **`func:`/`nullable:` return-boundary scanning duplicated across THREE hand-rolled scanners.**
   - Canonical: `FuncRetEnd`/`SkipTypeToken` (`:2048`/`:2066`).
   - `NullableFuncReturnErasure.RewriteToken` (`:4107`) reimplements "find nullable func-return end"
     with its OWN `func:nullable:` marker scan + inner-prefix skip loop `{clrg:,clr:,array:,gp:,byref:,func:}`
     + depth-0 `:` scan — instead of calling `FuncRetEnd`.
   - `NullableGenericReturnErasure.EraseNullableGpToken` (`:3732`) scans `nullable:gp:` and has its
     OWN special-case `if (idx>=5 && s[idx-5..]=="func:")` skip to avoid stepping on scanner #2.
   These three MUST stay mutually consistent by hand: #3 deliberately skips exactly the positions #2
   claims. A new prefix or a nested `func:` shape can silently desync them. **Highest-severity drift knot.**

3. **`BareOwner`/`BareOwnerFqn` — THREE copies with slightly different rules.**
   `ReferenceMetadataIndex.BareOwnerFqn` (`Program.cs:555`, strips `@`+`clrg:/clr:`+`[..]`),
   `CrossClassPrivateWidening.cs:35` (`BareOwner`), `SuspendColdLowering.cs:639` (`BareOwner`). Each
   strips a generic-instantiation suffix + a leading `@`, but they were written independently — verify
   they treat `clr:`/`clrg:` identically (the SuspendCold/CrossClass copies key on OWNER tokens that are
   still kotlin.* pre-lowering, so they may NOT strip `clr:` — a latent divergence to lock down).

4. **`gp:` representation is spelled inconsistently across the grammar.**
   The open-type-param form is `gp:T` almost everywhere (constructed self `Owner[gp:T]`,
   `MapVarianceRealign`, `GenericSelfInstantiation`, `SuspendColdLowering`). But the ref.dll-reflected
   `TypeName` (`Program.cs:1068`) emits `gp:` + `type.Name`, and the erasure-marker forms `!!T`/`!!0`/`!0`
   (a helper's OWN not-null type-param) appear only in COMMENTS (`ContinuationErasure.cs:29`,
   `Program.cs:148`,`:4617`) — they are NOT parsed in code. So `gp:` is the single canonical spelling;
   the `!!`/`!0` forms are documentation of ilemit-side helper erasure, not a bir2cir token. **Confirm at
   freeze that no code path emits `!!T`/`!0`** (currently none does) so the grammar can forbid them.

5. **`sfunc:<ret>:<args>` vs the `sfunc:`→erasure split — TWO erasure targets by position.**
   `sfunc:` (suspend fn type) is erased to different things depending on WHERE:
   - `LowerTypeString:1958` and `ParamKey:660`: `sfunc:` → **`object`/`obj`** (param/field/return/receiver
     slot — the SM VALUE is an object).
   - `LowerFuncTypeValued` / `FoldSuspendToFunc` (`:1905`): `sfunc:` → **`func:`** (the `funcType` key of a
     `newClosure`/`newDelegate` — a genuine delegate view, e.g. `iterator{}` SequenceScope path).
   Two rules keyed on the JSON KEY, not the token — a token audit that only greps `sfunc:` will miss that
   the same token has two lowerings. `H2` metadata (`:1833`/`:1835`) additionally records the RAW
   pre-erasure `sfunc:` alongside so ilemit can stamp the suspend flag. **ilemit must NEVER receive a raw
   `sfunc:`** (invariant, `:1956`).

6. **`array:nullable:` prefix-stacking parsed ad hoc in two places.**
   `ArrayNullableElemRealign.cs:42/46` (`StartsWith("array:nullable:")` + guard against re-stacking) and
   `ValueTypeNullableCollectionArg.cs:55/56` (`Contains("[nullable:gp:")` + exclude `array:`). Both hand-
   parse the same stacked-modifier grammar with string `Contains`/`StartsWith` rather than the walker.

7. **`InvariantGenericArgs` (MapVarianceRealign.cs:294) + `MapEntryArgs` (Program.cs:2292..) + `NestedCollectionCountLowering.HasNestedCollectionArg` (:38)** each re-implement "find `[`, check `]`,
   `SplitTop` the inner" for owner-token generic args, with per-pass head guards (`InvariantCollections`,
   `clrg:System.Collections.`, Map$Entry). Same shape, three copies.

### 3c. Non-comma splitters / normalizers (single-use, lower risk)
- `WalkNullable` / `SplitTopLevelArgs` (`SuspendColdLowering.cs:2886/2903`) — NRT pre-order byte walk.
- `SplitSig` (`Program.cs:5020`) — sig-string split (uses a SplitTopLevel copy).
- `NormalizeLists` (`TryValueOperandHoist.cs:54`), `StripArity` (`MapVarianceRealign.cs:317`).

---

## 4. SUBSTITUTION vocabulary (Rules 1–5 / MemberCallSubstitution / MapVarianceRealign)

Entry: `MemberCallSubstitution.Transform` → `TransformCall` (`Program.cs:~4560+`). Sourced ENTIRELY
from ref.dll `@ClrTypeAlias` (owner identity) + `@ClrIntrinsic` (member name); ilemit receives only
`System.X.Member`, never a kotlin.* label. Rule order (first match wins):

| Rule | Site | Owner condition | Emits |
|------|------|-----------------|-------|
| 1 (ctor) | TransformNew `:4400` | CLR-bound REFERENCE owner | `newClr System.X` |
| 1c (prim compareTo) | `:4635` | boxed kotlin.<Prim> | `clrInstance System.<Prim>.CompareTo` |
| 2 (intrinsic) | `:4644` `TryMemberIntrinsic` | member `@ClrIntrinsic("Name")` | `Constrainify(ClrCallNode …)` → clrInstance/clrStatic/clrPropGet/clrPropSet |
| 2p (@ClrProperty) | ClrPropNode | explicit accessor binding (READ=1/WRITE=2) | clrPropGet / clrPropSet on bare name |
| 3 (rule-3 body) | `:4654` `IsRule3Member` | concrete member, no intrinsic, non-interface | `callStatic <>dotkt_ClrH_<owner>.<member>` (recv threaded as arg 0) |
| 3-inherited | `:4663` | body on CLR-bound ancestor via `overrides` chain | `callStatic <ancestor helper>` |
| 5m (map defaults) | `:4679` | `kotlin.collections.Map`/`MutableMap` interface | `callStatic ClrMapDefaultsKt.clrMap{Get,Size,ContainsKey,…}` |
| 5 (coll defaults) | `:4722` | `kotlin.collections.*` interface | `callStatic ClrCollectionDefaultsKt.*` / `ClrIteratorBridgeKt.iteratorOverEnumerable` |
| 4 (already-BCL) | `:4736+` | kotc already emitted the BCL member name | `clrInstance`/`clrStatic` verbatim |

**Owner tokens bir2cir GENERATES for CLR-bound owners:** `clrg:System.IComparable[recvType]` (constrained
iface), `clrg:` + BCL FQN + `[args]` for generics, `clr:` + BCL FQN for non-generic, `System.X`
(unprefixed) as the `type` of clr* nodes.

**`gp:` handling in substitution:**
- `Constrainify` (`:4778`) fires only when receiver static type `StartsWith("gp:")` and the owner is a
  CLR-bound INTERFACE; recovers the constraint arg (`MutableCollection[gp:R]` → `gp:R`) → builds
  `iface = clrg:Owner[cargs]`.
- Rule 5 `OwnerElemArg`/`CollElemArg` extract the owner token's first type arg (`Owner[E]` → `E`) for the
  generic helper.
- `RetToken` skips threading a `gp:`-typed return (`:4624`) — an unbound `gp:` is useless to ilemit's keys.
- `MapVarianceRealign.cs` maps a `sig`'s `gp:NAME` → typeArg index (keyed name|arity) and realigns
  over-approximated `kotlin.Any` positions back to the constraint's concrete `gp:K` arg.

**Primitive substitution** (`BirTypeLowering`, mode-gated by `refBuild`): reference build keeps
`kotlin.Int` verbatim; every other build lowers `kotlin.Int`→`int`, `kotlin.UByte`→`ubyte`, etc.
(`Program.cs:1690+` map). `kotlin.Unit` is position-dependent: `void` in return slot, `@kotlin.Unit`
in type-arg (`:1764`).

---

## 5. Embedded-BIR-json re-parse ("splice") sites

The task's premise names `Program.cs:450 KotlinInlineAttr` as the "hidden 5th read site" that re-parses
an inline body. **FINDING (stale premise):** `KotlinInlineAttr` (`Program.cs:450`) is DEFINED but
**never referenced** anywhere in bir2cir — it is dead. The `[KotlinInline]` body-splice mechanism it
describes is gone; the LIVE embedded-BIR-json re-parse ("5th read") is now the `@KotlinDefault`
cross-module default-argument splice:

- `Program.cs:2581` — `JsonNode.Parse(bir)` on a `@KotlinDefault(index, bir)` attribute string read from
  ref.dll (`KotlinDefaultsOf` `:997` reads the `kotlin.clr.KotlinDefault` attribute's BIR-json payload),
  then binds the callee's default-expression tokens (`{this}`, `{param N}`) to THIS call's args
  (`DefaultArgSplice`, `:2516+`). This IS a second BIR parser that re-enters the type grammar and must
  agree with the main one.
- `Program.cs:3100` — `JsonNode.Parse(AdapterTypeJson)`: a literal BIR-json string constant for the
  injected CharSequence adapter type (pre-lowering kotlin.* vocabulary).
- `Program.cs:67` — the top-level input parse.

FREEZE ACTION: delete the dead `KotlinInlineAttr` const; document `@KotlinDefault` as the sole embedded
BIR-json (its payload rides the same token grammar, so it is covered by the freeze).

---

## RANKED drift summary (worst first)

1. **`SplitTopLevel` × 9 copies** (Program 1541/2104/2617/2985/3244/5271 + MapVarianceRealign/
   NestedCollectionCount/SuspendCold). Same loop, no shared owner. #1 mechanical drift risk.
2. **`func:`/`nullable:` return-boundary logic in 3 mutually-coupled hand scanners**
   (`FuncRetEnd`/`SkipTypeToken` canonical vs `NullableFuncReturnErasure.RewriteToken:4107` vs
   `NullableGenericReturnErasure.EraseNullableGpToken:3732`, the latter has a hard-coded `func:`-skip
   that MUST match the former). Highest-severity semantic knot.
3. **`sfunc:` erases to TWO different targets by JSON-key position** (`object`/`obj` for a type slot vs
   `func:` for a `funcType` delegate slot). Easy to miss; a token-only audit sees one spelling, two rules.
   Invariant: raw `sfunc:` must never reach ilemit (only H2 metadata carries it forward).
4. **`BareOwner` × 3 copies** (Program 555 / CrossClassPrivateWidening 35 / SuspendCold 639) with
   possibly-divergent `clr:`/`clrg:` stripping depending on pre/post-lowering owner tokens.
5. **owner-`[args]` generic-arg extraction re-implemented 3×** (InvariantGenericArgs / MapEntryArgs /
   HasNestedCollectionArg) each with its own head-guard.
6. **`array:nullable:` / `nullable:gp:` modifier-stacking hand-parsed** in ArrayNullableElemRealign +
   ValueTypeNullableCollectionArg via `StartsWith`/`Contains`, not the walker.
7. **Dead `KotlinInlineAttr` const (Program.cs:450)** — the task's assumed inline-splice site is gone;
   the real embedded-BIR re-parse is `@KotlinDefault` (`:2581`).

Non-drift (verified clean): NO producer/consumer `k`-spelling mismatch — `smSelf` (internal, rewritten
to `this`) and `clrMapSize` (a method-name, not a `k`) fully explain the two ilemit-zero-hit kinds.
`gp:` is the single canonical open-type-param spelling; `!!T`/`!!0`/`!0` live only in comments.

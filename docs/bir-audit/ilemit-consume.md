# ilemit — CIR consumption catalog (for the BIR/CIR freeze, #37)

READ-ONLY audit of what **ilemit** (the final CIR-JSON → CIL consumer) reads. This is the
**authoritative consumer list**: bir2cir MUST produce exactly these `k` spellings, type-token
prefixes, member-property names, and attribute ctor shapes. No code was changed.

Scope audited (exhaustive): `Program.cs`, `Emitter.Expressions.cs`, `Emitter.Statements.cs`,
`Emitter.Metadata.cs`, `Emitter.CompilerServices.cs`, `Emitter.ReverseBridge.cs`, `TypeInfo.cs`.

Line numbers are as of this audit (2026-07-06).

---

## 1. NODE KINDS CONSUMED (the `k` switch)

There are **four `k`-dispatch switches** that consume node kinds, plus three auxiliary `k`-reads:

- `EmitExpr` — expression evaluator (`Emitter.Expressions.cs:65`), default throws `expr <k>` (`:852`).
- `EmitStmt` — statement emitter (`Emitter.Statements.cs:14`), default throws `stmt <k>` (`:304`).
- `EmitAddr` — lvalue-address emitter (`Program.cs:1849`); recognizes only `local`/`this`/`field`,
  else materializes to a temp and takes its address.
- `EmitHandlerAsDelegate` — event/delegate-arg handler (`Program.cs:3455`); `newDelegate`/`newClosure`
  else pass-through.
- `StmtAlwaysReturns` (`Program.cs:1103`) and `StmtsHaveReturn`/`k=="return"` (`Program.cs:1082`) —
  control-flow analysis, read `return`/`throw`/`if`/`try`.
- `byrefOf` inner dispatch (`Emitter.Expressions.cs:781`) reads inner `k` = `clrInstance`/`clrStatic`.

### 1a. EmitExpr node kinds (expression position)

| `k` | handler (file:line) | IL emitted / effect |
|-----|--------------------|---------------------|
| `const` | Expr:69 → EmitConst (Program:2314) | `ldstr`/`ldc.i4`/`ldc.i8`/`ldc.r4`/`ldc.r8`/`ldnull` per const `type` |
| `clr.const` | Expr:70 → EmitConst | **SYNONYM of `const`** (producer-zero — dead) |
| `this` | Expr:71 | `ldarg.0` |
| `local` | Expr:73 | `ldloc`/`ldarg`; honors `_inlineSubst` splice bindings |
| `field` | Expr:82 | external prop → `callvirt get_X`; else `ldfld` (+`volatile.`) |
| `setFieldExpr` | Expr:103 | external prop → `callvirt set_X`; else `stfld` |
| `lateinitGet` | Expr:121 | `ldfld`; `dup`/`brtrue`, throw `InvalidOperationException` if null |
| `new` | Expr:137 | `newobj` (emitted ctor via `SelectCtor`, or reflected external ctor by `argTypes`) |
| `callInstance` | Expr:160 | `callvirt`/`call` to an EMITTED method (`ResolveMethod`); `dyn:true`→`EmitDynamicCall`; `dynRet`+clr-iface → dynamic fallback |
| `constrainedCall` | Expr:179 | `constrained. recvType; callvirt iface::method` (N-arg via `args`) |
| `clr.constrained.compareTo` | Expr:180 | **SYNONYM of `constrainedCall`** (producer-zero — dead); single-`arg` compareTo shape |
| `callStatic` | Expr:224 | `call` to emitted static/file-class fn (`FindStatic`/`FindMethod`) |
| `staticField` | Expr:239 | `ldsfld` (`FindField`, in-assembly or reflected external) |
| `clrStaticField` | Expr:249 | `ldsfld` on a **reflected .NET** static field (`ResolveType`+`GetField`). NOT a synonym of `staticField` |
| `staticFieldSet` | Expr:256 | `stsfld` |
| `bin` | Expr:268 → EmitBin (Program:2400) | arithmetic/compare/bitwise opcodes per op string |
| `clr.bin` | Expr:269 → EmitBin | **SYNONYM of `bin`** (dead) |
| `objEq` | Expr:270 → EmitObjEq (Program:2748) | null-safe structural `==` (`box`+`callvirt Equals`) |
| `clr.obj.eq` | Expr:271 → EmitObjEq | **SYNONYM of `objEq`** (dead) |
| `un` | Expr:272 → EmitUn (Program:2481) | `neg`/`not`/`!`(`ldc.i4.0;ceq`) |
| `clr.un` | Expr:273 → EmitUn | **SYNONYM of `un`** (dead) |
| `conv` | Expr:274 → EmitConv (Program:2495) | `conv.i4/i8/r8/r4/i2/i1/u2` per target |
| `clr.conv` | Expr:275 → EmitConv | **SYNONYM of `conv`** (dead) |
| `clr.safeCast.value` | Expr:276 → EmitNativeClrSafeCastValue | `x as? T` for value → `Nullable<T>` |
| `clr.nullable.null` | Expr:277 | `default(Nullable<T>)` |
| `clr.nullable.wrap` | Expr:278 | `newobj Nullable<T>(v)` |
| `clr.nullable.hasValue` | Expr:279 | `call Nullable<T>::get_HasValue` |
| `clr.nullable.value` | Expr:280 | `call Nullable<T>::get_Value` |
| `clr.typeof` | Expr:281 | `ldtoken`+`GetTypeFromHandle` |
| `clr.getType` | Expr:282 | `callvirt object::GetType` |
| `clr.enum.value`/`.ordinal`/`.values`/`.parse` | Expr:283-286 | enum helpers |
| `valueBlock` | Expr:287 | splice `stmts` then yield `result` |
| `newList` | Expr:293 | `new List<elem>` + repeated `Add` |
| `clrGenericStatic` | Expr:308 | `MakeGenericMethod`+`call` (LINQ overload by `shapes`) |
| `clrGenericInstance` | Expr:322 | `MakeGenericMethod`+`callvirt`/`call` |
| `newArray` | Expr:338 → EmitNewArray | `newarr` (+ init loop) |
| `clr.newarr` | Expr:339 → EmitNewArray | **SYNONYM of `newArray`** (dead) |
| `newArraySized` | Expr:340 | `newarr elem` (zero-fill) |
| `newArrayInit` | Expr:346 | `newarr` + `Func<int,elem>` fill loop |
| `default` | Expr:378 | `ldnull` / `initobj` zero value |
| `clr.default` | Expr:379 | **SYNONYM of `default`** (dead) |
| `spreadConcat` | Expr:389 | `List<elem>` Add/AddRange → `ToArray` |
| `clr.array.spread` | Expr:390 | **SYNONYM of `spreadConcat`** (dead) |
| `arrayGet` | Expr:411 | `EmitLdelem` |
| `clr.ldelem` | Expr:412 | **SYNONYM of `arrayGet`** (producer-zero; only appears in a bir2cir CONSUMER-side impure-kinds set, `SuspendColdLowering.cs:1457/1516`, never emitted) |
| `arraySet` | Expr:418 | coerce + `EmitStelem` |
| `clr.stelem` | Expr:419 | **SYNONYM of `arraySet`** (dead) |
| `arrayLen` | Expr:431 | `ldlen; conv.i4` |
| `clr.ldlen` | Expr:432 | **SYNONYM of `arrayLen`** (dead) |
| `forEachInline` | Expr:434 | inline enumerate+splice (also reachable in stmt position) |
| `isinst` | Expr:483 | `box?`+`isinst`+`ldnull;cgt.un` → bool |
| `cast` | Expr:496 | `box?`+`castclass`/`unbox.any` (universal cast) |
| `classRef` | Expr:511 → EmitNativeClrTypeOf | `typeof` |
| `getType` | Expr:515 → EmitNativeClrGetType | `GetType` |
| `isinstRef` | Expr:519 | `isinst T` (leaves ref/null) |
| `safeCastValue` | Expr:530 | value `as?` (twin of `clr.safeCast.value`) |
| `nullableNull`/`nullableWrap`/`nullableHasValue`/`nullableValue` | Expr:534-549 | non-`clr.` twins of the `clr.nullable.*` set |
| `repeatInline` | Expr:550 | counter loop |
| `enumValue`/`enumOrdinal`/`enumValues`/`enumParse` | Expr:567-580 | non-`clr.` twins of `clr.enum.*` |
| `objMethod` | Expr:581 → EmitObjMethod (Program:2730) | `GetHashCode`/`ToString`/`Equals` on `object` |
| `clr.obj.method` | Expr:582 → EmitObjMethod | **SYNONYM of `objMethod`** (dead) |
| `strReversed` | Expr:583 | `Enumerable.Reverse`+`ToArray`+`new string(char[])` |
| `newMap` | Expr:592 | `new Dictionary<K,V>`+`set_Item` |
| `newSet` | Expr:609 | `new HashSet<elem>`+`Add`/`pop` |
| `throwExpr` | Expr:625 | eval + `throw` |
| `returnExpr` | Expr:632 | expression-position return (mirrors `return` stmt, try-leave) |
| `newDelegate` | Expr:654 | `ldnull;ldftn;newobj Delegate` (non-capturing lambda) |
| `newBoundDelegate` | Expr:670 | `obj::method` → `dup;ldvirtftn`/`ldftn`+`newobj` |
| `newBoundClrDelegate` | Expr:682 | bound delegate over a reflected .NET method |
| `delegateInvoke` | Expr:697 | inline lambda-param splice, else `callvirt Invoke` |
| `inlineSplice` | Expr:735 → EmitInlineSplice | cross-module `[KotlinInline]` body splice |
| `newClosure` | Expr:736 | `newobj Closure(captures)`+`ldftn invoke`+`newobj Delegate` |
| `newSam` | Expr:749 | `newobj <Sam>(captures)` (fun-interface impl class) |
| `concat` | Expr:766 → EmitConcat | `String.Concat` |
| `clr.str.concat` | Expr:767 → EmitConcat | **SYNONYM of `concat`** (dead) |
| `cond` | Expr:768 → EmitCond | ternary/if-expr merge |
| `newClr` | Expr:769 → EmitClrNew | `newobj` on a **reflected .NET** ctor (by `argTypes`) |
| `clrStatic` | Expr:770 → EmitClrCall(instance:false) | `call` reflected .NET static |
| `clrInstance` | Expr:771 → EmitClrCall(instance:true) | `callvirt`/`call`/`constrained.` reflected .NET instance |
| `clrPropGet` | Expr:772 → EmitClrPropGet (Program:3300) | .NET property/field getter (or `get_X` method) |
| `clrPropSet` | Expr:773 → EmitClrPropSet (Program:3371) | .NET property/field setter |
| `clrEventAdd`/`clrEventRemove` | Expr:774-775 → EmitClrEvent | `+=`/`-=` event accessor |
| `byrefOf` | Expr:776 | managed pointer for `var x by byref(...)` |
| `stackAlloc` | Expr:787 | `localloc`+`initblk` |
| `clr.stackalloc` | Expr:788 | **SYNONYM of `stackAlloc`** (dead) |
| `stackGet` | Expr:803 | `ldobj` off stack ptr |
| `clr.stack.get` | Expr:804 | **SYNONYM of `stackGet`** (dead) |
| `stackSet` | Expr:812 | `stobj` |
| `clr.stack.set` | Expr:813 | **SYNONYM of `stackSet`** (dead) |
| `stackAsSpan` | Expr:822 | `newobj Span<T>(void*,int)` |
| `clr.stack.asSpan` | Expr:823 | **SYNONYM of `stackAsSpan`** (dead) |
| `byrefLoad` | Expr:834 | `ldloc ptr; ldobj` |
| `byrefStore` | Expr:842 | `ldloc ptr; stobj` |
| `unsupportedExpr` | Expr:851 | throws `NotSupportedException(of)` |

Retired (no case, no producer): **`console`** (println/print) — removed 2026-07-02, comment at Expr:265-267.

### 1b. EmitStmt node kinds (statement position)

| `k` | line | effect |
|-----|------|--------|
| `var` | Stmt:16 | `DeclareLocal` + coerced init store |
| `setLocal` | Stmt:31 | `stloc`/`starg` (coerced) |
| `setField` | Stmt:39 | external setter `callvirt`, else `stfld` |
| `return` | Stmt:59 | try-region `leave`, else `ret` (with return coercion) |
| `throw` | Stmt:81 | `throw` |
| `try` | Stmt:85 | `BeginExceptionBlock`/catch/`finally`; reads `body`/`catches`/`excType`/`var`/`finally` |
| `exprStmt` | Stmt:156 | eval + `pop` non-void |
| `while` | Stmt:162 | pre-test loop |
| `if` | Stmt:173 | branch chain (`branches`/`cond`/`else`/`body`) |
| `for` | Stmt:191 | counter loop (`var`/`from`/`to`/`cmp`/`step`) |
| `dowhile` | Stmt:217 | post-test loop |
| `forArray` | Stmt:229 | index loop over `array`/`elem`/`var` |
| `forRange` | Stmt:253 | range counter loop (`range`/`accessOwner`/`firstM`/`lastM`/`stepM`/`var`) — accessor names come from the node, no hardcoded kotlin.ranges |
| `block` | Stmt:286 | splice `body` |
| `forEachInline` / `repeatInline` | Stmt:290-291 | delegate to EmitExpr |
| `break` / `continue` | Stmt:294-295 | `br` to loop labels (optional `label`) |
| `label` | Stmt:297 | `MarkLabel(_cfgLabels[id])` (CFG block-IR, E-0.5) |
| `goto` | Stmt:298 | `br _cfgLabels[id]` |
| `brIf` | Stmt:299 | `brtrue`/`brfalse` per `on` to `_cfgLabels[id]` |
| `unsupportedStmt` | Stmt:303 | throws `NotSupportedException(of)` |

### 1c. Synonym / dead-node summary

- **Live authoritative `k` count**: ~78 expression kinds + 22 statement kinds actually produced.
- **`clr.*` twin family = DEAD (producer-zero).** Every `clr.<name>` alias in EmitExpr
  (`clr.const`, `clr.bin`, `clr.obj.eq`, `clr.un`, `clr.conv`, `clr.newarr`, `clr.ldelem`,
  `clr.stelem`, `clr.ldlen`, `clr.str.concat`, `clr.obj.method`, `clr.default`, `clr.array.spread`,
  `clr.stackalloc`, `clr.stack.get/set/asSpan`, `clr.constrained.compareTo`, `clr.nullable.*`,
  `clr.enum.*`, `clr.safeCast.value`, `clr.typeof`, `clr.getType`) has **0 producers** in kotc or
  bir2cir. The single grep hit for `clr.ldelem` in bir2cir is a **consumer-side** impure-kinds set
  (`SuspendColdLowering.cs:1457/1516`), not an emitter. The **live spelling is the non-`clr.` twin**
  in every case (`const`, `bin`, `arrayGet`, `objMethod`, `nullableWrap`, `enumValue`, `safeCastValue`,
  `classRef`/`getType`, …). **Freeze recommendation:** drop the `clr.*` alias cases from ilemit; they
  are pure dead weight and are the exact kind of "two spellings for one node" the M1 sweep targets.
- **NOT synonyms (distinct handlers, both live):**
  - `staticField` (in-assembly/rt static, `FindField`) vs `clrStaticField` (reflected .NET static field).
  - `callInstance` (EMITTED method call) vs `clrInstance` (reflected .NET call). Different resolution paths.
  - `field`/`setField`/`setFieldExpr` (emitted field, with external-prop fallback) vs
    `clrPropGet`/`clrPropSet` (reflected .NET property). No `clrLdfld` node exists.
  - `constrainedCall` two shapes (N-arg `args` vs single `arg`) share one case but are one live kind.

---

## 2. TYPE-TOKEN RESOLUTION

Two entry points: `MapType(string)` (`Program.cs:3808`) — the primary resolver — and `ClrRef(string)`
(`Program.cs:3726`) — a generic-aware wrapper used by the reflected-`clr*` emitters. `NativeType`
(`:3214`) and `NativeParameterTypes` (`:3203`) are `clr*`-node param resolvers that funnel into
`MapType`/`ClrRef`. `ResolveType(string)` (`:2785`) is pure BCL-FQN reflection.

### 2a. `MapType` prefix table (`Program.cs:3808`)

| prefix / form | line | resolves to |
|---------------|------|-------------|
| `byref:<T>` | 3810 | `MapType(T).MakeByRefType()` (`T&`) |
| `dotkt$stackptr` (literal) | 3811 | `byte*` |
| `clr:<FQN>` | 3812 | `ResolveType(FQN)` — a referenced .NET type |
| `array:<T>` | 3813 | `MapType(T).MakeArrayType()` |
| `func:<ret>:<args>` | 3814 | `FuncType` → `System.Func<…>`/`Action<…>` (or synthetic delegate if arity > 16) |
| `sfunc:<ret>:<args>` | — | **NOT resolved by MapType**; a suspend-fn-type token is erased to `object` by bir2cir; ilemit only sees its shape via the `suspendFnType`/`retSuspendFnType` metadata strings (§3). `FuncRetEnd`/`SkipTypeToken` parse `sfunc:` structurally |
| `clrg:<Open>[args]` | 3815 | `GenericType` → `Open`N`.MakeGenericType(args)` (arity fallback to non-generic if `Open`N` missing) |
| `nullable:<T>` | 3816 | `Nullable<MapType(T)>` |
| `gp:<Name>` | 3818 | generic param: `_curMethodParams[Name]` shadows `_curTypeParams[Name]`; throws `unresolved generic type parameter` if neither |
| `@<Name>` / `@<Name>[args]` | 3825 | emitted type (`_types[Name].AsType`), else referenced `.NET` (`ResolveType`, arity-suffixed for generic) |
| `void`/`int`/`long`/`double`/`float`/`bool`/`char`/`string`/`uint`/`ulong`/`ubyte`/`ushort`/`short`/`byte`/`object` | 3839 | primitive shorthand → the CLR primitive. **Kotlin Byte = `sbyte` (signed); UByte = `byte`** (:3846) |
| bare FQN (no prefix), e.g. `kotlin.Int`, `Foo`, `Name[args]` | 3854 | `TryMapEmittedType` (this-assembly `_types`) FIRST, else `GenericType` (if `[`), else `ResolveType` (if `.`), else fallback `object` |

### 2b. `ClrRef` prefix table (`Program.cs:3726`) — used by reflected `clr*` emitters

| form | resolves via |
|------|--------------|
| `byref:<T>` | `ClrRef(T).MakeByRefType()` |
| `clrg:<Open>[args]` | `GenericType` |
| `func:`/`clr:`/`array:`/`nullable:`/`gp:`/`@` | delegates to `MapType` |
| bare primitive shorthand (`PrimShorthand` set, :3738) | `MapType` |
| else | `ResolveType` (BCL FQN) |

### 2c. `gp:` handling — INCONSISTENCIES to flag

ilemit uses **several different spellings** for a generic-type-parameter reference depending on the
consumer, which the freeze should unify:

1. **`gp:<Name>`** (name-keyed) — the CIR/BIR wire form, resolved by `MapType` (:3818) against
   `_curMethodParams`/`_curTypeParams` name maps. This is the **only spelling in the JSON type tokens.**
2. **`!!T` / `!0` (IL display forms)** — these appear **only in comments** (e.g. `castclass !!T`,
   `constrained. !!C`, `IComparable`1<!0>`) describing the emitted IL. **ilemit never PARSES `!!`/`!0`
   from CIR** — they are Reflection.Emit's rendering of a resolved `GenericTypeParameterBuilder`, not
   an input token. So there is no `!!T` vs `!0` token divergence in the consumed vocabulary; the only
   consumed spelling is `gp:<Name>`.
3. **`gp:#0` canonicalization** — `CanonSig` (`Program.cs:1929-1942`) rewrites `gp:<Name>` to
   positional `gp:#0`/`gp:#1` for signature-key matching (a def names its own param, a call names the
   caller's). This is an internal normalization of the SAME `gp:` token, but it means **ilemit's
   overload-resolution key depends on `gp:` NAME→position remapping** — a freeze must guarantee
   bir2cir emits `sig` tokens whose `gp:` names are consistent within a signature (the def/call name
   mismatch is what `CanonSig` and `FindReflectedMethodBySigLoose` at :1946 paper over).
4. **Structural `gp:` matching** in `SigTokenMatches`/`ArgMatchesTok` (:2077-2192): a token
   *containing* `gp:` is compared **by shape** (open), while a fully-concrete token is compared by
   exact type. This split is load-bearing for generic-method overload disambiguation.

**Flag:** the resolution is internally consistent (single wire spelling `gp:<Name>`; `!!`/`!0` are
IL-render-only), but the **`gp:` NAME dependence** across `CanonSig` / loose-sig fallback is fragile
and is exactly the kind of thing a frozen `sig` grammar should pin down (positional `gp:#n` on the
wire would eliminate the def-vs-call name-mismatch dance).

### 2d. Other resolution notes

- `ParseOwner` (`:1346`) splits `Name[args]` → (open, constructed): emitted → `TB.MakeGenericType`;
  external → `ResolveType(open`N).MakeGenericType`.
- `GenericType` (`:3745`) arity-suffixes the open name and falls back to the non-generic BCL type if
  `Open`N` is absent (a Kotlin generic aliased to a non-generic BCL type, e.g. `Comparator<T>` →
  `System.Collections.IComparer`).
- `MapArg` (`:3743`) maps a generic **type argument**, coercing `void` → `object` (`Continuation<Unit>`).
- `ResolveType` (`:2785`) probes a fixed assembly list + `Outer+Inner` nested-type fallback.

---

## 3. ATTRIBUTE MODEL — embedded `[Kotlin*]` round-trip attrs

Defined + stamped into the emitted module by `EnsureKotlinAttrs` (`Emitter.CompilerServices.cs:46`)
via `DefineEmbeddedAttr`/`DefineEmbeddedAttrN` (`:20`/`:27`). All are `NotPublic Sealed : Attribute`
with metadata-only ctors (body chains to `Attribute()`; applied args live in the attribute blob).
Stamping is skipped entirely under `DOTKT_STRIP_METADATA` (`_stripMetadata`, the rt build).

| attribute (full name) | ctor signature(s) | defined at | stamped by (file:line) | carries BIR/token ABI? |
|-----------------------|-------------------|-----------|------------------------|------------------------|
| `DotKt.Runtime.CompilerServices.KotlinInlineAttribute` | **`(string)`** | CompSvc:52 | `ApplyKotlinInline` (Metadata:44); payload built at Program.cs:664 | **YES — the whole inline BIR body.** Payload = the JSON string `{"params":<params JSON>,"body":<body JSON>}`. Read back at Program.cs:3000-3006 (cross-module `inlineSplice`). **This is the `(version, byte[])` carrier the #37 freeze changes.** |
| `DotKt.Runtime.CompilerServices.KotlinFunctionAttribute` | `(int)` | CompSvc:50 | `ApplyKotlinFunction` (Metadata:51) | flag bits only: infix/operator=nmask, `suspend`→`4`, `suspendBridge`→`4` (Program:641-645) |
| `DotKt.Runtime.CompilerServices.KotlinFileClassAttribute` | `()` | CompSvc:51 | `ApplyKotlinFileClass` (Metadata:58) | no |
| `DotKt.Runtime.CompilerServices.KotlinReadOnlyAttribute` | `()` | CompSvc:53 | `ApplyKotlinReadOnly` (Metadata:37) | no |
| `DotKt.Runtime.CompilerServices.KotlinFunInterfaceAttribute` | `()` | CompSvc:56 | `ApplyKotlinFunInterface` (Metadata:66) | no |
| `DotKt.Runtime.CompilerServices.KotlinSealedAttribute` | `()` | CompSvc:57 | `ApplyKotlinSealed` (Metadata:74) | no |
| `DotKt.Runtime.CompilerServices.KotlinSuspendFunctionTypeAttribute` | **`(string)`** | CompSvc:64 | `ApplySuspendFnType` (CompSvc:113/119/125 — param/field/property) | **YES — carries the pre-erasure `sfunc:<ret>:<args>` SHAPE token** (bir2cir's `suspendFnType`/`retSuspendFnType` fact). A token-vocabulary ABI |
| `System.Runtime.CompilerServices.NullableAttribute` | **`(byte)` AND `(byte[])`** (two overloads) | CompSvc:68 | `ApplyNullable` (CompSvc:82/93/103) | flags only (0/1/2 per type node, pre-order). `byte[]` = nested walk (`Task<string?>`→{1,2}) |
| `System.Runtime.CompilerServices.NullableContextAttribute` | `(byte)` | CompSvc:70 | `ApplyNullableContext` (CompSvc:75) | per-type default = 1 (non-null) |

Also emitted (not a `[Kotlin*]` attr but part of the model):
- **`ParamArrayAttribute`** on `vararg` params (Metadata:194).
- **`[Optional]`+`DefaultParameterValue`** via `SetConstant` for constant defaults (Metadata:191/195).
- **`modreq(System.Runtime.CompilerServices.IsVolatile)`** custom modifier + `volatile.` prefix for
  `@Volatile` fields (`DefineVolatileField`/`MaybeVolatile`, Metadata:25/34).
- Synthetic-delegate types (`KFunc`N`/`KAction`N`, arity>16) stamp `CompilerGeneratedAttribute` +
  `KotlinFunctionAttribute(0)` (Program:3637-3640).
- `KotlinDefault` (`@kotlin.clr.KotlinDefault(index, bir)`) — an **applied** annotation on a defaulted
  stdlib param, NOT defined by ilemit; routed through the generic `BuildCab` path (Metadata:86,
  Program:960-962). Its `(int, string)` shape (index + carried default-expr BIR) is another
  BIR-token-carrying attribute, but defined in the stdlib, not synthesized here.

### 3a. Applied-annotation decoding (the `attr` sub-node, not a `k` node)

`BuildCab` (`Emitter.Metadata.cs:86`) decodes an applied annotation from an `attrs` array element:
reads `attr` (a `{t:fqn}` node → `SlotName`, #48), `args` (const nodes → `ConstArgValue`, :131), `argTypes`
(for `attrExternal`-flagged imported attrs, :95). `ConstArgValue` handles char-as-single-char-string coercion
and numeric-type widening (`long`/`double`/`float`/`short`/`byte`/`char`). This is the generic path for
`@KotlinDefault`, `@ClrRefArgument`, imported `attrExternal` .NET attributes (#54), etc.

### 3b. Relevance to the `(version, byte[])` carrier freeze (#37)

The two ctors the freeze touches are the **string body-carriers**:
- `KotlinInlineAttribute(string)` — CompSvc:52; produced at Program.cs:664 (`"{\"params\":…,\"body\":…}"`),
  consumed at Program.cs:3000-3006. Changing to `(int version, byte[] payload)` requires editing:
  (a) the `DefineEmbeddedAttr` type param list (CompSvc:52), (b) `ApplyKotlinInline`'s ctor lookup +
  arg (Metadata:44-48), (c) the payload build site (Program:664), (d) the read-back at Program:3000-3006
  (currently `.ConstructorArguments` string).
- `KotlinSuspendFunctionTypeAttribute(string)` — CompSvc:64; carries the `sfunc:` shape token. If the
  freeze versions token carriers too, this is the second one. `ApplySuspendFnType` has 3 overloads
  (param/field/property, CompSvc:113/119/125).

`KotlinDefault(int, string)` (stdlib-defined) also carries a BIR string but is stamped generically via
`BuildCab`, so it is out of ilemit's synthesis scope.

---

## 4. LABEL / STATE-MACHINE emission

**ilemit is coroutine-codegen-free.** It emits NO state-machine dispatch of its own. The confirmation
lives at `Program.cs:1003-1022` (the `suspend`-method guard) and `:825-830`:

- A method still carrying `"suspend":true` when it reaches ilemit is a **bir2cir transform MISS**:
  in a STDLIB build it emits a throwing stub (`EmitThrowStub`, expected — the coroutine primitives
  have no SM form); in an APP build it is a hard error (`:1018-1022`) telling bir2cir to lower it.
- The real cold-core lowering (public `Task<T>` bridge + `ContinuationImpl` SM class + cold entry) is
  produced **by bir2cir as PLAIN methods/types**. ilemit sees them as ordinary methods and emits them
  through the normal body path — there is no `$dotkt_suspend` / `$sm` special-casing in ilemit.

Therefore the "SM label dispatch" ilemit consumes is just the **generic CFG block-IR** (E-0.5,
`docs/design-il-cfg.md`):
- `label` (`Stmt:297`) → `MarkLabel(_cfgLabels[id])`,
- `goto` (`Stmt:298`) → `br _cfgLabels[id]`,
- `brIf` (`Stmt:299`, reads `cond`/`on`/`id`) → `brtrue`/`brfalse _cfgLabels[id]`.

`_cfgLabels` (`Program.cs:130`) is an `int id → Label` map pre-scanned over the whole body before
emission (forward references). **The naming ilemit expects from bir2cir**: integer `id`s on
`label`/`goto`/`brIf` nodes — NOT symbolic `$dotkt_suspend`/`$sm`/resume-label names. The
`$dotkt_suspend`/`$sm` strings appear ONLY in ilemit comments (`:645`, `:1445`) describing the method
NAMES bir2cir chooses; ilemit treats them as opaque method names with no structural meaning.

The metadata ilemit DOES consume for suspend: `suspend`(bool), `suspendBridge`(bool) → both set
`KotlinFunction` flag `4` (Program:641/645); `suspendFnType`/`retSuspendFnType`(string shape) →
`[KotlinSuspendFunctionType]` (§3). No label/resume vocabulary.

---

## Drift summary (return highlights)

**(a) Authoritative consumed-`k` list.** ~78 expression kinds + 22 statement kinds are LIVE. The
`clr.*` twin family (22 aliases across EmitExpr: `clr.const`, `clr.bin`, `clr.obj.eq`, `clr.un`,
`clr.conv`, `clr.newarr`, `clr.ldelem`, `clr.stelem`, `clr.ldlen`, `clr.str.concat`, `clr.obj.method`,
`clr.default`, `clr.array.spread`, `clr.stackalloc`, `clr.stack.{get,set,asSpan}`,
`clr.constrained.compareTo`, plus the `clr.nullable.*`/`clr.enum.*`/`clr.safeCast.value`/`clr.typeof`/
`clr.getType` set) are **DEAD (producer-zero)** — the live spelling is always the non-`clr.` twin.
Freeze should DELETE these alias cases. `console` is already retired. Genuinely-distinct (not synonym)
pairs: `staticField`≠`clrStaticField`, `callInstance`≠`clrInstance`, `field`≠`clrPropGet`.

**(b) Token-resolution inconsistencies, esp. `gp:`.** The only wire spelling for a type param is
`gp:<Name>`; `!!T`/`!0` are IL-render-only (comments), never parsed from CIR — so no `!!` vs `!0`
input divergence. BUT the `gp:` **NAME** is load-bearing and fragile: `CanonSig` remaps `gp:<Name>` →
positional `gp:#n` and `FindReflectedMethodBySigLoose` exists purely to reconcile the def-vs-call
`gp:` NAME mismatch in `sig` tokens. Recommend the frozen `sig` grammar pin generic params
**positionally** (`gp:#n`) to kill that reconciliation. Prefix set to freeze: `clr:`, `clrg:`,
`array:`, `nullable:`, `byref:`, `func:`, `sfunc:` (metadata-only), `gp:`, `@`, and the 15 primitive
shorthands (note **Byte→`sbyte`**, **UByte→`byte`**).

**(c) Attribute ctor signatures for the (version, byte[]) carrier.** The current string body-carrier
is **`KotlinInlineAttribute(string)`** (defined `Emitter.CompilerServices.cs:52`; payload built
`Program.cs:664`; read `Program.cs:3000-3006`; stamped `Emitter.Metadata.cs:44`). The parallel
token-carrier is **`KotlinSuspendFunctionTypeAttribute(string)`** (CompSvc:64, carries the `sfunc:`
shape). `NullableAttribute` already has the dual `(byte)`/`(byte[])` overload precedent for exactly
this "scalar + array" shape (CompSvc:68) — the model to mirror. `KotlinDefault(int, string)` also
carries a BIR string but is stdlib-defined and stamped via the generic `BuildCab` path (out of
ilemit's synthesis scope).

# facadegen — the injection-META vocabulary (BIR/CIR freeze #37 audit)

READ-ONLY audit. Source of truth: `toolchain/facadegen/Program.cs` (emitter) and its consumer
`toolchain/kotc/src/main/kotlin/kotc/frontend/ClrTypeInjection.kt` (`coneOf` + the line-kind parser).
Cross-referenced against the BIR emit vocabulary in
`toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitter.kt` (+ `BirMappings.kt`) and
`toolchain/ilemit/Program.cs` (`SkipTypeToken`).

facadegen reflects a **.NET dll** and writes a compact, **line-based** metadata file (the "meta") that
kotc's FIR injector (`ClrTypeInjection.kt`) consumes to inject `System.*` (and any referenced .NET /
DotKt-round-tripped) symbols into FIR. This doc catalogs **every meta token facadegen emits**, the
**BIR↔meta translation** that exists only for the H2 suspend-fn-type round-trip, and the `[KotlinInline]`
body read.

The headline for the freeze: **the meta grammar and the BIR grammar are two DIFFERENT vocabularies.**
facadegen writes/consumes the *meta* grammar; kotc's BirEmitter and ilemit write/consume the *BIR*
grammar. The only place they collide inside facadegen is `SuspendFnMeta`, which is forced to translate a
raw BIR token (stored verbatim in a round-trip attribute) into meta — see §2 and §B.

---

## 0. Two orientations of the file

The meta is **line-based**, one declaration per line, space-separated tokens. Top-level line kinds
(the first token, parsed by `ClrTypeInjection.kt` around L238):

| line kind | facadegen emit site | meaning |
|-----------|---------------------|---------|
| `object <Simple> <DotNetFQN>` | `EmitOneType` L334 (enum), L455 (static class) | static-call-site object |
| `class <Simple> <DotNetFQN> <open\|sealed> [<TP>...]` | L455/456 | instance class + arity |
| `interface <Simple> <DotNetFQN[=clr]> [<TP>...]` | L353 | implementable interface |
| `annotation <Simple> <DotNetFQN> [<p>:<t>]*` | L443 | .NET attribute → Kotlin annotation |
| `file <package> <fileClassFQN>` | `EmitKotlinFileClass` L1313; `EmitTaskAwait` L275 | opens a top-level (`[KotlinFileClass]`) section |
| `super <t> <t>...` | L365, L474 | supertype edges |
| `sealed` / `funinterface` / `basector none` | L356/357/460/479 | class-nature round-trip markers |
| `tvariance <T> <out\|in>` / `tbound <T> <bound>` / `mbound <T> <bound>` | `EmitTypeParamMeta` L1606/1623 | gap-① variance + constraints |
| `iterator <elemType>` | L372, L569 | frontend-only `operator fun iterator()` |

Member lines (emitted *under* a type/`file` line):

| line kind | emit site | shape |
|-----------|-----------|-------|
| `ctor [<p>:<t>]*` | L493 | constructor |
| `prop <name> <type> <ro\|rw> <modifier> [clr:<n>]` | L410,504,516,527,619,624,722 | property (member/static/field-backed) |
| `fun <name> <ret> <modifier> [clr:<n>] [<TP>...] [<p>:<t>]*` | L401,671,695,730 | member/abstract/operator fun |
| `sfun <name> <ret> [<TP>...] [<p>:<t>]*` | L597 | companion (static-on-normal-class) fun |
| `sprop <name> <type> <ro\|rw>` | L575,578 | companion static property/field |
| `memextprop <name> <type> <ro\|rw> <recvType> <modifier>` | L543 | member extension property |
| `index <idxType> <valType> <ro\|rw>` | L430,560 | indexer |
| `event <name> <ret> [<p>:<t>]*` / `sevent ...` | L424,553,610,636 | .NET event → `ClrEvent<T>` |
| **`tlfun <name> <ret> <modifier> [<TP>...] [<p>:<t>]*`** | `EmitKotlinFileClass` L1370; `EmitTaskAwait` L277/279 | top-level function |
| **`tlextprop <name> <type> <ro\|rw> <recvType>`** | L1329 | top-level extension property |
| **`tlprop <name> <type> <ro\|rw>`** | L1388 | top-level property (backing field) |

The `<modifier>` token (fun/prop) is a **single whitespace-free** token so it can never be mistaken for
a trailing type-param token: `Modifier()` (L1016) produces `abstract|open|final` with optional `prot-`
prefix, and `FunModifier()` (L1299) folds the no-.NET-analog Kotlin flags as comma-suffixes
(`final,infix`, `open,suspend,ext`, `final,operator`, `final,ext,suspend`, `,inline`). kotc parses it in
`parseFunMods` (ClrTypeInjection L121).

---

## 1. META TOKENS EMITTED — exhaustive

### 1a. Modifier flags (the comma-suffixes on a `fun`/`tlfun`/`sfun` modifier token)

| flag | source | emit site | kotc use |
|------|--------|-----------|----------|
| `,inline` | `KotlinInlineBody(m) != null` (a `[KotlinInline]` body present) | L666, L1369 | marks the injected fn `inline` (non-local return through lambda accepted); the BODY stays in the assembly, read at splice time by the **consumer's ilemit** — facadegen does NOT inline it |
| `,ext` | first param `__self` **or** `[Extension]` (`IsExtensionMethod`, L320) | L666, L1352/1369; `EmitTaskAwait` L277/279 | restore the extension receiver (`parseFunMods` → first `__self`/marked param becomes `extensionReceiverType`) |
| `,suspend` | `[KotlinFunction]` flag 4 (`KotlinFun`, L1036) | via `FunModifier` L1303 | mark the fn `suspend` |
| `,infix` | `[KotlinFunction]` flag 1 | `FunModifier` L1301 | mark `infix` |
| `,operator` | `[KotlinFunction]` flag 2, or name `compareTo` (forced L1042), or a `.NET op_*` (L695) | `FunModifier` L1302 / literal | mark `operator` |

`,inline`/`,ext` are appended **outside** `FunModifier` (they aren't `[KotlinFunction]` flags):
`FunModifier(base,k) + (KotlinInlineBody!=null?",inline":"") + (isExt?",ext":"")`.

### 1b. The META TYPE grammar (what a `<type>`/`<ret>` token can be)

Produced by `Map()` (L1468) / `CrossType()` (L1552) / `ParamTok()` (L1407); consumed by
`ClrTypeInjection.coneOf` (L1106). **This is the meta vocabulary — contrast with §1c (BIR).**

| meta token | producer | coneOf handling |
|------------|----------|-----------------|
| `Int Long Short Byte Double Float Boolean Char String Unit` | `Map` switch L1511 | builtin scalar (L1171) |
| `UInt ULong UShort` | `Map` L1519 | (falls to bare-name resolve) |
| `Any?` | `Map` (`System.Object`, unresolvable) L1532/1533 | `nullableAnyType` |
| `<Simple>` / `<Namespace.FQN>` (bare) | `CrossType` L1584/1587 | bare-name / dotted ClassId resolve (L1186) |
| **`generic:<OpenSimple>[<arg>,<arg>]`** | `CrossType` L1578 (also `Map` span/nullable exclusions) | L1144 → open ClassId applied to recursively-resolved args |
| **`func:[<ret>,<arg>,<arg>]`** | `Map` delegate branch L1502 | L1127 → `coneFunctionType` |
| **`sfunc:[<ret>,<arg>,<arg>]`** | `SuspendFnMeta` L1184 (ONLY) | L1137 → `coneSuspendFunctionType` |
| `array:<elem>` | `CrossType` L1554; `ParamTok` vararg expands to it | L1162 → `Array<T>`/primitive `IntArray` |
| `span:<elem>` | `Map` L1479 | L1124 → `Span<T>` |
| `byref:<T>` | `Map` L1472 | L1122 → `ClrRef<T>` |
| `<T>?` (trailing `?`) | `RefSuffix`/`PropSuffix`/`RetSuffix` NRT byte 2 | L1108 → nullable |
| `<T>!` (trailing `!`) | `RefSuffix` NRT byte 0 (oblivious) | L1112 → `ConeFlexibleType` (platform `T!`) |
| `opt:<T>=<const>` | `ParamTok` L1423 (default arg) | L1120 strips prefix; default applied via `optDefault` |
| `<name>:vararg:<elem>` | `ParamTok` L1418 | `parseParams` → `isVararg` (L734) |
| bare type-var `T` | `Map` L1476 (`IsGenericParameter`) | L1156 resolves against method/owner type params |

Nullability is a **suffix** in meta (`T?`, `T!`), read from .NET NRT metadata (`NullableAttribute` /
`NullableContextAttribute`, L1090–1124). A value-type `X?` (`System.Nullable<X>`) is projected to the
Kotlin `X?` suffix form, never the literal `generic:Nullable[X]` (L1483).

### 1c. The BIR TYPE grammar — the OTHER vocabulary (for contrast; NOT emitted by facadegen)

Produced by `BirEmitter.birType` and consumed by `ilemit`. facadegen never emits these except where a
raw BIR token was stored in a round-trip attribute (§2). Evidence: `BirEmitter.kt` L2810/2817/3034,
`BirMappings.kt` L107.

| BIR token | shape | meta counterpart |
|-----------|-------|------------------|
| `func:<ret>:<arg>,<arg>` | **COLON-separated**, ret then `:` then comma-list | meta `func:[ret,args]` (**BRACKETED**) |
| `sfunc:<ret>:<arg>,<arg>` | colon form (BirEmitter L2817) | meta `sfunc:[ret,args]` (bracketed) |
| `func:<N>` | arity-only form for a delegate slot (L3034/3041) | — |
| `gp:<Name>` | type variable, **prefixed** | meta bare `<Name>` |
| `nullable:<X>` | nullability **prefix** | meta trailing `<X>?` |
| `byref:<X>` / `array:@Name` | same idea, but element uses BIR names | meta `byref:`/`array:` with meta names |
| `clr:<Name>` / `clrg:<Name>[<arg>,<arg>]` | referenced-.NET (generic) type | meta `generic:<Open>[args]` / bare FQN |
| `@Name` | this-assembly-emitted type | (n/a — facadegen only reads referenced dlls) |
| `int long short byte double float bool char void object string` | primitive **shorthand** (`VALUE_PRIM_BIR`, `BirMappings.kt` L107; `void`, `object`, `string` too) | meta Kotlin names `Int … Unit Any? String` |

**The divergences (BIR → meta), enumerated:**
1. Function types: `func:ret:args` (colon) → `func:[ret,args]` (bracket). Same for `sfunc:`.
2. Type variables: `gp:X` (prefix) → bare `X`.
3. Nullability: `nullable:X` (prefix) → `X?` (suffix).
4. Primitives: `int`/`kotlin.Int` → `Int`; `string` → `String`; `void` → `Unit`; `object` → `Any?`.
5. Generics: `clrg:Name[args]` / `clr:Name` → `generic:Open[args]` / bare FQN.

These five divergences are the entire reason §2's translation code exists.

---

## 2. BIR↔META TRANSLATION — `SuspendFnMeta` and its helpers (H2 only)

**Why it exists (single reason):** ilemit stamps `[KotlinSuspendFunctionType(shape)]` on any
`suspend (…) -> T` function-type *position* (param/return/field/prop), because bir2cir erases the CLR
signature slot to `object` (a suspend lambda VALUE is a Continuation state machine, not a `Func`). The
attribute carries the **RAW pre-erasure BIR token** `sfunc:<ret>:<args>` — i.e. **kotc's BIR emit
vocabulary, colon form**. But the injector's `coneOf` consumes the **meta vocabulary**
`sfunc:[ret,args]` (bracket form). So facadegen must translate BIR → meta on read-back. (Program.cs
comment L1150–1155 states this explicitly.)

| function | file:line | rule |
|----------|-----------|------|
| `SuspendFnMeta(attrs)` | Program.cs L1157 | read `[KotlinSuspendFunctionType]`; strip `sfunc:` (colon form); split ret from args via `BirSkipTypeToken`; translate ret + each arg via `BirTokenToMeta`; re-emit as **bracketed** `sfunc:[metaRet,metaArg,...]`. Any unconvertible child → `null` (whole shape degrades to the plain erased `object` slot; suspend lost, but safe). |
| `BirSkipTypeToken(s,i)` | Program.cs L1189 | advance past exactly ONE BIR type token; handles `array:`/`nullable:`/`byref:` prefixes, the `func:`/`sfunc:` colon-form recursion (ret, `:` sep, comma args), and `clrg:`/`clr:`/`gp:` prefixes + bracket-depth scan. **A verbatim port of ilemit's `SkipTypeToken`** (Program.cs L1188 comment; ilemit Program.cs L3693). |
| `BirSplitTopLevel(s)` | Program.cs L1219 | split a BIR comma-list at bracket-depth 0 (a compound arg keeps its `[...]`). |
| `BirTokenToMeta(tok)` | Program.cs L1235 | the actual vocabulary map: `nullable:X`→`(meta X)+"?"` (prefix→suffix); `gp:Name`→bare `Name`; then a switch: `kotlin.Int`/`int`→`Int`, `kotlin.Long`/`long`→`Long`, … `kotlin.String`/`string`→`String`, `kotlin.Unit`/`void`→`Unit`, `kotlin.Any`/`object`→`Any?`. **Returns `null` for anything compound** (`func:`/`sfunc:`/`generic:`/`clr:`/`clrg:`/`array:`/user types) — so a suspend-fn-type whose ret/arg is itself compound degrades. |

`SuspendFnMeta` is called from `FieldType` (L1267), `RetType`/`RetTypeSfx` (L1271/1274), and `ParamTok`
(L1414) — i.e. every position where a `suspend`-lambda-typed value can appear.

**FLAG (freeze #37 — prime deletion candidate):** `BirSkipTypeToken` + `BirSplitTopLevel` +
`BirTokenToMeta` (Program.cs L1187–1263, ~77 LOC) exist **only** to bridge the BIR colon/`gp:`/`nullable:`
/shorthand vocabulary to the meta bracket/bare/suffix/Kotlin-name vocabulary, for the ONE round-trip
attribute that stores a raw BIR token. See §B for whether unifying the grammars deletes them.

---

## 3. `[KotlinInline]` READ — `KotlinInlineBody`

- `const KInlineAttr = "DotKt.Runtime.CompilerServices.KotlinInlineAttribute"` (Program.cs L1136).
- `KotlinInlineBody(MethodInfo m)` (L1138): reads the single ctor-arg string of `[KotlinInline]` — the
  **carried BIR body** (the splice surface) of an `inline` fn that takes a lambda param. Returns the
  string or `null`.
- **Its ONLY effect in facadegen is the `,inline` modifier flag** (L666 for member funs, L1369 for
  `tlfun`): `+ (KotlinInlineBody(m) != null ? ",inline" : "")`. facadegen does **not** parse, translate,
  or re-emit the body — it merely detects presence to set the flag. The body itself is read from the
  assembly and spliced by the **consumer's ilemit** at emit time (Program.cs L1343/L664 comments). This
  is a pure "presence → flag" read; the body string never enters the meta grammar. So it is *not* a
  BIR↔meta translation site and is orthogonal to §2's deletion question.

---

## 4. `KotlinSuspendFunctionType` READ — how the `sfunc:` meta is produced

- `const KSuspendFnAttr = "DotKt.Runtime.CompilerServices.KotlinSuspendFunctionTypeAttribute"`
  (Program.cs L1156).
- Read + translated by `SuspendFnMeta` (§2). The attribute's payload is a raw **BIR** `sfunc:` token;
  the output is a **meta** `sfunc:[...]` token that `coneOf` (ClrTypeInjection L1137) rebuilds into
  `coneSuspendFunctionType(args, ret)` → `kotlin.coroutines.SuspendFunctionN`.
- Separate, untouched path: a `suspend fun` DECLARATION itself is emitted returning `Task`/`Task<T>`
  (the CLR lowering of `suspend`), and its Kotlin result type is restored by `SuspendRetSupported` /
  `SuspendRetToken` / `SuspendRetSuffix` (L1276–1294) — the `,suspend` modifier flag + a normal
  return-type token, NOT an `sfunc:` token. `sfunc:` is only for a suspend-lambda **value in a type
  position** (param/return/field/prop of a non-suspend member).

---

## A. The tlfun / tlextprop / tlprop meta-line grammar (requested)

All three ride an enclosing `file <package> <fileClassFQN>` line (`EmitKotlinFileClass` L1313; empty
package → `-`). The `<fileClassFQN>` is where the backend emits the static call/field access.

- **`tlfun <name> <ret> <modifier> [<TP>...] [<p>:<t>]*`** (L1370)
  - `<modifier>` = `FunModifier("final",k) + (,inline?) + (,ext?)` — a `tlfun` is always base-`final`;
    an extension (`__self` first param) adds `,ext`; suspend/infix/operator/inline fold in as §1a.
  - `[<TP>...]` = bare type-param names for a generic method definition; followed by
    `mbound`/`tvariance` lines (`EmitTypeParamMeta`).
  - kotc: ClrTypeInjection L244 → `createTopLevelFunction`, extension receiver from the `__self` param.
  - Two ambiguity guards suppress emission (both `kotlin.*`-scoped, so a user tlfun is unaffected):
    a non-extension factory returning an unresolvable `generic:List|Set|Map|…[` collection (L1358, would
    collide with the jar's same-signature factory), and an extension whose receiver maps to `Any?`
    (L1368, a catch-all that mis-wins overload resolution).
  - Also the surface for `EmitTaskAwait`: `tlfun await T final,ext,suspend T __self:generic:Task1[T]`
    and `tlfun await Unit final,ext,suspend __self:Task` (L277/279).

- **`tlextprop <name> <type> <ro|rw> <recvType>`** (L1329)
  - A top-level extension property `val/var T.p`, compiled to static `get_p(__self:T)` (+ `set_p` for
    `var`). `<recvType>` = the mapped `__self` type; `ro`/`rw` from setter presence. The accessor
    methods are excluded from the `tlfun` loop (`extPropMembers`, L1328).
  - kotc: ClrTypeInjection L252 → `ClrTopLevelProp` with a receiver (L621/622 sets `extensionReceiverType`).

- **`tlprop <name> <type> <ro|rw>`** (L1388) — issue #34b
  - A top-level `val`/`var` with a backing field, compiled to a plain `Public|Static` FIELD on the file
    class (backing-field-LESS props emit `get_` accessors instead and are not covered here). Synthetic
    backing fields (`<...>`/`$...`) are skipped (L1385). `ro`/`rw` from `[KotlinReadOnly]` / `InitOnly`
    (`IsKotlinReadOnly`, L1081). `<type>` via `FieldType` (so a `suspend`-typed field round-trips through
    `SuspendFnMeta`).
  - kotc: ClrTypeInjection L257 → `ClrTopLevelProp` with an EMPTY receiver (the distinguisher from
    `tlextprop`); read/write routed to the file class's static field.

---

## B. FREEZE SUMMARY — the requested three points

### (a) The DUAL vocabulary (BIR colon-form vs meta bracket-form) + every translation site

There are two type grammars in play:

- **meta** (facadegen ↔ ClrTypeInjection.coneOf): function types **bracketed** `func:[ret,args]` /
  `sfunc:[ret,args]`; generics `generic:Open[args]`; type vars **bare** `T`; nullability **suffix**
  `T?`/`T!`; primitives as **Kotlin names** `Int`/`String`/`Unit`/`Any?`.
- **BIR** (BirEmitter ↔ ilemit): function types **colon** `func:ret:args` / `sfunc:ret:args`; generics
  `clrg:Name[args]`/`clr:Name`; type vars **prefixed** `gp:T`; nullability **prefix** `nullable:T`;
  primitives as **shorthands** `int`/`string`/`void`/`object`.

They differ in all five axes (function-type separator, type-var marker, nullability position, primitive
spelling, generic encoding) — enumerated in §1c.

**Every place the two are translated inside facadegen** (all in the H2 suspend path, §2):
`SuspendFnMeta` (Program.cs L1157), `BirSkipTypeToken` (L1189, a verbatim port of ilemit's
`SkipTypeToken` — a *duplicated* BIR structural parser), `BirSplitTopLevel` (L1219), `BirTokenToMeta`
(L1235). **No other facadegen code touches the BIR grammar** — everything else (`Map`, `CrossType`,
`ParamTok`, `EmitOneType`, `EmitKotlinFileClass`) is pure meta.

### (b) Would unifying BIR+meta grammar let `BirTokenToMeta` (and friends) be DELETED? — YES

The entire `SuspendFnMeta` translation cluster exists for exactly one reason: the
`[KotlinSuspendFunctionType]` attribute stores a **raw BIR token** (`sfunc:kotlin.Int:` colon form) while
`coneOf` needs the **meta token** (`sfunc:[Int]` bracket form). If the freeze unifies the two grammars —
by EITHER of:

  1. having **ilemit stamp the attribute in META form** already (facadegen then passes the shape
     straight to `coneOf`, no translation), or
  2. having **coneOf accept BIR form** (facadegen forwards the raw attribute verbatim),

then `SuspendFnMeta` collapses to a trivial "read attribute → forward string", and **`BirSkipTypeToken`,
`BirSplitTopLevel`, and `BirTokenToMeta` (Program.cs L1187–1263, ~77 LOC) can all be deleted.** This also
removes the *duplicated* `SkipTypeToken` structural parser that today lives in both ilemit and facadegen.
Recommend option (1): the emitter that already knows the shape writes it once, in the grammar the
consumer reads — no cross-vocabulary bridge in facadegen at all. This is the clean 4-layer-boundary move
(no dual-track), consistent with the project's "one grammar, translate at the boundary that owns it"
principle.

`KotlinInlineBody` (§3) is **not** affected — it is a presence check that sets `,inline`; the body string
never enters either type grammar and needs no translation. It stays regardless of grammar unification.

### (c) tlfun / tlextprop / tlprop grammar — see §A. In one line each:
- `tlfun <name> <ret> <modifier=final[,inline][,ext][,suspend][,infix][,operator]> [<TP>...] [<p>:<t>]*`
- `tlextprop <name> <type> <ro|rw> <recvType>`
- `tlprop <name> <type> <ro|rw>` (empty-receiver `ClrTopLevelProp`; the #34b static-field property)

---

## Freeze checklist (facadegen surface that the #37 freeze must pin)

1. Meta line kinds (§0) + member line kinds — the wire format between facadegen and ClrTypeInjection.
2. Meta TYPE grammar (§1b) — bracketed `func:`/`sfunc:`/`generic:`, suffix `?`/`!`, `opt:`, `vararg:`,
   `span:`, `byref:`, `array:`, Kotlin scalar names.
3. Modifier flags (§1a): `,inline ,ext ,suspend ,infix ,operator` + `prot- abstract|open|final`.
4. The `DotKt.Runtime.CompilerServices.*` attribute contract facadegen reads: `KotlinFunction`(flags),
   `KotlinFileClass`, `KotlinFunInterface`, `KotlinSealed`, `KotlinReadOnly`, `KotlinInline`,
   `KotlinSuspendFunctionType` (+ standard `Nullable`/`NullableContext`/`ParamArray`/`Extension`).
5. **The BIR↔meta bridge (§2) as a DELETION candidate once the grammars unify (§B(b)).**

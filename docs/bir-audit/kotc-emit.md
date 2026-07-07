# BIR Producer Canon — kotc (BirEmitter) emit vocabulary

> READ-ONLY AUDIT for the BIR/CIR freeze (#37). This is the **producer** side: the exhaustive set of
> BIR-JSON vocabulary that kotc's `BirEmitter` writes into `*.bir.json`. Cross-reference with the
> bir2cir-consume and ilemit-consume catalogs to find over-broad / under-consumed / drifted tokens.
>
> Source files (all under `toolchain/kotc/src/main/kotlin/kotc/backend/`):
> `BirEmitter.kt` (4963 ln), `BirEmitterExpressions.kt` (`expr`/`exprInner` dispatch),
> `BirEmitterStatements.kt` (`stmt` dispatch), `BirMappings.kt` (data tables), `ClrBackendPhase.kt` (driver).
>
> Convention: line numbers are `File:Line` at audit time (2026-07-06). "emit site" is the first/representative
> `"""..."""` template that produces the node; many nodes have several emit sites (counts shown).

---

## 0. File envelope + declaration schema (structural, NOT `"k"` nodes)

The top level of every `*.bir.json` is a **file object** (one per Kotlin source file), NOT a `"k"` node:

| Key | Meaning | Emit site |
|-----|---------|-----------|
| `fileClass` | the XKt file-class name (top-level fns/props land here as statics) | `BirEmitter.kt:628` |
| `hasMain` | bool — file declares a `main` entrypoint | `BirEmitter.kt:628` |
| `fields` | file-class static fields (top-level `val`/`var` backing) | `BirEmitter.kt:628` |
| `methods` | file-class static methods (top-level fns + lifted locals/accessors) | `BirEmitter.kt:628` |
| `types` | the file's type definitions (classes/interfaces/enums + synthetics) | `BirEmitter.kt:628` |

**Type definition** objects (inside `types`) carry `"kind"` ∈ {`class`, `interface`, `enum`}:

| `kind` | Shape (keys) | Emit site |
|--------|--------------|-----------|
| `class` | `name,kind,abstract,vis,nestedIn?,sealed?,typeParams?,base,interfaces,fields,ctors,methods,properties,attrs` | `BirEmitter.kt:1349` |
| `interface` | `name,kind,nestedIn?,funSealed?,typeParams?,base(null),interfaces,fields[],ctors[],methods,properties,attrs` | `BirEmitter.kt:704` |
| `enum` | `name,kind,nestedIn?,entries:[…]` (basic enum; RICH enums are lowered to a plain `class` — see §6) | `BirEmitter.kt:743` |
| `class` w/ `annotation:true` | user `annotation class` → plain class + `"annotation":true` flag; **`base:null`** (bir2cir derives `: System.Attribute`) | `BirEmitter.kt:1103` |

**Member schemas** (not `"k"` nodes; appear inside `methods`/`ctors`/`fields`/`properties`):
- **method**: `name,static,override,virtual,abstract,objectOverride,vis,typeParams?,infix?,operator?,inline?,retNullable?,suspend?,resultType?,params,ret,body,attrs,overrides?,clrOverride?` — `BirEmitter.kt:1461` (regular), `:660` (suspend), `:1618` (clr-iface override).
- **param**: `name,type,vararg?,nullable?,default?,attrs?` — `BirEmitter.kt:1692`.
- **ctor**: `params,baseArgs,thisArgs,vis,body` — `BirEmitter.kt:1390`.
- **field**: `name,type,static?,init?,nullable?` — e.g. `BirEmitter.kt:822`.

**Optional member flag fragments** (emitted only when set — string-concatenated onto the method/param JSON):
| Fragment | Meaning | Emit site |
|----------|---------|-----------|
| `,"typeParams":[…]` | generic class/iface/method params (`name` + `bounds`) | `typeParamsJson` `:1514` |
| `,"infix":true` / `,"operator":true` | Kotlin modifier facts (ilemit → `[KotlinFunction]`) | `kotlinModsJson` `:1473` |
| `,"inline":true` | inline-fn-with-lambda → ilemit stamps `[KotlinInlineBody]` | `:1452` |
| `,"retNullable":true` | nullable return (→ .NET NRT) | `:1454` |
| `,"suspend":true,"resultType":<ty>` | suspend FACT + Kotlin result type | `:1460` |
| `,"nullable":true` (param) | nullable param flag | `:1676` |
| `,"vararg":true` (param) | `vararg` → ilemit `[ParamArray]` | `:1675` |
| `,"default":<expr>` (param) | Tier-1 metadata-representable default | `:1681` |
| `,"overrides":[{owner,member,kind,arity}]` | override closure (bir2cir resolves intrinsics) | `overridesJson` `:1017` |
| `,"attrs":[…]` | annotations → .NET custom attributes | `attrsJson` `:1106` |
| `,"clrOverride":<clrOwner>` | method overrides an injected .NET member | `:1618` |
| `,"nullable":true` (var/field, gp:) | nullable type-param local/field → bir2cir erases to `object` | `nullableGpFieldFlag` `:1138` |

---

## 1. NODE KINDS (`"k":"…"`) — the full inventory

Grouped by category. **Count** = distinct emit-site lines matching `{"k":"<kind>"` across the three files
(dynamic `newList`/`newSet` at `:3476` are not counted by grep — see Literals). Meaning is 1 line; the
representative emit site is given.

### 1a. Literals / references
| `k` | Fields | Meaning | Emit site | Notes |
|-----|--------|---------|-----------|-------|
| `const` | `type,value` | literal constant; `type` is a **shorthand** (`int`/`bool`/`string`) OR a FQN (`kotlin.Int`,`kotlin.Unit`) | `Expr:79` | ⚠ mixed type vocab — see Drift D1 |
| `local` | `name` | local/param read | `Expr:103` | 60 sites |
| `this` | — | dispatch receiver | `Stmt:115` | 33 sites |
| `field` | `ownerType,recv,name` | instance field read | `Expr:141` | |
| `staticField` | `ownerType,name` | static field read (also enum singleton, `INSTANCE`) | `Expr:112,124` | |
| `default` | `type` | default/zero value of a type | `Stmt:87`, `:2593` | |
| `classRef` | `type` | `T::class` / `Foo::class` → System.Type token | `Expr:229` | |
| `getType` | `e` | `x::class` (runtime) → `x.GetType()` | `Expr:232` | |
| `enumValue` | `type,ordinal` | basic-enum value = ordinal const typed as CLR enum | `Expr:115` | |

### 1b. Calls / member access
| `k` | Fields | Meaning | Emit site | Notes |
|-----|--------|---------|-----------|-------|
| `callStatic` | `owner,method,args,sig?,typeArgs?,retType?,suspendCall?` | static/top-level call (`owner:null` = file-class) | `:4357` | 31 sites |
| `callInstance` | `ownerType,virtual,recv,method,args,sig?,typeArgs?,retType?,suspendCall?,overrides?` | virtual/instance call | `:4376` | 16 sites |
| `objMethod` | `method,recv` | forced System.Object method (`ToString`) | `:3620` | rare — see Drift D4 |
| `new` | `type,argTypes,args` | ctor of an in-assembly / user type | `Stmt:88`, `Expr:189` | |
| `setField` | `ownerType,recv,name,value` | instance field write (stmt) | `Stmt:119` | |
| `setFieldExpr` | `ownerType,recv,name,value` | @ClrField property set in expr position | `:4069` | ⚠ near-dup of `setField` — Drift D3 |
| `setLocal` | `name,value` | local assignment | `Stmt:111` | |
| `staticFieldSet` | `ownerType,name,value` | top-level `var` write via `set_` fallback | `:3872` | |
| `lateinitGet` | `ownerType,recv,name` | `lateinit` backing-field read + init check | `Expr:139` | |

### 1c. CLR-bound calls (`clr*`) — **legacy, facadegen-interop only** (see §7)
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `clrStatic` | `type,method,argTypes,ret,args,suspendCall?` | static call on FIR-injected .NET type | `:3825` |
| `clrInstance` | `type,method,argTypes,ret,recv,args` | instance call on injected .NET type | `:2214` |
| `clrGenericStatic` | `type,method,typeArgs,shapeTypes,args` | generic static on injected .NET type; kotc emits `shapeTypes` (pure-Kotlin declared-param `birType` nodes), bir2cir's `ShapeSynthesis` derives the frozen `shapes` string array off the `@ClrTypeAlias` index (#55 §4) | `:2729` |
| `clrGenericInstance` | `type,method,typeArgs,shapeTypes,recv,args,suspendCall?` | generic instance on injected .NET type; `shapeTypes`→`shapes` as above (#55 §4) | `:3783` |
| `newClr` | `type,argTypes,args` | ctor of injected .NET type | `Expr:174` |
| `clrPropGet` | `type,name,retType,static,recv` | injected .NET property get | `:3812`, `Expr:136` |
| `clrPropSet` | `type,name,static,recv,value` | injected .NET property set | `:3811`, `Stmt:117` |

### 1d. Operators / conversions
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `bin` | `op,l,r` | primitive binary op (op = `+ - * / % < <= > >= == & \| ^ << >> >>>`) | `:3420` |
| `un` | `op,e` | primitive unary op (op = `- + ! ~`) | `Expr:200` |
| `conv` | `to,e` | numeric conversion (`to` = int/long/double/…) | `:3610` |
| `concat` | `parts` | string template concatenation | `Expr:196` |
| `objEq` | `l,r` | structural `Object.Equals` equality | `:841` |
| `cond` | `cond,then,else` | expression-level ternary | `:3184` |
| `cast` | `type,e` | `x as T` / IMPLICIT_CAST / smart-cast (castclass/unbox) | `Expr:102,209` |
| `isinst` | `type,e` | `x is T` (bool) | `Expr:199` |
| `isinstRef` | `type,e` | `x as? T` for reference T | `Expr:214` |
| `safeCastValue` | `elem,e` | `x as? T` for value T (→ `Nullable<T>`) | `Expr:213` |

### 1e. Nullable (value-type `T?` = `Nullable<T>`) family
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `nullableValue` | `elem,e` | unwrap `Nullable<T>.Value` | `:4617`, `Expr:101` |
| `nullableWrap` | `elem,e` | wrap value → `Nullable<T>` | `:3184` |
| `nullableHasValue` | `elem,e` | `Nullable<T>.HasValue` | `:3184` |
| `nullableNull` | `elem` | a null `Nullable<T>` | `:3184` |

### 1f. Arrays / collections literals
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `newArray` | `elem,elems` | array literal from elements (also vararg) | `Expr:266`, `:838` |
| `newArrayInit` | `elem,size,init` | `IntArray(n){f}` — sized + fill lambda | `Expr:159` |
| `newArraySized` | `elem,size` | `IntArray(n)` — sized, zero-filled | `Expr:161` |
| `arrayGet` | `elem,array,index` | `a[i]` read | `:3668` |
| `arraySet` | `elem,array,index,value` | `a[i] = v` | `:3669` |
| `arrayLen` | `array` | `a.size` / `EnumEntries` length | `:3978` |
| `spreadConcat` | `elem,parts[{spread,e}]` | mixed `f(1,*a,2)` vararg build | `Expr:276` |
| `newList` | `elem,elems` | `listOf(...)` → List<elem> | `:3476` (dynamic `kind`) |
| `newSet` | `elem,elems` | `setOf(...)` → HashSet<elem> | `:3476` (dynamic `kind`) |
| `newMap` | `keyType,valType,entries` | `mapOf(a to 1,...)` → Dictionary | `:3501` |

### 1g. Control flow — statements
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `block` | `body` | statement block | `Stmt:175` |
| `exprStmt` | `expr` | expression used as statement | `Stmt:169` |
| `return` | `value?` | method return (Unit → no value) | `Stmt:144` |
| `if` | `branches:[{cond?,else?,body}]` | if/when as statement | `:324`, `:845` |
| `throw` | `value` | `throw` statement | `Stmt:162` |
| `try` | `type,body,catches:[…],finally?` | try/catch/finally statement | `:1879` |
| `for` | `label,var,from,to,cmp,step,body` | structured counter loop | `:1849` |
| `forArray` | `label,var,elem,array,body` | for over an array | `:1804` |
| `forEachInline` | `label,elem,src,var,body` | for over IEnumerable (GetEnumerator) | `:1824` |
| `forRange` | `label,var,elem,range,accessOwner,firstM,lastM,stepM,body` | for over IntProgression value | `:1838` |
| `repeatInline` | `var,count,body` | `repeat(n){}` | `:4277` |
| `label` | `id` | a CFG label target (int id) | `:1426` |
| `goto` | `id` | unconditional jump to label id | `Expr:247` |
| `brIf` | `id,on,cond` | conditional branch to label id | `:1770` |
| `break` | `label?` | structured-loop break (label = Kotlin loop label) | `Stmt:157` |
| `continue` | `label?` | structured-loop continue | `Stmt:159` |

### 1h. Control flow — expression position
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `valueBlock` | `stmts,result` | statements producing a value (try-in-expr, control-transfer) | `:1728` |
| `throwExpr` | `value` | `throw` in expr position (transfers control) | `Expr:235`, `:1728` |
| `returnExpr` | `value?` | `return` in expr position | `Expr:238` |

### 1i. Closures / delegates / SAM
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `newClosure` | `closureType,captures,method,funcType,typeArgs?` | lambda w/ captures → synthetic closure class | `:2076` |
| `newDelegate` | `method,funcType,typeArgs?` | capture-free lambda / `::foo` → bare delegate | `:2046` |
| `newBoundDelegate` | `ownerType,method,virtual,recv,funcType` | `obj::method` bound to a receiver (user type) | `:2169` |
| `newBoundClrDelegate` | `clrType,method,argTypes,virtual,recv,funcType` | `obj::method` bound (injected .NET owner) | `:2199` |
| `newSam` | `samType,captures,typeArgs?` | fun-interface SAM conversion → synthetic impl class | `:2115` |
| `newSuspendLambda` | `arity,captures,params,resultType,typeArgs,body,funcType` | `suspend {}` lambda (SM built downstream) | `:2019` |
| `delegateInvoke` | `funcType,recv,args` | invoke a delegate value | `:316` |

### 1j. Property-delegate glue (`by`)
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `clrPropGet`/`clrPropSet` | (see 1c) | also reused for @ClrProperty-bound accessors | |
| (delegated props route through `callInstance` getValue/setValue + a `new <>dotkt_KPropertyImpl`) | | | `:3323` |

### 1k. byref / stackalloc (intrinsics `kotlin.clr.*`)
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `byrefOf` | `inner` | keep a ref-return's managed pointer | `Stmt:81` |
| `byrefLoad` | `local,elem` | ldobj through a `ref T` local | `:3302` |
| `byrefStore` | `local,elem,value` | stobj through a `ref T` local | `:3301` |
| `stackAlloc` | `count,elem` | `stackalloc T[n]` | `:2664` |
| `stackGet` | `ptr,len,index,elem` | read from stackalloc buffer | `:2678` |
| `stackSet` | `ptr,len,index,elem,value` | write to stackalloc buffer | `:2680` |
| `stackAsSpan` | `ptr,len,elem` | wrap stackalloc buffer as `Span<T>` | `:2682` |

### 1l. Enums (reified intrinsics)
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `enumValues` | `type` | `T.values()` / `enumValues<T>()` | `:3577` |
| `enumParse` | `type,arg` | `T.valueOf(s)` / `enumValueOf<T>(s)` | `:3578` |
| `enumOrdinal` | `e` | `.ordinal` | `:3419` |

### 1m. Inline-splice + default-arg placeholders (ABI — consumed by bir2cir)
| `k` | Fields | Meaning | Emit site |
|-----|--------|---------|-----------|
| `inlineSplice` | `type,method,pc,ga,bindings,this?` | splice-site marker for a cross-module inline fn body | `:2520` |
| `defaultArg` | — | omitted cross-module default (positional placeholder) | `defaultArgPlaceholder :2866` |
| `defaultArgParam` | `idx` | a default expr reading another param → call-index token | `:1668` |

### 1n. Misc / one-offs
| `k` | Fields | Meaning | Emit site | Notes |
|-----|--------|---------|-----------|-------|
| `var` | `name,type,init,nullable?` | local var decl | `Stmt:82` | 22 sites |
| `strReversed` | `s` | `String.reversed()` (kotc-lowered, pending stdlib fix) | `:4292` | ⚠ lone kotc string-op lowering — Drift D5 |
| `unsupportedExpr` | `of` | placeholder for an unlowerable IR node (compile already ERRORed) | `:135` | |

**Total distinct `k` values: ~95** (93 grep-visible single-line + `newList`/`newSet` dynamic).

---

## 2. TYPE-TOKENS (`birType()` output vocabulary)

`birType(IrType): String` (`BirEmitter.kt:4764`) is the sole type encoder. Companion encoders that produce
the SAME vocabulary but with local rules: `birTypeDeleg` (`:2840`, KProperty→object, Unit-param), `funcTypeOf`
(`:2814`), `funcRetTypeOf` (`:2828`), `arrayElemType` (`:4581`), `ownerSpec` (`:1557`), `collectionElemType`
(`:2701`), `mapKV` (`:4590`), `nullableElem` (`:4596`).

### 2a. ATOMIC tokens (a leaf; no splitting needed)
| Form | Example | Produced at | Meaning |
|------|---------|-------------|---------|
| plain FQN | `kotlin.Int`, `kotlin.Unit`, `kotlin.Nothing`, `kotlin.Any`, `kotlin.String`, `System.Exception` | `:4850-4871` | the type's Kotlin FQN identity — **kotc emits NO CLR-resolution marker** (bir2cir lowers) |
| `gp:<name>` | `gp:T`, `gp:E` | `:4772` | a generic type parameter (reified on CLR) |
| `@<Name>` | `@Box`, `@kotlin.IllegalStateException`, `@<>dotkt_CharSequence` | `:4901,4933` | reference to an in-assembly / user / synthetic type |
| `clr:<Name>` | `clr:System.Text.StringBuilder` | `:4897` | a **referenced non-generic .NET type** (FIR-injected) |
| primitive shorthand | `int`,`long`,`short`,`byte`,`double`,`float`,`bool`,`char` | `BirMappings VALUE_PRIM_BIR / PRIMITIVE_ARRAY_ELEM` | only in ELEMENT/const/conv slots — **NOT** from `birType` top-level (see Drift D1) |
| `object` | `object` | `:4935,4919,4896` | Any?/erased/star-projection fallback |
| `void` | (params never; return special-cased) | — | Unit as return → NOT emitted by kotc (`kotlin.Unit` kept); shorthand only appears in hand-written synthetics |
| `stackptr` | `stackptr` | `:2664` | a stackalloc pointer local type (hand-written, not from birType) |

### 2b. COMPOUND tokens (need parsing/splitting to read back)

| Form | Format string | Produced at | Nesting notes |
|------|---------------|-------------|---------------|
| `nullable:<elem>` | `"nullable:" + elem` | `:4781` (value `T?`), `:3239` | wraps a value-primitive OR a `gp:T` (`nullable:gp:T`, `nullable:int`) |
| `array:<elem>` | `"array:" + arrayElemType(t)` | `:4782` | elem may itself be compound (`array:@Box[int]`) |
| `byref:<T>` | `"byref:" + birType(arg)` | `:4776` (`ClrRef<T>`) | inner is a full birType |
| `clrg:<openName>[a,b,…]` | `"clrg:$net[" + args.join(",") + "]"` | `:4893,4896,4798`, `Expr:166` | generic .NET type; raw/star → filled w/ `object` per param |
| `@<Name>[a,b,…]` | `"@"+typeName+"["+ (enclArgs+ownArgs).join(",") +"]"` | `:4929,4932` | constructed user generic; inner args full birType (may nest) |
| `func:<ret>:<p1,p2,…>` | `"func:$ret:${ps.join(",")}"` | `funcTypeOf :2818`, `:4822,4844` | **delegate shape**; ret + comma-joined params |
| `sfunc:<ret>:<p1,p2,…>` | `"sfunc:$ret:$ps"` | `funcTypeOf :2817`, `:4821` | **suspend** variant of `func:` (carries only the suspend FACT) |

**⚠ Fragile split hazard (the freeze must nail these):**
- `func:`/`sfunc:` use `:` as a 2-part separator (`func:<ret>:<params>`) AND `,` between params — but **a param
  can itself be a `func:` / `clrg:...[..]` / `@Name[..]`** containing `:`, `,`, `[`, `]`. A naive `split(":")`
  or `split(",")` MIS-parses a nested func-typed param. ilemit's `FuncRetEnd` is explicitly noted (`:2826`) to
  parse "a single leading prefix" — this is a hand-rolled bracket/colon-depth parse, not a grammar. **This is
  the single most drift-prone token in the canon.**
- `[...]` generic-arg lists (`clrg:`, `@Name[..]`) nest arbitrarily and share the `,` separator with `func:`
  params — a consumer MUST bracket-count, not string-split.

**Real nested examples (constructed from the emit rules):**
1. `func:` returning a `func:` — a `() -> (Int) -> String` value: `funcRetTypeOf` encodes the inner via
   `birTypeDeleg` → `func:kotlin.String:kotlin.Int`, so the outer is
   `func:func:kotlin.String:kotlin.Int:` (empty param list). The doubled `:` is real and must be depth-parsed.
2. `sfunc:` with a generic arg param — a `suspend (List<Int>) -> Unit`:
   `sfunc:kotlin.Unit:@kotlin.collections.List[kotlin.Int]` (or `clrg:` form in rt build).
3. Nullable generic-return func — `(T) -> R?`: `func:nullable:gp:R:gp:T` (`funcRetTypeOf :2831`). The
   `nullable:` marker rides INSIDE the `func:` ret slot; bir2cir erases it to `object`, and it must never reach
   ilemit's single-prefix `FuncRetEnd`.
4. Constructed user generic with an enclosing inner param — `Outer<E>.Inner` used as `State<T>`:
   `@Outer$Inner[gp:E]` or `@State[gp:T]` (`:4929`; note `$` type/namespace separator from `typeName :266`).

### 2c. Where a primitive's identity SPLITS across forms (the same `kotlin.Int` appears 3+ ways)
| Position | Token | Producer |
|----------|-------|----------|
| top-level type / type-arg | `kotlin.Int` | `birType :4856` |
| array element | `int` | `arrayElemType`/`PRIMITIVE_ARRAY_ELEM` |
| nullable value | `nullable:int` | `nullableElem` + `:4781` |
| `const`/`conv`/`var` synthetic slot | `int` | hand-written templates (`Expr:79`, `:3610`) |
| generic-return-nullable | `nullable:int` | `:3239` |

This inconsistency is **by design** for arrays (a CLR array element type is a resolution decision that ilemit
needs early) but is a genuine drift smell for `const`/synthetic slots — see Drift D1.

---

## 3. ATTRIBUTE-CARRIED BIR (persisted → ABI; survives the round-trip)

kotc stamps BIR vocabulary INTO round-trip attributes; these are **frozen ABI** because a ref.dll persists them
and bir2cir/kcc re-reads them cross-module.

| Attribute / carrier | Payload | Emit site | Consumer |
|---------------------|---------|-----------|----------|
| `,"inline":true` on method | flags the fn; ilemit stamps `[KotlinInlineBody]` carrying **this method def's `body:[…]` BIR** verbatim | `BirEmitter.kt:1449–1461` | ilemit persists body; bir2cir/kcc splice it at the call site (`inlineSplice`) |
| `@kotlin.clr.KotlinDefault(idx, bir)` param attr | `argTypes:["kotlin.Int","kotlin.String"]`, `args:[const idx, const <BIR-json-STRING of the default expr>]` | `:1686-1688` | bir2cir `DefaultArgSplice` fills `defaultArg` placeholders by index (PRE-lowering — the BIR string is opaque to this build's type lowering) |
| `{"k":"defaultArgParam","idx":N}` | a splice token INSIDE a `@KotlinDefault` BIR string, for a default that reads another param | `:1668` | bir2cir substitutes the caller's arg at index N |
| `{"k":"this"}` (`defaultArgThisToken`) | receiver-splice token inside a default BIR string | `:2867` | bir2cir substitutes the call's receiver |
| `,"suspend":true,"resultType":<ty>` | suspend FACT + Kotlin result type on the method def | `:1460` | ilemit kickoff signature; bir2cir SM transform |
| `,"suspendCall":true` | suspend FACT on a CALL node | `suspendCallTag :4395` | bir2cir await/SM lowering |
| `sfunc:<ret>:<params>` | suspend-fn-type token (a `func:` variant) as a value type | `:4821`, `funcTypeOf :2817` | bir2cir erases `sfunc:` → `object` in TYPE slots (only the `funcType` node key keeps `func:`); `newSuspendLambda.funcType` keeps it for the SM builder |
| `,"attrs":[…]` on decl/param | annotations → .NET custom attrs; user annotation named by plain Kotlin FQN | `attrsJson :1106`, `:1690` | bir2cir derives `: System.Attribute` base from `annotation:true` flag |

**meta tokens:** kotc does **NOT** emit `meta:` type tokens — the `meta`/facade-metadata vocabulary is
facadegen's (kotc consumes injected-symbol identity via `kotc.frontend.clrInjected*Name`, `:4409-4501`, it does
not emit a `meta` token). Confirmed: no `"meta"` literal is produced in any of the three emitter files.

---

## 4. LABEL / NAMING conventions kotc emits

### 4a. Synthetic TYPE names (`<>dotkt_*` — the `<>` prefix keeps them out of Kotlin source space)
| Name / prefix | Purpose | Site |
|---------------|---------|------|
| `<>dotkt_${synthScope}_Closure<N>` | lifted lambda closure class | `:2052` |
| `<>dotkt_${synthScope}_Sam<N>` | lifted SAM impl class | `:2096` |
| `<>dotkt_KProperty` / `<>dotkt_KPropertyImpl` | synthetic KProperty iface + impl | `:347,350` |
| `<>dotkt_CharSequence` | synthetic CharSequence iface | `:396` |
| `<>dotkt_KIterator_<elem>` / `<>dotkt_KIterator_gp_E` / `<>dotkt_KIterable_<elem>` | monomorphized iterator/iterable ifaces | `:445,450` |
| `<>dotkt_ROProperty_<v>` / `<>dotkt_RWProperty_<v>` | monomorphized Read/ReadWriteProperty ifaces | `propIface` `:433,438` |
| `<>dotkt_Ref_<elem>` | heap ref-cell class (captured mutable var) | `:465` |
| `<>dotkt_ClrH_<Class>` | rule-3 static-helper hoist (facadegen interop only) | `clrHelperName :1470` |
| `<>dotkt_tryval<N>` | try-in-expr result temp type | `:1887` |
| `<>dotkt_obj` / `<>dotkt_objN` | anonymous-object names | `anonNames` |

`synthScope` = a per-file prefix so `<>dotkt_Closure0` doesn't COLLIDE across files (`:184`).

### 4b. Capture / receiver substitution names (fixed identifiers)
| Name | Meaning | Site |
|------|---------|------|
| `__self` | extension-receiver param (a top-level ext fn / hoisted member) | `:1043,1429`, 37 sites |
| `__outer` | inner-class captured enclosing instance (a field) | `:950` |
| `__name` / `__ordinal` | rich-enum synthetic ctor params / fields | `:868,869` |
| `__old` / `__set` | Delegates.observable/vetoable/notNull backing fields | `:315,329` |
| `this` (`{"k":"this"}`) | dispatch receiver | passim |

### 4c. Fresh-counter temp locals (monotonic per-emitter counters)
| Pattern | Counter | Site |
|---------|---------|------|
| `__lambda<N>` | `lambdaCounter` | `:2038` |
| `__ctorref<N>` / `__mref<N>` | `lambdaCounter` | `:2131,2176` |
| `__scope<N>` | `scopeCounter` | `:2238` |
| `__use<N>` / `__useRes<N>` | `scopeCounter` | `:2268` |
| `__rng<N>` | `scopeCounter` | `:1847` |
| `__synth<N>` | `synthCounter` | `:2305` |
| `__inl<N>` / `__lam<N>` / `__inlRet<N>` | `inlCounter` | `:2423,2533,2589` |
| `__sbp<N>` / `__sbl<N>` | stackalloc ptr/len | `:2661` |

### 4d. CFG labels (integer ids, NOT strings)
`label`/`goto`/`brIf` `id` fields are `Int` from `cfgFresh()` (`cfgLabelN++`, `:174`). Kotlin loop labels
(`outer@`) ride separately as `,"label":<string>` on structured `break`/`continue`/`for*` nodes (`labelJson :1710`).

### 4e. Coroutine names — **kotc emits NONE**
`$dotkt_suspend`, `$sm`, resume labels, state-machine field names do **NOT** appear anywhere in kotc's output
(verified: no `$sm` / `$dotkt_suspend` literal in any emitter file). kotc emits suspend **FACTS only**
(`suspend:true` / `suspendCall:true` / `sfunc:` / `newSuspendLambda`); the entire SM naming vocabulary is
bir2cir's. This is the correct boundary and must stay in the freeze.

---

## 5. Data tables that seed the vocabulary (`BirMappings.kt`)

These are NOT node kinds but drive which nodes/tokens get emitted:
`BINARY`/`UNARY` (op strings), `PRIMITIVE_ARRAY_ELEM`/`ARRAY_CLASS_ELEM`/`VALUE_PRIM_BIR` (→ shorthand tokens),
`NUMBER_CONV` (→ `conv.to`), `PRIMITIVE_SHORTHANDS`, `PRIMITIVE_OP_FQ`/`PRIMITIVE_EQ_FQ` (which owners lower to
`bin`/`un`), `LIST_FACTORIES`/`SET_FACTORIES`/`MAP_FACTORIES` (→ `newList`/`newSet`/`newMap`),
`COLLECTION_MEMBER`/`COLLECTION_OPS` (interception sets), `ENUM_REIFIED_INTRINSICS`, `INT_PROGRESSION_FQ`,
`SEQUENCED_COLLECTION_LEAK`. Note the file explicitly REMOVED the math/string/char/exception maps (now bir2cir's).

---

## 6. Lowerings kotc STILL performs (things that arguably belong downstream)

kotc is supposed to do "zero lowering", but it currently still structurally lowers (each a candidate to migrate
toward bir2cir, per the layer rule):
- **primitive ops** → `bin`/`un`/`conv` (genuine IL ops — the sanctioned residual).
- **rich enum** → a hand-built `class` with `values`/`valueOf`/`ToString`/static singleton fields (`:743-870`).
- **collection/map factories** → `newList`/`newSet`/`newMap` (`:3471-3501`).
- **for-loops** → `for`/`forArray`/`forRange`/`forEachInline`/`repeatInline` (structured, IL-shaped).
- **`while`/`do-while`/`when`** → CFG `label`/`brIf`/`goto` (`cfgWhile`/`cfgDoWhile`/`cfgWhen`).
- **nullable value-type** (`Int?`) → the `nullable*` family (Nullable<T> IL model — a CLR representation choice).
- **inline fn splice / default args** → `inlineSplice`/`defaultArg` (frontend-jar-driven).
- **`use{}`/`with`/scope fns** → try/finally + inline scope.
- **`strReversed`** → the lone remaining kotc string-op lowering (D5).

---

## 7. The `clr*` node family + `clrName()` — LEGACY scope note

The `clr:`/`clrg:` type tokens and the `clrStatic`/`clrInstance`/`clrGenericStatic`/`clrGenericInstance`/
`newClr`/`clrPropGet`/`clrPropSet` nodes are produced **only** via `clrName()` (`:4451`), which — post Task-#5 —
reads its name **exclusively from FIR-injected .NET-interop symbols** (`kotc.frontend.clrInjectedDotNetName` /
`clrInjectedMemberName`, i.e. facadegen's `import System.X`), plus the one `java.util.Comparator` re-alias.
`clrName()` **no longer reads `@ClrIntrinsic`/`@ClrTypeAlias`** (the stdlib substitution is bir2cir's). So these
`clr*` nodes are the **app-side .NET-interop** path, not the stdlib path. They are legitimate today (facadegen
interop needs a resolved .NET owner), but they encode a CLR-resolution decision (`type` is already a `.NET`
name) — which is exactly the kind of resolution the freeze/architecture wants pushed below the kotc boundary.
Flag for the freeze: **decide whether the `clr*` node family stays a kotc emission or becomes a bir2cir
derivation from a plain injected-FQN + injection metadata.**

---

## SUSPECTED DRIFT (summary)

- **D1 — primitive type-token vocabulary is split three ways.** `birType` emits `kotlin.Int` for a top-level
  primitive, but `const`/`conv`/`var`-synthetic/array-element/nullable slots emit the **shorthand** `int`
  (and `nullable:int`). So the SAME primitive identity is `kotlin.Int` | `int` | `nullable:int` depending on
  JSON position. The CLAUDE.md invariant ("kotc emits ONLY the FQN identity … the shorthand vocabulary lives
  BELOW the kotc boundary") is honored by `birType` but **violated by the array-element / const / conv paths**,
  which still emit `int`/`bool`/`char`. A freeze must pick one and note the exception explicitly.
- **D2 — `func:`/`sfunc:` are the fragile compound tokens.** `func:<ret>:<params>` overloads `:` as both the
  ret/params separator AND (recursively) inside a nested func-typed param, sharing `,`/`[`/`]` with generic-arg
  lists. There is no grammar — consumers hand-roll a single-prefix / bracket-depth parse (`FuncRetEnd`
  comment at `:2826`). The `nullable:` marker riding inside a `func:` ret slot (`func:nullable:gp:R:…`) is an
  extra depth wrinkle. **Highest-risk item to pin in the freeze.**
- **D3 — `setField` vs `setFieldExpr` near-dup.** Two nodes with identical shape (`ownerType,recv,name,value`)
  differing only by statement (`setField`, `Stmt:119`) vs expression (`setFieldExpr`, `:4069`) position.
  Likewise `staticFieldSet` (`:3872`) is a third field-write variant. Candidate to unify.
- **D4 — `objMethod` is a near-orphan.** Emitted at exactly one logical site (`ToString` at `:3620`); a
  `callInstance` on `object`/`kotlin.Any` could subsume it. Worth confirming it's still reachable.
- **D5 — `strReversed` is the lone hand-lowered string op.** BirMappings' comment (`:22`) admits it stays only
  "pending a `StringBuilder(CharSequence)`-ctor stdlib fix" — a temporary kotc lowering that should die stdlib-side.
- **D6 — the `clr*` node family** (§7) encodes a CLR-name resolution (`type:"System..."`) inside kotc output.
  Post-#5 it's facadegen-interop-only, but it is exactly the "where does it live on the CLR" decision the
  architecture says belongs below kotc. Freeze decision needed.
- **D7 — `nullable`/`vararg`/`suspend`/`default`/`inline` etc. are string-concatenated flag FRAGMENTS**, not a
  structured `flags` object. Order-dependent template splicing (`$kmods$inlineFlag$retNull$suspendField`) is
  brittle; a frozen schema should enumerate the exact key order or make them a set.
- **D8 — three "for" variants + one CFG-while** all coexist (`for`/`forArray`/`forRange`/`forEachInline`/
  `repeatInline` PLUS `label`/`brIf`/`goto` for while). A consumer must handle both a structured-loop family
  AND a CFG family. The comment at `Stmt:151` notes for-loops are "structured until §5.4" — i.e. mid-migration;
  the freeze should record whether both families are permanent.

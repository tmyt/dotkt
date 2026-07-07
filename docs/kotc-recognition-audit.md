# kotc recognition audit — the "kotc purity completion" (post-#37, old bundle 8)

> **READ-ONLY audit. No code changed.** Goal: kotc must emit **only IR-derived Kotlin identity + genuine
> frontend facts** — **zero** hardcoded stdlib-symbol *recognition* and **zero** hardcoded FQN *literals*.
> Everything CLR-shaped is bir2cir/ilemit's job (derived from the ref.dll `@Clr*` metadata). This doc finds
> **every** residual site in `toolchain/kotc/src/main/kotlin/kotc/backend/` and gives a per-site verdict.

Scope audited: `BirEmitter.kt` (5016 L), `BirEmitterExpressions.kt`, `BirEmitterStatements.kt`,
`BirMappings.kt`. Verified against the 2026-07-07 tree; Codex (`gpt-5.5`) consulted for the per-category
migration verdicts (its conclusions are folded in below).

## The distinction (the three residual columns)

- **(A) INPUT recognition** — kotc pattern-matches a *specific* stdlib symbol (`classFqName == "kotlin.X"`,
  `name == "plus"`, a membership table `in LIST_FACTORIES`) to pick a lowering. **This is the residual the
  user is frustrated by.**
- **(B) OUTPUT FQN-literal** — kotc *synthesizes* a node whose type slot is a **string literal**
  (`fqnJson("kotlin.Int")`) instead of an IR-derived type (`birType(irType)`).
- **(C) SYNTHESIS lowering** — the kotc lowering that *produces* a (B). Almost every (B) is the output half
  of an (A): the recognition+synthesis is **one migratable unit** (e.g. inc-lowering recognizes `inc` **and**
  synthesizes `binOp(+, const 1:kotlin.Int)`).

**Verdict mechanisms** (per site):
- **(a) metadata-driven** — a `@Clr*` marker on the stdlib symbol that bir2cir reads off the ref.dll. Kills
  the hardcode entirely; the *principled* fix.
- **(b) FQN-recognition-in-bir2cir** — move the same hardcode down one layer (bir2cir recognizes the resolved
  callee FQN). Removes CLR knowledge from kotc but is still a hardcode, one layer lower.
- **(c) must-stay in kotc** — a pure frontend/language fact (control-flow shape, primitive IL op, class-kind,
  annotation flag). Justified per-site.
- **(d) IR-derive** — for a (B): replace the literal with `birType(irBuiltIns.<x>Type)`. Minimal, mechanical.

---

## The governing principle (user, 2026-07-07) — read this first

**kotc is a FAITHFUL IR→BIR transcriber.** Every type token it emits must come from a resolved `IrType`
(`irType.classFqName` via `birType(irType)`). **Faithful transcription NEVER needs a literal** — the type is
always in hand from the IR node being transcribed. Therefore:

> **The moment kotc writes `fqnJson("kotlin.Int")`, it has proven it is SYNTHESIZING a node that is not in the
> IR — i.e. doing a LOWERING — i.e. doing bir2cir's job.**

**Headline metric: the count of hardcoded `kotlin.*` FQN literals ≈ the amount of misplaced lowering in kotc.**
Current: **~95 occurrences / ~14 distinct FQNs.** **Target: 0.** Zero literals = zero synthesis = pure
transcriber. This reframes every (B) verdict: the *primary* answer is **not** "IR-derive the string" (a
half-measure that deletes the string but leaves the misplaced lowering — irBuiltIns-ing the `1` in `inc→+1`
still leaves kotc synthesizing `binOp(+, const 1)`). The primary answer is: **which synthesis creates the need
for this literal, and can that synthesis move to bir2cir** so kotc emits the faithful IR call and never names
the type by literal at all.

Per-literal ranking:
1. **Relocatable synthesis (the goal, and the verdict for MOST sites)** — a kotc lowering that should move to
   bir2cir; moving it deletes BOTH the synthesis and the literal.
2. **IR-derivable literal (fallback ONLY)** — no relocatable lowering, but the type is a real `irBuiltIns.*Type`
   / resolved symbol. Removes the string only; **flag it "still leaves kotc doing X, revisit."**
3. **Genuinely unavoidable frontend fact** — must be justified explicitly; should be near-zero.

### Worked example (the canonical offender): `inc`/`dec` — BirEmitter.kt:4228–4229

```kotlin
if (name == "inc" && operands.size == 1 && primOperand(operands[0]))
    return """{"k":"binOp","op":"+","lhs":${valueOperand(operands[0])},"rhs":{"k":"const","type":${fqnJson("kotlin.Int")},"value":1}}"""
```

One line, all three residuals at once: **(A)** recognizes the stdlib symbol `inc`; **(C)** synthesizes a
`binOp(+ …)` node absent from the IR (the IR has a faithful `callInstance kotlin.Int.inc`); **(B)** must invent
`fqnJson("kotlin.Int")` for the `const 1` it fabricated. The half-measure (irBuiltIns the `1`) still leaves
kotc turning a member call into arithmetic — CLR knowledge ("`System.Int32` has no `inc`, realize it as `+1`").
**Principled fix: kotc emits the faithful `callInstance(kotlin.Int.inc, recv)`; bir2cir lowers the primitive
member-operator to the CIL `add`.** Then the recognition, the synthesis, and the literal all vanish together.

This is why the "primitive operators / conv / range-desugar" families below are marked **relocatable to
bir2cir** even though they are *currently* the "genuine primitive IL op" bucket CLAUDE.md kept in kotc: under
the faithful-transcriber principle their principled home is bir2cir. They are deferred for **cost** (pervasive,
high churn), **not** because they are frontend facts. See §Part 5 for the honest cost/sequencing split.

---

## Honest totals (read this first)

| Bucket | Count | Disposition |
|---|---|---|
| **BirMappings.kt tables** | 15 | 1 DEAD (delete), 6 recognition-migrate, 8 keep/frontend |
| **(A) input recognition sites** (call/type/name) | **~46** | ~14 genuinely migratable, ~10 interop-legit, ~22 must-stay frontend |
| **(B) hardcoded FQN literals** (`fqnJson("kotlin.X")`) — **the misplaced-lowering count** | **~95 occurrences / ~14 distinct FQNs → target 0** | each traces to a (C) synthesis; primary fix = relocate that synthesis |
| **(C) synthesis lowerings emitting (B)** | ~18 | most relocatable to bir2cir; a few IR-derivable-fallback |

**Bottom line, honest.** Two truths held together:

1. **The strict target is 0 literals / 0 synthesis** (faithful transcriber). Under that principle, the largest
   family — primitive **operators / conv / inc-dec / range-desugar / `==`** — is **relocatable to bir2cir**, not
   a frontend fact. It is deferred for **cost** (pervasive, highest churn, and it is already Kotlin-clean at the
   `binOp` boundary so the risk/reward is poor), **not** because it belongs in kotc.
2. **The high-value, low-cost core** — the part that both offends the boundary *and* is cheap to move — is
   **small and contained**: the **conv** table, the **collection/array factories**, and the **specific-type**
   recognitions (`Pair`/`Triple`/`IndexedValue`/`to`). That is **~14 sites**, the whole high-ROI migration.

So: no giant hidden pile of *cheap* wins, but also **not** a large pile of "legitimately must-stay" code — the
operator/conv/range bucket is *misplaced-but-expensive*, and honesty requires marking it that way (relocatable,
deferred) rather than "must-stay." The genuinely-unavoidable frontend residue (a literal with no relocatable
synthesis and no IR origin) is **near-zero**.

---

## Part 1 — BirMappings.kt (15 tables)

| # | Table | Maps | Consumer site(s) | Category | Verdict |
|---|---|---|---|---|---|
| 1 | `BINARY` | op-name→IL symbol (`plus`→`+`) | 4195 binOp | recognition (operator) | **(c) keep** — primitive IL op; CLR primitives have no Kotlin member ops |
| 2 | `UNARY` | op-name→IL symbol | 4226 unaryOp | recognition (operator) | **(c) keep** |
| 3 | `PRIMITIVE_ARRAY_ELEM` | `kotlin.IntArray`→`kotlin.Int` | 4610 `isArrayType`, 4615 `arrayElemType` | recognition (array shape) | **(c) keep** — "IntArray is a CLR array" is a frontend representation fact; elem FQN is IR identity |
| 4 | `ARRAY_FACTORY_NAMES` | `arrayOf`,`intArrayOf`,… | 3545 | recognition (factory) | **(a/b) MIGRATE** — see §Factories |
| 5 | `LIST_FACTORIES` | `listOf`/`mutableListOf`/… FQNs | 3502 | recognition (factory) | **(a/b) MIGRATE** |
| 6 | `SET_FACTORIES` | `setOf`/… FQNs | 3502/3506 | recognition (factory) | **(a/b) MIGRATE** |
| 7 | `MAP_FACTORIES` | `mapOf`/… FQNs | 3510 | recognition (factory) | **(a/b) MIGRATE** |
| 8 | `COLLECTION_OPS` | 60+ op names (`map`,`filter`,…) | **NONE (comments only)** | **DEAD** | ✅ **DELETED (#52 Phase 0)** — no live consumer |
| 9 | `ARRAY_CLASS_ELEM` | array class→elem for sized ctor | Expressions.kt:150 (`IntArray(n){}`→newarr) | recognition (array shape) | **(c) keep** — CLR array allocation shape |
| 10 | `INT_PROGRESSION_FQ` | `IntRange`,`IntProgression` | 1868/1881/1893, Statements | recognition (range) | **(c) keep loop-shape**, but push CLR accessor names down (see §Ranges) |
| 11 | `ENUM_REIFIED_INTRINSICS` | `enumValues`/`enumValueOf`/… FQNs | 3620 | recognition (enum intrinsic) | **(c) keep** — reified-generic + enum-ness is a language fact |
| 12 | `NUMBER_CONV` | `toInt`→`kotlin.Int` | ~~4231~~ | **recognition (conv)** | ✅ **MIGRATED (#52 Phase 1)** — `@ClrConv` on the ref.dll; deleted |
| 13 | `NUMERIC_FQ` | numeric receiver types | ~~4233~~ | recognition (support for conv) | ✅ **MIGRATED (#52 Phase 1)** — deleted with #12 |
| 14 | `PRIMITIVE_EQ_FQ` | primitive value types | isPrimitiveEqType, nullableElem, safeCastValue, 3270, 4629/4638/4673 | recognition (primitive value-type) | **(c) keep** — "these Kotlin types are CLR value primitives / `Nullable<T>`" is a representation fact |
| 15 | `PRIMITIVE_OP_FQ` | `PRIMITIVE_EQ_FQ` + unsigned | 4126, 4193 (operator gate) | recognition (primitive op gate) | **(c) keep** — gates #1/#2 |

Plus `SCOPE_FUNCTIONS` (defined in BirEmitter.kt:500, not BirMappings): `let/run/with/apply/also` FQNs →
3485/3486. Recognition (scope fn). **(c) keep** as frontend inline (see §Scope).

---

## Part 2 — (A) input recognition sites

### 2.1 HIGH-VALUE, genuinely migratable (the real residual — ~14 sites)

| Site | Recognizes | Synthesizes (lowering) | Mechanism | Cost |
|---|---|---|---|---|
| ~~**Conv** 4231–4234~~ ✅ DONE (#52) | `NUMBER_CONV[name]` on `NUMERIC_FQ` receiver | `{k:conv,to:kotlin.X}` | **(a)** `@kotlin.clr.ClrConv` marker on each primitive's conversion members; bir2cir reads it off the ref.dll (`MemberBinding.Conv`/`ConvTo`) → conv node from the callee's return type. **kotc emits the plain call.** | DONE |
| **List/Set factory** 3502–3508 | `in LIST_FACTORIES`/`SET_FACTORIES` | `{k:newList/newSet}` | **(a)** `@ClrCollectionFactory(kind)` marker → bir2cir; or **(b)** FQN set in bir2cir; or **run the real stdlib body** and drop the intercept | contained |
| **Map factory** 3510–3535 | `in MAP_FACTORIES` | `{k:newMap}` (splits `a to b` literals) | **(a/b)** as above. **Risk:** the `mapOf(pair)` vs `mapOf(a to b)` split must move intact — do NOT force-split an arbitrary Pair | contained |
| **Array factory** 3545–3553 | `name in ARRAY_FACTORY_NAMES`, pkg `kotlin` | `{k:newArray}` | **(a/b)** `@ClrArrayFactory` marker or bir2cir FQN; allocation realization, not frontend semantics | contained |
| **arrayOfNulls** 3539–3543 | `name == "arrayOfNulls"`, pkg `kotlin` | `{k:newArraySized}` | **(a/b)** same bucket as array factories | contained |
| **`kotlin.to`** 3658–3662 | `calleeFq == "kotlin.to"` | `new kotlin.Pair` | **(a)** drop intercept; let the real stdlib `to` body build `Pair` (it already exists), or `@ClrIntrinsic`-style. | contained |
| **Pair/Triple/IndexedValue components** 3663–3673 | `declaringClass in setOf("kotlin.Pair","kotlin.Triple","kotlin.collections.IndexedValue")` + `componentN` | field read (`first`/`second`/`third`/`index`/`value`) | **(a)** these are real stdlib types with real properties — emit the plain `componentN()` call; `@ClrProperty`/real body resolves it | contained |

**Group verdict:** these 8 rows are the migration the user wants. They are all *"kotc decides the CLR shape of
a specific stdlib symbol"*. Shared mechanism: **run the real stdlib body OR add a `@Clr*` factory/conv marker
bir2cir reads off the ref.dll.** Total churn is small and self-contained.

### 2.2 INTEROP-legitimate (.NET space / injection metadata — KEEP, per the boundary)

These read the **.NET space** (facadegen injection metadata / the `kotlin.clr.*` interop fictions). The
boundary rule says the .NET space is exactly what kotc *may* read. **All (c) keep.**

| Site | Reads | Why legit |
|---|---|---|
| `clrName` 4483–4534 | facadegen injection meta (`clrInjectedMemberName`/`clrInjectedDotNetName`/`clrInjectedTopLevelFileClass`) | the sanctioned .NET-space read; **no `@ClrIntrinsic`/`@ClrTypeAlias` read remains** (already migrated) |
| `java.util.Comparator`→`kotlin.Comparator` 4488 | JVM-jar typealias leak | frontend jar artifact fix; not CLR knowledge |
| `kotlin.clr.byref` 4538 | byref marker intrinsic | user interop fiction; bir2cir does the actual byref binding |
| `kotlin.clr.ClrRefArgument` 4545 | stdlib byref-arg marker | kotc only *shapes* addressably; bir2cir decides the byref call |
| `kotlin.clr.stackBuffer` 3304 | stack-alloc intrinsic | genuine compiler intrinsic (no BCL equivalent) |
| `kotlin.clr.ClrEvent` 1155/3313 | .NET event fiction | kotc shapes the receiver; bir2cir binds add/remove |
| `kotlin.clr.ClrRef`/`Span` 4856/4859 | byref/span interop | representation fictions |
| `@ClrField` 980, `@ClrAwait` 1543, `@Volatile` 990 | annotation flags | genuine frontend annotation facts (field modifier / await marker) |
| top-level file-facade call 4334–4368 (`clrStatic`) | injection meta | the sanctioned round-trip .NET static-call path |

### 2.3 Frontend LOWERINGS — relocatable-but-deferred (NOT "must-stay")

**Honest reframe (2026-07-07 principle):** these are *lowerings*, not pure frontend facts. Each recognizes a
stdlib symbol and synthesizes a node absent from the IR (the IR holds a faithful member call). Under the
faithful-transcriber rule their **principled home is bir2cir** — kotc should emit the faithful `callInstance`
and let bir2cir realize the CLR form. The counter-argument ("primitive IL ops / control-flow are frontend
facts") is the *pragmatic* one CLAUDE.md currently follows; it is a **cost/sequencing** call, not a boundary
truth. They are listed here as **DEFERRED (relocatable, high churn, poor risk/reward)**, with the two genuine
exceptions flagged. The one narrow "truly must-stay" residue — a synthesis with no relocatable target AND no IR
origin (kotc's own `<>dotkt_*` closure/delegate/KProperty synthetic types) — is called out at the end.

| Site | Recognizes | Lowering | Why (c) keep |
|---|---|---|---|
| Operators 4195/4226/4228/4229 | `BINARY`/`UNARY`/`inc`/`dec` on `PRIMITIVE_OP_FQ` | `binOp`/`unaryOp` | primitive IL op; CLR primitives have no member operators |
| `EQEQ`/`EQEQEQ` 4169–4182 | `name=="EQEQ"/"EQEQEQ"` | `binOp ==`/`objEq` | structural-vs-identity equality is a language rule |
| `ieee754equals` 4275 | `name` | `binOp ==` | IEEE compare intrinsic |
| Scope fns 3485–3489 | `SCOPE_FUNCTIONS` | inline value-block | inline semantics (non-local return, `it`/`this` bind, evaluate-once) |
| `use` 3493–3497 | `kotlin.io.use`/`kotlin.use` | try/finally Dispose | inline control-flow (try/finally shape) |
| `repeat` 4301–4310 | `kotlin.repeat` | counter loop | inline control-flow |
| Preconditions 4263–4293 | `TODO`/`error`/`require`/`check`/`requireNotNull`/`checkNotNull`/`CHECK_NOT_NULL`/`noWhenBranch…` | throw / cond / passthrough | library *semantics*; **exception TYPE is emitted as the Kotlin FQN** (`kotlin.IllegalStateException`) and bir2cir aliases it — layer-correct already. Could alternatively run real inline bodies (deeper, low value) |
| Range `rangeTo`/`rangeUntil` 3560–3578 | `name` + primitive `declaringClass` | `new kotlin.ranges.*Range` (Kotlin FQN) | materializes the **Kotlin** range type; no CLR name |
| Range `contains` (`x in a..b`) 3583–3596 | `name=="contains"` on a range-call | short-circuit cond | control-flow lowering |
| Range for-loop 1868–1905 | `IntRange`/`IntProgression`/`Sequence` | `forRange`/counter loop/`forEachInline` | **control-flow shape** (counted vs iterator); the CLR accessor names (`get_first`…) are the one leak — see §Ranges |
| Enum `values`/`valueOf`/`name`/`ordinal`/`entries` 3599–3653 | `ENUM_CLASS` kind + name | `enumValues`/`enumParse`/`objMethod`/`enumOrdinal` | **enum-ness is a class-kind language fact**, not stdlib metadata |
| Reified enum intrinsics 3620–3637 | `ENUM_REIFIED_INTRINSICS` | same enum nodes | reified generics + enum-ness = language |
| `Function.invoke` 3423/3683 | `name=="invoke"` on `kotlin.Function*`/`KFunction*` | splice / `delegateInvoke` | delegate/closure lowering (a kept CLR-primitive family per CLAUDE.md) |
| `code`/`name`/`ordinal` on Char/enum 3639–3653 | property name + receiver kind | `conv`/`objMethod`/field | primitive/enum representation |
| Char arithmetic result typing 4219–4223 | `leftChar` + return FQN | `conv` | Kotlin's Char op return-typing rule |
| `String.plus` concat 4162 | `name=="plus"` + `kotlin.String` | `concat` | string-concat is a language op |
| `compareTo` Double/Float total order 3443–3461, enum 3447–3452 | receiver type/kind | stdlib helper call / `binOp` | Kotlin total-order semantics (differs from `System.Double.CompareTo`) — routes to a stdlib helper, layer-ok |
| Array `get`/`set` 3695–3701 | `isOperator` + `isArrayType` | `arrayGet`/`arraySet` | CLR array indexing is a primitive IL op |
| `Delegates.observable/vetoable/notNull` 3432–3438 | `declaringClass=="kotlin.properties.Delegates"` + name | synth delegate class | property-delegation protocol (frontend); **(a) migratable only if stdlib ships real delegate impls** — deferred, medium risk |
| Collection-default bridges 3376–3409 (`iterator`/`isEmpty`/`contains`/`listIterator`) | name + `clrName(declaringClass)!=null` + `kotlin.collections` | callStatic into `ClrIteratorBridgeKt`/`ClrCollectionDefaultsKt` | **substitute-mode only** (`clrName` non-null); bridges the BCL `IEnumerable` gap. Kotlin-semantic; borderline — could be stdlib default bodies (see §Bridges) |
| collection toString/equals routing 4137/4146/4178/4243, `collToStringRoute`, `floatTotalEqRoute`, `collEqRoute` | static collection/float type | callStatic into `ClrCollectionDefaultsKt`/`ClrMapDefaultsKt`/`NumbersKt` | Kotlin structural-equality / Kotlin-style `[a, b]` toString — a language semantic routed to a stdlib helper |

---

## Part 3 — (B) hardcoded FQN literals + (C) their synthesis

`~95` `fqnJson("kotlin.X")` occurrences, `~14` distinct FQNs. Grouped by role:

### 3.1 Const-node type tags (each is the OUTPUT of a synthesis — rank by that synthesis)

Every one of these is a type tag on a const/branch node **kotc fabricated** — so each traces to a parent
synthesis (rank #1 relocatable) unless it is transcribing a genuine IR const (rank #2 IR-derivable fallback).

| FQN | ~count | The synthesis it tags | Primary verdict |
|---|---|---|---|
| `kotlin.Unit` | 22 | mostly `{const:kotlin.Unit,value:null}` fabricated as a **void/no-value placeholder** for a synthesized `return`/`cond`/valueBlock (§2.3 preconditions, scope, use) | **#1 relocate** — the placeholder exists only because the *surrounding* node was synthesized; it disappears when that lowering moves. Where it truly tags a real Unit return → **#2 (d)** `irBuiltIns.unitType` |
| `kotlin.String` | 21 | type of a **string const kotc invents**: KPropertyImpl name arg (355/4055/4075/Expr:255 — KProperty synthesis), exception messages (preconditions), enum `valueOf` param (849 — enum synthesis) | **#1 relocate** with its parent synthesis (KProperty/precondition/enum). Only the genuinely-transcribed literal is **#2 (d)** |
| `kotlin.Boolean` | 4 | fabricated bool const: `else false` in range-`contains` (3592), CFG brIf (336/338) | **#1 relocate** with the range/CFG synthesis; brIf tags are CFG-lowering internal |
| `kotlin.Any` | 2 | `Any` fallback tag (312/434) | **#2 (d)** `irBuiltIns.anyType` — genuine fallback, flag "revisit" |

These are **not** independent hygiene items — they are the *tail* of the §2.3 / §3.2 synthesis units. Fixing
them in isolation (irBuiltIns) is the half-measure; the real fix is relocating the parent lowering.

### 3.2 (C) synthesis units — literal is the OUTPUT of an (A) recognition (fix as one unit)

| Literal site | FQN | Part of which (A) unit | Fix = migrate the whole unit |
|---|---|---|---|
| 4228/4229 `const 1` | `kotlin.Int` | inc/dec operator lowering (§2.3 operators) | (c) keep — but (d) IR-derive the `1` const's type from `irBuiltIns.intType` |
| 3573 `const 1` | `kotlin.Int`/`kotlin.Long` | `rangeUntil` end-1 (§2.3 range) | (c) keep; (d) IR-derive |
| 3406 index `0` | `kotlin.Int` | `listIterator()` default index (§2.3 bridges) | migrates with the bridge |
| 3641 conv target | `kotlin.Int` | `Char.code` (§2.3) | (c) keep; (d) IR-derive |
| 4221/4222 conv target | `kotlin.Int`/`kotlin.Char` | Char-arith result typing (§2.3) | (c) keep; (d) derive from `callee.returnType` (already have it) |
| 4270/4272/4274/4284/4264/4268/132/337/847 exc types | `kotlin.IllegalStateException` / `IllegalArgumentException` / `NotImplementedError` / `UnsupportedOperationException` | precondition helpers (§2.3) | (c) keep — **the Kotlin FQN is the correct identity** (bir2cir @ClrTypeAlias-aliases it); literal is fine, or resolve the symbol via `irBuiltIns`/context |
| 1885/1895 `get_first`… owner | `kotlin.ranges.IntProgression` | range for-loop (§2.3 / §Ranges) | (c) loop-shape keep; **the accessor owner+names should move to bir2cir/ilemit** |

### 3.3 (C) runtime-helper owners (kotc names a specific stdlib helper class)

kotc synthesizes `callStatic` into a **named stdlib runtime helper** by hardcoded FQN. These encode a
**Kotlin-semantic → stdlib-helper** routing:

| Owner literal | Sites | What it routes | Note |
|---|---|---|---|
| `kotlin.collections.ClrIteratorBridgeKt` | 2262, 3382 | `iterator()` → GetEnumerator bridge | substitute-mode bridge (§Bridges) |
| `kotlin.collections.ClrCollectionDefaultsKt` | 3397, 3407, 4717, 4774/4775 | isEmpty/contains/indexOf/subList/listIterator + coll toString/struct-eq | substitute-mode + Kotlin-semantic routing |
| `kotlin.collections.ClrMapDefaultsKt` | 4713, 4773 | map toString / struct-eq | Kotlin-semantic routing |
| `kotlin.NumbersKt` | 3460, 4741 | Double/Float total-order compare/equals | Kotlin total-order semantic |
| `kotlin.LibraryKt` | 4791 | (helper) | check at fix time |

**Verdict:** these are the *grayest* area. They are Kotlin *semantics* (structural equality, Kotlin-style
`toString`, the enumerator-protocol bridge), so by the letter they belong to kotc/frontend — **but** they name
a concrete stdlib helper, which is the same smell. The cleanest end-state is that these become **plain member
calls that resolve to real stdlib default bodies** (e.g. `AbstractList.toString`/`.equals` already exist),
letting kotc emit `x.toString()`/`x == y` plainly. **Medium effort, medium value; sequence AFTER §2.1.**

### 3.4 Synthetic-type / interop literals (KEEP)

`<>dotkt_KProperty*`, `<>dotkt_CharSequence`, `kotlin.clr.ClrEvent`, `kotlin.AutoCloseable` — compiler-internal
synthetic type names / interop fictions. Not stdlib recognition. (c) keep.

---

## Part 4 — phased migration plan

Ordered high-value-contained → gray → defer. Each phase is independently gate-checkable (`verify-il.sh`).

### Phase 0 — dead-code (do with / like #40) — ✅ DONE (#52)
- **Delete `COLLECTION_OPS`** (BirMappings.kt) — no live consumer, comments only. **DONE**: table removed;
  the two remaining `BirEmitter.kt` comment mentions were reworded (no dangling symbol reference).

### Phase 1 — the numeric conversion (highest value, most contained) — ✅ DONE (#52)
- Migrated `NUMBER_CONV`+`NUMERIC_FQ` (§2.1 Conv) via mechanism **(a)** — the **metadata-driven pattern**
  Phases 2–3 reuse:
  - **stdlib**: new marker `@kotlin.clr.ClrConv` (no argument), added in
    `libraries/stdlib/clr/kotlin/clr/ClrIntrinsic.kt`, annotates the 7 numeric conversions
    (`toByte`/`toShort`/`toInt`/`toLong`/`toFloat`/`toDouble`/`toChar`) on each signed primitive
    (Byte/Short/Int/Long/Float/Double in `clr/builtins/Primitives.kt`, Char in `clr/builtins/Char.kt`) — 49
    members. The conv TARGET is the function's OWN return type, so the marker carries no argument.
  - **bir2cir**: reads `@ClrConv` off the ref.dll into `MemberBinding.Conv`/`ConvTo` (`ConvTo` =
    `TypeName(method.ReturnType)`, the pre-lowering Kotlin FQN e.g. `kotlin.Long`); `MemberCallSubstitution`
    emits `{k:conv, to:<ConvTo>, e:<recv>}` for a `@ClrConv` member call (new `TryMemberConv` lookup, handled
    before Rule 0/2/3). `IsRule3Member` now also excludes `Conv` members (their throwing TODO body is a bound
    stub, not hoisted). Runs before `BirTypeLowering`, so the `kotlin.Long` target lowers to `System.Int64`
    and ilemit picks the conv opcode — byte-identical to the former kotc output.
  - **kotc**: deleted the conv recognition (`NUMBER_CONV[name]?.let{…}` → conv) + the `NUMBER_CONV` /
    `NUMERIC_FQ` tables. kotc now emits the plain `callInstance kotlin.Double.toInt` (faithful IR). The
    `fqnJson("kotlin.Int")`/`fqnJson("kotlin.Long")` literals at the conv site are GONE from kotc.
- **Subtlety discovered (marker needs an emitted method):** the signed primitives (Byte/Short/Int/Long/
  Float/Double) declared their conversion members BODYLESS (`public actual override fun toInt(): Int`) — and
  kotc emits NO method for a bodyless primitive-builtin member, so `@ClrConv` had no method to ride into the
  ref.dll (bir2cir's routing then MISSED `kotlin.Int.toInt`). Fix (stdlib-side, per the cardinal rule; mirrors
  how `Char.kt` already declared them): give the 42 primitive conversions a `= TODO("clr binding")` body so
  kotc emits the member and the attribute survives. The body is never called (the call is rewritten to `conv`;
  `IsRule3Member` excludes `Conv` members so the throwing body is not hoisted either).
- **Risk covered:** `Char` (`c.toInt()` → `kotlin.Int`, `i.toChar()` → `kotlin.Char`, byte/long overflow wrap,
  float→int truncation — all correct); `Number`-typed receivers are intentionally NOT `@ClrConv` (matching the
  old `NUMERIC_FQ` scope = the concrete primitives only). **Boxed/nullable receiver** (`n!!.toLong()` on
  `Int? = 5`) is byte-identical to pre-#52 but produces a wrong value — this is a PRE-EXISTING bug in the
  `Nullable<value-type>` `!!`-unwrap path (`println(n!!)` alone, with NO conversion, already throws
  `InvalidProgramException`), orthogonal to conv recognition; NOT introduced by this migration. Verified:
  verify-il 242/0, ktproj/differential(194/0)/roundtrip green, schema 0 violations.

### Phase 2 — the factories (shared mechanism)
- Migrate `LIST_FACTORIES`/`SET_FACTORIES`/`MAP_FACTORIES`/`ARRAY_FACTORY_NAMES`/`arrayOfNulls`
  (§2.1). Mechanism **(a)** `@ClrCollectionFactory(kind)`/`@ClrArrayFactory` markers bir2cir reads, **or**
  simply run the real stdlib factory bodies and delete the intercepts.
- **Risk:** the `mapOf(a to b)` literal-split must move to bir2cir intact; keep the "don't split an arbitrary
  Pair" guard. Contained.

### Phase 3 — the specific stdlib types
- `kotlin.to` (§2.1): drop the intercept; the real stdlib `to` builds `Pair`.
- `Pair`/`Triple`/`IndexedValue` `componentN` (§2.1): emit plain calls; `@ClrProperty`/real bodies resolve.
- **Risk:** low — these are real emitted stdlib types with real members.

### Phase 4 (gray, medium) — the stdlib-helper routings (§3.3)
- Make `collToString`/`collEq`/`floatTotalEq`/iterator-bridge/coll-defaults resolve to **real stdlib default
  bodies** (`AbstractList.toString`/`.equals`, an `IEnumerable`→Iterator adapter in the stdlib) so kotc emits
  plain `x.toString()`/`x == y`/`for`. Removes the `ClrCollectionDefaultsKt`/`ClrMapDefaultsKt`/`NumbersKt`/
  `ClrIteratorBridgeKt`/`LibraryKt` literal owners.
- **Risk:** medium — these were added to paper over BCL-collection gaps; needs the stdlib default bodies to
  actually run under substitution. Sequence AFTER Phases 1–3.

### Phase 5 — the operator / conv / range-desugar bucket (relocate to bir2cir; DEFERRED for cost)
- **Principled target, per the faithful-transcriber rule:** kotc emits the faithful `callInstance`
  (`kotlin.Int.inc`, `x.compareTo(y)`, `a.plus(b)`, `x.toLong()`, the for-loop's `iterator()`/`hasNext()`/
  `next()`) and **bir2cir** realizes the CLR form (CIL `add`/`sub`/`conv`, counter-loop optimization, IEEE
  compare). This deletes `BINARY`/`UNARY`/inc/dec/`EQEQ`/`EQEQEQ`/`NUMBER_CONV`-tail, the range desugar, and
  **the bulk of the `kotlin.Int`/`Boolean`/`Unit` FQN literals** in one architectural move — driving the
  literal count toward 0.
- **Why deferred (honest):** highest churn in the codebase, and these paths are already Kotlin-clean at the
  `binOp` boundary, so the boundary-purity win is real but the risk/reward is the worst of any phase. This is a
  **cost** deferral, not a "frontend fact" — do it last, as a dedicated pass, only after Phases 1–4 prove the
  ref.dll-metadata machinery.
- **Cheap partial within this bucket:** push the **range accessor names** (`get_first`/`get_last`/`get_step`,
  IntProgression owner in the `forRange` node) down to bir2cir/ilemit — there is already a
  `TODO(refactor, per user 2026-06-28)` at BirEmitter.kt:1877 saying exactly this (blocked on a synthetic-getter
  resolution issue).

### Genuinely unavoidable (near-zero) — the only true must-stay
- kotc's **own synthetic types** with no IR origin: the `<>dotkt_*` closure/delegate/`KPropertyImpl`/
  `CharSequence`-adapter family. These are CLR-representation inventions for closures/property-references that
  CLAUDE.md keeps in the "delegate/closure family" bucket; their FQN literals (`<>dotkt_*`) are not `kotlin.*`
  and name a type kotc itself defines, so they are outside the "hardcoded `kotlin.*` literal" metric. Even these
  are "move toward the boundary when touched" per CLAUDE.md — but they are the last, smallest residue.
- **Interop reads** (`kotlin.clr.*`, facadegen injection, `@Volatile`/`@ClrField`/`@ClrAwait`): the sanctioned
  .NET-space / annotation-flag reads. Legitimate by the boundary rule (kotc *may* read the .NET space).

---

## Appendix — exact site index (for the executor)

- BirMappings.kt: tables §Part 1.
- Conv: `BirEmitter.kt:4231–4234` (+ `NUMBER_CONV`/`NUMERIC_FQ`).
- List/Set factory: `3502–3508`. Map factory: `3510–3535`. Array factory: `3545–3553`. arrayOfNulls: `3539–3543`.
- `to`: `3658–3662`. componentN: `3663–3674`.
- Range for-loop: `1866–1905` (+ `forRange` node `1881–1885`, accessor TODO `1877`). rangeTo: `3560–3579`.
  range `contains`: `3583–3596`.
- Enum: `3599–3653`. Reified enum: `3620–3637`.
- Preconditions: `4263–4293` (+ `newExc` 132/337/847). Scope: `3485–3489`. use: `3493`. repeat: `4301`.
- Operators: `4159–4235`. EQEQ/EQEQEQ: `4169–4182`. String.plus: `4162`.
- Delegates.observable/vetoable/notNull: `3432–3438`.
- Collection bridges: `iterator` `3376–3384`, defaults `3387–3399`, `listIterator` `3401–3409`, lifted
  `iterator` mref `2260–2263`.
- Kotlin-semantic helper routes: `collToStringRoute` `4701–4720`, `floatTotalEqRoute` `4736–4742`,
  `collEqRoute` `4744+`, `compareTo` Double/Float `3457–3461`.
- clrName (interop, keep): `4483–4534`.

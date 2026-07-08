# kotc recognition audit — the "kotc purity completion" (post-#37, old bundle 8)

> **READ-ONLY audit. No code changed.** Goal: kotc must emit **only IR-derived Kotlin identity + genuine
> frontend facts** — **zero** hardcoded stdlib-symbol *recognition* and **zero** hardcoded FQN *literals*.
> Everything CLR-shaped is bir2cir/ilemit's job (derived from the ref.dll `@Clr*` metadata). This doc finds
> **every** residual site in `toolchain/kotc/src/main/kotlin/kotc/backend/` and gives a per-site verdict.

Scope audited: `BirEmitter.kt` (5016 L), `BirEmitterExpressions.kt`, `BirEmitterStatements.kt`,
`BirMappings.kt`. Verified against the 2026-07-07 tree; Codex (`gpt-5.5`) consulted for the per-category
migration verdicts (its conclusions are folded in below).

> **#66 (2026-07-08) — kotc is now SUBSTITUTE-INDEPENDENT.** `BirEmitter` no longer reads
> `DOTKT_STDLIB_SUBSTITUTE` or `DOTKT_STRIP_METADATA` (both getters deleted): it emits ONE pure-Kotlin BIR
> and the stdlib REFERENCE vs RUNTIME builds get BIT-IDENTICAL `*.bir.json` (proven by `diff -rq`). The
> five substitute/strip-gated sites moved down: (1) always-emit the roundtrip-metadata attrs / accessor
> attrs / `@KotlinDefault` — the rt strip is ilemit's (`_stripMetadata`); (2) the `kotlin.Comparable`
> upper-bound drop and (3) the `in`-variance drop → **bir2cir** `StdlibSubstituteTypeParams` (rt build only,
> before `BirTypeLowering`); (4) the `for`-over-`kotlin.collections`→`forEachInline` recognition is now gated
> on `stdlibCompile` alone (ref emits it too; the ref body is squashed by `RefBodySquash`); (5) the
> `clrName` ref-build early-return became `stdlibCompile` (substitute-free, identical result). The two
> `build-stdlib-{ref,rt}.sh` are unified into `scripts/build-stdlib.sh` (ONE kotc run → shared BIR → ref+rt
> emit); the emitted dlls are byte-identical (modulo PE timestamp + MVID) between the two paths.

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

> **✅ Update (#55 §4, 2026-07-08) — the `clrMethodShape` .NET-name matcher is GONE.** Not a `fqnJson`
> literal (so outside the ~95-count metric above), but the same species of leak: kotc's
> `clrMethodShape(IrType)` emitted the ilemit overload-matcher `shapes` tokens directly — including the
> .NET SIMPLE NAMES `Int64`/`SByte`/`Single`/… — onto `clrGenericStatic`/`clrGenericInstance`. That is
> CLR knowledge in the only IR-coupled layer (bump-blocking). kotc now emits the DECLARED parameter types
> as pure-Kotlin `birType` identities in a transient `shapeTypes` array; bir2cir's `ShapeSynthesis` pass
> derives the frozen `shapes` string tokens off the `@ClrTypeAlias` index and drops `shapeTypes`.
> `clrMethodShape` + the dead `clrGen` helper are deleted (`grep clrMethodShape toolchain/kotc/src` → 0);
> byte-identical overload resolution; full gate green.

---

## Part 1 — BirMappings.kt (15 tables)

| # | Table | Maps | Consumer site(s) | Category | Verdict |
|---|---|---|---|---|---|
| 1 | `BINARY` | op-name→IL symbol (`plus`→`+`) | 4195 binOp | recognition (operator) | **(c) keep** — primitive IL op; CLR primitives have no Kotlin member ops |
| 2 | `UNARY` | op-name→IL symbol | 4226 unaryOp | recognition (operator) | **(c) keep** |
| 3 | `PRIMITIVE_ARRAY_ELEM` | `kotlin.IntArray`→`kotlin.Int` | 4610 `isArrayType`, 4615 `arrayElemType` | recognition (array shape) | **(c) keep** — "IntArray is a CLR array" is a frontend representation fact; elem FQN is IR identity |
| 4 | `ARRAY_FACTORY_NAMES` | `arrayOf`,`intArrayOf`,… | ~~3545~~ | recognition (factory) | ✅ **MIGRATED (#52 Phase 2)** — `@ClrArrayFactory` on the ref.dll; deleted |
| 5 | `LIST_FACTORIES` | `listOf`/`mutableListOf`/… FQNs | ~~3502~~ | recognition (factory) | ✅ **MIGRATED (#52 Phase 2)** — `@ClrCollectionFactory("list")`; deleted |
| 6 | `SET_FACTORIES` | `setOf`/… FQNs | ~~3502/3506~~ | recognition (factory) | ✅ **MIGRATED (#52 Phase 2)** — `@ClrCollectionFactory("set")`; deleted |
| 7 | `MAP_FACTORIES` | `mapOf`/… FQNs | ~~3510~~ | recognition (factory) | ✅ **MIGRATED (#52 Phase 2)** — `@ClrCollectionFactory("map")`; deleted |
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
| ~~**List/Set factory** 3502–3508~~ ✅ DONE (#52) | `in LIST_FACTORIES`/`SET_FACTORIES` | `{k:newList/newSet}` | **(a)** `@ClrCollectionFactory("list"/"set")` marker → bir2cir `TryFactorySubst`; kotc emits the plain call | DONE |
| ~~**Map factory** 3510–3535~~ ✅ DONE (#52) | `in MAP_FACTORIES` | `{k:newMap}` (splits `a to b` literals) | **(a)** `@ClrCollectionFactory("map")`; the `mapOf(pair)` vs `mapOf(a to b)` split moved to bir2cir intact — a NON-literal Pair is left as a plain call | DONE |
| ~~**Array factory** 3545–3553~~ ✅ DONE (#52) | `name in ARRAY_FACTORY_NAMES`, pkg `kotlin` | `{k:newArray}` | **(a)** `@ClrArrayFactory("vararg")` → bir2cir unwraps the vararg `newArray` | DONE |
| ~~**arrayOfNulls** 3539–3543~~ ✅ DONE (#52) | `name == "arrayOfNulls"`, pkg `kotlin` | `{k:newArraySized}` | **(a)** `@ClrArrayFactory("sized")` → `{k:newArraySized}` from the size arg | DONE |
| ~~**`kotlin.to`** 3658–3662~~ ✅ DONE (#52 Phase 3) | `calleeFq == "kotlin.to"` | `new kotlin.Pair` | **(a)** intercept DROPPED — the real stdlib infix `to` (body `Pair(this, that)`) resolves the plain call; **no marker needed** (a real emitted member, unlike conv/factories) | DONE |
| ~~**Pair/Triple/IndexedValue components** 3663–3673~~ ✅ DONE (#52 Phase 3) | `declaringClass in setOf("kotlin.Pair","kotlin.Triple","kotlin.collections.IndexedValue")` + `componentN` | field read (`first`/`second`/`third`/`index`/`value`) | **(a)** intercept DROPPED — these are real emitted data-class types; the materialized `component1()`/`component2()`/`component3()` operators resolve the plain call; **no marker needed** | DONE |

**Group verdict:** these 8 rows are the migration the user wanted — **all ✅ DONE (#52 Phases 1–3).** They were
all *"kotc decides the CLR shape of a specific stdlib symbol"*. Two shared mechanisms landed: (Phases 1–2) add
a `@Clr*` conv/factory marker bir2cir reads off the ref.dll and re-emits the CLR-shaped node; (Phase 3) for the
`to`/`componentN` rows — real emitted stdlib types with real members — simply **drop the kotc intercept** and
let the plain call resolve against the real surface (no marker). Total churn was small and self-contained.

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
| Range for-loop 1868–1886 | `IntRange`/`IntProgression`/`Sequence` | faithful `forRange` (range value + var + `rangeType`)/`forEachInline` | ✅ **RELOCATED TO bir2cir (#52 Phase 5 "range partial")** — kotc emits a FAITHFUL `forRange` (no CLR accessor names/owner); bir2cir `RangeForLowering` derives `get_first`/`get_last`/`get_step` + the IntProgression owner and picks stdlib-`forRange`-with-accessors vs app-counter-loop by build mode. Loop-shape recognition (`INT_PROGRESSION_FQ`) stays as a pure-Kotlin gate. Byte-identical |
| Enum `values`/`valueOf`/`name`/`ordinal`/`entries` 3599–3653 | `ENUM_CLASS` kind + name | `enumValues`/`enumParse`/`objMethod`/`enumOrdinal` | **enum-ness is a class-kind language fact**, not stdlib metadata |
| Reified enum intrinsics 3620–3637 | `ENUM_REIFIED_INTRINSICS` | same enum nodes | reified generics + enum-ness = language |
| `Function.invoke` 3423/3683 | `name=="invoke"` on `kotlin.Function*`/`KFunction*` | splice / `delegateInvoke` | delegate/closure lowering (a kept CLR-primitive family per CLAUDE.md) |
| `code`/`name`/`ordinal` on Char/enum 3639–3653 | property name + receiver kind | `conv`/`objMethod`/field | primitive/enum representation |
| Char arithmetic result typing 4219–4223 | `leftChar` + return FQN | `conv` | Kotlin's Char op return-typing rule |
| ~~`String.plus` concat 4162~~ | `name=="plus"` + `kotlin.String` | `concat` | ✅ **RELOCATED TO bir2cir (#52 Phase 5)** — kotc emits the FAITHFUL `callInstance kotlin.String.plus(a,b)` (+ the same cast-stripped `partTypes` hint the string-template path carries); `PrimitiveOperatorLowering.Lower` recognizes `ownerType==kotlin.String && method==plus && args==1` and re-emits the identical 2-part `concat`; FaithfulHintRecognition then consumes `partTypes` unchanged. Byte-identical. (Was "language op"; the corrected discipline — default RELOCATE, no "special-op" excuse — applied since `kotlin.String.plus` is a real stdlib member) |
| `compareTo` Double/Float total order 3443–3461, enum 3447–3452 | receiver type/kind | stdlib helper call / `binOp` | Kotlin total-order semantics (differs from `System.Double.CompareTo`) — routes to a stdlib helper, layer-ok |
| Array `get`/`set` 3695–3701 | `isOperator` + `isArrayType` | `arrayGet`/`arraySet` | CLR array indexing is a primitive IL op |
| ~~`Delegates.observable/vetoable/notNull`~~ | `declaringClass=="kotlin.properties.Delegates"` + name | synth delegate class | ✅ **DELETED (#57)** — the stdlib now ships the real `ObservableProperty`/`Delegates`/`NotNullVar` (emitted into `DotKt.Stdlib.dll`), so the interception + `synthDelegate` (per-`V` monomorphized delegate class) are gone. `by Delegates.observable(…)` resolves to the real stdlib `Delegates.observable`, and the delegate-access sites dispatch getValue/setValue on the **real generic `kotlin.properties.ReadWriteProperty<Any?,V>`** (mirroring `by lazy` on real `kotlin.Lazy<T>`). The `<>dotkt_RWProperty_<V>`/`<>dotkt_ROProperty_<V>` monomorphization was **fully retired** (`propIface`/`propIface0`/`propIfaceDefs` deleted; `birType` + user-delegate-class supertypes now emit the real generic interface) — it was a pre-generic-interface workaround, disproven by generic `kotlin.Lazy<T>` already working with a value-type `V`. Byte-identical output; ilverify-clean (the field type, the `Delegates.observable` value, and the dispatch owner now share one type identity). The synthetic `<>dotkt_KProperty`/`KPropertyImpl` stays (KProperty is a pure binding, no BCL equivalent) |
| ~~Collection-default bridges (`iterator`/`isEmpty`/`contains`/`listIterator`)~~ | name + `clrName(declaringClass)!=null` + `kotlin.collections` | callStatic into `ClrIteratorBridgeKt`/`ClrCollectionDefaultsKt` | ✅ **DELETED (#52 Phase 4)** — dead code; bir2cir Rule 5 owns the routing (gate was null for jar-sourced stdlib interfaces) |
| collection toString/equals routing `collToStringRoute`/`floatTotalEqRoute`/`collEqRoute`/`concatOperand`, `compareTo` Double/Float | static collection/float type | callStatic into `ClrCollectionDefaultsKt`/`ClrMapDefaultsKt`/`NumbersKt`/`LibraryKt` | ✅ **RELOCATED TO bir2cir (#52 Phase 4b)** — kotc emits the faithful op + a cast-stripped static-type hint; bir2cir (`FaithfulHintRecognition` + the extended `PrimitiveOperatorLowering` EQEQ arm) recognizes the type and reproduces the SAME helper call. The recognition moved (mechanism-(b)); the helpers stay. (Was Phase-4 GENUINE-GAP; see Part 4 Phase 4b) |

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
| ~~1885/1895 `get_first`… owner~~ | `kotlin.ranges.IntProgression` | range for-loop (§2.3) | ✅ **MOVED to bir2cir (#52 Phase 5 "range partial")** — kotc emits a faithful `forRange`; bir2cir `RangeForLowering` derives the accessor owner+names |

### 3.3 (C) runtime-helper owners (kotc names a specific stdlib helper class)

kotc synthesizes `callStatic` into a **named stdlib runtime helper** by hardcoded FQN. These encode a
**Kotlin-semantic → stdlib-helper** routing:

| Owner literal | Sites | What it routes | Verdict (#52 Phase 4) |
|---|---|---|---|
| `kotlin.collections.ClrIteratorBridgeKt` | 2262 (mref), 3330 (call) | `iterator()` → GetEnumerator bridge | ✅ **CLEAN-MIGRATE (deleted)** — dead, bir2cir Rule 5 owns it |
| `kotlin.collections.ClrCollectionDefaultsKt` (member routing) | 3345, 3355 | isEmpty/contains/containsAll/indexOf/lastIndexOf/subList/listIterator | ✅ **CLEAN-MIGRATE (deleted)** — dead, bir2cir Rule 5 owns it |
| `kotlin.collections.ClrCollectionDefaultsKt` (semantics) | 4589, 4646, 4647 | coll toString / List+Set struct-eq | **GENUINE-GAP (kept)** — raw BCL `List<T>` has no Kotlin override |
| `kotlin.collections.ClrMapDefaultsKt` | 4585, 4645 | map toString / struct-eq | **GENUINE-GAP (kept)** — raw BCL `Dictionary` has no Kotlin override |
| `kotlin.NumbersKt` | 3373, 4613 | Double/Float total-order compare/equals | **GENUINE-GAP (kept)** — differs from `System.Double.CompareTo`/`Equals` |
| `kotlin.LibraryKt` | 4663 | null-safe `Any?.toString()` (`this?.toString() ?: "null"`) string-template stringifier | **GENUINE-GAP (kept)** — CLR concat of null → `""`, Kotlin → `"null"` |

**Verdict:** the *grayest* area, resolved by SPLIT (details in Part 4 Phase 4). The **collection-member routing**
(iterator/isEmpty/contains/…) is DEAD — bir2cir Rule 5 already routes it off the ref.dll `@ClrTypeAlias`
metadata; kotc's copies were gated on `clrName(declaringClass) != null` which is null for the jar-sourced stdlib
collection interfaces. **Deleted.** The **Kotlin-semantic routings** (structural `==`, Kotlin-style `toString`,
Double/Float total order, null-safe stringify) are **GENUINE-GAP, kept**: the naive "resolve to a real
`AbstractList.toString`/`.equals` body" premise is FALSE — `listOf(…)` lowers to a **raw .NET `List<T>`**, not a
Kotlin `AbstractList`, so no default body dispatches on it; a plain `x.toString()`/`x == y` would be wrong. The
helper is the only home for those semantics.

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

### Phase 2 — the factories (shared mechanism) — ✅ DONE (#52)
- Migrated `LIST_FACTORIES`/`SET_FACTORIES`/`MAP_FACTORIES`/`ARRAY_FACTORY_NAMES`/`arrayOfNulls` (§2.1) via
  mechanism **(a)** — the metadata-driven pattern Phase 1 established:
  - **stdlib**: two new markers `@kotlin.clr.ClrCollectionFactory(kind)` (kind = "list"/"set"/"map") and
    `@kotlin.clr.ClrArrayFactory(kind)` (kind = "vararg"/"sized"). Collection factories
    (`listOf`/`mutableListOf`/`arrayListOf`/`emptyList` + set/map families, every overload incl. the
    single-element `listOf(element)`/`setOf(element)`/`mapOf(pair)`) are annotated in the COMMON sources
    (`kotlin.collections.*`); array factories (`arrayOf`/`intArrayOf`/…, unsigned `ubyteArrayOf`/…, and the
    sized `arrayOfNulls`) in `clr/builtins/Library.kt` + `unsigned/src`. **The two annotations are DEFINED in
    the common source set** (`libraries/stdlib/src/kotlin/clr/Factories.kt`), not the platform
    `clr/kotlin/clr/ClrIntrinsic.kt`, because a COMMON factory body cannot reference a PLATFORM-only
    `kotlin.clr` declaration under the jar's `-Xcommon-sources` multi-platform compile.
  - **bir2cir**: reads the markers off the ref.dll into `ReferenceMetadata.CollectionFactories`/
    `ArrayFactories` (name → kind); a new `TryFactorySubst` (run FIRST in the `owner=null` top-level branch of
    `MemberCallSubstitution.TransformCall`, near the @ClrConv `TryMemberConv`) re-emits the
    `{k:newList/newSet/newMap/newArray/newArraySized}` node. **Type source = the call's `typeArgs`** (canonical;
    correct for `emptyList()`, `arrayOf<String>()` with 0 elems, the single-element overload, and mapOf's
    `[K,V]`); **elements** from the single vararg argument (kotc emits it as a `newArray`; the wrapper is
    identified by its `elem` matching `typeArgs[0]`, so a `listOf(intArrayOf(…))` single element is not
    mis-unwrapped), the lone non-vararg element, or none.
  - **kotc**: emits the plain top-level factory `callStatic` (faithful IR); the four recognition tables +
    their `BirEmitter.kt` sites (3502–3556) are deleted.
- **Risk covered:** the `mapOf(a to b)` literal-split moved to bir2cir INTACT with its guard — each vararg
  element must be a `new kotlin.Pair(k,v)` LITERAL node to be split into a key/value entry; a NON-literal Pair
  (`mapOf(pairVariable)`, `mapOf(this[0])`) aborts the whole substitution → the call stays a plain `mapOf` to
  the real body (never force-split). Verified: verify-il 242/0, ktproj/differential/roundtrip green, schema 0
  violations.

### Phase 3 — the specific stdlib types — ✅ DONE (#52)
- `kotlin.to` (§2.1): intercept DROPPED — kotc emits the plain `to` call; the real stdlib infix `to`
  (`= Pair(this, that)`) resolves it.
- `Pair`/`Triple`/`IndexedValue` `componentN` (§2.1): intercept DROPPED — kotc emits the plain
  `component1()`/`component2()`/`component3()` call; the materialized data-class `componentN()` operators
  (already emitted onto the stdlib surface) resolve it.
- **No marker, no stdlib change.** Unlike conv/factories (which synthesize CLR-shaped nodes and so needed a
  ref.dll marker + a bir2cir re-emit), these are real emitted types with real members — dropping the two kotc
  intercepts is the core migration. The explicit `.first`/`.second`/`.third`/`.index`/`.value` **property** read
  (3960–3976) is a SEPARATE site and stays a direct field read (out of scope).
- **One coupled bir2cir follow-on (mapOf-split).** Phase 2's `mapOf(a to b)` literal-split matched only a
  `new kotlin.Pair` node — the shape kotc emitted for `to`. With `to` now a plain call, the split (`PairKV` in
  `TryFactorySubst`) also decomposes a `callStatic .to(k,v)` element (body `Pair(this, that)`). Required for
  correctness, not just optimization: the real `mapOf` body builds a `Pair<K,V>[]` vararg array that
  `ArrayTypeMismatch`-crashes under reified generics when elements are more-specific (`Pair<String,String>`
  into `Pair<String,Any>[]`) — the split sidesteps the array. `mapOf(pairVar)` still aborts to the real body
  (the homogeneous single-element case that does not hit the mismatch).
- **Verified:** verify-il 242/0, ktproj/differential/roundtrip green, schema 0 violations; destructuring
  (`val (a, b) = pair`, `val (k, v) = mapEntry`, `val (i, v) = list.withIndex().first()`, `val (a, b, c) =
  triple`), explicit `t.component1()`, and `"x" to 1` all correct.

### Phase 4 (gray, medium) — the stdlib-helper routings (§3.3) — ✅ DONE (#52), PARTIAL by design
Investigated each §3.3 helper-owner site. The gray area splits cleanly into **DEAD-DUPLICATE** (bir2cir already
owns the routing — delete) and **GENUINE-GAP** (Kotlin semantics a plain op cannot resolve — keep + document).
The naive premise of the original plan — "make them resolve to a real `AbstractList.toString`/`.equals` body" —
is **false**: `listOf(…)` lowers (via bir2cir factory substitution → `newList`) to a **raw .NET `List<T>`**, NOT
an instance of Kotlin's `AbstractList`. So `AbstractCollection.toString`/`AbstractList.equals` never dispatch on
the runtime object — `System.Object.ToString` / reference-`Equals` run instead. A plain `x.toString()`/`x == y`
would therefore be WRONG. The helper is the only home for those Kotlin semantics.

**CLEAN-MIGRATE (deleted from kotc):**
- **Iterator bridge** — `ClrIteratorBridgeKt.iteratorOverEnumerable`, sites 2262 (lifted `Iterable::iterator`
  mref special-case) + 3330 (call-site `iterator()`). **Dead** — superseded by bir2cir Rule 5
  (`Program.cs` ~4993) + the emitted-collection self-call path (~4828). kotc's gate `clrName(declaringClass)
  != null` is null for the jar-sourced stdlib collection interfaces. Deleted → the `ClrIteratorBridgeKt` owner
  literal is GONE from kotc entirely.
- **Collection defaults** — `ClrCollectionDefaultsKt.{clrCollIsEmpty,clrCollContains,clrCollContainsAll,
  clrListIndexOf,clrListLastIndexOf,clrListSubList,clrListListIterator}`, sites 3345 (`isEmpty`/`contains`/…)
  + 3355 (`listIterator`). **Dead** — superseded by bir2cir Rule 5 (`CollectionDefaults` map + the
  `listIterator` branch). Deleted → the `clrListListIterator`/collection-default uses of `ClrCollectionDefaultsKt`
  are gone (the literal survives only in the toString/struct-equals GENUINE-GAP sites below).

**GENUINE-GAP (kept in kotc, documented BCL gap):** these are Kotlin **semantics** applied off the operand's
static type; the substituted runtime object (raw BCL collection / CLR primitive) has no Kotlin override, so no
real default body can resolve them. They correctly live in the frontend and route to a stdlib helper because the
BCL type offers no such method. (A future move to bir2cir would be mechanism-(b) recognition-relocation — same
hardcode one layer down, NOT a "real body resolves it" win — so out of scope for this phase, which migrates only
the clean/dead set.)
- **Collection/Map Kotlin-style `toString`** — `ClrCollectionDefaultsKt.clrCollToString` (`[a, b]`),
  `ClrMapDefaultsKt.clrMapToString` (`{k=v}`), sites 4589/4585. The BCL `List<T>`/`Dictionary<K,V>` `ToString`
  yields the raw .NET type name; Kotlin contracts `[a, b]`/`{k=v}`. `collToStringRoute` also feeds the
  string-template / `+`-concat / explicit-`toString()` paths.
- **Structural `==` on List/Set/Map** — `ClrCollectionDefaultsKt.{clrCollStructEquals,clrSetStructEquals}` +
  `ClrMapDefaultsKt.clrMapStructEquals`, sites 4645–4647. Kotlin `==` on collections is structural (element/
  entrywise); the substituted BCL collection's `Object.Equals` is REFERENCE identity. `collEqRoute` matches the
  collection KIND off both operands (`listOf(1) == setOf(1)` stays false).
- **`Double`/`Float` total-order** — `NumbersKt.{clrDoubleCompare,clrFloatCompare,clrDoubleEquals,clrFloatEquals}`,
  sites 3373 (direct `compareTo`) + 4613 (boxed `==`). Kotlin's total order (`-0.0 < 0.0`, `NaN` largest,
  `NaN.compareTo(NaN) == 0`, `NaN == NaN`) differs from `System.Double.CompareTo`/`Equals` — and bir2cir's
  Rule 1c would route a plain primitive `compareTo` to the WRONG `System.Double.CompareTo`. Direct `<`/`>`
  keep the fast IEEE intrinsics (unaffected).
- **Null-safe `Any?.toString()` stringifier** — `LibraryKt.toString` (`this?.toString() ?: "null"`), site 4663.
  A nullable string-template / concat operand must render a null as the string `"null"`; a bare CLR
  `String.Concat`/`Append` of a null reference yields `""`. Pure Kotlin-language rendering rule.

**Verified:** verify-il 242/0, ktproj/differential/roundtrip green, schema 0 violations; byte-identical behavior
on the collection/map struct-equality + toString + Double/Float total-order + iterator samples.

### Phase 4b — relocate the GENUINE-GAP routings to bir2cir (NOT the collection-MODEL rework) — ✅ DONE

**User decision (2026-07-08):** do NOT do the collection-MODEL rework (make `listOf` produce a Kotlin
`AbstractCollection`-derived type so real `toString`/`equals` bodies dispatch — the cardinal-rule-purest fix, but
it pays a "weird cost": a dual Kotlin-type + BCL-interface collection re-implementation). Instead **bir2cir OWNS
the `kotlin.collections.List` CLR realization** — including routing `toString`/structural-`==`/total-order to the
helpers. This is a **relocation** (kotc→bir2cir), the user-established valid resolution: bir2cir is ALLOWED CLR
knowledge; the helper stays as the mechanism, only the RECOGNITION moves (mechanism-(b), §3.3). It is the natural
extension of the Phase-5 machinery — bir2cir already substitutes `List`→`IReadOnlyList` + emits `newList` and
(Phase 5) gets `EQEQ` with `argTypes`.

**Mechanism.** kotc STOPS routing and emits the FAITHFUL op plus a TRANSIENT, IR-derived, **cast-stripped
static-type hint** (faithful type transcription — NOT a helper name). bir2cir does ALL the recognition off the
hint and reproduces the EXACT SAME helper `callStatic`, then STRIPS the hint (CIR is clean; final IL
byte-identical). The hints:

| kotc faithful op | hint field(s) | bir2cir recognition → helper |
|---|---|---|
| `objMethod ToString` (explicit `x.toString()`) | `recvType` | collection recvType → `clr{Coll,Map}ToString` |
| `objMethod Equals` (explicit `x.equals(y)`) | `recvType`+`argType` | same-kind collection → struct-eq; Double/Float → `clr{Double,Float}Equals` |
| `callStatic println/print` | `argTypes` | collection arg → wrap in `clr{Coll,Map}ToString` |
| `concat` (template + `String.plus`) | `partTypes` | collection part → collToString; else nullable part → `LibraryKt.toString` |
| `callStatic EQEQ` | surface `argTypes` + cast-stripped `argValueTypes` | prim fast-path (argTypes) → ceq; else same-kind coll (argValueTypes) → struct-eq; else Double/Float → float-eq; else `objEq` |
| `callInstance kotlin.Double/Float.compareTo` | (owner is the hint) | → `NumbersKt.clr{Double,Float}Compare` (before the primitive `System.Double.CompareTo` routing) |

kotc's `collToStringRoute`/`floatingUnwrap`/`floatTotalEqRoute`/`collEqRoute`/`concatOperand` and the `compareTo`
Double/Float special-case are DELETED; kotc names ZERO of the four helper FQNs. bir2cir: new file
`FaithfulHintRecognition.cs` (the ToString/Equals/println/concat/compareTo sites) + the extended EQEQ arm in
`PrimitiveOperatorLowering.cs`, both run EARLY (before `MemberCallSubstitution`/factory/`BirTypeLowering`).
**Precedence preserved exactly** (prim fast-path before float/coll; collToString before null-safe). The `objEq`
fallback and the primitive fast-path keep the ORIGINAL (un-stripped) operands — only the collection/float helper
gets the cast-stripped operand (matching kotc's former `expr(unwrapped)`); stripping the Any-box off a boxed value
operand into `Object.Equals` would be invalid IL. **After Phase 4b (+ range), kotc = ZERO CLR recognition — a
pure faithful IR→BIR transcriber.**

### Phase 5 — the operator bucket (relocate to bir2cir) — ✅ DONE

**kotc now recognizes ZERO operators.** Arithmetic / bitwise / unary / inc-dec / comparison / equality all
emit the FAITHFUL IR from kotc (a plain `callInstance kotlin.Int.plus` / a `kotlin.internal.ir` intrinsic
`callStatic`); a single new bir2cir pass — `PrimitiveOperatorLowering` (runs FIRST, unconditionally in ref +
app builds) — re-emits the identical `binOp`/`unaryOp`/`conv`/`objEq` nodes, so ilemit is UNCHANGED and every
gate stays byte-identical (verify-il 243/0, ktproj/differential 194·0/roundtrip green, schema 0 violations).
Operand VALUE-shaping (value-nullable unwrap + boxed-Any cast) stays in kotc as faithful call-operand coercion
(`recvExpr` + `argExpr`, the CLR twin of JVM's implicit `intValue()`), NOT operator recognition. The residual
kotc gates (`COMPARE` names, `name == "EQEQ"`) only IDENTIFY the intrinsic to emit faithfully (with its
package owner `kotlin.internal.ir` + — for EQEQ — the operands' `argTypes`) and run the kept Phase-4
structural-equality routings; the operator SYNTHESIS is entirely bir2cir's. `String.plus` → `concat` is now
**also relocated** (see "String.plus" below) — kotc recognizes ZERO operators, member operators included.



**Class 1 (arithmetic + bitwise + unary MEMBER operators) — ✅ DONE.** kotc's `BINARY` (arithmetic
`plus`/`minus`/`times`/`div`/`rem` + bitwise `and`/`or`/`xor`/`shl`/`shr`/`ushr`) and `UNARY`
(`unaryMinus`/`unaryPlus`/`not`/`inv`) recognition is REMOVED: kotc emits the faithful `callInstance
kotlin.Int.plus` / `callInstance kotlin.Char.unaryMinus`. A new bir2cir pass `PrimitiveOperatorLowering`
(runs FIRST, unconditionally in ref + app builds) re-emits the identical `binOp`/`unaryOp` — and the
Char-arith `conv` (Int/Char), derived from the member + the `sig`'s arg type. Operand shaping stays in
kotc as faithful VALUE coercion of the call's recv/args (the receiver-slot twin `recvExpr` + `argExpr`'s
boxed-Any cast — the CLR twin of JVM's implicit `intValue()`), NOT operator recognition. `BINARY`→`COMPARE`
(comparison-only, pending class 3); `UNARY` deleted. Byte-identical: verify-il 243/0, ktproj/differential
(194/0)/roundtrip green, schema 0 violations. **Key finding:** the pass MUST run in the reference build
too — a ref-build ctor field-init / base-arg is not body-squashed, so a surviving `callInstance
kotlin.Int.inv` (bodyless builtin, no ref.dll symbol) would reach ilemit as an unresolvable method call;
the OLD kotc emitted `unaryOp` (a raw IL op, no method lookup) in every build.

**Class 2 (inc/dec) — ✅ DONE.** kotc's `inc`/`dec` lowering (the `i++`/`i--` desugaring) is REMOVED: kotc
emits the faithful `callInstance kotlin.Int.inc()` (0-arg member, receiver value-shaped by recvExpr) and
`PrimitiveOperatorLowering` re-emits `(recv + 1)`/`(recv - 1)` — the `const 1:kotlin.Int` literal (typed
`kotlin.Int` for every primitive, matching the retired kotc literal) moved to bir2cir with it. Byte-identical:
verify-il 243/0, all gates green.

**Class 3 (comparison intrinsics) — ✅ DONE.** kotc's `COMPARE` (`less`/`lessOrEqual`/`greater`/
`greaterOrEqual` — the `<`/`<=`/`>`/`>=` `kotlin.internal.ir` COMPILER INTRINSICS) lowering is REMOVED: kotc
emits the intrinsic FAITHFULLY as a `callStatic owner=kotlin.internal.ir` (operands value-shaped exactly as
the retired binOp). `PrimitiveOperatorLowering` re-emits `{k:binOp, op:<}`. **Encoding note:** the owner
marker `kotlin.internal.ir` (the intrinsic's home package) makes the bir2cir match collision-safe — a user
top-level `fun less(a,b)` never carries that owner; and the transient node is rewritten to `binOp` before CIR,
so ilemit never sees the marker. Byte-identical: verify-il 243/0, all gates green.

**Range for-loop ("range partial") — ✅ DONE.** kotc's range for-loop lowering leaked CLR accessor names: the
stdlib-build `forRange` node carried `accessOwner="kotlin.ranges.IntProgression"` + `firstM`/`lastM`/`stepM` =
`get_first`/`get_last`/`get_step`, and the app-build counter loop emitted `callInstance` nodes to those getters
(the standing `TODO(refactor, per user 2026-06-28)` at BirEmitter.kt:1877). kotc now emits a FAITHFUL `forRange`
carrying ONLY the range VALUE expr, the loop var, and the range's own pure-Kotlin type (`rangeType`); a new
bir2cir pass `RangeForLowering` (runs FIRST in the per-file loop, before every other pass) DERIVES the accessor
access and dispatches by build mode: stdlib build (`DOTKT_STDLIB_COMPILE` set — IntProgression emitted locally)
keeps `forRange` and injects `accessOwner`/`get_first`/`get_last`/`get_step` (ilemit resolves off `_types`
generically); app build (IntProgression only REFERENCED) rewrites to `block{ var __rng = range; for(i =
__rng.get_first(); i <= __rng.get_last(); i += 1) { body } }` with cross-module getters. The **synthetic-getter
blocker** noted in the old TODO ("a synthetic callInstance to `get_first` doesn't resolve through ilemit's
callInstance path — KeyNotFound") is a STDLIB-BUILD-ONLY constraint (the getter is on a type being emitted in THIS
assembly); it is respected by keeping the stdlib form as the `_types`-resolved `forRange` node — the relocation
does NOT try to make ilemit resolve a same-assembly synthetic getter. The kotc gate is EXACTLY the union of the
retired branches (`stdlibCompile ? type∈INT_PROGRESSION_FQ : type==IntRange`), so routing to the remaining plain
`for` branches (const `1..5`, `rangeTo`/`until`/`downTo`) is unchanged → byte-identical. `INT_PROGRESSION_FQ` stays
as a pure-Kotlin recognition gate only. **kotc emits ZERO CLR accessor names/owner for ranges** (`grep
get_first|get_last|get_step|IntProgression toolchain/kotc/src` → gone; INT_PROGRESSION_FQ remains as the gate).

**With Phase 4b + Phase 5 (operators + range) complete, kotc = ZERO CLR recognition — a pure faithful IR→BIR
transcriber.**

**String.plus (member concat operator) — ✅ DONE.** The last operator-recognition residual. kotc's
`name=="plus"` + `kotlin.String` → `concat` recognition (BirEmitter.kt, the `String + x` site) is REMOVED: kotc
emits the FAITHFUL `callInstance kotlin.String.plus(a, b)` (a plain 2-operand member call), carrying the SAME
cast-stripped `partTypes` hint the string-template path already carries (the stripped static operand types —
List/nullable — are NOT recoverable from the declared param type `Any?`, so the hint is genuine). A new arm in
`PrimitiveOperatorLowering.Lower` recognizes `ownerType==kotlin.String && method=="plus" && args.Count==1` and
re-emits the identical 2-part `concat` node kotc used to synthesize; `FaithfulHintRecognition` (runs NEXT)
consumes `partTypes` exactly as for a template concat (collection part → clrCollToString, nullable part →
LibraryKt.toString). The STRING-TEMPLATE path (`IrStringConcatenation` → `concat`) is UNTOUCHED — that is
faithful transcription (concat IS the template's meaning), not member recognition. Byte-identical: nested `"a"
+ "b" + "c"` lowers bottom-up (inner call → concat first, then outer), and `"x" + listOf(1,2,3)` still routes
through the Phase-4b collection stringifier (verified `[1, 2, 3]`, not the raw .NET name).

_All operator classes (1–4) + the range partial + String.plus are done._

**#59 — the faithful-hint TYPE HINTS are RETIRED; bir2cir recovers operand static types itself.** The
Phase-4b/5 mechanism left kotc emitting a transient operand-static-type HINT alongside the faithful op
(`argTypes`+`argValueTypes` on `EQEQ`; `partTypes` on `String.plus`/template `concat`; `argTypes` on
`println`/`print`; `recvType`/`argType` on `objMethod ToString`/`Equals`) so bir2cir could re-derive the
collection/Double/Float/nullable split. **All of these hints are DELETED** — kotc emits ONLY the faithful op +
faithful operand nodes. **STEP-0 finding:** the smart-cast refined type is ALREADY a first-class BIR fact (a
smart-cast USE emits `{k:cast,type:<refined>,…}` on the operand; member calls carry the frontend-resolved
`ownerType`), so no new node was needed — the hints were REDUNDANT with the operand expression + a
local/param type environment. bir2cir now owns a single uniform recovery — `StaticTypeResolver.cs`
(`BirScope` local-type env, the early-pass twin of `SubstCtx.VarTypes`; `StaticType.Surface`/`.Value`) — read
by `PrimitiveOperatorLowering` (the `EQEQ` split) and `FaithfulHintRecognition` (concat/println/ToString/Equals/
compareTo). ilemit unchanged; byte-identical (all 9 helper families still fire in the stdlib CIR; verify-schema
0 violations; ilverify-clean). Two recovery subtleties (both caught by the full gate as NEW-FAILs and fixed): (1) LEXICAL SCOPING — `BirScope` records a `var` for the SUBSEQUENT siblings only, so two sibling `for ((k,v) in ...)` loops with a `v` of different element type (`il-groupby2`: List<Int> then List<String>) do not collide into one flat last-wins dict (the collision `clrCollToString<String>`-ed an Int list -> InvalidCast); (2) RET-LESS CALL RESOLUTION — a call whose node lacks a `ret` (kotc emits `ret` only for a GENERIC call, via `retHint`) is resolved from the ref.dll: `MemberBinding` carries the callee's structured return `TypeNode` (`TypeNodeOf`), and `StaticType` resolves a `callStatic owner=null` / member / field read via `TryTopLevelReturn`/`TryMemberReturn` (`il-cwindowed`: `"abcd".windowed(2)` returns `List<String>`, not on the node, now recovered -> `[ab, bc, cd]`). A this-assembly raw `field`/`lateinitGet`/`staticField` read resolves the same way (the property getter's declared return type). **COMPARE** carries NO type hint (only operand value-shaping, which STAYS in kotc as faithful call-operand coercion); its recognition already flows via the `kotlin.internal.ir` owner marker, so nothing hint-like moved for it.

**Original plan (for the full bucket):**
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
- **Cheap partial within this bucket — ✅ DONE:** the **range accessor names** (`get_first`/`get_last`/`get_step`,
  IntProgression owner in the `forRange`/counter-loop nodes) are pushed down to bir2cir (`RangeForLowering`). kotc
  emits a faithful `forRange` (range value + var + `rangeType`); bir2cir derives the accessors. The old
  synthetic-getter blocker is respected (the stdlib form stays the `_types`-resolved `forRange`, not a same-assembly
  synthetic getter call). See the "Range for-loop" note under Phase 5 above.

### Genuinely unavoidable (near-zero) — the only true must-stay
- kotc's **own synthetic types** with no IR origin: the `<>dotkt_*` closure/`KPropertyImpl`/
  `CharSequence`-adapter family. (The `<>dotkt_*Delegate_<V>` **delegate classes** and the `<>dotkt_RWProperty_<V>`
  monomorphized interface are **gone** — #57 retired them; `Delegates.*` uses the real stdlib + real generic
  `ReadWriteProperty`. The `<>dotkt_KIterator_<elem>`/`<>dotkt_KIterable_<elem>` **iterator-protocol**
  monomorphization is **gone too** — #58 retired it: a user `class R : Iterable<Int>`/`Iterator<Int>` (concrete
  value-type element) now links the REAL generic `kotlin.collections.Iterable<Int>` (bir2cir `@ClrTypeAlias`'d to
  `System.Collections.Generic.IEnumerable<int>`; ilemit's reverse GetEnumerator bridge — extended to resolve the
  shared adapter from the referenced stdlib dll in app assemblies — synthesizes `GetEnumerator` from `iterator()`)
  / `kotlin.collections.Iterator<Int>` (a real emitted stdlib interface), and every `for (x in r)` /
  `it.hasNext()`/`it.next()` dispatches on that real generic (bir2cir `IteratorConsumerNormalization` normalizes
  the dispatch to `clrInstance` on the base `Iterator<Int>`, covering the inherited-member `MutableIterator` case).
  Its premise ("IL can't define a generic interface") was false — the reverse bridge already used the real generic
  in the substitute build; app builds now match. The `<>dotkt_CharSequence` interface stays a synthetic — it has NO
  faithful BCL equivalent, a DIFFERENT (genuine) reason, not the false generic-interface premise; likewise the
  `<>dotkt_KProperty`/`KPropertyImpl` property-reference pair (a pure binding, no BCL equivalent).)

  **#52 (2026-07-08) — the synthetic TYPE *definitions* moved kotc → bir2cir.** These CLR-representation types are
  still *needed*, but kotc no longer SYNTHESIZES them — it emits only the Kotlin FACTS, and bir2cir (where CLR
  knowledge lives) assembles the actual type defs into the CIR `types`:
  - **closure class** (`<>dotkt_<scope>_Closure<N>`): kotc emits `newClosure` carrying a transient `synthClass`
    ingredient bag (capture fields `{name,type}` + invoke params/ret/body + generic `typeParams`);
    bir2cir `ClosureSynthesis` builds the class (class/base/interfaces wrapper + the ctor field-init body) and
    strips `synthClass`, leaving the lean `newClosure` (closureType + capture values + funcType + typeArgs) ilemit
    already consumes for the `new`. Runs FIRST in the phase-1 loop — before `SuspendColdLowering` builds its closure
    lookup from `types` for the `suspendCoroutineUninterceptedOrReturn` inliner.
  - **`<>dotkt_CharSequence` interface** + **`<>dotkt_KProperty(Impl)`**: kotc emits only the use-site references
    (the fact); bir2cir `SharedSyntheticSynthesis` injects the fixed-shape def into any file that references the
    identity (ilemit still dedups per assembly + canonicalizes to the rt stdlib's copy when it resolves externally).
  - **heap ref-cell** (`<>dotkt_<scope>_Ref_<elem>`): kotc emits a file-level `refTypes` registry ({name, element
    type} — the element type is unrecoverable from the bare `field .v` use-sites); bir2cir `SharedSyntheticSynthesis`
    assembles each `{ var v }` cell from it and drops the registry.
  (The **SAM shim** `<>dotkt_<scope>_Sam<N>` — a lift of a user `fun interface` impl — plus lifted local-class /
  anon-object types stay in kotc: they are lifts of user-authored declarations, not pure synthetics. Analogous to
  the closure move; a candidate follow-up, not in #52 scope.)

  Their FQN literals (`<>dotkt_*`) are not `kotlin.*`, so they were always outside the "hardcoded `kotlin.*` literal"
  metric; after #52 kotc no longer *defines* a closure/CharSequence/KProperty/ref-cell type at all — it emits the
  fact and bir2cir owns the CLR-representation synthesis, the last, smallest residue closed.
- **Interop reads** (`kotlin.clr.*`, facadegen injection, `@Volatile`/`@ClrField`/`@ClrAwait`): the sanctioned
  .NET-space / annotation-flag reads. Legitimate by the boundary rule (kotc *may* read the .NET space).
- **ClrH routing arm — DELETED (CLEANUP-A1, 2026-07-08).** Inside the member-call `if (clrType != null)` interop
  block, a vestigial "Rule 3" arm routed a concrete non-abstract member to the `<>dotkt_ClrH_<Class>` static hoist
  helper via `clrHelperName`. It was **reasoned-dead**: `clrType != null` requires `clrInteropName`, which resolves
  **only** facadegen-injection metadata (an .NET owner with **no** Kotlin bodies to hoist); stdlib `@ClrTypeAlias`
  classes — the real source of rule-3 Kotlin-body members — resolve to `clrInteropName == null`, fall through to the
  plain `kotlin.*` member-call path, and their ClrH is synthesized+routed **entirely by bir2cir's `AliasHelperHoist`**.
  The arm, its `injectedOwner` gate (which existed only to keep injected types out of the hoist), and `clrHelperName`
  + its doc-comment were removed; a call in that position now falls straight through to the `clrStatic`/`clrInstance`
  member-shape dispatch. Confirmed by grep (nothing else in kotc references `clrHelperName`/`dotkt_ClrH` except one
  bir2cir-describing comment) and by BIR sanity (il-injstatic, a pure `System.*` call, and a stdlib StringBuilder
  `append`/`reverse`/`length` sample all emit **zero** kotc `<>dotkt_ClrH_`; StringBuilder members route as plain
  `callInstance`). Codex independently confirmed the arm has no reachable trigger.

---

## Appendix — exact site index (for the executor)

- BirMappings.kt: tables §Part 1.
- Conv: `BirEmitter.kt:4231–4234` (+ `NUMBER_CONV`/`NUMERIC_FQ`).
- List/Set factory: `3502–3508`. Map factory: `3510–3535`. Array factory: `3545–3553`. arrayOfNulls: `3539–3543`.
- `to`: `3658–3662`. componentN: `3663–3674`.
- Range for-loop: `1866–1886` (faithful `forRange` at `1884–1886`; accessor realization now in bir2cir
  `RangeForLowering.cs`; plain `for` branches for const `1..5`/`rangeTo`/`until`/`downTo` at `1887–1904`).
  rangeTo: `3560–3579`. range `contains`: `3583–3596`.
- Enum: `3599–3653`. Reified enum: `3620–3637`.
- Preconditions: `4263–4293` (+ `newExc` 132/337/847). Scope: `3485–3489`. use: `3493`. repeat: `4301`.
- Operators: `4159–4235`. EQEQ/EQEQEQ: `4169–4182`. String.plus: ✅ RELOCATED to bir2cir (#52 Phase 5) —
  kotc emits the faithful `callInstance kotlin.String.plus`; `PrimitiveOperatorLowering` re-emits the `concat`.
- Delegates.observable/vetoable/notNull: ✅ DELETED (#57) — resolve to the real stdlib `Delegates`/`ObservableProperty`/
  `NotNullVar`; delegate-access dispatches getValue/setValue on the real generic `kotlin.properties.ReadWriteProperty<Any?,V>`
  (`by lazy`-parallel). `<>dotkt_RWProperty_<V>` monomorphization (`propIface*`) fully retired.
- Collection bridges (iterator/defaults/listIterator + lifted `iterator` mref): ✅ DELETED (#52 Phase 4) —
  routing owned by bir2cir Rule 5 (`Program.cs`).
- Kotlin-semantic helper routes (GENUINE-GAP, KEPT — #52 Phase 4): `collToStringRoute`, `floatTotalEqRoute`,
  `collEqRoute`, `concatOperand` (LibraryKt), `compareTo` Double/Float.
- clrName (interop, keep): `4483–4534`.

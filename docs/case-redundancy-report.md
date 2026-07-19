# IL-gate case-redundancy report (read-only proposal)

> **STATUS: PROPOSAL ONLY. NOTHING HAS BEEN DELETED OR MERGED.** This document maps every
> `verify-il.sh` sample to the code-path / issue it guards and flags *candidate* redundancy for a
> human (coordinator + user) to audit before any removal. Per the project rule
> *avoid-self-initiated-deletion-cleanup*, no case was touched.

## Scope & method

- Corpus: the **382** `il_check* ` invocations in `scripts/verify-il.sh` (288 `il_check`, 58
  `il_check_imports`, 34 `il_check_inject`, 1 `il_check_inject_nrt`, 1 stray-formatted `il_check`).
  There are 384 `cases/il-*` directories on disk; two (`il-widedeleg`, plus a handful driven by the
  other gates — e.g. `il-injectemit`, ktproj cases) are exercised by `verify-wide-delegates.sh` /
  `verify-ktproj.sh`, not `verify-il.sh`, so they are out of this report's scope.
- Each case was mapped from (a) its trailing guard comment in `verify-il.sh` and (b) a direct read of
  the `.kt` source and the asserted `expected` stdout. 106 cases carry an explicit issue-guard
  comment; 276 are older "foundational" cases with no comment (each still a distinct language/stdlib
  feature — see the appendix).
- **Conservatism rule applied verbatim** (from the task and MEMORY
  `build-cache-masks-stdlib-regressions` / `avoid-self-initiated-deletion-cleanup`): a case that adds
  **any** distinct edge — a different type, a null, a generic arg, a dispatch shape, an evaluation-order
  or single-eval assertion — is **NOT** redundant and is kept. A wrong "merge" that silently drops a
  regression guard is far worse than a slightly larger suite.

## Headline finding

**Confirmed genuinely-redundant cases: 0.** Every cluster that *looked* like reflexive
"one-fix-one-case" duplication turned out, on reading the sources, to exercise a **distinct code
path** — most tellingly the pairs that share identical asserted output (see `netgen`/`netgen2`
below). The premise that the suite accreted redundant cases does not hold up under audit: the
accretion produced **distinct edge coverage**, not duplicates.

**Consequence for gate speed:** the ~45 min gate cost is *not* meaningfully attributable to redundant
cases, so deleting cases is the wrong lever and would lose real coverage. The right levers are already
in play or delivered here:
1. **Change-aware selection** — the new `scripts/gate.sh` wrapper runs only the suites a change can
   affect (this same PR).
2. **Parallelism** — `verify-il.sh` already fans out across `nproc-2` cores (24-core box).
3. **Toolchain-fingerprint reuse** — `lib.sh` `need_*` + the `.toolstamp` sidecars (#13) already skip
   stdlib rebuilds when the toolchain is unchanged; `gate.sh` only forces the clean rebuild on the
   cache-masking axes (kotc / bir2cir / ilemit / retarget / stdlib).

## Closest-call clusters that were audited and found DISTINCT (recommend KEEP)

These are the clusters most likely to *look* redundant (numbered siblings, overlapping names, and in
one case identical output). All were read and are distinct; listed here so the auditor does not have to
re-derive them.

| cluster | why each member is a DISTINCT edge (KEEP all) |
|---|---|
| `netgen` / `netgen2` (identical output `3/True/2`) | `netgen` **uses** a generic .NET type directly (`Collection<Int>()`); `netgen2` **inherits** a generic .NET base (`class IntColl : Collection<Int>()`). Direct-use vs external-generic-base subclassing are different emit paths — identical output ≠ identical path. `netgen3` covers `Unsafe`/`RuntimeHelpers` generic-method interop (different metadata source `GMMETA`). |
| `gen` … `gen6` (`il-generic{,2..6}`) | Distinct generic shapes, each with different asserted output: basic generic fn/class (`gen`), bounded/`is`-typed box (`gen2`), multiple type params (`gen3/gen4`), variance/`out` and consumer (`gen5/gen6`). No two share an assertion set. |
| `coll` / `coll2` / `coll3` | `coll` = map/filter/take/drop/any/all/count/first/contains/reversed; `coll2` = fold + `joinToString` (separator + default); `coll3` = `forEach` with captured-var mutation. Disjoint operator coverage. |
| `inline` / `inline2` / `xinline` | `inline` = plain splice (`twice`/`clamp`); `inline2` = **non-local return** out of an inline lambda + `repeat`; `xinline` = **crossinline** + nested-lambda-to-delegate. Three different lowering mechanisms. |
| `str` / `strops` / `substr` / `subseq` | `str` = basic `uppercase`/`trim`/`substring`/`startsWith`; `strops` = char-arg `trim*`/`pad*`/`replace` variants; `substr` = `String.substring(start,end)` end-exclusive boundary; `subseq` = `CharSequence.subSequence` **single-eval** of the start expr (asserts `start()` ran once). |
| `charseq` / `charseqx` / `charseqs` | `charseq` = a user `CharSequence` subclass (operator `get`, polymorphic param); `charseqx` = user + String → stdlib **extension** (`hasSurrogatePairAt`, String→adapter); `charseqs` = `CharSequence` *params* fed String / `StringBuilder`-snapshot / `CharSequence`-local receivers. Distinct adapter/dispatch shapes. |
| `netbase` / `netbase2` | `netbase` = throw/catch across a facadegen-injected `System.Exception` base; `netbase2` = a second exception subclass exercising the ctor/message rename on a distinct shape. |

## Superficially-similar clusters NOT individually source-audited (LOW priority; default KEEP)

The following name-families each carry **distinct expected output** (the primary distinctness signal),
which is why they were not exhaustively opened. They are the only remaining places an auditor might
look, but the differing assertions already indicate distinct coverage, and the conservatism rule says
keep on any distinct edge:

- Coroutine/suspend family: `suspend*`, `inlsuspend*`, `cold*`, `co*` — the largest family (~70 cases),
  each guarding a specific suspension shape (catch, loop, capture, ref, default-arg, order, cancel,
  restrict, cross-module, DIM, inherited-base). Distinct issue numbers throughout (#67/#78/#80/#82/…).
- Inline-splice family: `inl*` — each maps to a numbered item of the holistic inline-splice program
  (§8.1–§8.6, #30/#31/#34/#60/#61/#62/#63/#75). Non-overlapping return/label/materialize edges.
- Nullable value-type family: `arrnull`/`copyofnull`/`nullgenlist`/`refcellnullable`/`nullbang`/
  `tryval`/`structfloateqnull`/`floateqnull` — each a different axis (write/return/read/field/direct-eq)
  of the `Nullable<T>` erasure work (#28/#36/#113/#124/#127/#152/#180). `structfloateqnull` (structural
  total-order) vs `floateqnull` (direct IEEE) are explicitly documented as DISTINCT paths.
- Regex family: `regex`/`regexanchor`/`regexopts`/`regexreplace`/`regexgroups`/`regexseq`/`groupvalues`
  — distinct Regex surface (match/anchor/options/replace/groups/sequence).

## Closed-issue subsumption flags (recommend KEEP as regression guards)

Some cases explicitly note that a narrower band-aid was deleted once a broader fix subsumed it. In
every instance the **case itself still guards a distinct, live path** and should be kept as a
regression guard even though its originating issue is closed (a closed bug's regression test is
exactly what prevents its return):

- `suspendintrinsicowned` (#157, "was #80-residual") vs `suspendintrinsic` (#80): the former reads
  `COROUTINE_SUSPENDED` from a **non-suspend** member (general owner-null resolver); the latter reads it
  inside a `suspendCoroutineUninterceptedOrReturn` **suspend** block (SM `Suspended()` canonicalization).
  Different resolver paths — keep both.
- `supercall` (#14) / `superobj` (#14-R1) / `supernet` (#14-R2): the #14 root is closed, but each guards
  a distinct `super` dispatch target (user base / `kotlin.Any`→`System.Object` / facadegen-injected .NET
  base). Non-virtual `call` vs re-dispatching `callvirt` is a stack-overflow regression per target — keep.

> Recommendation: treat "issue closed" as a reason to **keep** the guard, not remove it. No
> closed-issue case in this suite was found to be *behaviorally* subsumed (same IL path, same asserts)
> by a broader case.

## Bottom line for the coordinator

- **0 cases** are proposed for deletion or merge.
- The 7 audited "closest-call" clusters are **distinct**; keep them.
- If suite size must be reduced later, the safe procedure is: pick a specific pair, diff the emitted
  CIR/IL of the two `.kt` sources, and only merge if the IL paths and assertions are byte-identical —
  none checked here met that bar.
- The gate-time problem is addressed by change-aware selection (`scripts/gate.sh`), not by case
  removal.

---

## Appendix — full case → guard map (all 382 `verify-il.sh` samples, in source order)

`shape` = the `il_check` variant: `plain` (no .NET interop) / `imports` (façade-free `import System.X`)
/ `inject` (ships its own `runtime.cs`) / `inject-nrt` (same with C# NRT enabled). `_(foundational)_` =
a pre-issue-guard case with no trailing comment; the case name states the feature under test.

| # | case | shape | guard / code-path |
|---|------|-------|-------------------|
| 1 | `m0` | plain | _(foundational: no issue-guard comment)_ |
| 2 | `injectdedup` | plain | _(foundational: no issue-guard comment)_ |
| 3 | `mc1` | plain | _(foundational: no issue-guard comment)_ |
| 4 | `iface` | plain | _(foundational: no issue-guard comment)_ |
| 5 | `overrideprop` | plain | `override val` accessor fills the base CLASS abstract slot (not a fresh NewSlot) — else concrete subclass TypeLoad-fails |
| 6 | `overridemsg` | plain | #24: `override val message` on a @ClrTypeAlias base (kotlin.Exception->System.Exception) — DeclarationRename wires the get_message accessor to the @ClrProperty("Message") slot (rename + clrOverride) so DefineMethodOverride binds System.Exception.get_Message (else every read returns the base value) |
| 7 | `supercall` | plain | #14: super.X() from an override is a non-virtual `call` to the resolved base slot (else callvirt re-dispatches → infinite recursion); covers method/prop/3-level chain/user-base toString/interface-DIM + a virtual-dispatch non-regression |
| 8 | `superobj` | plain | #14 RESIDUAL R1: super.toString()/hashCode()/equals() to kotlin.Any → the System.Object slot NON-virtually (MemberCallSubstitution carries the `super` marker onto clrInstance; ilemit emits `call`, not the callvirt that re-dispatched → stack overflow) |
| 9 | `supernet` | imports | #14 RESIDUAL R2: super.Next() to a facadegen-injected .NET base (System.Random) → NetInteropBinding propagates the `super` marker onto clrInstance; ilemit's EmitClrCall emits `call`, not the callvirt that re-dispatched → infinite recursion |
| 10 | `xfaceimpl` | plain | cross-file + namespaced interface impl/dispatch (FindMethod key regression) |
| 11 | `ifacecompanion` | plain | #83: an interface's PLAIN companion flattens to the interface's statics (static fields run in its .cctor + static methods); kotc emitted `fields:[]` and ilemit skipped interface fields/.cctor -> `field SharingStarted.Eagerly not found`. Named-companion (Factory) const/val is the co-located non-regression. |
| 12 | `genhof` | plain | generic fn: (T)->Unit over List<T> (TypeBuilderInstantiation.GetMethod regression) |
| 13 | `genclosure` | plain | closure in a generic fn capturing T-typed values (generic closure class regression) |
| 14 | `caprefinline` | plain | a coerced `::pushDouble` reference inside a buildList{} inline lambda -> an ADAPTER_FOR_CALLABLE_REFERENCE local fn whose bound receiver is an ExtensionReceiver param `receiver`; liftLocalFn must emit the receiver param, else the body's `receiver.pushDouble` dangles (the kotlinx flow `__local*_add: references undeclared local 'receiver'` blocker) |
| 15 | `adapterref` | plain | #84 G: a coerced MEMBER reference (`s::add`/`::add`, Boolean-returning member adapted to (Int)->Unit) passed to an inline forEach — the ADAPTER_FOR_CALLABLE_REFERENCE must forward to the real member as callInstance (adapterRef replays the adapter body), not a top-level `callStatic owner:null` (`static method not found: add`, the consumeEach(collection::add) blocker) |
| 16 | `geninherit` | plain | #84 I: a non-generic subclass calling a method INHERITED from a generic base (`IntHolder : Holder<Int>`) + a self-referentially-bounded generic (`Segment<S : Segment<S>>`) — the inherited method must anchor onto the CONSTRUCTED base (`Holder<Int>`/`Segment<Seg>`), not the open `Base`1::m` (\"not fully instantiated\", the ConcurrentLinkedListNode blocker) |
| 17 | `genfield` | plain | R4 #91: raw @ClrField access whose owner is a GENERIC type — the FIELD-side mirror of #84-I. A bare `C`1::f` token is \"not fully instantiated\" (ilemit `field must be declared on a generic type definition`; ilverify get_GenericParameters IndexOutOfRange). Anchors onto the CONSTRUCTED instantiation via TypeBuilder.GetField over all axes: self-inst own field (Cell), self-inst inherited generic-base field via `this` (Wrap, the JobSupport ResumeAwaitOnCompletion`1.invoke blocker), inherited-base field via a non-generic subclass (IntBox), and via a constructed generic subclass (Sub<String>). SUSPEND-FREE. |
| 18 | `usermember` | plain | #96: explicit hashCode()/toString()/equals() (+ a bound method reference) on a user class/interface — a DECLARED override dispatches to the user member; a NON-overriding class/interface-receiver inherits the kotlin.Any slot by virtual dispatch (bir2cir AnySlotRebind -> objMethod; a method reference retargets its owner to kotlin.Any) instead of dead-ending at ilemit ("method <UserType>.GetHashCode not found"); a base-declared toString reached through a non-declaring subclass resolves to the base member |
| 19 | `inlineinherit` | plain | #87: a MEMBER `inline fun` with a non-local-return lambda INHERITED from a superclass (plain subclass + self-bounded generic `Seg<S:Seg<S>>`), spliced at a SUBCLASS-bounded call site — the call is a fake override (parent=subclass, body=null) but the [KotlinInline] payload is stashed under the DECLARING class; kotc must resolve the fake override so the callInline owner keys the stash AND the same-module splice path fires (else bir2cir InlineSplice: "no [KotlinInline] payload found", the Segment.nextOrIfClosed blocker) |
| 20 | `inheritedgenericinline` | plain | #88: an inherited member `inline fun` whose OWNER class is GENERIC (`IntBox/StrBox : Container<E>`) spliced at a subclass call site — kotc's F2A carries the owner's type args via the corresponding-supertype instantiation `Container<Int>`/`Container<String>`, so the spliced payload's `tv{scope:type,0}` (E) concretizes instead of staying an OPEN generic (which typed the dispatch temp as the open type -> BadImageFormatException); the third line covers a TYPE-PARAMETER receiver whose bound `T : Container<Int>` fixes the owner arg |
| 21 | `geninlinearg` | plain | #122: inline collection-factory arg of a `new` in a generic fn — declared class-scope tv instantiated through the `new` binding (else Add(T[]) splat mismatch) |
| 22 | `genextnew` | plain | #123: `new Ext<T>(v)` (external generic over a FREE method type-var) is a TypeBuilderInstantiation — resolve its ctor on the open def + re-anchor via TypeBuilder.GetConstructor (else .GetConstructors() throws "does not support resolving members") |
| 23 | `enum` | plain | _(foundational: no issue-guard comment)_ |
| 24 | `enumintr` | plain | _(foundational: no issue-guard comment)_ |
| 25 | `enumtostr` | plain | _(foundational: no issue-guard comment)_ |
| 26 | `netenumbound` | imports | _(foundational: no issue-guard comment)_ |
| 27 | `icmparity` | imports | _(foundational: no issue-guard comment)_ |
| 28 | `gendelegate` | imports | _(foundational: no issue-guard comment)_ |
| 29 | `jsongeneric` | imports | #44: a generic .NET method (JsonSerializer.Serialize<T>) with a facadegen-injected interop SIBLING param (JsonSerializerOptions) — ShapeSynthesis resolves the leaf off the refs to its .NET simple name so the overload-matcher shapes match ilemit's reflected shapes (was: "Object" erasure -> zero candidates -> ilemit "Sequence contains no elements") |
| 30 | `m2` | imports | _(foundational: no issue-guard comment)_ |
| 31 | `mi1` | imports | _(foundational: no issue-guard comment)_ |
| 32 | `alias` | imports | _(foundational: no issue-guard comment)_ |
| 33 | `dualrep` | imports | _(foundational: no issue-guard comment)_ |
| 34 | `bclinject` | imports | _(foundational: no issue-guard comment)_ |
| 35 | `tlvalint` | imports | _(foundational: no issue-guard comment)_ |
| 36 | `taskfam` | imports | _(foundational: no issue-guard comment)_ |
| 37 | `taskawait` | imports | _(foundational: no issue-guard comment)_ |
| 38 | `valueawait` | imports | _(foundational: no issue-guard comment)_ |
| 39 | `cfgawait` | imports | _(foundational: no issue-guard comment)_ |
| 40 | `cfgawaitgen` | imports | _(foundational: no issue-guard comment)_ |
| 41 | `awaitintercept` | imports | _(foundational: no issue-guard comment)_ |
| 42 | `extawait` | inject | _(foundational: no issue-guard comment)_ |
| 43 | `taskgen` | imports | _(foundational: no issue-guard comment)_ |
| 44 | `taskwhen` | imports | _(foundational: no issue-guard comment)_ |
| 45 | `coctxkey` | plain | _(foundational: no issue-guard comment)_ |
| 46 | `cointercept` | plain | _(foundational: no issue-guard comment)_ |
| 47 | `coldcf` | plain | _(foundational: no issue-guard comment)_ |
| 48 | `coforarray` | plain | _(foundational: no issue-guard comment)_ |
| 49 | `coldgen` | plain | _(foundational: no issue-guard comment)_ |
| 50 | `coldinst` | plain | _(foundational: no issue-guard comment)_ |
| 51 | `coldvirt` | plain | _(foundational: no issue-guard comment)_ |
| 52 | `coldsuper` | plain | _(foundational: no issue-guard comment)_ |
| 53 | `coroutinectx` | plain | _(foundational: no issue-guard comment)_ |
| 54 | `coldabstract` | imports | _(foundational: no issue-guard comment)_ |
| 55 | `ifacesuspend` | imports | _(foundational: no issue-guard comment)_ |
| 56 | `coldsubiface` | imports | _(foundational: no issue-guard comment)_ |
| 57 | `coldbaseinherit` | imports | _(foundational: no issue-guard comment)_ |
| 58 | `coldstaticmember` | imports | _(foundational: no issue-guard comment)_ |
| 59 | `colddimgen` | imports | _(foundational: no issue-guard comment)_ |
| 60 | `seqyieldall` | plain | _(foundational: no issue-guard comment)_ |
| 61 | `for` | plain | _(foundational: no issue-guard comment)_ |
| 62 | `exc` | plain | _(foundational: no issue-guard comment)_ |
| 63 | `ops` | plain | _(foundational: no issue-guard comment)_ |
| 64 | `charminus` | plain | _(foundational: no issue-guard comment)_ |
| 65 | `digittoint` | plain | _(foundational: no issue-guard comment)_ |
| 66 | `printlnnull` | plain | _(foundational: no issue-guard comment)_ |
| 67 | `maptostr` | plain | _(foundational: no issue-guard comment)_ |
| 68 | `mapmerge` | plain | _(foundational: no issue-guard comment)_ |
| 69 | `mapof1` | plain | _(foundational: no issue-guard comment)_ |
| 70 | `mapvalues` | plain | _(foundational: no issue-guard comment)_ |
| 71 | `emptymap` | plain | _(foundational: no issue-guard comment)_ |
| 72 | `colstr` | plain | _(foundational: no issue-guard comment)_ |
| 73 | `interpnull` | plain | _(foundational: no issue-guard comment)_ |
| 74 | `math` | plain | _(foundational: no issue-guard comment)_ |
| 75 | `mathabs` | plain | C9: kotlin.math.abs WRAPS at MIN_VALUE (unchecked neg), does NOT throw like System.Math.Abs |
| 76 | `radix` | plain | C4: Int/Long.toString(radix) -> stdlib actual (sign + arbitrary base), NOT System.Convert.ToString (two's-complement / base-36 crash) |
| 77 | `strhash` | plain | #167/#168: String/Double/Float hashCode() bind CLR-native GetHashCode — asserts equals-consistency + hash-set membership (contract), NOT a pinned value; primitive Int toString/equals/hashCode stay correct |
| 78 | `pairtostr` | plain | C11 gate regression guard: collection/tuple/data-class toString + String.hashCode within-run consistency (#167) |
| 79 | `pairnest` | plain | _(foundational: no issue-guard comment)_ |
| 80 | `collrevview` | plain | _(foundational: no issue-guard comment)_ |
| 81 | `nullcollarg` | plain | _(foundational: no issue-guard comment)_ |
| 82 | `extprop` | plain | C7 (+ #157 NON-coroutine guard): cross-module top-level extension-property getter -> callStatic get_<name>(receiver) (generic List.lastIndex carries type args); NOT a dropped-receiver field read. Resolves through the SAME general owner-null path as xmodtopval (prop:get -> get_<name> -> TryResolveTopLevelStatic recvKey branch) — a name-keyed re-special-case of that path would break these non-coroutine names |
| 83 | `str` | plain | _(foundational: no issue-guard comment)_ |
| 84 | `strops` | plain | trim(vararg)/padStart/padEnd/replace -> pure-Kotlin stdlib bodies (no kotc STRING_OPS System.String lowering) |
| 85 | `defargs` | plain | C3: cross+same-module default args — omitted middle default must not shift a later provided arg's slot (joinToString transform / substringAfter `= this` / data-class copy(field=)) |
| 86 | `defargs2` | plain | C3 residual: same-module default referencing ANOTHER value param (`b = a * 10`, `c = a + b`) — inlined with that param's filled arg substituted |
| 87 | `negzero` | plain | C14: boxed Double/Float total order (-0.0 < 0.0, NaN largest & NaN==NaN) via stdlib helpers; primitive ==/< stay IEEE |
| 88 | `listeq` | plain | collection `==` is STRUCTURAL (List ordered / Set unordered / Map entrywise), routed to stdlib helpers not reference Object.Equals |
| 89 | `indices` | plain | for-in over a non-literal IntRange from `.indices` (Collection + CharSequence) counter-lowered off the iterator protocol |
| 90 | `indicesv` | plain | _(foundational: no issue-guard comment)_ |
| 91 | `coerce` | plain | coerceAtMost/AtLeast/In -> pure-Kotlin stdlib bodies (no kotc System.Math lowering) |
| 92 | `blank` | plain | isBlank/isNotBlank -> pure-Kotlin index-loop body (no kotc IsNullOrWhiteSpace lowering) |
| 93 | `infloopret` | plain | #141: value-returning while(true){…return x} -> ilemit appends default(ret)+ret so the unreachable fall-through terminator is ilverify-clean (ReturnMissing gone) |
| 94 | `genarrlam` | plain | #142: `Array(size){ mk<T?>(null) }` in a generic class — nested constructed-generic `Ref<T?>` erased to `Ref<object>` CONSISTENTLY across method-sig/array-elem/newDelegate.funcType.ret; DelegateCtor gone. #4 (read side): reading the erased element back across the Box<Int>/Box<String> boundary (`b.a[0].v`/`b.elem(i).v`/retyped local/`val x: Int?=…`) re-derives `Ref<object>` from the erased decl (NullableTvErasureCallRealign) — was ilverify StackUnexpected `Ref`1<object>` vs `Ref`1<Nullable`1<int32>>` |
| 95 | `nullgenlist` | plain | GitHub #28: List<T?> uses the declaration's object-erased interface consistently at member reads (Count + get_Item + GetEnumerator), for reference and value instantiations. |
| 96 | `toplateinit` | plain | #104: top-level `lateinit var` (ref type) static field carries `"init": null` — must NOT hit the .cctor null-coercion store (crash); default-null + lateinitGet throw-before-init |
| 97 | `samcmp` | plain | explicit Comparator{} SAM conversion (plain fun interface; no kotc @ClrTypeAlias read) |
| 98 | `strnum` | plain | _(foundational: no issue-guard comment)_ |
| 99 | `ntostr` | plain | value-type-nullable/value arg BOXED into a REFERENCED method's object param (EmitCallArgs pt==null path) |
| 100 | `cp` | plain | _(foundational: no issue-guard comment)_ |
| 101 | `ext` | plain | _(foundational: no issue-guard comment)_ |
| 102 | `companionext` | plain | #177: an extension fun in a companion object lowers to a static method whose first param is the extension receiver — the call site must pass that receiver as the LEADING arg (was dropped -> arity miscompile) |
| 103 | `arr` | plain | _(foundational: no issue-guard comment)_ |
| 104 | `lam` | plain | _(foundational: no issue-guard comment)_ |
| 105 | `clo` | plain | _(foundational: no issue-guard comment)_ |
| 106 | `scope` | plain | _(foundational: no issue-guard comment)_ |
| 107 | `coll` | plain | _(foundational: no issue-guard comment)_ |
| 108 | `coll2` | plain | _(foundational: no issue-guard comment)_ |
| 109 | `coll3` | plain | _(foundational: no issue-guard comment)_ |
| 110 | `arraydeque` | plain | concrete generic stdlib class ArrayDeque<E>:AbstractMutableList<E> as a field/owner forces ilemit to resolve kotlin.collections.ArrayDeque`1 from the rt dll — exercises the ICollection/IList void-drop methodimpl bridge (ilemit) + the BCL-only slot synthesis Contains/CopyTo/IsReadOnly/IndexOf (bir2cir) |
| 111 | `copyintoverlap` | plain | #97: copyInto must be overlap-safe (System.Array.Copy = memmove); a forward element loop clobbers overlapping self-copies -> silently corrupts ArrayDeque.add(index,elem). Generic Array<T> path (the ArrayDeque victim); the 8 primitive copyInto actuals are fixed identically but not app-callable (pre-existing primitive-array-receiver resolution gap) |
| 112 | `roundhalfup` | plain | #103: roundToInt/roundToLong = round-half-UP toward +inf (floor(x+0.5)), NaN throws, out-of-range saturates — NOT banker's ToEven |
| 113 | `mathnumerics` | plain | #141: hypot/expm1/ln1p bind net10 System.Double/Single.Hypot/ExpM1/LogP1 (no sqrt-overflow, no exp-1/ln1p cancellation); Double 5.0 prints "5", Boolean prints "True" per CLR ToString |
| 114 | `utf8throw` | plain | #143: decodeToString/encodeToByteArray honor throwOnInvalidSequence=true -> CharacterCodingException via throwing UTF8Encoding(false,true) |
| 115 | `caseinvariant` | plain | #144: String/Char uppercase()/lowercase() are CLR-native 1:1 ToUpperInvariant/ToLowerInvariant — DELIBERATELY no Unicode one-to-many expansion (ß stays ß, not SS) |
| 116 | `fillrange` | plain | #145: array fill validates the range (IllegalArgumentException on fromIndex>toIndex, IndexOutOfBoundsException out-of-bounds); generic path (primitive actuals fixed identically but blocked by the primitive-array-receiver resolution gap) |
| 117 | `seq` | plain | _(foundational: no issue-guard comment)_ |
| 118 | `seqforin` | plain | _(foundational: no issue-guard comment)_ |
| 119 | `char` | plain | _(foundational: no issue-guard comment)_ |
| 120 | `sort` | plain | _(foundational: no issue-guard comment)_ |
| 121 | `boxgen` | plain | C2 boxed-primitive dual-representation: getOrPut/getOrElse/compareBy/Array<Int?>/T:Enum<T> |
| 122 | `arrnull` | plain | #113: arrayOfNulls<T>(n) allocates Nullable<T>[] (value-type nullability preserved) + copyOf() round-trip; general Int/Long/Double/Char/String |
| 123 | `arrslice` | plain | #117: Array<value-type>.slice/take/takeLast via runtime-type-preserving copyOfRange (value + reference T) |
| 124 | `intarraytolist` | plain | #153: primitive-array-receiver top-level stdlib extension (toList/copyOf/copyInto/contentToString) resolves to ArraysKt; fine first-param key disambiguates signed vs unsigned (UArraysKt) vs generic Array<T>, receiver-nullability-insensitive |
| 125 | `arrplus` | plain | #120: Array<value-type>.plus/plusElement body-local var reified-array element kept !T (value + reference T) |
| 126 | `copyofnull` | plain | #124: Array<value-type>.copyOf(newSize) builds Nullable<elem>[] by runtime reflection (grow null-tail/shrink/prefix read-back; value + reference + already-nullable T) |
| 127 | `funref` | plain | _(foundational: no issue-guard comment)_ |
| 128 | `extfunref` | plain | _(foundational: no issue-guard comment)_ |
| 129 | `boundextref` | plain | #91: bound ext-fn ref `expr::extFn` -> capture-class lift (receiver captured eagerly; delegate over instance invoke). #106: bound CharSeq-ext ref (::isNotBlank/::isBlank) -> String field-read adapter-wrapped by StringCharSequenceBridge |
| 130 | `mapdes` | plain | _(foundational: no issue-guard comment)_ |
| 131 | `mapgen` | plain | _(foundational: no issue-guard comment)_ |
| 132 | `unsgn` | plain | _(foundational: no issue-guard comment)_ |
| 133 | `ubytearr` | plain | _(foundational: no issue-guard comment)_ |
| 134 | `regex` | plain | _(foundational: no issue-guard comment)_ |
| 135 | `regexanchor` | plain | _(foundational: no issue-guard comment)_ |
| 136 | `regexopts` | plain | _(foundational: no issue-guard comment)_ |
| 137 | `linkedorder` | plain | _(foundational: no issue-guard comment)_ |
| 138 | `linkedset` | plain | _(foundational: no issue-guard comment)_ |
| 139 | `regexreplace` | plain | _(foundational: no issue-guard comment)_ |
| 140 | `regexgroups` | plain | _(foundational: no issue-guard comment)_ |
| 141 | `regexseq` | plain | _(foundational: no issue-guard comment)_ |
| 142 | `groupvalues` | plain | _(foundational: no issue-guard comment)_ |
| 143 | `gencolladd` | plain | _(foundational: no issue-guard comment)_ |
| 144 | `langtail` | plain | _(foundational: no issue-guard comment)_ |
| 145 | `tailrec` | plain | §2b: deep `tailrec` TCO'd to a back-jump loop (self / when / extension-receiver / member); no CLR stack overflow |
| 146 | `copydef` | plain | C3: data-class copy(field=x) with omitted fields — cross-module Pair/Triple reconstruct this.<field> |
| 147 | `equalscall` | plain | §5a: explicit .equals() -> total-order (Double/Float) / structural (collections), plain object stays reference |
| 148 | `enumbody` | plain | _(foundational: no issue-guard comment)_ |
| 149 | `bytearg` | plain | _(foundational: no issue-guard comment)_ |
| 150 | `iterable` | plain | _(foundational: no issue-guard comment)_ |
| 151 | `customexc` | plain | _(foundational: no issue-guard comment)_ |
| 152 | `comparator` | plain | _(foundational: no issue-guard comment)_ |
| 153 | `use` | plain | _(foundational: no issue-guard comment)_ |
| 154 | `comparable` | plain | _(foundational: no issue-guard comment)_ |
| 155 | `charseq` | plain | _(foundational: no issue-guard comment)_ |
| 156 | `charseqx` | plain | _(foundational: no issue-guard comment)_ |
| 157 | `charseqs` | plain | _(foundational: no issue-guard comment)_ |
| 158 | `charseqbcl` | plain | #148: computed/BCL-origin String receiver (property-read/app-fun-result/`!!`/StringBuilder.toString) into a stdlib CharSequence ext (split/replace/substring) — bir2cir must adapter-wrap it (else EntryPointNotFound on the body-less dotkt$CharSequence.subSequence; the #92 residual) |
| 159 | `charseqxfile` | plain | #149-1: a CROSS-FILE String receiver (a user-class property `c.body` / a top-level fun `banner()` declared in a SIBLING .kt of the SAME assembly) into a stdlib CharSequence.split — bir2cir aggregates all files' declared types (StaticType.GlobalTypes) so the cross-file static type resolves and the receiver is adapter-wrapped (else EntryPointNotFound) |
| 160 | `charseqmore` | plain | #149-2/3/4: a String BRANCH of a polymorphic CharSequence if/else, a StringBuilder receiver (snapshot->adapter), and `x!!.isNullOrEmpty()` (nullable slot + `!!`) into a stdlib CharSequence ext — bir2cir wraps each (else EntryPointNotFound) |
| 161 | `substr` | plain | _(foundational: no issue-guard comment)_ |
| 162 | `subseq` | plain | _(foundational: no issue-guard comment)_ |
| 163 | `seqfilter` | plain | _(foundational: no issue-guard comment)_ |
| 164 | `nulltostr` | plain | _(foundational: no issue-guard comment)_ |
| 165 | `result` | plain | _(foundational: no issue-guard comment)_ |
| 166 | `genstatic` | plain | _(foundational: no issue-guard comment)_ |
| 167 | `bmore` | plain | _(foundational: no issue-guard comment)_ |
| 168 | `chunk` | plain | _(foundational: no issue-guard comment)_ |
| 169 | `cwindowed` | plain | _(foundational: no issue-guard comment)_ |
| 170 | `cwindowedv` | plain | _(foundational: no issue-guard comment)_ |
| 171 | `eachcount` | plain | _(foundational: no issue-guard comment)_ |
| 172 | `collmore` | plain | _(foundational: no issue-guard comment)_ |
| 173 | `nestedstr` | plain | _(foundational: no issue-guard comment)_ |
| 174 | `tryexpr` | plain | _(foundational: no issue-guard comment)_ |
| 175 | `localclass` | plain | _(foundational: no issue-guard comment)_ |
| 176 | `writecapture` | plain | #68: a local class / object expression that WRITES a captured outer `var` shares a heap ref-cell (computeRefCells promotes the mutated capture) — was a whole-compile abort for the write-through capture |
| 177 | `genlocalclass` | plain | #69: a function-local class capturing an enclosing TYPE PARAMETER is lifted GENERICALLY (reified CLR generics); ownerSpec/birType name the constructed `L<T>` at the new site + denotable var slot + member access — was a whole-compile abort |
| 178 | `collops2` | plain | _(foundational: no issue-guard comment)_ |
| 179 | `genseq` | plain | _(foundational: no issue-guard comment)_ |
| 180 | `genseq2` | plain | _(foundational: no issue-guard comment)_ |
| 181 | `refcell` | plain | _(foundational: no issue-guard comment)_ |
| 182 | `annot` | plain | _(foundational: no issue-guard comment)_ |
| 183 | `props` | plain | _(foundational: no issue-guard comment)_ |
| 184 | `computedprop` | plain | _(foundational: no issue-guard comment)_ |
| 185 | `kstar` | plain | #82: KTypeProjection.STAR computed companion prop routes to get_STAR (not a spurious staticField STAR) |
| 186 | `valcls` | plain | _(foundational: no issue-guard comment)_ |
| 187 | `ctorref` | plain | _(foundational: no issue-guard comment)_ |
| 188 | `getcls` | plain | _(foundational: no issue-guard comment)_ |
| 189 | `forin` | imports | _(foundational: no issue-guard comment)_ |
| 190 | `ldeleg` | plain | _(foundational: no issue-guard comment)_ |
| 191 | `langf` | plain | _(foundational: no issue-guard comment)_ |
| 192 | `pair` | plain | _(foundational: no issue-guard comment)_ |
| 193 | `triple` | plain | COV4: Triple ctor/destructure/componentN/full-arg copy/toString (partial-copy-with-defaults omitted — cross-module default-arg bug) |
| 194 | `typealias` | plain | COV3: typealias over stdlib generic / function type / user class, used across a fn boundary |
| 195 | `atomics` | plain | COV2: kotlin.concurrent.atomics AtomicInt/AtomicLong exercising the @ClrRefArgument Interlocked byref binding |
| 196 | `volatileatomic` | plain | #130: scalar atomics load()/store() volatile round-trip (Volatile.Read/Write byref for int/long/bool; @Volatile field for AtomicReference) |
| 197 | `atomicarraytry` | imports | _(foundational: no issue-guard comment)_ |
| 198 | `null` | plain | _(foundational: no issue-guard comment)_ |
| 199 | `nullableprim` | plain | _(foundational: no issue-guard comment)_ |
| 200 | `refcellnullable` | plain | #36: a captured-and-mutated `var Int?`/`Long?`/`Double?` -> heap ref-cell whose `v` field is Nullable<T>; the INIT ctor arg (bare T -> Nullable<T>), the inline smart-cast READ (Nullable<T>.Value), and the WRITE must all agree — was `new Ref(bare int)` into a Nullable<int> ctor slot -> InvalidProgramException |
| 201 | `nullbang` | plain | #56/#115/#118/#126: `!!` (and unsigned SAFE_CALL/ELVIS/`as?`/if-else-null-join) on value-type (Int?/Long?/Double?/Byte?/UInt?/UByte?) routes the value-type nullable path (Nullable<T>) + throws NPE; reference (String?) `!!` throws NPE EAGERLY even when result is stored/discarded |
| 202 | `tryval` | plain | #127: `try{value}catch{null}` in VALUE position on a value-type result -> the shared temp is typed Nullable<T> (null branch = HasValue=false), mirror of ternary()'s value+null-branch join (incl. stdlib toFloatOrNull/toDoubleOrNull) |
| 203 | `nncontract` | plain | #6/#32: non-null CONTRACTS on the public surface — PARAMETER PRECONDITIONS (top-level fun / ctor / member fun) + RETURN POSTCONDITIONS (statement/expression-position top-level fun / member fun / getter / return-in-try: finally runs before the postcondition NPE propagates) throw NullPointerException fail-fast on a laundered null; a normal non-null call is unaffected |
| 204 | `nullv` | plain | _(foundational: no issue-guard comment)_ |
| 205 | `op` | plain | _(foundational: no issue-guard comment)_ |
| 206 | `dataq` | plain | _(foundational: no issue-guard comment)_ |
| 207 | `inline` | plain | _(foundational: no issue-guard comment)_ |
| 208 | `memberextinline` | plain | #20: inline MEMBER-extension (companion member + Long extension) called with a lambda via `state.withState{}`; dispatch(companion)-unused so the extension splices via __self; non-local return keeps it inline |
| 209 | `inlklibmembernlr` | plain | #60 (W1): a CROSS-MODULE klib-stdlib inline MEMBER (Duration.toComponents, dispatch receiver + lambda) with a NON-LOCAL return — kotc emits an owner-ful callInline unconditionally (body-blind) and bir2cir splices via §4.3; the return exits the CALLER, not a delegate (pre-fix: silent delegate-return -> -1) |
| 210 | `inline2` | plain | _(foundational: no issue-guard comment)_ |
| 211 | `xinline` | plain | _(foundational: no issue-guard comment)_ |
| 212 | `inldeflam` | plain | #34: inline splice fills an OMITTED defaulted param — a LAMBDA default (`= { 100 }`, Tier-2 @KotlinDefault defaultCarrier re-hoisted), a CONST default (Tier-1 p["default"]), a default reading an EARLIER param (defaultArgParam token), and a default lambda whose body has a NESTED inline call (`count{}`, re-walked at the hoist); each on the take-default AND override path |
| 213 | `inlmemdef` | plain | #34 residual: a MEMBER inline fn's non-const defaulted param is now carried (@KotlinDefault) so InlineSplice fills it — the kotlinx.coroutines `BufferedChannel.sendImpl(... onNoWaiterSuspend={ ... })` shape: a non-capturing LAMBDA default, a simple-expr `= emptyList()` default, and a Tier-1 CONST default, each on the take-default AND override path |
| 214 | `inlnestparamshadow` | plain | F2 (#61): a nested inlineLambda param `x` that SHADOWS the outer inline callee's value param `x` — RewriteLocalRefs must NOT rebind the inner param ref to outer's temp (silent miscompile; pre-fix -> 1050). The inlineLambda scope boundary in RewriteLocalRefs/ApplyPrefix/CollectDeclared |
| 215 | `inlsiblingdelegate` | plain | F4 (#63): a §4.4ii materialized carrier whose body carries a `newDelegate` targeting a `__lambda` lifted into a SIBLING file's file class — `_appLocalMethods` must be MODULE-WIDE (else the sibling target is mis-judged non-app-local -> HasUnmaterializableNested fail-loud). Regression from 923a820 |
| 216 | `inlnestnlr` | plain | §8.1 a{b{return}} — caller returns, NO "after" (the predicate-descent trap) |
| 217 | `inlouterlabel` | plain | §8.2 run outer@{ forEach{ return@outer } } — outer delegate + inner splice; post-label runs |
| 218 | `inlnlbreak` | plain | §8.3 forEach{ break@outer } — non-local break through a carrier; exercises §4.1 hygiene |
| 219 | `inlownlabel` | plain | §8.4 forEach{ return@forEach } — MUST take the delegate path |
| 220 | `inlmutcap` | plain | §8.5 var write-through on the delegate path (ref-cell) |
| 221 | `inlforward` | plain | §8.6 filter→filterTo forwarding (§4.4i) + escaping return |
| 222 | `inlcompose` | plain | F3 (#62) transitive forwarding of an inline PARAM through a user top-level inline (outer(b)=inner(b)) + escaping non-local return |
| 223 | `inlretexpr` | plain | #30 EXPRESSION-position return (elvis RHS / if-as-value / when-as-value / nested in an expr-body tail return) calling a lambda param — routed to the splice result-local + end-label; each call exercises BOTH the early expr-position return and the fall-through statement return |
| 224 | `inlretunit` | plain | #31 EXPRESSION-position `return unitFn()` (elvis RHS / if-as-value, Unit-typed) must EVALUATE the side-effecting call — the old arm dropped it (silent miscompile: counter stayed 0) |
| 225 | `inlretlocal` | plain | #31 lambda-LOCAL labeled `return@label expr` in EXPRESSION position routes via inlineReturnSubst (breakContinueExpr), not a raw returnExpr — a crossinline materialized carrier + a direct-invoke carrier; leaking a returnExpr made bir2cir MaterializeCarrier reject fail-loud |
| 226 | `inlretcoerce` | plain | BATCH-C (holistic inline-splice item 21): a value-type-nullable (Int?/UInt?) smart-cast `return@lambda` routed through the splice result-local reaches the bare-value slot already Nullable<T>.Value-UNWRAPPED — the unwrap is at expr()'s LEAF (narrowed-IrGetValue / IMPLICIT_CAST arms), so the spliced return arms intentionally do NOT mirror #32's return-site coerceValue (verified no-op). Covers param/property/local smart-cast, elvis, UInt?, generic T=Int |
| 227 | `ctor` | plain | _(foundational: no issue-guard comment)_ |
| 228 | `objex` | plain | _(foundational: no issue-guard comment)_ |
| 229 | `objgen` | plain | _(foundational: no issue-guard comment)_ |
| 230 | `nest` | plain | _(foundational: no issue-guard comment)_ |
| 231 | `scast` | plain | _(foundational: no issue-guard comment)_ |
| 232 | `vis` | plain | _(foundational: no issue-guard comment)_ |
| 233 | `throwx` | plain | _(foundational: no issue-guard comment)_ |
| 234 | `enumr` | plain | _(foundational: no issue-guard comment)_ |
| 235 | `reqnn` | plain | _(foundational: no issue-guard comment)_ |
| 236 | `precond` | plain | #73 M6/M7: precondition/error family + top-level repeat{} inline loop (moved to bir2cir) |
| 237 | `repeatnlr` | plain | #75: NON-LOCAL return + return@repeat + nested repeat + scope-fn-in-repeat through repeat{} (kotc carries the un-closured lambda body; bir2cir InlineSplice splices it) |
| 238 | `reif` | plain | _(foundational: no issue-guard comment)_ |
| 239 | `iter` | plain | _(foundational: no issue-guard comment)_ |
| 240 | `inner` | plain | _(foundational: no issue-guard comment)_ |
| 241 | `lazy` | plain | _(foundational: no issue-guard comment)_ |
| 242 | `volatile` | plain | @kotlin.concurrent.Volatile -> a real CLR volatile field: modreq(IsVolatile) + `volatile.` prefix (the C# volatile shape) on value-type/ref-type instance fields + a top-level static field |
| 243 | `deleg` | plain | _(foundational: no issue-guard comment)_ |
| 244 | `classdeleg` | plain | #81: CLASS delegation `class Foo : Bar by baz` — the frontend's synthetic `$$delegate_0` IrField + its ctor initializer must be emitted (single/two/expr/generic delegates) |
| 245 | `propref` | plain | _(foundational: no issue-guard comment)_ |
| 246 | `lateinitref` | plain | #66: a callable reference to a `lateinit var` property (bound `b::name` + unbound `Box::name`) lifts a KProperty over the backing FIELD (lateinitGet/setFieldExpr), not a get_/set_ accessor — was a whole-compile abort |
| 247 | `extpropref` | plain | #21: bound (`this::extProp` -> KProperty0) + unbound (`String::extProp` -> KProperty1) + mutable-bound (`this::varExtProp` -> KMutableProperty0, set() path) reference to a top-level EXTENSION property; get/set invoke the static ext accessor with the captured/passed receiver (was "KProperty2 has no lowering") |
| 248 | `rwp` | plain | _(foundational: no issue-guard comment)_ |
| 249 | `bymap` | plain | _(foundational: no issue-guard comment)_ |
| 250 | `topdeleg` | plain | #70: a TOP-LEVEL delegated property with an arbitrary getValue/setValue provider routes through `x$delegate.getValue/setValue` (static delegate field, null thisRef) — was a whole-compile abort (only member/local delegated props were routed) |
| 251 | `mapforin` | plain | _(foundational: no issue-guard comment)_ |
| 252 | `del2` | plain | _(foundational: no issue-guard comment)_ |
| 253 | `gen` | plain | _(foundational: no issue-guard comment)_ |
| 254 | `genctor` | plain | _(foundational: no issue-guard comment)_ |
| 255 | `gen2` | plain | _(foundational: no issue-guard comment)_ |
| 256 | `gen3` | plain | _(foundational: no issue-guard comment)_ |
| 257 | `gen4` | plain | _(foundational: no issue-guard comment)_ |
| 258 | `gen5` | plain | _(foundational: no issue-guard comment)_ |
| 259 | `gen6` | plain | _(foundational: no issue-guard comment)_ |
| 260 | `genbase` | plain | _(foundational: no issue-guard comment)_ |
| 261 | `genbaseext` | plain | _(foundational: no issue-guard comment)_ |
| 262 | `netbase` | plain | _(foundational: no issue-guard comment)_ |
| 263 | `netbase2` | plain | _(foundational: no issue-guard comment)_ |
| 264 | `netgen` | plain | _(foundational: no issue-guard comment)_ |
| 265 | `netgen2` | plain | _(foundational: no issue-guard comment)_ |
| 266 | `event` | plain | _(foundational: no issue-guard comment)_ |
| 267 | `loopjump` | plain | _(foundational: no issue-guard comment)_ |
| 268 | `netgen3` | plain | _(foundational: no issue-guard comment)_ |
| 269 | `fieldvis` | inject | _(foundational: no issue-guard comment)_ |
| 270 | `delegatearg` | inject | _(foundational: no issue-guard comment)_ |
| 271 | `delegobj` | inject | _(foundational: no issue-guard comment)_ |
| 272 | `threadlambda` | imports | _(foundational: no issue-guard comment)_ |
| 273 | `delegnull` | inject-nrt | _(foundational: no issue-guard comment)_ |
| 274 | `netenum` | inject | _(foundational: no issue-guard comment)_ |
| 275 | `injbase` | inject | _(foundational: no issue-guard comment)_ |
| 276 | `injfqn` | inject | _(foundational: no issue-guard comment)_ |
| 277 | `injstatic` | inject | _(foundational: no issue-guard comment)_ |
| 278 | `injuint` | inject | _(foundational: no issue-guard comment)_ |
| 279 | `ubyteinj` | inject | _(foundational: no issue-guard comment)_ |
| 280 | `c1net` | inject | _(foundational: no issue-guard comment)_ |
| 281 | `csext` | inject | _(foundational: no issue-guard comment)_ |
| 282 | `csextrecv` | inject | _(foundational: no issue-guard comment)_ |
| 283 | `genextval` | inject | _(foundational: no issue-guard comment)_ |
| 284 | `eventext` | inject | _(foundational: no issue-guard comment)_ |
| 285 | `ifaceevent` | imports | _(foundational: no issue-guard comment)_ |
| 286 | `tloverload` | inject | _(foundational: no issue-guard comment)_ |
| 287 | `vtprop` | inject | _(foundational: no issue-guard comment)_ |
| 288 | `netinterop` | inject | _(foundational: no issue-guard comment)_ |
| 289 | `firgap` | inject | _(foundational: no issue-guard comment)_ |
| 290 | `inherit` | inject | _(foundational: no issue-guard comment)_ |
| 291 | `geninj` | inject | _(foundational: no issue-guard comment)_ |
| 292 | `transinj` | inject | _(foundational: no issue-guard comment)_ |
| 293 | `cbk` | inject | _(foundational: no issue-guard comment)_ |
| 294 | `clriface` | inject | _(foundational: no issue-guard comment)_ |
| 295 | `clrimpl` | inject | _(foundational: no issue-guard comment)_ |
| 296 | `ifacechainvt` | inject | _(foundational: no issue-guard comment)_ |
| 297 | `clrifaceimpl` | imports | _(foundational: no issue-guard comment)_ |
| 298 | `clrifaceimplvt` | imports | _(foundational: no issue-guard comment)_ |
| 299 | `ixname` | inject | _(foundational: no issue-guard comment)_ |
| 300 | `clrasm` | inject | _(foundational: no issue-guard comment)_ |
| 301 | `selfref` | inject | _(foundational: no issue-guard comment)_ |
| 302 | `genim` | inject | _(foundational: no issue-guard comment)_ |
| 303 | `outref` | inject | _(foundational: no issue-guard comment)_ |
| 304 | `netattr` | inject | _(foundational: no issue-guard comment)_ |
| 305 | `stackalloc` | inject | _(foundational: no issue-guard comment)_ |
| 306 | `fmt` | plain | _(foundational: no issue-guard comment)_ |
| 307 | `mref` | inject | _(foundational: no issue-guard comment)_ |
| 308 | `cobuild` | imports | _(foundational: no issue-guard comment)_ |
| 309 | `genasync` | imports | genuine-async isolation: suspend fun with Task.Delay().await(), drained by blockOn |
| 310 | `suspendcatch` | imports | #78 Defect B: a suspend call INSIDE a catch handler (Select.kt:723 recoverAndThrow shape) — HoistSuspendingCatches lifts the handler out of the CLR catch clause so the SM can segment its suspension; the try body ALSO suspends (two-level dispatch) + multi-catch (both handlers suspend, per-clause capture) |
| 311 | `suspendintrinsic` | imports | #80: a direct user read of the top-level val COROUTINE_SUSPENDED in a suspendCoroutineUninterceptedOrReturn block — canonicalized to the SM's Suspended() marker in Rewrite (mis-owned by MemberCallSubstitution to the file class otherwise) |
| 312 | `suspendintrinsicowned` | plain | #157 (was #80-residual): a NON-suspend member (getResult shape) reads the top-level val COROUTINE_SUSPENDED — post-#89 kotc emits owner:null + prop:get (like every cross-module top-level val), and bir2cir binds it through the GENERAL owner-null resolver (prop:get -> get_COROUTINE_SUSPENDED -> TryResolveTopLevelStatic single-candidate -> IntrinsicsKt), NOT a COROUTINE_SUSPENDED special-case (that band-aid was deleted as redundant) |
| 313 | `suspendloop` | imports | #82: a structured collection loop (forArray + forEachInline) whose body spans a suspension — FlattenSuspendingLoops flattens it to CFG so the loop temps/element cross the resume as SM fields (else `load unknown var __inlsN$element`); + break/continue crossing the resume |
| 314 | `inlsuspend` | imports | _(foundational: no issue-guard comment)_ |
| 315 | `suspendnestedcapture` | imports | _(foundational: no issue-guard comment)_ |
| 316 | `comaindrain` | imports | _(foundational: no issue-guard comment)_ |
| 317 | `counit` | plain | _(foundational: no issue-guard comment)_ |
| 318 | `monitordrain` | imports | _(foundational: no issue-guard comment)_ |
| 319 | `cofinally` | imports | _(foundational: no issue-guard comment)_ |
| 320 | `coexc` | imports | _(foundational: no issue-guard comment)_ |
| 321 | `cocancel` | plain | _(foundational: no issue-guard comment)_ |
| 322 | `cocancelkt` | plain | _(foundational: no issue-guard comment)_ |
| 323 | `corestrict` | plain | _(foundational: no issue-guard comment)_ |
| 324 | `suspendco` | plain | _(foundational: no issue-guard comment)_ |
| 325 | `safecontresume` | imports | _(foundational: no issue-guard comment)_ |
| 326 | `coinline` | plain | _(foundational: no issue-guard comment)_ |
| 327 | `coevalorder` | plain | _(foundational: no issue-guard comment)_ |
| 328 | `cofieldorder` | plain | _(foundational: no issue-guard comment)_ |
| 329 | `coarrayorder` | plain | _(foundational: no issue-guard comment)_ |
| 330 | `lam1` | imports | _(foundational: no issue-guard comment)_ |
| 331 | `lam2` | imports | _(foundational: no issue-guard comment)_ |
| 332 | `suspendcapture` | imports | _(foundational: no issue-guard comment)_ |
| 333 | `suspendvalue` | imports | _(foundational: no issue-guard comment)_ |
| 334 | `suspendref` | imports | #67: a callable reference to a `suspend` function (top-level `::work` + bound member `d::apply`) lowered as a `newSuspendLambda` adapter (bir2cir builds the SuspendLambda SM); kotc emits only the suspend FACTS — was a whole-compile abort (KSuspendFunctionN type-token leak + no suspend-newDelegate lowering) |
| 335 | `suspendval2` | imports | _(foundational: no issue-guard comment)_ |
| 336 | `inlsuspendcarrier` | imports | _(foundational: no issue-guard comment)_ |
| 337 | `inlsuspendobj` | imports | _(foundational: no issue-guard comment)_ |
| 338 | `inlsuspendlaunch` | imports | _(foundational: no issue-guard comment)_ |
| 339 | `inlsuspendflow` | imports | _(foundational: no issue-guard comment)_ |
| 340 | `inlsuspendnest` | imports | _(foundational: no issue-guard comment)_ |
| 341 | `inlsuspendouter` | imports | _(foundational: no issue-guard comment)_ |
| 342 | `flowtransform` | imports | _(foundational: no issue-guard comment)_ |
| 343 | `inlsuspenddefault` | imports | _(foundational: no issue-guard comment)_ |
| 344 | `inlmatsetcap` | imports | _(foundational: no issue-guard comment)_ |
| 345 | `dsl` | plain | _(foundational: no issue-guard comment)_ |
| 346 | `object` | plain | _(foundational: no issue-guard comment)_ |
| 347 | `gfac` | plain | _(foundational: no issue-guard comment)_ |
| 348 | `xprop` | plain | _(foundational: no issue-guard comment)_ |
| 349 | `exprbody` | plain | _(foundational: no issue-guard comment)_ |
| 350 | `overload` | plain | _(foundational: no issue-guard comment)_ |
| 351 | `mfclosure` | plain | _(foundational: no issue-guard comment)_ |
| 352 | `mflambda` | plain | _(foundational: no issue-guard comment)_ |
| 353 | `arrops` | plain | _(foundational: no issue-guard comment)_ |
| 354 | `collrealkt` | plain | _(foundational: no issue-guard comment)_ |
| 355 | `mutcoll` | plain | _(foundational: no issue-guard comment)_ |
| 356 | `cmpord` | plain | _(foundational: no issue-guard comment)_ |
| 357 | `mutset` | plain | _(foundational: no issue-guard comment)_ |
| 358 | `hashset2` | plain | _(foundational: no issue-guard comment)_ |
| 359 | `iscoll` | plain | _(foundational: no issue-guard comment)_ |
| 360 | `starproj` | plain | _(foundational: no issue-guard comment)_ |
| 361 | `excmap` | plain | _(foundational: no issue-guard comment)_ |
| 362 | `mapfilter` | plain | _(foundational: no issue-guard comment)_ |
| 363 | `nan` | plain | _(foundational: no issue-guard comment)_ |
| 364 | `nestedtry` | plain | _(foundational: no issue-guard comment)_ |
| 365 | `trynullable` | plain | _(foundational: no issue-guard comment)_ |
| 366 | `tryexprop` | plain | _(foundational: no issue-guard comment)_ |
| 367 | `setlocalbox` | plain | _(foundational: no issue-guard comment)_ |
| 368 | `nancmp` | plain | _(foundational: no issue-guard comment)_ |
| 369 | `bytewiden` | plain | #93/#71: Byte/Short/UByte/UShort arith widens to Int/UInt & inc/dec/unaryMinus keep the declared return (bir2cir wraps the lowered op in a conv to dynRet); ilemit needs the unsigned Conv_U1/U2/U4/U8 arms — else the value truncates to the narrow left operand on box |
| 370 | `unsignedshr` | plain | #94: unsigned shr is LOGICAL (zero-filling) — bir2cir lowers a UInt/ULong `shr` to ">>>" (ilemit Shr_Un), not the sign-propagating ">>"; shl + signed shr are the non-regression checks |
| 371 | `structfloateq` | plain | #95: STRUCTURAL Double/Float equality (data-class equals/hashCode) is total-order (NaN==NaN true, +0.0!=-0.0) via clrDoubleEquals/clrFloatEquals, NOT IEEE ceq; a DIRECT a==b stays IEEE (ieee754equals) — last two lines are the non-regression guard |
| 372 | `structfloateqnull` | plain | #152: STRUCTURAL Double?/Float? equality (nullable data-class field) is total-order via null-safe bit-equality (nullableHasValue/nullableValue + clrDoubleEquals/clrFloatEquals), NOT boxed Double.Equals (IEEE: (-0.0).Equals(0.0)==true); null==null true, one null false, hashSet stays consistent |
| 373 | `floateqnull` | plain | #180: DIRECT/mixed nullable Double?/Float? `==` (frontend routes to ieee754equals with raw Nullable<T> operands; incl. `(x as Double?)==y` via SURFACE nullness + `!=` + single-eval) is null-safe IEEE-shaped (operand-hoist + raw binOp== core), NOT raw ceq over Nullable<T> structs (unverifiable IL). -0.0==0.0 true, NaN==NaN false, null==null true, one-null false; DISTINCT from the STRUCTURAL total-order #152 path |
| 374 | `whensubj` | plain | _(foundational: no issue-guard comment)_ |
| 375 | `safecallnv` | plain | _(foundational: no issue-guard comment)_ |
| 376 | `rangein` | plain | _(foundational: no issue-guard comment)_ |
| 377 | `userrange` | plain | #73 M2: `x in a..b` on a USER rangeTo/contains type dispatches the real contains(), not primitive comparisons |
| 378 | `duration` | plain | _(foundational: no issue-guard comment)_ |
| 379 | `nullcs` | plain | _(foundational: no issue-guard comment)_ |
| 380 | `inlonlyintr` | plain | _(foundational: no issue-guard comment)_ |
| 381 | `xmodtopval` | plain | _(foundational: no issue-guard comment)_ |
| 382 | `charseqlenref` | plain | _(foundational: no issue-guard comment)_ |

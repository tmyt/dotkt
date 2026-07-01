# Master task inventory — the de-duplicated "what's left" ledger

> **Canonical remaining-work list (2026-07-02).** This is a *de-duplicated* stocktake that consolidates the
> scattered task docs (`ship-tasks.md`, `remaining-tasks.md`, `bir2cir-migration-inventory.md`,
> `gap-analysis.md`, `future-work-interop.md`, `dotkt-interop-feedback.md`, `research-roadmap.md`,
> `coroutine-stdlib-port-plan.md`, `prioritized-tasks.md`). Where those docs disagree with reality, **this file +
> the code win** — many items those docs still mark "open" are actually DONE (see the META note). When you finish an
> item here, update it AND flag the stale source doc.

## META — the docs LAG reality (verified 2026-07-02)

A large fraction of the doc-listed "open" items are **already DONE** (this session or recently) and are NOT tasks:
`verify-il`/`verify-differential` `--scan-asm` removal · bir2cir `@ClrIntrinsic` substitution from ref.dll (the
`annClr` removal) · `@ClrRefArgument` (atomics byref, verified end-to-end) · `Span`/`stackBuffer`→`kotlin.clr` move ·
`--compat-bir`/`--native-cir` dual-track removal · stale-script cleanup (`build-dotkt-stdlib.sh`/`build-stdlib.sh`
deleted) · legacy `clr.Clr` app-façade reader (gone; its 26 samples deleted, 3 migrated to facadegen `import`) ·
`netType`→bir2cir / kotc-is-pure-Kotlin-frontend (#5). `research-roadmap.md` is almost entirely HISTORICAL (C#-backend
premise); `dotkt-interop-feedback.md` is a 2026-06 snapshot. **A doc-sync pass (phase ④) is needed.**

## The agreed execution order (user-directed 2026-07-02)

1. **Save this inventory** (this file). ✅
2. **Address bundles 【1】–【5】** below (the engineering remainder).
3. **Build-script cleanup + one-shot build** — a single script that builds facadegen · kotc · bir2cir · ilemit ·
   stdlib.jar · stdlib.ref.dll · stdlib.rt.dll · the nupack(s), and tidy the per-artifact scripts.
4. **Doc maintenance** — reconcile all task docs to reality (the META note above); archive `research-roadmap.md`.
5. **Coroutine** — the long-awaited one. It is *implementation grind, not a design fork* (the Task-based ABI is
   already decided); truly last. Plan = `coroutine-stdlib-port-plan.md` (see 【6】).

---

## 【1】 Layer-purity: retire kotc's hardcoded CLR lowerings  *(currency-corrected 2026-07-02)*
Sources: `bir2cir-migration-inventory.md`, `gap-analysis.md §2`, `ship-tasks.md §6–7`. **The 6-wave plan in those docs
is largely STALE** — a code-grounded currency check found:
- **The "lower each kind to a `clr.*` node + mirror in ilemit" mechanism is ABANDONED** (dropped with the
  `--native-cir`/`--compat-bir` removal, 2026-06-30). bir2cir emits only *reference tokens*
  (`clr.typeRef`/`clr.methodRef`/`clr.fieldRef`); the `clr.bin`/`clr.newobj`/`clr.call`/… cases in ilemit are
  **unreachable DEAD CODE** (`Emitter.Expressions.cs` has both `case "bin"` and `case "clr.bin"` → same emitter).
- **bir2cir's reference-metadata resolver + `@ClrIntrinsic` substitution is ALREADY BUILT and live** in production
  (`MemberCallSubstitution.Apply`, `bir2cir/Program.cs:2194`, wired `:118`, gated by `RefBuild` — rewrites plain
  `callInstance`/`callStatic` → `clrInstance`/`clrStatic`/`clrNew` on the BCL owner; reads ref.dll via
  `ReferenceMetadataIndex`; does `@ClrProperty`/`@ClrTypeAlias`/constrained-dispatch). So Wave 5's "heaviest resolver"
  is **essentially done** (`gap-analysis §1` "substitution ZERO" is stale).
- **Wave 3–4 are MOOT** — `this`/`local`/`setField`/`lateinitGet`/`byref*` are pure structural/physical nodes with NO
  CLR language knowledge; raw-through to ilemit codegen is correct now that the `clr.*` lower mechanism is gone. The
  per-method-type-env + same-module-resolver infra the 6-wave assumed is **unnecessary** (≈1/3 of the plan eliminated).

**Genuinely remaining scope:**
- **① Main body (mechanical, ~45+7 sites):** retire kotc's hardcoded CLR direct-lowerings — `System.Math`
  (`BirEmitter.kt:3854/3861/3892`), `System.String` (Format/Substring, `:3805/3914/3929`), `System.Convert`
  (`:3721`), `System.Char` (`:3941`), `System.Console.ReadLine` (`:3777`), `System.Text.RegularExpressions`
  (`:3787`), `Task.Delay` (`:1386`), `IComparable.CompareTo` (`:3195`), `IDisposable.Dispose` (`:2312`),
  `System.Lazy` (`:3177`), indexer `get_/set_Item` (`:3400/3414`), the generic `clrName→clrStatic` path
  (`:3533+`) → move to stdlib `@ClrIntrinsic` + let the (already-built) bir2cir `MemberCallSubstitution` consume it;
  then DELETE kotc's `clrName`/`annClr` read path (`BirEmitter.kt:4247`) so bir2cir is the SOLE substituter (today
  they run idempotently in parallel — `bir2cir/Program.cs:114-117`). Plus the 7 STILL-OPEN retire ops
  (`strRepeat`/`strReversed`/`split`/`console`/`listNew`/`setNew`/`mapNew`; `listNew`/`setNew` are emitted via a
  computed `kind` var at `BirEmitter.kt:3247`, NOT dead).
  - ✅ **`System.Math` (kotlin.math.*) — DONE 2026-07-02, the PILOT** (`5a3ab8e` kotc retire, `c80760a` bir2cir).
    Deleted `MATH_FUNCS` + the `BirEmitter.kt:3876-3893` emit site; MathClr.kt's `@ClrIntrinsic` bindings already
    existed (no stdlib change). Gate-neutral (fail-set identical before/after). **NB the `coerceIn/coerceAtMost/
    coerceAtLeast → Math.Min/Max/Clamp` sites (`:3837-3862`) are a SEPARATE family** — those stdlib funs are
    pure-Kotlin bodies with NO `@ClrIntrinsic` (retiring them is the "real-body" mechanism, not intrinsic subst),
    handle separately. **bir2cir gap found + fixed (recurs for other families):** the top-level `@ClrIntrinsic`
    index was NAME-keyed (first-wins), so arg-type-discriminated overloads collided (Math vs MathF; Double.* vs
    Single.* for isNaN/isInfinite/isFinite). Fixed by full-signature keying + ambiguity-guarded name fallback.
  - ✅ **`System.String` (partial) — DONE 2026-07-02** (`332462d` bir2cir arity fix, `3aec0a1` kotc retire). Retired the
    CLEANLY-substitutable `kotlin.text` String ops: `uppercase`/`lowercase` (@ClrIntrinsic ToUpper/ToLowerInvariant),
    `substring(startIndex)` (1-arg → @ClrIntrinsic Substring), the `NUMBER_PARSE` map (`"42".toInt()`/toLong/toDouble/
    toFloat/toShort/toByte → @ClrIntrinsic System.X.Parse), **`strRepeat`** (real StringBuilder body works), and the
    dead **`format`** lowering (JVM-ism; the CLR frontend jar has no `String.Companion.format`, so it never resolved —
    a stdlib binding, not a kotc lowering). **bir2cir gap fixed (reusable):** the bare-@ClrIntrinsic EXTENSION index was
    keyed `name|recvKey`, colliding across arities — `substring(String,Int)`@ClrIntrinsic captured the 3-arg
    `substring(String,Int,Int)` call → wrong `Substring(start,end)`. Now keyed `name|recvKey|paramCount`.
    **BLOCKED (kept lowered, NOT retired):** `trim`/`contains`/`startsWith`/`endsWith`/`replace`/`indexOf`/`padStart`/
    `padEnd`/**`strReversed`**/**`split`**/`substring(start,end)`/`isEmpty`/`isBlank` — their stdlib bodies are
    `CharSequence` extensions, so a System.String receiver hits the **String/CharSequence dual-representation** crash
    (InvalidProgram / EntryPointNotFound; `trim` also needs `::isWhitespace` method-ref lowering). Retire once
    bir2cir/ilemit bridge String↔CharSequence.
  - ✅ **`System.Char` — DONE 2026-07-02** (`3aec0a1`+Char commit, same kotc retire pass). Deleted the `CHAR_OPS` map +
    emit site; `CharClr.kt`'s `@ClrIntrinsic("System.Char.IsDigit"/"ToUpperInvariant"/…)` FQ bindings substitute via
    bir2cir's top-level-intrinsic-by-signature path → `clrStatic System.Char.*`. No stdlib change; gate-neutral (il-char
    run-correct).
  - ⛔ **`System.Convert` (`toString(radix)`, `:3721`) — BLOCKED, kept lowered.** bir2cir correctly attributes a plain
    call to the stdlib `StringNumberConversionsKt.toString(int,int)` digit-loop body, but that emitted body MISCOMPILES
    cross-module: base-2 is right (`"1010"`) but the letter-digit branch is not (`255.toString(16)` → `"ffffffff"`,
    `(-255).toString(16)` → `"1"`). Retiring ships a correctness regression, so the `System.Convert.ToString` lowering
    STAYS until the stdlib/emit bug (a `clrDigitToChar`/StringBuilder.insert path exercised only for radix>10) is fixed.
  - ✅ **`System.Console` (`println`/`print` + `readLine`) — DONE 2026-07-02** (`f1a456d`; batch 3, final mechanical
    batch). Retired the hardcoded `{"k":"console"}` node: kotc emits `println`/`print` as PLAIN top-level fun calls and
    bir2cir's `MemberCallSubstitution` substitutes them to `System.Console.Write`/`WriteLine` from `ConsoleClr.kt`'s
    `@ClrIntrinsic` bindings (top-level-intrinsic-by-name; both unambiguous → **NO stdlib or bir2cir change needed**).
    Value-type args box via ilemit `EmitArg`; the Kotlin collection→`clrCollToString` toString adapter is KEPT (calls a
    stdlib helper). Also deleted the now-dead ilemit `case "console"` consumer. `readLine()` deleted as DEAD (no
    `kotlin.io.readLine` in the CLR frontend jar; the API is `readln`/`readlnOrNull`@ClrIntrinsic→`Console.ReadLine`).
    Gate-neutral (36).
  - **⛔ BLOCKED (batch 3, NOT retired — the mechanically-retirable part of bundle 1 is now CLOSED; these belong to
    bundle 4 dual-rep/collection-bridge or the deferred delegate/coroutine families):**
    - **`use{}` / `IDisposable.Dispose` (`inlineUse`, `:2312`)** — a STRUCTURAL `try/finally` inline desugar, not a
      call-substitution. The only CLR bit (`close→System.IDisposable.Dispose`) is a `clrName` member-rename (`:931`,
      SHARED with the class-emit path), so a clean retire means restructuring `inlineUse` to inline the real stdlib
      body + route `close→Dispose` through bir2cir's clrName-migration — the same dual-rep bridge as collections.
    - **`by lazy` / `System.Lazy<T>` (`:3177`)** — STRUCTURAL delegate construction (`new System.Lazy<T>(Func<T>)` +
      `Value` prop). `kotlin.Lazy` is a Kotlin INTERFACE (UnsafeLazyImpl/…), the `→System.Lazy` map is a `netType`
      (app-only, `:4318`), and there is no `@ClrIntrinsic` factory to substitute. Deferred delegate/closure family.
    - **`compareTo` / `IComparable.CompareTo` (`:3195`/`:3205`)** — MIXED but dual-rep-blocked: the primitive arm is
      the primitive dual-rep, the user-`Comparable<T>` arm is a `constrained.` callvirt (structural CLR lowering, not
      `@ClrIntrinsic`), and `il-comparable` sits on the open Comparable-self dual-rep bug. Bundle-4 dual-rep.
    - **indexer `get_/set_Item` (`:3400`/`:3414`)** — `String s[i]`→`get_Chars` is String/CharSequence dual-rep (same
      class as batch-2-blocked String ops); the injected-`.NET`-indexer arm is per-sample facadegen metadata (NOT the
      stdlib ref.dll), so bir2cir's ref-sourced substitution cannot reach it. Bundle-4 dual-rep + facadegen interop.
    - **`listOf`/`setOf`/`mapOf` → `listNew`/`setNew`/`mapNew` (`:3247`/`:3254`)** — STRUCTURAL collection-literal
      factories; must retire TOGETHER with the `COLLECTION_MEMBER`/`COLLECTION_OPS` clrName table so "kotlin lists ARE
      BCL `List<T>`" stays coherent (the collection-bridge). Bundle-4.
    - **`System.Text.RegularExpressions` (`:3785`)** — CharSequence dual-rep + `MatchResult` adapters (`find`/`value`).
      Bundle-4 dual-rep.
    - **`Task.Delay` (`:1386`) — SKIPPED** (not blocked): it lives inside `coAwaitable`, the coroutine await/suspend
      machinery. Per the deferred-coroutine directive, untouched → bundle 6.

  > ### RETIRE-PATTERN RECIPE (hand this to each follow-up family: String/Convert/Char/Regex/Console/compareTo/…)
  > 1. **Precondition — confirm the binding exists stdlib-side.** grep `runtime/stdlib` for the target funs; each
  >    must carry `@kotlin.clr.ClrIntrinsic("System.X.Y")` (member) or an FQ top-level binding. If MISSING, ADD the
  >    binding stdlib-side (nested repo commit) — NEVER keep the kotc lowering. The bindings do nothing until bir2cir
  >    consumes them from the ref.dll.
  > 2. **Baseline.** Capture the sample's `dotkt.sh --run` output AND the verify-il fail-set (`build/fail-*` markers
  >    are reliable even when stdout races) BEFORE touching anything.
  > 3. **Retire in kotc.** Delete the hardcoded map + the `clrStatic/clrInstance` emit site in `BirEmitter.kt`. NO
  >    compat shim (CLAUDE.md). kotc must emit the PLAIN call (`callStatic owner=null` / `callInstance` on the bare
  >    Kotlin owner). Rebuild `:kotc:installDist`; dump the BIR to confirm it's now a plain call, not a `clrStatic`.
  > 4. **Verify bir2cir substitutes.** Rebuild bir2cir; run the sample. Inspect the CIR: the plain call must become
  >    `clrStatic/clrInstance` on the BCL owner with the right `argTypes`. If a call is left un-substituted or
  >    mis-routed, the fix is in **bir2cir (`MemberCallSubstitution`) or the stdlib binding — NEVER re-add the kotc
  >    lowering.** Watch for: (a) **overloads that map to different BCL statics** → the by-name collision fixed here
  >    (full-sig key); (b) **non-intrinsic sibling overloads with real bodies** (e.g. `Double.pow(Int)`) must MISS the
  >    intrinsic map and fall through to `_attributeTopLevelOwner`/rule-3; (c) **extension receiver** threaded as the
  >    first arg (kotc emits `owner=null callStatic` with the receiver leading `args`); (d) receiver-drop on
  >    property/indexer families.
  > 5. **Gate.** Re-run verify-il; the fail-set must be UNCHANGED vs the baseline (gate-neutral). Commit incrementally
  >    (bir2cir extension first — it's backward-compatible; then the kotc deletion). Co-Author + Claude-Session trailer.
- **② Cleanup (low-risk, bulk):** delete the 18 DEAD retire-list ilemit cases (`listGet`/`mapGet`/`associate*`/
  `groupBy`/`linq*`/`tupleNew`/… in `Emitter.Expressions.cs`) + the native-cir remnant physical `clr.*` cases
  (`clr.bin`/`clr.newobj`/`clr.call`/…).
- **③ Deferred:** Wave 6 (`delegateNew`/`boundDelegateNew`/`delegateInvoke`/`closureNew` — plumbing, low-pri;
  `delegateInvoke` gated on the inline phase) + `clrStaticField`/coroutine hardcode (coroutine phase).

## 【2】 stdlib completeness  *(#1 rt-green residual)*
- **~363 unbound `actual`s → `@ClrIntrinsic`** (Arrays / Char / StringBuilder / Unsigned / Regex families).
- The 25 "retire from compiler" ops in 【1】 become real stdlib `@ClrIntrinsic`.

## 【3】 facadegen .NET interop breadth  *(#4 + interop-feedback)*
Sources: `ship-tasks.md #4`, `future-work-interop.md #4`, `dotkt-interop-feedback.md`, `research-roadmap.md I1`.
- **static `.Companion` routing = the `il:injstatic` bug** (an app-injected static-companion call misroutes into the
  stdlib `<>dotkt_ClrH_` helper → unresolved method). Flagged 2026-07-02.
- `op_*` operators · C#-origin extension methods · **dual-rep collision** (`import System.Text.StringBuilder` vs the
  stdlib alias).
- **(3) generic-type members collapse to `Any?`** — inject `IList<T>`/`ICollection<T>`/`IEnumerable<T>`/`List<T>`
  construction types (reaches `Application.Resources.MergedDictionaries`).
- **(4) delegate-type args collapse to `Any?`** → map delegate → Kotlin function type `(A,B)->R`.
- **(5) aliased import silently ignored** (`import … as X`) → support + warn on non-injection.
- **(6)/future#4/roadmap-I1 transitive / on-demand type injection** — chain-inject an import type's member
  arg/return/property types (1–2 hops).
- **generic-type FIR direct injection (roadmap I1, L)** — `List<T>`/`Dictionary<K,V>` façade-free (last hole in the
  injection path).
- I4 remnants: `out`/`ref`, nullable value types, .NET enum import, generic delegates.

## 【4】 ilemit codegen bugs  *(interop-feedback (10)-(14) + the verify-il 36 clusters)*
- **(10)** `object` singleton — no .NET lowering. **(11)** cross-file top-level mutable property (`field XKt.foo not
  found`). **(12)** BCL generic instantiated with a user type (`new HashSet<UserType>` → `TypeBuilderInstantiation`).
  **(13)** generic factory fn → invalid IL (`fun <T> state(i:T)` type-arg dropped, ilverify `StackUnexpected`).
  **(14)** multi-file cross-package `TypeBuilder` Save order.
- **verify-il 36 failing → root-cause clusters** (36 = compile 5 / ilemit 11 / ilverify 20; NO coroutine samples —
  those were deleted):
  - cross-module default args (`bmore`/`bymap`/`fmt`/`mapfilter`) — the frontend jar drops default VALUES.
  - value-type array `sort` (`arrops` AccessViolation) · `mapNotNull` nullable generic · for-generic-receiver iterator.
  - dual-rep types (`regex` CharSequence, `result` Nullable, `comparable` self-ref).
  - enum-with-body (`enumbody`/`enumr`) — `Op.get_sym not found`.
  - generic closure/HOF (`genclosure`/`genhof`) · virtual-property override dispatch (`netbase2`).
  - long-standing ilverify-only noise (`customexc`/`tryexpr`/`mc1`/`funref`) — runs correctly, ilverify complains.

### 【4-A】 String ↔ CharSequence dual-representation — FIX PLAN  *(the highest-value bundle-4 blocker; 2026-07-02)*
> **One blocker, wide gate.** Solving this retires the whole remaining String family (`trim`/`trimStart`/`trimEnd`/
> `contains`/`startsWith`/`endsWith`/`replace`/`indexOf`/`padStart`/`padEnd`/`split`/`substring(2-arg)`/`isEmpty`/
> `isBlank`/`strReversed`), the indexer `s[i]`→`get_Chars` retire, the Regex retire (its inputs are `CharSequence`),
> and the verify-il `il-str`/`il-substr`/`il-regex` fails. It is investigation-complete; hand to the follow-up agents below.

> ### ✅ FOUNDATION (A) DONE — 2026-07-02 (bir2cir `StringCharSequenceBridge`) — but the adapter is **app-local**, and it does NOT by itself unblock B
> **What landed.** bir2cir gained a `StringCharSequenceBridge` pass (`toolchain/bir2cir/Program.cs`, app builds only,
> gated on `attributeTopLevelOwner`, runs after `MemberCallSubstitution`/`IteratorConsumerNormalization` and before
> `BirTypeLowering`). It wraps a value whose STATIC type is provably `String` (a `const` string, a String-typed
> local/param read via a forward name→type env, a String cast, or a `ret`/`retType`=String call) flowing into a
> `<>dotkt_CharSequence` slot at four sites — a call's CharSequence-typed **arg** (this covers site (a) the
> extension **receiver**, which is arg[0]/sig[0], AND site (b) an ordinary CharSequence param), (c) a `return` into a
> CharSequence return type, (d) a store into a CharSequence-typed local `var`, (e) an `as CharSequence` cast — into
> `new <>dotkt_StringCharSequence(str)`. **Purely additive:** it wraps ONLY positively-String values (never an
> already-`<>dotkt_CharSequence` StringBuilder/user-CharSequence), so kotc's `STRING_OPS` (which lowers a
> statically-String receiver directly to `System.String` methods, e.g. `il-str`'s `contains`/`trim`) and every
> passing sample are untouched → **verify-il gate-neutral**. Verified: `val cs: CharSequence = "abc"; cs.length`=3,
> `cs[1]`='b', a String literal to a `CharSequence` param=5, `sub=cs.subSequence(0,2); sub.length`=2/`sub[0]`='a';
> ilverify-clean.
>
> **KEY CORRECTION to the plan below (④A said "adapter in the STDLIB").** The synthetic `<>dotkt_CharSequence` is
> emitted **per-assembly** — the app defines its OWN copy, DISTINCT from the one in the rt stdlib dll. A **stdlib**
> adapter would implement the rt-dll's `<>dotkt_CharSequence`, which the app's interface dispatch
> (`callvirt <app>::<>dotkt_CharSequence::get_length`) cannot find on it → `EntryPointNotFoundException`. So the
> adapter MUST implement the **app's** synthetic → **bir2cir injects it into the app assembly** (a `<>dotkt_StringCharSequence`
> class, String-backed, modeled byte-for-byte on the verified user `class S : CharSequence` CIR shape), exactly where
> kotc injects the synthetic interface. There is **NO stdlib change** for foundation A. (`StringCharSequence.kt` was
> tried in the stdlib first and REMOVED — it is dead cross-assembly.)
>
> **⚠️ THE DEEPER BLOCKER for (B) — ✅ RESOLVED 2026-07-02 (CANONICALIZATION, option A).** Foundation A fixed only
> **intra-assembly** CharSequence polymorphism; calling a **stdlib** CharSequence-extension (`StringsKt.*`, rt dll) with
> an app value still crossed the app↔rt synthetic boundary (`EntryPointNotFoundException` — the app's
> `<>dotkt_CharSequence` copy is a DISTINCT CLR type from the rt dll's). **Canonicalization now makes the app REFERENCE
> the rt dll's `<>dotkt_CharSequence` instead of re-emitting its own** (ilemit `93f…`/this session). Two ilemit changes,
> both in CLR type-resolution (kotc/bir2cir unchanged): (1) **pass-1 skips the local definition** of a synthetic in
> `CanonicalSynthetics` (currently `{<>dotkt_CharSequence}`) when it already resolves in a `--ref`'d assembly —
> self-correcting, so a `--no-stdlib` build or the stdlib's OWN ref/rt build (ilemit gets no `--ref` there) still emits
> the canonical copy locally; (2) the pass-2 MethodImpl loop **binds a user `class S : CharSequence` (and the injected
> `<>dotkt_StringCharSequence` adapter) to the EXTERNAL canonical interface by reflection**, exactly like a `clr:`
> interface. Reference/method resolution (`MapType`/`FindMethod`/`AddInterfaceImplementation`) already routed a
> non-`_types` `@<>dotkt_X` through `ResolveType`→reflection over the `--ref`'d rt dll, so no call-site changes were
> needed. Proven: before = `EntryPointNotFoundException at <>dotkt_CharSequence.get_length() at StringsKt.hasSurrogatePairAt`;
> after = both `S("hi").hasSurrogatePairAt(0)` (user CharSequence) and `"hi".hasSurrogatePairAt(0)` (String → foundation-A
> adapter → stdlib ext) RUN. New sample `il-charseqx`; `il-charseq` stays green (now implements the canonical type);
> verify-il gate-neutral (36-fail set identical, PASS +1), verify-ktproj 9/9. **B / indexer / Regex are now UNBLOCKED**
> (see ⑧). The other shared synthetics (`Result`/`KProperty`/`KIterator_*`/`RWProperty_*`) were intentionally NOT
> canonicalized (scoped to `CharSequence`; each needs its own cross-assembly verification). **`il:injstatic` is NOT the
> same root cause** — it is a rule-3 *misrouting* of an app facadegen-injected static-companion member into the
> non-existent stdlib `<>dotkt_ClrH_<Type>` body-hoist helper (a facadegen/companion-resolution bug), not the shared-
> synthetic *duplication* pattern.

**① Mechanism (code-grounded).** Kotlin `String : CharSequence`. On CLR `kotlin.String` is `@ClrTypeAlias("System.String")`
(`runtime/stdlib/clr/builtins/String.kt:22`) — a **sealed** BCL type; its CharSequence surface is bound in place via
`@ClrIntrinsic("Length")` on `length` and `@ClrIntrinsic("get_Chars")` on `get(i)` (`String.kt:33/42`). But
`kotlin.CharSequence` has **no faithful BCL equivalent**, so kotc *synthesizes a monomorphic interface*
`<>dotkt_CharSequence` with `get_length()`/`get(int):char`/`subSequence(int,int)` (`BirEmitter.kt:357-369`
`charSeqIface`/`charSeqIfaceDefs`; `birType` maps `kotlin.CharSequence`→`@<>dotkt_CharSequence` at `:4307`; the
declaring-owner spec at `ownerSpec :1750`; member-name routing at `clrIfaceMemberName :934`). ilemit emits it as an
ordinary abstract interface (`ilemit/Program.cs:239/260/690`; `MapType` resolves `@<>dotkt_CharSequence` from `_types`
`:3070`). A **user** `class S : CharSequence` links this synthetic and works (`il-charseq` — the pure-Kotlin side). The
problem: **`System.String` (sealed) does NOT implement `<>dotkt_CharSequence`**, and the stdlib's real String ops are
`CharSequence` *extensions* whose compiled bodies dispatch `length`/`get`/`subSequence` against the synthetic-interface
receiver.

**② Exact failure (repro captured, tree left clean).** kotc emits e.g. `"hello".contains("ell")` as
`callStatic kotlin.text.StringsKt.contains` with `sig=@<>dotkt_CharSequence,@<>dotkt_CharSequence,bool` while the pushed
args are `System.String` constants (`scratchpad/str-cir/app.cir.json:144`) → the static-call boundary passes a
`System.String` where `<>dotkt_CharSequence` is required → **InvalidProgramException / EntryPointNotFound**. `trim()` is
the same class *plus* an explicit cast: the stdlib `String.trim()` body is `(this as CharSequence).trim().toString()`
(`runtime/stdlib/src/kotlin/text/Strings.kt:181`) → `castclass <>dotkt_CharSequence` on a sealed `System.String` →
InvalidCast; the target `CharSequence.trim(predicate)` body reads `length`, indexes `this[i]`, calls `subSequence`
(`Strings.kt:77`) against the synthetic. bir2cir has **no** rule for CharSequence — it only lowers `@ClrTypeAlias` owners
from the ref.dll (`bir2cir/Program.cs:2060/2101`), and CharSequence has none, so the synthetic survives to ilemit. This
is exactly why kotc still hard-lowers `STRING_OPS` (`BirMappings.kt:28`, consumed `BirEmitter.kt:3905`).

**③ Precedent — why this ISN'T like the already-fixed dual-reps.** Every fixed dual-rep aliased to a **real BCL type the
values genuinely are**: `Iterable`→`@ClrTypeAlias("…IEnumerable")` (works because `List<T>` *is* `IEnumerable<T>` —
`builtins/Collections.kt:13`), Exception→`System.Exception` (`builtins/Throwable.kt`), Closeable→`IDisposable`,
Comparator→`IComparer` (since retired to a plain fun-interface), Comparable→`@ClrTypeAlias("System.IComparable")` +
`@ClrIntrinsic("CompareTo")` (`builtins/Comparable.kt:21`). Shared mechanism: `typeDef` routes CLR-bound supertypes to
`clr:`/`clrg:` (`BirEmitter.kt:1143/1164`); bir2cir drops the alias def + rewrites member calls to the BCL owner. **CharSequence
is the sole dual-rep with NO alias target**: `System.String` is sealed (can't add the interface) and String/StringBuilder
share **no** BCL indexed-char+length interface. So `@ClrTypeAlias` cannot solve it — it needs a **materializing adapter**.

**④ Recommended fix — HYBRID (Codex-confirmed).**
- **(A) Foundation — a String→CharSequence adapter, in the STDLIB, wrapped by bir2cir.** Define a stdlib
  `internal class` implementing `CharSequence` (so it *is* `<>dotkt_CharSequence`) that delegates `length`→
  `System.String.Length`, `get`→`get_Chars`, `subSequence`→`Substring` (its members carry the same `@ClrIntrinsic`s String
  already uses). **bir2cir inserts the wrap** wherever a statically-`String` value flows into a `<>dotkt_CharSequence` slot
  — extension-receiver arg, ordinary arg, return, field/local store, and explicit `as CharSequence` (for an `Any?`→
  CharSequence cast, a helper that adapts a runtime `System.String` and otherwise does the plain interface cast).
  bir2cir already tracks the static type needed to detect this (`MemberCallSubstitution` ctx, `Program.cs:2253`). This
  makes the **entire** CharSequence-extension surface (dozens of funs, not just STRING_OPS) plus genuine polymorphism
  (`val cs: CharSequence = "abc"; cs.isBlank()`) work — B alone cannot, since it only helps *statically*-String receivers.
- **(B) Per-op directness — retire cleanly-substitutable ops WITHOUT the bridge** (the established retire recipe, member
  substitution): ops that already have a **String-surface-only** actual — `substring(1&2-arg)` (`StringsClr.kt:223/231`
  via `nativeSubstring`@ClrIntrinsic), `startsWith(String,…)` (`:235`), `endsWith(String,…)` (`:249`),
  `replace(Char,Char)` (`:73`) — retire immediately (overload resolution prefers the `String` actual; its inner
  `length`/`substring`/`==` all substitute). Ops that are **only** `CharSequence` extensions or whose String actual casts
  to CharSequence — `contains`, `indexOf`, `trim*`, `padStart`/`padEnd`, `split`, `reversed`, `isEmpty`/`isBlank` — either
  get a new String `@ClrIntrinsic` actual (`contains(String)`→`Contains`, `indexOf`→the existing `nativeIndexOf`
  `@ClrIntrinsic("IndexOf")`) or ride the (A) bridge. NB `replace(String,String)`'s String actual (`:90`) calls public
  `indexOf`, which is CharSequence-only — so it needs the bridge or an ordinal-`indexOf` String actual first.

**⑤ Layer placement (per CLAUDE.md).** Adapter **TYPE = stdlib** (owns the Kotlin↔BCL semantics; keeps ilemit dumb).
Wrap-insertion + detection = **bir2cir** (the Kotlin↔CLR relation; runs in the non-ref substitute/app builds only). kotc
keeps synthesizing `<>dotkt_CharSequence` (frontend synthetic machinery — legitimate) and, once (A)+(B) land, **deletes
`STRING_OPS` + the `s[i]` indexer lowering** (`BirEmitter.kt:3905`, indexer `:3400/3414`) — no new kotc CLR knowledge.
ilemit only emits the interface + the adapter class = pure CLR codegen, **no Kotlin knowledge added**.

**⑥ Unblock scope & risk.** Unblocks: bundle-1 ① residue (`trim`/`contains`/`startsWith`/`endsWith`/`replace`/`indexOf`/
`padStart`/`padEnd`/`strReversed`/`split`/`substring(2-arg)`/`isEmpty`/`isBlank`), the indexer `s[i]`→`get_Chars` retire,
the Regex retire (CharSequence inputs), and verify-il `il-str`/`il-substr`/`il-regex`. **`il-charseq` is NOT this bug** —
it is the *user*-CharSequence path (`class S : CharSequence`) that rides the synthetic directly; verify it independently.
Risk: retiring `STRING_OPS` regresses `il-str`/`il-substr` if the bridge/substitution is incomplete → gate each op via the
retire recipe (baseline the fail-set, require gate-neutral). Cost: one allocation per String→CharSequence coercion
(acceptable; optimize hot paths with B). Watch the `as CharSequence`-from-`Any?` runtime-type-check helper.

**⑦ Sibling dual-reps — the adapter technique does NOT transfer; each needs a separate (smaller) plan.**
- **Comparable-self (`compareTo`, `il-comparable`)** — already `@ClrTypeAlias("System.IComparable")` +
  `@ClrIntrinsic("CompareTo")` (`builtins/Comparable.kt:21`), lowered via a `constrainedCall` to `IComparable<T>`
  (`BirEmitter.kt:3205`). Blocker is **ilemit self-referential-generic interface-token resolution**
  (`TypeBuilder.GetMethod` on `IComparable<SelfType>`), which MEMORY `value-type-generic-interface-token` reports FIXED for
  *value* types — re-verify the user-type self-ref path; NO adapter needed.
- **collection-element / `listNew` (`listOf`/`setOf`/`mapOf`)** — collections HAVE a BCL representation (`List<T>`, already
  `@ClrTypeAlias` in `builtins/Collections.kt`); this is the **collection-bridge**: retire the `listNew`/`setNew`/`mapNew`
  factories together with the `COLLECTION_MEMBER`/`COLLECTION_OPS` clrName tables, riding the Iterable→IEnumerable
  precedent (`@ClrTypeAlias`), NOT an adapter.

**⑧ Follow-up agents.** (1) ~~bir2cir+stdlib agent — foundation A~~ **✅ DONE 2026-07-02** (app-local injection; see the
STATUS box above). (2) ~~synthetic-UNIFICATION agent~~ **✅ DONE 2026-07-02 (CANONICALIZATION, ilemit)** — the app now
references the rt stdlib dll's `<>dotkt_CharSequence` (ilemit pass-1 skip + pass-2 reflection MethodImpl; `il-charseq`
green, `il-charseqx` new-pass; see the STATUS box). The design fork resolved to **ilemit-resolves-external** (NOT
kotc-emits / bir2cir-repoints) — kotc keeps synthesizing the token, ilemit derives "already in a `--ref` → reference it".
(3) **stdlib+kotc retire agent (NOW UNBLOCKED)** — do the per-op B retires (`trim`/`contains`/`startsWith`/`endsWith`/
`replace`/`indexOf`/`padStart`/`padEnd`/`split`/`strReversed`/`substring(2-arg)`/`isEmpty`/`isBlank`) + delete
`STRING_OPS` and the `s[i]` indexer lowering under the retire recipe, gate-neutral. Each op's stdlib body is a
`CharSequence` extension whose String receiver now coerces via foundation-A's bridge to the canonical interface.
(4) **Regex agent (NOW UNBLOCKED)** — retire Regex (its `CharSequence` inputs then coerce). Comparable-self and the
collection-bridge are separate tracks (own agents), not gated on this fix.

## 【5】 exception map → `@ClrTypeAlias`  *(#2)* — ✅ DONE (verified 2026-07-02; was already complete)
- `BirMappings.NET_EXCEPTIONS` DELETED (kotc `5907510`); the 11 stdlib exception classes carry `@ClrTypeAlias` (stdlib
  `c119dd8`, `runtime/stdlib/clr/builtins/Throwable.kt`) with 11/11 parity to the retired map; bir2cir substitutes them
  to `System.*` (verified via metadata: the 11 classes are absent from the rt.dll TypeDefs, present only as TypeRefs).
  Samples throwx/reqnn/customexc/il-exc PASS. (`tryexpr`'s last-line crash is the unrelated `sum`-over-lazy-`map`
  collection bug in 【4】, NOT exceptions.) The `clr.Clr` sample quarantine #2 also listed is DONE (26 deleted + 3
  migrated this session).

---

## 【6】 Coroutine  *(deferred — truly last; implementation grind, not a design fork)*
Sources: `coroutine-stdlib-port-plan.md`, `gap-analysis.md §3`, `remaining-tasks.md D`, `research-roadmap.md C`.
- Lock design gates **G1–G6** (G6 BCL Task binding = biggest risk) → **Phases 1–6** (ilemit resolves `kotlin.*`
  coroutine types during stdlib-compile → port `TypedCont<T>` + suspended sentinel → port `Builders`
  (Root/Future/AwaitOnto/RunBlocking/StartCoroutine) → port the sequence builder → end-to-end verify → retire
  `DotKt.Runtime/Coroutines.cs`).
- C2 `CancellationToken` in the ABI (S). C4 structured concurrency (`Job`/`CoroutineScope`/`launch`/`async`, XL —
  compiling kotlinx, "Track 2").

## 【7】 1.0 ship gate  *(non-code / production)*
Sources: `remaining-tasks.md F`, `research-roadmap.md Track P/X`.
- **Licensing / attribution** (KotlinForCLR Apache-2.0 compliance, NOTICE) — **ship-blocking**.
- **User docs** (getting-started, `.ktproj`, importing .NET types, supported/unsupported feature matrix).
- **Distribution** (`dotnet new ktproj` template, NuGet/SDK, versioned release, self-contained, remove relative-path
  deps).
- Diagnostics quality (source-position messages, a standing `-Xverify-ir`-equivalent gate) · boundary null (platform
  type `T!`) · incremental compilation · perf (compile time + generated code) · VS/VS Code · CI (sample-matrix
  expansion, Avalonia cache) · version/support policy (Kotlin 2.2.0 pin, TFMs, semver).

## 【8】 Accepted known limitations  *(NOT tasks)*
- round-trip **② companion call / ③ fun-interface lambda SAM / ④ enum class / ⑥ non-const default** — all pinned
  Kotlin 2.2.0 plugin-FIR-API limits (unfixable without unpinning; documented in `dotkt-semantics.md §10`).
- context receivers (`-Xcontext-parameters` rejected) · `object` singleton round-trip consumption · generic-class
  member `suspend` (BadImageFormat) · `Pair<T,T>` generic construction (`Pair2<A,B>` workaround) · private/internal
  not exported.

## 【9】 Doc hygiene  *(phase ④)*
- `research-roadmap.md` — mostly HISTORICAL → mark/archive. `dotkt-interop-feedback.md` — 2026-06 snapshot.
- Many doc "open" items are stale (done — see META) → one reconciliation pass.
- `@ClrRefArgument(index)` vs `@ClrRefArguments(mask)` doc inconsistency (the impl is a per-param `VALUE_PARAMETER`
  marker, not a bitmask).
- facadegen REQ7 design-inconsistency prose (reads `@Clr`, legacy `package clr` generation).
- Heavy cross-doc duplication: the bir2cir 6-wave, the coroutine port, transitive injection, and diagnostics/dist all
  appear in 3–4 docs each — consolidation has high value.

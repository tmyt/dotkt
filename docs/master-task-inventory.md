# Master task inventory — the de-duplicated "what's left" ledger

> **Canonical remaining-work list (2026-07-02).** This is a *de-duplicated* stocktake that consolidates the
> scattered task docs (`ship-tasks.md`, `remaining-tasks.md`, `coroutine-stdlib-port-plan.md`, and — now in
> `docs/archive/` — `bir2cir-migration-inventory.md`, `gap-analysis.md`, `future-work-interop.md`,
> `dotkt-interop-feedback.md`, `research-roadmap.md`, `prioritized-tasks.md`). Where those docs disagree with reality, **this file +
> the code win** — many items those docs still mark "open" are actually DONE (see the META note). When you finish an
> item here, update it AND flag the stale source doc.

## META — the docs LAG reality (verified 2026-07-02)

A large fraction of the doc-listed "open" items are **already DONE** (this session or recently) and are NOT tasks:
`verify-il`/`verify-differential` `--scan-asm` removal · bir2cir `@ClrIntrinsic` substitution from ref.dll (the
`annClr` removal) · `@ClrRefArgument` (atomics byref, verified end-to-end) · `Span`/`stackBuffer`→`kotlin.clr` move ·
`--compat-bir`/`--native-cir` dual-track removal · stale-script cleanup (`build-dotkt-stdlib.sh`/`build-stdlib.sh`
deleted) · legacy `clr.Clr` app-façade reader (gone; its 26 samples deleted, 3 migrated to facadegen `import`) ·
`netType`→bir2cir / kotc-is-pure-Kotlin-frontend (#5). `research-roadmap.md` is almost entirely HISTORICAL (C#-backend
premise); `dotkt-interop-feedback.md` is a 2026-06 snapshot. **The doc-sync pass (phase ④) LANDED 2026-07-03** —
both (plus 5 more superseded docs) moved to `docs/archive/`; see 【９】.

## ✅ BUNDLES 1–5 CLOSED (2026-07-03)

Authoritative close-out gate: **run-FAIL 0 / PASS(run) 132 / verify-ktproj 9/9** (phase arc: fail-names 36→6,
run-FAILs 15→0, PASS 101→132, zero regressions). *Re-baselined 2026-07-03 (scripts overhaul): the gate's stdout
race is fixed, so the 4 coroutine-deferred crashers now PRINT as FAILs — the truthful figure is PASS(run) 132 /
run-FAIL exactly `chunk`/`cobuild`/`collops2`/`seq`; same underlying state, honestly counted (see CLAUDE.md).*
The accepted tail, NOT open work:
- **ilverify-formal-only (6, run-correct):** `collrealkt`/`gen3`/`iter`/`iterable` (+ `chunk`/`collops2`, also
  sequence-gated) — the IL runs correctly; ilverify's strict-verifiability complaints = documented noise.
- **coroutine/SequenceScope-deferred (4, by design):** `chunk`/`cobuild`/`collops2`/`seq` — unblocked by the
  bundle-6 sequence-builder/coroutine work, not before. **`verify-roundtrip`'s suspend section is the same bucket**
  (investigated 2026-07-03: the consumer runs `11/(4,6)/Hi, Vec` correctly, then SIGABRTs at the first suspend
  member call — the deferred throwing stub, NOT a 50c2c9f regression; the markers/packaged/generic sections PASS).
- `il:injstatic` was NOT the .Companion convention — a genuine bir2cir rule-3 classifier misfire on
  facadegen-injected owners (fixed `c8f5345`, mirroring the event-accessor precedent `32a1da6`). The `.Companion`
  requirement itself was then ALSO lifted (`50c2c9f`, 2026-07-02): implicit `App.start` now resolves via the eager
  companion link + `FirInternals.java` shim — the old "frontend limitation" was a FIR wiring gap, not a K2 limit.

## The agreed execution order (user-directed 2026-07-02)

1. **Save this inventory** (this file). ✅
2. **Address bundles 【1】–【5】** below (the engineering remainder). ✅ **CLOSED 2026-07-03 (see above).**
3. **Build-script cleanup + one-shot build** — a single script that builds facadegen · kotc · bir2cir · ilemit ·
   stdlib.jar · stdlib.ref.dll · stdlib.rt.dll · the nupack(s), and tidy the per-artifact scripts.
4. **Doc maintenance** — ✅ **DONE 2026-07-03**: 7 superseded docs → `docs/archive/` (research-roadmap,
   dotkt-interop-feedback, future-work-interop, prioritized-tasks, gap-analysis, bir2cir-migration-inventory,
   bir2cir-handoff); dotkt-semantics overhauled (TOC + suspend-hot/Appendable/enum/value-class/.Companion);
   `docs/user/` created (getting-started / using-dotnet-from-kotlin / kotlin-on-clr-differences); README refreshed.
5. **Coroutine** — the long-awaited one. It is *implementation grind, not a design fork* (the Task-based ABI is
   already decided); truly last. Plan = `coroutine-stdlib-port-plan.md` (see 【6】).

---

## 【1】 Layer-purity: retire kotc's hardcoded CLR lowerings  *(currency-corrected 2026-07-02)*
Sources: `archive/bir2cir-migration-inventory.md`, `archive/gap-analysis.md §2`, `ship-tasks.md §6–7`. **The 6-wave plan in those docs
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
  (`:3787`), `IComparable.CompareTo` (`:3195`), `IDisposable.Dispose` (`:2312`),
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
    **UPDATE — bundle 4-B (2026-07-02) retired most of these** now that CharSequence is canonical + the bridge runs on
    the RT stdlib build (see §4-A ⑧(3)): `contains`/`startsWith`/`endsWith`/`indexOf`/`split`/`substring(start,end)`/
    `isEmpty`/`isNotEmpty` RETIRED; `s[i]`→get_Chars + Regex RETIRED. **STILL LOWERED (distinct deeper stdlib-body
    bugs, NOT dual-rep):** `trim`/`trimStart`/`trimEnd` (`Char::isWhitespace` method-ref not lowered), `strReversed`
    (`StringBuilder(CharSequence)` no .NET ctor), `padStart`/`padEnd` (StringBuilder append/capacity), `replace(String,
    String)` (StringBuilder `append(seq,start,END)`), `isBlank`/`isNotBlank` (CharSequence iteration).
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
    - **`Task.Delay` (`coAwaitable`) — ✅ REMOVED (2026-07-03, pre-coroutine hardening B1):** the bespoke
      `kotlinx.coroutines.delay` → `Task.Delay` lowering was dead pre-stdlib legacy (reachable only from the
      `sequence{}` restricted-suspension CPS path, where an unrestricted suspend call like `delay` cannot appear;
      unrestricted suspend fns emit plainly with `"suspendCall":true`). Deleted — not aliased — so the Task-based
      coroutine bundle (6) does not inherit it as a load-bearing hack. `il-cobuild` behavior unchanged (still the
      legible deferred-coroutine `NotSupportedException` stub at run).

  > ### RETIRE-PATTERN RECIPE (hand this to each follow-up family: String/Convert/Char/Regex/Console/compareTo/…)
  > 1. **Precondition — confirm the binding exists stdlib-side.** grep `libraries/stdlib` for the target funs; each
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

## 【2】 stdlib completeness — ✅ ESSENTIALLY CLOSED (audited + executed 2026-07-02)
- ~~"~363 unbound actuals"~~ — **the doc number was ~3.8× inflated** (a stale early-stub count). Audited reality
  (discriminator = the binding ANNOTATION, not `TODO()` — MEMORY `stdlib-todo-is-filler-not-backlog`): 1481 actuals,
  **93.5% bound-or-implemented**; 96 real stubs, of which 60 = primitive IL lowering (never annotated, NOT backlog),
  10 = structural array-literal factories, and only ~24 were genuine work. **Char + StringBuilder were already fully
  bound** (the doc's family list was stale).
- The genuine remainder was then EXECUTED: ✅ generic `Array<T>` ops (copyOf/copyOfRange/plus×3/plusElement/orEmpty/
  arrayOfNulls — pure-Kotlin bodies leveraging CLR reified generics, bundle 2a, + 3 wrong-code compiler fixes) ·
  ✅ Unsigned div/rem/toString (below) · ✅ enum reflection (below). Still open (reclassified, small): Regex 3
  (Sequence-return, dual-rep-adjacent) · coroutine intrinsics 6 (→ bundle 【6】) · `Long.toString(radix)` beyond
  bases 2/8/10/16 (Convert.ToString limit, bundle-1 blocked note) · the `Array<Int?>` nullable-primitive-array
  element-read gap (KNOWN GAP, dual-rep design) · Enum-bounded cross-module generic constraint (§2 note by 2b agent).
- The 25 "retire from compiler" ops in 【1】 become real stdlib `@ClrIntrinsic`.
- ✅ **Unsigned family DONE (bundle 【2】b-A, 2026-07-02)**: the 6 `UnsignedClr.kt` div/rem/`toString(radix)` stubs
  got real pure-Kotlin bodies (JVM-actual/Guava ports; no BCL bind exists — unsigned `op_Division` is an
  explicit-interface generic-math impl); zero compiler change (call-site `bin /` + ilemit `div.un` pre-existed).
- ✅ **Enum reflection DONE (bundle 【2】b-B, 2026-07-02)**: `enumValues`/`enumValueOf`/`enumEntries`/
  `enumEntriesIntrinsic` call-site-lowered by kotc (`ENUM_REIFIED_INTRINSICS`) like `T.values()`/`T.valueOf()` —
  rich → synthesized statics, basic/generic-param → semantic `enumValues`/`enumParse` nodes. Gaps: rich enums via
  non-inlined generic contexts; the pre-existing `kotlin.Enum<T>` CLR-constraint emission (breaks ANY Enum-bounded
  cross-module generic call with a basic-enum type arg — orthogonal, still open).

## 【3】 facadegen .NET interop breadth  *(#4 + interop-feedback)*
Sources: `ship-tasks.md #4`, `archive/future-work-interop.md #4`, `archive/dotkt-interop-feedback.md`,
`archive/research-roadmap.md I1`.
- ~~static `.Companion` routing = the `il:injstatic` bug~~ — ✅ **FIXED (2026-07-02).** And the `.Companion`
  convention itself was subsequently **LIFTED** (`50c2c9f`): implicit `App.member` now resolves (eager companion
  link + the `FirInternals.java` shim setting the FIR-internal `ownerGenerator`; the old NPE was a FIR wiring gap,
  not a K2 limit — MEMORY `injected-static-members-need-companion` is RESOLVED). The injstatic bug's root cause was
  NOT the convention:
  it was the **Rule-3 hoist classifier misfiring on an injected owner**. A facadegen-injected external .NET type's
  synthesized-companion static METHOD (`App.Companion.start(cb)`) naturally carries no member interop marker (it isn't
  a stdlib binding), so kotc's BirEmitter hoist condition matched and fabricated a phantom
  `<>dotkt_ClrH_Kfc_App.start` callStatic (that helper exists only for stdlib @Clr classes with hoisted Kotlin
  bodies) → ilemit "unresolved method". Fix mirrors the event precedent `32a1da6`, generalized: the hoist is now
  gated on `ClrTypeRegistry.dotNetName(owner-or-companion-host) == null` — a registry hit means the owner is a real
  .NET type whose every concrete member is a real .NET member, so it routes to the direct `clrStatic`/`clrInstance`/
  `clrPropGet`/`clrEvent*` shapes (subsumes the narrower `ClrEventRegistry` gate). `il:injstatic` GREEN
  (`p=42/7/99/123`); this was the LAST run-FAIL — gates now PASS(run) 132 / fail-names 6 (all ilverify) / ktproj 9/9.
- ~~`op_*` operators · C#-origin extension methods~~ — ✅ DONE (verified 2026-07-02): facadegen surfaces a genuine
  .NET type's `op_Addition`/`op_Subtraction`/`op_Multiply`/`op_Division`/`op_Modulus`/`op_UnaryNegation`/
  `op_UnaryPlus`/`op_Increment`/`op_Decrement` as Kotlin `operator fun`s (left operand = receiver, `clr:op_*`
  routing), and `[ExtensionAttribute]` static methods as Kotlin extension functions (Int AND String receivers
  verified). `op_Equality`/`op_Inequality` are deliberately NOT mapped — Kotlin `==` routes to `Equals(Any?)`, the
  correct Kotlin semantics (a well-formed .NET type keeps op_Equality consistent with Equals); `op_Implicit`/
  `op_Explicit` have no Kotlin analog and are skipped. Gate: `cases/il-c1net` (full battery: `+ - * / unary-` on a
  C# struct + int/string extension methods).
- ~~**dual-rep collision** (`import System.Text.StringBuilder` vs the stdlib alias)~~ — ✅ DECIDED+DONE (2026-07-02):
  the two are **two typed views of one CLR type** — they coexist, never unified; mixing identities is a clear
  frontend type error; an explicit cast (`as kotlin.text.StringBuilder`) is the escape hatch (same CLR type at
  runtime). Rule + rationale: `docs/dotkt-semantics.md` §8b; gate: `cases/il-dualrep`.
- ~~**(3) generic-type members collapse to `Any?`**~~ — ✅ DONE (verified 2026-07-02): `CrossType` emits
  `generic:Open[args]` (recursive bracket grammar) and ClrTypeInjection resolves it by (name, arity);
  `IList<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IEnumerable<T>`/`Dictionary<K,V>`/`List<T>` member positions
  covered by `cases/il-geninj` + `cases/il-transinj` (gate). For-in over an INTERFACE-typed receiver fixed
  2026-07-02 (frontend-only `iterator` marker on the injected `IEnumerable<T>` itself; derived ifaces inherit it).
- ~~**(4) delegate-type args collapse to `Any?`**~~ — ✅ DONE (re-verified 2026-07-02): `Map` emits a delegate as the
  bracketed function-type token `func:[ret,args…]` (from the delegate's `Invoke`), ClrTypeInjection restores a Kotlin
  function type, a lambda binds, and the backend builds the specific delegate from the call-site param. Covers BCL
  `Func<int,int>`/`Action` AND custom GENERIC delegates (`delegate T Mapper<T>(T)`). Gates: `cases/il-delegatearg`,
  `cases/il-netinterop`.
- ~~**(5) aliased import silently ignored** (`import … as X`)~~ — ✅ DONE (verified 2026-07-02): the PSI import scan
  (`ImportScan.kt`, `importedFqName`) keeps aliased imports (canonical FQN out; Kotlin's own import machinery binds
  the alias to the injected classifier); facadegen warns on a no-match import, and a nonexistent type errors at the
  frontend (`unresolved reference`) — nothing is silent. Gate: `cases/il-alias`
  (`import System.Text.StringBuilder as SB`). Remaining nit: the wrappers (`dotkt.sh`/MSBuild targets) discard
  facadegen stderr, so the warning TEXT doesn't reach the build log (the frontend error still does).
- ~~**(6)/future#4/roadmap-I1 transitive / on-demand type injection**~~ — ✅ DONE (verified 2026-07-02):
  `EmitMeta` BFS-injects the full reachable closure of the imported seeds (member signatures + supertypes), capped
  at 5000 types, NO_INJECT/kotlin.* excluded, dedupe + fail-soft per type. 2-hop chain (un-imported
  `Gadget`→`Sprocket`) verified by `cases/il-transinj`. Design: full closure + cap, NOT depth-limited — no
  "hop N+1 collapses to Any?" cliff; measured closures stay small (~265 types for Console+Exception).
- ~~**generic-type FIR direct injection (roadmap I1, L)** — `List<T>`/`Dictionary<K,V>` façade-free (last hole in the
  injection path).~~ — ✅ subsumed by (3)/(6): generic DEFINITIONS (`List`1`) are injected with type params and
  constructed forms resolve at member positions (`il-transinj` exercises `Dictionary<String,Widget>` façade-free).
- ~~I4 remnants: `out`/`ref`, nullable value types, .NET enum import, generic delegates.~~ — ✅ ASSESSED, ALL WORKING
  (2026-07-02): `out`/`ref` = the `byref()`/`ClrRef` path, long gated by `cases/il-outref`; nullable value types
  (`int?`/`double?` both directions, incl. a plain Int and a null into an `int?` param) work via the `Nullable`1 → X?`
  map; a .NET enum imports as an object of enum-typed vals (read, pass, `==`, `when` all work); generic delegates via
  `func:[…]` (see (4)). New gate `cases/il-netinterop` locks enum+delegate+nullable in one sample.
  (Naming trap: `il-netenum` is the IEnumerable-for-in sample, not enums.)

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
  - ~~enum-with-body (`enumbody`/`enumr`) — `Op.get_sym not found`~~ — ✅ FIXED 2026-07-02: kotc `richEnumDef` emitted
    user ctor-val props as bare public fields while the access site (CLR property model) calls `get_<name>`; it now
    mirrors `typeDef` (internal backing field + `accessorMethod` get_/set_ + a `properties` entry). Both samples run-green.
  - ~~`valcls` (`@JvmInline value class`, compile error)~~ — ✅ FIXED 2026-07-02: the frontend jar lacked a platform
    `actual` for the `@OptionalExpectation` `kotlin.jvm.JvmInline` expect (same gap JvmName had) → any app value class
    died at the frontend. `build-stdlib-jar.sh` now stages `JvmInlineActual.kt` (3b). The backend already
    handles the class: `value class` lowers to a REAL wrapper class (see `docs/dotkt-semantics.md` §10.3), sample run-green.
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
(`libraries/stdlib/clr/builtins/String.kt:22`) — a **sealed** BCL type; its CharSequence surface is bound in place via
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
(`libraries/stdlib/src/kotlin/text/Strings.kt:181`) → `castclass <>dotkt_CharSequence` on a sealed `System.String` →
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
- **Comparable-self (`compareTo`, `il-comparable`) — ✅ DONE 2026-07-02** (`6f57048`; full record in the
  il-comparable/il-result section below). The recorded "ilemit self-ref interface-token" hypothesis was NOT the
  blocker — three other bugs were: the bir2cir name-only `sort` intrinsic fallback capturing the real-bodied
  `MutableList<T>.sort()`, the missing NON-generic `System.IComparable` face on user Comparable implementors
  (new bir2cir `ComparableBridgeSynthesis`), and ilemit's name-keyed clr-iface slot wiring mis-binding the
  resulting CompareTo overloads. NO adapter needed, as predicted.
- **collection-element / `listNew` (`listOf`/`setOf`/`mapOf`)** — collections HAVE a BCL representation (`List<T>`, already
  `@ClrTypeAlias` in `builtins/Collections.kt`); this is the **collection-bridge**: retire the `listNew`/`setNew`/`mapNew`
  factories together with the `COLLECTION_MEMBER`/`COLLECTION_OPS` clrName tables, riding the Iterable→IEnumerable
  precedent (`@ClrTypeAlias`), NOT an adapter.

**⑧ Follow-up agents.** (1) ~~bir2cir+stdlib agent — foundation A~~ **✅ DONE 2026-07-02** (app-local injection; see the
STATUS box above). (2) ~~synthetic-UNIFICATION agent~~ **✅ DONE 2026-07-02 (CANONICALIZATION, ilemit)** — the app now
references the rt stdlib dll's `<>dotkt_CharSequence` (ilemit pass-1 skip + pass-2 reflection MethodImpl; `il-charseq`
green, `il-charseqx` new-pass; see the STATUS box). The design fork resolved to **ilemit-resolves-external** (NOT
kotc-emits / bir2cir-repoints) — kotc keeps synthesizing the token, ilemit derives "already in a `--ref` → reference it".
(3) **stdlib+kotc retire agent — ✅ PARTIALLY DONE 2026-07-02 (bundle 4-B).** Two supporting infra fixes made the clean
ops retire: (a) **ilemit** `FindReflectedMethodBySig` — sig-aware overload pick on a referenced file-class (String-face
`substring(String,int,int)` vs CharSequence-face were arity-ambiguous → wrong body); (b) **bir2cir** — the
StringCharSequenceBridge now runs on the RT stdlib self-build too (gate `attributeTopLevelOwner`→`!RefBuild`), so the
stdlib's OWN internal String→CharSequence widenings (`indexOf(String)`→private `indexOf(CharSequence)`) materialize the
adapter (injected once into the rt assembly, on the canonical interface). **RETIRED (route to real stdlib body):**
`contains`, `indexOf`, `startsWith`, `endsWith`, `split`, `substring(2-arg)`, `isEmpty`/`isNotEmpty`. Gate-neutral
(run-fail set identical) → IMPROVING (ilverify 21→20, `il-tryexpr` now fully green). **STILL LOWERED — each a DISTINCT
deeper stdlib-body bug (a stdlib-body-fix follow-up, NOT dual-rep):** `trim`/`trimStart`/`trimEnd` (`Char::isWhitespace`
method-ref not lowered + un-wrapped inlined `as CharSequence`), `reversed` (`StringBuilder(CharSequence)` no .NET ctor),
`padStart`/`padEnd` (StringBuilder append/capacity mis-bind), `replace(String,String)` (StringBuilder
`append(seq,start,END)`→`Append(str,start,COUNT)`), `isBlank`/`isNotBlank` (`all{isWhitespace}` CharSequence iteration
`Iterator.hasNext` not found). (3b) **indexer `s[i]`→`get_Chars` — ✅ DONE 2026-07-02 (bundle 4-B).** kotc no longer
lowers `String s[i]`; `kotlin.String.get(index)`@ClrIntrinsic("get_Chars") + bir2cir MemberCallSubstitution route it.
Gate-neutral; `il-charseq` (user `class S : CharSequence` indexing `s[index]`) still green.
(4) **Regex agent — ✅ DONE 2026-07-02 (bundle 4-B).** Retired the kotc `toRegex`/`containsMatchIn`/`replace` lowerings;
`kotlin.text.Regex`@ClrTypeAlias + `containsMatchIn`/`replace`@ClrIntrinsic + real `matches`/`find`/`split`/`.value`
bodies route via bir2cir. `il-regex` RUN-passes; gate-neutral (run-fail + ilverify sets identical). The `il-regex`
ilverify FAIL was NOT cleared (pre-existing, still failing): the `@ClrIntrinsic("IsMatch")`/`("Replace")` bindings sit
on a `CharSequence` param while the BCL method takes `string` (substituted call keeps the Kotlin `<>dotkt_CharSequence`
argType, raw `string` pushed → `StackUnexpected`), plus the `find`/`.value` bodies' own verify noise — stdlib-body/
binding follow-ups (add `nativeIsMatch(String)`/`nativeReplace(String,String)` helpers that `toString()`-materialize).
The kotc `kotlin.text.Regex`→`clr:System...Regex` TYPE-token map is left in place (a `netType`-style concern).
Comparable-self and the collection-bridge are separate tracks (own agents), not gated on this fix.

### 【4-C】 Collection / sequence cluster — ROOT-CAUSE MAP + FIX PLAN  *(investigation-complete 2026-07-02)*
> **Scope.** The 10 collection/sequence verify-il samples: run-FAILs `collops2`/`collrealkt`/`mutcoll` and (nominally)
> "ilverify-only" `mapfilter`/`coll2`/`collmore`/`chunk`/`arrops`/`seq`/`sort`. Investigation was code-grounded (BIR/CIR
> dumps + `dotnet` run + `ilverify` + throwaway repros; tree left clean, no source edits). **Two premise corrections up
> front, then the de-duplicated root causes (5 distinct bugs), then the priority ranking.**

> **⛔ PREMISE CORRECTION 1 (the task's lead hypothesis is DISPROVEN — record it so nobody re-chases it).** The idea that
> the CharSequence CANONICALIZATION precedent (add to `ilemit` `CanonicalSynthetics`) also fixes the iterator synthetics
> `<>dotkt_KIterator_*`/`<>dotkt_KIterable_*` is **WRONG** (Codex-traced, session `9b58bc57`). Those synthetics are
> **monomorphized per-element-type** (`BirEmitter.kt:326/346` build the name from `elemBir`; `:335/351` register NOTHING
> when the element is `gp:` generic). So for a generic `Iterable<T>` there is **no `<>dotkt_KIterator_gp_T` to
> canonicalize** — nothing to reference. Unlike `<>dotkt_CharSequence` (one monomorphic shape shared app↔rt), the KIterator
> family has a distinct concrete type per element and the rt stdlib (compiled generic-over-T) never emits the app's
> concrete `<>dotkt_KIterator_kotlin_Int`. The generic `Iterable<T>`/`Sequence<T>` runtime path is **meant to lower to BCL
> `IEnumerable<T>` enumeration** (`BirEmitter.kt:4045` app map, `:4022/1895` substitute `forEachInline`; `ilemit`
> `Emitter.Expressions.cs:444` forEachInline uses `IEnumerable<T>.GetEnumerator`, falling back to the non-generic
> `IEnumerable.GetEnumerator` for a TypeBuilder element `:455`), and `bir2cir.IteratorConsumerNormalization`
> (`Program.cs:2191`) already rewrites app-side bridge consumers to the real referenced `kotlin.collections.Iterator[E]`.
> **Do NOT add `KIterator_*`/`KIterable_*` to `CanonicalSynthetics`.** Any real iterator bug is in the BCL-enumeration /
> generic-member-token path (RC3 below), NOT synthetic identity.

> **⛔ PREMISE CORRECTION 2 (the "run correctly, ilverify complains" split is INACCURATE for the current tree).**
> `mapfilter`/`coll2`/`collmore`/`chunk`/`seq`/`sort` do **NOT** run correctly — run directly they throw
> `InvalidProgramException` at JIT of `main()` (reproduced deterministically). The gate's "no `build/fail-*` marker" for
> them is a **false pass** (the documented gate stdout+marker race under concurrency — MEMORY
> `verify-il-gate-reality-and-baseline-gotchas`). Their ilverify diagnostic and their runtime crash are the SAME defect
> (RC1 below). Treat them as run-FAILs. (`arrops` is the one genuine ilverify-only case, RC2.)

**The 5 distinct root causes (de-duplicated across the 10 samples).**

- **RC1 — cross-module default arguments. ✅ DEFAULT-ARG MECHANISM DONE 2026-07-02** (`9613de4` kotc + stdlib
  `@KotlinDefault`; bir2cir `DefaultArgSplice` committed in `b7caaf7`). **But the collection SAMPLES do NOT green from
  RC1 alone — each is ALSO gated by a SEPARATE pre-existing bug (see below), which RC1 correctly EXPOSED.** Implemented as
  the user-decided **2-tier rule** (superseding the `$default`-dispatcher and the `[KotlinDefault]`-for-everything and the
  CharSequence→string-collapse designs), keyed on "can the param's own CLR type carry its default as a
  `[DefaultParameterValue]`?": **Tier 1** (primitive/String/null const on a matching param) → native `[Optional]`+
  `[DefaultParameterValue]` (unchanged, C#-consumable); **Tier 2** (a String const on a `CharSequence`/interface param, or
  ANY non-constant default) → param emitted **REQUIRED** + its default EXPRESSION carried as embedded BIR on
  `@kotlin.clr.KotlinDefault(index, bir)` (ref.dll-only, mirrors `[KotlinInline]`); bir2cir's `DefaultArgSplice` reads it
  and splices the expression as the omitted arg (before the CharSequence bridge + type lowering). A Tier-2-carrying
  function stamps `@KotlinDefault` on ALL its defaulted params (uniform contiguous splice source). **VERIFIED:**
  `listOf(1,2,3).joinToString("-")` now fills all 7 args and dispatches `joinToString` correctly (the stack-underflow /
  `InvalidProgramException` is GONE); gate-neutral spot-check (charseq/charseqx/str/arr/exc/cp/coll3/enum unaffected).
  **⛔ REMAINING SAMPLE BLOCKERS (separate, pre-existing, NOT default-args — RC1 exposed them):**
  (1) **`joinTo`/`Appendable`/`StringBuilder` dual-rep — ✅ DONE 2026-07-02 (Track A).** `joinToString`'s body calls
  `joinTo<T, A : Appendable>(StringBuilder(), …)`; `StringBuilder` is `@ClrTypeAlias("System.Text.StringBuilder")` yet
  declared `: Appendable`, so the BCL-aliased type lost the synthetic CLR interface →
  `VerificationException: type argument 'System.Text.StringBuilder' violates the constraint of 'A'`. **FIX:** `Appendable`
  is a JVM-ism with no distinct .NET rep (StringBuilder is the sole CLR appendable char sink), so — mirroring the
  CharSequence→String collapse — `Appendable` is `@ClrTypeAlias("System.Text.StringBuilder")` + `@ClrIntrinsic("Append")`
  on its `append(Char)`/`append(CharSequence?)` members (stdlib). bir2cir lowers every `Appendable` token from the
  ref.dll, so the bound `A : Appendable` becomes the satisfiable `A : System.Text.StringBuilder` (a SEALED-class
  constraint verifies fine — the option-a "sealed constraint" worry was moot). Three supporting codegen fixes (see below)
  made the joinTo/appendElement body run. **Layer:** stdlib binding (Appendable @ClrTypeAlias) + bir2cir (adapter
  ToString) + ilemit (overload/isinst codegen). **Greened:** `mapfilter`/`coll2`/`mutcoll`/`arrops` (verify-il PASS(run)
  108→113, run-FAIL + ilverify-FAIL sets both unchanged = gate-neutral-or-better; ktproj 9/9). `bmore` still blocked by
  `String.format` (blocker 3); `chunk` by `windowed`/SequenceScope-not-instantiated; `sort` by `reverseOrder`/Comparator
  InvalidCast; `collmore` by `mapNotNull` (RC2 transform-side) — each a DISTINCT blocker, NOT joinTo. `collrealkt`
  reaches `b,a,c` (joinToString) then hits `Map.get` (Track B). **Supporting ilemit/bir2cir fixes (general):**
  (a) ilemit `EmitClrCall` arity-fallback is now assignability-aware (`ParamAcceptsArg`): never picks a BCL overload the
  arg isn't assignable to (a `<>dotkt_CharSequence` → `Append(object)` not `Append(String)`; the latter reinterpreted the
  object → "Destination is too short" corruption); (b) ilemit `isinst`/`isinstRef` now BOX a value-type/generic-param
  receiver before `isinst` (was: NRE reading an unboxed value as a ref — `element is CharSequence?`/`element is Char` in
  appendElement); (c) bir2cir `<>dotkt_StringCharSequence` adapter gained a `ToString()`→backing-string override.
  <br>**Track B — Map/MutableMap → Dictionary dual-rep — ✅ DONE 2026-07-02.** `mapOf` returned a BCL Dictionary typed
  as the pure-Kotlin `Map`/`MutableMap` → `Map.get` threw `EntryPointNotFoundException`. **Landed as: BOTH `Map` and
  `MutableMap` `@ClrTypeAlias("System.Collections.Generic.IDictionary")`** (Codex-concurred; NOT the List-style
  IReadOnlyDictionary/IDictionary split — IDictionary does not extend IReadOnlyDictionary, so the split breaks
  `MutableMap : Map` at the IL level; empirically the existing IList→IReadOnlyList store is ALREADY ilverify-dirty, and
  a split Map pair would put that hole on the hot path. Both-IDictionary = the Kotlin/JVM model (both erase to
  java.util.Map) + the Iterable/MutableIterable→IEnumerable precedent; read-only-ness is frontend-enforced —
  `docs/dotkt-semantics.md §5c`). The three recorded hardnesses, as fixed: (i) ilemit `PropAccessor` +
  `ResolveInheritedIfaceMethod` now substitute ARITY-CHANGING constructed-arg base-interface chains
  (`SubstituteIfaceArgs`: `IDictionary`2.Count/Clear` on `ICollection`1<KeyValuePair`2>`); (ii) null-on-missing `get` =
  `ClrMapDefaults.clrMapGet` (`ContainsKey` + raw `get_Item` ext-intrinsic `clrMapItem`) behind the new bir2cir
  **Rule 5m** 2-type-arg map routing (`MapDefaultCall`, the K,V mirror of `CollDefaultCall`; also put/remove/putAll/
  getOrDefault/isEmpty/containsValue/keys/values/entries); (iii) subtyping preserved trivially by the identical alias.
  Supporting fixes: stdlib `Map.size/containsKey/MutableMap.clear/keys/values` @ClrIntrinsic (Count/ContainsKey/Clear/
  Keys/Values); `Map$Entry`/`MutableMap$MutableEntry` survive the alias-drop as top-level types (`nestedIn` fallback);
  ilemit now `$dupN`-mangles DUPLICATE (name, params) defs (Map/MutableMap-receiver extension pairs collapse under the
  shared alias — both `iterator()` bodies previously concatenated into ONE MethodBuilder → BadImageFormatException);
  bir2cir `IteratorConsumerNormalization` generalized to rt-returned iterators (Set.iterator/MapsKt.iterator vars) and
  KIterable-synthetic consumers with rt receivers (`withIndex` for-loops) — receiver-gated so app-side synthetic
  producer/consumer pairs (il-iter/il-iterable) stay untouched; kotc's legacy Map.Entry→`KeyValuePair.Key/.Value`
  destructure lowering DELETED (read a ref Entry object as a struct → garbage); bir2cir `RecvKey` normalizes NESTED
  ref-type names (`Map`2+Map$Entry`2` → `kotlin.collections.Map$Entry`) so Entry-receiver extensions attribute;
  `ClrOwnerType` pads a star-projection-erased arg list to the alias arity; `emptyMap()` returns a Dictionary-backed
  map (pure `EmptyMap` can't satisfy IDictionary). **Greened:** `collrealkt` + `mapdes` full-correct; `collops2`'s
  partition/associate/withIndex/scan/runningFold/getOrElse lines all pass — it stays run-FAIL ONLY on `windowed`
  (the SequenceScope blocker it shares with `chunk`, a separate track). Known accepted edges (recorded in §5c):
  snapshot (not live) read views; `MutableMap.iterator` degrades to the Map shape ($dup mangling keeps the first
  overload); pure-Kotlin Map implementers (AbstractMap/MapWithDefaultImpl) still fail to LOAD (the pre-existing
  dual-rep pure-path gap; +3 rt loader errors, 13→16, none sample-reachable). (2) **`bymap`** — `by data` Map-delegation unsupported (kotc delegate resolution — now the delegate ROUTES but needs a
  KProperty bridge: the stdlib `MapAccessorsKt.getValue` takes the RT `kotlin.reflect.KProperty`, the app materializes
  its own `<>dotkt_KPropertyImpl` synthetic — cross-assembly KProperty identity, NOT trivial). (3) **`fmt`/`bmore`** —
  `String.format` unresolved (the CLR frontend jar has no `String.Companion.format`; a stdlib-binding gap). — Historical
  root-cause analysis of the original stack-underflow follows:
  kotc emits the `joinToString`
  call with only **2 args** (receiver + the one provided separator) against a **7-param** signature (`receiver, separator,
  prefix, postfix, limit, truncated, transform` — confirmed in BIR: `#args=2` vs `sig params:7`). ilemit then emits
  `call joinToString(7)` with 2 stack values → **stack underflow / `InvalidProgramException`** (ilverify:
  `found <>dotkt_StringCharSequence, expected Func`2<int,<>dotkt_CharSequence>` + `found IReadOnlyList`1<int>, expected
  <>dotkt_CharSequence` + `StackUnderflow`). **Mechanism:** `filledArgExprs` (`BirEmitter.kt:2786`) OMITS a cross-module
  default because the frontend jar hands the default back as `IrErrorExpression` (`:2800`), by design deferring the fill to
  ilemit's `[Optional]/DefaultParameterValue` metadata path (`EmitDefaultArg` `Program.cs:2874`; kotc stamps `"default"`
  only for `IrConst`, `:1811`; ilemit stamps the attr `Emitter.Coroutines.cs:275-280`). This path works for
  primitive/string/null constant defaults — but `joinToString`'s omitted defaults `prefix=""`/`postfix=""`/`truncated="..."`
  are **`CharSequence`(object)-typed**, and an object param cannot carry a non-null `DefaultParameterValue` constant, so
  nothing is stamped and nothing is filled. (This is the SAME bundle-4 cross-module-default bug listed for
  `bmore`/`bymap`/`fmt` — those fail earlier, at compile.) **This surfaced when `joinToString` was retired from the kotc
  COLLECTION_OPS/`String.Join` lowering into the real stdlib body** — the retirement is correct, but it exposed the latent
  default-arg gap. **Samples:** blocks `mapfilter`,`coll2`,`chunk`,`sort` (RC1 is their ONLY blocker → fixing RC1 flips
  them green) and is a co-blocker in `collmore`,`seq`,`collops2`,`collrealkt`,`mutcoll`. Only `arrops` escapes (its
  `joinToString` rides the array/LINQ `String.Join` lowering, not the cross-module stdlib fn). **Fix (ranked):**
  **(b) generate `$default` dispatcher synthetics** in the stdlib build (`joinToString$default(args…, mask:int, marker)`
  filling each omitted param from its default expr in the callee scope) + kotc caller emits `fn$default` with a bitmask
  when any default is omitted — the general, robust option (handles object/non-const/receiver-referencing defaults),
  mirrors Kotlin/JVM. **Layer = kotc** (stdlib-build synthetic emission + caller-side mask). Effort **HIGH**. Narrower
  alternatives — (a) ilemit stamps `DefaultParameterValue` for the constant defaults + a wrap-marker so an object
  `CharSequence` default reconstructs `new <>dotkt_StringCharSequence(", ")` at the call site (kotc-visible only when the
  default is a bare `IrConst`, which the String→CharSequence coercion defeats), or (c) frontend-jar preserves the default
  expressions so kotc inlines the (all-constant, non-receiver-referencing) `joinToString` defaults at the call site
  (`filledArgExprs` already inlines a non-`IrError` const default) — layer = `build-stdlib-jar.sh`, effort
  MED-HIGH/unknown (frontend metadata format). **Recommend (b)**; it also clears `bmore`/`bymap`/`fmt`.

- **RC2 — value-type `Nullable<T>` in generic return / transform position. ✅ RETURN-SIDE DONE 2026-07-02
  (`b7caaf7` bir2cir+ilemit; `4f4d848` ilemit overload).** `firstOrNull`/`lastOrNull` on a value-type
  `List<Int>` returned **`default(int)=0`** instead of the real element; ilverify: `found Int32, expected Nullable`1<int32>`.
  **ROOT CAUSE (return side):** a Kotlin `fun <T> …(): T?` has its nullability erased by kotc to a bare `gp:T` return
  (`Nullable<T>` is inexpressible for an unconstrained T), null case = `ldnull`. Correct for a reference T, but for a value
  T `ldnull; ret !!T` collapses to `default(T)=0` — null-ness LOST. **FIX:** the CLR-faithful representation of a generic
  `T?` is `System.Object` (boxed/erased nullable). `bir2cir.NullableGenericReturnErasure` (all builds — ref.dll + rt.dll
  agree) rewrites a method with `ret=gp:X` + `retNullable=true` to return `object` (and its return-value `gp:X` type tags
  to `object`); ilemit boxes value/gp returns to the object slot; `CoerceReturn` at the call boundary converts the object
  actual → the caller's `Nullable<V>` (`unbox.any`: null→HasValue=false, boxed→HasValue=true) or reference type
  (`castclass`). Reference-type nullable returns keep working. **Also fixed en route (a pre-existing ilemit bug arrops hit):**
  `FindReflectedMethodBySig` returned null on a DUPLICATE-emitted overload (the stdlib expect/actual merge emits some
  top-level fns twice — `_ArraysKt.sum(int[])` has two method tokens), dropping to the arity fallback which picked
  `sum(sbyte[])` → `arrayOf(3,1,4,1,5).sum()` read int[] as bytes = 4; now keeps the first exact-sig match (=14).
  **Verified:** `listOf(10,20).firstOrNull()`=10, `listOf<Int>().firstOrNull()`=null, `lastOrNull`, ref-type `firstOrNull`,
  `arrayOf<Int>().firstOrNull()`=-1, `xs.sum()`=14, `xs.count{}`=2, `xs.map{}.filter{}` — arrops lines 5/7/8/9/12/13 all
  correct. **arrops line 6 (`…joinToString(",")`) STILL blocks arrops full-green — that is RC1**, not RC2: with the RC1
  `$default` synthetic partially landed in kotc, the omitted `transform` default emits as a `default type=func:…gp:T` node
  whose `gp:T` is unresolved at the app call site → `unresolved generic type parameter T` at emit. **TRANSFORM SIDE
  (`mapNotNull`) NOT done — BLOCKED ON kotc:** the transform param `(T)->R?` is erased to `func:gp:R:gp:T` with NO
  func-slot nullability flag (unlike `retNullable`), so bir2cir cannot soundly know R is nullable in that position
  (Codex: inferring from a `val != null` body is too-late/ambiguous). Needs kotc to preserve func-return nullability
  (`func(args,ret=gp:R,retNullable=true)`); then apply the same object-erasure to the delegate return. **Samples:** `arrops`
  return-side FIXED (only its RC1 joinToString line remains); `collmore` needs RC1 + the transform-side fix; a latent risk
  for `chunk`'s value-type `filterNotNull().sum()`. **Layer = bir2cir/ilemit** (done for the return side). Effort **MED**.

- **RC3 — ✅ DONE 2026-07-02 (`aea0e4e` ilemit).** ilemit couldn't resolve a member on an un-substituted generic Kotlin
  collection/iterator interface (`ResolveMethod` NRE, `mb` null because `FindMethod` returned 0 candidates). Two instances:
  `collrealkt` — `callInstance get owner=kotlin.collections.Map[gp:K,gp:V]`; `mutcoll` — `callInstance hasNext/next
  owner=kotlin.collections.Iterator[gp:T]` (the ClrIteratorBridge rewrite of `for (item in this: Iterable<T>)`). **ROOT
  CAUSE:** `ParseOwner` strips the `[gp:..]` args off, leaving the BARE open name, but reflection knows a generic interface
  only under its arity suffix (`Iterator`1`/`Map`2`), so `ClrRef(typeName)` returned null and `FindMethod`'s external
  branch gave up. **FIX (ilemit, NOT bir2cir):** these are genuine Kotlin interface calls (no BCL substitution — the
  concrete Iterator/Map is a real rt-dll type), so `FindMethod`'s external branch now probes `typeName`+backtick-N (N=1..8)
  and takes the unique resolvable open definition; `ResolveMethod`'s existing `TypeBuilder.GetMethod` re-anchors it onto
  the constructed instantiation. **VERIFIED:** the generic `for`-over-`List<T>`/`Iterable<T>` iteration RUNS — new bonus
  greens `il-genclosure`+`il-genhof` (same `Iterator[gp:T]` bridge path), and `mutcoll`'s Iterator + `collrealkt`'s
  List/Map member resolution emit. **⛔ SAMPLES NOT green (separate blockers, NOT RC3):** all of `collrealkt`/`mutcoll`
  call `joinToString` (rt-baked `StringBuilder`→`Appendable` dual-rep, see RC1 blocker (1)); `collrealkt`'s `Map.get`
  additionally throws `EntryPointNotFoundException` at RUN because `mapOf` returns a BCL `Dictionary` (`LinkedHashMap` is
  `@ClrTypeAlias("System.Collections.Generic.Dictionary")`) that does NOT implement the pure-Kotlin `kotlin.collections.Map`
  interface — the **Map/MutableMap dual-rep** (parallel to List but with null-vs-throw `get` semantics; needs `Map`/
  `MutableMap` `@ClrTypeAlias` + a null-returning `get` binding, or the collection-bridge). Own dual-rep track.

- **RC4 — ✅ DONE 2026-07-02 (`aea0e4e` ilemit).** `kotlin.Pair`.first/.second (a destructuring `component1()`/
  `component2()` that kotc lowers to a `field` access) hit `FindField` `KeyNotFound: 'kotlin.Pair'` on the referenced
  (rt-dll) type absent from `_types`. **The task's "mirror the FindMethod fallback into FindField" was necessary but NOT
  sufficient:** once the field resolved, a direct `Ldfld` of the PRIVATE backing field threw `FieldAccessException`
  cross-assembly (the CLR property model gives every Kotlin property a private backing field + public accessors). **FIX
  (ilemit):** `FindField`/`ResolveField` gained the external-type reflection fallback (incl. the arity probe + a
  `FindReflectedField` helper); a new `ExternalPropAccessor` routes an external type's `field` read/write through the
  public `get_`/`set_<name>` accessor (falling back to the field for a public `@ClrField`). **VERIFIED:** `il-pair` now
  RUNS (bonus green), and `collops2`'s `xs.partition{…}` → `Pair.first`/`.second` prints correctly. **⛔ `collops2` NOT
  green (separate, NOT RC4):** it also calls `joinToString` (StringBuilder→Appendable) and `associate{}` (→ `associateTo`
  with `LinkedHashMap`→BCL `Dictionary` violating the `M : MutableMap` constraint — the same Map dual-rep as RC3).

- **RC5 — lazy `Sequence` machinery is unimplemented (`asSequence` throws at runtime).** Throwaway repro
  `listOf(...).asSequence().map{…}.sum()` → **`NotSupportedException: [DOTKT-STDLIB] not lowered: an object expression that
  captures an enclosing generic type parameter` at `_CollectionsKt.asSequence[T]`** — the rt stdlib STUBBED `asSequence`'s
  body to `throw` because its anonymous `Sequence` object captures the generic `T` (same kotc codegen gap as the
  `genclosure`/`genhof` generic-closure run-FAILs). So EVERY lazy-sequence op is dead at runtime, independent of RC1.
  **Samples:** `seq` (needs RC1 for static validity AND RC5 for runtime). **Fix:** lower object expressions / local classes
  that capture an enclosing generic type parameter. **Layer = kotc** (generic-closure codegen). Effort **HIGH** (deep,
  cross-cutting with `genclosure`/`genhof`; a separate track, not collection-specific).

**✅ 2026-07-02 (session `9b58bc57`, later): the 4-bug batch `sort`/`collmore`/`regex`/`langf` — ALL GREEN
(run-correct AND ilverify-clean). Gate: fail-names 18 → 9, PASS(run) 121 → 124, ktproj 9/9.**
- **`il-sort` ✅** — three stacked JVM erasure-isms, all stdlib-side (+ the RC2 transform side below):
  (i) `naturalOrder()`/`reverseOrder()` were erased singleton objects (`Comparator<Comparable<Any>>`)
  unchecked-cast to `Comparator<T>` → InvalidCast under reified generics; now genuinely generic private classes
  (the `ReversedComparator<T>` pattern; `reversed()`'s natural↔reverse identity swap dropped — an uncontracted
  optimization untypable for unconstrained T). (ii) `sortedWith`'s `toTypedArray<Any?>() as Array<T>` Collection
  fast-path (Object[]≠Int32[]) → always `toMutableList().sortWith()`, mirroring `sorted()`'s existing CLR
  adaptation. (iii) `compareValues`' `a as Comparable<Any>` → reified `IComparable<object>`, which a boxed
  primitive does NOT implement → dispatch through the NON-generic `System.IComparable` (internal
  `@ClrTypeAlias` iface `ClrRawComparable` + expect/actual seam; ilemit `cast` now boxes a value/gp source
  before `castclass`). Stdlib `905ee49`/`be4eb93`, outer `b15ace4`.
- **`il-collmore` ✅ = RC2 TRANSFORM SIDE DONE** (outer `b15ace4`): kotc emits `func:nullable:gp:R:...` for a
  nullable generic func return (funcTypeOf + both birType function-type paths — the Kotlin fact only); bir2cir
  `NullableFuncReturnErasure` (all builds) lowers every nullable-marked func return to `Func<…, object>`
  (the one rep the open/value views agree on; reference instantiations stay bare and ride Func's out-covariance),
  erases the backing delegateNew/closureNew lambda-method rets, and repairs the local dataflow (gp: var →
  object; re-narrowing inits get the universal `cast`). ilemit never sees a stacked `nullable:gp:` (its
  FuncRetEnd parses one prefix). **Two more root causes surfaced under it:** (a) kotc's inline-splice
  `typeArgSubst` was NAME-keyed — `mapNotNullTo<T>` splicing `forEach<T>` erased the OUTER T to object, and
  `let<T,R:=Unit>` rewrote the outer R to `kotlin.Unit`; now keyed by the `IrTypeParameter` SYMBOL (self-star
  detection by classifier identity). (b) `MutableCollection.add` was `@ClrIntrinsic("Add")` but
  `ICollection<T>.Add` is VOID vs Kotlin's changed-Boolean (brIf on the phantom result = stack underflow), and
  1-arg `addAll` has no ICollection slot: bir2cir pre-Rule-2 routes them to new `clrCollAdd`/`clrCollAddAll`
  defaults (Add + size compare — set-duplicate-aware), element type recovered from the receiver's generic-param
  constraint (`CollElemArg`).
- **`il-regex` ✅** (outer `ec9aed6`) — NOT the recorded IsMatch/Replace binding shape (those substitute to the
  BCL `Regex.IsMatch/Replace(string)` and verify fine): the rule-3 helper calls (`matches`/`find` →
  `<>dotkt_ClrH_kotlin_text_Regex`) DROPPED the call `sig`, so the String→CharSequence bridge (positional off
  `sig`) never wrapped the app's raw string. `Rule3HelperCall` now carries the receiver-first param list
  (longer-than-args OK — omitted defaults fill downstream). Remaining rt-side `ClrMatch*` noise
  (`ClrMatchGroupCollection : AbstractCollection` missing iface synthesis, `get_destructured` ReturnMissing) is
  the documented pure-Kotlin dual-rep gap — not sample-reachable, NOT forced. Known edge: `println(null)`
  prints an EMPTY line (Console.WriteLine(null)) where Kotlin prints "null" — unasserted by the gate, unfixed.
- **`il-langf` ✅ +5 bonus greens (`netbase`/`netbase2`/`netgen2`/`customexc`/`mc1`)** (outer `da602e9`): kotc
  emitted a class-inherited FAKE-OVERRIDE property (`Sq : Shape("sq")` inheriting `name`) as a REAL accessor
  with an EMPTY body → ilverify ReturnMissing on every derived class of a property-carrying base (and an empty
  fake-override SETTER silently no-opped). `emitsGet/emitsSet` drop a fake-override resolved to a base CLASS;
  an ABSTRACT fake-override resolved only to an INTERFACE member is KEPT (the CLR requires the abstract class
  to re-declare the slot — dropping it broke the rt build). ilemit hardening surfaced by the drop: base-chain
  walks strip the inner-generic base's `[gp:E]` instantiation args (`BareTypeKey`); `FindInInterfaces` probes
  the open name best-effort. **Remaining known-wrong bindings of the same class (not sample-reachable, TODO):**
  `MutableList.set(i,e)`→`set_Item` (returns previous E vs void), `removeAt`→`RemoveAt` (returns E vs void),
  `removeAll`/`retainAll` (unbound, no ICollection slot — will need clrColl defaults like add/addAll).

- **`il-generic4` ✅ (2026-07-02)** — a generic METHOD on a generic CLASS called at a concrete instantiation
  (`Holder<int>.pairWith<string>()`) ran as the OPEN `Holder`1::pairWith<string>` → runtime
  `InvalidOperationException: not fully instantiated`. ilemit `ApplyTypeArgs`'s constructed-TypeBuilder-owner branch
  now splits on the owner's args: NO generic params (an external-style call site) → keep the
  `TypeBuilder.GetMethod`-anchored `MethodOnTypeBuilderInstantiation` and `MakeGenericMethod` it directly (the
  documented order; empirically supported on .NET 10 persisted emit — the "no clean API" assumption in the old
  comment was wrong for this case); args contain enclosing generic params (the rt-stdlib self/erased context that
  REVERTED the previous naive fix, MEMORY `generic-extension-property-getter-typeargs`) → the open-method path,
  byte-identical. rt/ref stdlib rebuilds stay green; il-sort/il-comparator/il-valclass/il-generic{,2,3,5,6} unchanged.
  (Baseline correction discovered en route: `il-comparable` was ALREADY failing pre-change — the 18:26 cached
  verify-il artifacts (`build/il-comparable/`, old rt dll md5 `97cb1025`) reproduce `Array.Sort[T] … not fully
  instantiated` inside rt `sorted[T]`; a REFLECTED open-generic-invoke bug in the rt body emission, separate track.)

- **`il-comparable` + `il-result` ✅ (2026-07-02, `6f57048` + `94e4226`)** — the LAST two non-coroutine-deferred
  line-less gate crashers (the subshell died before printing a result line; reproduced via `dotkt.sh --run`).
  **`il-comparable`** (rt `sorted[T]` → OPEN `Array.Sort[T]`, the baseline correction above) = THREE stacked bugs:
  (i) **bir2cir** — the name-only top-level-@ClrIntrinsic fallback captured the REAL-BODIED `MutableList<T>.sort()`
  call inside the compiled `sorted()` body (all 8 primitive-array `sort()` intrinsics agree on "System.Array.Sort",
  so the name was not "ambiguous"); the fallback is now also gated on `HasNonIntrinsicTopLevel(fn)` — a name with a
  real-bodied sibling substitutes only on a sig-EXACT match, so `sorted()` routes to the real `sort→sortWith`.
  (ii) **bir2cir `ComparableBridgeSynthesis` (new pass)** — `class Ver : Comparable<Ver>` lowered to the GENERIC
  `IComparable<Ver>` face only, but the CLR natural-ordering dispatch spine is the NON-generic `System.IComparable`
  (the il-sort `compareValues` design + the constrained-compareTo value-type-safe fallback the rt SAM shim rides) →
  `EntryPointNotFoundException`; every emitted class implementing `clrg:System.IComparable[X]` now also gets
  `clr:System.IComparable` + a `CompareTo(object)` cast-and-forward bridge (the Int32/String/DateTime BCL convention).
  (iii) **ilemit** — the `clr:`/`clrg:` interface-slot wiring picked the body method NAME-keyed; with the new
  CompareTo(object) overload it wired the wrong one to `IComparable`1<V>` (TypeLoad "signature … do not match") —
  overloads now disambiguate by the slot's instantiation-substituted param types (`SlotParamMatches`), mirroring the
  Kotlin-branch MethodsBySig rule. **`il-result`** (InvalidProgram at rt `runCatching[R]`; the recorded "value-type
  `T?`→Nullable dual-rep" diagnosis was STALE — RC2 had landed; the crash was elsewhere) = FOUR stacked bugs around
  a generic class's companion statics: (i) **ilemit `AnchorOpenGenericOwnerStatic`** — `Result<T>`'s companion
  `fun <T> success` emits as a static generic method ON `Result`1`, and the call site referenced the open-typedef
  parent (`call kotlin.Result`1::success<T>` — invalid IL); such statics now anchor onto the `object`-instantiated
  owner via `TypeBuilder.GetMethod` (a companion member cannot reference the class's T, so any instantiation is
  signature-identical; Codex-confirmed) and ride the gen4 concrete-owner `MakeGenericMethod` path. (ii) **kotc** —
  the companion-member `callStatic` dropped the call's TYPE ARGS (`typeArgsJson` never applied) → even anchored, the
  uninstantiated generic method (BadImageFormat); now carried like every other generic call. (iii) **kotc
  `ownerSpec`** — a STAR-projection type arg was DROPPED (`Result<*>.throwOnFailure` → bare open `kotlin.Result`
  ownerType → `get_value` open-typedef token, "not fully instantiated"); a star arg now renders `object`, mirroring
  `birType`'s star rule. (iv) **ilemit `EmitNewArgs`** — `new` ctor args ignored the node's declared `argTypes`: a
  bare `!!T` flowed UNBOXED into `Result`1<!!T>..ctor(object)` (InvalidProgram at any value instantiation, surfaced
  by `success<int>` once the call path worked); ctor args now box to their declared param types like
  `EmitArgsTyped`. Both samples now print their full expected output; protected set verified green (rt+ref stdlib,
  il-sort/il-comparator/il-valclass/il-generic4/il-deleg/il-charseq{,x,s}). **Authoritative full-gate numbers
  (2026-07-02, no siblings): PASS(run) 129 → 131 (+comparable +result, BOTH also ilverify-clean); fail-name set
  UNCHANGED at 7 = 6 ilverify-FAIL (chunk/collops2/collrealkt/gen3/iter/iterable) + 1 run-FAIL (`il:injstatic`,
  `<>dotkt_ClrH_Kfc_App.start` unresolved — verified PRE-EXISTING by replaying the same BIR through the HEAD~2
  bir2cir+ilemit, which fail identically; kotc/bir2cir paths provably untouched for that node); line-less crashers
  6 → 4, now exactly the coroutine/SequenceScope-deferred set (chunk/cobuild/collops2/seq); ktproj 9/9.**

- **`il-bymap` ✅ (2026-07-02)** — Map delegation (`val name by data`) — the recorded "cross-assembly KProperty
  identity, NOT trivial" turned out to be canonicalizable after all: `<>dotkt_KProperty`(+`Impl`) IS monomorphic
  (one get_name/ctor(string) shape everywhere — the KIterator_* per-element objection does not apply) and joined
  `CanonicalSynthetics` (apps reference the rt dll's copy; self-correcting for --no-stdlib). Two more pieces:
  kotc's delegate lowering now routes a TOP-LEVEL-extension delegate convention (the accessor body's resolved
  `kotlin.collections.getValue/setValue`) as a plain owner-null callStatic (was: "unsupported delegated property"
  compile error — the doc line above saying "the delegate ROUTES" was stale); and stdlib `MapAccessors.kt` pins
  `getOrImplicitDefault`'s K:=String (the `Map<in String,V>` captured-type approximation made reified CLR dispatch
  `IDictionary<object,V>.ContainsKey` on a `Dictionary<string,V>` → EntryPointNotFound; a variance JVM-ism).
  il-lazy/il-deleg/il-deleg2/il-rwp/il-localdeleg stay green.

**PRIORITY (leverage ÷ effort) + agent routing.**
1. **RC1 — cross-module default args (`joinToString`). DO FIRST.** Highest leverage in the whole cluster: alone flips
   `mapfilter`/`coll2`/`chunk`/`sort` green and is a prerequisite for `collmore`/`seq`/`collops2`/`collrealkt`/`mutcoll`
   (9/10). Also clears the non-collection `bmore`/`bymap`/`fmt`. Effort HIGH but singular. → **a kotc default-args agent**
   (implement `$default` synthetics; consult Codex on the mask/marker ABI and same-module reuse). Gate each retirement
   with the §1 RETIRE-PATTERN recipe (fail-set must not regress).
2. **RC2 — value-type `Nullable<T>` boundary.** Second-highest: flips `arrops` outright, unblocks `collmore` (with RC1),
   de-risks `chunk`. Effort MED. → **a bir2cir/ilemit primitive-dual-rep agent** (MEMORY `primitive-dual-representation`).
3. **RC3 + RC4 — ✅ DONE 2026-07-02 (`aea0e4e` ilemit).** The ilemit member/field resolution on referenced generic Kotlin
   types was fixed in ilemit (NOT bir2cir): the fork resolved to **ilemit-resolves-external** — these are genuine Kotlin
   interface/field references (no BCL substitution), so ilemit derives the open generic def by arity + re-anchors, and
   routes external property field access through the public accessor. Bonus greens: `il-genclosure`/`il-genhof` (shared
   `Iterator[gp:T]` bridge — so RC5's premise that they need generic-closure codegen is WRONG for the iteration path) +
   `il-pair`. `collrealkt`/`mutcoll`/`collops2` themselves stay RED, now gated ONLY by joinToString (StringBuilder→
   Appendable, RC1 blocker (1)) and the **Map/MutableMap dual-rep** (`mapOf`/`associate` → BCL `Dictionary` not
   implementing `kotlin.collections.Map`/`MutableMap` — a new sub-track surfaced here, parallel to List/CharSequence,
   with null-vs-throw `get` semantics). RC5 (HIGH, kotc generic-closure) rounds out the cluster.

## 【5】 exception map → `@ClrTypeAlias`  *(#2)* — ✅ DONE (verified 2026-07-02; was already complete)
- `BirMappings.NET_EXCEPTIONS` DELETED (kotc `5907510`); the 11 stdlib exception classes carry `@ClrTypeAlias` (stdlib
  `c119dd8`, `libraries/stdlib/clr/builtins/Throwable.kt`) with 11/11 parity to the retired map; bir2cir substitutes them
  to `System.*` (verified via metadata: the 11 classes are absent from the rt.dll TypeDefs, present only as TypeRefs).
  Samples throwx/reqnn/customexc/il-exc PASS. (`tryexpr`'s last-line crash is the unrelated `sum`-over-lazy-`map`
  collection bug in 【4】, NOT exceptions.) The `clr.Clr` sample quarantine #2 also listed is DONE (26 deleted + 3
  migrated this session).

---

## 【6】 Coroutine — cold Continuation core + hot CLR Task bridge  *(IN PROGRESS 2026-07-03, plan APPROVED)*
**Authoritative design: `docs/design-coroutine-cold-core-task-bridge.md` (+ its §11 implementation contract).**
The old G1-G6/TypedCont-port plan (`coroutine-stdlib-port-plan.md`) is SUPERSEDED. Summary:
- Core = COLD, Continuation-based (`f$dotkt_suspend(args, Continuation<object>): object` returning value |
  `COROUTINE_SUSPENDED`); public CLR ABI stays hot `Task<T>` via a synthesized TCS+RootContinuation BRIDGE;
  Kotlin→Kotlin suspend calls go direct to the cold body; builders (`sequence{}`, Flow, future kotlinx) are
  ordinary library code over the shared core — the compiler knows NO builder.
- kotc withdraws ALL coroutine lowering (its CPS engine dies at P5 sequence-cutover); **bir2cir does the whole
  SM transform as PLAIN CIR** (`SuspendColdLowering`, after MemberCallSubstitution / before BirTypeLowering;
  skipped in ref build); **ilemit becomes coroutine-free** (Emitter.Coroutines.cs + Co* consts + kickoff
  signature rewrite + suspend stubs all deleted in P6; keeps only the [KotlinFunction(Suspend)] stamp).
- stdlib: `kotlin.coroutines.clr.internal` bases (BaseContinuationImpl/ContinuationImpl/SuspendLambda) +
  RootContinuation/TCS + REAL bodies for the 6 IntrinsicsClr stubs + `kotlin.clr` surface
  (`Task.await()` / `blockOn` / `delay`). **kotlinx is PURGED outright** (user-directed breaking change).
- Phases P0(design-lock ✅)→P1(stdlib cold core+purge)→P2(transform v1)→P3(control flow/generics/lambdas)→
  P4(interop: await/blockOn/delay, cobuild)→P5(sequence cutover, kotc CPS dies)→P6(cleanup; baseline →
  `XFAIL_RUN={bymap}`). Gates at every phase; full plan in the approved plan file.
- Later layers (NOT this bundle): CancellationToken ABI (S); structured concurrency (`Job`/`launch`/`async`) =
  Track 2 = compiling kotlinx over the cold core.

## 【6b】 kotc purity completion  *(NEW 2026-07-03 — user-deferred separate bundle; does NOT gate 1.0)*
Removing the coroutine family does NOT make kotc CLR-free: a 10-family audit (2026-07-03) found the residual.
Keystone = **A2: the `clrName`/`clrInteropName`/`ClrTypeRegistry` facadegen-interop resolution living in the
kotc frontend** (`BirEmitter.kt:4375-4430` + `frontend/ClrTypeInjection.kt` population) — moving it to bir2cir
(same shape as the DONE stdlib `@ClrIntrinsic`→`MemberCallSubstitution` migration, extended to app-injected
types) makes kotc truly CLR-free. Satellites: A1 `appColl` collection-shape map, A3's registry `clr:`/`clrg:`
arm, A6 residual named-BCL-method lowerings (Math/String/Convert/Type/Lazy — some blocked on stdlib-body bugs).
Quick wins (mechanical, independent): A9 fun-interface `@ClrTypeAlias`/`@ClrIntrinsic` DIRECT READ at
`BirEmitter.kt:2216` (a bug vs the "reads NEITHER annotation" invariant), A4 BCL exception-type + accessor
decisions (~12 sites), A5 primitive `System.Int32` shapes, A3 single-type arms (Span/Regex/Closeable/Lazy/
Comparator). Medium: A7 `func:`/Func-Action delegate-shape encoding + `birTypeDeleg` CLR tokens, A8 the
monomorphized `<>dotkt_KIterator/KIterable/CharSequence/RW-ROProperty` synthetics (`<>dotkt_Ref`/`KProperty`
are structural-Kotlin and STAY). Order: A9+A4+A5+A3-single → A7+A8 → A2+A1+A6. (NB: some quick wins may have
landed via the concurrent kotc batch `3db4846` — re-audit before starting.)

## 【7】 1.0 ship gate  *(non-code / production)*
Sources: `remaining-tasks.md F`, `archive/research-roadmap.md Track P/X`.
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

## 【9】 Doc hygiene  *(phase ④)* — ✅ DONE 2026-07-03 (the doc overhaul pass)
- ✅ 7 superseded docs ARCHIVED to `docs/archive/` with HISTORICAL headers + inbound-ref repointing:
  `research-roadmap` · `dotkt-interop-feedback` · `future-work-interop` · `prioritized-tasks` · `gap-analysis` ·
  `bir2cir-migration-inventory` · `bir2cir-handoff`.
- ✅ Cross-doc duplication resolved to ONE home each: bir2cir 6-wave → 【1】 here (the archive keeps the taxonomy);
  coroutine port → `coroutine-stdlib-port-plan.md`; transitive injection → 【3】 here + `dotkt-semantics.md`;
  diagnostics/dist → 【7】 here + `remaining-tasks.md F`.
- ✅ Task docs reconciled: `ship-tasks.md` §1–8 closed out (§0 stays binding); `remaining-tasks.md` D-track
  currency-corrected (pre-stdlib coroutine claims marked historical); this file's source pointers updated.
- ✅ `dotkt-semantics.md` overhauled (TOC; §4 suspend=hot async Task; §5d Appendable; §5e enum shapes; §5f value
  class; §8c `.Companion`); user-facing set created under `docs/user/`; README refreshed.
- Still open (small, code-side or otherwise-owned):
  - `@ClrRefArgument(index)` vs `@ClrRefArguments(mask)` doc inconsistency (the impl is a per-param
    `VALUE_PARAMETER` marker, not a bitmask).
  - facadegen REQ7 design-inconsistency prose (reads `@Clr`, legacy `package clr` generation).
  - Stale code-comment doc pointers: `facadegen/Program.cs:161` → archive/dotkt-interop-feedback,
    `BirEmitter.kt:2909` → archive/future-work-interop, `ClrTypeInjection.kt` → archive/research-roadmap
    (toolchain edits — outside the doc pass).
  - CLAUDE.md's `bir2cir-migration-inventory.md` pointer → repoint to this file 【1】 (coordinator-owned).
  - `clr-stdlib-actual-index.md` numbers are a 2026-06-30 snapshot → regenerate via
    `scripts/gen-stdlib-actual-index.py`.

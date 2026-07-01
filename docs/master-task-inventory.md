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

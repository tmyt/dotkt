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

## 【1】 Layer-purity: the deep bir2cir migration  *(largest architectural remainder)*
Sources: `bir2cir-migration-inventory.md`, `gap-analysis.md §2`, `ship-tasks.md §6–7`.
- **Waves 3–6** (Waves 1 & 2 = physical primitives → `clr.*`, DONE): Wave 3 per-method scope/type env
  (`this`/`local` → `clr.this`/`clr.local`, + `Program.cs:EmitAddr`) → Wave 4 same-module member resolver
  (`setField`/`lateinitGet`/`byref*` → `clr.stfld`/…) → **Wave 5 reference-metadata overload resolver
  (`ClrProjection`, HEAVIEST — real overload resolution + `MakeGenericMethod`; `clrStatic`/`clrInstance`/`clrNew`/…)**
  → Wave 6 delegate/closure construction (`closureNew` = highest risk). Goal: move the CLR lowering still living in
  `BirEmitter` down to bir2cir.
- **Retire 25 ops from the compiler → stdlib** (`strRepeat`/`split`/`listNew`/`linq*`/`tupleNew`/`console`/…; several
  are already dead code = delete only).
- ⚠️ **Currency check needed first:** this 6-wave plan predates the #5 `netType`→bir2cir completion. Re-audit how much
  Wave 3–6 is already absorbed by #5 before diving in.

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

## 【5】 exception map → `@ClrTypeAlias`  *(#2)*
- Retire kotc's `BirMappings.NET_EXCEPTIONS` hardcoded `kotlin.*Exception → System.*` map; `@ClrTypeAlias` the stdlib
  exception classes + let bir2cir substitute. (The `clr.Clr` sample quarantine that #2 also listed is DONE.)

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

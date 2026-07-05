# Bundle-6 remaining-bug inventory

> **RECONCILED 2026-07-05 — this worklist is now HISTORICAL. Every ①/②/④/⑤ + "Deferred follow-ups" item
> below is FIXED/RESOLVED** (verified: all gates XFAIL-ZERO — verify-il 209/0, differential ALL MATCH,
> ktproj 9/9 — plus spot-checks: FilteringSequence.filter, suspendCoroutine E2E, Task.FromResult<T>,
> String.compareTo/toDouble culture, toInt/List[10] exception mapping, MutableList.set, nested-collection
> toString, null->ToString, subSequence — all run correct). The per-section detail below is kept for history.
>
> **GENUINELY-OPEN (the only residuals), tracked in the session task list:**
> - `roundtrip-memext2` — a suspend call inside a `with{}` sub-expression needs scope-function CPS lowering
>   (the ONE remaining `verify-roundtrip` RT_XFAIL; verify-il/differential/ktproj are XFAIL-zero).
> - Interface events (`INotifyPropertyChanged.PropertyChanged`) — deferred; needs kotc to not emit a
>   `ClrEvent<T>` fake-override member (task #9). Static events already work.
> - LOW hardening: `arrayGet` suspension-reorder (N4 sibling) + the same-module `IsSuspendIntrinsicBlock`
>   `NotImplementedError` string-marker (task #8).

---


Authoritative worklist after a full review. Status keys updated as fixes land.

## ① Coroutine new-code correctness (MOST IMPORTANT)
| symptom | site | sev / status |
|---|---|---|
| try/finally across a suspension → finally runs EARLY + TWICE (use{}/withLock broken) | bir2cir SuspendColdLowering.cs:966 (EmitTry) | HIGH · repro |
| cross-module suspend consume → InvalidCast (blockOn{crossFn()} → Task<Int>→Int) | bir2cir cross-asm + facadegen | HIGH · repro |
| suspend LEFT operand reordered after the suspension (sideEffect()+g() eval-order) | bir2cir SuspendColdLowering.cs:1063 (Rewrite) | HIGH-MED · repro |
| value-type-nullable sequence: FIELD erasure FIXED (nextItem:T?->object, kotc marker + bir2cir consumer) — map/toList/first now WORK; only FilteringSequence(.filter{}) still InvalidPrograms in calcNext/predicate | bir2cir/ilemit value-type FilteringSequence | MED · NARROWED · Wave-2 |
| unresolved suspendCoroutine closure → permanent suspend (silent hang) | bir2cir SuspendColdLowering.cs:1177 | MED |
| suspend fun main async drain uses null completion (NRE / lost result) | bir2cir SuspendColdLowering.cs:1848 (DrainMain) | LOW-MED |
| ~~F1: SafeContinuation UNDECIDED/RESUMED boxed-enum identity (time bomb, fires when F2 lands)~~ **FIXED 2026-07-05** — SafeContinuationClr.kt caches UNDECIDED_BOX/RESUMED_BOX (mirrors COROUTINE_SUSPENDED_BOX) + uses them for the ctor default, the RESUMED write, and every `===` check | stdlib SafeContinuationClr.kt | ✅ DONE |
| ~~F2: suspendCoroutine doesn't lower E2E (missing feature, hides F1)~~ **FIXED 2026-07-05** — root: our compiler does NOT inline @InlineOnly cross-module, so an APP's `suspendCoroutine{…}` reaches bir2cir un-inlined (`callStatic suspendCoroutine(<closure>) suspendCall:true`, owner resolved to `kotlin.coroutines.ContinuationKt`) → the `delegateNew`/`closureNew` block arg tripped `LambdaKinds` → the fun was rejected (0 transformed → ilemit "un-lowered" crash). Fix: `SuspendColdLowering.IsSuspendCoroutineCall` recognizes the shape; `EmitSuspendCoroutineCall` reconstructs the wrapper's SafeContinuation body in the caller SM via the new public `clr.internal` bridges `newSafeContinuation`/`safeGetOrThrow`. Case `cases/il-suspendco` (sync resume → 42; resumeWithException → caught). | bir2cir SuspendColdLowering.cs + stdlib ContinuationImpl.kt | ✅ DONE |

## ② async interop (facadegen, exposed by coroutines)
| symptom | site | sev |
|---|---|---|
| generic static vanishes (Task.FromResult<T>/Run<T> absent → can't build Task<T> from Kotlin) | facadegen Program.cs:534 | HIGH |
| suspend nullable return reads the OUTER Task (suspend fun f():String? de-nullified) | facadegen Program.cs:577 | HIGH |
| interface suspend/nullable qualifiers dropped (can't call as suspend) | facadegen Program.cs:363 | MED |
| denylist regex silently drops a collection-returning user tlfun | facadegen Program.cs:1094 | MED |
| generic-type operator silently dropped (FullName==null guard always true) | facadegen Program.cs:601 | MED |
| un-retargeted DotKt.Stdlib.dll → user-lib members with stdlib-typed sigs silently dropped | facadegen Program.cs:219 (catch) wiring | MED |

## ③ harness/gate — FIXED 2026-07-04 (Agent C, commit 624ccb1)
- verify-differential RED (JVM oracle: kotlin-compiler-embeddable needs kotlinx-coroutines-core) → kotlinx jar re-added to CCP. GREEN.
- roundtrip harness didn't pass DotKt.Stdlib refs → rt stdlib added to ilemit --ref + copied. GREEN. (surfaced ①'s cross-module + with-scope suspend as the true roundtrip blockers.)

## ④ existing stdlib silent-divergence (coroutine-UNRELATED, all repro'd)
String.compareTo culture-dependent (order reversed vs JVM) [builtins/String.kt:50] · toDouble/toInt culture-dependent Parse ("3,14".toDouble() doesn't throw) [StringNumberConversionsClr.kt] · "abc".toInt() NumberFormatException not caught as FormatException → process abort [Throwable.kt:89] · List[10] ArgumentOutOfRange not caught as IndexOutOfBounds [Throwable.kt:68] · MutableList.set/removeAt void-bound → InvalidProgram when value consumed [builtins/Collections.kt:67/69] · bymap getValue → clrMapGet EntryPointNotFound [ClrMapDefaults] · for((k,v) in mutableMap) EntryPointNotFound (mutable only) [ClrMapDefaults.kt:122] · HashSet(cap,loadFactor) wrong-overload EntryPointNotFound [TypeAliasesClr.kt] · digitToIntOrNull InvalidProgram even for ASCII '7' [CharKt] · printStackTrace() NRE [ExceptionsClr.kt:23] · Map/nested-List toString raw .NET type name [clrCollToString unrouted] · differential maxOrNull(Double) "not a GenericMethodDefinition" (m-b6) / sumOf{}=0 (m-b9) / groupBy (m-b10) [ilemit/stdlib Map]

## ⑤ latent/narrow
kotc .NET-member generic branch missing suspend tag [BirEmitter.kt:3524] · bir2cir shadowed-var intrinsic-capture skip [:775] · ilemit clrPropSet value-type receiver not by-address [Program.cs:3190] · EmitDynamicCall gp not boxed [Expressions.cs:50] · bir2cir null→ToString NRE [Program.cs:2884] · subSequence double-eval [:2870]


## Deferred follow-ups (2026-07-04)
- **star-projection is-test full lowering** (iscoll XFAIL): `x is Collection<*>` lowering the isinst to non-generic ICollection makes it true for value-type collections, but the smart-cast member access (`(this as Collection<*>).size`) still castclasses the reified `IReadOnlyCollection<object>` -> InvalidCast. Needs the star-cast + member-access to ALSO route to the non-generic BCL interface (ambiguity: `Collection[object]` star vs real Any). Fix #6 reverted to protect map/filter (collectionSizeOrDefault hot path).
- **FilteringSequence.filter value-type**: value-type `asSeq.filter{}` still InvalidPrograms (map/toList/first work).
- ⑤ bir2cir latent: shadowed-var intrinsic-capture / null->ToString NRE / subSequence double-eval.
- Map dual-rep sub-track (bymap, m-b6/m-b9/m-b10, map is Collection): separate track.

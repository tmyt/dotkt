# Bundle-6 remaining-bug inventory (user review, 2026-07-04)

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
| F1: SafeContinuation UNDECIDED/RESUMED boxed-enum identity (time bomb, fires when F2 lands) | stdlib SafeContinuationClr.kt:33/46/52 | MED · latent |
| F2: suspendCoroutine/createCoroutine doesn't lower E2E (missing feature, hides F1) | ilemit Program.cs:1992 / bir2cir | MED |

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

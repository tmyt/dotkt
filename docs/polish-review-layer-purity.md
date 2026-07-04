# Polish review — layer purity + coroutine design consistency (user review, 2026-07-04)

Address AFTER the XFAIL set is zero (user-directed ordering). Theme: kotc still holds CLR knowledge
(BCL member names, type specializations) that the 4-layer architecture assigns to bir2cir
(`compiler-layer-responsibilities`), plus coroutine design-consistency debts. This is the
**bundle-8 "kotc purity completion"** work (plan Appendix) + the coroutine polish.

## Overall — coroutine design consistency
- **[High] delay / blockOn residue contradicts the design.** Policy = NOT in stdlib. But a common
  `expect`, a jar stub `actual`, a taskinterop `actual`, and the Monitor-drain impl still exist. Delete them
  (re-home to the test harness over public primitives, per the earlier decision).
- **[High] kcc-origin suspend-function-type doesn't round-trip.** A `Func<…,Task<T>>` from a CLR-origin
  assembly is a normal function; but a kcc-origin position that WAS `suspend (…) -> T` needs an attribute —
  and NOT only on PARAMETER: also on RETURN / PROPERTY / FIELD positions.
- **[High] Unit public suspend bridge may be off-ABI.** ABI: `suspend fun f(): Unit` -> a NON-generic `Task`.
  Current emits `Task<Unit>` (bir2cir SuspendColdLowering.cs:2267). Fix the emit OR update the ABI doc.
- **[Medium] CLR Task delegate adapter unimplemented.** suspend lambda -> `Func<…,Task<T>>` and
  `Func<…,Task<T>>` -> suspend lambda: target-type-driven conversion still missing.

## kotc — CLR knowledge that belongs in bir2cir
- **[High] Throwable.message/cause -> System.Exception.Message/InnerException** (BirEmitterExpressions.kt:128,
  BirEmitter.kt:3051) — exception types are @ClrTypeAlias (bir2cir reads it); kotc knowing BCL prop names is a layer violation. (ALSO in ilemit — double lowering, see below.)
- **[High] kotlin.Lazy<T> -> System.Lazy<T> specialized in kotc** (BirEmitter.kt:3181/3731/4453) — `lazy{}`
  creation, delegate access, type representation all decided by kotc. Move to stdlib impl or bir2cir CLR platform lowering.
- **[Medium] kotlin.text.Regex -> System.Text.RegularExpressions.Regex in birType** (BirEmitter.kt:4416) —
  call lowering says "Regex lowering retired, bir2cir reads @ClrTypeAlias"; only the TYPE alias lingers in kotc (inconsistent).
- **[Medium] Closeable/AutoCloseable -> System.IDisposable in kotc** (BirEmitter.kt:4435/2188) — `use` lowering is
  language-ish but the CLR type/method names should move toward bir2cir.
- **[Medium] toInt()/toLong() etc -> BIR/CIL conv directly in kotc** (BirMappings.kt:112, BirEmitter.kt:3896) —
  emit a "Kotlin numeric conversion fact"; the conv-to-CLR belongs in bir2cir+.
- **[Low] @ClrAwait filter/comment residue** (BirEmitter.kt:507/1449) — decide: keep @ClrAwait as a live spec or delete.
- **[High] interface override -> BCL slot names in kotc** (BirEmitter.kt:960) — Collection.size->get_Count,
  List.get->get_Item, iterator->GetEnumerator, Closeable.close->Dispose. Move to bir2cir @ClrIntrinsic / override-slot resolution.
- **[High] injected .NET indexer -> get_Item/set_Item in kotc** (BirEmitter.kt:3459) — BIR should carry a
  semantic clrIndexerGet/Set (or metadata marker); the real CLR name is bir2cir's.
- **[High] .NET event accessor add_<E>/remove_<E> naming in kotc/frontend** (ClrTypeRegistry.kt:66,
  BirEmitter.kt:3539) — frontend should hold event METADATA; bir2cir makes clrEventAdd/Remove.
- **[Medium] clrInteropName / memberClrName BCL member names in kotc** (BirEmitter.kt:4217) —
  size->Count, get->get_Item, append->Append, StringBuilder.get->get_Chars.
- **[Medium] extension-property calls built as get_<name>/set_<name> static/instance in kotc**
  (BirEmitter.kt:3579/3634/3657) — keep as a semantic property call in BIR if separating layers.
- **[High] clrName / clrInteropName = mixed-responsibility hotspot** (BirEmitter.kt:4145) — registry .NET
  injected types + stdlib substitute + hardcoded collection/StringBuilder slot map + @ClrIntrinsic residue
  comments all in one function; `useAnnotation` is vestigial-per-comment but still branches.
- **[High] "retired" lowerings still have residual CLR** (BirMappings.kt:20, BirEmitter.kt:3996) — math retired
  but coerceAtMost/AtLeast/In->System.Math remain; string ops Trim/Replace/PadLeft/IsNullOrWhiteSpace remain.
- **[Medium] frontend injection + backend registry share CLR naming convention (name, not semantic metadata)**
  (ClrTypeRegistry.kt:65, ClrTypeInjection.kt:490/658) — events add_/remove_, indexer get/set -> get_Item/set_Item.
- **[Medium] collection CLR representation scattered** (BirEmitter.kt:3128/4177/4368) — appColl, listNew/mapNew
  factory, default-helper routing, toString routing all separate; hard to reconcile with the @ClrTypeAlias policy.
- **[Low] reflection/object override in kotc** (BirEmitterExpressions.kt:221, BirEmitter.kt:3224/4349) —
  classRef/getType/objectMethodOverride: make them semantic nodes, bir2cir lowers to System.Type/ToString.

## ilemit
- **[High] Throwable.message/cause -> Message/InnerException correction ALSO in ilemit** (Emitter.Expressions.cs:80)
  — double legacy lowering with kotc. If bir2cir resolves via @ClrTypeAlias, ilemit should emit the field / trust CIR clrPropGet.
- **[Medium] conv comments/input-contract still reference Kotlin stdlib calls (x.toLong())** (Emitter.Expressions.cs:266,
  Program.cs:2218) — ilemit emitting conv is correct; scope its contract to "CIR conv instruction", drop the Kotlin-aware wording.
- **[Medium] leftover "suspend":true -> throwing stub** (Program.cs:814/958) — fine for ref build; reaching it in
  app/rt is a bir2cir miss currently swallowed — make it error-like so the layer failure is visible.
- **[Medium] no suspend-function-type POSITION metadata path** (Emitter.CompilerServices.cs:50, Program.cs:632) —
  only method-level [KotlinFunction(Suspend)]; need a KotlinSuspendFunctionTypeAttribute for param/property/field/return round-trip.
- **[Low] sfunc: not handled in MapType** (Program.cs:3394/3563) — fine if bir2cir strips sfunc: from type slots;
  but for the round-trip attribute, make "CLR type = Func<…,Task<T>> + separate suspend-origin attribute" an explicit CIR contract.

## bir2cir
- **[High] kotlin.clr.CoroutinesKt excluded from suspend lowering by FILE-CLASS name** (SuspendColdLowering.cs:68)
  — await-as-marker is OK for now, but the comment still mentions delay (dropped). Narrow the skip to the await marker only, or make it intrinsic/metadata-based.
- **[High] public Task bridge Unit -> Task<Unit>** (SuspendColdLowering.cs:2267) — see Overall Unit-ABI item.
- **[Medium] sfunc: -> object in type slot, funcType folded to func:** (Program.cs:2254/2312) — SM-object rationale
  is sound, but the suspend-lambda-origin round-trip attribute isn't connected; if origin is erased here, emit the position metadata elsewhere.
- **[Medium] suspendCoroutineUninterceptedOrReturn detected via ERROR-MESSAGE STRING marker** (SuspendColdLowering.cs:92)
  — fragile; kotc should emit a stable BIR node / intrinsic tag that bir2cir reads.
- **[Low] sequenceNew (old kotc CPS/sequence node) still in the disqualifier guard** (SuspendColdLowering.cs:77) —
  sequenceNew is gone; stale guard, remove unless kept for old-input compat.

## Approach (after XFAIL=0)
The dominant theme is **layer-purity migration**: move BCL-name/type-specialization knowledge out of kotc into
bir2cir's @ClrTypeAlias/@ClrIntrinsic substitution (kotc emits semantic FQN/metadata; bir2cir derives the CLR form).
Group by mechanism: (1) exception/Regex/Lazy/Closeable TYPE aliases -> @ClrTypeAlias read by bir2cir (delete kotc birType specializations + the ilemit Throwable double-lowering); (2) BCL member/accessor slot names (get_Count/get_Item/GetEnumerator/Dispose/Count/Append/get_Chars) -> bir2cir member substitution; (3) indexer/event -> semantic BIR nodes (clrIndexerGet/Set, clrEventAdd/Remove) + metadata; (4) numeric conversion -> "numeric conversion fact" in kotc, conv in bir2cir; (5) the clrName/clrInteropName hotspot -> split/retire; (6) coroutine: delete delay/blockOn residue, Unit bridge ABI decision, suspend-function-type position attribute (kotc fact -> bir2cir type-slot + ilemit attribute), replace the error-string suspendCoroutine marker with a stable tag, drop stale guards.

---

## Second-perspective review (user, 2026-07-04) — additional themes

### Validation (not a task) — the coroutine bundle landed on the 4-layer design correctly
kotc holds ZERO coroutine lowering (facts only, CPS engine deleted with no orphans); ilemit is
coroutine-free (Emitter.Coroutines.cs gone, A1-A4 hardening intact); all lowering in bir2cir (cold-core
SM structurally sound, Task<R> bridge correct for all return shapes, ContinuationErasure idempotent,
reverse await bridge sound); B2 suspend channel wired (HasSuspendMember is a real consumer, cross-asm
fixpoint); COROUTINE_SUSPENDED box cache is a sound fix for the value-type-enum re-box JVM difference.

### [NEW THEME — High-value] Failure posture: make silent fallbacks LOUD
Silent fallbacks turn routing mistakes into distant runtime symptoms ("gate green but broken"):
- **bir2cir**: `?? cands[0]` (picks an ARBITRARY overload), Rule-4 silent dynamic dispatch, an
  unresolved `suspendCoroutine` closure -> PERMANENT suspend (silent hang) — should be a COMPILE-TIME throw.
- **ilemit**: the suspend throw-stub turns a bir2cir transform-MISS into a runtime throw instead of a
  loud EMIT-TIME error (boundary-correct but diagnostic-poor in app/rt builds).
Principle: a routing/transform miss should fail LOUD at compile/emit time, not silently degrade to a
runtime hang/throw. Audit the `?? cands[0]` / Rule-4 / suspend-stub sites and make the app/rt path error.

### [Dead code / staleness]
- ilemit `ilemitCompatBir` envelope branch — ZERO producers, dead枝, delete.
- CPS-deletion orphans (LambdaKinds/steps/coClass guards) — harmless, tidy.
- Stale comments: `SuspendLambdaLowering.cs` "dormant/NO-OP" header (it is LIVE now), kotc deleted-engine
  reference comments, bir2cir "no consumer yet".
- **XFAIL_ILVERIFY `[chunk]=` has 4 DUPLICATES** (bash last-wins → the first 3 diagnostic notes are dead) — dedup.

### [Coverage — add test cases; no case = green slips through]
- exception propagation across a SUSPENDED Task (no case).
- `@RestrictsSuspension` (commits exist, ZERO test cases).
- `il-coldvirt` un-wired DEAD fixture — wire it.
- (try/finally over suspension IS now covered = il-cofinally; digitToIntOrNull / try-expr-operand cases now added.)

### Overlaps with review 1 / already in-flight
- Layer-boundary debt (kotc coerceAt*/require/check/KClass/Throwable.message-cause/Closeable/Lazy/compareTo/
  StringBuilder slot-maps; ilemit Throwable.message/kotlin.Unit/Array.Clone/ReverseBridge name-lists) = the
  bundle-8 layer-purity work above.
- TaskAwaiter `constrained.` prefix (~2 lines, kills taskawait/genasync/comaindrain ilverify XFAILs) = IN FLIGHT
  (ilemit zero-XFAIL agent).
- build-cache-masks-stdlib-regressions = documented (MEMORY build-cache-masks-stdlib-regressions).

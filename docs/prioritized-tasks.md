# Prioritized task list

> A **priority-ordered backlog**, NOT a ship checklist (no release is committed at any point here — see
> MEMORY `release-flow-0.9.4-accumulate`). Last updated 2026-07-01.

## Main sequence (priority order)

1. **rt-green** — drive the rt stdlib build (`build-clr-stdlib-runtime.sh --emit`) to completion. *(in progress)*
2. **exception map → @ClrTypeAlias** — retire kotc's `BirMappings.NET_EXCEPTIONS` hardcoded `kotlin.*Exception→System.*`
   map (`@ClrTypeAlias` the stdlib exception classes + bir2cir substitutes + delete the kotc map). Plus quarantine the
   33 old `clr.Clr` samples. (MEMORY `exception-map-to-clrtypealias`)
3. **implicit ref-passing** — `@ClrRefArgument` byref for stdlib methods; unblocks atomics (Interlocked), TryParse,
   DivRem. Kotlin has no ref/out syntax → binding-metadata-driven. (MEMORY `implicit-ref-passing-to-stdlib-methods`)
4. **facadegen app .NET interop** — operators (`op_*`), C#-origin extension methods, static `.Companion` routing,
   dual-rep collision (`import System.Text.StringBuilder` vs stdlib alias).
5. **netType→bir2cir migration completion** — finish removing kotc's CLR knowledge so kotc reads NEITHER annotation +
   emits pure FQN identities. **Progress (2026-07-01)**: the rule-3 helper hoist + the `@ClrTypeAlias` type-strip are moved
   to bir2cir (kotc reads NO `@ClrTypeAlias`); the `@ClrIntrinsic`/`clrName` removal is **DONE** — `annClr` (kotc's
   `@ClrIntrinsic` reader) is DELETED; bir2cir reproduces every rename/strip/property-routing from the ref.dll and the
   rt-stdlib CIR is byte-identical (0/237 diff). **Proven gate-neutral**: the `verify-il` FAIL set is identical before
   (`c140acf`) and after (`d4b243d`) the annClr removal — 83 pre-existing runtime FAILs + 5 pre-existing VERIFY FAILs
   (`customexc`/`tryexpr`/`gen3`/`gen4`/`mc1`), none introduced or fixed by the removal. **Remaining = the `netType` chunk.
   The `netType` chunk is elevated to THE NEXT priority (user, 2026-07-01)**
   — it is NOT just layer purity, it **improves overload resolution**: `netType` resolves `kotlin.*→System.*` in the
   argTypes/ret/catch slots (which ilemit uses to pick the BCL overload), AND has no map for user classes so it **degrades
   user/reference types to `System.Object`** (BirEmitter.kt ~1956), which LOSES overload fidelity — a user `Foo : IComparable`
   passed to a BCL method with `(IComparable)` + `(object)` overloads can then only match `(object)`. Moving the resolution to
   bir2cir (which has the ref.dll + the full type hierarchy) keeps the real static types → strictly MORE ACCURATE overload
   resolution. NB: boxing of value types is a separate emit-time op (`EmitArg`), NOT an argType concern — the object-collapse
   is *unconditionally* a loss (the only legit `System.Object` argType is when the static type is genuinely `Any`).

   **(a) DONE (2026-07-01) — kotc is now a PURE Kotlin frontend.** `grep netType` in kotc = **0** (deleted). kotc does NO
   CLR type resolution (all sites emit pure-FQN `birType`; Unit→void + Sequence→IEnumerable resolve in bir2cir) and NO
   coroutine lowering (the suspend→CPS/Task state machine is REMOVED — kotc emits neutral tags `"suspend":true` on decls +
   `"suspendCall":true` at calls; the actual lowering is a DEFERRED downstream layer, coroutine-lowering-layer-deferred).
   The **Object-collapse fix is verified** (`list[0]=Widget` argType `System.Object`→`@Widget`). Journey: core netType→birType
   (`6ad3f24`/`bb455d3`/`4330509`, Sequence alias `f8321c4`) → DotKt.Runtime retirement (`03d1465`/`cb09ccd`/`09c9156`/
   `2ae8b99`/`7eced37`, sheds the kotlinx/Result forwarding) → kotc-pure 4 stages (`de2531f`/`28f1eb5`/`0aae262`/`ea5248c`,
   delete netType + coroutine lowering) → ilemit graceful-suspend stub (`00ad2f1`, an app suspend fn emits a throwing stub
   instead of crashing the emit). Gate: passing core byte-identical; the ONE net regression is `cobuild` (the only
   previously-passing coroutine sample) — an AUTHORIZED coroutine-deferred casualty (kotc no longer lowers it; now a clean
   runtime throw, not an emit crash). Residual +1 follow-ups from the retirement: `il:regex` (CharSequence dual-rep — runs
   but ilverify-fails) + `il:result` (value-type `T?`→Nullable dual-rep) — both pre-existing dual-rep gaps the real stdlib
   exposed. **#5(a) netType→bir2cir migration is COMPLETE.** Remaining #5-adjacent CLR-interop cleanups (NOT the netType
   migration): byref rework (`ClrRef`→`@ClrRefArgument`, stub-jar deletion — ABI parity), il:outref/il:stackalloc ilemit
   byref crash, Span/stackBuffer→kotlin.clr namespace move.

   **UNIFIED SCOPE (user, 2026-07-01)** — #5 is "accurate bir2cir **type + member** resolution", THREE sibling workstreams
   sharing one root (kotc emits pure FQN / no CLR resolution; bir2cir resolves accurately from the ref.dll + hierarchy):
   - **(a) netType→bir2cir** (type resolution): as above. Investigation DONE (2026-07-01) — bir2cir's `BirTypeLowering`
     already walks `argTypes`/`ret`/`retType`; the switch is a *vocabulary shift* (`System.Int32`→`int`, ilemit-equivalent)
     NOT byte-identical, so verify FUNCTIONALLY (rt.dll emit+ilverify + `dotkt --run` passing samples); the Object-collapse
     fix comes free (birType preserves user tokens); `birType` already covers `Span`/`ClrRef`; the real gaps are the special
     stdlib types `Sequence`/`Result`/atomics (best fixed by `@ClrTypeAlias` in the stdlib → bir2cir uniform). Coroutine
     types (`CancellableContinuation`, `coCatchBegin`, `coTaskType`) stay on `netType` until the coroutine layer.
   - **(b) property-accessor first-class binding** (member resolution) — **DONE (2026-07-01)**: new `@kotlin.clr.ClrProperty(access, name)`
     annotation (`READ`=1/`WRITE`=2 Int flags, combinable via `or`; `@Target(FUNCTION, PROPERTY)`) states the accessor role
     EXPLICITLY. bir2cir reads it from the ref.dll (`ClrPropertyOf`/`TryMemberProperty`/Rule 2p) → `access&READ`→clrPropGet(name),
     `access&WRITE`→clrPropSet(name); the fragile `get_`/`set_` intrinsic-string prefix-sniff (trigger ②) is REMOVED (only the
     genuine `val X`→`get_x` member-prefix ① remains). Migrated the 5 fun-bound plain accessors (StringBuilder setLength/capacity/
     nativeSetCapacity, MonoTimeSource ticks, ClrIterator current); indexers (`get_Item(i)` — index arg = real methods) stay
     @ClrIntrinsic. Gate-neutral (verify-il FAIL set identical, 83). Commits: nested stdlib `f882102`, bir2cir `e9b3ec9`.
     ORIGINAL PROBLEM (kept for context): the old model was ASYMMETRIC — read = `@ClrIntrinsic("Length")`
     bare name on the property → clrPropGet; write = a *standalone* `fun setLength(n) @ClrIntrinsic("set_Length")` whose call
     bir2cir routes to clrPropSet by **sniffing the `"set_"`/`"get_"` string prefix** of the intrinsic. This "folds a property
     into a method binding" + prefix-sniff is fragile (a real method named `get_foo` would mis-route). TARGET: a val/var's single
     `@ClrIntrinsic("X")` binds the .NET property as a UNIT (read→get_X, write→set_X), and a method that targets an accessor slot
     is marked EXPLICITLY (not inferred from a `"set_"` string). Indexers (`operator get/set`→`get_Item`/`set_Item`) are genuinely
     methods and stay method-bound.
   - **(c) ctor overload argTypes** — **DONE (2026-07-01)**: `StringBuilder("hello")` was resolving to `StringBuilder(Int32)` →
     `InvalidProgramException`. Root cause was DEEPER than "missing argTypes": bir2cir's `TransformNew` already synthesized argTypes,
     but ilemit's `ClrRef` sent the lowered primitive-SHORTHAND token (`"string"`) straight to `ResolveType` (FQN reflection) which
     can't resolve shorthand → threw → `EmitClrNew` nulled → ctor picked by ARITY only → wrong overload. FIX (general, no StringBuilder
     special-case): ilemit `ClrRef` routes bare primitive/string/void/object shorthand through `MapType` (root fix); added `NewCtorBySig`
     (ctor match by argTypes, arity fallback) for external `new`; kotc now emits `argTypes` (pure Kotlin FQN via `birType`) on the plain
     `new` node. Verified: StringBuilder("hello")→5/hello ilverify-clean; String/Int/no-arg ctor paths all resolve; ref+rt clean;
     gate-neutral (verify-il FAIL set identical, 83). Commits: ilemit `30d96aa`, kotc `dfcefcc`.

   **netType (a) — REFINED PLAN (user, 2026-07-01), by NAMESPACE:** the "special types" split by namespace, NOT all @ClrTypeAlias'd —
   - **kotlinx.\*** (`kotlinx.atomicfu.*`, `kotlinx.coroutines.*` types) are NOT stdlib (bundled libs, MEMORY kotlin-net-is-pure-binding):
     their netType branches + bespoke BirEmitter lowering (atomicfu factory→`clrNew DotKtx.Atomicfu.*`) are BOTH a layer AND a scope
     violation → REMOVE, do not alias/migrate. `DotKtx.*` bespoke names are the "compiler knows an external lib" smell, to retire.
     (NB: `kotlin.concurrent.atomics.*` — the kotlin.\* stdlib atomics, `AtomicsClr.kt` — is DIFFERENT and STAYS as a real emitted type.)
   - **kotlin.\* pure-Kotlin** (`kotlin.Result`, a `value class` with no BCL equivalent) → emit as the REAL `kotlin.Result` type,
     RETIRE the `DotKt.Result` shared-struct + its bespoke BirEmitter lowering. @ClrTypeAlias is WRONG here (that's Kotlin↔BCL dual-rep only).
   - **kotlin.\* dual-rep to BCL** (`kotlin.sequences.Sequence` → `System.Collections.Generic.IEnumerable`) → `@ClrTypeAlias` (the only alias case).
   - **coroutine TYPES ≠ coroutine LOWERING**: the types are just CLR types (resolved like the above); only the suspend→state-machine
     LOWERING is the deferred BIR-level decision. So NO coroutine-type carve-out in netType.
   - Everything else (primitives/collections/exceptions/user types): netType→`birType` "just works" (bir2cir already lowers argTypes via
     the rich @ClrTypeAlias set); the Object-collapse fix comes free. Verify FUNCTIONALLY (vocabulary shift `System.Int32`→`"int"`, NOT
     byte-identical). `birType` already covers Span/ClrRef/Regex/Comparator/IDisposable.
6. **coroutine lowering layer** — deferred design (Task-based). (MEMORY `coroutine-lowering-layer-deferred`)

## App / MSBuild / round-trip (added 2026-07-01; cluster around #4/#5)

7. **MSBuild app + lib** — build BOTH an app and a library with MSBuild, and reference the lib from the app via
   `<ProjectReference>`.
8. **Round-trip comprehensive review** — audit for any Kotlin semantics the Roundtrip attributes CANNOT restore
   (find the gaps, not just the known ones).
9. **MSBuild practical cases** — implement a variety of practical sample cases and confirm they build AND run via MSBuild.
   *(in progress)* DONE: the app-consume gap for a **List local + referenced top-level stdlib funs + `for (x in list)`** —
   bir2cir attributes a `callStatic owner=null` to its rt-dll file-class owner (Gap B), ilemit picks the arity-matching
   overload, and a bir2cir pass re-points the for-loop iterator protocol at the real `kotlin.collections.Iterator<E>`
   (Gap A; needed the rt bridge made `public`); `cases/ktproj-coll` builds+runs (`first`/`getOrElse`/`contains`/`indexOf`/
   `count`/`isEmpty`/`take` + two `for`-loops). REMAINING app-consume gaps:
   - **Collection-BUILDING ops (`map`/`filter`/`sorted`/`reversed`)** now reach the stdlib body (Gap B routed them) but
     crash *inside* it: `mapTo`/`filterTo` do `ICollection<T>.Add` on the result `ArrayList`, which throws
     `EntryPointNotFoundException` — the rt stdlib's **mutable-collection (ArrayList) actuals** are not bound. Layer =
     **stdlib** (the mutable-collection platform actuals), not the compiler.
   - **Element-type-overloaded statics (`sum`)**: arity alone can't disambiguate `sum(Iterable<Int>)` vs `sum(Iterable<Double>)`
     (both 1 param) — needs element-type matching in ilemit's reflected lookup or a bir2cir owner+overload pick. `last`/`lastIndex`
     remain blocked by the known generic-ext-property-getter-typeargs bug.

## Cross-cutting categories (not in the linear sequence)

### A. Known bugs (MEMORY known-bugs)
- cross-module default-args (frontend jar drops default VALUES → IrErrorExpression; ~20 samples)
- generic ext-property getter typeargs (`List.last()`/`lastIndex` "not fully instantiated")
- dual-representation open cases (Comparable-self-ref / `use{}`)
- `@InlineOnly` drops `@ClrIntrinsic` cross-module (direct `s[i]=c`)

### B. Layer-purity follow-ups + performance
- kotc "reads NEITHER annotation" final form — DONE so far: (1) rule-3 helper-EMISSION is fully bir2cir's
  (`clrHelperClassJson`/`clrHelperMethod`/`clrHelperMembers` DELETED); (2) the `@ClrTypeAlias` type-STRIP is fully
  bir2cir's (`substitutedAway`/`hasClrTypeAlias`/`hasHoistableBody`/`aliasPlainTypes`/alias-only-branch DELETED — kotc
  emits ALL types as ordinary Kotlin; bir2cir `AliasHelperHoist` drops every alias type def, helper only for `kind ==
  class`). (3) `clrName` migration UNDERWAY: Step 1 = kotc emits pure-Kotlin `overrides` markers; Step 2a = bir2cir
  `DeclarationRename` (markers + ref.dll `@ClrIntrinsic`, exact-arity) + property-scan reproduce the decl slot-names
  (`get_Count`/`ResumeWith`) IDEMPOTENTLY (byte-identical, annClr still active). **Remaining Step 2b/3** (the `annClr`
  removal — not single-pass-safe): `fn`-self in the marker + `properties:[{get,set}]` rename + `@ClrIntrinsic`-bound
  member-strip + fun-interface-SAM rewrite, then kotc plain-naming + delete `annClr`. Then the `netType`
  `kotlin.*→System.*` type map (move to bir2cir; `birType` already emits bare FQNs — netType is the legacy twin still
  emitting `System.Int32`/`System.Object`) (#5)
- `stackBuffer`/`Span` `FqName.ROOT` → `kotlin.clr.*` (§6)
- **static-helper (rule-3) performance review** — audit the stdlib pieces implemented as static helpers for perf
  problems; reimplement them a better way where found. *(added 2026-07-01)*

### C. rt-green internals (part of #1)
- unsigned value-class conversions — FIXED via the inline-class `.data` erasure collapse
- BLOCKED stdlib bindings (unsigned `Div_Un` etc., awaiting ilemit ops)

### D. Hygiene / recording
- quarantine/remove the 33 old `clr.Clr` samples (testing a removed feature)
- `docs/dotkt-semantics.md` — record this session's behavioral deltas
- `CHANGELOG` `## Unreleased` — accumulate this session's fixes (per `release-flow-0.9.4-accumulate`)
- **`scripts/` cleanup** — retire/consolidate old scripts (retired-backend leftovers, stale stdlib builders).
  *(added 2026-07-01)*

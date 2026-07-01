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
   - **(c) ctor overload argTypes**: kotc emits `new` nodes for stdlib/user types with NO `argTypes` (unlike `clrNew`), so when
     bir2cir substitutes the type to a BCL type with overloaded ctors, ilemit value-infers and picks WRONG: `StringBuilder("hello")`
     resolves to `StringBuilder(Int32)` (capacity) → ilverify `found 'string' expected Int32` → `InvalidProgramException`. FIX:
     carry the arg static types on `new` (bir2cir attaches them on substitution, or kotc emits them as FQN) so the ctor resolves by
     signature. Pre-existing (annClr's bir2cir changes are member-routing, not ctor selection); same family as the #9 app-consume gaps.
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

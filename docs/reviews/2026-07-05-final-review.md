# Final Review — 2026-07-05 (post bundle-6 + fine-bug cleanup)

**Reviewed at:** HEAD `18fb24b`
**Method:** 6 layer specialists (kotc / bir2cir / ilemit / stdlib / facadegen / gates) + coordinator race-free probes. Each agent ran/re-probed against fresh (clean-rebuilt) ref+runtime stdlib.
**Scope:** verify the "all bugs — including fine ones — fixed, gate ZERO XFAIL" claim; hunt new bugs in the post-bundle-6 work (interop-no-registry, ClrEvent<T>, @Volatile, Lazy migration, cold-sequence-SM completion, clrCollAdd, Regex groups); assess non-bug quality.

---

## VERDICT

**The gate is genuinely green — but "all bugs fixed" is REFUTED.**

- `verify-il`: `PASS(run) 202 / VERIFY 160 / 0 fail / XFAIL-zero` — **real**, independently confirmed by the bir2cir, kotc, and ilemit agents (each got a clean solo run) plus the coordinator's 8/8 race-free probe. Both `XFAIL_RUN` and `XFAIL_ILVERIFY` are genuinely empty. Every previously-flagged bug is FIXED/MIGRATED.
- **BUT** the project's own worklist `docs/bundle6-remaining-bugs.md` still lists open items, AND the deep review found **new, un-gated crashes** — including one memory-corruption (AccessViolationException). The gate is trustworthy but NARROW: its coverage does not reach regex-beyond-find/replace, `suspendCoroutine`, nested `toString`, or `Task.WhenAll/WhenAny`.

**Environmental caveat:** every agent hit a false-RED gate run caused by concurrent build processes churning the shared `build/` tree (companion `-r` dlls momentarily deleted → `FileLoadError` on interop-injection samples: `c1net fieldvis firgap injbase injfqn injstatic injuint`). All were re-verified clean with their companion assembly. **A true green/red certification requires a quiescent solo gate run.**

---

## NEW FINDINGS (un-gated; ranked by severity)

### N1 — [HIGH+] `Regex.replaceFirst` — wrong result + AccessViolationException (memory corruption)
- **File:** `libraries/stdlib/clr/kotlin/text/regex/RegexClr.kt:165-171`
- `replaceFirst("banana","X")` returns `banana` unchanged (correct: `bXnana`, Codex-confirmed). With a `CharSequence`-typed input it hard-crashes inside the .NET regex engine with `System.AccessViolationException`.
- **Root:** the private `nativeReplaceFirst(input: CharSequence, replacement: String, count: Int)` `@ClrIntrinsic("Replace")` 3-arg binding mis-resolves / mis-materializes. (The 2-arg `replace(CharSequence,…)` works: `bXnXnX`.)
- **Fix:** stdlib-side — correct the 3-arg `Replace(string, string, int)` binding / marshaling.
- **Gate:** NOT covered (no case uses `replaceFirst`). Audit ledger `docs/clr-stdlib-actual-index.md` falsely marks it done.
- **Needs a regression case.**

### N2 — [HIGH] `Regex.pattern` — ilemit hard emit-crash
- **File:** `libraries/stdlib/clr/kotlin/text/regex/RegexClr.kt:73-75`
- `re.pattern` aborts emit: `InvalidOperationException: no readable property OR field 'ToString' on System.Text.RegularExpressions.Regex`. The `@ClrIntrinsic("ToString")` on the **property** `pattern` routes as a `clrPropGet` (looks up a property named `ToString`), but `ToString` is a **method**.
- **Fix:** stdlib-side — a rule-3 body `get() = toString()` (the `toString()` method binding already works, returns `a(\d+)b`).
- **Gate:** NOT covered. `docs/clr-stdlib-actual-index.md:1952` falsely marks it "🟢 BOUND".
- **Needs a regression case.**

### N3 — [HIGH] `Task.WhenAll` / `WhenAny` — generic-arg corruption
- **File:** `toolchain/facadegen/Program.cs:1289`
- `Map` short-circuits `if (t.FullName == self.FullName) return KotlinName(self);`. When both `t` and `self` are open constructed generics containing a type param, both `FullName`s are `null` → `null == null` matches → returns the enclosing type's name instead of recursing. Corrupts every double-nested generic arg: `WhenAll<T>`/`WhenAny<T>` `IEnumerable<Task<T>>` params surface as `IEnumerable[IEnumerable]` / `Task1[Task1]`; `Task<T[]>` returns.
- This is the **unfixed twin of Bug ⑤** (already guarded at `Program.cs:643` with `FullName != null`).
- **Fix:** one guard — `if (t.FullName != null && t.FullName == self.FullName)`.
- **Gate:** NOT covered.

### N4 — [MED] Field-read reorder across a suspension (miscompile)
- **File:** `toolchain/bir2cir/SuspendColdLowering.cs:1287-1307` (`IsPureExpr`/`ImpureKinds`), site at `:1246`
- A raw member field read `{k:"field"}` is classed **pure**, so `this.x + suspendCallThatMutatesThisX()` leaves `this.x` inline and evaluates it AFTER the suspend resumes → reads the post-mutation value. Codex-confirmed miscompile.
- **Narrow:** a source-level property read goes through the getter (`callInstance` → impure → correctly spilled); only a direct backing-field / `@ClrField` read left of a mutating suspend call is affected.
- **Fix:** bir2cir — spill raw field reads in the eval-order pass.
- **Gate:** NOT covered.

### N5 — [MED] Same-name overload collision in interop-no-registry (latent regression)
- **File:** `toolchain/kotc/src/main/kotlin/kotc/frontend/ClrTypeInjection.kt:348-353` (top-level), `:328-336` (member)
- The registry replacement keys on `CallableId = (package, name)` only. Two restored top-level overloads with the same name in the same package but different source files (`foo()` in `UtilsKt`, `foo(Int)` in `HelpersKt`) collide → **last-put-wins**. This is a slight **regression** vs the deleted receiver-discriminator (the old `list.first()` arbitrary pick became an arbitrary last-wins).
- Surfaces as a hard ilemit "method not found" on the mis-routed file class (not silent wrong runtime).
- **Fix:** kotc — key the map by an overload-aware signature (the metadata `tlfun` params are available).
- **Gate:** NOT covered (no referenced DotKt lib with such overloads).

### N6 — [MED] Static events & interface events not surfaced (completeness)
- **File:** `toolchain/facadegen/Program.cs:522` (`GetEvents(Public|Instance)` inside `if (!isStatic)`) and the interface branch `:332-408` (no `GetEvents` loop)
- `System.Console.CancelKeyPress`, `TaskScheduler.UnobservedTaskException` (static) and `INotifyPropertyChanged.PropertyChanged` (interface) are absent from the meta → `x.PropertyChanged += h` on an interface-typed receiver won't resolve.
- **Downstream is already built for it** (bir2cir `ClrEventOperatorBinding` reads `isStatic`; ilemit `EmitClrEvent` handles static). Missing pieces: (a) facadegen emit a static-event / interface-event line; (b) kotc surface a static event as a **companion** `ClrEvent<T>` property (`ClrTypeInjection.kt:613` currently only makes a member property).
- **Gate:** NOT covered.

### N7 — [MED, KNOWN ④] Nested collection/map stringification renders raw .NET type names
- `mapOf("k" to listOf(1,2))` → `{k=System.Collections.Generic.List`1[System.Int32]}`; `listOf(listOf(1,2))` → raw. Only the top-level operand is routed to `clrCollToString`/`clrMapToString` at the static-type level; a nested collection hits runtime `.NET Object.ToString()`.
- Tracked in `CHANGELOG.md:903` + `bundle6-remaining-bugs.md` ④; still OPEN. Not gate-covered (`il-collmore` only uses `flatten()`).

### N8 — [LOW] Assorted
- **kotc:** a `ClrEvent<T>` read outside `+=`/`-=` (`val e = w.Changed`) compiles but emits a `clrPropGet` naming a non-existent `get_<Event>` that no bir2cir rule strips → downstream failure, no kotc diagnostic. (`BirEmitter.kt:3582`)
- **kotc:** `@Volatile` matches only `kotlin.concurrent.Volatile`; the deprecated `kotlin.jvm.Volatile` is silently ignored. (`BirEmitter.kt:948-952`)
- **ilemit:** `EmitClrPropGet/Set` emit `IsVirtual ? Callvirt : Call` directly on a value-type receiver address instead of routing through `EmitInstanceCall`; a virtual value-type property accessor would emit `CallVirtOnValueType`. Not triggered today. (`Program.cs:3298/3335/3357/3372`)

---

## KNOWN-DEFERRED (tracked in `docs/bundle6-remaining-bugs.md`)

### F2 — `suspendCoroutine` does not lower E2E
- A sync-resume `suspendCoroutine { it.resume(42) }` crashes at ilemit: `NotSupportedException: suspend method reached codegen un-lowered — bir2cir transform MISS` (bir2cir reports `0 await`). Direct suspend-calls-suspend lowers fine; the miss is specific to the `suspendCoroutineUninterceptedOrReturn` path.
- **`docs/design-coroutine-cold-core-task-bridge.md:436` is STALE** — it claims "Plain suspendCoroutine works". `bundle6-remaining-bugs.md` F2 correctly lists it OPEN.

### F1 — SafeContinuation UNDECIDED/RESUMED boxed-enum identity (latent time-bomb)
- **File:** `libraries/stdlib/clr/kotlin/coroutines/SafeContinuationClr.kt:33/37/38/46/52`
- CLR enums are value types with **unstable identity when boxed into `Any?`** (proven: `E.A === E.A` via two `Any` locals → False). `COROUTINE_SUSPENDED` was fixed by caching the box (`Intrinsics.kt:61`), but `UNDECIDED`/`RESUMED` are still accessed **uncached** → `cur === UNDECIDED` misfires (a sync resume falls to `else → throw IllegalStateException("Already resumed")`).
- **Dormant** because F2 blocks `suspendCoroutine`; **fires the moment F2 lands.** Fix F1 and F2 together.
- **Fix:** stdlib-side — cache the two boxes (mirror `COROUTINE_SUSPENDED_BOX`) OR use sentinel-left structural `==`.

### Layer-purity debt (§0)
- **kotc `BirEmitter.kt`:** exception-type map (`require`→ArgumentException etc., `:3933-3959`, known-deferred per `exception-map-to-clrtypealias`); **annotation base `clr:System.Attribute` (`:1076`) — contradicts the 2026-07-02 decision to emit a flag + let bir2cir derive the base (memory `annotation-base-lowering-to-bir2cir`, still unimplemented)**; KClass `simpleName`/`qualifiedName`→`System.Type` (`:3225-3233`); primitive/Comparable `compareTo` (`:3191-3216`, borderline primitive-IL).
- **ilemit:** ReverseBridge hardcoded `kotlin.collections.{Iterator,Set,MutableSet,MutableCollection,MutableList,MutableIterable}` (`Emitter.ReverseBridge.cs:24,107-108` — largest remaining leak). Debt to push to bir2cir.

#### §0-D1 — [ESCALATED, user-flagged repeatedly] kotc lowers Unit→`void` in return position (root of the `kotlin.Unit` leak)
This was under-rated as a LOW ilemit note; the **root is in kotc** and it is a genuine §0 violation. kotc emits the literal `"void"` for Unit-returning functions/accessors/suspend-result across **~10 sites**, all of the form `if (fn.returnType.isUnit()) "void" else birType(...)`: `BirEmitter.kt:618, 639, 1029/1045/1566, 1407, 1894, 1914, 1975, 2063-2064, 2087-2088` (+ `func:void:` at `:298`, try `"void"` at `:1777`, `const void null` at `:2139`). "Unit → void" is a **CLR-resolution decision** (where/what Unit maps to) that §0 assigns to bir2cir — kotc should emit `kotlin.Unit` and let bir2cir lower it.
- **Smoking gun — kotc is internally inconsistent:** in TYPE-ARG position it already emits `kotlin.Unit` correctly (`:1528`, with a comment at `:1519-1520`: "a `Unit` TYPE-ARG can't be `void` — `Continuation<Unit>` must be `Continuation[@kotlin.Unit]`, not `Continuation[void]`"). So the layer *knows* Unit≠void; it just fails to apply that discipline to return position. The ilemit `"unit"`/`"kotlin.Unit"` check (`Emitter.Expressions.cs:57`) is the downstream duplicate of the same decision — Unit→void is currently split across kotc **and** ilemit.
- **Correct design (no new invention — mirrors the established primitive dual-representation):** Unit is the "dual-representation of void". kotc emits `kotlin.Unit` in ALL positions; bir2cir lowers `kotlin.Unit`→`void` in return/void context and keeps it an object in type-arg context. Exactly parallel to `kotlin.Int`→`System.Int32` (bare value) vs `kotlin.Int` (type-arg). See `primitive-dual-representation`.
- **Why it persists:** the split "works" (gate green) and the fix touches ~10 kotc sites + a bir2cir Unit-return→void pass, so it keeps being deferred despite repeated user requests. Escalate: fold it into the same bir2cir lowering that already owns primitive dual-representation.

---

## NON-BUG QUALITY

### Failure posture — net improved
Three former silent paths now `throw`: unresolvable-`suspendCoroutine` hang (`SuspendColdLowering.cs:1426`), arbitrary-overload `cands[0]` (`bir2cir/Program.cs:566/588/710`), `@Clr*` overload ambiguity. **BUT** the foundational silent path persists: the ref.dll subst-scan swallows a `MetadataLoadContext` load failure into `Diagnostics` which never reaches stderr/exit (`bir2cir/Program.cs:1118-1121,1247-1256`) — and now underpins more machinery (suspend flags, Task aliases). A silent ref-scan miss becomes a distant EntryPointNotFound/NRE with no "ref scan failed" signal. **Recommend surfacing `metadata.Diagnostics`.**

### Gate hygiene
- FIXED: `[chunk]` 4-duplicate-key bug gone; `collops2` XFAIL gone (now normal green); `il-coldvirt` wired.
- Residual: stale cobuild comment referencing deleted `XFAIL_RUN[cobuild]` (`verify-il.sh:452`); `comaindrain` invoked twice (`:459`/`:464`); **41 run-cases have NO ilverify formal coverage** (incl. cofinally/coldabstract/ifacesuspend/cbk…) — a latent unverifiable-IL could pass silently; differential gate has a latent `empty==empty` false-MATCH hole (both-compile-fail → false MATCH; `verify-differential.sh` `|| true` with no exit guard); `verify-roundtrip` still carries ONE legitimate XFAIL `roundtrip-memext2` (suspend call inside a `with{}` sub-expression → needs scope-function CPS lowering; bir2cir/ilemit item) — so "zero XFAIL" is verify-il-only.

### Doc-vs-reality gaps (these hid bugs)
- `docs/clr-stdlib-actual-index.md` falsely marks `Regex.pattern` "🟢 BOUND" (N2) and `replaceFirst` done (N1).
- `docs/design-coroutine-cold-core-task-bridge.md:436` falsely claims `suspendCoroutine` works (F2).

---

## RECOMMENDED PRIORITY

1. **N1 `replaceFirst` (memory corruption / AccessViolation) — first.** Then **N2 `pattern`** and **N3 `WhenAll/WhenAny`** (all new, un-gated, 1–few-line fixes).
2. **Add regression cases for N1/N2/N3** so the gate's coverage reaches these corners (widen the narrow gate).
3. **Correct the audit ledger + design doc** (N1/N2 "BOUND", F2 "works") — stale docs were actively hiding these.
4. MED group: N4 (field-read reorder), N5 (CallableId collision), N6 (static/interface events), N7 (nested stringify).
5. F1/F2 together when the coroutine `suspendCoroutine` lowering lands.
6. Non-bug: surface `metadata.Diagnostics` (fail-loud), close the differential `empty==empty` hole, add ilverify coverage to the 41 uncovered run-cases.

**One-line summary:** the tested surface is fully green and real, but three un-gated crashes remain (one a memory corruption), the project's own worklist admits open items, and stale "done" docs were masking them.

---
## Follow-ups surfaced while fixing (2026-07-05)
- **N3-deep (from N3 fix):** `Task.WhenAll(IEnumerable<Task<T>>)` surface is now correct + compiles, but RUNTIME
  crashes "method is not fully instantiated" / ilverify StackUnexpected — the kotc/bir2cir generic-static-companion
  builder can't infer+bind `T` from a deeply-nested arg (`IEnumerable<Task<T>>`). Routed to kotc/bir2cir
  (nested-inferred type-param instantiation). il-taskwhen executes WhenAny only; WhenAll surface verified via meta dump.

# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

- **Gate hardening (pre-coroutine batch C1-C3): machine-readable XFAIL baselines + abort-proof harnesses.**
  *C1 (verify-il)* — the known-fail baseline moved from prose/flat name lists to `XFAIL_RUN` / `XFAIL_ILVERIFY`
  associative arrays (fail name → reason) diffed by the new shared `lib.sh xfail_diff`: exit 0 iff every actual
  fail is XFAIL-listed; any other name prints `NEW-FAIL` and exits 1; an XFAIL entry that starts passing prints
  `FIXED — remove it from the xfail list` without reddening the gate. CLAUDE.md's gate paragraph now points at
  the mechanism instead of prose numbers. RECORDED (not fixed): `bymap` regressed with the stdlib subtree bump
  (cde8afd) — the rt `clrMapGet` throws `EntryPointNotFoundException` on `IDictionary.ContainsKey`; XFAILed with
  an explicit REGRESSION reason, owned by the Map/MutableMap dual-rep sub-track.
  *C2 (verify-roundtrip)* — the gate used to die silently mid-script (SIGABRT 134 inside a `$(...)` under
  `set -e`) at the FIRST suspend-stub crash, so the 5 sections after it never ran and piping through `tail`
  masked the exit to 0. Now every section runs to completion (crash-safe captures via the `if var="$(cmd)"`
  errexit-exempt pattern; every pipeline step tolerates failure so it surfaces as its section's verdict), the 3
  suspend-consuming sections (`roundtrip`, `roundtrip-generic`, `roundtrip-memext2`) are `RT_XFAIL`-listed
  ("coroutine lowering deferred (bundle 6)"), and the final summary prints per-section PASS/FAIL/XFAIL with
  exit 0 iff no unexpected outcome. This script is the coroutine bundle's E2E gate: the suspend sections
  flipping to PASS surface as "FIXED — remove it from the RT_XFAIL baseline" lines.
  *C3a (verify-wide-delegates)* — the hand-written 17-arg `.bir.json` fixture, fed STRAIGHT to ilemit
  (bypassing kotc + bir2cir — a single-path violation whose hand-maintained expr vocabulary rotted twice), is
  DELETED. The gate now drives a real Kotlin source (`cases/il-widedeleg/wide.kt`: 17-arg function values +
  a wide-typed parameter) through the canonical kotc → bir2cir → ilemit pipeline and keeps all three
  assertions: run output, KFunc`18/KAction`17 synthesis in the dll, facadegen restoring the wide Kotlin
  function type (`rg` → `grep` for CI portability).
  *C3 (verify-differential)* — same `XFAIL_DIFF` mechanism: the 2 coroutine DIFFs (`il-seq`/`il-collops2`,
  mirroring verify-il's run-XFAILs) plus 3 RECORDED regressions from the 2026-07-02 stdlib subtree bump
  (cde8afd): `m-b6` (ilemit aborts on the rt's Double-specialized `maxOrNull` — "not a
  GenericMethodDefinition"), `m-b9` (`sumOf {}` returns 0 on CLR), `m-b10` (`groupBy` → `clrMapGet`
  `EntryPointNotFoundException`, the same Map dual-rep family as verify-il's `bymap`). These 4 stdlib-bump
  regressions (incl. bymap) are stdlib-side work, NOT gate bugs — the XFAIL entries carry the full symptom
  so the owning track can pick them up.
- **`scripts/` overhaul: one naming scheme + shared internal conventions + two harness bug fixes.**
  *Naming* — normalized to `<verb>-<noun>[-qualifier].sh`, aligned with the make target names (targets unchanged):
  `build-clr-stdlib.sh`→`build-stdlib-ref.sh`, `build-clr-stdlib-runtime.sh`→`build-stdlib-rt.sh`,
  `build-clr-stdlib-frontend.sh`→`build-stdlib-jar.sh`, `pack-dotkt.sh`→`pack-nuget.sh`,
  `verify-ilemit-wide-delegates.sh`→`verify-wide-delegates.sh`, `gen-clr-stdlib-actual-index.py`→
  `gen-stdlib-actual-index.py`; `run-clr-sample.sh` DELETED (pre-dated and duplicated `dotkt.sh`/`make dev`).
  All live references updated; `docs/archive/**` and released CHANGELOG entries intentionally keep the old names.
  *Conventions* — new `scripts/lib.sh` sourced by every script: strict mode (`set -euo pipefail`; tolerated
  failures are explicit `|| true`), `ROOT`, the tool/artifact paths as a single source, `info`/`warn`/`die`,
  a `usage()`/`-h` convention, lazy `need_*()` builders vs the UNCONDITIONAL `build_tool` the verify gates use.
  *Bug fix 1 (the rt grep-exit-1 footgun)* — `build-stdlib-rt.sh` ended with an error-grep that exited 1 exactly
  when the build was CLEAN; both stdlib build scripts now exit 0 on success / nonzero on real failure, and the
  compensating `|| true` in the Makefile and pack-nuget.sh is gone.
  *Bug fix 2 (the verify-il dropped-FAIL-line / stdout race)* — a crashing sample died before printing its FAIL
  line (`set -e` killed the parallel subshell) and concurrent output interleaved → false-pass headlines. Every
  sample now writes ONE atomic result record (`build/verify-il/run-<name>`, guaranteed by an EXIT trap),
  aggregated after `wait`. The 4 coroutine-deferred crashers (`chunk`/`cobuild`/`collops2`/`seq`) that used to
  drop their lines now PRINT as FAILs; the script encodes the known-fail baseline (`KNOWN_RUN_FAIL`/
  `KNOWN_ILVERIFY_FAIL`) and exits 0 iff there is no NEW fail name — green is machine-checked. Truthful baseline:
  **PASS(run) 132 / run-FAIL = exactly those 4 / 6 known ilverify-formal-only names**.
  Also un-broke `verify-wide-delegates.sh` (pre-existing: its hand-written BIR fixture still used the retired
  `k:"console"` expr; now the current `clrStatic` form — the gate passes again).
- **kotc: implicit companion access for injected .NET statics — `.Companion` no longer required.**
  `Application.Start(...)` / `App.Count` now resolve directly; previously only `App.Companion.Start(...)` worked
  (the old form stays supported — both forms emit byte-identical BIR). Root cause was a wiring gap, not a K2 limit:
  stock FIR only links `companionObjectSymbol` for source/deserialized classes (`FirCompanionGenerationProcessor`
  walks FirFiles only), so a fully-generated owner never got the link the implicit-qualifier path consults
  (`typeForQualifierByDeclaration` → `canBeValue`). Fix: `ClrTypeInjector` eagerly creates + links the companion for
  injected classes with statics and sets the FIR-internal `ownerGenerator` attribute via a bytecode-public Java shim
  (`kotc/frontend/FirInternals.java`) — required because the eager link makes the framework's only nested assignment
  site unreachable (`FirGeneratedScopes.kt:245-255`) and generated-origin member lookup dies on `ownerGenerator!!`
  (`:290`). `il-injstatic` now exercises both forms.
- **docs: overhaul** — 7 superseded docs archived to `docs/archive/` (HISTORICAL headers); `dotkt-semantics.md` gains a TOC + suspend-hot/Appendable/enum/value-class/.Companion sections; new user-facing set `docs/user/` (getting-started / using-dotnet-from-kotlin / kotlin-on-clr-differences / supported-features) + `docs/README.md` index; `README.md` refreshed to the single-path 4-layer reality.
### Added
- **Unified build interface (`Makefile`)** — a thin orchestrator over the canonical scripts, with incremental
  file targets for the whole artifact DAG (kotc → the 4 .NET tools → stdlib jar/ref/rt → pack). `make help`
  self-documents; key targets: `all` (toolchain → stdlib → pack), `toolchain`, `stdlib{,-jar,-ref,-rt}`, `pack`,
  `verify{,-il,-ktproj,-roundtrip,-differential,-widedelegates}` (the gate scripts are called verbatim),
  `dev SRC=… [RUN=1 …]` (wraps `dotkt.sh`), `facades`, `clean{,-tools,-stdlib,-pack}`. `make -j` builds the
  independent tools in parallel. The load-bearing output paths (`build/<tool>-bin`, `build/clr-stdlib*/dll`,
  `build/clr-stdlib-frontend-jvm`) are unchanged.
- **4-package NuGet structure + the stdlib packaging gap fixed** — `pack-dotkt.sh` shipped NO stdlib dlls while
  the shipped `DotKt.Toolchain.targets` needs both; the packed SDK could not actually compile. Now exactly four
  packages: **DotKt.Sdk** (MSBuild SDK; implicit refs to Toolchain + Stdlib), **DotKt.Toolchain** (kotc + bir2cir +
  ilemit + facadegen + retarget + `kotlin-stdlib-clr-frontend.jar` + the COMPILE-TIME reference stdlib
  `tools/stdlib/DotKt.Private.Stdlib.dll`, exposed as `$(DotKtStdlibRefAsm)` → a non-copy `<Reference>` in
  Sdk.props), **DotKt.Stdlib** (NEW: the RUNTIME stdlib `lib/net10.0/DotKt.Stdlib.dll`, copy-local via the SDK's
  implicit PackageReference; opt out with `<KotlinClrStdlibRef>false</KotlinClrStdlibRef>`), **DotKt.Templates**
  (unchanged). `DotKt.Runtime` stays retired — nothing creates or references it. Verified end-to-end: a fresh
  `.ktproj` consumer restored from the local feed alone builds and runs stdlib calls, with `DotKt.Stdlib.dll`
  copy-local and the reference face absent from output. Package version is single-sourced from
  `packaging/DotKt.Versions.props` (both `*.pack.csproj` now import it; the dead `VER` in `pack-dotkt.sh` is gone).

### Removed
- **`scripts/run-m0.sh`** — drove the retired C# backend (`kotc` → C# → dotnet); the IL pipeline gates
  (`verify-il.sh` / `make verify-il`) are the canonical entry.

### Fixed
- **Injected .NET static-companion members (`il-injstatic`)** — `App.Companion.start(cb)` on a facadegen-injected
  C# host type crashed with `unresolved method: <>dotkt_ClrH_Kfc_App.start`: kotc's Rule-3 hoist classifier
  ("no interop marker + concrete → its body was hoisted to the `<>dotkt_ClrH_<owner>` static helper") misfired on
  the synthesized companion's static method — an injected member naturally has no marker (it isn't a stdlib
  binding), and no helper exists for an external .NET type (the hoist is only for @Clr classes with hoisted Kotlin
  bodies). The hoist is now gated on the injected `ClrTypeRegistry`: an owner (or a companion's host class)
  registered there routes to the direct .NET member shapes (`clrStatic Kfc.App::start` etc.), never the hoist —
  generalizing (and subsuming) the narrower `ClrEventRegistry` gate from the event-accessor fix `32a1da6`. This was
  the last run-FAIL: verify-il PASS(run) 131 → 132, fail-names 7 → 6 (all remaining are the documented ilverify
  set: chunk/collops2/collrealkt/gen3/iter/iterable), verify-ktproj 9/9.
- **User `Comparable<T>` sorting (`il-comparable`)** — `listOf(v1,v2,v3).sorted()` over a user `class Ver :
  Comparable<Ver>` crashed silently (rt `sorted[T]` invoked an OPEN-generic `Array.Sort[T]` → "not fully
  instantiated"). Three coordinated fixes: (1) **bir2cir** no longer lets the name-only top-level `@ClrIntrinsic`
  fallback capture a call that has a REAL-BODIED (non-intrinsic) top-level sibling — `sort`'s 8 primitive-array
  intrinsics all bind "System.Array.Sort" (so the name wasn't "ambiguous"), yet `MutableList<T>.sort()` is a real
  Kotlin body; such names now substitute only on a sig-exact intrinsic match. (2) **bir2cir** new
  `ComparableBridgeSynthesis` pass: every emitted class implementing the generic `System.IComparable<X>` also gets
  the NON-generic `System.IComparable` + a `CompareTo(object)` forward bridge — the BCL convention the CLR-side
  natural-ordering dispatch (compareValues / the sortWith SAM shim's constrained fallback) depends on.
  (3) **ilemit** `clr:`/`clrg:` interface-slot wiring now disambiguates same-name body OVERLOADS by the slot's
  substituted param types instead of the name-keyed pick (which mis-wired the new CompareTo pair → TypeLoad).
- **`kotlin.Result` / `runCatching` (`il-result`)** — crashed silently (InvalidProgram inside the rt's
  `runCatching[R]`). Four coordinated fixes around a GENERIC class's companion statics: (1) **ilemit** anchors a
  static method of a generic emitted class (`Result<T>`'s companion `fun <T> success`) onto an `object`-instantiated
  owner (`TypeBuilder.GetMethod`) — the previous open-typedef parent token is invalid IL at a foreign call site;
  (2) **kotc** companion-member `callStatic` now carries the call's type args (`typeArgsJson`) so the anchored
  method is `MakeGenericMethod`'d; (3) **kotc** `ownerSpec` renders a STAR-projection type arg as `object` instead
  of dropping it (a dropped star collapsed `Result<*>.throwOnFailure`'s receiver owner to the bare open generic);
  (4) **ilemit** `new` ctor args now BOX to the ctor's declared `argTypes` (a bare `!!T` flowed unboxed into
  `Result(object)` — InvalidProgram at value instantiations).
- **Map delegation `val name by data` (`il-bymap`)** — three coordinated fixes across the layers it crosses:
  (1) **kotc** routes a delegated property whose convention resolved to a TOP-LEVEL extension (the stdlib
  `kotlin.collections.getValue/setValue`, MapAccessors.kt) by re-emitting the accessor body's RESOLVED call at the
  access site as the plain owner-null static call (receiver-first args + declared sig + typeArgs) — previously this
  fell to "unsupported delegated property". (2) **ilemit** canonicalizes `<>dotkt_KProperty`/`<>dotkt_KPropertyImpl`
  (added to `CanonicalSynthetics`): the synthetic is MONOMORPHIC (one get_name/ctor(string) shape everywhere, unlike
  KIterator_*), and a per-assembly copy made the rt's `MapAccessorsKt.getValue(map, thisRef, KProperty)` fail
  `EntryPointNotFound` on `get_name` when handed the APP's KPropertyImpl — apps now reference the rt dll's single
  copy (self-correcting: a --no-stdlib build still emits it locally). (3) **stdlib** `MapAccessors.kt` pins
  `getOrImplicitDefault`'s K to String via `(this as Map<String, V>)`: on the projected receiver `Map<in String, V>`
  the frontend approximates the captured K to Any — fine under JVM erasure, but reified CLR generics then dispatch
  `IDictionary<object,V>.ContainsKey` on a `Dictionary<string,V>` → EntryPointNotFound (a variance JVM-ism, discarded).
- **Generic method on a generic class, called with a CONCRETE owner instantiation (`il-generic4`)** — `Holder<int>.pairWith<string>()`
  threw `InvalidOperationException: … not fully instantiated` at runtime: ilemit's `ApplyTypeArgs` replaced the
  `TypeBuilder.GetMethod`-anchored member with the OPEN method's instantiation (`Holder`1::pairWith<string>`), losing the
  container's `<int>`. Fix (ilemit): when the constructed owner carries NO generic-parameter args, keep the anchored
  `MethodOnTypeBuilderInstantiation` and `MakeGenericMethod` it directly (the documented GetMethod→MakeGenericMethod
  order; verified supported on .NET 10 persisted emit). The erased-context path (owner constructed with enclosing
  generic params — the rt-stdlib self-instantiation case that broke a previous naive fix) is gated out unchanged.
- **Unsigned division/remainder/`toString(radix)` (bundle 【2】b-A)** — the 6 `UnsignedClr.kt` TODO stubs
  (`uintDivide`/`uintRemainder`/`ulongDivide`/`ulongRemainder`/`uintToString(base)`/`ulongToString(base)`) now have
  **real pure-Kotlin bodies** (JVM-actual ports; ULong via the Guava UnsignedLongs algorithm; radix `toString` via a
  self-contained digit loop — NOT `Long.toString(radix)`, whose call sites still lower to `Convert.ToString`,
  bases 2/8/10/16 only). **Zero compiler change**: direct `a / b` on UInt/ULong was already frontend-lowered to a raw
  `bin /` whose unsigned CLR operand type selects `div.un`/`rem.un` in ilemit (no BCL bind exists — `op_Division` on
  `UInt32`/`UInt64` is an explicit-interface generic-math impl, not a callable static). Fixes
  `UInt/ULong.toString(radix)` (previously threw `NotImplementedException`); verified incl. `2^63.toString(7)`,
  `ULong.MAX_VALUE.toString(10/16/36)`, unsigned div/rem edges (`2^63` divisor, `MAX/MAX`); `il-unsigned` unchanged.
- **Enum reflection `enumValues<T>()`/`enumValueOf<T>(name)`/`enumEntries<T>()`/`enumEntriesIntrinsic<T>()`
  (bundle 【2】b-B)** — kotc lowers the top-level reified intrinsics at the CALL SITE like `T.values()`/`T.valueOf()`
  (`ENUM_REIFIED_INTRINSICS`): a **rich** enum type arg → the synthesized static `values()`/`valueOf()`; a **basic**
  enum / generic-param type arg → the semantic `enumValues`/`enumParse` BIR nodes (`System.Enum.GetValues/Parse` in
  ilemit; an unknown name surfaces as `ArgumentException`, the CLR face of `IllegalArgumentException`). Previously
  every such call threw (`VerificationException`: the cross-module generic call's `T : kotlin.Enum<T>` constraint is
  unsatisfiable for a basic enum, which derives `System.Enum`). The entries family is not intercepted under
  `stdlibCompile` (the rt `enumEntries<T>` body would return `T[]` where `EnumEntries<T>` is declared — invalid IL).
  KNOWN GAPS (documented in the stubs): a RICH enum through a **non-inlined generic** context is invisible to
  `System.Enum` reflection; user-defined `inline fun <reified T : Enum<T>>` helpers still hit the pre-existing
  `kotlin.Enum<T>`-constraint emission issue (orthogonal — any Enum-bounded generic call, not enum reflection).
  Gates kept green: `il-enum`/`il-enumbody`/`il-enumrich`.
- **Generic `Array<T>` ops bound with real stdlib bodies (bundle 【2】a): `copyOf(newSize)`, `copyOfRange`,
  `plus(element)`, `plus(Array)`, `plus(Collection)`, `plusElement`, `orEmpty()`, `arrayOfNulls(reference, size)`**
  — all pure Kotlin in `runtime/stdlib/clr` (allocate via `arrayOfNulls<T>(n)` → generic `newarr !T`, reified-on-CLR;
  `TYPE_PARAMETER_AS_REIFIED` suppressed deliberately) mirroring the primitive-array siblings; **zero new
  `@ClrIntrinsic`/compiler special-casing**. Three compiler *wrong-code* fixes were required to make them behave:
  - ilemit `arraySet`/`clr.stelem`: don't `box` a value stored into a GENERIC-PARAM-element array (`stelem !T` with a
    boxed ref corrupts value-type instantiations — printed pointer bits); same guard as the local/field/coroutine box
    sites.
  - ilemit `FindReflectedMethodBySig`: STRUCTURAL matching for sig tokens `MapType` can't resolve at a cross-module
    call site (`gp:T`/`array:gp:T`/`clrg:X[gp:T]`), so `copyOf(array:gp:T,int)` selects the generic `copyOf<T>(T[],int)`
    over same-arity concrete siblings (previously: arity-pick chose `copyOf(sbyte[],int)` → short/sbyte reinterpretation
    garbage) and the three generic `plus` overloads stay distinguishable.
  - kotc BINARY operator lowering gated on "callee has NO extension receiver": `Array<Int> + 4` / `Array<String> + "d"`
    were lowered to a raw CIL `add` on the array REFERENCE (garbage/crash). Primitive operators are members and the IR
    compare intrinsics are top-level with plain params, so both still lower; stdlib `plus`/`minus` EXTENSIONS now emit
    real calls.
  - KNOWN GAP (pre-existing, unchanged): element reads of an `Array<Int?>` (e.g. the result of `Array<Int>.copyOf(n)`)
    emit `ldelem Nullable<int32>` against a runtime `int[]` — the nullable-primitive-array dual-representation is
    unresolved; reference-type `T` is fully correct. `enumValues`/`enumValueOf` skipped (need reified-enum lowering /
    typeArgs on `clrStatic` — compiler-side, follow-up).
- **facadegen interop bundle 【3】b closed — alias imports, op_* battery, C# extensions, dual-rep rule, I4 remnants
  (all verification + rule-setting; no compiler changes needed).**
  - **(5) aliased import**: `import System.Text.StringBuilder as SB` works end-to-end (the PSI import scan already
    canonicalizes the alias; Kotlin's import machinery binds it) — new gate `cases/il-alias`. A no-match .NET import
    warns in facadegen and errors at the frontend (nothing silent).
  - **`op_*` operators / C#-origin `[Extension]` methods**: full battery verified on a C# struct
    (`+ - * / unary-` + int/string extension receivers) — `cases/il-c1net` extended. `op_Equality`/`op_Inequality`
    deliberately unmapped (Kotlin `==` → `Equals(Any?)`); `op_Implicit`/`op_Explicit` skipped (no Kotlin analog).
  - **Dual-representation rule (DECIDED)**: an imported BCL type (`System.Text.StringBuilder`) and its stdlib alias
    (`kotlin.text.StringBuilder`) are TWO TYPED VIEWS of one CLR type — coexist, never unified; mixing is a clear
    frontend type error; explicit cast is the escape hatch. `docs/dotkt-semantics.md` §8b; gate `cases/il-dualrep`.
  - **I4 remnants assessed, all working**: .NET enum import (read/pass/`==`/`when`), generic delegates
    (`Func<int,int>` + custom `Mapper<T>`), nullable value types (`int?` both directions), `out`/`ref` (il-outref) —
    new gate `cases/il-netinterop` locks enum+delegate+nullable.
- **Collection/sequence + language-feature 4-bug batch: `il-sort`/`il-collmore`/`il-regex`/`il-langf` all green
  (run-correct AND ilverify-clean); verify-il fail-names 18 → 9, PASS(run) 121 → 124, ktproj 9/9.**
  - `sorted`/`sortedDescending`/`sortedBy`: three JVM erasure-isms fixed stdlib-side —
    `naturalOrder()`/`reverseOrder()` singleton-cast (now genuinely generic comparator classes), `sortedWith`'s
    `toTypedArray as Array<T>` fast-path (now the `toMutableList` branch), and `compareValues`' `as
    Comparable<Any>` cast (now dispatched through the NON-generic `System.IComparable` via the internal
    `ClrRawComparable` binding; ilemit's `cast` boxes a value/generic source before `castclass`).
  - RC2 transform side: a `(T) -> R?` function slot preserves its return nullability
    (kotc `func:nullable:gp:R:...`) and bir2cir's new `NullableFuncReturnErasure` lowers every nullable-marked
    func return to `Func<…, object>` uniformly (backing lambda rets erased + local dataflow repaired), fixing
    the delegate-reinterpretation crashes (`mapNotNull` InvalidProgram, `sortedBy` AccessViolation).
  - kotc inline-splice type-arg substitution re-keyed by `IrTypeParameter` SYMBOL (a name-keyed map erased a
    caller's same-named generic to `object` and cross-captured outer params: `mapNotNullTo`→`forEach`,
    `let<T,R:=Unit>`).
  - `MutableCollection.add`/`addAll` calls route to new `clrCollAdd`/`clrCollAddAll` stdlib defaults
    (`ICollection<T>.Add` is void vs Kotlin's changed-Boolean; `addAll` has no BCL slot).
  - Rule-3 helper calls carry their receiver-first `sig` so the String→CharSequence bridge wraps raw-string
    args (`Regex.matches`/`find` ilverify StackUnexpected).
  - kotc no longer emits class-inherited fake-override property accessors as empty-bodied methods (ilverify
    ReturnMissing on every derived class of a property-carrying base) — also greened
    `netbase`/`netbase2`/`netgen2`/`customexc`/`mc1`; abstract interface-only fake-overrides are kept (CLR
    re-declaration requirement). ilemit base-chain resolution handles the inner-generic `base[gp:E]` encoding
    (`BareTypeKey`) and probes interface tokens best-effort.

### Added
- **facadegen interop gaps (3)+(6) closed and gate-covered: constructed-generic member types + transitive
  injection.** Verified end-to-end and hardened: a .NET member typed as a constructed generic
  (`IList<Widget>`, `IReadOnlyList<Widget>`, `Dictionary<String,Widget>`, `IEnumerable<String>`) resolves as the
  real generic type (not `Any?`), and types appearing only in member signatures (never imported) are injected
  transitively by the facadegen reachable-closure BFS — full closure with a 5000-type cap, NOT depth-limited, so
  a 2-hop chain (`w.Make(): Gadget` → `g.Core(): Sprocket`) works with zero extra imports. New fix on top: for-in
  over an **interface-typed** receiver (`for (n in panel.Names())` where `Names(): IEnumerable<String>`) — the
  frontend-only `iterator` marker is now emitted on the injected `IEnumerable<T>` interface itself (abstract
  member; derived interfaces `IList<T>`/`ICollection<T>`/`IReadOnlyList<T>` inherit it through the generic super
  chain, one declaration point → no duplicate-member clash with a concrete class's own marker). New gate sample
  `cases/il-transinj`; 15 existing injection samples re-verified green. `docs/dotkt-interop-feedback.md` (3)/(6)
  and `docs/future-work-interop.md` #4 marked RESOLVED.
- **`Map`/`MutableMap` → `IDictionary<K,V>` dual-rep (Track B) — real Kotlin maps run on BCL dictionaries.** BOTH
  interfaces are `@ClrTypeAlias("System.Collections.Generic.IDictionary")` — deliberately NOT the List-style
  read-only/mutable split (IDictionary does not extend IReadOnlyDictionary, so a split breaks `MutableMap : Map`
  verifiability on the hot path; both-IDictionary mirrors Kotlin/JVM's java.util.Map erasure — see
  `docs/dotkt-semantics.md §5c`). Kotlin-semantic members route through the new rt `kotlin.collections.ClrMapDefaults`
  via bir2cir **Rule 5m** (2-type-arg `MapDefaultCall`): null-on-missing `get` (= `ContainsKey` + raw `get_Item`),
  previous-value-returning `put`/`remove`, `putAll`/`getOrDefault`/`isEmpty`/`containsValue`, and the
  `keys`/`values`/`entries` views (pure-Kotlin snapshot Sets; entry values live). `size`/`containsKey`/`clear` and
  `MutableMap.keys`/`values` bind 1:1 (`Count`/`ContainsKey`/`Clear`/`Keys`/`Values`). `il-collrealkt` and `il-mapdes`
  now run correct end-to-end (`mapOf`/`mutableMapOf`/`associate`/`for ((k,v) in m)`); `il-collops2`'s partition/
  associate/withIndex/scan/runningFold/getOrElse lines all pass (blocked only by the separate `windowed` gap).

### Fixed
- **kotc: rich-enum user properties now follow the CLR property model** (`il-enumbody`/`il-enumrich` greened).
  `richEnumDef` emitted a ctor-val property (`enum class Op(val sym: String)`) as a bare public FIELD while the
  general access site emits `callInstance get_<name>` → ilemit crashed `Op.get_sym not found`. The lowering now
  mirrors `typeDef`: internal backing field + real `get_`/`set_` accessor methods + a `properties` entry.
- **frontend jar: `@JvmInline` platform actual** (`il-valclass` greened). `kotlin.jvm.JvmInline` existed only as the
  `@OptionalExpectation` common `expect`, so any app `@JvmInline value class` failed the frontend ("can only be used
  in common module sources"). `build-clr-stdlib-frontend.sh` now stages a `JvmInlineActual.kt` (exactly the existing
  `JvmName` precedent). A `value class` lowers to a real wrapper class — see `docs/dotkt-semantics.md` §10.3.
- **ilemit: arity-changing constructed base-interface member/property resolution.** `PropAccessor` and
  `ResolveInheritedIfaceMethod` only walked SHARED-arity interface chains; `IDictionary<K,V>.Count`/`Clear` live on
  `ICollection<KeyValuePair<K,V>>` (2→1, constructed arg). New `SubstituteIfaceArgs` substitutes the open definition's
  type parameters positionally through the (possibly nested-constructed) base reference — a strict generalization.
- **ilemit: duplicate `(name, params)` method defs no longer merge into one MethodBuilder.** Kotlin overload pairs
  distinguished only by receiver types that COLLAPSE under an alias (`Map.iterator()`/`MutableMap.iterator()` both →
  `IDictionary<K,V>`) had both bodies written into a single builder (concatenated IL → `BadImageFormatException`, one
  body-less method). The second-and-later defs now get deterministic `$dupN` names; the first keeps the clean name.
- **kotc: deleted the legacy `Map.Entry.component1/2` → `KeyValuePair.Key/.Value` lowering** (CLR knowledge in kotc;
  it read the new ref-object entries as a struct → garbage values in `for ((k,v) in map)`). The components now emit as
  plain Kotlin extension calls resolved via the rt stdlib; bir2cir `RecvKey` learned to normalize NESTED ref-type
  names (`kotlin.collections.Map`2+Map$Entry`2` → `kotlin.collections.Map$Entry`) so the attribution matches.
- **bir2cir: `IteratorConsumerNormalization` generalized to rt-returned iterators.** Iterator-typed for-loop vars
  initialized from a `kotlin.*` owner (Set.iterator(), MapsKt.iterator(map)) and `<>dotkt_KIterable_*` synthetic
  consumers with rt receivers (`xs.withIndex()` loops) are re-pointed at the real `kotlin.collections.Iterator[E]` /
  the ClrIteratorBridge. Receiver-gated: app-emitted synthetic producers (il-iter/il-iterable) are untouched.
- **stdlib: `emptyMap()` returns a Dictionary-backed map** — the pure-Kotlin `EmptyMap` singleton cannot satisfy the
  IDictionary surface under the alias (its type fails to load). Read-only-ness stays frontend-enforced.
- **`String.format` as CLR platform API — .NET composite format, bound to `System.String.Format` (fixes `il-fmt` +
  `il-bmore` frontend failures).** Kotlin/JVM's `format` is JVM-only platform API (Native/JS have none); DotKt now
  provides its own: `fun String.Companion.format(format, vararg args)` + `fun String.format(vararg args)` in the CLR
  stdlib (`runtime/stdlib/clr/kotlin/text/StringsClr.kt`), delegating to a private `@ClrIntrinsic("System.String.Format")`
  helper — the format string is the **.NET composite format** (`"{0} items"`, `"{0:D5}"`, `"{0,-4}"`), NOT Java printf
  (`"%d"`), per the host-conventions rule (recorded in `docs/dotkt-semantics.md §5`). No compiler special-case: the
  binding is pure stdlib metadata. One general bir2cir rule landed with it: a **companion `INSTANCE` load on a
  CLR-bound owner** (`String.Companion` as the receiver arg of a companion-extension call) lowers to a null `object`
  const — the substituted BCL type (`System.String`) has no companion singleton and the flattened-companion `__self`
  param is never read. This makes companion-extension bindings (`Double.Companion.fromBits`, `CASE_INSENSITIVE_ORDER`)
  callable from apps in general, not just `format`.
- **`CharSequence` is `System.String` on the CLR — app-own declarations (the 3-point model, points ①/②).** A
  JVM-shaped `kotlin.CharSequence` has no faithful .NET equivalent, so DotKt models it as `string` (an immutable
  snapshot). New bir2cir pass `CharSeqStringLowering` (app build, no user `class S : CharSequence`): a CharSequence-typed
  param/return/local/field → `System.String`; member reads (`length`/`get`/`subSequence`) → `System.String.Length`/
  `get_Chars`/`Substring(a, b-a)`; a non-`String` value (a `StringBuilder`) flowing into a now-`string` slot is snapshot
  with an implicit `.toString()` (a `String` flows directly). Composes with the existing `StringCharSequenceBridge` (a
  now-`string` value into an un-rebuilt stdlib CharSequence-extension is still adapter-wrapped). Sample: `il-charseqs`.
  The synthetic `<>dotkt_CharSequence` is RETAINED for a user `class S : CharSequence` supertype (sealed `System.String`
  can't be subclassed) — an assembly declaring one keeps `CharSequence` polymorphic assembly-wide (`il-charseq`/
  `il-charseqx` unchanged). Snapshot-not-live-view deviation recorded in `docs/dotkt-semantics.md §5b`; design +
  landed/deferred split in `docs/design-charsequence-clr-string.md`. DEFERRED (needs a stdlib rebuild): lowering the
  stdlib's OWN CharSequence-extension signatures to `string` — the change that would retire the 5 still-lowered String
  ops (`trim`/`reversed`/`padStart`/`replace(S,S)`/`isBlank`).

### Fixed
- **`StringBuilder` → `Appendable` dual-rep: `joinToString`/`joinTo` now run (bundle 4-C RC1 blocker (1)).** `Appendable`
  is a JVM-shaped abstraction with no distinct .NET representation — the only CLR appendable char sink is
  `System.Text.StringBuilder` (its sole CLR implementer) — so, mirroring the `CharSequence`→`System.String` collapse,
  it is now `@ClrTypeAlias("System.Text.StringBuilder")` (stdlib). bir2cir lowers every `Appendable` token from the
  ref.dll, so the generic bound `A : Appendable` on `joinTo` becomes the satisfiable `A : System.Text.StringBuilder`
  (was: `VerificationException` "type argument System.Text.StringBuilder violates the constraint of A"). Three supporting
  codegen fixes make the joinTo/appendElement body run: (a) **ilemit** — the name+arity overload FALLBACK could pick a
  BCL overload the arg is NOT assignable to (a `<>dotkt_CharSequence` into `StringBuilder.Append(String)` reinterpreted
  the object as a string → memory corruption "Destination is too short"); it now keeps only overloads whose params
  ACCEPT the resolved arg, preferring the most-specific — a real `String` binds `Append(String)`, a synthetic ref binds
  `Append(object)` (which ToStrings it); (b) **ilemit** — `x is T` / `x as? T` on a value-type / generic-param receiver
  emitted `isinst` on an UNBOXED value → NRE; it now boxes a value-type/gp receiver first (as C# does for `element is X`
  on a generic `T`), exposed by `appendElement`'s `element is CharSequence?`/`element is Char`; (c) **bir2cir** — the
  `<>dotkt_StringCharSequence` adapter gained a `ToString()` override returning its backing string, so
  `Append(object).ToString()` materializes the real content. Greens `il-mapfilter`/`il-coll2`/`il-mutcoll`/`il-arrops`;
  unblocks `il-collrealkt` up to `Map.get` (the separate Map/MutableMap dual-rep track).
- **Cross-module default arguments via a 2-tier rule (bundle 4-C RC1).** kotc emits only the args a caller wrote
  (correct); the frontend jar drops a callee's default VALUES (`IrErrorExpression`), so an OMITTED cross-module default
  is filled by one of two per-parameter mechanisms, chosen by "can the param's own CLR type carry the default as a
  `[DefaultParameterValue]`?": **Tier 1** (a primitive/String/null const on a matching param) → native `[Optional]`+
  `[DefaultParameterValue]` (C#-consumable, unchanged); **Tier 2** (a String const on a `CharSequence`/interface param —
  a string constant can't sit on an interface-typed param — or ANY non-constant default) → the param is emitted REQUIRED
  and its default EXPRESSION is carried as embedded BIR on the new `@kotlin.clr.KotlinDefault(index, bir)` attribute
  (ref.dll-only, mirroring `[KotlinInline]`); bir2cir's `DefaultArgSplice` pass reads it and splices the expression as
  the omitted arg (before the CharSequence bridge + type lowering, so a String default is coerced/lowered exactly like an
  explicit arg — and callee-scope evaluation now handles a param-referencing default). A Tier-2-carrying function stamps
  `@KotlinDefault` on ALL its defaulted params (uniform contiguous splice source). `listOf(1,2,3).joinToString("-")` now
  fills all 7 args and dispatches correctly (the prior stack-underflow / `InvalidProgramException` is gone). NOTE: the
  `joinToString` SAMPLE remains blocked DOWNSTREAM by a separate pre-existing dual-rep bug (`joinTo`'s `A : Appendable`
  constraint unsatisfiable by the BCL-aliased `StringBuilder`), tracked in `docs/master-task-inventory.md §4-C RC1`.
- **Value-type nullable generic return (`T?`) now round-trips as `System.Nullable<T>` (bundle 4-C RC2).** A Kotlin
  `fun <T> …(): T?` has its nullability erased by kotc to a bare `gp:T` return (`Nullable<T>` is inexpressible for an
  unconstrained T), with the null case emitted as `ldnull`. That is correct for a reference T, but for a VALUE T
  `ldnull; ret !!T` collapses to `default(T)=0` — null-ness was LOST: `listOf(10,20).firstOrNull()` returned `0` (not
  `10`), and the result stored into a `Nullable<int>` slot corrupted (`ilverify: found Int32, expected Nullable<int32>`).
  The CLR-faithful representation of a generic `T?` is `System.Object` (the boxed/erased nullable form, which carries a
  real null for a value T): `bir2cir.NullableGenericReturnErasure` (all builds, so ref.dll + rt.dll signatures agree)
  rewrites a `ret=gp:X` + `retNullable=true` method to return `object`; ilemit boxes value/gp returns and, at the call
  boundary (`CoerceReturn`), converts the `object` actual to the caller's `Nullable<V>` (`unbox.any`) or reference type
  (`castclass`). Reference-type nullable returns keep working. Now `listOf(10,20).firstOrNull()`=10,
  `listOf<Int>().firstOrNull()`=null, `lastOrNull` correct. (`mapNotNull`'s transform-side `R?` is a separate,
  kotc-gated case — the delegate-return nullability is not preserved in the BIR func token.)
- **ilemit — a duplicate-emitted reflected overload resolves to the first exact match.** `FindReflectedMethodBySig`
  returned null on a SECOND exact-signature match ("ambiguous"), but two methods matching the same sig token have
  identical parameter types — so a second match can only be a DUPLICATE method emission (the stdlib expect/actual
  fileClass merge emits some top-level fns twice; `_ArraysKt.sum(int[])` carries two distinct method tokens). The null
  dropped to the arity-only fallback, which picked the wrong same-arity overload: `arrayOf(3,1,4,1,5).sum()` bound to
  `sum(sbyte[])` and read the int[] as bytes → `4` instead of `14`. Now keeps the first exact-sig match.
- **ilemit resolves members/fields on referenced generic Kotlin types (bundle 4-C RC3+RC4).** An APP that links the rt
  stdlib via `--ref` and touches a REFERENCED generic Kotlin type absent from this assembly's `_types` crashed at emit.
  **RC3:** a call on an un-substituted generic Kotlin interface owner — `kotlin.collections.Iterator[gp:T]`.hasNext/next
  (the `ClrIteratorBridge` rewrite of `for (x in genericIterable)`) or `kotlin.collections.Map[gp:K,gp:V]`.get — NRE'd at
  `ResolveMethod` because `FindMethod` returned 0 candidates: `ParseOwner` strips the `[gp:..]` args off, leaving the BARE
  open name, but reflection knows a generic interface only by its arity suffix (`Iterator`1`/`Map`2`). `FindMethod`'s
  external branch now probes `typeName`+backtick-N (N=1..8) and takes the unique resolvable open definition;
  `ResolveMethod`'s existing `TypeBuilder.GetMethod` re-anchors it onto the constructed instantiation. **RC4:**
  `kotlin.Pair`.first/.second (a destructuring `component1()`/`component2()` that kotc lowers to a `field` access) hit
  `FindField` KeyNotFound on the external `kotlin.Pair`; once resolved, a direct `Ldfld` of the PRIVATE backing field
  threw `FieldAccessException` cross-assembly (the CLR property model gives every Kotlin property a private backing field
  + public accessors). `FindField`/`ResolveField` gain the same external-type reflection fallback (incl. the arity probe),
  and a new `ExternalPropAccessor` routes an external type's `field` read/write through the public `get_`/`set_<name>`
  accessor (falling back to the field for a public `@ClrField`). Greens `il-genclosure`/`il-genhof`/`il-pair` (verify-il
  run-FAIL 15 → 8) and advances `il-collops2`/`il-collrealkt`/`il-mutcoll` past emit. The three §4-C target samples don't
  fully green yet — each also calls `joinToString` (blocked by the separate rt-baked `StringBuilder`→`Appendable`
  dual-rep) and `collrealkt`/`collops2` additionally hit the Map/MutableMap dual-rep (`mapOf`/`associate` return a BCL
  `Dictionary` that doesn't implement `kotlin.collections.Map`) — separate dual-rep tracks, tracked in
  `docs/master-task-inventory.md §4-C`.

### Changed
- **Retired the clean kotc String-op lowerings (bundle 4-B) — now that CharSequence is canonical.** Building on 4-A,
  the hardcoded `kotlin.text` String lowerings in kotc (`STRING_OPS` + the `BirEmitter` emit sites) are RETIRED for the
  ops whose real stdlib `CharSequence`-extension bodies now run cross-assembly: `contains`, `indexOf`, `startsWith`,
  `endsWith`, `split`, `substring(2-arg)`, `isEmpty`/`isNotEmpty` (joining the earlier uppercase/lowercase/
  substring(1)/NUMBER_PARSE). kotc emits a PLAIN call; bir2cir attributes it to `StringsKt` and the CharSequence bridge
  coerces the `String` receiver/args → the real Kotlin body runs. Two supporting fixes made this work:
  - **ilemit — sig-aware overload resolution on a referenced file-class.** `FindMethod`→`FindReflectedMethod` on an
    EXTERNAL file-class (the rt `StringsKt`) disambiguated only by arity, so a String-face `substring(String,int,int)`
    vs a CharSequence-face `substring(<>dotkt_CharSequence,int,int)` was an arbitrary pick → the wrong body ran
    (`EntryPointNotFound`). New `FindReflectedMethodBySig` matches each `sig` token's mapped `Type` to the reflected
    parameters (the same signature keying the in-`_types` path already does via `MethodsBySig`); falls back to the
    arity pick on any miss — purely additive.
  - **bir2cir — the StringCharSequenceBridge now runs on the RT stdlib self-build too** (gate widened from
    `attributeTopLevelOwner` to `!RefBuild`). The stdlib's own `CharSequence`-extension bodies widen a `String` into a
    `<>dotkt_CharSequence` slot INTERNALLY (`CharSequence.indexOf(string: String)` → the private
    `indexOf(other: CharSequence)`), which the compiled rt.dll body left as a raw String passed where the interface is
    required → `InvalidProgram`/`EntryPointNotFound` at run. The bridge now materializes those internal coercions and
    injects the adapter once into the rt assembly (implementing the RT's canonical `<>dotkt_CharSequence`). Ref build
    still skipped (its bodies are squashed to `throw`).

  Gate-neutral to gate-improving: the verify-il run-fail set is IDENTICAL, and the ilverify set improved 21→20
  (`il-tryexpr` is now fully green — the rt.dll's fixed internal coercions). `il-str`/`il-substr`/`il-char`/`il-charseq`/
  `il-charseqx` all pass. STILL LOWERED (each a DISTINCT deeper stdlib-body bug — a follow-up, no longer dual-rep):
  `trim`/`trimStart`/`trimEnd` (`Char::isWhitespace` method-ref not lowered + un-wrapped inlined `as CharSequence`),
  `reversed` (`StringBuilder(CharSequence)` has no .NET ctor), `padStart`/`padEnd` (StringBuilder append/capacity
  mis-bind), `replace(String,String)` (StringBuilder `append(seq,start,END)`→`Append(str,start,COUNT)`),
  `isBlank`/`isNotBlank` (`all { isWhitespace }` → CharSequence iteration `Iterator.hasNext` not found).
- **Retired the kotc String-indexer lowering `s[i]`→`get_Chars` (bundle 4-B).** kotc no longer hardcodes
  `String s[i]` → `System.String.get_Chars`; `kotlin.String.get(index)` carries `@ClrIntrinsic("get_Chars")`, so kotc
  emits the plain operator-`get` member call and bir2cir's `MemberCallSubstitution` rewrites it to
  `clrInstance System.String.get_Chars` off the ref.dll. Gate-neutral (run-fail set + ilverify set identical);
  `il-charseq` (a user `class S : CharSequence` that indexes `s[index]`) still passes.
- **Retired the kotc Regex lowering (bundle 4-B).** kotc no longer hardcodes `"p".toRegex()`→`new Regex`,
  `r.containsMatchIn(s)`→`IsMatch`, `r.replace(...)`→`Replace`. `kotlin.text.Regex` is
  `@ClrTypeAlias("System.Text.RegularExpressions.Regex")` with `containsMatchIn`/`replace` bound
  `@ClrIntrinsic("IsMatch")`/`("Replace")` and real Kotlin bodies for `matches`/`find`/`split`/`.value` (over the
  `ClrMatch`/`ClrMatchResult` adapters). kotc emits plain calls; bir2cir substitutes the ctor + members off the ref.dll
  and runs the real bodies. `il-regex` RUN-passes; gate-neutral (run-fail set + ilverify set identical — `il-regex`
  stays run-pass / ilverify-fail exactly as in the baseline). NB retiring did NOT clear the `il-regex` ilverify FAIL:
  the `@ClrIntrinsic("IsMatch")`/`("Replace")` bindings sit on a `CharSequence` param but the BCL method takes `string`,
  so the substituted call carries the Kotlin `<>dotkt_CharSequence` argType while a raw `string` is pushed
  (`StackUnexpected`); and the `find`/`.value` bodies (`ClrMatchResult : MatchResult`, `ClrMatchGroupCollection :
  AbstractCollection`) have their own verify noise. Both are stdlib-body/binding follow-ups (materialize the
  `CharSequence` via `toString()` behind a `nativeIsMatch(String)`/`nativeReplace(String,String)` helper, mirroring the
  existing `nativeMatch`/`nativeReplaceFirst` pattern), not kotc lowerings. The kotc TYPE token map
  `kotlin.text.Regex`→`clr:System...Regex` (`BirEmitter.kt`) is left in place (a type-token concern like the `netType`
  maps, separate from the call lowering).

### Added
- **CharSequence synthetic CANONICALIZATION (bundle 4-A) — cross-assembly CharSequence now works.** The synthetic
  interface `<>dotkt_CharSequence` (kotc emits it for `kotlin.CharSequence`, which has no faithful BCL equivalent) is
  now emitted ONCE, publicly, in the rt stdlib dll and REFERENCED by app assemblies instead of re-synthesized
  per-assembly. Previously every dll emitted its OWN copy — a DISTINCT CLR type — so a value crossing the app↔rt
  boundary (a stdlib `CharSequence`-extension called with an app value) threw `EntryPointNotFoundException`
  (`<>dotkt_CharSequence.get_length` not found on the rt-dll copy). ilemit now (1) SKIPS the local definition of a
  canonical synthetic when it already resolves in a `--ref`'d assembly (self-correcting: a `--no-stdlib` build, or the
  stdlib's own ref/rt build — which passes ilemit no `--ref` — still emits it locally), and (2) binds a user
  `class S : CharSequence` (and bir2cir's injected foundation-A `<>dotkt_StringCharSequence` adapter) to the EXTERNAL
  canonical interface by reflection (the existing `clr:` MethodImpl path). Reference/method resolution already routed a
  non-`_types` `@<>dotkt_X` through `ResolveType`/`FindMethod`→reflection, so no call-site changes were needed. Scoped
  to `CharSequence` — the other shared synthetics (`Result`/`KProperty`/`KIterator_*`/`RWProperty_*`) still re-emit
  per-assembly until each is verified cross-assembly. This UNBLOCKS retiring the remaining `STRING_OPS` + the `s[i]`
  indexer + Regex (their stdlib bodies are `CharSequence` extensions). New sample `il-charseqx`:
  `S("hello").hasSurrogatePairAt(0)` (user CharSequence → stdlib ext) and `"hi".hasSurrogatePairAt(0)` (String →
  foundation-A adapter → stdlib ext) both run. verify-il gate-neutral (36-fail set identical; PASS +1 for il-charseqx),
  verify-ktproj 9/9. kotc/bir2cir unchanged; ilemit only (CLR codegen reading .NET metadata — the layer that owns type
  resolution). NB `il:injstatic` is a SEPARATE root cause (rule-3 misrouting of an app facadegen-injected static member
  into the non-existent stdlib `<>dotkt_ClrH_<Type>` body-hoist helper), NOT this per-assembly-duplication pattern.
- **String → CharSequence adapter bridge (bundle 4-A FOUNDATION).** A bare `System.String` flowing into a
  `kotlin.CharSequence` slot now works polymorphically (`val cs: CharSequence = "abc"; cs.length` → `3`, `cs[1]` →
  `'b'`; a `String` literal passed to a `CharSequence`-typed function). `kotlin.String` is `@ClrTypeAlias("System.String")`
  — a **sealed** BCL type that cannot implement the synthetic `<>dotkt_CharSequence` interface kotc emits for
  `kotlin.CharSequence` — so bir2cir now MATERIALIZES the coercion: a new `StringCharSequenceBridge` pass detects a
  statically-`String` value flowing into a `<>dotkt_CharSequence` slot (a call's CharSequence-typed arg / extension
  receiver, a `CharSequence` return, a `CharSequence`-local store, an `as CharSequence` cast) and wraps it in
  `new <>dotkt_StringCharSequence(str)` — an **app-local** adapter class the pass injects (String-backed
  `length`/`get`/`subSequence`, modeled on the verified user-`class S : CharSequence` shape). App-local because the
  synthetic interface is emitted per-assembly: a stdlib adapter would implement the rt-dll copy, unreachable by the
  app's interface dispatch. Purely additive — wraps ONLY positively-`String` values, never an already-`CharSequence`
  one — so kotc's `STRING_OPS` (statically-`String`-receiver ops) and every passing sample are untouched. APP builds
  only (ref/rt stdlib self-builds byte-identical). kotc unchanged; ilemit emits the injected type as ordinary CLR.
  verify-il gate-neutral. NOTE: this unblocks intra-assembly CharSequence polymorphism, but calling a *stdlib*
  CharSequence-extension with an app value crosses the app↔rt synthetic-interface boundary — a separate, deeper blocker
  for the String-op retire (B) / Regex follow-ups (see `docs/master-task-inventory.md` 【4-A】).

### Changed
- **Layer-purity: retired kotc's hardcoded `kotlin.math.* → System.Math` lowering (the pilot of the "retire a kotc
  hardcoded CLR lowering" pattern).** kotc no longer rewrites `kotlin.math` calls into `clrStatic System.Math` nodes
  (`MATH_FUNCS` + the `BirEmitter` emit site are gone); it emits a plain call and bir2cir's `MemberCallSubstitution`
  substitutes it from `MathClr.kt`'s existing `@ClrIntrinsic` bindings on the ref.dll (no stdlib change needed). Also
  fixed a latent bir2cir bug this exposed: the top-level `@ClrIntrinsic` index was keyed by function NAME only, so
  arg-type-discriminated overloads collided — `sqrt`/`abs`/`pow`/… and `isNaN`/`isInfinite`/`isFinite` silently used
  the `System.Math`/`System.Double` overload for Float args instead of `System.MathF`/`System.Single`. Now resolved by
  the exact call signature (Float math correctly hits `System.MathF`). verify-il gate-neutral (fail-set identical).
- **Layer-purity: retired kotc's hardcoded `String`/`Char` CLR lowerings (bundle 1, batch 2).** Following the Math
  pilot's recipe, kotc no longer hardcodes the *cleanly-substitutable* `kotlin.text` ops — it emits a plain call and the
  already-built bir2cir `MemberCallSubstitution` consumes the stdlib's `@ClrIntrinsic` bindings off the ref.dll:
  - **String family:** `uppercase`/`lowercase` (→ `@ClrIntrinsic` `ToUpperInvariant`/`ToLowerInvariant`),
    `substring(startIndex)` (→ `Substring`), `"42".toInt()`/`toLong`/`toDouble`/`toFloat`/`toShort`/`toByte` (→
    `System.X.Parse`; the `NUMBER_PARSE` map deleted), and `repeat(n)` (the real StringBuilder body). `String.format`
    deleted as **dead code** — a `java.util.Formatter` JVM-ism the CLR frontend jar has no symbol for (unresolved
    before the backend runs); making it work is a stdlib `String.Companion.format` `@ClrIntrinsic` binding, not a kotc
    lowering.
  - **Char family:** `isDigit`/`isLetter`/`isWhitespace`/`isLetterOrDigit`/`uppercaseChar`/`lowercaseChar`/
    `isUpperCase`/`isLowerCase` (the `CHAR_OPS` map deleted) → `CharClr.kt`'s `@ClrIntrinsic("System.Char.*")` FQ
    bindings, substituted to `clrStatic System.Char.*`.
  - **Reusable bir2cir fix:** the bare-`@ClrIntrinsic` extension-fun index was keyed by `name|recvKey`, so a same-name/
    same-receiver overload of a **different arity** collided — `substring(String,Int)` captured the 3-arg
    `substring(String,Int,Int)` call and emitted `Substring(start,end)` with `end` read as a LENGTH. Now keyed by
    `name|recvKey|paramCount` (the sibling of the Math pilot's full-signature keying).
  - **DELIBERATELY KEPT lowered (blocked, not retired):** `trim`/`contains`/`startsWith`/`endsWith`/`replace`/
    `indexOf`/`padStart`/`padEnd`/`split`/`reversed`/`substring(start,end)`/`isEmpty`/`isBlank` — their stdlib bodies
    are `CharSequence` extensions, so a `System.String` receiver hits the known String/CharSequence
    **dual-representation** crash (InvalidProgram / EntryPointNotFound). And **`Int/Long.toString(radix)`** (the
    `System.Convert.ToString` lowering) — bir2cir attributes it correctly but the stdlib digit-loop body miscompiles
    cross-module (base-2 OK, but `255.toString(16)` → `"ffffffff"`), so retiring would ship a correctness regression.
    Both retire only once the underlying stdlib/emit bugs are fixed. verify-il gate-neutral (il fail-set + full FAIL
    list identical before/after).
- **Layer-purity: retired kotc's hardcoded `System.Console` (`println`/`print`) + `readLine` CLR lowering (bundle 1,
  batch 3).** kotc no longer emits the hardcoded `{"k":"console"}` node — `println`/`print` are emitted as PLAIN
  top-level fun calls and bir2cir's `MemberCallSubstitution` substitutes them to `System.Console.Write`/`WriteLine`
  from `ConsoleClr.kt`'s existing `@ClrIntrinsic` bindings (top-level-intrinsic-by-name path; both are unambiguous, so
  no stdlib or bir2cir change was needed). Value-type args box via ilemit's `EmitArg` (`object` param); the Kotlin
  collection→`clrCollToString` toString adapter is KEPT (Kotlin semantics — it calls a stdlib helper, not a CLR
  member). Deleted the now-dead ilemit `case "console"` consumer. `readLine()` deleted as **dead code** (like
  `String.format`): the CLR frontend jar has no `kotlin.io.readLine` symbol — the CLR I/O API is `readln()`/
  `readlnOrNull()` (the latter `@ClrIntrinsic`-bound to `System.Console.ReadLine`). verify-il gate-neutral (fail-set
  identical, 36). This closes the mechanically-retirable part of bundle 1; the rest of the batch-3 families are
  **BLOCKED on the dual-rep/collection-bridge (bundle 4) or the deferred delegate/coroutine layers, NOT retired:**
  - `use{}`/`IDisposable.Dispose` — a structural `try/finally` inline desugar; `close→Dispose` is a `clrName`
    member-rename (shared with the class-emit path), not an `@ClrIntrinsic` call-substitution.
  - `by lazy`/`System.Lazy<T>` — structural delegate construction (`new Lazy<T>(Func<T>)` + `Value`); `kotlin.Lazy`
    is a Kotlin interface with Kotlin implementors and there is no `@ClrIntrinsic` factory to substitute.
  - `compareTo`/`IComparable.CompareTo` — the primitive path is the primitive dual-rep and the user-`Comparable<T>`
    path is a `constrained.` callvirt (structural CLR lowering); `il-comparable`'s open Comparable-self dual-rep bug.
  - indexer `get_/set_Item` — `String s[i]`→`get_Chars` is the String/CharSequence dual-rep (same class as the
    batch-2-blocked String ops), and the injected-`.NET`-indexer arm is per-sample facadegen metadata (NOT stdlib
    ref.dll), so bir2cir cannot substitute it.
  - `listOf`/`setOf`/`mapOf`→`listNew`/`setNew`/`mapNew` — structural collection-literal factories that must retire
    together with the `COLLECTION_MEMBER`/`COLLECTION_OPS` clrName table (the collection-bridge, bundle 4).
  - `Regex` — CharSequence dual-rep + `MatchResult` adapters (`find`/`value`). `Task.Delay` — SKIPPED (inside
    `coAwaitable`, the coroutine await machinery; the coroutine layer is deferred to bundle 6).
- **Round-trip carrier attributes for Kotlin class-nature (`sealed` fully; `fun interface` nature) — re-consuming a
  DotKt `.dll` as Kotlin restores more of the original surface (round-trip gaps ③ + ⑤).** A `fun interface` (SAM) and a
  `sealed` class/interface lower to a plain CLR interface / abstract-class, dropping the Kotlin nature. Now: kotc emits
  `isFun` (from `IrClass.isFun`) and `isSealed` (from `Modality.SEALED`) BIR flags; ilemit synthesizes + stamps two new
  embedded metadata attributes `[KotlinFunInterface]` / `[KotlinSealed]` (the same self-embedded model as
  `[KotlinFunction]`/`[KotlinFileClass]`, stripped in the runtime build); facadegen reads them back as `funinterface` /
  `sealed` meta lines; and `ClrTypeInjection` restores `status.isFun` / `Modality.SEALED` on the re-consumer's FIR.
  - **`sealed` (⑤) round-trips fully:** the modality, **cross-module inheritance enforcement** (a rogue subclass in
    another module is rejected), AND **exhaustive `when` with no `else`** — the closed inheritor set is rediscovered
    because the sealed type's subtypes are themselves injected into the consumer's session via their `super` edges.
  - **`fun interface` (③) restores the NATURE, not the lambda:** a consumer sees a functional interface and can
    implement it (incl. anonymous `object : Handler { … }`), but a bare **lambda** still won't SAM-convert — the pinned
    Kotlin 2.2.0 `FirSamResolver.computeSamCandidateNames` scans `FirRegularClass.declarations` directly, which a
    `FirDeclarationGenerationExtension`-injected interface leaves empty (members are scope-served). Documented as a
    pinned-compiler limitation (same basis as `object`/companion).
  - **`enum class` (④) NOT restored:** blocked at the injection layer — a `FirDeclarationGenerationExtension` (2.2.0)
    cannot synthesize real `FirEnumEntry` declarations (FIR's exhaustiveness checker requires them; the plugin API has
    no entry hook), so no `[KotlinEnum]` carrier is emitted. A basic enum still round-trips as an `object` of `val`s
    (value access works). Flagged in `docs/dotkt-semantics.md` §10.2/§10.4.
  - Covered by a new `roundtrip-markers` section in `scripts/verify-roundtrip.sh` (a `fun interface` + `sealed`
    hierarchy + `enum` library, re-consumed: anonymous-object handler runs, exhaustive `when` compiles, a rogue
    sealed subclass is rejected). `docs/dotkt-semantics.md` §10 updated.
- **kotc→bir2cir `clrName` migration, Step 3: the bir2cir compensation for removing `annClr` (member-strip + flags +
  setter markers), verified byte-identical.** With the ilemit overload-attribute fix in place (ref.dll now carries every
  overload's `@ClrIntrinsic`), bir2cir gained the machinery to reproduce what kotc's `annClr`/`clrIfaceMemberName` does,
  so kotc can stop reading `@ClrIntrinsic`: (1) a `MemberStrip` pass (before `AliasHelperHoist`) that drops
  `@ClrIntrinsic`-bound stub declarations by FULL SIGNATURE (`IsBoundStub` + a `ParamKey` canonicalizer over the new
  `MemberBinding.ParamTypes`, so `StringBuilder.append(Char)` is dropped while `append(CharSequence?)` is kept; an
  alias-class member that merely OVERRIDES a `@ClrIntrinsic` member is dropped too; INTERFACE members are never stripped
  — they declare the CLR slot); (2) `DeclarationRename` restores the `override:true`/`vis:public` flags exactly when a
  CLASS member's rename fires (kotc's `clrIfaceName`-driven `isOvr`/vis — never inside an interface); (3) kotc's
  `overridesJson` now derives an accessor's marker from the PROPERTY's override closure (so a `var size` setter
  overriding a `val size` still renames `set_size`→`set_Count`), and `ResolveSlot` looks the intrinsic up on the
  `get_<name>` accessor for both getter and setter. All verified **byte-identical with annClr active** (idempotent
  no-ops). This drops the annClr-OFF diff from 71 → 6; the actual `annClr` deletion awaits those last 6 (see
  prioritized-tasks): top-level `sort`/`append` signature-strip (array-class param canonicalization), a call-side
  `clrPropGet` vs `clrInstance get_X` routing edge, and 3 helper/closure body diffs.

### Fixed
- **App-consume of collection-BUILDING ops (`map`/`filter`/`toList`/`toMutableList`/`reversed`) now works — two general
  codegen fixes (NOT a stdlib special-case; the mutable-collection actuals were already `@ClrTypeAlias`/`@ClrIntrinsic`
  bound and direct `ArrayList().add(...)` worked).** (1) **Generic-parameter-receiver constrained dispatch:** the real
  stdlib `mapTo`/`filterTo`/`toCollection` do `destination.add(x)` where `destination: C` and `C : MutableCollection<R>`.
  bir2cir lowered this to a plain `callvirt` on the `ICollection<object>` owner (the alias padded the missing type args
  with `object`), which mis-dispatches at runtime — a `List<R>` implements `ICollection<R>`, not `<object>`, so the JIT
  found no slot and threw `EntryPointNotFoundException`. bir2cir's `MemberCallSubstitution` now threads a lexical
  type-parameter/param environment (`SubstCtx`) and, when a CLR-aliased-interface member is invoked on a
  generic-parameter receiver, emits a `constrainedCall` node (`recvType=gp:C`, `iface=ICollection<R>` from the
  receiver's constraint); ilemit's `constrainedCall` handler gained an N-arg form that emits `constrained. !!C ; callvirt
  ICollection<R>::Add`. (2) **Ctor overload argType precision:** `ArrayList(collection)` (used by `toMutableList`/`toList`/
  `reversed`) lowered to `new List<T>(...)` with argType `object` (bir2cir dropped kotc's declared ctor param type and
  re-inferred `object` from the bare local), so ilemit couldn't disambiguate `List(int capacity)` from
  `List(IEnumerable<T>)` and picked the wrong one → `InvalidProgramException`. bir2cir's `TransformNew` now instantiates
  the ctor's declared param types by substituting the class type params with the `new`'s type args (`ArrayList[Int]` ⇒
  `E:=Int`, via a new ref.dll type-param-name index) — yielding a precise `IReadOnlyCollection<int>` overload key; ilemit
  falls back to assignability (`PickCtorByAssignable`) when the exact ctor misses (`IReadOnlyCollection<int>` IS
  `IEnumerable<int>`). Residuals (separate pre-existing bugs, unchanged): `sorted()` on a `Collection` (value-type
  `toTypedArray`/`Array.Sort`), `mapNotNull` (nullable-generic `?.let`), and `for (x in this: Iterable<T>)` over a
  generic receiver.
- **Round-trip gap ①: generic CONSTRAINTS and declaration-site VARIANCE now survive re-consuming a DotKt assembly as
  Kotlin.** `ilemit` already wrote the CLR constraints (`SetBaseTypeConstraint`/`SetInterfaceConstraints`) and interface
  variance (`GenericParameterAttributes.Covariant/Contravariant`), but `facadegen` emitted only the bare type-param NAME
  and the FIR injector hard-coded `Variance.INVARIANT` with no bounds — so a consumer saw an unconstrained, invariant
  `T`. `facadegen` now reads `GetGenericParameterConstraints()` / `GenericParameterAttributes` and emits them as
  backward-compatible metadata lines (`tvariance`/`tbound` for a class/interface type param, `mbound` for a method type
  param; a Kotlin `Comparable<T>` bound is reversed from the CLR `System.IComparable<T>` it lowers to), and
  `ClrTypeInjection` restores them on the synthesized FIR (`out`/`in` variance + upper bounds via lazy lookup-tag cones,
  self-referential-safe for the curiously-recurring BCL numeric tower reachable from a `System.*` closure, and fail-soft
  so a pathological bound degrades to an unconstrained `T` rather than crashing). A round-trip of `interface P<out T>` /
  `interface C<in T>` / `class SortedPair<T : Comparable<T>>` / `fun <T : Comparable<T>> maxOf2` now restores the
  variance (covariant/contravariant assignability compiles) and bounds cross-module. (docs/dotkt-semantics.md §10.)
- **`il:regex` restored after the DotKt.Runtime retirement — `matches`/`find` now run on the real stdlib bodies (no
  shim).** Removed two stale kotc CLR-lowerings the retirement missed: the `kotlin.text.MatchResult`→`System...Match`
  type alias (which made `ClrMatchResult : MatchResult` implement a CLASS as an interface → `TypeLoadException`) and the
  `MatchResult.value`→`Match.Value` call lowering. Stdlib-side, `matchEntire`/`matchAt`/`matchesAt` materialize the
  `CharSequence` input to a `String` before reading `.length` (System.String does not implement the synthetic
  `<>dotkt_CharSequence`), and `ClrMatchResult.groups` became a lazy getter (no eager `AbstractCollection` load).
  kotc now OMITS a cross-module default arg whose value deserialized as an `IrErrorExpression` so ilemit fills it from
  `[DefaultParameterValue]` metadata (fixes `Regex.find(input)` with `startIndex` omitted); `ilemit.EmitCallArgs` fills
  omitted trailing defaults on the callStatic/callInstance path.
- **`kotlin.Result` (and other pure-Kotlin, non-`@ClrTypeAlias` stdlib types) resolve as REFERENCED types cross-module.**
  ilemit `MapType`/`ParseOwner`/`ResolveMethod` resolve a `@Name`/`Name[args]` token absent from this assembly's
  `_types` as a referenced .NET type/generic (arity-suffixed), and resolve instance members on the reflection-constructed
  instantiation; bir2cir attributes a multi-overload top-level fun (e.g. `runCatching`) to its shared file-class owner
  when the receiver key doesn't disambiguate. `il:result` no longer crashes at emit (KeyNotFound gone); it now fully
  resolves. **Residual (scoped follow-up):** `getOrNull(): T?` for a value-type `T` returns bare `Int32` where the call
  site needs `Nullable<Int32>` (the pre-existing primitive-dual-representation gap) — `il:result` does not yet pass.
- **ilemit: `@ClrIntrinsic` (and every user annotation) dropped from all-but-last overload in the ref build.** The
  user-annotation → `.NET` custom-attribute application (`Program.cs`) resolved the target `MethodBuilder` by NAME
  only (`ti.Methods[name]`), which is last-declared-wins for overloads — so for an overloaded intrinsic function
  (`sin(Double)`+`sin(Float)`, `sort(IntArray/…)`, `append(…)`, `println(…)`) every def's attrs landed on the single
  last-declared builder while the earlier overloads got NONE. In `DotKt.Private.Stdlib.dll` this left `sin(Double)`
  with `intr=[]` and doubled `sin(Float)` to `["System.Math.Sin","System.MathF.Sin"]`. Since the ref.dll is bir2cir's
  binding source, the intrinsic was invisible for those overloads (blocked the `clrName`/annClr removal and mis-bound
  cross-module calls). Fix: resolve by SIGNATURE first (`MethodsBySig[SigKey(name, m)]`), name-only fallback —
  mirroring the Kotlin-metadata path. Verified 1:1: 262 ref.dll methods carry `@ClrIntrinsic` = 262 CIR method-defs
  (was fewer, with doubled values). rt build unaffected (metadata stripped there).

### Changed
- **kotc→bir2cir `clrName` migration, Step 3 part 2: CLR-property-entry slot rename.** kotc tags each emitted
  `properties:[{name,get,set}]` record with the getter's `overrides` marker, and bir2cir's `DeclarationRename` renames
  the record's `get`/`set` accessor references (`get_size`→`get_Count`, `set_size`→`set_Count`) via a new
  `ResolveBareIntrinsic` (the @ClrIntrinsic lives on the `get_<name>` accessor in the ref.dll; the bare value is the BCL
  property name, applied to both accessors). The record's `name` stays the Kotlin property name (matching annClr).
  Verified rt CIR byte-identical with annClr active (idempotent); an annClr-off probe confirms it FIRES (the property
  records emit `get_Count`). **Newly surfaced remainder for the annClr removal** (beyond the member-strip + SAM): the
  `override`/`virtual`/`vis` FLAGS are also computed via `clrIfaceMemberName` (an interface-override method's
  `override:true` depends on it) — these must move to a pure-Kotlin signal (`overridesIface`) or bir2cir; and the
  member-strip needs full-SIGNATURE (param-type) matching, not just name+arity (StringBuilder.append has same-arity
  @ClrIntrinsic + rule-3 overloads), and must run BEFORE AliasHelperHoist (else the rule-3 helper over-hoists).
- **kotc→bir2cir `clrName` migration, Step 3 part 1: CALL-SITE slot rename.** kotc now emits the same pure-Kotlin
  `overrides` marker on the `callInstance` nodes whose member name `clrIfaceMemberName` resolves via `@ClrIntrinsic`
  (the property-accessor and method-call paths), and bir2cir's `DeclarationRename` is now a recursive walk that renames
  a CALL's `method` (not just a declaration's `name`) from that marker + the ref.dll — so an implementor-side call
  `AbstractList.get_size` tracks its renamed declaration `get_Count`. The pass moved to run BEFORE
  `MemberCallSubstitution` (so a now-`get_Count` call on a CLR-bound owner still lowers to `clrPropGet`). Verified rt CIR
  byte-identical with annClr active (idempotent); an annClr-off probe confirms it FIRES — the call side now compensates
  (probe diff 71→46 files, the `AbstractList.get_size not found` failure gone). **Remaining for annClr removal**: the
  `@ClrIntrinsic`-bodyless member-strip (bir2cir, the member mirror of the @ClrTypeAlias type-strip), the
  `properties:[{get,set}]` entry rename, the fun-interface SAM rewrite — then kotc plain-naming + delete `annClr`.
- **kotc→bir2cir `clrName` migration, Step 2a FIX: the declaration-rename was inert; now functional.** Step 2a's
  `DeclarationRename` (552261e) was a verified no-op — it looked the property `@ClrIntrinsic` up by the property NAME
  (`size`), but in the ref.dll that attribute lives on the ACCESSOR METHOD (`get_size@ClrIntrinsic("Count")`, the
  intrinsic value being the BCL property name), so `ResolveSlot` always returned null and kotc's annClr name was simply
  kept (still byte-identical, but the rename did nothing). Fixed `ResolveSlot` to look up the accessor method
  (`get_`/`set_`+name) by exact arity and prefix the result; removed the dead `GetProperties()` scan + unused
  `TryMemberIntrinsicByName`. Verified: rt CIR still byte-identical with annClr active (idempotent), and an annClr-off
  probe now correctly renames `AbstractCollection.get_size`→`get_Count`. This makes the Step-3 prerequisite real (the
  rename actually compensates when annClr is removed).
- **kotc→bir2cir `clrName` migration, Step 2a (IDEMPOTENT declaration-rename, byte-identical): bir2cir now owns the BCL
  slot-name derivation.** Two bir2cir additions consume the Step-1 `overrides` markers to reproduce what kotc's
  `clrName`/`annClr` does for declaration naming: (1) `ScanSubstitutionMetadata` now also reads `GetProperties()`, so a
  property's `@ClrIntrinsic` (`Collection.size`→`"Count"`, `CharSequence.length`→`"Length"`) — which lives on the
  property, invisible to the `GetMethods()` scan — enters `MemberBindings`; (2) a new `DeclarationRename` pass (gated to
  NON-ref builds, runs before the marker is stripped) renames an emitted method/accessor to its BCL slot from the FIRST
  overridden member carrying an `@ClrIntrinsic` in the ref.dll (a `size` getter override → `get_Count`, `resumeWith` →
  `ResumeWith`). Method overloads match by EXACT arity (a new `TryMemberIntrinsicExact` — so `add(element)`→`Add` does
  NOT fall through to `add(index,element)`→`Insert`); property accessors match by name (`TryMemberIntrinsicByName`).
  With `annClr` STILL running in kotc the pass is **idempotent** → verified **rt CIR byte-identical** (0 diff) and ref
  💮 (`kotlin.Int : Comparable<kotlin.Int>`) intact. This moves the slot-name LOGIC to bir2cir without yet removing the
  kotc annotation read. **Remaining for Step 3** (the actual `annClr` removal, deferred — proven not single-pass-safe):
  add `fn`-self to the marker (a method with its OWN `@ClrIntrinsic`; harmless/idempotent today, a byte-identity
  prerequisite once annClr is gone), rename the `properties:[{get,set}]` entries, the `@ClrIntrinsic`-bound member-strip,
  the fun-interface SAM rewrite, then switch kotc's decl-name sites to plain Kotlin names and delete `annClr`.
- **kotc→bir2cir `clrName` migration, Step 1 (NEUTRAL groundwork): pure-Kotlin override markers.** Toward "kotc reads
  NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias`", kotc now emits an `overrides:[{owner,member,kind,arity}]` marker on each
  instance method / interface method / property accessor — the transitive closure of the interface/base members it
  overrides, in **pure Kotlin terms** (FQN + Kotlin member name + getter/setter/method + arity; NO `@ClrIntrinsic` read,
  NO BCL name). bir2cir **strips** the marker in `BirTypeLowering` so it never reaches the CIR/ilemit. **Behavior-neutral
  and verified CIR byte-identical** (rt stdlib: 0 differing/new/removed files vs the prior build; 95 BIR files carry the
  marker, 0 leak to CIR). The marker is the handshake a future Step 2 consumes — bir2cir resolves the BCL slot name from
  the ref.dll `@ClrIntrinsic` (`TryMemberIntrinsic`) instead of kotc's `clrName`/`annClr`: validated that e.g.
  `AbstractCollection.get_Count` ← `Collection.size`(getter) → ref.dll `@ClrIntrinsic("Count")`, and `String.get_Length`
  ← `CharSequence.length`(getter) → `@ClrIntrinsic("Length")` reproduce exactly. **Remaining** (Step 2/3, deferred — a
  large coordinated change proven not single-pass-safe by a 72-file/ilemit-crash probe): a bir2cir declaration-rename
  pass (markers + ref.dll) + the `@ClrIntrinsic`-bound-member DROP (member-strip, the `clrName(it)==null` emission
  filters) + the fun-interface SAM rewrite (Comparator→IComparer), then switch kotc decl-name sites to plain names and
  remove `annClr`. Also pending markers on the `properties` get/set entries + SAM methods + `clrAccessorMethod`.
- **`@ClrTypeAlias` type-STRIP moved kotc → bir2cir (layer-purity).** kotc no longer reads `@ClrTypeAlias` to strip a
  CLR-bound type from emission: `substitutedAway` / `hasClrTypeAlias` / `hasHoistableBody` and the `aliasPlainTypes` +
  "alias-only file" branches are **deleted**. kotc now emits EVERY type as ordinary Kotlin (a primitive `kotlin.Int`,
  the `kotlin.collections.List` interface, `kotlin.text.StringBuilder`, …); bir2cir's `AliasHelperHoist` DROPS each
  alias type def — hoisting a class's rule-3 members into the `<>dotkt_ClrH_*` helper, and dropping an interface/object
  alias with NO helper (a new `kind == "class"` guard, so a ref.dll default-interface-method can't false-positive into a
  bogus interface helper). The rt-stdlib emit is unchanged in IL (still 14 helpers; the only CIR deltas are internal
  label-id renumbering from the new type-emission order, a now-defined `<>dotkt_CharSequence` that `kotlin.String`'s
  helper already referenced, and the removal of 4 pointless **empty** file-classes — Primitives/Comparable/Any/MathH —
  which bir2cir now skips when an alias-only file lowers to nothing). The reference build is untouched (the strip was
  always a no-op there: `clrName` is null in the ref, so the old `substitutedAway` never fired). Drives kotc toward
  "reads NEITHER `@ClrIntrinsic` NOR `@ClrTypeAlias`": the `@ClrTypeAlias` read is now gone except the fun-interface-SAM
  alias lookup; what remains is the `clrName`/`netType` member-call + type maps.
- **Rule-3 static-helper SYNTHESIS moved kotc → bir2cir (layer-purity, MIXED-file hoist).** kotc no longer synthesizes
  the `<>dotkt_ClrH_<owner>` helper for a CLR-bound (`@ClrTypeAlias`) class: `clrHelperClassJson`/`clrHelperMethod`/
  `clrHelperMembers` (which read `@ClrIntrinsic`) are **deleted**. kotc now emits EVERY bound alias class with hoistable
  bodies as a PLAIN BIR type — the alias-only files (String/Char/Boolean) AND the previously kotc-synthesized MIXED
  files (StringBuilder/collections/Regex/unsigned) alike — gated by the pure-Kotlin `hasHoistableBody` (no annotation
  read); bir2cir's existing `AliasHelperHoist` (the single home of rule-3 synthesis) hoists their members and drops the
  type. bir2cir gained two fixes for the now-bir2cir-owned MIXED set: (a) a GENERIC alias owner types `__self` as the
  constructed `kotlin.collections.ArrayList[gp:E]` (lowers to `clrg:…List[gp:E]`, was a non-generic `clr:…List` that
  ilemit could not resolve); (b) an `@JvmInline` value-class alias (UInt/UByte/…) does NOT hoist its `Equals`/
  `GetHashCode`/`ToString` overrides (they read the erased `.data` field → an unresolvable `<self>.data` on the `ubyte`
  shorthand; they defer to the BCL primitive instead). The emitted rt-stdlib helper set is byte-identical to before (14
  `<>dotkt_ClrH_*`), with kotc now producing zero of them. Remaining for the "kotc reads NEITHER annotation" goal: the
  `substitutedAway` strip-routing (still reads `@ClrTypeAlias`/`@ClrIntrinsic`) and the `clrName`/`netType` member-call +
  type maps.

### Fixed
- **App-consume of the rt stdlib: `for (x in list)` now iterates a referenced collection.** kotc desugars the loop to
  a `<iterator>` var initialized by the rt bridge `ClrIteratorBridgeKt.iteratorOverEnumerable` (which returns the real
  generic `kotlin.collections.Iterator<E>`) and routes `hasNext`/`next` to a synthetic monomorphized
  `<>dotkt_KIterator_*` interface — a legacy "IL can't define a generic interface" workaround that KeyNotFounds in an
  app build (the synthetic + the `@kotlin.collections.Iterator` var type are referenced, not emitted). A new bir2cir
  pass (`IteratorConsumerNormalization`, app build only) retypes the var to `clrg:kotlin.collections.Iterator[E]` and
  converts the synthetic `hasNext`/`next` `callInstance` to a `clrInstance` on the real referenced interface (the
  `EmitClrCall` path the substituted IReadOnlyList already uses), in a single document-order walk so sibling/nested
  for-loops reusing the `<iterator>` name bind to their own element type. The rt stdlib bridge
  `iteratorOverEnumerable` (+ its two `@ClrTypeAlias` interface types) was made `public` (was `internal` →
  `MethodAccessException` from an app).
- **App-consume of the rt stdlib: referenced top-level stdlib funs now resolve.** A top-level stdlib function called
  from an app (`xs.getOrElse(i){…}`, `xs.first()`, …) is emitted by kotc as `callStatic owner=null`; ilemit's
  `FindStatic` only searches THIS assembly's file-classes, so it threw `static method not found`. bir2cir now reads
  the ref.dll for non-intrinsic file-class statics and, in an **app** build (`DOTKT_STDLIB_COMPILE` unset),
  attributes such an owner-less call to the file-class it actually lives in (`kotlin.collections._CollectionsKt`),
  disambiguated by the call's receiver type when overloaded across file-classes (CollectionsKt vs ArraysKt vs MapsKt).
  ilemit's owner-present `FindMethod` then resolves it by reflection against the runtime stdlib (the same path the
  iterator bridge already uses). Gated off for the stdlib self-build (the fun is local there) and when the name is
  locally defined; the rt/ref stdlib CIR is byte-identical after the change. New sample `cases/ktproj-coll` builds and
  runs a practical collections app (List local + `first`/`getOrElse`/`contains`/`indexOf`/`count`/`isEmpty`/`take`) via
  MSBuild `dotnet build`/`dotnet run`; wired into `verify-ktproj.sh`.
- **ilemit picks the arity-matching overload of a referenced file-class static.** The reflected-method lookup used an
  unconstrained `GetMethod(name)` that threw `AmbiguousMatchException` and fell back to an arbitrary pick, emitting a
  stack-mismatched call (`InvalidProgramException` at run) for e.g. `_CollectionsKt.first(List<T>)` vs
  `first(Iterable<T>, predicate)`. It now prefers the overload whose parameter count matches the call's `sig`.

### Changed
- **verify-il routes the migrated `m2`/`mi1`/`c1net` samples through the facadegen import path.** `m2`/`mi1` consume
  BCL types via `import System.X` (System.Math, StringBuilder) but ran under a bare `il_check` that injects nothing —
  moved to a new `il_check_imports` (scan-imports + facadegen `--meta`, no `runtime.cs`). `c1net` consumes its own
  `runtime.cs` types via `import Probe.X` — moved off `il_check_ref` (no import scan, the dead `@Clr`-facade path) onto
  `il_check_inject` (build runtime + scan imports + `--ref`). `il_check_ref` stays for the coroutine samples that ship
  a `runtime.cs` but import nothing.
- **bir2cir is now the single-path owner of Kotlin→CLR type substitution.** The `CompatBir` verbatim-copy mode and
  the `--compat-bir`/`--native-cir` output-selection flags are gone — there is one path: a real type-lowering pass
  rewrites the Kotlin type vocabulary in the BIR into the CLR-codegen vocabulary ilemit consumes, emitting a
  BIR-shaped CIR (same node shape; only type strings change, so ilemit needs no shape change). The lowering is
  build-gated by env (not a flag): the pure-Kotlin **reference** stdlib surface (`DOTKT_STDLIB_COMPILE` set,
  `DOTKT_STDLIB_SUBSTITUTE` unset) keeps `kotlin.*` tokens verbatim; **every other** build (the runtime stdlib and
  all app builds) lowers a bare `kotlin.*` primitive to its CLR token (`kotlin.Int` → `int`, …). kotc still emits the
  CLR shorthand today, so the rewrite is a verified no-op against current output (it activates once kotc is switched
  to emit `kotlin.*` symbols). `scripts/dotkt.sh` drops its `--native-cir` flag accordingly.

### Removed
- **Namespace projection** (`[DotKtNamespaceProjection]` / `ilemit --ns-projection` / the `nsproj` meta line). The
  assembly-level Kotlin-package ↔ .NET-namespace remap (e.g. consuming a `DotKt.Coroutines` library as
  `import kotlinx.coroutines.*`) had no real use — a DotKt assembly's types are seen 1:1 at their actual .NET
  namespace as the Kotlin package, and a library that wants a `kotlinx.*` package simply declares `package kotlinx.*`.
  Removed across kotc/ilemit/bir2cir/facadegen, the `DotKtNamespaceProjectionAttribute` runtime type, the MSBuild
  `<DotKtNamespaceProjection>` item, and the `roundtrip-nsproj` test.

### Added
- **`DotKt.Stdlib` — a tracked first-party library of real-Kotlin stdlib ops**, compiled by DotKt's own toolchain
  (`runtime/DotKt.Stdlib/`, built by `scripts/build-dotkt-stdlib.sh`). It holds standard-library operations migrated
  off the compiler's hand-written `COLLECTION_OPS` LINQ lowerings onto their real Kotlin source. Auto-referenced by the
  verify harnesses (and intended for every `.ktproj`); a call to a migrated op routes to the real body via the
  round-trip registry. First migrated op: **`List.getOrElse`** (random-access, runs directly on the BCL `List<T>`).
  Validated against the Kotlin/JVM oracle (verify-differential) — the real-Kotlin reimplementation matches JVM semantics.
- **`facadegen --scan-asm <dll>`** — inject ALL `[KotlinFileClass]` facades from a referenced DotKt library wholesale
  (auto-imported stdlib functions never appear in the `--import-list`), so DotKt.Stdlib's ops are visible to the FIR
  injector without naming each one.
- **`<DotKtKotcOptions>` MSBuild property** — pass raw kotc flags through to the compile step (appended verbatim, e.g.
  `-Xallow-kotlin-package`, `-opt-in=...`, `-Xcontext-parameters`). Needed to compile the Kotlin standard library itself
  (see `docs/design-stdlib-compilation.md`); useful for any advanced compiler option.

- **Kotlin `Iterable<T>` (as a parameter/receiver type) lowers to `IEnumerable<T>`.** The broadest read-only iteration
  interface — `List<T>`, `HashSet<T>`, and any CLR `IEnumerable<T>` all bind, so a real-Kotlin `Iterable<T>.map(...)` in
  DotKt.Stdlib accepts them all and `for (x in this)` enumerates via `GetEnumerator`/`MoveNext`/`Current`. As a user
  class SUPERTYPE, `Iterable`/`Iterator` stay the synthetic monomorphized interface (implementing `IEnumerable<T>` would
  need a synthesized `GetEnumerator` — the producing-side bridge, separate work), so user iterables are unaffected.
- **Collection ops migrated off the LINQ lowering onto real Kotlin.** A `List`/`Collection`/`Set`/`Iterable` receiver
  routes these ops to the real Kotlin body shipped in DotKt.Stdlib (iterate + build an `ArrayList`), matching Kotlin/JVM
  (verify-differential): **`map`, `filter`, `forEach`, `count`, `fold`, `any`, `none`, `all`, `toList`, `toMutableList`**
  (plus the random-access `getOrElse`). `Array`/`Sequence` receivers keep the LINQ lowering (DotKt.Stdlib ships only the
  `Iterable` overload). The skip is gated on the op being registered from a referenced DotKt.Stdlib, so it composes with
  the lowering-retirement seam. New verify-il case `mapfilter`.
- **Mutable collections + the real-stdlib `map`/`filter` shape now compile.** `ArrayList<R>()` (the JVM
  `java.util.ArrayList` typealias) lowers to `new System.Collections.Generic.List<R>()`, and the `MutableList`/
  `MutableCollection` mutation members (`add`/`remove`/`clear`/`removeAt`) bind to the BCL `List<T>` methods — so
  `mutableListOf(...).add(x)` etc. work (they previously hit an unsupported-owner gap), and a real-Kotlin
  `Iterable<T>.mapTo(ArrayList()) { … }` iterating + `.add(...)` runs on the BCL list. ilemit's `clrNew` resolves the
  ctor of a `List<R>` whose `R` is the enclosing generic function's type parameter (a `TypeBuilderInstantiation`) via
  `TypeBuilder.GetConstructor`. This unblocks migrating the iteration collection ops (`map`/`filter`/`fold`/…) off the
  LINQ lowering onto real Kotlin source. New verify-il case `mutcoll`.

### Fixed
- **`(P..) -> Unit` lambda shape matches `Action<P..>` for migrated/round-trip generic calls.** `clrMethodShape`
  counted the trailing `Unit` (`(T)->Unit` → `func:2`), but such a type lowers to `Action<T>` (one generic arg, no
  return slot) which ilemit shapes `func:1` — the mismatch made the generic-method shape lookup find 0 candidates
  (`Sequence contains no elements`). Now the trailing `Unit` is dropped from the count. (Surfaced migrating `forEach`.)
- **Injected stdlib top-level functions no longer re-emitted as broken stubs.** A consuming module's FIR holds the
  plugin-injected stdlib ops (restored from DotKt.Stdlib in the synthetic `__GENERATED DECLARATIONS__` file); the BIR
  emitter was emitting them as local top-level methods with no real body (invalid IL — `ReturnMissing` under ilverify).
  Now filtered to origin `DEFINED` (user code only), mirroring the existing filter for injected top-level properties.
- **Generic collection member access (`List<T>`/`MutableList<T>`/`Map<K,V>` indexers + size) inside a generic function.**
  `fun <T> List<T>.first(): T = this[0]` and friends now emit: when the element type is the enclosing generic function's
  own type parameter, `List<T>`/`Dictionary<K,V>` are `TypeBuilderInstantiation`s whose plain reflection `.GetMethod`
  throws (`TypeBuilder generic instantiation does not support resolving members`). ilemit now routes the `listGet`/
  `listSet`/`mapGet`/`mapSet`/`mapSize` member lookups through `TypeBuilder.GetMethod` (the existing `GenericMethod`
  helper). This unblocks compiling real Kotlin stdlib collection extensions to run on the BCL collections DotKt maps
  `kotlin.collections.*` to — the first step of moving random-access collection ops off the hand-written LINQ lowering
  onto real Kotlin source (see `docs/design-stdlib-compilation.md`).

### Changed
- **`String.format` binds directly to .NET `String.Format` — use .NET composite format strings, not Java printf.**
  `"{0:F2}".format(x)` / `String.format("{0:D5}-{1:x}", a, b)` now lower straight to `System.String.Format` with the
  format string passed through verbatim. DotKt no longer reproduces `java.util.Formatter` (the printf→composite
  translation and the `DotKt.Fmt` runtime helper are removed) — `String.format` is JVM-only in Kotlin (Kotlin/Native and
  Kotlin/JS don't have it), so binding it to the CLR's own formatter is the natural CLR-native choice and slims the
  runtime by one type. **Breaking:** a Java printf string like `"%.2f".format(x)` is no longer translated — it is passed
  to `String.Format`, which treats `%.2f` as literal text. Use `"{0:F2}"`, or string interpolation for the common case.

## 0.9.3 — 2026-06-24

Round-trip interop: a DotKt-compiled assembly can now be consumed **as Kotlin** by another
`.ktproj` (the basis for shipping compiled kotlinx-* libraries for the CLR), plus bidirectional
compile-time `ProjectReference` between C# and Kotlin projects.

### Added
- **Reference-type nullability via .NET NRT + platform types.** A reference-type `String?` now rides .NET's own
  nullable-reference metadata (`[Nullable]`/`[NullableContext]`) instead of a bespoke attribute: ilemit stamps
  `[NullableContext(1)]` per type and `[Nullable(2)]` on each nullable reference return/parameter, so a **C# consumer
  also sees** DotKt's `String?` as nullable. facadegen reads NRT uniformly for every assembly, which closes a soundness
  hole — a reference type from any non-DotKt assembly was previously injected as strictly non-null. A reference type from
  an assembly built without `<Nullable>enable</Nullable>` (oblivious) now injects as a Kotlin **platform type** `T!`
  (`ConeFlexibleType(T, T?)`, à la Kotlin/JVM's treatment of un-annotated Java), instead of lying "non-null". The old
  `[KotlinNullable]` attribute is retired. See `docs/dotkt-semantics.md` §9.
- **Round-trip metadata attributes are compiler-embedded per assembly.** The `[Kotlin*]` attributes moved to namespace
  `DotKt.Runtime.CompilerServices` and are now defined as internal types inside each emitted assembly (the csc model for
  its own `NullableAttribute`/`IsReadOnlyAttribute`) rather than referenced from `DotKt.Runtime`. They are metadata-only,
  so this makes each assembly self-contained and removes the "ilemit needs `--ref DotKt.Runtime` to stamp" coupling.
  (`[DotKtNamespaceProjection]` stays a referenced type — it is assembly-level, which PersistedAssemblyBuilder can't
  embed.) `DotKt.Runtime` now carries only executed code plus that one attribute.
- **Consume a DotKt assembly AS KOTLIN — Kotlin-modifier round-trip.** Kotlin-language facts with no native .NET
  representation now survive compilation and are restored on a consuming module's FIR, so a `.ktproj` can use
  another DotKt-compiled assembly with idiomatic Kotlin syntax (the basis for shipping compiled kotlinx-* libraries
  for the CLR). Embedded `DotKt.Runtime.CompilerServices` attributes (`[KotlinFunction(Infix|Operator|Suspend)]`, `[KotlinFileClass]`) are
  stamped onto the IL by ilemit, read back by `facadegen --meta`, and restored by the FIR injector:
  - `infix fun` / `operator fun` — restored as `status { isInfix/isOperator }` (call notation + operator resolution).
  - `suspend fun` — emitted as `Task<T>`; restored as `suspend fun(): T` (the Task is unwrapped and re-awaited by the
    coroutine machinery), for both members and top-level functions.
  - top-level functions — a `<File>Kt` facade carries `[KotlinFileClass]`; its statics restore as top-level package
    functions, called via a new `ClrTopLevelRegistry` as a static call on the file class. **Generic** top-level
    functions are restored with their type parameters and called via `clrGenericStatic`, so a cross-module
    `inline fun <reified T>` is consumed as a generic method (`f<Int>()`) — CLR generics are reified, so no inlining
    or carried body is needed. (The only cross-module inline case that can't degrade — a lambda with a non-local
    `return` — fails with a clean compile error; see docs/design-kotlin-metadata-attributes.md.)
  - `final`/`open`/`abstract`, visibility, and **`reified`** need no attribute — they ride plain .NET metadata (CLR
    generics are reified, so `inline fun <reified T>` is just a generic method).
  - **`inline` (with a lambda) — cross-module non-local `return`.** DotKt inlines at EMIT time (BirEmitter, no JVM
    `FunctionInlining` lowering), so a cross-module inline call to a body-less injected stub can't be inlined — which
    means a non-local `return` through the lambda (the one inline case that can't degrade to a regular call) was a
    compile error. Now: `ilemit` stamps `[KotlinInline(birJson)]` with the function's own BIR body; the injector
    marks it `inline`; and the consumer's `ilemit` reads that body from the referenced assembly and splices it at the
    call site (param + lambda-body substitution), so the lambda's `return` becomes the caller's `return`. Lighter than
    JVM's `@Metadata` (BIR, emit-time, no IR deserializer). Verified by `scripts/verify-roundtrip.sh`.

- **Bidirectional `ProjectReference` (R-1, reverse interop)** — a C# project can now
  `<ProjectReference>`/`<Reference>` a Kotlin `.ktproj` at **compile time** (not just
  reflection-load), so a Visual Studio solution can split code across C# and Kotlin
  projects that reference each other. New build-time tool **`tools/retarget`**
  (Mono.Cecil) repoints the emitted assembly's BCL `TypeRef`s off the single
  `System.Private.CoreLib` onto the real contract assemblies (`Object`/`Task` →
  `System.Runtime`, `List`/`Dictionary` → `System.Collections`, …) — the type→contract
  map is the forward path's machinery in reverse (the ref pack via `MetadataLoadContext`).
  This is pure post-emit metadata surgery, so it sidesteps the Reflection.Emit/MLC
  generic-instantiation limits that sank the two earlier attempts; `List`/`Dictionary`
  and `suspend fun` → `Task<T>` all consume cleanly from C#. New sample
  **`samples/ktproj-bidir`** (cslib.csproj ← klib.ktproj ← app.csproj: forward + reverse
  in one graph) is green in `verify-ktproj.sh`. Default ON; opt out with
  `<KotlinClrRetarget>false</KotlinClrRetarget>` / `<DotKtRetarget>false</DotKtRetarget>`.

### Fixed
- **A closure/local function capturing an enclosing generic type parameter crashed ilemit.** A lambda or local
  function inside a generic function that captured a value whose type involves the enclosing `T` (a `T` value, a
  `(T)->Unit`, a `List<T>`) threw `NotSupportedException: unresolved generic type parameter T` — the synthesized closure
  class / lifted method wasn't generic over `T` (reified CLR generics need it). The closure class is now generic over the
  captured type parameters and instantiated with the enclosing ones at the capture site; a captured local function is
  lifted to a generic static method. (An object expression or local *class* that captures an enclosing type parameter is
  not yet supported and now fails with a clear compile error instead of crashing.)
- **Cross-file / namespaced interface polymorphism crashed ilemit.** A class in a Kotlin `package` implementing an
  interface from another file threw `KeyNotFoundException` during the interface-link pass — `FindMethod` was keyed by the
  TypeBuilder's simple name while `_types` is keyed by the BIR full name. Now keyed consistently.
- **A generic function applying `(T) -> Unit` to a `List<T>` crashed ilemit.** `for (x in xs) f(x)` inside
  `fun <T> each(xs: List<T>, f: (T) -> Unit)` threw `NotSupportedException` (TypeBuilder generic instantiation doesn't
  resolve members) — the `forEach` lowering called `.GetMethod` on `IEnumerable<T>` directly instead of via
  `TypeBuilder.GetMethod`.
- **Assigning a Boolean to a .NET `bool?` property failed the frontend.** facadegen mapped a nullable value type
  `Nullable<X>` to the literal generic `Nullable<X>` (a distinct type) instead of Kotlin's `X?`, so e.g.
  `checkBox.IsChecked = true` reported an assignment type mismatch. `System.Nullable<X>` now maps to `X?`.
- **Kotlin → Kotlin `ProjectReference` round-trip — a library's top-level functions vanished.** A `.ktproj` consuming
  another `.ktproj` as Kotlin got `unresolved reference` on the library's top-level functions (`import mylib.boxed`),
  while classes resolved fine. The MSBuild `ilemit` step built its `--ref` list from `@(ReferenceCopyLocalPaths)`, which
  doesn't contain `DotKt.Runtime` (a compile reference, not copy-local) — so ilemit couldn't resolve the metadata
  attribute types and **silently skipped stamping** `[KotlinFileClass]`/`[KotlinFunction]`. The file facade then looked
  like a plain class to the consumer, which finds top-level functions only on `[KotlinFileClass]`-marked classes. ilemit
  is now passed `DotKt.Runtime` from `@(ReferencePath)` (SDK + in-repo targets). New regression test
  `samples/ktproj-roundtrip` (this Kotlin→Kotlin `ProjectReference` path had no coverage before).
- Renamed the metadata attribute `[KotlinFile]` → **`[KotlinFileClass]`** (clearer: it marks the `<File>Kt` *class* that
  holds a file's top-level declarations). Pre-1.0, no compat shim.
- **Omitting a non-constant default argument is a clean compile error instead of a backend crash.** A default that reads
  the callee's own parameters/receiver (`b: Int = a * 10`, or a data class `copy`'s `x = this.x`) can't be filled by
  inlining it at the call site (`a`/`this` aren't in scope there) — it needs callee-side evaluation (Kotlin/JVM's
  `$default`), not yet implemented on the .NET backend. Such an omission previously crashed ilemit with
  `InvalidProgram`/`NotSupported`; it now reports a source-located error at the omitting call. Detected at the call site,
  not the declaration, so a data class whose `copy` is never arg-omitted still compiles.
- **Kotlin packages are now projected to .NET namespaces** (`package geom; class Vec` → `.NET geom.Vec`, file facade
  `geom.LibKt`). Previously every type was flattened to the **root** namespace — a correctness bug: two classes with
  the same simple name in different packages (e.g. `alpha.Box` + `beta.Box`) both emitted as `.NET Box` and **collided**
  (ilemit crash), and a packaged library couldn't be consumed across an assembly boundary (`import geom.Vec` resolved
  nothing). `BirEmitter` now qualifies top-level classes/interfaces/enums and the file facade with `packageFqName`
  (nested types stay simple-named — their outer carries the namespace; root-package code is unchanged by construction).
  This unblocks consuming a packaged DotKt library via MSBuild, including its top-level functions (`import geom.greet`).
- **Member `suspend fun` returning a user type** crashed ilemit (`AsyncTaskMethodBuilder<T>`/`Task<T>`/`TaskAwaiter<T>`
  are TypeBuilder instantiations whose `GetMethod` throws). A `GenM` helper re-anchors those members via
  `TypeBuilder.GetMethod`, and `EmitClrCall` now substitutes the open return type (`TaskAwaiter`1<!0>`) from the BIR
  `ret` hint so the await temp is typed correctly. Works through both a `suspend fun` and a `runBlocking { … }` lambda.
- **Parameter names** weren't emitted into the IL (ilemit defined methods by type only), so cross-assembly callers
  couldn't use named arguments. ilemit now writes them via `DefineParameter` (the names were always in the BIR).
- **Forward `ProjectReference`/`PackageReference` under the IL backend** — the dev-path
  `msbuild/KotlinClr.targets` never passed copy-local references to `ilemit`, so a
  `.ktproj` consuming a referenced non-BCL .NET type (e.g. a C# project's `Theme.Palette`,
  `Ext.Widget`) crashed at emit on the default IL backend (`ktproj-extlib` was broken).
  ilemit now receives `@(ReferenceCopyLocalPaths)` as `--ref`, matching the packaged SDK.
- **`ProduceReferenceAssembly` for `.ktproj`** — the SDK built its `obj/ref` reference
  assembly from our placeholder `.cs` (which holds no Kotlin types), so a downstream C#
  `<ProjectReference>` bound the empty ref assembly (CS0246). Disabled for `.ktproj` so
  consumers reference the real, retargeted output.

### Added (round-trip interop — consume a DotKt assembly AS KOTLIN)
All identified round-trip gaps resolved; guarded by `scripts/verify-roundtrip.sh` (roundtrip-pkg), each kept verify-il green.
- **Properties** (`val`/`var`/custom getters) — facadegen surfaces public instance fields and non-special `get_`/`set_`
  methods as Kotlin `prop`s; ilemit's `clrPropGet/Set` falls back to a field then a `get_`/`set_` method. This also makes
  **data classes** consumable (property access + already-round-tripping `componentN` operators + `equals`/`toString`).
- **Asymmetric visibility** (`val`, `var ... private set`) — a not-publicly-settable property's backing field is stamped
  `[KotlinReadOnly]`; the consumer restores it read-only (rejecting external writes). Fixes `val x` being exposed writable.
- **Extension functions, extension properties & top-level extension operators** — an extension's `__self` receiver is
  marked and restored as an extension receiver; `operator fun Vec.plus` is usable as `a + b`; `val T.p` round-trips as an
  extension property (BirEmitter emits its `get_/set_(__self)` statics; the backend routes `x.p` to them). Also fixed
  `isBuiltin` defaulting top-level functions to "builtin", which had lowered a restored `Vec + Vec` to a primitive `bin`.
- **vararg** — ilemit stamps `[ParamArray]`, facadegen encodes `vararg:<elem>`, the injector restores `isVararg`; `f(1,2,3)`
  and empty `f()` both work.
- **Default arguments** (constant, trailing) — restored @JvmOverloads-style (one overload per trailing default omitted);
  ilemit stamps `[DefaultParameterValue]` so the omitted args are filled at the call site.
- **Nullable types** — a `[KotlinNullable]` bitmask carries the signature's nullability; the consumer restores `T?`
  (type-level: passing null to a non-null parameter is rejected).
- Named-argument calls also work (ilemit emits parameter names). New metadata attributes: `[KotlinNullable]`, `[KotlinReadOnly]`.
  Remaining known limits (not round-trip blockers): object singletons — see docs/future-work-interop.md §5.
- **Default arguments — omit ANYWHERE (named-middle, reordered), on functions AND constructors.** Previously a restored
  default arg was @JvmOverloads-style (one positional overload per *trailing* default omitted), so a **named middle
  omission** — skip a middle default but provide a later one (`box(1, c = 9)`, `greet("C", punct = "?")`, `Pt(y = 4)`) —
  matched no overload and failed. The restored param now carries a **real constant default**: facadegen encodes the
  value in the metadata token (`opt:Int=2`, spaces escaped), and the injector builds a `FirLiteralExpression` and
  `replaceDefaultValue`s it (fir2ir then inlines the constant for any omitted arg, which `filledArgExprs` fills at the
  call site). Constructor parameter **names** are now emitted too (`DefineParamNames` for ctors), so named-arg ctor calls
  work. A .NET BCL method with a non-constant default (an enum/struct, e.g. `NumberStyles = 7`) keeps the @JvmOverloads
  trailing-overload fallback — the two strategies can't mix on one function (a bare `hasDefaultValue` flag with no literal
  crashes fir2ir). Guarded by `scripts/verify-roundtrip.sh` (roundtrip-defargs).
- **Generic round-trip** — user generics now consume from another `.ktproj` as Kotlin in **every position** and
  **combined with every other restored feature**: a generic user **class** (`class Box<T>`, with `operator`/`infix`
  members and a generic method `fun <R> mapTo(f)`), **two type parameters** (`Holder<A, B>`), generic user types in
  **return** and **parameter** position (`fun <T> wrap(x: T): Box<T>`, `fun <T> unwrap(b: Box<T>): T`), generic
  **extension** functions and **extension operators** on a generic type (`fun <T> Box<T>.twice()`), generic **top-level
  `suspend`** (`echoAsync`), and generics combined with **nullable** / **default-arg** / **vararg**. (Reified generics
  already worked — a generic method with no carried type.) The coordinated fixes:
  - **facadegen** — a root-namespace generic type's open .NET name was `.Box` (a leading dot: `Type.Namespace` is null at
    the root); now `OpenName` omits it. `Supported`/`CrossType` dropped a generic user type appearing in a signature
    (`Box<T>` → `Any?`), so the whole function silently vanished from the metadata; both now keep it (`generic:Box:T`).
  - **ilemit** — a generic type was emitted as `Box` without the CLR ``Box`1`` arity suffix, so a cross-assembly
    `GetType("Box`1")` missed it (same-assembly use resolves through the `_types` registry by BIR name, so it never
    surfaced); the metadata name now carries the arity, the registry key stays bare. A generic **extension** call omitted
    the `__self` receiver's shape (so overload resolution saw 0 params); it's now included. A generic fn with a
    **default arg** supplies fewer shapes than the single .NET method's params — `ResolveGenericMethod` now tolerates the
    trailing optional params and the emit path default-fills them.
  - **injector** — `coneOf` lost the method type variable nested inside a `generic:Box:T` argument (resolved `T` → `Any?`
    with a null owner, so a returned `Box<T>` became `Box<object>` and corrupted the call site); a type-variable resolver
    is now threaded through every recursion. The generic top-level path also ignored the extension receiver / `inline` /
    `infix` / `operator` / `vararg` / default-arg overloads — unified into the one path the ordinary case already used.
  - Guarded by `scripts/verify-roundtrip.sh` (roundtrip-generic). Known limitation (NOT a round-trip regression — it
    fails the same way in a single module): a `suspend` member of a generic class (`class Box<T> { suspend fun f(): T }`)
    is a separate pre-existing coroutine×generics gap, tracked in docs/future-work-interop.md.
- **Higher-order generics — a generic user type nested in a lambda parameter.** A function-type parameter whose argument
  or return is a generic user type (`fun <U,V> apply2(f: (Box<U>) -> Box<V>, …)`) now round-trips, in every position
  (top-level / member / extension / `infix` / `operator` / `inline`). Root cause: the internal metadata **type grammar
  was flat** (`func:<ret>:<args>` / `generic:<Open>:<args>`, colon/comma-delimited), so a `generic:` couldn't nest
  inside a `func:` — facadegen deliberately dropped such a lambda to `Any?`, which erased the type variable and made it
  uninferable at the call site. The grammar is now **recursive (bracketed)**: `generic:Box[V]`, `func:[ret,a,b]` — a
  compound child keeps its own commas, the injector splits at bracket depth 0, and `(Box<U>)->Box<V>` survives as
  `func:[generic:Box[V],generic:Box[U]]`. Guarded by `scripts/verify-roundtrip.sh` (roundtrip-generic-hof).
- **Member-declared extension functions** (`class C { fun T.f() }`) now round-trip — plain, `infix`, `operator`,
  `inline`+generic-method, and `protected` — consumed as Kotlin via `with(c) { x.f() }`. This also fixes a **pre-existing
  single-module bug**: a member extension's two implicit receivers (the dispatch `this` and the extension `__self`, both
  named `<this>` in IR) were name-keyed and got swapped, producing wrong results; they're now substituted by symbol
  identity, and a member-extension call dispatches on the enclosing instance with the extension receiver prepended.
  facadegen stamps `,ext`/`,inline` on the member `fun` line; the injector restores the extension receiver on the member
  path (the `fun`-line parser had also been dropping `,ext`/`,inline`). Guarded by `scripts/verify-roundtrip.sh`
  (roundtrip-memext).
- **Member-declared extension properties** (`class C { val T.p }`, `var` too) now round-trip — public + protected. A new
  `memextprop` metadata line carries the `get_p(__self)`/`set_p(__self, v)` member accessors; the injector restores a
  member property with an extension receiver, and a `x.p` read/write inside `with(c)` routes to C's `get_`/`set_` method
  with the extension receiver prepended.
- **Suspend member extensions** (`class C { suspend fun T.f() }`) — public + protected, consumed via the natural
  `with(c) { x.f() }`. Two general coroutine fixes enable it: (1) a `suspend fun`'s state machine was a top-level type
  and so threw `MethodAccessException` when its body touched a `protected`/`private` member of the owner — the SM is now
  **nested in its owner** (non-generic owners), which can reach those members; (2) a **suspending call inside an inline
  scope function** (`with(x){ f() }`, `run`/`let`/`apply`/`also`) is now **CPS-linearized through the state machine**
  instead of emitting an un-awaited `Task` (was a silent `InvalidProgram`). The scope function's receiver is bound to a
  state-machine field, `this`/`it` is substituted, and the lambda body's suspensions become real await points (handles
  nested scope functions, suspending args, and multi-statement bodies). Guarded by `scripts/verify-roundtrip.sh`
  (roundtrip-memext2). Remaining edge: a scope function used as a **sub-expression** (`c.apply{ f() }.x`) is a clean
  compile error — bind it to a `val` first.
- **Namespace projection** (`[assembly: DotKtNamespaceProjection(kotlinPrefix, dotNetPrefix)]`) — a DotKt library whose
  types live in one .NET namespace (e.g. `DotKt.Coroutines`) can be consumed under a different Kotlin package (e.g.
  `import kotlinx.coroutines.*`). The producer stamps it via `ilemit --ns-projection k=d` (SDK: a `<DotKtNamespaceProjection>`
  item); the consumer's facadegen reverse-projects each import to the real .NET type and the FIR injector forward-projects
  the .NET namespace to the Kotlin package, so types resolve under the imported package while the backend calls the real
  type. Prefix-based (sub-packages follow). The import scanner no longer drops `kotlinx.*` (external libs, not stdlib);
  only `kotlin.*` is filtered. Verified by `scripts/verify-roundtrip.sh` (roundtrip-nsproj).

### Removed
- **C# backend regression suite (`scripts/verify-all.sh`)** — the C# backend was retired
  in 0.x (2026-06-18); regression-testing a backend we no longer ship has no value, and the
  harness had rotted (the generated C#/façade path no longer compiles). The valuable
  MSBuild/.ktproj end-to-end coverage it carried moved to the new **`scripts/verify-ktproj.sh`**,
  which runs those samples on the shipping **IL backend** (and adds `ktproj-bidir`). CI runs
  `verify-il` + `verify-differential` + `verify-ktproj`.

## 0.9.2 — 2026-06-23

Interop/primitive bug fixes, most surfaced building a real WinUI app from Kotlin.

### Fixed
- **Signed `Byte` / `Short`** as parameters, locals, fields, and constant args threw
  `InvalidProgramException` (or crashed ilemit). They were omitted from the primitive
  paths (Int/Long/unsigned were present): `birType` fell to the user-type fallback
  `@Byte`/`@Short`, and ilemit `EmitConst` had no `byte`/`short` case so a `const byte`
  pushed `null`. Kotlin `Byte` = signed `sbyte`, `Short` = `Int16` (UByte stays
  unsigned). Fixes `MemoryStream().WriteByte(65)` too. (`il-bytearg`)
- **Lambda passed to a .NET constructor's delegate parameter** (`new Thread({ … })`)
  crashed ilemit with a `NullReferenceException` (`EmitClrNew`): the façade erases the
  delegate param, so the exact-type ctor lookup found nothing. `EmitClrNew` now selects
  the ctor by arity (preferring delegate-param/lambda-arity matches) and builds the
  specific delegate. (`il-delegatearg`)
- **`for (x in <.NET IEnumerable<T>>)`** over a raw .NET enumerable (not a Kotlin
  collection) failed to compile: `iterator()` was ambiguous (only the clashing stdlib
  extension `iterator()`s applied). facadegen now injects a frontend-only
  `operator fun iterator(): Iterator<T>` for any type implementing `IEnumerable<T>`;
  the backend bypasses it and enumerates via GetEnumerator/MoveNext/Current
  (forEachInline). (`il-netenum`)
- **User class implementing Kotlin `Iterable<T>`** (`class R : Iterable<T>`) crashed
  ilemit (`KeyNotFoundException 'Iterable'`): `Iterator<T>` had a monomorphized
  synthetic interface but `Iterable<T>` did not. Added `KIterable_<elem>`
  (`operator fun iterator(): KIterator_<elem>`), parallel to the existing
  `KIterator_<elem>`; both the `for` loop and explicit `.iterator()` now work. (`il-iterable`)
- **User class implementing/extending a .NET-mapped Kotlin stdlib supertype** crashed
  ilemit (`KeyNotFound`) — the supertype emission didn't route these through their
  .NET mapping. A whole cluster:
  - **Custom exceptions** `class E(msg) : Exception(msg)` / `RuntimeException` -> a CLR
    class `: System.Exception` (ctor chains to `System.Exception(string)`, `.message`/
    `.cause` -> `.Message`/`.InnerException`, catchable by base type). (`il-customexc`)
  - **`Comparator<T>`** -> `IComparer<T>` (`compare` -> `Compare`). (`il-comparator`)
  - **`AutoCloseable`/`Closeable`** -> `IDisposable` (`close` -> `Dispose`).
  Mechanism: supertype base/interface emission now routes through `birType` when it
  maps to a `clr:`/`clrg:` spec; `clrIfaceMemberName` renames the overridden members;
  the `catch` clause types via `birType` (a user exception catches as its own type, not
  `object`); `MapType` resolves bare .NET FQNs. (Comparable<T> as a self-referential
  generic supertype is now handled too — see below.)
- **`use {}`** (Closeable/AutoCloseable) now lowers to `try { block(it) } finally { close()/Dispose() }`
  returning the block value — the CLR analogue of C# `using`. (`il-use`)
- **`Comparable<T>`** (`class V : Comparable<V>`) — the self-referential generic interface
  `IComparable<V>` (V the emitted type) made ilemit call `.GetMethods()` on a
  TypeBuilderInstantiation (throws). Interface-impl linking now enumerates the OPEN
  generic definition and re-anchors each method via `TypeBuilder.GetMethod` (same
  pattern as the self-ref base ctor). `<`/`>`/`<=`/`compareTo`/`sorted()` all work. (`il-comparable`)
- **`class S : CharSequence`** -> a synthetic `<>dotkt_CharSequence` interface (length
  getter + get(i) operator + subSequence); no faithful BCL equivalent exists. (`il-charseq`)
- **`String.substring(start, end)`** used .NET `Substring(start, LENGTH)` directly, but
  Kotlin's `end` is an EXCLUSIVE INDEX -> the 2-arg form now converts `end -> end - start`
  (`"hello".substring(1,4)` = "ell", was "ello"). (`il-substr`)
- **Type-injector metadata** (façade generation), found building a WinUI-on-Kotlin library:
  - Assignability edge no longer dropped for a non-constructible base (WinRT `UIElement`,
    `SafeHandle`): the supertype edge is emitted for is-a regardless of a base no-arg ctor;
    a `basector none` marker suppresses the synthesized `: super()` only. (`il-injbase`)
  - Member signature types now use the FULLY-QUALIFIED name, so a same-simple-name type from
    another namespace (`Microsoft.UI.Xaml.LaunchActivatedEventArgs` vs the UWP one) no longer
    shadows the right one — fixes overrides that "override nothing". (`il-injfqn`)
  - Public **static members of a normal class** (one with instance members too) are now
    injected — they were dropped, so `Application.Start(cb)` / `Application.Current` were
    unresolved. Surfaced on a synthesized companion: facadegen emits `sfun`/`sprop`, the
    injector generates the companion, the backend emits .NET static calls (lambda args bind
    to the .NET delegate). Accessed via `App.Companion.Start(cb)` / `App.Companion.Current`
    (`il-injstatic`). NOTE: the bare `App.Start` form is NOT supported — the current
    compiler doesn't resolve the implicit companion of a plugin-generated class, so the
    `.Companion` qualifier is required (accepted rule).
  - A .NET **FIELD surfaced as a Kotlin property** (facadegen records static/const fields
    and public instance fields as `sprop`) crashed ilemit with a `NullReferenceException`
    (later a 0xC0000005 access-violation via MSBuild) — `clrPropGet`/`clrPropSet` only looked
    up a property accessor. They now fall back to `ldfld`/`ldsfld` / `stfld`/`stsfld` — and a `const`/literal field is
    INLINED (its value pushed, as C# does, since a literal has no storage and can't be
    `ldsfld`'d) — otherwise an actionable "no property OR field" error. Verified via
    `il-injstatic` (`App.Companion.Answer`=99 static readonly; `App.Companion.Magic`=123 const).
  - `ilemit` gained an `ILEMIT_TRACE` env switch that prints each emission step (ref load,
    parents, signatures, bodies, createType, save) flushed to stderr — so a Reflection.Emit
    hard-crash (uncatchable AV, exit 0xC0000005) can be localized to the culprit type/method.
- **Per-file lifted state leaked across files (multi-file)** — one `BirEmitter` instance
  processes every file, but its per-file lifted collections (`liftedMethods`/`liftedTypes`/
  synthesized delegate classes/ref cells/iterator+property+CharSequence+KProperty synthetics)
  were never reset, so each file's BIR ACCUMULATED the prior files' lifted lambdas/types —
  duplicating e.g. `App.kt`'s `__lambda*` into ControlsKt/DslKt/LayoutKt/ReactiveKt. The
  `<>dotkt_*` types are de-duplicated by ilemit, but lifted `__lambdaN` are file-class methods
  that are not, so this was real metadata bloat (and a corruption hazard surfaced building a
  multi-file WinUI app). `emitFile` now resets all per-file lifted state up front. (`il-mflambda`)
- **Overloaded user functions resolved to the wrong method** — ilemit keyed methods by NAME
  only, so `f(String)` and `f(() -> String)` collided in one dictionary: the last-declared
  overwrote, a body was emitted into the wrong overload's `MethodBuilder`, and calls picked
  the wrong target. Manifested as a WinUI crash — the DSL's `text(String)` / `text(() -> String)`
  caused `text(() -> String)` to run `tb.Text = <the Func itself>` (the String overload's body),
  so CsWinRT marshaled a `Func` object as a string (`WindowsCreateStringReference` AV / OOM).
  ilemit now keys methods by name + parameter-type signature (`MethodsBySig`); BirEmitter emits
  that signature on each call (callStatic/callInstance, incl. extension and companion calls) so
  body emission AND call resolution pick the right overload. Covers top-level and member
  overloads, by arity and by parameter type. (`il-overload`)
- **Expression-body function with a Unit-typed body dropped the call** — `IrReturn(<expr>)`
  emitted a bare `{"k":"return"}` when the value's type was `Unit`, discarding the
  expression. So `fun main() = winUiApp { … }` (and `fun f() = sideEffect()`, or an explicit
  `return doCleanup()`) launched/ran NOTHING. A Unit-typed return value is now EVALUATED
  (`exprStmt`) before the bare return; only a plain Unit reference (`return`/`return Unit`)
  stays a bare return. (`il-exprbody`)
- **Unsigned .NET parameter types weren't mapped to Kotlin unsigned types** — facadegen's
  primitive map had `System.Int32→Int` etc. but no `System.UInt32`/`UInt64`/`UInt16`, so a
  `uint` parameter surfaced as the bare name `UInt32`, which doesn't unify with `kotlin.UInt`
  ("argument type mismatch: actual 'UInt', expected 'UInt32'") — hit calling WinUI's
  `Bootstrap.Initialize(uint majorMinorVersion)`. Added `UInt32→UInt`, `UInt64→ULong`,
  `UInt16→UShort`, `SByte→Byte`. (`il-injuint`)
- **Synthetic type names collided across files in a multi-file assembly** — every file's
  `BirEmitter` used a fresh counter, so `<>dotkt_Closure0…`, `<>dotkt_Ref_<elem>`, and
  `<>dotkt_Seq…` repeated across files. Linking all BIR into one assembly overwrote them in
  ilemit's `_types`, orphaning a `TypeBuilder` that was never `CreateType()`'d →
  `NotSupportedException` ("not supported before the type is created") at `Save`, or a
  `0xC0000005` via MSBuild. (Single-file samples never hit it.) BirEmitter now prefixes these
  per-file-DISTINCT synthetics with the file class (`<>dotkt_<FileKt>_Closure0`); ilemit
  de-dups per-file-IDENTICAL shared synthetics (`<>dotkt_Result`/`KProperty`/`KIterator_*`/…)
  by name; and `Ordered()`/a pre-Save sweep make every defined TypeBuilder get created.
  (`il-mfclosure` — two files, capturing closures + ref cells.) Found building a WinUI app
  whose `.ktproj` source-includes the whole library.

## 0.9.1 — 2026-06-23

Language/stdlib long-tail completion + a type-emission correctness refactor. The
direct-IL backend, coroutine surface, generics, and forward interop were already
complete in 0.9.0; this release closes the remaining A (language) / B (stdlib) gaps
so the A/B checklists in `docs/remaining-tasks.md` have **zero** open items.

### Added
- **Regex `matches` / `find`** — full-input match + `MatchResult?` (via `DotKt.Text.Regexes`
  shims), `MatchResult.value` → `Match.Value`. (`il-regex`)
- **`return` as an expression** — `val x = if (c) a else return b` (new `returnExpr`
  lowering, `tryStack`-aware). (`il-langtail`)
- **enum per-entry bodies** — `enum class Op { PLUS { override fun apply(…)=… }; abstract
  fun apply(…) }`: the base enum becomes abstract and each body entry is emitted as a
  subclass `<>Enum_NAME : Enum`. (`il-enumbody`)
- **Field-level visibility** — a property's visibility is honored on its backing field:
  `private` → true `FieldAttributes.Private`, `internal` → `Assembly`, `protected` →
  `FamORAssem`. (`il-fieldvis`)

### Changed
- **Inner / nested classes are now emitted as true CLR nested types** (`Outer+Inner`)
  instead of being flattened to separate top-level types. Nested types retain Kotlin's
  legal access to the enclosing type's `private` members, which is what makes true
  `private` field visibility correct. `inner` classes still capture `__outer`.

### Fixed
- **Compound-condition smart-cast** — `if (x is Int && x > 10)` no longer mis-takes the
  then-branch (the `>` operand stayed boxed as `Any`); `bin` now coerces a boxed operand
  to the other operand's primitive type, and `IrGetValue` honors a narrowed smart-cast.

### Notes
- Verified working & locked by samples this release: `lateinit` (uninitialized read
  throws), `field` in custom accessors, `when`+type smart-cast.
- Full IL suite green + JVM differential ALL MATCH + ilverify-clean.
- Known residue (unchanged, tracked in `docs/remaining-tasks.md` §F / §R): packaged-SDK
  end-to-end consumption still has MSBuild SDK-resolution plumbing to finish (F-308);
  reverse-interop cosmetic naming/`[Nullable]` is gated behind R-1.

## 0.9.0

Initial pre-1.0 line: direct-IL backend (C# codegen retired), CLR-native coroutines
(`suspend` ⇔ `Task<T>` / `IAsyncEnumerable`), user generics, forward .NET interop
(import-driven, façade-free), and the 3-package distribution (Sdk / Toolchain / Runtime
+ Templates).

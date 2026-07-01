# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Changed
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

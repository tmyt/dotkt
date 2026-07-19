# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Changed

- **bir2cir/ilemit ([tmyt/dotkt#48], area:bir2cir, area:ilemit): the legacy string-token type grammar is DELETED —
  structured `TypeNode` only, matching the frozen #37 schema; no dual-protocol.** The wire was already structured
  (`docs/bir-cir.schema.json` `$defs/type`), but the CODE still PARSED/EMITTED the retired `clr:` / `clrg:Name[..]` /
  `@Name` / primitive-shorthand / `func:`/`sfunc:` / `nullable:`/`array:`/`byref:`/`gp:` grammar. **S4 — owner islands →
  `TypeNode`:** every bir2cir-side owner slot (`ownerType`/`accessOwner`/`clrOverride`/clr\* `type`, and
  SuspendColdLowering's coroutine owners) is now a structured `{t:fqn}` node; the applied-attribute owner is a bare FQN
  + an `attrExternal` bool (`AttrExternalNormalize` strips kotc's `clr:`-imported prefix), retiring ilemit's
  `attr.StartsWith("clr:")` branches; `scripts/verify-schema.py` `STR_OK` shrinks (`ownerType`/`clrOverride`/
  `accessOwner`/`recv0` dropped) so any regression reds the gate. **S5 — sig-token island → one structural comparator:**
  ilemit's overload resolution no longer renders a `TypeNode` to a legacy token string and re-parses it — `SigTokenOf`/
  `SigTokenMatches`/`SigTokenMatchesOpen`/`SkipTypeToken`/`FuncType(string)`/`FuncRetEnd`/`NormalizeGpNames`/
  `FindByNormalizedSig`/`StripSigPrefixes` are replaced by a structural `Matches(TypeNode, System.Type)` and the `sig`
  parameter threads as `TypeNode[]` (the `MethodsBySig` dictionary keeps a `SigCanon` hash key — an internal encoding,
  never a wire spelling). ilemit's `MapType(string)`/`ClrRef(string)`/`NativeType(string)`/`ParseOwner` prefix parsers
  and bir2cir's `LowerTypeString` grammar-construction are deleted; bir2cir emits bare BCL FQNs. Gate: full
  `gate.sh --full` GREEN (il/schema/sanity/ktproj/roundtrip/differential-all-MATCH/widedelegates).

### Fixed

- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir, area:ilemit): CLOSED — ilemit re-resolves NOTHING on any clr*
  member axis (W1-S5 finishes the arc).** W1-S5 carries the `newBoundClrDelegate` target (Site 3 — a bound
  `netObj::method` reference) via a new `ResolveBoundClrDelegate` + `memberSig`, and deletes ilemit's LAST name-only
  first-pick (`Emitter.Expressions.cs`'s `type.GetMethod(name, argTypes) ?? type.GetMethod(name)` → the consume-only
  `LinkClrMethod`). After the full arc — S1 (#44, generic calls) → S2 (plain calls / ctors / dispatch) → S3
  (properties / fields / events) → S4 (override base-slot) → S5 (bound delegate) — ilemit's only remaining
  `GetMethod`/`ResolveMethod` are fixed BCL gets, the by-design Site-2 `callInstance` linker for MLC-unresolvable
  local-emitted owners (instrumented: 0 arbitrary-overload / dynamic-escape firings), and the deterministic
  `InterfaceMethodOn` single-abstract-method SAM lookup — none is an overload/arity/name first-pick. `MATCH 188 / DIFF 0`.
- **bir2cir/ilemit ([tmyt/dotkt#46], [tmyt/dotkt#183], area:bir2cir, area:ilemit): W1-S4 — declaration-side override
  base-slot memberRef carry.** A method DECLARATION overriding a .NET base-CLASS virtual (a property accessor such as
  `override val message` -> System.Exception.get_Message; the coroutine SM `create`/`invokeSuspend` overrides of
  `BaseContinuationImpl`) carries `clrOverride` (the base owner FQN). bir2cir (`ClrMemberResolution.OverrideBase.cs`,
  a partial of the S2 pass, running last on the fully-lowered tree) now resolves the EXACT base virtual off the ref.dll
  (MetadataLoadContext) and stamps its DECLARED param signature as `clrOverrideSig` (positional-`tv` for a generic
  base). ilemit's `LinkOverrideBase` (`Emitter.ClrInterop.cs`) links the UNIQUE base slot (0 = hard ABI error, >1 =
  malformed) and `DefineMethodOverride`s it — deleting the former `baseT.GetMethod(name, ps) ?? baseT.GetMethod(name)`
  NAME-ONLY first-pick fallback (`Emitter.Assembly.cs`). The match is STRUCTURAL identity (an override's params ARE the
  base slot's), NOT call-side applicability — so `BaseContinuationImpl.create(Any,Cont)` and `create(Any[],Cont)` stay
  distinguishable (a scalar arg no longer matches the array param via the object-downcast rule), with Kotlin `Any` ==
  `System.Object` as the only leaf normalization. Gated by `il-overridemsg`/`il-supercall`/`il-superobj`/`il-supernet`
  + every coroutine case (`il-corestrict`, `il-seqforin`, `il-inlsuspend*`, …). The `callInstance` `ResolveMethod` site
  (#183 Site 2) stays a LINKER consuming kotc's FIR-resolved `sig` (the empirical arbitrary-overload first-pick and the
  BCL-interface dynamic escape both fire 0× across the stdlib self-build + all app cases); its owners are either
  local-emitted (MLC-unresolvable by definition — the S2 local-`new` SelectCtor residual parallel) or referenced
  kotlin.* slots already structurally linked by the carried sig. #46 #183
- **ref-common ([tmyt/dotkt#51], area:packaging): reference-asset selection uses the TARGET RID, not the host RID,
  and consults the real .NET/NuGet portable RID fallback graph instead of a hand-rolled family table.**
  `ManagedReferenceCatalog.Create` gained `targetRid` / `ridGraphPath` parameters: `SelectRuntimeAsset` ranks
  `runtimes/<rid>/lib` assets against the TARGET RID's fallback chain (the transitive `#import` closure of
  `PortableRuntimeIdentifierGraph.json`, expanded breadth-first exactly like NuGet's `RuntimeGraph.ExpandRuntime`),
  so cross-target compilation (e.g. Linux host → `win-x64`) now selects the correct RID-impl asset instead of the
  host's — previously a Linux build targeting Windows picked the RID-neutral PlatformNotSupported placeholder and the
  special-RID fallback was input-order dependent. The graph path is MSBuild's `$(RuntimeIdentifierGraphPath)` when
  passed, else auto-discovered from the running SDK; `targetRid` defaults to the host RID when unset (correct for a
  host-targeted or direct-script run), so existing host-target builds are unchanged. The hand-rolled family chain
  survives only as a last resort when no portable graph is found. Wiring MSBuild's `$(RuntimeIdentifier)` /
  `$(RuntimeIdentifierGraphPath)` through `ilemit --runtime-refs` into `Create` is a follow-up (ilemit + targets). #51

- **bir2cir/ilemit ([tmyt/dotkt#46], [tmyt/dotkt#121], area:bir2cir): property / field / event memberRef carry —
  ilemit is a pure linker for every clr* member-ACCESS axis: calls, ctors, properties, fields, events, dispatch
  (W1-S3, closes #121). #46 stays OPEN for W1-S4 — two ilemit resolution sites remain (NOT in #121's enumerated
  use-sites): the declaration-side override base-slot link (`Emitter.Assembly.cs`, still `GetMethod(name, ps)` + a
  name-only fallback for every method override incl. ToString/Equals) and the `@Clr`/`@ClrIntrinsicAsDynamic`
  `callInstance` resolution (`Emitter.Expressions.cs`).** The remaining un-carried axes
  followed the S2 plan: bir2cir (`ClrMemberResolution.PropFieldEvent.cs`, a partial of the S2 pass, running last)
  now resolves `clrPropGet`/`clrPropSet`, `clrEventAdd`/`clrEventRemove`, and an external `field`/`setFieldExpr`/
  `setField` against the ref.dll (MetadataLoadContext), stamping a `member` discriminator (`accessor`|`field`), the
  resolved accessor NAME, `memberSig`, and `dispatch`. ilemit consumes them via the shared `LinkClrMethod` +
  `EmitClrDispatch` — no property-vs-`get_`-method-vs-field reclassify, no external-field→accessor reinterpret, no
  unchecked `GetEvent` (a missing event is now a hard ABI error, hardening #113), and no `call`/`callvirt`/
  `constrained` derivation from the reflected accessor. A generic base-interface accessor (`IReadOnlyCollection<T>.
  get_Count` on `IReadOnlyList<T>`) retargets the owner to the constructed base interface (the resolved twin of the
  deleted `PropAccessor`'s `SubstituteIfaceArgs` re-anchor). Deleted `PropAccessor`, `ExternalPropAccessor`,
  `EmitInstanceCall`, `PropList` (the KIND-derivation / first-pick helpers). A LOCAL emitted owner (ref.dll returns
  null) keeps its direct backing-field access. Gated by `il-extprop`/`il-vtprop`/`il-event`/`il-eventext`/
  `il-ifaceevent` + `roundtrip-property-type`. #46 #121

- **bir2cir ([tmyt/dotkt#157], area:bir2cir): general cross-module top-level `val` accessor resolution; delete
  the `COROUTINE_SUSPENDED` band-aid.** A cross-module top-level `val` read is kotc-emitted (post-#89) as
  `callStatic owner:null … prop:get`; bir2cir already reconstructs the `get_<name>` accessor and resolves it via
  `TryResolveTopLevelStatic` — and the ref-scan already indexes property accessors (`get_X`/`set_X` — file-class
  statics with `intrinsic==null`, no `IsSpecialName` exclusion). The prior `COROUTINE_SUSPENDED`-specific
  owner-rebind in `MemberCallSubstitution` was therefore redundant (post-#89 the "already-owner'd" shape it also
  covered no longer occurs — both reads now arrive owner:null); removed it (no-band-aid rule), so every
  cross-module top-level val resolves through the ONE general path. Byte-identical CIR before/after on
  `il-suspendintrinsicowned`; the non-coroutine sibling of the same path is gated by `il-extprop` (extension-property
  getters). NB the facadegen-consumed top-level val is a distinct owner-ful `staticField` shape (gated separately by
  `roundtrip-toplevel-val`), and the owner:null path is klib-package-fragment-only. #157

- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir): plain-call / ctor / dispatch memberRef carry — ilemit purified
  to a linker (W1-S2; the generic-call dual S1 was #44).** bir2cir now resolves `clrStatic`/`clrInstance`/`newClr`
  against the ref.dll (MetadataLoadContext), structurally matches the winning member, and carries its declared param
  signature as `memberSig` (+ `dispatch` for `clrInstance`); ilemit consumes it purely as a linker (structural match
  to exactly one handle — 0 = hard ABI error, >1 = malformed, no first-pick). Deletes ilemit's
  PickCtorByAssignable / PickClrCtor / ParamAcceptsArg / ResolveInheritedIfaceMethod / the EmitClrCall resolution
  cascade / the implicit EmitDynamicCall downgrade / dispatch derivation (new pass `ClrMemberResolution.cs`); an
  interface owner with no matching member becomes an explicit `clrDynInstance`, not a silent downgrade. #24
  override-dispatch preserved (`MATCH 203 / DIFF 0`). Remaining for W1-S3 (so #46 stays open): properties / fields /
  events + the declaration side (`clrPropGet`/`clrPropSet` still route through `EmitInstanceCall`); the local-`new`
  `SelectCtor` + referenced `kotlin.*`-helper arity-probe axes are MLC-unresolvable and stay by design.
- **bir2cir ([tmyt/dotkt#153], area:bir2cir): primitive-array-receiver top-level stdlib extensions resolve at app
  level.** `intArrayOf(1,2).toList()` failed with ilemit `static method not found` — `RecvKeyOf` keyed the
  primitive-array Fqn as `kotlin.IntArray` while the ref side collapses `int[]` to `[]`, so owner attribution missed.
  A shared `RecvKeyOfFqn` now maps every specialized-array Fqn (signed + unsigned) to `[]`; because that key is lossy
  (generic `Array<T>` + all primitives share it), a fine first-param `ParamKey` narrows the overload so generic
  `Array<out T>.toList` no longer erases the element to `object` and `ubyteArrayOf(..).toList()` binds the
  instantiated helper. Auto-recovers the app path for #97 (primitive `copyInto`) and #128 (`copyOf(newSize)`). Gate:
  `il-intarraytolist`.
- **packaging ([tmyt/dotkt#133], area:packaging): the MPP SDK (`DotKt.Sdk.Mpp`) builds out of the box — a new
  `dotkt-mpp` `dotnet new` template.** `Sdk="DotKt.Sdk.Mpp"` needs a `global.json` pinning both `DotKt.Sdk.Mpp`
  and the nested `DotKt.Sdk` (the NuGet resolver reads a nested SDK's version *only* from `global.json`), and
  nothing scaffolded it. The `dotkt-mpp` template now ships that `global.json` (both pins substituted to the
  release version at pack) alongside a common `expect` / CLR `actual` sample, so `dotnet new dotkt-mpp && dotnet
  run` works with no hand-written boilerplate. New gate case: `verify-packaged-sdk.sh` `mpp-template`.
- **packaging ([tmyt/dotkt#134], area:packaging): the DotKt build back-half is now incremental — a no-op build
  skips it.** `DotKtBir2Cir`/`DotKtIlEmit`/`DotKtRetarget` had no `Inputs`/`Outputs`, so every `dotnet build`
  re-lowered, re-emitted and re-retargeted, rewriting the output dll's timestamp and forcing every downstream C#
  `ProjectReference` to rebuild. Each target now keys `Inputs`/`Outputs` off a stable `.stamp` (the compile's
  `$(DotKtOut)/.stamp` cascades through `$(DotKtCirOut)/.stamp` to the emitted dll and a retarget stamp), and the
  `_DotKtPlaceholder.cs` write became `WriteOnlyWhenDifferent` (it was bumping its mtime every build and forcing
  `CoreCompile` to recompile). A no-op build now converges.
- **packaging ([tmyt/dotkt#135], area:packaging): the Windows compiler launcher is selected by OS.**
  `$(DotKtCompiler)` was hardcoded to the extension-less UNIX `kotc` script, leaving Windows to rely on cmd.exe's
  PATHEXT resolving a pathed extension-less command. It now selects the shipped `kotc.bat` when `$(OS)` is
  `Windows_NT` (both launchers ride in the package from the Gradle `installDist`), falling back to `kotc` elsewhere.
- **packaging ([tmyt/dotkt#151], area:packaging): corrected the `DotKt.Sdk` `Sdk.props` guard comment.** It said
  the pack guard compares the `DotKtVersion` default to `DotKtVersionPrefix`; it actually compares to the version
  CORE (prefix, plus `-suffix` when pre-release, e.g. `0.9.6-rc7`). Following the old comment during an RC would
  trip the (fail-safe) guard.

- **stdlib ([tmyt/dotkt#104], area:stdlib): `Regex.findAll`/`splitToSequence` and the `Regex.options` getter no longer
  throw `NotImplementedError`.** All three shipped as `TODO()` runtime stubs. Now implemented in pure Kotlin over the
  existing bindings: `findAll` = `generateSequence` over `find()`/`MatchResult.next()` (every non-overlapping match,
  left-to-right, `startIndex`-honored, via ordinary `Sequence` machinery — no coroutine `sequence{}` builder needed);
  `splitToSequence` = `split(input, limit).asSequence()`; `options` decodes the compiled `System...RegexOptions`
  `[Flags]` bitmask (`IgnoreCase`/`Multiline`/`Singleline`/`IgnorePatternWhitespace` → the matching `RegexOption`;
  `LITERAL`/`UNIX_LINES`/`CANON_EQ` have no .NET bit). Gate: `il-regexseq`.
- **facadegen ([tmyt/dotkt#132], area:facadegen): interface-companion statics survive the round-trip.** kotc flattens an
  interface's plain `companion object` to the interface's OWN static fields/methods (the `SharingStarted.Eagerly` #83
  path), but facadegen's interface branch enumerated only `Public|Instance` members and dropped every flattened static —
  so a consumer re-importing the DotKt library could not resolve `I.X`/`I.f()`. facadegen now surfaces an interface's
  `Public|Static` fields/props/methods/events as companion members (`staticProps`/`staticFuns`/`staticEvents`), reached
  via `I.Companion`; a C#11 static-abstract/static-virtual interface member (invokable only through a constrained type
  parameter) is excluded so no uncallable companion slot is advertised. Gate: `roundtrip-iface-companion`.
- **facadegen ([tmyt/dotkt#146], area:facadegen): `KotlinFun()` no longer silently demotes infix/operator/suspend.** The
  blanket `catch` around the `[KotlinFunction]` read erased a method's Kotlin vocabulary whenever an UNRELATED user
  attribute referenced a type outside the resolver set (materializing one attribute forces the whole set). The read is
  now guarded per-attribute (a bad sibling never blocks `[KotlinFunction]`); a genuine enumeration failure on an
  already-DotKt-classified assembly is surfaced LOUD instead of swallowed. The unconditional `if (name=="compareTo")
  op=true` hack — which force-flagged ANY method named `compareTo` and masked a genuinely-missing operator flag — is
  removed; kotc stamps the real `isOperator` (inherited by keyword-less overrides). Gate: `roundtrip-operator-flag`.
- **facadegen ([tmyt/dotkt#179], area:facadegen): a re-consumed `class C : Comparable<C>` regains its Kotlin operator
  surface.** At lib emit a Kotlin `class C : Comparable<C>` lowers `compareTo` to the PascalCase
  `System.IComparable<C>.CompareTo` slot and its supertype to `System.IComparable<C>` (+ a non-generic bridge), so on
  re-import facadegen surfaced neither the lowercase `operator fun compareTo` nor the `Comparable<C>` supertype — a
  consumer's `c1 < c2` / `sorted()` was unresolved. facadegen now (a) restores the `System.IComparable<X>` supertype as
  fully-qualified `kotlin.Comparable<X>` (dropping the non-generic bridge) so the type is seen as `Comparable` and
  `sorted()`'s constraint holds, and (b) renames the DotKt `IComparable<X>`-self-slot `CompareTo` to the lowercase
  `compareTo` + forces the `operator` flag so the FRONTEND resolves `<`/`>`/`<=`/`>=`. A genuine .NET `IComparable`
  keeps its verbatim PascalCase `CompareTo` (`il-icmparity`). The residual bir2cir call-binding half — `NetInteropBinding`
  rebinds the Kotlin `compareTo` call to the DotKt owner's PascalCase `CompareTo` slot when the owner implements generic
  `IComparable<T>` — landed too, so the end-to-end `<`/`sorted()` run passes. Gates: `roundtrip-comparable-meta` (surface)
  + `roundtrip-comparable` (end-to-end; its RT_XFAIL pruned). #179 fully closed.
- **bir2cir ([tmyt/dotkt#178], area:bir2cir): `Regex(pattern, Set<RegexOption>)` / `Regex(pattern, RegexOption)` ctors
  work.** The options-taking Regex constructors threw `InvalidProgramException` — the `Set<RegexOption>`/`RegexOption`
  → `System...RegexOptions` ctor-arg conversion was unwired. `NetInteropBinding.Reshape` now synthesizes the
  `RegexOptions` `[Flags]` bitmask (`IGNORE_CASE`→1, `MULTILINE`→2, `DOT_MATCHES_ALL`→16, `COMMENTS`→32; the three
  no-.NET-bit options drop to 0) at the `newClr` site and retypes the arg so `ClrMemberResolution` binds the BCL
  `Regex(String, RegexOptions)`. Gate: `il-regexopts`. Encode-side deviation recorded in `docs/dotkt-semantics.md` §5b-quater.
- **bir2cir ([tmyt/dotkt#180], area:bir2cir): direct/mixed nullable `Double?`/`Float?` `==` is verifiable IL.** The
  `ieee754equals` arm lowered a nullable-float `==` to a raw `Ceq` over `Nullable<T>` structs (unverifiable IL /
  `InvalidProgram`, latent). It now emits null-safe shaping (`null==null`→true, one-null→false, both-present→IEEE `==`
  on the values) — direct `==` stays IEEE per #95 (`-0.0 == 0.0` true, `NaN == NaN` false), distinct from #152's
  structural bit-equality. Nullness is read from `StaticType.Surface` (so an explicit `x as Double?` is caught too).
  Gate: `il-floateqnull`. Follow-up: #181 (safe-call `obj?.d == y` operand, same class).
- **bir2cir ([tmyt/dotkt#152], area:bir2cir): nullable `Double?`/`Float?` structural equality uses total-order
  bit-equality, not boxed `Double.Equals`.** A data-class / structural `==` over a `Double?`/`Float?` field fell
  through to a boxed `System.Double.Equals` (IEEE: `-0.0 == 0.0` true), violating the total-order equals/hashCode
  contract #95 adopted for the non-null case. The `EQEQ` lowering now, before the `objEq` fallback, emits null-safe
  bit-equality (`clrDoubleEquals`/`clrFloatEquals`) so `D(-0.0) != D(0.0)`, `D(NaN) == D(NaN)`, and hashSet
  membership is consistent; direct operator `==` stays IEEE per #95. Gate: `il-structfloateqnull`. Follow-up: #180
  (direct/mixed nullable `ieee754equals`).
- **kotc ([tmyt/dotkt#177], area:kotc): a `companion object` extension fun passes its extension receiver.** A
  `fun Receiver.ext()` declared inside a `companion object` lowered to a static with a leading `__self` param, but the
  call site emitted only the regular args — dropping the receiver → an arity miscompile. The companion-extension emit
  now prepends the extension receiver as the first arg (consistent with member/top-level extension emit). Gate:
  `il-companionext`.

- **stdlib ([tmyt/dotkt#141], area:stdlib): `hypot`/`expm1`/`ln1p` (Double & Float) bind the numerically-correct
  net10 BCL primitives.** The old bodies (`sqrt(x*x+y*y)`, `exp(x)-1`, `ln(1+x)`) overflowed for large magnitudes
  (`hypot(1e308,1e308)` → `Infinity`) and lost all precision to cancellation near 0. Now bound as `@ClrIntrinsic`
  to `System.Double.Hypot`/`ExpM1`/`LogP1` and `System.Single.Hypot`/`ExpM1`/`LogP1`. Gate: `il-mathnumerics`.
- **stdlib ([tmyt/dotkt#143], area:stdlib): `decodeToString`/`encodeToByteArray` honor `throwOnInvalidSequence=true`.**
  The 3-arg overloads previously ignored the flag and silently substituted U+FFFD. They now transcode through a
  throwing `UTF8Encoding(false, true)` and surface a `CharacterCodingException` (Kotlin contract) on malformed
  UTF-8 / unpaired surrogates; the default (`false`) path keeps replacement. Gate: `il-utf8throw`.
- **stdlib ([tmyt/dotkt#144], area:stdlib): `String`/`Char` `uppercase()`/`lowercase()` documented as CLR-native
  1:1 mapping — NOT a JVM one-to-many bug.** `#144` was re-triaged (not a defect): kotlin/clr has no binary interop
  with other Kotlin backends, so string-value parity (`"ß".uppercase() == "SS"`) has no functional value, and .NET's
  deliberate 1:1 no-expansion (`ToUpperInvariant`/`ToLowerInvariant`) is a valid platform choice. The public forms
  bind directly to `System.String.ToUpperInvariant`/`ToLowerInvariant` (`@ClrIntrinsic`); `"ß".uppercase() == "ß"`.
  The deliberate deviation from Kotlin/JVM/Native/JS one-to-many expansion is recorded in `docs/dotkt-semantics.md`
  §5g. Gate: `il-caseinvariant`.
- **stdlib ([tmyt/dotkt#145], area:stdlib): array `fill(element, fromIndex, toIndex)` validates its range.**
  A `fromIndex > toIndex` call silently no-op'd; the generic + all 8 primitive `fill` actuals now throw
  `IllegalArgumentException` on an inverted range and `IndexOutOfBoundsException` out of bounds (Kotlin contract).
  Gate: `il-fillrange` (generic path; the primitive actuals carry the identical guard but remain blocked from app
  calls by the pre-existing primitive-array-receiver resolution gap).
- **stdlib ([tmyt/dotkt#129]/[tmyt/dotkt#130]/[tmyt/dotkt#142], area:stdlib): concurrency-correctness in the atomics + coroutine primitives.**
  Three memory-model/locking defects fixed stdlib-side (CLR-native Interlocked/Volatile/Monitor bindings, no compiler
  special-casing). **#129**: the `AtomicIntArray`/`AtomicLongArray`/`AtomicArray` element ops did a bare
  `monitorEnter/…/monitorExit` around `array[index]`, whose bounds check throws mid-critical-section and leaked the
  monitor (a reentrant lock the throwing thread never notices but every OTHER thread on that instance deadlocks on);
  each section is now wrapped in `try { … } finally { monitorExit(lock) }`. **#130**: scalar `AtomicInt`/`AtomicLong`/
  `AtomicBoolean`/`AtomicReference` `load()`/`store()` were plain field access outside the memory model. The lock-free
  scalars (`AtomicInt`/`AtomicLong`) now bind `System.Threading.Volatile.Read/Write(ref …)` (byref, ordered and
  non-tearing for `long` on every platform); the monitor-backed `AtomicBoolean`/`AtomicReference` keep a `@Volatile`
  field for the unlocked acquire `load()` but route `store()` through the SAME monitor as their RMW ops — a lock-free
  store would slip inside the monitor's read-modify-write gap and be lost (non-linearizable). `toString()` now reads
  via `load()` so `AtomicLong` cannot tear. Separately, the `AtomicIntArray`/`AtomicLongArray`/`AtomicArray`
  array-adopting constructors now defensively `copyOf()` the argument (per the expect KDoc; aliasing left an
  unsynchronized side door into the monitor-guarded storage). **#142**:
  `SafeContinuation`'s `UNDECIDED→result` / `UNDECIDED→COROUTINE_SUSPENDED` state transition was a non-atomic
  check-then-store that races under a multithreaded dispatcher; it is now a lock-free CAS loop over a `@Volatile`
  field via `Interlocked.CompareExchange(ref object,…)`, faithful to the JVM `AtomicReferenceFieldUpdater` version.
  New gate cases `il-atomicarraytry` (cross-thread lock-release), `il-volatileatomic` (volatile round-trip), and
  `il-safecontresume` (async cross-thread `suspendCoroutine` resume).
### Changed

- **gates/tests (area:gates): NUnit migration foundation + first family (generics battery) migrated off the
  per-case bash gate.** Stood up the production in-process NUnit suite (`docs/design-nunit-test-harness.md`,
  playbook `docs/nunit-migration-playbook.md`): `tests/il/DotKt.Tests.Il.ktproj` resolves the LOCALLY-BUILT
  DotKt SDK from `build/nuget-feed` (`make pack`) via an active `tests/nuget.config` (isolated
  `globalPackagesFolder`, package-source mapping `DotKt.*`→local feed) — so the suite tests the compiler in the
  working tree, not a published nuget. `tests/run-nunit-il.sh` drives it and enforces a **discovered-count
  guard** (asserts `dotnet test` discovered exactly the expected number of methods — a dropped/added method or a
  0-test discovery failure reddens the gate) plus once-per-assembly ilverify. Migrated `cases/il-generic ..
  il-generic6` (the G-1..G-6 progressive-milestone cases the cases-test-design audit condemns as 6 permanent
  compiler processes) → one `GenericsTests` fixture, 6 `@TestAttribute` methods asserting the SAME values via
  `assertEquals` (aliased from `ClassicAssert.Companion.AreEqual`); the 6 case dirs, their `verify-il.sh`
  `il_check` lines, and their `verify-differential.sh` `PURE` entries were deleted in the SAME change (audit
  必須是正条件 #14). `dotnet test` runs the battery in ~17 s clean / ~3.6 s warm against the local SDK.

- **docs/process (area:semantics): the behavior-choice acceptance test is now stated as "consistent, documented,
  convincingly explainable"** (CLAUDE.md Design doctrine + `docs/dotkt-semantics.md` guiding principle): ① Kotlin
  contract by default, ② CLR-native where unspecified, ③ *interop-first deviation* may override even the KDoc letter
  when CLR/mscorlib consistency convincingly wins. Recorded the #144 case-mapping deviation as `docs/dotkt-semantics.md`
  §5b-ter (`"ß".uppercase()` stays `"ß"`, no Unicode one-to-many expansion — previously only a `CharClr.kt` comment).
  A new PostToolUse hook (`scripts/hooks/check-jvm-emulation.sh`, wired in `.claude/settings.json`) auto-injects this
  self-check whenever newly-written toolchain/stdlib text pattern-matches JVM-emulation intent ("matches JVM",
  "JVM parity", the hashCode 31-polynomial), so agents re-verify the judgment at write time instead of after review.

- **gates ([tmyt/dotkt#107]/[tmyt/dotkt#108]/[tmyt/dotkt#99]/[tmyt/dotkt#109], area:gates): hardened the verification harness.**
  `verify-il.sh` now (#107) FAILS LOUD when the ilverify lane cannot run (ILVerify.dll absent / runtime ref dir
  missing) instead of silently reporting green with zero IL coverage and printing spurious `FIXED` for every real
  XFAIL; (#108) wraps every per-sample run in a `timeout` (default 60s, `DOTKT_RUN_TIMEOUT`) so a coroutine
  resume/pulse-drop deadlock surfaces as a distinct `run timeout` FAIL instead of wedging the whole gate; and (#99)
  DERIVES the ilverify assembly set from the run set (each sample records its emitted assembly name) rather than a
  hand-maintained map that had drifted — closing the 78+ run-only-sample formal-coverage gap permanently, with a
  single explicit `ILVERIFY_EXCLUDE` (stackalloc's by-design-unverifiable `localloc`) printed loudly, no silent gaps.
  This exposed six pre-existing formal-only ilverify findings (all RUN-green, runtime-safe): `boxgen` (#62/#46
  compare-SAM boxing), `classdeleg` ([tmyt/dotkt#174], new — class-delegation forwarder narrows the MutableList
  iterator return), `copyofnull` (#127/#86 nullable-value-type array object-erasure), and `defargs`/`delegnull`/
  `linkedorder` (#170/#150 DelegateCtor) — each XFAIL_ILVERIFY-listed with a concrete reason. `verify-roundtrip.sh`
  adds (#109) a cross-module nullable VALUE-TYPE generic case (`T?` param+field instantiated at `T=Int`), which
  documents the #86/#147 cross-module gap as an RT_XFAIL (the consumer fails to compile because the `T?` restores as
  bare non-null `T`) — an axis every other gate missed by driving only `T=String`.
- **stdlib ([tmyt/dotkt#167]/[tmyt/dotkt#168], area:stdlib): String/Float/Double `hashCode()` bind to CLR-native `GetHashCode`.**
  Removed the hand-rolled JVM-forced hash bodies — String's `s[0]*31^(n-1)+…` polynomial and Float/Double's
  `toBits()` bit-hash. The Kotlin `hashCode` contract requires only within-run consistency + equals-consistency
  ("need not remain consistent from one execution to another"), not a specific value or across-run determinism;
  kotlin/clr consumes no JVM artifacts, so no interop needs the JVM value. `System.String/Single/Double.GetHashCode`
  already satisfy the contract (per-process consistent, NaN/zero normalized to be equals-consistent with the
  total-order structural equality). String binds via `@ClrIntrinsic("GetHashCode")` (falls through kotc's
  universal-method routing to the BCL slot); Float/Double drop the declaration entirely and inherit the `kotlin.Any`
  slot like Int/Long (routing to the native value-type `GetHashCode`). The `il-strhash`/`il-pairtostr` gate cases now
  assert equals-consistency + hash-set membership instead of a pinned integer.
- **CI: run the COMPLETE canonical gate set + a distinct packaged-SDK job + Windows coverage ([tmyt/dotkt#160], area:packaging).**
  `.github/workflows/verify.yml` previously ran only IL/differential/ktproj/round-trip/wide-delegate on a
  single `ubuntu-latest` job — it silently skipped `verify-schema`, `verify-sanity`, and (release-critically)
  `verify-packaged-sdk`, the only gate that restores + consumes the 5 real nupkgs. The workflow now invokes the
  Makefile aggregates (gate list single-sourced there, not copied into YAML): a `verify` job runs
  `make verify-core` (the canonical set), a distinct release-blocking `packaged-sdk` job runs
  `make verify-packaged-sdk`, and a `windows` job covers the Windows surface (kotc.bat install, nupkg restore,
  packaged build/run, `verify-ktproj`, `dotnet new` template creation). New `make verify-core` target =
  `make verify` minus the packaged-SDK gate.
- **NuGet packages carry provenance metadata + third-party notices ([tmyt/dotkt#166], area:packaging).**
  All 5 packages now declare an SPDX `Apache-2.0` license, `projectUrl`, and a `<repository>` with the source
  commit (stamped by `pack-nuget.sh`), and ship a packaged readme (`packaging/DotKt.README.md`). `DotKt.Toolchain`
  additionally ships `THIRD-PARTY-NOTICES.md` listing the redistributed components (Kotlin compiler/runtime,
  kotlinx-coroutines, JetBrains annotations, Mono.Cecil, `System.Reflection.MetadataLoadContext`) and their licenses.
- **docs: README + support matrix reconciled with actual behavior; JVM-framing cleanup ([tmyt/dotkt#164], area:docs).**
  The README "no bundled libraries" line now states DotKt ships no UI/framework abstraction but DOES ship its CLR
  Kotlin stdlib; the hardcoded corpus/pass counts are softened to point at the gates' XFAIL maps. The
  `supported-features.md` Regex row is regenerated method-by-method (`find`/`matchEntire`/`matches`/`replace`/`split`/
  group accessors work; `findAll`/`splitToSequence`/`options` pending). Recorded the correctness bar in
  `docs/dotkt-semantics.md` and `CLAUDE.md`: the bar is the Kotlin spec/KDoc contract, JVM is a reader reference
  (not a compat target), unspecified behavior takes the CLR-native form.
### Fixed

- **bir2cir/ilemit ([tmyt/dotkt#169], area:backend): the concrete `LinkedHashSet` (a #169 side-effect) emitted invalid
  IL for `setOf`/`distinct()`/`toMutableSet()`/`retainAll` — `InvalidProgramException` at runtime.** Making
  `LinkedHashSet` a real generic Kotlin class (was a `@ClrTypeAlias`) exposed three CLR-codegen bugs, all fixed while
  keeping the #169 insertion-order contract: (1) ilemit's `SelectCtor` picked a ctor by ARITY only, so
  `new LinkedHashSet(collection)` resolved to the arity-colliding `(Int)` ctor instead of `(Collection<E>)` — now it
  signature-matches the `new` node's declared `argTypes` (falling back to first-arity when absent/unreadable); (2)
  `CollectionBclSlotSynthesis` emitted its synthesized `ICollection.Contains`/`IList.IndexOf` self-forward against the
  OPEN generic self (`LinkedHashSet\`1`) instead of the constructed `LinkedHashSet<!0>` (the pass runs after
  GenericSelfInstantiation); (3) `MemberCallSubstitution` rerouted EVERY `.iterator()` on an emitted `kotlin.collections.*`
  non-alias type to the base-`Iterator` bridge, but the concrete `LinkedHashSet` declares its own `MutableIterator`-returning
  `iterator()` — the reroute is now suppressed for any type (local OR ref.dll) that declares a concrete `iterator()`, so
  an app's `linkedSetOf(..).iterator().remove()` binds the real slot (was `EntryPointNotFound` on `remove()`). Regression
  case `cases/il-linkedset`.
- **stdlib ([tmyt/dotkt#162]/[tmyt/dotkt#169], area:stdlib): two Kotlin-contract fixes in text/collections.**
  - **#162 `Regex.matchEntire`/`matches` now do a TRUE anchored full match.** The old path ran a leftmost
    `System...Regex.Match` (a SEARCH) and accepted it only if the first result spanned the input — so a shorter
    alternation branch winning the search (`Regex("a|ab").matchEntire("ab")` → `a` found first) returned `null`, and
    lazy quantifiers hit the same class. `matchEntire` now anchors the engine: it re-matches the pattern wrapped as
    `\A(?:<pattern>)\z` (the non-capturing group scopes a top-level alternation and preserves the user's capture-group
    NUMBERS) with the instance's OWN compiled options (read via a new `nativeOptions`/`ClrRegexOptions` binding and fed
    to the static `Regex.Match(string,string,RegexOptions)` overload), so the engine backtracks to a full-input match
    when one exists. `matches` delegates unchanged. Regression case: `cases/il-regexanchor`.
  - **#169 `LinkedHashMap`/`LinkedHashSet` (and `mapOf`/`setOf`) now preserve insertion order across removals.** They
    were aliased to `Dictionary`/`HashSet`, which only preserve insertion order incidentally and LOSE it after a
    removal — violating the Kotlin iteration-order contract. `LinkedHashMap` is now `@ClrTypeAlias`-bound to the
    insertion-ordered `System.Collections.Generic.OrderedDictionary<K,V>` (.NET 9+; a pure alias swap — it exposes the
    same non-generic `IDictionary`/`ICollection` facades and intrinsic members the map-defaults helpers rely on).
    `LinkedHashSet` — .NET has no ordered generic set — is now a REAL pure-Kotlin `MutableSet` backed by that
    `LinkedHashMap` (exactly as Kotlin/JVM backs it with a `LinkedHashMap`), so it gets the `CollectionBclSlotSynthesis`
    ICollection slots + the reverse `GetEnumerator` bridge. Plain `HashMap`/`HashSet` stay unordered (per contract).
    Regression case: `cases/il-linkedorder`.
- **packaging ([tmyt/dotkt#161]/[tmyt/dotkt#106], area:packaging): MSBuild-SDK + pack staleness fixes.**
  - **#161 stale injection metadata across a `<DotKtImport>` change / `dotnet clean`.** `DotKtInjectTypes` consumed
    `@(DotKtImport)` but did not track it as an Input (a non-file item cannot be a target Input), so removing/adding an
    import left the previous `obj/dotkt-clrtypes.meta` in place and the build kept succeeding against a dropped .NET
    type until an unrelated `.kt` edit forced a recompile; and none of the generated DotKt state under
    `$(BaseIntermediateOutputPath)` was tracked for `Clean`, so `dotnet clean` did not repair it. The ordered
    `@(DotKtImport)` set is now materialized into a `WriteOnlyWhenDifferent` manifest (`dotkt-clrimports-explicit.txt`)
    by a new `DotKtComputeImportManifest` target and added as an Input of `DotKtInjectTypes`, so add/remove/reorder
    flips a timestamp and re-runs injection (no-op rebuild stays byte/timestamp-stable); a new `DotKtClean`
    (`BeforeTargets="CoreClean"`) wipes the BIR/CIR dirs + the meta/import-list/options/import-manifest files.
  - **#106 pack could ship a STALE stdlib/klib.** `scripts/pack-nuget.sh` rebuilt the frontend klib and the stdlib
    ref/rt dlls only when MISSING, so a `pack-nuget.sh` run (directly or via `verify-packaged-sdk.sh`) could package a
    klib/stdlib baked by an older toolchain against freshly-built tools. It now uses the fingerprint-aware
    `need_fe_klib`/`need_stdlib_ref`/`need_stdlib_rt` builders (`scripts/lib.sh`), which rebuild on toolchain
    fingerprint mismatch OR absence.
- **ilemit ([tmyt/dotkt#91]/[tmyt/dotkt#92], area:ilemit): generic-field token anchoring + the abstract-slot body invariant.**
  - **#91 generic FIELD token anchoring** — a raw `@ClrField` access whose owner is a GENERIC type emitted a bare
    `C`1::f` operand ("not fully instantiated": `ResolveField`'s `TypeBuilder.GetField(constructed, fb)` threw
    `field must be declared on a generic type definition`, and ilverify crashed with an `IndexOutOfRange` in
    `get_GenericParameters`). `ResolveField` now mirrors the #84-I METHOD-side anchoring, FIELD side: an inherited
    generic-base field is re-anchored onto the owner's CONSTRUCTED base instantiation via a new
    `AnchorInheritedFieldOnBase` — for a non-generic subclass (`constructed == null`), a constructed generic-subclass
    receiver, and a self-instantiated `this` inside a generic method alike. Suspend-free; pure Reflection.Emit
    mechanics (the kotlinx port hit it at `JobSupport.kt ResumeAwaitOnCompletion`1.invoke`). Regression case:
    `cases/il-genfield`.
  - **#92 abstract-slot body invariant** — `EmitMethodBody` now skips any MethodBuilder DECLARED `Abstract`
    (`mb.IsAbstract`, the single source of truth) rather than re-deriving abstractness from the CIR `abstract` flag,
    making the `Method body should not exist` emit-crash impossible while WARNING (naming the def) when the skip is
    unexpected — so an upstream defect (a body written onto an abstract slot) stays visible. The dup-`$dupN` counter now
    runs for class abstract slots too, keeping the body phase in lockstep with declare.
- **kotc ([tmyt/dotkt#57]/[tmyt/dotkt#89]/[tmyt/dotkt#40], area:kotc): three frontend symbol-resolution fixes.**
  - **#57 the `length`-reference deferral is OWNER-keyed, not override-chain-keyed.** A property reference to
    `length` on a USER class implementing `CharSequence` now lifts faithfully — its accessor resolves on the
    class's OWN emitted `get_length` slot — for a DIRECT override AND one INHERITED through an intermediate
    (`B : A`, `A : CharSequence`). The retired override-chain walk over-deferred the direct case (a compile
    error on a liftable reference) while missing the indirect one (both should behave alike). The deferral now
    keys on the accessor's RESOLVED declaring owner (`getterFn.parent`): only a .NET-mapped CharSequence owner
    (`String`/`StringBuilder`/the polymorphic `kotlin.CharSequence`, whose slot bir2cir renames/collapses) stays
    deferred. (`BirEmitterLifts.kt`)
  - **#89 a CROSS-MODULE top-level `val` read is attributed `owner:null`, not the READING file's class.** A
    computed top-level val deserialized from the frontend metadata klib is PACKAGE-keyed (its parent is a package
    fragment, not an `IrFile`), so kotc cannot name its declaring file class and no longer mis-owns it to
    `<ReaderFile>Kt` (the #80 `COROUTINE_SUSPENDED` root). It emits the same "unresolved owner" fact it already
    emits for a cross-module top-level FUNCTION; bir2cir binds the true declaring file class off the ref.dll.
    (`BirEmitterCalls.kt`)
  - **#40 verified already-resolved on current main; regression guard added.** A cross-module `@InlineOnly` +
    `@ClrIntrinsic` stdlib function keeps its `@ClrIntrinsic` binding across the assembly boundary — kotc carries
    the annotation as UNCONDITIONAL, opaque ref.dll metadata (`attrsJson` is not gated on `@InlineOnly`), and
    bir2cir substitutes the plain call to the bound BCL member. No code change.
  - Regression cases: `cases/il-charseqlenref`, `cases/il-xmodtopval`, `cases/il-inlonlyintr`.
- **bir2cir/ilemit ([tmyt/dotkt#93]/[tmyt/dotkt#71]/[tmyt/dotkt#94]/[tmyt/dotkt#95], area:bir2cir/ilemit): a family of numeric/equality miscompiles.**
  - **#93 numeric widening** — `Byte`/`Short`/`UByte`/`UShort` arithmetic (and `inc`/`dec`/`unaryMinus`) dropped the
    operator's DECLARED return type, so the value truncated to the narrow left operand on box/narrow-store
    (`(100.toByte())+(100.toByte())` → `-56` not `200`; `(255u as UByte).inc()` → `256` not `0`).
    `PrimitiveOperatorLowering` now wraps the lowered bin/unary/inc op in a `conv` to the frontend-resolved return
    type (`dynRet`) for the narrow/char owners — generalizing the pre-existing `Char` precedent (`Byte`/`Short` → `Int`,
    `UByte`/`UShort` → `UInt`). Full-width owners stay bare.
  - **#71 ilemit unsigned conv arms** — `EmitConv` gained the `Conv_U1`/`U2`/`U4`/`U8` arms for `UByte`/`UShort`/`UInt`/`ULong`
    targets (previously a `default:` throw that aborted the whole compile); required by the #93 widening and by explicit
    `.toUByte()`/`.toUInt()`/… conversions.
  - **#94 unsigned shr** — `UInt`/`ULong` `shr` now lowers to `>>>` (ilemit `Shr_Un`, zero-filling) instead of the
    sign-propagating `>>` (`UInt.MAX_VALUE shr 1` → `2147483647` not `4294967295`). `shl` is bit-identical and unchanged.
  - **#95 structural float equality** — a STRUCTURAL `==` over two `Double`/`Float` (data-class `equals`/`hashCode`) now
    routes to the total-order helper (`clrDoubleEquals`/`clrFloatEquals`: `NaN == NaN` true, `+0.0 != -0.0`) instead of
    IEEE `ceq`, restoring the equals/hashCode contract. A DIRECT `a == b` stays IEEE (`ieee754equals`) — unchanged.
  - Regression cases: `cases/il-bytewiden`, `cases/il-unsignedshr`, `cases/il-structfloateq`.
- **stdlib: `copyInto` is now overlap-safe (#97).** All nine `copyInto` actuals (generic `Array<T>` +
  the 8 primitive arrays) bind to `System.Array.Copy` (memmove) instead of a naive forward element
  loop, which clobbered source slots on an overlapping self-copy with `destinationOffset > startIndex`.
  This silently corrupted `ArrayDeque.add(index, elem)` (an in-place right shift). (`_ArraysClr.kt`)
- **stdlib: `Double/Float.roundToInt`/`roundToLong` round half-up toward +inf (#103).** They now
  implement `floor(x + 0.5)` (ties: `2.5→3`, `-2.5→-2`, `0.5→1`, `-0.5→0`) instead of delegating to
  `kotlin.math.round` (banker's ties-to-even). NaN throws `IllegalArgumentException`; out-of-range
  saturates to `Int`/`Long` `MIN`/`MAX`. `kotlin.math.round` itself stays ties-to-even. (`MathClr.kt`)
- **stdlib: `CharArray.copyOf(newSize)` zero-fills grown slots with the null char `'\u0000'` (#128),**
  not a space (`U+0020`) — the Kotlin contract fills grown slots with the element type's default
  value (the null char for `Char`). (`_ArraysClr.kt`)
- **kotc ([tmyt/dotkt#66]/[#67]/[#68]/[#69]/[#70], umbrella [#72], area:kotc): lower five fail-loud
  callable-reference / capture / delegate shapes the frontend accepts (stop aborting the compile).**
  Each was a whole-compile abort on frontend-accepted IR; all now lower to pure Kotlin BIR facts (bir2cir
  owns any CLR/coroutine transform). (#66) a callable reference to a `lateinit var` / `@ClrField` property
  (`b::name`, `Box::name`) — the lifted `KProperty` class now reads/writes the plain backing field
  (`lateinitGet`/`field`/`setFieldExpr`) instead of a non-existent `get_/set_` accessor slot. (#67) a
  reference to a `suspend` function (`::work`, `d::apply`) is emitted as a `newSuspendLambda` adapter (the
  suspend lambda `{ a -> target(a) }` with a `suspendCall`-tagged body; bir2cir builds the `SuspendLambda`
  SM), and `kotlin.reflect.KSuspendFunctionN` now erases to a suspend `fn` type like `KFunctionN` — a plain
  suspend `newDelegate` had no cold-suspend lowering and the reflect type-token leaked to ilemit. (#68) a
  local class / object expression that WRITES a captured outer `var` now shares the enclosing frame's heap
  ref-cell (the mutated capture is promoted by `computeRefCells` before the lift). (#69) a local class
  capturing an enclosing TYPE PARAMETER is lifted GENERICALLY (reified CLR generics) — the object-literal
  generic-capture scan is reused, and a local class being DENOTABLE (`val l: L`, member access `l.x`),
  `ownerSpec`/birType now name the constructed `L<T>`. (#70) a TOP-LEVEL delegated property with an
  arbitrary `getValue`/`setValue` provider (`val x by Provider()`) routes through the static
  `x$delegate.getValue/setValue` with a null thisRef (only member/local delegated properties were routed
  before). Regression cases: `cases/il-{lateinitref,suspendref,writecapture,genlocalclass,topdeleg}`.

## 0.9.6-rc7 (2026-07-18)

A large compiler-correctness release. The kotlinx.coroutines CLR port now compiles through the
Kotlin frontend + the entire bir2cir layer (cold-core suspend lowering fires; all 108 CIR files
emit) and advances into ilemit; the remaining ilemit-stage work to make it fully compile+run
(abstract/interface/cross-member suspend cold-lowering completion + the covariance/variance-erasure
representation) is tracked under #85 and moved to 0.9.7. Highlights of what landed: the inline-splice
family (Set A #60–#63, the §4.4ii suspend-carrier + cold-SM nested-closure capture families, member
inline fake-override splicing #87); suspend cold-lowering (Defect A/B, #78/#80/#82, catch-hoist,
COROUTINE_SUSPENDED + coroutineContext binding, splice-local spill); #73 atomic-wrapper cross-module
re-import; #76 generic-base type-arg carriage; #77 concrete-collection loadability (ArrayDeque et al.);
#81 class delegation `$$delegate_0`; #83 interface companion members; #24/#36/#44 correctness; plus
packaging/docs (#50/#53/#54). The nullable value-type generic representation design is settled in #86
(object-erasure) for 0.9.7.

### Fixed

- **bir2cir ([tmyt/dotkt#80] residual, area:bir2cir): an ALREADY-OWNER'd `COROUTINE_SUSPENDED` read now canonicalizes.**
  The #80 fix rebinds the top-level val `COROUTINE_SUSPENDED` (`kotlin.coroutines.intrinsics`) to its declaring
  `IntrinsicsKt` owner, but only handled the OWNER-NULL emission. The real kotlinx.coroutines port surfaced a variant it
  missed: a NON-suspend reader (`DispatchedCoroutine.getResult(): Any?`) emits the read ALREADY-OWNER'd —
  `callStatic owner=kotlinx.coroutines.Builders_commonKt method=COROUTINE_SUSPENDED prop:get args:[]` (kotc stamps the
  reader's own file class, not owner-null) — so `MemberCallSubstitution`'s owner-null-only rewrite slipped it through and
  the owner-ful non-CLR path merely renamed the accessor, leaving ilemit with `kotlinx.coroutines.Builders_commonKt.
  get_COROUTINE_SUSPENDED not found` (15 sibling nodes normalized correctly). The COROUTINE_SUSPENDED canonicalization is
  now hoisted ahead of the owner-dependent branches and rebinds BOTH shapes (owner-null and already-owner'd) to
  `IntrinsicsKt.get_COROUTINE_SUSPENDED`, static + argless-guarded, regardless of the owner kotc stamped. Non-suspend
  readers never reach SuspendColdLowering's SM-body canonicalization, so this is their only rebind site.
  Gate: `cases/il-suspendintrinsicowned` (a non-suspend `getResult`-shape member reading the intrinsic val).

- **kotc ([tmyt/dotkt#88], area:kotc/area:bir2cir): splicing an inherited member `inline fun` on a GENERIC owner.**
  When an inherited member `inline fun` is spliced (a lambda arg → the same-module splice path) and its OWNER class is
  GENERIC — `IntBox : Container<E>` calling `Container.transform` — kotc's F2A guard omitted the owner's type args because
  the dispatch receiver's static class (`IntBox`) is not the owning class (`Container`). The spliced body's
  `tv{scope:type,0}` (the owner's `E`) then stayed OPEN, so ilemit typed the dispatch temp as the bare open generic →
  `BadImageFormatException`. kotc's F2A now carries the owner's args from the CORRESPONDING-SUPERTYPE instantiation
  (`Container<Int>` seen through `IntBox`), computed substitution-aware + transitively via
  `AbstractTypeChecker.findCorrespondingSupertypes` (`BirEmitter` gains `irBuiltIns` for the type-system context); the
  bir2cir F2B consumer (`recvs.dispatchTypeArgs`) was already implemented. The payload's `tv{scope:type,i}` now
  concretizes to the real call-site type. A TYPE-PARAMETER receiver whose bound fixes the owner (`T : Container<Int>`)
  is handled the same way. When the supertype instantiation CAPTURES a projected/star owner arg (`S : Slot<*>`) it is
  OMITTED (kept at the pre-#88 positional bind / ilemit object-fallback) rather than carried as a misleading
  `Base<Any>`. Gate: `cases/il-inheritedgenericinline` (value-type `Container<Int>`, reference-type `Container<String>`,
  and a `T : Container<Int>`-bound receiver; the value-type path being the one that BadImageFormats).

- **kotc ([tmyt/dotkt#87], area:kotc/area:bir2cir): an INHERITED member `inline fun` with a lambda arg now splices.**
  A member `inline fun` called through a SUBCLASS receiver — e.g. kotlinx.coroutines
  `ConcurrentLinkedListNode<N>.nextOrIfClosed`, a non-local-return-lambda inline fn invoked on a `Segment<S : Segment<S>>`
  — resolves in IR to a FAKE OVERRIDE whose `parent` is the subclass and whose `body` is `null`. kotc's inline-call
  emitter (`emitOwnerfulInlineNode`) took the `callInline` `owner` from `callee.parent` verbatim, so it named the
  SUBCLASS; but bir2cir's InlineSplice keys the `[KotlinInline]` payload under the REAL declaring class (`InlineBirStash`),
  so the lookup missed and the port build broke with `bir2cir: inline splice: cannot splice
  kotlinx.coroutines.internal.Segment.nextOrIfClosed (pc=1 ga=0): no [KotlinInline] payload found`. A fake override also
  has a `null` body, so the same-module splice-routing gate (`callee.body != null`) misrouted the call to the cross-module
  path. Now kotc resolves the fake override (`resolveFakeOverride`, the same normalization the ordinary member-call owner
  path already did at three sites but the inline path had omitted) for the callInline owner + all declaration facts, and
  routes the splice on the resolved declaration's body. The port now advances past bir2cir InlineSplice into the
  suspend-lowering + ilemit stages. Gate: `cases/il-inlineinherit` (a member inline fn with a non-local-return lambda,
  inherited through both a plain subclass and a self-bounded generic `Seg<S : Seg<S>>`, spliced at the subclass call site).

- **bir2cir ([tmyt/dotkt#78], area:bir2cir): a suspend call INSIDE a catch handler now lowers (catch-hoist).**
  Resuming into a CLR `catch` clause is illegal IL, so `SuspendColdLowering` used to refuse any suspend fun with a
  suspension in a catch/finally handler (`SuspensionsSupported`'s `inHandler` gate) — and, because the cold-entry ABI is
  coupled to body transformability, ONE such refusal (`SelectImplementation.processResultAndInvokeBlockRecoveringException`,
  a `catch (e) { recoverAndThrow(e) }`, kotlinx `Select.kt:723`) cascaded to the entire `select` family. bir2cir's new
  `HoistSuspendingCatches` (`toolchain/bir2cir/SuspendColdLowering.Normalize.cs`) lifts a suspending catch handler OUT of
  the CLR clause: the real catch only records the exception into an SM-field-backed capture, and the handler body runs as
  gated straight-line code (`if (__exc$N != null) { … }`) after the try, where the state machine segments its suspension
  normally. Finally-free trys only (hoisting past a finally would flip Kotlin's run-after-handler ordering). Gated in
  lockstep in `SuspensionsSupported`. Also fixes a pre-existing latent bug the newly-lowered value-returning try/catch
  exposed: an init-less value-type SM `var` (kotc's `tryExpr` value var) emitted a null-Int32 const; it now default-inits.

- **bir2cir ([tmyt/dotkt#80], area:bir2cir): `COROUTINE_SUSPENDED` intrinsic reads resolve everywhere.** The top-level
  val `kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED` was mis-owned by `MemberCallSubstitution` to the ENCLOSING file
  class (it is a val, absent from the top-level-fun index), so a bare `<FileClass>.get_COROUTINE_SUSPENDED` reached ilemit
  unresolved. Now bound to the canonical `IntrinsicsKt` owner at substitution time — covering EVERY reader, including the
  port's NON-suspend readers (`getResult(): Any?` in `CancellableContinuationImpl`/`Builders`) that never reach the SM
  transform. The former F2-only `SubstBlock` canonicalization is lifted into `Rewrite`/`RewriteNoSpill` so every SM-body
  path (incl. a direct user `suspendCoroutineUninterceptedOrReturn { … COROUTINE_SUSPENDED }`) normalizes to the SM's own
  `Suspended()` marker.

- **bir2cir ([tmyt/dotkt#82], area:bir2cir): a structured collection loop whose body spans a suspension now lowers
  (loop-flatten).** A `forArray` (`for (x in array)`) or `forEachInline` (inline `Iterable.forEach`) loop whose body
  contains a suspension carries implicit loop machinery (array + index; or an IEnumerator) and an element local that cross
  the resume point — but the straight-line SM cannot segment a structured loop, so a splice-generated element local
  reached ilemit as `load unknown var __inlsN$element`. bir2cir's new `FlattenSuspendingLoops`
  (`toolchain/bir2cir/SuspendColdLowering.Normalize.cs`) desugars such a loop to flat `label`/`brIf`/`goto` CFG with its
  loop temps made explicit `{k:var}`, so `CollectVarFields` spills them into SM fields and the resume re-enters across the
  back-edge. `forEachInline` uses a NON-generic `IEnumerator` (unconditional `viaNonGeneric`) so an open generic-param
  element never mints a broken `IEnumerable<!!T>` TypeBuilder token. A post-Build tripwire (`AssertLocalsResolved`) now
  converts any residual unspilled SM local into a loud bir2cir error instead of a distant ilemit `load unknown var`.

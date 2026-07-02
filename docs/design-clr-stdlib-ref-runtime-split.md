# CLR stdlib: reference / runtime assembly split + app-emit substitution

> **状態 (2026-07-03 見直し)**: ref/rt 分割 + app-emit 置換という骨格は**出荷済みアーキテクチャ**（`DotKt.Private.Stdlib.dll` / `DotKt.Stdlib.dll` / `kotlin-stdlib-clr-frontend.jar`、ビルドは `scripts/build-stdlib-*.sh` 3 本）。ただし本文の「置換は **ilemit** が行う / kotc-vs-ilemit fork」の探索記述は **superseded** — 置換は **bir2cir の `MemberCallSubstitution`**（ref.dll の `@ClrIntrinsic`/`@ClrTypeAlias` を消費）に着地した。現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。生きているのは「1 アセンブリで 2 つの顔は成立しない（C3 cascade を割る）」という動機と 3 アーティファクトの役割定義。

Status: **design (design owner, 2026-06-28).** Pivot away from the single-assembly @ClrIntrinsic binding (which forced the C3
reverse-direction cascade + an ilemit-generics whack-a-mole). Realizes [[clr-stdlib-grand-strategy]] concretely.

## The problem it solves

One assembly can't serve two masters. The stdlib must present:
1. a **compile-time face** = faithful PURE-KOTLIN type shapes (`kotlin.Char : Comparable<Char>`, `List : Collection :
   Iterable`, all members) so the Kotlin frontend resolves types correctly; and
2. a **runtime face** = BCL-bound (`System.Char`, `IReadOnlyList`, ...) for execution + .NET interop.

Doing both IN ONE assembly forces the **reverse direction (C3)**: a Kotlin class implementing the @ClrIntrinsic-bound interface
must ALSO satisfy the BCL interface (get_Count/get_Item/GetEnumerator from Kotlin size/get/iterator). That cascade is
open-ended (every concrete collection class) and drags in ilemit-generics edge cases. **Splitting the two faces removes
the tension.**

## Architecture: three artifacts

### 1. `stdlib.ref.dll` — COMPILE-TIME ONLY (never loaded at runtime)
The stdlib emitted as **pure Kotlin type shapes, NO BCL mapping**:
- `kotlin.collections.List : Collection : Iterable`, `kotlin.Char : Comparable<Char>` — all members, pure Kotlin types.
- The `@ClrIntrinsic("System.Collections.Generic.IReadOnlyList")` binding info is emitted as a **`[Clr]` CUSTOM ATTRIBUTE** on the
  type/member (metadata to be READ at app-emit), NOT consumed as a binding.
- All the `[Kotlin*]` metadata (`infix`/`operator`/`suspend`, the `[KotlinInline]` BIR payloads) — kept (the round-trip
  face).
- Bodies stubbed (`TODO`) — never executed.
- This is ~what `DOTKT_STDLIB_COMPILE=1` already produces (the 3.2MB pure-Kotlin assembly), with one change: under
  stdlib-compile, `@ClrIntrinsic` must NOT bind (clrName returns null) and instead be emitted as a `[Clr]` attribute. So it
  COMPILES trivially — pure Kotlin, **no C3, no ilemit-generics whack-a-mole** (no clrg: BCL refs are emitted at all).

The frontend + bir2cir compile an app against THIS. The app's BIR/CIR references `kotlin.*` types faithfully.

### 2. App-emit SUBSTITUTION (the new machinery — the only genuinely hard part)
When ilemit emits the APP's IL, it **substitutes every `kotlin.*` reference -> its BCL target**, driven by the `[Clr]`
attributes read from `stdlib.ref.dll`:
- `kotlin.collections.List<kotlin.Int>` -> `System.Collections.Generic.IReadOnlyList<int>` (a type reference)
- `List.get(i)` -> `IReadOnlyList<T>.get_Item(i)`, `List.size` -> `Count` (a member reference, from the member's `[Clr]`)
- `kotlin.Char` -> `System.Char` (the primitive maps become DATA in `[Clr]`, not hardcoded in the compiler)
- a call to `xs.map{}` (a runtime-stdlib extension fun) binds to the runtime stdlib's BCL-signature `map`.

The app's emitted IL ends up referencing **BCL types directly + the runtime stdlib** — `stdlib.ref.dll` is fully
substituted away and is NOT a runtime dependency.

Apply at a chokepoint in ilemit's type/member resolution (MapType/ResolveType/ResolveMethod): given a reference to a
`[Clr]`-carrying ref-assembly type/member, return the BCL target. Watch the references that bypass that chokepoint:
base/interface lists, generic args, custom-attribute args, and method-signature matching.

### 3. `stdlib.dll` — RUNTIME (loaded at runtime)
The genuinely-Kotlin stdlib parts (extension funs `map`/`filter`/`listOf`, the iterator bridge adapters) compiled WITH
the substitution applied (so they have BCL signatures), real bodies, and **ALL metadata STRIPPED** (`[Kotlin*]`/`[Clr]`/
inline-BIR). The @ClrIntrinsic-bound TYPES (List/ArrayList) are NOT here — they're substituted to BCL. So the runtime dll is a
lean executable dll. (Benefit, design owner: the [KotlinInline] BIR payloads = ~73.8% of the size live ONLY in the ref
assembly.)

## Why it's correct
- The frontend sees a clean pure-Kotlin reference -> no primitive-dual-representation hacks, no Char-typearg workaround
  ([[primitive-dual-representation]] dissolves: the dual representation IS ref(kotlin.Char) vs runtime(System.Char)).
- C3 is removed from the REFERENCE build (pure Kotlin). (Open Q5: the runtime build may still need C3 for the genuinely-
  Kotlin concrete collection classes — unsigned arrays / EmptyList / ranges — but that's a BOUNDED set, vs the open-ended
  cascade; and C3a member-naming is already done.)
- Substitution is BOUNDED (a table applied at a resolution chokepoint) vs the open-ended C3 cascade.

## What's reusable from prior work (all committed)
- The `@ClrIntrinsic` annotation + `clrName` -> becomes the SOURCE of the `[Clr]` metadata (just emit it as an attribute under
  stdlib-compile instead of binding).
- The iterator bridge (`ClrIteratorBridge.kt`) + the `iterator()` compiler lowering -> the runtime stdlib.
- C3a (clrIfaceMemberName reads the overridden @ClrIntrinsic member's BCL name) + the ilemit generic-self-call / TypeBuilder
  Instantiation robustness fixes -> the runtime build.

## Open design points (Codex consult pending)
- Q1 substitution chokepoint + the references that bypass it.
- Q2 generic substitution + variance (List<out E> covariant -> IReadOnlyList; MutableList invariant -> IList).
- Q3/Q4 ref vs runtime signature divergence: is it OK because the app is FULLY substituted (never binds to ref at
  runtime), or must ref/runtime stay binary-compatible (same assembly identity, .NET ref-assembly style)?
- Q5 the genuinely-Kotlin concrete collection classes in the RUNTIME build (C3 reverse, bounded?).

## VALIDATION (2026-06-28): the pivot's core assumption HOLDS

Empirically confirmed the key claim. Gated `clrName` to return null under `DOTKT_STDLIB_COMPILE` (so @ClrIntrinsic does NOT bind),
added @ClrIntrinsic to Collection/List, and built the stdlib: **frontend 0 errors, ilemit OK, sample runs — NO C3, NO ilemit-
generics whack-a-mole.** The single-assembly approach aborted here (clrOverride / TypeBuilderInstantiation); as
metadata-only it's a trivial pure-Kotlin build. The pivot dissolves the cascade, as designed.

### Concrete next steps (implementation order)
1. **Ref build:** `clrName` returns null under stdlib-compile (done in spike) + EMIT @ClrIntrinsic as a `[Clr]` attribute. GAP found:
   `interfaceDef`/`ifaceMethod`/interface-property emission do NOT call `attrsJson` (only `typeDef`/`method` do). Add attrs
   to the interface paths so the [Clr] metadata rides interfaces + their members (so app-emit can read it). ilemit must
   DefineCustomAttribute on interface types/methods.
2. **App-emit substitution (the core new work):** ilemit, when emitting an APP that references the ref assembly, reads the
   [Clr] attribute on a referenced stdlib type/member and substitutes -> BCL at a resolution chokepoint (MapType/Resolve
   Method). Drive the primitive maps (kotlin.Char->System.Char) from [Clr] too (retire the hardcoded maps — the goal of
   [[four-layer-purpose-retire-intrinsics]]).
3. **Runtime build:** the stdlib funcs/adapters compiled WITH substitution + metadata stripped.

## Design resolutions (design owner reasoning, Codex cross-check pending)

### The unification: ref-mode vs substitute-mode — ONE substitution, two emit modes
There aren't really "two builds with different rules" — there's ONE substitution mechanism and a flag:
- **ref mode** (`DOTKT_STDLIB_COMPILE`, substitute OFF): @ClrIntrinsic -> `[Clr]` attr, pure Kotlin shapes. Produces `stdlib.ref.dll`.
- **substitute mode** (default — used for BOTH apps AND the runtime stdlib build): compile against `stdlib.ref.dll`, then
  at emit READ its `[Clr]` attrs and substitute `kotlin.* -> BCL`. Produces app IL / `stdlib.dll`.
So the runtime stdlib build = "substitute mode applied to the stdlib source". Apps and the runtime stdlib are emitted by
the exact same machinery; only the stdlib's REFERENCE build is special (it's the one that emits the metadata everyone
else consumes).

### Q3/Q4 — ref vs runtime signature divergence is FINE (no binary compat needed)
The app is FULLY substituted: every `kotlin.*` type/member/call in the app IL becomes BCL or a runtime-stdlib BCL-signature
member. `stdlib.ref.dll` is NEVER loaded at runtime. ref and runtime have DISTINCT assembly names (ref =
`DotKt.Private.Stdlib.dll`, runtime = `DotKt.Stdlib.dll`) so a call resolves to the runtime dll `DotKt.Stdlib`, but they
need NOT be binary-compatible — the app references
the SUBSTITUTED (BCL) signature, and the runtime dll (same substitution applied) provides exactly that signature. The
substitution is the single source of truth, applied consistently to the app AND the runtime build, so signatures match by
construction. ref.dll = compile-time type-shape + metadata provider ONLY.

### Q5 — the runtime build needs BOUNDED C3 (not the open cascade)
In substitute mode, the genuinely-Kotlin concrete classes with NO BCL equivalent (UByteArray:Collection, EmptyList, the
ranges) become `: IReadOnlyCollection` etc., so they must satisfy the BCL interface. Substitution handles the NAMING
(size->get_Count via the member [Clr]) but NOT the iterator SEMANTIC bridge — so each such class still needs a generated
`GetEnumerator(): IEnumerator<T>` wrapping its Kotlin `iterator()` in an `EnumeratorOverKotlinIterator<T>` adapter (C3b).
This set is BOUNDED (a handful of classes) — tractable per-class — vs the single-assembly approach where C3 hit EVERY
type. C3a (member naming) is already done. So the residual hard piece (C3b GetEnumerator generation) shrinks to a small,
well-scoped generator.

### Q1/Q2 — the substitution chokepoint
ilemit loads the referenced `stdlib.ref.dll` (MetadataLoadContext), builds a substitution table from the `[Clr]` attrs
(type FQN -> BCL spec; member -> BCL member name), and applies it at the type/member resolution chokepoint (`MapType`/
`ResolveType`/`ResolveMethod`). Generic substitution composes structurally (`List<Int>`->`IReadOnlyList<int>`, nested OK);
variance rides the BCL target (`List<out E>`->covariant `IReadOnlyList`, `MutableList`->invariant `IList`) — it's the
target interface's own variance, so nothing special. Watch the bypass paths: base/interface lists, generic args, attribute
args, and by-shape method-overload matching all must route through the same chokepoint.

## Step 2 fork (2026-06-28): WHERE the substitution lives — kotc vs ilemit

> **SUPERSEDED (2026-06-30 見直し).** This "kotc vs ilemit" fork (and option (A)'s kotc-level placement) is no longer
> the architecture: `@ClrIntrinsic` / type substitution is a **bir2cir** responsibility, sourced from the ref assembly
> (`DotKt.Private.Stdlib.dll`), and the substituted CIR is a plain BCL call before `ilemit`. See [docs/ship-tasks.md](ship-tasks.md) §3.
> Known defect: the substitution code currently still sits in `kotc`'s `BirEmitter`; it is to be moved to bir2cir (ship-tasks §6, the "current violation").

Step 1 done (ref assembly carries `[Clr]` on types + members, verified). For step 2, two places to apply substitution:

- **(A) kotc-level (RECOMMENDED):** when an APP references stdlib.ref.dll, restore the `[kotlin.clr.ClrIntrinsic]` attribute as an @ClrIntrinsic
  annotation (or populate ClrTypeRegistry) on the injected/restored stdlib types+members, so the app's EXISTING clrName
  mechanism binds `List -> IReadOnlyList`, `size -> Count` at the app's BIR/CIR. Then the app's CIR is ALREADY substituted
  (clrg:IReadOnlyList) and ilemit needs NO new substitution logic (it already emits clrg:, hardened this session). The
  runtime stdlib build gets it for free (it also references the ref assembly). UNIFIES app+runtime via the existing @ClrIntrinsic
  path. COST: facadegen `--scan-asm` must emit the `[kotlin.clr.ClrIntrinsic]` info into the injection meta + the FIR injection must restore
  it (type-level via ClrTypeRegistry.dotNetName fallback in clrName; member-level needs @ClrIntrinsic-on-member restoration). The app
  is NOT stdlib-compile, so clrName is NOT gated -> it binds.
- **(B) ilemit-level:** ilemit reads `[Clr]` from the loaded ref assembly at emit and substitutes in ResolveType (types) +
  member resolution. Touches only ilemit, but must cover every emission path (base/iface lists, generic args, member
  calls, by-shape matching) — more edge cases, and leaves the app's CIR referencing kotlin.* (substituted late).

Recommendation: **(A)** — substitution at the frontend/backend boundary keeps ilemit dumb, reuses the proven @ClrIntrinsic
mechanism, and unifies the app + runtime builds. Confirmed: facadegen meta for `List` currently has NO @ClrIntrinsic (pure Kotlin
shape only) — so (A)'s work is "facadegen emits @ClrIntrinsic from [kotlin.clr.ClrIntrinsic] + injection restores it". (Codex cross-check of the
substitution chokepoint pending.)

## Step 2B (member substitution) — sub-parts identified (2026-06-28)

> **SUPERSEDED (2026-06-30 見直し).** Describes the kotc-level member substitution plumbing. In the current
> architecture the member/type substitution from `@ClrIntrinsic` lives in **bir2cir** (sourced from the ref assembly),
> not kotc. See [docs/ship-tasks.md](ship-tasks.md) §3; it currently still sits in `BirEmitter` (ship-tasks §6, the "current violation").

Step 2A (TYPE substitution) works: facadegen emits the BCL name as the injected dotNet token, the injection registers it
in ClrTypeRegistry, clrName binds `List -> IReadOnlyList`. Member substitution (`size -> Count`, `get -> get_Item`) is the
next slice and has more plumbing:
- The ref-assembly List's `size` (a property under the CLR property model) is reflected by facadegen as `fun get_size`
  (a method), not restored as `prop size`. So the app referencing it sees `get_size`, not a `size` property. Two issues:
  (a) facadegen should RESTORE the property (so the app uses `list.size`), and (b) carry the member's [kotlin.clr.ClrIntrinsic("Count")].
- The injection (ClrTypeInjection) has no PER-MEMBER clr-name today (only the type-level ClrTypeRegistry). So a member
  registry (member fqn -> BCL name) OR attaching an @ClrIntrinsic annotation to the synthesized FIR member is needed, so
  clrName(member) returns the BCL name (get_Count / get_Item) at the app's call sites.
- Then the existing call-resolution (`clrName(callee) ?: name`, the @ClrIntrinsic member path) emits the BCL member call.

So 2B = facadegen (emit member [kotlin.clr.ClrIntrinsic] + restore properties) + injection (per-member clr name) + (reuse) clrName. The
TYPE path is done; the MEMBER path is the same idea one level down. After 2B: the end-to-end app-against-ref test, then
the runtime build + bounded C3b.

### Current overall state (this session)
DONE+committed: design (architecture, Q1-Q5, fork A), step 1 (ref [Clr] metadata, verified), step 2A (type substitution,
verified at meta level). Plus reusable: @ClrIntrinsic mechanism (class/member/rollup/top-level/extension), iterator bridge +
iterator() lowering, C3a (clrIfaceMemberName @ClrIntrinsic), and ilemit robustness (generic self-calls, TypeBuilderInstantiation,
clrg: arity). The pivot is on a clear, validated track with no remaining UNKNOWN difficulty — the rest is the same
substitution pattern extended to members + the app/runtime build wiring.

## CORRECTION (2026-06-28): 2A conflated Kotlin identity with BCL binding

While wiring 2B, found that 2A (emit the BCL name AS the injected dotNet token) is WRONG: the FIR injection computes the
Kotlin PACKAGE from `namespaceOf(dotNetName)` (ClrTypeInjection L216/L235), so emitting `System.Collections.Generic.
IReadOnlyList` as the token moves the type to package `System.Collections.Generic` — the app's `List` (which resolves to
`kotlin.collections.List`) then never binds to it. The Kotlin IDENTITY (`kotlin.collections.List`, for the namespace/
ClassId) and the BCL BINDING (`IReadOnlyList`, for clrName) must be SEPARATE.

**Corrected mechanism — `=` encoding in token[2]:** facadegen emits `interface List kotlin.collections.List=System.
Collections.Generic.IReadOnlyList E` (KotlinFqn=BclName). The injection splits tok[2] on `=`: the LEFT drives namespace/
ClassId (Kotlin identity preserved), the RIGHT is registered in ClrTypeRegistry as the binding (clrName -> IReadOnlyList).
Members use a distinct trailing `clr:Count` token on the `prop`/`fun` line -> ClrTypeRegistry.memberNames (added) keyed by
the member's Kotlin fqn; clrName(IrProperty) looks it up (resolving fake-overrides). Then the existing clrName-driven
emission produces clrg:IReadOnlyList + get_Count, ilemit unchanged.

DONE so far: ClrTypeRegistry.memberNames + memberClrName (added); facadegen emits `clr:Count` on the interface prop line.
TODO: revert 2A's idot=ClrAttrName -> the `=` encoding; injection split + member-token parse + clrName member lookup; then
test the ACTUAL app-against-ref flow (the meta-string check in 2A did NOT exercise resolution — that's how the namespace
bug slipped). NO unknown difficulty, just the coordinated edits + a real end-to-end test.

## ★ SUBSTITUTION PROVEN END-TO-END (2026-06-28) ★

> **SUPERSEDED (2026-06-30 見直し).** The mechanism proven here is the kotc-level (`BirEmitter.clrName`) substitution.
> The current architecture relocates `@ClrIntrinsic` / type substitution into **bir2cir** (sourced from the ref assembly
> `DotKt.Private.Stdlib.dll`, producing a plain BCL call in CIR — it never reaches `ilemit`). See [docs/ship-tasks.md](ship-tasks.md) §3.
> Known defect: it currently still sits in `BirEmitter`, to be moved to bir2cir (ship-tasks §6, the "current violation").

The `=`-encoding correction works. Implemented + verified with a C# consumer:
- facadegen: `token[2] = KotlinFqn=BclName` for a @ClrIntrinsic type; `clr:Count`/`clr:get_Item` token on prop/fun lines.
- injection: split tok[2] on `=` (LEFT=Kotlin identity for namespace/ClassId, RIGHT=BCL binding -> ClrTypeRegistry); the
  prop/fun `clr:` token -> per-member binding (ClrTypeRegistry.memberNames, key=member fqn). A @ClrIntrinsic type (clrBinding!=null)
  is FILTERED OUT of byClassId — NOT re-created as a FIR type (the jar provides the builtin shape incl. operator/infix;
  only the binding is registered). This fixed `xs[0]` failing with "operator modifier required".
- BirEmitter.clrName: looks up an IrProperty / non-accessor IrSimpleFunction in the member registry, walking
  fake-overrides (inherited `List.size` -> `Collection.size`'s binding).

Test: `fun listSize(xs:List<Int>)=xs.size`, `firstElem=xs[0]`, `secondElem=xs.get(1)` compiled
`kotc <app> -no-stdlib -classpath <kotlin-stdlib.jar>` + `CLR_TYPES_METADATA=ref.meta` (clrName ACTIVE) -> bir2cir ->
ilemit -> retarget -> a C# consumer passes `int[]{10,20,30}` -> **size=3 first=10 second=20**. Type + property + method
substitution all correct. native-cir 18 PASS, roundtrip PASS.

Remaining: bind more Collection members (only size/get today; isEmpty/contains/indexOf aren't on IReadOnlyList<T> -> Kotlin
impls needed); the runtime stdlib build; bounded C3b (GetEnumerator for UByteArray/EmptyList/ranges); primitive [Clr] maps.

## Runtime-build architecture (2026-06-28, clarified by the listOf-essence proof)

PROVEN: a Kotlin function that CREATES a List works end-to-end in the app-flow. `fun <T> listOfMini(vararg elements: T):
List<T> = elements as List<T>` (the @Suppress'd cast — at the CLR a `T[]` IS `IReadOnlyList<T>`) compiled app-against-ref
-> return type `IReadOnlyList<T>`, body a cast -> a C# consumer `listOfMini<int>(7,8,9)` -> Count=3, [0]=7, [2]=9. This is
exactly the real stdlib's `listOf(vararg) = elements.asList()` where the CLR `Array<T>.asList() = this as List<T>`.

KEY INSIGHT — the RUNTIME stdlib is built in APP-FLOW mode, NOT stdlibCompile mode:
- stdlibCompile (clrName gated) produces the REF (pure Kotlin shapes + [Clr] metadata).
- The RUNTIME is SUBSTITUTED code, so it compiles like an APP: clrName ACTIVE + CLR_TYPES_METADATA=ref.meta + jar. Its
  `List` substitutes to `IReadOnlyList`, exactly as a consuming app's does.
- CATCH: the stdlib's TYPE DECLARATIONS (the `List` interface itself) can NOT be compiled in app-flow — byClassId filters
  kotlin.* out of injection (the jar owns them), so a source re-defining `kotlin.collections.List` would clash. Therefore
  the runtime build compiles ONLY the FUNCTION files (listOf/map/filter/asList...), referencing the ref's types. The type
  decls live only in the ref.
- ref and runtime have DISTINCT assembly names (ref = `DotKt.Private.Stdlib.dll`, runtime = `DotKt.Stdlib.dll`) -> the
  app compiles against the ref's List signatures and RUNS against the runtime's IReadOnlyList impls (List ≡ IReadOnlyList
  post-substitution). This is NOT a same-name ref/impl swap — the two dll names differ.

Next concrete step: a runtime-build script that compiles the stdlib FUNCTION files (not the type-decl files) in app-flow
mode, producing the same-named runtime assembly; set the CLR actuals' bodies (asList = `this as List<T>`, etc.); strip
[Clr]/[Kotlin*] metadata. Bounded C3b only bites for genuinely-Kotlin CONCRETE collection classes (UByteArray/EmptyList/
ranges) if they're in the compiled set — keep them in the ref/handle separately.

## Reverse GetEnumerator bridge (C3b) — DESIGN LOCKED (2026-06-28, Codex-reviewed)

A concrete Kotlin collection class implementing the @ClrIntrinsic-bound `List`/`Collection`/`Iterable` (now CLR IReadOnly*/
IEnumerable) must provide `IEnumerator<T> GetEnumerator()`, but only has a Kotlin `iterator(): Iterator<T>`. The bridge:

- **(A) Adapter in IL, not Kotlin (A2).** Generate `EnumeratorOverKotlinIterator<T>` directly in the ilemit backend:
  fields = the Kotlin Iterator + a cached current; `MoveNext()` = `if hasNext() { cur = next(); true } else false`;
  generic `T get_Current()`; EXPLICIT `System.Collections.IEnumerator.get_Current()` returning `(object)cur`; `Reset()`
  throws NotSupportedException; `Dispose()` no-op. Rationale (Codex): the two `Current` slots are genuinely distinct
  interface slots; Kotlin can't express explicit interface impl; a GENERAL "auto-emit the non-generic Current bridge"
  feature (A1) has too-broad semantic surface (boxing/nullability/value types/overrides/diagnostics) — only build it later
  if many interop scenarios need it. So a small backend-generated helper type is lowest-risk.
- **Generate `GetEnumerator()` on each qualifying class**: `IEnumerator<T> GetEnumerator() => new
  EnumeratorOverKotlinIterator<T>(this.iterator())` + the non-generic `IEnumerable.GetEnumerator()` returning the same.
  Emit at the class that INTRODUCES the @ClrIntrinsic-Iterable impl (avoid hierarchy duplicates).
- **(B) Break the iterator<->GetEnumerator cycle via an EXPLICIT IR call-kind, not a clrName(declaringClass) heuristic**
  (Codex's biggest risk). The generated GetEnumerator's `this.iterator()` must be a KotlinMemberCall (real method), never
  the forward-bridge lowering (which calls GetEnumerator -> infinite recursion). Mark calls ClrInterfaceBridgeCall vs
  KotlinMemberCall in BIR/CIR. Regression tests: concrete override, inherited concrete impl, abstract base, fake override,
  value-type element enumerators.
- **(C) Minimize the surface**: @ClrIntrinsic-bind concrete classes WITH BCL equivalents (ArrayList->System.Collections.Generic.
  List, HashMap->Dictionary, HashSet->HashSet) so they ARE the BCL type (no bridge); ABSTRACT bases (AbstractList/
  AbstractCollection) stay Kotlin with un-bindable interface members left ABSTRACT (an abstract class need not fully
  satisfy the interface); generate the reverse bridge ONLY for irreducibly-Kotlin concretes (EmptyList, ranges,
  UByteArray-like, sublist views). CAVEAT (Codex): treat ArrayList->System.List as targeted member mappings + tests, NOT
  "the APIs are identical" (Kotlin `add` returns Boolean vs List.Add void; nullability; mutation-during-iteration).

Implementation order: the reverse bridge (A)+(B) is the highest-leverage (unblocks ALL Kotlin collection classes at once);
(C) is a follow-up optimization. Then subList (returns such a List), metadata-strip, same-name assembly swap.

## User-library reverse-substitution: decision (A) breadcrumb — DEFERRED (2026-06-28)

A USER LIBRARY (built by kotc with substitution + KEEP attrs) has `IReadOnlyList<int>` in its IL signatures. At the ABI/call
level this is SMOOTH (List ≡ IReadOnlyList, identity-preserving — a consumer's List<Int> matches). BUT for an importer to
see the idiomatic `kotlin.collections.List`, the reverse substitution IReadOnlyList->List is AMBIGUOUS: the IL can't tell a
substituted `List<Int>` from an explicit `IReadOnlyList<int>` interop usage. DECISION (user): **(A) breadcrumb** — record
the original Kotlin type at each substituted position in the kept [Kotlin*] metadata, so import can restore List precisely.
**DEFERRED** (not blocking the stdlib runtime; the stdlib uses a ref/runtime split, not reverse-import). To do later: extend
the per-member metadata with the pre-substitution type at collection/@ClrIntrinsic positions; the import path (facadegen --scan-asm /
the round-trip injector) reads it to reverse-map. Until then, an imported user lib shows IReadOnlyList (functional, non-idiomatic).

## Primitive conversion lowering: hardcoded NUMBER_CONV -> metadata-driven (refinement, DEFERRED 2026-06-28)

`x.toDouble()`/`toInt()`/... on a numeric primitive lower to a CIL `conv` (BirEmitter ~L3683, driven by the hardcoded
`NUMBER_CONV = mapOf("toDouble" to "double", ...)` + `NUMERIC_FQ` in BirMappings.kt). User: hardcoding is a residual
intrinsic; ideally drive it from stdlib metadata. CLEAN DESIGN (deferred): the conversion TARGET is already the method's
RETURN TYPE (`Int.toDouble(): Double`), so a marker annotation on the stdlib conversion methods (e.g. `@clr.ClrConv`, or
reuse `@IntrinsicConstEvaluation`) + `conv to birType(callee.returnType)` removes BOTH NUMBER_CONV's hardcoded targets AND
the name set — same "stdlib declares, compiler reads" philosophy as @ClrIntrinsic. NOT urgent: a cast is a CIL `conv` instruction
with no method to bind to (can't be @ClrIntrinsic proper), so a small fixed intrinsic is acceptable; the win is removing the
hardcoded target map. Cost = annotating ~49 conversion methods (7 conversions × 7 numeric primitives).

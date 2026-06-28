# CLR stdlib: reference / runtime assembly split + app-emit substitution

Status: **design (design owner, 2026-06-28).** Pivot away from the single-assembly @Clr binding (which forced the C3
reverse-direction cascade + an ilemit-generics whack-a-mole). Realizes [[clr-stdlib-grand-strategy]] concretely.

## The problem it solves

One assembly can't serve two masters. The stdlib must present:
1. a **compile-time face** = faithful PURE-KOTLIN type shapes (`kotlin.Char : Comparable<Char>`, `List : Collection :
   Iterable`, all members) so the Kotlin frontend resolves types correctly; and
2. a **runtime face** = BCL-bound (`System.Char`, `IReadOnlyList`, ...) for execution + .NET interop.

Doing both IN ONE assembly forces the **reverse direction (C3)**: a Kotlin class implementing the @Clr-bound interface
must ALSO satisfy the BCL interface (get_Count/get_Item/GetEnumerator from Kotlin size/get/iterator). That cascade is
open-ended (every concrete collection class) and drags in ilemit-generics edge cases. **Splitting the two faces removes
the tension.**

## Architecture: three artifacts

### 1. `stdlib.ref.dll` — COMPILE-TIME ONLY (never loaded at runtime)
The stdlib emitted as **pure Kotlin type shapes, NO BCL mapping**:
- `kotlin.collections.List : Collection : Iterable`, `kotlin.Char : Comparable<Char>` — all members, pure Kotlin types.
- The `@Clr("System.Collections.Generic.IReadOnlyList")` binding info is emitted as a **`[Clr]` CUSTOM ATTRIBUTE** on the
  type/member (metadata to be READ at app-emit), NOT consumed as a binding.
- All the `[Kotlin*]` metadata (`infix`/`operator`/`suspend`, the `[KotlinInline]` BIR payloads) — kept (the round-trip
  face).
- Bodies stubbed (`TODO`) — never executed.
- This is ~what `DOTKT_STDLIB_COMPILE=1` already produces (the 3.2MB pure-Kotlin assembly), with one change: under
  stdlib-compile, `@Clr` must NOT bind (clrName returns null) and instead be emitted as a `[Clr]` attribute. So it
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
inline-BIR). The @Clr-bound TYPES (List/ArrayList) are NOT here — they're substituted to BCL. So the runtime dll is a
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
- The `@Clr` annotation + `clrName` -> becomes the SOURCE of the `[Clr]` metadata (just emit it as an attribute under
  stdlib-compile instead of binding).
- The iterator bridge (`ClrIteratorBridge.kt`) + the `iterator()` compiler lowering -> the runtime stdlib.
- C3a (clrIfaceMemberName reads the overridden @Clr member's BCL name) + the ilemit generic-self-call / TypeBuilder
  Instantiation robustness fixes -> the runtime build.

## Open design points (Codex consult pending)
- Q1 substitution chokepoint + the references that bypass it.
- Q2 generic substitution + variance (List<out E> covariant -> IReadOnlyList; MutableList invariant -> IList).
- Q3/Q4 ref vs runtime signature divergence: is it OK because the app is FULLY substituted (never binds to ref at
  runtime), or must ref/runtime stay binary-compatible (same assembly identity, .NET ref-assembly style)?
- Q5 the genuinely-Kotlin concrete collection classes in the RUNTIME build (C3 reverse, bounded?).

## VALIDATION (2026-06-28): the pivot's core assumption HOLDS

Empirically confirmed the key claim. Gated `clrName` to return null under `DOTKT_STDLIB_COMPILE` (so @Clr does NOT bind),
added @Clr to Collection/List, and built the stdlib: **frontend 0 errors, ilemit OK, sample runs — NO C3, NO ilemit-
generics whack-a-mole.** The single-assembly approach aborted here (clrOverride / TypeBuilderInstantiation); as
metadata-only it's a trivial pure-Kotlin build. The pivot dissolves the cascade, as designed.

### Concrete next steps (implementation order)
1. **Ref build:** `clrName` returns null under stdlib-compile (done in spike) + EMIT @Clr as a `[Clr]` attribute. GAP found:
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
- **ref mode** (`DOTKT_STDLIB_COMPILE`, substitute OFF): @Clr -> `[Clr]` attr, pure Kotlin shapes. Produces `stdlib.ref.dll`.
- **substitute mode** (default — used for BOTH apps AND the runtime stdlib build): compile against `stdlib.ref.dll`, then
  at emit READ its `[Clr]` attrs and substitute `kotlin.* -> BCL`. Produces app IL / `stdlib.dll`.
So the runtime stdlib build = "substitute mode applied to the stdlib source". Apps and the runtime stdlib are emitted by
the exact same machinery; only the stdlib's REFERENCE build is special (it's the one that emits the metadata everyone
else consumes).

### Q3/Q4 — ref vs runtime signature divergence is FINE (no binary compat needed)
The app is FULLY substituted: every `kotlin.*` type/member/call in the app IL becomes BCL or a runtime-stdlib BCL-signature
member. `stdlib.ref.dll` is NEVER loaded at runtime. ref and runtime share the assembly NAME (`DotKt.Stdlib`) so a call
to `DotKt.Stdlib.CollectionsKt.map` resolves to the runtime dll, but they need NOT be binary-compatible — the app references
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

Step 1 done (ref assembly carries `[Clr]` on types + members, verified). For step 2, two places to apply substitution:

- **(A) kotc-level (RECOMMENDED):** when an APP references stdlib.ref.dll, restore the `[clr.Clr]` attribute as an @Clr
  annotation (or populate ClrTypeRegistry) on the injected/restored stdlib types+members, so the app's EXISTING clrName
  mechanism binds `List -> IReadOnlyList`, `size -> Count` at the app's BIR/CIR. Then the app's CIR is ALREADY substituted
  (clrg:IReadOnlyList) and ilemit needs NO new substitution logic (it already emits clrg:, hardened this session). The
  runtime stdlib build gets it for free (it also references the ref assembly). UNIFIES app+runtime via the existing @Clr
  path. COST: facadegen `--scan-asm` must emit the `[clr.Clr]` info into the injection meta + the FIR injection must restore
  it (type-level via ClrTypeRegistry.dotNetName fallback in clrName; member-level needs @Clr-on-member restoration). The app
  is NOT stdlib-compile, so clrName is NOT gated -> it binds.
- **(B) ilemit-level:** ilemit reads `[Clr]` from the loaded ref assembly at emit and substitutes in ResolveType (types) +
  member resolution. Touches only ilemit, but must cover every emission path (base/iface lists, generic args, member
  calls, by-shape matching) — more edge cases, and leaves the app's CIR referencing kotlin.* (substituted late).

Recommendation: **(A)** — substitution at the frontend/backend boundary keeps ilemit dumb, reuses the proven @Clr
mechanism, and unifies the app + runtime builds. Confirmed: facadegen meta for `List` currently has NO @Clr (pure Kotlin
shape only) — so (A)'s work is "facadegen emits @Clr from [clr.Clr] + injection restores it". (Codex cross-check of the
substitution chokepoint pending.)

## Step 2B (member substitution) — sub-parts identified (2026-06-28)

Step 2A (TYPE substitution) works: facadegen emits the BCL name as the injected dotNet token, the injection registers it
in ClrTypeRegistry, clrName binds `List -> IReadOnlyList`. Member substitution (`size -> Count`, `get -> get_Item`) is the
next slice and has more plumbing:
- The ref-assembly List's `size` (a property under the CLR property model) is reflected by facadegen as `fun get_size`
  (a method), not restored as `prop size`. So the app referencing it sees `get_size`, not a `size` property. Two issues:
  (a) facadegen should RESTORE the property (so the app uses `list.size`), and (b) carry the member's [clr.Clr("Count")].
- The injection (ClrTypeInjection) has no PER-MEMBER clr-name today (only the type-level ClrTypeRegistry). So a member
  registry (member fqn -> BCL name) OR attaching an @Clr annotation to the synthesized FIR member is needed, so
  clrName(member) returns the BCL name (get_Count / get_Item) at the app's call sites.
- Then the existing call-resolution (`clrName(callee) ?: name`, the @Clr member path) emits the BCL member call.

So 2B = facadegen (emit member [clr.Clr] + restore properties) + injection (per-member clr name) + (reuse) clrName. The
TYPE path is done; the MEMBER path is the same idea one level down. After 2B: the end-to-end app-against-ref test, then
the runtime build + bounded C3b.

### Current overall state (this session)
DONE+committed: design (architecture, Q1-Q5, fork A), step 1 (ref [Clr] metadata, verified), step 2A (type substitution,
verified at meta level). Plus reusable: @Clr mechanism (class/member/rollup/top-level/extension), iterator bridge +
iterator() lowering, C3a (clrIfaceMemberName @Clr), and ilemit robustness (generic self-calls, TypeBuilderInstantiation,
clrg: arity). The pivot is on a clear, validated track with no remaining UNKNOWN difficulty — the rest is the same
substitution pattern extended to members + the app/runtime build wiring.

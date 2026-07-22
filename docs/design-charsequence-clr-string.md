# Design: `CharSequence` is `string` on the CLR (the 3-point model)

> **DECISION (user, 2026-07-02).** `kotlin.CharSequence` is a JVM-shaped abstraction with **no .NET equivalent**
> (System.String is sealed; System.Text.StringBuilder shares no indexed-char interface with it; `ReadOnlySpan<char>`
> — the only real .NET "char view" — is a `ref struct` that can't be a general parameter/field/generic-arg type). Per
> the project philosophy (*Kotlin carries JVM accidental complexity; on the CLR, identify and discard it* —
> `clr-not-jvm-discard-jvmisms`), DotKt **models `CharSequence` as `string`** at the CLR boundary. The synthetic
> `<>dotkt_CharSequence` interface is **retained only for genuine user polymorphism** (a user writing
> `class S : CharSequence`), which is rare.

## The 3-point model

1. **A `CharSequence` parameter / return type is emitted as `System.String`** on the CLR (NOT the synthetic
   `<>dotkt_CharSequence`). This is the default for all stdlib and app signatures. `String` (which *is* a
   `CharSequence` in Kotlin) flows directly — no wrapper, no synthetic.

2. **A non-`String` `CharSequence` value passed to a `CharSequence`(=`string`) slot is coerced with an implicit
   `.toString()` at the boundary** (a snapshot). This covers `StringBuilder` and any user `CharSequence`. It is the
   `String → <>dotkt_CharSequence` adapter's mirror image, and it is the same spirit as the existing implicit
   coercions DotKt already performs.

3. **`CharSequence` therefore has `string` (immutable snapshot) semantics on the CLR** — the JVM "live view" semantics
   (where mutating a `StringBuilder` after passing it as `CharSequence` is observable through the parameter) is
   **deliberately NOT supported**. This is a documented deviation (like CLR-native primitive stringification), recorded
   in `docs/dotkt-semantics.md`. It is honest because it is declared, not hidden.

## Why this is right (not a compromise)

- **.NET has no `CharSequence`.** Its char-view vocabulary is `string` (snapshot) + `ReadOnlySpan<char>` (perf view).
  A polymorphic heap `CharSequence` interface is un-idiomatic on .NET. `ReadOnlySpan<char>` — the one true "view" —
  is a `ref struct`, so it *cannot* back a general `CharSequence` (no heap, no generic arg, no field). There is no
  zero-copy polymorphic option on the CLR; `string` (with a copy for the rare non-`String` case) is the pragmatic
  and idiomatic choice, not a lossy shortcut.
- **The root awkwardness is Kotlin making `StringBuilder : CharSequence`** (a JVM hierarchy accident). On .NET,
  `StringBuilder` and `string` are distinct; converting explicitly (or via one implicit `.toString()`) is the norm.
- **Interop cleanliness:** `CharSequence` params become `string`, so C# sees `void f(string s = "")` with a native
  `[DefaultParameterValue]`, `[Optional]`, overload resolution, etc. — no ugly synthetic in the public API.
- **Cost is minimal + confined to a rare case:** a large `StringBuilder` read only briefly through a `CharSequence`
  param is copied by the `.toString()`. But (a) stdlib `CharSequence` params are read immediately (snapshot ≡ live
  read), (b) 99%+ of `CharSequence` values are already `String`, (c) non-`String` `CharSequence` args are vanishingly
  rare. The lost "live view" is a JVM affordance almost nobody relies on.

## Interaction with existing work

- **Default arguments:** under this model a `CharSequence` param with a
  string default becomes a **`string`** param with a string default → it moves from Tier 2 (`[KotlinDefault]` +
  required) to **Tier 1** (`[Optional][DefaultParameterValue("")]`, native for both C# and kcc). `joinToString`'s
  `separator`/`prefix`/`postfix`/`truncated` all become clean native-optional `string` params. RC1's `[KotlinDefault]`
  machinery is still needed (genuinely non-constant defaults) — this model just reroutes the CharSequence-param case
  from Tier 2 to Tier 1.
- **The 4-A synthetic + adapter + canonicalization (`<>dotkt_CharSequence`, `<>dotkt_StringCharSequence`, the
  `CanonicalSynthetics` ilemit path):** NOT wasted — it becomes the **user-`class S : CharSequence`-only** path
  (`il-charseq`/`il-charseqx`). It is simply hit far less (no longer on every stdlib `String`-op call).
- **The 4-B String-op retires:** the retired `contains`/`startsWith`/… routed through the stdlib `CharSequence`-ext
  bodies cross-assembly. Under this model those bodies take `string`, which is simpler (String flows directly, no
  synthetic boundary) — likely UNBLOCKS the 5 still-BLOCKED String ops (`trim`/`reversed`/`padStart`/`replace(S,S)`/
  `isBlank`) whose blockers were CharSequence-body interop (StringBuilder↔CharSequence, CharSequence iteration).

## Implementation plan (after RC1/RC2 land — it touches the same kotc/bir2cir/ilemit files)

Layer placement (per CLAUDE.md): kotc emits the pure `kotlin.CharSequence` FQN; **bir2cir resolves `kotlin.CharSequence`
→ `System.String`** in param/return/local slots (the "map a stdlib type to its CLR form" job it already does for
`kotlin.String`→`System.String`, collections→BCL) — EXCEPT a user-declared `class S : CharSequence` supertype, which
stays the synthetic `<>dotkt_CharSequence`. ilemit emits per the resolved tokens.

1. **bir2cir:** lower a `CharSequence` param/return/local **type token** to `System.String` (not the synthetic), in
   all non-user-supertype positions. Keep the synthetic only where a user class DECLARES `: CharSequence`.
2. **The implicit `.toString()` coercion:** where a non-`String` value whose static type is `CharSequence`/StringBuilder
   flows into a `string` slot, insert `.toString()` (the mirror of the 4-A `String→adapter` insertion, same static-flow
   analysis). A `String` needs no coercion.
3. **`subSequence`/`get`/`length` on a `CharSequence`-now-`string`** resolve to `System.String.Substring`/`get_Chars`/
   `Length` (already bound).
4. **Verify:** all String/text samples (`str`/`substr`/`char`/`fmt`/`regex`/`bmore`), the 5 previously-BLOCKED String
   ops, and **`il-charseq`/`il-charseqx` (user CharSequence — the synthetic must STILL work)** stay green; gate
   improves. Record the §3 snapshot-semantics deviation in `docs/dotkt-semantics.md`.
5. Retire whatever synthetic-CharSequence machinery is no longer reachable once stdlib params are `string` (but keep
   the user-implementer path).

## Status — what landed (2026-07-02)

**LANDED (bir2cir, gate-neutral):** the `CharSeqStringLowering` pass (`toolchain/bir2cir/Program.cs`) implements points
①/②/ for an **app build with NO user `class S : CharSequence`** (a "pure" assembly, decided by the driver's
`hasUserCharSeqImpl` scan of every type's `interfaces` for `<>dotkt_CharSequence`):

- a CharSequence-typed **param / return / local / field** declaration → `System.String`;
- a member read on such a now-`string` value — `cs.length` / `cs[i]` / `cs.subSequence(a,b)` (kotc emits these as a
  `callInstance` whose `ownerType` is the synthetic) → `System.String.Length` / `get_Chars` / `Substring(a, b-a)`;
- a **local** call (a top-level fn in `localTopLevelFns`) whose CharSequence `sig` slot is now `string` has its arg
  coerced — a `String` flows directly, a non-`String` (a `StringBuilder`) is snapshot with an implicit `.toString()`
  (an `objMethod ToString`, virtual). Same for a CharSequence return / local init / `as CharSequence` cast.

It runs BEFORE `StringCharSequenceBridge`, so a now-`string` value flowing into a *stdlib* CharSequence-extension (whose
param is still the synthetic in the un-rebuilt stdlib) is still adapter-wrapped by the bridge — **the two compose**
(verified: a pure-app `fun has(cs: CharSequence) = cs.contains(sub)` works for both a `String` and a `StringBuilder`).
Coverage: `tests/basic/fixtures/StringsTests.kt`. User `class S : CharSequence` cases stay on the
synthetic path unchanged (the per-assembly guard disables the pass) — both still green.

**RETAINED (technical necessity):** the synthetic `<>dotkt_CharSequence` + `<>dotkt_StringCharSequence` adapter +
`CanonicalSynthetics` machinery. Sealed `System.String` forbids a real `: string` supertype, so a user
`class S : CharSequence` MUST implement the synthetic. This is the point-1 EXCEPTION. `il-charseq`'s intra-assembly
polymorphic reads (`show(cs: CharSequence) = cs.length` with `show(S("hello"))` == 5; `val sub: CharSequence = c.subSequence(..)`)
genuinely require the synthetic — collapsing them to `string` + `.toString()` would snapshot `S` via its (default,
type-name) `toString` and lose the length. Hence: an assembly that declares a user implementer keeps `CharSequence`
polymorphic **assembly-wide** (the pass is skipped there).

**DEFERRED (follow-up — NOT done, needs a stdlib rebuild):** lowering the **stdlib's own** CharSequence-extension
signatures (`StringsKt.contains`/`trim`/… params) from the synthetic to `string`. That is the change that would let the
5 still-lowered String ops (`trim`/`reversed`/`padStart`/`replace(S,S)`/`isBlank`) retire cleanly to their stdlib bodies
(their blockers are synthetic-CharSequence↔StringBuilder interop inside those bodies). It requires: (a) rebuilding the
ref+rt stdlib with CharSequence→`string` (a multi-blocker emit grind), and (b) a cross-assembly call-site coercion so an
app calls the now-`string`-param stdlib ext with a `string` arg (a String directly, a non-String via `.toString()`),
replacing the adapter bridge for that path. Until then the 5 ops stay compiler-lowered and the stdlib CharSequence-ext
ABI stays synthetic — so this landed increment is confined to app-OWN CharSequence declarations and is a no-op on every
existing sample (none of them declare an app-own CharSequence signature outside the two `il-charseq*` cases).

## Open questions (decide during implementation)

- A user `class S : CharSequence` passed to a stdlib `CharSequence`(=`string`) param → implicit `.toString()` calls the
  user's `toString()` (correct, snapshot). Confirm a user CharSequence's own `toString()` is what materializes.
- `CharSequence?` (nullable) → `string?` = `string` (CLR ref, nullable) — trivial, no `Nullable<>` (it's a ref type).
- Does any current sample rely on live-view CharSequence semantics? (Expected: none.) Grep before landing.

# Primitive dual-representation — how kotlin.Int/Short/… emit on the CLR

Status: **design decision needed** (2026-06-28). This is the hardest type-representation problem in the stdlib; the
trigger is dotPeek showing the garbage `public class kotlin.Short : Number, Comparable<short>` (an empty shell) and the
primitive `toDouble` no-impl load errors.

## The symptom

`kotlin.Short` emits (CIR) as: `base = kotlin.Number`, `interfaces = [kotlin.Comparable[short]]`, **0 methods, 0 fields**.
Two things are wrong:
1. The supertype's type arg `Short` is BCL-lowered to `short` (System.Int16) -> `Comparable<short>` instead of Kotlin's
   self-referential `Comparable<Short>`.
2. The members (`toDouble`, `plus`, `compareTo`, …) are dropped -> empty shell -> `Number`'s abstract conversions are
   unfilled -> load failure.

## Why it's hard — the facts

- **Arithmetic is IL, not method calls.** kotc lowers `a + b` to a `{"k":"bin","op":"+"}` node; ilemit emits IL `add`,
  which requires the operands to be a CLR primitive (System.Int32). So a `kotlin.Int` value MUST be System.Int32 at the
  IL level.
- **The primitive maps are pervasive.** `birType`/`netType` map `kotlin.Int -> int`/`System.Int32`, `kotlin.Short ->
  System.Int16`, … — used for every parameter/field/local/generic-arg of a primitive type. They are correct at VALUE
  positions (and needed for IL).
- **The clr actuals declare members with NO body** (`class Byte private constructor() : Number(), Comparable<Byte> {
  fun toDouble(): Double … }` — Step-1 stubs, bodies `TODO`). kotc filters bodyless non-abstract methods -> the shell.
- **`kotlin.Number` has NO BCL equivalent.** CLR's numeric value types (Int32, …) share no common `Number` base. So
  `kotlin.Number` must be a real emitted type, and `Byte : Number` only holds if `Byte` is a type that extends it.

## The fork

The current state is an **incoherent hybrid**: kotc emits a `kotlin.Short` TYPE (so it can sit under `Number`) while
ALSO mapping `Short -> System.Int16` at references (so arithmetic works). The shell is the collision of the two.

- **Model A — primitives ARE the BCL types.** Don't emit `kotlin.Int/Short/…` as types; `kotlin.Int` === System.Int32
  everywhere. Arithmetic = IL (unchanged). Simple, and the shells/`Comparable<short>`/`toDouble` errors vanish (the
  types don't exist to fail). **BUT it breaks the `Number` hierarchy**: `System.Int32` does not extend `kotlin.Number`,
  so `fun f(n: Number)` can't take an `Int`. (Mitigations: box to `kotlin.Number` at the Number-typed boundary, or map
  `Number` to `System.object`/`System.IConvertible` and lose the Kotlin contract.) Not "pure kotlin.*"
  (contradicts [[clr-stdlib-grand-strategy]]).
- **Model B — primitives are PURE kotlin.* types** (the grand strategy; "BCL maps OFF under DOTKT_STDLIB_COMPILE").
  `kotlin.Int : Number, Comparable<kotlin.Int>` is emitted faithfully; the BCL substitution (`kotlin.Int -> System.Int32`)
  happens at APP-emit time. **Requires** (a) the app-time substitution machinery (memory: "NOT YET IMPLEMENTED"), and
  (b) a value model where a `kotlin.Int` value is NOT System.Int32 during stdlib build — so arithmetic can't be IL
  `bin`-on-Int32; it must be method calls (`Int.plus`) OR ilemit must resolve `@kotlin.Int -> System.Int32` for IL while
  keeping `@kotlin.Int` for type metadata (which collapses the separate type unless very carefully scoped).

## Recommendation

The grand strategy commits to **Model B**, and the dotPeek garbage is exactly Model B done halfway. But full Model B is
a major project (substitution machinery + value/arithmetic rework). Proposed phasing:

1. **Stop the incoherent hybrid first (cheap, unblocks load):** under DOTKT_STDLIB_COMPILE, make the primitive-type
   emission COHERENT. Two candidate cheap fixes, pick one:
   - **B-lite (keep the phantom type, fix it):** emit `kotlin.Short`'s supertype self-reference as `@kotlin.Short` (not
     `short`) and emit its members (don't filter the bodyless conversions — emit them as `@Clr`-bound or abstract-filled
     stubs). The type becomes a valid (phantom) `Number` subtype. Keeps BCL maps at value positions.
   - **A-lite (drop the phantom type):** under stdlib-compile, DON'T emit the primitive type definitions at all; they map
     to BCL. Resolves the shells immediately; defer the `Number`-hierarchy hole to when Number-typed stdlib code actually
     exercises it.
2. **Then the real Model B** (separate, large): the @Clr app-time substitution pass + the value/arithmetic model, so the
   stdlib is genuinely pure-kotlin and an app rewrites `kotlin.Int -> System.Int32` at emit.

The choice between B-lite and A-lite (and committing to full B later) is the design owner's — it's the pure-kotlin-stdlib
philosophy vs. pragmatic-BCL-alias, and it sets how `Number`/boxing/reflection behave. See also
[[dual-representation-stdlib-types]], [[four-layer-purpose-retire-intrinsics]].

## The general rule (design owner, 2026-06-28): preserve the primitive in TYPE-ARGUMENT positions

Supertype is just one case; the general rule is **a primitive in a TYPE-ARGUMENT position keeps its `kotlin.*` type
(`@kotlin.Int`), a primitive in a BARE value position lowers to the BCL primitive (System.Int32)**:

| position | form | example |
|---|---|---|
| bare value (param / local / field / return / arithmetic) | **BCL primitive** | `fun f(x: Int)` -> `x: System.Int32`; `a+b` -> IL add |
| type ARGUMENT (generic arg, incl. a supertype's args) | **`@kotlin.*`** | `Comparable<Int>` -> `Comparable<@kotlin.Int>`; `List<Int>` -> `List<@kotlin.Int>` |

This is exactly the **JVM boxing boundary** (`int` bare vs `Integer` in generics) — and it falls out as a *consequence*,
not a free choice: `kotlin.Number` is an abstract CLASS, a CLR struct cannot extend a class, so a `kotlin.Int` that must
satisfy `Int : Number` (and `Comparable<Int>` over ITSELF) has to be a **reference type** — i.e. the boxed form — exactly
where it appears as a type (generic args). So:
- `kotlin.Int/Short/…` ARE emitted as real reference types extending `Number`, implementing `Comparable<self>`, carrying
  their conversion members — used wherever a primitive appears as a TYPE ARGUMENT.
- bare primitive values stay `System.Int32` (IL-native arithmetic, no boxing) — the CLR efficiency is kept off the
  generic boundary.
- **kotc inserts box/unbox at the boundary**: passing a bare `Int` where `@kotlin.Int` is wanted (into `List<Int>.add`,
  into a `Comparable<Int>` slot) boxes System.Int32 -> kotlin.Int; reading it back unboxes.

Trade-off: this re-accepts boxing AT THE GENERIC BOUNDARY (a JVM-ism CLR could avoid with `List<int>`), in exchange for a
coherent `Number` hierarchy, self-referential `Comparable`, and faithful Kotlin reflection. It is the pragmatic Model-B:
the primitive types are pure `kotlin.*` reference types in the type system, BCL only at bare-value/IL positions. RECOMMENDED.

**Implementation sketch:** in `birType`/`netType`, thread a "type-argument position" flag — a primitive at a type-arg
position emits `@kotlin.Int`; bare stays `int`/`System.Int32`. Emit the primitive type definitions (Number subtypes,
Comparable<self>, conversion members — with bodies that box/convert). Insert box/unbox coercions where a bare value
crosses into a type-arg slot and back. (Supertype args are just the first type-arg case this covers.)

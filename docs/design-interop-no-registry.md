# Design: .NET interop without the static registries

> **Status: design (2026-07-05).** Supersedes the four process-global `object` registries in
> `toolchain/kotc/src/main/kotlin/kotc/ClrTypeRegistry.kt`. Derived from a design review (user-led,
> Socratic): the registries are an anti-pattern; this doc is the target state and the staged path to it.
> This is the concrete design for **bundle-8 A2 (the "kotc purity" keystone)**.

## The problem: four name-keyed side-channels

The FIR type-injection extension (`ClrTypeInjector : FirDeclarationGenerationExtension`) synthesizes
.NET interop symbols into FIR, and passes per-symbol CLR facts to the backend (`BirEmitter`) through
four **process-global mutable `object`s** keyed by **name**:

| registry | maps | consumed by |
|---|---|---|
| `ClrTypeRegistry.typeNames` | Kotlin type FQN → .NET type name | `BirEmitter.clrName` (type token) |
| `ClrTypeRegistry.memberNames` | member FQN (`…Collection.size`) → BCL name (`Count`) | `BirEmitter.clrName` (member) |
| `ClrTopLevelRegistry` | top-level fun FQN → **[** (file-class, recvDisc, suspend) **]** | `BirEmitter` call path |
| `ClrEventRegistry` | `owner#add_<E>` → (event name, `+=`/`-=`) | `BirEmitter` call path |

### Why they are wrong

1. **They re-key a *resolved* fact by *name*, discarding FIR's resolution.** By the time `BirEmitter`
   runs, FIR/Fir2Ir has already resolved every call to a **unique callee IR symbol**. `ClrTopLevelRegistry`
   throws that away — it keys by the plain name (`reversed`), which collides across file classes
   (`_CollectionsKt`/`_StringsKt`/`_ArraysKt`), and then re-disambiguates with a **receiver discriminator
   kludge** (whose own comment admits "last-registered wins"). The ambiguity is *self-inflicted*: the
   resolved `call.symbol.owner` is already unique.

2. **The "FIR annotations don't survive Fir2Ir" rationale is mostly false.** The injection generates real
   FIR **declarations** (ClassIds, callable symbols); Fir2Ir converts them to **IR declarations** — that
   survival is *how `BirEmitter` sees a `System.*` type at all*. So the **structural identity survives**:
   - a type's .NET name **is its IR `ClassId`** (`ClrTypeRegistry`'s own comment: the key "matches the
     injected FIR ClassId") — the type-name map is a near-**identity** indirection;
   - a top-level call's target **is its resolved IR callee symbol**.
   Only *detached extra metadata* (arbitrary annotations) is brittle across Fir2Ir — not the symbol.

3. **Process-global mutable state assumes a single JVM process** (the header says so). It breaks under a
   compiler daemon / parallel / multi-module compilation (cross-compilation contamination, no isolation).

4. **They leak CLR naming conventions into kotc.** `add_`/`remove_` (events), `get_Count`/`get_Item`
   (members) are *.NET* spellings. kotc emitting or keying on them violates the binding invariant
   "kotc reads/emits pure Kotlin identity; the CLR resolution lives in bir2cir."

## The unifying principle

> **kotc emits Kotlin identity plus classification *hints* only. How to bind any of it to the CLR —
> type, top-level target, member slot, event — is bir2cir's job, decided from the referenced-assembly
> metadata. No name-keyed process-global side-tables; no CLR naming convention (`add_`, `get_`) in kotc.**

This is the same mechanism already proven for the stdlib (`@ClrTypeAlias`/`@ClrIntrinsic` read from the
ref.dll by bir2cir), **generalized to user .NET interop**. The dual identity "Kotlin name ≠ .NET name" is
real *only for the stdlib* (`kotlin.Int` ≠ `System.Int32`); a user .NET type **is** its .NET self, so its
FQN already **is** the .NET name.

## Target state, per registry

| registry | replaced by |
|---|---|
| type name | `BirEmitter` reads the IR **`ClassId`** directly (it already **is** `System.Text.StringBuilder`). Arity (`Task\`1`↔`Task`) is a mechanical strip/re-append ilemit already does. No map. |
| member slot | `@ClrIntrinsic`/`@ClrProperty` on the member (read from the ref by bir2cir) — the stdlib mechanism, extended. |
| top-level | kotc emits the call to the **resolved callee** (unique); the file-class comes from that callee's IR symbol (its container/origin), **not** a name lookup — so no collision, no receiver discriminator. |
| event | kotc emits `plusAssign`/`minusAssign` **plus an "event" hint**; **bir2cir decides the CLR binding** (mechanism is bir2cir's to design at implementation — e.g. resolving the .NET `EventInfo` from metadata). kotc never spells `add_`/`remove_`. |

### The event boundary (worked, since it is the sharpest)

- **kotc** emits the pure Kotlin operator identity: `plusAssign(button.Click, handler)` — operator =
  `plusAssign`/`minusAssign`, event name = `Click`, owner = button's type — **plus a hint that the member
  is an event** (a Kotlin-level classification, not a CLR decision). kotc does **not** produce `add_Click`
  and does **not** know the `plusAssign → add` convention.
- **bir2cir** takes the hint and **designs how to bind it to the CLR** — reading `plusAssign → add` as the
  .NET convention, resolving the actual accessor from the event's metadata. *How* bir2cir models this
  (a `clrEventAdd` relation node, direct `EventInfo` resolution, …) is bir2cir's call, not prescribed here.
- Result: the synthesized `add_<E>`/`remove_<E>` methods, the `add_`/`remove_` strings, and
  `ClrEventRegistry` all disappear; a .NET event is modelled as an ordinary Kotlin `plusAssign` operator
  (MEMORY `clr-not-jvm-discard-jvmisms` — model idiomatically on the CLR, don't reproduce accidental
  method-synthesis).

## Staged implementation (each stage keeps the gate XFAIL-zero)

1. **Type-name spike:** replace `BirEmitter`'s `ClrTypeRegistry` type lookup with a direct IR **`ClassId`**
   read; prove equivalence (net/interop samples green, arity intact via ilemit). Delete `typeNames`.
2. **Member slot:** confirm the member map is redundant with `@ClrIntrinsic`/`@ClrProperty` (family-3
   already found the collection/StringBuilder maps DEAD); delete `memberNames`.
3. **Top-level:** carry the resolved callee's file-class off the IR symbol (not the name); drop the
   candidate list + receiver discriminator. Verify `reversed`-family overloads resolve 1:1.
4. **Event:** kotc emits `plusAssign`/`minusAssign` + the event hint; bir2cir binds it; delete
   `ClrEventRegistry` and the `add_`/`remove_` method synthesis.

## Caveats to verify during implementation

- Does `BirEmitter` reading the raw `ClassId` reproduce every case the registry covered (nested types,
  generic arity qualification, name-family renames like `Task1`)? Where a facadegen **rename** genuinely
  diverges from the ClassId, that is a real fact to carry — but on the **IR symbol**, not a name map.
- Can bir2cir resolve a **user** referenced assembly's metadata (not just the stdlib ref.dll) on the same
  path it resolves `@ClrTypeAlias`/`@ClrIntrinsic`? facadegen owns ".NET metadata → fact"; after the
  registries go, facadegen should surface the fact **into the BIR / onto the IR symbol**, not a static map.
- The top-level DotKt round-trip (`[KotlinFile]`/suspend flags): the file-class + suspend-ness must reach
  bir2cir off the resolved symbol; confirm they survive Fir2Ir on the injected declaration.

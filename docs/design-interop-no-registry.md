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
| event | kotc emits `plusAssign`/`minusAssign` on a receiver **typed `ClrEvent<T>`** — the *type itself is the classification*, no out-of-band hint. **bir2cir recognizes `ClrEvent<T>` (by FQN, like any type) and decides the CLR binding** (mechanism is bir2cir's to design — e.g. resolving the .NET `EventInfo` from metadata). kotc never spells `add_`/`remove_`. |

### The event boundary (worked, since it is the sharpest)

- **kotc** emits the pure Kotlin operator identity: `plusAssign(button.Click, handler)` — operator =
  `plusAssign`/`minusAssign`, event name = the member name `Click`, owner = button's type. The receiver
  `button.Click` is **typed `ClrEvent<EventHandler>`** — and *that type is the whole signal*: no separate
  "event" hint is needed, because bir2cir reads types to decide bindings anyway (the type identity subsumes
  the classification, exactly as a type's `ClassId` subsumes its .NET name). kotc does **not** produce
  `add_Click` and does **not** know the `plusAssign → add` convention — it just types the member.
- **bir2cir** recognizes the `ClrEvent<T>` receiver type (by FQN, like any recognized type) and **designs
  how to bind it to the CLR** — reading `plusAssign → add` as the .NET convention, resolving the actual
  accessor from the event's metadata. *How* bir2cir models this (a `clrEventAdd` relation node, direct
  `EventInfo` resolution, …) is bir2cir's call, not prescribed here.
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

---
## ClrEvent<T> idiomatic redesign — implementation plan (2026-07-05, user-approved) — ✅ IMPLEMENTED 2026-07-05

> **Landed.** `kotlin.clr.ClrEvent<T>` + its `plusAssign`/`minusAssign` member operators are injected by
> `ClrTypeInjector` (kotc), a .NET event surfaces as a `ClrEvent<HandlerFn>` property, and bir2cir's
> **`ClrEventOperatorBinding`** pass binds the plain `plusAssign`/`minusAssign` operator call to the existing
> `clrEventAdd`/`clrEventRemove` node (ilemit unchanged). The `add_<E>`/`remove_<E>` synthesis + `ClrEventRegistry`/
> `eventOpByCallableId` side-table + the kotc `add_X`→`clrEventAdd` rewrite are all deleted. Emitted add/remove
> accessor IL is identical; `cases/il-event` + `cases/ktproj-extlib` use the `+=`/`-=` form. Gates: verify-il 201/0 ·
> 159/0, ktproj 9/9, differential ALL MATCH.


Completes the INTENDED `w.Changed += handler` design that `cases/il-event/app.kt`'s own comment names but
that was never wired — the `add_<E>`/`remove_<E>` synthesized-method model shipped as the stopgap (a relic,
like the Lazy→System.Lazy hardcode). Goal: a .NET event subscribes via the idiomatic Kotlin `+=`/`-=`
operators; the `add_` naming disappears from user code.

### The key trick: ClrEvent<T> is a compile-time lvalue fiction bir2cir sees through
A .NET event is NOT a first-class value (you can only add/remove/raise it). So `w.Changed` does NOT
materialize a `ClrEvent<T>` object at runtime — it is a compile-time handle. `w.Changed += h` desugars
(normal Kotlin operator resolution) to `plusAssign(w.Changed, h)`; bir2cir PATTERN-MATCHES
`plusAssign(<memberAccess w "Changed">, h)` and emits the existing `clrEventAdd(owner=w, event="Changed", h)`
CIR node (ilemit already emits it). Owner + event name come straight from the `w.Changed` member-access node.
The `ClrEvent<T>` value is never emitted — same idea as a `ClrRef`/byref lvalue.

### Per-layer
- **facadegen** (NOT stdlib — user-corrected 2026-07-05): INJECT `ClrEvent<T>` + `operator fun <T>
  ClrEvent<T>.plusAssign(handler: T)` / `minusAssign(handler: T)` as SYNTHETIC FIR symbols, with NO body.
  There is no runtime implementation — bir2cir rewrites `plusAssign(w.Changed, h)` to `clrEventAdd` before
  emit, so the operator is compiler-consumed only and never executes. So `ClrEvent<T>` must NOT be a shipped
  stdlib type (no stub in DotKt.Stdlib.dll) — it is a pure frontend-resolution fiction, exactly the kind of
  synthetic symbol facadegen already injects (and facadegen can restore `operator` from Roundtrip attributes,
  per CLAUDE.md). The type is only the receiver type that makes `+=` resolve; its FQN is the signal bir2cir
  keys on. **stdlib is untouched.**
- **facadegen**: (a) surface a .NET event `Changed` as a MEMBER `Changed: ClrEvent<HandlerDelegate>`; (b)
  INJECT the `ClrEvent<T>` type + its `plusAssign`/`minusAssign` operators (synthetic, no body — see stdlib
  note below); (c) DROP the synthesized `add_<E>`/`remove_<E>` method injection.
- **kotc**: nothing special — `w.Changed += h` resolves through normal operator resolution to
  `plusAssign(w.Changed, h)`; kotc emits that plain call (the ClrEvent<T> member access + the operator). No
  `add_`/`remove_` names, no event registry (A2 already removed it).
- **bir2cir**: recognize `plusAssign`/`minusAssign` whose receiver is a `ClrEvent<T>` member-access → emit
  `clrEventAdd`/`clrEventRemove` (owner + event name from the member-access node; the handler is the arg).
  Reuses the existing ilemit `clrEventAdd`/`clrEventRemove` emission — no ilemit change.
- **cases**: rewrite `il-event/app.kt` to `c.CollectionChanged += { … }` / `-= h`; ktproj-extlib similarly.

### Verify: `il-event` + `ktproj-extlib` (real .NET event interop) run identically (same add/remove accessor
calls at IL level), gate XFAIL-zero. The USER-FACING syntax changes (`add_X(h)` → `X += h`); the emitted IL
is the same add/remove accessor call.

### Caveats
- `-=` needs delegate equality for removal (the stored-handler case) — the existing `remove_` path already
  handles it; the `minusAssign` rewrite must preserve the same handler-identity semantics.
- A handler that is a Kotlin lambda binds to the event's own delegate type (existing behavior) — the
  `plusAssign(handler: T)` where T = the delegate type keeps that.

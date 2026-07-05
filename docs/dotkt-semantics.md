# DotKt semantics — how Kotlin maps to the CLR, and where it deliberately differs from Kotlin/JVM

DotKt compiles Kotlin to a normal .NET assembly. A lot of Kotlin's surface is **JVM-shaped accidental complexity**
(erased generics, `@Metadata`, the Continuation ABI, JVM string conventions). On the CLR, DotKt **reinterprets or
discards** those rather than reproducing them. This page is the canonical list of those behavioral differences and
non-obvious interpretations — the things a Kotlin/JVM developer would otherwise be surprised by. Feature-by-feature
deep dives are linked per section.

Guiding principle: *Kotlin carries JVM accidental complexity; on the CLR, identify it and discard it — don't
reproduce it.* (Memory `clr-not-jvm-discard-jvmisms`.)

> Looking for the friendly tour instead of the canonical reference? Read
> [`docs/user/kotlin-on-clr-differences.md`](user/kotlin-on-clr-differences.md) first.

**Contents**

| § | Deviation |
|---|---|
| [1](#1-kotlin-packages--net-namespaces) | Kotlin packages → .NET namespaces |
| [2](#2-generics-are-reified--so-kotlin-reified-is-almost-a-no-op) | Reified generics — `reified` is (almost) a no-op |
| [3](#3-inline-happens-at-emit-time-and-is-decoration-unless-a-lambda-literal-is-passed) | `inline` is emit-time, decoration unless a lambda is passed |
| [4](#4-suspend-fun--an-async-taskt-function-hot-not-cold) | `suspend fun` = an async `Task<T>` function; **hot, not cold** |
| [5](#5-primitive-stringification-is-clr-native-not-kotlinjvm-cosmetics) | CLR-native stringification; `String.format` = .NET composite format |
| [5b](#5b-charsequence-is-string-on-the-clr--an-immutable-snapshot-not-a-live-view) | **`CharSequence` is `string`** — snapshot, not live view |
| [5c](#5c-mapmutablemap-both-erase-to-idictionarykv--read-only-ness-is-frontend-enforced) | `Map`/`MutableMap` → `IDictionary<K,V>` |
| [5d](#5d-appendable-is-systemtextstringbuilder) | `Appendable` is `System.Text.StringBuilder` |
| [5e](#5e-enum-classes-have-two-clr-shapes) | Enum classes: basic → real CLR `enum`, rich → singleton class |
| [5f](#5f-value-class-is-a-real-wrapper-class-never-erased) | `value class` = a real class (never erased) |
| [6](#6-consuming-a-dotkt-assembly-as-kotlin--what-rides-metadata-vs-needs-an-attribute) | Round-trip: what rides metadata vs. needs an attribute |
| [7](#7-default-arguments--a-two-tier-rule-native-metadata-else-a-carried-bir-expression) | Default arguments — the two-tier rule |
| [8](#8-reverse--cross-assembly-interop) | Reverse / cross-assembly interop |
| [8b](#8b-dual-representation-import-systemtextstringbuilder-vs-kotlintextstringbuilder--two-typed-views-of-one-clr-type) | Dual view: imported .NET type vs. its stdlib alias |
| [8c](#8c-injected-net-static-members-implicit-typemember-works-companion-optional) | Injected .NET statics: implicit `Type.member` works |
| [8d](#8d-net-events-subscribe-with-the-idiomatic---operators) | .NET events subscribe with `+=` / `-=` |
| [9](#9-reference-type-nullability--net-nrt-un-annotated-net-types-are-platform-types) | Nullability ⇔ .NET NRT; platform types `T!` |
| [10](#10-round-trip-fidelity-audit--what-re-consuming-a-dotkt-assembly-as-kotlin-loses) | Round-trip fidelity audit (incl. pinned-2.2.0 limitations) |

---

## 1. Kotlin packages → .NET namespaces

- **JVM:** package = a path; a class's binary name is `pkg/Name`. The package always survives.
- **DotKt:** a Kotlin package is projected to the **.NET namespace** — `package geom; class Vec` emits `.NET geom.Vec`,
  and the file-facade class is `geom.<File>Kt`. Nested types stay **simple-named** (their outer type carries the
  namespace, i.e. `geom.Outer+Inner`). Root-package code is unchanged (no namespace).
- **Why it matters:** without this, two classes with the same simple name in different packages (`alpha.Box` +
  `beta.Box`) would both emit `.NET Box` and **collide** — a hard error. It's also the prerequisite for consuming a
  packaged DotKt library across an assembly boundary (`import geom.Vec`).
- Gotcha: this is recent (2026-06-24). The injector derives the Kotlin package from the .NET namespace, so a
  consumer's `import geom.Vec` resolves only because the emit side now qualifies the name.

## 2. Generics are reified — so Kotlin `reified` is (almost) a no-op

- **JVM:** generics are **erased**. `reified` exists only to recover the type argument at the call site, and it
  *requires* `inline` (the type is baked in during inlining). You can't call a reified function non-inlined, and you
  can't pass a non-reified type parameter to a reified one.
- **DotKt:** the CLR has **real reified generics**. `inline fun <reified T> foo()` is emitted as an ordinary generic
  method `foo<T>()`; the body's `T::class` / `is T` / `as? T` become `typeof(T)` / type checks on the real runtime
  type. So:
  - `reified` is **decoration** — DotKt drops it; the function is just a generic method.
  - Dropping it *removes* the JVM constraint: a consumer can pass a **non-reified** type parameter
    (`fun <U> bar() = foo<U>()` is fine on the CLR, an error on the JVM).
  - There is **no `@Metadata`/reified attribute** to round-trip.
- Deep dive: §3 (inline), `docs/design-il-generics.md`, memory `function-inlining-spike`.

## 3. `inline` happens at EMIT time, and is decoration unless a lambda literal is passed

This is the single most surprising deviation, so it gets the most detail.

- **JVM:** inline functions are inlined during a frontend/IR lowering; the body is also serialized into `@Metadata`
  so other modules can re-inline at *their* call sites.
- **DotKt pipeline (four layers: `facadegen` / `kotc` / `bir2cir` / `ilemit`).** The
  frontend is `…Fir2Ir then ClrBackendPhase` — **there is NO JVM `FunctionInlining` lowering.** The IR that reaches the
  backend still has un-inlined `inline` calls. **Inlining (and the `[KotlinInline]` splice) is a `bir2cir` (BIR→CIR)
  responsibility — currently still partly in `BirEmitter`, being migrated** (`ilemit` is meant to be Kotlin-free):
  ```
  call() → if (callee.isInline && callee.body != null && hasLambdaArg(call)) inlineCall(call)  // splices the IR body
  ```
- Consequences:
  - **`inline` and `reified` are pure decoration UNLESS the call passes a lambda LITERAL.** A lambda-less `inline fun`
    (`inline fun twice(x: Int) = x + x`) is emitted as an ordinary method and called normally — the JIT inlines it.
    The modifier does nothing in DotKt's own codegen.
  - Same-module inline with a lambda (incl. **non-local return** and **crossinline**) works — the IR body is present
    and spliced (`il-inline`, `il-inline2`, `il-xinline`).
  - **Cross-module:** an injected stub has `body == null`, so it's never the IR-splice case. Lambda-less / no-non-local-
    return inline degrades to a plain (or generic) call — correct. The ONE case that can't degrade is a **non-local
    `return` through a lambda** (it must return from the *caller's* frame, which only inlining achieves).
- Cross-module non-local-return IS supported (2026-06-24), and — because inlining is over (near-)BIR — it's
  **much lighter than JVM's `@Metadata`** (no IR deserializer): `[KotlinInline(birJson)]` carries the function's own
  BIR body, the consumer's `bir2cir` reads it from the `--ref`'d assembly and splices it before codegen (a `return`
  in the spliced lambda body becomes the caller's `ret`; the splice still runs partly in `BirEmitter` today, being
  migrated into `bir2cir`, since `ilemit` is meant to be Kotlin-free). Full mechanism + scope in
  `docs/design-kotlin-metadata-attributes.md`.
- Pitfall (verified, do NOT do this): marking an injected body-less function `inline` *without* carrying the body lets
  the frontend accept a non-local return but leaves nothing to splice → `InvalidProgramException` at runtime (worse
  than the clean compile error). `inline` restoration and the carried body are a package deal.

## 4. `suspend fun` = an async `Task<T>` function; hot, not cold

**A `suspend` function IS, on the CLR, an async function returning `Task<T>`, and a suspend CALL is an `await`.**
This is a user-stated foundational deviation, not an implementation detail.

- **JVM:** a `suspend fun f(): T` compiles to `Object f(…, Continuation)` — the Continuation is an explicit parameter,
  CPS the public ABI.
- **DotKt:** the **public CLR ABI is `Task<T> f(…)`** — the Continuation never appears in the signature (it's the
  internal lowered form, with a `Task` sink). Calling a suspend function from suspend context is emitted as an
  **await** of that `Task<T>`. A C# caller `await`s it natively; a Kotlin caller in another module sees a
  `suspend fun` again (restored from a `[KotlinFunction(Suspend)]` attribute, with the `Task<T>` unwrapped to `T`).
- **Execution is HOT, not cold.** A .NET `Task` starts running when created — so invoking a suspend function starts
  its execution immediately, exactly like C# `async`. This deliberately deviates from the kotlinx-coroutines
  cold-by-default framing (a `suspend` body that only runs when awaited/launched). Kotlin's *language* semantics for
  a direct suspend call (call = run to the first suspension and continue) are preserved; what does NOT carry over is
  the kotlinx-style cold-start expectation around builders. Structured concurrency (`Job`/`CoroutineScope`) is a
  separate later track.
- Gotcha: a member `suspend fun` returning a **user type** drove out a Reflection.Emit limitation
  (`AsyncTaskMethodBuilder<UserT>` is a TypeBuilder instantiation) — fixed by re-anchoring those members via
  `TypeBuilder.GetMethod`.
- Deep dives: `docs/coroutine-abi.md` (the ABI contract), `docs/design-coroutines-clr.md` (design + Track-2 plan),
  `docs/coroutine-stdlib-port-plan.md` (the live implementation plan), memory `coroutine-abi-decision`.

## 4b. The default `lazy { }` is thread-safe (a Monitor lock, matching Kotlin/JVM and `System.Lazy`)

`lazy { }` on DotKt is thread-safe by default, exactly as on Kotlin/JVM (and matching .NET's own
`System.Lazy<T>` default of `LazyThreadSafetyMode.ExecutionAndPublication`). The mode → implementation
map (stdlib `libraries/stdlib/clr/kotlin/util/LazyClr.kt`):

- **default `lazy(initializer)`**, **`SYNCHRONIZED`**, **`PUBLICATION`**, and **`lazy(lock, initializer)`**
  → `SynchronizedLazyImpl` — memoization guarded by a `System.Threading.Monitor` lock (the stdlib
  `@ClrIntrinsic` `monitorEnter`/`monitorExit` in `kotlin.concurrent.atomics`; no compiler lowering).
  `PUBLICATION` is served — if more strictly than the spec requires (it permits multiple initializer
  runs) — by the same single-init locked impl; this is correct, only conservatively so.
- **`NONE`** → `UnsafeLazyImpl` (no synchronization), as on JVM.

**Locking discipline — always-lock, no lock-free fast path (deliberate).** `SynchronizedLazyImpl`
takes the Monitor lock on *every* `value` read; it does **not** use the classic double-checked-locking
lock-free fast path that Kotlin/JVM's `SynchronizedLazyImpl` uses. Reason: the JVM DCL fast path is
memory-safe only because it reads the value field through a `@Volatile` access; on the CLR
`@kotlin.concurrent.Volatile` is currently a **no-op annotation** (no binding to a volatile field
access), so a DCL fast path would have a subtle publication bug on weak-memory architectures (ARM)
that a single-threaded test can never surface. Correctness over speed: always-locking is
unconditionally correct. The initializer runs inside the locked critical section, and the lock is
released in a `finally` so a throwing initializer cannot leak the lock. (The value field is published
by a single reference-typed write of the fully-constructed value — atomic on .NET — so no torn value
is ever observed.) If a volatile field binding lands later, the DCL fast path can be restored for
lock-free reads after initialization.

## 5. Primitive stringification is CLR-native (not Kotlin/JVM cosmetics)

- A DotKt program IS a .NET program, so it follows the **host's** conventions: `println(true)` → `True` (not `true`),
  `println(4.0)` → `4` (not `4.0`). Kotlin's `true`/`4.0` are JVM/JS inherited cosmetics, not language essence.
- The JVM differential harness (`verify-differential.sh`) normalizes these cosmetic differences and checks the logic.
- Memory `clr-native-primitive-formatting`.
- **`String.format` exists on DotKt as platform API (like the JVM has its own; Native/JS have none), but uses the
  .NET composite format (`"{0} items"`, `"{0:D5}"`, `"{0,-4}"`), not Java printf (`"%d"`)** — the same
  host-convention family as the stringification above. Both shapes are provided (`String.format(fmt, args...)`
  and `"fmt".format(args...)`), bound to `System.String.Format` (stdlib `@ClrIntrinsic`, no compiler lowering).

## 5b. `CharSequence` is `string` on the CLR — an immutable snapshot, not a live view

`kotlin.CharSequence` is a JVM-shaped polymorphic char view with **no faithful .NET equivalent** (`System.String` is
sealed; `System.Text.StringBuilder` shares no indexed-char interface with it; `ReadOnlySpan<char>` — the only real .NET
"char view" — is a `ref struct` that cannot be a general parameter/field/generic-arg type). Per the project philosophy
(*Kotlin carries JVM accidental complexity; on the CLR, identify and discard it*), DotKt **models `CharSequence` as
`System.String`** at the CLR boundary. Consequences (a deliberate, declared deviation — like CLR-native
stringification above):

- A `CharSequence`-typed **parameter / return / local** is emitted as `System.String`; its member reads
  (`length` / `get`/`[]` / `subSequence`) resolve to `System.String.Length` / `get_Chars` / `Substring`. In C# a
  DotKt `fun f(cs: CharSequence)` therefore surfaces as `void f(string cs)` — clean interop, no synthetic type.
- A **non-`String` `CharSequence` value** (a `StringBuilder`, any other char sequence) flowing into a `CharSequence`
  (= `string`) slot is coerced with an **implicit `.toString()` snapshot** at the boundary. A `String` flows directly.
- Therefore `CharSequence` has **`string` (immutable-snapshot) semantics** — the JVM "live view" (mutating a
  `StringBuilder` after passing it as a `CharSequence` and observing the change through the parameter) is
  **NOT supported**. It is honest because it is declared, not hidden.
- The one construct that keeps a synthetic interface: a user-declared **`class S : CharSequence`**. Sealed
  `System.String` cannot be a supertype, so such a class implements a synthetic monomorphic `<>dotkt_CharSequence`
  interface, and an assembly that declares one keeps `CharSequence` polymorphic assembly-wide (so a
  `show(cs: CharSequence) = cs.length` still dispatches to the user impl). Passing a user `S` into a *different*
  assembly's `CharSequence` (= `string`) slot still snapshots it via `.toString()`.
- Design + layer plan: `docs/design-charsequence-clr-string.md`. Implemented in bir2cir (`CharSeqStringLowering`,
  app builds without a user implementer). The **stdlib's own** CharSequence-extension signatures are not yet lowered to
  `string` (a follow-up needing a stdlib rebuild); they still route through the `<>dotkt_CharSequence` adapter bridge.

## 5c. `Map`/`MutableMap` BOTH erase to `IDictionary<K,V>` — read-only-ness is frontend-enforced

Kotlin's `MutableMap : Map` subtype relation does **not** exist between the BCL's dictionary interfaces
(`IDictionary<K,V>` does not extend `IReadOnlyDictionary<K,V>`), so the List-style split alias
(`Map→IReadOnlyDictionary` + `MutableMap→IDictionary`) would make every `MutableMap`-value-into-`Map`-slot store
formally unverifiable — on the hot path (`Map.get` on a mutable receiver, `associateTo`'s `M : MutableMap`). DotKt
therefore aliases **BOTH `Map` and `MutableMap` to `System.Collections.Generic.IDictionary`** — exactly Kotlin/JVM's
own model (both erase to `java.util.Map`), and the in-repo precedent of `Iterable`/`MutableIterable` → `IEnumerable`.
Consequences (deliberate, declared):

- **A Kotlin `Map` surfaces to C# as a mutable `IDictionary<K,V>`** (concrete values are `Dictionary<K,V>`); Kotlin's
  read-only-ness is enforced by the Kotlin FRONTEND only, not the CLR type. `emptyMap()` returns a fresh
  Dictionary-backed map (the pure-Kotlin `EmptyMap` singleton cannot satisfy the IDictionary surface).
- **`Map.get` is null-on-missing** (Kotlin semantics), synthesized as `ContainsKey` + `get_Item` in
  `kotlin.collections.ClrMapDefaults` (`IDictionary`'s raw indexer throws); `put`/`remove` return the previous value
  the same way. `size`/`containsKey`/`clear` bind 1:1 (`Count`/`ContainsKey`/`Clear`).
- **`Map.keys`/`values`/`entries` are SNAPSHOTS, not live views** (Kotlin's are live): `keys`/`entries` return a
  pure-Kotlin `Set` (the BCL `KeyCollection` cannot implement the unaliased `kotlin.collections.Set`), `values` a
  BCL List. Entry VALUES are live (`entry.value`/`setValue` read/write through the backing map), but a key
  added/removed after taking the view is not reflected in it. `MutableMap.keys`/`values` bind directly to
  `IDictionary.Keys`/`.Values` (their `MutableSet`/`MutableCollection` slots lower to `ICollection`, which
  KeyCollection/ValueCollection implement); mutating THOSE views does not write back either (BCL contract: they throw).
- **`MutableMap.iterator()` degrades to the `Map.iterator()` shape**: both extensions collapse to the same lowered
  signature (same receiver type), and duplicate `(name, params)` overloads keep only the FIRST under the clean name
  (the second is `$dupN`-mangled). Destructuring `for ((k,v) in m)` works for both; calling `remove()` on the
  iterator, or `setValue` through the *mutable* entry-set element of a `Map`-typed receiver, is where the edges are.
- A **user class implementing `Map`/`MutableMap` in pure Kotlin** must satisfy the full `IDictionary` surface; today
  only the `@ClrIntrinsic`-renamed slots are generated, so such classes (stdlib `AbstractMap`, `MapWithDefaultImpl`)
  fail to LOAD when touched — the known under-tested pure-Kotlin dual-rep path (`dual-representation-stdlib-types`).
- **Map delegation (`val name by data`) requires a String-KEYED map at runtime.** The stdlib
  `Map<in String, V>.getValue` body pins `getOrImplicitDefault`'s K to `String` (a `(this as Map<String, V>)` CLR
  adaptation in `MapAccessors.kt`): the frontend approximates the contravariant captured K to `Any`, which under
  REIFIED generics dispatched `IDictionary<object,V>.ContainsKey` on a `Dictionary<string,V>` →
  `EntryPointNotFoundException`. Consequence: delegating through a `Map<Any, V>` receiver (legal for `Map<in
  String, V>` and fine under JVM erasure) `castclass`-fails on the CLR, because that value is an
  `IDictionary<object,V>`. Use a `Map<String, …>`-typed map for `by`-delegation.

## 5d. `Appendable` is `System.Text.StringBuilder`

`kotlin.text.Appendable` is a JVM-ism (`java.lang.Appendable`) with **no distinct .NET representation** —
`StringBuilder` is the CLR's sole general appendable char sink. Mirroring the `CharSequence`→`string` collapse (§5b),
DotKt aliases `Appendable` to `System.Text.StringBuilder` (`@ClrTypeAlias` + `@ClrIntrinsic("Append")` on
`append(Char)`/`append(CharSequence?)`). Consequences:

- An `Appendable`-typed parameter / return / bound (`<A : Appendable>`) surfaces to C# as `StringBuilder` — this is
  what makes `joinTo(StringBuilder(), …)`-style stdlib generics verifiable on the CLR.
- A **user class implementing `Appendable`** is therefore NOT supported (you cannot subclass the sealed-in-practice
  role); write to a `StringBuilder` instead. This is narrower than the JVM, and deliberate.

## 5e. Enum classes have two CLR shapes

- A **basic** `enum class` (constants only, no ctor params / methods / per-entry bodies) → a **real CLR `enum`** —
  ideal for .NET interop (usable in C# `switch`, attributes, etc.).
- A **rich** enum (constructor params, methods, per-entry bodies) → a **singleton-field class** (one static readonly
  instance per entry, with real properties/methods; `name`/`ordinal`/`values()`/`valueOf()` synthesized).
- Within a DotKt module both behave like Kotlin enums. **Across the round-trip** (re-consuming the dll as Kotlin)
  neither is restored as a Kotlin `enum class` — see §10.2 — so exhaustive `when` over a *consumed* enum degrades.
  A facadegen-imported **.NET** enum arrives as an object of enum-typed `val`s (read, pass, `==`, `when` all work,
  without exhaustiveness).

## 5f. `value class` is a real wrapper class — never erased

The OPPOSITE of Kotlin/JVM: a `@JvmInline value class Money(val amount: Int)` is emitted as an **ordinary reference
class** (private backing field + property + synthesized `equals`/`hashCode`/`toString`) — no inline-class erasure, no
mangled `-impl` statics, no .NET `struct`. Structural equality survives; what is lost is the value-ness itself
(identityless-ness is not enforced). The frontend still *requires* the `@JvmInline` annotation (a pinned-frontend
checker); the attribute itself is not emitted. See §10.3 for the round-trip view.

## 6. Consuming a DotKt assembly AS KOTLIN — what rides metadata vs. needs an attribute

When another `.ktproj` consumes a DotKt assembly, the Kotlin facts with **no native .NET representation** are carried
by `DotKt.Runtime.CompilerServices` attributes and restored on the consumer's FIR; the rest round-trips through plain
.NET metadata. Those attributes are **compiler-EMBEDDED** into each emitted assembly (internal types, like csc's own
`NullableAttribute`/`IsReadOnlyAttribute`) — they are metadata-only, never executed, so they don't live in a referenced
runtime.

| Kotlin construct | carrier |
|---|---|
| `infix` / `operator` | `[KotlinFunction(Infix\|Operator)]` |
| `suspend` | `[KotlinFunction(Suspend)]` (+ `Task<T>`→`T` unwrap) |
| top-level functions | `[KotlinFileClass]` on the `<File>Kt` facade → restored as package-level functions |
| `inline` (with a lambda) | `[KotlinInline(birJson)]` (only for cross-module non-local return; see §3) |
| **reference-type nullability** (`String?`) | **.NET's own NRT** `[Nullable]`/`[NullableContext]` (§9) — readable by C# too |
| `final`/`open`/`abstract`, visibility | **none** — ride .NET virtual-ness / accessibility |
| generics, `reified` | **none** — CLR generics are reified (§2) |
| parameter names (named-argument calls) | emitted via `DefineParameter` (were dropped before; not a FIR limitation) |

Deep dive: `docs/design-kotlin-metadata-attributes.md`.

## 7. Default arguments — a two-tier rule (native metadata, else a carried BIR expression)

Kotlin's default arguments are semantically **callee-side** (the default expression is evaluated inside the function, in
its scope) — Kotlin/JVM implements this with a synthetic `f$default(…, mask)` method. kotc emits only the arguments the
caller **actually wrote** (correct); an OMITTED argument is filled by one of two mechanisms, chosen per-parameter by a
single test — **can the parameter's own CLR type carry its default as a `[DefaultParameterValue]` constant?**

- **Tier 1 — YES (native).** A primitive/char/bool const on its primitive param, a `String` const on a `String` param,
  or a `null` const on any reference/nullable param → the parameter is emitted `[Optional]` + `[DefaultParameterValue(const)]`.
  ilemit's `EmitDefaultArg` fills the omitted arg from that metadata, and **C#/VB/F# consumers get the default natively**.
  Works for named-middle and reordered omission (`greet("C", punct = "?")`, `box(1, c = 9)`, `Pt(y = 4)`).
- **Tier 2 — NO (a carried BIR expression).** The prime cases are a `String` const on a `CharSequence`/interface-typed
  param (a string constant cannot sit in a `[DefaultParameterValue]` on an interface type) and **any non-constant
  default**. Such a parameter is emitted **REQUIRED** (no `[Optional]`) and its default EXPRESSION is carried as embedded
  BIR on a `@kotlin.clr.KotlinDefault(index, birJson)` attribute (ref.dll-only, mirroring `[KotlinInline]`). A **kcc**
  consumer reads it from the ref.dll and **splices the expression** as the omitted argument in the callee's scope
  (`bir2cir.DefaultArgSplice`, run before the CharSequence bridge + type-lowering, so a String default is coerced/lowered
  exactly like an explicit arg — and a default that references earlier params evaluates in the callee scope, unlike the
  old call-site inlining). A **C#** consumer sees a required parameter and passes it explicitly (accepted: a Tier-2
  default is not natively omittable from C#). A function with ≥1 Tier-2 parameter carries `@KotlinDefault` on ALL its
  defaulted parameters, so an omitted trailing run that interleaves Tier-1 and Tier-2 params splices contiguously from
  one source. Example — `Iterable.joinToString`: `limit: Int = -1` is Tier 1; `separator`/`prefix`/`postfix`/`truncated`
  (`CharSequence = "…"`) and `transform (…)? = null` are Tier 2, so `list.joinToString("-")` fills the omitted CharSequence
  defaults by splice (kcc) or requires them (C#).

## 8. Reverse / cross-assembly interop

- A DotKt assembly is a first-class .NET assembly; C# can reflection-load it. For **compile-time** `<Reference>`/
  `<ProjectReference>`, the emitted BCL `TypeRef`s (all scoped to the single `System.Private.CoreLib` that
  Reflection.Emit produces) are repointed to the real contract assemblies (`Object`/`Task`→`System.Runtime`,
  `List`/`Dictionary`→`System.Collections`, …) by the build-time `retarget` (Mono.Cecil). See memory
  `r1-reverse-projectreference-retargeter`.
- Forward (`Kotlin → .NET`): `import System.X` / a `<ProjectReference>` to a C# project just works (the import scan
  injects the referenced types into FIR). See `docs/design-kotlin-metadata-attributes.md` and memory
  `c2-import-driven-resolution`, `s5-fir-injection-seam`.

## 8b. Dual representation: `import System.Text.StringBuilder` vs `kotlin.text.StringBuilder` — two typed VIEWS of one CLR type

A BCL type that the stdlib **also** aliases (`kotlin.text.StringBuilder` is `@ClrTypeAlias`-bound to
`System.Text.StringBuilder`; same pattern for the aliased collections, exceptions, …) can appear in a program under
**two distinct frontend identities**:

- **the stdlib view** — default-imported, Kotlin-flavored members (`append`, `length`, the stdlib extensions);
- **the imported .NET view** — `import System.Text.StringBuilder` injects the RAW reflected surface
  (`Append`, `Length`, `ToString`, every BCL overload) as a separate classifier at its .NET namespace.

**The rule (deliberate, decided 2026-07-02): both views are legal and coexist; they are NOT unified.** Each is a
typed view of the SAME CLR runtime type (both erase to `System.Text.StringBuilder` at emit), but the frontend keeps
them distinct:

- Used separately in one program, both just work (`il-dualrep` gates this).
- **Mixing identities is a frontend type error** with an explicit message
  (`actual type is 'System.Text.StringBuilder', but 'kotlin.text.StringBuilder' was expected`). This is the
  intended diagnostic, not a bug.
- **Escape hatch**: an explicit cast crosses the views — `net as kotlin.text.StringBuilder`
  (plus `@Suppress("CAST_NEVER_SUCCEEDS")`; the frontend can't know they unify, the runtime checkcast is a no-op
  because the CLR type is literally the same).

Why not unify (the Kotlin/JVM `typealias` precedent)? Resolving the import TO the stdlib type would erase the very
thing the import asks for — the raw .NET member surface — and breaking the .NET view would break real interop code
(`sb.Append(...)`, `sb.Length`). Rejecting the import needs the alias map (`@ClrTypeAlias`, stdlib ref.dll) in the
frontend path, which the layer rules forbid (kotc reads no CLR-binding metadata). Two views with a clear boundary
diagnostic is the clean 1.0 rule; an explicit `clrView<T>()`-style conversion intrinsic is possible later if the
cast proves too blunt.

## 8c. Injected .NET STATIC members: implicit `Type.member` works (`.Companion` optional)

A facadegen-injected .NET class's static members surface on a synthesized **companion object**, and resolve
**implicitly** — exactly like a hand-written Kotlin companion:

```kotlin
import Avalonia.Application
Application.Start(...)             // implicit companion access — the natural form
Application.Companion.Start(...)   // explicit form — still works, identical BIR
```

The old rule requiring `.Companion` (2026-06-23; MEMORY `injected-static-members-need-companion`) was **retired
2026-07-03**: it was a wiring gap, not a pinned-compiler limitation. Stock K2 only links `companionObjectSymbol`
(the field the implicit-qualifier path consults) for source/deserialized classes — never for a fully-generated
owner — so kotc now eagerly creates + links the companion itself and sets the FIR-internal `ownerGenerator`
attribute through a bytecode-public Java shim (`kotc/frontend/FirInternals.java`; the eager link makes the
framework's only assignment site unreachable, upstream `FirGeneratedScopes.kt:245-255`/`:290`). Instance members,
constructors, properties, events, operators and extension methods resolve directly as before.

## 8d. Same-name .NET arity families: `Task` stays `Task`, `Task<TResult>` becomes `Task1<TResult>`

.NET allows a non-generic type and generic definitions that differ **only by arity** in one namespace
(`Task`/`Task``1`, `TaskCompletionSource`/`TaskCompletionSource``1`, `Tuple`/`Tuple``1..``8`, `Func``1..``17`,
`IComparable`/`IComparable``1`). C# disambiguates at the use site by type-argument count; a **Kotlin classifier
cannot be overloaded by arity** (one classifier per `(package, name)` — a K2 hard limit, typealiases included).
DotKt therefore projects the names (the `kotlin.Function0/Function1/…` precedent):

- the family's **non-generic** member keeps the plain simple name: `Task`, `Tuple` — so `Task.Delay(100)`,
  `Task.WhenAll(...)` read exactly like C#;
- each **generic** definition in a multi-member family is named `<Simple><arity>`: `Task1<TResult>`,
  `TaskCompletionSource1<T>`, `Func2<T,R>`, `Tuple2<A,B>`;
- a **singleton** generic family keeps the plain name: `List<T>`, `IEnumerable<T>` (`IEnumerable` non-generic
  lives in a *different namespace*, so no clash), `HashSet<T>`, …

The family is computed against the **loaded reference universe** (all `--refs` assemblies + BCL), not the
emitted closure, so a type's Kotlin name never changes when an unrelated import is added. `import
System.Threading.Tasks.Task` injects the **whole family** (both `Task` and `Task1`); `import
System.Threading.Tasks.Task1` also works (facadegen maps the trailing digits back to the CLR backtick arity).
In the injection metadata the .NET-name token is the **true CLR name** (`System.Threading.Tasks.Task``1`), so
the wire format itself is collision-free.

**Nested generic types are not injected** (`List<T>.Enumerator` — no CLR-addressable open name in the meta
grammar); members referencing them degrade to `Any?`. Iteration is unaffected (`for (x in list)` rides the
injected `IEnumerable<T>` iterator marker, and the backend enumerates via `GetEnumerator/MoveNext/Current`).

## 8d. .NET events subscribe with the idiomatic `+=` / `-=` operators

A .NET **event** (`ObservableCollection.CollectionChanged`, a WinForms/WPF `Button.Click`, a custom library
`Widget.Changed`) is subscribed and unsubscribed with the idiomatic Kotlin operators — **not** a method call:

```kotlin
val c = ObservableCollection<Int>()
c.CollectionChanged += { sender, e -> println("changed") }   // subscribe (the event's add accessor)
val h: (Any?, Any?) -> Unit = { s, e -> println("h fired") }
c.CollectionChanged += h                                     // subscribe a stored handler
c.CollectionChanged -= h                                     // unsubscribe (delegate equality — removes exactly h)
```

- The event surfaces as a **read-only property** `CollectionChanged: ClrEvent<HandlerFn>`, where `HandlerFn` is
  the handler's **Kotlin function type** (`(Any?, Any?) -> Unit`) — so a lambda `{ s, e -> … }` binds directly.
  `ClrEvent<T>` (`kotlin.clr.ClrEvent`) is a **compile-time-only handle**: a .NET event is not a first-class value
  (you can only add/remove/raise it), so `c.CollectionChanged` never materializes an object — it exists only to make
  `+=`/`-=` resolve. The compiler binds the operator to the event's underlying **add/remove accessor**; the handler
  lambda is wrapped as the event's own delegate type (not `Action`/`Func`).
- `-=` removes by **delegate equality**, so removal works only with a **stored** handler reference (as in the JVM
  idiom for listeners) — a fresh lambda literal at the `-=` site is a different delegate and removes nothing.
- This replaces the earlier `add_<Event>` / `remove_<Event>` accessor-method spelling, which no longer exists.

## 9. Reference-type nullability ⇔ .NET NRT; un-annotated .NET types are PLATFORM types

A Kotlin value-type `X?` is the structural `System.Nullable<X>` (§ value types). A **reference-type** `X?` has no
structural form on the CLR (a reference is always null-capable), so it rides **.NET's own nullable-reference metadata**:
ilemit stamps `[NullableContext(1)]` per type (reference positions default to non-null) and `[Nullable(2)]` on each
nullable reference return/parameter — the exact encoding the C# compiler uses, so a **C# consumer also sees** DotKt's
`String?` as nullable. There is no DotKt-specific nullability attribute.

Reading the other direction, consuming **any** .NET assembly:

| the .NET reference type's NRT info | injected Kotlin type |
|---|---|
| `[Nullable(2)]` / nullable context | `T?` |
| `[Nullable(1)]` / non-null context | `T` |
| **none** (assembly never opted into NRT) | `T!` — a **platform type** |

`T!` is a flexible type `(T..T?)` (`ConeFlexibleType`): the consumer may use it as `T` or `T?` and the compiler
enforces neither — exactly how Kotlin/JVM treats un-annotated Java. This avoids the unsound alternative of forcing a
possibly-null .NET value into a Kotlin non-null type.

## 10. Round-trip fidelity audit — what re-consuming a DotKt assembly as Kotlin LOSES

§6 lists what survives the round-trip (Kotlin → DotKt `.dll` → re-consumed as Kotlin: `facadegen` reflects the dll and
reads the `[Kotlin*]`/NRT attributes, the FIR injector rebuilds the declarations). **This section is the inverse: the
Kotlin surface that the round-trip does NOT fully restore.** It is an *audit* (prioritized-task #8) — the gaps are
documented here, not yet fixed. Findings are grounded in `toolchain/facadegen/Program.cs` (the reconstructor),
`toolchain/ilemit/Emitter.Metadata.cs` + `Emitter.CompilerServices.cs` (the attribute stampers), and
`toolchain/kotc/.../BirEmitter.kt` (the emitter), and were cross-checked with Codex against the CLR-metadata surface.

Three buckets — **Restored** (faithful), **Partial** (degraded), **Lost** (no carrier).

### 10.1 Restored (faithful) — see §6

`infix`/`operator`/`suspend`, top-level functions, cross-module `inline` non-local-return, `val`-vs-`var`,
reference-type nullability, parameter names (named-arg calls), constant default args, `vararg`, extension receivers,
reified generics, and `final`/`open`/`abstract` + `public`/`protected` visibility. **Data-class generated members
also round-trip**: `componentN()` carries `operator` (via `[KotlinFunction(Operator)]`, set from `fn.isOperator` in
`BirEmitter.kt`), so destructuring works cross-module, and `copy`/`equals`/`hashCode`/`toString` are real callable
methods. **Generic constraints/bounds and declaration-site variance also round-trip** (gap ①, now fixed): `facadegen`
reads `GetGenericParameterConstraints()`/`GenericParameterAttributes` and emits `tvariance`/`tbound`/`mbound` metadata,
and `ClrTypeInjection` restores `out`/`in` (interfaces) + upper bounds — so `interface P<out T>`, `interface C<in T>`,
`class SortedPair<T : Comparable<T>>`, and `fun <T : Comparable<T>> …` keep their variance and bounds cross-module.
**`sealed` classes/interfaces also round-trip (gap ⑤, now fixed):** a Kotlin `sealed` type lowers to a CLR
abstract-class / interface (which drops the sealed modality), so `ilemit` stamps `[KotlinSealed]`, `facadegen`
emits a `sealed` meta line, and `ClrTypeInjection` restores `Modality.SEALED`. Cross-module this restores the full
sealed contract: the modality, **cross-module inheritance enforcement** (a rogue subclass in another module is
rejected), **and exhaustive `when`** with no `else` — the closed inheritor set is rediscovered because the sealed
type's subtypes are themselves injected into the consumer's session via their `super` edges (importing
`Circle`/`Square` alongside `Shape` makes `when (s) { is Circle -> …; is Square -> … }` exhaustive).

### 10.2 Partially restored (in metadata, but degraded on reconstruction)

| Kotlin construct | What survives | What degrades / is lost |
|---|---|---|
| **Generic constraints / bounds** (`<T : Comparable<T>>`, `where`) | **NOW RESTORED (gap ①, §10.1)** | ~~`facadegen` never read `GetGenericParameterConstraints()`~~ — it now does, emitting `tbound`/`mbound` metadata that `ClrTypeInjection` restores as upper bounds (a `Comparable<T>` bound is reversed from the CLR `System.IComparable<T>` it lowers to). Multiple bounds (a `where` list) round-trip as several lines. |
| **Declaration-site variance** (`class Box<out T>`, `interface Cmp<in T>`) | **NOW RESTORED for interfaces (gap ①, §10.1)** | `facadegen` now reads `GenericParameterAttributes` and emits `tvariance`, which `ClrTypeInjection` restores as `out`/`in`. **Class**-type-param variance still has no CLR form (stays invariant); **use-site** variance / **star projection** `Foo<*>`: no analog, lost. |
| **`fun interface` (SAM)** | a plain interface | **The `fun interface` NATURE now round-trips (gap ③):** `ilemit` stamps `[KotlinFunInterface]`, `facadegen` emits a `funinterface` meta line, and `ClrTypeInjection` restores `status.isFun`. So a consumer sees it as a functional interface and can implement it (incl. via an anonymous `object : Handler { … }`). **What still degrades:** a bare **lambda** (SAM conversion) does NOT convert — blocked by the pinned Kotlin **2.2.0** FIR `FirSamResolver.computeSamCandidateNames`, which scans `FirRegularClass.declarations` **directly** for the single abstract method's name; a `FirDeclarationGenerationExtension`-injected interface serves its members lazily via scopes (empty `declarations`), so no SAM candidate is found. Same class of pinned-compiler limitation as `object`/companion (§10.4 #2) — not fixable from our side without materialising the SAM method into `declarations` (against the plugin contract) or a compiler bump. |
| **`enum class`** | entry values | A *basic* enum → a real CLR `enum` → `facadegen` restores it as an **`object` of `val`s** (value access like `Color.GREEN` works); a *rich* enum (ctor args / methods / per-entry bodies, `isRichEnum`) → a singleton-field **class** → restored as a plain **`class`**. Either way it is **not** a Kotlin `enum class`: exhaustive `when`, `.entries`/`values()`/`valueOf`, `.ordinal`/`.name` identity degrade. **Not fixable via the injection path (gap ④):** a `FirDeclarationGenerationExtension` (2.2.0) cannot synthesize real `FirEnumEntry` declarations — the exhaustiveness checker (`FirWhenExhaustivenessTransformer`) enumerates `enumClass.declarations.filterIsInstance<FirEnumEntry>()`, and the plugin API exposes no `createEnumEntry`/entry hook (only `createTopLevelClass`/`createMemberProperty`/…). Generating `ClassKind.ENUM_CLASS` with enum-shaped `val`s would mislead FIR without giving exhaustiveness, so no `[KotlinEnum]` carrier is emitted. |
| **`data class`** | generated members (10.1) | The **`data` modifier itself** is not carried (consumer sees an ordinary class); a `copy(...)` with **non-constant/self-referential defaults** (`x = this.x`) fails the call-site default rule (§7). |
| **Annotations** | RUNTIME/BINARY-retained with CLR-legal args; `KClass`→`System.Type` | `ilemit` **skips** annotations whose ctor-arg shape the CLR encoder rejects (`BuildCab`/`TryCab` → diagnostic, e.g. a generic-instantiation parameter). **SOURCE**-retention annotations are gone. **Use-site targets** (`@get:`/`@field:`/`@param:`) are only as faithful as which CLR target they landed on — the Kotlin intent is ambiguous. Repeatable-annotation semantics differ. |
| **Default arguments** | constants (§7) | non-constant defaults (reference callee params/receiver) are rejected at the omitting call, not restored. |
| **`internal` visibility** | hidden cross-assembly (correct for module≈assembly) | `kotc` lowers `internal`→ CLR `assembly`; `facadegen.Vis` skips assembly-visible members, so they don't inject — aligned with Kotlin's module boundary, but the **`internal` modifier is not itself restorable**, there is **no friend-module / `InternalsVisibleTo`** wiring, and no JVM-style name mangling. |

### 10.3 Lost (no carrier — not reconstructable from the current metadata)

| Kotlin construct | Closest .NET shape | What is lost |
|---|---|---|
| **`object` singleton** | class + static `INSTANCE` field | Restored as a plain **`class`**; the Kotlin singleton access `MyObject.member` does **not** round-trip (a consumer would need `.INSTANCE`/`.Companion`). |
| **Companion implicit access** | synthesized companion (`sfun`/`sprop`) | ~~must be written `Class.Companion.member`~~ **LIFTED (2026-07-02, `50c2c9f`)**: implicit `Class.member` now resolves — kotc eagerly creates+links the injected class's companion and sets the FIR-internal `ownerGenerator` via a bytecode-public Java shim (`kotc/frontend/FirInternals.java`; the old NPE was a FIR wiring gap, not a K2 limit). Both forms compile to identical BIR. |
| **`value`/inline class** (`@JvmInline`) | a **real wrapper class** (never erased, never a struct) | The OPPOSITE of Kotlin/JVM: no inline-class erasure and no name mangling — `Money` is emitted as an ordinary reference class (backing field + property + the synthesized `equals`/`hashCode`/`toString`), i.e. permanently "boxed". Structural equality survives; what is lost is the value-ness itself (identityless-ness is not enforced, no .NET `struct`, and the `value` modifier does not round-trip). The frontend still REQUIRES `@JvmInline` (JVM-frontend checker); the emitted `[kotlin.jvm.JvmInline]` attribute is skipped by ilemit. |
| **`typealias`** | the expanded type | The alias name is not visible cross-module (it is expanded at use). |
| **Contracts** (`@ExperimentalContracts`) | — | `callsInPlace`/returns-implies smart-cast facts are gone → consumer loses the smart-casts. |
| **`Nothing`** (bottom type) | `void` / a throwing method | The bottom-type semantics (unreachable, `List<Nothing>` covariance) have no CLR analog. |
| **Function types with receiver** (`A.() -> B`) and **suspend function types** | a delegate / `Func<>` | The receiver-vs-argument distinction and the suspend-function-type identity degrade to an ordinary delegate. |
| **`lateinit`** | a non-null `var` field | The definite-init contract / `isInitialized` is lost (restored as a plain non-null `var`). |
| **`inner` class** | a nested type | The `inner` modifier (implicit outer `this` capture) is not marked vs. a plain nested class. |
| **`const val`, `tailrec`, `crossinline`/`noinline`, property delegation `by`** | literal field / plain method / accessors | Compile-time-only facts: the value/behavior survives but the modifier/relationship is not a restorable declaration fact. (Mostly harmless — these don't change the callable API surface.) |

### 10.4 Highest-impact gaps (for a follow-up fix pass)

1. ~~**Generic constraints + interface variance dropped by `facadegen`**~~ — **FIXED (gap ①, 2026-07-01).** `facadegen`
   now reads `GetGenericParameterConstraints()` / `GenericParameterAttributes` and emits `tvariance`/`tbound`/`mbound`
   metadata; `ClrTypeInjection` restores `out`/`in` variance + upper bounds (lazy lookup-tag cones, self-ref-safe for the
   BCL numeric tower, fail-soft). Covers every generic library API (`<T : Comparable<T>>`, `Comparator<in T>`, …). No new
   attribute — reconstructor-side only (`facadegen` emission + injector consumption).
2. **`object` singleton / companion implicit access** — pervasive in real Kotlin libraries; the ergonomic
   `Type.member` call site does not round-trip. **KNOWN / ACCEPTED LIMITATION (2026-07-01): NOT a follow-up fix.**
   `facadegen` *would* emit the restoration, but the pinned Kotlin **embedded compiler (2.2.0)** does not support the
   implicit `Type.member`→companion/`.INSTANCE` resolution the consumer's FIR would need — so it is not facadegen-fixable
   from our side. Consumers use `.Companion`/`.INSTANCE` explicitly (MEMORY `injected-static-members-need-companion`).
3. **`fun interface` SAM** — **PARTIALLY FIXED (gap ③, 2026-07-02).** The `fun interface` *nature* now round-trips
   (`[KotlinFunInterface]` → `funinterface` meta → `status.isFun`), so a consumer sees a functional interface and can
   implement it (anonymous `object`). A bare **lambda** still won't SAM-convert — pinned-2.2.0 FIR `computeSamCandidateNames`
   reads `FirRegularClass.declarations` directly, which a generation-extension interface leaves empty (§10.2). **KNOWN /
   ACCEPTED LIMITATION** on the same basis as #2.
4. **`enum class`** — **NOT FIXED (gap ④).** Blocked at the injection layer: a `FirDeclarationGenerationExtension` (2.2.0)
   cannot synthesize real `FirEnumEntry` declarations, which FIR's exhaustiveness checker requires; the plugin API has no
   enum-entry hook (§10.2). A basic enum still round-trips as an `object` of `val`s (value access works). **KNOWN /
   ACCEPTED LIMITATION.**
5. **`sealed` hierarchies** — **FIXED (gap ⑤, 2026-07-02).** `[KotlinSealed]` → `sealed` meta → `Modality.SEALED`
   restores the modality, cross-module inheritance enforcement, AND exhaustive `when` (the injected subtypes supply the
   closed inheritor set). See §10.1.
6. **Non-constant default args / `data class copy` self-defaults** — rejected at the call (§7). **KNOWN / ACCEPTED
   LIMITATION (2026-07-01): NOT a follow-up fix for now.** A default that references callee params/receiver has no
   constant carrier; the omitting call is rejected rather than mis-restored (MEMORY `cross-module-default-args-not-preserved`).

Status: **#1 (variance/bounds), #5 (sealed) are FIXED; #3 (fun interface) is PARTIAL** (nature restored, SAM-lambda
pinned-compiler-blocked). **#2 (object/companion), #4 (enum class), #6 (non-constant defaults) remain KNOWN / ACCEPTED
limitations** — each blocked by the pinned Kotlin 2.2.0 `FirDeclarationGenerationExtension` surface (no companion-
implicit resolution, no `FirEnumEntry` synthesis) or the absence of a constant carrier, not by a missing `[Kotlin*]`
attribute we could add.

---

## Quick "this surprised me" index

- `inline`/`reified` written but no lambda passed → **ignored** (plain/generic method). §2, §3.
- `reified` lets you pass a non-reified type param on the CLR (JVM forbids it). §2.
- Inlining is done by the backend at emit, not the frontend. §3.
- A non-local `return` into a cross-module inline lambda → works (body is carried in `[KotlinInline]`). §3.
- `println(true)` prints `True`, `println(4.0)` prints `4`. §5.
- `String.toInt()/toLong()/toByte()/toShort()` are strict base-10 (like JVM): no leading/trailing whitespace, no group
  separators, and a bad string throws `NumberFormatException` (a `catch (IllegalArgumentException)` also catches it). §5.
- `String.toDouble()/toFloat()` parse with **InvariantCulture** (a `,` is always rejected, never a group separator, so
  `"3,14".toDouble()` throws like JVM). The accepted grammar is otherwise .NET's `Double/Single.Parse`
  (decimal point, sign, exponent, `NaN`/`Infinity`); the JVM-only hex-float and trailing `d`/`f` suffix forms are **not**
  accepted. Failure throws `NumberFormatException`. §5.
- `String.format` uses the .NET composite format (`"{0}"`), not Java printf (`"%d"`). §5.
- `suspend fun` has no Continuation parameter — it returns `Task<T>`, and it starts **hot** (like C# `async`), not cold. §4.
- A `CharSequence` parameter surfaces to C# as `string`; a `StringBuilder` passed as `CharSequence` is **snapshotted** by an implicit `.toString()` — no live view. §5b.
- A Kotlin `Map` surfaces to C# as a *mutable* `IDictionary<K,V>`; `keys`/`values`/`entries` are snapshots. §5c.
- A `value class` is a real (reference) class on the CLR — never erased, never a struct. §5f.
- `import System.Text.StringBuilder` and `kotlin.text.StringBuilder` are two distinct typed views of one CLR type; mixing them is a type error (cast to cross). §8b.
- An injected .NET class's statics resolve implicitly (`Application.Start(...)`); `.Companion` is optional. §8c.
- Two same-simple-named classes in different packages coexist (packages are namespaces now). §1.
- A reference type from a .NET assembly built WITHOUT `<Nullable>enable</Nullable>` arrives as a platform type `String!`, not `String`. §9.
- Re-consuming a DotKt `.dll` as Kotlin now **restores** generic **bounds/interface variance** (gap ①), **`sealed`** (gap ⑤ — modality, cross-module enforcement, exhaustive `when`), and the **`fun interface` nature** (gap ③ — usable, though a bare lambda still won't SAM-convert under the pinned 2.2.0 compiler); `enum class` and `object`/companion still restore as plain `object`/`class`. §10.

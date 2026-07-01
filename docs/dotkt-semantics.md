# DotKt semantics — how Kotlin maps to the CLR, and where it deliberately differs from Kotlin/JVM

DotKt compiles Kotlin to a normal .NET assembly. A lot of Kotlin's surface is **JVM-shaped accidental complexity**
(erased generics, `@Metadata`, the Continuation ABI, JVM string conventions). On the CLR, DotKt **reinterprets or
discards** those rather than reproducing them. This page is the canonical list of those behavioral differences and
non-obvious interpretations — the things a Kotlin/JVM developer would otherwise be surprised by. Feature-by-feature
deep dives are linked per section.

Guiding principle: *Kotlin carries JVM accidental complexity; on the CLR, identify it and discard it — don't
reproduce it.* (See `docs/research-roadmap.md`; memory `clr-not-jvm-discard-jvmisms`.)

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
- **DotKt pipeline (four layers: `facadegen` / `kotc` / `bir2cir` / `ilemit`; `native-cir` is the target).** The
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

## 4. `suspend` ⇔ `Task<T>` (the Continuation is hidden)

- **JVM:** a `suspend fun f(): T` compiles to `Object f(…, Continuation)` — the Continuation is an explicit parameter,
  CPS the public ABI.
- **DotKt:** the **public CLR ABI is `Task<T> f(…)`** — the Continuation never appears in the signature (it's the
  internal lowered form, with a `Task` sink). A C# caller `await`s it; a Kotlin caller in another module sees a
  `suspend fun` again (restored from a `[KotlinFunction(Suspend)]` attribute, with the `Task<T>` unwrapped to `T`).
- Gotcha: a member `suspend fun` returning a **user type** drove out a Reflection.Emit limitation
  (`AsyncTaskMethodBuilder<UserT>` is a TypeBuilder instantiation) — fixed by re-anchoring those members via
  `TypeBuilder.GetMethod`.
- Deep dives: `docs/design-coroutines-clr.md`, `docs/coroutine-abi.md`, memory `coroutine-abi-decision`.

## 5. Primitive stringification is CLR-native (not Kotlin/JVM cosmetics)

- A DotKt program IS a .NET program, so it follows the **host's** conventions: `println(true)` → `True` (not `true`),
  `println(4.0)` → `4` (not `4.0`). Kotlin's `true`/`4.0` are JVM/JS inherited cosmetics, not language essence.
- The JVM differential harness (`verify-differential.sh`) normalizes these cosmetic differences and checks the logic.
- Memory `clr-native-primitive-formatting`.

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

## 7. Default arguments are filled at the CALL site (constants only)

Kotlin's default arguments are semantically **callee-side** (the default expression is evaluated inside the function, in
its scope) — Kotlin/JVM implements this with a synthetic `f$default(…, mask)` method. The .NET backend instead fills an
omitted argument by **inlining the default expression at the call site** (like C#'s `[Optional]`/`[DefaultParameterValue]`,
which it also emits, so C#/VB/F# consumers get the defaults natively). Consequences:

- **Constant defaults work everywhere** — including named-middle and reordered omission (`greet("C", punct = "?")`,
  `box(1, c = 9)`, `Pt(y = 4)`): call-site inlining and caller-side evaluation agree for a constant.
- **A non-constant default that references the callee's own parameters/receiver is rejected** at the omitting call with
  a clean source-located error (`b: Int = a * 10`; a data class `copy`'s `x = this.x` when you write `p.copy(y = 9)`).
  `a`/`this` aren't in scope at the call site, so it can't be inlined there — it needs callee-side evaluation, which the
  backend doesn't do yet. Rejected at the **call** (not the declaration): a data class always *declares* `copy` with
  `this.x` defaults, but compiles fine as long as you don't arg-omit `copy`.

## 8. Reverse / cross-assembly interop

- A DotKt assembly is a first-class .NET assembly; C# can reflection-load it. For **compile-time** `<Reference>`/
  `<ProjectReference>`, the emitted BCL `TypeRef`s (all scoped to the single `System.Private.CoreLib` that
  Reflection.Emit produces) are repointed to the real contract assemblies (`Object`/`Task`→`System.Runtime`,
  `List`/`Dictionary`→`System.Collections`, …) by the build-time `retarget` (Mono.Cecil). See memory
  `r1-reverse-projectreference-retargeter`.
- Forward (`Kotlin → .NET`): `import System.X` / a `<ProjectReference>` to a C# project just works (the import scan
  injects the referenced types into FIR). See `docs/design-kotlin-metadata-attributes.md` and memory
  `c2-import-driven-resolution`, `s5-fir-injection-seam`.

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

### 10.2 Partially restored (in metadata, but degraded on reconstruction)

| Kotlin construct | What survives | What degrades / is lost |
|---|---|---|
| **Generic constraints / bounds** (`<T : Comparable<T>>`, `where`) | **NOW RESTORED (gap ①, §10.1)** | ~~`facadegen` never read `GetGenericParameterConstraints()`~~ — it now does, emitting `tbound`/`mbound` metadata that `ClrTypeInjection` restores as upper bounds (a `Comparable<T>` bound is reversed from the CLR `System.IComparable<T>` it lowers to). Multiple bounds (a `where` list) round-trip as several lines. |
| **Declaration-site variance** (`class Box<out T>`, `interface Cmp<in T>`) | **NOW RESTORED for interfaces (gap ①, §10.1)** | `facadegen` now reads `GenericParameterAttributes` and emits `tvariance`, which `ClrTypeInjection` restores as `out`/`in`. **Class**-type-param variance still has no CLR form (stays invariant); **use-site** variance / **star projection** `Foo<*>`: no analog, lost. |
| **`enum class`** | entry values | A *basic* enum → a real CLR `enum` → `facadegen` restores it as an **`object` of `val`s**; a *rich* enum (ctor args / methods / per-entry bodies, `isRichEnum`) → a singleton-field **class** → restored as a plain **`class`**. Either way it is **not** a Kotlin `enum class`: exhaustive `when`, `.entries`/`values()`/`valueOf`, `.ordinal`/`.name` identity degrade. |
| **`data class`** | generated members (10.1) | The **`data` modifier itself** is not carried (consumer sees an ordinary class); a `copy(...)` with **non-constant/self-referential defaults** (`x = this.x`) fails the call-site default rule (§7). |
| **Annotations** | RUNTIME/BINARY-retained with CLR-legal args; `KClass`→`System.Type` | `ilemit` **skips** annotations whose ctor-arg shape the CLR encoder rejects (`BuildCab`/`TryCab` → diagnostic, e.g. a generic-instantiation parameter). **SOURCE**-retention annotations are gone. **Use-site targets** (`@get:`/`@field:`/`@param:`) are only as faithful as which CLR target they landed on — the Kotlin intent is ambiguous. Repeatable-annotation semantics differ. |
| **Default arguments** | constants (§7) | non-constant defaults (reference callee params/receiver) are rejected at the omitting call, not restored. |
| **`internal` visibility** | hidden cross-assembly (correct for module≈assembly) | `kotc` lowers `internal`→ CLR `assembly`; `facadegen.Vis` skips assembly-visible members, so they don't inject — aligned with Kotlin's module boundary, but the **`internal` modifier is not itself restorable**, there is **no friend-module / `InternalsVisibleTo`** wiring, and no JVM-style name mangling. |

### 10.3 Lost (no carrier — not reconstructable from the current metadata)

| Kotlin construct | Closest .NET shape | What is lost |
|---|---|---|
| **`object` singleton** | class + static `INSTANCE` field | Restored as a plain **`class`**; the Kotlin singleton access `MyObject.member` does **not** round-trip (a consumer would need `.INSTANCE`/`.Companion`). |
| **Companion implicit access** | synthesized companion (`sfun`/`sprop`) | `Class.member` must be written `Class.Companion.member` (MEMORY `injected-static-members-need-companion`). |
| **`fun interface` (SAM)** | a plain interface | `kotc` does SAM-conversion in-module (`SAM_CONVERSION`/`samConversion`), but no `fun interface` marker is emitted and `facadegen` restores a **plain interface** → a consumer **cannot pass a lambda** (no SAM conversion) for a DotKt `fun interface`. |
| **`sealed` class/interface** | abstract class / interface | The closed sub-hierarchy is not carried; CLR `sealed` means *final*, a different concept → no exhaustive-`when` guarantee cross-module. |
| **`value`/inline class** (`@JvmInline`) | the erased underlying type | The wrapper identity is erased (the inline-class `.data` collapse) — a consumer sees the underlying type, not the value class. |
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
3. **`fun interface` SAM** — a DotKt callback interface can't take a lambda from a consuming module.
4. **`enum class`** restored as `object`/`class` — no exhaustive `when`, no `.entries`.
5. **`sealed` hierarchies** — no exhaustive `when` cross-module.
6. **Non-constant default args / `data class copy` self-defaults** — rejected at the call (§7). **KNOWN / ACCEPTED
   LIMITATION (2026-07-01): NOT a follow-up fix for now.** A default that references callee params/receiver has no
   constant carrier; the omitting call is rejected rather than mis-restored (MEMORY `cross-module-default-args-not-preserved`).

Each of 1–6 needs either a new `[Kotlin*]` carrier attribute (`object`/`enum`/`sealed`/`fun interface`/`data`/`value`
markers) **or**, for #1, just a richer `facadegen` read of metadata that is already present. None are fixed yet.

---

## Quick "this surprised me" index

- `inline`/`reified` written but no lambda passed → **ignored** (plain/generic method). §2, §3.
- `reified` lets you pass a non-reified type param on the CLR (JVM forbids it). §2.
- Inlining is done by the backend at emit, not the frontend. §3.
- A non-local `return` into a cross-module inline lambda → works (body is carried in `[KotlinInline]`). §3.
- `println(true)` prints `True`, `println(4.0)` prints `4`. §5.
- `suspend fun` has no Continuation parameter — it returns `Task<T>`. §4.
- Two same-simple-named classes in different packages coexist (packages are namespaces now). §1.
- A reference type from a .NET assembly built WITHOUT `<Nullable>enable</Nullable>` arrives as a platform type `String!`, not `String`. §9.
- Re-consuming a DotKt `.dll` as Kotlin now **restores** generic **bounds/interface variance** (gap ① fixed — `facadegen` reads them back, the injector re-applies them), but still restores `object`/`enum class`/`sealed`/`fun interface`/`data` only as plain classes. §10.

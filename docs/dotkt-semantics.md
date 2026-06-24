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
- **DotKt pipeline:** `…Fir2Ir then ClrBackendPhase` — **there is NO JVM `FunctionInlining` lowering.** The IR that
  reaches the backend still has un-inlined `inline` calls. **Inlining happens at emit time in `BirEmitter`**:
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
- Cross-module non-local-return IS supported (2026-06-24), and — because inlining is emit-time over (near-)BIR — it's
  **much lighter than JVM's `@Metadata`** (no IR deserializer): `[KotlinInline(birJson)]` carries the function's own
  BIR body, the consumer's `ilemit` reads it from the `--ref`'d assembly and splices it at the call site (a `return`
  in the spliced lambda body becomes the caller's `ret`). Full mechanism + scope in
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
by `DotKt.Metadata` attributes and restored on the consumer's FIR; the rest round-trips through plain .NET metadata.

| Kotlin construct | carrier |
|---|---|
| `infix` / `operator` | `[KotlinFunction(Infix\|Operator)]` |
| `suspend` | `[KotlinFunction(Suspend)]` (+ `Task<T>`→`T` unwrap) |
| top-level functions | `[KotlinFileClass]` on the `<File>Kt` facade → restored as package-level functions |
| `inline` (with a lambda) | `[KotlinInline(birJson)]` (only for cross-module non-local return; see §3) |
| `final`/`open`/`abstract`, visibility | **none** — ride .NET virtual-ness / accessibility |
| generics, `reified` | **none** — CLR generics are reified (§2) |
| parameter names (named-argument calls) | emitted via `DefineParameter` (were dropped before; not a FIR limitation) |

Deep dive: `docs/design-kotlin-metadata-attributes.md`.

## 7. Reverse / cross-assembly interop

- A DotKt assembly is a first-class .NET assembly; C# can reflection-load it. For **compile-time** `<Reference>`/
  `<ProjectReference>`, the emitted BCL `TypeRef`s (all scoped to the single `System.Private.CoreLib` that
  Reflection.Emit produces) are repointed to the real contract assemblies (`Object`/`Task`→`System.Runtime`,
  `List`/`Dictionary`→`System.Collections`, …) by the build-time `tools/retarget` (Mono.Cecil). See memory
  `r1-reverse-projectreference-retargeter`.
- Forward (`Kotlin → .NET`): `import System.X` / a `<ProjectReference>` to a C# project just works (the import scan
  injects the referenced types into FIR). See `docs/design-kotlin-metadata-attributes.md` and memory
  `c2-import-driven-resolution`, `s5-fir-injection-seam`.

---

## Quick "this surprised me" index

- `inline`/`reified` written but no lambda passed → **ignored** (plain/generic method). §2, §3.
- `reified` lets you pass a non-reified type param on the CLR (JVM forbids it). §2.
- Inlining is done by the backend at emit, not the frontend. §3.
- A non-local `return` into a cross-module inline lambda → works (body is carried in `[KotlinInline]`). §3.
- `println(true)` prints `True`, `println(4.0)` prints `4`. §5.
- `suspend fun` has no Continuation parameter — it returns `Task<T>`. §4.
- Two same-simple-named classes in different packages coexist (packages are namespaces now). §1.

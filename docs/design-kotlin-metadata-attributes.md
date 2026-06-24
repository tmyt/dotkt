# Consuming a DotKt assembly AS KOTLIN — metadata attributes

**Status: v1 implemented & verified (2026-06-24).** `scripts/verify-roundtrip.sh`.

## Goal

A DotKt-compiled assembly is a normal .NET assembly, so it can be consumed from C# (reverse interop, R-1).
But to consume it **as Kotlin** — another `.ktproj` using it with Kotlin syntax — the Kotlin-language facts that
have **no native .NET representation** must travel with the assembly and be restored on the consumer's FIR. This is
the foundation for shipping compiled Kotlin libraries (kotlinx-coroutines, serialization, …) for the CLR and
consuming them as Kotlin (see [[dotkt-compile-kotlin-libraries]]).

## What needs an attribute (and what doesn't)

Only modifiers .NET metadata **can't already express**:

| Kotlin construct | .NET emission | Recoverable from plain metadata? | Carrier |
|---|---|---|---|
| `infix fun` | ordinary method | no (pure call-syntax modifier) | `[KotlinFunction(Infix)]` |
| `operator fun` | ordinary method | no (convention-name resolution) | `[KotlinFunction(Operator)]` |
| `suspend fun` | `Task<T>`-returning method | no (the Task ABI hides the suspend-ness) | `[KotlinFunction(Suspend)]` |
| top-level `fun` | static method of a `<File>Kt` class | no (.NET has no top-level functions) | `[KotlinFileClass]` on the file class |
| `final`/`open`/`abstract` (modality) | non-virtual / virtual / abstract | **yes** — rides .NET virtual-ness | (none) |
| visibility | public/assembly/family | **yes** | (none) |
| generics, including `reified` | real CLR generic method `<T>` | **yes** — CLR generics are reified | (none) |

The attributes live in **`DotKt.Runtime`** (`DotKt.Metadata` namespace) for cross-assembly identity — every DotKt
assembly already references it ([[dotkt-naming-and-runtime-split]]).

## Pipeline (mirror of the forward `--refs` injection)

```
  emit:   BirEmitter records infix/operator/suspend + the file class
            -> ilemit stamps [KotlinFunction(flags)] / [KotlinFileClass]   (skipped if DotKt.Runtime absent)
  retarget: dotkt-retarget repoints BCL refs (also needed so facadegen can MLC-load the dll)
  read:   facadegen --meta reads the attributes -> meta tokens
            `fun <name> <ret> final,infix|operator|suspend ...`   (suspend: Task<T> unwrapped to T)
            `file <package> <fileClassFqn>` + `tlfun <name> ...`  (top-level)
  inject: ClrTypeInjection parses them and restores the Kotlin modifier on the synthesized FIR:
            members -> status { isInfix/isOperator/isSuspend }
            top-level -> getTopLevelCallableIds + generateFunctions(owner==null)
  backend (consumer): a call to a restored top-level fun -> ClrTopLevelRegistry -> a static call on the file class;
            a restored suspend call's .NET return is Task<T> (coTaskType), awaited by the coroutine machinery.
```

## `inline` / `reified` — deliberately NOT round-tripped (design conclusion)

From the **consumer surface**, whether an imported function was `reified`/`inline` is irrelevant:

- **CLR generics are reified** ([[clr-not-jvm-discard-jvmisms]]). A Kotlin `inline fun <reified T>` is emitted as an
  ordinary CLR generic method `M<T>()`; the consumer calls `M<Int>()` and `T` is a real runtime type. No body to
  carry, no re-inlining at the call site (which is what Kotlin's `@Metadata` protobuf blob exists to enable on the
  JVM, where generics are erased — an accidental complexity we don't reproduce).
- The **only** thing true inlining buys that a generic-method call can't: a **non-local return through a lambda
  parameter**. Without the body we can't inline, so such a call simply won't compile on the consumer (a normal
  "return not allowed here") — not silent breakage.

So `[Reified]`/`[ReifiedInline]`/`[KotlinInlineBody]` are **not part of the design.** Empirically (2026-06-24), the
cross-assembly inline matrix is:

| case | result |
|---|---|
| same-module inline (incl. non-local return, crossinline) | ✅ existing (`il-inline`/`il-inline2`/`il-xinline`) |
| cross-module **non-reified** inline | ✅ emitted as a normal method; consumed as a regular (non-inlined) call |
| cross-module **reified** inline | ✅ emitted as a real generic method; consumed as `f<Int>()` (CLR generics are reified, so the `T::class`/`is T` body works) — required restoring generic TYPE PARAMS on the top-level injector + a `clrGenericStatic` call |
| cross-module inline + lambda with **non-local return** | ❌ the one case that can't degrade |

**The last row is now IMPLEMENTED** (2026-06-24) — cross-module inline with a lambda + non-local `return` works.

**Where inlining happens.** DotKt does NOT run the standard JVM IR `FunctionInlining` lowering — its pipeline is
`…Fir2Ir then ClrBackendPhase` (no JVM lowerings). **Inlining happens at EMIT time in BirEmitter**
(`call()` → `if (callee.isInline && callee.body != null && hasLambdaArg(call)) inlineCall(call)`, which `spliceBody`s
the callee's IR body at the call site; lambda-less inline funs are left as ordinary calls for the JIT, and `inline`/
`reified` are pure decoration unless a lambda LITERAL is passed). Because inlining is emit-time over (near-)BIR, the
cross-module fix is **lighter than JVM's `@Metadata`** (no frontend IR deserializer):

1. **emit** — `ilemit` stamps an inline+lambda fn with `[KotlinInline(birJson)]` carrying its own `{params, body}` BIR.
2. **read** — `facadegen` flags the restored fn `,inline` in the meta; the body STAYS in the assembly (read at splice time).
3. **inject** — `ClrTypeInjection` marks the fn `status { isInline = true }`, so the consumer's frontend ACCEPTS a
   non-local `return` through the lambda (a body-less stub here, so BirEmitter's `callee.body == null`).
4. **splice** — the consumer's BirEmitter emits an `inlineSplice` node (the call's value/lambda bindings); the
   consumer's `ilemit` (which `--ref`s the library) reads `[KotlinInline].Body`, parses it, and emits the callee body
   HERE with substitution: a callee param `local` → its bound value; a `delegateInvoke` of a lambda param → the
   caller's lambda body (binding its param). A `return` in that spliced lambda body emits a `ret` from the caller →
   non-local return works. CFG labels are re-`DefineLabel`d per splice (the BIR ids are baked, so re-emitting a body
   would otherwise redefine a Label).

Scope: lambda-taking inline funcs only (the only ones whose body must travel; lambda-less degrade to plain/generic
methods). Not yet handled: a callee body with its OWN locals (name scoping), and value-returning lambda params with
a non-local return (the Unit-lambda forEach shape is covered — the common non-local-return case).

## Fixed along the way (2026-06-24)

- **Member `suspend fun` returning a USER type** (`suspend fun f(): Vec`) — previously crashed ilemit
  (`AsyncTaskMethodBuilder<Vec>`/`Task<Vec>`/`TaskAwaiter<Vec>` are TypeBuilderInstantiations whose `GetMethod`
  throws). Fixed by the `GenM` helper (re-anchor the open method via `TypeBuilder.GetMethod`) across the coroutine
  state machine, **and** by the `EmitClrCall` return-type substitution: `TypeBuilder.GetMethod` leaves the method's
  return type open (`TaskAwaiter`1<!0>`), so the runBlocking/await path mis-typed its temp — now it trusts the BIR
  `ret` hint. Works through both a `suspend fun` and a `runBlocking { … }` lambda. (`scripts/verify-roundtrip.sh`.)
- **Parameter names** — ilemit defined methods by type only (never `DefineParameter`), so names were lost and
  facadegen fell back to `arg0`/`arg1`, blocking named-argument calls across a boundary. Now emitted; `f(b = 2, a = 1)`
  round-trips. (The names were always in the BIR — it was purely an emit omission, not a FIR limitation.)

- **Kotlin package → .NET namespace (was FOUNDATIONAL; FIXED 2026-06-24).** DotKt used to flatten all packages to the
  **root** namespace — a correctness bug (`alpha.Box` + `beta.Box` both emitted `.NET Box` → collision/ilemit crash) and
  the blocker for packaged consumption. `BirEmitter.typeName()`/`fileClassName()` now qualify top-level
  classes/interfaces/enums and the file facade with `packageFqName` (nested types stay simple; root-package unchanged).
  Verified: `alpha.Box`+`beta.Box` coexist, and a `package geom` library is consumed via `import geom.Vec`/`import
  geom.Dir`/`import geom.greet` (top-level too) through the MSBuild import-scan flow.

## Known gaps / follow-ups

- Extension functions (`fun T.f()`) restore as plain statics, not Kotlin extensions, yet (a future flag/marker).
- An MSBuild `.ktproj → .ktproj` consume-as-Kotlin sample isn't wired into `verify-ktproj.sh` yet (the round-trip is
  covered by the shell harness `verify-roundtrip.sh`, which drives the same import-scan → facadegen → inject flow).

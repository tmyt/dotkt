# Consuming a DotKt assembly AS KOTLIN — metadata attributes

**Status: v1 implemented & verified (2026-06-24).** `tests/roundtrip/scenarios/run.sh`.

## Goal

A DotKt-compiled assembly is a normal .NET assembly, so it can be consumed from C# (reverse interop, R-1).
But to consume it **as Kotlin** — another `.ktproj` using it with Kotlin syntax — the Kotlin-language facts that
have **no native .NET representation** must travel with the assembly and be restored on the consumer's FIR. This is
the foundation for shipping compiled Kotlin libraries (kotlinx-coroutines, serialization, …) for the CLR and
consuming them as Kotlin (see [[dotkt-compile-kotlin-libraries]]).

## What needs an attribute (and what doesn't)

Only Kotlin facts that plain .NET metadata cannot express or cannot express with Kotlin semantics:

| Kotlin construct | .NET emission | Recoverable from plain metadata? | Carrier |
|---|---|---|---|
| `infix fun` | ordinary method | no (pure call-syntax modifier) | `[KotlinFunction(Infix)]` |
| `operator fun` | CLR `op_*` when the operator has a CLR ABI equivalent; otherwise ordinary method | no for ordinary methods; CLR `op_*` names are recoverable but still need Kotlin operator status for Kotlin re-consumption | `[KotlinFunction(Operator)]` |
| `suspend fun` | `Task<T>`-returning method | no (the Task ABI hides the suspend-ness) | `[KotlinFunction(Suspend)]` |
| top-level `fun` | static method of a `<File>Kt` class | no (.NET has no top-level functions) | `[KotlinFileClass]` on the file class |
| inline function body needed for cross-module lambda/non-local-return splicing | ordinary method | no | `[KotlinInline(body)]` |
| Kotlin `val` backed by a **`@ClrField` public field** | public field | no; a plain public field looks writable | `[KotlinReadOnly]` — survives **only** for the `@ClrField` plain-field case; a normal `val` is now a get-only CLR property, recoverable from plain metadata (see [design-clr-property-model.md](design-clr-property-model.md)) |
| reference-type nullability (`String?`) | .NET nullable reference metadata | yes for NRT-aware tools; must be emitted | `[Nullable]` / `[NullableContext]` |
| a nullable GENERIC `T?` on an unconstrained `T` (`fun <T> f(x: T?): T?`, `Holder<T?>`, `Array<T?>`, `(T) -> T?`) | `System.Object` at that position — the one CLR slot that carries a real null for a value AND a reference instantiation (#86) | no; `object` names neither `T` nor the `?` | `[KotlinNullableGeneric(pre-erasure type node)]` **plus** the slot's own `[Nullable(2)]` byte |
| imported CLR event endpoint (`CLREvent<T>`) | real CLR event metadata + add/remove accessors | yes for the event itself; Kotlin endpoint syntax must be synthesized | plain CLR event metadata, no DotKt attribute by default |
| `final`/`open`/`abstract` (modality) | non-virtual / virtual / abstract | **yes** — rides .NET virtual-ness | (none) |
| visibility | public/assembly/family | **yes** | (none) |
| generics, including `reified` | real CLR generic method `<T>` | **yes** — CLR generics are reified | (none) |

The attributes are **compiler-EMBEDDED per-assembly** as internal `DotKt.Runtime.CompilerServices.*` types (like csc's
own `NullableAttribute`/`IsReadOnlyAttribute`) — there is **no referenced `DotKt.Runtime` DLL** (that runtime is
ELIMINATED; the real CLR stdlib superseded it). They are metadata-only, never executed ([[dotkt-naming-and-runtime-split]]).

### Provenance

Full-name equality is not enough to identify these internal carriers: an ordinary C# assembly can declare a
lookalike. DotKt therefore stamps `[assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")]` and stamps each
embedded carrier definition with `[CompilerGenerated]`. `dll2klib` accepts Kotlin metadata only when both signals are
present. An unmarked `DotKt.Runtime.CompilerServices.Kotlin*Attribute` is treated as an ordinary third-party attribute
and cannot enable Kotlin-only reverse mappings. Outputs from compilers predating this provenance contract must be
rebuilt; there is deliberately no namespace-only compatibility fallback because it recreates the false-positive
classification.

## Pipeline

```
  emit:   BirEmitter records infix/operator/suspend, inline bodies, read-only fields, file classes,
          and reference nullability
            -> ilemit stamps [assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")],
               compiler-generated embedded carrier definitions,
               [KotlinFunction(flags)] / [KotlinFileClass] / [KotlinInline(body)] / [KotlinReadOnly] /
               .NET NRT [Nullable*]
  project: dll2klib verifies assembly + carrier provenance, then writes standard KLIB metadata
            (suspend: Task<T> unwrapped to T)
            CLR `op_*` methods map back to Kotlin operator names through the standard operator table
            CLR events restore as ClrEvent<T> endpoints from EventInfo + delegate Invoke
            top-level declarations carry their physical file-class owner in `@ClrExternal`
            field mutability, inline body availability, and NRT nullability
  resolve: kotc's ordinary KLIB symbol provider restores the projected Kotlin declarations
  backend (consumer): a call to a restored top-level fun forwards its `@ClrExternal` owner into BIR;
            a restored suspend call's .NET return is Task<T> (coTaskType), awaited by the coroutine machinery;
            a restored event subscription lowers to add + an EventSubscription close-token whose callback invokes
            remove with the exact same handler bound to the event's exact CLR delegate type.
```

### `[KotlinNullableGeneric]` — which slots carry it, and why it needs the NRT byte too

`bir2cir` erases `Nullable(Tv)` to `object` at **every** slot (`docs/dotkt-semantics.md` §9c-bis), and records the
pre-erasure type node on the same slot. The position set is therefore the full declaration surface, not a subset:

| slot | carrier rides |
|---|---|
| method return | `retAttrs` |
| method parameter | the parameter's `attrs` |
| **constructor** parameter | the parameter's `attrs` |
| field | the field's `attrs` |
| property | the property's `attrs` |

At each, the erased `Nullable(Tv)` may be the slot's **head** (`x: T?`) or **nested** (`Holder<T?>`, `Array<T?>`,
`(T) -> T?`, `Holder<T?>?`). The two need different amounts of help on the way back:

- A reader **strips the carrier's outer nullability** before use — the carrier owns the inner tree, and the slot's own
  `[Nullable]` byte owns the outer `?`, exactly as it does for `[KotlinSuspendFunctionType]`. So a nested erasure
  round-trips on the carrier alone, while a **head** erasure additionally needs a `[Nullable(2)]` byte or it restores
  as a non-null `T`.
- That byte cannot come from the ordinary decl-position NRT walk, which runs **after** the erasure and would walk
  `object` — whose non-null default emits no override at all. It is computed from the **pre-erasure** type by the
  recorder, and rides `DeclNullableFlags`' never-overwrite contract.
- **A VALUE head is the one case the byte cannot carry at all**, and there the carrier keeps its own outer `?`. An
  NRT byte array describes reference nodes only — `Int` contributes none — so `Int?` stripped to `Int` plus a
  `[Nullable(2)]` byte restores as a non-null `Int`. The reader therefore strips the outer nullability only when the
  inner can consume a byte (a type variable or a reference type). The slot that has this shape is the #86 D3 override
  bridge: a physical `object` over a declared, concrete `Int?`.

Dropping either channel is invisible to every runtime-shaped gate: the producer is unchanged and only a separately
compiled Kotlin **consumer** fails, at compile time, with a type mismatch against the degraded slot.

#### The carrier has two readers, and `bir2cir` is one of them

`dll2klib` reads it to restore the **Kotlin surface** a consumer compiles against. That is not the whole job: the
consumer then emits calls against the *restored* surface — `unwrapSlot(Slot<Int?>)` — while the producer's CLR slot is
`Slot<object>`, and the two are unrelated invariant reified generics that no cast reconciles. So `bir2cir` reads the
same carrier off the referenced assembly (`ReferenceMetadataIndex.TryNullableGenericSlot`) and types every USE of that
slot as `Subst(Erase(declared), typeArgs)` — the identical formula it applies to a same-module declaration. Without
that second reader the consumer compiles and then corrupts memory: a `Slot<Nullable<int32>>` handed to a callee that
reads it as `Slot<object>` faults in `CastHelpers.Unbox_Nullable`.

The reader's discipline, in order:

- **the carrier first.** It is the only statement that a given `object` came from an erasure at all.
- **the physical declaration second, and only while it still carries a `Tv`.** The producer emitted its signature
  through the same `Erase`, so a `Slot<T>.get_value(): !0` or a `List<E>.get(i): !0` is the real declaration and
  substituting the call's type arguments into it is exact. A `Tv`-free physical slot is refused: the one thing it
  could contribute is a bare `System.Object`, which without a carrier beside it is indistinguishable from a declared
  `Any` — and deriving every `Any`-returning member's use as `object` is not this family, it is all of them.
- **a same-shape overload set is refused, never guessed.** Name, static-ness, parameter count and generic arity are
  all a call site gives; picking a sibling would manufacture the mismatch the pass exists to remove.
- **a member declared at a level ends the search**, facts or no facts — a concrete member that shadows an inherited
  namesake IS the declaration the call binds to, and the base's carrier is not a stand-in for it. The walk over
  base and interfaces is path-local and keyed on the CONSTRUCTED supertype, so `I<int>` and `I<string>` are both
  visited and must agree rather than the first one reflection happens to report winning.
- **a refusal is not a fallback.** Where the carrier is refused the slot yields nothing; the physical declaration is
  not substituted for it, because it is the same erasure spelled without the evidence that it was one.
- **an `Array<X?>` slot is served like any other (#86 D2).** It used to be refused, because it was the one position
  where the producer did not implement what its own slot said — `Array<T>.copyOf(newSize)` declared `Array<T?>`
  (physically `object[]`) and reflectively allocated a `Nullable<V>[]`. `Array<X?>` is now canonically `object[]` for
  every possibly-value `X`, so the carrier's erasure and the emitted signature agree and the slot is derivable.
- **a call that states its result only in `sty` is out of the axis**, so a cross-module generic factory's erased
  return is still a formal-only finding. Deriving it needs the parameter half of the func-slot erasure first: the
  same call's function-type argument would otherwise be handed a delegate the consumer cannot build erased.

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
| cross-module **reified** inline | ✅ emitted as a real generic method; consumed as `f<Int>()` (CLR generics are reified, so the `T::class`/`is T` body works) |
| cross-module inline + lambda with **non-local return** | ✅ carried as raw BIR and spliced by bir2cir |

**Where inlining happens.** DotKt does NOT run the standard JVM IR `FunctionInlining` lowering — its pipeline is the
four layers `dll2klib` / `kotc` / `bir2cir` / `ilemit` (`native-cir` is the target; the frontend is
`…Fir2Ir then ClrBackendPhase`, no JVM lowerings). **Inlining (the `[KotlinInline]` splice) is a `bir2cir` (BIR→CIR)
responsibility.** kotc projects the call and caller-lambda body to a `callInline` BIR node; bir2cir resolves either
the same-module raw stash or the referenced `[KotlinInline]` payload and performs the splice. Lambda-less inline funs
are left as ordinary calls for the JIT, and `inline`/`reified` are pure decoration unless a lambda literal is passed.
Because inlining is over raw BIR, the
cross-module fix is **lighter than JVM's `@Metadata`** (no frontend IR deserializer):

1. **emit** — `bir2cir` freezes the raw inline payload
   `{v:1,fqn,owner,fileClass,recv,static,typeParams,params,ret,body,lifted}` before lowering; `ilemit` stamps those
   bytes verbatim as `[KotlinInline("bir-json/1", content)]`. `lifted` closes the body transitively over every
   `generated:true` file-class method reached by a `newDelegate`.
2. **project** — `dll2klib` preserves the inline modifier in KLIB metadata; the body stays in the assembly
   and is read at splice time.
3. **resolve** — the ordinary KLIB frontend sees the function as inline, so the consumer frontend accepts a
   non-local `return` through the lambda.
4. **splice** — the consumer's `bir2cir` emits an `inlineSplice` node (the call's value/lambda bindings), reads
   `[KotlinInline].Body` from the `--ref`'d library, parses it, re-hoists carried generated methods into the
   consumer file class under fresh names, and splices the callee body HERE with substitution: a
   callee param `local` → its bound value; a `delegateInvoke` of a lambda param → the caller's lambda body (binding its
   param). A `return` in that spliced lambda body emits a `ret` from the caller → non-local return works. CFG labels are
   freshened per splice. ilemit only realizes the resulting CIR.

Scope: lambda-taking inline funcs only (the only ones whose body must travel; lambda-less calls degrade to
plain/generic methods). Splice hygiene covers callee locals, labels, generic lifted methods, transitively nested
non-capturing lambdas, member inline functions, and non-local returns.

## Fixed along the way (2026-06-24)

- **Member `suspend fun` returning a USER type** (`suspend fun f(): Vec`) — previously crashed ilemit
  (`AsyncTaskMethodBuilder<Vec>`/`Task<Vec>`/`TaskAwaiter<Vec>` are TypeBuilderInstantiations whose `GetMethod`
  throws). Fixed by the `GenM` helper (re-anchor the open method via `TypeBuilder.GetMethod`) across the coroutine
  state machine, **and** by the `EmitClrCall` return-type substitution: `TypeBuilder.GetMethod` leaves the method's
  return type open (`TaskAwaiter`1<!0>`), so the runBlocking/await path mis-typed its temp — now it trusts the BIR
  `ret` hint. Works through both a `suspend fun` and a `runBlocking { … }` lambda.
- **Parameter names** — ilemit defined methods by type only (never `DefineParameter`), so names were lost and
  dll2klib fell back to `arg0`/`arg1`, blocking named-argument calls across a boundary. Now emitted; `f(b = 2, a = 1)`
  round-trips. (The names were always in the BIR — it was purely an emit omission, not a FIR limitation.)

- **Kotlin package → .NET namespace (was FOUNDATIONAL; FIXED 2026-06-24).** DotKt used to flatten all packages to the
  **root** namespace — a correctness bug (`alpha.Box` + `beta.Box` both emitted `.NET Box` → collision/ilemit crash) and
  the blocker for packaged consumption. `BirEmitter.typeName()`/`fileClassName()` now qualify top-level
  classes/interfaces/enums and the file facade with `packageFqName` (nested types stay simple; root-package unchanged).
  Verified: `alpha.Box`+`beta.Box` coexist, and a `package geom` library is consumed via `import geom.Vec`/`import
  geom.Dir`/`import geom.greet` (top-level too) through the MSBuild import-scan flow.

## Known gaps / follow-ups

- Extension functions (`fun T.f()`) restore as plain statics, not Kotlin extensions, yet (a future flag/marker).
- Remaining roundtrip limitations are tracked in GitHub Issues and covered where possible by `tests/roundtrip/`.

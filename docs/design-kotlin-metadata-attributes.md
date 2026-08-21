# Consuming a DotKt assembly AS KOTLIN — metadata attributes

**Status: v1 implemented & verified (2026-06-24).** `tests/roundtrip/consumer`.

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
| named/default `companion object` | compiler-reserved singleton carrier, nested in a non-generic owner and hoisted beside a generic one | no; CLR nesting does not record the source companion association/name | `[KotlinCompanion(version, bytes)]` on the physical carrier type |
| `class C { companion { … } }` member | a real static member of `C` | **yes** — a CLR static member IS the shape (`IS_STATIC_FUNCTION`/`IS_STATIC_PROPERTY`) | (none) |
| `companion fun C.foo()` / ordinary `companion val C.bar` | released C# 14 static extension-member graph; a generic receiver additionally uses a source-named CLR wrapper plus an unspeakable receiverless Kotlin core | receiver/name/kind: **yes**, from `ExtensionAttribute` + `ExtensionMarkerAttribute`; a generic wrapper's Kotlin call target is not expressible in the standard graph | generic wrapper only: `[KotlinExtensionCore(version, bytes)]` carrying `{name}`; it links wrapper to core and does not duplicate extension semantics |
| suspend companion extensions and context-parameter properties (staged migration) | receiverless, collision-free static of the `<File>Kt` class | no; this temporary physical member does not preserve its association or role | `[KotlinCompanionExtension(version, bytes)]` carrying `{receiver, name, kind}` |
| C# 14 companion extension property with Kotlin-only storage semantics (`const` / `lateinit`) | standard extension Property graph plus private file-facade storage | the receiver/name/accessors are recoverable, but a CLR Property row cannot identify the storage owner/field whose `Literal` / `[KotlinLateinit]` fact belongs to it | `[KotlinPropertyStorage(version, bytes)]` on the implementation getter carrying only `{owner,field}`; receiver/name/kind are not duplicated |
| field-backed `lateinit var` | ordinary mutable field plus checked Kotlin reads | no; CLR metadata has no `lateinit` modifier | `[KotlinLateinit]` on the backing field; `dll2klib` restores `IS_LATEINIT` and sets the declaration-owned `@ClrField` flag used by Kotlin 2.4 static-property fake overrides |
| companion-block statics on generic `G<T>` | members of one compiler-generated, public, non-generic CLR carrier | CLR statics on `G<T>` would be duplicated per closed type and a bare generic owner is not a legal MemberRef parent | `[KotlinStaticCarrier(version, bytes)]` on the carrier names semantic `G`; `dll2klib` merges its declarations back into `G` |
| inline function body needed for cross-module lambda/non-local-return splicing | ordinary method | no | `[KotlinInline("bir-json/1", content)]` |
| `@ClrTypeAlias` constructor whose Kotlin delegation ends at a different physical signature | the selected physical CLR constructor | no; the alias TypeDef/body is absent from the runtime twin | `[KotlinConstructorAdapter(version, bytes)]` on the reference constructor, carrying the declaration parameter vector, terminal arguments, and terminal signature |
| Kotlin `val` backed by a **`@ClrField` public field** | public field | no; a plain public field looks writable | `[KotlinReadOnly]` — survives **only** for the `@ClrField` plain-field case; a normal `val` is now a get-only CLR property, recoverable from plain metadata (see [design-clr-property-model.md](design-clr-property-model.md)) |
| reference-type nullability (`String?`) | .NET nullable reference metadata | yes for NRT-aware tools; must be emitted | `[Nullable]` / `[NullableContext]` |
| a nullable GENERIC `T?` on an unconstrained `T` (`fun <T> f(x: T?): T?`, `Holder<T?>`, `Array<T?>`, `(T) -> T?`) | `System.Object` at that position — the one CLR slot that carries a real null for a value AND a reference instantiation (#86) | no; `object` names neither `T` nor the `?` | `[KotlinNullableGeneric(pre-erasure type node)]` **plus** the slot's own `[Nullable(2)]` byte |
| imported CLR event endpoint (`CLREvent<T>`) | real CLR event metadata + add/remove accessors | yes for the event itself; Kotlin endpoint syntax must be synthesized | plain CLR event metadata, no DotKt attribute by default |
| `final`/`open`/`abstract` (modality) | non-virtual / virtual / abstract | **yes** — rides .NET virtual-ness | (none) |
| visibility | public/assembly/family | **yes** | (none) |
| generics, including `reified` | real CLR generic method `<T>`; reified method parameters add hidden Boolean nullability witnesses | runtime type: yes; Kotlin nullability/reified indices: no | `[KotlinDeclarationIdentity]` carries the reified indices |

The attributes are **compiler-EMBEDDED per-assembly** as internal `DotKt.Runtime.CompilerServices.*` types (like csc's
own `NullableAttribute`/`IsReadOnlyAttribute`) — there is **no referenced `DotKt.Runtime` DLL** (that runtime is
ELIMINATED; the real CLR stdlib superseded it). They are metadata-only, never executed ([[dotkt-naming-and-runtime-split]]).

### Provenance

Full-name equality is not enough to identify these internal carriers: an ordinary C# assembly can declare a
lookalike. DotKt therefore stamps `[assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")]` and stamps each
embedded carrier definition with `[CompilerGenerated]`. `dll2klib` accepts Kotlin metadata only when both signals are
present. An unmarked `DotKt.Runtime.CompilerServices.Kotlin*Attribute` is treated as an ordinary third-party attribute
and cannot enable Kotlin-only reverse mappings. Artifacts without the current provenance contract are outside the
supported input set; there is no namespace-only compatibility fallback because it would recreate false-positive
classification.

## Pipeline

```
  emit:   BirEmitter records infix/operator/suspend, companion-extension receiver/name/kind, inline bodies,
          read-only/lateinit fields, file classes, and reference nullability
            -> ilemit stamps [assembly: AssemblyMetadata("DotKt.Compiler", "metadata-v1")],
               compiler-generated embedded carrier definitions,
               [KotlinFunction(flags)] / [KotlinFileClass] / [KotlinCompanionExtension(receiver,name,kind)] /
               [KotlinExtensionCore(wrapper-to-core)] /
               [KotlinInline("bir-json/1", content)] / [KotlinConstructorAdapter(delegation)] / [KotlinReadOnly] / [KotlinLateinit] /
               [KotlinPropertyStorage] / [KotlinStaticCarrier] /
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

### `[KotlinCompanion]` — association and source name on the CLR carrier

`kotc` always emits one representation-neutral semantic companion declaration carrying
`{owner:"p.Host",name:"Factory",visibility:"public"}`. Its identity is deliberately non-physical
(`p.Host.<companion:Factory>`): `kotc` emits no CLR `INSTANCE` and does not choose a physical owner.

`bir2cir` consumes that logical declaration and creates one ordinary CLR carrier for it, spelled with a `$` the
source cannot write, so this physical namespace cannot collide with source declarations. The carrier owns the
companion's instance members, supertypes, constructor effects, and one public static self-typed `$INSTANCE`. It never
declares a generic parameter of its own — one closed TypeDef is what makes `$INSTANCE` one singleton — and where it
lives follows from the owner's genericity:

| physical owner | carrier `kind` | physical identity |
|---|---|---|
| non-generic | `nested` | nested in the owner, named `$` + source name (`p.Host+$Factory`) |
| generic (own or captured slots) | `sidecar` | top-level beside the owner, the owner's `+` nesting path flattened to `$`, then the reserved `$companion$` marker (`p.Host$companion$Factory`, `p.Outer$Inner$companion$Factory`) |

A nested TypeDef of a generic type redeclares every enclosing slot, so a nested carrier of `Foo<T>` would be a
*different closed type* — and therefore a different singleton and a different static region — for `Foo<int>` than for
`Foo<string>`. Kotlin declares one companion on the class declaration, and that companion does not have the owner's
`T` as a parameter of its own, so the carrier is hoisted out of the owner instead. The outer source-name field remains
an ordinary CLR static, of which a generic owner has one per closed instantiation; each is initialized from the single
carrier singleton, so `Foo<int>.Companion` and `Foo<string>.Companion` read the same reference and share companion
state. Leaving the owner costs the carrier CLR nested access to the owner's private declarations; the ordinary
`[UnsafeAccessor]` projection restores every such lexical edge without widening any target member.
A basic CLR enum still owns the nested carrier and round-trips as an enum, but has no outer source-name CLR field:
ECMA-335 enum types cannot own the `.cctor` needed to initialize that reference-valued accessor. Degrading the enum to
a class would lose enum-entry semantics, so the C# accessor is explicitly deferred for this owner kind.
The trusted `[KotlinCompanion(version, bytes)]` payload records `kind`, semantic `owner`/`name`/`visibility`, and the
exact CLR metadata identity `physicalOwner` (including `+` nesting) plus declared generic arity. `ilemit` emits that
authored CIR attribute 1:1.

`dll2klib` validates every explicit carrier and its physical declaration before hiding or projecting anything — a
`nested` claim over a generic owner and a `sidecar` claim over a non-generic one are both refused, as is a carrier
whose CLR nesting contradicts its kind. It then attaches the physical type to the carried owner as a metadata
`COMPANION_OBJECT`, retaining supertypes and instance members while hiding the physical `$INSTANCE` field. It sets
`companion_object_name` and `nested_class_name`; no path recognizes a
companion from a CLR suffix, the word `Companion`, or member names. Consequently a named companion and an unrelated
nested class called `Companion` remain distinct, and arbitrary CLR static projection keeps its existing synthetic
companion behavior without masquerading as a restored Kotlin declaration. A protected companion on an externally
visible owner is projected with protected Kotlin class/member flags; its carrier is NestedPublic because generated
reference/state-machine helpers are not subclasses of the source outer class. `dll2klib` also maps carrier type
signatures back to the exact nested companion classifier ID,
not a similarly rendered dotted top-level name. A valid but source-invisible association
(a private/internal semantic companion or any companion whose owner/carrier is not externally visible) is skipped before
projection indexes or hiding are populated, so it cannot abort assembly conversion or synthesize a public empty companion.

### `[KotlinNullableGeneric]` — which slots carry it, and why it needs the NRT byte too

`bir2cir` erases a possibly-value `X?` to `object` in every reified ARGUMENT and an open `Nullable(Tv)` everywhere
(carrier-argument erasure, `docs/dotkt-semantics.md` §9c-bis), and records the pre-erasure type node on the same
slot. The position set is therefore the full declaration surface, not a subset:

| slot | carrier rides |
|---|---|
| method return | `retAttrs` |
| method parameter | the parameter's `attrs` |
| **constructor** parameter | the parameter's `attrs` |
| field | the field's `attrs` |
| property | the property's `attrs` |

ELIGIBILITY is the erasure's own rule, so it covers a CONCRETE argument as well as an open one: a slot needs the
carrier when it has an open `Nullable(Tv)` anywhere, or a possibly-value `X?` in a reified argument — `List<Int?>`,
`Box<Int?>`, `Array<Int?>`, `(Int?) -> R`, `List<List<Int?>>`. Without it a reader sees only the `object` argument
and restores `List<Any?>`, a DIFFERENT Kotlin type a consumer's own `List<Int?>` cannot bind to. A direct `Int?`
HEAD is deliberately not eligible: it keeps its `System.Nullable<int32>` and reads back without help.

### `[KotlinSupertypes]` — the edge a per-slot carrier cannot reach

A SUPERTYPE ARGUMENT erases like any other reified argument, and it is the one erased position with no declaration
slot to hang a carrier on. Left unrestored it is a **Kotlin SOURCE break**, not an internal one: a consumer that
re-imports `class E : Sink<Int?>` sees `Sink<Any?>`, so `val s: Sink<Int?> = E()` no longer compiles. Member carriers
cannot repair it — every member's own slot is already exact, and what was lost is the identity of the EDGE — and
source compatibility is the one thing an internal representation decision may not spend. So the edges ride a
TYPE-LEVEL carrier:

| carrier | rides | payload |
|---|---|---|
| `[KotlinSupertypes(version, bytes)]` | the type's `attrs` | `{base?, interfaces?, bounds?}` of pre-erasure TypeNodes |

The payload is the same opaque TypeNode encoding every other carrier uses, so no new format is introduced; `bounds`
maps a TYPE parameter's index to the pre-erasure list of that parameter's upper bounds, which erase for the same
reason and are lost the same way. Each member is recorded only where the erasure actually moved that position — an
edge it did not touch is not on the carrier, so the consumer's own projection decisions for it stand — and an
ordinary type carries nothing at all. dll2klib reads all three back (`RestoreErasedSupertypes`): the two supertype
members replace a projected edge by head, `bounds` is applied to the type parameter at that index.

Two boundaries of `bounds`, both measured. A METHOD's type-parameter bounds are not on it — this carrier is
type-level and giving a member one is a channel that does not exist — so a `fun <T : Sink<Int?>> f()` still
re-imports its bound as the physical `Sink<object>`. For a CLASS type parameter, dll2klib first projects the ordinary
CLR constraint rows directly, so an unmoved `class Box<T : Sink<String>>` retains that bound without help from the
carrier. `bounds` then replaces or adds only the pre-erasure form that the carrier actually records. The CLR-only half
is not invented as Kotlin nominal bounds: dll2klib omits a type or member parameter's implicit
`System.ValueType`/`System.Enum` rows (including the custom-modified `ValueType` row for `unmanaged`), which no Kotlin
classifier can inhabit. bir2cir validates those rows together
with the `class`, `struct`, and `new()` generic-parameter flags against each physical type construction and each
generic member use from authoritative reference metadata. Member constraints come from the exact resolved MethodDef;
their declarations remain in the callee's frame while supplied arguments are interpreted in the caller's frame.
The validation follows metadata rather than Kotlin callability: a rich Kotlin enum has a reference-class
representation and therefore is not a CLR enum, and default arguments do not make a nonzero-parameter constructor
satisfy `new()` unless a public zero-parameter `.ctor` is actually emitted.

The channel has two independent producers. Nullable-generic erasure records every edge or bound its positional
`Erase` rule moves. Collection-identity recording does the same when Root-V lowering collapses a nested read-only
`List`/`Set`/`Collection` onto its invariant CLR sibling. They merge by edge head and type-parameter index before
the attribute is authored: when both transforms touch one edge, the earlier producer's less-erased TypeNode wins;
unrelated moved edges and bounds are appended. Thus `class B : Box<List<String>>` re-imports with that Kotlin edge,
not the physical `Box<IList<string>>`, without teaching dll2klib which transform produced the correction.

Collection-bearing type edges are captured at the last all-Kotlin boundary, before inner applications rotate to CLR
argument order, F-bound stars become existential views, or reference nullability is stripped. Their classifier names
are bound later to exact nested metadata paths without changing the captured Kotlin argument order. A `bounds` key is
the parameter's flattened publication index, including captured outer parameters of an `inner` class; that is the
same frame ilemit emits and dll2klib assigns to `TypeParameter.Id`.

`dll2klib` restores the edges by HEAD, not by position, and that is load-bearing: the projected supertype list is not
a transcription of the metadata's interface list — it drops the non-generic shadows, collapses the `IComparable`
bridge and synthesizes `kotlin.Throwable`/`kotlin.Any` edges — so an index would line up with nothing. Replacing an
entry whose class name and argument count the carrier also names keeps every one of those decisions and moves only
the arguments, which is all that was erased. Witnessed cross-module by
`roundtrip-nullable-vt-generic-supertype-edge`.

At each carried slot, the erasure may be at the slot's **head** (`x: T?`) or **nested** (`Holder<T?>`, `List<Int?>`,
`Array<T?>`, `(T) -> T?`, `Holder<T?>?`). The two need different amounts of help on the way back:

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
  inner is one the byte walk emits a byte for: everything except the Kotlin primitives (`Boolean`/`Char`/the sized
  integers and floats, signed and unsigned) and `Unit`. The slot that has this shape is the #86 D3 override bridge: a
  physical `object` over a declared, concrete `Int?`.

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

## `inline` bodies and `reified` nullability

From the **consumer surface**, an imported inline body and a reified type parameter are separate facts:

- **CLR generics carry the runtime type**, so a Kotlin `inline fun <reified T>` remains an ordinary CLR generic
  method `M<T>()`. CLR does not carry Kotlin's nullable-instantiation bit, so `bir2cir` appends one hidden Boolean
  parameter per reified method type parameter. `[KotlinDeclarationIdentity]` records their indices; `dll2klib` hides
  the physical parameters, while consumer `bir2cir` uses the trusted indices to pass or forward each witness. The
  KLIB declaration remains an ordinary generic as before, preserving DotKt's non-reified-parameter allowance. A body
  lifted into a closure, SAM shim, suspend state machine, or generated object captures the witness using the lift's
  explicit type-argument correspondence. Physical call arguments are materialized only after Kotlin factory and
  intrinsic recognition, so those semantic passes continue to see exactly the source-visible arity.
- No full frontend body is needed merely for reification. `[KotlinInline]` carries raw BIR only when lambda/non-local
  return splicing requires the body.
- The **only** thing true inlining buys that a generic-method call can't: a **non-local return through a lambda
  parameter**. Without the body we can't inline, so such a call simply won't compile on the consumer (a normal
  "return not allowed here") — not silent breakage.

So `[Reified]`/`[ReifiedInline]`/`[KotlinInlineBody]` are **not part of the design.** Empirically (2026-06-24), the
cross-assembly inline matrix is:

| case | result |
|---|---|
| same-module inline (incl. non-local return, crossinline) | ✅ existing (`il-inline`/`il-inline2`/`il-xinline`) |
| cross-module **non-reified** inline | ✅ emitted as a normal method; consumed as a regular (non-inlined) call |
| cross-module **reified** inline | ✅ real generic method plus hidden nullability witness; physical witness parameters stay hidden from Kotlin source |
| cross-module inline + lambda with **non-local return** | ✅ carried as raw BIR and spliced by bir2cir |

**Where inlining happens.** DotKt does NOT run the standard JVM IR `FunctionInlining` lowering — its pipeline is the
four layers `dll2klib` / `kotc` / `bir2cir` / `ilemit` (`native-cir` is the target; the frontend is
`…Fir2Ir then ClrBackendPhase`, no JVM lowerings). **Inlining (the `[KotlinInline]` splice) is a `bir2cir` (BIR→CIR)
responsibility.** kotc projects the call and caller-lambda body to a `callInline` BIR node; bir2cir resolves either
the same-module raw stash or the referenced `[KotlinInline]` payload and performs the splice. Lambda-less inline funs
are left as ordinary calls for the JIT. `inline` is decoration unless a lambda literal is passed; `reified` still
controls the nullability-witness ABI.
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

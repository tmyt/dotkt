# BIR/CIR Specification (v1 — frozen contract)

> NORMATIVE. This is the single source of truth for the BIR/CIR serialization format. Every layer
> (kotc emit / bir2cir consume+produce / ilemit consume / dll2klib carrier decoding / [KotlinInline] splice)
> implements to THIS. Earlier freeze proposals and producer/consumer audits are preserved in Git history.
> Durable-ABI principles: uniformity, self-describing, additive-extensible,
> codec-agnostic, single-source. **BIR contains NO stringly-typed compound tokens — types are nodes.**

## 0. Envelope & versioning
- **STATUS (#37 m6): LANDED.** The carrier attributes stamp `(string version, byte[] content)`. `version` =
  `"bir-json/1"` today (future binary = `"bir-msgpack/1"`; schema bump = `"bir-json/2"`). `content` = the
  codec-encoded body (today `UTF8(json)`).
- A single `BirCarrier.DecodeBody(version, byte[])` / `EncodeBody(version, node)`
  (`toolchain/bir-common/TypeNode.cs`) dispatches on `version`. An UNKNOWN version is REJECTED
  (loud `NotSupportedException`, never a silent mis-decode).
- Carriers include `KotlinInlineAttribute(string version, byte[] content)` (inline-fn body),
  `KotlinSuspendFunctionTypeAttribute(string version, byte[] content)` (a `suspend (…) -> T` position's
  pre-erasure `fn` `Type` shape), and `KotlinConstructorAdapterAttribute(string version, byte[] content)`
  (an `@ClrTypeAlias` constructor's declaration-authored terminal delegation vector). The latter lets a consuming
  `bir2cir` lower an adapter such as `(cause)` to the exact physical `(message,cause)` constructor without recovering
  semantics from a name or choosing a same-arity CLR member. The payload carries
  `{parameters,signature,statements,arguments,terminalSignature}` in BIR vocabulary. `statements` is the ordered
  evaluation prefix authored by constructor delegation; a consumer substitutes its materialized source arguments
  and executes that prefix before evaluating the terminal physical constructor argument vector.
  The old bare `(string)` ctors are DELETED (no dual-track). Producers
  (`ilemit` `ApplyKotlinInline` / `ApplySuspendFnType`) and consumers (`bir2cir` cross-module splice,
  `dll2klib` carrier decoding) all route through the one codec.
- A decoded `[KotlinInline]` content is the current payload object
  `{fqn,owner,fileClass,recv,static,typeParams,params,ret,body,lifted}`. The carrier codec string (`bir-json/1`)
  identifies its codec and schema. `body` is the raw BIR body.
  `lifted` is the transitive closure of raw, compiler-generated file-class method declarations reached by
  `newDelegate` edges from `body`; every entry MUST carry `generated:true`, `static:true`, `params`, `ret`,
  and `body`. `fileClass` is the declaration identity those carried delegate edges originally target.
  At a cross-module splice, bir2cir re-hoists the complete `lifted` set into the consuming file class under
  fresh names and rewrites the delegate edges before normal lowering. Same-module splices use the original
  declarations. The payload is closed structurally from `generated:true`; generated-name spelling is not an
  ownership signal. Readers consume this current shape directly; compiler artifacts with another shape are outside
  the supported input set.

## 1. Type — the universal type representation (FULL structured, no exceptions)

A `Type` is ALWAYS a JSON object with a `t` discriminator. **There is no bare-string type.** Readers
`dispatch(t)`; they never split/scan a string. `T` below denotes a nested `Type`.

| `t` | fields | Kotlin meaning | replaces (old string token) |
|-----|--------|----------------|-----------------------------|
| `fqn` | `name:string`, `args?:[T…]` | a named type `kotlin.collections.List<…>` — a PURE Kotlin/CLR FQN identity, generic args optional | plain FQN, `clr:`, `clrg:Name[..]`, `@Name`/`@Name[..]`, primitive shorthand (`int`/`string`/`void`/`object`/…) |
| `tv` | `scope:"type"\|"method"`, `i:int` | a type variable — `scope` is the CLR generic-param SPACE, `i` the owner-local positional index | `gp:X` (name-keyed, space-blind) |
| `star` | — | a Kotlin `*` projection; preserved in BIR so bir2cir can choose a provenance-backed CLR existential representation | no CIR form |
| `fn` | `suspend:bool`, `ret:T`, `params:[T…]`, `recv?:T` | a function type; `suspend` is a flag, `recv` = extension receiver | `func:ret:args`, `sfunc:ret:args` |
| `nullable` | `of:T` | `T?` (NRT-annotated nullable, `NullableAttribute`=2) | `nullable:X` |
| `oblivious` | `of:T` | `T!` — an NRT-*oblivious* flexible type `(T..T?)` (`NullableAttribute`=0); the CLR term, not the Kotlin-consumer "platform" name | the META `!` platform suffix |
| `array` | `elem:T`, `rank?:1..32` | `Array<T>`; an absent `rank` is the SZ vector `T[]`. A **CIR-only** stated rank names the general ECMA ARRAY: ≥2 is `T[,…]`, and 1 is the non-vector `T[*]`, a distinct type from `T[]` | `array:X` |
| `byRef` | `of:T` | a CLR by-ref `ref T` | `byRef:X` |
| `ptr` | `of:T` | **CIR-only**: a CLR unmanaged pointer `T*` | no Kotlin form |
| `mod` | `req:bool`, `m:T`, `of:T` | **CIR-only**: an ECMA custom modifier (II.7.1.1) at its signature position — `req` selects modreq from modopt | no Kotlin form |

Notes:
- **The three CIR-only ECMA signature carriers** (`ptr`, `mod`, `array.rank`) exist for one reason: a §2.2.2
  `memberRef` must be able to spell any signature the *target metadata* can declare, and these three shapes
  are declarable there while being unspeakable in Kotlin. They are not new expressiveness for the source
  language — kotc MUST omit all three, and the validator refuses them in BIR. Dropping them is not neutral:
  `T*` degrades to the FQN string `"System.Int32*"`, an identity naming no type; `T[,]` and `T[*]` collapse
  onto `T[]`;
  and `void V(in DateTime)` becomes indistinguishable from `void V(DateTime)`, since `modreq(InAttribute)` on
  a by-ref parameter is the only thing separating them. Each is a way for two distinct members to look like
  one, which is exactly what a resolved reference must never allow.
- **No CLR-resolution marker in kotc output.** kotc emits `{t:"fqn",name:"kotlin.Int"}` — the *identity*
  only. bir2cir DERIVES the CLR form (primitive opcode, generic construction, referenced-type resolution).
  `clr:`/`clrg:`/`@`/shorthand are DELETED; the resolution decision lives below the kotc boundary.
- **`tv` is scope-tagged + positional**, killing the `gp:`-name remap (`CanonSig`/`FindReflectedMethodBySigLoose`
  deleted). `scope:"method"` → the method's own generic params (CLR `!!i`, `GenericMethodParameter`);
  `scope:"type"` → the enclosing TYPE's generic params (CLR `!i`, `GenericTypeParameter`), where `i` is
  FLATTENED over the nesting chain (a nested generic type repeats its enclosing types' params — kotc computes
  the flattened type-index, as it already does). The two spaces are DISTINCT on the CLR; a single flat index
  conflating type+method is a Reflection.Emit bug (Codex-confirmed), so `scope` is MANDATORY. bir2cir/ilemit
  map `scope`+`i` straight to `!i` / `!!i`.
- `fn` subsumes both plain and suspend function types; the H2 position metadata is just an `fn` with
  `suspend:true` in a param/return/field slot — no separate `sfunc:` token, no `BirTokenToMeta`.
  kotc BIR contains only that Kotlin function shape and MUST omit `clr`. During type lowering bir2cir
  adds the CIR-only `clr` delegate-family field: `System.Action` for a void return and arity 0–16,
  `System.Func` for a value return and arity 0–16, otherwise
  `DotKt.Runtime.CompilerServices.KAction`/`KFunc`. Arity includes an extension receiver.
  Every `fn` reaching ilemit MUST carry this field; ilemit realizes that exact nominal delegate type
  and never chooses a family from TypeBuilder state or assembly names.
  The stdlib builds also receive CIR-only `kind:"delegate"` type declarations for the selected wide family.
  Such a declaration carries its exact arity-qualified CLR metadata name, type parameters and variance, `params`,
  and `ret`; ilemit maps that physical declaration to the conventional `MulticastDelegate` `.ctor`/`Invoke`
  metadata without choosing any Kotlin ABI fact. kotc never emits this declaration kind.
  A CIR type-parameter descriptor may additionally carry
  `specialConstraints:["class"|"struct"|"new"|"allowsRefStruct"]` when
  bir2cir copies the exact CLR constraint flags of a referenced classifier onto a C# 14 static-extension grouping
  and its implementation wrapper. These are physical CLR facts; kotc does not author them, and ilemit stamps them
  one-to-one alongside the descriptor's structured `constraints`.
  **STATUS (#49): the `funcType` slot is FOLDED.** The delegate-view function type on
  `newClosure`/`newDelegate`/`newSam`/`newSuspendLambda`/`newBoundDelegate`/`delegateInvoke` was the LAST
  string-typed type slot (`func:<ret>:<args>` / `sfunc:<ret>:<args>`); kotc now emits it as the structured
  `fn` node (0 `func:`/`sfunc:` strings in the emitted BIR), bir2cir's `LowerFuncTypeValued` lowers the `fn`
  node via `LowerFnDelegate` (suspend→delegate shape kept for the sequence/iterator closure path; a suspend
  `fn` in a plain type slot still erases to `object`), and ilemit realizes the CIR-selected delegate from the `fn` node
  (`MapType(Fn)`→`FuncType(Fn)`, `FuncArityOf`/`FuncRetType`/`FuncArgTypes` read the node). The dead
  `func:`/`sfunc:` STRING-parsing scanners are deleted from the serialized-type paths.
  **A delegate CONSTRUCTION's `funcType` may also be a named `fqn`.** `newClosure`/`newDelegate` state the delegate
  they PHYSICALLY build, and a literal lambda whose slot declares a different delegate (a custom .NET delegate, or a
  construction of the same family whose return the slot widened) is pointed at that slot's delegate by
  `bir2cir`'s `ClrMemberResolution.DelegateSlots`, which names it directly rather than as a Kotlin function type —
  a custom delegate has no `fn` spelling at all. `delegateInvoke`, whose reader needs the parameter/return vector,
  keeps the structured `fn`. A construction carries no `invokeRef`: it names the constructor it runs, and the
  Invoke its value is called through is stated by the CALL.
- **Nullability is TRI-STATE, named with the CLR/Roslyn vocabulary** (`NullableAttribute` 1/2/0 =
  not-annotated / annotated / **oblivious**). A reference type is one of three states, each a COHERENT node
  naming its own CLR state (the representation must NOT collapse oblivious to nullable — that breaks overload
  resolution + null-safety on every un-annotated BCL member):
  - **not-null `T`** — the BARE type node (no wrapper). Default.
  - **nullable `T?`** — `{t:"nullable","of":T}` (NRT-annotated nullable).
  - **oblivious `T!`** — `{t:"oblivious","of":T}` — a flexible type `(T..T?)`; the frontend/`ConeFlexibleType`
    decides null-safety per use, exactly as Kotlin treats un-annotated Java. `oblivious` is the CLR/Roslyn term
    for `NullableAttribute`=0, NOT the Kotlin-consumer "platform" name — the node states the .NET metadata's
    actual annotation, not how a consumer treats it.
  This is ONE tri-state model shared by BIR and META: **kotc BIR emits not-null + nullable + oblivious** —
  `{t:"oblivious"}` IS produced for a platform/flexible type `T!` (`(T..T?)`), i.e. a reference-KLIB-projected
  `[MaybeNull]`/un-annotated .NET member (`ThreadLocal<Int>.Value`, #8). Fir2Ir attaches the
  `@kotlin.internal.ir.FlexibleNullability` marker onto the flexible IR type (kotc installs the
  `JvmIrSpecialAnnotationSymbolProvider` — see `ClrCliPipeline`), and `BirEmitterTypes.birType` reads it to emit
  `{t:"oblivious"}` instead of collapsing the flexible type to a plain `{t:"nullable"}`. **bir2cir lowers it to the
  BARE inner** (a value `Int!` → bare `int32`, default `0`; a reference `String!` → a bare NRT-oblivious ref) —
  NEVER a `Nullable<T>` wrapper; ilemit has no oblivious case, so the wrapper must not survive bir2cir. A genuine
  user `Int?` (no marker) stays `{t:"nullable"}` → `Nullable<Int32>`. `dll2klib` encodes all three states in
  standard KLIB type metadata (a .NET member with no `NullableAttribute` gets flexible lower/upper bounds).
  `oblivious` is a coherent sibling node (each state names itself), NOT a `nullable`-node refinement flag —
  additive per principle 3. **STATUS (#48): FOLDED — landed.** The old duplicate nullability encodings — the type
  wrapper AND the separate decl-level `"nullable":true` / `"retNullable":true` flags — have collapsed onto the Type
  node: **kotc BIR emits `{t:"nullable","of":T}` UNIFORMLY** for value AND reference AND type-variable `?` (the
  decl-level scalar flags are RETIRED — a type's nullability lives on its Type node, nowhere else). The value-vs-
  reference split is derived BELOW the kotc boundary, on the tri-state model where `{t:"nullable"}` means
  "NRT-annotated nullable" (`NullableAttribute`=2):
  - **bir2cir** (`DeclNullableFlags` → `ReferenceNullableStrip` → `BirTypeLowering`, in that order, all on the
    semantic tree): `DeclNullableFlags` walks each decl slot's Type node and emits the flattened `NullableAttribute`
    byte array (`nullableFlags` on a method/constructor param, field or property; `retNullableFlags` on a method
    return) — the NRT byte-walk now derives from the **type node**, not a flag. `ReferenceNullableStrip` then removes
    EVERY reference
    `{t:"nullable","of":<reference>}` in ANY position (decl slots, owner generic type-args, `argTypes`/`typeArgs`,
    expression `cast`/`type`), leaving a bare ref type (ilemit's `MapType` asserts a VALUE inner, so no reference
    `Nullable<>` may reach it); a VALUE `{t:"nullable","of":<value/struct/enum>}` is KEPT as the structural
    `System.Nullable<T>`. An **unconstrained `T?`** (`{t:"nullable","of":{t:"tv"}}`) erases to `object` in every
    value-holding position (return / field / local accumulator / safe-call & delegate-invoke temp / forEach loop-var
    over a `<T?>` source) — the one CLR rep that carries a real null for BOTH a value and a reference instantiation —
    EXCEPT a top-level generic **param** `T?`, which is kept as the bare `T` + its NRT byte so a reference KLIB round-trips
    the type-param identity (`orDefault<T>(x: T?)`, not a `T`-less `Any?`).
  - **ilemit** (`MapNullable`): a value `{t:"nullable"}` realizes `System.Nullable<T>` (via `TypeBuilder.GetConstructor`
    for an emitted-value-type inner — `EmitNullableCoerced`); a reference is the bare type; the scalar `nullable`/
    `retNullable` reads are retired. ilemit does NOT read `nullableFlags`/`retNullableFlags`: bir2cir's
    `RoundtripMetadata` folds them into the decl's `attrs`/`retAttrs` as a plain `NullableAttribute` entry, which
    ilemit stamps through its generic attribute path (dll2klib projects it into KLIB metadata). The value-vs-reference
    decision is `IsValueType` + generic-constraint driven, per the tri-state model — never a hardcoded FQN set.
- Examples:
  - `kotlin.Int` → `{"t":"fqn","name":"kotlin.Int"}`
  - `List<Int>` → `{"t":"fqn","name":"kotlin.collections.List","args":[{"t":"fqn","name":"kotlin.Int"}]}`
  - `(Int)->String` → `{"t":"fn","suspend":false,"ret":{"t":"fqn","name":"kotlin.String"},"params":[{"t":"fqn","name":"kotlin.Int"}]}`
  - `suspend Foo<T>.()->T?` → `{"t":"fn","suspend":true,"recv":{"t":"fqn","name":"Foo","args":[{"t":"tv","scope":"type","i":0}]},"ret":{"t":"nullable","of":{"t":"tv","scope":"type","i":0}},"params":[]}`

## 2. Node kinds — the `{"k":…}` expression/statement/decl vocabulary

Node kinds stay `{"k":…}`-tagged objects (already structured). The freeze CLEANS them (no representation
change). Canonical set = the live kinds from the audit, MINUS the dead/merged below. Every `type`/`ret`/
`elem`-valued field inside a node now holds a **`Type` node** (§1), never a string.

Deleted producer-zero vocabulary:
- The entire `clr.*` twin family: `clr.const`, `clr.bin`, `clr.un`, `clr.conv`, `clr.obj.eq`, `clr.newarr`,
  `clr.ldelem`, `clr.stelem`, `clr.ldlen`, `clr.str.concat`, `clr.obj.method`, `clr.default`,
  `clr.array.spread`, `clr.stackalloc`, `clr.stack.*`, `clr.constrained.compareTo`, `clr.nullable.*`,
  `clr.enum.*`, `clr.safeCast.value`, `clr.typeof`, `clr.getType`. (The live spelling is the non-`clr.` twin.)
- `sequenceNew` and `tupleNew`: producer-zero construction kinds. `sequenceNew` was retired in the coroutine
  sequence cutover (the real `SequenceBuilderIterator` landed); `tupleNew` is unused (`Pair`/`Triple` construct
  via `new`). Removed from the KINDS set (the rest of the construction family renamed thing-first→operation-first
  `<thing>New`→`new<Thing>` in the same change).

MERGED (same-shape variants → one canonical kind):
- `setField` / `setFieldExpr` / `staticFieldSet` field-write family → decide one canonical write node
  (a `field`/`staticField` target + a `value`; stmt-vs-expr is a position, not a kind). [finalize in impl]
- `objMethod` (one `ToString` site) → `callInstance`.

KEPT distinct (NOT synonyms — different semantics): `staticField`≠`clrStaticField`,
`callInstance`≠`clrInstance`, `field`≠`clrPropGet`, `field`/`setField` (this-asm field) ≠ property accessors.

Companion vocabulary (#275) also stays phase-owned. kotc emits `companionValue` as a BIR-only semantic singleton
access and records `kotlinCompanion:{owner,name,visibility}` on the semantic companion declaration. bir2cir alone
chooses the ordinary nested CLR carrier, gives that CIR declaration `nestedIn` plus declaration-name
`capturedTypeParams`, and eliminates every `companionValue`. A CLR-static callable reference is the distinct
`newClrStaticDelegate` node after external binding; ilemit consumes that already-resolved physical decision. The
temporary `companionCaptureOwner`/`externalCompanionOwner` strings are declaration-association keys for lifted and
suspend captures, not Type slots, and must be consumed before CIR. The schema gate enforces those phase boundaries:
`companionValue`, `kotlinCompanion`, and both capture-owner keys are forbidden in CIR; `newClrStaticDelegate` and
`capturedTypeParams` are forbidden in BIR. A CIR `newClrStaticDelegate` must already carry the resolved
`memberRef` of the method it binds.

Kotlin property accessors follow the same phase ownership (#397). In BIR, each accessor declaration carries the
source `propertyName`, an explicit `propertyAccessor` role (`get` or `set`), and a file-local `propertyAssociation`;
the Property declaration carries the same association plus its `kotlinAccessors` role list. The association is needed
because properties may be overloaded by context or extension receiver while sharing a source name and accessor role;
it is limited to linking one Property record to its own accessors and is not a general declaration identity. kotc does
not author CLR MethodDef names or Property getter/setter links.
Property calls continue to use the bare Kotlin property name plus the existing `prop:get|set` role. bir2cir is the
only layer that projects those facts to physical MethodDef/MethodRef names and exact Property links (physical name,
method generic arity, and parameter signature). ilemit consumes that descriptor one-to-one. For this format version
the physical rule is `prop_get<name>` / `prop_set<name>`, but that spelling is one-way output: no pass may parse it to
recover Kotlin meaning. External CLR properties are read through Property/MethodSemantics metadata. The four BIR-only
identity fields (`propertyName`, `propertyAccessor`, `propertyAssociation`, `kotlinAccessors`) and `prop:get|set`
calls are forbidden in final CIR.

When that dedicated accessor implements an external CLR property, bir2cir records the exact external accessor as one
scalar member reference. An external base-class virtual carries the `requiresClrOverride:true` MethodImpl instruction
and its exact operand in the declaration's `clrOverrideRef`; external interface
slots are named in the owner-scoped `interfaceSlotRefs` table. A class accessor can itself be the MethodImpl body.
A default accessor declared on an interface requires the CLR explicit-interface shape instead: bir2cir emits a
private forwarding body with `clrInterfaceSlotBridge:true`, and ilemit maps that CIR instruction directly to a
`Private|Virtual|Final|NewSlot` MethodDef plus the stated MethodImpl. Neither path derives a slot from an accessor name.
When otherwise-identical generic slots differ by constraints, the MethodImpl descriptor also carries the exact
`typeParams` declarations; ilemit compares that resolved vector opaquely and does not reinterpret Kotlin bounds.

A declaration's `interfaces` array is likewise the COMPLETE physical interface set of the emitted TypeDef: ilemit
emits one InterfaceImpl row per entry, in the stated order, and adds no interface of its own to a declared type.
(The emitter still synthesizes one adapter TYPE with interfaces of its own — the reverse enumerator bridge — which
is a separate obligation to move into ordinary CIR declarations.) A face the CLR representation
of a Kotlin declaration owes but the source never named is bir2cir's to state. The standing case is the read-only
view of a mutable collection: Kotlin's `MutableList<E>` IS-A `List<E>`, but their lowered faces `IList<T>` and
`IReadOnlyList<T>` are unrelated CLR interfaces, so bir2cir adds the read-only sibling (`IList`→`IReadOnlyList`,
`ICollection`/`ISet`→`IReadOnlyCollection`) to every type that names the mutable one. The sibling obliges no
MethodImpl descriptor of its own: its members are the same names and signatures the mutable face already forced onto
the type, and the CLR binds an interface slot to a matching public virtual method implicitly, per interface. (ilemit's
own interface-slot wiring — the separate #400 obligation to delete it — does currently emit a redundant explicit
MethodImpl for such a slot; it names the same body the implicit binding would have selected.)

Fake-override resolution remains a frontend decision. kotc records a concrete selected declaration on an interface
fake override as `inheritedImplementation`, and records each default property accessor inherited by a class in
`inheritedDefaultAccessors` together with its class-frame Kotlin signature. These selected identities include the
target declaration's method `typeParams`, because equal name/arity/parameter/return shapes may still denote distinct
frontend declarations when their generic constraints differ. The ordinary-function twin is
`inheritedDefaultMethods`; it covers a DIM whose physical CLR name collides with an unrelated virtual class method. bir2cir consumes those facts when eliding
the non-declaration and, only if the CLR shape collides with an unrelated ordinary virtual method, allocating a class-level
MethodImpl bridge. It may resolve the stated Kotlin declaration to a CLR slot, but must not rediscover whether a
default exists or which declaration won by inspecting ancestor bodies, metadata abstractness, or physical names.
Interface declarations likewise carry their frontend modality explicitly as `abstract`. An empty `body` is valid for
a concrete Unit-returning default implementation, so neither bir2cir nor ilemit may use statement count as modality.

There is one external-assembly representability boundary that more identity does not remove. A foreign CLR interface
may provide the selected default only through a `private final` explicit MethodImpl body. If a consuming Kotlin class
also declares an ordinary virtual method at the implemented property's physical accessor signature, preserving the
frontend-selected default would require a new class-level MethodImpl bridge; that bridge cannot call the foreign private body
(`MethodAccessException`), cannot non-virtually call the abstract base declaration (invalid IL), and cannot call it
virtually without redispatching to the colliding class method. Supporting that source shape therefore requires either a
public producer-side trampoline or a dedicated compilation refusal. bir2cir performs that refusal when a colliding
ordinary MethodDef requires a bridge but the frontend-selected external implementation has no public concrete call
target. It must not be "fixed" by selecting a different default from names or hierarchy. DotKt-produced interfaces do not have this limitation because their public source
accessor remains callable while the private exact bridge supplies only the declaration-side MethodImpl identity.

`@kotlin.clr.ClrName("physicalName")` is the canonical source instruction for an independently allocatable function,
getter, or setter; `@kotlin.jvm.JvmName` is accepted as a compatibility alias. kotc carries the selected constant
string as BIR-only `explicitClrName`. Identical values coalesce; malformed arguments or different values on the same
declaration are errors. bir2cir applies that fact only after CLR type lowering and consumes it before CIR. An explicit
name always applies, even without a collision. Open/override families are rejected until the frontend can supply one
slot-wide naming decision rather than letting bir2cir infer propagation from hierarchy or names.
For an ordinary field-backed top-level property, an explicit accessor name makes that accessor a MethodDef-backed
surface even when its Kotlin body is default. A field-backed companion extension remains a bir2cir-owned physical
representation: kotc carries the getter/setter name and declaration identity as paired BIR field facts, and bir2cir
transfers them to the synthesized accessor MethodDefs. None of those hand-off facts survive in CIR.

The final MethodDef identity is declaring type, physical name, method generic arity, and lowered parameter vector.
Return type, nullability metadata, constraints, and declaration order do not distinguish a CLR overload. If two
independently authored declarations occupy one such identity, bir2cir rejects them unless their explicit names make
the identities distinct; it never appends a declaration hash. Explicit names that still collide are rejected too.
Generic-parameter scope and index remain part of the lowered parameter vector: `!0`, `!1`, and `!!0` are distinct
MethodDef signature elements even if a constructed owner later supplies equal concrete arguments for two slots.
Compiler-generated physical artifacts may use documented role-based unspeakable names and deterministic suffixes,
but collision suffixes do not contain the compiler product name and no consumer may reconstruct a missing fact from
an old `$dotkt$<hash>` spelling.

Whenever the physical name differs from the Kotlin source name, bir2cir records the exact source identity in the
trusted `KotlinSourceMethod` carrier on that MethodDef. An explicit MethodImpl bridge carries the same identity so
`dll2klib` can re-surface the implemented function under its Kotlin name. `ReferenceMetadataIndex` ignores that
compiler-generated bridge copy and uses the source declaration's carrier to map a frontend-selected declaration back
to its physical MethodDef. Neither consumer derives source identity from capitalization, a known method-name table,
or a method body.

A CLR Property signature has no method-generic parameter owner, so a method-generic extension accessor cannot be
attached to a valid Property row when its receiver or context mentions `!!T`. bir2cir omits that unrepresentable row.
Such an accessor carries the exact source name, role, and opaque association in the trusted
`KotlinPropertyAccessor` carrier. `dll2klib` reconstructs it from that carrier without inspecting the physical method
name or re-resolving an erased signature; representable properties otherwise use ordinary Property/MethodSemantics
metadata. A synthesized override bridge carries its own physical-property association plus an explicit
`sourceAssociation` back to the source accessor, and that source accessor carries its association as well. This
relation lets a downstream class consume the frontend-selected default through the bridge even when their CLR
signatures differ; no consumer compares those signatures or parses either opaque association.

Control flow: the structured `for*` family and the CFG `label`/`brIf`/`goto` while-family coexist
(mid-migration, audit D8) — the freeze picks the CFG form as canonical for lowered output; the structured
`for*` may remain as a kotc-emit sugar that bir2cir lowers. [finalize in impl]

### 2.1 Declaration modifiers — structured `mods` (replaces string-concat fragments, audit D7)
Today a decl's modifiers are **order-dependent string-concatenated fragments** (`$inlineFlag$kmods`, the
meta `final,inline,ext,suspend,infix,operator` comma string) — a stringly-typed set that drifts on ordering
and forces substring/`Contains` checks. FREEZE: every declaration (method/property/class/param) carries a
single **`mods` object**, a set of `name:true` flags (absent = not set). Order-free, self-describing,
additive (a new modifier = a new key). No comma strings, no `Contains`/`StartsWith` on a modifier blob.
```jsonc
"mods": { "inline":true, "infix":true, "operator":true }        // a fun; omitted keys = false
```
- Method/fun flags: `inline`, `infix`, `operator`, `tailrec`, `external`, `ext` (extension), `override`,
  `abstract`, `open`, `suspend` (also drives the `fn`-type `suspend` flag §1 — keep consistent), `data`-generated.
- Class flags: `data`, `sealed`, `inner`, `abstract`, `open`, `enum`, `fun` (fun-interface), `annotation`, `value`,
  `object` (singleton identity retained across a referenced DLL boundary).
- Property flags: `const`, `lateinit`, `override`, `open`, `ext`.
- Param flags: `noinline`, `crossinline`, `vararg`.
- **Visibility is NOT a mod** (it is an enum, not a boolean): a separate
  `"vis": "public"|"private"|"protected"|"internal"|"protectedInternal"`. `protectedInternal` is CIR-only:
  bir2cir authors it when lifted code needs the CLR `FamORAssem` accessibility; kotc never emits it.
- **Modifier semantics that drive lowering stay first-class** where a consumer keys on them (e.g. `suspend`
  already gates cold-lowering) — `mods.suspend` is the single source; a redundant top-level `suspend` field is removed.
  `suspend` is also the one modifier that is **BIR-only**: bir2cir consumes it (the cold lowering, then the
  `[KotlinFunction(Suspend)]` metadata stamp) and drops it, so no CIR declaration carries it and ilemit has no
  coroutine vocabulary to interpret.
Reference KLIB declarations preserve the corresponding standard Kotlin modifier flags.

(The full per-kind field table is enforced by the schema and validators; this section
lists only the freeze DECISIONS. The validator (§4) enforces the canonical set.)

### 2.5 Node-kind FORMAT stabilization — canonical field names + per-kind schema + validator
Node kinds are `{k}`-tagged objects but their FIELD names drifted (each wave's agent named fields ad-hoc).
Audit-confirmed drift: the "a type" concept is spelled `type`/`retType`/`ret`/`elem`/`of`/`keyType`
(`retType`≡`ret` are the SAME return type; `type`≡`of` overlap); a value/sub-expr is `value`/`val`/`init`/
**`e`/`l`/`r`** (cryptic single letters = expression/left/right); a list is `args`/`params`/`elems`; a name is
`name`/`member`/`method`/`field`. (`recv` is the good case — one spelling.)

Unlike `Type` (§1), node kinds CANNOT be collapsed to one shared model — there are ~95 distinct shapes. So
node-format stability is achieved DECLARATIVELY, in three parts:
0. **Casing convention** — every `k` value AND every field name is **lowerCamelCase**, uniformly. Audit
   (89 kotc `k` values): no snake_case/UpperCamel/dotted (good), but flattened abbreviations HIDE case
   boundaries inconsistently — `isinst`/`isinstRef` spell "instance" as `inst` while `callInstance` uses
   `Instance`. **APPLIED policy (m5 batch-1, landed): de-abbreviate** — `isinst`→`isInst`, `isinstRef`→`isInstRef`,
   `bin`→`binOp`, `un`→`unaryOp`. No short-operator exception is used; every `k` value is spelled-out lowerCamel.
   Single-word kinds (`for`/`if`/`block`) are already one-word lowerCamel (fine). The validator's canonical
   `k` set is the casing enforcer: any spelling not in the frozen set reddens the gate.
1. **Canonical field names** — one name per concept. **APPLIED (m5 batches 2-4, landed):** pure synonyms
   collapsed — `retType`→`ret`, `val`→`value` (map-entry value); cryptic left/right renamed `l`/`r`→`lhs`/`rhs`
   (on `binOp`/`objEq`). **`e` is KEPT and DOCUMENTED** as the canonical single-sub-expression / operand field —
   it is shared uniformly by `unaryOp`, `conv`, `cast`, `isInst`, `isInstRef`, `cond`/`if` (as a child), and the
   `nullable*` nodes, so it is one documented name per concept, not a cryptic drift (the schema's per-kind shapes
   use `e` accordingly; `conv` carries its target type as `to`, the `nullable*` element as `elem`). Keep genuinely
   role-distinct fields distinct (a call's `args` ≠ a decl's `params` ≠ an array's `elems`; `ret`≠`elem`≠`keyType`
   when the roles differ). Every type-valued field holds a `Type` node (§1).
   - **Return-position type keys** (the `ReturnKeys` set — a return-slot type where `kotlin.Unit` lowers to `void`)
     = **`{ret, dynRet, suspendRet}`**, a consistent `<context>Ret` family: `ret` (plain return), `dynRet`
     (`@Clr` dynamic-dispatch return), `suspendRet` (a suspend fn/lambda's `T` of `Continuation<T>` — renamed from
     the odd-one-out `resultType` in m5). These are DISTINCT ROLES that can COEXIST on one node (a `callInstance`
     carries `ret`+`dynRet`; a `newSuspendLambda` carries `ret`+`suspendRet`) — grouped by shared position, NOT
     synonyms; the return-position parallel to the value-position `TypeKeys`. Dead keys `selRet`/`returnType`
     (0 emit, never read) were deleted in m5.
2. **Per-kind schema** — the spec pins each `k`'s exact field set (name, required/optional, value shape),
   generated from the audit. This is the normative node shape.
3. **Schema validator (§4/§5)** — validates every node against its kind's schema: unknown `k`, unknown/missing
   field, or a wrong value shape reddens a gate. **This is the ENFORCER** — because there is no single shared
   node model, the validator is node-format's ONLY structural safety net (contrast `Type`, which is drift-proof
   by construction). Therefore the validator is NOT deferred to last; it lands early enough to guard the flip.

### 2.2 `sig` — resolved declaration signature is a `Type[]`
A call node carries `sig` so its consumer links the already-resolved overload by name and declaration signature.
It is a **JSON array of `Type` nodes** (§1) — `"sig":[T, T, …]` (extension receiver first, then value params).
Generic params use positional `tv` (§1), including their distinct type/method scopes.

In BIR this descriptor remains in Kotlin vocabulary. For a call into a referenced assembly, bir2cir reads the
referenced declaration, applies the selected runtime actual/type-alias representation, and writes its physical
declaration shape to CIR. Compiler-generated alias helpers also remap the aliased class and member type variables
onto the helper method's flattened generic-parameter space. ilemit consumes a present CIR `sig` exactly: zero or
multiple structural matches are ABI errors, never a request to retry by name and arity.

### 2.2.2 `memberRef` — ONE resolved external-member identity (CIR-only, #370)
A reference to a member of ANOTHER assembly is a single scalar object, not a set of adjacent fields:

```jsonc
"memberRef": {
  "kind": "method|ctor|field|propertyAccessor|eventAccessor",
  "assembly": "System.Runtime",              // PHYSICAL defining-assembly simple name
  "declaringType": { "t": "fqn", "name": "System.Collections.Generic.List`1", "args": [T…] },   // metadata FullName VERBATIM
  "name": "Add",                             // exact metadata name (".ctor", "get_X", "add_X", …)
  "genericArity": 0,
  "callingConvention": "static|instance|varargStatic|varargInstance",   // absent for a field
  "parameterTypes": [T…],                    // OPEN declared params; absent for a field
  "returnType": T                            // OPEN declared return; the field TYPE when kind is `field`
}
```

The shape follows from one rule: **bir2cir resolves a Kotlin operation to exactly one declaration, and CIR
serializes that answer.** Split the answer across a parameter vector, an owner name and a few flags and the
consumer has to re-combine them — and re-combining candidates *is* member selection, which belongs upstream.
So a consumer performs an EXACT lookup (resolve `declaringType` in `assembly`, then take the one
`DeclaredOnly` member whose signature equals this one) and encodes it. Zero matches is a CIR/target mismatch
and MUST name the complete reference; more than one is a schema/identity defect. Neither is a cue to fall
back on name, arity, applicability, assignability, most-derived rules, host reflection or declaration order.

Field-by-field, each entry is there because its absence would force a search: without `assembly` the
consumer scans every loaded assembly for a name; without `returnType` it cannot separate an inherited slot
from a redeclaration that shadows it by return type alone; without `callingConvention` a static and an
instance member of the same shape are one candidate; without `genericArity` the arity has to be guessed from
a call site's type-argument count. `declaringType` names the DECLARER — a member the receiver merely
inherits is anchored on the type that declares it — spelled as the target's own metadata FullName (arity
backtick and `+` nesting verbatim, since that is the key the target universe is indexed by), and carries the
use-site instantiation in `args`, so no base walk or owner projection is needed downstream.

The signature is the **OPEN declared** one (ECMA II.9.8: a MemberRef carries the uninstantiated signature).
Method instantiation rides on the node's own `typeArgs` and owner instantiation on `declaringType.args`; a
substituted vector would collapse distinct overloads (`M(!0)` and `M(!1)` become one). `assembly` is the
simple name only: a reference-pack assembly and its implementation legitimately differ in version/culture/PKT,
and it is the PHYSICAL identity — where a reference twin and its runtime twin differ in name, this already
names the runtime one. A vararg signature is a DIFFERENT member from its fixed-arity neighbour, so the
convention states it rather than the producer refusing to describe it; whether such a signature can be
emitted is a question for the emitter, asked where it can be answered.

**WHAT THE TARGET DECLARES IS SPELLED AS THE TARGET SPELLS IT.** The declaring head, every parameter, the
return and every custom modifier are read off the target declaration, so they carry its **verbatim metadata
FullName**: arity backtick and `+` nesting kept, delegates not rewritten as `fn`, `System.Nullable` not
collapsed to a nullability wrapper.

`declaringType.args` are the exception, and deliberately: they are not something the target declares, they
are the **use site's** instantiation of it — the caller's own type arguments, projected along the declaration
edge. Those are ordinary document type nodes in the document's vocabulary, resolved by a consumer exactly as
every other type slot is. Reading a type slot is not member selection; the rule this whole section exists to
protect is that nobody chooses a *member*, and choosing an instantiation the document already states is not
that. The document's ordinary spelling (§1) is the right vocabulary for talking
about a Kotlin program and the wrong one for an identity, because it merges types a reference must keep
apart — stripping at the first backtick turns a type nested in a generic outer into its outer alone, and
`Ns.Outer+Inner` and `Ns.Outer.Inner` become one string. The single exception is the void return, which names
a type that appears nowhere else in a signature and so makes no member ambiguous.

**ONE SPELLING PER SHAPE.** A consumer compares these references exactly, so two ways to write one physical
member would be two members to it. The rules, all validator-enforced:

- a non-generic declarer **omits** `args`; it never carries an empty list, and the length of `args` equals
  the arity the name itself states;
- `declaringType.args` is **flattened** over the enclosing-type chain, matching `tv{scope:"type"}` (§1) —
  `Outer\`1+Inner\`1` carries the outer argument first;
- a signature carries no `oblivious` and no `star`: those are Kotlin type-system facts, and a physical CLR
  signature has neither. `nullable` appears only as the `System.Nullable\`1` value-type collapse;
- `.ctor` names a constructor and nothing else;
- the CIR-only carriers `ptr`, `mod` and `array.rank` appear **only** inside a `memberRef`. Ordinary type
  slots are rewritten by lowering passes that reconstruct an array as a vector and know nothing of pointers
  or modifiers, so one outside a reference would be silently flattened — re-creating exactly the collisions
  those carriers exist to prevent.

Same-emission-unit members are NOT memberRefs: they have no assembly identity yet (their MethodDefs are being
built by this compilation) and stay on the internal linkage (`localCtorIndex`, the emitted type's own signature
table). The presence of a `memberRef` is therefore itself the external-vs-emitted discriminator.

For a BIR `new`, `memberSignature` is the frontend-selected OPEN constructor declaration vector (including any
compiler-authored enclosing/capture slots), while `argTypes` is the substituted use-site vector. `bir2cir` consumes
the former to select one same-unit constructor, closes that declaration in the constructed owner's final physical
frame, materializes any required representation conversion, writes the resulting use-site `argTypes`, and replaces
the declaration fact with scalar `localCtorIndex`. Neither `memberSignature` nor a constructor candidate set reaches
CIR.

A carrier key holds ONE reference, never a list. A candidate set reaching a consumer is precisely the failure
this shape removes: whoever received it would have to choose, and choosing is the producer's decision.

The carriers are `memberRef` on a node, and on a DECLARATION `baseCtorRef` (a constructor's delegation to an
external base) and `clrOverrideRef` (the external base virtual a method overrides — the MethodImpl target).
An external-base override also carries `requiresClrOverride:true`, a durable emission instruction independent of
the operand: the schema requires the instruction and reference together, while the member identity remains scalar.
Each carrier is the sole external-member identity for its operation. Consumers read that current scalar reference
directly and do not recognize earlier owner/name/signature layouts.

### 2.2.1 The TWO intentional string islands (documented KEEP — not producer-zero)
The BIR/CIR **wire format** carries no stringly-typed compound type token (§1): every `type`/`ret`/`elem`/
`funcType`/`base`/`interfaces`/`sig` slot is a structured `Type` node or an array of them. But TWO
consumer-internal string forms are DELIBERATELY retained (rendering a structured `Type`→string for a
narrow, entangled, low-payoff comparison); they are NOT drift and MUST NOT be "cleaned up" by re-stringing
the format:

1. **The owner-FQN island** — ilemit `ParseOwner`/`ParseOwnerSlot`/`TryMapEmittedType` key this
   assembly's emitted types (`_types`) by their bare FQN **string** and split a constructed-generic
   `Name[arg,…]` owner spec into (open name, args). `ParseOwnerSlot(JsonElement)` reads a structured `fqn`
   owner node (`ParseOwnerT`) — the wire stays structured — but the internal `_types` lookup and the
   `Name[…]` split remain string-keyed. This is a private in-assembly type-table index, not a serialization
   token.
2. **The sig-key reflection island** — ilemit `SigTokenOf`/`SigTokenMatches`/`SigTokenMatchesOpen`
   (and bir2cir `ParamKey`) RENDER a structured `Type` (incl. `fn`→`func:`/`sfunc:`, `clr:`/`clrg:`/`array:`/
   `nullable:`/`byRef:`/`gp:` prefixes) to a canonical **string token** SOLELY to compare a call/binding
   signature against a **reflected `MethodInfo`** from a `--ref` .NET assembly (`FindReflectedMethodBySig`).
   Reflection surfaces `System.Type`, not our nodes, so the comparison canonicalizes both sides. These tokens
   remain private comparison keys: no BIR/CIR reader parses them back into a type.

Also NOTE — the bare-FQN + CLR-shorthand string LEAF resolver inside ilemit
(`MapType(string)`'s `_ =>` FQN/shorthand switch + `TryMapEmittedType`) is the primary resolver for every
structured `fqn` node's bare `name` (reached via `MapType(fqn.Name)`). It is an internal identity resolver,
not a serialized type-token reader.

### 2.3 `@ClrProperty(access:Int)` bitmask → structured flags (no encoded int)
The stdlib `@ClrProperty` accessor binding encodes read/write as an **int bitmask** (`READ=1`/`WRITE=2`,
`bir2cir Program.cs:580` `out int access`). A "mysterious int" — replace with explicit booleans
`{"read":true,"write":true}` (or two attr fields). Same principle for any binding-annotation argument that
packs structure into an int/string. (`@ClrIntrinsic("System.String.Format")` stays a string — it names one
BCL method, not an encoded structure — but its target should resolve through the shared naming, not ad-hoc.)

### 2.4 Synthetic type/name generation — collision-free + documented (no lossy regex-mangle)
Synthetic CLR type names are built by **lossy regex-mangle** of a type FQN
(`"<>dotkt_ClrH_" + Regex.Replace(fqn, "[^A-Za-z0-9]", "_")` and peers). The abbreviation zoo — `ClrH`
(CLR helper), `K*` (`KIterator`/`KIterable`/`KProperty` Kotlin synthetics), `RW`/`RO` (read-write/read-only
property), `Ref` (ref cell), `tryval` (try-expr temp), `obj` (object-expr) — encodes a TYPE into a NAME
lossily: `kotlin.Char` and a hypothetical `kotlin$Char` collide (both → `kotlin_Char`), and it is not
reversible. FREEZE: derive synthetic names through a **single registry** that assigns a stable, collision-free
unique name per DISTINCT structured `Type` (dedup by the `Type` node, not by the mangled string), with the
prefix set (`ClrH`/`KIterator`/…) DOCUMENTED here as an enum. Name-mangling is not a serialization DSL, but a
lossy type→string encoding is the same durable-ABI smell (structure hidden in a string) and a real collision bug.

### 2.6 Naming convention — ONE rule for the whole format vocabulary
Every identifier in the serialization vocabulary is **lowerCamelCase**, uniformly: node `k` values, type
`t` values (§1), ALL field names (§2.5.1), `mods` keys (§2.1), `vis` enum values, and carrier field names. Rules:
- **Multi-word → camelCase boundaries; never case-hiding-flat.** `byref`→`byRef`, `isinst`→`isInst`,
  `staticfieldset`→`staticFieldSet`. A boundary between words is always a case change.
- **No cryptic single letters / silent truncations** where they hide meaning: `l`/`r`→`lhs`/`rhs` (applied);
  `e` is KEPT as the DOCUMENTED single-operand field (§2.5 part 1); `bin`/`un`→`binOp`/`unaryOp` (applied,
  de-abbreviated — no short-operator exception).
- **Accepted acronym tags** (documented, treated as a single lowercase unit — like the top-level `k`/`t`
  keys themselves): `fqn`, `tv`, `fn`. These are universal 2-3 letter type-tag units, NOT case-hiding
  multiword flattenings; they stay lowercase. (If one ever appears mid-identifier it becomes `Fqn`/`Tv`/`Fn`.)
- **Documented EXCEPTIONS (different domain, intentionally not lowerCamel):**
  - **.NET attribute TYPE names** are UpperCamel — `KotlinInlineAttribute`, `KotlinSuspendFunctionTypeAttribute`,
    `KotlinDefaultAttribute` — because they are CLR types and MUST follow the CLR/BCL convention. Their
    *constructor-arg / field* names still follow the lowerCamel rule.
  - **carrier version tags** are kebab-with-slash — `"bir-json/1"`, `"bir-msgpack/1"` — a codec+schema version
    identifier, not a vocabulary identifier; the `/` separates codec from schema-major.
- **SCOPE — vocabulary, NOT payload data.** This policy governs the format's OWN identifiers (the
  meta-language: `k`/`t` tags, field names, `mods` keys, decl kinds). It does **NOT** govern the DATA those
  fields carry — the Kotlin/CLR **symbol names** in a `name` value (`{"t":"fqn","name":"…"}`), which follow
  their SOURCE language's conventions and are copied verbatim. Concretely: the BIR type TAG for a by-ref is
  `byRef` (our vocabulary → lowerCamel), but the user-facing Kotlin by-ref SYMBOL is `kotlin.clr.byref`
  (lowercase BY DESIGN — a Kotlin API name, carried as a `name` value, untouched by this policy). Same for
  `kotlin.Int`, `System.String`, a user's `myFun` — `name` values are object-language data, never re-cased.
- **The validator enforces the canonical sets** (§4/§5): any `k`/`t`/field/mod/decl-kind spelling not in the
  frozen spec — wrong casing, an undocumented abbreviation, a synonym — reddens the gate. There is no
  case-insensitive fallback and no alias table; the spelling in this spec is THE spelling. (The validator
  checks VOCABULARY spelling, never a `name`-value's payload.)

### 2.7 Call-evaluation plan — `callEval` / `bindRef` / `delegationBindings` (BIR-only)

**Why it exists.** A Kotlin call evaluates its receiver, then each supplied argument, then the callee's omitted
defaults — each exactly once, however many emitted positions read it. On the CLR the readers are not one position: a
same-module default splices an earlier value, a reconstructed cross-module data-class `copy` field reads the receiver,
a `[kotlin.clr.KotlinDefault]` carrier binds `{defaultArgReceiver kind}` / `{defaultArgParam n}` to the call's own
values. While a call
was represented TWICE — as expressions substituted into those readers, and as independently hoisted `var`s sorted
ahead of the call — an illegal storage answer forced one of the two to be abandoned, and **no point in that design
satisfies all three of single evaluation, Kotlin order and legal storage at once**. That is a property of the
representation, not of any gate: three successive attempts each fixed one invariant by breaking another (evidence:
PR #270's three iterations and the read-only enumeration at commit `cb4ff8d`, reachable via `refs/pull/270/head`).
The plan replaces the two representations with one.

**Shape.**

```json
{"k":"callEval","type":<Type>,"bindings":[<binding>…],"expr":<the call node>}
{"k":"bindRef","id":"dotkt$b3","sty":<Type>}
```

A binding is a plain object (not a `{k}` node):

| field | meaning |
|---|---|
| `id` | the binding's name, and the `bindRef` key. NOT the name of the local it may lower to — see *Id namespaces* below |
| `phase` | `recv` \| `arg` \| `default` — where the value comes from (documentation and diagnostics; the array order already carries the evaluation order) |
| `kind` | `value` \| `address`. An `address` is a byref / `@ClrRefArgument` slot's addressable lvalue: an ordering marker, never storage |
| `stable` | may this value be READ more than once (a literal, an immutable local/parameter read)? Judged ONCE by the producer and consumed downstream, never re-derived |
| `type` | the CALLER-instantiated semantic type of the value |
| `role` | the source-level phrase a storage refusal names the value by (`receiver of 'copy'`); travels onto the lowered `var` |
| `expr` | the value. For a cross-module omitted default this is the `{"k":"defaultArg"}` RESERVATION `DefaultArgSplice` fills |

**Order rules.** `bindings` is in Kotlin evaluation order, and that order IS the array order — there is no sort key:

1. the dispatch receiver, then the extension receiver;
2. the supplied arguments, in the declaration's positional sequence (contexts then regulars, §2.2);
3. the filled defaults, in the callee's declaration order.

Every reader of a bound value is a `bindRef`. A `bindRef` is a pure READ and may be cloned freely — cloning a read is
not cloning an evaluation, which is the whole point.

**Granularity.** A plan is emitted only where a value can acquire a SECOND reader: a same-module default that reads
one of the call's values, a cross-module omission (a `defaultArg` reservation, or a data-class `copy` field
reconstructed from the receiver), or a `callInline` (below). Without one the positional argument array IS the
evaluation plan — one reader per value, positional order. The standing invariant is the converse: **any transform that
gives a call value a second consumer must go through a plan.** A `defaultArg` outside one is refused loudly by
`DefaultArgSplice`.

**`callInline`.** A spliced inline call's body becomes the caller's, and it may read a parameter any number of times,
in a loop, inside a closure, or not at all — so every value the call SUPPLIES has a second reader by construction, and
a `callInline` binds every one of them. The general rule still decides whether a plan exists at all: a call that
supplies NO value binds nothing and emits no plan — the lambda-only inline call, `run { … }` or a user
`inline fun go(block: () -> Unit)` invoked as `go { }`, where the only argument is the body being spliced. The bound
values are:

- the DISPATCH receiver, then the EXTENSION receiver, then the supplied arguments in positional order — the same order
  rule as every other call. `recvs.dispatch` / `recvs.extension` / `args[i]` hold the `bindRef`s.
- a SPLICED LAMBDA is not a value and is not bound: a literal carrier (`inlineLambda`) and a by-name forward of the
  enclosing inline fn's own lambda parameter are the body `InlineSplice` splices, matched by the name it travels under.
  A `noinline` lambda IS a value (a real delegate) and is bound like any other argument.
- an OMITTED default is `null` in its slot. It is NOT a call-site value: Kotlin evaluates a default in the CALLEE's
  scope, after every supplied value, so `InlineSplice` fills it from the callee's own carrier and binds it to a local
  of the spliced block — which is exactly that position, because the call site's bindings are materialised ahead of
  the block. That local is always TYPED, from the callee's parameter closed against the call site; an untyped one
  would reach the suspend lowering as a `kotlin.Any` slot, so it is refused at the splice instead.

`InlineSplice` consumes the bindings rather than minting a local per parameter: it substitutes each `bindRef` into the
payload body, and `CallEvalLowering` decides the physical form once, like any other plan. Two positions can only name
a SLOT — a closure/state-machine capture DESCRIPTOR and an assignment target — so a value left as a `bindRef` is
pinned into a named local there, which is a pure read of the binding and not a second evaluation.

The passes that run BETWEEN the splice and `CallEvalLowering` therefore see a `callEval` where they expect a call.
The ones that ask *what does this expression produce* peel it exactly as they peel a `valueBlock` — the bindings are
statements evaluated ahead of the call, so the value is the wrapped call's (`StaticTypeResolver`,
`bir-common/NodeType.cs`, the splice's own covariant-construction widening). The ones that MOVE statements cannot: a
binding is not a statement until this pass makes it one, so a splice that wants a spliced block flattened into its own
statements has to leave a plan alone. `CallEvalLowering` therefore folds what it created — a `valueBlock` whose
`result` is a `valueBlock` becomes one block, in place, evaluation order untouched — which is what restores the single
layer downstream expects.

**Reading a plan's bindings.** A binding is inlined back into its reader only when that reader sits on the node's
EAGER SPINE — the chain of operand positions evaluated once, in order, unconditionally, when the node itself is
evaluated. A read reached through a statement list, a conditionally-taken branch, a loop body or a closure is
evaluated at a different time, a different number of times, or not at all, so the binding is MATERIALISED instead.
`CallEvalLowering` lists the eager kinds rather than the lazy ones, so an unfamiliar kind costs one local rather than a
reordered evaluation. A `stable` binding is exempt: re-reading an immutable value observes neither a side effect nor a
different value, wherever the read is.

**Nesting.** Plans nest — a default that is itself a call with defaults, an inline splice's own bindings wrapped
around a block that reads its caller's. `CallEvalLowering` walks POST-ORDER, so the inner plan lowers first and an id
it does not know is left alone: it belongs to a plan further out, whose lowering substitutes it. The rule that makes
that sound is that **every `bindRef` resolves OUTWARD to an enclosing plan's binding, declared before the reading
position** — a binding's own `expr` sees only the bindings ahead of it in its plan, plus everything in scope outside.
`scripts/verify-schema.py` checks it structurally on BIR; a `bindRef` naming nothing would otherwise survive to the
pass's terminal chokepoint with no way back to the producer that emitted it.

**Id namespaces.** kotc mints `dotkt$bN`; bir2cir mints `cir$bN`, both from their own counters, and `$` is not
writable in a plain Kotlin identifier, so neither can alias a user name or the other's. An id is unique only within
its PRODUCER, which matters at the two points where a plan is CLONED into another document — an inline
`[KotlinInline]` body spliced at a call site, a `@KotlinDefault` carrier materialised into a reserved binding. Both
re-mint the ids they carry (`CallEvalLowering.FreshenPlanIds`), so a plan spliced twice into one frame, or a
consumer's own `bindRef` substituted into a producer's carrier, cannot collide. For the same reason a binding id is
NOT the name of the local it lowers to: `CallEvalLowering` mints that name, in the frame that will hold it.

**Declaration-position call sites.** A constructor delegation (`: this(…)` / `: super(…)`, including a per-entry enum
body's base call) has its arguments on the constructor DECLARATION, with no wrapping expression. Its plan rides the
declaration as `delegationBindings`, an array of the same bindings; `thisArgs`/`baseArgs` read them. An enum entry's
`NAME(args)` needs nothing special — a static field initializer is an expression position.

**Lowering contract.** bir2cir's `CallEvalLowering` runs immediately after `DefaultArgSplice` — i.e. once every splice
that can add a reader has finished — and is the ONLY consumer of the vocabulary:

- a binding with exactly ONE reader is inlined back into that reader (the emitted CIR is what it would have been with
  no plan at all) — unless doing so would REORDER it: an inlined binding is evaluated at its reader's position, and
  the plan's order is not the argument array's, so a binding read later in the node than a binding that follows it in
  the plan is materialised instead;
- a binding with SEVERAL readers becomes a `var`, evaluated once and loaded per reader; a `stable` binding is inlined
  at every reader instead;
- a binding NOTHING reads is still evaluated as a `var` — Kotlin evaluates every value a call supplies — unless
  evaluating it is unobservable, in which case it is dropped;
- **order is never traded.** The invariant, stated once: *the emitted pre-call statement sequence is ordered by plan
  position, and a binding that emits ANY pre-call statement forces every earlier non-stable binding to emit one too.*
  Every binding is handled at its own position in ONE stream, so this holds whatever mix of kinds a plan carries — it
  is about pre-call WORK, not about which bindings happen to become `var`s;
- an `address` binding never becomes a `var` — no storage holds a managed pointer — and splits by what its location's
  ROOT is. An lvalue FORMER (`local`/`field`/`arrayGet`/…) designates storage without evaluating anything itself, so
  only the impure VALUES it is computed from move, in the location's own operand order, leaving a pure location in the
  slot: `byref(mk().f)` pins `mk()`, `byref(a[i()])` pins `i()`, `byref(x)` pins nothing. Any other root IS an
  evaluation, so the whole location moves to the binding's position — into a `ref T` local (`byrefOf`) when the
  location's own DECLARED type is a byref, else into a plain `T` local whose ADDRESS the slot takes, which is what
  taking the address of an rvalue means. The decision is by declared type, never by node shape: storing a `T` into a
  `T&` slot is unverifiable IL, and the frontend accepts `byref(<rvalue call>)`. Every pinned local is TYPED — an
  untyped local is unverifiable IL, so a node the shared deriver (`bir-common/NodeType.cs`) cannot type is a hole in
  the deriver and says so, never a `kotlin.Any` fallback;
- a delegation's plan becomes the constructor's `preStmts`, which ilemit emits ahead of the `this`/`base` call.

It decides NOTHING about storage. A `var` here is a request for a scoped local; whether a coroutine state machine may
keep it in the frame or must promote it to an instance field — and whether the CLR admits the type at all — is
`SuspendColdLowering`'s single decision, from liveness, ~300 passes later (`docs/dotkt-semantics.md` §4d/§7).

**A SECOND producer, for a second reason: suspension order.** kotc emits a plan where a value can acquire a second
READER. bir2cir's suspend lowering emits one where a value must be evaluated on a particular SIDE of a suspension, which
is a different question with the same answer. Its stage 0 (`SuspendOperandPlan.cs`, inside `SuspendColdLowering.ApplyAll`
and therefore ~300 passes after `CallEvalLowering`) wraps a node whose operands contain a suspension in a plan binding
every operand in array order, and lowers it on the spot through the same `Materialise`, supplying the ordering itself:
every operand LEFT of the last suspension-bearing one is forced to a `var`, and so is that operand when the node is
itself a suspend call — which is what lifts a nested suspension out of a suspending call's own argument list, where the
state machine would otherwise write the outer resume label and let the inner suspension overwrite it. Operands to the
RIGHT are left in their slots, because Kotlin evaluates them after the resume; forcing one would be the very reorder the
plan prevents. So the plan's `force` input, when present, IS the ordering answer and the two general order rules above
are skipped — they would only re-derive it, or contradict it.

**WHICH nodes, exactly — this is coverage, not a universal rule.** Stage 0 acts on the kinds its operand descriptor
(`EvalOrderOf`) names: `binOp`, `concat`, and the call/new set (`callStatic`/`callInstance`, the four `clr*` call forms,
`new`/`newClr`). Every other multi-operand kind is NOT normalized, so a suspension in a later operand of one still
reorders an earlier operand — `arrayGet`/`arraySet`, `setField`/`clrPropSet`, `delegateInvoke`, `objMethod`,
`constrainedCall`, `dynCall`, `newArray*`, the collection literals, the event add/remove forms. `makeArray()[susp()]` is
the shortest example. That gap is PRE-EXISTING (the retired eval-order rewrite named the same call/new set) and is
recorded here rather than in a comment because a later change must not assume otherwise: **nothing downstream may treat
"a descriptor-bearing node" and "any node" as the same set** — in particular the storage/liveness analysis stays
conservative about operands generally (`SuspendLiveness.ReReadOperands` re-reads every operand it finds) precisely
because the un-normalized kinds are still out there. Stage 0's own chokepoint shares `EvalOrderOf`, so it cannot see
them either; widening the descriptor is a behavioral change with its own gate.

Nothing else changes: the bindings are ordinary `cir$b…` bindings in bir2cir's own id namespace, `stable` is the
same Q1 answer (`ValueStability.IsReReadable`), and the vocabulary still does not survive its own lowering. A stage-0
plan that materialises nothing rewrites nothing — a node with no suspension among its operands is left byte-identical,
which is what keeps suspension-free code out of the diff.

**A THIRD producer, for a third reason: a parameter the CLR mapping maps AWAY.** `MemberCallSubstitution` lowers a
Kotlin constructor onto a BCL one that has no slot for one of its arguments — `HashSet(initialCapacity, loadFactor)`
onto the capacity-only overload, because the CLR has no load-factor concept. Kotlin evaluated that argument, so the
question "what does a value with no reader cost" is exactly the one the plan already answers: the mapping binds every
ORIGINAL argument in Kotlin order, gives the KEPT ones a `bindRef` in their slot and the mapped-away ones no reader at
all, and lowers it on the spot through the same `Materialise` (no `force`, so both general order rules apply — the
prefix rule is what stops a kept value sliding behind a mapped-away one). Asking Q2 a second time at that site instead
is what the single-classifier rule forbids. The result is a `valueBlock`, or the bare call when nothing materialises —
which is every construction whose mapped-away argument is a literal, so the `HashSet(16, 0.75f)` idiom is unchanged.

Because this producer runs ~240 passes after `CallEvalLowering.Apply`, the block it mints is NOT folded by
`MergeNestedResult` and may nest inside another block's `result`. Consumers recurse, so nesting is tolerated rather
than normalized here; what is NOT tolerated is a `try` inside such a block reaching an operand slot, which
`TryValueOperandHoist` hoists (it searches a block's inline statements for one, not just its top level).

**Phase.** `callEval`, `bindRef` and `delegationBindings` are BIR-only; `preStmts` is CIR-only. `CallEvalLowering`
asserts the split at its own exit, and `scripts/verify-schema.py` enforces it structurally on both documents. Stage 0's
plans are made and lowered within one pass, so they too never appear in a serialized document.

**`defaultCarrier.lifted` is unchanged.** A carrier's lifted method declarations remain a RAW TOKEN payload parsed out
of the `[kotlin.clr.KotlinDefault]` attribute string, not plan vocabulary: they are declarations re-hoisted into the
consuming file class, and nothing about them is a call-site value. Only the carrier's `expr` is token-substituted, and
its `{defaultArgReceiver kind}` / `{defaultArgParam n}` tokens resolve to the call's `bindRef`s. Receiver `kind`
distinguishes `dispatch`, `extension` and an inner constructor's `enclosing` instance; an ordinary `{this}` nested
inside a closure/SAM/suspend-lambda is that synthesized frame's own receiver and is not a carrier token.

## 3. Labels & naming (conventions consumed as opaque strings)
- SM / coroutine method names: `<name>$dotkt_suspend` (cold entry), `<name>$sm` (state machine class) —
  chosen by bir2cir, opaque to ilemit. Resume labels: integer CFG `id`s (ilemit consumes only `label`/
  `goto`/`brIf` with int ids; no textual resume-label vocabulary).
- Synthetic types `<>dotkt_*`; capture fields `__outer`/`$this`/`__self`; temp vars via a fresh counter.

## 4. Shared helper API (single-source — the anti-drift linchpin)

ONE type read/write per language, used by EVERY site. No other code parses/builds a `Type`.

**Kotlin (kotc)** — `kotc.bir.TypeNode` (sealed) + `TypeNode.toJson()`.
`birType(IrType): TypeNode` produces the node; nothing emits a type string.

**C# (bir2cir / ilemit / dll2klib)** — a shared `DotKt.Bir.TypeNode` record hierarchy (Fqn/Tv/Fn/Nullable/
Array/Byref) + `TypeNode Read(JsonElement)` / `JsonNode Write(TypeNode)`, in ONE shared file referenced by
all three C# tools. Its `Fn.Clr` member is the sole phase extension: absent in kotc BIR, required in
ilemit-facing CIR (§1). Serialized types are read only through `TypeNode`; internal CLR identity lookup and
signature-key canonicalization consume the resulting nodes rather than parsing wire-format strings.

## 5b. Reference declarations (dll2klib → kotc)

CLR reference declarations are standard packed KLIB metadata. `dll2klib` projects one reference assembly to
one KLIB; kotc resolves those declarations through the ordinary KLIB symbol provider. Physical CLR ownership
that must survive frontend resolution is carried by the projected `kotlin.clr.ClrExternal` annotation and
forwarded into BIR without reinterpretation.

The fixed `kotlin.clr` intrinsic vocabulary is declared in the CLR stdlib and loaded from its frontend KLIB.

## 5. Validator (§7 of the plan)
Validate live BIR/CIR + every emitted `[KotlinInline]` body against this spec: unknown `k`, a type that is
not a valid `Type` node, or an unknown `version` reddens a gate. Round-trip: decode every stdlib ref.dll
inline body, assert it re-encodes identically.

## 7. The validator — LANDED (#37 m6, the freeze ENFORCER)
`scripts/verify-schema.py` (corpus runner `tests/ir/run-schema.sh`, Makefile target `verify-schema`) is the
structural enforcer for this contract. Because `Type` is drift-proof by construction but node FIELD names are
NOT (§2.5 — there is no single shared node model), the validator is node-format's ONLY safety net: it walks the
freshly-emitted BIR + CIR and reddens the gate on any drift.

**What it checks**
- **Types are nodes (§1) — the core invariant.** Enforced by an INVERSE allow-list: the finite set of keys that
  MAY carry a bare string (`STR_OK` — the format vocabulary `k`/`t`, object-language `name` payloads, enum
  entry/underlying/physical-value payloads, the enum keys `scope`/`op`/`vis`/`variance`/`kind`, and the documented
  owner/member NAME islands §2.2.1 plus an external attribute's exact `attrAssembly` metadata scope) is
  fixed; a bare string at ANY other key is a type-token leak and reds. Array string elements red too, except the
  `typeParams`/`capturedTypeParams` name-declaration shorthand (`STRARR_OK`). The
  `kotlinCompanion.owner`/`.visibility` exception is path-scoped to that declaration fact; it does not re-admit a
  bare `owner` type slot elsewhere. This fails closed across the whole tree — a future
  string type token anywhere reddens without the validator having to enumerate every type slot.
- **Canonical node kinds + type tags (§2.5/§2.6).** Every `{k}` must be in the frozen `KINDS` set (the union of
  every kind the current toolchain emits across a full fresh build — regenerate with `--dump-kinds`); every emitted
  BIR/CIR `{t}` is in `{fqn,tv,star,fn,nullable,oblivious,array,byRef}`. `star` is valid only in BIR and must be
  eliminated by bir2cir before CIR. A typo, a retired spelling (`bin`/`un`/
  `isinst`/`isinstRef`/
  `setFieldExpr`/`staticFieldSet`), or an ad-hoc new kind reds. Casing is enforced by set membership.
- **Well-formed types (§1):** each `{t}` carries its required fields with the right value shapes; a `{k}`+`{t}`
  mixed object (roles are disjoint) reds.
- **`mods` keys ⊆ the frozen set, `vis` ∈ the enum (§2.1).**

**Coverage** = freshly emitted stdlib and application BIR/CIR. `tests/ir/run-schema.sh` drives the structural
validator, and `make verify` includes the schema and sanity gates.

**Carrier (§0) scope.** The `[KotlinInline]`/`[KotlinSuspendFunctionType]` carriers ride as CLR attributes on the
emitted assembly, not as document nodes, so they are out of the document walk. Their version is guarded LOUDLY at
decode time by `bir-common` `BirCarrier.DecodeBody` (an unknown version throws `NotSupportedException` — never a
silent mis-decode) and is exercised end-to-end by the roundtrip tests (dll2klib decodes projected DotKt
assembly carriers through the one codec). The decoded carrier BODY is itself a node/type that ALSO appears inline as the
emitting method's body in the BIR/CIR — validated there by the document walk.

**Residual string type slots structuralized to land the enforcer clean** (bir2cir/kotc were still injecting a few
bare-FQN strings the wire format forbids):
- `conv.to` (kotc `BirEmitter.kt` numeric-conversion path) — was `str(to)` (bare `"kotlin.Int"`) → `fqnJson(to)`.
- Synthetic `<>dotkt_KProperty` interface refs (kotc `synthDelegate`/`kPropertyDefs`) — `str(iface)`/literal → `fqnJson`.
- `newSuspendLambda`'s free-type-param list — a type-param NAME-declaration list, not a type-usage slot: renamed
  `typeArgs` → `typeParams` (the name-shorthand, consistent with the other lambda paths; kotc emit + bir2cir
  `SuspendLambdaLowering` read). A DISTINCT `typeArgs` (a type-USAGE list) is the SM **construction channel** (#75
  Batch B, 2A): each generic `newSuspendLambda` carries the ORIGINAL enclosing type for every dense SM parameter, so
  `SuspendLambdaLowering` instantiates `new smName<typeArgs…>(…)` without interpreting a declaration position in the
  enclosing generic frame. Omission on a generic node is malformed internal ABI; only a non-generic node may omit the
  list. The optional `capValues` (per-capture construction-value overrides, positional with
  `captures`) carries an SM-vocabulary spill (`SuspendColdLowering` GAP 2) or an `__outer` rebound to the splice's
  receiver temp (InlineSplice 2B). `funcType` is the canonical Kotlin function type: an extension receiver appears
  only in `funcType.recv`, while `funcType.params` contains regular parameters. The node's physical `params` remains
  receiver-first because those descriptors supply the state-machine field names and `create` arguments. Receiver
  reads in `body` name that leading parameter explicitly; a bare `this` is therefore reserved for a captured enclosing
  dispatch receiver and bir2cir never guesses which meaning was intended.
- The `StringCharSequenceBridge` adapter (bir2cir `AdapterTypeJson` literal + `WrapAdapter`) — every `type`/`ret`/
  `elem`/`argTypes`/`interfaces`/`ownerType` slot rewritten to `{t:"fqn",…}`; the retired `@<name>` this-assembly
  marker dropped (bir2cir/ilemit derive local-vs-referenced from the FQN via `_types`).

The OWNER-FQN and SIG-KEY string islands (§2.2.1) are deliberately OUT of scope — they are not document type slots
(owner is its own slot kind; the sig-key is a transient reflection-comparison key), so `STR_OK` allow-lists the
narrow owner/member-name keys and the validator never flags them. As of #48, `ownerType` is NO LONGER one of those
keys: kotc emits EVERY owner slot (`owner`/`ownerType`) as a structured `{t:"fqn",…}` node (the last bare-string
sites — top-level file-class calls, `__mref` interop forwarders, class-delegation forwarders — now use `fqnJson`),
so `ownerType` is removed from `STR_OK` and the owner-FQN island survives ONLY as ilemit's private in-assembly
`_types` string-keyed lookup, never on the wire.

**`sty` — the frontend static-type stamp (#122, BIR-only transient).** kotc stamps the instantiated `node.type` as a
structured `{t:…}` `sty` slot on every value node (`local`/`callStatic`/`callInstance`/`field`/`lateinitGet`/
`staticField`) at its `expr()` chokepoint, so bir2cir's `StaticType` CONSUMES the frontend-resolved operand type
instead of re-resolving a callee return against the ref.dll. bir2cir carries it across the passes that synthesize
new nodes (MemberCallSubstitution / NetInteropBinding stamp `sty` onto the clr* nodes they build) and STRIPS it in
`BirTypeLowering` before CIR — like `overrides`, it is bir2cir-internal metadata, never reaches CIR/ilemit. Its
VALUE is a `{t:…}` node, so `verify-schema` validates it as a type node with no `STR_OK` entry. The
primitive-shorthand LEAF vocabulary (`int`/`void`/`object`) inside a structured `fqn.name` stays a sanctioned
below-kotc CLR-resolution form (ilemit normalizes toward it via `PrimShorthandName`), NOT a value-slot string.

**One deriver, two layers.** *What type does this node produce* is answered in ONE place per fact. `bir-common/
NodeType.cs` owns the NODE-LOCAL answer — a stamp, then whatever slot the kind carries its own result type in,
recursing through the caller's deriver for the kinds whose type is an operand's. `StaticType.Surface` is founded
on it and adds ONLY what a node cannot know about itself: the enclosing lexical scope (a synthesized `local`
read), and the name-keyed `kotlin.Array<E>` spelling its classifiers match instead of the structural `{t:array}`
a declared slot needs. A stamp-less desugar is typed through its own shape — a `cond` by its LIVE branch (a
`throw`/`return` arm produces no value, so it cannot answer for one that does), a `valueBlock` by the `var` it
declares — because the alternative is a spill slot with no type, which is unverifiable IL and so a refusal to
compile accepted source.

**Stamp PRECEDENCE — `sty`, then `ret`, then `dynRet`.** One order, stated once in `bir-common/NodeType.cs` and
inherited by every reader (#199). `sty` is the frontend's INSTANTIATED type, stamped per CALL SITE, so where it
exists it is the precise answer. `ret` is emitted only when the callee or its owner is GENERIC — exactly where it
may name the UNinstantiated declared type — so reading it first typed a generic-owner call by its declaration
instead of by its use. `dynRet` is last: on a kotc-emitted `callInstance` it duplicates the instantiated type, and
a bir2cir synthesizer that stamps only `dynRet` means it. No reader restates this order; a second copy is how the
four variants that preceded this paragraph came to disagree.

> **INVARIANT — a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`.** The stamp is a claim
> about the value the node produces, not a historical note about the node it used to be. A pass that re-owns,
> erases, substitutes or narrows a call/field result (`UncheckedGenericCastReturnErasure`,
> `ConstructedMemberReturnSubstitution`, `CharSeqStringLowering`, `InheritedMemberOwnerBinding`, the `clr*`
> reshapes) must carry `sty` with the change or drop it, alongside the `ret`/`dynRet` it already updates. A stale
> `sty` surviving on a retyped node is a bug in THAT pass — never a reason to demote the stamp below `ret`.

That invariant is MECHANICALLY CHECKED, not merely stated (#305). bir2cir runs the shared `bir-common/IrSanity`
check 7 over each file's fully-passed BIR immediately before `BirTypeLowering` — the last point at which the stamp
still exists — and refuses a `sty` that names a different type than the `ret`/`dynRet` beside it. `IrSanity`'s
`CheckStampAgreement` documents the accepted-equivalence set and the corpus it is calibrated on; the short version
is that the relation is a REFUTATION test (a type VARIABLE, a `*`, `kotlin.Nothing`, a nullability wrapper, a
spelling difference between the kotlin.*/shorthand/System.* vocabularies, and any
pair of unlike or different-arity shapes all AGREE), so it names only the class that motivated it: a same-shape
pair whose argument names a genuinely different type. A MISSING stamp is not a disagreement — dropping it is one of
the two things this invariant permits.

A pass that CAN compute the new instantiated type rewrites the stamp; a pass that cannot — because what it wrote is
the physical, erased or declared shape rather than this call site's instantiation — deletes it through
`bir-common/NodeType.cs` `DropStampIfStale`, which deletes it *when, and only when, the new result refutes it*. The
qualifier is not caution, it is correctness in both directions: a stamp the new result still describes is worth
keeping (`ret` would answer the same, so nothing is gained by dropping), and where a pass's own rewrite is the LESS
trustworthy of the two — `ConstructedMemberReturnSubstitution` cannot tell a callee-relative `tv` from one kotc
already instantiated, so it can re-substitute an instantiated `Map$Entry<K,V>` into `Map$Entry<Map$Entry<K,V>,V>` —
the stamp is what shields every downstream deriver from it. That helper asks `IrSanity.StampAgrees`, the same
relation the check refutes with, so a pass and the chokepoint cannot answer the question differently; the check
consequently cannot fire on a pass that discharges its obligation this way, which is the intended outcome and not a
gap. The chokepoint is for the pass that has not been written yet.

**No `kotlin.Any` for a slot whose type could not be derived.** A declared slot — a state-machine field, a spill
local, a plan binding — with an underivable type is a REFUSAL that names the shape, not a box: `kotlin.Any` hides
a type the CLR would refuse and converts an earlier layer's dropped stamp into a runtime unbox fault. The
`kotlin.Any` occurrences that remain are ABI, not fallback (the cold entry's `Any?` return, `Continuation<Any>`,
`Result<Any?>`); `docs/dotkt-semantics.md` §7b holds the site-by-site triage. The refusals cannot fire on the BIR
the frontend produces, which is why they are witnessed by synthetic documents under `tests/ir/lowering/reject-*`
rather than by Kotlin source.

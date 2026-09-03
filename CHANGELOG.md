# Changelog

All notable changes to DotKt (Kotlin → .NET/CLR). Package versions carry the embedded
Kotlin compiler version as SemVer build metadata (e.g. `0.9.1+kotlin-2.2.0`).

## Unreleased

### Toolchain

- **Referenced method-generic accessors now retain every value-parameter slot (#656).** bir2cir recognizes the
  generic-call `shapeTypes` signature carrier alongside ordinary Kotlin and CLR call signatures when a protected
  member imported from another DotKt assembly is called from a lifted closure. The synthesized CLR UnsafeAccessor and
  its call therefore agree on the target and source-value parameters instead of failing during emission.

- **UnsafeAccessor calls now preserve object-erased generic return ownership (#652).** bir2cir carries the selected
  member's nullable-generic provenance onto synthesized accessors, allowing nullable realignment to distinguish its
  own erasure from other physical `object` contracts and retain the concrete use projection needed by arithmetic and
  other value consumers.

- **Inherited nullable-generic array access now retains its physical CLR result (#649).** bir2cir re-applies the
  declaration-driven nullable-generic use contract after binding a call to its generic base owner, so a protected
  `Array<T?>` accessor returns `object[]` physically and an explicit checked projection restores a concrete reference
  array instead of leaving a verifier-invalid edge for ilemit.

- **Generated generic value-class equality now reads peers through existential CLR accessors (#647).** kotc carries
  the compiler-generated equality origin for data and value classes into BIR, allowing bir2cir to replace an invalid
  `G$star`-receiver backing-field access with the exact getter bridge instead of applying a `G<T>` field token.

- **Star-dependent generic member results now retain their existential CLR carrier (#645).** bir2cir preserves the
  exact physical result of a star-projected slot instead of casting invariant nested generics to an invalid
  `G<object>` construction, while concrete arguments in mixed star/exact projections keep their checked projection.

- **Inherited class `super` calls now bind the concrete base MethodDef (#637).** bir2cir follows only the constructed
  base-class chain when an immediate superclass inherits a method or property implementation alongside an abstract
  interface slot, preventing ilemit from calling that abstract slot. Abstract Kotlin methods also remain bodyless in
  BIR instead of carrying parameter-check statements that an abstract CLR declaration cannot execute.

- **Kotlin `Unit` returns now preserve the CLR evaluation-stack contract (#636).** Return lowering follows the
  declared return target instead of the returned expression's static type, retaining `Unit` as an object for
  non-`Unit` declarations while emitting value-less returns for constructors and physical `void` coroutine frames.

## 0.9.12 (2026-09-02)

### Toolchain

- **One DotKt project can now build isolated outputs for multiple target frameworks (#338).** MSBuild outer/inner
  builds retain independent reference KLIB, BIR, CIR, response, fingerprint, and emitted-assembly state per TFM;
  platform-qualified outputs preserve the SDK's framework/platform attributes, matching C# and DotKt consumers bind
  the correct target graph, and source or reference removal invalidates only the affected inner compiler pipeline.

- **Kotlin can now declare CLR P/Invoke methods through the real `DllImportAttribute` (#339).** Top-level
  `external fun` declarations lower to bodyless CLR imports with their module, entry point, calling convention,
  character set, last-error, and related import flags preserved through CIR, reference assemblies, and DLL-to-KLIB
  round trips. The initial surface supports blittable primitives and enums, `IntPtr`/`UIntPtr`, and `ClrRef<T>`;
  unsupported declaration and marshalling shapes fail with focused diagnostics.

- **CLR unmanaged-pointer signatures now survive DLL-to-KLIB consumption exactly (#274).** dll2klib projects
  pointer fields, parameters, and returns through an opaque `ClrPointer<T>` Kotlin vocabulary; bir2cir materializes
  exact pointer, `void*`, nested-pointer, array, and nullable-value-pointee shapes only at the CLR representation
  boundary, and emitted MemberRefs retain that identity without widening ILVerify's permanent baseline.

- **User-defined Kotlin functions can now expose real CLR `ref` parameters (#276).** Non-suspend functions may accept
  `ClrRef<T>` and read or update the caller's live storage through `.value`; ordinary, generic, inline-spliced,
  C#-consumer, and DLL-to-KLIB round-trip paths preserve the managed-reference ABI and aliasing.

- **Common stdlib sources now remain byte-identical to upstream Kotlin (#516).** CLR physical names, sequence
  adapters, unsigned-array additions, and reified-array-incompatible sorting bodies live in explicit CLR-owned
  overlays selected by exact declaration identity. An offline full-subtree snapshot gate prevents future drift.

- **Generic continuation overrides now retain coherent Kotlin signatures across DLL-to-KLIB projection (#624).**
  `bir2cir` keeps an earlier source-type carrier authoritative when a declaration has already moved onto the
  monomorphic `Continuation<object>` CLR slot, preventing a later physical intermediate from masking the original
  `Continuation<T>` parameter while preserving the uniform runtime ABI.

- **Nullable-generic witness demand is now independent of Kotlin's `reified` modifier (#466).** bir2cir derives the
  hidden CLR nullability witness structurally from nullable-sensitive operations and propagates it through exact calls
  and lifted frames, while round-trip metadata carries semantic `reified` indices separately. This also removes the
  previous CLR-specific accidental allowance: passing a non-reified type parameter to a reified function is now
  rejected consistently with Kotlin, including through DLL-to-KLIB consumption.

## 0.9.11 (2026-08-31)

### Toolchain

- **Star-projected data-class field reads now bind through existential getter slots (#621).** Canonical backing-field
  reads in generated `copy` defaults and `equals` bodies are projected through the existing property getter on a
  fieldless `$star` interface, while custom and genuinely overridable getter semantics remain untouched.

- **Inline materialized coroutine blocks now retain constructed generic specializations (#619).** When an inline
  helper specializes its closure payload to a constructed owner type such as `List<T>`, bir2cir removes the obsolete
  synthetic type slot and instantiates the closure's exact construction frame before rebuilding its body in the
  enclosing suspend state machine. The emitted CIR therefore references the owner's real generic slot instead of an
  unbound synthetic `!N`.

- **Concrete reference arrays now retain their reified view across open nullable-generic returns (#322).** An open
  `Array<T?>` declaration keeps its uniform `object[]` CLR ABI, while a call instantiated with a reference type now
  states the checked projection back to the concrete Kotlin array (`Array<String?>` is `string[]`) before a following
  generic consumer is resolved. This removes the last compiler-defect ILVerify baseline entry without erasing
  reference element types from the public CLR surface; only the two by-design `localloc` entries remain.

- **Foreign nullable-value array refusals now report their exact uninhabitable crossing (#354).** A .NET member
  declaring `Nullable<V>[]` is rejected at call or implementation use with an actionable diagnostic that names both
  the declared array and Kotlin's canonical `object[]` physical image, rather than describing the crossing only
  through a generic collection example. General-array rank is retained in both names.

- **Inline `try` values now produce valid IL in ordered operand slots (#437).** `bir2cir` models array access,
  structural equality, object-method calls, and field/property writes in their physical evaluation order, hoisting
  protected regions to an empty CLR evaluation stack while preserving Kotlin's left-to-right side effects and the
  original storage location of addressable value-type receivers.

- **Cross-module suspend `super` calls now retain valid non-virtual dispatch (#439).** Consumer state machines call a
  private derived-instance forwarder that targets the producer's exact cold entry on its real `this`, avoiding
  recursive redispatch and invalid receiver IL.

- **Pure-app String values now cross referenced CharSequence helper boundaries with valid IL (#443).**
  `bir2cir` keeps frontend result-type stamps synchronized when local declarations and local-call returns collapse from
  `CharSequence` to `String`, so referenced Kotlin wrappers receive an explicit adapter instead of a raw CLR string.

- **Intrinsic override allocation now preserves exact declaration identity (#444).** Declaration rename and
  referenced-interface MethodImpl allocation select `@ClrIntrinsic` from the frontend-resolved generic arity and full
  parameter vector instead of taking the first same-name, same-parameter-count overload.

- **Collection-view conversions are now explicit CIR facts (#513).** After final member binding, `bir2cir`
  materializes every resolved mutable/read-only CLR sibling conversion across branch merges, constructor delegation,
  arguments, lexical/field storage, returns, and expression results. It closes exact field/member declarations against
  their constructed owner frames; `ilemit` emits those casts one-to-one instead of inferring Kotlin collection ABI
  from stack types, including both directions of nested `List` storage seams.

- **Collection helper dispatch now requires an exact Kotlin classifier identity (#600).** Names such as `Map.Entry`
  and `ListIterator` no longer acquire collection semantics merely because they contain `Map` or `List`; rendering,
  structural equality, and hash codes remain on the actual value instead of entering an incompatible collection helper.

- **DLL-to-KLIB round trips now preserve extension-receiver roles explicitly (#512).** Kotlin extension methods and
  property accessors carry a trusted parameter-role marker instead of relying on the physical name `__self`; ordinary
  parameters with that name remain ordinary, and receiver slots are collision-safe across inline and suspend lowering.

- **DLL-to-KLIB batch projection is faster and dependency-incremental (#615, #617).** dll2klib now converts stale
  references in one bounded process, derives every immutable catalog from one short-lived metadata snapshot per input,
  indexes type-forwarder targets, and persists a compact per-input MVID/direct-TypeRef graph. Forwarded references
  retain both facade and definition edges. A changed DLL regenerates only itself and its current or former reverse
  dependents; whole-universe arity changes additionally invalidate definitions and users of the affected name. Batch
  staging prevents a failed conversion from mixing a new projection universe with the last successful cache.

- **Non-public Kotlin interfaces and annotation classes now retain their CLR metadata visibility (#604).** `kotc`
  carries their source visibility through BIR alongside enums and now emits nested annotation declarations, so private
  and internal top-level declarations and private, internal, and protected nested declarations are no longer widened
  to public TypeDefs. Compiler-owned collection carrier interfaces that must cross assembly boundaries now state their
  published contract explicitly with `@PublishedApi`; BIR retains `internal` plus the annotation, and `bir2cir`
  consumes those facts into CLR-public TypeDefs instead of relying on the former visibility loss.

- **Non-public Kotlin enums now retain their declaration visibility in CLR metadata (#602).** `kotc` carries the
  source visibility of basic and explicit `@ClrEnum` declarations through BIR, so top-level and nested enum TypeDefs
  are no longer widened to public while `bir2cir` and `ilemit` continue consuming the explicit fact one-to-one.

- **Referenced CLR `[Flags]` enums now expose typed bitwise operations (#496).** Kotlin can use `or`, `and`, `xor`,
  `inv`, and `in` with the exact enum type, preserving unnamed bit patterns and every signed or unsigned underlying
  width. Operands are evaluated once in Kotlin order, and the contract survives DLL-to-KLIB round trips.

- **Regex option constructors now use an explicit stdlib binding contract (#515).** The stdlib authors the
  `RegexOption` and `Set<RegexOption>` conversion through alias-constructor delegation and explicit CLR enum values;
  bir2cir no longer recognizes Regex declarations, Kotlin enum ordinals, or the `RegexOptions` bit table. Constructor
  arguments remain single-evaluated, and unresolved local enum shapes no longer implicitly match arbitrary CLR enums.

- **The embedded Kotlin frontend is now 2.4.10 (#598).** Packages publish the new compiler identity, while
  `dll2klib` mirrors Kotlin's 2.4-line KLIB contract: compiler version 2.4.10 with ABI and metadata version 2.4.0.

## 0.9.10 (2026-08-26)

This final release includes all changes from `0.9.10-beta1` and `0.9.10-beta2`, plus the changes below.

### Added

- **Kotlin can now publish native CLR enums with explicit integral constants (#526).** `@ClrEnum` gives one
  non-property enum constructor parameter a compile-time-only role for selecting any legal signed or unsigned CLR
  enum underlying type. Declaration order remains Kotlin `ordinal` even for sparse or negative physical values,
  including through generic `T : Enum<T>` code and DLL-to-KLIB round trips; exact-name `valueOf`, CLR attributes,
  optional defaults, reflection, C# switches, and `System.FlagsAttribute` all consume the same explicit value map.

### Fixed

- **Mutable Kotlin collections now retain mutable iterator semantics across delegation and widened views (#590).**
  `MutableSet`, `MutableCollection`, `MutableIterable`, and `MutableList` route iterator calls through a compiler-owned
  mutable capability slot instead of returning the read-only `Iterator<T>` face. Pure-Kotlin and BCL-backed values,
  covariant and star-projected views, value elements, and duplicate-permitting collections therefore keep verifiable
  `MutableIterator<T>` behavior, including exact removal of the last returned occurrence.

- **Kotlin collection runtime classifiers now distinguish collections, sets, and maps without wrapping values
  (#315).** Emitted Kotlin collections carry compiler-owned nominal identities alongside their BCL operational faces,
  while star-projected BCL values are classified from the exact generic collection/set faces they implement.
  Dictionaries and arrays are excluded from `Collection`, reference identity is preserved, and mutable map keys and
  entries expose live identity-bearing Kotlin set views.

- **Suspending expressions now preserve Kotlin operand order across every current CIR operand shape (#306).**
  bir2cir records and reconstructs exact ordered operands for array and member writes, delegate/object/constrained
  calls, construction, collection literals, spread parts, and loop-borne inline iteration. Values before a suspension
  are evaluated once before resumption, and addressable value-type or constrained receivers keep their original
  storage location instead of mutating a copy.

- **Inherited CLR interface slots are resolved before emission (#355).** bir2cir now applies the same forwarding-
  bridge rules to plain .NET generic interfaces as to Kotlin-projected declarations, including nullable value-type
  seams, value-to-void returns, constructed method constraints, and return-only base/derived slot families, and
  carries only exact declaration operands for required MethodImpl rows. ilemit
  no longer enumerates interface members, guesses an implementation from a same-name overload set, or synthesizes
  semantic bridges; it consumes the resolved descriptors and rejects any descriptor left unmatched.

- **Packaged Kotlin builds no longer exceed Windows' 8191-character batch-command limit (#592).** The MSBuild
  integration writes each generated `kotc`, `bir2cir`, and `ilemit` argument set to a configuration-local response
  file, leaving only a short tool-and-`@file` invocation (plus explicit raw `kotc` user options) on the command line.
  The back-end tools share kotc-compatible response parsing, quoted values preserve punctuation, whitespace, and
  Windows backslashes, and `dotnet clean` removes the new intermediates.

- **By-reference paths through nested CLR structs now address the original storage (#308).** Call-plan lowering keeps
  only value-type receiver links in the location path while fixing arrays, indices, call roots, and other computed
  values once in Kotlin order. ilemit follows that physical path with recursive `ldelema`/`ldflda`, including CLR
  fields projected through Kotlin property syntax, so writes no longer disappear into temporary struct copies.

- **Constructed member returns no longer re-substitute caller-frame type variables (#328).** bir2cir treats exact
  agreement with the frontend result stamp as the closed-frame boundary and substitutes only distinct
  callee-relative results through the constructed owner. Nested results such as
  `Iterator<Map.Entry<K,V>>.next()` therefore retain `Map.Entry<K,V>` across both lowering sweeps instead of growing
  another nested entry at each pass; synthesized inherited-class/interface forwarding calls carry the same explicit
  caller-frame stamp.

- **Recursive CLR delegate graphs now fail with a bounded diagnostic (#584).** dll2klib tracks the exact active
  TypeDef path while expanding delegate `Invoke` signatures and rejects local, generic, and cross-assembly cycles
  that Kotlin metadata cannot represent as finite function types. Valid recursive CLR metadata therefore no longer
  terminates a worker with a stack overflow, while acyclic delegates retain their ordinary Kotlin function shape.

- **External delegate decoding now indexes each referenced assembly once per projection (#582).** dll2klib keeps
  referenced PE metadata and its arity, attribute, delegate, and value-type seeds at assembly-scanner scope while
  retaining decoded `KType` shapes in each package's own name table. Multi-namespace consumers therefore avoid
  reopening and rescanning the same external TypeDef table for every package without changing delegate or NRT
  projection.

- **DLL-to-KLIB signature decoding no longer rescans every local type for each namespace (#546).** dll2klib now
  discovers immutable local delegate and value-type seeds once per assembly, shares them across package decoders, and
  keeps only signature-derived value-type observations decoder-local. Large multi-namespace reference assemblies
  therefore avoid the former namespace-count × TypeDef-count construction cost without changing delegate or NRT
  projection.

- **Lifted lambdas now share a constrained inner class's physical generic frame (#579).** When existential projection
  removes an owner-dependent CLR constraint such as `E : T`, bir2cir propagates owner-slot permutations through
  nested lifted types, synchronizes the weakened copied slots on compiler-generated closures and state machines, and
  follows transitive erased constraint edges when binding calls inside their bodies. Concrete and star-outer
  construction therefore load and dispatch through the original Kotlin bound without leaving an impossible
  constraint on a generated TypeDef.

- **Transitive inner constraints now remain constructible through a star-projected outer (#575).** Existential inner
  factories keep indirect arguments such as `F : List<E>` in their own generic frame while fixing the directly
  owner-dependent `E : T` argument to Kotlin bottom. The physical inner type omits constraints that cannot name the
  hidden outer frame, preserves their complete Kotlin form for DLL-to-KLIB projection, and explicitly converts bound
  receivers in method bodies, producing verifiable same-module and cross-module CLR binaries without losing values.

- **Nullable type-parameter bounds now survive DLL-to-KLIB projection (#576).** When a producer-authored bound is
  restored from `KotlinSupertypes`, dll2klib replaces the CLR-erased constraint list as one semantic unit instead of
  retaining a spurious `Any` bound beside the restored nullable type. Method bounds travel on their own MethodDef
  carrier and follow the same complete-list rule, including suspend bridges and inherited interface declarations;
  captured outer parameters remain owned by their enclosing class. Cross-module consumers can therefore infer
  `Nothing?` for owner-dependent inner constructors and pass nullable arguments to `E : T?` functions just as in
  the producer.

- **Star-outer inner construction now preserves owner-dependent constraints (#567).** Existential constructor
  factories keep `E : T` on the real inner type while closing Kotlin's only universally constructible argument,
  `Nothing`, to the captured outer bound inside the concrete bridge. Direct, derived, mixed-generic, stored-result,
  and cross-module uses therefore produce verifiable CLR instantiations without guessing an outer argument.

- **Packaging and version changes now select the packaged-SDK gate locally (#549).** The change-aware gate
  recognizes package inputs, guarded documentation, and both sides of renamed paths while keeping ordinary
  compiler-only FULL plans free of the release-package verification cost.

- **Array factories now preserve spread arguments (#550).** Lone forwarded arrays and mixed `spreadConcat`
  operands flow through `arrayOf` and primitive array factories without being replaced by an empty allocation,
  including when a spread operand suspends or has side effects.

- **Constrained calls now have one current CIR operand shape (#552).** ilemit consumes only the `args` array emitted
  by current bir2cir producers; the unsupported legacy single-`arg` fallback and its parallel resolver path are gone.
  Malformed current `args` input is rejected by the ordinary BIR/CIR schema contract.

- **Inherited inner construction through star-projected derived receivers now has a verifiable CLR path (#561).**
  bir2cir exposes exact inner-constructor factories on the enclosing existential carrier, including overloaded,
  generic, defaulted, stored-result, method-bound, and cross-module uses, without inventing an invariant closed outer
  type. Inner generic constraints that depend on the unknown outer argument are rejected instead of producing an
  invalid closed CLR type.

- **Star-projected calls now bind their exact existential CLR slot (#556).** bir2cir follows the referenced existential
  interface's source-member metadata and preserved Kotlin parameter descriptors when an owner-dependent generic
  declaration and its physical slot have different CLR descriptors, including across overloads, explicit-name
  collisions, and same-module or cross-module inline substitution of a concrete receiver.

- **Suspend lambdas in member extensions now keep both captured receivers distinct (#563).** kotc preserves each
  generated capture descriptor in the lambda body so dispatch-receiver and extension-receiver reads map to their own
  state-machine fields instead of both resolving through `__outer`.

- **Captured array receivers now retain their element type through suspending inline iteration (#558).** kotc carries
  the captured receiver's exact static type on its BIR body spelling so inline-spliced array loops and accesses retain
  generic and specialized element facts through lowering.

- **Inherited inner-class construction now retains the selected enclosing-owner slot (#555).** kotc projects a
  derived enclosing-instance receiver through the frontend-selected inner application before describing same-module
  and referenced constructors, preserving generic substitutions and exact same-arity overload selection.

- **Inline-spliced suspend carriers now capture only the caller's generic frame (#557).** bir2cir keeps constructor and
  local-function declaration descriptors in their own frames while densely remapping caller-owned construction types.

- **Explicit CLR names now work on concrete default-interface members (#553).** kotc gives independently allocated
  interface bodies and their override edges exact declaration identities, while bir2cir preserves the chosen physical
  name through suspend, inherited-DIM collision, and generic existential-slot MethodImpl synthesis.

## 0.9.10-beta2 (2026-08-23)

### Fixed

- **CLR reference projection now indexes local type identities instead of rescanning every TypeDef (#543).** dll2klib
  resolves awaitable and enumerable pattern edges through one per-assembly Kotlin-facing name index while preserving
  the existing metadata-order selection, reducing large Windows SDK projections from minutes to tens of seconds.

- **Inherited Kotlin default arguments now retain their declaring semantics through override chains (#542).** kotc
  keeps the selected call-site shape while sourcing omitted expressions and their receiver/parameter symbols from the
  base declaration that owns the defaults, including abstract and generic bases.

- **Callable references now adapt to open nullable function slots without rewriting their declarations (#348).**
  kotc binds top-level and companion references through generated static forwarders and ordinary bound member
  references through receiver-capturing closures, including members projected from CLR assemblies. bir2cir may
  therefore align only the compiler-owned target with an erased
  `Func<object, …>` slot and narrow inside its body, including when the selected declaration was restored from a
  referenced DotKt assembly. A nullable CLR value-type receiver is narrowed before capture, and constrained virtual
  dispatch names the base slot while selecting the captured value-type implementation.

- **Generic inner defaults now close their complete enclosing type frame (#277).** When an inner constructor or member's
  omitted argument reads a generic outer instance, kotc maps the inner declaration's own parameters and each enclosing
  frame in Kotlin's semantic order before carrying the default into BIR.

- **Malformed CLR optional constants now stop at the bir2cir metadata boundary (#538).** A reflected Constant or
  custom-constant carrier must exactly inhabit a declared primitive, string, or enum slot; a reference slot accepts
  only an assignable carrier and receives an explicit boxing/upcast. Incompatible metadata gets the existing source,
  callee, and parameter diagnostic instead of reaching ilemit.

- **Non-null CLR optional constants now inhabit `Nullable<T>` slots as real values (#535).** bir2cir types primitive
  and enum metadata constants as the nullable element and constructs the `Nullable<T>` wrapper explicitly instead of
  emitting `ldnull`. Null remains `default(Nullable<T>)`, while a value that cannot inhabit the element is rejected
  with source, callee, and parameter context.

- **CLR decimal and value-type optional arguments now materialize as valid values (#527).** bir2cir reconstructs
  `DecimalConstantAttribute` and `DateTimeConstantAttribute` values through their exact public value-type constructors
  and lowers a reflected null for a value-type slot to `default(T)` rather than `ldnull`. Static, instance, inherited,
  and constructor omissions share the rule; unsupported metadata is rejected with source and parameter context.

- **CLR enum-valued optional arguments now retain their declared enum slot and exact underlying value (#525).**
  bir2cir materializes zero, non-zero, flags-composite, signed, and unsigned ECMA-335 constants from the selected
  referenced declaration. An uncarryable omitted value now reports its source declaration, exact callee and parameter
  role, and explains that either a stale reference or an unrepresentable value may be responsible.

- **Cross-module overload binding now recognizes extension-receiver function slots (#523).** bir2cir selects the
  referenced MethodDef by the frontend declaration identity, then validates its function slots through the shared CLR
  delegate parameter sequence. A `P.() -> R` parameter therefore agrees with its reflected
  `Action<P>`/`Func<P,R>` representation without reselecting a same-name sibling, including generic receiver slots;
  an identity/signature disagreement now fails in bir2cir with the call-site context.

- **Suspend captures now retain explicit ownership across inline-spliced anonymous methods (#520).** kotc carries the
  exact suspend-frame slot identity on movable inline-lambda carriers, while bir2cir threads method- and type-scoped
  reified-nullability witnesses through synthesized generic state-machine frames. Transported, nested, shadowed,
  mutable ref-cell, and nullable witness captures therefore survive a real resume without becoming unspilled locals.

- **Covariant suspend overrides now retain their exact Kotlin result across DLL-to-KLIB projection (#511).** Logical
  suspend results are carried independently of their erased `Task<T>` representation, including nested nullable
  generic types and nested classifiers. Re-import omits compiler-generated hot/cold MethodImpl machinery.

- **Compiler and gate diagnostics now describe the violated invariant without stale tracker or design-document
  coordinates.** User-facing bir2cir, ilemit, packaging, and verification failures no longer embed GitHub issue
  numbers, historical batch labels, or section references whose targets can close, move, or describe only an older
  bug shape. The emitter-residual gate uses semantic marker names so its remediation remains understandable without
  repository archaeology.

## 0.9.10-beta1 (2026-08-22)

### Fixed

- **Covariant overrides now fill interface slots declared in referenced Kotlin assemblies (#320).** bir2cir joins
  frontend override edges with the referenced declaration's exact MethodDef identity, synthesizing one private
  exact-return bridge per physical signature and attaching every redeclared interface slot to it. Property getters,
  ordinary and generic functions, inherited generic interfaces, and `Nothing` returns now load and dispatch through
  both the broad interface type and the authored narrow member. Bridge forwarding remains virtual, so an inherited
  MethodImpl reaches a further-derived override instead of pinning interface calls to the first concrete class.

- **Read-only collection identity now survives on cross-module supertypes and class bounds (#350).** bir2cir records
  collection-bearing base edges, interface edges, and type-parameter constraints in the shared Kotlin supertype
  carrier before CLR inner-type, star-projection, and nullability transforms erase their source meaning. dll2klib
  therefore restores nested collections, including nullable/star elements and inner-class applications, without
  confusing them with mutable collection aliases.

- **Projected CLR owners now retain each nested generic segment's exact arity (#505).** `dll2klib` records both the
  Kotlin-facing owner and its exact ECMA TypeDef identity, so legal declarations such as ``Outer`1+Leaf`1`` and
  ``Outer+Leaf`2`` no longer collapse to the same flattened BIR owner before bir2cir resolves their members.

- **External generic structs now cross nullable value slots with their structural CLR representation (#501).**
  bir2cir wraps a bare value entering `Nullable<V>` and extracts a proven-present value before it enters a bare
  receiver, argument, local, field, array element, branch, or return slot, using the complete value-type identity
  rather than member or type-name special cases.

- **Constructed nested generic types now bind external member signatures (#503).** bir2cir resolves the exact CLR
  metadata name for each flattened type identity, so an argument such as `Outer<Int>.Leaf<String>?` matches the
  declaring MethodDef instead of guessing one aggregate arity suffix for the nested name.

- **Referenced value-type classification now uses the complete type identity (#356).** Nested CLR names are
  normalized consistently and generic argument count distinguishes legal same-stem declarations, so every bir2cir
  consumer agrees whether a constructed external type needs value-type lowering regardless of scan order or which
  compiler stage produced its token.

- **Projected generic methods and properties now preserve the CLR-only half of their type-parameter constraints
  (#498).** dll2klib keeps implicit `System.ValueType`/`System.Enum` rows, including the modified `ValueType` row used
  by `unmanaged`, out of Kotlin bounds on every callable and
  accessor projection path, while bir2cir validates those rows and CLR `class`, `struct`, and `new()` flags against
  the exact MethodDef selected for each use. Closed and open calls, extension methods, delegates, and generic Kotlin
  property accessors therefore reject invalid physical instantiations before emission without inventing Kotlin
  classifiers or interpreting a callee's generic variables in the caller's frame.

- **Class type-parameter constraints now survive DLL-to-KLIB projection without inventing an uninhabitable Kotlin
  bound (#351).** Ordinary nominal rows are enforced by the Kotlin frontend, including multiple bounds, while the
  implicit CLR `ValueType`/`Enum` rows are kept out of the Kotlin classifier lattice. bir2cir validates those physical
  roots and the CLR `class`, `struct`, and `new()` flags against the referenced construction, so legal value, enum,
  reference, and default-constructible arguments run and invalid ones fail before emission. Round-trip inspection also
  pins the exact classifier, self argument, and single-bound cardinality of Kotlin-origin `Comparable<T>` constraints.

- **`suspendCoroutine` now accepts an already-materialized block value (#246).** `bir2cir` invokes a block held in a
  local, parameter, field, or other function-valued expression directly, preserving its single evaluation and
  captures instead of requiring a literal lambda body that can be reconstructed inline.

- **The rich-enum BIR carrier is now part of the enforced schema (#492).** Its current declaration/member-map shape
  is documented and structurally validated, including empty enums, and the gate rejects a malformed carrier or one
  that survives bir2cir into CIR.

- **Empty rich enums now emit valid BIR (#490).** Their synthesized `values()` returns an empty array, while
  `valueOf(name)` emits its `IllegalArgumentException` path directly instead of constructing a malformed empty
  conditional branch list.

- **Rich enums retain their Kotlin enum contract across a DLL-to-KLIB round trip (#487).** The producer now carries
  an explicit trusted entry/metadata/API map, so `dll2klib` projects the physical reference-class implementation as a
  final enum with its source interfaces, enum entries, no callable constructors, and no leaked compiler-only fields or
  methods. Cross-module `name`, `ordinal`, `compareTo`, `entries`, `values`, and `valueOf` use that carrier instead of
  recognizing physical member names.

- **Kotlin-declared CLR events can now be subscribed directly (#482).** `bir2cir` binds add/remove calls to the
  synthesized local event declaration and carries its exact owner, delegate, and accessor signature into CIR, so
  ordinary, inherited, generic, cross-file, and rich-enum entry events support `subscribe` and reliable removal
  through `close()`, including custom interface delegates and type-parameter receivers.

- **Rich enum entries may now select secondary constructors (#484).** Every rich-enum constructor preserves the
  frontend-selected delegation target after the synthesized name and ordinal parameters, including constructor
  chaining, default arguments, secondary-constructor bodies, and entries with anonymous class bodies.

- **Rich enums now preserve their implemented Kotlin interfaces (#479).** Constructed interface supertypes remain
  explicit BIR facts, so rich-enum methods and properties fill their selected slots and inherited default methods and
  property accessors remain available through interface-typed calls.

- **Rich-enum base state now follows Kotlin instance-initialization semantics (#480).** Enum-body property
  initializers and `init` blocks run in declaration order after constructor-property storage and before an entry
  subclass initializes its own state. Property- and initializer-only enums use the rich plain-class shape as well,
  and a regular non-property constructor parameter no longer becomes a nonexistent backing-field write.

- **Rich-enum entry bodies now preserve their own state and initialization (#478).** Per-entry subclasses emit
  property backing fields and run property initializers and `init` blocks in declaration order after base
  construction. Kotlin-owned CLR events declared by an entry are synthesized on that subclass as well.

- **CLR properties accessed through a type-parameter receiver now emit verifier-valid dispatch (#325).** Interface
  accessors use `constrained.` for both reads and writes, while non-virtual class accessors use the receiver's resolved
  class constraint. Generic `CharSequence` constraints stay aligned when that app-level representation becomes
  `System.String`, including compiler-generated private-access forwarders.

- **Constrained calls through nullable-value generic bounds now widen arguments to the bound's physical slot
  (#345).** Once bir2cir closes a type-parameter receiver's interface owner, it applies the same
  `Subst(Erase(declared slot), owner arguments)` rule as other nullable-generic calls. A value passed through a bound
  such as `T : Sink<Int?>` is boxed for the erased `Sink<object>` slot, including across a projected reference KLIB.

- **Calls through `Comparable<Int?>` and other object-erased `Comparable` receiver types now use the interface the
  value actually implements (#346).** bir2cir keeps argument-dependent alias owners semantic until nullable-generic
  erasure and final type lowering select their physical classifier, so the receiver, call owner, and resolved member
  all consistently target non-generic `IComparable.CompareTo(object)`.

- **A `Comparable` implementation whose `compareTo` returns `Nothing` now loads and terminates correctly (#321).**
  bir2cir synthesizes the non-generic `IComparable.CompareTo(object)` bridge while the Kotlin return stamp is still
  available, so the bridge terminates instead of returning `Nothing`'s CLR `object` erasure into an `Int32` slot.

- **The public reference-KLIB MSBuild contract now works from multi-target outer builds (#469).**
  `DotKtResolveKlibReferences` dispatches reference resolution and projection to each TFM-specific inner build and
  returns the generated KLIBs as `@(DotKtResolvedKlibReference)`, with source-assembly and target metadata. The old
  synthetic frontend-input target/items were removed instead of retained as a second contract.

- **MSBuild intermediates are isolated by configuration, target framework, and runtime identifier (#467).** BIR,
  CIR, projected reference KLIBs, response/options files, stamps, and the generated C# placeholder now live under
  `$(IntermediateOutputPath)`. Concurrent Debug/Release builds no longer delete or consume each other's compiler
  state, and cleaning one configuration leaves the others intact.

- **CLR explicit interface implementations no longer become ordinary Kotlin class APIs (#463).** `dll2klib`
  represents method, property, indexer, and event satisfaction as hidden fake overrides, preserving colliding and
  constructed generic slots. A Kotlin subclass can re-list an interface and provide a new exact `MethodImpl`; a
  same-named declaration on a subclass that does not re-list it leaves the base mapping unchanged. Classes that also
  expose a final public member of the same shape remain callable and can still reimplement the distinct interface
  slot.

- **`EventSubscription.close()` and other inherited CLR-interface calls now bind their exact physical slot (#462).**
  `bir2cir` closes the frontend-selected interface owner in the receiver's generic type frame, preserves Kotlin-only
  collection call semantics, and emits a complete CLR member identity for both referenced and locally derived classes.

- **Public CLR classes no longer expose inaccessible interface supertypes or lose public default-interface slots
  (#451).** `dll2klib` now evaluates constructed interface visibility against the selected reference universe,
  retains resolvable public/protected edges, and projects public slots implemented through hidden interfaces as
  concrete class members. Generic methods, properties, indexers, events, nullability metadata, cross-assembly
  providers, type forwarders, and Kotlin subclasses use the authoritative public declaration shape.

- **C# extension methods no longer appear twice in Kotlin completion.** `dll2klib` now projects the declaring-class
  view as an ordinary static function and emits exactly one receiver-style extension in the CLR namespace package.
  The former synthetic package named after the static extension container is gone; static calls and namespace-
  imported extension calls still target the same CLR MethodDef.

- **Property references to `length` on .NET-mapped `String`, `StringBuilder`, and `CharSequence` owners now work
  (#242).** `kotc` now projects them as ordinary Kotlin property references with an explicit callable-interface fact;
  `bir2cir` materializes a CLR delegate when that value fills a function slot and resolves the accessor to its physical
  CLR representation.
- **Value-producing `try` expressions in array, string-concatenation, spread-vararg, and collection construction
  elements no longer emit invalid CLR programs (#319).** `bir2cir` now hoists protected regions out of ordered value
  slots whose CLR emitters already hold an accumulator, while preserving key/value and element evaluation order.
- **An inline function with a value-producing `try` body now works in the first string-concatenation operand
  (#285).** The inline-spliced value block is normalized before `ilemit` pushes the concatenation accumulator.
- **A same-module `super`-qualified suspend call now targets the base cold entry non-virtually (#436).** `bir2cir`
  preserves the resolved base declaration and dispatch fact through cold lowering, preventing an override from
  recursively redispatching into itself.
- **Nullable `StringBuilder.append`/`insert` overloads now preserve Kotlin's documented `"null"` rendering (#317).**
  `bir2cir` consumes the frontend-selected member's complete signature instead of conflating same-name, same-arity
  CLR bindings. `Appendable.append(value, startIndex, endIndex)` also translates Kotlin's exclusive end to the CLR
  count slot while preserving receiver and argument evaluation order.
- **Generic `Array<T>(size) { init }` construction in valid reified bodies now preserves its element type (#353).**
  `kotc` carries a scoped type variable into BIR array construction instead of falling through to a bogus empty
  `kotlin.Array`; `bir2cir` then resolves the generic CLR allocation and initializer invocation normally.
- **Lazy `Sequence.mapNotNull`/`filterNotNull` and `Sequence.filterIsInstance<R>` preserve their result element
  identity (#349, #446, #449).** Their Kotlin declaration bodies retain the lazy null/type predicates, while one
  annotation-driven bir2cir rule replaces only the final erased-platform cast with a typed element-view adapter.
- **Rich enums may mix entries with and without per-entry bodies (#279).** An entry subclass no longer makes the enum
  base abstract by itself, so a body-less sibling can instantiate the base while concrete open members retain their
  default implementation. Entry subclasses also preserve method and property overrides, including abstract property
  accessors.

## 0.9.9 (2026-08-17)

### Added

- **Regression coverage now pins constructed state-machine callback owners at generic `.await()` points (#303).**
  Incomplete-task tests exercise both method- and owner-generic state machines, and same- and cross-module generic
  suspend fixtures use their own real await points instead of non-generic helpers.

- **The Kotlin-only mutable-collection members have a physical CLR representation, and runtime-reflection dispatch is
  gone (#400).** `MutableCollection<E>` is `ICollection<E>` and `MutableList<E>` is `IList<E>`, neither of which has a
  slot for Kotlin's `removeAll`/`retainAll`/`addAll(elements)`/`addAll(index, elements)`. `removeAll` and `retainAll`
  used to reach a `clrDynInstance` node that `ilemit` emitted as `recv.GetType().GetMethod(name).Invoke(recv, args)` —
  a name-only runtime lookup returning null, i.e. an opaque `NullReferenceException`, for every BCL-backed receiver
  (`mutableListOf`, `HashSet`) — and `addAll` reached an unconditional static helper that silently bypassed a Kotlin
  implementer's override. Both are replaced by one contract: `bir2cir` routes all four to
  `kotlin.collections.ClrCollectionDefaults` dispatchers, and a new pass (`KotlinCollectionSlotSynthesis`, the mirror
  of `CollectionBclSlotSynthesis`) gives every emitted Kotlin class that declares one of them the compiler-owned
  `DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots`/`KotlinMutableListSlots` interface plus an exact
  `clrInterfaceImpls` MethodImpl bridge, so an override is reached by ordinary virtual dispatch — locally and
  cross-module. The slot interfaces are non-generic and their element-collection parameter is erased to `Any`, so
  the capability test is independent of the instantiation the dispatcher was called at; a constructed `Slots<E>` test
  would instead be correct only while the dispatchers keep an invariant receiver parameter, and its failure mode is a
  silently skipped override. They are `internal` (unnameable from user Kotlin source) while still emitting as
  CLR-public TypeDefs, and dll2klib keeps the compiler's reserved `DotKt.Runtime.CompilerServices` namespace out of
  projected Kotlin supertype lists. `removeAll` now honours Kotlin's contract for duplicates, and the
  self-aliasing forms (`c.removeAll(c)`, `c.retainAll(c)`, `c.addAll(c)`, `l.addAll(i, l)`) are defined; see
  `docs/dotkt-semantics.md` §5c-quater.

- **External members cross CIR as one complete scalar identity (#370).** `bir2cir` now serializes every external
  call, constructor, delegate target, MethodImpl target, field/accessor, attribute constructor, and compiler-authored
  operand as a `memberRef` containing the physical assembly, exact declaring instantiation, metadata name, generic
  arity, calling convention, return and parameter signatures, and modifier-aware type shapes. `ilemit` maps that
  identity to exactly one declaration in the target universe and fails diagnostically on zero or multiple matches;
  it performs no overload ranking, name/arity fallback, assignability selection, or standard-library ABI inference.
  The former split #336 owner/signature families and shadow-parity path are retired and forbidden in CIR. Provenance
  checks at every call, constructor, field, and MethodImpl emission boundary keep compiler-authored expansions under
  the same rule, while the schema, lowering, runtime, packaged-SDK, reverse-interop, round-trip, ILVerify, and
  target-universe gates exercise the completed cutover.

- **Explicit CLR callable names and fail-closed physical signature collisions (#402).** `@ClrName` (with `@JvmName`
  as a compatibility alias) now travels as an explicit BIR fact to bir2cir's post-erasure MethodDef allocation.
  User declarations no longer receive automatic `$dotkt$<hash>` names: an unresolved or still-colliding explicit
  name is a compile error, while unavoidable generated collision suffixes are deterministic and unbranded.

- **Kotlin 2.4 `companion { }` blocks and `companion` extensions are supported as native Kotlin/CLR static
  declarations (#382).** Both spellings now compile, run and survive a DLL → KLIB → second-module round trip, with no
  CLR-specific source annotation involved.

  A member of `class C { companion { … } }` becomes a genuine static member of the CLR type `C` — a static method, or
  a static property over static storage initialized in the **type initializer** rather than in a constructor. This
  holds in a class, a nested class, an `inner` class, an interface (a CLR interface legally carries static methods,
  static fields and a `.cctor`) and an enum class; an enum that declares one takes the plain-class shape, because an
  ECMA-335 enum TypeDef may not carry a non-literal static field. Overloads, visibilities, `val`/`var`, `const` and
  callable references (`C::f`, `C::v`) all work, and a real `companion object` remains structurally distinct — a class
  may declare both. On a generic owner the statics live on one explicit non-generic compiler carrier that is merged
  back into the semantic owner on round-trip, so `Box.count` is ONE variable exactly as the Kotlin source says,
  rather than one per closed generic type and without inventing an invalid representative type argument.

  A top-level `companion fun C.foo()` / `companion val C.bar` gets one uniform physical representation: an ordinary
  receiverless static of the declaring file's facade class, with the associated type carried in trusted
  `[KotlinCompanionExtension]` metadata. It is never made a member of `C`, so it behaves identically when `C` is an
  external CLR type. `dll2klib` restores the standard Kotlin shape — the static-declaration flag plus a receiver type,
  which is exactly what Kotlin means by a companion extension — so a second module resolves `C.foo(...)` from metadata
  alone; like any extension it must be in scope at the use site. Two frontend erasures had to be undone for this:
  fir2ir drops a companion extension's receiver parameter (recovered from FIR at capture time), and its LAZY
  declaration builder — used for library declarations — adds that parameter back unconditionally, so a cross-module
  callee declared a parameter its compiled method does not have.

  See `docs/dotkt-semantics.md` §8c-bis.

### Removed

- **The runtime-reflection dispatch layer (#400).** Deleted with its last producer: the `clrDynInstance` CIR node kind
  (and its entries in `scripts/verify-schema.py`), `bir2cir`'s interface-owner-miss catch that minted it, `ilemit`'s
  `EmitDynamicCall`/`OwnerHasClrInterface` emitter, the `callInstance` `dyn:true` branch, and the catch-based
  static-resolution fallback — an unresolvable member is now a hard error at the layer that dropped it. The
  `Type.GetMethod` and `MethodInfo.Invoke` well-known roles are gone from all three lockstep tables. The
  `@ClrIntrinsicAsDynamic` annotation is removed with its `ReferenceMetadataIndex` arm and its `AGENTS.md`
  undefined-behavior entry: it had zero use sites and there was no producer anywhere of the `dyn:true` flag it was
  meant to set, and an instrumented emitter run over the whole corpus recorded zero firings of it and of the catch
  fallback.

### Fixed

- **Array-backed sequences iterate safely again (#284).** `sequenceOf(vararg)` and `Array<T>.asSequence()` now reach
  `Array<T>.iterator()` through a correctly closed generic implementation type instead of faulting the process with
  `AccessViolationException`. The NUnit gate forces both public entry points through value-element materialization.

- **Intentional `localloc` unverifiability is no longer reported as XFAIL.** The two stack-buffer fixtures now live in
  a dedicated `ILVERIFY_UNVERIFIABLE` baseline that accepts only ILVerify's `[Unverifiable]` finding kind; a
  StackUnexpected, DelegateCtor, or any other error on the same method remains a NEW-FAIL. Runtime assertions continue
  to validate stack-buffer, Span and byref behavior, and stale entries still fail the baseline audit.

- **A generic method on a same-assembly constructed generic owner could borrow its signature from a sibling overload
  (#400).** The call was initially linked by its complete owner/name/arity/parameter descriptor, but MethodSpec
  construction then searched the open TypeBuilder again using only name and generic arity. `ilemit` now preserves the
  exact open MethodDef when it anchors that selected declaration onto the constructed owner and uses only that
  declaration for method/type-argument substitution. Fixtures cover both declaration orders for same-name,
  same-arity generic methods whose open parameter signatures swap value and reference slots.

- **Kotlin property accessors no longer collide with ordinary `get_<name>` / `set_<name>` functions on the CLR
  (#393).** bir2cir assigns every Kotlin accessor the dedicated `prop_get<name>` / `prop_set<name>` physical name in
  top-level, instance, companion, extension, and companion-extension placements. External CLR property interfaces and
  virtual base properties retain their native accessor slots through exact MethodImpl metadata, so a property and an
  ordinary function with the former accessor-shaped name can implement separate interfaces and dispatch independently.
  Default interface properties use a private final forwarding MethodImpl, preserving the public overridable DIM while
  filling the external CLR property slot exactly. Same-kind declarations that collide only after CLR erasure remain
  the separate frontend-callee-identity problem tracked by #395.

- **Property accessors now retain their frontend-resolved Kotlin identity until bir2cir assigns their CLR
  representation (#397).** Calls, fake overrides, bridges, companion and extension properties, reference metadata,
  and DLL → KLIB projection use explicit property name, accessor role, association, owner, and signature facts instead
  of reconstructing semantics from a physical `get_`/`set_` method name. CIR carries exact Property/MethodSemantics
  links for one-to-one emission, while method-generic extension properties use trusted accessor metadata because CLR
  Property rows cannot represent method generic parameters.

- **A Kotlin companion-block static on a referenced GENERIC type reached the emitted IL with an open generic owner
  (#382).** Those statics are now resolved to their trusted non-generic physical carrier before CIR emission, so
  `ilemit` receives one complete MethodDef/FieldDef owner and never invents a closed generic instantiation. Ordinary
  CLR statics declared by a foreign generic type continue to use the existing explicit representative TypeSpec.

- **A Kotlin class extending a .NET type re-declared that type's static members (#382).** The frontend materializes a
  member on the subclass so `Sub.Shared` resolves; the CLR does not inherit statics into a derived TypeDef, so
  emitting one produced a second, unrelated member — and, once it was correctly marked static, one that was
  simultaneously static and an override. The subclass now emits nothing for it and `Sub.Shared` is `Base.Shared`.


- **Nested and local declarations now retain their Kotlin ownership through BIR and receive CLR nesting in bir2cir
  (#225).** Local functions remain lexical BIR declarations linked by explicit IDs until bir2cir selects their
  MethodDef owner; local, anonymous, closure, state-machine, and inner types carry an explicit semantic owner instead
  of being inferred from generated names or bodies. CLR nested generic frames preserve enclosing constraints, private
  access no longer requires blanket cross-class widening, and dll2klib reconstructs the nested Kotlin classifier tree
  while hiding compiler-generated implementation methods and capture fields.

- **Round-trip: companion objects now retain their Kotlin declaration across DLL/KLIB
  re-consumption (#275).** `kotc` carries the source association/name, `bir2cir` materializes a trusted narrow
  `[KotlinCompanion]` metadata record, and `dll2klib` writes the standard KLIB companion link from that record without
  suffix or name inference. Every companion is emitted as a compiler-reserved ordinary nested CLR carrier with one
  `$INSTANCE`, preserving singleton identity within each closed CLR owner, supertypes, instance members, custom names,
  and use as a value or type.
  Generic owners contribute separate unconstrained physical capture parameters; those parameters are hidden from the
  semantic KLIB companion and unqualified Kotlin uses close them independently of the owner's source type arguments.
  Consequently C# views different closed generic owners as different CLR static regions in this first nested-carrier
  implementation; cross-instantiation singleton unification is deliberately deferred. Protected
  companions retain protected Kotlin visibility while their generated carrier remains reachable from lifted callable-
  reference and suspend helpers. Custom names remain distinct from ordinary nested classes. CLR static members use
  the standard KLIB static flags directly and no longer manufacture a companion type/value. A basic CLR enum keeps
  its enum representation and nested carrier; because CLR enums cannot own the
  type initializer needed for a reference-valued outer field, only its C# source-name accessor remains deferred. The
  round-trip gate independently inspects the semantic BIR, physical CIR/DLL carrier, generated KLIB
  linkage, generic constraints, and runtime behavior.

- **kotc: an OMITTED `vararg` argument is now the empty array it always denoted.** A vararg is omissible without
  being optional — Kotlin forbids it a default expression — so every argument-vector builder read the empty slot as
  an omitted DEFAULT it had nothing to fill from: the two that key on `defaultValue` dropped the slot outright and
  emitted a call one argument shorter than the declaration it named, and the inline splice left a `null` in it.
  `f()` on `fun f(vararg xs: Int)` was refused at CIL emission ("CIR argument count mismatch"), the inline splice
  failed loud on the same slot ("missing (non-defaulted) arg"), and the shape reached ordinary .NET interop
  through `params`: `Console.WriteLine("x")` selects `WriteLine(format, vararg arg)` because a non-null `String` is
  strictly more specific than the `String?` of `WriteLine(value)`, so the canonical console call did not compile.
  All three builders — same-module, reference-KLIB, and the inline splice — now fill the slot with `newArray` of
  the vararg's element type, rendered in the callee's type frame so `f<String>()` fills `Array<String>`. (An
  annotation's argument vector was never affected: the frontend materializes an explicit empty vararg there, so
  `@A()` on `annotation class A(vararg val xs: String)` already emitted one.) Not a regression of a previously
  working call: the plain-Kotlin arm was refused for as long as the builders have keyed omission on `defaultValue`,
  and the retired facade injection did project a `params` parameter as a `vararg` too, so the .NET arm's candidate
  set is not what changed. `docs/dotkt-semantics.md` §8g records the resolution and its formatting
  consequence. The fill is also one VALUE of the call rather than an expression in a slot: it is an allocation, and
  an allocation is observable through its identity, so where the call carries an evaluation plan it becomes a
  binding like every other argument. Left raw it was re-rendered per reader — a later default naming the vararg
  (`fun f(vararg xs: Int, y: IntArray = xs)`, and the same shape cross-module, where the `@KotlinDefault` carrier
  clones the slot) received a second and a third empty array, so `y === xs` was false where Kotlin's
  evaluate-each-argument-once rule makes it true. Nothing but identity could see it: two empty arrays agree on
  size and content.

- **dll2klib: a .NET value type no longer takes an NRT annotation, or the wrong byte position.** The
  `NullableAttribute`/`NullableContextAttribute` walk treated every named type outside a hardcoded Kotlin-primitive
  list as a reference position, so a struct or enum both consumed a byte the emitting compiler never wrote and
  inherited the declaration's context annotation. `String.Compare(string?, string?, StringComparison)` carries
  `[NullableContext(2)]`, so its enum parameter projected as `StringComparison?`; the descriptor the frontend then
  resolved named a member that does not exist and bir2cir refused the call. The same misalignment shifted every
  later byte in the slot, so a signature putting a bare enum or struct ahead of another node lost or moved its `?`:
  `Dictionary<Grade, string?>`, which csc writes as `[Nullable(1,2)]`, projected as `Dictionary<Grade?, String>`.
  (A bare *primitive* was already excluded by name, so `Dictionary<int, string?>` was never affected.) The walk now
  asks the ECMA signature's own `ELEMENT_TYPE_VALUETYPE`/`ELEMENT_TYPE_CLASS` kind: a bare value type holds no byte,
  a constructed generic value type holds one that is always `0`, `System.Nullable<T>` and byrefs are transparent,
  and a value type is never annotated. `kotlin.Unit` now holds no byte on BOTH ends: the reader had always skipped it
  (it is the name ECMA `void` decodes to, and nothing in a signature tells the two apart) while bir2cir's writer gave
  it one, so `Pair<Unit, String?>` re-imported as `Pair<Unit!, String>` — the writer now skips it too, and
  `docs/dotkt-semantics.md` § 9 states the deviation from what csc would flatten for the `Unit` class.
  `bir2cir`'s `NullableFlags` writes the same flattening from the other side, so an oblivious wrapper now delegates
  to the same walk instead of re-deciding it (`T!` over an array or a byref stopped descending into the node it
  wrapped). Measured against csc, two positions the decoder collapses to `kotlin.Any?` still shift the bytes after
  them — a native `nint`/`nuint`, which the emitting compiler gives no byte at all, and a function pointer, which it
  flattens node by node; an ordinary pointer and a `where T : struct` parameter hold exactly the one byte the walk
  consumes for them. All are named at the predicate. That writer states one precedence for a position reached
  through both markers — oblivious wins, because `T!` is the un-annotated position — and its nullable arm was
  deciding it a second time by dropping the oblivious marker as it delegated, writing `2` where the rule says `0`.
  Both markers now travel together and the rule is resolved in one place. Unlike the byte-COUNT faults above this one
  moves nothing: the reader's traversal comes from the signature, so a wrong byte value mis-annotates its own position
  and only its own. The shape is not reachable from the FRONTEND (its only oblivious producer wraps a made-not-null
  type) but a pass can build it, by substituting a nullable type argument under an `Oblivious(T)` an un-annotated .NET
  generic member left behind; it does not occur anywhere in the current corpus, which is why the witness is
  `tests/ir/lowering/oblivious-over-nullable-byte` — that lane exists for a rule the corpus does not instantiate.

### Changed

- **The reverse enumerator bridge is ordinary CIR authored by `bir2cir` (#139/#400).** A Kotlin class implementing
  `Iterable`/`Collection`/`List` lowers onto a BCL enumerable face that obliges `IEnumerator<E> GetEnumerator()`,
  while the class has only Kotlin's `iterator(): Iterator<E>`. That whole ABI used to be synthesized inside `ilemit`
  from `clrBridgeRole` semantic hints `bir2cir` stamped by Kotlin FQN (`kotlin.collections.Iterator`) and member name
  (`hasNext`/`next`/`iterator`): the emitter minted the `dotkt$EnumeratorOverKotlinIterator<T>` adapter TypeDef with
  its wrapped-iterator field, constructor, `MoveNext`, both `Current` slots, `Reset` and `Dispose`, decided which
  classes qualified from a hardcoded BCL collection-interface set and a reflected hierarchy walk, and wired every
  MethodImpl itself. `bir2cir` now authors all of it as ordinary declarations, bodies and exact `clrInterfaceImpls`
  MethodImpl descriptors, so `ilemit` emits the graph one-to-one. The adapter — which Kotlin source cannot express,
  because `IEnumerator<T>` and the non-generic `IEnumerator` declare two `Current` slots differing only in return
  type — is now emitted once per module and is assembly-private: its CLR identity never appears in a signature, so a
  module-private copy is indistinguishable from one shared out of the runtime stdlib, and the cross-assembly
  `enumeratorAdapterCtorRef` carrier is retired with it. `clrBridgeRole` and `enumeratorAdapterCtorRef` are gone from
  the BIR/CIR schema and its validator; `ilemit` loses `Emitter.ReverseBridge.cs`, the `EnumerableDerived` face
  registry, the `TypeInfo.BridgeRoles` marker registry, the name-based `GetEnumerator` skip in its interface wiring,
  and the eight fixed-member roles (`Enumerable.GetEnumerator`, `Enumerator.MoveNext/Current/Reset`,
  `EnumeratorT.Current`, `EnumerableT.GetEnumerator`, `Disposable.Dispose`, `NotSupportedException.ctor0`) that had
  no other consumer. The runtime standard library's emitted metadata is unchanged except that the synthesized
  members no longer carry `final`/`hidebysig` — matching every other `bir2cir`-authored MethodImpl bridge — and the
  metadata-only reference standard library, which states no BCL enumerable face and therefore owes no bridge, no
  longer carries a dead copy of the adapter. In an application or user-library build the adapter is now an ordinary
  CIR TypeDef when round-trip metadata is stamped, so it carries the same `[NullableContext]` carrier every other
  compiler-authored type does. The change also FIXES a shape the emitter could not reach: a class implementing an
  `Iterable`-derived interface declared in ANOTHER assembly, whose enumerable face the emitter's worklist never
  followed, failed to load with "Method 'GetEnumerator' … does not have an implementation". The final closure also
  resolves `iterator()` supplied by a non-enumerable base class or an interface default, locally and through a
  referenced assembly, and preserves a narrower iterator element behind a two-parameter internal adapter rather than
  constructing the one-parameter adapter at a type its input does not implement. The element is resolved from the
  return type's actual `Iterator<E>` supertype rather than guessed from that type's own generic arguments, covering
  primitive iterators and arbitrary user iterator subclasses; provider selection also retains a physically renamed
  declaration's Kotlin source identity and does not let an inaccessible private base member suppress a selected
  interface default.

- **Which delegate a lambda or callable reference constructs is decided in `bir2cir`, and the Unit/void adapter is ordinary CIR
  (#400).** `ilemit` used to compare the reflected parameter type at each call site with the lambda's own delegate and
  re-wrap the construction when they differed, and — when the slot's `Invoke` returned a value while the Kotlin lambda
  was `Unit`-valued — to author the reconciling adapter itself: a synthesized
  `DotKt.Runtime.CompilerServices.UnitDelegateAdapters` TypeDef, one `Unit$N` MethodDef per conversion, a rewritten
  generic frame with cloned constraints, and a hand-built body. `bir2cir` now marks every construction with the
  delegate its slot declares and, after the last resolution pass, makes the construction state what it physically
  builds: the same delegate (nothing to state), a different bindable one (its `funcType`, `delegateCtorRef` and
  `invokeRef` become the slot's), or — for the void-into-value mismatch — an ordinary `newClosure` over an adapter class
  `bir2cir` authors, which holds the natural delegate and whose `invoke` calls it and returns the `Unit` singleton
  through the same `staticField` node every Kotlin `object` instance is read with. The adapter is generic in the
  delegate's PARAMETER TYPES rather than in the enclosing frame's type variables, so it declares no constraints at all
  and none have to be reconstructed: a delegate family constrains none of its own parameters, so any type legal as
  `Action<X>`'s argument is legal as the adapter's. Bound Kotlin methods, bound CLR methods and CLR static methods now
  take the same declared-slot rule as lambdas; previously their natural `Func`/`Action` delegate could be passed to a
  different custom delegate slot by type-punning. Event subscription remains the stored-handler form the producer
  actually emits, while exact event-forwarder parameters retain their explicit `handlerExact` path. The
  `unitInstanceRef` and `targetDelegateCtorRef` CIR carriers are retired.

- **The body of an un-lowered `suspend` declaration is authored by `bir2cir`, and the Kotlin `suspend` modifier no
  longer reaches CIR (#400).** `ilemit` used to synthesize a throwing body for any declaration that still carried
  `mods.suspend` in a standard-library build, and to refuse one in an application build — Kotlin coroutine policy
  (what `suspend` means, which builds may leave one un-lowered) living in the emitter. The two declarations the
  cold-core lowering deliberately does not lower — the Kotlin surface the stdlib self-build retains beside its cold
  entry, and the inline coroutine primitives whose call sites are reconstructed inline — now get an explicit
  `throw NotSupportedException(…)` body stated as ordinary CIR, derived from the declaration's own facts rather than
  from any function-name list. The modifier itself is dropped once `[KotlinFunction(Suspend)]` has been stamped from
  it, so CIR carries no Kotlin coroutine vocabulary at all, and the shared IR-sanity gate refuses both a surviving
  modifier and, with no exemption left, any surviving `suspendCall`. The runtime standard library's IL is unchanged
  apart from the stub message text; the reference twin's stubs are now the same metadata-only
  `throw NotImplementedException()` every other reference body carries.
- **The read-only view of a mutable collection is stated in CIR, not inferred by the emitter (#400).** A Kotlin
  `MutableList<E>` IS-A `List<E>`, but the CLR faces they lower to — `IList<T>` and `IReadOnlyList<T>` — are unrelated
  interfaces, so an emitted type's read-only view is real only when the type declares it. `ilemit` used to notice a
  mutable collection face on a TypeDef and add the read-only sibling itself, which is a decision about what a Kotlin
  declaration becomes on the CLR and therefore `bir2cir`'s. `bir2cir` now states the sibling (`IList`→`IReadOnlyList`,
  `ICollection`/`ISet`→`IReadOnlyCollection`) in the `interfaces` array of every type that names the mutable face —
  classes and interfaces alike — and `ilemit` emits one InterfaceImpl row per stated entry and infers nothing. The
  relation lives in one shared table (`toolchain/bir-common/CollectionViewFaces.cs`), and the IR-sanity gate both
  tools run now REFUSES a document that states a mutable face without its read-only view, so an omission fails at the
  CIR boundary instead of as an `InvalidCastException` in an unrelated caller. The emitted InterfaceImpl rows are
  identical, in the same order. `bir2cir` authors no MethodImpl for the sibling — its members are the ones the mutable
  face already forced onto the type, which the CLR binds implicitly — but ilemit's own still-implicit interface-slot
  wiring now sees the stated face and emits a redundant explicit MethodImpl binding the read-only `get_Item` slot to
  the same public method that already satisfied it (four rows in the runtime stdlib). That row is semantically
  identical to the implicit binding it restates, projects and re-consumes identically through `dll2klib` and a
  cross-module build, and disappears when the emitter's implicit wiring does.
- **A collection literal's constructed BCL type now comes from the reference that names its constructor (#400).**
  `newList`/`newSet`/`newMap`/`spreadConcat` already carried the exact constructor and accumulator `bir2cir` chose,
  but `ilemit` still built the result type by naming `List`1`/`HashSet`1`/`Dictionary`2` itself — a second, parallel
  decision about which BCL type a Kotlin literal becomes. It now reads that type off the named constructor's
  declaring instantiation, and `forEachInline` likewise takes its enumerator local from whichever `GetEnumerator` it
  emits instead of naming `IEnumerator`. Which enumerator arm the emitter can encode remains its own call — an
  instantiation over a type still being built cannot carry a usable member token, and neither a type variable nor a
  reference resolution decides that from CIR — but it is now made by testing the element type the node already
  carries, so nothing in these five expansions names a BCL type to pick an operand or an owner. A node whose
  element/key/value type cannot be read now fails in `bir2cir`, where the fact is missing, instead of reaching the
  emitter as a construction with nothing to construct. Emitted IL is unchanged, and a mixed vararg spread
  (`f(1, *xs, 2)`) — the only source shape that builds through the spread accumulator, and previously exercised by no
  gate fixture at all — is now covered across primitive, reference, locally-emitted and open element types.

- **A `companion object` of a generic class is now ONE object across every instantiation (#383).** CLR static storage
  belongs to each closed constructed generic type, so the nested carrier every companion received in #275 gave
  `Foo<int>.Companion` and `Foo<string>.Companion` a singleton each — with separate state — while Kotlin's own uses,
  which closed the carrier with the representative `object`, formed a third region that shared with neither. Kotlin
  declares one companion on the class declaration, and that companion does not have the owner's `T` as a parameter of
  its own, so a carrier whose physical owner has any generic slot is now **hoisted out of the owner** to a top-level
  sidecar (`p.Foo$companion$Companion`, with the owner's own nesting path flattened into the name); a non-generic owner keeps its
  nested `p.Host+$Companion`. `ReferenceEquals(Foo<int>.Companion, Foo<string>.Companion)` is true, companion state is
  shared across closed owners, and Kotlin and C# see the same object. The source-name accessor is still an ordinary CLR
  static — a generic owner has one field per instantiation — but every one of them is initialized from that single
  carrier singleton.

  A hoisted carrier shares a namespace with other compiler types derived from an owner's name — notably the
  star-projection existential `<owner>$dotkt_star` — while a companion's SOURCE name is an ordinary Kotlin identifier
  that may be spelled `dotkt_star` too. The carrier name therefore carries a reserved `$companion$` marker, which no
  source name can supply; without it `class Holder<T> { companion object dotkt_star }` used through `Holder<*>` made
  ilemit resolve the owner's members against the companion carrier and abort the emit.

  Moving the carrier out of the owner puts three lexical edges through the caller-side `[UnsafeAccessor]` projection
  that no source had reached before, and each was broken: a private field WRITE crashed bir2cir outright (the load and
  the store were handed the same pointer-call node, and a JSON node has one parent); an accessor-routed `lateinit`
  read produced a node the IR-sanity gate rejected for missing an owner it no longer addresses; and a private field
  whose access named the bare generic declaration (a delegated property's `$delegate`) refused the accessor because
  the owner frame was open — its construction is now recovered from the receiver's static type. Separately, `kotc`
  stamped the *accessor's* Kotlin visibility onto delegated-property reads it had already inlined to the delegate's
  own member, so `private val x by lazy` asked for an `UnsafeAccessor` on `kotlin.Lazy.value` — a public method —
  and failed at runtime with `MissingMethodException`. The stamp now follows the declaration the emitted node
  actually addresses, for every delegate form: `by lazy`, a provider's `getValue`/`setValue`, and the stdlib `Map`
  extension convention, at local, member and top-level scope alike.

  No carrier declares generic parameters any more, so the physical capture slots, the `object` closure every Kotlin use
  site applied to them, and the arity bookkeeping that went with them are deleted rather than kept beside the new
  shape. Hoisting costs the carrier CLR nested access to the owner's `private`/`protected` declarations, including a
  private constructor; the ordinary caller-side `[UnsafeAccessor]` projection restores those edges with no target
  member widened. The trusted `[KotlinCompanion]` payload records the shape as `kind: "nested" | "sidecar"`, and both
  `dll2klib` and `bir2cir` refuse a nested claim over a generic owner, a sidecar claim over a non-generic one, and any
  carrier whose CLR nesting or arity contradicts its kind. The semantic KLIB companion is unchanged, so Kotlin source,
  companion names, visibility, callable references, supertypes and cross-module consumption are unaffected.

- **Function types of 17..22 parameters are now a real cross-assembly ABI (#220).** `System.Func`/`Action` stop at 16
  value parameters, and the wider shapes used to be minted per assembly, so one declared `(…17 Ints…) -> Int` was a
  different nominal type in every module that mentioned it. The six pairs `DotKt.Runtime.CompilerServices.KAction`17`
  …`KAction`22` / `KFunc`18`…`KFunc`23` (Kotlin arities 17 through 22) are now emitted unconditionally into BOTH
  stdlib twins with identical signatures, and every other assembly references them and defines nothing. A wide
  function type is therefore legal wherever a narrow one is: in a parameter, in a return (which previously emitted
  an unverifiable `callvirt` through the consumer's own copy), and
  nested in a generic such as `List<(…) -> R>` (which previously aborted the emit with "no referenced method matches
  the resolved descriptor"). A single producer declaring two wide arities no longer breaks KLIB re-import through
  dll2klib's arity-clash rename, since producers declare no delegate to clash. `dll2klib` restores the canonical
  family to `kotlin.FunctionN` from its ABI-fixed name, as it already did for `System.Func`/`Action`.

  Each canonical delegate is declared variant exactly as its BCL sibling (`KFunc<in T1, …, out TResult>` /
  `KAction<in T1, …>`), so `(Any, …) -> String` remains assignable to a `(String, …) -> Any` slot above arity 16 as
  it is below.

  With one definition to link against, ilemit no longer chooses between a local and a referenced delegate for this
  range: the on-demand synthesis path, the `EmitArg` rewrap exemption for TypeBuilder-backed delegates, and the
  unconditional structural comparison of function-type signature nodes are gone — every fully concrete function
  type, narrow or wide, is now matched by ordinary Reflection identity, and the stdlib is the sole definition site
  with no exception.

  Kotlin function arities of **23 and above have no CLR delegate and are refused** by bir2cir, naming the source
  file and the arity. The limit is the representation's, not the frontend's: the frontend resolves
  `kotlin.FunctionN` for any N, but each arity is a distinct pre-baked type in the stdlib and Kotlin's function
  types are unbounded, so the family cannot grow another row — going further needs a variadic representation. The
  bound is on DELEGATES, so a `suspend` function type is unaffected at any arity: it is an object carrier, not a
  delegate, and 23 suspend parameters compile and run exactly as 2 do. Arities 0..22 are the supported surface,
  recorded in `docs/dotkt-semantics.md` §8e-bis.

- **NRT-only fixed/`params` overload inversions now resolve like C# without compiler or library special cases (#367).**
  For a foreign CLR family whose fixed signature is exactly a `params` overload's physical prefix and whose Kotlin
  views differ only by strict outer nullable-reference narrowing, `dll2klib` retains both declarations, lowers the
  priority of the original nullable view, and adds a non-null metadata view of the fixed physical member. Stock Kotlin
  resolution can then apply its non-vararg tie-break: `Console.WriteLine("{0}")` reaches `WriteLine(string?)` and
  treats braces literally, while supplied or spread arguments still reach the formatting overload. Real nominal
  differences such as `object?` versus `string`, platform and nested nullability, and DotKt-origin declarations are
  excluded. Static, instance, virtual/override, constructor, generic, extension, nullable-argument, arbitrary
  C#-producer, and Kotlin round-trip cases are covered by interop tests.

- **Tests: #227 consolidates eleven redundant NUnit cases into their existing feature owners.** Numeric parsing,
  nullable string rendering, enum APIs, character ranges, preconditions, collection rendering, BCL imports, and
  configured-await shapes now have one authoritative test location each. Assertions that preserve a distinct CLR
  shape—mutable-map rendering, enum `entries`, raw `Math.Max`, fluent `Append(Int)`, and non-generic
  `ConfigureAwait(false)`—move into those survivor methods before the duplicate methods and four single-test
  fixtures are removed. The reviewed discovery baseline moves from 710 to 699 without removing a unique compiler
  path.

- **Tests: #227 finishes replacing opaque fixture collision tokens with feature names.** Remaining shorthand in
  captured-variable, lambda, language-core, default-argument, non-constant-default, and coroutine fixtures now
  states the behavior it isolates; the round-trip default-argument package is likewise named
  `roundtrip.defaultarguments`. Secondary coroutine case tokens are expanded as well, so names distinguish
  conditional/loop control flow, generic/non-generic await, receiver capture, evaluation order, dispatch, and
  suspend-function-value shapes without requiring the retired shell-case map. Cross-file references and
  name-sensitive assertions move with their declarations; discovery counts are unchanged.

- **Tests: #227 replaces migration-batch identifiers with feature-oriented names.** The `M1`–`M5`/`MigM`,
  `CorA`/`CorB`, and `IntropA`–`IntropD` families now use fixture-specific stems across the shared Basic,
  Coroutines, and Interop assemblies. Analogous opaque stems in suspend operand/result/capture, open-generic-slot,
  byref-order, and compile-fail fixtures are expanded as well. Cross-file support symbols, packages, reflection- or
  string-sensitive expectations, diagnostic baselines, and comments move in the same sweep; discovery counts do
  not change.

- **Tests: #227 removes the superseded three-case task lifecycle fixture.** Its genuine asynchronous and completed
  `Task` await paths are both covered by `DynamicCaptureContextTests`, which additionally exercises generic and
  non-generic awaitables under runtime and constant capture policies. Its “finally once” check is covered by
  `SuspendTryLoweringTests.namedNestedTryResumesInKotlinOrder`, whose exact trace proves that a real suspension in
  the protected body resumes before each nested `finally` runs exactly once. The coroutines discovery baseline is
  reconciled from 162 to 159; no unique compiler path or assertion is removed.

- **Tests: the #86 round-trip probes now share the compilation graph their semantics share (#227).** The shell
  lane still runs one process and records one verdict per observable, so a crashing XFAIL cannot hide a later
  result, but it no longer recompiles every tiny source program independently. Fifteen same-module probes compile
  into one dispatched assembly; sixteen cross-module producer groups and thirty-four consumers compile into one
  producer/consumer pair; the one expected compile rejection remains isolated. This reduces the #86 block from
  66 Kotlin compiler starts to 4, and from 65 bir2cir/ilemit pairs to 3, without weakening per-case XFAIL shape
  matching. The NUnit test guidance now makes the governing rule explicit: isolate runtime processes for failure
  attribution, not compiler invocations. The five NUnit suites also pin their reviewed v0.9.8 discovery baseline
  (713 tests total), so future additions and removals must reconcile their count in the same review instead of
  silently growing or shrinking the gate. Compile-fail's thirteen companion C# fixture mappings now share nine
  source files and are assembled once instead of copying fixtures and rebuilding thirteen one-file projects; the
  Kotlin refusal verdicts remain isolated because a compiler error may stop before later sources reach their
  owning phase.

## 0.9.8 (2026-08-02)

### Added

- **The nullable-generic family is now measured at VALUE instantiations, and the C#-visible ABI is measured at
  all (#86).** Every gate that touched `T?` over an unconstrained type parameter drove it at `T=String`, where the
  whole family is invisible — a bare `T?` slot is trivially sound for a reference type — so the representation
  could be wrong in either direction without a single red gate. `tests/basic` gained the `T=Int`/`T=Boolean`
  instantiations of the idioms that work today (`mapNotNull`/`mapNotNullTo`, `filterNotNull`, `chunked`,
  `Sequence.single`/`singleOrNull`/`filter`, `getOrPut` and `merge`'s remove-on-null over a value-typed map value,
  `toTypedArray`/`plus`/`plusElement`, and a top-level `T?` return), which is the regression armor the erasure work
  will be measured against.
  Driving the value axis also showed that several shapes assumed working are not, so they land in
  `tests/roundtrip/scenarios` as documented reds rather than as fixtures. With **no module boundary at all**, a
  null through a top-level `T?` param or a `T?` constructor param faults with `InvalidProgramException` at
  `T=Int`, and an override narrowing a base `T?` slot to a concrete `Int?` faults with `TypeLoadException` — so
  the defect is the representation, not the cross-module carrier, and the comment claiming otherwise is
  corrected. Cross-module, a top-level `T?` **return** re-imports as non-null `Any` and the consumer no longer
  compiles (and that one is not confined to value types); an `Array<Int?>` **param** re-imports as `IntArray`,
  while an `Array<Int?>` **return** re-imports with a non-null `Int` element, so the consumer indexes a
  `Nullable<int32>[]` as an `int32[]` and reads the layout words back as elements — an array of `4/null/8`
  reports `3/1/4/0`, with no diagnostic and no exception; and carrying a **value** — not a null — through a
  nested `Slot<T?>` param or property at `T=Int` corrupts memory in `CastHelpers.Unbox_Nullable`.
  Every one of those is its own section with its own app: a section verdict is one stdout comparison, so an app
  driving several faulty shapes reports one result and the first fault hides the rest. That is not a hypothetical
  — a bundled app made the nested-`Slot<T?>` axis look like param-vs-property-vs-return when it is actually
  present-vs-null, with the whole null path green. Each entry now also pins the SHAPE of its failure (exception
  type, compiler diagnostic, or the wrong value itself) against the section's captured evidence: compiler and
  emitter diagnostics, the app's stderr and exit status, and its stdout. A listed entry that fails for some other
  reason reddens as an `XFAIL SHAPE MISMATCH` instead of absorbing it, and a listed entry with no documented
  shape is rejected outright. The green control sections — the same shapes at a reference instantiation, at a
  non-nullable slot, and on the null path — are load-bearing for the same reason: they are what makes "the value
  axis is the subject" a measurement rather than a claim.
  `tests/packaged-sdk` gained `csharp-consumer`: a real C# Exe that `ProjectReference`s a packaged-SDK Kotlin
  library and binds its emitted CLR signatures **literally**. Every other gate re-imports an emitted library as
  Kotlin, so it measures what the compiler can restore rather than what the ABI is; this one cannot. It reports two
  verdicts — the erased slot's physical type plus its `[KotlinNullableGeneric]` carrier (asserted by reflection
  through a new `refcheck --shape` mode), and whether a C# program compiles and runs against those slots. Both are
  written against the post-erasure ABI and are `XFAIL_PKG`-listed until it lands, and both baseline their exact
  expected failure — the five C# diagnostics and the five slot mismatches — under names that are *not* listed, so
  a missing tool, a restore failure, or a changed or extra diagnostic reddens instead of hiding inside the
  expected red. `refcheck` itself is now generated and built in a staging directory and swapped in atomically,
  keyed on a hash of its actual generated sources, so neither a source change nor a failed rebuild can leave a
  stale tool answering.

- **A stale `sty` stamp is now caught mechanically, right where the stamp dies (area:bir2cir).** The spec §2.7
  invariant — *a pass that changes a node's result type rewrites or deletes its `sty`* — was stated and swept by
  hand once, but nothing caught the next pass that reintroduced the drift, and the stamp is read FIRST by every
  type deriver: a spill local or state-machine field declared from `List<Int?>` while the call actually returns
  `List<object>` is invalid IL, not a diagnosable drop. bir2cir now runs the shared `bir-common/IrSanity` over each
  file's fully-passed BIR immediately before `BirTypeLowering` (which strips `sty`, so this is the last point the
  stamp exists) and refuses a `sty` that names a different type than the `ret`/`dynRet` beside it; the same check
  runs on the CIR in both bir2cir and ilemit, and `scripts/verify-sanity.py` mirrors it offline. The relation is a
  REFUTATION test calibrated on the 442-file stdlib reference + runtime pre-lowering corpus (16,070 stamp pairs)
  and the app corpus: a type variable, a `*`, `kotlin.Nothing`, a `$dotkt_star` existential view, a nullability
  wrapper, a spelling difference between the `kotlin.*`/shorthand/`System.*` vocabularies, and any pair of unlike
  or different-arity shapes all AGREE, and a missing stamp is not a disagreement. Calibrating it found four live
  violators, all fixed here. `ContinuationErasure` promoted a discarded `Result` accessor's `ret` from `Unit` to
  `kotlin.Any` so ilemit would pop the value, and left `sty` at `kotlin.Unit` — restoring for every deriver the
  exact stale `void` hint the promotion exists to remove; it now restamps. The other three are one family, the
  CROSS-MODULE generic erasure the FU-⑧ sweep did not reach: `NullableGenericErasure`,
  `ReferenceExistentialAbiBinding` and `ConstructedMemberReturnSubstitution` each replace a call's declared result
  with the PHYSICAL one — a `Slot<T?>`/`Slot<String>` bound as `Slot<object>`, unrelated invariant reified generics
  — while the frontend stamp still named the pre-erasure instantiation. None can rewrite the stamp (the
  instantiation is not recoverable from an erased owner), so each DELETES it, which is the other thing §2.7 permits
  — but only where the new result REFUTES it, through one shared `NodeType.DropStampIfStale`. That qualifier is
  correctness, not caution: `ConstructedMemberReturnSubstitution` cannot tell a callee-relative `tv` from one kotc
  already instantiated, so it can re-substitute `Map$Entry<K,V>` into `Map$Entry<Map$Entry<K,V>,V>`, and there the
  stamp is the more trustworthy of the two and survives. `IteratorConsumerNormalization` — which retypes a
  `hasNext`/`next` call onto the owner's element, `object` when that element is erased — carries the same
  obligation and now discharges it too. `tests/ir/selftest` pins the check against BOTH implementations and
  `tests/ir/lowering` pins the chokepoint itself, each with the legitimate neighbours beside it.
- **A suspension that escapes the cold lowering is now caught at the CIR boundary (area:bir2cir, area:ilemit).**
  `suspendCall:true` is kotc's frontend fact that a call site suspends, and bir2cir's `SuspendColdLowering` is its
  only consumer — it rebuilds each suspending call as a resume label plus the callee's cold-shape call (a
  `$dotkt_suspend` cold entry, or the awaiter sequence for a `.await()` CLR bridge), out of fresh nodes that carry
  no tag. Until now nothing said so: a suspension that slipped through reached ilemit as an ordinary invocation, so
  the caller read the raw `Task`/`COROUTINE_SUSPENDED` sentinel where the awaited value belonged and the state
  machine got no resume point — an `InvalidCastException` far from its cause, or a silently wrong value. The shared
  `IrSanity` check set (run in-process by both bir2cir and ilemit, mirrored offline by `scripts/verify-sanity.py`
  for `make verify-sanity`) gained the invariant, and the message names the declaration. A METHOD that still
  carries `mods.suspend` is exempt, because ilemit's guard on that exact flag returns before it reaches the
  statement walk; such a survivor is stdlib-only, since the self-build deliberately retains the original beside its
  cold entry and the admit gate excludes the inline coroutine primitives, where an app build removes the original
  outright. Only a method scope can be exempt: ilemit emits a constructor body, and builds a type initializer from
  the fields, without consulting the flag at all, so a ctor and a static-initializer group are always checked —
  deriving the exemption from the scope's declaration instead of its kind would have let a suspension through a
  `.cctor` under a type that carried the modifier. Calibrated against the current corpus: all seven survivors in
  the runtime stdlib CIR sit in exempt declarations, no emitted body carries one, and the app corpus has none.
- **The IR sanity gate gained a self-test lane, and the schema gate a granularity fixture
  (`tests/ir/selftest/`).** Both gates validate whatever is on disk, so one that stopped checking would look
  exactly like a clean corpus. `tests/ir/run-sanity.sh` now runs the directory's `*.cir.json` half first
  (`run-schema.sh` keeps the `*.bir.json` half), pinning both the suspension invariant above and the
  `mods.suspend` exemption it is calibrated against — neither has a natural negative in the corpus. On the schema
  side, `accept-unplanned-suspension-operand.bir.json` pins the §2.7 granularity rule for the shape the
  suspension work touches: a call whose operand merely suspends acquires no second reader, so `h(f(), 1)` is
  plain BIR with no `callEval` around it. Where a suspension is planned is bir2cir's decision, and a BIR-side
  rule requiring one would make the emitter's own legal output illegal — the shape is ordinary emitter output,
  present in the hundreds across the stdlib and coroutine corpora, so such a rule would redden real builds and
  not just the fixture.
  The sanity lane asserts each fixture against BOTH implementations, not only the offline mirror: the normative
  checker is the C# `IrSanity` compiled into ilemit, and until now nothing exercised it — a check deleted there
  would have left every gate green. Both lanes also now fail when they discover no fixture of either kind, and
  when a `reject-*` case has no expectation text (an empty one matched any message, degrading the assertion to
  "exited non-zero").

- **Cross-target (target RID != host RID) reference-asset selection is now covered by the gate
  ([tmyt/dotkt#192], area:ilemit, area:packaging).** `tests/msbuild/run.sh` gained
  `ktproj-crosstarget-rid-assets`: it builds a throwaway RID-implementation package from
  `tests/msbuild/rid-probe/` and runs a real `dotnet build -r <rid>` for a RID derived to differ from the host,
  asserting that ilemit loads the `runtimes/<rid>/lib` asset of the TARGET RID — on an exact-RID hit and through
  the RID fallback chain (`win-x64` to `win`, `linux-x64` to `unix`), under both the portable RID graph and the
  built-in chain. The package's RID-neutral placeholder omits a member its compile surface declares, so a wrong
  selection is a red build rather than a subtly different program; the scenario additionally replays the emit at
  the host RID and requires that replay to fail, so the assertion cannot pass vacuously. Previously the RID flow
  was only exercised at the host RID and cross-target selection had been confirmed by hand.
- **A negative-compile gate (`tests/compile-fail/`, wired into `make verify-tests`).** Some behavior is only
  expressible as a REFUSAL — source the compiler must reject, with the message it owes the author. Each case is a
  `.kt` plus an `.expected` list of substrings the diagnostic must contain; the lane is green iff every failing
  case is listed in its (currently empty) `CF_XFAIL` baseline, and reports NEW-FAIL/FIXED like the other gates.
  It opens with the eight byref-like storage refusals below.

### Removed

- **facadegen (area:toolchain): remove the retired CLR-to-FIR injection tool.** The production frontend now
  consumes one standard metadata-only KLIB per resolved CLR reference assembly through `dll2klib`, so the old
  import-seeded JSON projection has no build, package, test, or developer entry point. The `facadegen` project,
  `make facades`, and `scripts/gen-facades.sh` are deleted; `make toolchain` now builds only the shipping tools.

### Fixed

- **bir2cir: a suspend override narrowed to a value instantiation now fills both final CLR slots (#344).**
  `override suspend fun accept(x: Int?)` against `Sink<T>.accept(T?)` used to receive one private MethodImpl
  bridge while the declaration was still in its logical Kotlin shape. Suspend lowering then deleted that target
  and replaced it with a public `Task<String> accept(Nullable<Int>)` plus a continuation cold entry, leaving the
  old `accept(object): String` descriptor behind; the CLR rejected the implementing type at load. Suspend lowering
  now carries the source override ownership onto both generated declarations under their final names, and the
  erasure-slot bridge pass runs after that expansion. It emits one exact bridge for the public Task obligation and
  one for the cold obligation, while ilemit continues to consume resolved MethodImpl descriptors one-to-one.
  The round-trip fixture drives present/null values through the interface and the concrete signature, and its stale
  RT_XFAIL entry is removed.
- **ilemit (area:ilemit): a GENERIC slot on a referenced supertype is wired at a locally emitted type argument
  (#86).** A referenced generic supertype instantiated at a locally emitted type argument
  (`class C : RSink<Local>`) is a `TypeBuilderInstantiation` whose members cannot be reflected, so ilemit
  enumerates the OPEN definition and re-anchors each slot's signature onto the instantiation. That re-anchoring
  substituted every generic parameter positionally against the OWNER's type arguments — but a method's own type
  parameters are generic parameters too, numbered from zero in their own scope, so `interface RSink<T> { fun <U, V>
  put(x: T, u: U, v: V): String }` had `U` rewritten to the owner's first argument and `V` indexed past the end of
  a one-element list, and the emit died with `ilemit: Index was outside the bounds of the array`. Only
  OWNER-declared parameters are positions in the owner's argument list now; a method type parameter is left as
  declared, which is what the method being matched still states.

  Three further things had to be true before such a slot was actually filled, each independently measured. The
  base-CLASS arm resolves its slot from the erasure bridge's own `clrBaseImpls` descriptor, which states the
  parameter vector in the BRIDGE's vocabulary — so a method-scope type variable in it is one of the bridge's own
  type parameters. That pool was not in scope while the descriptor was resolved, so the resolver fell back to the
  enclosing TYPE's parameters by position and produced `object` on a non-generic owner; the vector then matched no
  member of the base and the emit refused outright with "does not resolve to exactly one method of that signature".
  A slot DEFAULTED by an emitted sub-interface got a forwarding bridge whose signature named `!!0`/`!!1` while the
  bridge itself declared no type parameters — a methodimpl the CLR rejects, so no implementer of that sub-interface
  loaded; the bridge now declares them and mirrors the slot's variance and constraints. And the parameter vectors
  were compared by NAME, which a method type parameter does not have across two declarations: `override fun <X, Y>
  keep(...)` filling `<U, V> keep(...)` is the same slot spelled differently. Method-scoped parameters now compare
  by position, which is what the CLI signature encodes; since a `GenericTypeParameterBuilder` reports neither a
  declaring method nor a declaring type — identically so for a type's parameter and a method's — the emitter keeps
  its own registry of which emitted parameters belong to a method.

  Element-wrapped positions were rebuilt in the same substitution while it was being corrected: `T[]`, `T&` and
  `T*` are neither generic parameters nor generic types, so they used to survive re-anchoring in their OPEN form —
  a signature the instantiation never has.

  Every implementer in the four new `tests/roundtrip/scenarios` witnesses RENAMES the slot's type parameters, so a
  name comparison cannot stand in for the position; each declares two method type parameters over a one-argument
  owner so the out-of-range half is exercised and not only the mis-match; and a non-generic slot on the same owner
  at the same instantiation is the control that says the method's type parameters are the variable.

- **bir2cir (area:bir2cir): the uninhabitable-slot crossing is refused at the IMPLEMENTING position too, on any
  provenance (#86).** A call is not the only way to meet a slot no Kotlin expression inhabits: a Kotlin class can
  DERIVE from a .NET type that declares one. `class C : ITake` for a C# `interface ITake { string Take(List<int?>
  xs); }` compiled clean and died at load with "Signature of the body and declaration in a method implementation do
  not match" — the abstract base twin with "does not have an implementation". The carrier machinery that repairs an
  erased slot reads DotKt metadata, so a PLAIN BCL or third-party supertype has nothing for it to read and that
  whole provenance fell through. bir2cir now asks the REFLECTED declaration, which every referenced assembly has,
  and refuses at compile time naming the deriving type, the supertype and the slot. Filling the slot from the
  reflected signature would not have worked: no Kotlin type states that position, so the body could not name the
  value it is handed and the mismatch would move out of load time and into the body.

  The slot is looked for over the WHOLE supertype graph and includes accessors. Reflection does not hand a derived
  interface its base's members, so `class C : IDerived` where the slot is on `IBase` still reached the load-time
  failure; and a C# `List<int?> Items { get; }` is a `get_Items` marked SpecialName, so a Kotlin property override
  emitted the mismatched slot and died at load too. The walk is `SupertypeGraph`, shared with the override-slot
  bridge rather than copied — two copies of it are what said opposite things about a delegate parameter before —
  and it crosses provenances, so a .NET slot reached only through a Kotlin interface declared here is reached.

  And it fires only where THIS type is obliged to fill THAT slot. Refusing every inherited crossing rejected
  programs with a perfectly good lowering: a Kotlin `interface KI : ITake` and an `abstract class KA : BTake()` are
  not instantiable and emit no body, and an abstract slot some .NET type in the chain already implements is nobody's
  obligation — including where the implementation is an EXPLICIT one, whose CLR member name is qualified with the
  interface and so looked like a different member entirely. A concrete virtual is now refused only where this type
  actually overrides it, matched against the signature the override would physically state — the slot's erased image
  — rather than by name and parameter count, which refused a Kotlin `override fun Take(s: String)` for an unrelated
  `Take(List<int?>)` sibling.

  Two things that image has to be asked carefully. It is compared in the frame the deriving type CONSTRUCTS the
  supertype in: reflection hands back `GBase<T>.Put(!0, List<int?>)` while a `class C : GBase<String>()` states
  `Put(String, List<object>)`, so an open comparison disagreed at the type variable and let the uninhabitable
  override through. And the image is itself an ordinary .NET signature that a sibling slot may state OUTRIGHT — a
  real `Take(List<object>)` beside the `List<int?>` one — so `override fun Take(xs: List<Int?>)` and
  `override fun Take(ys: List<Any?>)` emit ONE CLR member and only the second legitimately fills a slot. Which
  source slot a body belongs to is answered by the fact this erasure recorded on the declaration (its pre-erasure
  Kotlin type on `[KotlinNullableGeneric]`, present at exactly the positions the erasure moved), not by whether some
  other slot happens to state the same physical signature: deciding it that way let ANY body of that shape off, and
  the CLR then bound it to the `object` slot while a call through `Take(List<int?>)` ran the base implementation —
  a silently wrong answer, which is the outcome this refusal exists to prevent.

  That record is READ, not counted. `List<Boolean?>` records its pre-erasure type exactly as `List<Int?>` does and
  erases to the same `List<object>`, so a body that legitimately fills a DotKt supertype's `Take(List<Boolean?>)`
  answered for a foreign `Take(List<int?>)` slot it never mentions — the same conflation one level in, refusing a
  program with a perfectly good lowering. The record is decoded and compared against the type the crossing slot
  states, at exactly the positions the erasure moved: every other position survived physically and is already
  matched exactly, and at a moved position the Kotlin name and the CLR one are read as the same type through the
  stdlib's own `@ClrTypeAlias` (`kotlin.Int` IS `System.Int32`) rather than through a second correspondence
  invented here. The minted attribute is matched by its exact `DotKt.Runtime.CompilerServices` FQN, so no
  similarly-named attribute from another assembly can answer for it. Only PARAMETERS are asked: a return-position
  crossing cannot pose the question, because two members differing only in return type are two CLR slots but one
  Kotlin declaration, which the frontend rejects outright.

  And the record now REACHES that reader in the runtime stdlib build, which mints no attribute and dropped the raw
  stash before the check — so the whole concrete-override arm of the refusal was blind there and its safety rested
  on the runtime corpus happening to contain no such supertype. The stash is consumed by its last reader instead —
  the crossing check itself, exactly as that check already consumes `memberRet` — which is why bir2cir now writes
  no CIR file until every file has been lowered and checked. (Measured: 176 slot records reach the check in that
  build, against none before.)


- **bir2cir (area:bir2cir): a supertype edge's erased argument is CARRIED, closing a Kotlin source break (#86).**
  `class E : Sink<Int?>` erases its edge to `Sink<object>`, and a supertype is the one erased position with no
  declaration slot to hang a per-slot carrier on — so a separately compiled consumer re-imported `E : Sink<Any?>`
  and `val s: Sink<Int?> = E()` stopped compiling. Member carriers cannot repair it: every member's own slot is
  already exact, and what was lost is the identity of the EDGE. Internal shapes are free to break; Kotlin source
  compatibility is not, so the pre-erasure edges (and the type's own type-parameter bounds, which erase the same
  way) now ride a type-level `[KotlinSupertypes]` carrier in the same opaque TypeNode encoding every other carrier
  uses. `dll2klib` restores the edges by HEAD rather than by position, because the projected supertype list is not a
  transcription of the metadata's — it drops non-generic shadows, collapses the `IComparable` bridge and synthesizes
  `kotlin.Throwable`/`kotlin.Any` edges — so only the arguments move and every one of those decisions is kept. Only
  the edges the erasure actually MOVED are carried, so an untouched one is not rebuilt from the carrier and keeps
  whatever the projection decided about it.

  The `bounds` member is now produced and consumed, where it was a documented payload with neither. The producer
  looked for a singular `bound` key on a type parameter, and kotc writes `constraints` (a list — Kotlin allows
  several upper bounds), so nothing was ever recorded; and `RestoreErasedSupertypes` read only `base` and
  `interfaces`. So `class Box<T : Sink<Int?>>` re-imported with NO bound at all: `Box<BadSink>` compiled and then
  died at LOAD with the CLR's wording, on a line the author never wrote. Both ends are wired, and the bad type
  argument is now refused by the frontend against the Kotlin bound the author declared. Two limits, both measured
  and recorded in `docs/design-kotlin-metadata-attributes.md`: a METHOD's type-parameter bounds are not on this
  carrier (it is type-level), and a class bound the erasure never moved is still lost, because a CLASS type
  parameter's CLR constraint is not projected at all — a gap older than this erasure, which the non-erasing
  reference control fails on identically.

- **bir2cir (area:bir2cir): every node resolved against a .NET member states that member's declared type, and the
  build now asserts it (#86).** Two families were silently outside the crossing refusal: a GENERIC .NET method,
  whose parameter descriptor comes from the frontend so it never entered resolution at all, and a genuine public CLR
  FIELD, which is read through `ldfld` and was marked `member: "field"` without its type ever being stated. Each read
  as `List<object>` and left a `List<Nullable<Int32>>` on a stack typed as the unrelated Kotlin form. Both stamp now,
  `void` is written explicitly so an omission and a void member are distinguishable, and a CHOKEPOINT after
  resolution refuses a node carrying a resolved parameter vector — or a kind only resolution produces — without a
  declared return. The next omission of that shape fails the compiler instead of a review.

  Unknown is no longer spelled as a genuine `void`. A generic method the resolver could not narrow by name, generic
  arity and parameter count was stamped `void` — which satisfied the chokepoint, since a stamp WAS made, while
  telling the refusal there was no declared return to object to. A C# `List<int?> Make<T>(int)` beside a
  `string Make<T>(string)` therefore passed both, and emission — which links by the exact `memberSig` the frontend
  resolved — picked the right overload and handed back a `List<Nullable<int32>>` consumed as a `List<object>`. The
  return is now resolved THROUGH that same `memberSig`, by the unique-match discipline every other member here uses
  — including ilemit's own fallback to the implemented interfaces, without which a `string Make<T>(string)` on the
  class answered for the `I.Make<T>(int)` the emitter actually links. What remains genuinely unreadable is stamped
  as unresolved rather than as a type: this pass reads the REFERENCE stdlib and ilemit the runtime one plus this
  compilation's own emitted types, and a suspend cold entry synthesized here is in neither reference set, so
  refusing those would be a backend abort on source the frontend accepted. The chokepoint accepts that stamp and
  the refusal reads it as nothing to check, which is what actually happened.

- **bir2cir (area:bir2cir): a `::fn` moves the declaration it NAMES, not every declaration of that name (#86).** A
  callable reference bound into a slot the erasure object-stated moves the target's own slot to match — the one
  place a declared signature is decided by a use — and the demand was keyed on the target's NAME, so every same-name
  sibling moved with it. `::handle` naming an `Int?` overload silently retyped an unrelated
  `fun handle(x: CharSequence)`, and a generic one produced `ilemit: method …Kt.handle not found`: an emit abort on
  frontend-accepted source, or a PUBLIC Kotlin signature changed by a use that never mentions it, with no
  round-trip carrier to say what it used to be. The demand is now keyed by the frontend-resolved signature the
  reference resolved to, which BIR already carries on the node; a compiler-minted lifted target, whose name is
  unique by construction, still matches by name alone. Method generic ARITY is part of the CLI signature and the
  reference does not carry the target's, so a demand that still matches two declarations moves neither — the
  malformed delegate that results fails loudly at emit, where moving both silently rewrote a public signature the
  reference never mentions.

- **bir2cir/ilemit (area:bir2cir): the referenced override arm relates the marker to the supertype, and one body per
  slot (#86 D3).** The marker's owner established only that the supertype was external; the lookup then ran against
  EVERY reachable spec of the same erased shape, so `class Two : A<Int>, B<String>` — two `accept` overloads that
  both erase to `accept(object)` — wired both slots to one bridge and cast a `string` into a `Nullable<int32>` at run
  time. The marker's owner must now be reachable FROM the spec, and the bridge is keyed by the declaration it
  forwards to as well as by the slot, so the sharing that is correct (one body reached through several supertypes of
  one shape) still collapses to one while these two do not. A slot reached through two sub-interfaces is wired once,
  which is all the CLR accepts.

- **ilemit (area:ilemit): a referenced generic base at a locally emitted type argument is linked, not dropped
  (#86 D3).** `class C : Base<LocalType>` produces a `TypeBuilderInstantiation`, whose `GetMethods()` throws; the
  base path treated that as absence and continued silently — leaving an abstract slot to fail type-load later, or a
  concrete virtual slot dispatching to the base body so the override never ran. It now enumerates the open
  definition and re-anchors, exactly as the referenced-INTERFACE path already did for the same reflection shape, and
  a resolved MethodImpl that still cannot be linked FAILS LOUD rather than vanishing.

- **bir2cir (area:bir2cir): a foreign generic RETURN is refused like a foreign parameter (#86).** The crossing check
  read the node's own `ret`, which is the CALLER's Kotlin view and has already been erased as a Kotlin slot — so a
  C# `List<int?> Make()` or a `List<int?>` property read as returning `List<object>`, was not refused, and left a
  `List<Nullable<Int32>>` on a stack typed as the unrelated Kotlin form (ilverify StackUnexpected; no diagnostic).
  Resolution now stamps the FOREIGN declared return beside `memberSig`, which is the same channel the parameters
  already had, and the refusal reads that. It is a pass-to-pass fact the check strips, so no new key reaches CIR.

- **bir2cir (area:bir2cir): the crossing refusal sees every node a .NET declaration is stamped on (#86).** The kind
  set was assembled from the passes that happened to ask, and omitted `newBoundClrDelegate`, the event accessors and
  accessor-backed external fields — so `netObj::Use` where the target takes `List<int?>` built a delegate whose
  parameter was Kotlin's `List<object>`, and the descriptor was re-erased in Kotlin's vocabulary until the member no
  longer resolved at all. The sets in `ClrBoundNode` are now read off `ClrMemberResolution`'s own switch, and the
  refusal keys on the STAMPED declaration (`memberSig`/`memberRet`) rather than on a kind list, so it cannot drift
  from where the stamping happens.

- **bir2cir/ilemit (area:bir2cir): the referenced-supertype bridge covers accessors, inherited slots, base classes
  and generic arity (#86 D3).** The first cut answered exactly one shape — an ordinary method declared directly on
  the referenced interface — and each sibling failed its own way:
  a PROPERTY marker names the Kotlin property (`v`, kind `getter`) while the slot is `get_v`;
  a slot declared one level UP (`class C : Derived<Int>` where `accept` is on `Sink`) was skipped, because the arm
  required the override's marker to name the reachable spec, and the MethodImpl has to name the DECLARING interface;
  a referenced abstract BASE CLASS produced a `clrBaseImpls` descriptor the emitter refused outright, aborting the
  emit;
  and two members differing only in method generic ARITY shared one bridge, which the CLR rejects
  (*Signature of the body and declaration in a method implementation do not match*).
  The walk now continues through the REFERENCED supertype graph, so each declaring owner is reached as a spec of its
  own; accessor markers are translated the way the declaration rename translates them; arity is part of both the
  bridge identity and the descriptor; and a base class declared elsewhere is wired by resolving the real slot
  through reflection, exactly as the referenced-INTERFACE path already does. ilemit additionally wires a referenced
  interface's own BASE interfaces from the resolved directive — reflection's `GetMethods()` on an interface does not
  include them, so those slots were never even visited.

- **bir2cir/ilemit (area:bir2cir): the override-slot bridge reads a supertype declared in ANOTHER assembly (#86
  D3).** The bridge walked only the current compilation's supertype graph, so a class implementing a generic
  supertype from a referenced assembly — the STDLIB included — got no bridge and the base's erased slot went
  unfilled: the type failed to LOAD, which no verification pass reports and only running the code catches. Carrier-
  argument erasure made that reachable from ordinary source: `class Cmp : Comparable<Int?>` erases the supertype
  ARGUMENT (to `IComparable<object>`, which the lowering collapses onto the non-generic `System.IComparable`) while
  the override's own parameter is a DIRECT slot and correctly keeps its `Nullable<int32>`, so nothing filled
  `CompareTo(object)` — `System.TypeLoadException` on the first use.
  A referenced supertype is now answered by the same D1 carrier reader every other referenced-declaration derivation
  uses. The two arms ask from opposite ends, because that is what each side can answer: a LOCAL supertype hands over
  its slot list, so the walk goes slot -> implementer; a REFERENCED one does not, but every override that must fill
  a slot names its owner and member in its own `overrides` marker, so the walk goes implementer -> slot and asks the
  reader for exactly the member the author said they were overriding. The MethodImpl names the CLR slot rather than
  the Kotlin member (`kotlin.Comparable.compareTo` fills `System.IComparable.CompareTo`), resolved through the same
  `@ClrIntrinsic` binding the declaration rename reads. ilemit consumes that directive on a REFERENCED interface
  too: its external-interface wiring searched for a body NAMED like the slot, and a bridge is deliberately named
  nothing of the sort, so the bridge was emitted and then never wired.
  Prunes `roundtrip-nullable-vt-generic-override-crossmodule-base`, the red PR5 documented for exactly this gap.

- **bir2cir (area:bir2cir): the foreign-crossing refusal stops rejecting `Func<int?, string>` (#86).** The refusal
  that guards a .NET declaration Kotlin cannot inhabit carried its own copy of the position walk, and the copy
  disagreed with `Erase`: it called a delegate PARAMETER an argument, where the erasure calls it a slot. A .NET
  `Use(Func<int?, string> cb)` — a signature an ordinary Kotlin lambda fills exactly — was therefore refused, which
  is a compiler abort on accepted IR. The walk now lives beside `Erase` as `ErasureWouldMove`, its arms readable
  against `Erase`'s position for position; `IsClrBoundKind`'s three copies (which were NOT the same predicate — two
  listed the property accessors, one did not, and every comment claimed agreement) collapse into `ClrBoundNode`,
  which states the CALL/ACCESS split once. Both halves of the boundary gained a live witness, because neither kind
  of test can see the other: a compile-fail case pins the message for the genuinely uninhabitable `List<int?>`
  against a real C# surface (the lane learned to build a `.cs` companion — a refusal ABOUT a foreign declaration has
  no Kotlin-only witness), and an interop battery RUNS the shapes that are inhabited.

- **bir2cir (area:bir2cir): the crossing refusal stops offering a remedy that does not exist (#86).** It suggested
  building the .NET collection explicitly and passing it; `System.Collections.Generic.List<Int?>()` written in
  Kotlin erases its own argument the same way and produces a `List<object>`, so that route ends where it started.
  The message now names what does move — a different .NET surface, or keeping the value on the .NET side — and the
  source break is recorded in `docs/dotkt-semantics.md` §9c-bis beside the `int?[]` one.

- **bir2cir (area:bir2cir): `X?` in a reified argument is `System.Object` in every position, closing the split a
  concrete `Array<Int?>` left open (#86).** `Nullable(Tv)` was `object` everywhere while a CONCRETE `Int?` kept its
  `Nullable<int32>` in a type argument, so one Kotlin type had two physical forms by POSITION: `Array<Int?>` was an
  `object[]` (D2) but `List<Int?>` was an `IReadOnlyList<Nullable<int32>>`, and a generic carrying an element across
  that boundary could satisfy one end or the other and not both. `fun f(xs: Array<Int?>): List<Int?> = xs.toList()`
  had to instantiate at `object` — no other instantiation's `!!0[]` parameter accepts an `object[]` — and then
  returned a `List<object>` into a slot the emitter resolved as `IReadOnlyCollection<Nullable<int32>>`, which threw
  `EntryPointNotFoundException` at the first member call; the same split reached from the array side
  (`Array<Int?>.plus(Collection<Int?>)`) threw `InvalidCastException`.
  The erasure is now POSITIONAL and uniform: a direct concrete `V?` slot keeps `System.Nullable<V>`, and a
  possibly-value `X?` used as an ARRAY ELEMENT or as an actual argument to a CLR-reified construction — type,
  method, or delegate — is `System.Object`. `List<Int?>` is an `IReadOnlyList<object>`, `MutableList<Int?>` an
  `IList<object>`, `Box<Int?>` a `Box<object>`, `Comparable<Int?>` an `IComparable<object>`, `f<Int?>(…)`
  instantiates at `object`, and `(Int?) -> String` is a `Func<object, string>`; `List<String?>` and `Array<String?>`
  are unchanged, because a reference `?` is not a physical difference on the CLR. Generics are INSTANTIATED at the
  erased argument from the start rather than built wrongly and cast — no cast joins two instantiations of one
  invariant reified generic — and the pre-erasure Kotlin type rides the same `[KotlinNullableGeneric]` carrier every
  other erased slot does, now including concrete arguments, so a separately compiled consumer restores `List<Int?>`
  and types its own use as `Subst(Erase(declared))`.
  A DELEGATE's target follows the delegate slot it fills, in the direction ECMA-335 II.14.6 requires of each
  position: every parameter the slot states as `object` (contravariant — only `object` is assignable from `object`,
  so a `(T?) -> String` at `T = String` moves too), and a value/`Nullable`/type-variable return (covariant — a
  reference return already reaches `object` and stays, which is the #189 rule). A delegate INVOCATION is now an
  argument position like any other, so `f(3)` on a `(Int?) -> R` gets the value-nullable wrap a direct call has
  always had instead of pushing an `int32` into a `Nullable<int32>` slot — an `InvalidProgramException` before the
  first instruction.
  ONE POSITION IS SCOPED OUT and recorded as such in `docs/dotkt-semantics.md` §9c-bis: a delegate PARAMETER keeps a
  CONCRETE `V?`, so `(Int?) -> String` stays a `Func<Nullable<int32>, string>`. A delegate's target may be a member
  the author DECLARED (`::handle`, `expr::member`), and erasing the parameter leaves only two wrong answers — rewrite
  that member's public signature by a use of it, or emit a delegate no target can fill and read a struct's bits as a
  reference at run time. Closing it means synthesizing a forwarder at the reference, which is the same missing piece
  the RETURN's inherited move needs; the two land together.
  Prunes both pinned roundtrip sections (`-array-to-collection`, `-collection-to-array`) and five ilverify entries:
  the three value-element base-view findings (`copyOfGrowsWithNullTailAtValueElements`, `boxedGenericValues`,
  `arrayOfNulls`), the cross-module `nullableGenericMembersRoundTrip` delegate findings, and #324's
  `nullableGenericCollectionArgKeysOnTheReceiver`. The `Enumerable.Cast<object>` receiver conversion that entry
  tracked no longer fires for a nullable element — a Kotlin `List<Int?>` is now an `IReadOnlyList<object>`, which
  already IS an `IEnumerable<object>` — and is narrowed to the shape that still needs it: Kotlin covariance over a
  NON-nullable value element (`List<Int>` IS an `Iterable<Int?>`, and an `IReadOnlyList<int32>` is not an
  `IEnumerable<object>`), judged PER POSITION against a slot the wrap can actually fill, which is
  `kotlin.collections.Iterable<T?>` and nothing else. Filling a `List<T?>` slot with it was #324. What remains is the
  REFERENCE half of the same
  question — an open `Array<T?>`/`List<T?>` is `object[]`/`IReadOnlyList<object>` T-independently while a concrete
  `Array<String?>` keeps `string[]` — carried as the one surviving `ArrayTests::copyOfGrowsWithNullTail` entry.

- **bir2cir (area:bir2cir): a .NET member declaring `G<int?>` is REFUSED at the crossing, not silently mis-typed
  (#86).** With `X?` in a reified argument physically `System.Object`, no Kotlin type's form is
  `List<Nullable<int32>>` — but a .NET API may declare one, and a resolved foreign declaration is authoritative
  (the erasure never restates what a CLR member declares). `List<object>` and `List<Nullable<int32>>` are unrelated
  invariant reified generics with no conversion between them, and adapting by copying would change the argument's
  identity and mutation semantics, so the call is refused with the member, the slot and both shapes named. A DIRECT
  `Nullable<V>` parameter or return is untouched — a Kotlin scalar `Int?` IS a `System.Nullable<int32>`.

- **bir2cir (area:bir2cir): the erasure's overload-collision refusal now covers CONSTRUCTORS and nested arguments
  (#86 §5.3).** `f(List<Int?>)` beside `f(List<Boolean?>)`, and `Bag(List<Int?>)` beside `Bag(List<Long?>)`, are
  distinct Kotlin declarations and one CLR signature each — whichever the emitter binds wins every call and the
  other is unreachable. Both are now refused, naming both source signatures as written (recovered from the
  pre-erasure carrier) and the one signature they collapse onto. SUPERTYPE EDGES and GENERIC CONSTRAINTS are
  deliberately NOT checked: two edges can only collapse if they are two instantiations of one head, which the Kotlin
  frontend already rejects, so a backend check there would be unreachable code pretending to be a safety net —
  pinned as `tests/compile-fail/NullableGenericInterfaceCollision.kt`.

- **bir2cir/ilemit (area:bir2cir): an override that narrows a base `T?` slot now keeps its own signature, and a
  bridge fills the slot (#86 D3).** `class IntSink : Sink<Int>` overriding `Sink<T>.accept(x: T?)` has to satisfy the
  base's erased `accept(object)` — the erasure belongs to the declaration, not to the type argument — while its own
  declaration is `Int?`, which erases to `Nullable<int32>` like any other concrete `Int?`. Both were true and only one
  was emitted: the override's signature was rewritten to the base slot, so the type loaded and dispatched through the
  interface, but its Kotlin surface and its physical members permanently disagreed. A separately compiled consumer
  type-checks against the re-imported `accept(x: Int?)` and then aborts with *no referenced method matches the
  resolved descriptor `IntSink.accept(nullable:System.Int32)`* — a missing MEMBER, which no amount of argument
  re-derivation can reach — and a C# consumer saw a parameter the declaration never named.
  The override now keeps `accept(Nullable<int32>)` and a compiler-generated PRIVATE bridge with the slot's exact
  signature carries the MethodImpl and forwards to it across the `object` seam, virtually, so a further-derived
  override is still what runs. One bridge — exactly one, since the covariant-return synthesizer now yields any slot
  whose divergence is this erasure rather than bridging it a second time with a non-virtual forward — fills every
  slot of that shape the supertype graph declares: the constructed interface, its own base interfaces (including a
  synthesized `G$dotkt_star` existential view), and the base-CLASS chain, which is wired by different CLR metadata
  and had no path at all before. That graph is the COMPILATION's: a base declared in a referenced assembly is still
  not indexed, so its narrowed override still fails to load — a pre-existing cross-module reader gap the erase-in-
  place design had too, now measured as `roundtrip-nullable-vt-generic-override-crossmodule-base` instead of being
  stated away. Private is what keeps it
  off the Kotlin surface: dll2klib projects public and protected members only, so the re-imported type carries the one
  declaration the author wrote, where a public bridge would appear as a second `accept(x: Any?)` overload and make
  `IntSink().accept("s")` compile. The one position that still moves the declaration is a `T?` NESTED in a constructed
  generic (`Box<T?>` overridden as `Box<Int?>`), where `Box<object>` and `Box<Nullable<int32>>` are unrelated
  invariant reified generics and no forwarding body exists; its Kotlin type rides `[KotlinNullableGeneric]`, so the
  surface survives the move. A difference this erasure did NOT create — a covariantly narrowed return over an
  otherwise-exact signature — is left to the pass that owns it.
  Two carrier facts had to become true for the private bridge to be genuinely invisible, and both were latent holes.
  dll2klib deliberately re-surfaces a private MethodImpl body under the interface member's name — a class that
  satisfies a slot only privately would otherwise re-import still carrying the abstract obligation — and it
  de-duplicates that against the class's public functions by signature, so the bridge now carries its slots' Kotlin
  types and de-duplicates away instead of appearing as a second `accept(x: Any)` overload. And a
  `[KotlinNullableGeneric]` carrier whose outer `?` sits over a VALUE type now keeps that `?` in the carrier rather
  than delegating it to the slot's NRT byte: an NRT byte array describes reference nodes only, so a stripped `Int?`
  came back as a non-null `Int` and the re-imported member did not exist. Pins: the cross-module round-trip sections for both
  entry points at an interface and at a base class, and `tests/basic` fixtures for the same at both, one level
  deeper, and at a reference instantiation — the failure mode is a `TypeLoadException` at type LOAD, which ilverify
  does not see.

- **bir2cir (area:bir2cir): the overload-collision refusal names both declarations as the author wrote them
  (#86 §5.3).** The refusal for two declarations that erase to one CLR signature printed the half whose `?` rides the
  NRT byte rather than the `[KotlinNullableGeneric]` carrier as `System.Object` — naming a declaration that is not in
  the file, in the one message whose whole job is to say which of two declarations to change. It reads the byte back
  now and prints `Any?`; the DIFFERENTIAL that decides whether to refuse at all deliberately does not, because a
  reference `?` was never a CLR distinction and restoring it there would refuse pairs like the stdlib's own
  `contentDeepEquals(Array<T>, …)` / `contentDeepEquals(Array<T>?, …)`, which have emitted as one signature since
  long before this erasure existed. Pinned in both directions: a `tests/ir/lowering` refusal document holding the
  colliding pair, and a `tests/basic` fixture for the neighbouring pair that differs in method GENERIC ARITY and so
  stays two reachable slots.

- **kotc/bir2cir (area:kotc): a value-position join no longer decides a CLR slot in kotc (#86 §3).** A `try`/`catch`
  or `if`/`when` join the frontend resolved to a bare VALUE type while one branch still yields a literal `null` had
  its slot widened to `Nullable<V>` by the BIR emitter, reasoning in-comment about `HasValue=false` and "a bare `int`
  slot" — a physical-representation decision two layers above where representation is decided, and one that asked a
  hardcoded primitive/unsigned list where every other erasure decision asks the struct-ness oracle.
  The split now runs along the layer boundary: kotc records the FRONTEND FACT (`joinNullBranch`) on the declaration
  it *mints* for the join — the `try`-expression temp, each `cond` of a `when` chain — and bir2cir's
  `ValueJoinNullWidening` decides the physical consequence, off the oracle, so a `value class` or a BCL struct join is
  covered by the rule that covers `Int`. Both halves of the decision moved, so the two can no longer disagree.
  That the fact comes from the producer is load-bearing, not tidiness: a first attempt recognized the join by its
  emitted SHAPE instead — a `var` beside a `try` in a `valueBlock` — which is also the shape of an ordinary user local
  written before a `try` in an expression-position block, and it retyped that local to `Nullable<int32>` over an
  `int32` initializer. Measured: an `AccessViolationException`, and, for the plain swallow-and-null idiom
  (`try { n = s.length } catch { null }`), a silently wrong answer. Nothing in the BIR could separate the two — the
  temp's name is not stable across `InlineSplice` and the `try`'s own type is `Unit` either way.
  The fact is COMPOSITIONAL, so it has to survive the emitter's own wrapping: a `when (subject)` needing a temp, or
  any block in value position, hands the enclosing join a `valueBlock` rather than the `cond` or the `null` inside it,
  and an enclosing join that cannot see through that wrapper never learns there is a `null` below it. Every
  `valueBlock` the emitter mints now goes through one constructor that stamps the fact exactly when its `result`
  yields null — a `valueBlock` is the one PASS-THROUGH wrapper, its statements being side effects and its result being
  its value. Nothing else is transparent, and that is a rule rather than an omission: the widening only ever matters
  at a VALUE join, where the branch's static type IS that value type, so every other wrapper produces a value of the
  wrapped-to type instead of a null (`if (c) 1 else (null as Int)` throws `NullReferenceException`; `nullableValue` /
  `safeCastValue` / `nullableWrap` yield a `Nullable<V>` struct; `isInstRef` can answer null but only at a reference
  join, which never widens).
  The whole stdlib (reference + runtime) lowers byte-identically across the move; the arming shape has no natural
  instance in the corpus, so it is pinned by a `tests/ir/lowering` document whose discriminations now include the
  axis that broke — a neighbouring user local of the same type must keep it — beside an all-values join, a REFERENCE
  join, and a join reached through the `valueBlock` wrapper, and by runtime fixtures for both measured miscompiles.

- **bir2cir/stdlib (area:bir2cir): `Array<X?>` is `object[]` — the last two representations of the nullable-generic
  array are gone (#86 D2).** One Kotlin type constructor had three physical forms: a concrete `Array<Int?>` was a
  `Nullable<int32>[]`, an open `Array<T?>` was an `object[]`, and the `arrayOfNulls<T>(n) … as Array<T>` reify-back
  chain kept a bare `!T[]` end to end. `object[]` and `Nullable<int32>[]` are **unrelated** CLR types — array
  compatibility requires reference-compatible elements (ECMA-335 I.8.7.1) — so no two of them could ever meet, which
  is why the same array behaved differently depending on which one produced it: a generic `Array<T?>` parameter at
  `T=Int` segfaulted the process, a cross-module `Array<Int?>` param re-imported as `IntArray` (the consumer would
  not compile), and a cross-module `Array<Int?>` return was indexed as an `int32[]` so its LAYOUT WORDS came back as
  elements — `4/null/8` read as `3/1/4/0`, with no diagnostic and no exception.
  `Erase` now maps an array element that is `X?` for a possibly-value `X` — ANY type variable, or a value FQN
  including a CONSTRUCTED one like `KeyValuePair<K,V>` or `ArraySegment<T>` — to
  `object`, at declaration slots, at `newarr`/`ldelem`/`stelem` tokens and at the element a `for (x in arr)` binds
  alike; the pre-erasure `Array<Int?>` rides the same `[KotlinNullableGeneric]` carrier every other erased slot does,
  so the Kotlin surface survives the round trip; and the carrier reader, which refused an `Array<X?>` slot precisely
  because the two forms disagreed, now serves it. `Array<String?>` still keeps its `string[]` and `Array<Int>` its
  `int32[]` — a reference element is not part of this — and `IntArray` is untouched.
  A generic reached THROUGH such an array is instantiated at `object`, because that is the only instantiation whose
  `T[]` parameter an `object[]` inhabits: `arrayOfNulls<Int>(3).copyOf(4)` binds `T = Int?` and calls `copyOf<object>`.
  This is not the call-site type-argument collapse #86 rejects — a type argument that never reaches an array is
  untouched, so `listOf<Int?>(null, 2)` is still a `List<Nullable<int32>>`.
  The reify-back representation is deleted rather than adjusted, and with it the stdlib bodies that depended on it:
  `emptyArray`/`orEmpty`/`toTypedArray` allocate the genuine `T[]` they promise through `Array.CreateInstance` off
  the reified `T::class`; `plus`/`plusElement` allocate from the RECEIVER's own runtime array type, as `copyOfRange`
  already did; `arrayOfNulls(reference, size)` does the same off its reference array; and `copyOf(newSize)` decides
  at runtime — `object[]` for a value element, `elem[]` for a reference one — so a consumer at `T=String` still gets
  the real `string[]` its `Array<String?>` slot names. The user-visible consequences are recorded in
  `docs/dotkt-semantics.md` §9c-bis: `Array<Int?>` surfaces to C# as `object[]` rather than `int?[]`, its elements
  box, and `is Array<Int?>` loses precision.
  **One shape regresses and is listed rather than hidden**, because it is what says D2 cannot be finished on its own:
  `Int?` now has two physical forms by POSITION — `object` as an array element, `Nullable<int32>` as an ordinary type
  argument — so a generic carrying the element across that boundary can satisfy one end or the other, not both.
  `fun f(xs: Array<Int?>): List<Int?> = xs.toList()` instantiates at `object` (nothing else accepts an `object[]`) and
  hands back a `List<object>` where the declared slot is an `IReadOnlyCollection<Nullable<int32>>`. It is driven
  same-module as `roundtrip-nullable-vt-generic-array-to-collection` and blocked on the type-argument half of the
  decision: either `X?` is `object` at every type-argument position (`List<Int?>` becomes `List<object>`), or the
  concrete `Array<Int?>` keeps a representation of its own after all.
  The prune #86 predicted for `ArrayTests::copyOfGrowsWithNullTail` **happened** — the
  `object[]`-vs-`Nullable<int32>[]` cause it named is gone — but the method is not verifier-clean, so reading the
  issue's checklist against the code needs care. What remains there is a DIFFERENT, formal-only shape, shared with
  `boxedGenericValues` and `arrayOfNulls`: `Array<Int?>.toList()` yields an `IReadOnlyList<object>` whose consumer slot
  is an `IReadOnlyCollection<Nullable<int32>>`, because a consumer's type argument is not inferred across two
  different generic heads. Three `ILVERIFY_XFAIL` entries carry it, one per method, and the value-element assertions
  were SPLIT out of `copyOfGrowsWithNullTail` into `copyOfGrowsWithNullTailAtValueElements` so that no entry absorbs a
  cause its reason does not describe — the baseline is keyed by method name, so a mixed method hides the difference.
  All three RUN green. The cross-module `Array<Int?>` entries and the whole packaged-SDK baseline did prune.
- **bir2cir (area:bir2cir): a nullable generic `T?` now has ONE physical CLR representation — `System.Object` — at
  every position (#86).** `Nullable<T>` is inexpressible for an unconstrained `T` and a bare `!T` slot collapses a
  null to `default(T)`, so a `T?` slot has exactly one sound CLR form; the backend nevertheless kept two, erasing
  most positions to `object` while holding **method params, constructor params and body locals** back as the bare
  `!T`. Two representations of one Kotlin type meet at every value instantiation, and they cannot: `pickOr<Int>(null,
  7)` and `Cell<Int>(null)` pushed `ldnull` into an `int32` slot, so the *whole enclosing method* failed JIT
  verification and the program printed nothing before dying with `InvalidProgramException` — with no module boundary
  and no metadata involved. The carve-outs are gone, and with them the positional exception is gone as a category:
  the rule is now `physical(s) = Erase(declaredKotlinType(s))` for every declaration slot, and a `Nullable(Tv)`
  surviving anywhere in emitted CIR is a defect a lowering document asserts against directly.
  A function type's return now obeys the same rule as its parameters instead of being handed to a second owner, and
  the `as?` subject-temp erasure that kotc performed above the layer boundary is deleted — a body local is a slot,
  so the uniform rule covers it, and the CLR-representation decision belongs in bir2cir.

- **bir2cir (area:bir2cir): a top-level `T?` return no longer re-imports as a non-null `Any` (#86).** It carried
  neither channel: the `[KotlinNullableGeneric]` recorder skipped a head-position `Nullable(Tv)` outright, and the
  NRT byte walk runs *after* the erasure, so it walked `object` — whose non-null default emits no override at all.
  A reader strips the carrier's outer nullability by contract, so only the byte can restore the `?`, and with
  neither present the slot came back as non-null `Any` and the consumer **stopped compiling**. Unlike the parameter
  axis this was never confined to value types — a `String?` return degraded identically — and being a consumer
  *compile* failure it was invisible to every runtime-shaped gate. The recorder now records the head position and
  stamps its NRT byte from the pre-erasure type.

- **bir2cir (area:bir2cir): the erasure's WRITE positions are reconciled, not just its reads (#86).** A use of an
  erased slot is typed `Subst(Erase(declared), typeArgs)` — never `Erase(Subst(...))`, which substitutes away the
  very type variable that says the position was erased. That formula was applied only where a value is *produced*;
  where one is *consumed* it was not, so a store, a `return`, an `if/else` join and every call or constructor
  ARGUMENT met the erased slot with the callsite's substituted type. For an argument that is worse than a mistyped
  stack slot: the signature descriptor a call is resolved by drifted with it, so the emitter looked for a member
  that does not exist. `setLocal`, `setField`, `arraySet`, `return`, the value-position `cond`, call and ctor
  arguments (descriptor included) and the call RECEIVER now derive their target the same way the read side derives
  a result, and a value is converted only across a bare `object` seam — the only one the CLR can express, since
  `Ref<object>` and `Ref<Nullable<int32>>` are unrelated invariant reified generics that no cast reconciles.

- **bir2cir (area:bir2cir): an overload the erasure collapses is REFUSED, not silently mis-bound (#86 §5.3).**
  `class C<T> { fun f(x: T?) ; fun f(x: Any?) }` is two Kotlin declarations the frontend accepts, and Kotlin's own
  resolution picks `f(T?)` for `c.f(3)` at `C<Int>`. Both emit `f(object)` — `T?` reaches it through the erasure and
  `Any?` through the reference-nullable strip — so one member occupies the slot and the other is unreachable:
  measured, `c.f(3)` and `c.f("s")` both ran the `Any?` body, with no diagnostic. A program with no valid CIL lowering
  owes its author an actionable message, so this now refuses and names both source signatures and the one signature
  they collapse onto. The condition is DIFFERENTIAL — distinct parameter vectors before the erasure, identical after —
  because pairs that were always one CLR signature are not this rule's business: the stdlib's two `contains`
  overloads differ only in their type-parameter constraints, which no CLR signature ever carried, and refusing those
  would reject code that emits exactly as it did before. Generic arity stays part of the key, so
  `fun <T> f(x: T?)` beside `fun f(x: Any?)` is still two slots (ECMA-335 I.8.6.1.6).

- **bir2cir (area:bir2cir): a use of an erased slot is derived from the REFERENCED declaration too, not only from a
  same-compilation one (#86 D1).** `[KotlinNullableGeneric]` had one reader — `dll2klib`, restoring the Kotlin
  *surface* a consumer compiles against — and restoring the surface is only half of consuming it. A consumer that
  re-imports `fun <T> unwrapSlot(slot: Slot<T?>): T?` writes `unwrapSlot(Slot<Int?>(5))` and builds a
  `Slot<Nullable<int32>>`, while the producer's slot is physically `Slot<object>`: unrelated invariant reified
  generics that no cast reconciles, so the callee read the argument at the wrong shape and the program **corrupted
  memory** in `CastHelpers.Unbox_Nullable` — with the identical code carrying `null` instead of a value entirely
  green, which is why a bundled test made this look like a param-vs-property problem rather than a present-vs-absent
  one. bir2cir now reads the same carrier off the referenced assembly and types the use as
  `Subst(Erase(declared), typeArgs)` — the identical formula it already applied to a local declaration — so the
  construction is *built* as `Slot<object>` rather than built wrongly and converted afterwards. The reader takes the
  carrier first, then the producer's physical signature but only while it still carries a generic parameter (a
  `Tv`-free physical slot could only contribute a bare `object`, which without a carrier beside it is a declared
  `Any`), walks base and interfaces because a call names the owner it is DISPATCHED on and not the one that DECLARES
  the member, and refuses a same-shape overload set outright rather than picking a sibling.
  That real declaration **replaces a hardcoded member table**: `DeriveKnownReceiverReturn` and its
  collection/iterator owner name sets — eight collection FQNs, four iterator FQNs, and three special-cased member
  names — are deleted, and `Iterable<E>.iterator()`, `Iterator<E>.next()` and `List<E>.get(i)` on a receiver that
  came through the erasure are now derived from what the stdlib actually declares, along with every other referenced
  generic member the table never listed.
  One position is deliberately **not** served: an `Array<X?>` slot, because the producer does not implement its own
  declaration there — `Array<T>.copyOf(newSize)` declares `Array<T?>` (physically `object[]`) and reflectively
  allocates a `Nullable<V>[]` — so deriving a use from it turns today's formal ilverify finding into a real access
  violation. The refusal deletes itself when `Array<X?>` becomes canonically `object[]` (#86 D2).
  And one call SHAPE is deliberately out: a call that states its result only in the frontend `sty` stamp and carries
  no `ret` — which is how a cross-module generic factory arrives, so `holderOf<String>(3)`'s erased `Vault<object>`
  return still meets the restored `Vault<string>` slot as a formal-only ilverify finding. Deriving from `sty` closes
  that one and was tried; it then reaches the same call's function-type ARGUMENT, whose delegate the consumer cannot
  yet build at the erased shape, turning one formal finding into two `DelegateCtor` ones. It lands with the
  parameter half of the func-slot erasure, not before it.
  Measured and NOT deleted: the `forEach` re-narrow trio. Its second half narrows the loop variable back to the
  PRE-erasure element type, which the blanket sweep has already consumed by the time any use axis runs — a missing
  TYPE, not a missing declaration, so no reader can supply it. Deleting it reproduced the `filterNotNullTo`
  `InvalidProgramException` at a value element, and its comment now says that instead of pointing at the table.
  Four refusal-discipline holes closed on review, each one a place where a refusal was stated and then not honoured.
  A refused carrier no longer falls back to the physical declaration — that fallback is the same erasure spelled
  *without* the evidence that it was one, so a `Pair<Array<T?>, U>` slot (physically `Pair<object[], !1>`, still
  generic-parameter-bearing) served precisely the `object[]` derivation the array refusal exists to prevent. A member
  DECLARED at a level now terminates the search whether or not it carries erasure facts, so a concrete member that
  shadows an inherited namesake can no longer be handed the base's carrier. The supertype cycle guard is path-local
  and keyed on the CONSTRUCTED supertype, so `I<int>` and `I<string>` are both visited and compared instead of the
  answer depending on reflection interface order. And a `.NET`-bound call's argument is converted to the CLOSED slot:
  `memberSig` states the callee's parameters OPEN and stays that way for member matching, so
  `Enumerable.Repeat<Int?>` was casting an erased argument to whatever `!!0` lowered to in the CALLER — an
  `InvalidProgramException` before the method printed anything, now covered by a fixture.
  Whether a call is `.NET`-bound is now read off its KIND rather than off which descriptor key holds its parameter
  vector. A GENERIC .NET call carries `memberSig` from the moment it is bound; a NON-GENERIC one carries `argTypes`
  until member resolution stamps `memberSig`, which happens long after this narrowing is decided — so every
  non-generic .NET call was taking the Kotlin fallback, whose widening screen drops reference slots. An erased
  `object` therefore reached a `string` parameter with no `castclass`: `Path.GetExtension(…)`,
  `StringBuilder.Append(…)` and `StringBuilder(…)` all ran (both sides are references) while the emitted method
  failed verification with `[found ref 'object'][expected ref 'string']`. All three shapes are fixtures now, in the
  suite whose ILVerify lane is what catches a fault a RUN assertion cannot see.

- **bir2cir (area:bir2cir): a `vararg xs: T?` pack is built at the erased element type (#86).** The packed array and
  its elements are ONE decision: the pack fills an `Array<T?>` slot erased to `object[]`, and built as
  `Nullable<int32>[]` it cannot be converted afterwards — the `newarr` and the `stelem` filling it disagreed and
  `count<Int>(1, null, 3)` segfaulted. An array construction is now a typed use like any other: the caller's argument
  realignment corrects its element type before it is evaluated, and its elements are then reconciled against that
  element, so allocation and stores agree by construction.

- **bir2cir (area:bir2cir): a `T?` through the two SUSPEND channels keeps its Kotlin surface (#86).** Both re-imported
  as `Any` and a cross-module consumer stopped compiling — invisible to every runtime-shaped gate, which is why
  neither had been seen. A suspend declaration's result rides `suspendRet`, and the Task bridge that becomes its
  public ABI is constructed fresh, so it inherited nothing from the declaration it replaces; it now transfers the
  pre-erasure result on both channels, the carrier and the NRT byte the reader needs past the `Task` node. A
  `suspend (…) -> T?` VALUE erases whole to `object` and its shape rides the dedicated `[KotlinSuspendFunctionType]`
  carrier — which is built during type lowering, after the erasure, and so faithfully recorded
  `suspend () -> object`; it is now built from the pre-erasure shape stashed before the sweep. The nullable-generic
  carrier still excludes suspend function types for the reason it always did — there is no physical delegate for it
  to align with — so this makes the one carrier truthful rather than adding a second.

- **bir2cir (area:bir2cir): the hand-written special cases the uniform erasure subsumes are DELETED (#86).** Each
  was the erasure formula applied by hand at one node kind,
  and each existed because the formula was not applied everywhere — so with the rule uniform they say nothing the
  rule does not. Gone: the property-accessor retype (a `get_x` return and a `set_x` parameter are declaration slots
  of the same declared type, erased on their own, so row and accessors are coherent by construction) with its
  accessor-name collection, its reader-local retype, its call-return retype and its setter-argument wrap; the
  init-gated body-local retype with all three of its idiom gates — the gates existed to tell a genuine accumulator
  from a synthesized safe-call temp, a distinction that stops mattering once BOTH are erased and both re-narrow at
  their typed uses; and the return-value retype, subsumed by `return` becoming a use position like any other.
  The `Enumerable.Cast<object>` receiver conversion is NARROWED rather than retired — see the carrier-argument
  erasure entry above for what it still converts and which slot it may fill. `List<Int?>.filterNotNullTo` at a value
  element and **#324**'s `countG(nullBoxes(7), 2)` are pinned as fixtures and both run green.

- **bir2cir (area:bir2cir): an override of an object-erased `T?` slot is now an override, not a new overload
  (#86 D3).** `class TextSink : Sink<String> { override fun accept(x: String?) }` writes a CONCRETE type, so no
  `Nullable(Tv)` sweep can reach it — but the slot it must fill is the base's `accept(object)`, at every
  instantiation, because the erasure is a property of the declaration and not of the type argument. Emitted as
  `accept(string)` it filled nothing: the interface method stayed unimplemented and the type failed to load. Erasure
  now propagates from the overridden slot, read out of the same-compilation declaration index, and the override's own
  Kotlin type is recorded on the carrier and NRT-byte channels so its surface still round-trips. The base slot's
  erasure PATTERN propagates, not a blanket `object`: a base `Box<T?>` erases to `Box<object>`, so an override's
  `Box<Int?>` becomes `Box<object>` and every position the base did not erase keeps the override's own concrete
  type. This closes the narrowed override at a value instantiation, same-module and cross-module, which had been
  failing with `TypeLoadException` and an emitter abort — reached through the base slot, which is how an override is
  normally called. Reaching it through its OWN declared type cross-module still does not bind: the physical slot is
  `accept(object)` while the re-imported Kotlin surface is truthfully `accept(x: Int?)`, and converting between them
  needs the referenced declaration. That is the `.override` bridge half of D3, and it is now a documented red rather
  than an unmeasured shape.

- **bir2cir (area:bir2cir): constructor bodies and base/`this` delegation arguments are part of the use axis
  (#86).** The walk visited `methods` only, so `class Derived(y: Int?) : Base<Int>(y)` handed a `Nullable<int32>`
  straight to `Base<T>`'s erased `object` constructor slot and `Derived`'s own constructor failed JIT verification.
  A delegation argument is a call argument into the delegated constructor's parameter vector, and a constructor body
  is a body; both are now reconciled like every other use.

- **bir2cir (area:bir2cir): the `.NET`-interop reshapes carry the node's result-type stamp, so a `.NET` operand
  left of a suspension compiles again ([tmyt/dotkt#304]).** `NetInteropBinding` re-forms the plain call/read kotc
  emits by a .NET owner's identity into the CLR vocabulary. That changes a node's SHAPE and not what it produces,
  so every result-type stamp it carried stays true of the `clr*` node — but three of the reshapes dropped one:
  the FIELD reshape (`field` → `clrPropGet`) dropped both `sty` and `ret`, the generic branch
  (`clrGenericStatic`/`clrGenericInstance`) dropped `ret`, and the `.NET`-event branch cleared the node before
  either was re-added. `bir-common/NodeType.cs` has no derivation arm for any `clr*` kind, so those stamps ARE the
  reshaped node's static type; without one the node cannot be typed at all, and an operand with no static type
  standing LEFT of a suspension is refused by the stage-0 operand planner (it would declare an untyped spill
  local). `v.X + suspending()` on a `.NET` field was therefore a compile-time rejection of source the frontend
  accepts, while the same expression without the suspension compiled and ran. The stamps now travel with every
  reshape, stated once at the top of the pass. `dynRet` deliberately does not travel: it is the UNBOUND Kotlin
  call's dynamic-dispatch channel (ilemit falls back to reflection on its presence), so on a node already bound to
  a concrete CLR slot it would be a dispatch instruction rather than a type fact, and `sty` carries the same
  instantiated type without it.
  The sibling audit found one more node in the same class, synthesized rather than reshaped: the value-element
  collection conversion wraps its argument in `System.Linq.Enumerable.Cast<object>` and left the wrap unstamped.
  That one RETYPES the operand, so it is stamped with what the wrap itself produces (`IEnumerable<object>`) rather
  than with the wrapped node's stamp — which would be a lie, not merely an imprecision, exactly as at the
  `NullableTvErasureCallRealign` restamp sites. (The pass was later narrowed and renamed to
  `ValueElementIterableCoercion`; the stamp and its reason are unchanged.)
  CIR is unchanged across the whole gated corpus and on the measured non-suspend control (byte-identical): `sty` is
  bir2cir-internal and stripped before CIR, and the `ret` carries only add a slot where the reshaped node had none.
  One shape is a deliberate, semantically-neutral exception rather than a no-op — `CharSeqStringLowering` reads
  `ret` while classifying an operand, so a now-`ret`-carrying `.NET` String field read (a generic owner, the only
  case where kotc stamps `ret` on a `field`) flowing into a `CharSequence` slot is recognized as the statically
  non-null String it is, and loses the null-safe `toString` wrapper it used to get for want of a type. The value is
  the same; there is simply no longer a null check on a reference that cannot be null.
  The `ret` half of the carry has no Kotlin-source witness in either branch, so it is pinned by a pass-level
  document instead: `tests/ir/lowering/net-interop-reshape-result-stamp` asserts the slot survives both reshapes,
  and goes red if either carry is removed.
- **bir2cir (area:bir2cir): a member called on a TYPE-PARAMETER receiver now emits constrained dispatch for every
  spelling of the receiver, and for a non-generic constraint.** `fun <T : Tagged> f(t: T) = t.tag()` put a `!!T` on
  the evaluation stack and then a plain `callvirt Tagged::tag()` — ECMA-335 requires `constrained. !!T ; callvirt`
  there, so the verifier reported `[found value 'T'][expected ref 'Tagged']`. (The boxing half of that argument is
  formal here rather than measured: a Kotlin-declared constraint cannot be satisfied by a CLR value type, because
  every Kotlin class is a reference type on this platform — §5f. `T : Comparable<T>` at `T = Int` is the one
  value-type instantiation Kotlin source can express, and it reaches constrained dispatch through
  `MemberCallSubstitution`, not through this pass; it is pinned as a test either way.) The old binding covered
  exactly one slice of this: a receiver spelled
  as a plain LOCAL, whose constraint owner was GENERIC (`fun <N : Node<N>> N.close()`), because that is the slice
  where the MemberRef is invalid as well. Everything else was emitted unverifiably — an ordinary non-suspend
  function, a local copy of the parameter, a field read, a `T`-returning call result, a property accessor body, a
  nullable `T?` receiver behind `!!`, and (the shape that had an ilverify baseline entry) the state-machine field
  the suspend lowering spills a suspend function's receiver into.
  The binding is now keyed on the receiver's STATIC TYPE, read through the one uniform source
  (`StaticType.Surface`), so the spelling no longer decides; and the owner is closed from the type parameter's
  lexical bound only where BIR names it bare, an already-constructed or non-generic owner being closed already —
  or, for a member declared on a generic BASE of the bound, by the inherited-owner hierarchy substitution this
  pass now runs after. It
  moved out of `InheritedMemberOwnerBinding` — whose subject is the hierarchy substitution `Derived<T>.m` ->
  `Base<T>.m`, not a constraint — into its own `ConstrainedTypeParameterReceiverBinding`.
  The owner it names is the member's DECLARING type: closing the bare token from the bound is a separate step from
  rewriting the dispatch, and it runs BEFORE the inherited-owner walk so the walk still has a constructed type to
  substitute into. Naming the bound instead — `Leaf<Int>` for a member `Root<X>` declares — is a MemberRef on a
  type that does not declare the member, which binds only through the emitted fake override that happens to sit
  there. Only the DISPATCH changes: the call's overload key, a generic member's instantiation and its declared
  result view all ride the node into ilemit, which applies them on the constrained arm exactly as on the ordinary
  one, and ilemit now SELECTS a member by that descriptor — name, generic arity, parameter count and parameter
  types — refusing when nothing matches exactly instead of falling through to a name-only lookup that returns
  whichever overload was declared last.
  (Without that, the constrained form dispatches `t.describe(7)` to the `String` overload and calls a generic
  member's uninstantiated definition — a silently wrong answer and a runtime `InvalidOperationException`; both
  are now pinned by value in `tests/basic`.)
- **bir2cir (area:bir2cir): a constructor argument the collection mapping maps AWAY is now evaluated (#278).**
  `HashSet(initialCapacity, loadFactor)` has no CLR counterpart for its load factor — the concept is a JVM hashtable
  one — so `MemberCallSubstitution` maps the call onto the capacity-only BCL constructor. It dropped the argument's
  EVALUATION with its value: `HashSet<Int>(16, computeLoadFactor())` never called `computeLoadFactor()`, and an
  exception that argument would have thrown simply never happened, which is the same fault class as evaluating a
  call value twice, at zero instead. The mapping now re-expresses the arguments as a call-evaluation plan — one
  binding per original argument in Kotlin order, the kept ones read from their slots, the mapped-away ones read by
  nobody — and hands it to `CallEvalLowering.Materialise`, so the existing rules decide: an unread binding is
  evaluated into a local unless Q2 (`ValueStability.IsDroppable`) says the evaluation is unobservable, and the
  prefix rule materialises every earlier argument so a kept value cannot slide behind a mapped-away one. Building a
  plan rather than prepending an evaluate-and-discard statement is what keeps those two rules in one place; the
  literal `HashSet(16, 0.75f)` idiom materialises nothing and emits the same bare `newClr` as before. The rule holds
  for every constructor the table covers, so `HashMap` (`Dictionary`) and `LinkedHashMap` (`OrderedDictionary`) are
  fixed with it, and `LinkedHashSet` — a real Kotlin class whose constructor keeps both arguments — is unaffected.
  `MappedConstructorArgumentTests` pins the order, the single evaluation, the propagated throw, and the delegation
  /property-initializer/lambda positions.
- **bir2cir (area:bir2cir): a `try` expression inside a lowering-MINTED operand block no longer produces invalid
  IL.** A CLR protected region must be entered with an empty evaluation stack, which is why `TryValueOperandHoist`
  moves a try-valued operand out of a non-first slot into preceding statements. It recognised only kotc's own
  spelling — a block whose `stmts` contain a `try` DIRECTLY — but several lowerings materialise an operand into a
  minted `valueBlock` whose `var` initializer is then the try-valued expression: a call-evaluation plan's bindings,
  `RangeMembershipLowering`'s bounds, `PreconditionLowering`'s subject, `NetInteropBinding`'s adapters, and now the
  mapped-away constructor argument above. The hazard is identical and the hoist missed all of them, so
  `f("z", (try { 1 } catch { 2 }) in 1..5)` compiled to an `InvalidProgramException` from source the frontend had
  accepted. The hoist now searches a block's inline statements — both statement lists and the result, stopping at a
  nested declaration whose body runs on its own stack — and moves them in the order their consumers run them. Two
  assumptions that only held for kotc's own spelling went with it: a hoisted block's RESULT is now spilled like any
  other operand rather than left in the slot (kotc's result is a stack-neutral `local`, but a minted block's can be
  a `newClr` whose constructor throws, and leaving it behind let a LATER argument's side effect overtake it), and
  recognizing a block no longer ends the walk inside it (a `when` with a try-valued subject is such a block, and a
  try in one of its branches' operand slots still needs hoisting). `ExceptionTests.tryInsideAMintedOperandBlock`
  and `.tryInABranchOfATrySubjectedWhen` pin the shapes, with the ordering half in
  `MappedConstructorArgumentTests.laterArgumentDoesNotOvertakeTheConstruction`.
- **bir2cir (area:bir2cir): a suspend state machine's `create()` no longer puts a live CLR object in the document.**
  The synthesized `new SM(capture…, completion)` added each capture's `TypeNode` RECORD straight into its `argTypes`
  array instead of the wire node `TypeJson.Write` produces, so the BIR carried a `JsonValueCustomized<TypeNode>` — a
  slot no reader can parse, and one that makes any full-document write of that tree throw unless the writer happens
  to be carrying System.Text.Json's default reflection resolver. Nothing noticed because those entries are dropped
  again before the CIR is written and the CIR writer's own options are only ever handed already-clean trees; the
  #305 chokepoint, which serializes the post-pass BIR, is the first thing that had to write one.
- **bir2cir (area:bir2cir): `await(captureContext = <expression>)` no longer refuses a non-constant Boolean
  ([tmyt/dotkt#64]).** dll2klib publishes two await bridges for an awaitable that exposes `ConfigureAwait(bool)` —
  `await()` and `await(captureContext: Boolean)` — so `task.await(captureContext = policy)` is a frontend-resolved
  call; `EmitAwaitPoint` accepted only a constant in that slot and threw, aborting the whole compile. The awaiter
  never needed the value: `ConfigureAwait(true)` and `ConfigureAwait(false)` return the same configured awaitable,
  hence the same awaiter type, so a runtime Boolean selects no state-machine field type and needs no branch — the
  configured awaiter is stored statically and the Boolean only reaches the .NET call. The lowering is now TWO arms
  picked by the argument's SHAPE: an omitted argument or a constant `true` keeps the direct `GetAwaiter()` (which
  is what capturing already means), and everything else — including a constant `false`, which had an arm of its
  own passing a synthesized literal — is `ConfigureAwait(<the expression>).GetAwaiter()`. The expression is
  evaluated exactly once and after the awaitable receiver, including when it suspends: the await marker rewrites
  its own operands (it is excluded from the stage-0 operand plan), so the receiver is bound into a state-machine
  field whenever the argument's own lowering emits statements — an argument that suspends, or one that transfers
  control instead of producing a value. That question is asked by a predicate written for it rather than by the two
  frame-ownership predicates next door: those stop at every lambda kind, while the rewrite descends into a
  `newClosure`'s CAPTURES, where a bound callable reference `(<expr>)::f` puts an arbitrary expression — so a
  `throw` there left the receiver evaluated ZERO times on the throwing path. `tests/coroutines/fixtures/DynamicCaptureContextTests.kt` drives the runtime
  shapes; the five `tests/ir/lowering/await-capture-*` documents pin what no runtime assertion can witness — the arm
  SELECTION (`ConfigureAwait(true)` and `GetAwaiter()` behave identically, so only the emitted shape shows which was
  chosen), the receiver binding a suspending argument forces, and the configured awaiter's field type. Folding the
  constant-`false` arm is output-neutral, and its document says so rather than claiming a difference.
  The refusal that remains — an awaitable whose `ConfigureAwait(bool)` returns something that is not itself
  awaitable, which dll2klib publishes the overload for because the returned type may live in an assembly it does not
  read — now names THAT as the reason instead of reporting a missing `ConfigureAwait(bool)` member.
- **bir2cir (area:bir2cir): the capture-control hop is read from the awaitable's metadata instead of being rebuilt
  from its receiver, so an awaitable that is not shaped like `Task` works.** Three defects, all reachable through
  `await(captureContext = …)` and the first two of them older than it. (1) The configured awaitable's type was
  reconstructed by repeating the RECEIVER's type arguments under `ConfigureAwait`'s return type NAME, so a
  declaration that permutes them — `Awaitable<A,B>.ConfigureAwait(bool): Configured<B,A>` — produced
  `Configured<A,B>`: a real type on which none of the members then called exist, i.e. unverifiable IL and a run-time
  failure. This already broke a constant `false`. The plan now carries every awaiter and configured type as a
  TEMPLATE of the DECLARED type, closed at the call site, so a permuted, dropped or fixed type argument comes out as
  declared. (2) The configured awaitable's `GetAwaiter` was looked for as an instance member only, though the
  awaitable contract has always accepted a referenced `[Extension] static GetAwaiter` — so capture control on such a
  type was REFUSED although C# `await` compiles the same shape. It is now resolved and emitted through both halves of
  the contract, a generic extension's type arguments unified from its declared receiver rather than copied. (3) The
  receiver binding introduced above always took a state-machine FIELD, which the CLR forbids for a byref-like
  (`ref struct`) awaitable — turning a legal program into a compile-time refusal. Only a SUSPENSION between the
  binding and its use needs a field; suspension-free statements need a typed local, which is exactly what §4d says a
  byref-like value may be. `tests/interop/consumer/fixtures/CaptureContextAwaitTests.kt` covers all three against
  producer types written for them (`tests/interop/producer/CaptureAwaitable.cs`).
- **kotc (area:kotc, #67): suspend extension and re-imported DotKt member callable references now lower through
  the general suspend-reference adapter.** Suspend callable references are represented by `newSuspendLambda`, whose
  body carries the same Kotlin call facts as a direct invocation. The router previously admitted only non-extension
  references and rejected member declarations restored by dll2klib; extension references also keep their receiver in
  the function type's receiver slot rather than its ordinary parameter list. The adapter now derives its physical
  parameters from both slots, captures bound receivers exactly once, and handles local and referenced top-level/member
  provenance uniformly. Bound and unbound extension forwarding share one call-shape builder with ordinary callable
  references, so generic arguments and referenced file-facade ownership cannot drift between the suspend and
  non-suspend paths. Coroutine fixtures cover both extension shapes, and the ProjectReference lane covers bound and
  unbound suspend members imported from another DotKt assembly.
- **bir2cir (area:bir2cir): a call to a `fun f(): Nothing` no longer leaves its erased `object` in a value slot
  (#197).** `Nothing` has no CLR analog, so such a function returns `object`. A `throw`/`return` in expression
  position announces "no value" in its own node kind and ilemit emits it as the terminator it is, but a CALL
  announces it only in its type stamp — so the erased `object` reached whatever read the expression: the other arm
  of an `if`/`when` merge, the method's `ret`, a typed local. `object` is not assignable to `string`, so `ilverify`
  reported `StackUnexpected [found ref 'object'][expected ref 'string']` on a merge the program never performs (the
  arm always throws first, so every affected program RAN correctly). bir2cir now TERMINATES a `Nothing`-stamped
  value position where it stands — `else boom()` becomes `else throw boom()` — so nothing is merged and no cast
  papers over an arm that delivers nothing. The fix is the fault class, not the reported example: same-module and
  cross-module, `then` arm and `else` arm, a `when` arm, an elvis right-hand side, a block whose LAST expression is
  the call, BOTH arms, a bare expression-body `ret`, and a value-typed (`Int`) merge — plus one instance the
  termination has to be run TWICE to catch: a covariant `override fun f(): Nothing` (legal, since `Nothing` is below
  every type) makes bir2cir synthesize an exact-CLR-slot bridge that forwards to it, minting a fresh
  `Nothing`-stamped call in a method that did not exist during the per-file sweep, whose erased `object` then met the
  slot's return at the bridge's own `ret`. A second sweep runs after the two interface-bridge synthesizers; it is
  idempotent (an already-terminated position is a `throwExpr`, whose operand the pass does not re-enter — measured,
  not assumed), and it does not replace the first, because passes in between rewrite or drop a node's stamp.
  Because it runs before the
  suspend transform, the state-machine lowering — which already stores nothing for a `throwExpr` arm — is covered
  by the same rule; that axis was previously unexercised anywhere and now has its own battery
  (`SuspendNothingValueTests`), which matters because terminating an arm WIDENS what the suspend lowering handles:
  its escape check is a whole-subtree walk, so a terminated arm anywhere under a conditional now routes the whole
  node through the `__cond$` control-flow path, where a slot it cannot type would be a hard refusal.
  The termination keys on the frontend's explicit `sty`/`ret`/`dynRet` STAMP and never on a
  derived type: `NodeType.Of` answers a `cond` it cannot fully type from whichever arm it can, so kotc's `!!`
  desugar derives as `Nothing` while its value is plainly the non-null operand — terminating on that would delete a
  live value. The stamp lookup itself moved into `NodeType.Stamp`, so the `sty`-then-`ret`-then-`dynRet` precedence
  stays stated once. `roundtrip-nothing` was the case this gap held in the stdout-only shell lane; it is now
  `crossModuleNothingBranchMerge` in the in-process ProjectReference lane, which ilverifies — including the
  DEFAULT-package producer the shell scenario had and the migration would otherwise have dropped, so a regression in
  root-namespace file-class attribution or in `[KotlinNothing]` restoration through it cannot pass silently.
- **`x is T?` answers TRUE for null again, and with it every `joinToString` over a null element (area:bir2cir,
  area:ilemit).** Kotlin's `is` against a NULLABLE type operand accepts null — `null is String?`, `null is Int?`,
  `null is Any?` are all true — and the frontend DEPENDS on it: the `else` branch of `when { x is T? -> … }` is
  reachable only for a non-null `x`, so it carries a smart-cast, and `x.toString()` there resolves to the
  `kotlin.Any` MEMBER rather than the null-safe `Any?.toString()` extension. kotc emitted the type operand's `?`
  faithfully, but nothing downstream read it: type lowering erases nullability from every reference type (every CLR
  reference is nullable, so the lowered type cannot carry the signal), leaving a bare `isinst`, which matches no
  null. The test went false for null and the smart-cast the frontend had already granted dereferenced one. The
  stdlib's `appendElement` is exactly that shape — `element is CharSequence?`, else `element.toString()` — so
  `arrayOfNulls<String>(2).joinToString()` threw a `NullReferenceException` inside `AppendableKt.appendElement`
  instead of rendering `null, null`, on every join receiver (array, list, sequence, `joinTo`), with or without a
  transform, and at any null position. bir2cir's new `NullableIsInstMatch` marks the node `nullMatches` while the
  `?` is still on it, and ilemit projects the one extra `dup; brtrue` that answers true for null — the operand is
  still evaluated exactly once, and a non-nullable type operand is untouched.
  The invariant the `?` spelling owes is now uniform: for a non-null receiver `x is T?` answers exactly what
  `x is T` answers, and for null it answers true. That required the star-projected form to reach the same
  non-generic BCL facade as its plain twin (`x is Collection<*>?`/`List<*>?`/`Map<*,*>?` were stuck on the reified
  interface, which a value-argument `List<int>`/`Dictionary<int,int>` does not implement), and it required
  `Set`/`MutableSet` to leave that facade table: they mapped to the non-generic `ICollection`, which identifies a
  set in NEITHER direction — a `HashSet<T>` does not implement it, while a `List<T>` does, so `listOf(1) is Set<*>`
  was true. That unsound answer is gone. What remains wrong is recorded rather than fixed, with fixtures asserting
  today's values so it cannot drift silently: a Kotlin `Set` has no distinct CLR identity to test against (it shares
  `IReadOnlyCollection<T>` with `Collection`), `Collection<*>` misses sets and admits maps, and a nullable REIFIED
  type ARGUMENT still loses its `?` because one generic method serves every instantiation. Both boundaries are
  written up in `docs/dotkt-semantics.md` §2. (#287)
- **bir2cir (area:bir2cir): a nullable-generic return that was object-erased no longer crosses a suspension under
  its PRE-erasure type.** `fun <T> f(x: T): List<T?>` has its `Nullable(T)` erased to `object` on the declaration
  side, so the emitted method returns `List<object>`, while the call site — emitted with `T` already substituted —
  is stamped `List<Nullable<Int>>`. `NullableTvErasureCallRealign` realigned the call's `ret`/`dynRet` to the erased
  form but left the frontend `sty` stamp behind, and the deriver reads `sty` first. `List<object>` and
  `List<Nullable<int32>>` are UNRELATED invariant reified generics — the very reason that pass exists — so any slot
  declared from the stale stamp is invalid IL rather than a diagnosable drop. A suspension is what makes such a slot
  exist, in two shapes: the erased call sitting LEFT of a suspending operand, where stage 0 declares the plan's
  spill local from the stamp, and the erased call BEING the suspension, where the awaited state-machine field is.
  Both produced an ilverify `StackUnexpected` (`IReadOnlyList<object>` where `IReadOnlyList<Nullable<int32>>` was
  expected, and the reverse at the read). The pass now restamps `sty` at each of its four result-retype sites, per
  the spec §2.7 invariant. The corpus had never composed nullable-generic erasure with a suspension, which is why
  no gate saw it; `SuspendResultTypePrecedenceTests` composes both shapes now.
- **kotc (area:kotc): a `suspend fun interface`'s SAM shim carries its Kotlin RESULT TYPE, not just the suspend
  modifier.** `suspendRet` rides alongside `mods.suspend` on every declaration — the modifier is the fact, the slot
  is the type — and the SAM lift (`BirEmitterLifts`, the shim behind `FlowCollector { … }` and every other suspend
  `fun interface` lambda) emitted the modifier alone. bir2cir's cold registry reads the slot, so the shim's awaited
  values were typed `kotlin.Any`: boxed on the way into the state machine and unboxed on the way out, at every
  suspension inside a suspend SAM body. Found by the refusal that replaced that `kotlin.Any` (see §7b), which is
  what a fallback hides — the drop had been silent since the shim was introduced.
- **bir2cir (area:bir2cir): a suspension inside a suspending call's own operand list no longer hangs forever
  ([tmyt/dotkt#272]), and an operand that CONTAINS a suspension is no longer evaluated after the operand to its
  right ([tmyt/dotkt#286]).** `corAdd(x, corTick(1))` never completed: the outer call wrote its resume label, the
  inner suspension overwrote it, and every resume jumped back into the inner state. `h(f()) + g()` traced F,G,H
  where Kotlin requires F,H,G: the suspension's segments were appended and the residual `h(<awaited>)` was left
  in its slot, so it ran after the NEXT operand's suspension. Both are now unreachable by construction. A new
  STAGE 0 of the suspend lowering (`toolchain/bir2cir/SuspendOperandPlan.cs`) runs before any state machine
  exists and, for every node the shared operand descriptor recognises, wraps the operands in a call-evaluation
  plan (spec §2.7) forced by POSITION: every operand left of the last suspension-bearing one becomes a `var`
  ahead of the node, and — when the node is itself a suspend call — so does that operand, which lifts the nested
  suspension out of the argument list entirely. Everything to the right stays in its slot and is still evaluated
  after the resume. `tests/coroutines/fixtures/SuspendOperandOrderTests.kt` pins the issue's own repro plus the
  instance, receiver-position, generic and four cross-module `clr*` arms of the same shape — one of which
  (`clrGenericInstance`) the descriptor only listed for a call that WAS the suspension, never one that merely
  contained it. The issue's string-valued sibling (`wrap(f()) + g()`, and the `"…${f()}…${g()}…"` template that
  means the same thing) is covered too: both are a `concat` by the time the plan is made, which the descriptor
  had never named.
- **bir2cir (area:bir2cir): a .NET property read, a delegate invoke, an `Any`-slot method or an interface call on
  a type-parameter receiver, sitting left of a suspending operand, now observes the state BEFORE the suspension.**
  `sb.Length + susp()` read the length the suspending callee had already changed. The retired rule decided which
  operands were evaluated before a suspension by KIND — a subtree free of calls, allocations, assignments and
  control transfers was judged safe to defer past the resume — and that set never closed: a raw field read and an
  array element had been added, `clrPropGet`/`delegateInvoke`/`objMethod`/`constrainedCall` never were. Stage 0
  answers by position instead, so the whole family is covered without naming a kind, and the predicate, the
  eval-order rewrite it fed and the liveness analysis's model of that rewrite are all deleted.
- **bir2cir (area:bir2cir): an argument that never returns, left of a suspension in a suspending call, compiles
  and runs instead of being refused.** `sum(run { throw … }, relay())` was a compile-time refusal, because the
  cold-call builder met it half-way through assembling a suspension point it then had no way to elide. Stage 0
  sees the same shape before any state machine exists, so the node and every operand right of the terminal one
  are simply dropped and the throw is the expression's value. `tests/compile-fail/` loses that case (its runtime
  twin is in `SuspendOperandOrderTests.kt`); the neighbouring arrangements it contrasted with are unchanged.
- **bir2cir (area:bir2cir): a `!!`, an elvis or a safe call in an argument to the LEFT of a suspending argument
  no longer aborts the compile.** `h(x!!, susp())` was REJECTED — "the operand … carries no static type" — on
  source the frontend had accepted. kotc lowers `x!!` to `{ var __nn = x; if (__nn != null) __nn else throw }`
  and stamps a type on none of the three nodes, so the deriver that has to type the operand's evaluation-order
  spill slot had nothing to read. It now reads a `cond` through its LIVE branch — a branch that never returns
  says nothing about the type of the value the other branch produces, so a `throw` arm cannot answer while the
  other arm can — and a `local` through the `var` the block itself declares. The value-nullable arms
  (`nullableWrap`/`nullableValue`/`safeCastValue`) also read their `elem` instead of a `type` slot no producer
  writes on them, so `n!!`, `n ?: 0` and `b?.size()` in the same position resolve too. Five shapes that were
  compile aborts are pinned as running tests.
- **bir2cir (area:bir2cir): a side-effecting operand to the left of an operand-position `try` no longer faults
  at runtime.** `f() + try { … } catch { … }` spills `f()` to a temp so it keeps evaluating before the hoisted
  try, and that temp's declared type was copied from whichever of `type`/`ret`/`dynRet` the node carried — a
  call node carries none, so the spill was declared `kotlin.Any`, and the emitted unbox read a value that was
  never boxed (`AccessViolationException`, process abort). The hoist now threads the lexical scope and derives
  the type the way every other spill site does.

- **bir2cir (area:bir2cir): `x in a..b` now evaluates `a`, `b` and `x` exactly once each, in that order.** Range
  membership is `(a..b).contains(x)`: the range is constructed first, so BOTH bounds always run, left to right, and
  the subject is read after them. The short-circuit fast path (`x >= a && x <op> b`) put the upper-bound test inside
  the lower-bound test's `then`, so a subject below `a` never ran `b` — `0 in lo()..hi()` silently dropped `hi()`'s
  side effect — and it read the subject before either bound, so a subject a bound assigns (`var x = -1;
  x in run { x = 5; 0 }..50`) compared the stale value and answered `false` where Kotlin answers `true`. The three
  operands are now bound to temps up front in Kotlin's order and the comparison legs read the temps. An operand is
  still spliced in place when re-reading it is free — `ValueStability.IsReReadable`, the one answer to that question
  (Q1), replaces a local "stable" set that had wrongly accepted any `local`, mutable or not — so an all-constant
  membership (`5 in 1..10`) still lowers to bare comparisons with no temp at all. The fix covers every form the fast
  path handles: `..`, `..<`, the `until` extension, `!in`, and the `Int`/`Long`/`Char` element types.
  `StringCharSequenceBridge`'s copy of that same "stable" set — harmless, because nothing evaluates between its two
  reads — is gone the same way, so there is no second answer left to drift.
- **bir2cir (area:bir2cir): an argument that never returns, to the left of a suspending one, is no longer treated
  as a value to carry across the suspension.** `pair(run { throw IllegalStateException() }, later())` refused to
  compile — the evaluation-order spill wanted a type for an operand that has no value — and the shapes it stood for
  (`run { return … }`, `error(…)`, any `Nothing`-typed call) were the same. Kotlin evaluates such an operand and then
  nothing else in the expression, *including* the suspension: it is the expression's value, not something to store
  and reload after a resume, and the operands to its right are unreachable. The suspend lowering now says so — the
  terminal operand becomes the value, the rest is dropped, and no spill slot is minted (one would also have left the
  state machine's receiver on the stack when control left through the throw). `bir-common/NodeType.cs` answers
  `Nothing` for an expression-position `throw`/`return`, which is what lets "this has no value" be told apart from
  "I could not derive its type" — the first is ordinary code, the second the dropped type the spill reports.
  The rule applies exactly where it is needed: only an operand with a suspension to its RIGHT is treated specially,
  because that is the only arrangement the spill would have got wrong. `pair(later(), run { throw … })` still
  suspends, resumes and only then leaves, and an argument that never returns in a SUSPENDING call's own list
  (`one(run { throw … })`, `sum(relay(), run { throw … })`) lowers as it always did. The one arrangement that
  remains refused is a never-returning argument to the LEFT of a nested suspension in a suspending call's own list —
  a suspension point that would have to be elided, which the state machine cannot express; the refusal names the
  shape and the workaround (`tests/compile-fail/SuspendTerminalArgumentBeforeSuspension.kt`).
- **kotc/bir2cir (area:kotc, area:bir2cir): an INLINE call's arguments now follow the same evaluation plan as every
  other call's — one evaluation each, in Kotlin's order, whatever the spliced body does with them.** `InlineSplice`
  bound each parameter to a temp in PARAMETER order, so a call that also filled a default ran the default in its own
  slot: `inline fun f(a: Int = t("A"), b: Int, c: Int = t("C"), block: (Int) -> Int)` called `f(b = t("B"))` ran
  A, B, C where Kotlin runs B, A, C — the inline half of the ordering defect the plan fixed for ordinary calls.
  kotc now emits a plan for a `callInline` too (`docs/bir-cir-spec.md` §2.7, granularity trigger (d)): the dispatch
  receiver, the extension receiver and every supplied argument become bindings, in that order. A spliced lambda is not
  a value and is not bound — a literal carrier and a by-name forward of the enclosing inline fn's own lambda parameter
  are the body being spliced — so a lambda-only inline call (`run { … }`) supplies nothing, binds nothing, and still
  emits no plan, exactly as the granularity rule says. `InlineSplice` consumes the bindings instead of minting a temp per parameter, so a body
  that reads a parameter twice, in a loop, or not at all no longer costs a redundant local; a filled default becomes a
  local of the spliced block, which is where Kotlin evaluates a default (in the callee's scope, after every supplied
  value). Consequences:
  - a binding is inlined back into its reader only when that reader sits on the node's EAGER SPINE. A read inside a
    spliced body's statements, a conditional branch, a loop or a closure happens at a different time, a different
    number of times, or not at all, so the value is materialised — which is what "evaluate it at the call, exactly
    once" means once the callee's body is the reader;
  - every value an inline call binds, and every default it fills, carries a type. That closes the staged hole the
    liveness work had to leave open: an untyped local, and an untyped evaluation-order spill in the suspend lowering,
    are now errors that name the lowering which dropped the type, not a `kotlin.Any` slot that silently boxes a value
    type behind a warning;
  - the passes that run between the splice and the plan lowering peel a `callEval` exactly as they peel a
    `valueBlock` when they ask what an expression produces, so a covariant construction spliced under a plan
    (`xs.partition { … }`'s `Pair`) is still widened to its declared slot; and the plan lowering folds the layer it
    creates — a block whose result is a block is one block — so a lambda body ending in a nested inline call
    (`xs.map { s -> s.let { it.trim() } }`) keeps the single layer the splice's own flatten used to guarantee.
- **kotc/bir2cir (area:kotc, area:bir2cir): a call's values are now ONE ordered evaluation plan, so a filled default
  can no longer duplicate a value, reorder a call, or be traded away for storage.** A Kotlin call evaluates its
  receiver, then each supplied argument, then the callee's omitted defaults, each exactly once — but on the CLR those
  values had TWO representations: expressions substituted into filled defaults, and independently hoisted `var`s
  sorted ahead of the call. Whenever a hoisted value turned out to be unholdable one of the two had to be abandoned,
  and every possible choice lost single evaluation, Kotlin order, or legal storage. That is why the same defect kept
  reappearing: three successive attempts each fixed one invariant by breaking another (the enumeration is at commit
  `cb4ff8d`, reachable via `refs/pull/270/head`).
  kotc now emits an ordered **call-evaluation plan** wherever a fill can give a value a second reader
  (`docs/bir-cir-spec.md` §2.7): a `callEval` node carrying the call's bindings in Kotlin evaluation order, with every
  reader — the call's own slot, a spliced same-module default, a reconstructed cross-module data-class `copy` field, a
  `@KotlinDefault` carrier's `{this}` / `{defaultArgParam n}` token — a `bindRef`, a pure read. A constructor
  delegation's plan rides the declaration as `delegationBindings`. bir2cir's `DefaultArgSplice` shrinks to
  materialise-and-reference (it fills the reserved binding and clones only reads), and the new
  `toolchain/bir2cir/CallEvalLowering.cs` turns each plan into locals once every splice has finished: a single-reader
  binding straight back into its slot, a shared one into a `var`, a delegation's into `preStmts` that ilemit emits
  ahead of the base call. Storage remains a separate decision, made later from liveness, and its refusals now name the
  value's source role ("the receiver of `copy`") instead of a minted id.
  Behavioural fixes that follow:
  - a cross-module data-class `copy` evaluates its receiver ONCE — `nextTriple().copy(second = 9)` ran the receiver
    once per omitted field ON TOP of the call's own use of it (three times for `Triple`, four when every field was
    omitted), and re-rendering it also put a receiver evaluation AFTER the argument;
  - a filled default no longer runs BEFORE the values the call supplies: `host().f()` logged the default before the
    receiver, `host().g(arg())` before both, and `host().h(c = arg())` before the argument;
  - an EXTENSION call site ran its whole default-filling pass twice, so a default that another default reads was
    rendered — and evaluated — twice (`"s".ext()` against `fun String.ext(a: Int = bump(), b: Int = a * 10)` called
    `bump()` twice where the non-extension form called it once);
  - a call to a facadegen-injected TOP-LEVEL function bound none of its values, the same fault as the `copy`
    receiver and unfired only because the splice happened to hoist values itself;
  - a byref-like argument at a call with defaults keeps Kotlin's order instead of being jumped by the fill's
    temporary (the coverage arrives here inverted: the fixture as written on the branch it comes from asserted the
    reverse, `"dT"`, while its own comment stated Kotlin required `"Td"` — the compromise it recorded was a property
    of the old shape, not of the CLR);
  - a filled default whose SLOT precedes a slot the call supplies no longer runs first: `f(a: Int = mk(), c: Int)`
    called `f(c = arg())` ran `mk()` before `arg()`, because the positional argument array's order is not Kotlin's
    when an omitted default sits to the left of a supplied one;
  - a generic base class's chained constructor defaults no longer produce an `InvalidProgramException` in a derived
    class with a different type-parameter frame — a delegation's owner instantiation is read from the call's type
    arguments, since a `: super(…)` is a statement whose own type is `Unit`;
  - the synthetic data-class `copy` is selected by its generated SIGNATURE (parameter names AND types mirroring the
    primary constructor), not by the name alone, so a user-declared `copy` overload cannot be mistaken for it.
  - a default that reads its RECEIVER in a generic callee no longer produces an `InvalidProgramException` at load:
    `class G<T>(val v: T) { fun one(a: T = v) }` left `G`'s positional type variable in a caller frame that has no
    such slot. Splicing a default now closes the callee's WHOLE type frame against the call site — everything a
    default may read (the receiver's property, a member call on it, the receiver inside a generic constructor's
    default, an extension receiver, the callee's own type parameter beside the owner's), not just the omitted
    parameter's own type;
  - a by-reference argument at a call with defaults keeps its position: an address is not a value, so the impure
    values its location is computed from (`byref(mk().f)`, `byref(a[i()])`) are evaluated at the argument's own
    position and the address is taken off those; when the location's root is a CALL, the invocation IS the evaluation,
    so the whole location moves there — into a `ref T` local when its declared type is a byref, else into a plain `T`
    local whose address the slot takes. A value read THROUGH a managed pointer now answers `EmitAddr` with that
    pointer rather than with the address of a copy, which fixes passing a `var x by byref(m())` delegate on to another
    `ref` parameter: `c.Swap(byref(a), byref(b))` swapped two temporaries and dropped both writes — verifiable IL and
    a silently wrong program;
  - a default's TYPE frame is closed whatever its expression reads. The closure was installed only for a default that
    splices one of the call's values, so a default mentioning the callee's type parameters only through TYPES
    (`fun <U> f(xs: List<Pair<T, U>> = emptyList())` in a `class C<T>`) rendered them open in the caller's frame —
    an `EntryPointNotFoundException` at the first call. Constructor delegations and enum entries reached it the same
    way;
  - nested defaults compose their type frames instead of replacing them, so a default filling a default filling a
    default closes every open type variable against the OUTERMOST call site, at any depth;
  - a GENERIC callee's non-constant default no longer arrives with the callee's type parameters open. The
    `@KotlinDefault` carrier holds the default as the callee wrote it, so its type parameters ride it as positional
    type variables; the splice now substitutes the CALL's type arguments into the materialized carrier before binding
    its value tokens, the way an inline body's splice already does. `fun <T> f(xs: MutableList<T> = mutableListOf())`
    omitted as `f<String>()` built a `MutableList<Any>` holding the right values — an `EntryPointNotFoundException`
    where the erased object met the declared slot, and unverifiable IL where it did not;
  - a stack-buffer slot taken BY REFERENCE evaluates its index once. The bounds check and the address computation are
    one access behind a single helper the read and the write share; as two independent pieces they each evaluated the
    index, so `Swap(byref(b[i++]), byref(b[i++]))` incremented `i` four times and swapped the wrong slots;
  - a local pinned out of a by-reference argument's location is always typed. `bir-common/NodeType.cs` is the one
    node-local "what type does this expression produce" derivation, shared with the suspend lowering's spill typing;
    a `kotlin.Any` fallback would box a value type and hide a type the CLR refuses, so an underivable node is
    reported as a hole in the deriver.
  The negative lane gains the shape where the plan genuinely has no CLR form — a byref-like argument at a call whose
  LATER value suspends — and its refusal names the value's source role rather than the minted binding id.
- **bir2cir (area:bir2cir): a suspend function no longer promotes EVERY local to a state-machine field — storage
  is now decided by real liveness, behind one gate, and a value the CLR cannot put in a field is refused at
  compile time instead of crashing at run time.** `CollectVarFields` spilled every `var` in a suspend body into an
  instance field of the generated state-machine class. For a byref-like (`ref struct`) value — `System.Span<T>`,
  `kotlin.clr.Span<T>` from `stackBuffer { … }.asSpan()`, any `ref struct` from a referenced assembly — the CLR
  refuses such a field, so the state machine failed to load with `TypeLoadException` even when the value never
  spanned a suspension, and a byref-like value captured by an ordinary lambda produced the same TypeLoad from the
  closure class. A byref-like PARAMETER failed either way, in a shape-dependent form: `TypeLoadException` when the
  body suspends (the state machine's parameter field), `InvalidProgramException` at the generated cold entry when
  it does not (no state machine is built, so there is no field to reject).
  New `toolchain/bir2cir/SuspendLiveness.cs` runs a precise backward liveness over the normalized body (an
  evaluation-order walk into use/def/susp/label/goto/brIf events, then a worklist solve over the induced CFG,
  with every point in a protected region reaching each catch entry and the region exit). A local needs a field
  iff it is live at a suspension point; everything else stays a `MoveNext` local. It is liveness, not a lexical
  interval: a value created and consumed inside each iteration of a loop whose body also suspends is accepted,
  as C# accepts it, while the same value carried across the loop back edge is refused. The walk also models the
  emitter reordering that makes an unspilled operand read at the RESUME point (`acc + f()` reads `acc` after `f`
  resumes), so an accumulator still gets its field.
  Every field the machine mints — spilled locals, parameters, captures, `$this`/`label`, and the synthesized
  `__aw$`/`__ord$`/`__cond$`/`__awaiter$` temporaries — now goes through a single `FieldStorage` gate, and a
  byref-like or `ref T` type reaching it is a diagnostic naming the declaration, the storage role, the type and
  the suspending callee it lives across. The suspend ABI (parameters, result, suspend-lambda captures) is checked
  unconditionally, since none of it has a "dead across" escape. The same legality oracle
  (`toolchain/bir-common/FieldLegality.cs`) serves the third minting site, `ClosureSynthesis`, so a byref-like
  closure capture is refused too. The three diagnostics mirror C# CS4007, CS4012 and CS8352 and are documented in
  `docs/dotkt-semantics.md` §4d. Two smaller drops closed on the way: an evaluation-order spill whose operand
  type could not be read was silently typed `kotlin.Any` — the spill now types every node kind that carries a
  type, and where a type is genuinely absent it warns, naming the function and the node kind, before falling back
  to `kotlin.Any` as before (the remaining source of untyped operands is InlineSplice, which mints its
  `valueBlock`s without a `type` slot; erroring instead of warning waits on closing that) — and a conditional
  lowered only because a branch ESCAPES no longer mints a field for its result.
- **kotc/bir2cir/facadegen (area:kotc, area:bir2cir, area:facadegen): a declaration with a Kotlin `context`
  parameter is now compiled correctly — previously every such call miscompiled.** Context parameters need no
  opt-in at language version 2.4, so this was reachable from ordinary user source. kotc emitted the context
  parameter into the declaration's parameter list but counted only the `Regular` parameters when building the
  call's argument list, the `sig`/`paramSig` overload key, the inline payload's `pc`, the local-function lift and
  the lambda/delegate parameter projection — so a call passed a SHORT argument list against a longer method. The
  observed failures were `System.InvalidProgramException` at run for `with(c) { g(5) }` (no diagnostic at compile
  time), a silent `null` argument when the context type was generic, `bir2cir: sanity: 'local' references
  undeclared local 'c'` when an omitted default read the context parameter together with a value parameter, and
  `inline splice: cannot splice … no [KotlinInline] payload found` for an `inline` context function. The fix is one
  rule applied at every site: **`Context` and `Regular` parameters are one positional sequence — the emitted
  parameter list, the argument list, the overload key and the `@KotlinDefault` index all count
  `[__self?] + contexts + regulars`** (`docs/dotkt-semantics.md` §5i). The previous refusal for an omitted default
  that reads a context parameter is deleted rather than widened: the default is now bound by symbol to this call's
  context argument, exactly as an earlier value parameter or a receiver already was.
  Fixed across functions (top-level, member, extension, member-extension, companion, interface/`override`,
  `suspend`, `inline`, generic, `vararg`/overloaded, local), properties (top-level, extension, member, companion,
  `var` get+set), multiple context parameters, defaults reading a context parameter alone or together with a value
  parameter and a receiver, lambdas capturing a context parameter, the stdlib's own `contextOf<T>()`, and
  **context function types** (`context(A) (B) -> C` — the lambda's context parameter is a physical delegate
  argument and was dropped from the lifted method while the invoke passed it, a silently wrong result).
  Cross-module, each context slot now carries a `[KotlinContextParameter]` marker (kotc `mods.context` ->
  bir2cir -> facadegen -> the FIR injector), so a consuming Kotlin module restores it AS a context parameter and
  keeps writing `with(scale) { scaled(5) }`; without the marker the same physical method surfaced as a plain
  leading value parameter, a Kotlin SOURCE break at the module boundary. `docs/user/kotlin-on-clr-differences.md`
  and `docs/user/supported-features.md` claimed context parameters were rejected outright; both are corrected.
  A context FUNCTION TYPE (`context(A) B.(D) -> E`) round-trips too, which needed its own carrier: fir2ir ERASES
  which of a function type's leading arguments were contexts (at IR level `context(A) B.(D) -> E` is *identical* to
  `B.(A, D) -> E`), so bir2cir stamped `[KotlinExtensionFunctionType]` and facadegen promoted the delegate's first
  argument — the CONTEXT — to the restored receiver. A consumer's `evaluate { this.n }` then read the context's field
  instead of the receiver's and returned the wrong number with no diagnostic. kotc now captures the arity from FIR
  before it is dropped (`kotc.frontend.ClrContextFnTypes`) and carries it as the slot fact `ctxFnType`/`retCtxFnType`
  -> `[KotlinContextFunctionType(N)]` -> facadegen's context split -> the injector's `ContextFunctionTypeParams` cone
  attribute. A lambda LITERAL of such a type needed one more fix: the lift had no binding for the lambda's own
  receiver, so the body's `this` fell through to `{k:this}` — the enclosing instance, or nothing in a static lift. It
  now mints a name for the receiver parameter and binds `this` to it, as the inline splice carrier already did. That
  ALSO closes the context-free sibling, where the same gap made `val f: Int.(Int) -> Int = { d -> this + d }` throw a
  NullReferenceException; both lift shapes (non-capturing static and capturing closure) are covered.
  The arity is keyed by the slot's file path and its END source offset — the one offset FIR and IR always agree on —
  recorded from a DECLARATION-only walk (never a body, initializer or default value, because a callable nested in an
  expression body ends where its enclosing declaration ends and the two arities would land on one key). Carried for
  the stdlib's own `kotlin.context(...)` family too, which compiles through the other frontend phase. The table is
  cleared once per pipeline execution: it hangs off an object, so a HOSTED kotc could otherwise read a previous
  compilation's entry — a latent hazard closed by construction, not a bug that was reproducing (today's launcher
  execs a fresh JVM per invocation and each pipeline runs once).
  Also fixed in the same family: a cross-module MEMBER (and member-extension) call never emitted a positional
  `defaultArg` placeholder for an omitted default — it built `args`/`argTypes` from the expressions that happened to
  be present, so the omitted slot was DELETED and a later provided argument slid into it (`h.pick(b = 3)` bound `3`
  to `a` and zero-filled `b`). Those paths now use the same positional filler and carry `sig`, and the
  `@KotlinDefault` metadata lookup — previously constructors and top-level functions only — covers members.
  Regression coverage: `tests/basic/fixtures/ContextParameterTests.kt`,
  `tests/coroutines/fixtures/SuspendContextParameterTests.kt`, and `crossModuleContextParameters` in
  `tests/roundtrip/consumer/KotlinMetadataRoundtripTests.kt` over `tests/roundtrip/producer/Ctxparams.kt`.
- **kotc/bir2cir/ilemit ([tmyt/dotkt#68], area:kotc, area:bir2cir, area:ilemit): a captured `var` written from a
  local class, an object expression, a lambda or a local `fun` is now heap ref-celled under EVERY emission root and
  for every one of those boundaries — previously only a function body, and never a local `fun`.** The promotion of a
  captured-and-mutated local to a shared `dotkt$Ref<T>` was decided per emission root, and only two roots decided it —
  `method()` and `topLevelAccessorMethod()`. Everywhere else the emitter ran with an empty set, so a constructor body,
  an `init` block, a property or field initializer, a member accessor, a default interface method, a static-field
  initializer, a rich-enum entry argument and a `@KotlinDefault` default-value expression each aborted the compile
  ("does not support an object expression / a local class that writes to a captured outer variable") — while the same
  shape written with a LAMBDA hit no guard at all and emitted a `setLocal` against a local that does not exist in the
  lifted closure, which bir2cir then rejected as an undeclared local. Needing a cell is a property of the VARIABLE,
  not of the frame emitting it, so the set is now computed ONCE per module (`BirEmitter.initRefCells`, driven by
  `ClrBackendPhase`) and is identity-keyed; the per-root save/restore is gone with it. This also makes the two frames
  that emit ONE default-argument expression — the callee's `@KotlinDefault` carrier and the omitting call site —
  agree. The two guards that used to reject the shape now report a broken emitter invariant instead of an unsupported
  language construct (they can only fire on a mutated capture that is not a `var` local, which valid frontend IR
  cannot produce).

  A local `fun` is now a capture boundary too. It lifts to a static method whose captures are BY-VALUE parameters, so
  `fun f(): Int { var n = 0; fun bump() { n++ }; bump(); bump(); return n }` compiled clean and returned 0 instead of
  2 — the only boundary whose missing cell lost the write with no diagnostic. Reaching a capturing local fun from
  another boundary failed loud, since the lift supplies its captures at the CALL SITE. Closing both halves took: one
  capture scan shared by the lambda/local-fun and object/local-class walks, which follows a call INTO a local fun and
  a CONSTRUCTION of a local class (cycle-guarded), so a lambda, object expression or local class that merely calls
  `bump()` captures `bump`'s `n` too; a local declaration recognized by the frontend's own `Local` visibility rather
  than by probing its IR parent, which is exact for a class as well and needs no `init { }` special case; both lifts
  RESTORING the enclosing frame's capture binding instead of dropping it, so a local fun or local class declared
  inside a closure/object/local-class member no longer leaves that frame reading a bare local it does not have (in an
  `inner class` member, dropping it left every LATER member reading the enclosing instance off the inner one); a
  capture keeping its own name unless something else already owns it in the same namespace — the lifted class's own
  fields and constructor parameters, the lifted local fun's own value parameters, or an earlier capture — in which
  case it moves into a `cap$` prefix; the lift also generic over the type operands in its BODY, like the lambda lift,
  and over the BOUNDS of the type parameters it re-declares; kotc recording on the lifted method which enclosing type
  variable each of its own type params re-declares (`_syntheticTypeArgs`, the same key and meaning ClosureSynthesis
  already derives for a lifted closure CLASS) and bir2cir's `SharedSyntheticSynthesis` applying its synthetic-frame
  remap to METHODS as well, so a generic cell used inside the lift is constructed in the method's own parameter space
  and a lifted synthetic can supply the parameter constraints when the celled `var` is declared inside it; that binder
  running collect/bind/construct in separate passes, so its result no longer depends on declaration order; and
  ilemit's `setField`/`staticFieldSet` taking their owner through `ParseOwnerSlot` like the field READ paths beside
  them instead of collapsing a constructed-generic owner to its open name — writing a `Cell<T>` field from a generic
  static method emitted `Cell<!0>::v` where the frame has only `!!0`, which the runtime rejected as a bad image.

  A lifted class also keeps the BOUNDS of the enclosing type parameters it re-declares (they were emitted as bare
  names, so a member needing one had no constraint to dispatch through — wrong metadata, no diagnostic). A local class
  INHERITING from a capturing local class, and a `this(...)` delegation between two constructors of one, both forward
  the captures ahead of the source-level arguments. Ref-cell identity keys on the bounds of the variables its element
  mentions, not the printed element alone, so two same-file generic classes no longer share one cell and one of them
  its constraint. `::localFun` lowers through the lifted static — a plain delegate with no captures, and a closure
  over the captured values when there are some, including the enclosing instance and enclosing generic parameters.
  Callable references participate in the same transitive declaration-reachability scan as calls and constructions;
  a capturing local-class constructor reference binds the hidden lifted-ctor arguments in a closure while preserving
  its Kotlin-visible arity. Finally, every function-local variable is assigned a BIR frame slot by IR declaration
  identity rather than source spelling, so shadowed locals and two distinct captures with the same name cannot alias
  in ilemit's flat local table; capture names (including the preferred `__outer`) are collision-free bindings rather
  than downstream semantic markers. Suspend-lambda captures likewise use compiler-only descriptor bindings and carry
  their enclosing-frame value explicitly, so the body and construction site never depend on sharing a source name.
  With declaration identity preserved in BIR, bir2cir's former `DisambiguateShadowedVars` JSON scope reconstruction
  has been deleted; coroutine lowering now only spills the slots the frontend actually declared.

  Regressions: `tests/basic/fixtures/CapturedVarRefCellTests.kt` and the asynchronous shadowed-slot case in
  `tests/coroutines/fixtures/SuspendCaptureTests.kt`; the function-body root stays pinned by `LambdaTests.kt`'s
  `localClassObject`. The two `tests/known-fail/localfun-capture-write*` reproductions this replaced are deleted.

- **kotc (area:kotc): a default argument that reads the DISPATCH receiver of a member EXTENSION function no
  longer reads it off the extension receiver.** `filledArgs` collapsed the call's receivers to one expression
  (`extensionReceiver ?: dispatchReceiver`) and bound *every* receiver parameter to it, so a callee with two
  receivers got the wrong one: `class Host(val k: Int) { fun Int.scaled(f: Int = k) = this * f }` emitted the
  filled default as `Host.get_k()` on the extension receiver's value — `"recv": {"k":"const","type":"kotlin.Int",
  "value":3}` in the BIR — which bir2cir and ilemit projected faithfully into a `Host` member call on an `Int`:
  a `NullReferenceException` at runtime with nothing loud at compile time. Each receiver parameter now binds to
  the call's receiver of its own kind, and the enclosing-`this` chain hangs off the dispatch receiver rather than
  the collapsed one (an inner class's member extension reading `this@Outer` failed identically). Only a receiver
  the default actually READS is emitted, so an unread receiver's lifted lambdas cannot leak into the file class.
  This is the same-module substitution path only: the cross-module `@KotlinDefault` carrier still refuses a default
  that reads the DISPATCH or an enclosing-instance receiver (a pure extension-receiver default is carried and
  filled), and a cross-module data-class `copy` still evaluates a non-stable receiver once per omitted field —
  both unchanged here. The value-param arm (`f: Int = base`) and the single-receiver top-level
  extension arm (`fun Int.f(x: Int = this)`) are unchanged and pinned. Covered by
  `tests/basic/fixtures/DefaultArgumentTests.kt` `defargsReceiverKind`.
- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir, area:ilemit): calls into referenced Kotlin helpers
  now carry and link the physical declaration signature instead of falling back to a same-name/same-arity method.**
  bir2cir preserves the frontend Kotlin descriptor before nullable-generic erasure, resolves the referenced
  declaration after owner attribution, then carries that declaration through actual/type-alias lowering; synthesized
  alias helpers likewise carry their flattened method type-variable scope. ilemit consumes that physical signature
  exactly. In particular,
  `Collection<T>.maxOrNull()` from inside a generic function keeps its `IEnumerable<T>` descriptor and cannot
  silently bind the neighboring `IEnumerable<Double>` overload. A missing descriptor match is now a link-time ABI
  error; the former standalone known-failure is an in-process NUnit regression covering concrete, value-generic,
  and reference-generic calls.

- **bir2cir ([tmyt/dotkt#251], area:bir2cir): constructor parameters now carry their `[Nullable]` annotation, so a
  nullable ctor parameter stays nullable across a module boundary.** The declaration-position NRT walk
  (`toolchain/bir2cir/DeclNullableFlags.cs`) visited methods, fields, properties and nested types but never `ctors`,
  while the stamp that turns those bytes into `[Nullable]` already walked constructor parameters — so every
  constructor parameter was emitted unannotated. A Kotlin consumer of a DotKt library therefore saw `C(val s:
  String?)` as taking a non-null `String` and `C(null)` failed to compile with "null cannot be a value of a non-null
  type"; a C# consumer lost the annotation outright. The walk now covers constructor parameters, matching the
  declaration kinds the stamp traverses. Regressions:
  `tests/roundtrip/producer/Nrt.kt` + `tests/roundtrip/consumer/KotlinMetadataRoundtripTests.kt`
  (`nullableConstructorParams`, cross-module Kotlin) and
  `tests/roundtrip/bidirectional/consumer/BidirectionalTests.cs`
  (`KotlinNullableConstructorParameterCarriesNullableAttribute`, the C# metadata assert).

- **bir2cir ([tmyt/dotkt#228], area:bir2cir): an auto-property no longer emits a same-named field, so
  reflection-driven .NET libraries (JSON.NET) can serialize a Kotlin type again.** An accessor-routed property
  emitted its backing field under the property's own name, so the CLR type carried `Value` as BOTH a property and a
  field; Newtonsoft groups candidate members by name and could not resolve the pair — `JsonConvert.SerializeObject`
  silently produced `{}` and deserializing back threw on the null constructor argument. The backing field is now
  emitted as `<Value>k__BackingField` (the C# auto-property convention) with
  `[System.Runtime.CompilerServices.CompilerGenerated]`. The name is unwritable in Kotlin — even backtick-quoted, the
  frontend rejects it with "name contains illegal characters: <>" — so it can never collide with a user declaration.
  The rename lives in bir2cir (`toolchain/bir2cir/BackingFieldRename.cs`), which owns the Kotlin↔CLR representation;
  kotc keeps emitting the pure Kotlin identity. Only accessor-routed properties are affected (including one with a
  custom accessor over a backing field) — `lateinit var`, `const`, a delegated `p$delegate`, companion/top-level
  statics and the `@ClrField` opt-out emit no CLR property and keep their plain field name. An `@JvmInline value
  class`'s erased-value getter is now read off the property that owns the single instance field instead of being
  spelled from that field's name. Documented in `docs/dotkt-semantics.md` §5h and
  `docs/design-clr-property-model.md`; regression in
  `tests/interop/consumer/fixtures/AutoPropertyBackingFieldTests.kt`.

- **bir2cir/facadegen ([tmyt/dotkt#147], area:bir2cir): nested nullable-generic shapes now round-trip through every
  declaration slot.** `[KotlinNullableGeneric]` records the pre-erasure Kotlin `Holder<T?>` shape on method and
  constructor parameters, fields, and properties as well as returns; facadegen restores that shape before NRT
  composition, including ordinary function-type trees and public interface bridges synthesized after erasure. Restored
  user types keep their namespace-qualified identity. A separately compiled Kotlin consumer now retains generic
  inference and member types instead of seeing `Any?`, while metadata regressions cover raw fields and same-simple-name
  types directly. Bare top-level `T?` remains the distinct dual-representation work tracked by #86.
  
 - **packaged-SDK gate ([tmyt/dotkt#250], area:packaging): each `dotnet new` template case now gets its own scratch
  hive, so concurrent worktree gates no longer false-RED each other.** `tests/packaged-sdk/run.sh` isolated NuGet
  state but installed/uninstalled `DotKt.Templates` in the machine-global template store under `$HOME`. Two
  `make verify` runs in different worktrees carry the same package id at the same version, so one run's `--force`
  reinstall or its cleanup uninstall landed between the other's install and its scaffold — reported as
  `ThrowMoreThanOneMatchException` / "Could not find the template package containing template 'DotKt.Templates.Cli'"
  on a gate that had nothing wrong with it. Every `dotnet new` invocation now goes through a `dotnet_new` helper that
  passes `--debug:custom-hive` pointing at the case's own hive inside the run's scratch workspace, which is wiped at
  the start of each run: nothing is installed into or uninstalled from the machine-global store any more, and the
  uninstall trap that existed only to keep it clean is gone with it. The hive is per case rather than per run because
  installing a package a hive already carries is either a hard error or, with `--force`, a second registration for the
  same id that makes every later scaffold ambiguous with the very same error — which the old cross-case uninstall had
  been hiding. `--force` goes with it, since a fresh hive never has anything to force over. Each case also asserts
  that the packed nupkg really landed in its own hive, so an SDK that silently stopped honouring the switch cannot
  restore the race behind a green gate.

- **kotc ([tmyt/dotkt#235], area:kotc): a constructor call may omit a default argument whose value reads an earlier
  constructor parameter — `class Rect(val w: Int, val h: Int = w * 2)` + `Rect(3)` compiles and yields `h == 6`.**
  kotc carried two default-argument filling passes: the one behind ordinary calls already inlined such a default with
  each referenced parameter rewritten to that call's filled argument, while the one behind `new` / array constructors /
  a lifted local class's `new` refused the identical shape. The constructor path is folded onto that pass, so one
  implementation now serves both. Folding takes the UNION of the two passes' behaviors: constructor arguments gain the
  call path's arg-slot coercions (byref shaping, `Nullable<T>` unwrap, boxed-`Any` narrowing), calls gain the
  constructor path's facadegen constant-default fill (#134) and its loud refusal when an unfillable cross-module
  default is omitted with a LATER argument provided (that slot-shift was previously a silent miscompile on the call
  path). The remaining constructor call sites are routed through the same pass with it: a `: this(…)` / `: super(…)`
  **delegation** and an **enum entry**'s `NAME(args)` (including a per-entry body's base call) dropped an omitted
  default's slot outright, sliding every later argument one place down — `class D(val a: Int, val b: Int = a * 2) {
  constructor() : this(3) }` produced an unloadable `D..ctor()`. A constructor default that reads an **enclosing
  instance** (`inner class In(val x: Int = outerProp)`, and the same default on a member of an inner class) now lowers
  too: the enclosing `this` binds to the call's dispatch receiver, a member call and each further level through the
  `__outer` capture field (it previously emitted the *caller's* `this` and threw `InvalidProgramException`). Every
  binding is now made BY SYMBOL rather than by rewriting the emitted `this` token, which also fixes an argument that
  reads `this` being re-pointed at the call's receiver — `class D { val k = 5; val c = C(7); fun f() = c.m(k) }`, with
  `C.m(a: Int, b: Int = a * 10)`, filled `b` from `c.k` by emitting `D::get_k` against a `C` receiver. Filling saves
  and restores the substitutions it installs, so a closure that captured the callee's own parameter keeps its binding
  across a recursive omitting call; a delegation's args read the leading capture PARAMS, which (unlike the capture
  fields) are already live before the constructor body.
  **A value a same-module filled default splices is now evaluated exactly ONCE**: the receiver (or enclosing instance) a
  `= this` / `= outerProp` default reads, and the earlier argument a `= a * 10` default reads, are bound to a call-site
  temporary in a wrapping `valueBlock`, so `mkOuter().In()` runs `mkOuter()` once — the constructor and its default now
  see the SAME instance — and `f(next())` calls `next()` once however many defaults read it. A stable value (a literal
  or an immutable local/parameter read) still splices directly, so an ordinary call emits no temporary, and every
  non-stable value to the left of a bound one is bound with it so the evaluation order stays Kotlin's. This holds at
  EVERY site that fills a default: a constructor delegation and an enum entry ride a declaration rather than an
  expression, so their temporaries are declared by the first argument. **Cross-module omissions bind too**
  (area:bir2cir): `DefaultArgSplice` used to deep-clone the call's receiver/argument into the default it filled, so
  `sideEffect().substringAfter(".")` ran `sideEffect()` twice and a value two defaults read ran four times — it now
  hoists as it fills, including a filled default a later default reads (`chain(a, b = bump(), c = b * 10)` calls
  `bump()` once). Working on emitted JSON it cannot tell a `val` from a `var`, so there only a literal or `this` stays
  spliced in place (`docs/dotkt-semantics.md` §7).
  **Cross-module too** (area:bir2cir with it): a CONSTRUCTOR now carries the same `@kotlin.clr.KotlinDefault` stamp a
  function does, facadegen surfaces its non-constant defaulted parameter OPTIONAL, and `bir2cir.DefaultArgSplice` fills
  the `{"k":"new"}` placeholder from the reference dll — so a consumer of a DotKt library can write `Rect(3)` against
  `class Rect(val w: Int, val h: Int = w * 2)` and get `h == 6`, where it previously failed the frontend with *no value
  passed for parameter 'h'*. The splice keys a constructor as `<type>|.ctor|<declared parameter count>`
  (`ReferenceMetadataIndex` now scans `GetConstructors` alongside `GetMethods`), and the stamped index is the parameter's
  position in the emitted constructor's own parameter list. Both constructors and methods are additionally keyed by the
  declared parameter VECTOR, so two same-arity overloads carrying different defaults resolve their own instead of the
  arity key serving whichever declaration the metadata scan reached first; a pair the key cannot separate is refused. A
  cross-module `: super(…)` is filled too — its arguments ride the constructor declaration rather than a call node, so
  they were previously left unfilled and aborted the build; lacking a signature vector there, the target constructor is
  identified by arity alone (a base with same-arity constructor overloads whose defaults disagree is refused).
  Lower slots fill first, so a chain fills too
  (`class Tri(a, b = a + 1, c = a * 100 + b)` consumed as `Tri(2)` gives `c == 203`). A metadata-representable (Tier-1)
  constant still fills from the facadegen metadata and never becomes a placeholder; a ctor default reading an enclosing
  instance is still refused at stamp time. Covered by `tests/basic/fixtures/DefaultArgumentTests.kt` (`defargsCtor`,
  `defargsCtorDelegation`, `defargsCtorEnclosingInstance`, `defargsSingleEval`, `defargsEvalOrder`) and by the round-trip
  lane's `nonConstDefaultArgs` (`tests/roundtrip/producer/Nonconst.kt` + `tests/roundtrip/consumer/`).

### Changed

- **tests: an unexpected PASS now reddens every machine-readable XFAIL gate (#357).** Round-trip,
  compile-fail and packaged-SDK used to print `FIXED — remove it from the xfail list` but still exit zero, so a
  stale entry could outlive its defect indefinitely unless somebody noticed an advisory line in CI output.
  `scripts/lib.sh` now records both NEW failures and FIXED entries, and the three lanes share the same clean
  verdict: every actual failure must be listed, and every listed failure must still occur. The ILVerify complete-set
  audit already enforced the same rule. A build-free self-test injects one XFAIL, one NEW failure and one FIXED
  entry so deleting either half of the final verdict cannot look green.
- **tests: an ilverify baseline entry that masks NOTHING now reddens the gate.** `ILVERIFY_XFAIL` in
  `tests/run-ilverify.sh` only ever classified findings, so a key whose defect had been fixed stayed in the list
  silently — and kept masking, ready to absorb whatever finding lands on that method next. (One had already
  rotted that way: `GenericMetadataRoundtripTests::nestedGenericCollectionsRoundTrip()` never produced the
  finding its key describes — the same commit that added the key wrote the fixture to omit the generic-member
  read that would have surfaced the Root-V variance collapse, and says so in the fixture's own comment. It is
  pruned here.) `tests/run-ilverify.sh --audit-baseline` reports
  every unmatched key with `scripts/lib.sh`'s `xfail_diff` wording, `FIXED … remove it from the xfail list`, and
  then exits non-zero. That strict verdict is now shared by every XFAIL lane; the ILVerify audit remains opt-in
  because a partial assembly set cannot distinguish an unmatched key from a fixed finding. A
  stale ilverify key is a live substring filter over future findings, not just a stale name in a fail-set, so
  this lane stays red until the entry is pruned. `tests/run-nunit-tests.sh` passes the flag because it
  verifies the COMPLETE emitted set; `tests/packaged-sdk/run.sh` verifies a two-assembly subset, where an
  unmatched key means "not in this subset" and the audit would be a false red.
- **tests: the surviving ilverify/round-trip baseline reasons name the issue that actually owns them.**
  `ArrayTests::copyOfGrowsWithNullTail`, `GenericMetadataRoundtripTests::nullableGenericMembersRoundTrip` and the
  `roundtrip-nullable-vt-generic` scenario cited #127, #18 and #109/#127 — all closed, and #18 unrelated. All
  three are the one open nullable-generic representation design, #86. The two dead `tests/known-fail/` references
  (`tests/README.md`, `scripts/gate.sh`'s routing table) are dropped; the directory does not exist.
- **bir2cir (area:bir2cir): the three result-type stamps have ONE precedence — `sty`, then `ret`, then `dynRet` —
  stated once in `bir-common/NodeType.cs`.** `sty` is the frontend's INSTANTIATED static type, stamped per call
  site; `ret` is emitted only when the callee or its owner is GENERIC, which is exactly where it may name the
  UNinstantiated declared type. Reading `ret` first therefore typed a generic-owner call by its declaration
  instead of by its use, and four separate restatements of the order had accumulated — the core, an explicit
  "deliberately not unified yet" override on `StaticType.Surface`'s call/field arm, the awaited-value read in
  `EmitSuspensionPoint`, and `IsSuspendFunctionValue`'s own copy. The core is flipped and the other three are
  gone: the call/field/`bindRef` family needs no arm at all now that `sty` wins everywhere, and the two suspend
  readers ask the shared deriver. The order rests on an invariant now written into `docs/bir-cir-spec.md` §2.7
  and cited at the flip site: **a pass that changes a node's result type rewrites or deletes its `sty`** — a
  stale stamp on a retyped node is a bug in that pass, not a reason to demote the stamp. Byte-identical CIR
  across the reference and runtime stdlib corpora (497 files) and across the test corpora.
- **bir2cir (area:bir2cir): a slot whose type cannot be derived is a REFUSAL, not a `kotlin.Any` box.**
  `kotlin.Any` in a declared slot is never neutral — it boxes a value type, it makes the read unverifiable
  without an unbox, and it turns an earlier layer's dropped stamp into a runtime `InvalidCastException` far from
  the cause. Four fallbacks are retired. The suspend lowering's conditional temporary is now typed from the
  conditional's LIVE branch, which is what made every value-type `x?.suspendFoo()` crossing a suspension box and
  unbox; `TryValueOperandHoist`'s spill type (formerly `GuessType`, carried as a KNOWN GAP) refuses instead of
  boxing, since a null from the shared deriver there is a hole in the DERIVER; and the two method-return indexes
  the stage-0 typer consults refuse a declaration carrying neither `suspendRet` nor `ret` — surveyed as
  impossible by construction (0 of 7308 stdlib declarations, and every kotc method emitter writes `ret`
  unconditionally). What remains is ABI rather than fallback (the cold entry's `Any?` return, `Continuation<Any>`,
  `Result<Any?>`, the non-generic `IEnumerable` element, an undeclared catch filter), and the site-by-site triage
  — LEGITIMATE-ABI / RETIRED / FOLLOW-UP with its precondition — is `docs/dotkt-semantics.md` §7b.
  These refusals cannot fire on frontend-produced BIR, so `tests/ir/run-lowering.sh` gained a `reject-*` half
  (synthetic BIR that bir2cir must refuse, with the wording pinned) to keep them from being silently defeated;
  all four fixtures are calibrated — the previous binary accepted each one.
- **bir2cir (area:bir2cir): `StaticType.Surface` is founded on the shared node-local deriver
  (`bir-common/NodeType.cs`) instead of restating it.** The two derivations had drifted into disagreeing about
  five kinds — the nullable wrap/unwrap slots, an untyped `cond`, `Nothing`, the `&&`/`||` result, and the two
  spellings of an array type — and a kind classified one way for a spill slot and another way for an operand
  classifier is exactly the drift the shared file exists to prevent. Each disagreement is now either fixed in
  the core (so both consumers inherit it) or one named adapter (`ArrayAsFqn`: the core answers structurally,
  this reader's classifiers are name-keyed). `Surface` keeps only the arms the core cannot answer — the ones
  needing the enclosing lexical scope — and delegates the rest, including the whole call/field family once the
  stamp precedence was unified (see the `sty`-first entry above).
- **A call value NOTHING reads is now evaluated unless evaluating it is genuinely unobservable
  (area:bir2cir, area:kotc; recorded as `docs/dotkt-semantics.md` §7a).** Kotlin evaluates a call's receiver and
  every supplied argument whether the emitted CLR shape has a slot for the value or not; the backend was skipping
  the evaluation for anything that *looked* like a pure load. Two of those are not: a **static-field** read (and an
  **enum-value** read with it) runs the declaring type's initializer — which on this backend is where a top-level
  property initializer and an `object`'s body live, so it can print, throw or mutate — and an **instance-field**
  read dereferences, so it throws on a null receiver, which a platform type makes reachable (§9a). `IsDroppable`
  (Q2 of the value questions) is narrowed to the loads whose evaluation genuinely cannot be detected: `const`,
  `this`, `local`, `bindRef`, `default`, `classRef`. Anything else with no reader becomes a local nobody reads —
  at most one local, and only at a call site that supplies a value the emitted shape cannot place. The
  zero-reader case is now decided by that question ALONE: a `stable` binding used to vanish the same way through
  the inline path without ever being asked whether its evaluation mattered ("may be read twice" was standing in
  for "may be read zero times"). `KClassMemberBinding`'s `value::class` const-fold, which asks the same question
  about the receiver it folds away, delegates to `IsDroppable` instead of restating a narrower ad-hoc set —
  widening the fold to a `this`/plan-read receiver of a known-final builtin type, while explicitly rejecting a
  `classRef` receiver: the DOUBLE class literal `(Int::class)::class` reflects the `KClass` VALUE, and a
  `classRef`'s type slot names the type it REFERS to rather than the type it IS, so folding it would answer "Int"
  for a receiver that is not an `Int`.
  The one shape that relied on the old, too-generous answer is gone at the producer: a plain `companion object` is
  flattened onto its enclosing class and a projected .NET static holder has no instance either, so **kotc no longer
  mints a call-evaluation-plan binding for a receiver naming one** — at any receiver site, ordinary or inline. It
  was a binding nothing could read, holding a read of an `INSTANCE` field this representation never emits (the
  inline emitter already named it a "dangling token"), surviving only because the drop hid it; there was never a
  value there to evaluate. A REAL `object` and a super-typed companion are the opposite case and keep their
  binding: their `INSTANCE` exists and loading it runs the object's own body, so it is an observable evaluation
  that Kotlin orders BEFORE every argument — without the binding, `O.f(side())` let `side()` run first.
- **bir2cir (area:bir2cir): the by-reference argument's location pins ask their own question.**
  `CallEvalLowering`'s `LocationHasPinWork`/`PinLocationOperands` shared Q2's implementation, but they decide
  whether a node is a link of an addressable location's own path — pinning one into a local would take the address
  of a COPY for a value type, so a callee writing through the `byref` would miss the real storage. That is storage
  identity, not side effects; it is now `StaysInLocation`, next to the code that asks it, and the Q2 narrowing
  above leaves it untouched.
- **bir2cir (area:bir2cir): every "is this value pure / stable" predicate is now named after the question it
  answers, and each question has exactly one home.** Five different questions were being asked under three
  interchangeable-sounding names, which invited the assumption that a kind classified one way in one of them was
  a bug in another. `bir-common/BindingStability.cs` becomes `ValueStability.cs` and heads the roster;
  `IsStable` becomes `IsReReadable` (Q1 — may this value be read more than once, with other evaluation in
  between) and `IsTriviallyPure` becomes `IsDroppable` (Q2 — is evaluating it unobservable, so a binding nothing
  reads may be skipped); `TryValueOperandHoist`'s `PureKinds`/`IsPure` become `StackNeutralKinds`/`IsStackNeutral`
  (Q4 — may it stay in its slot when a later sibling hoists out of the protected region); `CallEvalLowering`'s
  `IsLvalueFormer` gains the question it answers (Q5). Naming Q3 — the suspend lowering's resume-stability set —
  is what made it answerable, and the operand-plan entry above then RETIRED it outright, so the roster ships with
  FOUR questions and no resume-stability predicate at all. The control-transfer kind set that the suspend
  lowering stated twice — once inside its impure set, once inside `EscapesExpression` — is now one named constant
  (and the impure set that was its other reader is gone with Q3). Two kind sets lost entries no producer mints at the point they are consulted: `TryValueOperandHoist`
  (`param`, `constNull`, `null` — no producer anywhere) and `CallEvalLowering.EagerKinds` (`newList`, `newSet`,
  `newMap`, `clrPropGet`, `clrPropSet` — minted by passes that run hundreds of lines after it). Pure refactor:
  the emitted CIR corpus is byte-identical.

- **kotc (area:kotc): one home per stability question in the BIR emitter.** `bindOnce` — the splice-once binder
  behind a when-subject, a safe call and a range membership — carried a byte-identical inline copy of the
  call-evaluation plan's `isStableValue` predicate, so the two could drift apart while both claimed to answer
  "is this value free to be re-read and to move past another value". It now calls `isStableValue`, which is the
  single implementation. `isStableAddress` is renamed `isStableLocation`, because it answers a DIFFERENT question
  — whether an argument's *address* may be taken twice and moved, which holds for a mutable `var` too — and a
  name that echoed the value predicate invited the immutability clause to be "restored" into it; its doc now
  states the question and why that clause is deliberately absent. The `BirEmitterCallPlan` granularity note
  pointed at a `BirEmitter.callNeedsPlan` that does not exist and never did; it now describes what really gates a
  `callEval` — a plan scope installed around every call that costs nothing when empty, and, inside it, the
  `planNeeded` tests of `filledArgs`/`filledExternalArgs` (§2.7 triggers (a)-(c)) together with the unconditional
  binding a `callInline` performs (trigger (d)), which the old note did not mention at all. Behavior-preserving:
  emitted BIR and CIR are byte-identical over the full stdlib corpus (ref + runtime, 1001 files) and a
  198-source fixture sweep.

## 0.9.7 (2026-07-22)

### Added

- **CLR event model — a Kotlin class can now IMPLEMENT and RAISE a .NET interface event ([tmyt/dotkt#187],
  [tmyt/dotkt#113], area:kotc, area:bir2cir, area:ilemit): first-class WPF/Avalonia/WinUI MVVM
  (`INotifyPropertyChanged`) on Kotlin-on-CLR.** `class ViewModelBase : INotifyPropertyChanged { override val
  PropertyChanged by clrEvent() }` now compiles to a loadable type: kotc synthesizes `add_/remove_/raise_<E>` accessors
  + a backing delegate field; bir2cir (`ClrEventImplBinding`) resolves the concrete `EventHandlerType` delegate off the
  ref.dll and lowers the accessor bodies; ilemit emits the C# field-like-event shape (a lock-free
  `Delegate.Combine/Remove` + `Interlocked.CompareExchange<D>` CAS loop for add/remove, `field?.Invoke(args)` for raise)
  + the `.event` metadata, and the synthesized accessors satisfy the interface `add_/remove_` slots. `kotlin.clr.ClrEvent<T>`
  is now an **abstract covariant marker with a private ctor** (non-constructable, non-subclassable); the interface event
  member is emitted OPEN, so a missing `by clrEvent()` on a direct interface implementer is a real compile diagnostic (a
  kotc emission-time check — an abstract member is unsatisfiable when a .NET base explicitly implements the event with a
  different-signature same-name public event, so it would wrongly break the `class MyApp : Avalonia.Application` ELIDE
  case). **RAISE-from-outside** is a deliberate CLR-native deviation (interop-first): `vm.E.invoke(sender, args)`
  is legal from any type via a public synthesized `raise_<E>` (`docs/dotkt-semantics.md` §8d). #113: all event emit routes
  through guarded resolution — a missing/value-type/constructed-generic event owner gives a legible `ilemit:`/`bir2cir:`
  breadcrumb instead of an opaque NRE. Design: `docs/design-clr-event-model.md`. (Class-delegation event forwarding #186
  is deferred to 0.9.8.)

### Fixed

- **bir2cir (area:bir2cir): `dotkt` and `dotkt.*` are ordinary user namespaces again.** Reference resolution still
  skipped the namespace once used by the retired pre-stdlib compiler-intrinsics runtime, even though only the
  unspeakable `dotkt$...` generated-type prefix remains compiler-owned. A cross-module Kotlin library under
  `dotkt.foo.bar` could therefore compile cleanly but be mis-bound at runtime. `ResolveNetType`/`ResolveRefType` now
  reserve only `dotkt$...`; the round-trip regression captures and mutates a referenced generic value from the exact
  `dotkt.*` namespace through a stored delegate.

- **bir2cir ([tmyt/dotkt#140], area:bir2cir): an asynchronously faulted or canceled `suspend fun main` now surfaces
  the raw await exception instead of `AggregateException`.** The synthesized blocking entry point drains its root
  `Task<Unit>` with `GetAwaiter().GetResult()` rather than `Task.Wait()`, matching the coroutine bridge design and
  normal .NET await semantics. A process-level regression verifies the asynchronous fault path.

- **bir2cir ([tmyt/dotkt#125], area:bir2cir): non-segmentable suspend lambdas now fail loud at invocation instead of
  emitting invalid IL.** `newSuspendLambda` uses the same structural classifier as named suspend functions; a
  suspension in `finally`, a suspending `catch` paired with `finally`, or a nested suspending `try` produces a valid
  `SuspendLambda` state machine whose `invokeSuspend` throws an explanatory `NotSupportedException`. The normal
  capture and `create()` protocol remains intact. Coroutine regressions drive all three shapes and require the
  call-time diagnostic rather than `InvalidProgramException`.

- **bir2cir ([tmyt/dotkt#98], area:bir2cir): a counted range loop now resumes inside every iteration instead of
  hoisting its suspension before the loop.** SuspendColdLowering flattens app-build `for (i in a..b)` and
  `for (i in a downTo b)` nodes into state-machine CFG before segmentation, spilling the counter as an SM field and
  preserving `break`/`continue` targets. This fixes both the silent “suspend once, execute the body N times” result
  and the unresolved loop variable failure when the resumed expression reads `i`. Coroutine regressions cover
  suspension count, ascending ranges, descending ranges, runtime results, and ILVerify.

- **facadegen ([tmyt/dotkt#205], area:facadegen): a generic .NET interface that derives a member-bearing
  NON-generic base interface now surfaces the base as a supertype, so its INHERITED members resolve and the
  generic interface stays assignable to the base.** `interface IReader<T> : IPingable` used to surface `IReader`1`
  with NO super edge (`InterfaceSuperTypes` emitted only GENERIC direct supers), dropping `IPingable.Ping` — a
  consumer's `reader.Ping()` gave `unresolved reference 'Ping'` and `IReader<Doc>` was not assignable to
  `IPingable`. Fix: `InterfaceSuperTypes` now also emits the namespace-qualified (#199 reference-token rule)
  non-generic direct super edge, dropping only the legacy same-simple-name generic shadow (`IEnumerable` beside
  `IEnumerable<T>`), mirroring the class path's `genericNames` guard. General to any generic BCL/user interface.
  New regression: `tests/interop` `ifacebasegen`. This is the narrow genuine follow-up found during #205; the issue's
  broader provenance false-positive was fixed separately by PR #201.

- **compiler ([tmyt/dotkt#203], area:kotc/ilemit): callable references now bind same-owner overloads by resolved
  parameter signature.** `calleeOwner` fixed package selection for `::foo`, but `newDelegate` and
  `newBoundDelegate` still performed a name-only `ldftn` lookup inside the selected file class / declaring class, so
  `(Int) -> String` and `(String) -> String` references could bind the same overload. kotc now carries the same
  structured `sig` used by ordinary calls on top-level and bound references (and on the inner call of an unbound member
  forwarder); ilemit consumes it in both normal and event-handler delegate construction. The signature contains type
  facts only, so bir2cir's declaration/slot name rewrites cannot stale it. Regression coverage exercises top-level,
  bound-member, and unbound-member overload pairs.
- **facadegen ([tmyt/dotkt#202], area:facadegen): generic method overrides no longer make
  `MetadataLoadContext.GetMethods` skip otherwise valid types and awaitable candidates.** The runtime's inherited-member
  suppression can call `GetGenericTypeDefinition()` on a generic parameter and throw for a non-generic derived type,
  producing 90 duplicate `skipped type` / `skipped awaitable` warnings in the NUnit-backed interop build. facadegen now
  falls back to derived-first, declared-only hierarchy enumeration for that reflection failure. A C# generic-override
  producer and Kotlin consumer call cover both metadata injection and dispatch; the same clean build now emits zero
  warnings.
- **bir2cir ([tmyt/dotkt#200], area:bir2cir): nested suspend lambdas inside a materialized inline carrier now retain
  their transitive captures.** A deep inline `Flow.transform`/`filter` chain could leave the nested lambda's
  `predicate` capture off the enclosing suspend state machine; synthesized-name ordering then decided whether ilemit
  saw a valid SM field or an unspilled local. InlineSplice now promotes current-frame nested-SM capture dependencies
  into the outer carrier, making spill ownership independent of declaration names. The coroutine flow-transform
  fixture uses the formerly failing prefixed declaration shape as the regression guard.
- **facadegen / roundtrip ([tmyt/dotkt#205], area:bir2cir/ilemit/facadegen): DotKt assembly provenance is now
  explicit instead of inferred from a namespace name.** DotKt output carries assembly-level
  `[AssemblyMetadata("DotKt.Compiler", "metadata-v1")]`, and every embedded
  `DotKt.Runtime.CompilerServices.*Attribute` definition is also `[CompilerGenerated]`; facadegen requires both before
  reading Kotlin metadata or applying collection reverse maps.
  An ordinary C# assembly containing a same-full-name lookalike therefore stays ordinary C#. The `il-tloverload`
  regression now uses two real Kotlin producer files and round-trips their genuine file facades, replacing the C#
  stand-in attribute that caused PR #201's shared producer DLL to be misclassified.
- **compiler ([tmyt/dotkt#199], area:kotc/bir2cir/ilemit): two same-simple-name top-level functions in DIFFERENT
  packages (`a.foo`/`b.foo`) now dispatch to their OWN package's body instead of a global first-match. Root: the
  `callStatic.owner` slot overloads two concepts — `owner:null` is the load-bearing "top-level call" axis that ~12
  bir2cir recognizers key on (`@ClrIntrinsic`/collection/array-factory substitution, Precondition/Repeat/Enum/ForIn/
  CharSeq lowerings), so the earlier fix of stamping the file-class on `owner` silently disabled substitution and
  broke `make stdlib` (`clrTimestamp` et al. reached ilemit unresolved). Fix (Design B): split the axes — `owner`
  keeps its meaning (`null` = top-level, UNTOUCHED) and a NEW advisory `calleeOwner` carries the FIR-resolved callee
  file-class DISPATCH hint (mirrors `sty`); the bir2cir owner-null machinery ignores it, only ilemit's `callStatic`
  dispatch consults it (falling back to the global `FindStatic` on a hint miss). Covers the non-suspend path, the
  suspend cold-lowering (the hint rides the rewrite + synthesized cold-entry/Task-bridge calls stamp it), and the
  top-level extension-property accessor. Regression fixtures: tests/il `XPkgSameNameFunTests`, tests/coroutines
  `SameNameAcrossPackagesTests`.**
- **compiler ([tmyt/dotkt#199], area:kotc/ilemit): a `::foo` function-REFERENCE delegate over a top-level fun now
  dispatches to its OWN package's body — the delegate analogue of the same-simple-name call bug above. kotc emitted
  the bare-name `newDelegate method:foo` (dropping the FIR-resolved callee file-class) and ilemit bound it by global
  first-match `FindStatic`, so two same-simple-name top-level funcs across packages (`a.foo`/`b.foo`) both bound to
  the first. Fix: extend Design B's `calleeOwner` DISPATCH hint to `newDelegate` — kotc stamps the callee file-class
  when the target is a top-level fun (`owner` stays absent, the substitution axis unchanged), and both ilemit
  newDelegate binding sites resolve `FindMethod(calleeOwner, name) ?? FindStatic(name)` (global fallback on a hint
  miss). Lifted `__lambdaN`/`__ctorref`/`__mref`/adapter forwarders (unique names, no `calleeOwner`) keep the plain
  `FindStatic` path, unchanged. Regression fixture: tests/il `XPkgSameNameDelegTests`.**
- **compiler ([tmyt/dotkt#199], area:kotc): the Design-B `calleeOwner` dispatch hint is now also stamped on a LIFTED
  LOCAL function call — completing the rule that EVERY `owner:null` `callStatic` carries its FIR-resolved dispatch
  owner.** A local `fun` is lifted to a static `__local<n>_<fn>` in the current file's file class; its call site emitted
  `owner:null` with no dispatch hint, leaving ilemit to resolve via global first-match `FindStatic`. It now carries
  `calleeOwner = <current file class>` (used directly, since a local fn's parent is the enclosing function, not an
  `IrFile`, so the `calleeOwnerTag` gate excludes it). This is a **defensive** hardening, not a reproducible-bug fix:
  `__local<n>`'s `<n>` is `scopeCounter`, which is monotonic across all files in one kotc invocation, and every
  canonical build is one invocation per assembly — so two `__local<n>` with the same `<n>` never coexist in an assembly
  and the global `FindStatic` resolves correctly today. A mis-dispatch is reachable only by linking BIR from two
  SEPARATE kotc invocations into one assembly (no canonical path does this). It is the method-dispatch analog of the
  `synthScope` per-file prefix already applied to synthetic closure TYPE names. No fixture is added — a single-`.ktproj`
  fixture compiles in one invocation, gets unique `__local<n>` names, and would pass with AND without the fix (a fake
  guard).**
- **facadegen ([tmyt/dotkt#199], area:facadegen): a re-imported/injected type REFERENCE to another type that shares
  its simple name with a type in a DIFFERENT namespace now carries the NAMESPACE-QUALIFIED name, so the injector
  resolves the EXACT type instead of the by-simple-name last-wins collision.** Two symptom families are fixed: ① a
  GENERIC reference (a factory RETURN `a.State<T>`, a `var` PROPERTY type, a generic supertype) was emitted as the bare
  `State` — collapsing `a.State<T>` and `b.State<T>` to one, so a factory's return / a var's type resolved to the WRONG
  package's type (var mutability + members degraded); the generic-reference paths (`CrossTypeT`/`CrossTypeTN`), the
  self-reference short-circuits, and the enum self-type now qualify with the namespace. ② a NON-generic base class and
  interface supertype was emitted as the bare simple name, so a subclass of `Inherit.Widget` could bind to a
  same-named `Ext.Widget` whose missing no-arg ctor CRASHED kotc's `generateConstructors`
  (`No arguments constructor for class Ext/Widget not found`); `SuperTypes`/`ClassInterfaceSuperTypes` now emit the
  qualified name. Nested base types key on `namespace + simpleName` (matching the injector's `+`-stripped ClassId), so
  the nested edge resolves too. Regression fixtures: `tests/roundtrip/producer/Genclash{A,B}.kt` (two same-simple-name
  generic `Cell<T>` across packages) and `tests/interop/producer/Extlib.cs` (`Ext.Widget` restored to collide with
  `Inherit.Widget`).
- **bir2cir ([tmyt/dotkt#138], area:bir2cir): `KClass.simpleName`/`qualifiedName` now report the KOTLIN name for a
  statically-known `::class`, not the .NET reflection name.** `1::class.simpleName` was `"Int32"` and
  `.qualifiedName` `"System.Int32"`; `"x"::class.qualifiedName` was `"System.String"` — the accessors were wired
  straight through to `System.Type.Name`/`.FullName`. `KClassMemberBinding` runs before `BirTypeLowering`, where the
  receiver's type slot is still a pure Kotlin FQN, so it now const-folds the accessor to the Kotlin name
  (`qualifiedName` = the FQN, `simpleName` = its last `.`-segment) for both an unbound `Int::class`/`Foo::class`
  (`classRef`) and a bound `1::class`/`"x"::class` on a known-final builtin (`getType` — a final type's runtime class
  == its static type). Fixes the primitive tower + String; a genuinely-dynamic `x::class` on an open/interface static
  type keeps the run-time read (a sequenced stdlib follow-up — see `docs/dotkt-semantics.md` §5g).
- **kotc ([tmyt/dotkt#184], area:kotc): a .NET attribute with a `params` (varargs) constructor parameter can now be
  applied bare (zero args) from Kotlin.** The injected annotation class constructor was not marking `params array`
  parameters as vararg — `ClrTypeInjection.generateConstructors` iterated all params with a plain `valueParameter`
  call that ignored `p.vararg`, so a `params object[]` ctor parameter surfaced as REQUIRED rather than omittable.
  The same `if (p.vararg) … isVararg = true` branch already used for injected method parameters is now applied to
  constructor parameters too. `@TestFixtureAttribute` (and any attribute whose sole ctor is `params T[]`) can now
  be applied bare (`@TestFixtureAttribute` with no args → empty array) or with arguments, matching C#/CLR semantics.
  Gated by `il-netattr-vararg`.
- **bir2cir ([tmyt/dotkt#189], area:bir2cir): a nullable-REFERENCE-returning lambda bound into a delegate
  (`Api.RunNullable(Func<string?>) { null }`) is now ilverify-clean.** `NullableFuncReturnErasure` erased EVERY
  nullable func-return `(…) -> R?` to `object`, so the lifted lambda's ret became `object` while the concrete delegate
  slot's `Invoke` returns `string` — `object` is not assignable-to `string`, so `newobj Func<string>::.ctor(ldftn object …)`
  failed ilverify `DelegateCtor` (runtime-safe; the ctor is not JIT-verified). The erasure to `object` is only needed
  when a plain reference cannot carry the null — a VALUE-type inner (`Int?`) or an unconstrained open generic (`T?`);
  a REFERENCE inner (`String?`) already carries null and now stays the (nullable-stripped) reference type, keeping the
  lifted return covariantly-assignable to the delegate slot (ECMA-335: the ldftn target's return must be
  assignable-TO the delegate `Invoke` return). Gated by `isValueFqn`; distinct axis from #170's String->CharSequence
  delegate-return bridge. Prunes the `il-delegnull` `XFAIL_ILVERIFY` entry.
- **toolchain ([tmyt/dotkt#51], area:ilemit, area:packaging): reference-asset selection now keys off the
  TARGET RID, not the build HOST RID — cross-target builds pick the right `runtimes/<rid>/lib` asset.** The core
  slice made `ManagedReferenceCatalog` rank RID-impl assets against the target RID's portable-RID-graph `#import`
  closure (NuGet `RuntimeGraph.ExpandRuntime`), but nothing passed the target RID *in*, so ilemit still selected on
  the host RID. This completes the wiring: ilemit gained `--target-rid` / `--rid-graph-path`, `RuntimeReferences.Load`
  forwards both to `ManagedReferenceCatalog.Create`, and the shipped MSBuild pipeline
  (`DotKt.Toolchain.targets`, imported by both the packaged SDK and the in-repo `cases/KotlinClr.targets`) passes
  `$(RuntimeIdentifier)` + `$(RuntimeIdentifierGraphPath)` on the ilemit `Exec`. ilemit is the sole consumer that
  reaches RID-asset selection (bir2cir/facadegen/retarget read RAR's RID-neutral compile set — a repeated simple name
  there still throws); an empty `$(RuntimeIdentifier)` (framework-dependent, no-RID build) degrades to the host RID.
  `scripts/dotkt.sh` gained a matching `--target-rid` passthrough for direct cross-target dev runs. Verified: on a
  linux-x64 host, `--target-rid win-x64` selects the `runtimes/win-x64/lib` asset (was the host linux-x64 asset).

- **stdlib/bir2cir ([tmyt/dotkt#56], area:bir2cir): high-arity (17–22) function-type declarations no longer
  silently dropped during the stdlib build.** bir2cir's `HighArityFunctionFilter` dropped (with only a stderr
  warning) any stdlib declaration whose signature mentioned a function type with >16 params — a silent-drop
  landmine (its "context() overloads are arity 17-22" premise was stale; those overloads are arity 1-6, so the
  filter guarded nothing that exists). Deleted the filter entirely: ilemit's module-local `KFunc`N`/`KAction`N`
  delegate synthesis is arity-driven and mode-INDEPENDENT, so the stdlib build now emits wide function-type
  declarations exactly like an app build (verified: ref+rt builds emit `KFunc`23`/`KAction`22` for a 22-arg
  probe). The Func/Action 16-param cap is now owned solely by ilemit's `BuildFuncType`; no bir2cir arity filter
  remains. Aligns with the cardinal rule (never drop/special-case a stdlib decl) and the frontend-resolved ⇒
  backend-must-compile invariant.

### Changed

- **bir2cir consumes the frontend-resolved operand static type instead of re-deriving it ([tmyt/dotkt#122],
  [tmyt/dotkt#48], area:bir2cir, area:kotc): the no-re-resolution-downstream invariant, realized on the type-contract
  surface.** kotc now stamps each value node's instantiated static type as a structured `sty` slot at its `expr()`
  chokepoint; bir2cir's `StaticType` reads that stamp (carried onto the clr* nodes MemberCallSubstitution/NetInteropBinding
  synthesize, stripped before CIR in BirTypeLowering) rather than re-doing overload return-type resolution against the
  ref.dll. The ~27KB `StaticTypeResolver` re-inference — `ResolveCallReturn`/`ResolveFieldType`/`LocalMemberType`/
  `TryGlobalTopLevel`/`SubstMemberTv` + the cross-file `GlobalTypes` aggregation + `ReferenceMetadataIndex.TryFieldType` —
  is DELETED; the lexical `BirScope` (declared var/param types) stays for bir2cir-synthesized locals. Stdlib CIR is
  byte-identical (only recognition moved). Also closes **#48 residual 1**: the last bare-string `ownerType` sites
  (top-level file-class calls, `__mref` forwarders, class-delegation forwarders) now emit a structured `{t:"fqn"}` node,
  so `ownerType` is removed from the `verify-schema` `STR_OK` allow-list. **#48 residual 2** (primitive-shorthand leaf
  `int`/`void`/`object`) is confirmed a sanctioned below-kotc CLR-resolution vocabulary (ilemit normalizes toward it),
  not a value-slot string — no change. Two follow-on consistency fixes complete the change: (a) `CharSeqStringLowering`
  now collapses a stale `dotkt$CharSequence` `sty` on a declaration-read (`local`/`field`/`lateinitGet`/`staticField`)
  in lockstep with the decl it already retypes to `System.String`, so the sty-first `StaticType.Surface` no longer
  shadows the CharSequence→String model and the `StringCharSequenceBridge` correctly adapter-wraps a String flowing into
  an un-rebuilt stdlib `dotkt$CharSequence` arg slot (`il-regexreplace` ilverify StackUnexpected fixed — was a raw
  `string` reaching a `dotkt$CharSequence` param); (b) the user-delegate `getValue`/`setValue` owner emit site (the
  member-property `by` arm) was the last bare-string `ownerType` and now emits a `{t:"fqn"}` node like its two sibling
  delegate sites, so `verify-schema` `STR_OK` genuinely holds zero `ownerType` (`il-deleg`/`il-rwp`). #122 closes; #48 closes.
- **bir2cir/ilemit ([tmyt/dotkt#48], area:bir2cir, area:ilemit): the legacy string-token type grammar is DELETED —
  structured `TypeNode` only, matching the frozen #37 schema; no dual-protocol.** The wire was already structured
  (`docs/bir-cir.schema.json` `$defs/type`), but the CODE still PARSED/EMITTED the retired `clr:` / `clrg:Name[..]` /
  `@Name` / primitive-shorthand / `func:`/`sfunc:` / `nullable:`/`array:`/`byref:`/`gp:` grammar. **S4 — owner islands →
  `TypeNode`:** every bir2cir-side owner slot (`ownerType`/`accessOwner`/`clrOverride`/clr\* `type`, and
  SuspendColdLowering's coroutine owners) is now a structured `{t:fqn}` node; the applied-attribute owner is a bare FQN
  + an `attrExternal` bool (`AttrExternalNormalize` strips kotc's `clr:`-imported prefix), retiring ilemit's
  `attr.StartsWith("clr:")` branches; `scripts/verify-schema.py` `STR_OK` shrinks (`ownerType`/`clrOverride`/
  `accessOwner`/`recv0` dropped) so any regression reds the gate. **S5 — sig-token island → one structural comparator:**
  ilemit's overload resolution no longer renders a `TypeNode` to a legacy token string and re-parses it — `SigTokenOf`/
  `SigTokenMatches`/`SigTokenMatchesOpen`/`SkipTypeToken`/`FuncType(string)`/`FuncRetEnd`/`NormalizeGpNames`/
  `FindByNormalizedSig`/`StripSigPrefixes` are replaced by a structural `Matches(TypeNode, System.Type)` and the `sig`
  parameter threads as `TypeNode[]` (the `MethodsBySig` dictionary keeps a `SigCanon` hash key — an internal encoding,
  never a wire spelling). ilemit's `MapType(string)`/`ClrRef(string)`/`NativeType(string)`/`ParseOwner` prefix parsers
  and bir2cir's `LowerTypeString` grammar-construction are deleted; bir2cir emits bare BCL FQNs. Gate: full
  `gate.sh --full` GREEN (il/schema/sanity/ktproj/roundtrip/differential-all-MATCH/widedelegates).

- **kotc ([tmyt/dotkt#48], area:kotc: CLOSES #48 — the LAST string-token type grammar is deleted EVERYWHERE; kotc emits
  structured `TypeNode` only.** Completes the kotc-owned residual the bir2cir/ilemit slice left open. (1) `callInline`'s
  `owner`/`callee` are structured `{t:fqn}` identity nodes (owner may be JSON-null for the owner-less stdlib scope-fn
  arm); bir2cir `InlineSplice` reads them via `TypeJson.OwnerName`. (2) An applied attribute's `attr` type is a
  `{t:fqn}` node; kotc flags an imported .NET attribute with `"attrClr":true` (a frontend origin fact — no `clr:`
  prefix), which bir2cir `AttrExternalNormalize` consumes into the `attrExternal` bool. (3) the `stackptr` pseudo-FQN
  is renamed to the canonical synthetic identity `dotkt$stackptr`. `scripts/verify-schema.py` `STR_OK` drops
  `owner`/`callee`/`attr` — every remaining entry is a genuine non-type string (member/accessor NAME islands, the
  `ownerType` owner island, CFG/opaque payloads). Dead-data cleanup: `BirTypeLowering.LowerLeaf`'s `@`-decorated
  dual-representation branch and `MemberCallSubstitution.WrapByref`'s `byref:` string form (both producer-dead). Gate:
  full `gate.sh --full` GREEN; `verify-schema` 0 violations; differential MATCH unchanged.

### Fixed

- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir, area:ilemit): CLOSED — ilemit re-resolves NOTHING on any clr*
  member axis (W1-S5 finishes the arc).** W1-S5 carries the `newBoundClrDelegate` target (Site 3 — a bound
  `netObj::method` reference) via a new `ResolveBoundClrDelegate` + `memberSig`, and deletes ilemit's LAST name-only
  first-pick (`Emitter.Expressions.cs`'s `type.GetMethod(name, argTypes) ?? type.GetMethod(name)` → the consume-only
  `LinkClrMethod`). After the full arc — S1 (#44, generic calls) → S2 (plain calls / ctors / dispatch) → S3
  (properties / fields / events) → S4 (override base-slot) → S5 (bound delegate) — ilemit's only remaining
  `GetMethod`/`ResolveMethod` are fixed BCL gets, the by-design Site-2 `callInstance` linker for MLC-unresolvable
  local-emitted owners (instrumented: 0 arbitrary-overload / dynamic-escape firings), and the deterministic
  `InterfaceMethodOn` single-abstract-method SAM lookup — none is an overload/arity/name first-pick. `MATCH 188 / DIFF 0`.
- **bir2cir/ilemit ([tmyt/dotkt#46], [tmyt/dotkt#183], area:bir2cir, area:ilemit): W1-S4 — declaration-side override
  base-slot memberRef carry.** A method DECLARATION overriding a .NET base-CLASS virtual (a property accessor such as
  `override val message` -> System.Exception.get_Message; the coroutine SM `create`/`invokeSuspend` overrides of
  `BaseContinuationImpl`) carries `clrOverride` (the base owner FQN). bir2cir (`ClrMemberResolution.OverrideBase.cs`,
  a partial of the S2 pass, running last on the fully-lowered tree) now resolves the EXACT base virtual off the ref.dll
  (MetadataLoadContext) and stamps its DECLARED param signature as `clrOverrideSig` (positional-`tv` for a generic
  base). ilemit's `LinkOverrideBase` (`Emitter.ClrInterop.cs`) links the UNIQUE base slot (0 = hard ABI error, >1 =
  malformed) and `DefineMethodOverride`s it — deleting the former `baseT.GetMethod(name, ps) ?? baseT.GetMethod(name)`
  NAME-ONLY first-pick fallback (`Emitter.Assembly.cs`). The match is STRUCTURAL identity (an override's params ARE the
  base slot's), NOT call-side applicability — so `BaseContinuationImpl.create(Any,Cont)` and `create(Any[],Cont)` stay
  distinguishable (a scalar arg no longer matches the array param via the object-downcast rule), with Kotlin `Any` ==
  `System.Object` as the only leaf normalization. Gated by `il-overridemsg`/`il-supercall`/`il-superobj`/`il-supernet`
  + every coroutine case (`il-corestrict`, `il-seqforin`, `il-inlsuspend*`, …). The `callInstance` `ResolveMethod` site
  (#183 Site 2) stays a LINKER consuming kotc's FIR-resolved `sig` (the empirical arbitrary-overload first-pick and the
  BCL-interface dynamic escape both fire 0× across the stdlib self-build + all app cases); its owners are either
  local-emitted (MLC-unresolvable by definition — the S2 local-`new` SelectCtor residual parallel) or referenced
  kotlin.* slots already structurally linked by the carried sig. #46 #183
- **ref-common ([tmyt/dotkt#51], area:packaging): reference-asset selection uses the TARGET RID, not the host RID,
  and consults the real .NET/NuGet portable RID fallback graph instead of a hand-rolled family table.**
  `ManagedReferenceCatalog.Create` gained `targetRid` / `ridGraphPath` parameters: `SelectRuntimeAsset` ranks
  `runtimes/<rid>/lib` assets against the TARGET RID's fallback chain (the transitive `#import` closure of
  `PortableRuntimeIdentifierGraph.json`, expanded breadth-first exactly like NuGet's `RuntimeGraph.ExpandRuntime`),
  so cross-target compilation (e.g. Linux host → `win-x64`) now selects the correct RID-impl asset instead of the
  host's — previously a Linux build targeting Windows picked the RID-neutral PlatformNotSupported placeholder and the
  special-RID fallback was input-order dependent. The graph path is MSBuild's `$(RuntimeIdentifierGraphPath)` when
  passed, else auto-discovered from the running SDK; `targetRid` defaults to the host RID when unset (correct for a
  host-targeted or direct-script run), so existing host-target builds are unchanged. The hand-rolled family chain
  survives only as a last resort when no portable graph is found. Wiring MSBuild's `$(RuntimeIdentifier)` /
  `$(RuntimeIdentifierGraphPath)` through `ilemit --runtime-refs` into `Create` is a follow-up (ilemit + targets). #51

- **bir2cir/ilemit ([tmyt/dotkt#46], [tmyt/dotkt#121], area:bir2cir): property / field / event memberRef carry —
  ilemit is a pure linker for every clr* member-ACCESS axis: calls, ctors, properties, fields, events, dispatch
  (W1-S3, closes #121). #46 stays OPEN for W1-S4 — two ilemit resolution sites remain (NOT in #121's enumerated
  use-sites): the declaration-side override base-slot link (`Emitter.Assembly.cs`, still `GetMethod(name, ps)` + a
  name-only fallback for every method override incl. ToString/Equals) and the `@Clr`/`@ClrIntrinsicAsDynamic`
  `callInstance` resolution (`Emitter.Expressions.cs`).** The remaining un-carried axes
  followed the S2 plan: bir2cir (`ClrMemberResolution.PropFieldEvent.cs`, a partial of the S2 pass, running last)
  now resolves `clrPropGet`/`clrPropSet`, `clrEventAdd`/`clrEventRemove`, and an external `field`/`setFieldExpr`/
  `setField` against the ref.dll (MetadataLoadContext), stamping a `member` discriminator (`accessor`|`field`), the
  resolved accessor NAME, `memberSig`, and `dispatch`. ilemit consumes them via the shared `LinkClrMethod` +
  `EmitClrDispatch` — no property-vs-`get_`-method-vs-field reclassify, no external-field→accessor reinterpret, no
  unchecked `GetEvent` (a missing event is now a hard ABI error, hardening #113), and no `call`/`callvirt`/
  `constrained` derivation from the reflected accessor. A generic base-interface accessor (`IReadOnlyCollection<T>.
  get_Count` on `IReadOnlyList<T>`) retargets the owner to the constructed base interface (the resolved twin of the
  deleted `PropAccessor`'s `SubstituteIfaceArgs` re-anchor). Deleted `PropAccessor`, `ExternalPropAccessor`,
  `EmitInstanceCall`, `PropList` (the KIND-derivation / first-pick helpers). A LOCAL emitted owner (ref.dll returns
  null) keeps its direct backing-field access. Gated by `il-extprop`/`il-vtprop`/`il-event`/`il-eventext`/
  `il-ifaceevent` + `roundtrip-property-type`. #46 #121

- **bir2cir ([tmyt/dotkt#157], area:bir2cir): general cross-module top-level `val` accessor resolution; delete
  the `COROUTINE_SUSPENDED` band-aid.** A cross-module top-level `val` read is kotc-emitted (post-#89) as
  `callStatic owner:null … prop:get`; bir2cir already reconstructs the `get_<name>` accessor and resolves it via
  `TryResolveTopLevelStatic` — and the ref-scan already indexes property accessors (`get_X`/`set_X` — file-class
  statics with `intrinsic==null`, no `IsSpecialName` exclusion). The prior `COROUTINE_SUSPENDED`-specific
  owner-rebind in `MemberCallSubstitution` was therefore redundant (post-#89 the "already-owner'd" shape it also
  covered no longer occurs — both reads now arrive owner:null); removed it (no-band-aid rule), so every
  cross-module top-level val resolves through the ONE general path. Byte-identical CIR before/after on
  `il-suspendintrinsicowned`; the non-coroutine sibling of the same path is gated by `il-extprop` (extension-property
  getters). NB the facadegen-consumed top-level val is a distinct owner-ful `staticField` shape (gated separately by
  `roundtrip-toplevel-val`), and the owner:null path is klib-package-fragment-only. #157

- **bir2cir/ilemit ([tmyt/dotkt#46], area:bir2cir): plain-call / ctor / dispatch memberRef carry — ilemit purified
  to a linker (W1-S2; the generic-call dual S1 was #44).** bir2cir now resolves `clrStatic`/`clrInstance`/`newClr`
  against the ref.dll (MetadataLoadContext), structurally matches the winning member, and carries its declared param
  signature as `memberSig` (+ `dispatch` for `clrInstance`); ilemit consumes it purely as a linker (structural match
  to exactly one handle — 0 = hard ABI error, >1 = malformed, no first-pick). Deletes ilemit's
  PickCtorByAssignable / PickClrCtor / ParamAcceptsArg / ResolveInheritedIfaceMethod / the EmitClrCall resolution
  cascade / the implicit EmitDynamicCall downgrade / dispatch derivation (new pass `ClrMemberResolution.cs`); an
  interface owner with no matching member becomes an explicit `clrDynInstance`, not a silent downgrade. #24
  override-dispatch preserved (`MATCH 203 / DIFF 0`). Remaining for W1-S3 (so #46 stays open): properties / fields /
  events + the declaration side (`clrPropGet`/`clrPropSet` still route through `EmitInstanceCall`); the local-`new`
  `SelectCtor` + referenced `kotlin.*`-helper arity-probe axes are MLC-unresolvable and stay by design.
- **bir2cir ([tmyt/dotkt#153], area:bir2cir): primitive-array-receiver top-level stdlib extensions resolve at app
  level.** `intArrayOf(1,2).toList()` failed with ilemit `static method not found` — `RecvKeyOf` keyed the
  primitive-array Fqn as `kotlin.IntArray` while the ref side collapses `int[]` to `[]`, so owner attribution missed.
  A shared `RecvKeyOfFqn` now maps every specialized-array Fqn (signed + unsigned) to `[]`; because that key is lossy
  (generic `Array<T>` + all primitives share it), a fine first-param `ParamKey` narrows the overload so generic
  `Array<out T>.toList` no longer erases the element to `object` and `ubyteArrayOf(..).toList()` binds the
  instantiated helper. Auto-recovers the app path for #97 (primitive `copyInto`) and #128 (`copyOf(newSize)`). Gate:
  `il-intarraytolist`.
- **packaging ([tmyt/dotkt#133], area:packaging): the MPP SDK (`DotKt.Sdk.Mpp`) builds out of the box — a new
  `dotkt-mpp` `dotnet new` template.** `Sdk="DotKt.Sdk.Mpp"` needs a `global.json` pinning both `DotKt.Sdk.Mpp`
  and the nested `DotKt.Sdk` (the NuGet resolver reads a nested SDK's version *only* from `global.json`), and
  nothing scaffolded it. The `dotkt-mpp` template now ships that `global.json` (both pins substituted to the
  release version at pack) alongside a common `expect` / CLR `actual` sample, so `dotnet new dotkt-mpp && dotnet
  run` works with no hand-written boilerplate. New gate case: `verify-packaged-sdk.sh` `mpp-template`.
- **packaging ([tmyt/dotkt#134], area:packaging): the DotKt build back-half is now incremental — a no-op build
  skips it.** `DotKtBir2Cir`/`DotKtIlEmit`/`DotKtRetarget` had no `Inputs`/`Outputs`, so every `dotnet build`
  re-lowered, re-emitted and re-retargeted, rewriting the output dll's timestamp and forcing every downstream C#
  `ProjectReference` to rebuild. Each target now keys `Inputs`/`Outputs` off a stable `.stamp` (the compile's
  BIR `.stamp` cascades through the CIR `.stamp` to the emitted dll and a retarget stamp), and the
  `_DotKtPlaceholder.cs` write became `WriteOnlyWhenDifferent` (it was bumping its mtime every build and forcing
  `CoreCompile` to recompile). A no-op build now converges.
- **packaging ([tmyt/dotkt#135], area:packaging): the Windows compiler launcher is selected by OS.**
  `$(DotKtCompiler)` was hardcoded to the extension-less UNIX `kotc` script, leaving Windows to rely on cmd.exe's
  PATHEXT resolving a pathed extension-less command. It now selects the shipped `kotc.bat` when `$(OS)` is
  `Windows_NT` (both launchers ride in the package from the Gradle `installDist`), falling back to `kotc` elsewhere.
- **packaging ([tmyt/dotkt#151], area:packaging): corrected the `DotKt.Sdk` `Sdk.props` guard comment.** It said
  the pack guard compares the `DotKtVersion` default to `DotKtVersionPrefix`; it actually compares to the version
  CORE (prefix, plus `-suffix` when pre-release, e.g. `0.9.6-rc7`). Following the old comment during an RC would
  trip the (fail-safe) guard.

- **stdlib ([tmyt/dotkt#104], area:stdlib): `Regex.findAll`/`splitToSequence` and the `Regex.options` getter no longer
  throw `NotImplementedError`.** All three shipped as `TODO()` runtime stubs. Now implemented in pure Kotlin over the
  existing bindings: `findAll` = `generateSequence` over `find()`/`MatchResult.next()` (every non-overlapping match,
  left-to-right, `startIndex`-honored, via ordinary `Sequence` machinery — no coroutine `sequence{}` builder needed);
  `splitToSequence` = `split(input, limit).asSequence()`; `options` decodes the compiled `System...RegexOptions`
  `[Flags]` bitmask (`IgnoreCase`/`Multiline`/`Singleline`/`IgnorePatternWhitespace` → the matching `RegexOption`;
  `LITERAL`/`UNIX_LINES`/`CANON_EQ` have no .NET bit). Gate: `il-regexseq`.
- **facadegen ([tmyt/dotkt#132], area:facadegen): interface-companion statics survive the round-trip.** kotc flattens an
  interface's plain `companion object` to the interface's OWN static fields/methods (the `SharingStarted.Eagerly` #83
  path), but facadegen's interface branch enumerated only `Public|Instance` members and dropped every flattened static —
  so a consumer re-importing the DotKt library could not resolve `I.X`/`I.f()`. facadegen now surfaces an interface's
  `Public|Static` fields/props/methods/events as companion members (`staticProps`/`staticFuns`/`staticEvents`), reached
  via `I.Companion`; a C#11 static-abstract/static-virtual interface member (invokable only through a constrained type
  parameter) is excluded so no uncallable companion slot is advertised. Gate: `roundtrip-iface-companion`.
- **facadegen ([tmyt/dotkt#146], area:facadegen): `KotlinFun()` no longer silently demotes infix/operator/suspend.** The
  blanket `catch` around the `[KotlinFunction]` read erased a method's Kotlin vocabulary whenever an UNRELATED user
  attribute referenced a type outside the resolver set (materializing one attribute forces the whole set). The read is
  now guarded per-attribute (a bad sibling never blocks `[KotlinFunction]`); a genuine enumeration failure on an
  already-DotKt-classified assembly is surfaced LOUD instead of swallowed. The unconditional `if (name=="compareTo")
  op=true` hack — which force-flagged ANY method named `compareTo` and masked a genuinely-missing operator flag — is
  removed; kotc stamps the real `isOperator` (inherited by keyword-less overrides). Gate: `roundtrip-operator-flag`.
- **facadegen ([tmyt/dotkt#179], area:facadegen): a re-consumed `class C : Comparable<C>` regains its Kotlin operator
  surface.** At lib emit a Kotlin `class C : Comparable<C>` lowers `compareTo` to the PascalCase
  `System.IComparable<C>.CompareTo` slot and its supertype to `System.IComparable<C>` (+ a non-generic bridge), so on
  re-import facadegen surfaced neither the lowercase `operator fun compareTo` nor the `Comparable<C>` supertype — a
  consumer's `c1 < c2` / `sorted()` was unresolved. facadegen now (a) restores the `System.IComparable<X>` supertype as
  fully-qualified `kotlin.Comparable<X>` (dropping the non-generic bridge) so the type is seen as `Comparable` and
  `sorted()`'s constraint holds, and (b) renames the DotKt `IComparable<X>`-self-slot `CompareTo` to the lowercase
  `compareTo` + forces the `operator` flag so the FRONTEND resolves `<`/`>`/`<=`/`>=`. A genuine .NET `IComparable`
  keeps its verbatim PascalCase `CompareTo` (`il-icmparity`). The residual bir2cir call-binding half — `NetInteropBinding`
  rebinds the Kotlin `compareTo` call to the DotKt owner's PascalCase `CompareTo` slot when the owner implements generic
  `IComparable<T>` — landed too, so the end-to-end `<`/`sorted()` run passes. Gates: `roundtrip-comparable-meta` (surface)
  + `roundtrip-comparable` (end-to-end; its RT_XFAIL pruned). #179 fully closed.
- **bir2cir ([tmyt/dotkt#178], area:bir2cir): `Regex(pattern, Set<RegexOption>)` / `Regex(pattern, RegexOption)` ctors
  work.** The options-taking Regex constructors threw `InvalidProgramException` — the `Set<RegexOption>`/`RegexOption`
  → `System...RegexOptions` ctor-arg conversion was unwired. `NetInteropBinding.Reshape` now synthesizes the
  `RegexOptions` `[Flags]` bitmask (`IGNORE_CASE`→1, `MULTILINE`→2, `DOT_MATCHES_ALL`→16, `COMMENTS`→32; the three
  no-.NET-bit options drop to 0) at the `newClr` site and retypes the arg so `ClrMemberResolution` binds the BCL
  `Regex(String, RegexOptions)`. Gate: `il-regexopts`. Encode-side deviation recorded in `docs/dotkt-semantics.md` §5b-quater.
- **bir2cir ([tmyt/dotkt#180], area:bir2cir): direct/mixed nullable `Double?`/`Float?` `==` is verifiable IL.** The
  `ieee754equals` arm lowered a nullable-float `==` to a raw `Ceq` over `Nullable<T>` structs (unverifiable IL /
  `InvalidProgram`, latent). It now emits null-safe shaping (`null==null`→true, one-null→false, both-present→IEEE `==`
  on the values) — direct `==` stays IEEE per #95 (`-0.0 == 0.0` true, `NaN == NaN` false), distinct from #152's
  structural bit-equality. Nullness is read from `StaticType.Surface` (so an explicit `x as Double?` is caught too).
  Gate: `il-floateqnull`. Follow-up: #181 (safe-call `obj?.d == y` operand, same class).
- **bir2cir ([tmyt/dotkt#152], area:bir2cir): nullable `Double?`/`Float?` structural equality uses total-order
  bit-equality, not boxed `Double.Equals`.** A data-class / structural `==` over a `Double?`/`Float?` field fell
  through to a boxed `System.Double.Equals` (IEEE: `-0.0 == 0.0` true), violating the total-order equals/hashCode
  contract #95 adopted for the non-null case. The `EQEQ` lowering now, before the `objEq` fallback, emits null-safe
  bit-equality (`clrDoubleEquals`/`clrFloatEquals`) so `D(-0.0) != D(0.0)`, `D(NaN) == D(NaN)`, and hashSet
  membership is consistent; direct operator `==` stays IEEE per #95. Gate: `il-structfloateqnull`. Follow-up: #180
  (direct/mixed nullable `ieee754equals`).
- **kotc ([tmyt/dotkt#177], area:kotc): a `companion object` extension fun passes its extension receiver.** A
  `fun Receiver.ext()` declared inside a `companion object` lowered to a static with a leading `__self` param, but the
  call site emitted only the regular args — dropping the receiver → an arity miscompile. The companion-extension emit
  now prepends the extension receiver as the first arg (consistent with member/top-level extension emit). Gate:
  `il-companionext`.

- **stdlib ([tmyt/dotkt#141], area:stdlib): `hypot`/`expm1`/`ln1p` (Double & Float) bind the numerically-correct
  net10 BCL primitives.** The old bodies (`sqrt(x*x+y*y)`, `exp(x)-1`, `ln(1+x)`) overflowed for large magnitudes
  (`hypot(1e308,1e308)` → `Infinity`) and lost all precision to cancellation near 0. Now bound as `@ClrIntrinsic`
  to `System.Double.Hypot`/`ExpM1`/`LogP1` and `System.Single.Hypot`/`ExpM1`/`LogP1`. Gate: `il-mathnumerics`.
- **stdlib ([tmyt/dotkt#143], area:stdlib): `decodeToString`/`encodeToByteArray` honor `throwOnInvalidSequence=true`.**
  The 3-arg overloads previously ignored the flag and silently substituted U+FFFD. They now transcode through a
  throwing `UTF8Encoding(false, true)` and surface a `CharacterCodingException` (Kotlin contract) on malformed
  UTF-8 / unpaired surrogates; the default (`false`) path keeps replacement. Gate: `il-utf8throw`.
- **stdlib ([tmyt/dotkt#144], area:stdlib): `String`/`Char` `uppercase()`/`lowercase()` documented as CLR-native
  1:1 mapping — NOT a JVM one-to-many bug.** `#144` was re-triaged (not a defect): kotlin/clr has no binary interop
  with other Kotlin backends, so string-value parity (`"ß".uppercase() == "SS"`) has no functional value, and .NET's
  deliberate 1:1 no-expansion (`ToUpperInvariant`/`ToLowerInvariant`) is a valid platform choice. The public forms
  bind directly to `System.String.ToUpperInvariant`/`ToLowerInvariant` (`@ClrIntrinsic`); `"ß".uppercase() == "ß"`.
  The deliberate deviation from Kotlin/JVM/Native/JS one-to-many expansion is recorded in `docs/dotkt-semantics.md`
  §5g. Gate: `il-caseinvariant`.
- **stdlib ([tmyt/dotkt#145], area:stdlib): array `fill(element, fromIndex, toIndex)` validates its range.**
  A `fromIndex > toIndex` call silently no-op'd; the generic + all 8 primitive `fill` actuals now throw
  `IllegalArgumentException` on an inverted range and `IndexOutOfBoundsException` out of bounds (Kotlin contract).
  Gate: `il-fillrange` (generic path; the primitive actuals carry the identical guard but remain blocked from app
  calls by the pre-existing primitive-array-receiver resolution gap).
- **stdlib ([tmyt/dotkt#129]/[tmyt/dotkt#130]/[tmyt/dotkt#142], area:stdlib): concurrency-correctness in the atomics + coroutine primitives.**
  Three memory-model/locking defects fixed stdlib-side (CLR-native Interlocked/Volatile/Monitor bindings, no compiler
  special-casing). **#129**: the `AtomicIntArray`/`AtomicLongArray`/`AtomicArray` element ops did a bare
  `monitorEnter/…/monitorExit` around `array[index]`, whose bounds check throws mid-critical-section and leaked the
  monitor (a reentrant lock the throwing thread never notices but every OTHER thread on that instance deadlocks on);
  each section is now wrapped in `try { … } finally { monitorExit(lock) }`. **#130**: scalar `AtomicInt`/`AtomicLong`/
  `AtomicBoolean`/`AtomicReference` `load()`/`store()` were plain field access outside the memory model. The lock-free
  scalars (`AtomicInt`/`AtomicLong`) now bind `System.Threading.Volatile.Read/Write(ref …)` (byref, ordered and
  non-tearing for `long` on every platform); the monitor-backed `AtomicBoolean`/`AtomicReference` keep a `@Volatile`
  field for the unlocked acquire `load()` but route `store()` through the SAME monitor as their RMW ops — a lock-free
  store would slip inside the monitor's read-modify-write gap and be lost (non-linearizable). `toString()` now reads
  via `load()` so `AtomicLong` cannot tear. Separately, the `AtomicIntArray`/`AtomicLongArray`/`AtomicArray`
  array-adopting constructors now defensively `copyOf()` the argument (per the expect KDoc; aliasing left an
  unsynchronized side door into the monitor-guarded storage). **#142**:
  `SafeContinuation`'s `UNDECIDED→result` / `UNDECIDED→COROUTINE_SUSPENDED` state transition was a non-atomic
  check-then-store that races under a multithreaded dispatcher; it is now a lock-free CAS loop over a `@Volatile`
  field via `Interlocked.CompareExchange(ref object,…)`, faithful to the JVM `AtomicReferenceFieldUpdater` version.
  New gate cases `il-atomicarraytry` (cross-thread lock-release), `il-volatileatomic` (volatile round-trip), and
  `il-safecontresume` (async cross-thread `suspendCoroutine` resume).
### Changed

- **gates/tests (area:gates): NUnit migration foundation + first family (generics battery) migrated off the
  per-case bash gate.** Stood up the production in-process NUnit suite (`docs/design-nunit-test-harness.md`):
  `tests/il/DotKt.Tests.Il.ktproj` resolves the LOCALLY-BUILT
  DotKt SDK from `build/nuget-feed` (`make pack`) via an active `tests/nuget.config` (isolated
  `globalPackagesFolder`, package-source mapping `DotKt.*`→local feed) — so the suite tests the compiler in the
  working tree, not a published nuget. `tests/run-nunit-il.sh` drives it and enforces a **discovered-count
  guard** (asserts `dotnet test` discovered exactly the expected number of methods — a dropped/added method or a
  0-test discovery failure reddens the gate) plus once-per-assembly ilverify. Migrated `cases/il-generic ..
  il-generic6` (the G-1..G-6 progressive-milestone cases the cases-test-design audit condemns as 6 permanent
  compiler processes) → one `GenericsTests` fixture, 6 `@TestAttribute` methods asserting the SAME values via
  `assertEquals` (aliased from `ClassicAssert.Companion.AreEqual`); the 6 case dirs, their `verify-il.sh`
  `il_check` lines, and their `verify-differential.sh` `PURE` entries were deleted in the SAME change (audit
  必須是正条件 #14). `dotnet test` runs the battery in ~17 s clean / ~3.6 s warm against the local SDK.

- **docs/process (area:semantics): the behavior-choice acceptance test is now stated as "consistent, documented,
  convincingly explainable"** (CLAUDE.md Design doctrine + `docs/dotkt-semantics.md` guiding principle): ① Kotlin
  contract by default, ② CLR-native where unspecified, ③ *interop-first deviation* may override even the KDoc letter
  when CLR/mscorlib consistency convincingly wins. Recorded the #144 case-mapping deviation as `docs/dotkt-semantics.md`
  §5b-ter (`"ß".uppercase()` stays `"ß"`, no Unicode one-to-many expansion — previously only a `CharClr.kt` comment).
  A new PostToolUse hook (`scripts/hooks/check-jvm-emulation.sh`, wired in `.claude/settings.json`) auto-injects this
  self-check whenever newly-written toolchain/stdlib text pattern-matches JVM-emulation intent ("matches JVM",
  "JVM parity", the hashCode 31-polynomial), so agents re-verify the judgment at write time instead of after review.

- **gates ([tmyt/dotkt#107]/[tmyt/dotkt#108]/[tmyt/dotkt#99]/[tmyt/dotkt#109], area:gates): hardened the verification harness.**
  `verify-il.sh` now (#107) FAILS LOUD when the ilverify lane cannot run (ILVerify.dll absent / runtime ref dir
  missing) instead of silently reporting green with zero IL coverage and printing spurious `FIXED` for every real
  XFAIL; (#108) wraps every per-sample run in a `timeout` (default 60s, `DOTKT_RUN_TIMEOUT`) so a coroutine
  resume/pulse-drop deadlock surfaces as a distinct `run timeout` FAIL instead of wedging the whole gate; and (#99)
  DERIVES the ilverify assembly set from the run set (each sample records its emitted assembly name) rather than a
  hand-maintained map that had drifted — closing the 78+ run-only-sample formal-coverage gap permanently, with a
  single explicit `ILVERIFY_EXCLUDE` (stackalloc's by-design-unverifiable `localloc`) printed loudly, no silent gaps.
  This exposed six pre-existing formal-only ilverify findings (all RUN-green, runtime-safe): `boxgen` (#62/#46
  compare-SAM boxing), `classdeleg` ([tmyt/dotkt#174], new — class-delegation forwarder narrows the MutableList
  iterator return), `copyofnull` (#127/#86 nullable-value-type array object-erasure), and `defargs`/`delegnull`/
  `linkedorder` (#170/#150 DelegateCtor) — each XFAIL_ILVERIFY-listed with a concrete reason. `verify-roundtrip.sh`
  adds (#109) a cross-module nullable VALUE-TYPE generic case (`T?` param+field instantiated at `T=Int`), which
  documents the #86/#147 cross-module gap as an RT_XFAIL (the consumer fails to compile because the `T?` restores as
  bare non-null `T`) — an axis every other gate missed by driving only `T=String`.
- **stdlib ([tmyt/dotkt#167]/[tmyt/dotkt#168], area:stdlib): String/Float/Double `hashCode()` bind to CLR-native `GetHashCode`.**
  Removed the hand-rolled JVM-forced hash bodies — String's `s[0]*31^(n-1)+…` polynomial and Float/Double's
  `toBits()` bit-hash. The Kotlin `hashCode` contract requires only within-run consistency + equals-consistency
  ("need not remain consistent from one execution to another"), not a specific value or across-run determinism;
  kotlin/clr consumes no JVM artifacts, so no interop needs the JVM value. `System.String/Single/Double.GetHashCode`
  already satisfy the contract (per-process consistent, NaN/zero normalized to be equals-consistent with the
  total-order structural equality). String binds via `@ClrIntrinsic("GetHashCode")` (falls through kotc's
  universal-method routing to the BCL slot); Float/Double drop the declaration entirely and inherit the `kotlin.Any`
  slot like Int/Long (routing to the native value-type `GetHashCode`). The `il-strhash`/`il-pairtostr` gate cases now
  assert equals-consistency + hash-set membership instead of a pinned integer.
- **CI: run the COMPLETE canonical gate set + a distinct packaged-SDK job + Windows coverage ([tmyt/dotkt#160], area:packaging).**
  `.github/workflows/verify.yml` previously ran only IL/differential/ktproj/round-trip/wide-delegate on a
  single `ubuntu-latest` job — it silently skipped `verify-schema`, `verify-sanity`, and (release-critically)
  `verify-packaged-sdk`, the only gate that restores + consumes the 5 real nupkgs. The workflow now invokes the
  Makefile aggregates (gate list single-sourced there, not copied into YAML): a `verify` job runs
  `make verify-core` (the canonical set), a distinct release-blocking `packaged-sdk` job runs
  `make verify-packaged-sdk`, and a `windows` job covers the Windows surface (kotc.bat install, nupkg restore,
  packaged build/run, `verify-ktproj`, `dotnet new` template creation). New `make verify-core` target =
  `make verify` minus the packaged-SDK gate.
- **NuGet packages carry provenance metadata + third-party notices ([tmyt/dotkt#166], area:packaging).**
  All 5 packages now declare an SPDX `Apache-2.0` license, `projectUrl`, and a `<repository>` with the source
  commit (stamped by `pack-nuget.sh`), and ship a packaged readme (`packaging/DotKt.README.md`). `DotKt.Toolchain`
  additionally ships `THIRD-PARTY-NOTICES.md` listing the redistributed components (Kotlin compiler/runtime,
  kotlinx-coroutines, JetBrains annotations, Mono.Cecil, `System.Reflection.MetadataLoadContext`) and their licenses.
- **docs: README + support matrix reconciled with actual behavior; JVM-framing cleanup ([tmyt/dotkt#164], area:docs).**
  The README "no bundled libraries" line now states DotKt ships no UI/framework abstraction but DOES ship its CLR
  Kotlin stdlib; the hardcoded corpus/pass counts are softened to point at the gates' XFAIL maps. The
  `supported-features.md` Regex row is regenerated method-by-method (`find`/`matchEntire`/`matches`/`replace`/`split`/
  group accessors work; `findAll`/`splitToSequence`/`options` pending). Recorded the correctness bar in
  `docs/dotkt-semantics.md` and `CLAUDE.md`: the bar is the Kotlin spec/KDoc contract, JVM is a reader reference
  (not a compat target), unspecified behavior takes the CLR-native form.
### Fixed

- **bir2cir/ilemit ([tmyt/dotkt#169], area:backend): the concrete `LinkedHashSet` (a #169 side-effect) emitted invalid
  IL for `setOf`/`distinct()`/`toMutableSet()`/`retainAll` — `InvalidProgramException` at runtime.** Making
  `LinkedHashSet` a real generic Kotlin class (was a `@ClrTypeAlias`) exposed three CLR-codegen bugs, all fixed while
  keeping the #169 insertion-order contract: (1) ilemit's `SelectCtor` picked a ctor by ARITY only, so
  `new LinkedHashSet(collection)` resolved to the arity-colliding `(Int)` ctor instead of `(Collection<E>)` — now it
  signature-matches the `new` node's declared `argTypes` (falling back to first-arity when absent/unreadable); (2)
  `CollectionBclSlotSynthesis` emitted its synthesized `ICollection.Contains`/`IList.IndexOf` self-forward against the
  OPEN generic self (`LinkedHashSet\`1`) instead of the constructed `LinkedHashSet<!0>` (the pass runs after
  GenericSelfInstantiation); (3) `MemberCallSubstitution` rerouted EVERY `.iterator()` on an emitted `kotlin.collections.*`
  non-alias type to the base-`Iterator` bridge, but the concrete `LinkedHashSet` declares its own `MutableIterator`-returning
  `iterator()` — the reroute is now suppressed for any type (local OR ref.dll) that declares a concrete `iterator()`, so
  an app's `linkedSetOf(..).iterator().remove()` binds the real slot (was `EntryPointNotFound` on `remove()`). Regression
  case `cases/il-linkedset`.
- **stdlib ([tmyt/dotkt#162]/[tmyt/dotkt#169], area:stdlib): two Kotlin-contract fixes in text/collections.**
  - **#162 `Regex.matchEntire`/`matches` now do a TRUE anchored full match.** The old path ran a leftmost
    `System...Regex.Match` (a SEARCH) and accepted it only if the first result spanned the input — so a shorter
    alternation branch winning the search (`Regex("a|ab").matchEntire("ab")` → `a` found first) returned `null`, and
    lazy quantifiers hit the same class. `matchEntire` now anchors the engine: it re-matches the pattern wrapped as
    `\A(?:<pattern>)\z` (the non-capturing group scopes a top-level alternation and preserves the user's capture-group
    NUMBERS) with the instance's OWN compiled options (read via a new `nativeOptions`/`ClrRegexOptions` binding and fed
    to the static `Regex.Match(string,string,RegexOptions)` overload), so the engine backtracks to a full-input match
    when one exists. `matches` delegates unchanged. Regression case: `cases/il-regexanchor`.
  - **#169 `LinkedHashMap`/`LinkedHashSet` (and `mapOf`/`setOf`) now preserve insertion order across removals.** They
    were aliased to `Dictionary`/`HashSet`, which only preserve insertion order incidentally and LOSE it after a
    removal — violating the Kotlin iteration-order contract. `LinkedHashMap` is now `@ClrTypeAlias`-bound to the
    insertion-ordered `System.Collections.Generic.OrderedDictionary<K,V>` (.NET 9+; a pure alias swap — it exposes the
    same non-generic `IDictionary`/`ICollection` facades and intrinsic members the map-defaults helpers rely on).
    `LinkedHashSet` — .NET has no ordered generic set — is now a REAL pure-Kotlin `MutableSet` backed by that
    `LinkedHashMap` (exactly as Kotlin/JVM backs it with a `LinkedHashMap`), so it gets the `CollectionBclSlotSynthesis`
    ICollection slots + the reverse `GetEnumerator` bridge. Plain `HashMap`/`HashSet` stay unordered (per contract).
    Regression case: `cases/il-linkedorder`.
- **packaging ([tmyt/dotkt#161]/[tmyt/dotkt#106], area:packaging): MSBuild-SDK + pack staleness fixes.**
  - **#161 stale injection metadata across a `<DotKtImport>` change / `dotnet clean`.** `DotKtInjectTypes` consumed
    `@(DotKtImport)` but did not track it as an Input (a non-file item cannot be a target Input), so removing/adding an
    import left the previous `obj/dotkt-clrtypes.meta` in place and the build kept succeeding against a dropped .NET
    type until an unrelated `.kt` edit forced a recompile; and none of the generated DotKt state under
    `$(BaseIntermediateOutputPath)` was tracked for `Clean`, so `dotnet clean` did not repair it. The ordered
    `@(DotKtImport)` set is now materialized into a `WriteOnlyWhenDifferent` manifest (`dotkt-clrimports-explicit.txt`)
    by a new `DotKtComputeImportManifest` target and added as an Input of `DotKtInjectTypes`, so add/remove/reorder
    flips a timestamp and re-runs injection (no-op rebuild stays byte/timestamp-stable); a new `DotKtClean`
    (`BeforeTargets="CoreClean"`) wipes the BIR/CIR dirs + the meta/import-list/options/import-manifest files.
  - **#106 pack could ship a STALE stdlib/klib.** `scripts/pack-nuget.sh` rebuilt the frontend klib and the stdlib
    ref/rt dlls only when MISSING, so a `pack-nuget.sh` run (directly or via `verify-packaged-sdk.sh`) could package a
    klib/stdlib baked by an older toolchain against freshly-built tools. It now uses the fingerprint-aware
    `need_fe_klib`/`need_stdlib_ref`/`need_stdlib_rt` builders (`scripts/lib.sh`), which rebuild on toolchain
    fingerprint mismatch OR absence.
- **ilemit ([tmyt/dotkt#91]/[tmyt/dotkt#92], area:ilemit): generic-field token anchoring + the abstract-slot body invariant.**
  - **#91 generic FIELD token anchoring** — a raw `@ClrField` access whose owner is a GENERIC type emitted a bare
    `C`1::f` operand ("not fully instantiated": `ResolveField`'s `TypeBuilder.GetField(constructed, fb)` threw
    `field must be declared on a generic type definition`, and ilverify crashed with an `IndexOutOfRange` in
    `get_GenericParameters`). `ResolveField` now mirrors the #84-I METHOD-side anchoring, FIELD side: an inherited
    generic-base field is re-anchored onto the owner's CONSTRUCTED base instantiation via a new
    `AnchorInheritedFieldOnBase` — for a non-generic subclass (`constructed == null`), a constructed generic-subclass
    receiver, and a self-instantiated `this` inside a generic method alike. Suspend-free; pure Reflection.Emit
    mechanics (the kotlinx port hit it at `JobSupport.kt ResumeAwaitOnCompletion`1.invoke`). Regression case:
    `cases/il-genfield`.
  - **#92 abstract-slot body invariant** — `EmitMethodBody` now skips any MethodBuilder DECLARED `Abstract`
    (`mb.IsAbstract`, the single source of truth) rather than re-deriving abstractness from the CIR `abstract` flag,
    making the `Method body should not exist` emit-crash impossible while WARNING (naming the def) when the skip is
    unexpected — so an upstream defect (a body written onto an abstract slot) stays visible. The dup-`$dupN` counter now
    runs for class abstract slots too, keeping the body phase in lockstep with declare.
- **kotc ([tmyt/dotkt#57]/[tmyt/dotkt#89]/[tmyt/dotkt#40], area:kotc): three frontend symbol-resolution fixes.**
  - **#57 the `length`-reference deferral is OWNER-keyed, not override-chain-keyed.** A property reference to
    `length` on a USER class implementing `CharSequence` now lifts faithfully — its accessor resolves on the
    class's OWN emitted `get_length` slot — for a DIRECT override AND one INHERITED through an intermediate
    (`B : A`, `A : CharSequence`). The retired override-chain walk over-deferred the direct case (a compile
    error on a liftable reference) while missing the indirect one (both should behave alike). The deferral now
    keys on the accessor's RESOLVED declaring owner (`getterFn.parent`): only a .NET-mapped CharSequence owner
    (`String`/`StringBuilder`/the polymorphic `kotlin.CharSequence`, whose slot bir2cir renames/collapses) stays
    deferred. (`BirEmitterLifts.kt`)
  - **#89 a CROSS-MODULE top-level `val` read is attributed `owner:null`, not the READING file's class.** A
    computed top-level val deserialized from the frontend metadata klib is PACKAGE-keyed (its parent is a package
    fragment, not an `IrFile`), so kotc cannot name its declaring file class and no longer mis-owns it to
    `<ReaderFile>Kt` (the #80 `COROUTINE_SUSPENDED` root). It emits the same "unresolved owner" fact it already
    emits for a cross-module top-level FUNCTION; bir2cir binds the true declaring file class off the ref.dll.
    (`BirEmitterCalls.kt`)
  - **#40 verified already-resolved on current main; regression guard added.** A cross-module `@InlineOnly` +
    `@ClrIntrinsic` stdlib function keeps its `@ClrIntrinsic` binding across the assembly boundary — kotc carries
    the annotation as UNCONDITIONAL, opaque ref.dll metadata (`attrsJson` is not gated on `@InlineOnly`), and
    bir2cir substitutes the plain call to the bound BCL member. No code change.
  - Regression cases: `cases/il-charseqlenref`, `cases/il-xmodtopval`, `cases/il-inlonlyintr`.
- **bir2cir/ilemit ([tmyt/dotkt#93]/[tmyt/dotkt#71]/[tmyt/dotkt#94]/[tmyt/dotkt#95], area:bir2cir/ilemit): a family of numeric/equality miscompiles.**
  - **#93 numeric widening** — `Byte`/`Short`/`UByte`/`UShort` arithmetic (and `inc`/`dec`/`unaryMinus`) dropped the
    operator's DECLARED return type, so the value truncated to the narrow left operand on box/narrow-store
    (`(100.toByte())+(100.toByte())` → `-56` not `200`; `(255u as UByte).inc()` → `256` not `0`).
    `PrimitiveOperatorLowering` now wraps the lowered bin/unary/inc op in a `conv` to the frontend-resolved return
    type (`dynRet`) for the narrow/char owners — generalizing the pre-existing `Char` precedent (`Byte`/`Short` → `Int`,
    `UByte`/`UShort` → `UInt`). Full-width owners stay bare.
  - **#71 ilemit unsigned conv arms** — `EmitConv` gained the `Conv_U1`/`U2`/`U4`/`U8` arms for `UByte`/`UShort`/`UInt`/`ULong`
    targets (previously a `default:` throw that aborted the whole compile); required by the #93 widening and by explicit
    `.toUByte()`/`.toUInt()`/… conversions.
  - **#94 unsigned shr** — `UInt`/`ULong` `shr` now lowers to `>>>` (ilemit `Shr_Un`, zero-filling) instead of the
    sign-propagating `>>` (`UInt.MAX_VALUE shr 1` → `2147483647` not `4294967295`). `shl` is bit-identical and unchanged.
  - **#95 structural float equality** — a STRUCTURAL `==` over two `Double`/`Float` (data-class `equals`/`hashCode`) now
    routes to the total-order helper (`clrDoubleEquals`/`clrFloatEquals`: `NaN == NaN` true, `+0.0 != -0.0`) instead of
    IEEE `ceq`, restoring the equals/hashCode contract. A DIRECT `a == b` stays IEEE (`ieee754equals`) — unchanged.
  - Regression cases: `cases/il-bytewiden`, `cases/il-unsignedshr`, `cases/il-structfloateq`.
- **stdlib: `copyInto` is now overlap-safe (#97).** All nine `copyInto` actuals (generic `Array<T>` +
  the 8 primitive arrays) bind to `System.Array.Copy` (memmove) instead of a naive forward element
  loop, which clobbered source slots on an overlapping self-copy with `destinationOffset > startIndex`.
  This silently corrupted `ArrayDeque.add(index, elem)` (an in-place right shift). (`_ArraysClr.kt`)
- **stdlib: `Double/Float.roundToInt`/`roundToLong` round half-up toward +inf (#103).** They now
  implement `floor(x + 0.5)` (ties: `2.5→3`, `-2.5→-2`, `0.5→1`, `-0.5→0`) instead of delegating to
  `kotlin.math.round` (banker's ties-to-even). NaN throws `IllegalArgumentException`; out-of-range
  saturates to `Int`/`Long` `MIN`/`MAX`. `kotlin.math.round` itself stays ties-to-even. (`MathClr.kt`)
- **stdlib: `CharArray.copyOf(newSize)` zero-fills grown slots with the null char `'\u0000'` (#128),**
  not a space (`U+0020`) — the Kotlin contract fills grown slots with the element type's default
  value (the null char for `Char`). (`_ArraysClr.kt`)
- **kotc ([tmyt/dotkt#66]/[#67]/[#68]/[#69]/[#70], umbrella [#72], area:kotc): lower five fail-loud
  callable-reference / capture / delegate shapes the frontend accepts (stop aborting the compile).**
  Each was a whole-compile abort on frontend-accepted IR; all now lower to pure Kotlin BIR facts (bir2cir
  owns any CLR/coroutine transform). (#66) a callable reference to a `lateinit var` / `@ClrField` property
  (`b::name`, `Box::name`) — the lifted `KProperty` class now reads/writes the plain backing field
  (`lateinitGet`/`field`/`setFieldExpr`) instead of a non-existent `get_/set_` accessor slot. (#67) a
  reference to a `suspend` function (`::work`, `d::apply`) is emitted as a `newSuspendLambda` adapter (the
  suspend lambda `{ a -> target(a) }` with a `suspendCall`-tagged body; bir2cir builds the `SuspendLambda`
  SM), and `kotlin.reflect.KSuspendFunctionN` now erases to a suspend `fn` type like `KFunctionN` — a plain
  suspend `newDelegate` had no cold-suspend lowering and the reflect type-token leaked to ilemit. (#68) a
  local class / object expression that WRITES a captured outer `var` now shares the enclosing frame's heap
  ref-cell (the mutated capture is promoted by `computeRefCells` before the lift). (#69) a local class
  capturing an enclosing TYPE PARAMETER is lifted GENERICALLY (reified CLR generics) — the object-literal
  generic-capture scan is reused, and a local class being DENOTABLE (`val l: L`, member access `l.x`),
  `ownerSpec`/birType now name the constructed `L<T>`. (#70) a TOP-LEVEL delegated property with an
  arbitrary `getValue`/`setValue` provider (`val x by Provider()`) routes through the static
  `x$delegate.getValue/setValue` with a null thisRef (only member/local delegated properties were routed
  before). Regression cases: `cases/il-{lateinitref,suspendref,writecapture,genlocalclass,topdeleg}`.

## 0.9.6-rc7 (2026-07-18)

A large compiler-correctness release. The kotlinx.coroutines CLR port now compiles through the
Kotlin frontend + the entire bir2cir layer (cold-core suspend lowering fires; all 108 CIR files
emit) and advances into ilemit; the remaining ilemit-stage work to make it fully compile+run
(abstract/interface/cross-member suspend cold-lowering completion + the covariance/variance-erasure
representation) is tracked under #85 and moved to 0.9.7. Highlights of what landed: the inline-splice
family (Set A #60–#63, the §4.4ii suspend-carrier + cold-SM nested-closure capture families, member
inline fake-override splicing #87); suspend cold-lowering (Defect A/B, #78/#80/#82, catch-hoist,
COROUTINE_SUSPENDED + coroutineContext binding, splice-local spill); #73 atomic-wrapper cross-module
re-import; #76 generic-base type-arg carriage; #77 concrete-collection loadability (ArrayDeque et al.);
#81 class delegation `$$delegate_0`; #83 interface companion members; #24/#36/#44 correctness; plus
packaging/docs (#50/#53/#54). The nullable value-type generic representation design is settled in #86
(object-erasure) for 0.9.7.

### Fixed

- **bir2cir ([tmyt/dotkt#80] residual, area:bir2cir): an ALREADY-OWNER'd `COROUTINE_SUSPENDED` read now canonicalizes.**
  The #80 fix rebinds the top-level val `COROUTINE_SUSPENDED` (`kotlin.coroutines.intrinsics`) to its declaring
  `IntrinsicsKt` owner, but only handled the OWNER-NULL emission. The real kotlinx.coroutines port surfaced a variant it
  missed: a NON-suspend reader (`DispatchedCoroutine.getResult(): Any?`) emits the read ALREADY-OWNER'd —
  `callStatic owner=kotlinx.coroutines.Builders_commonKt method=COROUTINE_SUSPENDED prop:get args:[]` (kotc stamps the
  reader's own file class, not owner-null) — so `MemberCallSubstitution`'s owner-null-only rewrite slipped it through and
  the owner-ful non-CLR path merely renamed the accessor, leaving ilemit with `kotlinx.coroutines.Builders_commonKt.
  get_COROUTINE_SUSPENDED not found` (15 sibling nodes normalized correctly). The COROUTINE_SUSPENDED canonicalization is
  now hoisted ahead of the owner-dependent branches and rebinds BOTH shapes (owner-null and already-owner'd) to
  `IntrinsicsKt.get_COROUTINE_SUSPENDED`, static + argless-guarded, regardless of the owner kotc stamped. Non-suspend
  readers never reach SuspendColdLowering's SM-body canonicalization, so this is their only rebind site.
  Gate: `cases/il-suspendintrinsicowned` (a non-suspend `getResult`-shape member reading the intrinsic val).

- **kotc ([tmyt/dotkt#88], area:kotc/area:bir2cir): splicing an inherited member `inline fun` on a GENERIC owner.**
  When an inherited member `inline fun` is spliced (a lambda arg → the same-module splice path) and its OWNER class is
  GENERIC — `IntBox : Container<E>` calling `Container.transform` — kotc's F2A guard omitted the owner's type args because
  the dispatch receiver's static class (`IntBox`) is not the owning class (`Container`). The spliced body's
  `tv{scope:type,0}` (the owner's `E`) then stayed OPEN, so ilemit typed the dispatch temp as the bare open generic →
  `BadImageFormatException`. kotc's F2A now carries the owner's args from the CORRESPONDING-SUPERTYPE instantiation
  (`Container<Int>` seen through `IntBox`), computed substitution-aware + transitively via
  `AbstractTypeChecker.findCorrespondingSupertypes` (`BirEmitter` gains `irBuiltIns` for the type-system context); the
  bir2cir F2B consumer (`recvs.dispatchTypeArgs`) was already implemented. The payload's `tv{scope:type,i}` now
  concretizes to the real call-site type. A TYPE-PARAMETER receiver whose bound fixes the owner (`T : Container<Int>`)
  is handled the same way. When the supertype instantiation CAPTURES a projected/star owner arg (`S : Slot<*>`) it is
  OMITTED (kept at the pre-#88 positional bind / ilemit object-fallback) rather than carried as a misleading
  `Base<Any>`. Gate: `cases/il-inheritedgenericinline` (value-type `Container<Int>`, reference-type `Container<String>`,
  and a `T : Container<Int>`-bound receiver; the value-type path being the one that BadImageFormats).

- **kotc ([tmyt/dotkt#87], area:kotc/area:bir2cir): an INHERITED member `inline fun` with a lambda arg now splices.**
  A member `inline fun` called through a SUBCLASS receiver — e.g. kotlinx.coroutines
  `ConcurrentLinkedListNode<N>.nextOrIfClosed`, a non-local-return-lambda inline fn invoked on a `Segment<S : Segment<S>>`
  — resolves in IR to a FAKE OVERRIDE whose `parent` is the subclass and whose `body` is `null`. kotc's inline-call
  emitter (`emitOwnerfulInlineNode`) took the `callInline` `owner` from `callee.parent` verbatim, so it named the
  SUBCLASS; but bir2cir's InlineSplice keys the `[KotlinInline]` payload under the REAL declaring class (`InlineBirStash`),
  so the lookup missed and the port build broke with `bir2cir: inline splice: cannot splice
  kotlinx.coroutines.internal.Segment.nextOrIfClosed (pc=1 ga=0): no [KotlinInline] payload found`. A fake override also
  has a `null` body, so the same-module splice-routing gate (`callee.body != null`) misrouted the call to the cross-module
  path. Now kotc resolves the fake override (`resolveFakeOverride`, the same normalization the ordinary member-call owner
  path already did at three sites but the inline path had omitted) for the callInline owner + all declaration facts, and
  routes the splice on the resolved declaration's body. The port now advances past bir2cir InlineSplice into the
  suspend-lowering + ilemit stages. Gate: `cases/il-inlineinherit` (a member inline fn with a non-local-return lambda,
  inherited through both a plain subclass and a self-bounded generic `Seg<S : Seg<S>>`, spliced at the subclass call site).

- **bir2cir ([tmyt/dotkt#78], area:bir2cir): a suspend call INSIDE a catch handler now lowers (catch-hoist).**
  Resuming into a CLR `catch` clause is illegal IL, so `SuspendColdLowering` used to refuse any suspend fun with a
  suspension in a catch/finally handler (`SuspensionsSupported`'s `inHandler` gate) — and, because the cold-entry ABI is
  coupled to body transformability, ONE such refusal (`SelectImplementation.processResultAndInvokeBlockRecoveringException`,
  a `catch (e) { recoverAndThrow(e) }`, kotlinx `Select.kt:723`) cascaded to the entire `select` family. bir2cir's new
  `HoistSuspendingCatches` (`toolchain/bir2cir/SuspendColdLowering.Normalize.cs`) lifts a suspending catch handler OUT of
  the CLR clause: the real catch only records the exception into an SM-field-backed capture, and the handler body runs as
  gated straight-line code (`if (__exc$N != null) { … }`) after the try, where the state machine segments its suspension
  normally. Finally-free trys only (hoisting past a finally would flip Kotlin's run-after-handler ordering). Gated in
  lockstep in `SuspensionsSupported`. Also fixes a pre-existing latent bug the newly-lowered value-returning try/catch
  exposed: an init-less value-type SM `var` (kotc's `tryExpr` value var) emitted a null-Int32 const; it now default-inits.

- **bir2cir ([tmyt/dotkt#80], area:bir2cir): `COROUTINE_SUSPENDED` intrinsic reads resolve everywhere.** The top-level
  val `kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED` was mis-owned by `MemberCallSubstitution` to the ENCLOSING file
  class (it is a val, absent from the top-level-fun index), so a bare `<FileClass>.get_COROUTINE_SUSPENDED` reached ilemit
  unresolved. Now bound to the canonical `IntrinsicsKt` owner at substitution time — covering EVERY reader, including the
  port's NON-suspend readers (`getResult(): Any?` in `CancellableContinuationImpl`/`Builders`) that never reach the SM
  transform. The former F2-only `SubstBlock` canonicalization is lifted into `Rewrite`/`RewriteNoSpill` so every SM-body
  path (incl. a direct user `suspendCoroutineUninterceptedOrReturn { … COROUTINE_SUSPENDED }`) normalizes to the SM's own
  `Suspended()` marker.

- **bir2cir ([tmyt/dotkt#82], area:bir2cir): a structured collection loop whose body spans a suspension now lowers
  (loop-flatten).** A `forArray` (`for (x in array)`) or `forEachInline` (inline `Iterable.forEach`) loop whose body
  contains a suspension carries implicit loop machinery (array + index; or an IEnumerator) and an element local that cross
  the resume point — but the straight-line SM cannot segment a structured loop, so a splice-generated element local
  reached ilemit as `load unknown var __inlsN$element`. bir2cir's new `FlattenSuspendingLoops`
  (`toolchain/bir2cir/SuspendColdLowering.Normalize.cs`) desugars such a loop to flat `label`/`brIf`/`goto` CFG with its
  loop temps made explicit `{k:var}`, so `CollectVarFields` spills them into SM fields and the resume re-enters across the
  back-edge. `forEachInline` uses a NON-generic `IEnumerator` (unconditional `viaNonGeneric`) so an open generic-param
  element never mints a broken `IEnumerable<!!T>` TypeBuilder token. A post-Build tripwire (`AssertLocalsResolved`) now
  converts any residual unspilled SM local into a loud bir2cir error instead of a distant ilemit `load unknown var`.

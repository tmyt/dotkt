# bir2cir responsibility and state ownership

Inventory baseline: `66c5940913f068dacc5f63366d09166d3661addd`, after the
typed-IR migration and directory housekeeping. This is an implementation map
for #1555, not a claim that moving files or removing `partial` establishes a
boundary. The completion obligations in [phase contracts](bir2cir-phase-contracts.md)
continue to apply at every construction/admission point.

## Dependency map and ordered work

```text
compile-reference catalog -> metadata scan -> reference declaration facts
                                     |       + query caches / reflection lifetime
                                     v
source roots -> captured declaration bindings -> native proof / carrier demand
                       |                          -> surface preparation
                       |                          -> type projection
                       + explicit producer transitions -> realization / rewriting
                                                         -> physical declaration view
source call signatures -> suspend cold-call projection -> referenced-call selection
physical declarations + reference queries -> exact member/slot resolution -> CIR
```

Source semantic facts, physical declarations, derived analyses and disposable
caches are different authorities, even where they currently share a class.
The graph is ordered, but callbacks and process-wide fields currently hide
some of its edges. The following dependency-ordered slices keep intermediate
contracts usable; this issue is not complete after the first slice.

1. Preserved source call signatures have a compilation owner. Capture,
   cold-call extension, query and release are explicit operations with no
   process table or sibling-file cleanup dependency (implemented below).
2. Fixed-reference loading, lookup caches and reflection lifetime have an
   explicit owner. Local-emission changes invalidate the physical-name view,
   not fixed reference facts. Further metadata-ingestion/payload
   authority extraction remains separate work.
3. Separate exact member selection and physical-slot policy from graph writers;
   remove ambient reference/local-declaration inputs and process allocators.
4. Narrow remaining carrier realization/rewrite capabilities from the inventory
   below. Preserve the existing demand, proof and declaration transition
   authorities instead of creating parallel signature/declaration catalogs.

No slice may silently absorb unrelated feature or compiler-bug work. In
particular #1526 and the independently tracked generic constraint bug #1600
are not acceptance conditions for a responsibility extraction.

## Carrier and declaration family

The issue's original `_localMethods`, `_localDeclarations`, `_sourceSupertypes`,
`_usedCarrierNames` and `_valueSlots` are **not static fields in this baseline**.
Their relevant responsibilities already have the owners below. Retain those
useful boundaries; do not reproduce the old process-state inventory as if it
were current.

| Owner / facts | Creator, readers and writers | Lifetime and invalidation | Mutation rights / remaining dependency |
|---|---|---|---|
| `CurrentDeclarationBindings`: `source`, `sourceFiles`, roots, definition / method / identity / supertype indexes | Pipeline captures once; demand, proofs and realization read the same `DeclarationBinding` anchors; `GetSource` admits producer-reported declarations | Source phase until `CompleteSourceCustody`; source membership is the captured input clock, not a refreshable physical index | Binding registration, retirement and release only through its methods. Read-only dictionaries still contain mutable source anchors; callers are not isolated just by their interface type |
| Same owner: `retiredMethods`, `retiredProperties`, `retiredForests`, `bound` | Actual mutation producers report attached occurrences before detaching; terminal ledger validates continuations | Cleared only after all terminal checks pass; failed completion retains authority | `RetireSourceMembers/Properties/Forest` and `CompleteSourceCustody`, not a scan that guesses missing declarations |
| `CurrentDeclarationPreparation`: generic owner eligibility and local CLR aliases | Constructed from captured bindings before physical shaping; demand/projection consumers | One source clock; never rediscover eligibility from a physical declaration view | No graph writes; stores bindings, not independent signature declarations |
| `CarrierDemandPlan`: `owners`, `usedNames`, `BaseContracts`; mutable `Owner.Needed` and chosen `ErasedName` | Demand walk and fixed-point reachability choose requirements and deterministic names; synthesis/projection consume them | One module source lifetime; name allocation is not repeated for late physical types | Initial demand analysis does not append/replace graph nodes. Later reference-contract allocation uses the same owner's `AllocateReferenceContractName`; realization no longer borrows a writable name set |
| `NativeProofPlan`: `rows`, `methods`, `foreignTypes`, `foreignMethods`, `lateLocalTypes`, `lateSourceTypes`, captured definitions/supertypes | Preparation records source bounds; exact proof demand adds rows; declaration producers transfer independent method proofs and register current definitions | Source-bound rows follow exact declaration anchors; foreign query caches depend on the fixed reference universe; late local rows updated at reported physical births | Proof updates are not graph mutation. Do not rebuild proof demand from final physical bodies or names |
| `CarrierSurfacePreparation` / `SourceMethodSignatures.signatures` | Demand first, method-bound preparation next, field accessor birth next, signature capture last; realization consumes | Signature snapshot after accessor birth, explicitly ended by `CompleteSourceLifetime` | Surface preparation currently combines planning and accessor attachment; keep its birth callback/order explicit while narrowing it |
| `SourceCarrierDeclarations`: `slots`, `factories`, `PhysicalDeclarationSources`; `SourceSlot` / `SourceInnerFactory` selected and emitted bindings | Realization records proposals and actual emission separately; metadata/suspend consumers use producer-reported physical/source links | Source rows cleared by `ReleaseSourceRows`; physical/source links continue through suspend projection | `Add` owns membership; mutable row properties and public dictionary remain wider than necessary |
| `CarrierRealizationContext`: references to surface/projection/bindings/proofs/values, alias merge and admission callbacks | Pipeline constructs; carrier synthesis, constraints and rewriting read it | One module source realization; source custody ends at `CompleteRealization` | `AttachMethod/Type` append actual nodes, register bindings/proof transfers, then notify admission callbacks. This is not an unrestricted pipeline mutation capability, but consumers currently receive more of it than each requires |
| `CarrierProjectionContext`: declaration reader, current view, owners, alias/policy/proof inputs | Separate `ForSource`, `ForCurrentSource`, `ForReferencedValuesSource` factories preserve membership/precedence | Source reads guarded against bound custody; a current view is a pass-local physical snapshot | Type projection should consume facts without gaining synthesis/retirement rights; alias-header reads must not recreate retired source graphs |
| `SourceRewriteState`: `projections`, `arrayElements`, `projectedReads`, `normalizedMethods`, type query view | One rewrite creates and updates derived expression facts; local rewrite helpers consume | Per rewrite, `ClearProjectionFacts` resets expression facts; normalized-method set is consumed explicitly | Rewriter replaces owning graph slots; analysis dictionaries do not own declarations. Broad realization access remains to narrow |
| `CurrentPhysicalTypeView` | `RefreshCurrentPhysicalTypesSource` captures actual present definitions and already-admitted bindings | Recreate after a declaration birth/removal; refresh registers physical owners with value policy and native proofs | Does not rediscover source eligibility, repeat name allocation, or authorize unreported declarations |
| `CarrierRealizationContinuation.LocalOwners` | Terminal source completion exports only already chosen, needed physical carrier names | Immutable-by-use physical continuation after source custody ends | No source anchor or graph writer handed to later physical resolution |

`GenericRepresentationPolicy`, scalar slots, nullable views, alias transitions and
suspend declaration transitions are distinct existing owners. Their contracts
are inputs to this family, not fields to copy into a new all-purpose context.
`AttachMethod/Type -> admission callback -> declaration/proof/policy update` is
an intentional producer-to-consumer notification edge. It must not become a
backreference through which an analysis can invoke arbitrary orchestration.

## CLR member resolution family

The resolution path's state and its current ownership:

| State | Writers / readers | Lifetime and problem | Required boundary |
|---|---|---|---|
| `ReferencedCallSignatures.snapshots`, `nextId` | Module source capture allocates; cold-call capability extends; referenced graph reader borrows read-only vectors; root cleanup removes markers only | Compilation lifetime, independent from another module and each root's marker cleanup | Private catalog; only capture, projection, query/release and disposal operations; no selector reads the table |
| Binding reference queries | Pipeline supplies its existing reference index; binding walks forward it to exact selection, templates and reference rendering | Borrowed compilation universe, including calls before final binding; no process-held last-call index | Explicit method inputs; callers cannot initialize another walk's reference universe |
| `_localTypes` | Native, declaration, referenced and delegate entry points replace it | Per invocation facts stored process-wide | Pass-owned local physical identity input, not reference metadata |
| Referenced `externalCanonicalTypes`, `localDeclarationIds` | Caller supplies; entry normalizes absent inputs and planner reads | Entry-local references, released with that walk | Ordinary explicit selection inputs, not process-held sets or new catalog owners |
| Physical delegate `localCarriers` | Pipeline supplies its chosen carrier relation to physical target binding, construction and value-flow | Borrowed module relation; value-flow keeps it in its existing pass-local index | Explicit seam/adapter input, not a process-held map; source-only target stamping does not consume it |
| Nullable-definition / root-array queries | Native matching asks the current reference index | Borrowed handles from its universe; no process-wide outer Type cache | Fixed-input lookup is cached only by the owning universe, so interleaved compilations do not share handles |
| `PhysicalDelegateDeclarations.nextAdapter`, `nextValue` | Adapter declaration construction and value-temporary allocation | One compilation, shared by construction and final value-flow consumers | Pipeline supplies the declaration builder; consumers retain separate per-file caches and publication rights |
| Index `nativeSignatures` | Metadata scan and physical CLR binding query the existing `NativeSignatureReader` by path/MVID/token | One reference index owns the decoder from scan through binding and drops it on disposal | Borrowed read-only declaration facts, preserving the decoder's existing behavior; ordinary declaration projection remains outside it |

`CollectionTemplates`, well-known-member tables and `ResolvedOnlyKinds` are
fixed vocabulary, not invocation state. Keep them shared. Matchers that only
substitute/compare structured types can remain pure static policies.

`ClrDeclarationMatching` now owns scalar descriptor matching, candidate selection
and reflected signature projection. Reference queries and local physical type
membership are explicit arguments; it has no invocation fields, graph writer,
allocator or resource lifetime. It returns selected reflection declarations and
new type descriptors, borrowed within the index lifetime. Binding entry points
still own call/slot graph updates and pass their current facts; removing their
remaining ambient invocation state is a separate boundary, not claimed here.

`ClrMemberReferences` constructs exact method/field identities and physical
declaring/signature types from selected reflection declarations and explicit
reference/local-type facts. It has no fields, graph writer, resource ownership
or resolver backreference. Selection and dispatch policy stay with binding.
Existing binding entry points still supply their current facts; the two scalar
forwarders for current external callers now receive the reference index explicitly;
local physical-type membership remains a separate input-extraction obligation. No new compatibility path or
internal-mutation guarantee is introduced.

Referenced declaration validation receives the planner's current local-type
membership alongside its own reference index. Constructed-super signature
validation and final MemberRef construction use those same facts explicitly;
the index does not borrow the resolver's ambient scalar forwarder for this path.
The referenced graph writer still has the remaining binding inputs listed above.

Physical delegate seam checks receive the pipeline's chosen local carrier relation explicitly.
Construction forwards it to materialization; final value-flow retains it in
its existing index and supplies it per conversion. Source-only target stamping
no longer accepts an unused relation. Cache scope, adapter construction and
publication order stay unchanged; no map copy or new owner class is introduced.

`PhysicalDelegateDeclarations` owns adapter declaration construction and its two
identity allocators. The pipeline creates one instance for construction and final
value-flow lowering. It receives the existing selected plan, reference queries
and Unit-value factory and returns a new declaration, not an appended graph.
`TypedPhysicalDelegateAdapter` retains per-file reuse and declaration publication;
the two lowering consumers keep separate caches as before. Allocation order and
declaration bodies are unchanged within a compilation. No new validation,
defensive copies or internal-mutation guarantees are added.

Scan borrows method/slot queries before index construction; binding uses the
same index-owned decoder. It retains no reflection handles and closes each PE
reader locally. There is no additional owner wrapper or defensive query copy.
The shared grammar and emitter's instance reader are unchanged. Binding methods
borrow that index explicitly rather than saving it process-wide. The remaining
ambient local-type membership above is a separate obligation.

The referenced-call path already has `ReferencedCallInput` and
`ReferencedCallPlan`: the reader/writer owns graph transport; selection owns
the chosen declaration and physical reference. Preserve this plan/apply seam.
Other native/slot walks still combine traversal, selection and writes. The
selector must not append declarations; adapter synthesis must report its exact
new declarations for the existing admission obligations.

### Implemented: compilation-owned call signatures

`ReferencedCallSignatures` owns the first row above. `LoadBirFiles` creates
and disposes it in one invocation, including exceptional exit. Its only inputs
are current call descriptors at the pre-erasure source clock and the explicit
completion type supplied by cold lowering. It has no reference-index or
declaration-mutation capability.

- `Capture` deep-copies signatures and writes only call snapshot tokens. It
  respects opaque executable payloads and does not recapture an existing token.
- `ProjectColdCall` creates an independent extended row and writes the new
  call's token. Suspend lowering receives this operation as a callback, not the
  catalog, other CLR binding services, or arbitrary graph mutation rights.
- `Read` lends the frozen vector read-only to the graph reader. The referenced-call planner
  receives that source vector as data and no longer reads a global table.
  The graph reader/writer receives only read and marker-release callbacks, not
  capture, cold projection or module disposal authority. Calls created without
  a snapshot still use their explicit current descriptor.
- `ReleaseMarkers` removes tokens only from the processed root. Snapshot rows
  remain valid for independently owned clones in sibling roots until module
  disposal. It does not release another file's shared row.
- `Dispose` clears all rows; a new compilation starts its own allocator.
  No extra disposed-query or missing-token diagnostic contract is added.

Boundary selftests cover nested/payload capture, source/cold-row isolation,
real cold-call synthesis through the supplied capability, cross-file clone
selection after original-root release, module disposal, and interleaved then
subsequent independent compilations. These tests replace/extend the existing
signature and referenced-call checks rather than adding a duplicate runtime
test host. Other ambient CLR-resolution state in the baseline table remains
separate follow-up work; this slice does not claim the whole issue complete.

## Reference metadata family

`Build` creates a `ManagedReferenceCatalog`, scans each reference in a temporary
metadata context into `ReferenceAssembly` / `ReferenceDotKtMetadata`, then
aggregates the facts. These are declaration facts, not current source graphs.
The scan context is disposed before queries. Queries lazily create two further
reflection contexts, whose `Type` / `MemberInfo` handles must live through all
physical binding consumers and no longer. `ReferenceMetadataUniverse` owns
these lazy resources and the fixed-input type cache. `Pipeline.Run` owns the
index with `using`, so both normal and exceptional exits dispose the universe.

Each field below belongs to the `ReferenceMetadataIndex` instance unless noted.
For the **ingested facts**, the writer is scan/aggregate construction, readers
are the index's narrow queries and their lowering/binding consumers, lifetime is
one fixed reference universe, and there is no source-graph mutation right or
cache invalidation. Grouping fields does not permit later writers.

| Fact responsibility | State fields (leading underscore omitted) |
|---|---|
| Input identity / diagnostics | `assemblies`, `compileRefs`; each `ReferenceAssembly.DotKt` scan result and its `Diagnostics` |
| Type identity / representation | `ownerAlias`, `ownerAliasKind`, `ownerKind`, `ownerKindByPhysicalOwner`, `byRefLikeOwners`, `byRefLikePhysicalOwners`, `dotKtOwners`, `richEnums`, `conflictingRichEnums`, `basicEnums`, `conflictingBasicEnums`, `existentialPhysicalBySemanticOwner`, `existentialPhysicalOwners`, `existentialSemanticByPhysicalOwner`, `managedReferenceContracts` |
| Companion / static declaration hosts | `fileClassOwners`, `companionStaticByPhysicalOwner`, `singletonCompanionCarrierBySemanticOwner`, `semanticOwnerByCompanionCarrier`, `companionCarrierByPhysicalOwner`, `companionSourceNameByPhysicalOwner`, `companionExtensionMembers`, `companionPhysicalOwnerBySemanticType`, `genericStaticCarrierBySemanticOwner`, `genericStaticCarriers`, `companionSemanticTypeByPhysicalOwner` |
| Declaration frames / constraints | `ownerArity`, `ownerTypeParams`, `ownerTypeParamDeclarations`, `ownerNullableFrames`, `kotlinConstructorOwnerFrames`, `publicParameterlessConstructibleOwners`, `publicParameterlessConstructiblePhysicalOwners`, `ownerCtorParams`, `ownerTypeParamConstraints`, `ownerTypeParamBounds`, `innerCapturedCount`, `innerSemanticOwner` |
| Member / constructor / hierarchy identity | `membersByOwner`, `membersByPhysicalOwner`, `methodImplsByBody`, `declarationById`, `declarationFamilyCounts`, `ctorsByOwner`, `aliasConstructorAdaptersByOwner`, `referenceTypeShapes`, `referenceTypeShapesByPhysicalOwner`, `exactPhysicalTypeByDottedName`, `physicalTypeBySemanticName` |
| Compiler binding facts | `helperTypes`, `restrictsSuspension`, `topLevelIntrinsics`, `topLevelIntrinsicsBySig`, `ambiguousTopLevelIntrinsics`, `topLevelIntrinsicByref`, `extMemberIntrinsics`, `inlineBacking`, `topLevelStatics`, `collectionFactories` |
| Default / inline payload authority | Index `executablePayloads` delegates to `ReferenceExecutablePayloads`: private `defaults`, `conflictingDefaults`, `defaultsById`, `conflictingDefaultIds`, `ownerlessDefaults`, `ambiguousDefaults`, `inline`, `inlineById`, `conflictingInlineIds`, `ownerlessInline`. Construction is the only writer; every query returns an independent graph |

Payload lookup returns source ingredients for the owning splice/admission
contract, not permission to mutate the stored shared payload. `TypeNode` arrays
and payload graphs are mutable objects: a read-only dictionary wrapper alone
does not enforce this ownership.

| Derived / mutable state | Authority, dependencies, refresh and release |
|---|---|
| `localEmittedTypes`, `physicalTypeNamesSnapshot` | Pipeline reports local membership through `SetLocalEmittedTypes`; the index normalizes its private set and invalidates the physical-name view. Fixed reference facts and their type cache remain unchanged |
| `ReferenceMetadataUniverse.referencesContext`, `references`, `referencesInitialized`, `types` | One universe lazily loads the fixed `ManagedReferenceCatalog` and caches name/arity hits and misses. Scoped, ordered and ambiguity-checked lookups remain distinct queries. Local/Kotlin-owner policy stays outside this cache. Handles are borrowed until disposal; the assembly collection is read-only |
| `ReferenceMetadataUniverse.physicalContext`, `physicalStdlib`, `physicalInitialized` | The same owner lazily creates the separate runtime-stdlib context using the catalog's existing twin policy, not the semantic/ref-reader context. Disposal releases both contexts and cached handles, including after failure |
| Index `shippedCache` | Exact trusted-ID physical-twin selection owns cached member results; disposal clears these before releasing the universe. No name/layout inference or load-context mutation is granted to consumers |
| Index `literalFieldCache`, `volatileFieldCache`, `awaitPlanCache` | Cold lowering and source storage consume these after initial local membership is set and before its late update. They are not queried after that update; disposal clears them. No new arbitrary-update/invalidation guarantee is needed |
| `kotlinMapStorageDefinitions`, `kotlinHashStorageDefinitions` | Lazy derived queries over ingested facts/reference signatures, not mutable local declaration graphs |
| Index `nativeSignatures` | The existing decoder is created before scan and retained by the index for physical binding. It stores only managed signature facts, not open reflection/PE handles. No process-wide reader remains |
| Static `assemblyProvenance` weak table | `IsDotKtEmittedAssembly` memoizes the immutable assembly-marker classification. Keys are actual `Assembly` objects, not names or a changing local context; weak keys allow the owning metadata universe to be collected. This cache can remain shared without imposing a process-wide reset order |

The scan's `ReferenceDotKtMetadata` mutable containers are writer-owned scan
outputs. Payload queries materialize separate splice graphs as described below.
Fact aggregation shares read-only templates with the completed scan; later
extraction must freeze/copy values or keep a single explicit writer, not merely
rename the index to a read-only service. Foundational alias/primitive sets and
metadata attribute names are fixed shared vocabulary, not resettable state.

### Implemented: fixed-reference query lifetime

Metadata ingestion now belongs to `ReferenceMetadataReader`: a reference path,
the scan's metadata context and exact native-signature readers produce one
`ReferenceDotKtMetadata`. It owns decoding and the existing metadata-key value
types, but receives no query index or local graph. `Build` owns scan ordering
and context disposal; the index aggregates the completed facts and owns queries
and their caches. Decoded templates retain the existing single-writer handoff,
not a new defensive-copy guarantee. Lateinit decoding follows the same boundary;
its declaration lookup remains with the index. Existing tests cover these paths.

`ReferenceMetadataUniverse` receives only a fixed catalog and has no reference
to the index, source graphs or pipeline. It owns all lazy context creation,
fixed-input type hits/misses and resource release. Metadata `Type` / `Assembly`
results are borrowed and must not outlive it. Exact scoped lookup, the existing
ordered hierarchy probe, and ambiguity-checked binding retain their current
policies rather than sharing an accidental first-match cache decision.

The index applies source/local precedence before querying fixed reference facts.
`SetLocalEmittedTypes` is the update authority for its physical-name view.
Literal/volatile/await queries finish before the late membership update; there
is no later consumer requiring new invalidation behavior. Reflection-context
ownership is not authority to modify declarations or infer source semantics.

Default-value readers receive only a core-type lookup callback for same-context
literal-carrier proofs, never a mutable load context. Scan-time readers use
their scan context; query-time readers use their universe. The physical stdlib
is loaded in the existing distinct runtime-twin universe. Exact member matching
and metadata ingestion/payload policy stay with their current owning units.

Boundary tests use real metadata contexts for lazy loading, cached hits/misses,
arity and assembly identity, interleaved/subsequent compilations and disposal
on exceptional exit. Native nullable/array matching is exercised across actual
universes. The artificial local-owner update fixture is removed; it exercised
query ordering not used by the pipeline. This does not
complete the other resolver, metadata-fact and carrier ownership work in #1555.

### Implemented: imported executable-payload authority

`ReferenceExecutablePayloads` receives only five decoded payload/conflict inputs,
not the reference index, reflection contexts, other metadata facts, or live
source/physical declarations. Scan writes finish before these query indexes are
constructed. Signature/arity/identity/candidate views borrow the same read-only
decoded templates, preserving the original aggregation behavior without an
additional capture copy. The catalog has no scan update capability. Its facts live
with the compilation's reference index; there is no reflection resource to close
or changing local-membership input to invalidate.

Queries return independent maps/graphs for the existing splice/admission owners.
Materializing one lookup result cannot change the templates. Mutation of a
completed scan is not a supported operation to add defensive guarantees for.
Default signature/relaxed/arity ordering,
constructor separation, conflict handling and stdlib ownerless candidates retain
their existing policies. Scan conflict recording and query comparison share
`ExecutableBirCodec`'s pure equality helper; this catalog does not interpret expressions,
select overloads from bodies, or reconstruct declaration identities.

The local `TypedInlineBirIndex` source snapshots and exact reflected optional
default readers remain distinct authorities. Source graph construction, mutation
and declaration admission remain with their existing producers, not this catalog.
Existing default/inline collision and runtime producer/consumer tests remain
the behavior coverage for this extraction; the artificial scan-input mutation
fixture is removed. Other ingested facts, ambient CLR
resolver inputs and carrier capabilities still need their own work.

## Suspend and orchestration boundaries

`SuspendModuleContext` already has an explicit module lifetime. It owns the
entry/closure/helper inventories, source return/field snapshots,
`ConsumedIntrinsicClosures`, late `LambdaOwnerParameters`, `continuationStar`,
`Stage0Complete` and `sourceReleased`. Construction precedes named Cold;
`BeginLambdaPhase` requires completed stage zero; `ReleaseSourceDeclarations`
ends source roots/anchors after terminal handoff. Source transitions own clone,
replace, retire and publish operations; a state-machine builder returns methods,
types and declaration projections rather than mutating an unrelated pass.

`SourceSignatures`, physical parameter/source-link maps, reference queries and
carrier-owner facts are external authorities passed into that context. Cold
call signature projection receives only the module owner's projection callback,
not a CLR resolver or the source call catalog. Do not turn the context into a
container for the full pipeline.

## Adjacent pass dependencies, not completed ownership extractions

The inventory above targets the named large families and their handoffs, not
every mutable field in the compiler. The four ordered work areas are not a
claim that those families have no stateful neighbors. The following inspected
incoming/outgoing dependencies remain relevant when replacing ambient inputs;
they are not all part of the first call-signature slice.

| Adjacent owner / state | Current producer, consumers and lifetime dependency |
|---|---|
| `MemberCallSubstitution`: `_localTopLevelFns`, `_attributeTopLevelOwner`, `_typesWithDeclaredIterator`, `_iteratorTypeDefinitions`, `_localPropertyAccessors`, `_localPropertyOwners`, `_isValue`, `_representations`, `_localFactories`, `_localDeclarationIds` | Source `Apply` replaces invocation inputs and derives iterator/property views; substitution readers consume them and write current calls. Inputs come from module inventory, representation policy and physical property preparation. A narrowed exact-binding unit must receive those facts explicitly, not read this neighbor's ambient state |
| `ForeignStarProjectionBinding`: `_reservedNames`, `_closedViewHints`, `_dependentLocalTypes`, `_localExistentialOwners`, `_localClrAliases`, `_nextTemp`, `_nextDelegateAdapter`, `_bindProjectedHelpers`, `_projectValueType`, `UsedRuntimeFallback` | `ApplyAll` initializes module binding/usage, `ApplyLate` intentionally continues usage/adapter numbering while refreshing current-root hints/names/locals. Projection callback and aliases are set by the initial entry/`ContinueWithProjection`. Adapter birth invokes explicit admission callbacks; late binding currently depends on earlier initialization |
| `CapturedGenericInvocation`: `_next`, `_parameterWitnesses`, `_methodWitnessFrames` | `PreserveSourceFacts` resets all three before capture; witness preparation and later physical binding consume continuing declaration facts. Source preservation, witness capture and final physical binding are separate pipeline clocks; reset order is currently process-global |
| `ClosureSynthesis._pendingCaptureRefusals` | Owning ordinary/materialized synthesis records heap-capture verdicts; `AssertSurvivingCapturesLegal` queries actual surviving classes after suspend pruning. No module reset/release is present. This is deferred storage-legality state, not permission to reclassify captures in an unrelated consumer |
| `NullableTvErasureCallRealign`: `_isValue`, `_refs`, `_sourceApplicationOrder` | Inherited/source-use entry points set invocation inputs, and scalar binding helpers read them. Prepared declaration views and producer transitions remain the authority; narrowing must not reintroduce a competing nullable-signature catalog |
| `StringCharSequenceBridge`: `_adapterEmitted`, `_fired`, `_refs`, `_retLambdas`, `_delegateAdapterCounter`, `_counter` | Source entry sets per-file inputs/usage; initial and materialized adapters share assembly-level emission and numbering state. Carrier/suspend producers call the materialized-return boundary. Graph birth still owes closure/declaration admission; this state is not immutable shared vocabulary |
| `ContinuationErasure`: `_continuationTypeNames`, `_resultTypeNames`, `_physicalTypeNames` | Source preservation and erasure set physical-name inputs through `SetTypeNames`; recursive protocol projection reads them. These are reference-universe inputs stored process-wide, not independent semantic authority |
| `InlineBirStash.Current` / `InlineBirIndex` | Pipeline `Reset` creates an executable-publication index before per-file lowering, then `Stash` populates candidate/ownerless/identity dictionaries. Inline splicing reads frozen rows and materializes independent graphs; reference payload queries are a distinct authority. Current lifetime depends on reset, not an explicit compilation owner |

Temporary allocators outside these families also exist: range/sequence/for-in
lowering, unsafe accessors, alias constructor expansion, enum entries/values,
primitive operators, CLR flags, lexical clone IDs, CharSequence string lowering,
prepared suspend default frames, repeat/precondition/protected-region lowering,
default/inline splicing, call evaluation and physical-operand normalization.
Their naming/label scopes must be inventoried before claiming whole-pipeline
independent invocation or deterministic replay. They are not immutable shared
facts, but their presence is not a new language/runtime bug and does not by
itself authorize changing these unrelated passes in this first PR.

`Program.cs` owns stage ordering, module/file clocks and handoff, not matching,
representation or storage policy. Preserve child-before-parent binding,
original source captures before erasure, declaration birth callbacks, late
physical-view refresh, and terminal source capability release. Remaining broad
context/callback cycles above are explicit extraction work, not justification
for a permanent unrestricted context. This document is updated with each
bounded implementation and its boundary tests; no issue-wide completion claim
is made while an identified ownership edge remains ambient.

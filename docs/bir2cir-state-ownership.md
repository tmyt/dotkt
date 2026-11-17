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
2. Separate reference loading/facts from lookup-cache and reflection lifetime
   ownership. Local-emission shadowing is a changing compilation input, not
   reference metadata; its dependent view must be invalidated by its owner.
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
| `CarrierDemandPlan`: `owners`, `usedNames`, `BaseContracts`; mutable `Owner.Needed` and chosen `ErasedName` | Demand walk and fixed-point reachability choose requirements and deterministic names; synthesis/projection consume them | One module source lifetime; name allocation is not repeated for late physical types | Initial demand analysis does not append/replace graph nodes. `ReservedNames` still exposes a mutable set to realization; this is a capability to narrow, not an immutable plan claim |
| `NativeProofPlan`: `rows`, `methods`, `foreignTypes`, `foreignMethods`, `lateLocalTypes`, `lateSourceTypes`, captured definitions/supertypes | Preparation records source bounds; exact proof demand adds rows; declaration producers transfer independent method proofs and register current definitions | Source-bound rows follow exact declaration anchors; foreign query caches depend on the fixed reference universe; late local rows updated at reported physical births | Proof updates are not graph mutation. Do not rebuild proof demand from final physical bodies or names |
| `CarrierSurfacePreparation` / `SourceMethodSignatures.signatures` | Demand first, method-bound preparation next, field accessor birth next, signature capture last; realization consumes | Signature snapshot after accessor birth, explicitly ended by `CompleteSourceLifetime` | Surface preparation currently combines planning and accessor attachment; keep its birth callback/order explicit while narrowing it |
| `SourceCarrierDeclarations`: `slots`, `factories`, `PhysicalDeclarationSources`; `SourceSlot` / `SourceInnerFactory` selected and emitted bindings | Realization records proposals and actual emission separately; metadata/suspend consumers use producer-reported physical/source links | Source rows cleared by `ReleaseSourceRows`; physical/source links continue through suspend projection | `Add` owns membership; mutable row properties and public dictionary remain wider than necessary |
| `CarrierRealizationContext`: references to surface/projection/bindings/proofs/values, alias merge, reserved names and admission callbacks | Pipeline constructs; carrier synthesis, constraints and rewriting read it | One module source realization; source custody ends at `CompleteRealization` | `AttachMethod/Type` append actual nodes, register bindings/proof transfers, then notify admission callbacks. This is not an unrestricted pipeline mutation capability, but consumers currently receive more of it than each requires |
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
| `ReferencedCallSignatures.snapshots`, `nextId`, `disposed` | Module source capture allocates; cold-call capability extends; referenced graph reader queries copies; root cleanup removes markers only | Compilation lifetime, independent from another module and each root's marker cleanup | Private catalog; only capture, projection, query/release and disposal operations; no selector reads the table |
| `_refs` | Native, referenced, inherited, interface, attribute, delegate, well-known and MemberRef entry points assign it; scalar matchers/renderers read it | Ambient last-call reference universe, including calls made before the final binding pass | Explicit reference queries passed to exact selection / rendering; no dependency on another entry point's initialization |
| `_localTypes` | Native, declaration, referenced and delegate entry points replace it | Per invocation facts stored process-wide | Pass-owned local physical identity input, not reference metadata |
| `_externalCanonicalTypes`, `_localDeclarationIds` | Referenced entry assigns; planner consumes | Per invocation, process-held sets | Referenced selection inputs; not shared mutation rights |
| `_localCarriers` | Local target and delegate entry points assign; physical function/delegate shaping reads | Module physical continuation stored ambiently | Explicit physical carrier relation input |
| `_nullableDef`, `_sysArr` | Lazy scalar queries cache reflection `Type` handles | Process-wide handles tied to whichever metadata context initialized them | Cache with the reference universe that owns the handles; never reuse after its disposal |
| `_nextAdapter`, `_nextValueAdapter` | `FreshDelegateAdapterName` / `FreshUnitDelegateTemporary`; typed physical adapter producers consume | Process allocation order leaks into compilation output | Compilation-owned declaration/name allocator with only synthesis clients |
| `NativeSignatures` reader's method/field dictionaries | Exact pointer signature reader populates by path/MVID/token | Process-retained cache; does not depend on mutable local declarations, but lifetime is currently unlimited | Reference-reading/cache ownership, not an immutable static fact |

`CollectionTemplates`, well-known-member tables and `ResolvedOnlyKinds` are
fixed vocabulary, not invocation state. Keep them shared. Matchers that only
substitute/compare structured types can remain pure static policies.

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
- `Read` returns a deep copy to the graph reader. The referenced-call planner
  receives that source vector as data and no longer reads a global table.
  The graph reader/writer receives only read and marker-release callbacks, not
  capture, cold projection or module disposal authority. Calls created without
  a snapshot still use their explicit current descriptor.
- `ReleaseMarkers` removes tokens only from the processed root. Snapshot rows
  remain valid for independently owned clones in sibling roots until module
  disposal. It does not release another file's shared row.
- `Dispose` clears all rows; a new compilation starts its own allocator.
  Operations after this internal phase frontier fail rather than using a stale
  process catalog. No persisted-format compatibility contract is added.

Boundary selftests cover nested/payload capture, input/query/cold-row isolation,
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
physical binding consumers and no longer. The pipeline currently calls
`DisposeNet` only on its success path; explicit lifetime ownership remains work.

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
| Default / inline payload authority | `kotlinDefaults`, `kotlinDefaultsByDeclarationId`, `conflictingDefaultDeclarationIds`, `kotlinDefaultsOwnerless`, `kotlinDefaultsAmbiguous`, `kotlinDefaultsConflicted`, `inlinePayloads`, `ownerlessInlineCandidates`, `inlinePayloadByDeclarationId`, `conflictingInlineDeclarationIds` |

Payload lookup returns source ingredients for the owning splice/admission
contract, not permission to mutate the stored shared payload. `TypeNode` arrays
and payload graphs are mutable objects: a read-only dictionary wrapper alone
does not enforce this ownership.

| Derived / mutable state | Authority, dependencies, refresh and release |
|---|---|
| `localEmittedTypes`, `physicalTypeNamesSnapshot` | Pipeline reports local emission membership before transformation and after late synthesis via `SetLocalEmittedTypes`; index normalizes a private set and invalidates the snapshot. Type resolution applies local precedence. This input can change while reference facts cannot |
| `netMlc`, `netRefAsms`, `netInit`, `netTypeCache`, `literalFieldCache`, `volatileFieldCache` | Lazy reference queries create/read/write. Depend on fixed catalog contents; local precedence must be applied outside cached reference facts. Context-backed values die at `DisposeNet`; no consumer may retain them into another compilation |
| `physicalMlc`, `physicalStdlib`, `physicalStdlibInit`, `shippedCache` | Physical twin queries load the shipped stdlib, map trusted declaration IDs and cache member handles; same catalog/lifetime, not inferred from physical layout |
| `kotlinMapStorageDefinitions`, `kotlinHashStorageDefinitions`, `awaitPlanCache` | Lazy derived queries; depend on ingested facts/reference signatures. Fixed-universe caches, no source declaration mutation |
| `NativeSignatures` reader's method/field dictionaries | Exact metadata decoding cache currently stored static even though the index owns the query lifetime; same issue as the CLR family's reader |
| Static `assemblyProvenance` weak table | `IsDotKtEmittedAssembly` memoizes the immutable assembly-marker classification. Keys are actual `Assembly` objects, not names or a changing local context; weak keys allow the owning metadata universe to be collected. This cache can remain shared without imposing a process-wide reset order |

The scan's `ReferenceDotKtMetadata` mutable containers are writer-owned scan
outputs. Aggregation currently shares some row objects with them; later
extraction must freeze/copy values or keep a single explicit writer, not merely
rename the index to a read-only service. Foundational alias/primitive sets and
metadata attribute names are fixed shared vocabulary, not resettable state.

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

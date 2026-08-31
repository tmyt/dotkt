# bir2cir materialization contracts

bir2cir may receive executable BIR from the source file, deserialize it from a metadata carrier, or synthesize it
after the main per-file lowering has started. A graph is normalized at the boundary that admits it; a later phase
must not repair the containing file by repeating an idempotent whole-tree pass.

## Capability entries

| Producer / transition | Newly invalid capability | Admission path before publication |
|---|---|---|
| Source BIR | Kotlin `Any` slot names are not CLR slot names | `ObjectSlotRename` at the per-file representation entry |
| `InlineSplice`, `DefaultArgSplice` / `KotlinInline`, `KotlinDefault` | The deserialized raw payload has not entered representation lowering | `MaterializedBirPayload.Normalize` before inspection, substitution, or re-hoisting |
| `ClrEventSubscriptionBinding` | New fixed receiver/handler slots and a new closure ingredient graph | exact returned roots through `NullableTvErasureCallRealign.ApplyMaterialized`, then `ClosureSynthesis.ApplyMaterialized` |
| `StringCharSequenceBridge` | New delegate-adapter closure ingredient graph | exact returned roots through `ClosureSynthesis.ApplyMaterialized` |
| interface/collection/comparable and suspend synthesizers | New receiver-relative call result and possible `Nothing` value position | `MaterializedExecutable.Normalize` before appending the executable graph |
| `InheritedMemberOwnerBinding` | Rebinding may make a constructed declaring-owner frame available | `ConstructedMemberReturnSubstitution.ApplyCall` on the same visited call |
| `DelegateTargetSlotAlignment` | Existing target declaration slots moved to the delegate's erased shape | the explicit `ApplyAfterDelegateSlotAlignment` body-flow transition, only when a slot moved |
| `MemberCallSubstitution` referenced-owner attribution | A referenced declaration becomes resolvable for nullable-Tv use realignment | `ApplyAfterReferencedOwnerBinding`; this is a distinct owner-capability transition |

The initial `ConstructedMemberReturnSubstitution` remains module-wide because suspend lowering must receive every
already-materialized call with its receiver-relative result closed. Later bridge construction and inherited-owner
binding discharge that capability locally, so there is no second module repair walk. Module-wide declaration and
supertype indexes remain module-wide analyses; this rule concerns normalization of newly created executable nodes,
not facts that inherently require the complete emission unit.

## Producer rule

A phase that clones or constructs executable nodes must do one of the following before a consumer relies on them:

1. return the exact materialized roots to the caller and name the capability entry they require; or
2. invoke the relevant construction-boundary contract before appending the node.

Do not add a second file/module invocation of a normalizer merely because it is idempotent. If a transformation
changes an existing declaration capability rather than creating nodes, expose that transition as a separately named
entry, gate it on an actual change, and document why a scoped work item is not sufficient.

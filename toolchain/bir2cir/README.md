# bir2cir source map

These directories organize existing files for navigation; they do not redefine pass responsibilities,
metadata authority, namespaces, or pipeline order. Program.cs remains the orchestration entry point.

| Directory | Existing code placed here |
| --- | --- |
| Pipeline | Declaration custody, inventories and explicit producer/consumer coordination |
| IR | Owning BIR/model/codec/frozen type infrastructure and physical CIR projection |
| IR/Generated | Six generated bir2cir model/codec/projection products; regenerate with scripts/generate-ir-model.py |
| Metadata | Reference catalogs/readers, roundtrip declaration facts, attributes and payload carriers |
| Binding/Clr | Selected CLR members, local/external physical calls and static-owner bindings |
| Binding/Identity | Declaration/slot signatures, names and exact identity consumers |
| Binding/Interop | Native/interface/event/reference-boundary binding families |
| Binding/Properties | Existing backing-field, property/accessor and lateinit families |
| Lowering/Generics | Existing generic/nullability/carrier/frame/witness families |
| Lowering/Values | Existing representation and scalar/value lowering families |
| Lowering/Collections | Existing arrays/collections/iterator/map/range families |
| Lowering/Coroutines | Existing suspend/continuation/state-machine families |
| Lowering/Closures | Existing closure/inline/default-argument/delegate families |
| Lowering/ControlFlow | Existing exception/transfer/termination/precondition families |
| Lowering/Declarations | Existing declaration/visibility/placement/enum/member finishing families |

Families with eight or more files have one existing-family-named directory; smaller families stay together
within their category. Separate existing tests live in the family's or category's Tests directory, still
compiled into the same project. Embedded SelfTest methods stay with their original implementation file.
Shared sources in bir-common, cir-common and ref-common keep their existing owners and linked paths.

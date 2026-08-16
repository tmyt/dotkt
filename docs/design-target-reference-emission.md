# Target-reference emission universe

DotKt has three distinct CLR assembly universes. They are data with different owners and must not be
interchanged merely because two `System.Type` objects have the same `FullName`.

| Universe | Selected by | Purpose |
|---|---|---|
| compiler host | the runtime executing `ilemit` | runs Reflection.Emit and the tool itself; owns no output identity |
| target compile references | MSBuild `@(ReferencePath)` or the direct driver's targeting-pack set | sole authority for contract type/member availability and metadata identity |
| target runtime references | MSBuild `@(ReferenceCopyLocalPaths)` | disambiguates contract/runtime twins already present in the compile set; also owns RID asset selection |

`bir2cir` already resolves Kotlin meaning against the target compile references and serializes resolved CLR
identities into CIR. `ilemit` must encode those identities, not re-select a same-named host member. The compiler may
continue to execute on `net10.0`; that execution TFM does not select the framework written to a user assembly.

## #335 boundary

`ilemit` requires `--compile-refs` and constructs one long-lived `MetadataLoadContext` through
`ManagedReferenceCatalog`. The resulting `TargetReferenceUniverse`:

- validates every supplied managed reference and the target core assembly before emission;
- loads only the exact paths selected by the build—there is no directory scan, TPA, or host fallback;
- owns target type lookup and rejects missing or multiply-defined identities;
- exposes an ownership assertion for the atomic #336 migration;
- is passed explicitly to `Emitter`, while #335 deliberately leaves all existing emit paths on their old runtime/
  host types so the prerequisite is behavior-preserving.

The SDK, direct compiler driver, stdlib self-build, and special test drivers all pass a compile set explicitly.
An absent or empty set is a CLI error; host runtime discovery is not a compatibility fallback.

## Host-derived path inventory frozen by #335

The migration baseline contains 352 `typeof(...)` sites plus two `Type.GetType(...)` sites. Not every site writes a
signature directly, but comparisons, generic construction, override wiring, and member lookup must use the same type
universe as the signatures they support, so every category is in #336 scope.

| Category | Current owners |
|---|---|
| assembly root, bases, interfaces, constraints, declarations | `Emitter.Assembly.cs`, `Emitter.Bodies.cs` |
| primitive, array, byref, pointer, nullable, generic and function types | `Emitter.Types.cs`, `Emitter.Delegates.cs` |
| external constructors/methods/fields/properties/events and attributes | `Emitter.Resolve.cs`, `Emitter.Metadata.cs`, `Emitter.ClrInterop.cs` |
| emitted helper calls, operators, locals and conversions | `Emitter.Operators.cs`, `Emitter.Expressions.cs`, `Emitter.Statements.cs` |
| generated bridges, explicit slot wiring, and delegate signatures | `Emitter.ReverseBridge.cs`, `Emitter.Assembly.cs`, `Emitter.Resolve.cs`, `Emitter.Delegates.cs` |
| runtime/TPA lookup and host fallback | `RuntimeReferences.cs`, `Emitter.Resolve.cs`, `Emitter.Assembly.cs` |

The largest mechanical clusters at the baseline are `Emitter.Operators.cs` (89 sites), `Emitter.Bodies.cs` (60),
`Emitter.Expressions.cs` (51), `Emitter.ClrInterop.cs` (36), `Emitter.Types.cs` (30), and
`Emitter.Assembly.cs` (29). This is an inventory, not permission to migrate piecemeal: a partially mixed
MetadataLoadContext/host graph is invalid.

## Direct-emission gate

`tests/target-universe` emits one representative assembly and inspects the raw artifact. Its assertions cover:

- primitive parameter/return signature encoding;
- constructed generic (`List<String>`) signatures;
- delegate (`(String) -> String`) signatures;
- standard assembly and target-framework custom attributes;
- absence of assembly-local nullable metadata TypeDefs;
- external base/interface identities; and
- external types in public member signatures.

At the #335 baseline the raw TypeRefs were scoped through host `System.Private.CoreLib` and required a post-emit
repair. #336 emits every external type/member from the target context, and #337 removed that second authority. A
target generic definition combined with an emit-time type parameter is represented by the CLR's
`Type.MakeGenericSignatureType` adapter and mechanically re-anchored member wrappers; no overload selection occurs.
Raw output carries target scopes directly and is covered by SDK, reverse-interop, ILVerify, stdlib, coroutine, and
round-trip gates.

## CIR member identity

#370 retired #336's split owner/signature descriptors. Every external member now crosses the CIR boundary as one
scalar `memberRef` containing the target assembly, exact declaring type, name, generic arity, calling convention,
return and parameter signatures, and custom modifiers. `ilemit` maps that identity to exactly one target metadata
declaration and fails on zero or multiple matches; it does not choose by name, arity, assignability, inheritance
preference, or reflection order.

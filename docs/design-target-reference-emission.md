# Target-reference emission universe

DotKt has three distinct CLR assembly universes. Their owners and identities remain separate even
when two `System.Type` objects have the same `FullName`.

| Universe | Selected by | Purpose |
|---|---|---|
| compiler host | the runtime executing `ilemit` | runs Reflection.Emit and the tool itself; owns no output identity |
| target compile references | MSBuild's resolved compile set or the direct driver's targeting-pack set | authority for contract type/member availability and metadata identity |
| target runtime references | MSBuild's resolved runtime set, including copy-local assets | disambiguates contract/runtime twins in the compile set; owns RID asset selection |

`bir2cir` resolves Kotlin meaning against target compile references and serializes the resulting CLR
identities into CIR. `ilemit` encodes those identities. The compiler's own execution TFM does not
select the framework written to a user assembly.

## Target context

`ilemit` requires `--compile-refs` and constructs one long-lived `MetadataLoadContext` through
`ManagedReferenceCatalog`. Its `TargetReferenceUniverse` validates the supplied managed references
and target core assembly, loads the exact selected paths, and rejects missing or ambiguous identities.
There is no directory scan, TPA discovery, or host fallback for target references.

The SDK, direct driver, stdlib self-build, and special test drivers pass their compile set explicitly.
An absent or empty set is a CLI error. Target-context types are used throughout emitted declarations,
constraints, generic construction, member operands, attributes, helper calls, and override wiring.
A target generic definition combined with an emit-time type parameter uses the CLR's
`Type.MakeGenericSignatureType` adapter and mechanically re-anchored member wrappers.

Raw output carries target assembly scopes directly; it does not need post-emit scope repair.
Host objects may execute the compiler, but do not supply alternate target type or member identities.

## CIR member identity

Every external member crosses the CIR boundary as a scalar `memberRef` containing the target
assembly, exact declaring type, name, generic arity, calling convention, return and parameter
signatures, and custom modifiers. `ilemit` maps that identity to exactly one target metadata
declaration and fails on zero or multiple matches. It does not repeat overload resolution or select
by assignability, inheritance preference, or reflection order.

The emission-only exceptions and role-keyed `wellKnownRefs` contract are documented in
[architecture.md](architecture.md). Member identity and serialized signatures are defined in
[bir-cir-spec.md](bir-cir-spec.md).

## Verification

`tests/target-universe` inspects the raw emitted artifact, covering primitive and constructed generic
signatures, delegates, assembly/framework attributes, external bases/interfaces, public signatures,
and absence of assembly-local nullable metadata TypeDefs. SDK, reverse-interop, ILVerify, stdlib,
coroutine, and roundtrip gates also exercise direct target-scoped emission.

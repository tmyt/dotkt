# Compiler build modes

This document defines the modes that change compiler output. Layer ownership is
defined in [architecture.md](architecture.md).

## Reference projection

`dll2klib` has one semantic mode: it reads an MSBuild-resolved CLR reference
assembly and emits one metadata-only KLIB. The KLIB contains declarations and
Kotlin-facing metadata, never implementation bodies. Every projected reference
is loaded by `kotc` through the ordinary KLIB classpath.

## Frontend

`kotc` always projects Kotlin semantics to the same BIR vocabulary. It has three
frontend build paths:

| Invocation | Frontend inputs | Output |
| --- | --- | --- |
| application or library | source, stdlib frontend KLIB, reference KLIBs | BIR |
| stdlib implementation | common and CLR stdlib source | BIR |
| `DOTKT_BUILD_KLIB=1` | common and CLR stdlib source | stdlib frontend KLIB |

The last path is an artifact-production path, not a CLR representation mode.
`kotc` does not choose CLR owners, member names, call shapes, primitive
representations, or standard-library bindings.

## BIR to CIR

`bir2cir` owns the three physical build modes:

| Property | metadata (`--build-stdlib=metadata`) | runtime (`--build-stdlib=runtime`) | application/library (default) |
| --- | --- | --- | --- |
| Artifact | `DotKt.Private.Stdlib.dll` | `DotKt.Stdlib.dll` | user assembly |
| Bodies | replaced with metadata-only throw stubs | retained | retained |
| Kotlin-to-CLR substitution | disabled for the reference surface | enabled | enabled |
| Round-trip metadata | emitted | stripped | emitted |
| Inline BIR carriers | emitted where required | not required by consumers | emitted where required |
| Alias-only declarations | retained | omitted after substitution | omitted after substitution |

All three modes consume the same BIR shape. Mode-specific physical decisions
are made only in `bir2cir`; `ilemit` receives already-resolved CIR.

### Binding metadata

`@ClrTypeAlias`, `@ClrIntrinsic`, and related annotations are declaration facts
in the stdlib reference assembly. `bir2cir` consumes them while resolving
Kotlin identities to CLR types and members. They are not runtime dispatch
mechanisms and must not reach `ilemit` as unresolved binding instructions.

### Round-trip metadata

User libraries retain the metadata needed to recover their Kotlin declaration
surface when their CLR reference assembly is projected to a KLIB. This includes
Kotlin function modifiers, file-facade ownership, inline payloads, nullability,
and other declaration facts that cannot be derived from a lowered CLR signature
alone.

Reference assembly body stripping changes statements only. It must preserve
declaration signatures, generic constraints, nullability, and Kotlin metadata.

### By-reference parameters

Normal CLR `ref`/`out` interop is represented in the reference KLIB as
`ClrRef<T>` and called with `byref(value)`. `@ClrRefArguments(mask)` is a
separate stdlib-binding escape hatch used when an intrinsic Kotlin signature
cannot expose `ClrRef<T>` directly. `bir2cir` consumes both forms and selects
the CLR managed-pointer representation.

A user-defined non-suspend Kotlin function may likewise declare a `ClrRef<T>`
parameter. kotc carries it as the BIR managed-reference vocabulary and lowers
`.value` to explicit managed-reference load/store nodes; bir2cir resolves the
referent representation, and ilemit emits the resulting `T&` signature and
operations one-to-one. Managed-reference storage and capture remain invalid.

## CIL emission

`ilemit` has no semantic build mode. It emits the CIR it receives one-to-one,
including already-constructed attributes and resolved member references. It
does not redo overload resolution, infer standard-library ABI, or reconstruct
Kotlin semantics.

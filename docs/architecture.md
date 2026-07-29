# Compiler architecture

DotKt reuses the stock Kotlin frontend and replaces the backend with a CLR pipeline whose
frontend dependencies are ordinary KLIBs:

```text
CLR reference DLLs -> dll2klib -> reference KLIBs --+
                                                     v
Kotlin source + stdlib frontend KLIB ------------> kotc -> BIR -> bir2cir -> CIR -> ilemit -> CIL
                                                                      ^
                                                            CLR reference metadata
```

## Layer responsibilities

| Stage | Inputs | Owns | Must not own |
|---|---|---|---|
| `dll2klib` | One referenced CLR assembly plus the resolved reference catalog | ECMA-335 declaration metadata projected into Kotlin vocabulary as one metadata-only KLIB | CLR implementation bodies or downstream physical lowering |
| `kotc` | Kotlin source, stdlib frontend KLIB, reference KLIBs | PSI/FIR/IR processing and BIR serialization | CLR physical representation or BCL policy |
| `bir2cir` | BIR, stdlib reference assembly, referenced CLR assemblies | Kotlin-to-CLR type/call substitution, inline and suspend lowering, CIR production | Frontend symbol resolution |
| `ilemit` | CIR and runtime references | Mechanical CIL emission through `System.Reflection.Emit` | Kotlin-language or stdlib-binding policy |

`toolchain/retarget` is a post-processing utility that rewrites emitted BCL references so ordinary .NET compilers can consume DotKt assemblies.

## Standard-library artifacts

The CLR stdlib has three deliberately separate views:

| Artifact | Consumer | Purpose |
|---|---|---|
| `kotlin-stdlib-clr-frontend.klib` | `kotc` | Kotlin declarations and language metadata used for frontend resolution |
| `DotKt.Private.Stdlib.dll` | `bir2cir` | Reference-only metadata carrying `@ClrTypeAlias`, `@ClrIntrinsic`, and related bindings |
| `DotKt.Stdlib.dll` | Emitted applications | Shipping runtime implementations |

The split prevents frontend declarations, binding metadata, and executable implementations from being treated as one interchangeable artifact.

## Binding invariants

1. `kotlin.*` symbols come from the frontend KLIB; CLR declarations come from per-assembly reference KLIBs.
2. `kotc` emits Kotlin identities and frontend facts. It does not decide BCL owners, member names, or call shapes.
3. `bir2cir` is the binding layer. It reads reference-assembly metadata and rewrites Kotlin identities to CLR types and calls.
4. `@ClrIntrinsic` and related annotations are substitution metadata consumed by `bir2cir`; they are not runtime dispatch mechanisms.
5. `ilemit` consumes resolved CIR and must not recover Kotlin semantics or implement stdlib recognition.
6. BIR and CIR use the structured vocabulary defined by [bir-cir-spec.md](bir-cir-spec.md) and [bir-cir.schema.json](bir-cir.schema.json).

## Build modes

`bir2cir` distinguishes metadata, runtime, and application builds. The frontend emits one BIR representation; mode-specific physical representation is selected below the frontend boundary. See [design-compiler-modes.md](design-compiler-modes.md) for the artifact matrix.

## Sources of truth

- [dotkt-semantics.md](dotkt-semantics.md): user-visible Kotlin-to-CLR behavior and deliberate Kotlin/JVM differences.
- [bir-cir-spec.md](bir-cir-spec.md): serialized backend contract.
- [design-compiler-modes.md](design-compiler-modes.md): mode-specific lowering and attribute rules.
- [GitHub Issues](https://github.com/tmyt/dotkt/issues): the only task and bug backlog.
- The implementation and verification scripts: final authority for current mechanics when explanatory documents lag.

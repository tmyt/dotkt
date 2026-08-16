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
| `ilemit` | CIR, exact target compile references, and temporary runtime references | Mechanical CIL emission through `System.Reflection.Emit`; target metadata encoding | Kotlin-language or stdlib-binding policy, target inference from its execution runtime |

The compiler execution runtime is not the emitted target. `ilemit` receives the same exact compile-reference set
selected by MSBuild, keeps it in one target `MetadataLoadContext`, and treats it as the sole authority for contract
identity. Runtime references only disambiguate contract/runtime twins and supply deployment assets; they do not add
compile-time member availability.
See [Target-reference emission universe](design-target-reference-emission.md).

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
7. `kotc` serializes each Kotlin `*` as a BIR `star`; it never substitutes `Any`. `bir2cir` alone selects the CLR
   representation: trusted DotKt existential ABI, a known non-generic BCL surface, or the exact-token foreign-CLR
   reflection runtime. `ilemit` sees only the already-authored CIR calls and types.
8. Kotlin companion-extension association remains a BIR semantic fact. `bir2cir` alone partitions the declarations
   by receiver and authors the released C# 14 extension graph. For a generic receiver it also separates the
   source-named, receiver-parameterized C# wrapper from the receiverless Kotlin semantic core; `ilemit` emits both
   descriptions one-to-one and does not reconstruct that relationship.
9. A Kotlin call carries the exact declaration selected by FIR through BIR. `bir2cir` maps that identity to one
   physical CLR member before erased signatures can become ambiguous, and rewrites the declaration and every use from
   the same map. It may use erasure to allocate a stable physical name, but must never use the erased owner/name/signature
   to repeat Kotlin overload resolution. Duplicate CIR method signatures are malformed input; `ilemit` refuses them
   instead of inventing an order-dependent name.

10. Every external member `ilemit` encodes as an operand comes from the CIR node's resolved `memberRef`. It performs
    exact metadata lookup — declaring type in the named assembly, one member whose whole signature equals the one
    stated — and no selection: no name-and-arity candidate set, no most-derived rule, no assignability, no
    reflection-order first-wins. A node that names no member is an earlier-layer drop and fails loudly.

    Three things reflect on a member by name and are NOT that, because none of them can name a different member:

    - **Delegate mechanics.** ECMA-335 II.14.6 defines a delegate as a class with exactly one `Invoke` and exactly
      one `.ctor(object, native int)`. Fetching either on a delegate type has no candidate set to choose from; it is
      the same kind of act as knowing `newobj` needs a constructor token. This also covers the per-arity
      `Func`/`Action` adapters the emitter synthesizes for instantiations over a type still being built, which
      belong to no CIR node and so have no reference to carry.
    - **The local axis.** A member of the assembly under construction has no assembly identity to reference yet, so
      wiring an override, a MethodImpl or an accessor onto a type being built still resolves structurally. Closing
      that is [#395](https://github.com/tmyt/dotkt/issues/395), not this rule.
    - **Assembly boilerplate.** `typeof` (`GetTypeFromHandle`) and the attribute/metadata stamping the output format
      obliges, which describe the emitted assembly rather than anything a Kotlin program said.
    The test is whether an EXTERNAL member reaches a CIL operand, NOT whether the Kotlin source wrote the call.
    A Kotlin operation the backend expands into a BCL call — `enumValues()` into `Enum.GetValues`, string `+` into
    `String.Concat`, an emitted enumerator's slots
    into `IEnumerator`'s — encodes an external member however the shape got there, so those members arrive named
    like any other: bir2cir stamps them as a per-document `wellKnownRefs` table, keyed by role. They take no
    per-site decision, which is why one table says them all rather than a carrier per node. The EXPANSION stays in
    the emitter; that is a question about which layer owns the shape, and it is separable from whether the member
    is resolved.

    `tests/ir/check-emitter-residual.sh` holds the emitter to this list. It matches all three shapes a by-name
    lookup takes — the name written, computed, or used as a predicate over an enumerated candidate set — because
    a check that saw only the first reported green twice while the other two were live.

## Build modes

`bir2cir` distinguishes metadata, runtime, and application builds. The frontend emits one BIR representation; mode-specific physical representation is selected below the frontend boundary. See [design-compiler-modes.md](design-compiler-modes.md) for the artifact matrix.

## Sources of truth

- [dotkt-semantics.md](dotkt-semantics.md): user-visible Kotlin-to-CLR behavior and deliberate Kotlin/JVM differences.
- [bir-cir-spec.md](bir-cir-spec.md): serialized backend contract.
- [design-compiler-modes.md](design-compiler-modes.md): mode-specific lowering and attribute rules.
- [GitHub Issues](https://github.com/tmyt/dotkt/issues): the only task and bug backlog.
- The implementation and verification scripts: final authority for current mechanics when explanatory documents lag.

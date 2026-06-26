# FIR -> BIR -> CIR -> IL

This is the target split for the Kotlin/CLR backend.

## Layer Contract

- **FIR -> BIR** preserves Kotlin semantic structure and metadata. It should not decide CLR projection, inline bodies, suspend state machines, or physical CLR member references.
- **BIR -> CIR** is the first CLR-semantic lowering stage. It consumes BIR plus referenced assembly metadata and produces CLR-resolved CIR.
- **CIR -> IL** emits already-lowered CIR to IL with minimal policy decisions.

## CIR v1

The first implementation is a compatibility skeleton:

- `toolchain/bir2cir` accepts BIR JSON files and `--ref <dll>` inputs.
- It validates input JSON and writes `.cir.json` files that remain BIR-compatible.
- `ilemit` can read these files unchanged while lowerings are migrated into `bir2cir`.

The driver is structured as a real compiler stage even while the transform is still mostly identity:

1. `LoadBirFiles`
2. `BuildReferenceMetadataIndex`
3. `TransformFiles`
4. `WriteCirFiles`

`--compat-bir` is the default output mode. It emits BIR-compatible JSON and is the only mode that should be passed to `ilemit` today.

`--native-cir` is experimental. It emits a CIR envelope with `cirVersion`, referenced assembly identities, and the original BIR payload. This mode is for schema development only until `ilemit` grows a native CIR reader.

## Native CIR Direction

Native CIR should make CLR decisions explicit. The stable shape is still open, but v1 nodes should be named around CLR concepts rather than Kotlin frontend concepts:

- `clr.typeRef`: physical CLR type identity, including assembly identity where needed.
- `clr.methodRef`: physical CLR method identity, including owner, name, generic arity, lowered parameter types, and return type.
- `clr.fieldRef`: physical CLR field identity.
- `clr.local`: lowered local slot with a CLR type.
- `clr.call`: resolved static or instance method call.
- `clr.newobj`: resolved constructor call.
- `clr.ldfld` / `clr.stfld`: resolved field access.
- `clr.cast` / `clr.isinst`: CLR type tests and casts.

## Suspend Lowering Target

Suspend lowering should move into `bir2cir`, but the first CLR shape should be an async/await-level CIR representation rather than raw IL state-machine instructions.

That means BIR keeps Kotlin suspend semantics, then CIR introduces CLR async concepts such as:

- `clr.asyncFunction`: a lowered CLR async method with a `Task<T>` or `Task` return type.
- `clr.asyncLambda`: a lowered async delegate/closure body.
- `clr.await`: an await expression over `Task<T>`, `Task`, `ValueTask<T>`, or future supported awaitables.
- `clr.task<T>` / `clr.taskUnit`: normalized task result types.
- `clr.asyncLocal`: a local that must survive across await points.
- `clr.asyncTry`: try/catch/finally regions containing await points.

The initial public ABI remains compatible with the existing `Task<T>`-based behavior. The important responsibility shift is that `ilemit` should eventually emit a lowered CLR async/state-machine CIR form instead of discovering Kotlin suspend semantics itself.

## Projection Lookup Rule

`ClrAttribute` and projection metadata in the current BIR module are metadata output, not lookup input. `bir2cir` resolves physical CLR type/member references from referenced assemblies only. This keeps stdlib self-compilation Kotlin-shaped while consumers lower `actual class @Clr("System.X")` through referenced metadata.

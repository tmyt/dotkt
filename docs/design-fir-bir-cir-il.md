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

## Suspend Lowering Target

Suspend lowering should move into `bir2cir`, but the first CLR shape should be an async/await-level CIR representation rather than raw IL state-machine instructions.

That means BIR keeps Kotlin suspend semantics, then CIR introduces CLR async concepts such as:

- `clr.asyncFunction` / `clr.asyncLambda`
- `clr.await`
- `clr.task<T>` / `clr.taskUnit`
- async locals and captured live values
- try/catch/finally regions around await points

The initial public ABI remains compatible with the existing `Task<T>`-based behavior. The important responsibility shift is that `ilemit` should eventually emit a lowered CLR async/state-machine CIR form instead of discovering Kotlin suspend semantics itself.

## Projection Lookup Rule

`ClrAttribute` and projection metadata in the current BIR module are metadata output, not lookup input. `bir2cir` resolves physical CLR type/member references from referenced assemblies only. This keeps stdlib self-compilation Kotlin-shaped while consumers lower `actual class @Clr("System.X")` through referenced metadata.

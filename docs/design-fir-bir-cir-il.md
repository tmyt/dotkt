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

The first non-identity pass is `SuspendShapeAnalyzer`. It does not rewrite bodies yet; it identifies BIR suspend functions and records:

- result type
- `coSuspend` await count
- `coSuspendIntrinsic` await count
- `coReturn` count
- CPS field count

This analysis is emitted in `--native-cir` as `analysis.suspendFunctions` and printed as an aggregate in the driver log. It is the insertion point for the future suspend-to-async transform.

`--native-cir` also emits `cirDraft.asyncFunctions`. This is not yet executable CIR, but it maps current coroutine BIR steps into the intended async vocabulary:

- `coSuspend` -> `clr.await`
- `coSuspendIntrinsic` -> `clr.awaitIntrinsic`
- `coReturn` -> `return`
- `var` in coroutine steps -> `clr.asyncLocalInit`
- `exprStmt` / `setLocal` -> `clr.exprStmt` / `clr.setLocal`
- `coLabel` / `coGoto` / `coCondGoto` -> `clr.label` / `clr.goto` / `clr.brfalse`
- `coTryBegin` / `coCatchBegin` / `coTryEnd` -> `clr.asyncTryBegin` / `clr.asyncCatchBegin` / `clr.asyncTryEnd`

Each draft async function also carries `loweringStatus`:

- `linear`: only local initialization, awaits, and return.
- `control-flow`: labels or branches are present.
- `try`: async try/catch/finally markers are present.
- `unsupported`: a step kind is not yet represented in the draft; `unknownSteps` lists the kinds.

The draft lets the async shape evolve independently from `ilemit`; compatibility mode remains byte-for-byte BIR-compatible.

## Reference Metadata Index

`bir2cir` builds its projection input only from `--ref` assemblies. Current-module BIR attributes are deliberately not read as projection metadata.

The reference index currently records a small DotKt metadata surface:

- assembly-level `DotKtNamespaceProjectionAttribute`
- `[KotlinFileClass]` facade types
- public constructors, fields, and methods on referenced types
- `[KotlinFunction]` flags
- whether a method has `[KotlinInline]`
- diagnostics for references that cannot be fully inspected

This data is emitted in `--native-cir` under `references[].dotkt`. It is not yet used for rewriting, but it is the lookup source for later projection/type/inline lowering.

`resolutionDraft` uses this reference-only index to probe `kotlin-symbol` call sites. It reports `resolved-in-reference`, `ambiguous-in-references`, or `unresolved-in-references`. It intentionally does not consult definitions from the current BIR module.

`cirDraft.resolvedCalls` is the first lowering-facing view over that data. For uniquely resolved reference symbols it emits draft CLR operations:

- `new` -> `clr.newobj` with `clr.constructorRef`
- `callStatic` / `callInstance` -> `clr.call` with `clr.methodRef`
- field reads/writes -> `clr.ldfld` / `clr.ldsfld` / `clr.stfld` with `clr.fieldRef`

This is still native-CIR-only and does not rewrite compatibility output. Its purpose is to make physical member references explicit before `ilemit` learns to consume native CIR.

## Call Site Inventory

`--native-cir` emits `callSites` as an observation aid for the TypeLowering migration. It scans BIR expressions and classifies call/member/type sites as:

- `already-clr`: a physical CLR-ish node already emitted by FIR -> BIR, such as `clrStatic`, `clrNew`, or a `clr:` / `clrg:` owner.
- `kotlin-symbol`: a Kotlin symbol that still needs BIR -> CIR resolution, such as `callStatic`, `callInstance`, `new`, or `field`.

Each site carries a stable JSON path into the original BIR payload. The path is the rewrite anchor for later native CIR transforms and lets `cirDraft.resolvedCalls` point back to the exact expression that can become a CLR node.

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

The migration order for suspend is:

1. Detect and index current BIR coroutine shapes (`suspend`, `steps`, `coSuspend`, `coSuspendIntrinsic`, `coReturn`, `cpsFields`).
2. Emit native CIR analysis alongside the original BIR payload.
3. Emit a native CIR draft for simple linear suspend functions: `steps` -> `clr.asyncFunction` + `clr.await` + `return`.
4. Extend to executable branches, loops, and try/finally around await.
5. Teach `ilemit` to consume native async/state-machine CIR, then remove its Kotlin coroutine discovery.

## Projection Lookup Rule

`ClrAttribute` and projection metadata in the current BIR module are metadata output, not lookup input. `bir2cir` resolves physical CLR type/member references from referenced assemblies only. This keeps stdlib self-compilation Kotlin-shaped while consumers lower `actual class @Clr("System.X")` through referenced metadata.

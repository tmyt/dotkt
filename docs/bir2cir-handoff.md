# bir2cir Handoff

Last updated: 2026-06-27

This note is for agents resuming the FIR -> BIR -> CIR -> IL split work.

## Current State

The active `bir2cir` direction is documented in [design-fir-bir-cir-il.md](design-fir-bir-cir-il.md). The current implementation keeps `--compat-bir` as the production path and uses `--native-cir` for schema/lowering development only.

Recent committed milestones on `main`:

- `Draft resolved CLR call CIR`: reference-only constructor/method/field metadata and `cirDraft.resolvedCalls`.
- `Track BIR paths for resolved CIR calls`: JSON paths on call sites and resolved call draft nodes.
- `Draft native lowered BIR expressions`: native-only cloned BIR tree with uniquely resolved call sites lowered to draft CLR nodes.
- `Filter resolved CLR members by arity`: constructor/method candidates filtered by argument count.
- `Filter resolved CLR members by type hints`: candidates filtered with BIR `sig` and expression type hints where known. The current type normalizer covers primitive aliases, arrays, delegates, nullable/byref/generic parameters, and constructed `clrg:` types.
- `Draft reference type resolution in bir2cir`: `typeSites`, `typeResolutionDraft`, and `cirDraft.resolvedTypes`.
- `Lower resolved type sites in native draft`: resolved BIR type strings are replaced with `clr.typeRef` objects in `cirDraft.loweredBir`.

Important invariant: `--compat-bir` must remain byte-for-byte BIR-compatible because `ilemit` still consumes that mode.

## What Native CIR Emits Now

For `--native-cir`, the envelope currently contains:

- `references[].dotkt`: reference-only metadata from `--ref` assemblies.
- `analysis.suspendFunctions`: suspend/coroutine shape inventory.
- `callSites`: BIR expression call/member sites with JSON paths.
- `typeSites`: BIR type-string sites with JSON paths.
- `resolutionDraft`: reference-only call/member resolution results.
- `typeResolutionDraft`: reference-only type resolution results.
- `cirDraft.asyncFunctions`: draft async view of suspend functions.
- `cirDraft.resolvedCalls`: unique call/member resolutions as `clr.call`, `clr.newobj`, `clr.ldfld`, `clr.ldsfld`, or `clr.stfld`.
- `cirDraft.resolvedTypes`: unique type resolutions as `clr.typeRef`.
- `cirDraft.loweredBir`: cloned BIR payload with uniquely resolved call/type sites replaced by draft CLR nodes.

The native draft is not executable CIR yet. It exists to make the next `ilemit` reader work mechanical and measurable.

## Reproduction Commands

Build `bir2cir`:

```bash
dotnet build toolchain/bir2cir -c Release -o build/bir2cir-bin -v q --nologo
```

Run the focused native-CIR guard:

```bash
scripts/verify-bir2cir-native.sh
```

Generate native CIR for the interop sample:

```bash
dotnet build/bir2cir-bin/bir2cir.dll /tmp/cir-native-handoff \
  --native-cir \
  --ref cases/ktproj-il/bin/Debug/net10.0/hello-il.dll \
  cases/ktproj-il/obj/dotkt-bir/App.bir.json
```

Check that compatibility output is unchanged:

```bash
dotnet build/bir2cir-bin/bir2cir.dll /tmp/cir-compat-handoff build/bir-kctx/app.bir.json
cmp -s build/bir-kctx/app.bir.json /tmp/cir-compat-handoff/app.cir.json
```

Smoke-test the current production path:

```bash
scripts/dotkt.sh --no-stdlib --run -d /tmp/dotkt-bir2cir-check cases/m0/M0.kt
dotnet build cases/ktproj-il/hello-il.ktproj -v minimal --nologo
```

The current stdlib emit still stops in `ilemit` at:

```text
System.NotSupportedException: field kotlin.random.Random.INSTANCE not found
```

That is a known downstream stdlib/ilemit issue, not a `bir2cir --compat-bir` regression by itself.

## Worktree Warning

At the time this handoff was written, the repo had unrelated uncommitted work outside `bir2cir`:

- `docs/design-stdlib-compilation.md`
- `toolchain/facadegen/Program.cs`
- `toolchain/ilemit/Emitter.Coroutines.cs`
- `toolchain/ilemit/Program.cs`
- `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitter.kt`
- `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitterExpressions.kt`
- `toolchain/kotc/src/main/kotlin/kotc/backend/ClrBackendPhase.kt`
- `toolchain/kotc/src/main/kotlin/kotc/frontend/ClrTypeInjection.kt`
- `runtime/stdlib/`

Do not revert or clean these unless the user explicitly asks.

## Suggested Next Steps

1. Decide whether `cirDraft.loweredBir` should keep BIR wrapper structure or become a separate executable CIR schema. Keeping both temporarily is acceptable.
2. Extend overload resolution for generic owners, generic methods, variance/conversion rules, and richer nullable/reference-type semantics.
3. Teach `ilemit` a native-CIR reader behind an explicit mode. Do not route normal `dotkt` builds to native CIR until compatibility checks exist.
4. Move one narrow lowering from `kotc`/`ilemit` into `bir2cir`, then verify `--compat-bir` remains unchanged until the consumer can read native CIR.

## Useful Code Pointers

- `toolchain/bir2cir/Program.cs`: all current stage logic.
- `ReferenceMetadataIndex`: reads referenced assemblies and resolves types/members.
- `CallSiteAnalyzer` and `TypeSiteAnalyzer`: inventories BIR sites and assigns JSON paths.
- `ResolvedCallCirDraft` and `ResolvedTypeCirDraft`: native draft summaries.
- `NativeExpressionCirDraft`: clones BIR and rewrites uniquely resolved sites into draft CLR nodes.
- `docs/design-fir-bir-cir-il.md`: higher-level architecture and invariants.

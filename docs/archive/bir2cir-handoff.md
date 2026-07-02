> **HISTORICAL — superseded by `docs/master-task-inventory.md` 【1】.** An operational handoff for the abandoned
> native-CIR migration path (both modes removed 2026-06-30). Kept for context only.

# bir2cir Handoff

> **状態 (2026-06-30 見直し)**: この文書は大半が陳腐化している（MOSTLY-STALE）。現行アーキテクチャの正は [docs/ship-tasks.md](../ship-tasks.md) §0。

Last updated: 2026-06-27

This note is for agents resuming the FIR -> BIR -> CIR -> IL split work.

## Current State

The active `bir2cir` direction is documented in [design-fir-bir-cir-il.md](../design-fir-bir-cir-il.md). `--native-cir` is the TARGET default mode; `--compat-bir` is being removed (nothing has shipped, so the byte-for-byte invariant is abandoned — [[break-for-elegance]]).

Recent committed milestones on `main`:

- `Draft resolved CLR call CIR`: reference-only constructor/method/field metadata and `cirDraft.resolvedCalls`.
- `Track BIR paths for resolved CIR calls`: JSON paths on call sites and resolved call draft nodes.
- `Draft native lowered BIR expressions`: native-only cloned BIR tree with uniquely resolved call sites lowered to draft CLR nodes.
- `Filter resolved CLR members by arity`: constructor/method candidates filtered by argument count.
- `Filter resolved CLR members by type hints`: candidates filtered with BIR `sig` and expression type hints where known. The current type normalizer covers primitive aliases, arrays, delegates, nullable/byref/generic parameters, and constructed `clrg:` types.
- `Draft reference type resolution in bir2cir`: `typeSites`, `typeResolutionDraft`, and `cirDraft.resolvedTypes`.
- `Lower resolved type sites in native draft`: resolved BIR type strings are replaced with `clr.typeRef` objects in `cirDraft.loweredBir`.
- `Executable native CIR`: `cirDraft.executableCir` rewrites uniquely resolved reference constructors/methods/fields to native `clr.newobj`/`clr.call`/`clr.ldfld`/`clr.stfld` nodes with `memberRef` metadata, and `ilemit` emits those nodes directly. Generic method `typeArgs` are preserved on `clr.call` and emitted via `MakeGenericMethod`; constructed generic owners are preserved as node `ownerType`; physical `clrPropGet`/`clrPropSet` and `clrEventAdd`/`clrEventRemove` nodes are lowered to native calls/field ops when reference metadata is available. Physical type/nullable/reflection/enum operations are now lowered to `clr.conv`, `clr.isinst`, `clr.castclass`, `clr.isinst.ref`, `clr.safeCast.value`, `clr.nullable.*`, `clr.typeof`, `clr.getType`, and `clr.enum.*`. Object-identity helpers (`objEq` / `objMethod`) are lowered to `clr.obj.eq` / `clr.obj.method`.

Note: the old `--compat-bir` byte-for-byte BIR-compatibility invariant is DROPPED ([[break-for-elegance]]) — nothing has shipped, so `--compat-bir` is being deleted rather than kept binary-stable.

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
- `cirDraft.executableCir`: executable native-CIR payload with BIR wrapper shape and native CLR expression nodes carrying `memberRef`.
- `cirDraft.ilemitCompatBir`: legacy transition fallback with BIR wrapper shape and old `ilemit` `clr*` nodes.

The native draft is not the final executable CIR schema yet, but the envelope is now executable for the covered reference constructor/method/field/property/event/type-operation subset because `ilemit` reads `cirDraft.executableCir`.

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

Smoke-test the legacy `--compat-bir` path (default today, being removed):

```bash
scripts/dotkt.sh --no-stdlib --run -d /tmp/dotkt-bir2cir-check cases/m0/M0.kt
dotnet build cases/ktproj-il/hello-il.ktproj -v minimal --nologo
```

Check wide Kotlin function delegates in `ilemit`:

```bash
scripts/verify-ilemit-wide-delegates.sh
```

Check the native-CIR envelope can be consumed by `ilemit`:

```bash
scripts/verify-native-cir-ilemit.sh
```

Smoke-test the developer native-CIR pipeline switch:

```bash
scripts/dotkt.sh --native-cir --no-stdlib --run -d /tmp/dotkt-native-cir-handoff cases/m0/M0.kt
```

`ilemit` maps ordinary `func:` types to `System.Func` / `System.Action` while they fit. Wider function types synthesize public module-local delegates under `DotKt.Runtime.CompilerServices`:

- ``KFunc`N`` for non-`Unit` returns, with the last type parameter as `TResult`.
- ``KAction`N`` for `Unit` returns.

They are stamped `[CompilerGenerated]` plus DotKt metadata and are read back by `facadegen` / `bir2cir` as ordinary `func:` types.

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

The full remaining-work map is in **[bir2cir-migration-inventory.md](bir2cir-migration-inventory.md)**: all 102 BIR node kinds classified, with only ~34 being genuine near-term `bir2cir` `clr.*` targets (26 BasicLowering + 8 ClrProjection); the rest retire to stdlib (25), are control-flow pass-through (21), or belong to the deferred suspend (17) / inline (3) phases. The plan there is breaking-changes-aware ([[break-for-elegance]]): nothing has shipped, so the `--compat-bir` byte-for-byte invariant is being abandoned.

1. **Wave 1 — DONE (2026-06-27).** All 12 self-contained physical primitives lower to `clr.*` in `ExecutableCirDraft` (`TryLowerPhysicalArrayOp`/`ArithOp`/`BasicOp`/`StackOp`), each with a `verify-native-cir-ilemit.sh` fixture: arrays (`clr.ldelem`/`clr.stelem`/`clr.ldlen`/`clr.newarr`), `bin`/`un` (`clr.bin`/`clr.un`), `const`/`default` (`clr.const`/`clr.default`), `nullableOf` (`clr.nullable.wrap`), `concat` (`clr.str.concat`), `stack*` (`clr.stackalloc`/`clr.stack.get`/`clr.stack.set`). Legacy ilemit cases are kept (still used by `--compat-bir`); they get deleted at Milestone 0.
2. **Wave 2 — DONE (2026-06-27).** `spreadConcat` → `clr.array.spread`, `stackAsSpan` → `clr.stack.asSpan`, `constrainedCall` → `clr.constrained.compareTo` (`newArray` was Wave 1). `verify-native-cir-ilemit.sh` now has 18 PASS. The earlier DotKt.Stdlib emit crash is resolved and the stdlib rebuilds (use the non-destructive `scripts/build-clr-stdlib.sh --emit`), so **Milestone 0**'s remaining work is purely the flip to `--native-cir` as default + deletion of the `--compat-bir` path; it is also the gateway to the stdlib-retire core.
3. **Waves 3-6 are gated on shared infrastructure** (see inventory): a per-method scope/type environment (`this`/`local`), a same-module member resolver (`setField`/`lateinit`/byref family), then the reference-metadata overload resolver (physical `clr*` calls), and finally delegate/closure construction.
4. **Parallel non-wave workstreams:** retire the 25 stdlib intrinsics out of the compiler (several ilemit cases are already dead code — delete, don't migrate), and the deferred suspend/inline phases.

## Useful Code Pointers

- `toolchain/bir2cir/Program.cs`: all current stage logic.
- `ReferenceMetadataIndex`: reads referenced assemblies and resolves types/members.
- `CallSiteAnalyzer` and `TypeSiteAnalyzer`: inventories BIR sites and assigns JSON paths.
- `ResolvedCallCirDraft` and `ResolvedTypeCirDraft`: native draft summaries.
- `NativeExpressionCirDraft`: clones BIR and rewrites uniquely resolved sites into draft CLR nodes.
- `ExecutableCirDraft`: executable transition bridge from resolved reference symbols to native CIR `clr.*` nodes.
- `IlemitCompatCirDraft`: legacy fallback bridge from resolved reference symbols to existing `ilemit` `clr*` nodes.
- `docs/design-fir-bir-cir-il.md`: higher-level architecture and invariants.

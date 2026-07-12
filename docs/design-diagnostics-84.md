# #84 — Diagnostics quality: source-position errors + IR sanity gate (PLAN)

> Status (2026-07-12): **PLAN ONLY** (user-directed: "#84 は plan だけ"). Implementation deferred. This doc is
> the phased design to implement later.

## Current-state findings

- **kotc already has the gold-standard pattern, but only inside kotc.** `BirEmitter.unsupported()`
  (`BirEmitter.kt:123-128`) reports an un-lowerable construct as a source-located compile error via
  `locationOf()` (`:105-111`, `IrElement.startOffset` + `IrFileEntry` → `file:line:col` through
  `MessageCollector`), sets `hadError` (`:102`), fails the build in `ClrBackendPhase.kt:75`. But it only fires
  for constructs kotc KNOWS it can't handle — the failures #84 targets are the ones kotc emits happily that
  blow up two stages later.
- **BIR carries NO position metadata.** The frozen schema node def requires only `k`
  (`docs/bir-cir.schema.json:220-224`); `funDecl`/`propDecl`/`fieldDecl` + root all set
  `additionalProperties: true` (`:194,:206,:216,:19`) — so **a `pos` field can be added without breaking the
  #37 freeze.**
- **BIR nodes are raw JSON string-interpolation, not a serializer** (hundreds of triple-quoted templates). So
  inline per-node positions are a massive edit; **decl-level positions touch only a handful of central
  templates** (concrete-method near `BirEmitterDeclarations.kt:106`, top-level field `BirEmitter.kt:457`, file
  root `:506`).
- **bir2cir** (`Program.cs:14-33`): `catch (Exception)` → `bir2cir: {ex.Message}` (message only, no decl/source).
- **ilemit** (`Program.cs:16-40`): **no try/catch at all** — every failure is an unhandled .NET stack trace. The
  ~31 throw sites include the "method X.Y not found" class (`Emitter.Resolve.cs:26/68/73/301/430/647/836/948`,
  `Emitter.ClrInterop.cs:153/172/395/549/592`, …). No current-decl/node tracking for diagnostics today.
- **The existing schema validator is purely structural** (`scripts/verify-schema.py`, `make verify-schema`):
  types-are-nodes, canonical kinds, `mods`/`vis` enums. It does NOT check semantic invariants (resolvable
  owners, arity, dangling refs) and runs OFFLINE over files, not in the pipeline.

## The design, phased by value/risk

### Phase 1 — ilemit failure-context wrapping (highest value, lowest risk, NO format change)
Give ilemit a diagnostic boundary that names WHICH declaration was being emitted at a throw. "while emitting
method `Foo.bar`: method `E.ToString` not found" beats a raw stack trace even without positions.
- `_ctx` breadcrumb (file-class + type + method, optional node `k`) on `Emitter`, set at
  `EmitMethodBody`/`EmitCtorBody` (`Emitter.Bodies.cs:89,:12`), optionally refined in `EmitStmt`/`EmitExpr`.
- Wrap each method/ctor body emit in try/catch → rethrow a `CirEmitException` carrying `_ctx` + inner message;
  the outer `EmitAssembly` loop (`Emitter.Assembly.cs:12`) is the seam.
- Top-level try/catch in `IlEmit.Main` (`Program.cs:16`) prints `ilemit: <file-class>.<method>: <message>`,
  returns 1; keep the raw stack behind `ILEMIT_TRACE` (`Program.cs:113`).
- Symmetric enrichment in bir2cir's existing catch (`Program.cs:28`) via a current-decl breadcrumb in `Pipeline`.
- Owner: bir2cir + ilemit. ~1–1.5 days. Low risk (additive, no contract change, no kotc rebuild).

### Phase 2 — decl-level position threading (kotc → bir2cir → ilemit)
Optional `pos` at declaration granularity (method/property/field/type) — the cheap 80% (ilemit failures are
almost always inside a method body).
- Format: structured `pos` `{ "f": path, "l": line, "c": col }` (numbers, not a `"file:line:col"` string — so
  the validator's bare-string check isn't tripped; but `f` is a string → **add `pos.f` to
  `verify-schema.py:29` `STR_OK`** and document in `docs/bir-cir-spec.md` — the one required frozen-tooling edit).
- kotc: reuse `locationOf()` at each decl template (`BirEmitterDeclarations.kt:106` + siblings).
- bir2cir: `pos` must survive lowering — JsonNode-DOM passes preserve unknown keys; **audit decl-REBUILDING
  passes** (`ShapeSynthesis.cs`, `SharedSyntheticSynthesis.cs`, `DeclarationRename.cs`) to carry it forward.
  Synthetics with no source omit `pos` (optional).
- ilemit: read `pos` off the current decl into `_ctx` → `ilemit: File.kt:42: in method Foo.bar: method
  E.ToString not found`.
- Owner: kotc (emit) + bir2cir (preserve) + ilemit (consume). ~2–3 days (the preservation audit dominates).
  Medium risk (must not regress #37; mitigated by `additionalProperties:true` headroom + the single `STR_OK` edit).

### Phase 3 — node-level `pos` on resolution-bearing kinds (OPTIONAL, DEFER)
If decl granularity proves too coarse, stamp `pos` on the narrow set that resolves against external metadata and
throws "not found": `callInstance`/`callStatic`/`new`/`field`/`setField`/`staticField` + bir2cir `clr*` kinds. A
node-id-keyed position map is NOT worth it (node ids don't exist and would be invasive). Defer until Phases 1–2
show a concrete gap. ~1–2 days, low-medium risk.

### Phase 4 — IR sanity gate (semantic invariants, distinct from the schema)
Schema validates SHAPE; sanity validates MEANING, in-process before emit — so malformed IR fails loud with source
context instead of a Reflection.Emit crash / silent BadImageFormat.
Invariants (none checked today): resolved `local`/`setLocal` in scope; `args.length==argTypes.length`; `cond` has
`cond`+`then`; every `goto`/`brIf` label has a matching `label` (mirror `PrescanCfgLabels`,
`Emitter.Bodies.cs:129`); `callStatic`/`field`/`staticField` carry non-null `ownerType`; a `this`-referencing
`funDecl` is non-`static`.
Placement (two surfaces): (1) an in-pipeline C# validator in `bir-common` (`IrSanity.cs` sibling to
`TypeNode.cs`), called by both `Bir2Cir.Pipeline.Run` (input BIR) and `IlEmit.Main` (CIR, before EmitAssembly),
throwing a `CirSanityException` carrying the decl's Phase-2 `pos` → the Phase-1 clean-diagnostic path; (2) an
offline `verify-sanity.py` sibling wired into a new `make verify-sanity` after `verify-schema`
(`Makefile:98,:103`) as the corpus regression net. The in-pipeline half is higher-value; keep both in sync via
the spec's invariant list. Owner: bir-common (C#) + scripts (Python). ~3–4 days. Medium risk (must not
false-positive on legitimate synthetic IR — calibrate on the 250-file stdlib corpus before hard-enabling).

## Ordering
1. Phase 1 (format-decoupled, ships value day one) → 2. Phase 2 (adds the source line; needs P1's `_ctx` seam) →
3. Phase 4 (parallelizable with P2; the offline gate needs no format change) → Phase 3 deferred.

## Verification story (prove a broken input now yields a good diagnostic)
Add a **negative-fixture harness** (none today): a `cases/neg-*/` convention with `expected-error.txt` + a driver
(extend `scripts/verify-il.sh:155` where it already recognizes an ilemit error) asserting the DIAGNOSTIC TEXT
(file:line + decl + message), not just the exit code. Fixtures: (a) a `.kt` provoking an ilemit "method not
found" → assert `File.kt:<line>: … method X not found`; (b) hand-authored malformed BIR/CIR (dangling `goto`;
arity mismatch; undeclared `local`) fed straight to the bir-common sanity validator → assert the precise
invariant message. Wire `make verify-sanity` into the `verify` aggregate.

## Critical files
- `toolchain/ilemit/Program.cs` (P1 top-level catch; P4 CIR-boundary sanity call)
- `toolchain/ilemit/Emitter.Bodies.cs` (P1 `_ctx`; P2 `pos` read)
- `toolchain/kotc/src/main/kotlin/kotc/backend/BirEmitterDeclarations.kt` (P2 decl `pos`; `locationOf` reuse)
- `toolchain/bir2cir/Program.cs` (P1 breadcrumb; P4 BIR-boundary sanity call)
- `scripts/verify-schema.py` (P2 `STR_OK` for `pos`; P4 semantic gate)
- Secondary: `toolchain/bir-common/TypeNode.cs` (home for `IrSanity.cs`), `BirEmitter.kt:105-128` (the
  `locationOf`/`unsupported` pattern), `docs/bir-cir.schema.json:194,206,216` (`pos` headroom),
  `scripts/dotkt.sh` + `scripts/verify-il.sh:155` (negative-fixture wiring).

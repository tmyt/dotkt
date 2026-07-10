# #71 + #75 Unified — dumb ilemit: raw-BIR [KotlinInline] + bir2cir-owned roundtrip metadata (Fable design 2026-07-11)

User diagnosis (2026-07-11): #75 (inline unification) and #71 (move roundtrip-metadata generation out
of ilemit) are ONE cleanup. ilemit stamps carriers + emits IL; it does NOT splice inline bodies and
does NOT generate Kotlin metadata. Everything Kotlin-semantic is bir2cir's. Supersedes the
splice-placement half of `design-inline-unification-75.md` (its 3-mechanism analysis + klib-carries-no-
bodies crux stay valid; its line numbers are stale post-#41). Re-anchor by NAME.

## 1. Current-state map (verified 2026-07-11, post-#41)
- **[KotlinInline] generated+stamped BOTH in ilemit (the #71 violation):** Emitter.Assembly.cs pass-4
  block `if (inl) ApplyKotlinInline(mb, {params,body})` builds the payload FROM THE CIR method def
  (post-lowering, post-squash) — not raw Kotlin BIR. Emitter.Metadata.cs `ApplyKotlinInline` encodes
  via `BirCarrier.EncodeBody`. Emitter.CompilerServices.cs `EnsureKotlinAttrs` SYNTHESIZES the 7
  embedded attr classes + Nullable{,Context}Attribute.
- **RefBodySquash (bir2cir/RefBodySquash.cs, Program.cs:557) is the LAST bir2cir step** — replaces
  every `body` with `throw NotImplementedException`. ilemit then stamps [KotlinInline] from that
  squashed body → all 1819 ref payloads are throw-stubs. The "CIR-in-payload" bug: the payload must be
  RAW BIR captured BEFORE lowering, stored SEPARATELY from the executable body (the throw sentinel stays).
- **ilemit splice (quarantined by #41): Emitter.InlineSplice.cs** (EmitSplicedStmts/EmitInlineSplice) +
  4 touchpoints (Expressions.cs:81 `_inlineSubst`; :684-690 `_inlineLambdas`; :716 `case inlineSplice`;
  Bodies.cs:496 `_inlineThis`). Producer: kotc BirEmitterCalls.kt:1163 → `inlineSpliceCall`
  (BirEmitterInline.kt:401). Exercised only by verify-roundtrip ~281-309. WRONG layer: a spliced body
  can't re-lower in the call-site context (reified/generics/@ClrIntrinsic/suspend) because ilemit runs
  after all lowering. `DecodeCarrier` (Emitter.CompilerServices.cs:142) stays until nothing in ilemit
  reads carriers.
- **ilemit GENERATES (decides semantics):** the attr-class DEFS; [KotlinFunction(flags)] from mods;
  [KotlinFileClass]/[KotlinFunInterface]/[KotlinSealed]/[KotlinReadOnly]; [NullableContext]; the
  [KotlinInline] payload; the `_stripMetadata` decision. **ilemit merely STAMPS (bir2cir computed):**
  nullableFlags NRT, suspendFnType H2, user annotations via `BuildCab` (the generic CIR-attr path).
- **bir2cir InlineSplice.cs** (#75 slice-1): runs Program.cs:231, BEFORE ClosureSynthesis(232)/
  MemberCallSubstitution(408)/SuspendColdLowering(~470)/BirTypeLowering(537)/RefBodySquash(557).
  Consumes kotc `callInline` (today only kotlin.repeat). THIS is the seam — exactly the right pipeline
  position for spliced bodies to re-lower in the app context.
- **kotc 3 mechanisms:** (1) same-module `inlineCall` (BirEmitterInline.kt:256 + spliceLambdaCall:424 +
  spliceBodyWithReturns:486; embeds 5 fixes: crossinline/noinline fallback, symbol-keyed typeArgSubst
  w/ self-star, dispatch-recv binding, nestedCapturesValue, suspendCoroutineUninterceptedOrReturn→
  suspendIntrinsic); (2) cross-module `inlineSpliceCall` (:401); (3) hardcodes SCOPE_FUNCTIONS
  (BirEmitter.kt:364)/inlineScope(:94)/inlineUse(:176); repeat already migrated to callInline.

## 2. Target contract — the raw-BIR [KotlinInline] carrier
**Producer (bir2cir, ref + user-library builds):** new pass `InlineBirStash`, FIRST in the pipeline
(before any lowering). For every `mods.inline` method, deep-clone the RAW facts {fqn,typeParams,params,
recv shape,ret,body} and store as ONE OPAQUE STRING: `"inlineBir": base64(BirCarrier.EncodeBody(JsonV1,
raw))`. Encoding AT STASH TIME is load-bearing — every downstream walker (BirTypeLowering, RefBodySquash)
sees a JsonValue string and cannot descend/rewrite. Also feeds an in-memory index `fqn+pc+ga → raw BIR`
for SAME-module splices. RefBodySquash UNTOUCHED (squashes `body` = throw sentinel stays; `inlineBir`
rides through). ilemit stamps the `inlineBir` string verbatim (base64-decode → (version,byte[]) ctor
args); never reads params/body. rt build: don't attach the attr.
**Consumer (bir2cir InlineSplice, extended):** kotc emits ONE `callInline` for EVERY inline+lambda call
(same+cross module): {k:callInline, callee:FQN, owner, pc, ga, typeArgs:[resolved TypeNodes], recvs,
args:[expr|lambda{params,body}], retType}. Lambda bodies emitted by kotc IN THE CALLER'S SCOPE (only
kotc holds the IR; caller-scope emission is what makes a bare `return` non-local), labeled return@callee
already routed to a trailing end-label + result-local (the spliceBodyWithReturns shape, kept in kotc as
the CARRIER producer, no longer the splicer). InlineSplice resolves callee body: same-module from the
stash index; cross-module by reading [KotlinInline] off the --ref'd assembly via ReferenceMetadataIndex's
MetadataLoadContext (GetCustomAttributesData, same pattern as KotlinFunctionFlags), decoding via a
bir2cir-side BirCarrier.DecodeBody. Overload key: owner+name+pc+ga. Splice AT BIR LEVEL, fixpoint
(depth-guard): POSITIONAL type-param subst (payload typeParams[i]→call typeArgs[i], immune to name
capture), bind receivers+value params to fresh temps, splice lambda-param invocations with the carried
caller-scope bodies, route callee-returns to result-local+end-label, wrap in valueBlock (value-producing,
unlike ilemit's void-only splice). Hygiene: freshen every {k:label,id} above the file's max id + prefix-
rename payload locals per splice. Because InlineSplice runs before all lowering, the spliced RAW body
lowers IN THE APP CONTEXT (@ClrIntrinsic binds against the app's ref.dll, generics resolve with call-site
type args, reified is free on CLR, suspend reaches SuspendColdLowering). Fallback (non-public origin
symbols / lifted origin closures): leave the plain call (callee is a real CLR method; only NLR/suspend-in-
lambda strictly require the splice), log loudly.

## 3. The #71 metadata move — bir2cir generates, ilemit stamps
**Moves to bir2cir** (new pass `RoundtripMetadata`, after DeclNullableFlags / before BirTypeLowering;
skipped in rt = deletes ilemit's _stripMetadata): the attr-class DEFS become ordinary CIR type decls
(internal sealed : System.Attribute, ctor chaining); the STAMPS become ordinary CIR `attrs` entries
{attr:fqn,args:[…]} through ilemit's generic BuildCab path — [KotlinFunction(flags)] (bir2cir computes
flags), [KotlinFileClass]/[KotlinFunInterface]/[KotlinSealed]/[KotlinReadOnly], [NullableContext],
[Nullable] + [KotlinSuspendFunctionType] as PARAM-level attrs (CIR gains a `retAttrs` slot for
return-position attrs), [KotlinInline(version,bytes)] from §2. CIR attr-arg encoding gains a `bytes` kind
(base64 + kind tag) decoded generically by ilemit ConstArgValue — a codec extension, NOT Kotlin knowledge.
**ilemit keeps (dumb):** BuildCab/TryCab/ConstArgValue, field/param/ret-attr stamping, DefineParamNames,
modreq(IsVolatile), StampCompilerGenerated (#68, marks ilemit's OWN synthesized members). **Deleted from
ilemit:** EnsureKotlinAttrs, all ApplyKotlin*/ApplyNullable*/ApplySuspendFnType, DecodeCarrier/
ReadByteArrayArg/ReadNullableFlags, _stripMetadata, the Kotlin-metadata block (Emitter.Assembly.cs
~489-556). BirCarrier stays in bir-common, bir2cir-only.

## 4. Staging — gate-safe slices (each independently GATEFAST_GREEN)
- **S1 (keystone, joint #71/#75): raw-BIR carrier + splice re-home.** bir2cir InlineBirStash (opaque
  inlineBir + same-module index); ilemit ApplyKotlinInline reads inlineBir verbatim; kotc inlineSpliceCall
  → generic callInline (extension receivers now carried); bir2cir InlineSplice gains the generic
  resolver+splicer (§2); DELETE Emitter.InlineSplice.cs + the 4 touchpoints + the inlineSplice CIR node.
  Producer+consumer MUST flip in one slice (payload shape lowered-CIR→raw BIR breaks the old consumer).
  Gate focus: verify-roundtrip ~281-309 (forEach3 NLR, reified typeName) + full gates. ref.dll payloads
  become real raw BIR (grows; compile-time-only, rt strips — acceptable).
- **S2 (#71): metadata generation moves to bir2cir** (§3). Behavior-neutral (same attrs/names, facadegen
  read contract unchanged); verify via facadegen roundtrip + a metadata-dump diff of ref/app dll before/
  after. Deletes ilemit's generation surface + _stripMetadata.
- **S3 (#75 step-2): retire kotc mechanism-3** — DELETE SCOPE_FUNCTIONS/inlineScope/inlineUse + dispatches;
  kotc emits callInline for every inline+lambda call whose body is absent (cross-module); scope-fns/
  @InlineOnly/use{} resolve from the ref.dll's now-real payloads + splice via S1's engine. use{} gains
  real closeFinally fidelity. Gates: il-scope/il-use + roundtrip suspend-in-with (roundtrip-memext2
  expected to FIX here).
- **S4 (#75 step-4, HARDEST): retire kotc mechanism-1** — callInline for same-module inline too; DELETE
  inlineCall/spliceLambdaCall. Port the 5 embedded fixes into the bir2cir engine (crossinline/noinline→
  delegate fallback, positional type subst, dispatch-recv temp, suspendCoroutineUninterceptedOrReturn by
  FQN in bir2cir). Hardest: whole rt-stdlib self-build runs through this (let/run/forEach same-module
  pervasive), zero XFAIL cushion, regression surface = coroutines × generics. De-risk: (a) S3 battle-tests
  the engine on the cross-module shape first — S4 is then mostly kotc deletion + same-module index lookup;
  (b) branch-local rt-stdlib IL/BIR diff old-vs-new (never a committed dual track) + il-inline*/il-xinline/
  il-co* + differential; (c) split S4a (non-suspend user inline green) / S4b (suspend-intrinsic + stdlib
  flip) only if each half is a fully-green deletion — no compat flag between.

Order S1→S2→S3→S4. S2 sits between the keystone and the risky kotc retirements: independent, shrinks
ilemit while inline work is fresh, makes the stamp generic before S3 multiplies payload traffic.

## 5. Risks
1. **Mechanism-1 parity (S4)** — 5 hard-won kotc fixes × the rt-stdlib self-build (il-co*/Duration/
   il-collmore). Treat each fix as a named engine behavior with a dedicated sample before the kotc deletion.
2. **BIR-level splice hygiene** — label-id collisions (freshen per splice above the consuming file's max
   id), local-name collisions (per-splice prefix), nested/fixpoint (depth guard), valueBlock-in-statement
   positions (the value-producing form is new surface; ilemit's splice was void-only).
3. **Reified/generic re-lowering** — positional type-param subst must hit EVERY type token in the payload
   (params, locals, typeArgs, cast/is targets); a miss → gp:T (0-candidate red). Verify il-collmore +
   roundtrip typeName<T>().
4. **Ref sentinel + payload survivability** — RefBodySquash keeps the throw sentinel; the stash is opaque
   (base64-at-stash-time); measure ref.dll growth at S1.
5. **Suspend-in-inline** — spliced suspend bodies precede SuspendColdLowering (pass order guarantees it);
   intrinsic-FQN recognition moves with S4. roundtrip suspend sections (RT_XFAIL) are the canary;
   suspend-in-with (roundtrip-memext2) is the expected FIX.
6. **Cross-module payload → origin-module internals** (no @PublishedApi) / lifted closures — audit at S3;
   fall back to the plain call with a diagnostic.

# #75 Inline Unification — design (Fable consult 2026-07-10)

Collapse kotc's 3 inline mechanisms into ONE downstream splice, placed in **bir2cir** (NOT ilemit).

## The 3 mechanisms (file:line)
1. kotc `inlineCall` (same-module, body visible): BirEmitter.kt:3127 dispatch; engine 2193-2312, spliceLambdaCall 2361-2388, spliceBodyWithReturns 2423. Embeds hard-won fixes: crossinline/noinline delegate-local (2276-2295), tv-symbol typeArgSubst (2214-2240, il-collmore), dispatch-recv (2252-2264, Duration), nestedCapturesValue (2152-2175), suspendCoroutineUninterceptedOrReturn→suspendIntrinsic (2303-2311, coroutine-critical). Samples il-inline/il-inline2/il-xinline.
2. `[KotlinInline]` cross-module splice: producer BirEmitter 1328 + isInlineWithLambda 1362; ilemit Emitter.Assembly.cs (EmitAssembly metadata-strip)/Metadata.cs 45; call site 3915-3929→inlineSpliceCall 2338-2358 (lambda body in CALLER scope = NLR mechanism); consumer ilemit Emitter.InlineSplice.cs EmitInlineSplice. Limits: statement-only (void, Emitter.InlineSplice.cs), no type-arg subst, extension-inline excluded (3925), local-name scoping unhandled (EmitSplicedStmts, Emitter.InlineSplice.cs). Only roundtrip:281-309 exercises it.
3. Hardcodes (bodies absent at kotc stage): SCOPE_FUNCTIONS 368/dispatch 3176/inlineScope 2081; use{} 3184/inlineUse 2116; repeat→repeatInline 3890. Samples il-scope/il-use.

## CRUX: klib does NOT carry inline bodies (metadata protobuf — verified empirically). mechanism 3 persists post-klib. Also: all 806 [KotlinInline] stdlib payloads are throw-NotImplementedException stubs (RefBodySquash Program.cs:493 squashes BEFORE ilemit stamps; rt strips [KotlinInline] Program.cs:102). @InlineOnly fns ARE real callable CLR methods, so the ONLY semantic reason to splice = NLR + suspension-in-lambda. Reified needs NO splice (CLR reified generics).

## Target: kotc emits ONE `callInline` node (faithful FQ identity + pc/ga + resolved typeArgs + receivers + lambda params/bodies in caller scope); bir2cir resolves body (same-module from module BIR w/ mods.inline; cross-module from [KotlinInline] on ref) + splices to valueBlock, fixpoint, BEFORE MemberCallSubstitution + SuspendColdLowering. Delete inlineCall/spliceLambdaCall/inlineScope/inlineUse/SCOPE_FUNCTIONS/inlineSpliceCall/repeatInline.

## Why bir2cir not ilemit: coroutines (spliced suspend must reach SuspendColdLowering); layer rules ([KotlinInline] read = bir2cir); lowering coverage (payload = raw Kotlin BIR, needs MemberCallSubstitution/iterator/CharSeq); feasibility (value-splice trivial in JSON, ilemit returns void today).

## Migration (each step gate-green):
1. Fix stdlib carrier: stash raw pre-squash params+body for mods.inline through CIR so ilemit stamps REAL payloads; unify mechanism-2 to raw-BIR provenance. (prereq, gate-neutral)
2. bir2cir InlineSplice + kotc callInline; retire mechanism 3 (scope/use/repeat). Payload bodies trivial (FIR strips contract{}; let = return block(this); use gains real closeFinally fidelity). Gates il-scope/il-use/roundtrip suspend-in-with.
3. Re-home mechanism 2: delete kotc inlineSpliceCall + ilemit EmitInlineSplice; ext-inline exclusion (3925) lifts → NLR through ext-inline lambdas newly correct. Gate roundtrip:301.
4. Retire mechanism 1 (riskiest last): coroutine + generics; port crossinline/noinline closure-lift, move suspendCoroutineUninterceptedOrReturn recognition to bir2cir by FQN, positional tv-keyed type subst (immune to il-collmore name-capture). Gates il-inline/2/xinline + full il-co* family.

## Hard parts: value-producing splice (return e→result=e;br end; lambda-body return stays = NLR); hygiene (fresh locals + CFG labels per splice at BIR level); payload referencing internal helpers (no @PublishedApi handling — audit; fall back to plain call) or lifted closures (origin-module class — fall back). Biggest regression surface = step 4 × coroutines/generics.

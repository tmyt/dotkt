using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using DotKt.Bir;

// INLINE SPLICE (#71/#75). Consumes kotc's `callInline` node.
//
//      the GENERIC arm (#71/#75) — an inline fun taking a lambda, whose non-local `return` (or suspend) through the lambda
//      strictly requires the body to live inline at the call site. kotc emits the bindings; we RESOLVE the callee's RAW BIR
//      body by the overload key {owner|name|pc|ga}, disambiguating same-name overloads by a structural `paramSig` match
//      (§4.2) — same-module candidates from InlineBirStash's index, cross-module from [KotlinInline] on the --ref'd assembly
//      (ReferenceMetadataIndex.InlineCandidates / OwnerlessInlineCandidates) — and
//      SPLICE it at BIR level into a value-producing `valueBlock`. Because InlineSplice runs BEFORE all lowering
//      (Program.cs, before ClosureSynthesis/MemberCallSubstitution/BirTypeLowering), the spliced RAW body re-lowers IN
//      THIS app's context (@ClrIntrinsic binds against the app ref.dll, generics resolve with call-site type args,
//      reified is free on CLR). This SUPERSEDES ilemit's old EmitInlineSplice (deleted) — which spliced post-lowering,
//      void-only, and could not re-lower.
//
// The splice mirrors kotc's own same-module `inlineCall` (BirEmitterInline.kt) output shape: value/receiver params bound
// to fresh temps, each lambda-param `invoke` replaced by the carried caller-scope lambda body (its own param bound to a
// temp), the callee's own returns routed to a result-local + end-label, a bare non-local `return` kept verbatim (= the
// caller's return). HYGIENE is mandatory (payload label ids come from the ORIGIN file's dense-from-0 counter, so they
// collide with the consuming file): fresh SEQUENTIAL cfg ids above the file max, per-clone, and a per-splice local
// prefix. On any unsupported shape we FAIL LOUD (#95/§4.5 splice-all: kotc emits a `callInline` for EVERY inline call
// carrying a lambda arg — same- and cross-module — so this engine MUST splice it; there is no non-splice fallback, and an
// un-spliceable shape is a hard build-break to fix here or in the kotc gate, never a silently-degraded plain call).
//
// Runs at the same phase-1 position RepeatInlineLowering did (before ClosureSynthesis so any nested closure in the
// spliced body is synthesized once, before MemberCallSubstitution). Unconditional (ref + rt + app).
static class InlineSplice
{
    static int _counter;                       // global unique splice-instance id (local-prefix minting)
    static int _nextLabelId;                    // per-file fresh cfg label id (set at Apply entry)
    static ReferenceMetadataIndex _refs;
    static JsonArray _hoist;                    // #34: lifted default-lambda methods re-hoisted from a `defaultCarrier`
    static HashSet<string> _appLocalMethods;   // #43/#63: file-class method names a `newDelegate` can `ldftn` — MODULE-WIDE (every input file's file-class methods, seeded from Program.cs) + every drained re-hoist (a PENDING re-hoist is checked live via `_hoist`). Provenance oracle for the §4.4ii materialization-side newDelegate guard, the materialization-side counterpart of the §4.6 `!sameModule` payload-side guard in RewriteGeneric.
    // §4.4ii ref-cell write-through: a MATERIALIZED carrier that WRITES a captured enclosing `var` (a bare `setLocal` to a
    // capture kotc did NOT ref-cell-box — a cross-module inline body's callee-local, etc.) must promote that var to a shared
    // heap cell so the closure's write is visible in the enclosing scope. `_refCellNames` dedups one `dotkt$…$Ref$…` class
    // per element-type-JSON per file; `_boxRequests` records each enclosing `var` to box (name -> {refName, elem}); flushed
    // to the file `refTypes` registry (SharedSyntheticSynthesis assembles the class) + a whole-method box post-pass at Apply.
    static Dictionary<string, string> _refCellNames;          // elem-type JSON -> ref-cell class name (per file)
    static List<(string varName, string refName, JsonNode elem)> _boxRequests;   // DISTINCT (enclosing var, ref class, elem) box requests — keyed by cell, NOT bare name, so two same-named-but-different-typed captures both survive (BoxMaterializedCaptures scopes each by its own cell)

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, IReadOnlyCollection<string> moduleWideAppLocalMethods)
    {
        _refs = refs;
        _nextLabelId = MaxLabelId(root) + 1;
        _hoist = new JsonArray();
        // #63 (F4): SEED module-wide (every input file's file-class methods), NOT just this file's — ilemit FindStatic
        // resolves a `newDelegate` target by bare name against ALL IsFileClass types module-wide, and the inline stash
        // spans all files, so a carrier materializing a SIBLING file's lifted `__lambdaN` IS app-local. Copy into a fresh
        // per-file set so drained #34 re-hoists (added below) never leak across files.
        _appLocalMethods = new HashSet<string>(moduleWideAppLocalMethods, StringComparer.Ordinal);
        _refCellNames = new Dictionary<string, string>(StringComparer.Ordinal);
        _boxRequests = new List<(string, string, JsonNode)>();
        Walk(root, 0);
        // #34: a `defaultCarrier` default (a non-capturing lambda default `= { error(...) }`) re-hoists its lifted
        // `__lambdaN` helper into THIS file's file-class methods (fresh name), exactly like DefaultArgSplice's cross-module
        // path — do it BEFORE the post-passes so they normalize the hoisted bodies too.
        if (_hoist.Count > 0)
        {
            if (root is not JsonObject fo || fo["methods"] is not JsonArray methods)
                throw new InvalidOperationException("bir2cir: InlineSplice re-hoisted a carried default lambda but the file root has no `methods` array");
            // DRAIN: a re-hoisted default-lambda body may ITSELF contain a `callInline` (a nested inline call in the
            // default, e.g. `= { listOf(1,2).count { it > 0 } }`) — splice it through InlineSplice BEFORE the method lands
            // (an un-spliced callInline reaching ilemit is a hard break), and that splice may enqueue further carriers.
            while (_hoist.Count > 0)
            {
                var h = _hoist[0]; _hoist.RemoveAt(0);
                Walk(h, 0);
                if (h is JsonObject ho && Str(ho["name"]) is string hn) _appLocalMethods.Add(hn);   // #43: a drained re-hoist is now `ldftn`-resolvable app-local
                methods.Add(h);
            }
        }
        // §4.4ii ref-cell write-through: promote each enclosing `var` a materialized carrier WRITES (recorded in
        // `_boxRequests`) to a shared heap cell — flush the cell classes to the file `refTypes` registry (SharedSyntheticSynthesis
        // assembles them) and rewrite each var's decl + all in-method reads/writes to go through the cell's `.v` field. The
        // carrier-side field rewrite (this.<cap>.v) already happened at materialization; this is the ENCLOSING-scope half.
        if (_boxRequests.Count > 0) BoxMaterializedCaptures(root);

        // POST-PASS: an IMPLICIT `Nullable<VT> -> VT` flow left by SubstTv concretizing a payload's generic `V?` to a
        // concrete `Nullable<VT>` struct, then flowing that local into a non-nullable value-type slot WITHOUT a cast
        // node — a cond branch (`getOrPut`'s `else value`), a var init (a `?.let` receiver `__self = tmp0_safe_receiver`),
        // a return. ilemit emits the raw struct into the VT slot and reads its HasValue bit (=1). Runs whole-tree (the
        // `?.let` receiver binding is minted by STEP 5, AFTER the per-splice STEP-2c cast normalization). Types are still
        // the pre-lowering `kotlin.*` form here, so IsValueTypeFqn matches (same oracle as NormalizeConcretizedCasts).
        NormalizeImplicitNullableUnwrap(root);
        WidenCovariantConstruction(root);
        RetypeReceiverToConcrete(root);
        // CHOKEPOINT (§4.5, mirrors DefaultArgSplice.AssertNoPlaceholder): every `callInline` kotc emits under
        // splice-all must be consumed by this pass. A survivor — notably one kotc emitted WITHOUT a `pc` (silently
        // skipped by Rewrite at the `o.ContainsKey("pc")` gate) — would reach ilemit, which cannot emit a callInline
        // (it fails opaquely there). Fail loud HERE with the callee so the un-spliced site is identifiable.
        AssertNoUnsplicedInline(root);
    }

    // #43/#63: the FILE-CLASS method names a `newDelegate` target `ldftn`-resolves against — collected MODULE-WIDE across
    // EVERY input file (ilemit's FindStatic binds a delegate method by bare name against ALL `IsFileClass` types in the
    // module, and the inline stash spans all files, so a materialized carrier splicing a SIBLING file's lifted `__lambdaN`
    // is app-local too — #63/F4). Nested-TYPE member methods are deliberately excluded — a bare-name match there would
    // ACCEPT here then fail loud in ilemit's ldftn, so keeping the set exactly ilemit's file-class universe fails such a
    // mismatch at THIS layer instead. Pre-collected once by Program.cs before the per-file Apply loop; re-hoisted #34
    // defaults then fold into the per-file working set live as they drain.
    public static HashSet<string> CollectAppLocalMethodNames(IEnumerable<JsonNode> roots)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
            if (root is JsonObject ro && ro["methods"] is JsonArray a)
                foreach (var m in a) if (m is JsonObject mo && Str(mo["name"]) is string mn) set.Add(mn);
        return set;
    }

    static void Walk(JsonNode node, int depth)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) if (kv.Value != null) Walk(kv.Value, depth);
            Rewrite(o, depth);
        }
        else if (node is JsonArray a)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] is JsonNode c)
                {
                    Walk(c, depth);
                    if (a[i] is JsonObject co) Rewrite(co, depth);
                }
        }
    }

    static void Rewrite(JsonObject o, int depth)
    {
        // Every `callInline` kotc emits under splice-all carries `pc`/`ga`/`paramSig` (+ an `owner` that is a file-class
        // string for a facadegen-injected user fn OR JSON-null for a stdlib scope-fn/@InlineOnly fn whose owner bir2cir
        // resolves). `kotlin.repeat` now splices through this same path (kotc no longer emits the specialized loop node).
        if (Str(o["k"]) == "callInline" && o.ContainsKey("pc")) RewriteGeneric(o, depth);
    }

    // ---- (2) the generic cross-module splicer ---------------------------------------------------------------------

    static void RewriteGeneric(JsonObject o, int depth)
    {
        var owner = Str(o["owner"]);
        var callee = Str(o["callee"]);
        var name = callee != null && callee.Contains('.') ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        int pc = Int(o["pc"]);
        int ga = Int(o["ga"]);
        var paramSig = o["paramSig"] as JsonArray;   // §4.2 overload key: the callee's DECLARED param type nodes (kotc-emitted)

        if (depth > 32) { FailLoud(o, owner, name, pc, ga, "inline splice depth > 32 (recursive-inline data corruption)"); return; }

        // RESOLVE (§4.2, #75 S4b): pick the UNIQUE inline overload whose declared params match the call's `paramSig`.
        //  - OWNER-FUL (a facadegen-injected user fn, or a same-module kotc inlineSpliceCallSameModule): candidates come from
        //    the same-module stash first, else the ref.dll [KotlinInline] under that owner (ResolveInlinePayload).
        //  - OWNER-LESS (a `kotlin.*` stdlib scope-fn/@InlineOnly fn — kotc can't name the klib file class): the bare
        //    name|pc|ga collides across owners (Iterable/Array/IntArray `filter`/`map`/…), so gather candidates across EVERY
        //    kotlin.* host and let paramSig pick the winner, whose own `owner` names the host. In the stdlib SELF-BUILD the
        //    target is same-module (in the stash); in an app build it is cross-module (the ref.dll) — try the stash FIRST.
        //    `sameModule` gates the §4.6 newDelegate guard.
        JsonObject payload;
        bool sameModule;
        if (owner != null)
        {
            var r = ResolveInlinePayload(owner, name, pc, ga, paramSig);
            if (r.payload == null) { FailLoud(o, owner, name, pc, ga, r.diag); return; }
            payload = r.payload; sameModule = r.sameModule;
        }
        else
        {
            var (hit, sm, diag) = ResolveOwnerless(name, pc, ga, paramSig);
            if (hit == null) { FailLoud(o, null, name, pc, ga, diag); return; }
            payload = (JsonObject)hit.DeepClone();
            owner = Str(payload["owner"]);   // the winner's own file-class host
            sameModule = sm;
        }

        // GUARD SCAN -> FAIL LOUD (#95/§4.5 splice-all): kotc emits a callInline for EVERY inline call with a lambda arg,
        // so this engine MUST splice it — there is no plain-call fallback and every un-handled shape is a hard build-break,
        // not a silently-degradable call. o is untouched until step 7, so a mid-stream FailLoud is sound.
        if (Int(payload["v"]) != 1) { FailLoud(o, owner, name, pc, ga, "stale [KotlinInline] payload (pre-raw-BIR)"); return; }
        var pParams = payload["params"] as JsonArray ?? new JsonArray();
        var pBody = payload["body"] as JsonArray;
        if (pBody == null) { FailLoud(o, owner, name, pc, ga, "payload has no body"); return; }
        var typeArgs = o["typeArgs"] as JsonArray ?? new JsonArray();
        if (typeArgs.Count < ga) { FailLoud(o, owner, name, pc, ga, "fewer typeArgs than generic arity"); return; }
        // §4.6: a payload `newDelegate` is a capture-less nested lambda lifted to the ORIGIN file's __lambdaN class; that
        // type token dangles when spliced into another file. Legal same-module (the class is in this file); across a
        // ref.dll edge it is a hard error (engine-side zero-capture synthesis is a future follow-up, never a kotc hack).
        if (!sameModule && HasNode(pBody, "newDelegate")) { FailLoud(o, owner, name, pc, ga, "cross-module payload carries a newDelegate (origin-file __lambdaN) — dangles when spliced (§4.6)"); return; }
        // BATCH B (#75): a capturing `newSuspendLambda` in the payload is now a first-class HYGIENE citizen — its
        // `captures[].name` descriptors are renamed JOINTLY with the frame by ApplyPrefix/RewriteLocalRefs (skipping the
        // SM's own inner scope), and a captured lambda-param is materialized into a real suspend SM value (§4.4ii suspend
        // arm). The broad fail-loud guard is NARROWED to a capture-name that collides with the SM's own inner scope, PLUS
        // a captured `__outer` ONLY when the payload is NEITHER an extension NOR a dispatch member — i.e. a genuinely
        // unbindable enclosing `this`. For an extension/dispatch payload the enclosing receiver IS bound to a splice temp
        // (STEP 5 `__self` / §4.3 dispatch `this`), so 2B rebinds each payload `newSuspendLambda`'s `__outer` construction
        // value to that temp (BindOuterCapValues) instead of refusing.
        bool payloadExt = Str(payload["recv"]) == "extensionParam";
        bool payloadDispatch = Str(payload["recv"]) == "dispatch";
        if (SuspendCaptureHazard(pBody, refuseOuter: !(payloadExt || payloadDispatch)) is string suspHazard)
        { FailLoud(o, owner, name, pc, ga, $"payload newSuspendLambda capture '{suspHazard}' cannot be splice-rewritten (captured enclosing receiver, or a name colliding with the SM's own scope) — #75 Batch B"); return; }

        var pRet = payload["ret"]?.DeepClone();

        // STEP 2 — positional type-param subst (payload tv{scope:method,i} -> the call's typeArgs[i]).
        // STEP 2a0 — rename each spliced `newSam`/`newClosure` synthClass to a PER-SPLICE-INSTANCE unique name FIRST: the
        // same inline fn spliced at 2+ sites with DIFFERENT type args yields divergent (SubstTv-specialized) class bodies
        // under the SAME origin name (`dotkt$…$SamN`); ClosureSynthesis' name-dedup would collapse them to the first, so the
        // other sites construct the wrong class (boxgen: `compareBy{it}` vs `compareBy{it.first}` both -> Sam102).
        RenameSpliceSynthClasses(pBody, Interlocked.Increment(ref _counter));
        // F2B: the dispatch receiver's concretized owning-class type args (in the flattened `scope:type` index space), or
        // null when kotc could not supply them (no dispatch / non-generic owner / receiver-class != owner / tv-render /
        // arity mismatch). Substituted alongside `scope:method` in the SAME fused STEP-2 pass over the pristine payload.
        var dispatchTypeArgs = (o["recvs"] as JsonObject)?["dispatchTypeArgs"] as JsonArray;
        if (dispatchTypeArgs != null && dispatchTypeArgs.Count == 0) dispatchTypeArgs = null;
        SubstTv(pBody, typeArgs, ga, dispatchTypeArgs);
        SubstTvIn(pRet, typeArgs, ga, dispatchTypeArgs);
        foreach (var p in pParams) if (p is JsonObject po && po["type"] is JsonNode pt) SubstTvIn(pt, typeArgs, ga, dispatchTypeArgs);
        // STEP 2b — a spliced `newSam`/`newClosure` whose synthClass SubstTv just made FULLY CONCRETE (every tv replaced by a
        // call-site type) but still declares generic `typeParams` + a positional `typeArgs` is a generic-class-with-UNUSED-
        // params implementing a CONCRETE (value-type) interface (`Sam102<T> : Comparator<Int32>`) — value-type interface
        // dispatch then can't bind `compare` (EntryPointNotFound). Drop the now-unused typeParams AND the node's typeArgs to
        // make it a non-generic `Sam102 : Comparator<Int32>`. No-op when any tv survives (a genuinely-generic closure/SAM).
        PruneConcreteSynthClasses(pBody);
        // STEP 2c (seq/single,last): kotc lowers a CONCRETE value-type `as` (`x as Int`) to `nullableValue` (an unbox), but a
        // GENERIC `x as T` to a raw `cast` node. When SubstTv concretizes `cast to tv{T}` -> `cast to Int32` (a value type),
        // the raw `cast` of a boxed/nullable operand is a ref castclass on a value type -> unverifiable native IL (SIGSEGV,
        // e.g. `Iterable.single`/`last`'s `return single as T`). kotc never emits a concrete value-type `cast`, so any
        // `cast to <value type>` here is a concretized generic -> rewrite it to `nullableValue` (ilemit Unbox_Any).
        NormalizeConcretizedCasts(pBody);

        // (FINDING 2 — #75 holistic — a residual `tv{scope:type}` (a generic OWNER class's param) in a dispatch (member)
        // inline. NO GUARD: the signal is not cleanly separable at this layer. The common SAME-CLASS splice — a generic
        // class's own inline member called from its own methods (`ArrayDeque<E>.filterInPlace` into `retainAll`) — is
        // SOUND (caller class IS owner class, so ilemit binds the payload's `tv{scope:type,i}` positionally to the caller's
        // param[i] correctly) and stdlib-load-bearing, so a blanket residual-scope:type guard is a hard FP that breaks the
        // stdlib; and even gated on cross-class it can mis-fire on a transient referenced-interface scope:type that a
        // downstream pass resolves. The genuinely unsound case — a CROSS-class same-module generic-owner member inline
        // (emittable: BirEmitterInline.inlineSpliceCallSameModule has no same-class restriction) — resolves SILENTLY in
        // ilemit (Emitter.Types.ResolveTv binds positionally then falls back to `object`, never throws). CROSS-LAYER
        // FOLLOW-UP (coordinator): kotc carries the dispatch receiver's instantiated type args on `recvs`; RewriteGeneric
        // substitutes `tv{scope:type,i}` like SubstTv does scope:method — the correct fix, and it also types the dispatch
        // temp precisely instead of the bare `Fqn(owner)`.)

        // STEP 3 — hygiene: fresh cfg ids + per-splice local prefix over the callee body.
        int n = Interlocked.Increment(ref _counter);
        string prefix = "__inls" + n + "$";
        FreshenLabels(pBody);
        PrefixLocals(pBody, prefix);

        // STEP 4 — route the callee's OWN returns to a result-local + end-label (BEFORE lambda splicing: at this point
        // every `{k:return}` in the body is the origin fn's, not a caller-lambda's). Unit callee -> no result-local.
        bool unit = IsUnit(pRet);
        JsonNode result = RouteReturns(pBody, unit, pRet, prefix);
        // FINDING 3a-fold (#75 holistic): RouteReturns TAIL-FOLDS a no-early-return `return <T?-local>` into `result` = a
        // bare Nullable<VT> local (e.g. `if (x==null) throw …; return x`). That local feeds the caller's concrete VT slot,
        // so the raw struct's HasValue bit would be read as the value. The whole-tree var/cond/setLocal arms don't see a
        // valueBlock `result` local — unwrap it HERE against the concretized return type (an EARLY return routes through
        // the setLocal arm instead). Prefixed body locals already carry their declared `nullable(VT)` type.
        if (!unit && VtFqnOf(pRet) is string foldVt && result != null)
        {
            var pbTypes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            CollectVarTypes(pBody, pbTypes);
            if (UnwrapNullableLocal(result, foldVt, pbTypes) is JsonNode folded) result = folded;
        }

        // STEP 5 — bind extension receiver + value params to temps; register lambda args; rewrite body param refs.
        var stmts = new JsonArray();
        var subst = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var lambdaMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var lambdaFuncType = new Dictionary<string, JsonNode>(StringComparer.Ordinal);   // §4.4ii: lamParam -> its funcType (for materialization)
        var origLambdaParams = new HashSet<string>(StringComparer.Ordinal);   // #75 B3: the ORIGINAL (unprefixed) lambda-param names
        var callArgs = o["args"] as JsonArray ?? new JsonArray();
        bool ext = Str(payload["recv"]) == "extensionParam";
        var recvs = o["recvs"] as JsonObject;
        // #34: emitted-position i -> the temp its param bound to, so a TIER-2 default carrier's `{defaultArgParam idx}`
        // token (a default reading an EARLIER param) resolves to that param's already-bound temp. `defaultThisRecv` binds a
        // `{this}` token in a default (a `= this` extension-receiver default / a dispatch-receiver read).
        var boundArgs = new JsonArray();
        JsonNode defaultThisRecv = Str(payload["recv"]) == "dispatch" ? new JsonObject { ["k"] = "local", ["name"] = prefix + "this" } : null;

        // §4.3 — DISPATCH RECEIVER (member inline fn): the payload's `this` refs are the callee's dispatch receiver, not
        // the CALLER's `this`. Bind kotc's carried `recvs.dispatch` to a fresh temp and rewrite the payload body's own
        // `{k:this}` (not descending into nested closures/type-defs — their `this` is their own). Dormant until kotc wires
        // the cross-module member-inline gate (S4b), but required-and-fail-loud when a `dispatch` payload does resolve.
        if (Str(payload["recv"]) == "dispatch")
        {
            if (recvs?["dispatch"] is not JsonNode disp)
            { FailLoud(o, owner, name, pc, ga, "dispatch (member) inline but no recvs.dispatch carried"); return; }
            string thisTemp = prefix + "this";
            // owner = the enclosing type name for a member payload (InlineBirStash keys members under the type), so it is
            // the dispatch receiver's declared type.
            // F2B: type the dispatch temp at the CONCRETE `owner<dispatchTypeArgs>` when carried (else the bare owner) — so
            // a `this.field: T` member read on it binds the precise instantiation instead of the erased owner.
            var thisType = dispatchTypeArgs != null
                ? new JsonObject { ["t"] = "fqn", ["name"] = owner, ["args"] = dispatchTypeArgs.DeepClone() }
                : (JsonNode)TypeJson.Fqn(owner);
            stmts.Add(new JsonObject { ["k"] = "var", ["name"] = thisTemp, ["type"] = thisType, ["init"] = disp.DeepClone() });
            RewriteThis(pBody, thisTemp);
            RewriteThis(result, thisTemp);
        }

        for (int i = 0; i < pParams.Count; i++)
        {
            if (pParams[i] is not JsonObject p) continue;
            string pn = Str(p["name"]);
            var ptype = p["type"];
            // The extension receiver rides payload param[0] == "__self"; its value is recvs.extension, then the call
            // args align to the REMAINING params. Non-extension: args align 1:1.
            JsonNode argNode;
            if (ext && i == 0) argNode = recvs?["extension"];
            else argNode = callArgs.ElementAtOrDefault(ext ? i - 1 : i);

            if (argNode is JsonObject ao && Str(ao["k"]) == "inlineLambda")
            {
                // M1 hygiene: key lambdaMap by the PREFIXED param name (and rewrite the payload's refs to it via `subst`),
                // so the key is splice-local and cannot collide with a caller-CAPTURED var of the SAME name (e.g. a user
                // local `transform` free-referenced inside the escaping lambda) that gets spliced into the payload at STEP
                // 6 — which would otherwise mis-forward/mis-detect it. The carrier's own captured refs stay unprefixed.
                string lname = prefix + pn;
                lambdaMap[lname] = ao;   // spliced at its invoke sites (step 6) / forwarded (§4.4), no temp
                if (ptype != null) lambdaFuncType[lname] = ptype;   // §4.4ii: the delegate/funcType for a materialized carrier
                subst[pn] = new JsonObject { ["k"] = "local", ["name"] = lname };
                origLambdaParams.Add(pn);   // #75 B3
                RecordBound(boundArgs, i, lname);
                continue;
            }
            // A null arg slot = an OMITTED DEFAULT (splice-all: kotc emits a `null` in the defaulted param's slot). Fill it
            // from the callee's own default value. TIER-1 (a metadata-representable const) rides `p["default"]`; a TIER-2
            // default (a NON-const expr — notably a lambda default `= { error(...) }`, #34) rides the param's `@KotlinDefault`
            // carrier attr — the SAME default-value source DefaultArgSplice reads cross-module. Both are SubstTv'd into the
            // call's type frame like the body. An un-carried extension receiver, or a null slot with NO carried default, is
            // a real error -> fail loud (no fallback under #95).
            if (argNode == null)
            {
                if (ext && i == 0) { FailLoud(o, owner, name, pc, ga, "extension receiver not carried"); return; }
                if (p["default"] is JsonNode pdef)
                {
                    argNode = pdef.DeepClone();
                    RewriteLocalRefs(argNode, subst);   // a default that references an EARLIER param -> its already-bound temp
                }
                else if (KotlinDefaultCarrier(p) is string carrierBir)
                {
                    // Parse the carrier (refuse a `defaultUnsupported` poison — a capturing/SAM/suspend lambda default; unwrap
                    // a `defaultCarrier`, re-hoisting its lifted `__lambdaN` into this file via `_hoist`), then bind its
                    // `{defaultArgParam idx}` / `{this}` tokens to the ALREADY-bound earlier-param temps (Kotlin defaults
                    // reference only earlier params) — the shared DefaultArgSplice machinery.
                    var raw = DefaultArgSplice.MaterializeDefault(carrierBir, _hoist, _refs, name, i);
                    if (raw == null) { FailLoud(o, owner, name, pc, ga, $"param '{pn}' default carrier BIR is unparseable"); return; }
                    var recvForTokens = defaultThisRecv ?? (ext ? boundArgs.ElementAtOrDefault(0) : null);
                    argNode = DefaultArgSplice.SubstituteTokens(raw, recvForTokens, boundArgs);
                }
                else { FailLoud(o, owner, name, pc, ga, $"missing (non-defaulted) arg for param {pn}"); return; }
                SubstTvIn(argNode, typeArgs, ga, dispatchTypeArgs);
                // BATCH B (#75): a capturing newSuspendLambda built inside a param default binds to a temp whose init
                // flows through the same joint-hygiene rewriters as the body — the former fail-loud guard is retired.
                // (A `defaultCarrier`'s own capturing/SAM/suspend lambda form is still refused as `defaultUnsupported`.)
            }
            string temp = prefix + pn;
            stmts.Add(new JsonObject { ["k"] = "var", ["name"] = temp, ["type"] = ptype?.DeepClone(), ["init"] = argNode.DeepClone() });
            subst[pn] = new JsonObject { ["k"] = "local", ["name"] = temp };
            RecordBound(boundArgs, i, temp);
        }
        RewriteLocalRefs(pBody, subst);
        RewriteLocalRefs(result, subst);   // D2: a tail-folded `result` (`= action(x)`) keeps raw param refs otherwise

        // 2B (#75 Batch B) — a PAYLOAD `newSuspendLambda` that captured `__outer` (the enclosing dispatch/extension
        // receiver of the inline fn being spliced, e.g. `flow { … this@transform … }` in an extension `transform`) has
        // its construction value synthesized LATER by SuspendLambdaLowering from the descriptor NAME `__outer`, which its
        // fallback resolves to the CALLER's `this`/`__self` — the wrong receiver. Rebind each payload-frame `__outer`
        // construction value to the splice's bound receiver temp: extension -> `<prefix>__self` (STEP 5), dispatch ->
        // `<prefix>this` (§4.3). SuspendLambdaLowering consumes the `capValues` override verbatim.
        if (payloadDispatch)
            { var ov = new JsonObject { ["k"] = "local", ["name"] = prefix + "this" }; BindOuterCapValues(pBody, ov); BindOuterCapValues(result, ov); }
        else if (ext)
            { var ov = new JsonObject { ["k"] = "local", ["name"] = prefix + "__self" }; BindOuterCapValues(pBody, ov); BindOuterCapValues(result, ov); }

        // B3 (#75 holistic) — a `{k:typeDef}` (a named local class in the payload — an `object :` literal) is a SCOPE
        // BOUNDARY that RewriteLocalRefs/PrefixLocals skip WHOLE, so an ORIGINAL (unprefixed) lambda-param `{k:local}` ref
        // INSIDE it never gets bound to its carrier -> it would silently dangle at ilemit. (A well-formed capturing object
        // rides the lambda-param as a ctor-arg OUTSIDE the typeDef, so this is rare — but converting the silent hole to a
        // loud one is the point.) Subtract each typeDef method's OWN scope so a coincidental same-named local is not a
        // false positive. The D3 remainder (below) only catches PREFIXED names, which never reach inside a typeDef.
        if (origLambdaParams.Count > 0 && TypeDefLambdaParamRef(pBody, origLambdaParams) is string dangling)
        { FailLoud(o, owner, name, pc, ga, $"a payload local class (object literal) references lambda param '{dangling}' directly — a lambda-param captured inside a typeDef is not splice-bound (#75 Batch B3)"); return; }

        // STEP 6 — splice each lambda-param `invoke` with the carried caller-scope lambda body (fresh per invocation).
        SpliceLambdaInvokes(pBody, lambdaMap);
        SpliceLambdaInvokes(result, lambdaMap);   // D2: the folded `result` may itself BE the invoke (`= action(x)`)

        // §4.4(i) — FORWARDING: a lambda param passed BY NAME into a NESTED stdlib-inline call (`map` forwards `transform`
        // to the plain `callStatic mapTo(dest, transform)`) is not a direct invoke here — convert that nested call into a
        // callInline carrying the caller's carrier, so STEP 8's fixpoint splices it where mapTo invokes `transform`.
        ForwardLambdaArgs(pBody, lambdaMap);
        ForwardLambdaArgs(result, lambdaMap);

        // §4.4(iii) — RETIRE a nested inlineLambda carrier's capture DESCRIPTOR whose lambda-param STEP 6 (invoke-splice)
        // / §4.4(i) (forwarding) already fully consumed, i.e. a descriptor-only survivor: the carrier body no longer
        // reads it, so the field a later MaterializeCarrier / MaterializeSuspendCarrier would mint for it is provably
        // dead (RewriteCapturesToFields + FunGen bind fields ONLY via surviving body refs), while the descriptor still
        // forces a name-synthesized `{k:local,name}` ctor arg that no `var` declares — the dangling-capture IrSanity
        // fault (e.g. `filter { it is R }`: `predicate` invoked+inlined, but the unsafeTransform carrier still lists it).
        // This is STEP 6's DUAL (E5 keeps descriptors in lockstep with refs; once refs are gone, the descriptor is splice
        // residue). Only inlineLambda descriptors are pruned — a newSuspendLambda's capValues are POSITIONAL (pruning one
        // slot would skew the rest) and its STEP-6 SM boundary means an SM capture always keeps a `{k:local}` body ref, so
        // it is never descriptor-only. Restricted to `lambdaMap` keys (globally-unique minted param names) so a genuine
        // user capture is untouchable. Runs BEFORE §4.4ii so a now-unreferenced carrier is not spuriously materialized.
        PruneConsumedCarrierCaptures(pBody, lambdaMap);
        PruneConsumedCarrierCaptures(result, lambdaMap);

        // §4.4(ii) — MATERIALIZE any lambda-param carrier still referenced in a NON-INVOKE position (captured in a
        // `newClosure`, e.g. `AtomicArray(size, init) = AtomicArray(Array(size){ init(it) })` where `init` is a capture of
        // the `{ init(it) }` closure; or forwarded to a non-inline call) as a real `newClosure` VALUE bound to a fresh
        // temp, rewriting the surviving `{k:local,name:<lamParam>}` refs to that temp. Legal here: InlineSplice runs BEFORE
        // ClosureSynthesis, which assembles the class from the emitted `synthClass`.
        foreach (var (lname, carrier) in lambdaMap.ToList())
        {
            if (!HasLocalIn(pBody, new[] { lname }) && !HasLocalIn(result, new[] { lname })) continue;   // already invoked/forwarded
            var matTemp = MaterializeCarrier(carrier, lambdaFuncType.GetValueOrDefault(lname), stmts);
            if (matTemp == null)
                // Un-materializable (a NON-LOCAL-return carrier can't be a delegate; or a `{k:local}` capture kotc did not
                // list on the carrier) — fail loud (no fallback under #95; never a silent miscompile).
                { FailLoud(o, owner, name, pc, ga, $"lambda param '{lname}' in a non-invoke position could not be materialized (§4.4ii) — non-local-return or unlisted-capture carrier [reason={_matReason}]"); return; }
            var rebind = new Dictionary<string, JsonNode>(StringComparer.Ordinal) { [lname] = new JsonObject { ["k"] = "local", ["name"] = matTemp } };
            RewriteLocalRefs(pBody, rebind);
            RewriteLocalRefs(result, rebind);
        }

        // D3 remainder: any lambda-param ref STILL present after §4.4ii materialization is a dangling local — fail loud.
        if (HasLocalIn(pBody, lambdaMap.Keys) || HasLocalIn(result, lambdaMap.Keys))
        { FailLoud(o, owner, name, pc, ga, "lambda param aliased to a non-invoke position (not directly invoked) — not materialized (§4.4ii remainder)"); return; }
        // §4.4(iii) tripwire: a lambda-param surviving ONLY as a nested carrier's capture DESCRIPTOR after the retire +
        // §4.4ii rebind is an un-consumed hole (the prune's CarrierStillReferences kept it, yet §4.4ii found no `{k:local}`
        // to materialize) — convert to a loud splice-site diagnosis rather than a downstream dangling-capture fault.
        if (lambdaMap.Keys.Any(k => HasCaptureDescIn(pBody, k) || HasCaptureDescIn(result, k)))
        { FailLoud(o, owner, name, pc, ga, "lambda param survives only as a nested carrier's capture descriptor after §4.4(iii) retire + §4.4(ii) rebind"); return; }

        // STEP 7 — assemble the value-producing valueBlock, swap it in-place.
        foreach (var st in pBody) if (st != null) stmts.Add(st.DeepClone());
        var repl = new JsonObject { ["k"] = "valueBlock", ["stmts"] = stmts, ["result"] = result };
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();

        // STEP 8 — fixpoint: the spliced body may itself contain a nested `callInline` (e.g. kotlin.repeat).
        Walk(o, depth + 1);
    }

    // §4.5 FAIL LOUD (#95 splice-all): kotc emits a callInline for EVERY inline call carrying a lambda arg, so this engine
    // MUST splice it — there is no plain-call fallback, and any shape it cannot splice is a hard build-break (a dropped
    // non-local return / trapped suspension), never a silently-degradable plain call. Throw with the full overload key +
    // reason so the failing call site is identifiable.
    static void FailLoud(JsonObject o, string owner, string name, int pc, int ga, string reason) =>
        throw new NotSupportedException(
            $"inline splice: cannot splice {owner}.{name} (pc={pc} ga={ga}): {reason} — "
            + "under splice-all a callInline arrives for every inline+lambda call, so there is no fallback; fix the splice shape or the kotc gate.");

    // #34: record that emitted-position param `i` bound to local `name`, so a later param's TIER-2 default carrier token
    // `{defaultArgParam idx=i}` (a default reading this earlier param) resolves to it.
    static void RecordBound(JsonArray boundArgs, int i, string name)
    {
        while (boundArgs.Count <= i) boundArgs.Add(null);
        boundArgs[i] = new JsonObject { ["k"] = "local", ["name"] = name };
    }

    // #34: the TIER-2 default-value BIR string a param carries in its `@KotlinDefault(index, bir)` attr (kotc stamps it on
    // every non-const defaulted param of a qualifying fn — the SAME carrier DefaultArgSplice reads cross-module), or null.
    static string KotlinDefaultCarrier(JsonObject p)
    {
        if (p["attrs"] is not JsonArray attrs) return null;
        foreach (var a in attrs)
            if (a is JsonObject ao && Str(ao["attr"]) == "kotlin.clr.KotlinDefault"
                && ao["args"] is JsonArray args && args.Count >= 2
                && args[1] is JsonObject bv && Str(bv["k"]) == "const"
                && (bv["value"] as JsonValue)?.TryGetValue<string>(out var bir) == true)
                return bir;
        return null;
    }

    // §4.2 (#75 S4b) — RESOLVE the callee's raw-BIR payload by the overload key owner|name|pc|ga, disambiguating same-name
    // inline OVERLOADS by a STRUCTURAL match of each candidate's declared param types against the call's `paramSig`
    // (InlineBirStash.SelectByParamSig — DeepEquals, exact because both sides are kotc-emitted decl type nodes). Same-module
    // candidates come from the in-run stash; cross-module from the ref.dll [KotlinInline]. Returns the (cloned) unique
    // payload + whether it is same-module, or (null, false, diag) with a fail-loud reason (no match / ambiguous overloads).
    static (JsonObject payload, bool sameModule, string diag) ResolveInlinePayload(string owner, string name, int pc, int ga, JsonArray paramSig)
    {
        if (InlineBirStash.Index.TryGetValue($"{owner}|{name}|{pc}|{ga}", out var smCands) && smCands.Count > 0)
        {
            var hit = InlineBirStash.SelectByParamSig(smCands, paramSig, out int mc);
            if (hit != null) return ((JsonObject)hit.DeepClone(), true, null);
            return (null, false, $"no unique same-module inline overload for the call's param signature "
                + $"({smCands.Count} candidate(s), {mc} matched) — structurally-ambiguous inline overloads (e.g. differ only in generic bounds like ifEmpty)");
        }
        var crossCands = _refs?.InlineCandidates(owner, name, pc, ga);
        if (crossCands != null && crossCands.Count > 0)
        {
            var hit = InlineBirStash.SelectByParamSig(crossCands, paramSig, out int mc);
            if (hit != null) return ((JsonObject)hit.DeepClone(), false, null);
            return (null, false, $"no unique cross-module [KotlinInline] overload for the call's param signature "
                + $"({crossCands.Count} candidate(s), {mc} matched) — structurally-ambiguous inline overloads (e.g. differ only in generic bounds like ifEmpty)");
        }
        return (null, false, "no [KotlinInline] payload found");
    }

    // §4.4(i) / §4.2 — resolve an OWNER-LESS kotlin.* inline call (a scope-fn/@InlineOnly fn kotc could not name): try the
    // SAME-MODULE stash FIRST (stdlib self-build — the target is being compiled this run), then the ref.dll (app build).
    // Returns the unique paramSig-matched payload (NOT cloned) + whether it is same-module, or (null, false, diag). The
    // winner's own `owner` field names the host file class.
    static (JsonObject hit, bool sameModule, string diag) ResolveOwnerless(string name, int pc, int ga, JsonArray paramSig)
    {
        var smCands = InlineBirStash.OwnerlessCandidates(name, pc, ga);
        if (smCands != null)
        {
            var hit = InlineBirStash.SelectByParamSig(smCands, paramSig, out int mc);
            if (hit != null) return (hit, true, null);
            if (mc > 1) return (null, false, $"owner-less callInline: no unique same-module paramSig match among {smCands.Count} kotlin.* candidate(s) ({mc} matched) — structurally-ambiguous overloads (e.g. differ only in generic bounds like ifEmpty)");
            // 0 same-module matches -> fall through to the ref.dll (a same-name kotlin.* fn may live cross-module).
        }
        var xCands = _refs?.OwnerlessInlineCandidates(name, pc, ga);
        if (xCands != null)
        {
            var hit = InlineBirStash.SelectByParamSig(xCands, paramSig, out int mc);
            if (hit != null) return (hit, false, null);
            return (null, false, $"owner-less callInline: no unique paramSig match among {xCands.Count} kotlin.* candidate(s) ({mc} matched) — structurally-ambiguous overloads (e.g. differ only in generic bounds like ifEmpty)");
        }
        return (null, false, "owner-less callInline: no file class (same-module stash or ref.dll) hosts this inline fn");
    }

    // §4.3 — rewrite a member-inline payload's own `{k:this}` to a bound dispatch-receiver temp. A `typeDef`/`newSuspendLambda`
    // (its body + `this` are its OWN scope) is skipped WHOLE; a `newClosure`/`newSam`'s SAM/invoke body lives in `synthClass`
    // (its `this` is the closure's own) so only the `synthClass` KEY is skipped — the node's `captures` are STILL descended,
    // so a capture VALUE `{k:this}` (a lambda that captured the ENCLOSING receiver) is correctly rebound to the dispatch temp.
    static void RewriteThis(JsonNode node, string thisTemp)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) is "typeDef" or "newSuspendLambda") return;
            if (Str(o["k"]) == "this")
            {
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                o["k"] = "local";
                o["name"] = thisTemp;
                return;
            }
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") RewriteThis(kv.Value, thisTemp);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RewriteThis(c, thisTemp);
    }

    // §4.4(i) — FORWARDING: a lambda param passed BY NAME into a NESTED call that is itself a stdlib inline fn. stdlib
    // `map`'s raw payload is `return mapTo(__self, ArrayList(...), transform)` — a PLAIN owner-less `callStatic` to the
    // inline `mapTo`, `transform` forwarded by name (_Collections.kt:1559, verified against the ref.dll payload). Because
    // the caller's lambda ESCAPES, `mapTo` must ALSO be spliced, so we CONVERT that callStatic into an owner-less
    // `callInline` (recvs.extension + regular args, the forwarded lambda local -> the caller's carrier) and let STEP 8's
    // fixpoint splice it where `mapTo` invokes `transform`. An already-formed nested `callInline` arg is handled directly.
    // A forward we cannot resolve to a stdlib inline fn is LEFT as-is -> the D3-remainder fail-loud (never a silent drop).
    static void ForwardLambdaArgs(JsonNode node, Dictionary<string, JsonObject> lambdaMap)
    {
        if (lambdaMap.Count == 0 || node == null) return;
        if (node is JsonObject o)
        {
            // BATCH B (#75): a `newSuspendLambda` is a BOUNDARY — a lambda-param forwarded to a call INSIDE the SM body
            // must not be rewritten in the caller frame (it is a suspend value captured by the SM); the §4.4ii materialize
            // loop turns the surviving capture into a real suspend SM value. Symmetric with SpliceLambdaInvokes.
            if (Str(o["k"]) == "newSuspendLambda") return;
            var k = Str(o["k"]);
            // (braces MANDATORY: without them the `else if` dangles onto the inner for-loop `if` and never runs.)
            if (k == "callInline" && o["args"] is JsonArray cargs)
            {
                for (int i = 0; i < cargs.Count; i++)
                    if (cargs[i] is JsonObject ao && Str(ao["k"]) == "local"
                        && Str(ao["name"]) is string ln && lambdaMap.TryGetValue(ln, out var lam))
                        cargs[i] = lam.DeepClone();
            }
            // F3 (#62): a lambda param forwarded BY NAME into a nested inline call kotc emitted as a PLAIN call — an
            // owner-less kotlin.* `callStatic` (map->mapTo), an owner-FUL `callStatic` (a facadegen-injected / user
            // top-level inline fn), or a `callInstance` (a member inline fn — carries `recv` as the dispatch receiver).
            // Convert it into a `callInline` so STEP 8's fixpoint splices it; an unresolvable target is LEFT as-is
            // (D3-remainder / §4.4ii materialize handles a non-inline forward — never a silent drop).
            else if ((k == "callStatic" || k == "callInstance")
                     && o["args"] is JsonArray sargs
                     && sargs.Any(a => a is JsonObject so && Str(so["k"]) == "local" && Str(so["name"]) is string n && lambdaMap.ContainsKey(n)))
                TryForwardCall(o, k, lambdaMap);
            foreach (var kv in o) if (kv.Value != null) ForwardLambdaArgs(kv.Value, lambdaMap);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) ForwardLambdaArgs(c, lambdaMap);
    }

    // F3 (#62): convert a PLAIN nested call (a lambda param forwarded into it) into a `callInline`, mirroring kotc's inline
    // node shape so RewriteGeneric splices it at STEP 8's fixpoint. `k` selects the source shape:
    //   - `callStatic`, no owner     — an owner-less `kotlin.*` stdlib inline fn (apply/let/also/run/with, or map->mapTo):
    //     resolve across kotlin.* hosts via ResolveOwnerless (SAME-MODULE stash first, then the ref.dll), stay owner-less
    //     (RewriteGeneric re-resolves by paramSig at the fixpoint). A user top-level inline forward (`outer(b)=inner(b)`)
    //     is owner-less too (kotc emits its `owner` as the file-facade class only when named; a same-module top-level
    //     inline forward resolves owner-less by name via the stash).
    //   - `callStatic`, owner present — an owner-FUL static call (a companion/object/enum member flattened to a static,
    //     whose `owner` is a STRUCTURED Fqn): resolve via the OWNER-FUL ResolveInlinePayload (NOT the kotlin.*-gated
    //     ResolveOwnerless), keep the owner. `TypeJson.OwnerName` reads the Fqn (or a legacy string) uniformly.
    //   - `callInstance`             — a MEMBER inline fn: owner = the receiver's type name; carry the node's dispatch
    //     receiver (`recv`) as `recvs.dispatch` so RewriteGeneric §4.3 rebinds the payload's `{k:this}` (rides F1). A
    //     member-EXTENSION (#23) payload is left untouched (see the dispatch-drop guard below).
    // The overload is disambiguated by the call's `sig` — the callee's DECLARED param type nodes (kotc emits `sig` from
    // `birType(param.type)`, identical to the payload's declared `params[i].type`), which become the new callInline's
    // `paramSig` (§4.2). pc = arg count, ga = typeArg count. An extension callee (recv==extensionParam) takes args[0] as
    // recvs.extension; the remaining args become regular args, with a forwarded lambda local swapped for the caller's
    // carrier. A target that does not resolve to a UNIQUE inline payload is LEFT untouched (§4.4ii materializes a real
    // delegate for a non-inline forward; a genuinely-unspliceable inline shape falls to the D3-remainder fail-loud).
    static void TryForwardCall(JsonObject o, string k, Dictionary<string, JsonObject> lambdaMap)
    {
        var name = Str(o["method"]);
        var sargs = o["args"] as JsonArray;
        if (name == null || sargs == null) return;
        var sig = o["sig"] as JsonArray;
        var typeArgs = o["typeArgs"] as JsonArray;
        int pc = sargs.Count;
        int ga = typeArgs?.Count ?? 0;

        JsonObject payload;
        string ownerOut;
        if (k == "callInstance")
        {
            var ownerName = TypeJson.OwnerName(o["ownerType"]);
            if (ownerName == null) return;
            if (ResolveInlinePayload(ownerName, name, pc, ga, sig).payload is not JsonObject mp) return;
            payload = mp; ownerOut = ownerName;
        }
        // An owner-FUL callStatic carries its owner as a STRUCTURED Fqn (companion/object/enum member flattened to a
        // static; `TypeJson.OwnerName` reads both an Fqn object AND a legacy string, and yields null for the genuinely
        // owner-LESS kotlin.* scope-fn shape (`"owner":null`)). Keying on `Str(o["owner"])` alone would miss the Fqn
        // object -> mis-route a companion/object call into the owner-less kotlin.*-only resolver (a wrong-owner splice
        // by name coincidence). So: owner-ful iff OwnerName is non-null.
        else if (TypeJson.OwnerName(o["owner"]) is string owner)   // owner-FUL callStatic
        {
            if (ResolveInlinePayload(owner, name, pc, ga, sig).payload is not JsonObject op) return;
            payload = op; ownerOut = owner;
        }
        else   // owner-LESS callStatic (kotlin.* stdlib inline fn)
        {
            if (ResolveOwnerless(name, pc, ga, sig).hit is not JsonObject lp) return;
            payload = lp; ownerOut = null;
        }
        var recv = Str(payload["recv"]);

        // #23 guard: a member-EXTENSION callInstance carries BOTH a dispatch receiver (`o["recv"]`) and the extension
        // receiver (`args[0]`), but the stash classifies its payload `recv=="extensionParam"` (extension SHADOWS dispatch,
        // InlineBirStash.StashMethod) — so converting here would bind the extension and SILENTLY DROP the dispatch receiver
        // (RewriteGeneric §4.3 binds a dispatch temp only for `recv=="dispatch"`). If the payload body reads that dispatch
        // `this`, the drop is a silent miscompile. Leave it untouched (§4.4ii materializes a sound real delegate) — the
        // consumer twin of kotc's producer `bodyReferencesDispatch` fail-loud, pending the #23 2-slot receiver model.
        if (k == "callInstance" && recv == "extensionParam" && o["recv"] != null && HasNode(payload["body"], "this")) return;

        var recvs = new JsonObject();
        var callArgs = new JsonArray();
        int start = 0;
        if (recv == "extensionParam") { recvs["extension"] = sargs[0]?.DeepClone(); start = 1; }
        else if (recv == "dispatch" && o["recv"] is JsonNode disp) recvs["dispatch"] = disp.DeepClone();
        for (int i = start; i < sargs.Count; i++)
        {
            if (sargs[i] is JsonObject ao && Str(ao["k"]) == "local" && Str(ao["name"]) is string ln && lambdaMap.TryGetValue(ln, out var lam))
                callArgs.Add(lam.DeepClone());
            else callArgs.Add(sargs[i]?.DeepClone());
        }
        var repl = new JsonObject
        {
            ["k"] = "callInline",
            ["callee"] = name,
            ["owner"] = ownerOut,
            ["pc"] = pc,
            ["ga"] = ga,
            ["paramSig"] = sig?.DeepClone() ?? new JsonArray(),
            ["typeArgs"] = typeArgs?.DeepClone() ?? new JsonArray(),
            ["recvs"] = recvs,
            ["args"] = callArgs,
            ["retType"] = o["ret"]?.DeepClone(),
        };
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
        foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
    }

    // Splice invocations of a lambda param: `{k:callInstance, method:invoke, recv:{k:local,name:<lamParam>}}` where
    // <lamParam> is a registered lambda -> the carried lambda body, freshened+prefixed PER INVOCATION, with the lambda's
    // own param(s) bound to temps initialized from the invoke args. A bare `{k:return}` inside stays the caller's NLR.
    static void SpliceLambdaInvokes(JsonNode node, Dictionary<string, JsonObject> lambdaMap, JsonObject hostCarrier = null)
    {
        if (node is JsonObject o)
        {
            // BATCH B (#75): a `newSuspendLambda` is a BOUNDARY — a lambda-param invoked INSIDE the SM body is a suspend
            // VALUE captured by the SM; splicing the caller-scope carrier body into the SM's execution context would
            // dangle caller-frame locals. Leaving the invoke un-spliced lets the §4.4ii materialize loop turn the
            // surviving capture into a real suspend SM value (the correct crossinline semantics).
            if (Str(o["k"]) == "newSuspendLambda") return;
            // E7: track the NEAREST-ENCLOSING inlineLambda carrier as the host for capture propagation below.
            if (Str(o["k"]) == "inlineLambda") hostCarrier = o;
            foreach (var kv in o) if (kv.Value != null) SpliceLambdaInvokes(kv.Value, lambdaMap, hostCarrier);
            if (Str(o["k"]) == "callInstance" && Str(o["method"]) == "invoke"
                && o["recv"] is JsonObject rc && Str(rc["k"]) == "local"
                && Str(rc["name"]) is string ln && lambdaMap.TryGetValue(ln, out var lam))
            {
                // E7: a captured carrier spliced INSIDE a host inlineLambda frees ITS OWN captures into the host frame —
                // propagate them (verbatim descriptors) to the host's `captures` BEFORE the splice (lam's body is still
                // intact here), or they dangle when the host is later materialized (§4.4ii / MaterializeSuspendCarrier).
                if (hostCarrier != null) PropagateSplicedCaptures(lam, hostCarrier);
                var repl = BuildLambdaSplice(lam, o["args"] as JsonArray ?? new JsonArray());
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                foreach (var kv in repl) o[kv.Key] = kv.Value?.DeepClone();
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var c in a) if (c != null) SpliceLambdaInvokes(c, lambdaMap, hostCarrier);
        }
    }

    // E7 — the splice counterpart of the E5 descriptor↔ref lockstep. STEP 6 REPLACES a `<lamParam>()` invoke with the
    // carrier body wholesale, injecting the carrier's OWN captured refs (a genuine value capture like `klass` in
    // `filter { klass.isInstance(it) }`) into the HOST inlineLambda frame with NO matching host descriptor — so once the
    // host is lifted (a `newClosure` / suspend SM) they dangle (`references undeclared local`). Restore the invariant:
    // copy each spliced-carrier capture descriptor VERBATIM (name+type, tvs in the shared flattened frame) onto the host
    // `captures`, gated on the capture actually SURVIVING in the spliced body (a descriptor-only residue frees nothing —
    // keeps green shapes byte-identical) and dedup'd against the host's existing captures. A name the host ALREADY binds
    // (its own param/local) is SKIPPED: the freed ref resolves to that binding — the exact pre-E7 behavior. A type
    // conflict against a same-named host capture is the one unrepresentable case -> fail loud (#95 doctrine).
    static void PropagateSplicedCaptures(JsonObject lam, JsonObject host)
    {
        if (lam["captures"] is not JsonArray lamCaps || lamCaps.Count == 0) return;
        HashSet<string> hostScope = null;   // lazily built: host params + host-declared locals
        foreach (var c in lamCaps.OfType<JsonObject>())
        {
            if (Str(c["name"]) is not string cn || c["type"] is null) continue;
            bool outer = c["outer"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob;
            // Gate: only a capture the spliced body still references frees anything at this site (an `outer` capture's
            // freed ref is a bare `{k:this}`, so scan for that; a named capture -> a surviving `{k:local}`/write/desc).
            if (outer ? !(HasThisOutsideSynthClass(lam["body"]) || HasThisOutsideSynthClass(lam["result"]))
                      : !CarrierStillReferences(lam, cn)) continue;
            var hostCaps = host["captures"] as JsonArray;
            var existing = hostCaps?.OfType<JsonObject>().FirstOrDefault(h =>
                outer ? (h["outer"] is JsonValue hv && hv.TryGetValue<bool>(out var hb) && hb)
                      : Str(h["name"]) == cn);
            if (existing != null)
            {
                if (!outer && !JsonNode.DeepEquals(existing["type"], c["type"]))
                    throw new NotSupportedException($"inline splice: spliced carrier capture '{cn}' type-conflicts with the host carrier's same-named capture (E7)");
                continue;   // same enclosing entity in the flattened frame — already listed
            }
            if (!outer)
            {
                // The host already BINDS this name (its own param or a declared local): the freed body ref resolves to
                // that binding directly — the exact pre-E7 behavior (no propagation existed), which is correct when the
                // carrier captured that same host variable (the common flattened-frame case: e.g. a stdlib `index`
                // carried through a `forEachIndexed`-style host whose own loop var is `index`). SKIP — adding a
                // descriptor would double-bind it. (A genuine same-name-DIFFERENT-var collision is a pre-existing
                // hygiene concern, unchanged by E7 — the prefix pass disambiguates distinct splice-locals.)
                if (hostScope == null) { hostScope = InlineLambdaParamNames(host); CollectDeclaredLocals(host["body"], hostScope); }
                if (hostScope.Contains(cn)) continue;
            }
            if (hostCaps == null) { hostCaps = new JsonArray(); host["captures"] = hostCaps; }
            hostCaps.Add((JsonObject)c.DeepClone());
        }
    }

    static JsonObject BuildLambdaSplice(JsonObject lam, JsonArray invokeArgs)
    {
        int m = Interlocked.Increment(ref _counter);
        string prefix = "__inll" + m + "$";
        var lamParams = lam["params"] as JsonArray ?? new JsonArray();
        var lamBody = (lam["body"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
        var lamResult = lam["result"]?.DeepClone();

        // BATCH B (#75): a `newSuspendLambda` in this carrier whose descriptor names a carrier-declared local/param is now
        // renamed JOINTLY by PrefixLocalsJoint below — CollectDeclared skips the SM's inner scope and ApplyPrefix prefixes
        // the descriptor + its body capture-refs in lockstep with the carrier frame, so the SM field and the invokeSuspend
        // body ref stay aligned. Only a capture colliding with the SM's own inner scope stays unsound (FunGen name-
        // conflation); `__outer` is SOUND here (the carrier's captured `this` IS the caller's receiver) -> refuseOuter:false.
        if ((SuspendCaptureHazard(lamBody, refuseOuter: false) ?? (lamResult != null ? SuspendCaptureHazard(lamResult, refuseOuter: false) : null)) is string badDesc)
            throw new NotSupportedException($"inline splice: a carrier newSuspendLambda capture '{badDesc}' collides with the SM's own inner scope (FunGen name-conflation) — #75 Batch B");

        // D5: hygiene the carrier's `body` + `result` JOINTLY — the spliceBodyWithReturns carrier's `result` (a value-
        // producing `return@f`) is `{k:local,name:__inlRetN}` whose `var` decl + end-label live in `body`, so an
        // independent per-node map would leave the result naming an un-renamed local. One id-map + one declared-set,
        // applied to both. (The same body may be spliced N times — forEach3 invokes 3× — hence fresh per invocation.)
        FreshenLabelsJoint(lamBody, lamResult);
        PrefixLocalsJoint(prefix, lamBody, lamResult);

        var stmts = new JsonArray();
        var subst = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        for (int j = 0; j < lamParams.Count; j++)
        {
            if (lamParams[j] is not JsonObject lp) continue;
            string pn = Str(lp["name"]);
            string temp = prefix + pn;
            stmts.Add(new JsonObject
            {
                ["k"] = "var",
                ["name"] = temp,
                ["type"] = lp["type"]?.DeepClone(),
                ["init"] = invokeArgs.ElementAtOrDefault(j)?.DeepClone()
                           ?? new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null },
            });
            subst[pn] = new JsonObject { ["k"] = "local", ["name"] = temp };
        }
        RewriteLocalRefs(lamBody, subst);
        RewriteLocalRefs(lamResult, subst);

        foreach (var st in lamBody) if (st != null) stmts.Add(st.DeepClone());
        // FLATTEN a `valueBlock` result into THIS splice's own stmts. When the carrier's lambda has a VALUE-POSITION
        // valueBlock body (a `try`/`when`/`elvis`/`run` expression, e.g. `map { try { it.toInt() } catch { 0 } }` whose
        // kotc `tryExpr` result is `{k:valueBlock, stmts:[var tryval; try{…}], result:tryval}`), leaving it as the outer
        // `result` produces a NESTED valueBlock: TryValueOperandHoist then lifts the INNER stmts (`var tryval; try{…}`)
        // above, but the param binding `var __inllN$it = item` stays in the arg position — so the DECL becomes a sibling
        // AFTER the read (`load unknown var __inllN$it`). Splicing the inner stmts here keeps it single-layer + in order.
        while (lamResult is JsonObject ro && Str(ro["k"]) == "valueBlock")
        {
            if (ro["stmts"] is JsonArray rs) foreach (var st in rs) if (st != null) stmts.Add(st.DeepClone());
            if (ro["body"] is JsonArray rb) foreach (var st in rb) if (st != null) stmts.Add(st.DeepClone());
            lamResult = ro["result"]?.DeepClone();
        }
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = stmts,
            ["result"] = lamResult ?? new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null },
        };
    }

    // §4.4(ii) — build a `newClosure` VALUE from a lambda-param carrier (its params + body + result + kotc-emitted
    // `captures`) and bind it to a fresh temp in `stmts`; return the temp name (or null if unmaterializable). Mirrors
    // kotc's lambda() synthClass shape (fields=captures, invoke params/ret/body, generic typeParams over the tvs the reified
    // closure references) so ClosureSynthesis assembles the closure class exactly as for a source lambda.
    //
    // CAPTURES come DIRECTLY from the carrier (kotc lists them, typed in the ENCLOSING frame — a captured local's declared
    // type; `__outer` = the enclosing class type WITH its type args): each becomes a closure FIELD verbatim; the invoke
    // body's `{k:local,name:X}` -> `this.X`, `{k:this}` -> `this.__outer`; the ctor arg (spliced at the materialization
    // site, in the ENCLOSING scope) is the enclosing `{k:local,name:X}` / `{k:this}`. So the generic-class-member case is
    // handled by construction — no scope reconstruction. NON-MATERIALIZABLE (returns null -> caller fails loud): a
    // NON-LOCAL-return carrier (cannot be a delegate); a residual `{k:local}` kotc did not list (e.g. a nested-closure capture).
    [ThreadStatic] static string _matReason;
    static string MatNull(string code) { _matReason = code; return null; }

    static string MaterializeCarrier(JsonObject carrier, JsonNode funcType, JsonArray stmts)
    {
        if (funcType is not JsonObject ft || Str(ft["t"]) != "fn") return MatNull("MC:funcType-not-fn");   // no delegate type to build the closure against
        var lamParams = carrier["params"] as JsonArray ?? new JsonArray();
        var lamBody = (carrier["body"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
        var lamResult = carrier["result"]?.DeepClone();

        // A bare `{k:return}` OR expression-position `{k:returnExpr}` in the body OR the `result` cannot survive as a
        // closure invoke body — refuse (caller fails loud). bir2cir cannot prove from the node alone whether such a
        // return is non-local (targets the enclosing fn) or lambda-local: kotc's expr-position IrReturn does not yet
        // route through inlineReturnSubst, so a lambda-local labeled return can leak here as a raw `returnExpr`. Refusing
        // both is correct — fail-loud beats a silent caller-frame `ret` in the non-local case (the common one).
        if (HasNodeNonClosure(lamBody, "return") || HasNodeNonClosure(lamBody, "returnExpr")
            || (lamResult != null && (HasNodeNonClosure(lamResult, "return") || HasNodeNonClosure(lamResult, "returnExpr")))) return MatNull("MC:return-in-body");

        // BATCH B (#75 holistic) — SUSPEND carrier: a `{t:fn,suspend:true}` carrier in a non-invoke position must become
        // a real `newSuspendLambda` VALUE (a cold SM instance), NOT a plain `newClosure` delegate. The SM /
        // startSuspendUninterceptedOrReturn protocol (SuspendColdLowering / SuspendLambdaLowering) drives a suspend
        // lambda, not a delegate — minting a delegate here is a SILENT MISCOMPILE. Route to the suspend arm.
        if (ft["suspend"] is JsonValue sv && sv.TryGetValue<bool>(out var isSuspend) && isSuspend)
            return MaterializeSuspendCarrier(carrier, ft, lamParams, lamBody, lamResult, stmts);

        int n = Interlocked.Increment(ref _counter);
        var cname = "dotkt$inlmat" + n + "$Closure";

        // The invoke body = the carrier body statements, then `return <result>`. A SINGLE-EXPRESSION lambda (e.g.
        // `Iterable { this.iterator() }`) has body=[] and carries the whole value — INCLUDING its captured `this`/locals — in
        // `result`, so the synthetic `return` is where those refs live. ALL of materialization below (capture rewrite,
        // stray/declared scan, nested-closure guard, tv collection) operates on this full `invBody` so `result` is covered.
        var invBody = new JsonArray();
        foreach (var st in lamBody) if (st != null) invBody.Add(st.DeepClone());
        // FLATTEN a value-position `valueBlock` result (a `try`/`when`/`elvis`/`run` body) into the invoke stmts, then
        // return its inner result — same rationale as BuildLambdaSplice (avoid a nested valueBlock that TryValueOperandHoist
        // would split across the return).
        while (lamResult is JsonObject rvb && Str(rvb["k"]) == "valueBlock")
        {
            if (rvb["stmts"] is JsonArray rs) foreach (var st in rs) if (st != null) invBody.Add(st.DeepClone());
            if (rvb["body"] is JsonArray rb) foreach (var st in rb) if (st != null) invBody.Add(st.DeepClone());
            lamResult = rvb["result"]?.DeepClone();
        }
        // The invoke returns the funcType's ret. A void/Unit invoke (a `() -> Unit` lambda, e.g. `{ n += 10 }`) must NOT
        // `return <value>` — a value on the stack at a void `ret` is unverifiable IL (ilverify ReturnVoid). Evaluate a
        // non-const result for its side effect, then a BARE return; a non-void invoke returns the (coerced) result value.
        bool retVoid = TypeJson.Read(ft["ret"]) is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" } || ft["ret"] == null;
        if (retVoid)
        {
            if (lamResult is JsonObject lr && Str(lr["k"]) is string rk && rk != "const")
                invBody.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = lamResult });
            invBody.Add(new JsonObject { ["k"] = "return" });
        }
        else
            invBody.Add(new JsonObject { ["k"] = "return", ["value"] = lamResult ?? UnitConst() });

        // A NESTED `newClosure`/`newSam` in the carrier is materializable AS-IS: kotc emits it self-contained (its
        // `synthClass` is its OWN frame; already-field/param refs) with its CAPTURE VALUES (ctor args) living in THIS
        // carrier's scope. The sibling scans below (RewriteCapturesToFields, HasStrayLocal, the this-guard, CollectTvKeys/
        // RenumberTvs) all DESCEND into those capture values (skipping the nested `synthClass`), so a nested closure that
        // captures an invoke param (`cont`), a carrier capture (-> `this.<field>`), or a carrier local is bound correctly,
        // and one capturing anything else fails loud via HasStrayLocal. This is the `suspendCancellableCoroutine { cont ->
        // … cont.invokeOnCancellation { … } }` pattern (#22). But a nested `newSuspendLambda` (its SM ctor binds captures
        // by DESCRIPTOR NAME, which the splice's field/this rewrites do not touch -> field vs body-ref skew, FINDING 1), a
        // CROSS-MODULE `newDelegate` (a dangling origin-file `__lambdaN` type token, §4.6 — an APP-LOCAL one, e.g. a #34
        // re-hoisted `__dflt$lambda$N`, IS wrapped verbatim, #43), or an un-spliced `inlineLambda` (a nested inline-call
        // lambda arg, only handled at STEP 8's fixpoint) cannot be wrapped verbatim — refuse those.
        if (HasUnmaterializableNested(invBody)) return MatNull("MC:unmaterializable-nested");

        // Consume kotc's carrier `captures`: one closure FIELD each (verbatim {name,type}); ctor arg = the enclosing local
        // (`{k:local,name}`) or, for `outer:true`, the enclosing `{k:this}`. Field + ctor-arg order match (positional ctor).
        var fields = new JsonArray();
        var captures = new JsonArray();      // ctor arg VALUES — same order as `fields` (positional ctor in ClosureSynthesis)
        var capNames = new HashSet<string>(StringComparer.Ordinal);
        string outerName = null;
        if (carrier["captures"] is JsonArray caps)
            foreach (var c in caps.OfType<JsonObject>())
            {
                if (Str(c["name"]) is not string cn || c["type"] is not JsonNode ct) return MatNull("MC:capture-no-name-type");
                fields.Add(new JsonObject { ["name"] = cn, ["type"] = ct.DeepClone() });
                if (c["outer"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob)
                { outerName = cn; captures.Add(new JsonObject { ["k"] = "this" }); }   // enclosing `this`
                else { capNames.Add(cn); captures.Add(new JsonObject { ["k"] = "local", ["name"] = cn }); }
            }
        // A bare `{k:this}` (the enclosing receiver — a lambda has no `this` of its own) with NO `outer:true` capture listed
        // is an UNLISTED enclosing-`this` capture: fail loud (symmetric to the stray-local check; else the `{k:this}` would
        // survive into the invoke body and resolve to the CLOSURE instance — a silent miscompile). Scans the carrier's OWN
        // frame — the top-level body AND a nested closure's CAPTURE VALUES (a nested closure that captured the enclosing
        // receiver), skipping only a nested `synthClass` (its `this` is its own instance). Checked BEFORE the field rewrite
        // introduces legitimate `field.recv:{k:this}` nodes.
        if (outerName == null && HasThisOutsideSynthClass(invBody)) return MatNull("MC:this-outside-synthclass");
        // §4.4ii REF-CELL WRITE-THROUGH: a `setLocal` WRITING a captured enclosing var (X ∈ capNames) is a `{k:setLocal}`
        // WRITE that the `{k:local}`-READ scans miss. kotc ref-cell-boxes a mutated capture BEFORE it reaches here, so this
        // shape only survives when kotc did NOT box (e.g. a cross-module inline body's callee-local). Promote each such X to
        // a shared heap cell `dotkt$…$Ref$<elem>{ var v }`: the closure captures the CELL (field typed as the Ref class), the
        // carrier body's X reads/writes go through `this.<X>.v`, and a post-pass (BoxMaterializedCaptures) boxes X's decl +
        // uses in the ENCLOSING scope. Mirrors kotc's own ref-cell path (BirEmitterStatements.stmt), one axis over.
        var boxedCaps = new HashSet<string>(StringComparer.Ordinal);
        CollectWrittenCaptures(invBody, capNames, boxedCaps);
        // A written capture whose element type references an enclosing type variable would bake a raw `tv` into the
        // non-generic Ref cell's field (a BadImageFormat, the same hazard the closure-frame tv scan guards) — refuse loud.
        if (boxedCaps.Count > 0)
            foreach (var f in fields.OfType<JsonObject>())
                if (Str(f["name"]) is string fn && boxedCaps.Contains(fn) && HasTv(f["type"])) return MatNull("MC:boxed-cap-has-tv");
        // Non-boxed caps first (B1): RewriteCapturesToFields rewrites a bare enclosing `{k:this}` to `this.__outer` when
        // outerName!=null — it MUST run BEFORE the boxed rewrite, else it would also rewrite the closure-instance `{k:this}`
        // the boxed `this.<X>.v` recv carries (turning it into `this.__outer.<X>.v`). Boxed names are excluded here.
        var plainCaps = boxedCaps.Count == 0 ? capNames : new HashSet<string>(capNames.Where(c => !boxedCaps.Contains(c)), StringComparer.Ordinal);
        RewriteCapturesToFields(invBody, plainCaps, outerName, cname);
        if (boxedCaps.Count > 0)
        {
            var boxRefName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in fields.OfType<JsonObject>())
            {
                if (Str(f["name"]) is not string fn || !boxedCaps.Contains(fn)) continue;
                var elem = f["type"];   // the capture's plain type = the cell's element type
                var refName = RefCellNameFor(elem);
                boxRefName[fn] = refName;
                f["type"] = TypeJson.Fqn(refName);            // the closure FIELD now holds the Ref cell
                if (!_boxRequests.Any(r => r.varName == fn && r.refName == refName))
                    _boxRequests.Add((fn, refName, elem.DeepClone()));   // DISTINCT enclosing-scope box request (flushed at Apply)
            }
            RewriteBoxedCaptureInCarrier(invBody, boxedCaps, boxRefName, cname);
        }
        // Any residual `{k:local}` that is neither an invoke param NOR one of the carrier's OWN declared locals is a capture
        // kotc did not list -> fail loud rather than leak an unbound local to ilemit.
        var allowed = new HashSet<string>(lamParams.OfType<JsonObject>().Select(p => Str(p["name"])).Where(x => x != null), StringComparer.Ordinal);
        CollectDeclaredLocals(invBody, allowed);
        if (HasStrayLocal(invBody, allowed)) return MatNull("MC:stray-local");

        // Generic-ness: the closure must be generic over every tv its funcType/params/ret/body/FIELDS reference (reified CLR
        // generics — an unresolved tv is a BadImageFormat). Key tvs by (SCOPE, index): kotc numbers method type-params
        // independently of type (class) type-params, so `tv{method,0}` and `tv{type,0}` are DISTINCT enclosing params and
        // must map to DISTINCT closure params. Renumber the closure-frame refs to a 0-based space, declare that many
        // typeParams, and pass the ORIGINAL enclosing tvs (scope+index) as `typeArgs`. The `newClosure.funcType` stays the
        // CALLER-frame `ft` (un-renumbered) — ilemit resolves it in the caller, `typeArgs` bind the closure params to it.
        var invParams = (JsonArray)lamParams.DeepClone();
        var invRet = ft["ret"]?.DeepClone() ?? TypeJson.Fqn("kotlin.Unit");
        var keys = new SortedSet<(string scope, int i)>();
        CollectTvKeys(invBody, keys); CollectTvKeys(invParams, keys); CollectTvKeys(invRet, keys);
        CollectTvKeys(ft, keys); CollectTvKeys(fields, keys);
        var remap = new Dictionary<(string, int), int>();
        var typeArgs = new JsonArray();
        var typeParams = new JsonArray();
        foreach (var key in keys)
        {
            int ni = remap.Count;
            remap[key] = ni;
            typeArgs.Add(new JsonObject { ["t"] = "tv", ["scope"] = key.scope, ["i"] = key.i });
            typeParams.Add("T" + ni);
        }
        if (remap.Count > 0)
        {
            RenumberTvs(invBody, remap); RenumberTvs(invParams, remap); RenumberTvs(invRet, remap); RenumberTvs(fields, remap);
        }

        var synthClass = new JsonObject
        {
            ["name"] = cname,
            ["fields"] = fields,
            ["params"] = invParams,
            ["ret"] = invRet,
            ["body"] = invBody,
        };
        if (typeParams.Count > 0) synthClass["typeParams"] = typeParams;

        var newClosure = new JsonObject
        {
            ["k"] = "newClosure",
            ["closureType"] = TypeJson.Fqn(cname),
            ["captures"] = captures,
            ["method"] = "invoke",
            ["funcType"] = ft.DeepClone(),
            ["synthClass"] = synthClass,
        };
        if (typeArgs.Count > 0) newClosure["typeArgs"] = typeArgs;

        var matTemp = "__inlmat" + n;
        stmts.Add(new JsonObject { ["k"] = "var", ["name"] = matTemp, ["type"] = ft.DeepClone(), ["init"] = newClosure });
        return matTemp;
    }

    // BATCH B (#75) — §4.4ii SUSPEND arm: materialize a `suspend`-typed lambda-param carrier into a real
    // `newSuspendLambda` VALUE bound to a fresh temp; return the temp name (or null if unmaterializable). Mirrors the
    // BYTE SHAPE kotc mints for a source suspend-lambda literal (BirEmitterLifts.suspendLambda:142-165) so it flows
    // through SuspendLambdaLowering (the LIVE `newSuspendLambda` consumer, which runs AFTER InlineSplice in the pass
    // order) IDENTICALLY:
    //  - CAPTURES come DIRECTLY from the carrier (kotc lists them; `__outer` = a captured enclosing `<this>`) and are
    //    emitted VERBATIM as {name,type} descriptors. The SM builder (SuspendColdLowering.FunGen) makes each a ctor-set
    //    FIELD and rewrites the body's plain `{k:local,name:X}` / `{k:this}` into field reads ITSELF — so, UNLIKE the
    //    newClosure arm, we do NOT RewriteCapturesToFields and do NOT run the `this`-outside-synthClass guard (the SM
    //    lowering redirects a lambda-body `{k:this}` to the `__outer` field). The construction VALUES are synthesized by
    //    SuspendLambdaLowering from each capture NAME (read as the enclosing local `{k:local,name}` / `this`).
    //  - the invoke BODY = the carrier statements + a value-position valueBlock flatten + a trailing `return <result>`
    //    (Unit -> a bare return), the SAME shape the newClosure arm builds; the SM lowering routes the returns.
    //  - typeParams = the DISTINCT enclosing tv keys the SM references — one placeholder name each. This is BYTE-faithful
    //    to kotc's freeTypeParams (a subset in encounter order, body tvs LEFT at their enclosing indices): both kotc and
    //    SuspendLambdaLowering resolve the open SM's tvs POSITIONALLY (ilemit ResolveTv is by index, name-agnostic), so
    //    the placeholder names never matter. Target inline-splice cases have a non-generic caller -> typeParams = [].
    static string MaterializeSuspendCarrier(JsonObject carrier, JsonObject ft, JsonArray lamParams, JsonArray lamBody, JsonNode lamResult, JsonArray stmts)
    {
        int n = Interlocked.Increment(ref _counter);

        // invoke body = carrier stmts + flatten a value-position valueBlock result + trailing return(result).
        var invBody = new JsonArray();
        foreach (var st in lamBody) if (st != null) invBody.Add(st.DeepClone());
        while (lamResult is JsonObject rvb && Str(rvb["k"]) == "valueBlock")
        {
            if (rvb["stmts"] is JsonArray rs) foreach (var st in rs) if (st != null) invBody.Add(st.DeepClone());
            if (rvb["body"] is JsonArray rb) foreach (var st in rb) if (st != null) invBody.Add(st.DeepClone());
            lamResult = rvb["result"]?.DeepClone();
        }
        bool retVoid = TypeJson.Read(ft["ret"]) is TypeNode.Fqn { Args: null, Name: "void" or "kotlin.Unit" } || ft["ret"] == null;
        if (retVoid)
        {
            if (lamResult is JsonObject lr && Str(lr["k"]) is string rk && rk != "const")
                invBody.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = lamResult });
            invBody.Add(new JsonObject { ["k"] = "return" });
        }
        else
            invBody.Add(new JsonObject { ["k"] = "return", ["value"] = lamResult ?? UnitConst() });

        // A nested `newSuspendLambda` / `newClosure` / `newSam` in the body is materializable AS-IS (the SM lowering
        // recurses bottom-up over nested suspend lambdas; a nested closure is self-contained; a nested `inlineLambda`
        // rides under a `callInline` that STEP 8's fixpoint splices INSIDE the SM frame). A `newDelegate` is refused
        // ONLY when it dangles cross-module (§4.6, #43): a same-module target — e.g. a __dflt$lambda$N a nested
        // member-inline default-fill re-hoisted app-local — is a capture-less static that wraps verbatim (this is what
        // unblocks the `suspendCancellableCoroutineReusable` §4.4ii suspend carriers). A cross-module token fails loud.
        if (HasNonAppLocalDelegate(invBody)) return MatNull("MSC:non-applocal-delegate");

        // Captures verbatim (drop the carrier's `outer` flag — the name `__outer` is itself the enclosing-`this` signal
        // SuspendLambdaLowering keys on; a carrier `__outer` is SOUND at THIS materialization site — the carrier's
        // captured `this` IS the caller's receiver). A capture with no name/type kotc failed to list -> refuse.
        var captures = new JsonArray();
        var innerScope = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in lamParams.OfType<JsonObject>()) if (Str(p["name"]) is string pn) innerScope.Add(pn);
        CollectDeclaredLocals(invBody, innerScope);
        if (carrier["captures"] is JsonArray caps)
            foreach (var c in caps.OfType<JsonObject>())
            {
                if (Str(c["name"]) is not string cn || c["type"] is not JsonNode ct) return MatNull("MSC:capture-no-name-type");
                if (innerScope.Contains(cn)) return MatNull("MSC:innerscope-collide");   // FunGen name-conflation (a capture colliding with the SM's own scope)
                captures.Add(new JsonObject { ["name"] = cn, ["type"] = ct.DeepClone() });
            }

        // A suspend carrier that WRITES a captured enclosing var is a SILENT stale-value miscompile: SuspendColdLowering
        // rewrites a `setLocal`-to-capture into a `setField` on the SM's OWN copy field, so the enclosing local never sees
        // the write. Ref-cell write-through for the suspend arm is a follow-up (the SM would have to capture the cell, not a
        // copy) — until then, refuse loud rather than miscompile. (The non-suspend newClosure arm above DOES box.)
        {
            var suspendCapNames = new HashSet<string>(captures.OfType<JsonObject>().Select(c => Str(c["name"])).Where(n => n != null), StringComparer.Ordinal);
            var written = new HashSet<string>(StringComparer.Ordinal);
            CollectWrittenCaptures(invBody, suspendCapNames, written);
            if (written.Count > 0) return MatNull("MSC:written-capture");
        }

        // TV FRAME (#75 Batch B, 2A): renumber every enclosing tv the SM references to a DENSE 0-based space, declare
        // that many placeholder typeParams, and pass the ORIGINAL enclosing tvs (ANY scope/index) as construction
        // `typeArgs` — the SAME mechanism the non-suspend newClosure arm (MaterializeCarrier :885-922) already uses.
        // This DISSOLVES the former single-scope-0..N-1-prefix limitation: SuspendLambdaLowering consumes `typeArgs`
        // to instantiate `new smName<origTvs…>(…)` instead of the positional `smName<tv{type,0..N-1}>` fallback. A
        // member-sig tv (e.g. `FlowCollector<T>`'s `tv{type,0}` on an `emit` call) is renumbered + carried as a
        // construction typeArg that resolves to `object` at the (non-generic) construction site but is NEVER consulted
        // — ilemit re-resolves the receiver call against the field's static type. IDENTICAL soundness to newClosure.
        // Names are cosmetic — ilemit/SuspendLambdaLowering resolve tvs POSITIONALLY (ResolveTv is by index).
        var invParams = (JsonArray)lamParams.DeepClone();
        var invSuspendRet = ft["ret"]?.DeepClone() ?? TypeJson.Fqn("kotlin.Unit");
        var keys = new SortedSet<(string scope, int i)>();
        CollectTvKeys(invBody, keys); CollectTvKeys(invParams, keys); CollectTvKeys(invSuspendRet, keys); CollectTvKeys(captures, keys);
        var remap = new Dictionary<(string, int), int>();
        var ctorTypeArgs = new JsonArray();
        var typeParams = new JsonArray();
        foreach (var key in keys)
        {
            int ni = remap.Count;
            remap[key] = ni;
            ctorTypeArgs.Add(new JsonObject { ["t"] = "tv", ["scope"] = key.scope, ["i"] = key.i });
            typeParams.Add("Tsm" + ni);
        }
        // A NESTED kotc-emitted `newSuspendLambda` inside this carrier body keeps its OWN `typeParams` and resolves its
        // body tvs POSITIONALLY against them — so CollectTvKeys/RenumberTvs SHIELD its own frame (body/params/suspendRet/
        // typeParams) exactly like a `synthClass`, descending only into its outer-frame refs (captures/typeArgs/funcType).
        // Its body indices are thereby left intact (no positional drift), and the outer SM still collects the enclosing
        // tvs it needs from the nested captures' types — so a non-identity outer remap is sound for the flow shapes.
        //
        // Soundness is NOT unconditional, so replace the old blanket F3 refusal (Fable #75) with TWO narrow invariant
        // guards. A kotc-emitted nested NSL (no explicit ctor `typeArgs`) is instantiated by SuspendLambdaLowering via the
        // POSITIONAL fallback `nestedSM<tv{type,0..N-1}>` in THIS SM's frame, binding nested param i to this SM's param
        // i = the i-th smallest key.
        //   (i) BODY resolution: the nested body/typeParams (positional) get the right enclosing tv ONLY if `(method,
        //       0..N-1)` are all present (SortedSet order then pins dense slots 0..N-1 to them). A gapped/non-prefix set
        //       would bind a nested body tv to the wrong reified type -> refuse LOUD (MSC:nested-sm-nonprefix).
        //   (ii) A BARE-tv capture (`{t:tv}` VERBATIM as the field type) that the outer remap SHIFTS would make the
        //       nested SM's field/ctor-param type — resolved positionally against ITS own typeParams — a wrong reified
        //       (possibly VALUE) type -> refuse LOUD (MSC:nested-sm-bare-tv-capture-shift). A shift buried inside a
        //       NON-bare (composite) capture — `fn`/`fqn<…>`/`array`/`nullable` — is only ever fed to ilemit's
        //       positional `ResolveTv`, whose out-of-range fallback is `object` (check-i pins slots 0..N-1 to (method,
        //       0..N-1), so a shift necessarily lands >= N). So the nested field degrades to the monomorphic erasure
        //       `Comp<…,object,…>`, NEVER a wrong in-range reified type: it is either uniformly `object` on BOTH sides
        //       (a suspend-`fn` slot — BirTypeLowering erases `sfunc:`→object — so the construction and field agree,
        //       ilverify-clean; the rc6 case, incl. the gapped `[(m,0),(m,1),(m,3)]` combineTransform carrier whose only
        //       shift `(m,3)` rides inside a suspend-`fn`) OR a reference/value mismatch that ilverify REJECTS LOUD at
        //       the nested `newobj` (a NEW-FAIL, never a silent miscompile). (Fable #75: a future reified-composite shift
        //       thus redlines the ilverify lane — it must NOT be waived into the #12/#46 covariance-erasure XFAIL class
        //       without the #74/#46 resolved-identity representation fix, which its value-binding variants also need.)
        foreach (var nsm in FirstLevelNestedSuspendLambdas(invBody))
        {
            if (nsm["typeArgs"] is JsonArray) continue;   // an InlineSplice-materialized nested NSL carries explicit ctorTypeArgs — always fine
            int nN = (nsm["typeParams"] as JsonArray)?.Count ?? 0;
            for (int i = 0; i < nN; i++)
                if (!keys.Contains(("method", i))) return MatNull("MSC:nested-sm-nonprefix");
            if (nsm["captures"] is JsonArray ncaps)
                foreach (var c in ncaps.OfType<JsonObject>())
                    if (Str(c["type"]?["t"]) == "tv" && c["type"] is JsonObject ctv
                        && remap.TryGetValue((Str(ctv["scope"]) ?? "method", Int(ctv["i"])), out var ni2) && ni2 != Int(ctv["i"]))
                        return MatNull("MSC:nested-sm-bare-tv-capture-shift");
        }
        if (remap.Count > 0)
        {
            RenumberTvs(invBody, remap); RenumberTvs(invParams, remap); RenumberTvs(invSuspendRet, remap); RenumberTvs(captures, remap);
        }

        var newSuspendLambda = new JsonObject
        {
            ["k"] = "newSuspendLambda",
            ["arity"] = lamParams.Count,
            ["captures"] = captures,
            ["params"] = invParams,
            ["suspendRet"] = invSuspendRet,
            ["typeParams"] = typeParams,
            ["body"] = invBody,
            ["funcType"] = ft.DeepClone(),
        };
        if (ctorTypeArgs.Count > 0) newSuspendLambda["typeArgs"] = ctorTypeArgs;

        var matTemp = "__inlmat" + n;
        stmts.Add(new JsonObject { ["k"] = "var", ["name"] = matTemp, ["type"] = ft.DeepClone(), ["init"] = newSuspendLambda });
        return matTemp;
    }

    // Rewrite the closure invoke body: a captured `{k:local,name:X}` (X in `capNames`) -> `this.X` field read; the enclosing
    // `{k:this}` -> `this.<outerName>` field read (when the carrier captured `this`, `outerName != null`). Replaces the node
    // at its slot (does NOT recurse into the built replacement's `{k:this}` recv), and does NOT descend into a nested
    // lambda/closure (its `X`/`this` are its own).
    static void RewriteCapturesToFields(JsonNode node, HashSet<string> capNames, string outerName, string cname)
    {
        JsonNode FieldOf(string fn) => new JsonObject
        {
            ["k"] = "field",
            ["ownerType"] = TypeJson.Fqn(cname),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = fn,
        };
        bool IsCapLocal(JsonNode v) => v is JsonObject lo && Str(lo["k"]) == "local" && Str(lo["name"]) is string ln && capNames.Contains(ln);
        bool IsOuterThis(JsonNode v) => outerName != null && v is JsonObject to && Str(to["k"]) == "this";
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
                if (IsCapLocal(a[i])) a[i] = FieldOf(Str(((JsonObject)a[i])["name"]));
                else if (IsOuterThis(a[i])) a[i] = FieldOf(outerName);
                else if (a[i] != null) RewriteCapturesToFields(a[i], capNames, outerName, cname);
            return;
        }
        if (node is not JsonObject o) return;
        // A nested closure/SAM's `synthClass` is its OWN frame — its `{k:local}`/`{k:this}` are already field/param refs of
        // that frame, so must NOT be rewritten. But DESCEND into the node's OTHER keys (notably `captures` — the ctor-arg
        // VALUES evaluate in THIS carrier's scope, so a value capturing an outer-carrier capture / the enclosing `this`
        // must be rewritten to `this.<field>` here, exactly as at the carrier's top level; #22). The materialized closure's
        // sibling scans stay consistent with this same skip-synthClass/descend-captures boundary.
        bool nested = Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam";
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys))
        {
            if (nested && key == "synthClass") continue;
            if (IsCapLocal(o[key])) o[key] = FieldOf(Str(((JsonObject)o[key])["name"]));
            else if (IsOuterThis(o[key])) o[key] = FieldOf(outerName);
            else if (o[key] != null) RewriteCapturesToFields(o[key], capNames, outerName, cname);
        }
    }

    // True if the subtree still holds a `{k:local,name:X}` with X ∉ `allowed` (a capture kotc did not list) — not descending
    // into a nested lambda/closure (its locals are its own).
    static bool HasStrayLocal(JsonNode node, HashSet<string> allowed)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "local") return Str(o["name"]) is string ln && !allowed.Contains(ln);
            // Descend into a nested closure's CAPTURE VALUES (they resolve in THIS carrier's scope — a stray there is a
            // genuine unlisted capture, e.g. a nested closure grabbing an enclosing-suspend-fn local kotc didn't list on
            // the carrier) but NOT its `synthClass` body (its locals are its own frame).
            bool nested = Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam";
            foreach (var kv in o) if (kv.Value != null && (!nested || kv.Key != "synthClass") && HasStrayLocal(kv.Value, allowed)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasStrayLocal(c, allowed)) return true;
        return false;
    }

    // Add every name a subtree BINDS into `set` (the carrier's OWN locals — valid `{k:local}` targets). Beyond a `{k:var}`
    // declaration, a LOOP/ITERATOR node binds its element in a `"var"` field, NOT a `{k:var}` statement — a spliced inner
    // inline-EXTENSION iterator (`arr.forEach { … }`) lowers to a `forArray`/`forIn` whose `"var"` element then flows into
    // the lambda-param temp (`var __it = __element`), so the `{k:local,name:__element}` ref must count as a declared local
    // (else MaterializeCarrier's HasStrayLocal wrongly reports it as an unlisted capture — the #22 forEach residual). The
    // binder set is kept IDENTICAL to CollectDeclared (the PrefixLocals-hygiene scanner) — a binder PrefixLocals renames
    // must be an accepted local here, and vice versa: `forIn`/`forArray`/`repeatInline`/`callInline` `"var"` + try-catch
    // `"var"` (the only binders kotc/pre-InlineSplice passes emit; `for`/`forRange`/`forEachInline` are minted only by the
    // LATER lowering passes). Does not descend into a nested lambda/closure (its locals are its own scope).
    static void CollectDeclaredLocals(JsonNode node, HashSet<string> set)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam") return;
            if (k == "var" && Str(o["name"]) is string vn) set.Add(vn);
            if ((k is "forIn" or "forArray" or "repeatInline" or "callInline") && Str(o["var"]) is string fv) set.Add(fv);
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv) set.Add(cv);
            foreach (var kv in o) if (kv.Value != null) CollectDeclaredLocals(kv.Value, set);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectDeclaredLocals(c, set);
    }

    // BATCH B (#75) — a `newSuspendLambda`'s OWN INNER scope: its own params + the locals its body declares (the SM's
    // private names). A body `{k:local,name:X}` with X in this set is the SM's OWN — the splice's frame renamers (prefix
    // / subst) must SKIP it; X ∉ this set is a CAPTURE ref (an enclosing name) they rename in lockstep with the matching
    // `captures[].name` descriptor. CollectDeclaredLocals stops at nested fn boundaries, so this is exactly this SM's frame.
    static HashSet<string> SuspendLambdaInnerScope(JsonObject nsl)
    {
        var inner = new HashSet<string>(StringComparer.Ordinal);
        if (nsl["params"] is JsonArray ps)
            foreach (var p in ps.OfType<JsonObject>()) if (Str(p["name"]) is string pn) inner.Add(pn);
        if (nsl["body"] is JsonNode b) CollectDeclaredLocals(b, inner);
        // BATCH B (#75) 2B — kotc names a suspend lambda's captured enclosing extension receiver `__outer` in the
        // capture DESCRIPTOR, yet references it in the BODY as a plain `local __self` (SuspendColdLowering maps that
        // body `__self` -> the `__outer` field). So `__self` is a CAPTURE-LINKED inner name, NOT a frame local — the
        // splice's frame renamers (subst/prefix) must SKIP it, leaving it literal for the SM lowering; its construction
        // value is rebound to the splice receiver temp via BindOuterCapValues. Only when an `__outer` descriptor is
        // present AND there is no real `__self` descriptor (which would be an ordinary joint-renamed capture).
        if (nsl["captures"] is JsonArray caps)
        {
            var names = caps.OfType<JsonObject>().Select(c => Str(c["name"])).Where(n => n != null).ToHashSet(StringComparer.Ordinal);
            if (names.Contains("__outer") && !names.Contains("__self")) inner.Add("__self");
        }
        return inner;
    }

    // F2 (#61) — an `inlineLambda` carrier's OWN param names (its regular params + a leading extension-receiver param).
    // A frame renamer (subst / prefix declared-set) must SUBTRACT these before descending the carrier's `body`/`result`:
    // kotc emits the carrier body's refs to its own params as BARE `{k:local,name:<param>}` (emitInlineLambdaCarrier
    // deliberately shadows them out of the enclosing valSubst), so an OUTER callee param / splice-declared local that
    // shares a name would else be rebound to the outer temp / prefixed — destroying the inner param ref (bound at the
    // NESTED splice, BuildLambdaSplice). The carrier's `captures[].name` descriptors ALSO need lockstep renaming (a later
    // §4.4ii materialization reads them back as ctor-arg values) — the renamers do that in the same `inlineLambda` case,
    // excluding these params (a param is bound, never a free capture).
    static HashSet<string> InlineLambdaParamNames(JsonObject il)
    {
        var ps = new HashSet<string>(StringComparer.Ordinal);
        if (il["params"] is JsonArray pa)
            foreach (var p in pa.OfType<JsonObject>()) if (Str(p["name"]) is string pn) ps.Add(pn);
        return ps;
    }

    // BATCH B (#75): a `newSuspendLambda` capture that CANNOT be soundly splice-rewritten (fail-loud, never silent):
    //  - a capture named `__outer` when `refuseOuter` (a PAYLOAD suspend lambda that captured the enclosing dispatch/
    //    extension receiver): post-splice SuspendLambdaLowering would bind its construction value to the CALLER's `this`/
    //    `__self`, not the payload's §4.3 dispatch temp / extension `__self` — a receiver mis-bind. (A CARRIER-side
    //    `__outer` is sound — the carrier's captured `this` IS the caller's receiver — so `refuseOuter` is false there.)
    //  - a capture whose name collides with the SM's OWN inner scope: FunGen conflates them by name (one `_fields` slot),
    //    so a descriptor rename + a body-ref skip diverge irreconcilably.
    // Returns the first offending descriptor name, else null. Same frame boundaries as the other scanners.
    static string SuspendCaptureHazard(JsonNode node, bool refuseOuter)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return null;
            if (Str(o["k"]) == "newSuspendLambda" && o["captures"] is JsonArray caps)
            {
                var inner = SuspendLambdaInnerScope(o);
                foreach (var c in caps.OfType<JsonObject>())
                    if (Str(c["name"]) is string cn)
                    {
                        if (refuseOuter && cn == "__outer") return "__outer";
                        if (inner.Contains(cn)) return cn;
                    }
            }
            foreach (var kv in o) if (kv.Key != "synthClass" && kv.Value != null && SuspendCaptureHazard(kv.Value, refuseOuter) is string h) return h;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && SuspendCaptureHazard(c, refuseOuter) is string h) return h;
        return null;
    }

    // BATCH B (#75) 2B — set a `capValues` override for the `__outer` slot on every PAYLOAD-FRAME `newSuspendLambda`
    // (the enclosing dispatch/extension receiver, rebound to the splice's receiver temp `outerVal`). A `newSuspendLambda`
    // is a SCOPE BOUNDARY: its `captures` construction values evaluate in THIS (payload) frame, but a `__outer` NESTED
    // INSIDE its body denotes ITS OWN enclosing (this lambda's instance), NOT the payload receiver — so we rebind at this
    // frame and do NOT descend into the lambda's body. Leaves other capture slots null so their fallback (a prefixed
    // local) applies. Skips a `typeDef` (its own scope).
    static void BindOuterCapValues(JsonNode node, JsonNode outerVal)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;
            if (Str(o["k"]) == "newSuspendLambda")
            {
                if (o["captures"] is JsonArray caps)
                {
                    int idx = -1;
                    for (int i = 0; i < caps.Count; i++)
                        if (caps[i] is JsonObject c && Str(c["name"]) == "__outer") { idx = i; break; }
                    if (idx >= 0)
                    {
                        if (o["capValues"] is not JsonArray cv) { cv = new JsonArray(); o["capValues"] = cv; }
                        while (cv.Count < caps.Count) cv.Add(null);
                        cv[idx] = outerVal.DeepClone();
                    }
                }
                return;   // its body is its OWN frame — a nested __outer belongs to it, not this payload
            }
            // Skip a nested closure/SAM `synthClass` (its own frame; never prefixed, so a `<prefix>__self`/`<prefix>this`
            // stamp there would name a non-existent local) — symmetric with RewriteThis/RewriteLocalRefs/ApplyPrefix.
            foreach (var kv in o) if (kv.Key != "synthClass" && kv.Value != null) BindOuterCapValues(kv.Value, outerVal);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) BindOuterCapValues(c, outerVal);
    }

    // BATCH B (#75) B3 — the first ORIGINAL (unprefixed) lambda-param `{k:local}` ref that dangles INSIDE a payload
    // `{k:typeDef}` (a local class), subtracting the typeDef's OWN bound names (its fields + every method/ctor param +
    // its declared locals) so a coincidental same-named member is not a false positive. Returns the name, else null.
    static string TypeDefLambdaParamRef(JsonNode node, HashSet<string> lamParams)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef")
            {
                var own = new HashSet<string>(StringComparer.Ordinal);
                CollectTypeDefOwnNames(o, own);
                return LocalRefNotIn(o, lamParams, own);
            }
            foreach (var kv in o) if (kv.Value != null && TypeDefLambdaParamRef(kv.Value, lamParams) is string h) return h;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && TypeDefLambdaParamRef(c, lamParams) is string h) return h;
        return null;
    }

    // Gather a typeDef's OWN bound names: `fields[].name`, every nested method/ctor `params[].name`, and every declared
    // local anywhere inside it (a broad `var`/loop/catch binder sweep). A ref to one of these is the class's own, not a
    // dangling capture.
    static void CollectTypeDefOwnNames(JsonNode node, HashSet<string> own)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if ((k == "var" || k == "field") && Str(o["name"]) is string vn) own.Add(vn);
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv) own.Add(fv);
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv) own.Add(cv);
            if (o["params"] is JsonArray ps) foreach (var p in ps.OfType<JsonObject>()) if (Str(p["name"]) is string pn) own.Add(pn);
            foreach (var kv in o) if (kv.Value != null) CollectTypeDefOwnNames(kv.Value, own);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectTypeDefOwnNames(c, own);
    }

    // The first `{k:local,name:X}` in the subtree with X ∈ `names` and X ∉ `bound`, else null.
    static string LocalRefNotIn(JsonNode node, HashSet<string> names, HashSet<string> bound)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "local" && Str(o["name"]) is string nm && names.Contains(nm) && !bound.Contains(nm)) return nm;
            foreach (var kv in o) if (kv.Value != null && LocalRefNotIn(kv.Value, names, bound) is string h) return h;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && LocalRefNotIn(c, names, bound) is string h) return h;
        return null;
    }

    // True if the carrier's OWN frame (skipping any nested `synthClass` — that is the nested closure's own frame) holds a
    // nested-fn node that cannot be wrapped verbatim into the materialized closure (§4.4ii): a `newSuspendLambda` (SM ctor
    // binds captures by descriptor name — the splice's field/this rewrites would skew it), a `newDelegate` (dangling
    // origin-file lambda type token), or an un-spliced `inlineLambda` (a nested inline-call lambda arg, only resolved at
    // STEP 8's fixpoint). A plain `newClosure`/`newSam` is materializable — its captures are handled by the descending
    // sibling scans (RewriteCapturesToFields / HasStrayLocal / HasThisOutsideSynthClass / CollectTvKeys).
    static bool HasUnmaterializableNested(JsonNode node)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k is "newSuspendLambda" or "inlineLambda") return true;
            // #43: a `newDelegate` is unmaterializable ONLY if it dangles cross-module (§4.6). A same-module target —
            // notably a __dflt$lambda$N a nested member-inline default-fill just re-hoisted app-local — IS materializable
            // verbatim (the delegate is capture-less; its static method `ldftn`-resolves in THIS assembly).
            if (k == "newDelegate" && !IsAppLocalDelegate(o)) return true;
            foreach (var kv in o) if (kv.Key != "synthClass" && kv.Value != null && HasUnmaterializableNested(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasUnmaterializableNested(c)) return true;
        return false;
    }

    // True if a `{k:this}` survives at the carrier's OWN frame level — the top-level body OR a nested closure's CAPTURE
    // value (a nested closure that captured the enclosing receiver) — skipping a nested `synthClass` (its `this` is its
    // own instance). Used as the unlisted-enclosing-`this` guard when the carrier lists no `outer:true` capture.
    static bool HasThisOutsideSynthClass(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "this") return true;
            bool nested = Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam";
            foreach (var kv in o) if (kv.Value != null && (!nested || kv.Key != "synthClass") && HasThisOutsideSynthClass(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasThisOutsideSynthClass(c)) return true;
        return false;
    }

    // The own-frame keys of a nested `newSuspendLambda` — the SM lowering resolves its `body`/`params`/`suspendRet` tvs
    // POSITIONALLY against its OWN `typeParams`, so an enclosing frame's tv collect/renumber must NOT descend into them
    // (exactly the `synthClass` shield rationale, one node-kind over). Its OUTER-frame refs — `captures` (ctor-arg types),
    // `typeArgs` (construction args), `funcType`, `capValues` — ARE descended.
    static readonly HashSet<string> SuspendLambdaOwnFrame = new(StringComparer.Ordinal) { "body", "params", "suspendRet", "typeParams", "arity", "k" };

    // The TOP-MOST `newSuspendLambda` nodes in the subtree — descent STOPS at each one (a doubly-nested NSL rides under a
    // first-level NSL's shielded body, so the outer remap never touches it). Used by the nested-SM invariant guard.
    static IEnumerable<JsonObject> FirstLevelNestedSuspendLambdas(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "newSuspendLambda") { yield return o; yield break; }
            foreach (var kv in o) if (kv.Value != null) foreach (var n in FirstLevelNestedSuspendLambdas(kv.Value)) yield return n;
        }
        else if (node is JsonArray a)
            foreach (var c in a) if (c != null) foreach (var n in FirstLevelNestedSuspendLambdas(c)) yield return n;
    }

    // Collect the distinct `{t:tv}` (scope, index) KEYS in the subtree. kotc numbers method type-params independently of
    // type (class) type-params, so scope is part of a tv's identity.
    static void CollectTvKeys(JsonNode node, SortedSet<(string scope, int i)> keys)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv") keys.Add((Str(o["scope"]) ?? "method", Int(o["i"])));
            // Skip a nested closure's `synthClass`: its typeParams live in that frame's OWN 0-based space and its body's
            // `tv{…}` resolve positionally against them — collecting/renumbering them into the outer frame would corrupt
            // the nested class. Its `captures`/`funcType`/`typeArgs` (outer-frame tv refs) ARE descended (#22). A nested
            // `newSuspendLambda` is the SAME kind of tv scope boundary — shield its own frame, descend its outer refs.
            bool nestedSm = Str(o["k"]) == "newSuspendLambda";
            foreach (var kv in o)
                if (kv.Value != null && kv.Key != "synthClass" && !(nestedSm && SuspendLambdaOwnFrame.Contains(kv.Key)))
                    CollectTvKeys(kv.Value, keys);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectTvKeys(c, keys);
    }

    // Renumber every `{t:tv}` index in place via `remap` keyed by (scope, index). Scope is PRESERVED on the ref (ilemit's
    // cross-pool positional ResolveTv resolves it against the closure class's type params by the renumbered index).
    static void RenumberTvs(JsonNode node, Dictionary<(string, int), int> remap)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv" && o["i"] is JsonValue iv && iv.TryGetValue<int>(out var i)
                && remap.TryGetValue((Str(o["scope"]) ?? "method", i), out var ni)) o["i"] = ni;
            // Skip a nested closure's `synthClass` (its tvs are its own frame — see CollectTvKeys); its outer-frame tv
            // refs on `captures`/`funcType`/`typeArgs` ARE renumbered into the materialized closure's param space (#22).
            // A nested `newSuspendLambda`'s own frame is shielded identically — only its outer-frame refs are renumbered.
            bool nestedSm = Str(o["k"]) == "newSuspendLambda";
            foreach (var kv in o)
                if (kv.Value != null && kv.Key != "synthClass" && !(nestedSm && SuspendLambdaOwnFrame.Contains(kv.Key)))
                    RenumberTvs(kv.Value, remap);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RenumberTvs(c, remap);
    }

    // Route origin-fn returns: mirror kotc spliceBodyWithReturns. Tail `{k:return,value}` folds to the block value; an
    // early return routes through `setLocal res + goto end`. Does NOT descend into nested closure/type-def subtrees.
    static JsonNode RouteReturns(JsonArray body, bool unit, JsonNode retType, string prefix)
    {
        bool hasEarly = HasEarlyReturn(body);
        if (!hasEarly)
        {
            if (!unit && body.Count > 0 && body[^1] is JsonObject last && Str(last["k"]) == "return" && last["value"] is JsonNode tv)
            {
                // Fold a tail `{k:return,value}` into the block value.
                var v = tv.DeepClone();
                body.RemoveAt(body.Count - 1);
                return v;
            }
            // D4: a UNIT callee ending in an explicit `{k:return}` (possibly with a side-effecting value) must NOT leave
            // that bare return in the block — it would return from the CALLER. Strip it (hoisting a non-trivial value as
            // a trailing exprStmt so its side effect survives).
            if (unit && body.Count > 0 && body[^1] is JsonObject ulast && Str(ulast["k"]) == "return")
            {
                body.RemoveAt(body.Count - 1);
                if (ulast["value"] is JsonNode uv && Str(uv["k"]) is string vk && vk != "const")
                    body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = uv.DeepClone() });
            }
            return UnitConst();
        }

        int end = _nextLabelId++;
        JsonNode result;
        if (!unit)
        {
            string res = prefix + "ret";
            var rt = retType?.DeepClone() ?? TypeJson.Fqn("kotlin.Unit");
            body.Insert(0, new JsonObject
            {
                ["k"] = "var",
                ["name"] = res,
                ["type"] = rt.DeepClone(),
                ["init"] = new JsonObject { ["k"] = "default", ["type"] = rt.DeepClone() },
            });
            RewriteReturns(body, res, end);
            body.Add(new JsonObject { ["k"] = "label", ["id"] = end });
            result = new JsonObject { ["k"] = "local", ["name"] = res };
        }
        else
        {
            RewriteReturns(body, null, end);
            body.Add(new JsonObject { ["k"] = "label", ["id"] = end });
            result = UnitConst();
        }
        return result;
    }

    // Rewrite every origin `{k:return}`/`{k:returnExpr}` (top-level of the body, non-descending into closures) into the
    // routed form. A statement-position `{k:return}` (an array element) becomes `setLocal res; goto end`; an EXPRESSION-
    // position `{k:returnExpr}` (a value slot — an elvis RHS, an if/when-as-value branch) becomes a value-position
    // `valueBlock{ setLocal res; goto end; result:throwExpr }` — the same "wrap a control transfer to sit in an expr
    // slot" shape kotc's breakContinueExpr uses (§ BirEmitterControlFlow), so the surrounding merge keeps only the live
    // branch's type and the goto diverges before the enclosing store, exactly as the raw `returnExpr`'s `ret` did.
    static void RewriteReturns(JsonNode node, string res, int end)
    {
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                // Both a statement-position `{k:return}` and (defensively) a bare `{k:returnExpr}` element lift to the
                // SAME flat `setLocal res; goto end`. Route any control transfer NESTED in (or directly being) the value
                // FIRST — the expression-body `= input ?: return onClosed()` (kotc emits one tail `{k:return, value:elvis{
                // …,returnExpr}}`), or a pathological `return (x ?: return y)` — so the INNER return's `goto` fires while
                // evaluating this store's value and the inner return wins (matching Kotlin), never leaking a raw
                // `returnExpr` into the cloned `setLocal` value (that would emit a caller-frame `ret`).
                if (a[i] is JsonObject ro && Str(ro["k"]) is "return" or "returnExpr")
                {
                    if (ro["value"] != null) ro["value"] = RouteValue(ro["value"], res, end);
                    var repl = new JsonArray();
                    if (res != null && ro["value"] is JsonNode rv)
                        repl.Add(new JsonObject { ["k"] = "setLocal", ["name"] = res, ["value"] = rv.DeepClone() });
                    // A UNIT callee's early `return sideEffect()` (res == null) still evaluates its value for effect —
                    // hoist a non-trivial value as an exprStmt (symmetric with the D4 tail-strip); a const is dropped.
                    else if (res == null && ro["value"] is JsonObject uv && Str(uv["k"]) != "const")
                        repl.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = uv.DeepClone() });
                    repl.Add(new JsonObject { ["k"] = "goto", ["id"] = end });
                    a.RemoveAt(i);
                    for (int j = 0; j < repl.Count; j++) { a.Insert(i + j, repl[j].DeepClone()); }
                    i += repl.Count - 1;
                }
                else if (a[i] is JsonNode c) RewriteReturns(c, res, end);
            }
        }
        else if (node is JsonObject o)
        {
            if (IsClosureBoundary(o)) return;
            foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys))
                if (o[key] != null) o[key] = RouteValue(o[key], res, end);
        }
    }

    // Route a VALUE-position node: an expression-position `{k:returnExpr}` lifts to a value-position `valueBlock` (below);
    // anything else recurses in place (its own nested returns/returnExprs route where they sit). Returns the (possibly
    // replaced) node — the single choke point so a `returnExpr` is handled identically whether it is an object value, an
    // array element, or the direct value of another return.
    static JsonNode RouteValue(JsonNode v, string res, int end)
    {
        if (v is JsonObject o && Str(o["k"]) == "returnExpr") return RouteReturnExpr(o, res, end);
        RewriteReturns(v, res, end);
        return v;
    }

    // Lift an expression-position `{k:returnExpr,value?}` to a value-position `valueBlock` that performs the same routed
    // control transfer (setLocal res + goto end) then a never-reached `throwExpr` result — the shape kotc's
    // breakContinueExpr uses. Routes the value FIRST (a nested/direct control transfer inside it wins). Unit splice
    // (res == null) evaluates a non-const value for its side effect, then jumps.
    static JsonNode RouteReturnExpr(JsonObject reo, string res, int end)
    {
        var val = reo["value"];
        if (val != null) val = RouteValue(val, res, end);
        var stmts = new JsonArray();
        if (res != null && val is JsonNode rv)
            stmts.Add(new JsonObject { ["k"] = "setLocal", ["name"] = res, ["value"] = rv.DeepClone() });
        else if (res == null && val is JsonObject uv && Str(uv["k"]) != "const")
            stmts.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = uv.DeepClone() });
        stmts.Add(new JsonObject { ["k"] = "goto", ["id"] = end });
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = stmts,
            ["result"] = new JsonObject { ["k"] = "throwExpr", ["value"] = UnitConst() },
        };
    }

    static bool HasEarlyReturn(JsonNode node)
    {
        bool found = false;
        void Rec(JsonNode nn, bool top, JsonArray parent, int idx)
        {
            if (found) return;
            if (nn is JsonArray a) { for (int i = 0; i < a.Count && !found; i++) Rec(a[i], top, a, i); }
            else if (nn is JsonObject o)
            {
                if (IsClosureBoundary(o)) return;
                // An EXPRESSION-position return (`x ?: return v`, an if/when-as-value branch) is ALWAYS a control
                // transfer nested inside a value slot — never a foldable tail statement — so it always forces the
                // result-local + end-label routing path (RewriteReturns lifts it to a `valueBlock{setLocal;goto}`).
                if (Str(o["k"]) == "returnExpr") { found = true; return; }
                if (Str(o["k"]) == "return")
                {
                    // A tail return as the LAST top-level statement folds into the value (not "early").
                    bool tailTop = top && parent != null && idx == parent.Count - 1;
                    if (!tailTop) { found = true; return; }
                }
                foreach (var kv in o) if (kv.Value != null) Rec(kv.Value, false, null, -1);
            }
        }
        if (node is JsonArray arr) for (int i = 0; i < arr.Count && !found; i++) Rec(arr[i], true, arr, i);
        return found;
    }

    // A subtree whose `{k:return}` belongs to a NESTED function (closure / SAM / lifted delegate / type-def method), not the
    // spliced origin fn — return routing / early-return scanning must not descend into it. `newSam` carries a `synthClass`
    // whose SAM-override method body has its own `{k:return}` (kotc emits plain IrReturn) — treat it as a boundary too, else
    // RewriteReturns would re-route the SAM method's return into the caller's end-label (a dangling goto).
    static bool IsClosureBoundary(JsonObject o)
    {
        var k = Str(o["k"]);
        return k == "newClosure" || k == "newDelegate" || k == "newSam" || k == "newSuspendLambda";
    }

    // ---- hygiene helpers -------------------------------------------------------------------------------------------

    // Fresh SEQUENTIAL cfg ids for the DISTINCT ids of the DECLARED labels only (§4.1). The Joint variant maps the ids of
    // MULTIPLE roots through ONE map (a carrier's `body` + `result` cross-reference each other's labels).
    static void FreshenLabels(JsonNode node) => FreshenLabelsJoint(node);

    static void FreshenLabelsJoint(params JsonNode[] roots)
    {
        var map = new Dictionary<int, int>();
        foreach (var r in roots) if (r != null) CollectIds(r, map);
        if (map.Count == 0) return;
        foreach (var key in new List<int>(map.Keys)) map[key] = _nextLabelId++;
        foreach (var r in roots) if (r != null) ApplyIds(r, map);
    }

    // §4.1 HYGIENE (silent-miscompile fix): collect ONLY `label`-declared ids. A carrier body can carry a non-local
    // `{k:goto,id:<caller-loop-label>}` (arm-b break/continue, #95 traffic) whose matching `{k:label}` lives OUTSIDE this
    // region, in the surrounding function. If we re-minted that goto's id (as the old `label||goto||brIf` collect did) it
    // would dangle — and worse, a later same-file splice could mint the same fresh id for an unrelated label, silently
    // branching to a foreign program point. Collecting only labels leaves an out-of-region goto untouched (ApplyIds's
    // TryGetValue guard skips it) so it keeps resolving against the caller's live label.
    static void CollectIds(JsonNode nn, Dictionary<int, int> map)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;   // a lifted local class's labels are its own scope
            var k = Str(o["k"]);
            if (k == "label" && o["id"] is JsonValue) map.TryAdd(Int(o["id"]), 0);
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") CollectIds(kv.Value, map);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) CollectIds(c, map);
    }

    // Remap label/goto/brIf ids — but ONLY when the id is in the map (i.e. a label DECLARED in this freshened region;
    // §4.1). A goto/brIf targeting an out-of-region (caller) label is NOT in the map and passes through untouched. Do NOT
    // "simplify" this into an unconditional remap: that reintroduces the dangling-goto miscompile.
    static void ApplyIds(JsonNode nn, Dictionary<int, int> map)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;   // scope boundary (its labels are its own)
            var k = Str(o["k"]);
            if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue && map.TryGetValue(Int(o["id"]), out var nid))
                o["id"] = nid;
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") ApplyIds(kv.Value, map);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) ApplyIds(c, map);
    }

    // Prefix-rename locals DECLARED in this clone (var/forIn.var/forArray.var/repeatInline.var/callInline.var (nested
    // kotlin.repeat)/try.catches[].var) plus every matching {k:local}/{k:setLocal} reference. Leaves param refs (not in
    // the declared set) for step-5 binding. The Joint variant shares ONE declared-set across roots (body + result).
    static void PrefixLocals(JsonNode node, string prefix) => PrefixLocalsJoint(prefix, node);

    static void PrefixLocalsJoint(string prefix, params JsonNode[] roots)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roots) if (r != null) CollectDeclared(r, declared);
        if (declared.Count == 0) return;
        foreach (var r in roots) if (r != null) ApplyPrefix(r, declared, prefix);
    }

    // SCOPE BOUNDARY (typeDef whole / `synthClass` key): a nested lifted class's own `var`s must NOT enter the splice's
    // declared-set (else a splice ref of the same name is wrongly prefixed), and its ctor/method local refs must NOT be
    // prefixed (they are its OWN scope). Capture VALUES on the node ride the sibling keys, still descended.
    static void CollectDeclared(JsonNode nn, HashSet<string> declared)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;
            // BATCH B (#75): a `newSuspendLambda`'s inner vars are the SM's OWN scope (its `captures[].name` are enclosing
            // names handled JOINTLY by ApplyPrefix below, not declared-set members). Skip it whole, like a typeDef.
            if (Str(o["k"]) == "newSuspendLambda") return;
            // F2 (#61) SCOPE BOUNDARY (symmetric with ApplyPrefix/RewriteLocalRefs): a nested `inlineLambda` carrier's
            // OWN params are NOT splice-declared locals (they bind at BuildLambdaSplice), so they never enter `declared`;
            // its `body`/`result` DO declare real locals that still need hygiene prefixing, so descend them. `params`/
            // `captures` descriptors declare nothing collectable — descending only body/result is byte-identical.
            if (Str(o["k"]) == "inlineLambda")
            {
                if (o["body"] is JsonNode ib) CollectDeclared(ib, declared);
                if (o["result"] is JsonNode ir) CollectDeclared(ir, declared);
                return;
            }
            var k = Str(o["k"]);
            if (k == "var" && Str(o["name"]) is string vn) declared.Add(vn);
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv) declared.Add(fv);
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv) declared.Add(cv);
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") CollectDeclared(kv.Value, declared);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) CollectDeclared(c, declared);
    }

    static void ApplyPrefix(JsonNode nn, HashSet<string> declared, string prefix)
    {
        if (nn is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;
            // BATCH B (#75): JOINT hygiene for a `newSuspendLambda`. Its `captures[].name` are enclosing-frame names and
            // its body references each as a plain `{k:local,name:X}` (the SM lowering field-ifies them). Prefix a capture
            // descriptor + its body refs in LOCKSTEP with the frame — iff the name is a splice-declared local NOT shadowed
            // by the SM's OWN inner scope (params + body-declared locals). The SM's inner names never enter `declared`
            // (CollectDeclared skips the node), so descending the body with `declared` MINUS the inner scope prefixes only
            // capture refs and leaves the SM's private locals/params untouched. Nested SMs recurse via this same case.
            if (Str(o["k"]) == "newSuspendLambda")
            {
                var inner = SuspendLambdaInnerScope(o);
                if (o["captures"] is JsonArray caps)
                    foreach (var c in caps.OfType<JsonObject>())
                        if (Str(c["name"]) is string cn && declared.Contains(cn) && !inner.Contains(cn)) c["name"] = prefix + cn;
                var bodyDeclared = new HashSet<string>(declared, StringComparer.Ordinal);
                bodyDeclared.ExceptWith(inner);
                if (bodyDeclared.Count > 0 && o["body"] is JsonNode nb) ApplyPrefix(nb, bodyDeclared, prefix);
                // `capValues` are the captures' construction VALUES in the ENCLOSING (splice) frame — outer-frame refs like
                // `captures`/`typeArgs`, NOT the SM's inner scope — so prefix them with the FULL `declared` set (STEP-8
                // walker: "capValues ARE descended"). Skipped by the generic descent below (early return), so do it here.
                if (o["capValues"] is JsonNode ncv) ApplyPrefix(ncv, declared, prefix);
                return;
            }
            // F2 (#61) SCOPE BOUNDARY: descend a nested `inlineLambda` carrier's `body`/`result` with the declared-set
            // MINUS this carrier's OWN params, so a splice-declared local sharing a name with a carrier param does NOT
            // prefix the carrier's bare `{k:local,name:<param>}` body ref (which would strand it from the un-prefixed
            // `params[]` descriptor at BuildLambdaSplice). Body-declared locals of the carrier stay in the set and are
            // still prefixed for hygiene.
            if (Str(o["k"]) == "inlineLambda")
            {
                var lamParams = InlineLambdaParamNames(o);
                // JOINT lockstep (symmetric with RewriteLocalRefs): prefix a capture descriptor naming a splice-declared
                // local together with its body refs, so the later MaterializeCarrier / MaterializeSuspendCarrier ctor-arg
                // read targets the PREFIXED local, not the hygiene-renamed-away one. Skip own params + `outer:true`.
                if (o["captures"] is JsonArray ilCaps)
                    foreach (var c in ilCaps.OfType<JsonObject>())
                        if (Str(c["name"]) is string cn && declared.Contains(cn) && !lamParams.Contains(cn)
                            && !(c["outer"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob))
                            c["name"] = prefix + cn;
                var bodyDeclared = new HashSet<string>(declared, StringComparer.Ordinal);
                bodyDeclared.ExceptWith(lamParams);
                if (bodyDeclared.Count > 0)
                {
                    if (o["body"] is JsonNode ib) ApplyPrefix(ib, bodyDeclared, prefix);
                    if (o["result"] is JsonNode ir) ApplyPrefix(ir, bodyDeclared, prefix);
                }
                return;
            }
            var k = Str(o["k"]);
            if ((k == "var" || k == "local" || k == "setLocal") && Str(o["name"]) is string nm && declared.Contains(nm))
                o["name"] = prefix + nm;
            if ((k == "forIn" || k == "forArray" || k == "repeatInline" || k == "callInline") && Str(o["var"]) is string fv && declared.Contains(fv))
                o["var"] = prefix + fv;
            if (k == "try" && o["catches"] is JsonArray cs)
                foreach (var c in cs) if (c is JsonObject co && Str(co["var"]) is string cv && declared.Contains(cv)) co["var"] = prefix + cv;
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") ApplyPrefix(kv.Value, declared, prefix);
        }
        else if (nn is JsonArray a) foreach (var c in a) if (c != null) ApplyPrefix(c, declared, prefix);
    }

    // Replace {k:local,name} references whose name is in `subst` with the bound node; retarget a {k:setLocal,name} on a
    // bound param to the temp's name (D9: assignment to an inline value param — rare, tailrec starg).
    static void RewriteLocalRefs(JsonNode node, Dictionary<string, JsonNode> subst)
    {
        if (node == null || subst.Count == 0) return;
        if (node is JsonObject o)
        {
            // SCOPE BOUNDARY: a nested `typeDef` (lifted local class) is its OWN scope; a `synthClass` (the closure/SAM class
            // ON a newClosure/newSam node) is likewise — its ctor/method params+locals are NOT the splice's. Do NOT rewrite
            // their `{k:local}` refs (a SAM ctor's `{local selector}` = its OWN ctor param, not the spliced `selector`). But
            // DO descend into the node's `captures`/`args` (those capture VALUES are in the splice scope). So skip only the
            // `synthClass` KEY here, and a `typeDef` node whole.
            if (Str(o["k"]) == "typeDef") return;
            // BATCH B (#75): JOINT hygiene for a `newSuspendLambda` — rewrite a capture descriptor + its body refs in
            // lockstep with the frame's subst, skipping the SM's OWN inner scope. A captured lambda PARAM's subst points
            // at its prefixed carrier local (STEP 5) or the materialized suspend temp (STEP 6 rebind) — a simple
            // `{k:local,name:T}`, so the descriptor can adopt `T`. A capture that maps to a NON-local expr cannot be a
            // descriptor name -> fail loud (never silently drop the suspend capture).
            if (Str(o["k"]) == "newSuspendLambda")
            {
                var inner = SuspendLambdaInnerScope(o);
                if (o["captures"] is JsonArray caps)
                    foreach (var c in caps.OfType<JsonObject>())
                        if (Str(c["name"]) is string cn && !inner.Contains(cn) && subst.TryGetValue(cn, out var cb))
                        {
                            if (cb is JsonObject cbo && Str(cbo["k"]) == "local" && Str(cbo["name"]) is string tn) c["name"] = tn;
                            else throw new NotSupportedException(
                                $"inline splice: a payload newSuspendLambda captures '{cn}' but the splice binds it to a non-local expression — a suspend-lambda capture descriptor can only name a local/temp (#75 Batch B)");
                        }
                if (o["body"] is JsonNode nb)
                {
                    var bodySubst = inner.Count == 0 ? subst
                        : subst.Where(kv => !inner.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    RewriteLocalRefs(nb, bodySubst);
                }
                // `capValues` are the captures' construction VALUES in the ENCLOSING (splice) frame — outer-frame refs, so
                // rewrite them with the FULL `subst` (not the inner-scope-minus body subst). Skipped by the generic descent
                // below (early return); descend explicitly here so a spliced payload's capValue local is rebound in lockstep.
                if (o["capValues"] is JsonNode ncv) RewriteLocalRefs(ncv, subst);
                return;
            }
            // F2 (#61) SCOPE BOUNDARY: a nested `inlineLambda` carrier's OWN params shadow same-named outer callee params.
            // Descend its `body`/`result` with `subst` MINUS this carrier's params so an inner param ref keeps its bare
            // name (bound later at BuildLambdaSplice), never rebound to the outer temp. Nested carriers recurse through
            // this same case.
            if (Str(o["k"]) == "inlineLambda")
            {
                var lamParams = InlineLambdaParamNames(o);
                // JOINT lockstep: the carrier's `captures[].name` are ENCLOSING-frame names that a later §4.4ii
                // materialization reads back as `{k:local,name}` ctor-arg VALUES (MaterializeCarrier / MaterializeSuspend
                // Carrier), and the suspend arm's FunGen field-ifies the SM body's refs BY that name. So a descriptor
                // whose name THIS frame's subst rebinds MUST be renamed in lockstep with the body descent below — else the
                // ctor arg dangles as a stale `{k:local,name:X}` (the nested-inline-splice `transform` fault) and, in the
                // suspend arm, the body field-ref skews from the descriptor. Same doctrine as the newSuspendLambda case
                // above. Skip the carrier's OWN params (bound, not free) and an `outer:true` capture (its value is the
                // enclosing `{k:this}`, not a name-keyed local — MaterializeCarrier emits `{k:this}` for it).
                if (o["captures"] is JsonArray ilCaps)
                    foreach (var c in ilCaps.OfType<JsonObject>())
                        if (Str(c["name"]) is string cn && !lamParams.Contains(cn)
                            && !(c["outer"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob)
                            && subst.TryGetValue(cn, out var cb))
                        {
                            if (cb is JsonObject cbo && Str(cbo["k"]) == "local" && Str(cbo["name"]) is string tn) c["name"] = tn;
                            else throw new NotSupportedException(
                                $"inline splice: a nested inlineLambda carrier captures '{cn}' but the splice binds it to a non-local expression — a carrier capture descriptor can only name a local/temp");
                        }
                var bodySubst = lamParams.Count == 0 ? subst
                    : subst.Where(kv => !lamParams.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                if (o["body"] is JsonNode ib) RewriteLocalRefs(ib, bodySubst);
                if (o["result"] is JsonNode ir) RewriteLocalRefs(ir, bodySubst);
                return;
            }
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") RewriteLocalRefs(kv.Value, subst);
            var k = Str(o["k"]);
            if (k == "local" && Str(o["name"]) is string nm && subst.TryGetValue(nm, out var b))
            {
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                if (b.DeepClone() is JsonObject bo) foreach (var kv in bo) o[kv.Key] = kv.Value?.DeepClone();
            }
            else if (k == "setLocal" && Str(o["name"]) is string sn && subst.TryGetValue(sn, out var sb)
                     && sb is JsonObject sbo && Str(sbo["name"]) is string tn)
                o["name"] = tn;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RewriteLocalRefs(c, subst);
    }

    // Positional type-param subst over a statement array (walks all nodes) — {t:tv,scope:method,i<ga} <- typeArgs[i],
    // {t:tv,scope:type,i} <- dispatchTypeArgs[i] (F2B), in ONE fused pass (see SubstTvIn).
    static void SubstTv(JsonArray body, JsonArray typeArgs, int ga, JsonArray dispatchTypeArgs = null)
    {
        foreach (var st in body) SubstTvIn(st, typeArgs, ga, dispatchTypeArgs);
    }

    // FUSED single walk (F2B): each `{t:tv}` node is replaced IN-PLACE by its scope's positional value — `scope:method` <-
    // the node's `typeArgs[i]` (caller-frame types), `scope:type` <- `dispatchTypeArgs[i]` (the dispatch receiver's
    // concretized owning-class args, in the flattened `scope:type` index space). The replacement RETURNS immediately (never
    // re-walked), so the two substitutions can NEVER see each other's output: a caller `tv{scope:type,j}` INSIDE an inserted
    // `scope:method` value stays (it resolves a frame up), and a `tv` entry in `dispatchTypeArgs` (same-class `this`
    // identity, or a caller method param) is likewise inserted verbatim, not re-substituted. `dispatchTypeArgs` is null/empty
    // when kotc carried none (no dispatch / non-generic owner / receiver-class != owner / tv-render / arity mismatch).
    // TYPE-SCOPE BOUNDARY (hazard): a `synthClass` (closure/SAM class), a `{k:typeDef}` local class, and a
    // `newSuspendLambda` encode their OWN class type params as `tv{scope:type,i}` — the OWNER-class dispatchTypeArgs must
    // NOT reach them. `typeScope` flips off descending through those; METHOD-scope subst continues everywhere (a closure
    // body legitimately references the enclosing `tv{scope:method,i}`).
    static void SubstTvIn(JsonNode node, JsonArray typeArgs, int ga, JsonArray dispatchTypeArgs = null, bool typeScope = true)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv")
            {
                var scope = Str(o["scope"]);
                JsonNode repl = scope == "method" && Int(o["i"]) is int mi && mi < ga ? typeArgs.ElementAtOrDefault(mi)
                              : scope == "type" && typeScope && dispatchTypeArgs != null && Int(o["i"]) is int ti && ti < dispatchTypeArgs.Count ? dispatchTypeArgs.ElementAtOrDefault(ti)
                              : null;
                if (repl != null)
                {
                    var c = repl.DeepClone();
                    foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                    if (c is JsonObject co) foreach (var kv in co) o[kv.Key] = kv.Value?.DeepClone();
                    return;
                }
            }
            // This node opens its OWN class-type-param scope -> the owner-class dispatchTypeArgs stop here.
            bool ownBoundary = Str(o["k"]) is "typeDef" or "newSuspendLambda";
            foreach (var kv in o)
                // §4.2: a nested call's `sig`/`paramSig` is the NESTED callee's DECLARED type-param frame (its own tv{method,i}),
                // NOT the outer method's — substituting the outer method's typeArgs into it corrupts the overload key the
                // fixpoint (paramSig) / forwarding (sig) matches against. The nested call's ACTUAL type args ride `typeArgs`
                // (a sibling key), which IS substituted. So skip these two keys; a callee's declared params can never
                // legitimately reference the outer method's tvs.
                if (kv.Value != null && kv.Key != "sig" && kv.Key != "paramSig")
                    SubstTvIn(kv.Value, typeArgs, ga, dispatchTypeArgs, typeScope && !ownBoundary && kv.Key != "synthClass");
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) SubstTvIn(c, typeArgs, ga, dispatchTypeArgs, typeScope);
    }

    // STEP 2b (boxgen): after SubstTv, a `newSam`/`newClosure` whose node `typeArgs` are all CONCRETE (the call-site bound
    // the closure/SAM class's type params to real types) is EFFECTIVELY non-generic — but the synthClass still declares
    // `typeParams` and encodes its OWN params as `tv{scope:type,i}` (which SubstTv, scope:method-only, never touched), while
    // the interface got decoupled to a CONCRETE arg (`Sam102<T> : Comparator<Int32>` — the value-type dispatch then can't
    // bind `compare`). Concretize the class's own params (`tv{scope:type,i}` -> the concrete typeArgs[i]), strip the now-
    // spurious type-args off self-references to the class, and drop `typeParams` + the node's `typeArgs` -> a clean
    // non-generic class. No-op when a typeArg is itself a tv (a genuinely-generic instantiation keeps the class generic).
    static void PruneConcreteSynthClasses(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if ((Str(o["k"]) == "newSam" || Str(o["k"]) == "newClosure") && o["synthClass"] is JsonObject sc
                && Str(sc["name"]) is string cname
                && sc["typeParams"] is JsonArray tps && tps.Count > 0
                && o["typeArgs"] is JsonArray ta && ta.Count > 0 && !HasTv(ta))
            {
                SubstTypeScopeTvs(sc, ta);       // tv{scope:type,i} -> the concrete typeArgs[i]
                StripSelfGenericArgs(sc, cname);  // Sam102<...> (self-ref) -> Sam102 (now non-generic)
                sc.Remove("typeParams");
                o.Remove("typeArgs");
            }
            foreach (var kv in o) if (kv.Value != null) PruneConcreteSynthClasses(kv.Value);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) PruneConcreteSynthClasses(c);
    }

    // Substitute a synthClass's OWN type params (`tv{scope:type,i}`) with the concrete `typeArgs[i]` bound at construction.
    static void SubstTypeScopeTvs(JsonNode node, JsonArray typeArgs)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv" && Str(o["scope"]) == "type" && typeArgs.ElementAtOrDefault(Int(o["i"])) is JsonNode t)
            {
                var c = t.DeepClone();
                foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(key);
                if (c is JsonObject co) foreach (var kv in co) o[kv.Key] = kv.Value?.DeepClone();
                return;
            }
            foreach (var kv in o) if (kv.Value != null) SubstTypeScopeTvs(kv.Value, typeArgs);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) SubstTypeScopeTvs(c, typeArgs);
    }

    // Strip the generic type-args off a SELF-reference to the (now non-generic) class `cname` (`{t:fqn,name:cname,args:[…]}`
    // -> drop `args`). Leaves references to OTHER types intact.
    static void StripSelfGenericArgs(JsonNode node, string cname)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "fqn" && Str(o["name"]) == cname) o.Remove("args");
            foreach (var kv in o) if (kv.Value != null) StripSelfGenericArgs(kv.Value, cname);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) StripSelfGenericArgs(c, cname);
    }

    // A `cast to <value type X>` whose OPERAND is a `Nullable<X>` value (a `T?` local, once SubstTv concretized T->X): the
    // ilemit `cast` path emits `unbox.any X` — INVALID on a `Nullable<X>` STRUCT operand (not a boxed object) -> SIGSEGV
    // (`Iterable.single`/`last`'s `return single as T`, single: T?). kotc lowers a concrete `Int? as Int` to `nullableValue`
    // (Nullable.get_Value); mirror that. DISCRIMINATE by the operand: convert ONLY a `{k:local}` whose declared type is
    // exactly `nullable(X)` — a boxed/object operand (`Any? as Int` in Result.getOrElse) stays a `cast` (ilemit unbox.any is
    // correct there).
    static void NormalizeConcretizedCasts(JsonNode node)
    {
        var varTypes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        CollectVarTypes(node, varTypes);
        NormalizeCastsWith(node, varTypes);
    }

    static void CollectVarTypes(JsonNode node, Dictionary<string, JsonNode> map)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string vn && o["type"] is JsonNode vt) map[vn] = vt;
            foreach (var kv in o) if (kv.Value != null) CollectVarTypes(kv.Value, map);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectVarTypes(c, map);
    }

    static void NormalizeCastsWith(JsonNode node, Dictionary<string, JsonNode> varTypes)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "cast" && o["type"] is JsonObject ct && Str(ct["t"]) == "fqn" && Str(ct["name"]) is string cn
                && _refs != null && _refs.IsValueTypeFqn(cn)
                && o["e"] is JsonObject e && Str(e["k"]) == "local" && Str(e["name"]) is string en
                && varTypes.TryGetValue(en, out var et)
                && et is JsonObject eto && Str(eto["t"]) == "nullable" && eto["of"] is JsonObject ofo
                && Str(ofo["t"]) == "fqn" && Str(ofo["name"]) == cn)
            {
                o["k"] = "nullableValue";
                var elemT = o["type"].DeepClone();
                o.Remove("type");
                o["elem"] = elemT;
            }
            foreach (var kv in o) if (kv.Value != null) NormalizeCastsWith(kv.Value, varTypes);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) NormalizeCastsWith(c, varTypes);
    }

    // Whole-tree companion to NormalizeConcretizedCasts for the IMPLICIT positions (no `cast` node): a `Nullable<VT>`
    // local flowing into a non-nullable value-type slot. Two observed shapes (both #75-splice-induced):
    //   - a `cond` typed VT whose `then`/`else` branch is a bare `Nullable<VT>` local (getOrPut's `else value`);
    //   - a `var` typed VT whose `init` is a bare `Nullable<VT>` local (a spliced `?.let` receiver `__self = tmp_safe`).
    // Same SAFE discrimination as the cast pass: only a `{k:local}` whose DECLARED type is exactly `nullable(VT)` — a
    // struct — is unwrapped (get_Value); a boxed/object operand is left alone. Runs after ALL splicing, so a param-binding
    // var minted by STEP 5 (outside the per-splice STEP-2c pass) is covered too.
    static readonly string[] CondBranchKeys = { "then", "else" };
    static void NormalizeImplicitNullableUnwrap(JsonNode root)
    {
        // Per-METHOD var-type scope: collect + normalize within each function `body` subtree independently, so a
        // same-named local in a SIBLING method (a user `x: Int?` in one, `x: Int` in another) can never cross-alias and
        // wrongly unwrap a non-nullable copy. The first `body`-bearing node owns its whole subtree (nested closures/
        // local fns included) — recursion stops there. (Splice temps are already `_counter`-unique; this hardens the
        // rare user-var case.)
        void FindScopes(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (o["body"] is JsonArray)
                {
                    var vt = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                    CollectVarTypes(o, vt);
                    NormalizeFlowsWith(o, vt);
                }
                else foreach (var kv in o) if (kv.Value != null) FindScopes(kv.Value);
            }
            else if (n is JsonArray a) foreach (var c in a) if (c != null) FindScopes(c);
        }
        FindScopes(root);
    }

    // The Kotlin declaration-site-COVARIANT classes whose covariance is unrepresentable on a CLR (invariant) class, so a
    // `new G<narrow>` stored into a `G<wide>` slot needs the construction widened to match the slot's stack type. Only
    // these two: both are `<out ...>` data classes routinely upcast (`partition`'s `Pair<List,List>` -> the declared
    // `Pair<List(=IReadOnlyList),...>`). NOT a general widen — a `<in T>` class would be UNSOUND to widen this way.
    static readonly HashSet<string> CovariantClasses = new(StringComparer.Ordinal) { "kotlin.Pair", "kotlin.Triple" };

    // A `var X: G<wideArgs> = new G<narrowArgs>` (the `new` directly, or the sole `result` of the init valueBlock): a
    // Kotlin declaration-site-COVARIANT class (`Pair<out A,out B>` from `partition`/`to`, `Triple`) constructed with the
    // concrete element types but stored into a slot typed with the widened supertypes (`List` -> `IReadOnlyList`). A CLR
    // class is INVARIANT, so the concrete `G<narrow>` is not the declared `G<wide>` — it RUNS (each element IS the wider
    // type) but ilverify rejects the stack type (`Pair<List,List>` vs `Pair<IReadOnlyList,IReadOnlyList>`). Adopt the
    // declared slot's args onto the `new`: the ctor params are the class's own `tv{scope:type,i}` vars, so each narrow
    // arg is assignable to its now-wide param, and the frontend already proved `X : G<wide> = new G<narrow>` sound.
    static void WidenCovariantConstruction(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && o["type"] is JsonObject vt && Str(vt["t"]) == "fqn"
                && Str(vt["name"]) is string g && CovariantClasses.Contains(g) && vt["args"] is JsonArray wargs
                && !HasTv(wargs))   // an unconcretized `tv` slot arg would corrupt (or dangle) the construction
            {
                var ctor = o["init"] as JsonObject;
                if (ctor != null && Str(ctor["k"]) == "valueBlock") ctor = ctor["result"] as JsonObject;
                if (ctor != null && Str(ctor["k"]) == "new" && ctor["type"] is JsonObject ntn
                    && Str(ntn["t"]) == "fqn" && Str(ntn["name"]) == g && ntn["args"] is JsonArray nargs
                    && nargs.Count == wargs.Count && !JsonNode.DeepEquals(nargs, wargs)
                    && nargs.All(IsRefTypeArg))   // every narrow arg must be a REFERENCE type: a value-narrow -> ref-wide
                    ntn["args"] = wargs.DeepClone();  // (`Pair<Int,Int> -> Pair<Any,Any>`) needs a box the widened ctor arg
            }                                          // won't get -> unboxed int32 into an object field -> memory corruption.
            foreach (var kv in o) if (kv.Value != null) WidenCovariantConstruction(kv.Value);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) WidenCovariantConstruction(c);
    }

    // F8 Piece 1 — retype ANY local (a SPLICED extension `__self`/`groupByTo`-dest temp, OR a NAMED `val g = groupBy{}`)
    // declared at a covariance-erased INVARIANT-collection type — kotc's use-site `out K`/`in K`->`kotlin.Any`
    // approximation, or a declaration-site covariant value (`Map<K, out V>`: nested `List` erased to `IReadOnlyList`) —
    // but bound to a statically-MORE-CONCRETE same-family value. The concrete value's `stloc` into the invariant local is
    // unverifiable (`Map<Int,…>`/`Map<…,List>` is NOT `Map<Any,…>`/`Map<…,IReadOnlyList>`) and coercing is impossible (a
    // `castclass` to the projection throws at runtime). Retype the local at the arg's actual static type so the store
    // verifies EXACTLY; nested frames fix recursively (their receiver is now this local). ONLY fires on the already-
    // unverifiable concrete-into-projected shape (args DIFFER) — gate-monotone, regression-safe. Runs pre-lowering
    // (kotlin.* FQNs), so `MapVarianceRealign.InvariantCollections` matches. Pairs with Piece 2 (stdlib Any-helpers).
    static void RetypeReceiverToConcrete(JsonNode root)
    {
        StaticType.Refs = _refs;
        StaticType.LocalTypes = StaticType.CollectTypes(root);
        void ProcessMethod(JsonObject method)
        {
            var scope = BirScope.Empty.Child();
            if (method["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                        scope.VarTypes[pn] = pt;
            CollectVarTypesForRetype(method["body"], scope.VarTypes);   // flat seed (splice temps are _counter-unique)
            RetypeVarsWalk(method["body"], scope);
        }
        if (root is JsonObject ro)
        {
            if (ro["methods"] is JsonArray ms) foreach (var m in ms) if (m is JsonObject mo) ProcessMethod(mo);
            if (ro["types"] is JsonArray ts) foreach (var t in ts)
                if (t is JsonObject to && to["methods"] is JsonArray tms) foreach (var m in tms) if (m is JsonObject mo) ProcessMethod(mo);
        }
    }

    static void CollectVarTypesForRetype(JsonNode node, Dictionary<string, TypeNode> vt)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n && TypeJson.Read(o["type"]) is TypeNode t) vt[n] = t;
            foreach (var kv in o) if (kv.Value != null) CollectVarTypesForRetype(kv.Value, vt);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectVarTypesForRetype(c, vt);
    }

    static void RetypeVarsWalk(JsonNode node, BirScope scope)
    {
        if (node is JsonObject o)
        {
            // POST-ORDER (compositional): retype the INNER temps of a var's init FIRST, so this var's guard sees the
            // CONCRETE Surface of an init that itself contains just-retyped temps (a chained `val h = g.mapValues{}` whose
            // concreteness materializes only after g's own receiver temp is retyped is otherwise a one-pass miss). Sibling
            // propagation is preserved: doc-order + this var's scope update lands before the next sibling is visited.
            foreach (var kv in o) if (kv.Value != null) RetypeVarsWalk(kv.Value, scope);
            // Any local (a SPLICED `__self`/dest temp OR a NAMED `val g = groupBy{}`) declared at a covariance-erased
            // invariant-collection type but bound to a statically-MORE-CONCRETE same-family value — the spliced-receiver,
            // helper-param, and named-result stores are all this ONE root. Guard: invariant-collection head + same arity
            // + no-tv + args-differ (fires ONLY on the already-unverifiable concrete-into-projected shape, so a sound
            // `arg == declared` local is untouched — gate-monotone: an already-green method cannot go red). COND-JOIN:
            // skip when the init is a `cond` whose arms disagree — Surface reports only the FIRST arm, so retyping to it
            // could make the other arm's store newly unverifiable.
            if (Str(o["k"]) == "var" && Str(o["name"]) is string vn
                && o["init"] is JsonNode init && CondArmsAgree(init, scope)
                && TypeJson.Read(o["type"]) is TypeNode declT
                && InvariantCollArgs(declT) is TypeNode[] declArgs
                && StaticType.Surface(init, scope) is TypeNode argT && InvariantCollArgs(argT) is TypeNode[] argArgs
                && argArgs.Length == declArgs.Length && !HasTvType(argT)
                && !argArgs.SequenceEqual(declArgs))
            {
                o["type"] = TypeJson.Write(argT);
                scope.VarTypes[vn] = argT;   // propagate the concrete type to a nested frame whose receiver is this temp
            }
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RetypeVarsWalk(c, scope);
    }

    // The type-args of an INVARIANT-collection Fqn (`kotlin.collections.Map<K,V>` -> [K,V]), seeing through nullable/
    // oblivious wrappers; null for anything else. Reuses MapVarianceRealign's canonical invariant-collection set.
    static TypeNode[] InvariantCollArgs(TypeNode t) => t switch
    {
        TypeNode.Nullable n => InvariantCollArgs(n.Of),
        TypeNode.Oblivious ob => InvariantCollArgs(ob.Of),
        TypeNode.Fqn { Args: { } args } f when MapVarianceRealign.InvariantCollections.Contains(f.Name) => args,
        _ => null,
    };

    // COND-JOIN guard: if `node` (unwrapping valueBlock results) is a `cond`, both arms must Surface to the SAME type —
    // `StaticType.Surface` reports only the `then` arm (StaticTypeResolver.cs), so retyping to it could make the `else`
    // arm's store newly unverifiable. True (safe) for a non-cond or a cond whose arms agree.
    static bool CondArmsAgree(JsonNode node, BirScope scope)
    {
        while (node is JsonObject vb && Str(vb["k"]) == "valueBlock" && vb["result"] is JsonNode r) node = r;
        if (node is not JsonObject o || Str(o["k"]) != "cond") return true;
        var thenT = o["then"] is JsonNode tn ? StaticType.Surface(tn, scope) : null;
        var elseT = o["else"] is JsonNode en ? StaticType.Surface(en, scope) : null;
        return thenT != null && elseT != null && thenT == elseT;
    }

    static bool HasTvType(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => HasTvType(n.Of),
        TypeNode.Oblivious ob => HasTvType(ob.Of),
        TypeNode.Array a => HasTvType(a.Elem),
        TypeNode.ByRef b => HasTvType(b.Of),
        TypeNode.Fn fn => HasTvType(fn.Ret) || fn.DelegateParams.Any(HasTvType),   // DelegateParams incl. a `T.() -> R` receiver (#145: stdlib apply/run/with)
        TypeNode.Fqn f => f.Args != null && f.Args.Any(HasTvType),
        _ => false,
    };

    // A construction type-arg that is a REFERENCE type (a plain `{t:fqn}` whose name is not a value-type primitive/struct):
    // safe to widen to a reference supertype with no boxing. A value-type (`kotlin.Int`), a `nullable`(-of-value struct),
    // or a `tv` is NOT — widening its slot to `object`/an interface would leave the unboxed value in a reference field.
    static bool IsRefTypeArg(JsonNode argType) =>
        argType is JsonObject a && Str(a["t"]) == "fqn" && Str(a["name"]) is string nm && !(_refs?.IsValueTypeFqn(nm) ?? false);

    static void NormalizeFlowsWith(JsonNode node, Dictionary<string, JsonNode> varTypes)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "cond" && VtFqnOf(o["type"]) is string condVt)
            {
                foreach (var br in CondBranchKeys)
                    if (o[br] is JsonNode bn && UnwrapNullableLocal(bn, condVt, varTypes) is JsonNode w) o[br] = w;
            }
            else if (k == "var" && VtFqnOf(o["type"]) is string slotVt
                     && o["init"] is JsonNode ini && UnwrapNullableLocal(ini, slotVt, varTypes) is JsonNode wi)
                o["init"] = wi;
            // FINDING 3a: a `setLocal <VT-local> = <Nullable<VT> local>` — the shape RouteReturns MINTS for an early
            // `return <T?-local>` with a concretized value-type return (`setLocal __inlsN$ret(:Int32) = {nullable Int32}`).
            // Without the unwrap ilemit stores the raw struct and later reads its HasValue bit as the value.
            else if (k == "setLocal" && Str(o["name"]) is string sn && varTypes.TryGetValue(sn, out var st)
                     && VtFqnOf(st) is string setVt && o["value"] is JsonNode sv
                     && UnwrapNullableLocal(sv, setVt, varTypes) is JsonNode sw)
                o["value"] = sw;
            // FINDING 3b: a `cast to <VT>` over a Nullable<VT> splice local that STEP-2c NormalizeConcretizedCasts MISSED
            // — a payload PARAM operand (params bind to vars only in STEP 5, AFTER 2c's var-decl-only scan), e.g.
            // `inline fun <T> f(x: T?) = x as T` with T=Int. Same unbox as seq/single; re-tag the cast to nullableValue.
            else if (k == "cast" && VtFqnOf(o["type"]) is string castVt && o["e"] is JsonNode ce
                     && UnwrapNullableLocal(ce, castVt, varTypes) != null)
            {
                o["elem"] = o["type"].DeepClone();
                o.Remove("type");
                o["k"] = "nullableValue";
            }
            foreach (var kv in o) if (kv.Value != null) NormalizeFlowsWith(kv.Value, varTypes);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) NormalizeFlowsWith(c, varTypes);
    }

    // The fqn of a `{t:fqn,name}` type node when it names a (non-nullable) value type, else null.
    static string VtFqnOf(JsonNode typeNode) =>
        typeNode is JsonObject t && Str(t["t"]) == "fqn" && Str(t["name"]) is string nm
            && _refs != null && _refs.IsValueTypeFqn(nm) ? nm : null;

    // A `{k:local}` whose declared type is exactly `nullable(vtFqn)` -> a `nullableValue` (Nullable.get_Value) wrapper;
    // else null (leave the node untouched — a boxed/object or non-matching operand).
    static JsonNode UnwrapNullableLocal(JsonNode value, string vtFqn, Dictionary<string, JsonNode> varTypes)
    {
        if (value is not JsonObject e || Str(e["k"]) != "local" || Str(e["name"]) is not string en) return null;
        // Gate on a SPLICE-MINTED local (`__inls<N>$`/`__inll<N>$`, always `__inl`-prefixed): the raw-Nullable<VT>-into-VT
        // pathology is purely splice-induced (SubstTv concretizing a payload's `V?`), and those temps are `_counter`-unique
        // so they can never alias a same-named user local (which kotc emits under its raw source name). A user's own
        // `val x: Int? ... else x` never hits this — it is not a struct-reinterpret bug.
        if (!en.Contains("__inl", StringComparison.Ordinal)) return null;
        if (!varTypes.TryGetValue(en, out var et) || et is not JsonObject eto) return null;
        if (Str(eto["t"]) != "nullable" || eto["of"] is not JsonObject ofo
            || Str(ofo["t"]) != "fqn" || Str(ofo["name"]) != vtFqn) return null;
        return new JsonObject { ["k"] = "nullableValue", ["elem"] = new JsonObject { ["t"] = "fqn", ["name"] = vtFqn }, ["e"] = value.DeepClone() };
    }

    // Rename each spliced `newSam`/`newClosure` synthClass (and every reference to it — the node's samType/closureType and
    // the class's own self-references) to a per-splice-instance unique name, so divergent instantiations of the same origin
    // class never collide under one name.
    static void RenameSpliceSynthClasses(JsonNode node, int id)
    {
        if (node is JsonObject o)
        {
            if ((Str(o["k"]) == "newSam" || Str(o["k"]) == "newClosure") && o["synthClass"] is JsonObject sc
                && Str(sc["name"]) is string old && !old.EndsWith("$sp" + id, StringComparison.Ordinal))
                RenameFqnRefs(o, old, old + "$sp" + id);
            foreach (var kv in o) if (kv.Value != null) RenameSpliceSynthClasses(kv.Value, id);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RenameSpliceSynthClasses(c, id);
    }

    static void RenameFqnRefs(JsonNode node, string old, string neu)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "fqn" && Str(o["name"]) == old) o["name"] = neu;                                   // a type ref
            else if (Str(o["name"]) == old && (o.ContainsKey("fields") || o.ContainsKey("methods"))) o["name"] = neu;  // the class decl
            foreach (var kv in o) if (kv.Value != null) RenameFqnRefs(kv.Value, old, neu);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) RenameFqnRefs(c, old, neu);
    }

    static bool HasTv(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv") return true;
            foreach (var kv in o) if (kv.Value != null && HasTv(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasTv(c)) return true;
        return false;
    }

    static bool HasNode(JsonNode node, params string[] kinds)
    {
        if (node is JsonObject o)
        {
            if (kinds.Contains(Str(o["k"]))) return true;
            foreach (var kv in o) if (kv.Value != null && HasNode(kv.Value, kinds)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasNode(c, kinds)) return true;
        return false;
    }

    // #43/#63: is a `{k:newDelegate, method:<name>}` target resolvable app-locally (its static `__lambdaN`/`__dflt$lambda$N`
    // lives in a MODULE file-class, so `ldftn` binds) vs a dangling cross-MODULE origin-file token (§4.6)? Resolvable = the
    // target name is a declared file-class method ANYWHERE in the module (`_appLocalMethods` is seeded module-wide, #63/F4)
    // OR a PENDING #34 re-hoist still in `_hoist` (a nested member-inline default fill mints it during the SAME pass,
    // BEFORE the outer §4.4ii materialization sees it — the #43 seam). The materialization-side counterpart of the §4.6
    // `!sameModule` payload-side guard; this now matches ilemit FindStatic's module-wide file-class ldftn universe exactly
    // (a cross-module dangling token still fails loud). NOTE: a bare-name cross-file collision inherits ilemit's pre-existing
    // sig-less first-file-class-match ambiguity — durable fix is provenance-by-signature (Set B, #46).
    static bool IsAppLocalDelegate(JsonObject nd)
    {
        if (Str(nd["method"]) is not string m) return false;
        if (_appLocalMethods.Contains(m)) return true;
        foreach (var h in _hoist) if (h is JsonObject ho && Str(ho["name"]) == m) return true;
        return false;
    }

    // #43: does the subtree hold a `newDelegate` that does NOT resolve app-locally (a dangling origin-file `__lambdaN`,
    // §4.6)? Used by the SUSPEND §4.4ii arm — a same-module re-hoisted delegate is materializable verbatim; only a
    // cross-module token must still be refused loud.
    static bool HasNonAppLocalDelegate(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "newDelegate" && !IsAppLocalDelegate(o)) return true;
            foreach (var kv in o) if (kv.Value != null && HasNonAppLocalDelegate(kv.Value)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasNonAppLocalDelegate(c)) return true;
        return false;
    }

    // Collect every `{k:setLocal,name:X}` WRITING a captured enclosing var (X ∈ `capNames`) at the carrier's OWN frame —
    // skipping a nested `synthClass`. These are the caps that need ref-cell write-through (a bare setLocal write kotc did
    // not box); MaterializeCarrier promotes each to a shared heap cell.
    static void CollectWrittenCaptures(JsonNode node, HashSet<string> capNames, HashSet<string> written)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "setLocal" && Str(o["name"]) is string sn && capNames.Contains(sn)) written.Add(sn);
            bool nested = Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam";
            foreach (var kv in o) if (kv.Value != null && (!nested || kv.Key != "synthClass")) CollectWrittenCaptures(kv.Value, capNames, written);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) CollectWrittenCaptures(c, capNames, written);
    }

    // Dedup one heap-cell class per element-type-JSON per file. The name is a `dotkt$…` unspeakable synthetic (#68); the
    // exact spelling is private to this file (bir2cir emits the registry AND every use site), so it need only be unique per
    // element type — kotc's own cells (a different name space) coexist, SharedSyntheticSynthesis dedups by name.
    static string RefCellNameFor(JsonNode elem)
    {
        var key = elem?.ToJsonString() ?? "null";
        if (_refCellNames.TryGetValue(key, out var name)) return name;
        var mangled = new string(key.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        // Suffix a CONTENT hash of the full key when truncating — a per-file counter would let two DIFFERENT >60-char
        // elem types truncate to the same `…_0`, and the per-assembly (name-keyed) dedup would then unify distinct cells.
        if (mangled.Length > 60) mangled = mangled.Substring(0, 60) + "_" + (uint)StringComparer.Ordinal.GetHashCode(key);
        name = "dotkt$inlmatref$Ref$" + mangled;
        _refCellNames[key] = name;
        return name;
    }

    // Carrier-side ref-cell rewrite: a boxed capture X read `{k:local,name:X}` -> `this.<X>.v` (the cell field's `.v`), and a
    // write `{k:setLocal,name:X,value:E}` -> `setField this.<X>.v = E`. `this.<X>` is the closure FIELD holding the cell
    // (typed as the Ref class). Replaces the node at its slot; does NOT descend into a nested `synthClass` (its X is its own).
    static void RewriteBoxedCaptureInCarrier(JsonNode node, HashSet<string> boxed, Dictionary<string, string> refName, string cname)
    {
        JsonNode CapField(string x) => new JsonObject
        {
            ["k"] = "field",
            ["ownerType"] = TypeJson.Fqn(cname),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = x,
        };
        JsonNode ReadV(string x) => new JsonObject
        {
            ["k"] = "field",
            ["ownerType"] = TypeJson.Fqn(refName[x]),
            ["recv"] = CapField(x),
            ["name"] = "v",
        };
        bool IsBoxedRead(JsonNode v) => v is JsonObject lo && Str(lo["k"]) == "local" && Str(lo["name"]) is string ln && boxed.Contains(ln);
        // Wrap a `{k:setLocal name:X}` into `setField this.<X>.v = <value>`, rewriting the value's boxed reads too — INCLUDING
        // a value that IS itself the bare read (`X = X`), which the slot-replacement recursion would otherwise miss.
        void HandleWrite(JsonObject wo, string wn)
        {
            RewriteBoxedWrite(wo, wn, refName, cname);
            if (IsBoxedRead(wo["value"])) wo["value"] = ReadV(Str(((JsonObject)wo["value"])["name"]));
            else RewriteBoxedCaptureInCarrier(wo["value"], boxed, refName, cname);
        }
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (IsBoxedWrite(a[i], boxed, out var wn)) HandleWrite((JsonObject)a[i], wn);
                else if (IsBoxedRead(a[i])) a[i] = ReadV(Str(((JsonObject)a[i])["name"]));
                else if (a[i] != null) RewriteBoxedCaptureInCarrier(a[i], boxed, refName, cname);
            }
            return;
        }
        if (node is not JsonObject o) return;
        bool nested = Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam";
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys))
        {
            if (nested && key == "synthClass") continue;
            if (IsBoxedWrite(o[key], boxed, out var wn)) HandleWrite((JsonObject)o[key], wn);
            else if (IsBoxedRead(o[key])) o[key] = ReadV(Str(((JsonObject)o[key])["name"]));
            else if (o[key] != null) RewriteBoxedCaptureInCarrier(o[key], boxed, refName, cname);
        }
    }

    static bool IsBoxedWrite(JsonNode v, HashSet<string> boxed, out string name)
    {
        name = null;
        if (v is JsonObject o && Str(o["k"]) == "setLocal" && Str(o["name"]) is string sn && boxed.Contains(sn)) { name = sn; return true; }
        return false;
    }

    // In-place turn a `{k:setLocal,name:X,value:E}` into `{k:setField, ownerType:Ref, recv:this.<X>, name:v, value:E}`. The
    // recv (the closure field holding the cell) is `this.<X>` when boxing a CARRIER-side write (ownerCname != null), or the
    // enclosing local cell `{k:local,name:X}` when boxing an ENCLOSING-scope write (ownerCname == null).
    static void RewriteBoxedWrite(JsonObject o, string x, Dictionary<string, string> refName, string ownerCname)
    {
        JsonNode recv = ownerCname != null
            ? new JsonObject { ["k"] = "field", ["ownerType"] = TypeJson.Fqn(ownerCname), ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = x }
            : new JsonObject { ["k"] = "local", ["name"] = x };
        var value = o["value"];
        foreach (var k in new List<string>(((IDictionary<string, JsonNode>)o).Keys)) o.Remove(k);
        o["k"] = "setField";
        o["ownerType"] = TypeJson.Fqn(refName[x]);
        o["recv"] = recv;
        o["name"] = "v";
        o["value"] = value;
    }

    // §4.4ii ENCLOSING-scope box: for each recorded `var` (a materialized carrier writes it), rewrite — per method/ctor body
    // that OWNS the cell (see BodyHasMaterializedCell) — the decl `var X = init` -> `var X:Ref = new Ref(init)`, every read
    // `{k:local,name:X}` -> `X.v`, and every write `{k:setLocal,name:X}` -> `setField X.v`. The Ref classes are flushed to
    // the file `refTypes` registry (SharedSyntheticSynthesis assembles them).
    static void BoxMaterializedCaptures(JsonNode root)
    {
        if (root is not JsonObject file) return;
        // Flush the Ref-cell classes into the file registry (SharedSyntheticSynthesis builds the `{ var v }` classes).
        var reg = file["refTypes"] as JsonArray;
        if (reg == null) { reg = new JsonArray(); file["refTypes"] = reg; }
        var present = new HashSet<string>(reg.OfType<JsonObject>().Select(e => Str(e["name"])).Where(n => n != null), StringComparer.Ordinal);
        foreach (var (varName, refName, elem) in _boxRequests)
            if (present.Add(refName))
                reg.Add(new JsonObject { ["name"] = refName, ["elem"] = elem.DeepClone() });

        // Rewrite each body that OWNS the materialized cell for a boxed var — i.e. contains a `newClosure`/etc whose
        // `synthClass` has a field {name:X, type:Fqn(refName)} (the cell this pass just minted). Scoping by the cell (not by
        // bare var name) is PRECISE: it boxes exactly the enclosing method that got the materialized carrier, so a same-named
        // var in an unrelated method — including one kotc ALREADY ref-cell-boxed — is never touched (no double-boxing). The
        // var's decl is guaranteed in the same body (a materialized carrier captures it from that scope). Runs BEFORE
        // ClosureSynthesis strips `synthClass`, so the cell field is still visible here.
        foreach (var body in AllBodies(file))
            foreach (var (varName, refName, elem) in _boxRequests)
                if (BodyHasMaterializedCell(body, varName, refName))
                {
                    // Name-based in-method rewrite is safe only when X binds ONE way and no OTHER capturer expects the plain
                    // element type. Refuse the pathological shapes loud (beats a silent type-skew miscompile).
                    if (BoxUnsafeReason(body, varName, refName, elem) is string reason)
                        throw new NotSupportedException($"inline splice §4.4ii: cannot ref-cell-box captured var '{varName}' — {reason}");
                    BoxVarInBody(body, varName, refName, elem);
                }
    }

    // Null if boxing var X in `body` is safe; else a reason. Refuses: (a) a FOREIGN capturer of X — a `synthClass` field or a
    // `newSuspendLambda`/`newSam` capture DESCRIPTOR {name:X, type != Ref} still expecting the plain element (BoxVarInBody
    // would feed it the cell → a type-skew); (b) shadowing — more than one `{k:var,name:X}` decl, or a decl whose type != the
    // cell's element (the rewrite would Ref-wrap the wrong binder).
    static string BoxUnsafeReason(JsonNode body, string x, string refName, JsonNode elem)
    {
        int decls = 0; string reason = null;
        void Rec(JsonNode n)
        {
            if (reason != null) return;
            if (n is JsonObject o)
            {
                if (Str(o["k"]) == "var" && Str(o["name"]) == x)
                {
                    decls++;
                    if (!JsonNode.DeepEquals(o["type"], elem)) reason = $"a same-named local of a different type shadows it (type-ambiguous rewrite)";
                }
                // A capture DESCRIPTOR {name:X, type} (synthClass field OR suspend/sam capture) typed as something OTHER than
                // the cell class is a second capturer that still wants the plain value.
                if ((o["fields"] is JsonArray || o["captures"] is JsonArray) && (o["fields"] ?? o["captures"]) is JsonArray descs)
                    foreach (var dsc in descs.OfType<JsonObject>())
                        if (Str(dsc["name"]) == x && dsc["type"] is JsonObject dt
                            && !(Str(dt["t"]) == "fqn" && Str(dt["name"]) == refName))
                            reason = "a second closure/suspend-lambda captures it (would receive the ref-cell where the plain value is expected)";
                foreach (var kv in o) Rec(kv.Value);
            }
            else if (n is JsonArray a) foreach (var c in a) Rec(c);
        }
        Rec(body);
        if (reason != null) return reason;
        if (decls > 1) return "it is declared more than once in the method (shadowing — ambiguous which binder to box)";
        return null;
    }

    // True if the body contains a `newClosure`/`newSuspendLambda`/`newSam`/`newDelegate`/`inlineLambda` whose `synthClass`
    // declares a FIELD {name:x, type:Fqn(refName)} — the ref-cell capture MaterializeCarrier just minted for `x`.
    static bool BodyHasMaterializedCell(JsonNode node, string x, string refName)
    {
        if (node is JsonObject o)
        {
            if (o["synthClass"] is JsonObject sc && sc["fields"] is JsonArray fs)
                foreach (var f in fs.OfType<JsonObject>())
                    if (Str(f["name"]) == x && f["type"] is JsonObject ft && Str(ft["t"]) == "fqn" && Str(ft["name"]) == refName)
                        return true;
            foreach (var kv in o) if (kv.Value != null && BodyHasMaterializedCell(kv.Value, x, refName)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && BodyHasMaterializedCell(c, x, refName)) return true;
        return false;
    }

    static IEnumerable<JsonArray> AllBodies(JsonObject file)
    {
        if (file["methods"] is JsonArray ms)
            foreach (var m in ms.OfType<JsonObject>()) if (m["body"] is JsonArray b) yield return b;
        if (file["types"] is JsonArray ts)
            foreach (var t in ts.OfType<JsonObject>())
            {
                if (t["methods"] is JsonArray tm) foreach (var m in tm.OfType<JsonObject>()) if (m["body"] is JsonArray b) yield return b;
                if (t["ctors"] is JsonArray tc) foreach (var c in tc.OfType<JsonObject>()) if (c["body"] is JsonArray b) yield return b;
            }
    }


    // Rewrite one boxed var X within a method body: decl -> Ref alloc, reads -> `X.v`, writes -> `setField X.v`. Skips a
    // nested `synthClass` (the closure's own frame) AND the exact `{k:local,name:X}` in a `newClosure`/etc `captures` entry
    // (that passes the CELL by reference and must stay a bare local). Replaces at the parent slot (no re-descent into a
    // freshly-built `{k:local,name:X}` recv).
    static void BoxVarInBody(JsonNode node, string x, string refName, JsonNode elem)
    {
        JsonNode ReadV() => new JsonObject
        {
            ["k"] = "field",
            ["ownerType"] = TypeJson.Fqn(refName),
            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = x },
            ["name"] = "v",
        };
        var refMap = new Dictionary<string, string>(StringComparer.Ordinal) { [x] = refName };
        bool IsRead(JsonNode v) => v is JsonObject lo && Str(lo["k"]) == "local" && Str(lo["name"]) == x;
        bool IsWrite(JsonNode v) => v is JsonObject wo && Str(wo["k"]) == "setLocal" && Str(wo["name"]) == x;
        // Wrap a `{k:setLocal name:X}` into `setField X.v = <value>`, rewriting the value's X reads first — INCLUDING a
        // value that IS the bare read (`X = X`), which the slot-replacement recursion would otherwise miss (silent cell-into-v).
        void HandleWrite(JsonObject wo)
        {
            if (IsRead(wo["value"])) wo["value"] = ReadV();
            else BoxVarInBody(wo["value"], x, refName, elem);
            RewriteBoxedWrite(wo, x, refMap, null);
        }
        if (node is JsonArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (IsWrite(a[i])) HandleWrite((JsonObject)a[i]);
                else if (IsRead(a[i])) a[i] = ReadV();
                else if (a[i] != null) BoxVarInBody(a[i], x, refName, elem);
            }
            return;
        }
        if (node is not JsonObject o) return;
        // The var declaration: `var X = init` -> `var X:Ref = new Ref(init)`.
        if (Str(o["k"]) == "var" && Str(o["name"]) == x)
        {
            var init = o["init"];   // a JSON `null` init parses to a C# null node (as does an absent key)
            var arg = init != null ? init.DeepClone() : new JsonObject { ["k"] = "default", ["type"] = elem.DeepClone() };
            BoxVarInBody(arg, x, refName, elem);   // an init that itself reads X (rare) still routes through the cell
            o["type"] = TypeJson.Fqn(refName);
            o["init"] = new JsonObject { ["k"] = "new", ["type"] = TypeJson.Fqn(refName), ["args"] = new JsonArray { arg } };
            return;
        }
        bool nested = Str(o["k"]) is "inlineLambda" or "newClosure" or "newDelegate" or "newSuspendLambda" or "newSam";
        bool suspendNested = Str(o["k"]) == "newSuspendLambda";
        foreach (var key in new List<string>(((IDictionary<string, JsonNode>)o).Keys))
        {
            // A nested closure's `synthClass` is its own frame; a `newSuspendLambda`'s `body` is redirected by
            // SuspendColdLowering (capture name -> SM field) — do NOT rewrite either here (BoxUnsafeReason already refused a
            // FOREIGN capturer of X, so any suspend lambda reaching here does not capture X).
            if (nested && (key == "synthClass" || (suspendNested && key == "body"))) continue;
            // A capture-ctor-arg `{k:local,name:X}` passes the CELL — leave it bare.
            if (nested && key == "captures" && o[key] is JsonArray caps)
            {
                for (int i = 0; i < caps.Count; i++)
                {
                    if (IsRead(caps[i])) continue;                // the cell, by reference — keep
                    if (IsWrite(caps[i])) HandleWrite((JsonObject)caps[i]);
                    else if (caps[i] != null) BoxVarInBody(caps[i], x, refName, elem);
                }
                continue;
            }
            if (IsWrite(o[key])) HandleWrite((JsonObject)o[key]);
            else if (IsRead(o[key])) o[key] = ReadV();
            else if (o[key] != null) BoxVarInBody(o[key], x, refName, elem);
        }
    }

    // FIX 3 chokepoint: throw if any `callInline` survived the pass. Under splice-all every callInline must be consumed;
    // a survivor (e.g. kotc emitted it WITHOUT a `pc`, so Rewrite's `ContainsKey("pc")` gate skipped it silently) would
    // reach ilemit un-emittable. Fail with the callee + whether it lacked a `pc`, mirroring DefaultArgSplice.
    static void AssertNoUnsplicedInline(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "callInline")
                throw new NotSupportedException(
                    $"inline splice: a `callInline` (callee={Str(o["callee"])}) survived the pass un-spliced"
                    + (o.ContainsKey("pc") ? "" : " — it carries NO `pc`, so Rewrite silently skipped it")
                    + "; ilemit cannot emit a callInline. Fix the kotc emission or the splice gate.");
            foreach (var kv in o) if (kv.Value != null) AssertNoUnsplicedInline(kv.Value);
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) AssertNoUnsplicedInline(c);
    }


    // Like HasNode, but does NOT descend into a nested-fn boundary (a `returnExpr` inside a closure is the closure's).
    static bool HasNodeNonClosure(JsonNode node, string kind)
    {
        if (node is JsonObject o)
        {
            if (IsClosureBoundary(o)) return false;
            if (Str(o["k"]) == kind) return true;
            foreach (var kv in o) if (kv.Value != null && HasNodeNonClosure(kv.Value, kind)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasNodeNonClosure(c, kind)) return true;
        return false;
    }

    // Any `{k:local,name}` whose name is one of `names` (a lambda param that was never bound = aliased/forwarded).
    static bool HasLocalIn(JsonNode node, IEnumerable<string> names)
    {
        var set = names as HashSet<string> ?? new HashSet<string>(names, StringComparer.Ordinal);
        if (set.Count == 0) return false;
        bool Rec(JsonNode nn)
        {
            if (nn is JsonObject o)
            {
                if (Str(o["k"]) == "local" && Str(o["name"]) is string nm && set.Contains(nm)) return true;
                foreach (var kv in o) if (kv.Value != null && Rec(kv.Value)) return true;
            }
            else if (nn is JsonArray a) foreach (var c in a) if (c != null && Rec(c)) return true;
            return false;
        }
        return Rec(node);
    }

    // §4.4(iii) — retire a nested `inlineLambda` carrier's capture DESCRIPTOR whose lambda-param (a `lambdaMap` key) is no
    // longer referenced in that carrier — dead splice residue that would otherwise mint a dangling name-synthesized ctor
    // arg. POST-ORDER (recurse first) so a doubly-nested carrier chain retires bottom-up before its enclosing carrier is
    // tested. Skips the standard boundary set (a nested `synthClass` KEY, a `typeDef` node whole). Only `inlineLambda`
    // descriptors are pruned (never a `newSuspendLambda`'s — positional capValues).
    static void PruneConsumedCarrierCaptures(JsonNode node, Dictionary<string, JsonObject> lambdaMap)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "typeDef") return;
            foreach (var kv in o) if (kv.Value != null && kv.Key != "synthClass") PruneConsumedCarrierCaptures(kv.Value, lambdaMap);
            if (Str(o["k"]) == "inlineLambda" && o["captures"] is JsonArray caps)
            {
                var lamParams = InlineLambdaParamNames(o);
                for (int i = caps.Count - 1; i >= 0; i--)
                    if (caps[i] is JsonObject c && Str(c["name"]) is string cn && lambdaMap.ContainsKey(cn)
                        && !lamParams.Contains(cn)
                        && !(c["outer"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob)
                        && !CarrierStillReferences(o, cn))
                        caps.RemoveAt(i);
            }
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null) PruneConsumedCarrierCaptures(c, lambdaMap);
    }

    // True if an `inlineLambda` carrier still references `cn` in its body/result — a read (`{k:local}`), a write
    // (`{k:setLocal}`), or a TRANSITIVE nested-carrier capture descriptor. Its own `captures`/`params` are excluded (the
    // descriptor under test). Over-approximation is SAFE: keeping a descriptor degrades to the loud IrSanity backstop.
    static bool CarrierStillReferences(JsonObject carrier, string cn)
    {
        var names = new[] { cn };
        return HasLocalIn(carrier["body"], names) || HasLocalIn(carrier["result"], names)
            || HasSetLocalIn(carrier["body"], cn) || HasSetLocalIn(carrier["result"], cn)
            || HasCaptureDescIn(carrier["body"], cn) || HasCaptureDescIn(carrier["result"], cn);
    }

    // Any `{k:setLocal,name:name}` in the subtree — the write-ref complement of HasLocalIn (which sees only reads).
    static bool HasSetLocalIn(JsonNode node, string name)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "setLocal" && Str(o["name"]) == name) return true;
            foreach (var kv in o) if (kv.Value != null && HasSetLocalIn(kv.Value, name)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasSetLocalIn(c, name)) return true;
        return false;
    }

    // The blindness-complement of HasLocalIn: any `inlineLambda`/`newSuspendLambda` whose `captures[].name == name` (a
    // DESCRIPTOR-style capture, invisible to HasLocalIn's `{k:local}` scan). newSam/newClosure captures are VALUE exprs
    // (`{k:local}`/`{k:field}`), already covered by HasLocalIn, so they are not scanned here.
    static bool HasCaptureDescIn(JsonNode node, string name)
    {
        if (node is JsonObject o)
        {
            if ((Str(o["k"]) == "inlineLambda" || Str(o["k"]) == "newSuspendLambda") && o["captures"] is JsonArray caps)
                foreach (var c in caps.OfType<JsonObject>()) if (Str(c["name"]) == name) return true;
            foreach (var kv in o) if (kv.Value != null && HasCaptureDescIn(kv.Value, name)) return true;
        }
        else if (node is JsonArray a) foreach (var c in a) if (c != null && HasCaptureDescIn(c, name)) return true;
        return false;
    }

    // Seeds _nextLabelId (Apply entry). Scans label||goto||brIf DELIBERATELY WIDE (asymmetric with CollectIds's label-only
    // §4.1 narrowing): _nextLabelId must stay strictly above EVERY id in the file so a freshly-minted label can never
    // collide with a caller-loop label that a non-local goto still points at.
    static int MaxLabelId(JsonNode node)
    {
        int max = -1;
        void Rec(JsonNode nn)
        {
            if (nn is JsonObject o)
            {
                var k = Str(o["k"]);
                if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue) max = Math.Max(max, Int(o["id"]));
                foreach (var kv in o) if (kv.Value != null) Rec(kv.Value);
            }
            else if (nn is JsonArray a) foreach (var c in a) if (c != null) Rec(c);
        }
        Rec(node);
        return max;
    }

    static bool IsUnit(JsonNode retType)
    {
        if (retType is JsonObject o && Str(o["t"]) == "fqn")
        {
            var nm = Str(o["name"]);
            return nm == "kotlin.Unit" || nm == "void";
        }
        return retType == null;
    }

    static JsonObject UnitConst() =>
        new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Unit"), ["value"] = null };

    // ---- (1) kotlin.repeat -> counted loop (unchanged) ------------------------------------------------------------

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
    static int Int(JsonNode n) => (n as JsonValue)?.TryGetValue<int>(out var i) == true ? i : 0;
}

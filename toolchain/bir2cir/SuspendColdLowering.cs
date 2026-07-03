// bir2cir — SuspendColdLowering (bundle-6 P2 straight-line + P3 control-flow/generics/try + P3 wave-2a
// instance-members/member-calls): the cold-core suspend -> state-machine transform.
//
// Per docs/design-coroutine-cold-core-task-bridge.md §11 (the LOCKED contract) + the approved plan
// (functional-nibbling-pearl.md "The bir2cir transform"). This pass lowers a Kotlin `suspend fun` into
// the COLD Continuation shape:
//
//   suspend fun f(a): R           (top-level file-class static; extension = leading `__self` param)
//     -- SM class:   <Owner>_f$sm[<tp>] : kotlin.coroutines.clr.internal.ContinuationImpl
//                      fields: int label; [$this for an instance member]; <spilled params/locals/temps>
//                      object invokeSuspend(object result)   // label dispatch + segmented body
//     -- cold entry: object f$dotkt_suspend[<tp>](a, completion: Continuation<Any?>)
//                      { val sm = new <Owner>_f$sm[<tp>]([this,] a, completion); return sm.invokeSuspend(null) }
//     -- suspend main additionally gets a synthesized PLAIN `fun main()` that drains the cold body.
//
// The blueprint is kotc's LIVE CPS engine (BirEmitter.kt:1412-1744 collectCpsVars/spillExpr/emitWhenCps/
// emitWhileCps/emitTryCps), re-implemented over BIR JSON targeting the cold shape. CRITICAL OBSERVATION:
// kotc already FLATTENS `while`/`for`/`do-while` into structured `block`/`label`/`brIf`/`goto` BIR, so
// loops need no re-segmentation here — only `if`/`when` survive as `cond` (ternary) EXPRESSIONS, which
// this pass lowers to label/brIf/goto control flow when they contain a suspension (mirroring emitWhenCps).
//
// The SM resume protocol matches Kotlin/JVM's ContinuationImpl lowering: a single `result` carrier (the
// invokeSuspend parameter), label dispatch that jumps to each post-suspend merge point, a
// `COROUTINE_SUSPENDED` check after each cold call, and a `throwOnFailure(result)` prologue at each merge
// point (the SM-prologue rethrow that surfaces a failed async resume — the CLR analog of the JVM SM's
// `ResultKt.throwOnFailure($result)`).
//
// SUPPORTED: straight-line + control flow across suspension (if/when via cond-lowering, while/for/do-while
// already flat), try/catch where the suspension is in the TRY BODY (two-level dispatch), generic suspend
// funs (`suspend fun <T> f(x): T` -> a generic SM `f$sm<T>`), extension suspend funs (kotc lowers the
// receiver to a `__self` param), INSTANCE suspend MEMBERS (`class C { suspend fun m() }` — the SM carries a
// `$this` field of type C; `this`/implicit-receiver reads become `SM.$this`; the cold entry is an INSTANCE
// method on C so a member direct/no-suspension body keeps `this` verbatim), and MEMBER + cross-file/
// cross-assembly suspend CALLS (`x.g()` callInstance / an owner'd top-level callStatic — rewritten to the
// callee's `<name>$dotkt_suspend` cold shape on the correct receiver; cross-assembly resolved via the
// ref.dll MemberBinding.Suspend flag + the naming convention).
//
// The whole analysis is GLOBAL across the compilation's files (ApplyAll) because a same-assembly cross-file
// suspend call keeps `owner:null` (kotc emits it identically to a same-file call) and a cold entry it names
// may live in another file — so the transformability fixpoint spans every input file.
//
// LEFT UNTOUCHED (rides the existing ilemit throw-stub, zero regression): suspension inside a
// catch/finally block, a nested suspending try, suspend lambdas / closures, a suspending member of a GENERIC
// class (the generic-class SM needs the enclosing class type params threaded — deferred), a static member
// suspend fun inside a class, and any suspend call whose callee cold shape can't be resolved (same-assembly
// non-transformable or a cross-assembly member without a ref.dll Suspend flag). Those keep `"suspend":true`.
//
// Runs AFTER MemberCallSubstitution and BEFORE BirTypeLowering, in app builds only (gated in Pipeline via
// attributeTopLevelOwner; skipped in the ref AND rt-stdlib builds). Its synthesized nodes are emitted in
// the SUBSTITUTED call form but in the kotlin.* TYPE vocabulary, so they flow through BirTypeLowering.

using System.Text.Json.Nodes;

static class SuspendColdLowering
{
    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string SuspendLambdaFqn = "kotlin.coroutines.clr.internal.SuspendLambda";
    // A @RestrictsSuspension-scope (e.g. SequenceScope) suspend lambda's SM base. Same 2-arg (arity, completion)
    // ctor + create() protocol as SuspendLambda; RestrictedContinuationImpl pins EmptyCoroutineContext.
    const string RestrictedSuspendLambdaFqn = "kotlin.coroutines.clr.internal.RestrictedSuspendLambda";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    const string ContinuationOfAny = "kotlin.coroutines.Continuation[kotlin.Any]";
    // BaseContinuationImpl.create returns Continuation<Unit> (ContinuationImpl.kt:82/87); a CLR virtual
    // override needs an EXACT return-type match (no covariance), so the SM's create() must return this,
    // NOT Continuation<Any>. The returned SM value converts by Continuation's `in T` contravariance.
    const string ContinuationOfUnit = "kotlin.coroutines.Continuation[kotlin.Unit]";
    const string IntrinsicsKtFqn = "kotlin.coroutines.intrinsics.IntrinsicsKt";
    // The CLR-interop coroutine bridge file-class (kotlin.clr.await / delay). Its suspend fns are NOT genuine cold
    // coroutine bodies: `await` is a facadegen call-site MARKER (lowered by EmitAwaitPoint at CALL sites, its
    // DEFINITION a plain suspend declaration kept for ref/rt signature symmetry); `delay` is built on it. When the
    // cold transform runs in the rt-stdlib build (bundle-6 P5), these must be EXCLUDED — transforming their
    // definitions into cold entries / Task bridges would manufacture the wrong ABI (Codex-confirmed rt-gate decision).
    const string InteropBridgeFileClass = "kotlin.clr.CoroutinesKt";
    // Top-level `throwOnFailure(result)` helper (ContinuationImpl.kt, package kotlin.coroutines.clr.internal).
    const string ThrowOnFailureOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";

    // Node kinds whose PRESENCE around a suspension disqualifies the fun (leave untouched for the ilemit
    // throw-stub): suspend lambdas / closures / the old kotc CPS/sequence nodes.
    static readonly HashSet<string> LambdaKinds = new(StringComparer.Ordinal)
    {
        "closureNew", "delegateNew", "lambda", "sequenceNew", "forEachInline", "repeatInline",
        "steps", "coClass",
        // Part B: a suspend-lambda VALUE inside a suspend fun disqualifies the enclosing fun from cold
        // transform (its own SM is built separately by SuspendLambdaLowering, which runs after).
        "suspendLambdaNew",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    static string NonEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // The inline `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic marker. kotc's IR inliner has
    // already run when this reaches bir2cir: the `@InlineOnly inline` intrinsic's fake body (a bare
    // `throw NotImplementedError("Implementation of suspendCoroutineUninterceptedOrReturn is intrinsic")`)
    // survives as the `valueBlock` RESULT, and its `{ c -> … }` block is materialized as a separate closure
    // class captured into a dead `var __inlN` in the block's stmts. That message string is the ONLY reliable,
    // stable marker (the frontend never invokes `block`, so no `suspendCall` tag exists). We recognize it here
    // and lower it to a real cold suspension point — inlining the closure's invoke body, binding `c` to the SM.
    // This is kotc's live emitSuspendIntrinsic/coSelfCont (BirEmitter.kt:1669-1688) re-expressed over the cold SM.
    const string SuspendIntrinsicMarker = "suspendCoroutineUninterceptedOrReturn is intrinsic";

    // Is this a `valueBlock` whose result is the intrinsic's fake `throw NotImplementedError(<marker>)`? Such a
    // block IS a suspension point (its embedded closure is the suspension body), NOT an ordinary lambda value.
    static bool IsSuspendIntrinsicBlock(JsonNode node)
    {
        if (node is not JsonObject o || Str(o["k"]) != "valueBlock") return false;
        if (o["result"] is not JsonObject res || Str(res["k"]) != "throwExpr") return false;
        if (res["value"] is not JsonObject nw || Str(nw["k"]) != "new") return false;
        if (Str(nw["type"]) != "kotlin.NotImplementedError") return false;
        return nw["args"] is JsonArray a && a.Count >= 1 && a[0] is JsonObject c0
            && Str(c0["k"]) == "const" && (Str(c0["value"])?.Contains(SuspendIntrinsicMarker) ?? false);
    }

    // The `closureNew` (the `{ c -> … }` block) buried in an intrinsic block's stmts (a `var __inlN` init).
    static JsonObject IntrinsicClosureNew(JsonObject block)
    {
        if (block["stmts"] is JsonArray a)
            foreach (var s in a)
                if (s is JsonObject so && so["init"] is JsonObject init && Str(init["k"]) == "closureNew")
                    return init;
        return null;
    }

    // A suspend fun's identity: Owner=null for a top-level file-class static, else the enclosing class FQN. Sig
    // is the joined param-type list — it discriminates OVERLOADS that share (Owner, Name) (e.g. SequenceScope
    // has three `yieldAll` overloads differing only by param type: Iterator/Iterable/Sequence). Without it the
    // registry would collapse the overloads to one, dropping the others (see SigOf).
    readonly record struct FunKey(string Owner, string Name, string Sig);

    // The param-type signature discriminating overloaded suspend members (see FunKey.Sig).
    static string SigOf(JsonObject m) =>
        m["params"] is JsonArray ps ? string.Join(",", ps.OfType<JsonObject>().Select(p => Str(p["type"]) ?? "")) : "";

    // A shape-eligible suspend fun + where it lives (for cold-entry/SM splicing).
    sealed record Entry(JsonObject Method, JsonObject Root, JsonObject TypeNode, string Owner, string FileClass);

    // A suspend CALL site descriptor (for the resolvability fixpoint).
    readonly record struct CallRef(bool Instance, string Owner, string Name);

    // Returns the callee-return-type map (cold-entry name -> Kotlin resultType), so the SEPARATE
    // SuspendLambdaLowering phase can type a suspend-lambda's awaited value the SAME way (else a
    // lambda's `h()` await falls back to kotlin.Any and the value is never unboxed -> `object + int`).
    public static IReadOnlyDictionary<string, string> ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeFqns)
    {
        // 1. Global registry of shape-eligible suspend funs across every input file.
        var entries = new Dictionary<FunKey, Entry>();
        // Closure-class registry (for the suspendCoroutine intrinsic inliner): kotc materializes the intrinsic's
        // `{ c -> … }` block as a top-level closure class; the inliner resolves its `invoke` body by name here.
        var closures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var r in roots)
            if (r is JsonObject f && f["types"] is JsonArray fts)
                foreach (var t in fts)
                    if (t is JsonObject to && Str(to["name"]) is string tn) closures[tn] = to;
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            var fileClass = Str(file["fileClass"]) ?? "Kt";
            // Skip the CLR-interop bridge (kotlin.clr.await/delay): its suspend fns are call-site markers /
            // interop declarations, not cold coroutine bodies (see InteropBridgeFileClass). Relevant only in the
            // rt-stdlib build, where this file-class is present; a no-op in app builds.
            var isInteropBridge = fileClass == InteropBridgeFileClass;
            if (file["methods"] is JsonArray methods && !isInteropBridge)
                foreach (var m in methods)
                    if (m is JsonObject mo && Str(mo["name"]) is string name && IsShapeEligible(mo))
                        entries[new FunKey(null, name, SigOf(mo))] = new Entry(mo, file, null, null, fileClass);
            if (file["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to && Str(to["name"]) is string owner && to["methods"] is JsonArray tms)
                        foreach (var m in tms)
                            if (m is JsonObject mo && Str(mo["name"]) is string name && IsMemberShapeEligible(mo, to))
                                entries[new FunKey(owner, name, SigOf(mo))] = new Entry(mo, file, to, owner, fileClass);
        }
        // callee-return-type fallback for await-temp field typing when a call node carries no instantiated
        // ret (a bare `one()` has `sig:""`): the callee's declared resultType, keyed by cold-entry name.
        // Built here (before the early returns) so it is ALWAYS returned for the lambda phase's use.
        var calleeRet = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, e) in entries)
            calleeRet[k.Name] = Str(e.Method["resultType"]) ?? "kotlin.Any";

        if (entries.Count == 0) return calleeRet;

        // 2. Fixpoint: a fun stays transformable only if EVERY suspend call it makes is RESOLVABLE — a
        //    same-assembly transformable callee (its cold entry will be synthesized) OR a cross-assembly
        //    callee whose ref.dll MemberBinding.Suspend flag + the naming convention give the cold entry.
        var transformable = new HashSet<FunKey>(entries.Keys);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var key in transformable.ToList())
                foreach (var call in SuspendCalls(entries[key].Method))
                    if (!IsResolvable(call, transformable, refs))
                    {
                        transformable.Remove(key);
                        changed = true;
                        break;
                    }
        }
        if (transformable.Count == 0) return calleeRet;

        var baseIsLocal = localTypeFqns.Contains(ContinuationImplFqn);

        // The public Task<R> bridge (bundle-6 P4, design §11): resolve the Task-family BCL owners from the ref.dll
        // @ClrTypeAlias index (the SAME ref.dll-sourced substitution the member-call pass reads). `TaskCompletionSource`
        // is the sink the RootContinuation completes; `Task` is the hot public return. When either alias is absent from
        // the ref.dll (a build whose stdlib predates the taskinterop set), the bridge is skipped — the cold entry still
        // emits (Kotlin->Kotlin suspend calls are unaffected), only the cross-language Task ABI is dropped.
        var tcsBcl = refs.TryResolveClrOwner("kotlin.clr.TaskCompletionSource", out var tb, out _) ? tb : null;
        var taskBcl = refs.TryResolveClrOwner("kotlin.clr.Task", out var kb, out _) ? kb : null;

        // SM-type-name disambiguation for OVERLOADED members (same Owner+Name, differing only by param type):
        // each overload needs a UNIQUE SM class name (the cold-entry NAME stays `<name>$dotkt_suspend` — they are
        // IL overloads resolved by param type). A group of one keeps the bare `<owner>_<name>$sm` name (existing
        // output unchanged); a group of >1 appends a suffix from the param types' simple names
        // (`_Iterator`/`_Iterable`/`_Sequence`), falling back to a positional index on a residual collision.
        var smSuffix = new Dictionary<FunKey, string>();
        foreach (var g in entries.Keys.GroupBy(k => (k.Owner, k.Name)))
        {
            var members = g.ToList();
            if (members.Count == 1) { smSuffix[members[0]] = ""; continue; }
            var cands = members.Select(k => ParamSimpleNames(entries[k].Method)).ToList();
            var unique = cands.All(c => c.Length > 0) && cands.Distinct(StringComparer.Ordinal).Count() == cands.Count;
            for (var i = 0; i < members.Count; i++)
                smSuffix[members[i]] = "_" + (unique ? cands[i] : "ov" + i);
        }

        // 3. Transform each transformable fun, splicing the cold entry (into its declaring container) and the
        //    SM type (into its file's top-level types).
        foreach (var key in transformable)
        {
            var e = entries[key];
            // The ENCLOSING class's type-param names (for an instance member on a generic class): the SM is made
            // generic over them, `$this` is typed as the CONSTRUCTED self `Box[gp:T]`, and the bridge's self
            // cold-call targets that constructed self (not the open `Box`), or `this` (Box<T>) mismatches the
            // callee's declaring type at verification (StackUnexpected).
            var ownerTps = new List<string>();
            if (e.TypeNode?["typeParams"] is JsonArray otps)
                foreach (var t in otps)
                    if (t is JsonValue tv2 && tv2.TryGetValue<string>(out var s2)) ownerTps.Add(s2);
                    else if (t is JsonObject to2 && Str(to2["name"]) is string n2) ownerTps.Add(n2);
            var gen = new FunGen(e.Method, key.Name, e.FileClass, e.Owner, calleeRet, baseIsLocal, tcsBcl, taskBcl, ownerTps, closures, smSuffix[key]);
            var newMethods = new List<JsonNode>();
            var newTypes = new List<JsonNode>();
            gen.Build(newMethods, newTypes);

            var container = e.TypeNode != null
                ? (e.TypeNode["methods"] as JsonArray) ?? EnsureArray(e.TypeNode, "methods")
                : (e.Root["methods"] as JsonArray) ?? EnsureArray(e.Root, "methods");
            // In an APP build the original suspend method is REPLACED (public Task bridge + cold entry + SM). In the
            // rt-STDLIB build (baseIsLocal) it is RETAINED alongside the cold entry: kotc's pre-ignition
            // @RestrictsSuspension builder path (`sequence{}`/`iterator{}`, e.g. SlidingWindow.windowedIterator)
            // still calls the Task-shaped SequenceScope.yield/yieldAll BY NAME (BirEmitter.kt:2169-2173 routes those
            // restricted builders through the old closure path) — removing the original would break the stdlib
            // build. The kotc-ignition handoff (delete BirEmitter.kt :3329 + isYield/isYieldAll :1640-1657) retires
            // that path; the original then goes dead and a follow-up drops it. ref/rt stay symmetric (both keep the
            // Task `yield`); rt ADDITIONALLY carries the cold entry an app resolves via the ref.dll Suspend flag.
            if (!baseIsLocal)
                for (var i = container.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(container[i], e.Method)) container.RemoveAt(i);
            foreach (var nm in newMethods) container.Add(nm);

            if (newTypes.Count > 0)
            {
                var ts = (e.Root["types"] as JsonArray) ?? EnsureArray(e.Root, "types");
                foreach (var nt in newTypes) ts.Add(nt);
            }

            // A synthesized instance-member SM is a SEPARATE top-level class, so its `$this.<member>` accesses to
            // the enclosing class's PRIVATE members (e.g. SequenceBuilderIterator.yield's SM writing the private
            // `set_nextValue`/`set_state`) fail CLR visibility (MethodAccessException) — on the JVM the SM is a
            // nested class with private access; on CLR our SM is top-level. Widen exactly the private members the
            // SM touches on `$this` to `internal` (assembly-visible) so the same-assembly SM can reach them. The
            // enclosing class is itself internal machinery (SequenceBuilderIterator is a `private class`), so this
            // leaks nothing to the public surface.
            if (e.TypeNode != null) WidenPrivatesAccessedBySm(e.TypeNode, newTypes);
        }
        return calleeRet;
    }

    // Collect every member name a synthesized SM reads/writes through its `$this` field, then relax any matching
    // PRIVATE member on the enclosing type to `internal` so the separate SM class can access it.
    static void WidenPrivatesAccessedBySm(JsonObject typeNode, List<JsonNode> smTypes)
    {
        var accessed = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode n)
        {
            if (n is JsonObject o)
            {
                // A node whose receiver is the SM's `$this` field: a callInstance's `method`, or a field read's `name`.
                if (o["recv"] is JsonObject r && Str(r["k"]) == "field" && Str(r["name"]) == "$this")
                {
                    if (Str(o["method"]) is string mn) accessed.Add(mn);
                    if (Str(o["k"]) is "field" or "setField" && Str(o["name"]) is string fn) accessed.Add(fn);
                }
                foreach (var kv in o) if (kv.Value != null) Collect(kv.Value);
            }
            else if (n is JsonArray a) foreach (var it in a) if (it != null) Collect(it);
        }
        foreach (var t in smTypes) Collect(t);
        if (accessed.Count == 0) return;

        void Relax(JsonObject member)
        {
            if (member != null && Str(member["vis"]) == "private") member["vis"] = "internal";
        }
        if (typeNode["methods"] is JsonArray ms)
            foreach (var m in ms)
                if (m is JsonObject mo && Str(mo["name"]) is string n && accessed.Contains(n)) Relax(mo);
        if (typeNode["fields"] is JsonArray fs)
            foreach (var f in fs)
                if (f is JsonObject fo && Str(fo["name"]) is string n && accessed.Contains(n)) Relax(fo);
        if (typeNode["properties"] is JsonArray ps)
            foreach (var p in ps)
                if (p is JsonObject po)
                {
                    // The SM calls property accessors by their get_/set_ method name; relax the accessor whose
                    // synthesized method name was accessed (and the property itself if it is directly named).
                    if (Str(po["name"]) is string pn)
                    {
                        if (accessed.Contains(pn)) Relax(po);
                        if (accessed.Contains("get_" + pn)) Relax(po["getter"] as JsonObject);
                        if (accessed.Contains("set_" + pn)) Relax(po["setter"] as JsonObject);
                    }
                }
    }

    static JsonArray EnsureArray(JsonObject o, string key)
    {
        var a = new JsonArray();
        o[key] = a;
        return a;
    }

    // The concatenated simple names of a member's param types (`Iterator<T>` -> "Iterator"), used to build a
    // stable, readable SM-name suffix that disambiguates overloaded suspend members.
    static string ParamSimpleNames(JsonObject m)
    {
        if (m["params"] is not JsonArray ps) return "";
        var parts = new List<string>();
        foreach (var p in ps.OfType<JsonObject>())
        {
            var t = Str(p["type"]) ?? "";
            var br = t.IndexOf('['); if (br >= 0) t = t.Substring(0, br);         // strip generic args
            var dot = t.LastIndexOf('.'); if (dot >= 0) t = t.Substring(dot + 1); // simple name
            t = new string(t.Where(char.IsLetterOrDigit).ToArray());             // sanitize to an identifier
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join("", parts);
    }

    // Part B entry: build a suspend-LAMBDA state-machine TYPE from a suspendLambdaNew node's parts (used by
    // SuspendLambdaLowering). Returns null for arity >= 2 — the create()/invoke protocol covers arities 0/1
    // only (JVM parity), so a wider lambda is not expressible v1 and the caller keeps the node (reports it).
    public static JsonObject BuildLambdaSm(string smName, int arity,
        List<(string name, string type)> captures, List<JsonObject> lambdaParams, JsonArray body,
        string resultType, List<string> typeParams, bool baseIsLocal,
        IReadOnlyDictionary<string, string> calleeRet = null, bool restricted = false)
    {
        if (arity is < 0 or > 1) return null;
        var gen = new FunGen(smName, arity, captures ?? new List<(string, string)>(), lambdaParams, body,
            resultType, typeParams,
            calleeRet as Dictionary<string, string> ??
                (calleeRet != null ? new Dictionary<string, string>(calleeRet, StringComparer.Ordinal)
                                   : new Dictionary<string, string>(StringComparer.Ordinal)),
            baseIsLocal, restricted);
        var types = new List<JsonNode>();
        gen.Build(new List<JsonNode>(), types);
        return types.Count > 0 ? (JsonObject)types[0] : null;
    }

    // Can a suspend call site be rewritten to a cold entry? Same-assembly: the callee is in `transformable`
    // (its cold entry gets synthesized here). Cross-assembly: the ref.dll flags the member `suspend`, so the
    // `<name>$dotkt_suspend` convention names the cold entry.
    static bool IsResolvable(CallRef call, HashSet<FunKey> transformable, ReferenceMetadataIndex refs)
    {
        // Match by (owner, name) — the call site carries no resolved overload signature, so a same-assembly
        // callee is resolvable if ANY overload with that owner+name is transformable (its cold entry gets
        // synthesized). Cross-assembly: the ref.dll Suspend flag + the `<name>$dotkt_suspend` convention.
        bool LocalMatch(string owner) => transformable.Any(k => k.Owner == owner && k.Name == call.Name);
        if (call.Instance)
            return LocalMatch(call.Owner) || refs.HasSuspendMember(call.Owner, call.Name);
        // callStatic: owner==null -> same-assembly top-level (possibly cross-file, keyed by name);
        // owner set -> a cross-assembly file-class static (ref.dll flag).
        if (call.Owner == null) return LocalMatch(null);
        return LocalMatch(call.Owner) || refs.HasSuspendMember(call.Owner, call.Name);
    }

    // --- shape gate ------------------------------------------------------------------------------------

    static bool IsShapeEligible(JsonObject m)
    {
        if (!Bool(m["suspend"])) return false;
        if (!Bool(m["static"])) return false;                       // top-level statics + extensions (kotc: __self param)
        if (Bool(m["inline"]) || Bool(m["abstract"])) return false;
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;  // old CPS / sequence path
        if (m["body"] is not JsonArray body) return false;
        return SuspensionsSupported(body, inHandler: false, tryDepth: 0);
    }

    // An INSTANCE suspend member (static==false, lives inside a class). Same structural gate as a top-level
    // fun, minus the static requirement. Now ADMITS (bundle-6 P5 A1b): generic-class members (the SM is threaded
    // the enclosing class type params — `$this` typed as the constructed self, the SM generic over them), and
    // abstract / open / override / virtual members (the cold entry is emitted abstract / virtual / override in
    // lockstep with the original so a virtual `x.g()` resolves to the right override at runtime).
    static bool IsMemberShapeEligible(JsonObject m, JsonObject typeNode)
    {
        if (!Bool(m["suspend"])) return false;
        if (Bool(m["static"])) return false;                        // a static member fun -> deferred
        if (Bool(m["inline"])) return false;
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;
        // A member that is BOTH generic on its own (its own type params) AND on a generic class is deferred v1:
        // the SM would need to thread the union of both param lists (e.g. DeepRecursiveScope<T,R>'s
        // `<U,S> ...callRecursive`), an untested combination not needed for the sequence path. yield/yieldAll
        // (no own type params, only the class's) transform. Kept for abstract members too so a deferred
        // abstract slot and its (equally deferred) overrides stay consistent.
        var ownGeneric = m["typeParams"] is JsonArray mtp && mtp.Count > 0;
        var genericClass = typeNode["typeParams"] is JsonArray tps && tps.Count > 0;
        if (ownGeneric && genericClass) return false;
        // An ABSTRACT suspend member (no body) -> an abstract cold entry `<name>$dotkt_suspend` (Virtual|Abstract,
        // no SM); its concrete overrides fill the slot. Admitted here (no body to structurally validate).
        if (Bool(m["abstract"])) return true;
        if (m["body"] is not JsonArray body) return false;
        return SuspensionsSupported(body, inHandler: false, tryDepth: 0);
    }

    // Validate that every suspension point is in a position this pass can lower. Rejects: suspension in a
    // catch/finally handler, inside a lambda/closure, and a suspending try nested inside another suspending
    // try (the two-level dispatch is single-level v1). Member/cross-assembly suspend CALLS are now allowed —
    // their cold-shape resolvability is decided by the fixpoint, not here.
    static bool SuspensionsSupported(JsonNode node, bool inHandler, int tryDepth)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var k = Str(o["k"]);
                // The inline `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic (a valueBlock whose
                // result is the fake NotImplementedError throw) IS a supported cold suspension point — do NOT
                // descend into its embedded closureNew (which would trip the LambdaKinds refusal below).
                if (IsSuspendIntrinsicBlock(o)) return true;
                // ANY OTHER lambda/closure/sequence node -> unsupported (genuine suspend lambdas, which emit a
                // `closureNew` and are NOT flagged `suspendCall`, are handled separately by SuspendLambdaLowering).
                // Left untouched.
                if (k != null && LambdaKinds.Contains(k))
                    return false;
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"]))
                {
                    if (inHandler) return false;                        // suspension in catch/finally -> unsupported
                }
                if (k == "try")
                {
                    var bodyHasSusp = o["body"] != null && HasSuspension(o["body"]);
                    if (bodyHasSusp && tryDepth > 0) return false;      // nested suspending try -> unsupported (v1)
                    if (!SuspensionsSupported(o["body"] ?? JsonValue.Create(0), inHandler, bodyHasSusp ? tryDepth + 1 : tryDepth))
                        return false;
                    if (o["catches"] is JsonArray cs)
                        foreach (var c in cs)
                            if (c is JsonObject co && !SuspensionsSupported(co["body"] ?? JsonValue.Create(0), inHandler: true, tryDepth))
                                return false;
                    if (o["finally"] != null && !SuspensionsSupported(o["finally"], inHandler: true, tryDepth))
                        return false;
                    return true;
                }
                foreach (var kv in o)
                    if (kv.Value != null && !SuspensionsSupported(kv.Value, inHandler, tryDepth)) return false;
                return true;
            }
            case JsonArray a:
                foreach (var it in a) if (it != null && !SuspensionsSupported(it, inHandler, tryDepth)) return false;
                return true;
            default:
                return true;
        }
    }

    // Every suspend call this method makes, as a CallRef (kind + owner + callee name).
    static IEnumerable<CallRef> SuspendCalls(JsonObject method)
    {
        var seen = new HashSet<CallRef>();
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (Bool(o["suspendCall"]) && Str(o["method"]) is string mn)
                {
                    var k = Str(o["k"]);
                    if (k == "callInstance")
                        seen.Add(new CallRef(true, BareOwner(Str(o["ownerType"])), mn));
                    else if (k == "callStatic")
                        seen.Add(new CallRef(false, BareOwner(Str(o["owner"])), mn));
                }
                foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
            }
            else if (n is JsonArray a)
                foreach (var it in a) if (it != null) Walk(it);
        }
        if (method["body"] is JsonArray body) Walk(body);
        return seen;
    }

    // Strip a generic instantiation suffix from an owner token so a call site's instantiated ownerType
    // (`Box[kotlin.Int]`) matches the registry's bare class key (`Box`) / a ref.dll owner FQN.
    static string BareOwner(string s)
    {
        if (s == null) return null;
        var i = s.IndexOf('[');
        return i >= 0 ? s.Substring(0, i) : s;
    }

    static bool HasSuspension(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"])) return true;
                if (IsSuspendIntrinsicBlock(o)) return true;   // the inline suspendCoroutine intrinsic IS a suspension
                foreach (var kv in o) if (kv.Value != null && HasSuspension(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var it in a) if (it != null && HasSuspension(it)) return true;
                return false;
            default:
                return false;
        }
    }

    // --- per-fun code generation -----------------------------------------------------------------------

    sealed class FunGen
    {
        const string ThisField = "$this";
        // The Task-bridge root sink (a real emitted stdlib class, referenced cross-assembly like ContinuationImpl).
        const string RootContinuationFqn = "kotlin.coroutines.clr.internal.RootContinuation";
        // bundle-6 P4 REVERSE bridge — the facadegen-injected Task.await marker + the BCL awaiter family.
        const string AwaitMarkerOwner = "kotlin.clr.CoroutinesKt";
        const string TaskFqn = "System.Threading.Tasks.Task";
        const string TaskAwaiterFqn = "System.Runtime.CompilerServices.TaskAwaiter";
        const string ActionFqn = "System.Action";

        readonly JsonObject _m;
        readonly string _name;
        readonly string _fileClass;
        readonly string _ownerClass;             // enclosing class FQN for an instance member, else null
        readonly bool _isMember;
        readonly Dictionary<string, string> _calleeRet;
        readonly bool _baseIsLocal;
        // The public Task<R> bridge BCL owners (from the ref.dll @ClrTypeAlias index); null -> no bridge (see ApplyAll).
        readonly string _tcsBcl;
        readonly string _taskBcl;
        readonly List<string> _ownerTypeParams;   // enclosing class type-param names (instance member on a generic class)
        readonly List<string> _smAllTps;           // owner + method type-param names (the SM's own generic params)
        readonly string _selfType;                 // constructed self `Box[gp:T]` (instance member), else _ownerClass/null
        readonly bool _memberAbstract;             // source member is `abstract` -> abstract cold entry, no SM
        readonly bool _memberOverride;             // source member is `override` -> override cold entry (fills base slot)
        readonly bool _memberVirtual;              // source member is `open` -> virtual cold entry (new vtable slot)
        // Closure-class registry (name -> type node) for the suspendCoroutine intrinsic inliner. Empty in lambda mode.
        readonly IReadOnlyDictionary<string, JsonObject> _closures;
        // Lambda mode (bundle-6 P3 wave-2b Part B): a suspend LAMBDA SM (extends SuspendLambda, no cold
        // entry/main-drain, adds the create() override protocol). Left at defaults for the named-fun path.
        readonly bool _isLambda;
        readonly bool _restrictedBase;           // lambda mode: receiver is @RestrictsSuspension -> RestrictedSuspendLambda base
        readonly int _arity;                     // the lambda's own param count (v1: 0 or 1)
        readonly List<(string name, string type)> _captures;   // captured vars -> ctor params + fields
        readonly JsonArray _lambdaBody;          // the lambda's structured body (no `_m` in lambda mode)
        readonly string _smType;                 // bare SM type name
        readonly string _smTypeInst;             // instantiated (`f$sm[gp:T]`) or bare when non-generic
        readonly string _coldName;
        readonly string _resultType;             // Kotlin resultType token ("void" for Unit)
        readonly List<JsonObject> _params;       // original params (extension: leading __self)
        readonly List<string> _typeParams;       // generic type-param names ([] when non-generic)
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        readonly List<(string name, string type)> _fieldDecls = new();
        // Synthesized SM methods for each `task.await()` suspension point (the OnCompleted Action callback
        // that re-drives THIS SM via resumeWith). Populated during body emission; spliced into the SM type.
        readonly List<JsonObject> _awaitResumeMethods = new();

        int _state;                              // resume-state counter (>=1)
        int _label;                              // label id allocator (above kotc's low ids)
        int _condCounter;
        readonly List<(int state, int label)> _dispatch = new();
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        public FunGen(JsonObject m, string name, string fileClass, string ownerClass,
            Dictionary<string, string> calleeRet, bool baseIsLocal, string tcsBcl = null, string taskBcl = null,
            List<string> ownerTypeParams = null, IReadOnlyDictionary<string, JsonObject> closures = null,
            string smNameSuffix = "")
        {
            _m = m; _name = name; _fileClass = fileClass; _ownerClass = ownerClass;
            _isMember = ownerClass != null;
            _calleeRet = calleeRet; _baseIsLocal = baseIsLocal;
            _tcsBcl = tcsBcl; _taskBcl = taskBcl;
            _closures = closures ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _ownerTypeParams = ownerTypeParams ?? new List<string>();
            // Virtuality of the source member (kept in lockstep on the cold entry): an abstract member -> an abstract
            // cold entry (no SM); an override/open member -> an override/virtual cold entry (fills/opens the slot).
            _memberAbstract = _isMember && Bool(m["abstract"]);
            _memberOverride = _isMember && Bool(m["override"]);
            _memberVirtual = _isMember && Bool(m["virtual"]);
            _smType = (ownerClass ?? fileClass) + "_" + name + smNameSuffix + "$sm";
            _coldName = name + "$dotkt_suspend";
            _resultType = Str(m["resultType"]) ?? "void";
            _params = (m["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            _typeParams = ReadTypeParamNames(m["typeParams"]);
            // The SM is generic over the ENCLOSING class's type params (an instance member on a generic class) PLUS
            // the member's own — its fields / `$this` / label reference them. `_selfType` is the constructed self.
            _smAllTps = new List<string>(_ownerTypeParams);
            foreach (var t in _typeParams) if (!_smAllTps.Contains(t)) _smAllTps.Add(t);
            _selfType = _ownerTypeParams.Count == 0 ? _ownerClass
                : _ownerClass + "[" + string.Join(",", _ownerTypeParams.Select(t => "gp:" + t)) + "]";
            _smTypeInst = _smAllTps.Count == 0
                ? _smType
                : _smType + "[" + string.Join(",", _smAllTps.Select(t => "gp:" + t)) + "]";
        }

        // Lambda-mode ctor (Part B). Builds a `<smName> : SuspendLambda` SM from a suspendLambdaNew node's
        // parts. Captures become ctor params + fields; the lambda's own params become fields set by create().
        public FunGen(string smName, int arity, List<(string name, string type)> captures,
            List<JsonObject> lambdaParams, JsonArray body, string resultType, List<string> typeParams,
            Dictionary<string, string> calleeRet, bool baseIsLocal, bool restricted = false)
        {
            _isLambda = true;
            _restrictedBase = restricted;
            _ownerTypeParams = new List<string>();
            _closures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _m = null;
            _arity = arity;
            _captures = captures;
            _lambdaBody = body;
            _name = smName;
            _fileClass = null;
            _ownerClass = null;
            _isMember = false;
            _calleeRet = calleeRet;
            _baseIsLocal = baseIsLocal;
            _smType = smName;
            _coldName = null;
            _resultType = string.IsNullOrEmpty(resultType) ? "void" : resultType;
            _params = lambdaParams ?? new List<JsonObject>();
            _typeParams = typeParams ?? new List<string>();
            _smAllTps = _typeParams;   // a lambda SM has no enclosing-class type params
            _smTypeInst = _typeParams.Count == 0
                ? _smType
                : _smType + "[" + string.Join(",", _typeParams.Select(t => "gp:" + t)) + "]";
        }

        static List<string> ReadTypeParamNames(JsonNode tps)
        {
            var names = new List<string>();
            if (tps is JsonArray a)
                foreach (var t in a)
                    if (t is JsonValue v && v.TryGetValue<string>(out var s)) names.Add(s);
                    else if (t is JsonObject o && Str(o["name"]) is string n) names.Add(n);
            return names;
        }

        int NextLabel() => ++_label;

        public void Build(List<JsonNode> newMethods, List<JsonNode> newTypes)
        {
            if (_memberAbstract)
            {
                // An abstract suspend member -> just the abstract cold-entry DECLARATION (no SM, no bridge, no
                // drain). Concrete overrides fill the slot; a Kotlin virtual `scope.yield(x)` dispatches through it.
                newMethods.Add(ColdEntryAbstract());
                return;
            }

            var body = _isLambda ? _lambdaBody : ((_m["body"] as JsonArray) ?? new JsonArray());
            var hasSuspension = HasSuspension(body);

            if (!_isLambda && !hasSuspension)
            {
                // No suspension point: the cold entry IS the body directly (extra unused completion param,
                // Any? return so a value return boxes). No SM needed. For an instance member the cold entry
                // stays an INSTANCE method on the class, so a `this`/receiver in the body remains valid.
                // (A suspend LAMBDA always becomes an SM even without suspension — its VALUE is the SM instance.)
                newMethods.Add(ColdEntryDirect(body));
                if (_name == "main" && !_isMember) newMethods.Add(DrainMain());
                if (WantsBridge) newMethods.Add(BuildBridge());
                return;
            }

            _label = MaxLabelId(body) + 1000;

            AddField("label", "kotlin.Int");
            if (_isMember) AddField(ThisField, _selfType);          // holds the enclosing (constructed) instance
            if (_isLambda)
                foreach (var (n, t) in _captures) AddField(n, t);   // captured vars -> ctor-set fields
            foreach (var p in _params)
                AddField(Str(p["name"]), Str(p["type"]));           // lambda: create()-set param field(s)
            CollectVarFields(body, inHandler: false);

            var bodyOut = new List<JsonNode>();
            foreach (var s in body) EmitStmt(s, bodyOut);
            if (_resultType is "void" or "kotlin.Unit")
                bodyOut.Add(Ret(NullConst("kotlin.Any")));

            var invoke = new JsonArray();
            foreach (var (state, label) in _dispatch)
                invoke.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(state)), true, label));
            foreach (var st in bodyOut) invoke.Add(st);

            if (_isLambda)
            {
                newTypes.Add(SmTypeLambda(invoke));
                return;
            }

            newTypes.Add(SmType(invoke));
            newMethods.Add(ColdEntrySm());
            if (_name == "main" && !_isMember) newMethods.Add(DrainMain());
            if (WantsBridge) newMethods.Add(BuildBridge());
        }

        // A named (non-lambda), non-`main` suspend fun whose Task-family aliases resolved gets a public Task<R>
        // bridge. `main` is excluded (it is the entry point, drained by the synthesized plain `main`).
        // EXCLUDED too:
        //  - `_baseIsLocal` (the rt-STDLIB build): the bridge's RootContinuation/TCS/Task sinks are the coroutine
        //    primitives being DEFINED here, not external .NET refs — a bridge would `clrNew` a local type as if it
        //    were referenced (NotSupported). The stdlib's own suspend members (yield/yieldAll/callRecursive) are
        //    internal machinery, not C#-facing Task APIs; the bridge is an APP-build concern (consumers of the dll).
        //  - a virtual/abstract/override member: only the cold entry (+ SM) is wanted (a public Task bridge in
        //    vtable lockstep with the override is a separate, unneeded feature v1).
        bool WantsBridge => !_isLambda && _name != "main" && _tcsBcl != null && _taskBcl != null
            && !_baseIsLocal && !(_memberAbstract || _memberOverride || _memberVirtual);

        static int MaxLabelId(JsonNode node)
        {
            int max = 0;
            void Walk(JsonNode n)
            {
                if (n is JsonObject o)
                {
                    var k = Str(o["k"]);
                    if ((k == "label" || k == "goto" || k == "brIf") && o["id"] is JsonValue v && v.TryGetValue<int>(out var id))
                        max = Math.Max(max, id);
                    foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
                }
                else if (n is JsonArray a) foreach (var it in a) if (it != null) Walk(it);
            }
            Walk(node);
            return max;
        }

        void AddField(string name, string type)
        {
            if (name == null || !_fields.Add(name)) return;
            _fieldDecls.Add((name, type ?? "kotlin.Any"));
        }

        void AddFieldTyped(string name, string type)
        {
            if (_fields.Add(name)) _fieldDecls.Add((name, type));
        }

        void CollectVarFields(JsonNode node, bool inHandler)
        {
            switch (node)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
                    // The intrinsic block's `var __inlN` (the dead materialized closure) is inlined away, not
                    // spilled — do NOT collect it as an SM field.
                    if (IsSuspendIntrinsicBlock(o)) return;
                    if (k != null && LambdaKinds.Contains(k)) return;
                    if (k == "var" && !inHandler)
                        AddField(Str(o["name"]), Str(o["type"]));
                    if (k == "try")
                    {
                        CollectVarFields(o["body"] ?? JsonValue.Create(0), inHandler);
                        if (o["catches"] is JsonArray cs)
                            foreach (var c in cs)
                                if (c is JsonObject co) CollectVarFields(co["body"] ?? JsonValue.Create(0), inHandler: true);
                        if (o["finally"] != null) CollectVarFields(o["finally"], inHandler: true);
                        return;
                    }
                    foreach (var kv in o) if (kv.Value != null) CollectVarFields(kv.Value, inHandler);
                    return;
                case JsonArray a:
                    foreach (var it in a) if (it != null) CollectVarFields(it, inHandler);
                    return;
            }
        }

        // ---- statement lowering ----

        void EmitStmt(JsonNode stmt, List<JsonNode> outp)
        {
            if (stmt is not JsonObject o) return;
            switch (Str(o["k"]))
            {
                case "var":
                {
                    var nm = Str(o["name"]);
                    var init = o["init"];
                    var val = init == null ? NullConst(Str(o["type"]) ?? "kotlin.Any") : Rewrite(init, outp);
                    if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                    else outp.Add(new JsonObject { ["k"] = "var", ["name"] = nm, ["type"] = Str(o["type"]), ["init"] = val });
                    break;
                }
                case "setLocal":
                {
                    var nm = Str(o["name"]);
                    var val = Rewrite(o["value"], outp);
                    if (_fields.Contains(nm)) outp.Add(SetField(nm, val));
                    else outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = nm, ["value"] = val });
                    break;
                }
                case "return":
                {
                    var v = o["value"];
                    outp.Add(v == null ? Ret(NullConst("kotlin.Any")) : Ret(Rewrite(v, outp)));
                    break;
                }
                case "exprStmt":
                    outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = Rewrite(o["expr"], outp) });
                    break;
                case "block":
                    if (o["body"] is JsonArray bb) foreach (var s in bb) EmitStmt(s, outp);
                    break;
                case "label":
                case "goto":
                    outp.Add(o.DeepClone());
                    break;
                case "brIf":
                    outp.Add(new JsonObject
                    {
                        ["k"] = "brIf",
                        ["id"] = o["id"]?.DeepClone(),
                        ["on"] = o["on"]?.DeepClone(),
                        ["cond"] = Rewrite(o["cond"], outp),
                    });
                    break;
                case "try":
                    EmitTry(o, outp);
                    break;
                default:
                    outp.Add(Rewrite(o, outp));
                    break;
            }
        }

        void EmitTry(JsonObject o, List<JsonNode> outp)
        {
            var bodyHasSusp = o["body"] != null && HasSuspension(o["body"]);
            if (!bodyHasSusp)
            {
                outp.Add(RewriteTryPlain(o));
                return;
            }
            var tryEntry = NextLabel();
            outp.Add(Label(tryEntry));

            var inner = new List<(int state, int label)>();
            _tryStack.Push((inner, tryEntry));
            var tryBody = new List<JsonNode>();
            if (o["body"] is JsonArray tb) foreach (var s in tb) EmitStmt(s, tryBody);
            _tryStack.Pop();

            var body2 = new JsonArray();
            foreach (var (state, label) in inner)
                body2.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(state)), true, label));
            foreach (var st in tryBody) body2.Add(st);

            var catches = new JsonArray();
            if (o["catches"] is JsonArray cs)
                foreach (var c in cs)
                    if (c is JsonObject co)
                    {
                        var cbody = new List<JsonNode>();
                        if (co["body"] is JsonArray cb) foreach (var s in cb) EmitStmt(s, cbody);
                        var cbodyArr = new JsonArray();
                        foreach (var st in cbody) cbodyArr.Add(st);
                        catches.Add(new JsonObject
                        {
                            ["excType"] = Str(co["excType"]),
                            ["var"] = Str(co["var"]),
                            ["body"] = cbodyArr,
                        });
                    }

            var tryNode = new JsonObject
            {
                ["k"] = "try",
                ["type"] = Str(o["type"]),
                ["body"] = body2,
                ["catches"] = catches,
            };
            if (o["finally"] is JsonArray fin)
            {
                var finOut = new List<JsonNode>();
                foreach (var s in fin) EmitStmt(s, finOut);
                var finArr = new JsonArray();
                foreach (var st in finOut) finArr.Add(st);
                tryNode["finally"] = finArr;
            }
            outp.Add(tryNode);
        }

        JsonObject RewriteTryPlain(JsonObject o)
        {
            var copy = new JsonObject();
            foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : RewriteNoSpill(kv.Value);
            return copy;
        }

        // Rewrite a suspension-free subtree: redirect field reads + `this`, no suspension segments to append.
        JsonNode RewriteNoSpill(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (Str(o["k"]) == "smSelf") return new JsonObject { ["k"] = "this" };
                if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (Str(o["k"]) == "local" && Str(o["name"]) == "__self" && CapturedSelfField() is JsonNode sf0)
                    return sf0;
                if (Str(o["k"]) == "setLocal" && Str(o["name"]) is string sln && _fields.Contains(sln))
                    return SetField(sln, RewriteNoSpill(o["value"]));
                if (Str(o["k"]) == "var" && Str(o["name"]) is string vln && _fields.Contains(vln))
                    return SetField(vln, o["init"] == null
                        ? NullConst(Str(o["type"]) ?? "kotlin.Any") : RewriteNoSpill(o["init"]));
                if (_isMember && Str(o["k"]) == "this")
                    return FieldOf(ThisField, _ownerClass);
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : RewriteNoSpill(kv.Value);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : RewriteNoSpill(it));
                return copy;
            }
            return node?.DeepClone();
        }

        // Rewrite an expression: lower a suspending `cond` to control flow, spill each suspend call (post-order)
        // into a suspension segment + await field, redirect param/local reads to SM field reads, and (for an
        // instance member) redirect `this`/implicit-receiver to the SM's `$this` field. Appends to `outp`.
        JsonNode Rewrite(JsonNode node, List<JsonNode> outp)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                // `smSelf`: the state-machine's OWN identity (`this` in invokeSuspend). Introduced by the intrinsic
                // inliner for the `c`/continuation binding — it must survive the `this`->`$this` member rewrite
                // (a captured `this` means the ENCLOSING receiver -> $this; `c` means the SM itself).
                if (k == "smSelf") return new JsonObject { ["k"] = "this" };
                if (k == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (k == "local" && Str(o["name"]) == "__self" && CapturedSelfField() is JsonNode sf1)
                    return sf1;
                // A `setLocal`/`var` that assigns a SPILLED variable but sits INSIDE an expression subtree (e.g. the
                // `index++` post-increment lowered to `valueBlock { var <unary> = index; index = index+1; <unary> }`)
                // is reached via Rewrite, not the statement-level EmitStmt, so its field-assignment must be redirected
                // here too — else a bare `setLocal index` to an SM FIELD reaches ilemit as `store unknown var index`.
                if (k == "setLocal" && Str(o["name"]) is string sln && _fields.Contains(sln))
                    return SetField(sln, Rewrite(o["value"], outp));
                if (k == "var" && Str(o["name"]) is string vln && _fields.Contains(vln))
                    return SetField(vln, o["init"] == null
                        ? NullConst(Str(o["type"]) ?? "kotlin.Any") : Rewrite(o["init"], outp));
                if (_isMember && k == "this")
                    return FieldOf(ThisField, _ownerClass);
                if (IsSuspendIntrinsicBlock(o))
                    return EmitIntrinsicSuspension(o, outp);
                if ((k == "callStatic" || k == "callInstance") && Bool(o["suspendCall"]))
                    return EmitSuspensionPoint(o, outp);
                if ((k == "clrStatic" || k == "clrGenericStatic") && Bool(o["suspendCall"])
                    && Str(o["type"]) == AwaitMarkerOwner && Str(o["method"]) == "await")
                    return EmitAwaitPoint(o, outp);
                if (k == "cond" && HasSuspension(o))
                    return EmitCondValue(o, outp);
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value, outp);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : Rewrite(it, outp));
                return copy;
            }
            return node?.DeepClone();
        }

        JsonNode EmitCondValue(JsonObject c, List<JsonNode> outp)
        {
            var ty = Str(c["type"]) ?? "kotlin.Any";
            var resultField = "__cond$" + (++_condCounter);
            AddFieldTyped(resultField, ty);
            var elseL = NextLabel();
            var endL = NextLabel();

            var condExpr = Rewrite(c["cond"], outp);
            outp.Add(BrIf(condExpr, false, elseL));
            outp.Add(SetField(resultField, Rewrite(c["then"], outp)));
            outp.Add(Goto(endL));
            outp.Add(Label(elseL));
            outp.Add(SetField(resultField, Rewrite(c["else"], outp)));
            outp.Add(Label(endL));
            return FieldOf(resultField, ty);
        }

        // A suspension point (mirrors kotc emitSuspend): set label, start the cold call passing `this` (the SM,
        // a Continuation) as the callee's completion; if it returns COROUTINE_SUSPENDED, return SUSPENDED
        // (inline); else fall through to the merge label, rethrow a failed resume, store the awaited value.
        JsonNode EmitSuspensionPoint(JsonObject callNode, List<JsonNode> outp)
        {
            var retTok = NonEmpty(Str(callNode["retType"]))
                ?? NonEmpty(Str(callNode["dynRet"]))
                ?? (_calleeRet.TryGetValue(Str(callNode["method"]) ?? "", out var d) ? d : null)
                ?? NonEmpty(Str(callNode["sig"]))
                ?? "kotlin.Any";
            if (retTok is "void" or "kotlin.Unit") retTok = "kotlin.Any";
            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var field = "__aw$" + state;
            AddFieldTyped(field, retTok);

            outp.Add(SetField("label", IntConst(state)));
            outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result", ["value"] = ColdCall(callNode, outp) });
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, false, resumeLabel));
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(field, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        // The inline `suspendCoroutineUninterceptedOrReturn { c -> stmts; tail }` intrinsic, lowered to a real cold
        // suspension point (kotc's emitSuspendIntrinsic/coSelfCont over the cold SM — BirEmitter.kt:1669-1688):
        //   <inlined closure stmts, with `c`->this(SM) and captures->fields>
        //   this.label = state
        //   result = <tail>                          // the block's return value (e.g. COROUTINE_SUSPENDED)
        //   if (result !== COROUTINE_SUSPENDED) goto resumeLabel      // synchronous fast path
        //   return COROUTINE_SUSPENDED
        //   resumeLabel: throwOnFailure(result); __aw = (T)result     // resumed value (or the sync tail)
        // The block receives THIS coroutine's own continuation as `c` — and the SM IS a Continuation, so `c` binds
        // to the SM itself (`smSelf`, surviving the this->$this member rewrite). Captured `this` means the ENCLOSING
        // receiver, so it rewrites to `$this`. For `yield` the tail is a literal COROUTINE_SUSPENDED and the result
        // is Unit (discarded); the general form supports a synchronous value too.
        JsonNode EmitIntrinsicSuspension(JsonObject block, List<JsonNode> outp)
        {
            var closureNew = IntrinsicClosureNew(block);
            var closureType = Str(closureNew?["closureType"]);
            if (closureType == null || !_closures.TryGetValue(closureType, out var closureCls))
            {
                // No resolvable closure body: emit a bare suspension (store `this` as the continuation is impossible
                // without the body). Return SUSPENDED unconditionally — never a genuine input, but keeps the SM valid.
                var s0 = ++_state; var l0 = NextLabel(); RegisterResume(s0, l0);
                outp.Add(SetField("label", IntConst(s0)));
                outp.Add(Ret(Suspended()));
                outp.Add(Label(l0));
                return NullConst("kotlin.Any");
            }

            var invoke = (closureCls["methods"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(m => Str(m["name"]) == "invoke");
            var cParam = (invoke?["params"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is JsonObject cp
                ? Str(cp["name"]) : null;

            // capture[i] binds closure field[i] (declaration order). A field read in the invoke body ->
            // the corresponding capture expression (later rewritten: a captured `this` -> $this).
            var capMap = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            if (closureCls["fields"] is JsonArray flds && closureNew["captures"] is JsonArray caps)
                for (var i = 0; i < flds.Count && i < caps.Count; i++)
                    if (flds[i] is JsonObject fo && Str(fo["name"]) is string fn)
                        capMap[fn] = caps[i];

            var invBody = invoke?["body"] as JsonArray ?? new JsonArray();
            // Split the invoke body: everything but the final `return` is a pre-statement; the return VALUE is the tail.
            JsonNode tail = Suspended();
            var pre = new List<JsonNode>();
            for (var i = 0; i < invBody.Count; i++)
            {
                if (i == invBody.Count - 1 && invBody[i] is JsonObject last && Str(last["k"]) == "return")
                    tail = last["value"] ?? NullConst("kotlin.Any");
                else
                    pre.Add(invBody[i]);
            }

            var retTok = NonEmpty(Str(block["type"])) ?? "kotlin.Any";
            if (retTok is "void" or "kotlin.Unit") retTok = "kotlin.Any";
            var state = ++_state;
            var resumeLabel = NextLabel();
            RegisterResume(state, resumeLabel);
            var field = "__aw$" + state;
            AddFieldTyped(field, retTok);

            // Inlined pre-statements (c->smSelf, captures->exprs), then rewritten (smSelf->this, captures' this->$this).
            foreach (var s in pre)
                EmitStmt(SubstClosure(s, capMap, cParam, closureType), outp);

            outp.Add(SetField("label", IntConst(state)));
            outp.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result",
                ["value"] = Rewrite(SubstClosure(tail, capMap, cParam, closureType), outp) });
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, false, resumeLabel));
            outp.Add(Ret(Suspended()));
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(field, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        // Substitute a closure-class invoke body for inlining: a field read of the closure's own captured field ->
        // its capture expression; the `c`/continuation param -> `smSelf` (the SM itself). Produces a tree still in
        // the ENCLOSING method's vocabulary, which Rewrite/EmitStmt then lower (smSelf->this, captured this->$this).
        JsonNode SubstClosure(JsonNode node, Dictionary<string, JsonNode> capMap, string cParam, string closureType)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k == "field" && Str(o["ownerType"]) == closureType && Str(o["name"]) is string fn
                    && capMap.TryGetValue(fn, out var cap))
                    return cap.DeepClone();
                if (k == "local" && cParam != null && Str(o["name"]) == cParam)
                    return new JsonObject { ["k"] = "smSelf" };
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : SubstClosure(kv.Value, capMap, cParam, closureType);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : SubstClosure(it, capMap, cParam, closureType));
                return copy;
            }
            return node?.DeepClone();
        }

        void RegisterResume(int state, int resumeLabel)
        {
            if (_tryStack.Count == 0)
                _dispatch.Add((state, resumeLabel));
            else
            {
                var top = _tryStack.Peek();
                top.inner.Add((state, resumeLabel));
                _dispatch.Add((state, top.tryEntry));
            }
        }

        // A `task.await()` suspension point (bundle-6 P4 REVERSE bridge, design §4/§5) — the .NET-Task ⇒ Kotlin
        // suspend boundary. The facadegen-injected marker (kotlin.clr.CoroutinesKt.await, suspendCall) becomes the
        // cold-core awaiter dance, structurally IDENTICAL to EmitSuspensionPoint but obtaining the resume value from
        // a TaskAwaiter instead of a cold `$dotkt_suspend` return:
        //
        //   this.<aw> = ((Task<T>)task).GetAwaiter()        // TaskAwaiter<T> (a struct) spilled into an SM field
        //   if (this.<aw>.IsCompleted) goto L_state          // sync fast path — no suspension
        //   this.label = state
        //   this.<aw>.OnCompleted(<Action bound to this.$awaitOnDone$state>)   // flows ExecutionContext
        //   return COROUTINE_SUSPENDED
        //   L_state: <value> = this.<aw>.GetResult()          // throws on a faulted/canceled task
        //
        // The Action callback (a synthesized SM instance method) re-drives THIS SM: `this.resumeWith(Result(null))`.
        // The resumed `result` is only a WAKE TOKEN (Codex-verified Option B): the real value / fault comes from
        // GetResult() at L_state — a fault THROWS there, propagating up through invokeSuspend into
        // BaseContinuationImpl.resumeWith's catch → the completion (exactly the JVM Task.await semantics). So NO
        // throwOnFailure and NO try/catch in the callback are needed. TaskAwaiter is a readonly struct over a task
        // reference, so the field spill/copy is safe (Codex-confirmed).
        JsonNode EmitAwaitPoint(JsonObject awaitNode, List<JsonNode> outp)
        {
            var generic = Str(awaitNode["k"]) == "clrGenericStatic";
            string resultTok = "kotlin.Unit", taskType, awaiterType;
            if (generic)
            {
                resultTok = NonEmpty((awaitNode["typeArgs"] as JsonArray)?.FirstOrDefault() is JsonValue tv
                    && tv.TryGetValue<string>(out var t0) ? t0 : null) ?? "kotlin.Any";
                taskType = "clrg:" + TaskFqn + "[" + resultTok + "]";
                awaiterType = "clrg:" + TaskAwaiterFqn + "[" + resultTok + "]";
            }
            else
            {
                taskType = "clr:" + TaskFqn;
                awaiterType = "clr:" + TaskAwaiterFqn;
            }

            var task = Rewrite((awaitNode["args"] as JsonArray)?[0], outp);

            var state = ++_state;
            var afterLabel = NextLabel();
            RegisterResume(state, afterLabel);

            var awField = "__awaiter$" + state;
            AddFieldTyped(awField, awaiterType);

            // this.<aw> = ((Task<T>)task).GetAwaiter();
            outp.Add(SetField(awField, new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = taskType, ["method"] = "GetAwaiter",
                ["recv"] = task, ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = awaiterType,
            }));
            // if (this.<aw>.IsCompleted) goto L_state;   (sync fast path — no suspension)
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "clrPropGet", ["type"] = awaiterType, ["name"] = "IsCompleted",
                ["static"] = false, ["recv"] = FieldOf(awField, awaiterType), ["retType"] = "kotlin.Boolean",
            }, true, afterLabel));
            // this.label = state; this.<aw>.OnCompleted(<callback Action>); return COROUTINE_SUSPENDED;
            outp.Add(SetField("label", IntConst(state)));
            var cbName = "$awaitOnDone$" + state;
            _awaitResumeMethods.Add(AwaitResumeMethod(cbName));
            outp.Add(new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = awaiterType, ["method"] = "OnCompleted",
                    ["recv"] = FieldOf(awField, awaiterType),
                    ["argTypes"] = new JsonArray { "clr:" + ActionFqn },
                    ["args"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "boundDelegateNew", ["funcType"] = "func:void:",
                            ["ownerType"] = _smTypeInst, ["method"] = cbName, ["virtual"] = false,
                            ["recv"] = new JsonObject { ["k"] = "this" },
                        },
                    },
                    ["ret"] = "void",
                },
            });
            outp.Add(Ret(Suspended()));
            outp.Add(Label(afterLabel));

            // L_state: <value> = this.<aw>.GetResult();   (throws on a faulted/canceled task)
            var getResult = new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = awaiterType, ["method"] = "GetResult",
                ["recv"] = FieldOf(awField, awaiterType), ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = generic ? resultTok : "void",
            };
            if (generic)
            {
                var valField = "__awval$" + state;
                AddFieldTyped(valField, resultTok);
                outp.Add(SetField(valField, getResult));
                return FieldOf(valField, resultTok);
            }
            // Non-generic Task.await(): Unit — GetResult is `void` (side-effecting), the value is Unit.
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = getResult });
            return NullConst("kotlin.Unit");
        }

        // void $awaitOnDone$state() { this.resumeWith(Result(null)); } — the OnCompleted Action target that
        // re-drives THIS SM (Option B WAKE TOKEN; the value/fault flows from GetResult at the resume label). The
        // resumeWith call mirrors the rt-stdlib BaseContinuationImpl form (Continuation<object>.resumeWith,
        // Result(object) construction); ContinuationErasure then normalizes both to the Result<object> slot.
        JsonObject AwaitResumeMethod(string name)
        {
            var resumeCall = new JsonObject
            {
                ["k"] = "callInstance",
                ["ownerType"] = ContinuationOfAny,
                ["virtual"] = true,
                ["recv"] = new JsonObject { ["k"] = "this" },
                ["method"] = "resumeWith",
                ["sig"] = "@kotlin.Result[kotlin.Any]",
                ["retType"] = "void",
                ["args"] = new JsonArray
                {
                    // Result.success(null): the PUBLIC static companion factory (the internal `Result(value)` ctor is
                    // inaccessible cross-assembly, so an app SM cannot `new kotlin.Result`). typeArgs erased to Any by
                    // ContinuationErasure to hit the Result<object> resumeWith slot.
                    new JsonObject
                    {
                        ["k"] = "callStatic", ["owner"] = "kotlin.Result", ["method"] = "success",
                        ["typeArgs"] = new JsonArray { "kotlin.Any" },
                        ["args"] = new JsonArray { NullConst("kotlin.Any") },
                        ["ret"] = "@kotlin.Result[kotlin.Any]",
                    },
                },
            };
            return new JsonObject
            {
                ["name"] = name,
                ["static"] = false,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray(),
                ["ret"] = "void",
                ["body"] = new JsonArray { new JsonObject { ["k"] = "exprStmt", ["expr"] = resumeCall } },
                ["attrs"] = new JsonArray(),
            };
        }

        string FieldType(string name)
        {
            foreach (var (n, t) in _fieldDecls) if (n == name) return t;
            return "kotlin.Any";
        }

        // kotc names a suspend lambda's captured ENCLOSING extension receiver `__outer` (the `<this>` capture-field
        // convention, BirEmitter.kt:2929) yet, INSIDE the lambda body, references that receiver as `local __self`
        // (the enclosing static extension's receiver-param name, via selfSubst — BirEmitter.kt:1308). The two names
        // are the SAME captured value, so a body `__self` read maps to the `__outer` capture FIELD (`this.__outer`).
        // Guarded on `!__self` field: a NAMED-fun cold entry spills its real `__self` PARAM into a `__self` field,
        // which the generic local->field rule already redirects — this alias is only for the lambda-capture mismatch.
        JsonNode CapturedSelfField() =>
            (!_fields.Contains("__self") && _fields.Contains("__outer"))
                ? FieldOf("__outer", FieldType("__outer")) : null;

        // The cold call. Two shapes:
        //   callStatic  -> <method>$dotkt_suspend(<args>, cast(this -> Continuation<Any?>))   (owner preserved)
        //   callInstance-> recv.<method>$dotkt_suspend(<args>, cast(this -> Continuation<Any?>))
        // `this` (the caller SM) is the callee's completion. typeArgs are preserved. Args/receiver are rewritten
        // (spilling nested suspensions, redirecting locals/`this`).
        JsonObject ColdCall(JsonObject callNode, List<JsonNode> outp)
        {
            var method = Str(callNode["method"]) + "$dotkt_suspend";
            var args = new JsonArray();
            if (callNode["args"] is JsonArray oa)
                foreach (var arg in oa) args.Add(arg == null ? null : Rewrite(arg, outp));
            args.Add(new JsonObject
            {
                ["k"] = "cast",
                ["type"] = ContinuationOfAny,
                ["e"] = new JsonObject { ["k"] = "this" },
            });

            JsonObject call;
            if (Str(callNode["k"]) == "callInstance")
            {
                call = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = Str(callNode["ownerType"]),
                    ["virtual"] = Bool(callNode["virtual"]),
                    ["recv"] = Rewrite(callNode["recv"], outp),
                    ["method"] = method,
                    ["args"] = args,
                    ["retType"] = "kotlin.Any",
                };
            }
            else
            {
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = callNode["owner"]?.DeepClone(),
                    ["method"] = method,
                    ["args"] = args,
                    ["ret"] = "kotlin.Any",
                };
            }
            if (callNode["typeArgs"] is JsonArray ta) call["typeArgs"] = ta.DeepClone();
            return call;
        }

        // ---- declaration synthesis ----

        JsonObject SmType(JsonArray invokeBody)
        {
            var fields = new JsonArray();
            foreach (var (n, t) in _fieldDecls)
                fields.Add(new JsonObject { ["name"] = n, ["type"] = t, ["vis"] = "internal" });

            var ctorParams = new JsonArray();
            var ctorBody = new JsonArray();
            if (_isMember)
            {
                ctorParams.Add(new JsonObject { ["name"] = ThisField, ["type"] = _selfType });
                ctorBody.Add(SetField(ThisField, new JsonObject { ["k"] = "local", ["name"] = ThisField }));
            }
            foreach (var p in _params)
            {
                var pn = Str(p["name"]);
                ctorParams.Add(new JsonObject { ["name"] = pn, ["type"] = Str(p["type"]) });
                ctorBody.Add(SetField(pn, new JsonObject { ["k"] = "local", ["name"] = pn }));
            }
            ctorParams.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });

            var invoke = new JsonObject
            {
                ["name"] = "invokeSuspend",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray { new JsonObject { ["name"] = "result", ["type"] = "kotlin.Any" } },
                ["ret"] = "kotlin.Any",
                ["body"] = invokeBody,
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal) invoke["clrOverride"] = BaseContinuationImplFqn;

            var methods = new JsonArray { invoke };
            foreach (var rm in _awaitResumeMethods) methods.Add(rm);

            var type = new JsonObject
            {
                ["name"] = _smType,
                ["kind"] = "class",
                ["abstract"] = false,
                ["vis"] = "public",
                ["isSealed"] = false,
                ["base"] = _baseIsLocal ? ContinuationImplFqn : "clr:" + ContinuationImplFqn,
                ["interfaces"] = new JsonArray(),
                ["fields"] = fields,
                ["ctors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["params"] = ctorParams,
                        ["baseArgs"] = new JsonArray
                        {
                            new JsonObject { ["k"] = "local", ["name"] = "completion" },
                            NullConst("kotlin.coroutines.CoroutineContext"),
                        },
                        ["thisArgs"] = null,
                        ["vis"] = "public",
                        ["body"] = ctorBody,
                    },
                },
                ["methods"] = methods,
                ["properties"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            // The SM is generic over the owner's type params (instance member on a generic class) PLUS the
            // member's own — `$this`/fields/label reference them (see _smAllTps / _smTypeInst).
            if (_smAllTps.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _smAllTps) tp.Add(n);
                type["typeParams"] = tp;
            }
            return type;
        }

        // The suspend-LAMBDA SM (Part B): `<smName>[<tp>] : SuspendLambda`. Mirrors SmType but (1) the base is
        // SuspendLambda (ctor `base(arity, completion)`), (2) the ctor params are the CAPTURES (+ completion),
        // NOT the lambda's own params, and (3) it adds the create() override(s) — the cold-lambda VALUE protocol
        // (createCoroutineUnintercepted / startCoroutineUninterceptedOrReturn: IntrinsicsClr.kt:42-56). The
        // lambda's own params are fields set by create() on the fresh instance, not by the ctor.
        JsonObject SmTypeLambda(JsonArray invokeBody)
        {
            var lambdaBaseFqn = _restrictedBase ? RestrictedSuspendLambdaFqn : SuspendLambdaFqn;
            var fields = new JsonArray();
            foreach (var (n, t) in _fieldDecls)
                fields.Add(new JsonObject { ["name"] = n, ["type"] = t, ["vis"] = "internal" });

            var ctorParams = new JsonArray();
            var ctorBody = new JsonArray();
            foreach (var (n, t) in _captures)
            {
                ctorParams.Add(new JsonObject { ["name"] = n, ["type"] = t });
                ctorBody.Add(SetField(n, new JsonObject { ["k"] = "local", ["name"] = n }));
            }
            ctorParams.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });

            var invoke = new JsonObject
            {
                ["name"] = "invokeSuspend",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray { new JsonObject { ["name"] = "result", ["type"] = "kotlin.Any" } },
                ["ret"] = "kotlin.Any",
                ["body"] = invokeBody,
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal) invoke["clrOverride"] = BaseContinuationImplFqn;

            var methods = new JsonArray { invoke };
            foreach (var cm in CreateMethods()) methods.Add(cm);
            foreach (var rm in _awaitResumeMethods) methods.Add(rm);

            var type = new JsonObject
            {
                ["name"] = _smType,
                ["kind"] = "class",
                ["abstract"] = false,
                ["vis"] = "public",
                ["isSealed"] = false,
                ["base"] = _baseIsLocal ? lambdaBaseFqn : "clr:" + lambdaBaseFqn,
                ["interfaces"] = new JsonArray(),
                ["fields"] = fields,
                ["ctors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["params"] = ctorParams,
                        // (Restricted)SuspendLambda(arity: Int, completion: Continuation<Any?>?) — the 2-arg base ctor.
                        ["baseArgs"] = new JsonArray
                        {
                            IntConst(_arity),
                            new JsonObject { ["k"] = "local", ["name"] = "completion" },
                        },
                        ["thisArgs"] = null,
                        ["vis"] = "public",
                        ["body"] = ctorBody,
                    },
                },
                ["methods"] = methods,
                ["properties"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                type["typeParams"] = tp;
            }
            return type;
        }

        // The create() override(s) — a fresh SM bound to a new completion, carrying THIS SM's captures.
        //   arity-0:  create(completion): Continuation            -> new SM(captures..., completion)
        //   arity-1:  create(value, completion): Continuation     -> sm = new SM(captures..., completion);
        //                                                             sm.<param> = value; return sm
        // Matches BaseContinuationImpl.create's erased CLR ABI: params (Continuation<object>) / (object,
        // Continuation<object>), return Continuation<object> (Unit-as-typearg erases to object). ilemit binds
        // the base slot by name + param types (clrOverride), so the param types MUST match exactly.
        IEnumerable<JsonObject> CreateMethods()
        {
            if (_arity == 0)
            {
                yield return CreateMethod(
                    new JsonArray { new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny } },
                    new JsonArray { Ret(NewSm()) });
            }
            else
            {
                // arity-1: the lambda's single own param (extension receiver OR value) -> a field set here.
                // The `value` param is erased `object`; storing it into the (possibly value-typed) param field
                // needs an explicit unbox/castclass (ilemit's setField does not auto-coerce object -> value) —
                // the same `cast` wrap FunGen uses for await fields. A kotlin.Any field takes the value verbatim.
                var paramName = Str(_params[0]["name"]);
                var paramType = Str(_params[0]["type"]) ?? "kotlin.Any";
                JsonNode storedValue = paramType == "kotlin.Any"
                    ? new JsonObject { ["k"] = "local", ["name"] = "value" }
                    : new JsonObject { ["k"] = "cast", ["type"] = paramType, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "value" } };
                yield return CreateMethod(
                    new JsonArray
                    {
                        new JsonObject { ["name"] = "value", ["type"] = "kotlin.Any" },
                        new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny },
                    },
                    new JsonArray
                    {
                        new JsonObject
                        {
                            ["k"] = "var",
                            ["name"] = "__sm",
                            ["type"] = _smTypeInst,
                            ["init"] = NewSm(),
                        },
                        new JsonObject
                        {
                            ["k"] = "setField",
                            ["ownerType"] = _smTypeInst,
                            ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "__sm" },
                            ["name"] = paramName,
                            ["value"] = storedValue,
                        },
                        Ret(new JsonObject { ["k"] = "local", ["name"] = "__sm" }),
                    });
            }
        }

        // `new SM(this.cap1, ..., this.capN, completion)` — captures read from THIS SM's fields (create runs on
        // the template SM); the `completion` local is the create() parameter.
        JsonObject NewSm()
        {
            var args = new JsonArray();
            var argTypes = new JsonArray();
            foreach (var (n, t) in _captures) { args.Add(FieldOf(n, t)); argTypes.Add(t); }
            args.Add(new JsonObject { ["k"] = "local", ["name"] = "completion" });
            argTypes.Add(ContinuationOfAny);
            return new JsonObject { ["k"] = "new", ["type"] = _smTypeInst, ["argTypes"] = argTypes, ["args"] = args };
        }

        JsonObject CreateMethod(JsonArray createParams, JsonArray body)
        {
            var m = new JsonObject
            {
                ["name"] = "create",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = createParams,
                ["ret"] = ContinuationOfUnit,
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal) m["clrOverride"] = BaseContinuationImplFqn;
            return m;
        }

        // object f$dotkt_suspend[<tp>](params..., completion) {
        //   val sm = new SM[<tp>]([this,] params..., completion); return sm.invokeSuspend(null) }
        JsonObject ColdEntrySm()
        {
            var ctorArgs = new JsonArray();
            if (_isMember) ctorArgs.Add(new JsonObject { ["k"] = "this" });
            foreach (var p in _params) ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
            ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = "completion" });
            var argTypes = new JsonArray();
            if (_isMember) argTypes.Add(_selfType);
            foreach (var p in _params) argTypes.Add(Str(p["type"]));
            argTypes.Add(ContinuationOfAny);

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var",
                    ["name"] = "__sm",
                    ["type"] = _smTypeInst,
                    ["init"] = new JsonObject { ["k"] = "new", ["type"] = _smTypeInst, ["argTypes"] = argTypes, ["args"] = ctorArgs },
                },
                Ret(new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = _smTypeInst,
                    ["virtual"] = true,
                    ["recv"] = new JsonObject { ["k"] = "local", ["name"] = "__sm" },
                    ["method"] = "invokeSuspend",
                    ["sig"] = "kotlin.Any",
                    ["args"] = new JsonArray { NullConst("kotlin.Any") },
                    ["retType"] = "kotlin.Any",
                }),
            };
            return ColdMethod(body);
        }

        JsonObject ColdEntryDirect(JsonArray body)
        {
            // For an instance member the cold entry stays an instance method — `this` in the cloned body remains
            // valid. For a top-level fun the body has no `this`. Either way no rewrite is needed (no suspension).
            return ColdMethod((JsonArray)body.DeepClone());
        }

        JsonObject ColdMethod(JsonArray body)
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });
            var method = new JsonObject
            {
                ["name"] = _coldName,
                ["static"] = !_isMember,
                // Lockstep virtuality: an override cold entry fills the base slot (Virtual, no NewSlot); an `open`
                // member's cold entry opens a new virtual slot; a final member stays non-virtual (unchanged).
                ["override"] = _memberOverride,
                ["virtual"] = _memberVirtual,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = "kotlin.Any",
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                method["typeParams"] = tp;
            }
            return method;
        }

        // An abstract member's cold entry: `<name>$dotkt_suspend(params..., completion): Any?`, Virtual|Abstract,
        // no body/SM. Concrete overrides emit an `override:true` ColdMethod filling this slot. On a generic class
        // the params keep the class type params verbatim (the cold entry is an instance method of that class).
        JsonObject ColdEntryAbstract()
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });
            var method = new JsonObject
            {
                ["name"] = _coldName,
                ["static"] = false,
                ["override"] = _memberOverride,
                ["virtual"] = true,
                ["abstract"] = true,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = "kotlin.Any",
                ["body"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                method["typeParams"] = tp;
            }
            return method;
        }

        // A synthesized PLAIN `fun main(...)` (no `suspend`) that drains the cold body. v1: pass a null
        // completion — a fully-synchronous body completes inline (never returns SUSPENDED); the real
        // TCS/blockOn drain that supports genuine async resumption lands in P4.
        JsonObject DrainMain()
        {
            var ps = new JsonArray();
            var fwd = new JsonArray();
            foreach (var p in _params) { ps.Add(p.DeepClone()); fwd.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) }); }
            fwd.Add(NullConst(ContinuationOfAny));
            return new JsonObject
            {
                ["name"] = "main",
                ["static"] = true,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = "kotlin.Unit",
                ["body"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["k"] = "exprStmt",
                        ["expr"] = new JsonObject
                        {
                            ["k"] = "callStatic",
                            ["owner"] = null,
                            ["method"] = _coldName,
                            ["args"] = fwd,
                            ["ret"] = "kotlin.Any",
                        },
                    },
                },
                ["attrs"] = new JsonArray(),
            };
        }

        // ---- the public Task<R> bridge (bundle-6 P4, design §11) ----
        //
        //   public Task<R> f(args...) {
        //     var __tcs  = new TaskCompletionSource<R>();
        //     var __root = new RootContinuation<R>(__tcs);   // : Continuation<Any> (post ContinuationErasure)
        //     var __r    = COROUTINE_SUSPENDED;               // object
        //     try { __r = f$dotkt_suspend(args..., (Continuation<Any>)__root); }
        //     catch (e: Throwable) { __tcs.TrySetException(e); __r = COROUTINE_SUSPENDED; }
        //     if (__r !== COROUTINE_SUSPENDED) __tcs.TrySetResult((R)__r);   // sync-completion fast path
        //     return __tcs.Task;
        //   }
        //
        // Sync/async completions are mutually exclusive by the coroutine contract: a non-SUSPENDED cold return means the
        // body completed inline (complete the TCS here); a SUSPENDED return means the eventual resume lands in
        // RootContinuation.resumeWith, which completes the TCS. A synchronous throw is caught and faults the TCS.
        // R = Unit/void is treated uniformly as kotlin.Unit (the cold entry returns null for a Unit body; `(Unit)null`
        // is null, matching what RootContinuation.resumeWith stores for the async Unit path — the two agree). The bridge
        // carries `suspendBridge:true` so ilemit stamps [KotlinFunction(Suspend)] (a re-consuming Kotlin sees `suspend fun`).
        JsonObject BuildBridge()
        {
            var rKotlin = _resultType is "void" or "kotlin.Unit" ? "kotlin.Unit" : _resultType;
            var tcsType = "clrg:" + _tcsBcl + "[" + rKotlin + "]";
            var taskType = "clrg:" + _taskBcl + "[" + rKotlin + "]";
            var rootType = "clrg:" + RootContinuationFqn + "[" + rKotlin + "]";

            var body = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__tcs", ["type"] = tcsType,
                    ["init"] = new JsonObject { ["k"] = "clrNew", ["type"] = tcsType, ["argTypes"] = new JsonArray(), ["args"] = new JsonArray() },
                },
                new JsonObject
                {
                    ["k"] = "var", ["name"] = "__root", ["type"] = rootType,
                    ["init"] = new JsonObject
                    {
                        ["k"] = "clrNew", ["type"] = rootType,
                        ["argTypes"] = new JsonArray { tcsType },
                        ["args"] = new JsonArray { Local("__tcs") },
                    },
                },
                new JsonObject { ["k"] = "var", ["name"] = "__r", ["type"] = "kotlin.Any", ["init"] = Suspended() },
                new JsonObject
                {
                    ["k"] = "try",
                    ["type"] = "void",
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = BridgeColdCall() },
                    },
                    ["catches"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["excType"] = "kotlin.Throwable",
                            ["var"] = "__e",
                            ["body"] = new JsonArray
                            {
                                new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetException", "kotlin.Throwable", Local("__e")) },
                                new JsonObject { ["k"] = "setLocal", ["name"] = "__r", ["value"] = Suspended() },
                            },
                        },
                    },
                },
            };

            var skipL = NextLabel();
            body.Add(BrIf(new JsonObject { ["k"] = "objEq", ["l"] = Local("__r"), ["r"] = Suspended() }, true, skipL));
            JsonNode resultVal = rKotlin == "kotlin.Any"
                ? Local("__r")
                : new JsonObject { ["k"] = "cast", ["type"] = rKotlin, ["e"] = Local("__r") };
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = TcsCall(tcsType, "TrySetResult", rKotlin, resultVal) });
            body.Add(Label(skipL));
            body.Add(Ret(new JsonObject
            {
                ["k"] = "clrPropGet", ["type"] = tcsType, ["name"] = "Task", ["static"] = false,
                ["recv"] = Local("__tcs"), ["retType"] = taskType,
            }));

            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            var method = new JsonObject
            {
                ["name"] = _name,
                ["static"] = !_isMember,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["suspendBridge"] = true,
                ["vis"] = "public",
                ["params"] = ps,
                ["ret"] = taskType,
                ["body"] = body,
                ["attrs"] = new JsonArray(),
            };
            if (_typeParams.Count > 0)
            {
                var tp = new JsonArray();
                foreach (var n in _typeParams) tp.Add(n);
                method["typeParams"] = tp;
            }
            return method;
        }

        // The bridge's cold-entry call: forward the bridge params + the RootContinuation (cast to the erased
        // Continuation<Any> completion). typeArgs thread the bridge's own generic params to the generic cold entry.
        JsonObject BridgeColdCall()
        {
            var args = new JsonArray();
            foreach (var p in _params) args.Add(Local(Str(p["name"])));
            args.Add(new JsonObject { ["k"] = "cast", ["type"] = ContinuationOfAny, ["e"] = Local("__root") });

            // On a GENERIC enclosing class the callee's declaring type is the CONSTRUCTED self `Box[gp:T]` (matching
            // `this`), never the open `Box` — else verification rejects the recv type.
            var selfOwner = _ownerTypeParams.Count == 0
                ? _ownerClass
                : _ownerClass + "[" + string.Join(",", _ownerTypeParams.Select(t => "gp:" + t)) + "]";

            JsonObject call;
            if (_isMember)
                call = new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = selfOwner,
                    ["virtual"] = false,
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["method"] = _coldName,
                    ["args"] = args,
                    ["retType"] = "kotlin.Any",
                };
            else
                call = new JsonObject
                {
                    ["k"] = "callStatic",
                    ["owner"] = null,
                    ["method"] = _coldName,
                    ["args"] = args,
                    ["ret"] = "kotlin.Any",
                };
            if (_typeParams.Count > 0)
            {
                var ta = new JsonArray();
                foreach (var n in _typeParams) ta.Add("gp:" + n);
                call["typeArgs"] = ta;
            }
            return call;
        }

        // A substituted @ClrIntrinsic instance call on the TaskCompletionSource<R> sink (TrySetResult/TrySetException).
        // Emitted post-MemberCallSubstitution, so already in the BCL-owner clrInstance form (result discarded via exprStmt).
        JsonObject TcsCall(string tcsType, string method, string argType, JsonNode arg) => new()
        {
            ["k"] = "clrInstance",
            ["type"] = tcsType,
            ["method"] = method,
            ["recv"] = Local("__tcs"),
            ["argTypes"] = new JsonArray { argType },
            ["args"] = new JsonArray { arg },
            ["ret"] = "kotlin.Boolean",
        };

        static JsonObject Local(string name) => new() { ["k"] = "local", ["name"] = name };

        // ---- small node builders ----

        JsonObject SetField(string name, JsonNode value) => new()
        {
            ["k"] = "setField",
            ["ownerType"] = _smTypeInst,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["value"] = value,
        };

        JsonObject FieldOf(string name, string type) => new()
        {
            ["k"] = "field",
            ["ownerType"] = _smTypeInst,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["retType"] = type,
        };

        static JsonObject Suspended() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = IntrinsicsKtFqn,
            ["method"] = "get_COROUTINE_SUSPENDED",
            ["args"] = new JsonArray(),
            ["ret"] = "kotlin.Any",
        };

        static JsonObject ThrowOnFailure() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = ThrowOnFailureOwner,
            ["method"] = "throwOnFailure",
            ["args"] = new JsonArray { new JsonObject { ["k"] = "local", ["name"] = "result" } },
            ["ret"] = "void",
        };

        static JsonObject Ret(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
        static JsonObject IntConst(int v) => new() { ["k"] = "const", ["type"] = "kotlin.Int", ["value"] = v };
        static JsonObject NullConst(string type) => new() { ["k"] = "const", ["type"] = type, ["value"] = null };
        static JsonObject Label(int id) => new() { ["k"] = "label", ["id"] = id };
        static JsonObject Goto(int id) => new() { ["k"] = "goto", ["id"] = id };
        static JsonObject BrIf(JsonNode cond, bool on, int id) => new()
            { ["k"] = "brIf", ["cond"] = cond, ["on"] = on, ["id"] = id };
        static JsonObject BinEq(JsonNode l, JsonNode r) => new()
            { ["k"] = "bin", ["op"] = "==", ["l"] = l, ["r"] = r };
    }
}

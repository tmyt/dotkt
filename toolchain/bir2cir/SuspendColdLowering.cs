// bir2cir — SuspendColdLowering (bundle-6 P2 straight-line + P3 control-flow/generics/try): the
// cold-core suspend -> state-machine transform.
//
// Per docs/design-coroutine-cold-core-task-bridge.md §11 (the LOCKED contract) + the approved plan
// (functional-nibbling-pearl.md "The bir2cir transform"). This pass lowers a Kotlin `suspend fun` into
// the COLD Continuation shape:
//
//   suspend fun f(a): R           (top-level file-class static; extension = leading `__self` param)
//     -- SM class:   <FileClass>_f$sm[<tp>] : kotlin.coroutines.clr.internal.ContinuationImpl
//                      fields: int label; <spilled params/locals/await-temps/cond-temps>
//                      object invokeSuspend(object result)   // label dispatch + segmented body
//     -- cold entry: object f$dotkt_suspend[<tp>](a, completion: Continuation<Any?>)
//                      { val sm = new <FileClass>_f$sm[<tp>](a, completion); return sm.invokeSuspend(null) }
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
// SUPPORTED (P3): straight-line + control flow across suspension (if/when via cond-lowering,
// while/for/do-while already flat), try/catch where the suspension is in the TRY BODY (two-level
// dispatch — the outer dispatch enters the try, an inner dispatch inside the try body resumes at the
// suspension), generic suspend funs (`suspend fun <T> f(x): T` -> a generic SM `f$sm<T>`), and extension
// suspend funs (kotc lowers the receiver to a `__self` param -> handled as an ordinary param field).
// The SUSPENDED exit is emitted INLINE (`if (result===SUSPENDED) return SUSPENDED`) rather than a shared
// out-of-region goto, so a suspension inside a `.try` returns via ilemit's structured-try leave without
// any cross-region branch (no ilemit change needed).
//
// LEFT UNTOUCHED (rides the existing ilemit throw-stub, zero regression): suspension inside a
// catch/finally block, a nested suspending try, suspend lambdas / closures, member/cross-assembly suspend
// calls (owner'd callStatic / callInstance suspendCall), instance suspend MEMBERS (static==false, live in
// `types`). Those keep `"suspend":true` and are P3-wave2/P4 handoff items.
//
// Runs AFTER MemberCallSubstitution and BEFORE BirTypeLowering, in app builds only (gated in Pipeline via
// attributeTopLevelOwner; skipped in the ref AND rt-stdlib builds). Its synthesized nodes are emitted in
// the SUBSTITUTED call form but in the kotlin.* TYPE vocabulary, so they flow through BirTypeLowering.

using System.Text.Json.Nodes;

static class SuspendColdLowering
{
    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    const string ContinuationOfAny = "kotlin.coroutines.Continuation[kotlin.Any]";
    const string IntrinsicsKtFqn = "kotlin.coroutines.intrinsics.IntrinsicsKt";
    // Top-level `throwOnFailure(result)` helper (ContinuationImpl.kt, package kotlin.coroutines.clr.internal).
    const string ThrowOnFailureOwner = "kotlin.coroutines.clr.internal.ContinuationImplKt";

    // Node kinds whose PRESENCE around a suspension disqualifies the fun (leave untouched for the ilemit
    // throw-stub): suspend lambdas / closures / the old kotc CPS/sequence nodes.
    static readonly HashSet<string> LambdaKinds = new(StringComparer.Ordinal)
    {
        "closureNew", "delegateNew", "lambda", "sequenceNew", "forEachInline", "repeatInline",
        "steps", "coClass",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
    static string NonEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeFqns)
    {
        if (root is not JsonObject file) return;
        if (file["methods"] is not JsonArray methods) return;
        var fileClass = Str(file["fileClass"]) ?? "Kt";

        // 1. Candidate top-level static suspend funs eligible BY SHAPE (independent of their callees).
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var eligibleShape = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            if (m is not JsonObject mo) continue;
            var name = Str(mo["name"]);
            if (name == null) continue;
            byName[name] = mo;
            if (IsShapeEligible(mo)) eligibleShape.Add(name);
        }
        if (eligibleShape.Count == 0) return;

        // 2. Fixpoint: a fun stays transformable only if EVERY suspend call it makes targets a name that
        //    is itself transformable (its cold entry will exist). Iterate to a fixed point.
        var transformable = new HashSet<string>(eligibleShape);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var name in transformable.ToList())
            {
                foreach (var callee in SuspendCallees(byName[name]))
                    if (!transformable.Contains(callee)) { transformable.Remove(name); changed = true; break; }
            }
        }
        if (transformable.Count == 0) return;

        // callee-return-type map (for await-temp field typing when a call node carries no instantiated
        // retType — a bare `one()` has `sig:""`): the callee's declared resultType.
        var calleeRet = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in transformable)
            calleeRet[name] = Str(byName[name]["resultType"]) ?? "kotlin.Any";

        var baseIsLocal = localTypeFqns.Contains(ContinuationImplFqn);
        var newMethods = new List<JsonNode>();
        var newTypes = new List<JsonNode>();
        var removed = new HashSet<JsonObject>();

        foreach (var name in transformable)
        {
            var method = byName[name];
            removed.Add(method);
            var gen = new FunGen(method, name, fileClass, calleeRet, transformable, baseIsLocal);
            gen.Build(newMethods, newTypes);
        }

        // 3. splice: drop the originals, append cold entries / drain mains + SM types.
        for (var i = methods.Count - 1; i >= 0; i--)
            if (methods[i] is JsonObject mo && removed.Contains(mo)) methods.RemoveAt(i);
        foreach (var nm in newMethods) methods.Add(nm);

        if (newTypes.Count > 0)
        {
            if (file["types"] is not JsonArray types) { types = new JsonArray(); file["types"] = types; }
            foreach (var nt in newTypes) types.Add(nt);
        }
    }

    // --- shape gate ------------------------------------------------------------------------------------

    static bool IsShapeEligible(JsonObject m)
    {
        if (!Bool(m["suspend"])) return false;
        if (!Bool(m["static"])) return false;                       // top-level statics + extensions (kotc: __self param)
        if (Bool(m["inline"]) || Bool(m["abstract"])) return false;
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;  // old CPS / sequence path
        if (m["body"] is not JsonArray body) return false;
        // Every suspension must sit in a SUPPORTED position, and every suspend call must be a plain
        // top-level-fun call (callStatic, owner absent/null). Anything else -> untouched.
        return SuspensionsSupported(body, inHandler: false, tryDepth: 0);
    }

    // Validate that every suspension point is in a position this pass can lower. Rejects: suspension in a
    // catch/finally handler, inside a lambda/closure, a member/cross-assembly suspend call, and a
    // suspending try nested inside another suspending try (the two-level dispatch is single-level v1).
    static bool SuspensionsSupported(JsonNode node, bool inHandler, int tryDepth)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var k = Str(o["k"]);
                // ANY lambda/closure/sequence node -> unsupported (suspend lambdas + the inline
                // `suspendCoroutine {…}` / `suspendCoroutineUninterceptedOrReturn {…}` intrinsics, which
                // emit a `closureNew` and are NOT flagged `suspendCall`, are P3-wave2/P4). Left untouched.
                if (k != null && LambdaKinds.Contains(k))
                    return false;
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"]))
                {
                    if (inHandler) return false;                        // suspension in catch/finally -> unsupported
                    if (k != "callStatic") return false;               // member/instance suspend call -> P4 handoff
                    if (o["owner"] is JsonValue) return false;          // owner'd (cross-file) -> P4 handoff
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

    static IEnumerable<string> SuspendCallees(JsonObject method)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (Str(o["k"]) == "callStatic" && Bool(o["suspendCall"]) && Str(o["method"]) is string mn)
                    seen.Add(mn);
                foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
            }
            else if (n is JsonArray a)
                foreach (var it in a) if (it != null) Walk(it);
        }
        if (method["body"] is JsonArray body) Walk(body);
        return seen;
    }

    static bool HasSuspension(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.ContainsKey("suspendCall") && Bool(o["suspendCall"])) return true;
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
        readonly JsonObject _m;
        readonly string _name;
        readonly string _fileClass;
        readonly Dictionary<string, string> _calleeRet;
        readonly HashSet<string> _transformable;
        readonly bool _baseIsLocal;
        readonly string _smType;                 // bare SM type name
        readonly string _smTypeInst;             // instantiated (`f$sm[gp:T]`) or bare when non-generic
        readonly string _coldName;
        readonly string _resultType;             // Kotlin resultType token ("void" for Unit)
        readonly List<JsonObject> _params;       // original params (extension: leading __self)
        readonly List<string> _typeParams;       // generic type-param names ([] when non-generic)
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        readonly List<(string name, string type)> _fieldDecls = new();

        int _state;                              // resume-state counter (>=1)
        int _label;                              // label id allocator (above kotc's low ids)
        int _condCounter;
        // Outer (method-top) dispatch entries: state -> the label to branch to at invokeSuspend entry.
        readonly List<(int state, int label)> _dispatch = new();
        // Inner-dispatch stack for suspensions inside a `.try` body (two-level dispatch).
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        public FunGen(JsonObject m, string name, string fileClass, Dictionary<string, string> calleeRet,
            HashSet<string> transformable, bool baseIsLocal)
        {
            _m = m; _name = name; _fileClass = fileClass; _calleeRet = calleeRet;
            _transformable = transformable; _baseIsLocal = baseIsLocal;
            _smType = fileClass + "_" + name + "$sm";
            _coldName = name + "$dotkt_suspend";
            _resultType = Str(m["resultType"]) ?? "void";
            _params = (m["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            _typeParams = ReadTypeParamNames(m["typeParams"]);
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
            var body = (_m["body"] as JsonArray) ?? new JsonArray();
            var hasSuspension = HasSuspension(body);

            if (!hasSuspension)
            {
                // No suspension point: the cold entry IS the body directly (extra unused completion param,
                // Any? return so a value return boxes). No SM needed.
                newMethods.Add(ColdEntryDirect(body));
                if (_name == "main") newMethods.Add(DrainMain());
                return;
            }

            // Label allocator base: above any kotc-emitted label id in the body (flattened loops use 0,1,…).
            _label = MaxLabelId(body) + 1000;

            // Collect SM fields: the state label + params + every `var` outside a catch/finally/lambda region.
            AddField("label", "kotlin.Int");
            foreach (var p in _params)
                AddField(Str(p["name"]), Str(p["type"]));
            CollectVarFields(body, inHandler: false);

            // Segment the body (fills the out list + generates await/cond fields + resume states/dispatch).
            var bodyOut = new List<JsonNode>();
            foreach (var s in body) EmitStmt(s, bodyOut);
            if (_resultType is "void" or "kotlin.Unit")
                bodyOut.Add(Ret(NullConst("kotlin.Any")));

            // Assemble invokeSuspend: [outer dispatch] ++ [segmented body].
            var invoke = new JsonArray();
            foreach (var (state, label) in _dispatch)
                invoke.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(state)), true, label));
            foreach (var st in bodyOut) invoke.Add(st);

            newTypes.Add(SmType(invoke));
            newMethods.Add(ColdEntrySm());
            if (_name == "main") newMethods.Add(DrainMain());
        }

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

        // Every user `var` (crossing a suspension or not — a field is always correct, just less optimal)
        // becomes an SM field EXCEPT vars inside a catch/finally handler or a nested lambda (those stay
        // ordinary locals — the catch clause binds its parameter to a local, and a handler-local var never
        // crosses a suspension). The catch PARAMETER is a `"var"` string on the try node, not a `var` node.
        void CollectVarFields(JsonNode node, bool inHandler)
        {
            switch (node)
            {
                case JsonObject o:
                    var k = Str(o["k"]);
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
                    // throw / any other statement: deep-rewrite its sub-expressions (spilling suspensions).
                    outp.Add(Rewrite(o, outp));
                    break;
            }
        }

        // try/catch with the suspension in the TRY BODY (two-level dispatch). The outer dispatch routes an
        // in-try resume state to `tryEntry` (a label BEFORE the try, in the method-top region — legal); the
        // try body BEGINS with an inner dispatch that branches to the actual resume label inside the body
        // (same region — legal). Catch/finally are rewritten but never contain a suspension (gate-enforced).
        void EmitTry(JsonObject o, List<JsonNode> outp)
        {
            var bodyHasSusp = o["body"] != null && HasSuspension(o["body"]);
            if (!bodyHasSusp)
            {
                // A suspension-free try: rewrite its sub-trees in place (a stray sync try around synced code).
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

        // Rewrite a suspension-free subtree: only redirect field reads (no suspension segments to append).
        JsonNode RewriteNoSpill(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
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

        // Rewrite an expression: lower a suspending `cond` to control flow, spill each suspend call
        // (post-order) into a suspension segment + await field, and redirect param/local reads to SM field
        // reads. Appends generated statements to `outp`.
        JsonNode Rewrite(JsonNode node, List<JsonNode> outp)
        {
            if (node is JsonObject o)
            {
                var k = Str(o["k"]);
                if (k == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (k == "callStatic" && Bool(o["suspendCall"]))
                    return EmitSuspensionPoint(o, outp);
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

        // A suspending `if`/`when` (a `cond` ternary with a suspension in a branch/condition): lower to
        // label/brIf/goto control flow assigning a result field (mirrors kotc emitWhenCps). Returns a read
        // of the result field. Only the TAKEN branch's suspension executes.
        JsonNode EmitCondValue(JsonObject c, List<JsonNode> outp)
        {
            var ty = Str(c["type"]) ?? "kotlin.Any";
            var resultField = "__cond$" + (++_condCounter);
            AddFieldTyped(resultField, ty);
            var elseL = NextLabel();
            var endL = NextLabel();

            var condExpr = Rewrite(c["cond"], outp);        // evaluated unconditionally (spill ok)
            outp.Add(BrIf(condExpr, false, elseL));         // if condition false -> else
            outp.Add(SetField(resultField, Rewrite(c["then"], outp)));
            outp.Add(Goto(endL));
            outp.Add(Label(elseL));
            outp.Add(SetField(resultField, Rewrite(c["else"], outp)));
            outp.Add(Label(endL));
            return FieldOf(resultField, ty);
        }

        // A suspension point (mirrors kotc emitSuspend): set label, start the cold call passing `this` as
        // the callee's completion; if it returns COROUTINE_SUSPENDED, return SUSPENDED (inline — a plain
        // ret, or an ilemit-leave when inside a .try); else fall through to the merge label, rethrow a
        // failed resume (throwOnFailure), store the awaited value to an SM field, return a read.
        JsonNode EmitSuspensionPoint(JsonObject callNode, List<JsonNode> outp)
        {
            // Prefer the call's instantiated return (generics: `gp:T`), then the callee's declared
            // resultType, then the sig — a bare `one()` carries only `sig:""`, needing the declared type.
            var retTok = NonEmpty(Str(callNode["retType"]))
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
            // if (result === COROUTINE_SUSPENDED) return SUSPENDED  (inline; false -> skip to resume)
            outp.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, false, resumeLabel));
            outp.Add(Ret(Suspended()));
            // resume merge point
            outp.Add(Label(resumeLabel));
            outp.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = ThrowOnFailure() });
            outp.Add(SetField(field, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        // Route a resume state to the outer dispatch, or (inside a .try) to the inner dispatch + the outer
        // dispatch's try-entry label.
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

        string FieldType(string name)
        {
            foreach (var (n, t) in _fieldDecls) if (n == name) return t;
            return "kotlin.Any";
        }

        // callee$dotkt_suspend(<args>, cast(this -> Continuation<Any?>)), Any? result; preserve typeArgs.
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
            var call = new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = null,
                ["method"] = method,
                ["args"] = args,
                ["ret"] = "kotlin.Any",
            };
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

            // The cold-core `ContinuationImpl.get_context` is emitted NewSlot in the stdlib, so it does NOT
            // fill `BaseContinuationImpl`'s abstract `get_context` slot — a concrete subclass (this SM) must.
            // v1 has no interceptor/context dispatch (§11), so return null (nothing reads the SM's context).
            var getContext = new JsonObject
            {
                ["name"] = "get_context",
                ["static"] = false,
                ["override"] = _baseIsLocal,
                ["virtual"] = false,
                ["abstract"] = false,
                ["objectOverride"] = false,
                ["vis"] = "public",
                ["params"] = new JsonArray(),
                ["ret"] = "kotlin.coroutines.CoroutineContext",
                ["body"] = new JsonArray { Ret(NullConst("kotlin.coroutines.CoroutineContext")) },
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal) getContext["clrOverride"] = BaseContinuationImplFqn;

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
                ["methods"] = new JsonArray { invoke, getContext },
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

        // object f$dotkt_suspend[<tp>](params..., completion) { val sm = new SM[<tp>](params..., completion); return sm.invokeSuspend(null) }
        JsonObject ColdEntrySm()
        {
            var ctorArgs = new JsonArray();
            foreach (var p in _params) ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
            ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = "completion" });
            var argTypes = new JsonArray();
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

        JsonObject ColdEntryDirect(JsonArray body) => ColdMethod((JsonArray)body.DeepClone());

        JsonObject ColdMethod(JsonArray body)
        {
            var ps = new JsonArray();
            foreach (var p in _params) ps.Add(p.DeepClone());
            ps.Add(new JsonObject { ["name"] = "completion", ["type"] = ContinuationOfAny });
            var method = new JsonObject
            {
                ["name"] = _coldName,
                ["static"] = true,
                ["override"] = false,
                ["virtual"] = false,
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

        // kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED — a top-level `val` whose getter returns the
        // CoroutineSingletons enum singleton (same reference each call).
        static JsonObject Suspended() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = IntrinsicsKtFqn,
            ["method"] = "get_COROUTINE_SUSPENDED",
            ["args"] = new JsonArray(),
            ["ret"] = "kotlin.Any",
        };

        // throwOnFailure(result): rethrows a failed raw resume (a boxed kotlin.Result.Failure).
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

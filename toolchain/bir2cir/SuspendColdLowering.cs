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

    // A suspend fun's identity: Owner=null for a top-level file-class static, else the enclosing class FQN.
    readonly record struct FunKey(string Owner, string Name);

    // A shape-eligible suspend fun + where it lives (for cold-entry/SM splicing).
    sealed record Entry(JsonObject Method, JsonObject Root, JsonObject TypeNode, string Owner, string FileClass);

    // A suspend CALL site descriptor (for the resolvability fixpoint).
    readonly record struct CallRef(bool Instance, string Owner, string Name);

    public static void ApplyAll(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs, IReadOnlySet<string> localTypeFqns)
    {
        // 1. Global registry of shape-eligible suspend funs across every input file.
        var entries = new Dictionary<FunKey, Entry>();
        foreach (var r in roots)
        {
            if (r is not JsonObject file) continue;
            var fileClass = Str(file["fileClass"]) ?? "Kt";
            if (file["methods"] is JsonArray methods)
                foreach (var m in methods)
                    if (m is JsonObject mo && Str(mo["name"]) is string name && IsShapeEligible(mo))
                        entries[new FunKey(null, name)] = new Entry(mo, file, null, null, fileClass);
            if (file["types"] is JsonArray types)
                foreach (var t in types)
                    if (t is JsonObject to && Str(to["name"]) is string owner && to["methods"] is JsonArray tms)
                        foreach (var m in tms)
                            if (m is JsonObject mo && Str(mo["name"]) is string name && IsMemberShapeEligible(mo, to))
                                entries[new FunKey(owner, name)] = new Entry(mo, file, to, owner, fileClass);
        }
        if (entries.Count == 0) return;

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
        if (transformable.Count == 0) return;

        // callee-return-type fallback for await-temp field typing when a call node carries no instantiated
        // ret (a bare `one()` has `sig:""`): the callee's declared resultType, keyed by cold-entry name.
        var calleeRet = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, e) in entries)
            calleeRet[k.Name] = Str(e.Method["resultType"]) ?? "kotlin.Any";

        var baseIsLocal = localTypeFqns.Contains(ContinuationImplFqn);

        // 3. Transform each transformable fun, splicing the cold entry (into its declaring container) and the
        //    SM type (into its file's top-level types).
        foreach (var key in transformable)
        {
            var e = entries[key];
            var gen = new FunGen(e.Method, key.Name, e.FileClass, e.Owner, calleeRet, baseIsLocal);
            var newMethods = new List<JsonNode>();
            var newTypes = new List<JsonNode>();
            gen.Build(newMethods, newTypes);

            var container = e.TypeNode != null
                ? (e.TypeNode["methods"] as JsonArray) ?? EnsureArray(e.TypeNode, "methods")
                : (e.Root["methods"] as JsonArray) ?? EnsureArray(e.Root, "methods");
            for (var i = container.Count - 1; i >= 0; i--)
                if (ReferenceEquals(container[i], e.Method)) container.RemoveAt(i);
            foreach (var nm in newMethods) container.Add(nm);

            if (newTypes.Count > 0)
            {
                var ts = (e.Root["types"] as JsonArray) ?? EnsureArray(e.Root, "types");
                foreach (var nt in newTypes) ts.Add(nt);
            }
        }
    }

    static JsonArray EnsureArray(JsonObject o, string key)
    {
        var a = new JsonArray();
        o[key] = a;
        return a;
    }

    // Can a suspend call site be rewritten to a cold entry? Same-assembly: the callee is in `transformable`
    // (its cold entry gets synthesized here). Cross-assembly: the ref.dll flags the member `suspend`, so the
    // `<name>$dotkt_suspend` convention names the cold entry.
    static bool IsResolvable(CallRef call, HashSet<FunKey> transformable, ReferenceMetadataIndex refs)
    {
        if (call.Instance)
            return transformable.Contains(new FunKey(call.Owner, call.Name))
                || refs.HasSuspendMember(call.Owner, call.Name);
        // callStatic: owner==null -> same-assembly top-level (possibly cross-file, keyed by name only);
        // owner set -> a cross-assembly file-class static (ref.dll flag).
        if (call.Owner == null) return transformable.Contains(new FunKey(null, call.Name));
        return transformable.Contains(new FunKey(call.Owner, call.Name))
            || refs.HasSuspendMember(call.Owner, call.Name);
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
    // fun, minus the static requirement. A suspending member of a GENERIC class is deferred (its SM would need
    // the enclosing class type params threaded through `$this` + generic instantiation) — left untouched.
    static bool IsMemberShapeEligible(JsonObject m, JsonObject typeNode)
    {
        if (!Bool(m["suspend"])) return false;
        if (Bool(m["static"])) return false;                        // a static member fun -> deferred
        if (Bool(m["inline"]) || Bool(m["abstract"])) return false;
        // An OPEN/overridden suspend member is deferred: an instance cold entry must be virtual/override in
        // lockstep with the original (a per-override SM), else a virtual `x.g()` call resolves the cold entry
        // statically to the wrong implementation. v1 handles only final (non-virtual, non-override) members.
        if (Bool(m["virtual"]) || Bool(m["override"])) return false;
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;
        if (m["body"] is not JsonArray body) return false;
        // A generic enclosing class is deferred ONLY when the member actually suspends (a direct/no-suspension
        // member cold entry is a plain instance method that inherits the class type params unchanged).
        if (typeNode["typeParams"] is JsonArray tps && tps.Count > 0 && HasSuspension(body)) return false;
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
                // ANY lambda/closure/sequence node -> unsupported (suspend lambdas + the inline
                // `suspendCoroutine {…}` intrinsics, which emit a `closureNew` and are NOT flagged
                // `suspendCall`, are P3-wave2/P4). Left untouched.
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

        readonly JsonObject _m;
        readonly string _name;
        readonly string _fileClass;
        readonly string _ownerClass;             // enclosing class FQN for an instance member, else null
        readonly bool _isMember;
        readonly Dictionary<string, string> _calleeRet;
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
        readonly List<(int state, int label)> _dispatch = new();
        readonly Stack<(List<(int state, int label)> inner, int tryEntry)> _tryStack = new();

        public FunGen(JsonObject m, string name, string fileClass, string ownerClass,
            Dictionary<string, string> calleeRet, bool baseIsLocal)
        {
            _m = m; _name = name; _fileClass = fileClass; _ownerClass = ownerClass;
            _isMember = ownerClass != null;
            _calleeRet = calleeRet; _baseIsLocal = baseIsLocal;
            _smType = (ownerClass ?? fileClass) + "_" + name + "$sm";
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
                // Any? return so a value return boxes). No SM needed. For an instance member the cold entry
                // stays an INSTANCE method on the class, so a `this`/receiver in the body remains valid.
                newMethods.Add(ColdEntryDirect(body));
                if (_name == "main" && !_isMember) newMethods.Add(DrainMain());
                return;
            }

            _label = MaxLabelId(body) + 1000;

            AddField("label", "kotlin.Int");
            if (_isMember) AddField(ThisField, _ownerClass);        // holds the enclosing instance
            foreach (var p in _params)
                AddField(Str(p["name"]), Str(p["type"]));
            CollectVarFields(body, inHandler: false);

            var bodyOut = new List<JsonNode>();
            foreach (var s in body) EmitStmt(s, bodyOut);
            if (_resultType is "void" or "kotlin.Unit")
                bodyOut.Add(Ret(NullConst("kotlin.Any")));

            var invoke = new JsonArray();
            foreach (var (state, label) in _dispatch)
                invoke.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(state)), true, label));
            foreach (var st in bodyOut) invoke.Add(st);

            newTypes.Add(SmType(invoke));
            newMethods.Add(ColdEntrySm());
            if (_name == "main" && !_isMember) newMethods.Add(DrainMain());
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
                if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
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
                if (k == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (_isMember && k == "this")
                    return FieldOf(ThisField, _ownerClass);
                if ((k == "callStatic" || k == "callInstance") && Bool(o["suspendCall"]))
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
                ctorParams.Add(new JsonObject { ["name"] = ThisField, ["type"] = _ownerClass });
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
                ["methods"] = new JsonArray { invoke },
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

        // object f$dotkt_suspend[<tp>](params..., completion) {
        //   val sm = new SM[<tp>]([this,] params..., completion); return sm.invokeSuspend(null) }
        JsonObject ColdEntrySm()
        {
            var ctorArgs = new JsonArray();
            if (_isMember) ctorArgs.Add(new JsonObject { ["k"] = "this" });
            foreach (var p in _params) ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = Str(p["name"]) });
            ctorArgs.Add(new JsonObject { ["k"] = "local", ["name"] = "completion" });
            var argTypes = new JsonArray();
            if (_isMember) argTypes.Add(_ownerClass);
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

// bir2cir — SuspendColdLowering (bundle-6 P2): the cold-core suspend -> state-machine transform.
//
// Per docs/design-coroutine-cold-core-task-bridge.md §11 (the LOCKED contract) + the approved plan
// (functional-nibbling-pearl.md "The bir2cir transform"). This pass lowers a Kotlin `suspend fun` into
// the COLD Continuation shape:
//
//   suspend fun f(a): R           (top-level file-class static)
//     -- SM class:   <FileClass>_f$sm : kotlin.coroutines.clr.internal.ContinuationImpl
//                      fields: int label; <spilled params/locals/await-temps>
//                      object invokeSuspend(object result)   // label dispatch + segmented body
//     -- cold entry: object f$dotkt_suspend(a, completion: Continuation<Any?>)
//                      { val sm = new <FileClass>_f$sm(a, completion); return sm.invokeSuspend(null) }
//     -- suspend main additionally gets a synthesized PLAIN `fun main()` that drains the cold body.
//
// The blueprint is kotc's LIVE CPS engine (BirEmitter.kt:1412-1744 collectCpsVars/spillExpr/emitCps),
// re-implemented over BIR JSON targeting the cold shape. The SM resume protocol matches Kotlin/JVM's
// ContinuationImpl lowering: a single `result` carrier (the invokeSuspend parameter), label dispatch
// that jumps to the post-suspend merge point, and `COROUTINE_SUSPENDED` checks after each cold call.
//
// v1 SCOPE (P2): straight-line bodies (no control flow, no try, no suspend lambdas), non-generic,
// top-level static suspend funs whose suspend calls are all `callStatic owner=null` to LOCAL,
// transformable suspend funs. Anything outside this shape is LEFT UNTOUCHED (it keeps `"suspend":true`
// and rides the existing ilemit throw-stub path -> zero regression). P3 lifts these limits. Exception
// propagation on ASYNC resume (throwOnFailure prologue) is a P3 item; sync-completion rungs never
// resume with a failure, so it is safely omitted here.
//
// Runs AFTER MemberCallSubstitution and BEFORE BirTypeLowering, in app AND rt-stdlib builds (skipped in
// the ref build). Its synthesized nodes are emitted in the SUBSTITUTED call form (already-final BCL /
// sibling-call shapes, mirroring StringCharSequenceBridge) but in the kotlin.* TYPE vocabulary, so they
// flow through BirTypeLowering. In the rt-stdlib build NOTHING matches the v1 gate (every stdlib suspend
// fun is inline / generic / control-flowed / restricted), so the pass is a verified no-op there.

using System.Text.Json.Nodes;

static class SuspendColdLowering
{
    const string ContinuationImplFqn = "kotlin.coroutines.clr.internal.ContinuationImpl";
    const string BaseContinuationImplFqn = "kotlin.coroutines.clr.internal.BaseContinuationImpl";
    const string ContinuationOfAny = "kotlin.coroutines.Continuation[kotlin.Any]";
    const string IntrinsicsKtFqn = "kotlin.coroutines.intrinsics.IntrinsicsKt";

    // Statement kinds a v1 straight-line body may contain. Anything else (control flow, try, block,
    // CFG nodes, throw) disqualifies the fun -> it is left untouched for the existing path.
    static readonly HashSet<string> AllowedStmtKinds = new(StringComparer.Ordinal)
        { "var", "return", "exprStmt", "setLocal" };
    // Node kinds whose PRESENCE anywhere in a body disqualifies v1 (control flow / lambdas / old CPS).
    static readonly HashSet<string> DisqualifyingKinds = new(StringComparer.Ordinal)
    {
        "if", "when", "while", "for", "forArray", "forRange", "dowhile", "try", "block", "throw",
        "label", "goto", "brIf", "break", "continue", "forEachInline", "repeatInline",
        "closureNew", "delegateNew", "lambda", "sequenceNew",
    };

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();
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

        // 3. callee-return-type map (for await-temp field typing) over ALL transformable funs.
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

        // 4. splice: drop the originals, append cold entries / drain mains + SM types.
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
        if (!Bool(m["static"])) return false;                       // v1: top-level statics only
        if (Bool(m["inline"]) || Bool(m["abstract"])) return false;
        if (m["typeParams"] is JsonArray tps && tps.Count > 0) return false;   // generic -> P3
        if (m.ContainsKey("steps") || m.ContainsKey("coClass")) return false;  // old CPS / sequence path
        if (m["body"] is not JsonArray body) return false;
        // top-level statements must be a v1-allowed kind
        foreach (var s in body)
            if (s is JsonObject so && !AllowedStmtKinds.Contains(Str(so["k"]) ?? "")) return false;
        // no disqualifying node anywhere; every suspend call must be a callStatic owner=null
        if (!BodyIsV1Clean(body)) return false;
        return true;
    }

    static bool BodyIsV1Clean(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                var k = Str(o["k"]);
                if (k != null && DisqualifyingKinds.Contains(k)) return false;
                if (k != null && Str(o["k"]) is string && o.ContainsKey("suspendCall") && Bool(o["suspendCall"]))
                {
                    // a suspend call must be a plain top-level-fun call (callStatic, owner absent/null)
                    if (k != "callStatic") return false;
                    if (o["owner"] is JsonValue) return false;   // owner present (non-null) -> not a local sibling
                }
                foreach (var kv in o)
                    if (!BodyIsV1Clean(kv.Value ?? JsonValue.Create(0))) return false;
                return true;
            case JsonArray a:
                foreach (var it in a) if (it != null && !BodyIsV1Clean(it)) return false;
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
        readonly string _smType;
        readonly string _coldName;
        readonly string _resultType;         // Kotlin resultType token ("void" for Unit)
        readonly List<JsonObject> _params;   // original params
        readonly HashSet<string> _fields = new(StringComparer.Ordinal);   // param + body-var names -> SM fields
        readonly List<(string name, string type)> _fieldDecls = new();
        readonly List<JsonNode> _out = new();     // invokeSuspend body under construction
        int _state;                                // resume-state counter (>=1)
        int _cfgId = 900000;                       // CFG label id allocator (high base; v1 bodies have none)
        int _retSuspId;

        public FunGen(JsonObject m, string name, string fileClass, Dictionary<string, string> calleeRet,
            HashSet<string> transformable, bool baseIsLocal)
        {
            _m = m; _name = name; _fileClass = fileClass; _calleeRet = calleeRet;
            _transformable = transformable; _baseIsLocal = baseIsLocal;
            _smType = fileClass + "_" + name + "$sm";
            _coldName = name + "$dotkt_suspend";
            _resultType = Str(m["resultType"]) ?? "void";
            _params = (m["params"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        }

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

            // Collect SM fields: the state label + params + every top-level body `var`.
            AddField("label", "kotlin.Int");
            foreach (var p in _params)
                AddField(Str(p["name"]), Str(p["type"]));
            foreach (var s in body)
                if (s is JsonObject so && Str(so["k"]) == "var")
                    AddField(Str(so["name"]), Str(so["type"]));

            _retSuspId = _cfgId++;

            // Build the segmented body first (fills _out + generates await-temp fields + resume states).
            foreach (var s in body) EmitStmt(s);
            // A Unit-returning body with no trailing return -> complete with null.
            if (_resultType is "void" or "kotlin.Unit")
                _out.Add(Ret(NullConst("kotlin.Any")));

            // Assemble invokeSuspend: [dispatch] ++ [segmented body] ++ [SUSPENDED exit].
            var invoke = new JsonArray();
            for (var k = 1; k <= _state; k++)
                invoke.Add(BrIf(BinEq(FieldOf("label", "kotlin.Int"), IntConst(k)), true, ResumeId(k)));
            foreach (var st in _out) invoke.Add(st);
            invoke.Add(Label(_retSuspId));
            invoke.Add(Ret(Suspended()));

            newTypes.Add(SmType(invoke));
            newMethods.Add(ColdEntrySm());
            if (_name == "main") newMethods.Add(DrainMain());
        }

        void AddField(string name, string type)
        {
            if (name == null || !_fields.Add(name)) return;
            _fieldDecls.Add((name, type ?? "kotlin.Any"));
        }

        int ResumeId(int state) => 900000 + state;   // stable id per resume state (distinct from _retSuspId base)

        // ---- statement lowering ----

        void EmitStmt(JsonNode stmt)
        {
            if (stmt is not JsonObject o) return;
            switch (Str(o["k"]))
            {
                case "var":
                {
                    var nm = Str(o["name"]);
                    var init = o["init"];
                    var val = init == null ? NullConst(Str(o["type"]) ?? "kotlin.Any") : Rewrite(init);
                    _out.Add(SetField(nm, val));   // the var is an SM field
                    break;
                }
                case "setLocal":
                {
                    var nm = Str(o["name"]);
                    var val = Rewrite(o["value"]);
                    if (_fields.Contains(nm)) _out.Add(SetField(nm, val));
                    else _out.Add(new JsonObject { ["k"] = "setLocal", ["name"] = nm, ["value"] = val });
                    break;
                }
                case "return":
                {
                    var v = o["value"];
                    _out.Add(v == null ? Ret(NullConst("kotlin.Any")) : Ret(Rewrite(v)));
                    break;
                }
                case "exprStmt":
                    _out.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = Rewrite(o["expr"]) });
                    break;
            }
        }

        // Rewrite an expression: spill each suspend call (post-order) into a suspension segment + await
        // field, and redirect param/local reads to SM field reads. Appends suspension segments to _out.
        JsonNode Rewrite(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (Str(o["k"]) == "local" && Str(o["name"]) is string ln && _fields.Contains(ln))
                    return FieldOf(ln, FieldType(ln));
                if (Str(o["k"]) == "callStatic" && Bool(o["suspendCall"]))
                    return EmitSuspensionPoint(o);
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : Rewrite(kv.Value);
                return copy;
            }
            if (node is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var it in a) copy.Add(it == null ? null : Rewrite(it));
                return copy;
            }
            return node?.DeepClone();
        }

        // A suspension point (mirrors kotc emitSuspend): start the cold call passing `this` as the callee's
        // completion; if it returns COROUTINE_SUSPENDED, save state and return SUSPENDED; else (or on resume)
        // land at the merge label with the awaited value in `result`, store it to an SM field, return a read.
        JsonNode EmitSuspensionPoint(JsonObject callNode)
        {
            var method = Str(callNode["method"]);
            var retTok = _calleeRet.TryGetValue(method ?? "", out var rt) ? rt : "kotlin.Any";
            if (retTok is "void" or "kotlin.Unit") retTok = "kotlin.Any";
            var state = ++_state;
            var field = "__aw$" + state;
            AddFieldTyped(field, retTok);

            // this.label = state
            _out.Add(SetField("label", IntConst(state)));
            // result = callee$dotkt_suspend(<spilled args>, this)
            _out.Add(new JsonObject { ["k"] = "setLocal", ["name"] = "result", ["value"] = ColdCall(callNode) });
            // if (result === COROUTINE_SUSPENDED) goto RET_SUSP
            _out.Add(BrIf(new JsonObject
            {
                ["k"] = "objEq",
                ["l"] = new JsonObject { ["k"] = "local", ["name"] = "result" },
                ["r"] = Suspended(),
            }, true, _retSuspId));
            // resume merge point
            _out.Add(Label(ResumeId(state)));
            // this.__aw$k = (retTok) result
            _out.Add(SetField(field, retTok == "kotlin.Any"
                ? new JsonObject { ["k"] = "local", ["name"] = "result" }
                : new JsonObject { ["k"] = "cast", ["type"] = retTok, ["e"] = new JsonObject { ["k"] = "local", ["name"] = "result" } }));
            return FieldOf(field, retTok);
        }

        void AddFieldTyped(string name, string type)
        {
            if (_fields.Add(name)) _fieldDecls.Add((name, type));
        }

        string FieldType(string name)
        {
            foreach (var (n, t) in _fieldDecls) if (n == name) return t;
            return "kotlin.Any";
        }

        // callee$dotkt_suspend(<args>, cast(this -> Continuation<Any?>)), Any? result.
        JsonObject ColdCall(JsonObject callNode)
        {
            var method = Str(callNode["method"]) + "$dotkt_suspend";
            var args = new JsonArray();
            if (callNode["args"] is JsonArray oa)
                foreach (var arg in oa) args.Add(arg == null ? null : Rewrite(arg));
            args.Add(new JsonObject
            {
                ["k"] = "cast",
                ["type"] = ContinuationOfAny,
                ["e"] = new JsonObject { ["k"] = "this" },
            });
            return new JsonObject
            {
                ["k"] = "callStatic",
                ["owner"] = null,
                ["method"] = method,
                ["args"] = args,
                ["ret"] = "kotlin.Any",
            };
        }

        // ---- declaration synthesis ----

        JsonObject SmType(JsonArray invokeBody)
        {
            var fields = new JsonArray();
            foreach (var (n, t) in _fieldDecls)
                fields.Add(new JsonObject { ["name"] = n, ["type"] = t, ["vis"] = "internal" });

            // ctor(params..., completion) : base(completion, null-context) { this.p = p ... }
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
            // v1 has no interceptor/context dispatch (§11), so return the EmptyCoroutineContext singleton.
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
                // v1 has no interceptor/context dispatch (§11) — nothing reads the SM's context — so return
                // null rather than plumb the EmptyCoroutineContext singleton (a P3 fidelity item).
                ["body"] = new JsonArray { Ret(NullConst("kotlin.coroutines.CoroutineContext")) },
                ["attrs"] = new JsonArray(),
            };
            if (!_baseIsLocal) getContext["clrOverride"] = BaseContinuationImplFqn;

            return new JsonObject
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
        }

        // object f$dotkt_suspend(params..., completion) { val sm = new SM(params..., completion); return sm.invokeSuspend(null) }
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
                    ["type"] = _smType,
                    ["init"] = new JsonObject { ["k"] = "new", ["type"] = _smType, ["argTypes"] = argTypes, ["args"] = ctorArgs },
                },
                Ret(new JsonObject
                {
                    ["k"] = "callInstance",
                    ["ownerType"] = _smType,
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
            return new JsonObject
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
            ["ownerType"] = _smType,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["value"] = value,
        };

        JsonObject FieldOf(string name, string type) => new()
        {
            ["k"] = "field",
            ["ownerType"] = _smType,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["name"] = name,
            ["retType"] = type,
        };

        // kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED — a top-level `val` whose getter returns the
        // CoroutineSingletons enum singleton (same reference each call). Referenced as the plain static
        // property getter (the ilemit `coSuspendedSentinel` node expects a nonexistent field).
        static JsonObject Suspended() => new()
        {
            ["k"] = "callStatic",
            ["owner"] = IntrinsicsKtFqn,
            ["method"] = "get_COROUTINE_SUSPENDED",
            ["args"] = new JsonArray(),
            ["ret"] = "kotlin.Any",
        };

        static JsonObject Ret(JsonNode value) => new() { ["k"] = "return", ["value"] = value };
        static JsonObject IntConst(int v) => new() { ["k"] = "const", ["type"] = "kotlin.Int", ["value"] = v };
        static JsonObject NullConst(string type) => new() { ["k"] = "const", ["type"] = type, ["value"] = null };
        static JsonObject Label(int id) => new() { ["k"] = "label", ["id"] = id };
        static JsonObject BrIf(JsonNode cond, bool on, int id) => new()
            { ["k"] = "brIf", ["cond"] = cond, ["on"] = on, ["id"] = id };
        static JsonObject BinEq(JsonNode l, JsonNode r) => new()
            { ["k"] = "bin", ["op"] = "==", ["l"] = l, ["r"] = r };
    }
}

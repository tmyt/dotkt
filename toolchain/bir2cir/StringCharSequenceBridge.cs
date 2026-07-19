using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class StringCharSequenceBridge
{
    const string CharSeq = "dotkt$CharSequence";
    const string Adapter = "dotkt$StringCharSequence";
    static readonly HashSet<string> StringTokens = new(StringComparer.Ordinal)
        { "kotlin.String", "System.String", "string" };
    // A StringBuilder is a non-String CharSequence that does NOT implement the synthetic `dotkt$CharSequence`; flowing
    // one into a CharSequence slot is snapshot to a String first (#149-2, WrapStringBuilder). Every spelling the static
    // type can carry at this stage (the Kotlin typealias, the JDK class it aliases, the CLR type it lowers to).
    static readonly HashSet<string> StringBuilderTokens = new(StringComparer.Ordinal)
        { "kotlin.text.StringBuilder", "java.lang.StringBuilder", "java.lang.AbstractStringBuilder", "System.Text.StringBuilder" };

    // Injected exactly once per app assembly (dedup below). Pre-BirTypeLowering vocabulary: kotlin.* signature tokens
    // (lowered by the next pass), CLR-call bodies (String.get_Chars/Length/Substring — the SAME shape kotc emits for a
    // user `class S(val s:String): CharSequence`). Structurally mirrors that verified S class, renamed s->value.
    // Type slots are STRUCTURED `{t:"fqn",…}` nodes (§1 — types are nodes, no bare strings), exactly as kotc emits
    // for a real user `class S(val s:String): CharSequence`; the subsequent DeclNullableFlags/ReferenceNullableStrip/
    // BirTypeLowering passes lower the `kotlin.*` identities to the CLR forms uniformly. (The retired `@<name>`
    // this-assembly marker is dropped — bir2cir/ilemit derive local-vs-referenced from the FQN via `_types`.)
    const string AdapterTypeJson = """
    {
      "name": "dotkt$StringCharSequence",
      "kind": "class", "generated": true, "abstract": false, "vis": "public", "base": null,
      "interfaces": [{"t":"fqn","name":"dotkt$CharSequence"}],
      "fields": [{"name": "value", "type": {"t":"fqn","name":"kotlin.String"}, "vis": "internal"}],
      "ctors": [{
        "params": [{"name": "value", "type": {"t":"fqn","name":"kotlin.String"}}],
        "baseArgs": null, "thisArgs": null, "vis": "public",
        "body": [{"k": "setField", "ownerType": {"t":"fqn","name":"dotkt$StringCharSequence"}, "recv": {"k": "this"}, "name": "value", "value": {"k": "local", "name": "value"}}]
      }],
      "methods": [
        {"name": "get", "static": false, "override": false, "virtual": true, "abstract": false, "objectOverride": false, "vis": "public", "mods": {"operator": true},
         "params": [{"name": "index", "type": {"t":"fqn","name":"kotlin.Int"}}], "ret": {"t":"fqn","name":"kotlin.Char"},
         "body": [{"k": "return", "value": {"k": "clrInstance", "type": {"t":"fqn","name":"System.String"}, "method": "get_Chars", "argTypes": [{"t":"fqn","name":"System.Int32"}], "ret": {"t":"fqn","name":"System.Char"},
           "recv": {"k": "callInstance", "ownerType": {"t":"fqn","name":"dotkt$StringCharSequence"}, "virtual": false, "recv": {"k": "this"}, "method": "get_value", "args": []},
           "args": [{"k": "local", "name": "index"}]}}], "attrs": []},
        {"name": "subSequence", "static": false, "override": false, "virtual": true, "abstract": false, "objectOverride": false, "vis": "public",
         "params": [{"name": "startIndex", "type": {"t":"fqn","name":"kotlin.Int"}}, {"name": "endIndex", "type": {"t":"fqn","name":"kotlin.Int"}}], "ret": {"t":"fqn","name":"dotkt$CharSequence"},
         "body": [{"k": "return", "value": {"k": "new", "type": {"t":"fqn","name":"dotkt$StringCharSequence"}, "argTypes": [{"t":"fqn","name":"kotlin.String"}],
           "args": [{"k": "clrInstance", "type": {"t":"fqn","name":"System.String"}, "method": "Substring", "argTypes": [{"t":"fqn","name":"System.Int32"}, {"t":"fqn","name":"System.Int32"}], "ret": {"t":"fqn","name":"System.String"},
             "recv": {"k": "callInstance", "ownerType": {"t":"fqn","name":"dotkt$StringCharSequence"}, "virtual": false, "recv": {"k": "this"}, "method": "get_value", "args": []},
             "args": [{"k": "local", "name": "startIndex"}, {"k": "binOp", "op": "-", "lhs": {"k": "local", "name": "endIndex"}, "rhs": {"k": "local", "name": "startIndex"}}]}]}}], "attrs": []},
        {"name": "get_value", "static": false, "override": false, "virtual": false, "abstract": false, "objectOverride": false, "vis": "public",
         "params": [], "ret": {"t":"fqn","name":"kotlin.String"},
         "body": [{"k": "return", "value": {"k": "field", "ownerType": {"t":"fqn","name":"dotkt$StringCharSequence"}, "recv": {"k": "this"}, "name": "value"}}]},
        {"name": "get_length", "static": false, "override": true, "virtual": true, "abstract": false, "objectOverride": false, "vis": "public",
         "params": [], "ret": {"t":"fqn","name":"kotlin.Int"},
         "body": [{"k": "return", "value": {"k": "clrPropGet", "type": {"t":"fqn","name":"System.String"}, "name": "Length", "ret": {"t":"fqn","name":"System.Int32"}, "static": false,
           "recv": {"k": "callInstance", "ownerType": {"t":"fqn","name":"dotkt$StringCharSequence"}, "virtual": false, "recv": {"k": "this"}, "method": "get_value", "args": []}}}]},
        {"name": "ToString", "static": false, "override": true, "virtual": true, "abstract": false, "objectOverride": true, "vis": "public",
         "params": [], "ret": {"t":"fqn","name":"kotlin.String"},
         "body": [{"k": "return", "value": {"k": "field", "ownerType": {"t":"fqn","name":"dotkt$StringCharSequence"}, "recv": {"k": "this"}, "name": "value"}}]}
      ],
      "properties": [
        {"name": "value", "type": {"t":"fqn","name":"kotlin.String"}, "get": "get_value", "set": null},
        {"name": "length", "type": {"t":"fqn","name":"kotlin.Int"}, "get": "get_length", "set": null}
      ],
      "attrs": []
    }
    """;

    // Process-wide: the app-local adapter type is emitted into EXACTLY ONE file's `types` per assembly (all of an app's
    // BIR files are lowered by a single bir2cir process; other files that also wrap resolve the type assembly-wide via
    // ilemit's `_types`). Fresh per process; app builds only. `_fired` tracks whether the file just walked wrapped.
    static bool _adapterEmitted;
    static bool _fired;
    // The ref.dll index — consulted ONLY to read a spliced anonymous object's (`dotkt$obj*`) ctor param types, so a
    // static-String arg flowing into its `CharSequence` capture slot is adapter-wrapped (WrapNewCtorArgs). Null-safe.
    static ReferenceMetadataIndex _refs;
    // #170 — lifted lambdas bound into a `dotkt$CharSequence`-returning delegate (from CharSeqStringLowering). Null-safe.
    static CharSeqStringLowering.CharSeqRetLambdas _retLambdas;

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    // A lexical name -> declared-type environment (method/lambda params + local `var` decls), plus the enclosing
    // method's return type (for the return-site wrap). Copy-on-extend so a child scope never mutates its parent.
    sealed class Env
    {
        public readonly Dictionary<string, TypeNode> Vars;
        public readonly TypeNode RetType;
        public Env() { Vars = new(StringComparer.Ordinal); RetType = null; }
        Env(Dictionary<string, TypeNode> vars, TypeNode retType) { Vars = vars; RetType = retType; }

        // A declaration node (has a `params` array — methods/lambdas always emit one, even empty) opens a child scope
        // seeded with its params and return type. A non-decl node (call/expr — no `params`) returns `this` unchanged.
        public Env WithDecl(JsonObject decl)
        {
            if (decl["params"] is not JsonArray ps) return this;
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal);
            foreach (var p in ps)
                if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                    vars[pn] = pt;
            return new Env(vars, TypeJson.Read(decl["ret"]) ?? RetType);
        }

        public Env WithVar(string name, TypeNode type)
        {
            var vars = new Dictionary<string, TypeNode>(Vars, StringComparer.Ordinal) { [name] = type };
            return new Env(vars, RetType);
        }
    }

    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs = null,
        CharSeqStringLowering.CharSeqRetLambdas retLambdas = null)
    {
        _refs = refs;
        _fired = false;
        _retLambdas = retLambdas;
        // #170 — a lifted lambda bound into a delegate whose declared RETURN is the synthetic `dotkt$CharSequence`
        // (CharSeqStringLowering recorded these before it collapsed the CharSequence-return signal to `string`). Retype
        // its lifted `ret` back to the synthetic BEFORE the walk, so `Env.WithDecl` sees `RetType = dotkt$CharSequence`
        // and the existing WrapReturn adapter-wraps every String return — the lifted ldftn signature then matches the
        // `KFunc<…,dotkt$CharSequence>` ilemit rewraps the literal into (else ilverify DelegateCtor). Point ① of the
        // 3-point sync; point ② is WrapReturn during the walk; point ③ is the funcType sync in Transform.
        if (retLambdas != null && root is JsonObject rootObj) RetypeLiftedRets(rootObj, retLambdas);
        // Seed the shared static-type resolver for THIS file so IsStaticString can recover a receiver's static type
        // uniformly (a property-getter call, an app top-level fun result, a `!!`/elvis valueBlock — none carry a `ret`).
        StaticType.Refs = refs;
        StaticType.LocalTypes = StaticType.CollectTypes(root);
        var walked = Walk(root, new Env());
        // Emit the app-local adapter type into this file's `types` if a wrap fired here and no other file already got
        // it (one per assembly). ilemit resolves a wrap in a sibling file against it via the assembly-wide `_types`.
        if (_fired && !_adapterEmitted && walked is JsonObject fileObj)
        {
            var types = fileObj["types"] as JsonArray;
            if (types == null) { types = new JsonArray(); fileObj["types"] = types; }
            types.Add(JsonNode.Parse(AdapterTypeJson));
            _adapterEmitted = true;
        }
        return walked;
    }

    static JsonNode Walk(JsonNode node, Env env)
    {
        if (node is JsonObject obj)
        {
            var childEnv = env.WithDecl(obj);
            // A valueBlock's `stmts` declare locals that scope into its sibling `result` (a spliced inline body is a
            // valueBlock: `var it = element; result = first(it)`). The generic per-key walk would visit `result` with
            // only childEnv, so a String local declared in stmts stays invisible to a wrap site in result -> a raw
            // String flows into a CharSequence-ext arg unwrapped. Thread the stmts' var decls into the result env.
            var resultEnv = childEnv;
            if (Str(obj["k"]) == "valueBlock" && obj["stmts"] is JsonArray sarr)
                foreach (var s in sarr)
                    if (s is JsonObject so && Str(so["k"]) == "var" && Str(so["name"]) is string sn
                        && TypeJson.Read(so["type"]) is TypeNode st)
                        resultEnv = resultEnv.WithVar(sn, st);
            var copy = new JsonObject();
            foreach (var kv in obj)
                copy[kv.Key] = kv.Value is JsonArray arr ? WalkArray(arr, childEnv)
                             : kv.Value == null ? null
                             : Walk(kv.Value, kv.Key == "result" ? resultEnv : childEnv);
            return Transform(copy, env);   // this node's own coercion sites use its ENCLOSING env
        }
        if (node is JsonArray topArr) return WalkArray(topArr, env);
        return node.DeepClone();
    }

    // Walk an array's elements in document order, threading each `var` decl's name->type forward so a LATER sibling
    // statement's read of that local resolves its static type (a `var`'s own init is walked BEFORE the var is added,
    // so `val x = <x>` can't see itself). Non-body arrays (args/params/…) contain no `var` nodes, so this is a no-op
    // for them.
    static JsonArray WalkArray(JsonArray arr, Env env)
    {
        var copy = new JsonArray();
        var cur = env;
        foreach (var item in arr)
        {
            var walked = item == null ? null : Walk(item, cur);
            copy.Add(walked);
            if (walked is JsonObject wo && Str(wo["k"]) == "var"
                && Str(wo["name"]) is string vn && TypeJson.Read(wo["type"]) is TypeNode vt)
                cur = cur.WithVar(vn, vt);
        }
        return copy;
    }

    static JsonNode Transform(JsonObject node, Env env)
    {
        switch (Str(node["k"]))
        {
            case "callStatic":
            case "callInstance":
                WrapCallArgs(node, env);
                return node;
            case "newDelegate":
            case "newClosure":
                SyncDelegateFuncRet(node);
                return node;
            case "new":
                WrapNewCtorArgs(node, env);
                return node;
            case "var":
                WrapVarInit(node, env);
                return node;
            case "return":
                WrapReturn(node, env);
                return node;
            case "cast":
                return WrapCast(node, env) ?? node;
            default:
                return node;
        }
    }

    // (a)+(b): a call arg whose DECLARED slot (positional in `sig`, the comma-joined param types with the extension
    // receiver first) is a CharSequence and whose value is statically a String. `sig` may be LONGER than `args` when
    // trailing defaulted params were dropped — pair only the present args.
    static void WrapCallArgs(JsonObject node, Env env)
    {
        if (node["args"] is not JsonArray args || node["sig"] is not JsonArray sig) return;
        var n = Math.Min(sig.Count, args.Count);
        for (var i = 0; i < n; i++)
        {
            if (TypeJson.Read(sig[i]) is not TypeNode tn || !IsCharSeqT(tn) || args[i] is not JsonNode a) continue;
            var c = CoerceCharSeqArg(a, env, nonNullSlot: tn is TypeNode.Fqn);
            if (!ReferenceEquals(c, a)) args[i] = c;
        }
    }

    // Coerce a value flowing into a `dotkt$CharSequence` slot into an interface-implementing value:
    //   - a statically-String value            -> `new dotkt$StringCharSequence(str)` (WrapAdapter).
    //   - a StringBuilder (#149-2)              -> snapshot to String via LibraryKt.toString, then WrapAdapter.
    //   - a polymorphic if/else unified to      -> DESCEND into then/else and coerce EACH branch, so a String branch
    //     CharSequence (#149-3)                    is wrapped while a genuine-CharSequence branch is left as-is.
    // Anything else (a user `class S : CharSequence`, an already-wrapped adapter, an unknown expr) is returned as-is.
    //
    // `nonNullSlot` = the target slot is a NON-nullable `dotkt$CharSequence`: the frontend guarantees a non-null value,
    // so a nullable-String value (a `!!`/elvis result still carrying the pre-strip `String?` type here) is safe to peel
    // + wrap (the `x!!.split(...)` receiver path). A NULLABLE slot (`CharSequence?`-receiver ext) keeps the strict
    // bare-String test so a genuine null stays unwrapped — EXCEPT a `!!` non-null assertion, which is provably non-null
    // and so is peel-safe even there (#149-4, `x!!.isNullOrEmpty()`).
    static JsonNode CoerceCharSeqArg(JsonNode a, Env env, bool nonNullSlot)
    {
        if (a is not JsonObject o) return a;
        if (IsStaticString(a, env, allowNullable: nonNullSlot || IsNotNullAssertion(a))) return WrapAdapter(a);
        if (IsStaticStringBuilder(a, env)) return WrapStringBuilder(a);
        // #156 — a genuinely-nullable String value (`z: String? = null`) into a NULLABLE `CharSequence?` slot (the strict
        // path, nonNullSlot=false): the checks above deliberately leave it RAW to preserve null, but a raw `String` into a
        // `dotkt$CharSequence` interface slot is ilverify-UNSOUND (String does not implement the synthetic adapter iface) —
        // it only runs because a null short-circuits (isNullOrEmpty). Emit a runtime-conditional wrap
        // `v == null ? (dotkt$CharSequence)null : new dotkt$StringCharSequence(v)`: the slot receives a genuine adapter or
        // a typed null, ilverify-clean, null-preserving. The non-null-String and `!!`/elvis cases already wrapped above.
        if (!nonNullSlot && IsStaticNullableString(a, env)) return WrapAdapterNullable(a, env);
        // The slot is CharSequence (every caller gates on IsCharSeqT), so a `cond` reaching here unifies String/CharSeq
        // branches — coerce each. `!!`/elvis desugars are a valueBlock (handled above), never a bare `cond`, so this
        // only descends a genuine `if/else`.
        if (Str(o["k"]) == "cond")
        {
            if (o["then"] is JsonNode t) { var ct = CoerceCharSeqArg(t, env, nonNullSlot); if (!ReferenceEquals(ct, t)) o["then"] = ct; }
            if (o["else"] is JsonNode e) { var ce = CoerceCharSeqArg(e, env, nonNullSlot); if (!ReferenceEquals(ce, e)) o["else"] = ce; }
            return o;
        }
        return a;
    }

    // A `!!` non-null assertion desugars to `valueBlock{ var t = <e>; result = if (!(t == null)) t else throw }`. In the
    // `then` arm `t` is proven non-null, so the whole block's value is GUARANTEED non-null (it throws otherwise) and is
    // wrap-safe even into a nullable `CharSequence?` slot (#149-4, `x!!.isNullOrEmpty()`). Matched STRUCTURALLY — the
    // condition is `!(t == null)` (the `objEq` RHS MUST be the null const, else `t != OTHER` does not prove non-null),
    // and the `then` reads the SAME local `t` — so it stays tight (a nullable `if (s != sep) s else throw` does NOT
    // match) and survives the exception-type substitution that already ran (`throw NPE` -> `newClr NRE`).
    static bool IsNotNullAssertion(JsonNode n)
    {
        if (n is not JsonObject o || Str(o["k"]) != "valueBlock" || o["result"] is not JsonObject r || Str(r["k"]) != "cond") return false;
        if (r["else"] is not JsonObject e || Str(e["k"]) != "throwExpr") return false;
        if (r["cond"] is not JsonObject c || Str(c["k"]) != "unaryOp" || Str(c["op"]) != "!"
            || c["e"] is not JsonObject eq || Str(eq["k"]) != "objEq") return false;
        // RHS must be the null literal (`{k:const, value:null}`) — a `t == someNonNull` comparison is NOT a null check.
        if (eq["rhs"] is not JsonObject rhs || Str(rhs["k"]) != "const" || rhs["value"] is not null) return false;
        var lhsName = eq["lhs"] is JsonObject l && Str(l["k"]) == "local" ? Str(l["name"]) : null;
        var thenName = r["then"] is JsonObject t && Str(t["k"]) == "local" ? Str(t["name"]) : null;
        return lhsName != null && lhsName == thenName;
    }

    // A StringBuilder value: static type is one of the StringBuilder spellings (never String / the adapter / a user CS).
    static bool IsStaticStringBuilder(JsonNode n, Env env)
        => n is JsonObject && StaticType.Surface(n, BirScope.FromVars(env.Vars)) is TypeNode.Fqn { Args: null } f
           && StringBuilderTokens.Contains(f.Name);

    // Snapshot a StringBuilder to a String via the null-safe `kotlin.LibraryKt.toString(object)` (the same coercion
    // CharSeqStringLowering uses for a non-String value flowing into a `string` slot), then wrap that String in the
    // adapter. StringBuilder.split/replace consume the sequence immediately, so the snapshot is faithful (#149-2).
    static JsonObject WrapStringBuilder(JsonNode sbExpr)
    {
        var snapshot = new JsonObject
        {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn("kotlin.LibraryKt"), ["method"] = "toString",
            ["sig"] = new JsonArray { TypeJson.Fqn("object") }, ["args"] = new JsonArray { sbExpr.DeepClone() },
        };
        return WrapAdapter(snapshot);
    }

    // (f): a static-String arg flowing into a SPLICED anonymous object's (`dotkt$obj*`) `CharSequence` ctor slot. A
    // spliced `new dotkt$obj90(receiver, keySelector)` (the anonymous Grouping from `CharSequence.groupingBy`) captures
    // the receiver as `kotlin.CharSequence`, but the spliced node carries no argTypes and CharSeqStringLowering already
    // collapsed the receiver var to `System.String` — so the raw String reaches obj90's `get_length()` and NREs. The
    // ctor param types come from the ref.dll (_refs); wrap each static-String arg whose ctor slot is CharSequence.
    static void WrapNewCtorArgs(JsonObject node, Env env)
    {
        if (_refs == null || node["args"] is not JsonArray args) return;
        if (TypeJson.Read(node["type"]) is not TypeNode.Fqn tf
            || !tf.Name.StartsWith("dotkt$obj", StringComparison.Ordinal)) return;
        var ctorParams = _refs.OwnerCtorParamTypeNames(tf.Name);
        // Require an EXACT arity match: a positional skew (a synthetic `__outer` present on one side only) would align the
        // CharSequence-slot check against the wrong argument and wrap a genuine value. Skip rather than risk it.
        if (ctorParams == null || ctorParams.Length != args.Count) return;
        for (var i = 0; i < args.Count; i++)
            if ((IsCharSeqSlot(ctorParams[i]) || Bare(ctorParams[i]) == "kotlin.CharSequence")
                && args[i] is JsonNode a && IsStaticString(a, env))
                args[i] = WrapAdapter(a);
    }

    // (d): a store into a CharSequence-typed local `var cs: CharSequence = <String | StringBuilder | if/else>`.
    static void WrapVarInit(JsonObject node, Env env)
    {
        if (TypeJson.Read(node["type"]) is TypeNode tn && IsCharSeqT(tn) && node["init"] is JsonNode init
            && CoerceCharSeqArg(init, env, nonNullSlot: tn is TypeNode.Fqn) is JsonNode ci && !ReferenceEquals(ci, init))
            node["init"] = ci;
    }

    // (c): a return of a String | StringBuilder | polymorphic if/else into a CharSequence return type.
    static void WrapReturn(JsonObject node, Env env)
    {
        if (env.RetType is TypeNode rt && IsCharSeqT(rt) && node["value"] is JsonNode v
            && CoerceCharSeqArg(v, env, nonNullSlot: rt is TypeNode.Fqn) is JsonNode cv && !ReferenceEquals(cv, v))
            node["value"] = cv;
    }

    // #170 point ① — retype each recorded lifted lambda's declared `ret` from `System.String`/`kotlin.String` back to
    // the synthetic `dotkt$CharSequence`, so the walk's WrapReturn adapter-wraps its String returns and the ldftn
    // signature matches the CharSequence-returning delegate. A `newDelegate` target is a top-level lifted static in
    // `methods`; a `newClosure` target is the `invoke` of the synthetic class in `types`. Only retype an actual String
    // ret (idempotent for a `(…) -> CharSequence` lambda whose ret already survived as the synthetic).
    static void RetypeLiftedRets(JsonObject root, CharSeqStringLowering.CharSeqRetLambdas rl)
    {
        if (root["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo && Str(mo["name"]) is string mn && rl.Statics.Contains(mn))
                    RetypeRetToCharSeq(mo);
        if (root["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to && Str(to["name"]) is string tn && rl.Closures.Contains(tn)
                    && to["methods"] is JsonArray tms)
                    foreach (var tm in tms)
                        if (tm is JsonObject tmo && Str(tmo["name"]) == "invoke")
                            RetypeRetToCharSeq(tmo);
    }

    static void RetypeRetToCharSeq(JsonObject decl)
    {
        if (TypeJson.Read(decl["ret"]) is TypeNode rt && IsStringTokT(rt))
            decl["ret"] = new JsonObject { ["t"] = "fqn", ["name"] = CharSeq };
    }

    // #170 point ③ — sync a recorded delegate's `funcType` RETURN to the synthetic, so a self-build fallback
    // (ilemit's `DelegateCtor(MapType(funcType))` path, taken when the callee slot is an open/TypeBuilder generic)
    // constructs a `KFunc<…,dotkt$CharSequence>` consistent with the now-CharSequence-returning lifted body.
    static void SyncDelegateFuncRet(JsonObject node)
    {
        if (_retLambdas == null) return;
        var isTarget = Str(node["k"]) == "newDelegate"
            ? Str(node["method"]) is string mn && _retLambdas.Statics.Contains(mn)
            : TypeJson.Read(node["closureType"]) is TypeNode.Fqn { Name: { } ct } && _retLambdas.Closures.Contains(ct);
        if (!isTarget || node["funcType"] is not JsonObject ft) return;
        if (TypeJson.Read(ft["ret"]) is TypeNode rt && IsStringTokT(rt))
            ft["ret"] = new JsonObject { ["t"] = "fqn", ["name"] = CharSeq };
    }

    // A structured Type is (nullable/array of) the CharSequence synthetic.
    static bool IsCharSeqT(TypeNode t) => t switch
    {
        TypeNode.Fqn f => f.Name == CharSeq,
        TypeNode.Nullable n => IsCharSeqT(n.Of),
        TypeNode.Array a => IsCharSeqT(a.Elem),
        _ => false,
    };
    static bool IsStringTokT(TypeNode t) => t is TypeNode.Fqn { Args: null } f && StringTokens.Contains(f.Name);

    // (e): `as CharSequence` on a static String -> REPLACE the (would-be InvalidCast) `castclass dotkt$CharSequence`
    // with the materializing adapter. A non-statically-String cast (an `Any?`->CharSequence runtime check) is left as
    // the plain cast — a runtime-type-check adapter helper for that is a follow-up (see docs 【4-A】).
    static JsonNode WrapCast(JsonObject node, Env env)
    {
        if (IsCharSeqT(TypeJson.Read(node["type"])) && node["e"] is JsonNode e && IsStaticString(e, env))
            return WrapAdapter(e);
        return null;
    }

    // `new kotlin.StringCharSequence(<str>)`. Not @ClrTypeAlias, so MemberCallSubstitution.TransformNew (already run)
    // leaves it; BirTypeLowering lowers `type`/`argTypes` (kotlin.String -> System.String); ilemit reflects the ctor
    // against the runtime stdlib.
    static JsonObject WrapAdapter(JsonNode strExpr)
    {
        _fired = true;   // request the app-local adapter type injection for this file (Apply)
        // Structured type slots (§1 — types are nodes): the adapter owner + the `kotlin.String` ctor-arg type as
        // `{t:"fqn",…}` nodes; BirTypeLowering lowers `kotlin.String` -> `System.String` downstream.
        return new JsonObject
        {
            ["k"] = "new",
            ["type"] = new JsonObject { ["t"] = "fqn", ["name"] = Adapter },
            ["argTypes"] = new JsonArray { new JsonObject { ["t"] = "fqn", ["name"] = "kotlin.String" } },
            ["args"] = new JsonArray { strExpr.DeepClone() },
        };
    }

    // #156 — the value's recovered static type is a NULLABLE String (`String?`): a `Nullable`-wrapped String token. The
    // genuine-null value the strict `CharSequence?`-slot path keeps unwrapped; it gets the runtime-conditional adapter wrap.
    static bool IsStaticNullableString(JsonNode n, Env env)
        => n is JsonObject
           && StaticType.Surface(n, BirScope.FromVars(env.Vars)) is TypeNode.Nullable nn && IsStringTokT(nn.Of);

    // #156 — the runtime-conditional adapter wrap for a nullable String into a `CharSequence?` slot:
    //   v == null ? (dotkt$CharSequence)null : new dotkt$StringCharSequence(v)
    // bindOnce (mirrors RangeMembershipLowering): a stable subject (const/local/this — side-effect-free) is read in both
    // legs directly; anything else is bound to a temp via a valueBlock so a side-effecting value runs exactly once. The
    // temp's declared type is the value's own static type (a `String?` -> the nullable token, stripped downstream).
    static JsonNode WrapAdapterNullable(JsonNode v, Env env)
    {
        var subjKind = Str((v as JsonObject)?["k"]);
        var stable = subjKind is "const" or "local" or "this";
        JsonNode read; JsonNode tempStmt = null;
        if (stable) read = v;
        else
        {
            var name = "__cswrap$" + System.Threading.Interlocked.Increment(ref _counter);
            var vType = StaticType.Surface(v, BirScope.FromVars(env.Vars)) is TypeNode vt
                ? TypeNode.Write(vt) : TypeJson.Fqn("kotlin.String");
            tempStmt = new JsonObject { ["k"] = "var", ["name"] = name, ["type"] = vType, ["init"] = v.DeepClone() };
            read = new JsonObject { ["k"] = "local", ["name"] = name };
        }
        var core = new JsonObject
        {
            ["k"] = "cond",
            ["type"] = TypeJson.Fqn(CharSeq),
            ["cond"] = new JsonObject
            {
                ["k"] = "objEq", ["lhs"] = read.DeepClone(),
                ["rhs"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn(CharSeq), ["value"] = null },
            },
            ["then"] = new JsonObject { ["k"] = "const", ["type"] = TypeJson.Fqn(CharSeq), ["value"] = null },
            ["else"] = WrapAdapter(read),
        };
        return tempStmt == null
            ? core
            : new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { tempStmt }, ["result"] = core };
    }

    static int _counter;

    // POSITIVE static-String detection: only an expression whose recovered static type is provably a bare String. The
    // static type comes from the SHARED StaticType.Surface resolver (#59) — so EVERY String origin is covered uniformly:
    // a const/local/cast, a property-getter `callInstance` (`h.text`), an app top-level fun result (`get()`), a BCL call
    // (`Encoding.GetString(...)`), a `!!`/elvis `valueBlock`, a map indexer. Anything else (a StringBuilder, a user
    // CharSequence, an already-wrapped `dotkt$StringCharSequence`, an unknown expr) resolves to a non-String type -> no
    // wrap. Prior to #148 this was an ad-hoc switch that only saw const/local/cast/field + a `ret`-carrying call, so a
    // ret-less String receiver (property read / app-fun result / `!!`) reached the `dotkt$CharSequence` slot RAW and the
    // stdlib extension's `subSequence`/`get_length` interface call hit the body-less synthetic -> EntryPointNotFound.
    static bool IsStaticString(JsonNode n, Env env, bool allowNullable = false)
    {
        if (n is not JsonObject) return false;
        var t = StaticType.Surface(n, BirScope.FromVars(env.Vars));
        if (allowNullable && t is TypeNode.Nullable nn) t = nn.Of;
        return IsStringTokT(t);
    }

    static bool IsCharSeqSlot(string t) => Bare(t) == CharSeq;

    // Strip a leading `nullable:` then `@` (the this-assembly-emitted marker) so `@dotkt$CharSequence` /
    // `nullable:kotlin.String` compare by their bare identity.
    static string Bare(string t)
    {
        if (t == null) return null;
        t = t.Trim();
        if (t.StartsWith("nullable:", StringComparison.Ordinal)) t = t["nullable:".Length..];
        if (t.StartsWith("@", StringComparison.Ordinal)) t = t[1..];
        return t;
    }
}


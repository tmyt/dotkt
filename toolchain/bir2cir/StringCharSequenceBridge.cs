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

    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs = null)
    {
        _refs = refs;
        _fired = false;
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
            // A NON-nullable `dotkt$CharSequence` slot is guaranteed a non-null value by the frontend, so a nullable-String
            // value (a `!!`/elvis result whose node still carries the pre-strip `String?` type at this stage) is safe to
            // peel + wrap — this is the `x!!.split(...)` / `map[k]!!.split(...)` receiver path. A nullable slot
            // (`CharSequence?`-receiver ext) keeps the strict bare-String test so a genuine null stays unwrapped.
            if (IsStaticString(a, env, allowNullable: tn is TypeNode.Fqn))
                args[i] = WrapAdapter(a);
        }
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

    // (d): a store into a CharSequence-typed local `var cs: CharSequence = <String>`.
    static void WrapVarInit(JsonObject node, Env env)
    {
        if (IsCharSeqT(TypeJson.Read(node["type"])) && node["init"] is JsonNode init && IsStaticString(init, env))
            node["init"] = WrapAdapter(init);
    }

    // (c): a return of a static String into a CharSequence return type.
    static void WrapReturn(JsonObject node, Env env)
    {
        if (IsCharSeqT(env.RetType) && node["value"] is JsonNode v && IsStaticString(v, env))
            node["value"] = WrapAdapter(v);
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


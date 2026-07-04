using System.Text.Json.Nodes;

// Variance -> invariance type-argument REALIGNMENT for invariant @ClrTypeAlias collection generics.
//
// kotc's frontend approximates a use-site `in`/`out` variance projection to `kotlin.Any` (harmless on the JVM = erased).
// e.g. `val name: String by data` (data: Map<String, Any?>): the property-delegate getValue chain calls
// `getOrImplicitDefault<K,V>(this)` on a receiver that the frontend views as `Map<in String, V>` — so it infers
// `K = kotlin.Any` for the call while the ACTUAL receiver is `Map<String, V>`. On the CLR, `IDictionary<,>` is
// INVARIANT: an `IDictionary<string,V>` argument cannot flow into an `IDictionary<object,V>` param (and
// `IDictionary<object,object>::ContainsKey` finds no interface slot on a runtime `Dictionary<string,object>`) ->
// EntryPointNotFound. The fix: for each type param the callee's `sig` places inside an INVARIANT constructed
// collection generic, realign the CALL's `typeArg` to the corresponding type-argument of the ACTUAL argument's
// declared type, overriding the frontend's `kotlin.Any` variance-approximation.
//
// Same bug class as the mutable-map for-in reroute (il-mapforin) and HashSet(cap, loadFactor) (il-hashset2): a
// general variance -> invariance realignment, scoped to the invariant BCL collection generics so covariant/
// unconstrained positions and non-collection params are left as-is. A `typeArg` is changed ONLY when the actual arg
// pins it to a DIFFERENT concrete type, so a genuine `<Any>` call (whose receiver really is `Map<Any,V>`) is a no-op.
//
// Runs in BIR-space (before MemberCallSubstitution + type lowering), in every non-ref build. `typeArgs` are positional
// to the callee's declared type params; the callee's ORDERED param names (aggregated from all input BIR files, keyed
// by name|arity) map a `sig`'s `gp:NAME` to its `typeArg` index. An unresolvable callee (not a local input file) is
// left untouched — an app build never re-lowers a referenced stdlib body, so the rt-stdlib self-build fix suffices.
static class MapVarianceRealign
{
    // The invariant BCL collection generics (their @ClrTypeAlias Kotlin FQNs). Type params here do NOT lift via CLR
    // variance — unlike List/Collection/Iterable (`IReadOnly*<out T>` covariant), which stay untouched.
    static readonly HashSet<string> InvariantCollections = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Map", "kotlin.collections.MutableMap",
        "kotlin.collections.HashMap", "kotlin.collections.LinkedHashMap",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
        "kotlin.collections.HashSet", "kotlin.collections.LinkedHashSet",
    };

    // funName|arity -> the callee's ORDERED generic-param names, aggregated across every input BIR file (a same-
    // assembly cross-file call keeps `owner:null`, so the callee may live in another input). First-wins; a
    // gp-name-membership check at the realign site guards a same-name/same-arity-but-different-params overload.
    public static Dictionary<string, string[]> CollectCalleeTypeParams(IEnumerable<JsonNode> roots)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, map);
        return map;
    }

    static void CollectFrom(JsonNode root, Dictionary<string, string[]> map)
    {
        if (root is not JsonObject o) return;
        CollectMethods(o["methods"], map);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) CollectFrom(t, map);
    }

    static void CollectMethods(JsonNode methods, Dictionary<string, string[]> map)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
        {
            if (m is not JsonObject mo) continue;
            if (Str(mo["name"]) is not string name) continue;
            if (mo["typeParams"] is not JsonArray tps || tps.Count == 0) continue;
            var names = new List<string>();
            foreach (var tp in tps)
            {
                // A type param is either a bare "V" string or a {name:"V1", constraints:[...]} object.
                if (tp is JsonValue v && v.TryGetValue<string>(out var s)) names.Add(s);
                else if (tp is JsonObject to && Str(to["name"]) is string n) names.Add(n);
            }
            if (names.Count == 0) continue;
            map.TryAdd(name + "|" + names.Count, names.ToArray());
        }
    }

    public static void Apply(JsonNode root, IReadOnlyDictionary<string, string[]> calleeTypeParams)
    {
        if (root is not JsonObject o) return;
        ProcessMethods(o["methods"], calleeTypeParams);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) Apply(t, calleeTypeParams);
    }

    static void ProcessMethods(JsonNode methods, IReadOnlyDictionary<string, string[]> calleeTypeParams)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
        {
            if (m is not JsonObject mo) continue;
            // Per-method local type environment: params + local `var` declarations -> its declared BIR type token.
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mo["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && Str(po["type"]) is string pt)
                        env[pn] = pt;
            if (mo["body"] is JsonNode body)
            {
                GatherLocals(body, env);
                Walk(body, env, calleeTypeParams);
            }
        }
    }

    static void GatherLocals(JsonNode node, Dictionary<string, string> env)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n && Str(o["type"]) is string t)
                env.TryAdd(n, t);
            foreach (var kv in o)
                if (kv.Value != null) GatherLocals(kv.Value, env);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) GatherLocals(it, env);
    }

    static void Walk(JsonNode node, Dictionary<string, string> env, IReadOnlyDictionary<string, string[]> calleeTypeParams)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "callStatic" || k == "callInstance")
                Realign(o, env, calleeTypeParams);
            foreach (var kv in o)
                if (kv.Value != null) Walk(kv.Value, env, calleeTypeParams);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) Walk(it, env, calleeTypeParams);
    }

    static void Realign(JsonObject call, Dictionary<string, string> env, IReadOnlyDictionary<string, string[]> calleeTypeParams)
    {
        if (call["typeArgs"] is not JsonArray typeArgs || typeArgs.Count == 0) return;
        if (Str(call["method"]) is not string method) return;
        if (Str(call["sig"]) is not string sig || sig.Length == 0) return;
        if (call["args"] is not JsonArray args) return;
        // The callee's ordered generic-param names -> a gp:NAME's typeArg index. Keyed by name|arity; the membership
        // check below rejects a mismatched same-name/same-arity overload (its gp names would differ).
        if (!calleeTypeParams.TryGetValue(method + "|" + typeArgs.Count, out var gpNames)) return;

        var sigParams = SplitTop(sig);
        // callStatic (extension): the receiver IS arg0 / sig param0. callInstance: kotc's `sig` covers the VALUE params
        // and `args` are those value params — a positional 1:1 either way (the separate `recv` is not matched here).
        var count = Math.Min(sigParams.Count, args.Count);
        Dictionary<string, string> subst = null;
        for (var i = 0; i < count; i++)
        {
            if (InvariantGenericArgs(sigParams[i]) is not IReadOnlyList<string> sigGps) continue;
            if (ActualArgType(args[i], env) is not string actual) continue;
            if (InvariantGenericArgs(actual) is not IReadOnlyList<string> actualGps) continue;
            var n = Math.Min(sigGps.Count, actualGps.Count);
            for (var j = 0; j < n; j++)
            {
                var sg = sigGps[j];
                if (!sg.StartsWith("gp:", StringComparison.Ordinal)) continue;   // a concrete sig arg pins nothing new
                var concrete = actualGps[j];
                if (concrete.StartsWith("gp:", StringComparison.Ordinal)) continue; // actual still open -> leave as-is
                (subst ??= new Dictionary<string, string>(StringComparer.Ordinal))[sg["gp:".Length..]] = concrete;
            }
        }
        if (subst == null) return;
        foreach (var (gpName, concrete) in subst)
        {
            var idx = Array.IndexOf(gpNames, gpName);
            if (idx < 0 || idx >= typeArgs.Count) continue;       // gp not this callee's -> mismatched overload, skip
            if (Str(typeArgs[idx]) == concrete) continue;         // already aligned (a genuine <Any> call is a no-op)
            typeArgs[idx] = concrete;
        }
    }

    // The generic type-argument tokens of a token whose head (arity-stripped) is an INVARIANT collection generic,
    // e.g. `@kotlin.collections.Map[gp:K,gp:V]` -> ["gp:K","gp:V"]. Null for a non-collection or non-generic token —
    // which prevents matching against an unrelated bracketed shape (func:/array:) positionally.
    static IReadOnlyList<string> InvariantGenericArgs(string token)
    {
        var t = token.Trim();
        if (t.StartsWith("@", StringComparison.Ordinal)) t = t[1..];
        var br = t.IndexOf('[');
        if (br < 0 || !t.EndsWith("]", StringComparison.Ordinal)) return null;
        var head = StripArity(t[..br]);
        if (!InvariantCollections.Contains(head)) return null;
        return SplitTop(t[(br + 1)..^1]);
    }

    // The declared BIR type of a call argument node: a local/param reference resolved through the method's type env,
    // else the node's own `type`/`retType` when present. Null when the argument's type is not statically recoverable
    // here (a bare `this`, a literal, etc.) — such args simply do not drive a realignment.
    static string ActualArgType(JsonNode arg, Dictionary<string, string> env)
    {
        if (arg is not JsonObject o) return null;
        if (Str(o["k"]) == "local" && Str(o["name"]) is string nm && env.TryGetValue(nm, out var t)) return t;
        if (Str(o["type"]) is string tt) return tt;
        if (Str(o["retType"]) is string rt) return rt;
        return null;
    }

    static string StripArity(string s)
    {
        var bt = s.IndexOf('`');
        return bt < 0 ? s : s[..bt];
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;

    // Top-level comma split respecting `[...]` nesting.
    static IReadOnlyList<string> SplitTop(string value)
    {
        if (value.Length == 0) return Array.Empty<string>();
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[') depth++;
            else if (value[i] == ']') depth--;
            else if (value[i] == ',' && depth == 0)
            {
                result.Add(value[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(value[start..].Trim());
        return result;
    }
}

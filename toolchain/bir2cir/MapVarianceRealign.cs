using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Variance -> invariance type-argument REALIGNMENT for invariant @ClrTypeAlias collection generics.
//
// kotc's frontend approximates a use-site `in`/`out` variance projection to `kotlin.Any` (harmless on the JVM = erased).
// e.g. `val name: String by data` (data: Map<String, Any?>): the property-delegate getValue chain calls
// `getOrImplicitDefault<K,V>(this)` on a receiver the frontend views as `Map<in String, V>` — so it infers
// `K = kotlin.Any` for the call while the ACTUAL receiver is `Map<String, V>`. On the CLR `IDictionary<,>` is INVARIANT:
// an `IDictionary<string,V>` argument cannot flow into an `IDictionary<object,V>` param -> EntryPointNotFound. The fix:
// for each type param the callee places inside an INVARIANT constructed collection generic, realign the CALL's `typeArg`
// to the corresponding type-argument of the ACTUAL argument's declared type, overriding the `kotlin.Any` approximation.
//
// #37 m1: FULLY POSITIONAL. kotc's `sig` now collapses every type var to `gp:T` (indistinguishable by name), so the
// realignment keys on the callee's STRUCTURED param TYPES (a `Tv` in an invariant-collection param names the type-param
// INDEX via its `i`), matched positionally against the actual arg's structured type — never a `gp:NAME` string match.
static class MapVarianceRealign
{
    // The invariant BCL collection generics (their @ClrTypeAlias Kotlin FQNs). Type params here do NOT lift via CLR
    // variance — unlike List/Collection/Iterable (`IReadOnly*<out T>` covariant), which stay untouched.
    public static readonly HashSet<string> InvariantCollections = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Map", "kotlin.collections.MutableMap",
        "kotlin.collections.HashMap", "kotlin.collections.LinkedHashMap",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
        "kotlin.collections.HashSet", "kotlin.collections.LinkedHashSet",
    };

    // funName|typeParamCount -> the callee's ORDERED param TYPES (structured), aggregated across every input BIR file
    // (a same-assembly cross-file call keeps `owner:null`, so the callee may live in another input). First-wins.
    public static Dictionary<string, TypeNode[]> CollectCalleeTypeParams(IEnumerable<JsonNode> roots)
    {
        var map = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, map);
        return map;
    }

    static void CollectFrom(JsonNode root, Dictionary<string, TypeNode[]> map)
    {
        if (root is not JsonObject o) return;
        CollectMethods(o["methods"], map);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) CollectFrom(t, map);
    }

    static void CollectMethods(JsonNode methods, Dictionary<string, TypeNode[]> map)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
        {
            if (m is not JsonObject mo) continue;
            if (Str(mo["name"]) is not string name) continue;
            if (mo["typeParams"] is not JsonArray tps || tps.Count == 0) continue;
            var paramTypes = (mo["params"] as JsonArray ?? new JsonArray())
                .Select(p => (p as JsonObject) is JsonObject po ? TypeJson.Read(po["type"]) : null).ToArray();
            map.TryAdd(name + "|" + tps.Count, paramTypes);
        }
    }

    public static void Apply(JsonNode root, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams, ReferenceMetadataIndex refs)
    {
        if (root is not JsonObject o) return;
        ProcessMethods(o["methods"], calleeTypeParams, refs);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) Apply(t, calleeTypeParams, refs);
    }

    static void ProcessMethods(JsonNode methods, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams, ReferenceMetadataIndex refs)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
        {
            if (m is not JsonObject mo) continue;
            // Per-method local type environment: params + local `var` declarations -> its declared structured type.
            var env = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            if (mo["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                        env[pn] = pt;
            // The method's own type-param bounds: `M -> MutableMap<K,…>`. Recovers the PRECISE static type of a receiver
            // whose declared type is a type-param `Tv` (used by OwnerVarianceRealign to undo the `in K`->Any variance
            // approximation). Keyed by the type-param INDEX (matching a receiver Tv.I).
            var constraints = new Dictionary<int, TypeNode>();
            if (mo["typeParams"] is JsonArray tps)
                for (var i = 0; i < tps.Count; i++)
                    if (tps[i] is JsonObject to && to["constraints"] is JsonArray cs)
                        foreach (var c in cs)
                            if (TypeJson.Read(c) is TypeNode ct && InvariantGenericArgs(ct) != null) { constraints[i] = ct; break; }
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mo["body"] is JsonNode body)
            {
                GatherLocals(body, env, aliases);
                if (constraints.Count > 0) RealignVarTypes(body, env, aliases, constraints);
                Walk(body, env, calleeTypeParams, aliases, constraints, refs);
            }
        }
    }

    static void GatherLocals(JsonNode node, Dictionary<string, TypeNode> env, Dictionary<string, string> aliases)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n)
            {
                if (TypeJson.Read(o["type"]) is TypeNode t) env.TryAdd(n, t);
                if (o["init"] is JsonObject io && Str(io["k"]) == "local" && Str(io["name"]) is string src)
                    aliases.TryAdd(n, src);
            }
            foreach (var kv in o)
                if (kv.Value != null) GatherLocals(kv.Value, env, aliases);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) GatherLocals(it, env, aliases);
    }

    // Restore the type an inlined temp had BEFORE the `in`/`out`->kotlin.Any variance approximation erased it. Recover
    // each temp's precise type from its alias-root source (a `Tv` param -> its invariant-collection bound).
    static void RealignVarTypes(JsonNode node, Dictionary<string, TypeNode> env,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<int, TypeNode> constraints)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "var" && Str(o["name"]) is string n && TypeJson.Read(o["type"]) is TypeNode declType
                && aliases.ContainsKey(n))
            {
                var root = ResolveAliasRoot(n, aliases);
                if (env.TryGetValue(root, out var srcType) && RealignedType(declType, srcType, constraints) is TypeNode nt && nt != declType)
                { o["type"] = TypeJson.Write(nt); env[n] = nt; }
            }
            foreach (var kv in o)
                if (kv.Value != null) RealignVarTypes(kv.Value, env, aliases, constraints);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) RealignVarTypes(it, env, aliases, constraints);
    }

    // The precise type for a temp declared `declType` but aliased to a source of `srcType`. When the source is a
    // type-param `Tv`: a bare kotlin.Any/object temp regains that Tv; a temp declared as an invariant collection generic
    // regains the bound's concrete args wherever it holds an over-approximated (kotlin.Any) position.
    static TypeNode RealignedType(TypeNode declType, TypeNode srcType, IReadOnlyDictionary<int, TypeNode> constraints)
    {
        if (srcType is not TypeNode.Tv srcTv) return declType;
        if (IsObjectish(declType)) return srcType;
        if (!constraints.TryGetValue(srcTv.I, out var bound)) return declType;
        if (InvariantGenericArgs(declType) is not TypeNode[] declArgs) return declType;
        if (InvariantGenericArgs(bound) is not TypeNode[] boundArgs || boundArgs.Length != declArgs.Length) return declType;
        return RealignArgs((TypeNode.Fqn)declType, declArgs, boundArgs);
    }

    static void Walk(JsonNode node, Dictionary<string, TypeNode> env, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<int, TypeNode> constraints, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject o)
        {
            var k = Str(o["k"]);
            if (k == "callStatic" || k == "callInstance")
                Realign(o, env, calleeTypeParams);
            if (k == "callInstance")
                OwnerVarianceRealign(o, env, aliases, constraints);
            if (k == "new")
                RealignFactoryCtorArgTypes(o, refs);
            foreach (var kv in o)
                if (kv.Value != null) Walk(kv.Value, env, calleeTypeParams, aliases, constraints, refs);
        }
        else if (node is JsonArray a)
            foreach (var it in a)
                if (it != null) Walk(it, env, calleeTypeParams, aliases, constraints, refs);
    }

    // CONSTRUCTION-ARGUMENT covariance realign (il-bymap regression, klib migration #80): a collection-factory
    // LITERAL (`mapOf(...)`/`listOf(...)`/`setOf(...)`) passed directly as a `new` node's argument infers its OWN
    // typeArgs from the literal's element/pair VALUES (Kotlin's lower-bound inference — e.g. `mapOf("k1" to
    // "Alice", "k2" to 30)` with String/Int values infers `V = Comparable<Any>`, the tightest common supertype of
    // the two value literals), which can be NARROWER than the constructor's declared parameter type when the
    // target slot's Kotlin type is wider (`User(data: Map<String, Any?>)`). Kotlin accepts this with NO cast —
    // `Map`/`List`/`Set` are declaration-site covariant (`out V`) — but MemberCallSubstitution's `TryFactorySubst`
    // builds the literal's runtime `Dictionary<K,V>`/`List<E>`/`HashSet<E>` straight off those narrower typeArgs,
    // and the CLR's generic collection instantiations are INVARIANT: passing a `Dictionary<string,IComparable>`
    // where `IDictionary<string,object>` is expected is unverifiable (ilverify StackUnexpected — the ctor argument
    // slot never reconciles the two). Realign the factory call's `typeArgs` to the constructor's declared
    // `argTypes` slot HERE, before MemberCallSubstitution builds the literal, so it is constructed at the WIDE
    // type Kotlin already type-checked the assignment against — the same "realign to the actually-intended type"
    // move as `Realign`/`OwnerVarianceRealign` above, just sourced from the ENCLOSING slot instead of a callee
    // constraint. BIR-space (Kotlin FQNs) — runs before type lowering + MemberCallSubstitution.
    static void RealignFactoryCtorArgTypes(JsonObject newNode, ReferenceMetadataIndex refs)
    {
        if (newNode["argTypes"] is not JsonArray declaredArgTypes) return;
        if (newNode["args"] is not JsonArray args) return;
        var n = Math.Min(declaredArgTypes.Count, args.Count);
        for (var i = 0; i < n; i++)
        {
            if (args[i] is not JsonObject call || Str(call["k"]) != "callStatic") continue;
            if (Str(call["method"]) is not string fn || refs.CollectionFactoryKind(fn) is not string kind) continue;
            if (call["typeArgs"] is not JsonArray callTypeArgs || callTypeArgs.Count == 0) continue;
            if (TypeJson.Read(declaredArgTypes[i]) is not TypeNode declared) continue;
            if (UnwrapNullableOblivious(declared) is not TypeNode.Fqn { Args: { } declArgs }) continue;
            var expected = kind == "map" ? 2 : 1;                       // map -> [K,V]; list/set -> [E]
            if (declArgs.Length != expected || callTypeArgs.Count != expected) continue;
            for (var j = 0; j < expected; j++)
            {
                var cur = TypeJson.Read(callTypeArgs[j]);
                if (cur != declArgs[j]) callTypeArgs[j] = TypeJson.Write(declArgs[j]);
            }
        }
    }

    // Strip BOTH nullability wrappers (`nullable`/`oblivious`) off a declared slot type before matching it against
    // the factory call's own Fqn — a `Map<String,Any?>?` param is a `Nullable(Fqn(Map,[...]))` at this layer.
    static TypeNode UnwrapNullableOblivious(TypeNode t) => t switch
    {
        TypeNode.Nullable n => UnwrapNullableOblivious(n.Of),
        TypeNode.Oblivious ob => UnwrapNullableOblivious(ob.Of),
        _ => t,
    };

    // Undo the use-site `in`/`out` variance over-approximation baked into an INLINED Map member call: recover the
    // receiver's PRECISE static type from its type-param bound (`M : MutableMap<K,…>`) and realign each over-approximated
    // (kotlin.Any) ownerType position to the constraint's concrete arg.
    static void OwnerVarianceRealign(JsonObject call, Dictionary<string, TypeNode> env,
        IReadOnlyDictionary<string, string> aliases, IReadOnlyDictionary<int, TypeNode> constraints)
    {
        if (TypeJson.Read(call["ownerType"]) is not TypeNode ownerType) return;
        if (InvariantGenericArgs(ownerType) is not TypeNode[] ownerArgs || !ownerArgs.Any(IsObjectish)) return;
        if (call["recv"] is not JsonObject recv || Str(recv["k"]) != "local" || Str(recv["name"]) is not string rn) return;
        var root = ResolveAliasRoot(rn, aliases);
        if (!env.TryGetValue(root, out var rootType) || rootType is not TypeNode.Tv rootTv) return;
        if (!constraints.TryGetValue(rootTv.I, out var bound)) return;
        if (InvariantGenericArgs(bound) is not TypeNode[] boundArgs || boundArgs.Length != ownerArgs.Length) return;
        var realigned = RealignArgs((TypeNode.Fqn)ownerType, ownerArgs, boundArgs);
        if (realigned != ownerType) call["ownerType"] = TypeJson.Write(realigned);
    }

    // Replace every over-approximated (kotlin.Any/object) arg with the corresponding `boundArgs` arg (when it is more
    // specific), returning the reconstructed Fqn; the original when nothing changed.
    static TypeNode RealignArgs(TypeNode.Fqn owner, TypeNode[] args, TypeNode[] boundArgs)
    {
        var realigned = new TypeNode[args.Length];
        var changed = false;
        for (var i = 0; i < args.Length; i++)
            if (IsObjectish(args[i]) && !IsObjectish(boundArgs[i])) { realigned[i] = boundArgs[i]; changed = true; }
            else realigned[i] = args[i];
        return changed ? new TypeNode.Fqn(owner.Name, realigned) : owner;
    }

    static string ResolveAliasRoot(string name, IReadOnlyDictionary<string, string> aliases)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (aliases.TryGetValue(name, out var src) && seen.Add(name)) name = src;
        return name;
    }

    static void Realign(JsonObject call, Dictionary<string, TypeNode> env, IReadOnlyDictionary<string, TypeNode[]> calleeTypeParams)
    {
        if (call["typeArgs"] is not JsonArray typeArgs || typeArgs.Count == 0) return;
        if (Str(call["method"]) is not string method) return;
        if (call["args"] is not JsonArray args) return;
        // The callee's ordered param TYPES (keyed by name|typeParamCount == typeArgs.Count). A `Tv` inside an
        // invariant-collection param names the type-param INDEX (its `i`); the actual arg pins that typeArg.
        if (!calleeTypeParams.TryGetValue(method + "|" + typeArgs.Count, out var paramTypes)) return;

        var count = Math.Min(paramTypes.Length, args.Count);
        Dictionary<int, TypeNode> subst = null;
        for (var i = 0; i < count; i++)
        {
            if (paramTypes[i] is not TypeNode pt || InvariantGenericArgs(pt) is not TypeNode[] sigGps) continue;
            if (ActualArgType(args[i], env) is not TypeNode actual || InvariantGenericArgs(actual) is not TypeNode[] actualGps) continue;
            var n = Math.Min(sigGps.Length, actualGps.Length);
            for (var j = 0; j < n; j++)
                if (sigGps[j] is TypeNode.Tv tv && actualGps[j] is not TypeNode.Tv && tv.I >= 0 && tv.I < typeArgs.Count)
                    (subst ??= new Dictionary<int, TypeNode>())[tv.I] = actualGps[j];
        }
        if (subst == null) return;
        foreach (var (idx, concrete) in subst)
            if (!(TypeJson.Read(typeArgs[idx]) is TypeNode cur && cur == concrete))   // already aligned -> no-op
                typeArgs[idx] = TypeJson.Write(concrete);
    }

    // The generic type-argument tokens of a type whose head is an INVARIANT collection generic, e.g.
    // `kotlin.collections.Map<K,V>` -> [K,V]. Null for a non-collection / non-generic type.
    static TypeNode[] InvariantGenericArgs(TypeNode t) =>
        t is TypeNode.Fqn { Args: { } args } f && InvariantCollections.Contains(f.Name) ? args : null;

    // The declared structured type of a call argument node: a local/param reference resolved through the method's type
    // env, else the node's own `type`/`retType`. Null when not statically recoverable here.
    static TypeNode ActualArgType(JsonNode arg, Dictionary<string, TypeNode> env)
    {
        if (arg is not JsonObject o) return null;
        if (Str(o["k"]) == "local" && Str(o["name"]) is string nm && env.TryGetValue(nm, out var t)) return t;
        return TypeJson.Read(o["type"]) ?? TypeJson.Read(o["ret"]);
    }

    // The `in`/`out` use-site variance over-approximation is `kotlin.Any` — but a projected key/value is genuinely
    // NULLABLE (`Map<in K, V>` -> the key projects to `Any?`), so post-#37/#48 kotc emits the marker as the wrapped
    // `{t:nullable,of:kotlin.Any}` rather than a bare `kotlin.Any` (pre-#48 the `?` was a retired scalar flag, leaving a
    // bare Fqn here). See through the nullability wrapper so the realignment still recognizes the approximation and
    // restores the concrete constraint arg — without this, a `MutableMap<Any?, MutableList<T>>` inlined receiver in
    // groupByTo left `clrMapPut`/`set_Item` dispatched on `IDictionary<object,…>` (value-type-invariance EntryPointNotFound).
    static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null } f => f.Name is "kotlin.Any" or "object",
        _ => false,
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}

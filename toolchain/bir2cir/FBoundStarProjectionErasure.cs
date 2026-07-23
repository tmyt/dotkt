using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR generics are reified and invariant, so a Kotlin star projection on a bounded generic cannot be represented by
// substituting the bound (`Key<*>` -> `Key<Element>`): a `Key<Concrete>` value is not a `Key<Element>`.  Nor can a
// self/dependent bound (`Node<N : Node<N>>`) legally use object.  Give every local one-parameter BOUNDED generic a
// deterministic non-generic existential view instead.  Every closed `G<X>` implements that view, preserving identity
// and allowing a `G<X>` value to flow through a `G<*>` slot without a fictitious variance conversion.
//
// BIR remains unchanged (it faithfully says Node<Any>, the frontend's star spelling).  This pass is entirely bir2cir:
// it synthesizes the CLR-facing interface, attaches it to the generic declaration, and rewrites only the invalid
// objectish construction.  For a non-Any bound, `G<Any>` is not valid Kotlin, so that spelling unambiguously originated
// as the frontend's current star erasure; genuine `G<Any>` is never captured.  Reference and runtime builds both run it,
// so downstream compilations recognize the deterministic view from DotKt provenance.  Multi-parameter projection masks
// remain a separate generalization; silently erasing their still-concrete positions would be incorrect.
static class FBoundStarProjectionErasure
{
    const string Suffix = "$dotkt_star";

    sealed class Owner
    {
        public string Name;
        public string ErasedName;
        public JsonObject Def;
        public JsonObject Root;
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.OfType<JsonObject>().ToList();
        var owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        foreach (var root in rootList) Collect(root, root, owners);

        foreach (var owner in owners.Values) Synthesize(owner, owners);
        foreach (var root in rootList) Rewrite(root, owners, refs);
    }

    static void Collect(JsonObject root, JsonObject container, Dictionary<string, Owner> owners)
    {
        if (container["types"] is not JsonArray types) return;
        foreach (var def in types.OfType<JsonObject>().ToList())
        {
            var name = Str(def["name"]);
            if (name != null && IsSingleBounded(def))
                owners.TryAdd(name, new Owner { Name = name, ErasedName = name + Suffix, Def = def, Root = root });
            Collect(root, def, owners);
        }
    }

    static bool IsSingleBounded(JsonObject def)
    {
        // Lifted/local compiler artifacts are not part of the Kotlin ABI and cannot be named by a
        // downstream star-projected use.  Attaching an existential interface to them also turns
        // their implementation-detail type variables into public CLR MethodImpl signatures.
        if (Bool(def["generated"])) return false;
        if (def["typeParams"] is not JsonArray tps || tps.Count != 1 || tps[0] is not JsonObject tp
            || tp["constraints"] is not JsonArray constraints) return false;
        return constraints.Any(c => TypeJson.Read(c) is TypeNode t && !IsObjectish(t));
    }

    static void Synthesize(Owner owner, IReadOnlyDictionary<string, Owner> owners)
    {
        var rootTypes = owner.Root["types"] as JsonArray;
        if (rootTypes == null || rootTypes.OfType<JsonObject>().Any(t => Str(t["name"]) == owner.ErasedName)) return;

        var inherited = new JsonArray();
        AddErasedAncestor(owner.Def["base"], inherited, owners);
        if (owner.Def["interfaces"] is JsonArray interfaces)
            foreach (var i in interfaces) AddErasedAncestor(i, inherited, owners);

        var methods = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (owner.Def["methods"] is JsonArray declared)
        {
            var originals = declared.OfType<JsonObject>().ToList();
            foreach (var method in originals)
            {
                if (Bool(method["static"]) || !IsPublic(method)) continue;
                var dependent = ContainsOwnerTvInSignature(method);
                var slot = InterfaceSlot(method, dependent ? StarMethodName(owner, method) : null);
                var key = MethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                methods.Add(slot);
                if (dependent) declared.Add(BridgeMethod(owner, method));
            }
        }

        var erased = new JsonObject
        {
            ["name"] = owner.ErasedName,
            ["kind"] = "interface",
            ["base"] = null,
            ["interfaces"] = inherited,
            ["fields"] = new JsonArray(),
            ["ctors"] = new JsonArray(),
            ["methods"] = methods,
            ["properties"] = new JsonArray(),
            ["attrs"] = new JsonArray(),
        };
        if (owner.Def["vis"] != null) erased["vis"] = owner.Def["vis"].DeepClone();
        rootTypes.Add(erased);

        var ownerIfaces = owner.Def["interfaces"] as JsonArray;
        if (ownerIfaces == null) owner.Def["interfaces"] = ownerIfaces = new JsonArray();
        if (!ownerIfaces.Any(i => TypeJson.Read(i) is TypeNode.Fqn f && f.Name == owner.ErasedName))
            ownerIfaces.Add(TypeJson.Write(new TypeNode.Fqn(owner.ErasedName)));
    }

    static void AddErasedAncestor(JsonNode slot, JsonArray target, IReadOnlyDictionary<string, Owner> owners)
    {
        if (TypeJson.Read(slot) is not TypeNode.Fqn f || !owners.TryGetValue(f.Name, out var ancestor)) return;
        if (!target.Any(i => TypeJson.Read(i) is TypeNode.Fqn x && x.Name == ancestor.ErasedName))
            target.Add(TypeJson.Write(new TypeNode.Fqn(ancestor.ErasedName)));
    }

    static JsonObject InterfaceSlot(JsonObject method, string replacementName)
    {
        var slot = new JsonObject
        {
            ["name"] = replacementName ?? method["name"]?.DeepClone(),
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = true,
            ["objectOverride"] = false,
            ["vis"] = "public",
            ["params"] = EraseParams(method["params"] as JsonArray),
            ["ret"] = TypeJson.Write(EraseOwnerTv(TypeJson.Read(method["ret"]) ?? new TypeNode.Fqn("kotlin.Unit"))),
            ["body"] = new JsonArray(),
            ["attrs"] = new JsonArray(),
        };
        if (method["typeParams"] is JsonArray tps && tps.Count > 0) slot["typeParams"] = tps.DeepClone();
        return slot;
    }

    static JsonObject BridgeMethod(Owner owner, JsonObject method)
    {
        var originalParams = method["params"] as JsonArray ?? new JsonArray();
        var bridgeParams = EraseParams(originalParams);
        var args = new JsonArray();
        var sig = new JsonArray();
        for (var i = 0; i < originalParams.Count; i++)
        {
            if (originalParams[i] is not JsonObject p) continue;
            var name = Str(p["name"]) ?? "p" + i;
            var originalType = TypeJson.Read(p["type"]) ?? new TypeNode.Fqn("kotlin.Any");
            JsonNode value = new JsonObject { ["k"] = "local", ["name"] = name };
            if (ContainsOwnerTv(originalType))
                value = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Write(originalType), ["e"] = value };
            args.Add(value);
            sig.Add(TypeJson.Write(originalType));
        }

        var originalRet = TypeJson.Read(method["ret"]) ?? new TypeNode.Fqn("kotlin.Unit");
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(new TypeNode.Fqn(owner.Name, new TypeNode[] { new TypeNode.Tv("type", 0) })),
            ["virtual"] = Bool(method["virtual"]) || Bool(method["abstract"]),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = method["name"]?.DeepClone(),
            ["sig"] = sig,
            ["ret"] = TypeJson.Write(originalRet),
            ["args"] = args,
        };
        var body = new JsonArray();
        if (originalRet is TypeNode.Fqn { Name: "kotlin.Unit" })
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = call });
        else
            body.Add(new JsonObject { ["k"] = "return", ["value"] = call });

        var bridge = new JsonObject
        {
            ["name"] = StarMethodName(owner, method),
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "public",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(EraseOwnerTv(originalRet)),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
        };
        if (method["typeParams"] is JsonArray tps && tps.Count > 0) bridge["typeParams"] = tps.DeepClone();
        return bridge;
    }

    static JsonArray EraseParams(JsonArray parameters)
    {
        var result = new JsonArray();
        if (parameters == null) return result;
        foreach (var p in parameters.OfType<JsonObject>())
        {
            var copy = p.DeepClone() as JsonObject;
            if (TypeJson.Read(p["type"]) is TypeNode pt) copy["type"] = TypeJson.Write(EraseOwnerTv(pt));
            result.Add(copy);
        }
        return result;
    }

    static string StarMethodName(Owner owner, JsonObject method)
    {
        var methods = owner.Def["methods"] as JsonArray;
        var ordinal = methods == null ? 0 : methods.TakeWhile(m => !ReferenceEquals(m, method)).Count();
        return "$dotkt_star$" + Str(method["name"]) + "$" + ordinal;
    }

    static string MethodKey(JsonObject method)
    {
        var name = Str(method["name"]);
        if (name == null) return null;
        var ga = (method["typeParams"] as JsonArray)?.Count ?? 0;
        var ps = method["params"] as JsonArray;
        return name + "|" + ga + "|" + string.Join(";", ps?.OfType<JsonObject>()
            .Select(p => TypeJson.Read(p["type"])?.ToString() ?? "?") ?? Enumerable.Empty<string>());
    }

    static bool ContainsOwnerTvInSignature(JsonObject method)
    {
        if (TypeJson.Read(method["ret"]) is TypeNode ret && ContainsOwnerTv(ret)) return true;
        if (method["params"] is JsonArray ps)
            foreach (var p in ps.OfType<JsonObject>())
                if (TypeJson.Read(p["type"]) is TypeNode pt && ContainsOwnerTv(pt)) return true;
        if (method["typeParams"] is JsonArray mtps)
            foreach (var tp in mtps.OfType<JsonObject>())
                if (tp["constraints"] is JsonArray cs)
                    foreach (var c in cs)
                        if (TypeJson.Read(c) is TypeNode ct && ContainsOwnerTv(ct)) return true;
        return false;
    }

    static void Rewrite(JsonNode node, IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                BindInheritedStarMember(obj, owners);
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name") continue;
                    if (TypeJson.Read(value) is TypeNode type)
                        obj[key] = TypeJson.Write(RewriteType(type, owners, refs));
                    else
                        Rewrite(value, owners, refs);
                }
                if (Str(obj["k"]) == "callInstance"
                    && TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn erasedOwner
                    && (owners.Values.Any(o => o.ErasedName == erasedOwner.Name) || refs.HasDotKtOwner(erasedOwner.Name)))
                    obj["virtual"] = true;
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    if (value == null) continue;
                    if (TypeJson.Read(value) is TypeNode type)
                        arr[i] = TypeJson.Write(RewriteType(type, owners, refs));
                    else
                        Rewrite(value, owners, refs);
                }
                break;
        }
    }

    // A star smart-cast keeps the receiver's most-derived Kotlin type (`ComparableRange<*>.isEmpty`) even when the
    // member is declared by an ancestor (`ClosedRange<T>.isEmpty`).  Once the receiver becomes an erased interface,
    // CIR must name that exact declaring interface; ilemit is intentionally not allowed to search/infer it.
    static void BindInheritedStarMember(JsonObject call, IReadOnlyDictionary<string, Owner> owners)
    {
        if (Str(call["k"]) != "callInstance"
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn { Args: { Length: 1 } args } f
            || !IsObjectish(args[0]) || !owners.TryGetValue(f.Name, out var start)
            || Str(call["method"]) is not string method) return;

        var pc = (call["sig"] as JsonArray)?.Count
            ?? (call["argTypes"] as JsonArray)?.Count
            ?? (call["args"] as JsonArray)?.Count ?? 0;
        var ga = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        if (FindDeclaringOwner(start, method, pc, ga, owners) is { } found)
        {
            var (declaring, declaration) = found;
            call["ownerType"] = TypeJson.Write(new TypeNode.Fqn(declaring.ErasedName));
            call["virtual"] = true; // erased owner is an interface; CIR must carry callvirt explicitly
            if (ContainsOwnerTvInSignature(declaration)) call["method"] = StarMethodName(declaring, declaration);
        }
    }

    static (Owner Owner, JsonObject Method)? FindDeclaringOwner(Owner start, string method, int pc, int ga,
        IReadOnlyDictionary<string, Owner> owners)
    {
        var frontier = new List<Owner> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (frontier.Count > 0)
        {
            var matches = new List<(Owner, JsonObject)>();
            foreach (var owner in frontier)
            {
                if (!seen.Add(owner.Name)) continue;
                if (owner.Def["methods"] is JsonArray methods)
                    foreach (var m in methods.OfType<JsonObject>())
                        if (Str(m["name"]) == method && IsPublic(m) && !Bool(m["static"])
                            && ((m["params"] as JsonArray)?.Count ?? 0) == pc
                            && ((m["typeParams"] as JsonArray)?.Count ?? 0) == ga)
                            matches.Add((owner, m));
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return null; // ambiguous multiple inheritance: never guess a slot

            var next = new List<Owner>();
            foreach (var owner in frontier)
            {
                AddAncestor(owner.Def["base"], next, owners);
                if (owner.Def["interfaces"] is JsonArray interfaces)
                    foreach (var i in interfaces) AddAncestor(i, next, owners);
            }
            frontier = next;
        }
        return null;
    }

    static void AddAncestor(JsonNode slot, List<Owner> target, IReadOnlyDictionary<string, Owner> owners)
    {
        if (TypeJson.Read(slot) is TypeNode.Fqn f && owners.TryGetValue(f.Name, out var owner)) target.Add(owner);
    }

    static TypeNode RewriteType(TypeNode type, IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { Length: 1 } args } f when IsObjectish(args[0]):
            {
                var erased = owners.TryGetValue(f.Name, out var local) ? local.ErasedName : f.Name + Suffix;
                if (local != null || refs.HasDotKtOwner(erased)) return new TypeNode.Fqn(erased);
                return new TypeNode.Fqn(f.Name, args.Select(a => RewriteType(a, owners, refs)).ToArray());
            }
            case TypeNode.Fqn { Args: { } args } f:
                return new TypeNode.Fqn(f.Name, args.Select(a => RewriteType(a, owners, refs)).ToArray());
            case TypeNode.Nullable n: return new TypeNode.Nullable(RewriteType(n.Of, owners, refs));
            case TypeNode.Oblivious o: return new TypeNode.Oblivious(RewriteType(o.Of, owners, refs));
            case TypeNode.Array a: return new TypeNode.Array(RewriteType(a.Elem, owners, refs));
            case TypeNode.ByRef b: return new TypeNode.ByRef(RewriteType(b.Of, owners, refs));
            case TypeNode.Fn fn: return new TypeNode.Fn(fn.Suspend, RewriteType(fn.Ret, owners, refs),
                fn.Params.Select(p => RewriteType(p, owners, refs)).ToArray(),
                fn.Recv == null ? null : RewriteType(fn.Recv, owners, refs));
            default: return type;
        }
    }

    static bool ContainsOwnerTv(TypeNode t) => t switch
    {
        TypeNode.Tv { Scope: "type" } => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsOwnerTv),
        TypeNode.Nullable n => ContainsOwnerTv(n.Of),
        TypeNode.Oblivious o => ContainsOwnerTv(o.Of),
        TypeNode.Array a => ContainsOwnerTv(a.Elem),
        TypeNode.ByRef b => ContainsOwnerTv(b.Of),
        TypeNode.Fn fn => ContainsOwnerTv(fn.Ret) || fn.Params.Any(ContainsOwnerTv)
            || (fn.Recv != null && ContainsOwnerTv(fn.Recv)),
        _ => false,
    };

    static TypeNode EraseOwnerTv(TypeNode t) => t switch
    {
        TypeNode.Tv { Scope: "type" } => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name, args.Select(EraseOwnerTv).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseOwnerTv(n.Of)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(EraseOwnerTv(o.Of)),
        TypeNode.Array a => new TypeNode.Array(EraseOwnerTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseOwnerTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, EraseOwnerTv(fn.Ret), fn.Params.Select(EraseOwnerTv).ToArray(),
            fn.Recv == null ? null : EraseOwnerTv(fn.Recv)),
        _ => t,
    };

    static bool IsObjectish(TypeNode t) => t switch
    {
        TypeNode.Nullable n => IsObjectish(n.Of),
        TypeNode.Oblivious o => IsObjectish(o.Of),
        TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "object" or "System.Object" } => true,
        _ => false,
    };

    static bool Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    // Older/common BIR omits `vis` for Kotlin's default public visibility; explicit non-public declarations carry a
    // value (`internal`, `private`, ...).  Treat omission exactly as the emitter does, rather than dropping public slots.
    static bool IsPublic(JsonObject method) => Str(method["vis"]) is null or "public";
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize an exact CLR MethodImpl body for a Kotlin covariant interface override.
//
// Kotlin permits `override val x: Box<Derived>` for an interface slot returning `Box<Base>` when the generic
// declaration is covariant. CLR MethodImpl metadata does not: the body and declaration signatures must be byte-exact,
// even though the body return is assignable to the slot return. Keep the source declaration (and its Kotlin ABI)
// untouched, then synthesize a private exact-signature forwarding bridge. `clrInterfaceImpls` is a fully-resolved CIR
// instruction consumed mechanically by ilemit; ilemit does not decide whether a bridge is required.
static class CovariantInterfaceReturnBridge
{
    sealed class Def
    {
        public string Name;
        public string Kind;
        public int Arity;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonObject Node;
        public JsonArray Methods;
    }

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        var defs = Collect(roots);
        foreach (var cls in defs.Values.Where(d => d.Kind == "class"))
            ApplyClass(cls, defs);
    }

    static Dictionary<string, Def> Collect(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, Def>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, result);
        return result;
    }

    static void CollectFrom(JsonNode node, Dictionary<string, Def> result)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is not string name) continue;
            result[name] = new Def
            {
                Name = name,
                Kind = Str(type["kind"]),
                Arity = (type["typeParams"] as JsonArray)?.Count ?? 0,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Node = type,
                Methods = type["methods"] as JsonArray ?? new JsonArray(),
            };
            CollectFrom(type, result);
        }
    }

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs)
    {
        if (cls.Node["methods"] is not JsonArray methods) return;
        var bridges = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var bridgeOrdinal = 0;

        foreach (var ifaceSpec in ReachableInterfaces(cls, defs))
        {
            if (!defs.TryGetValue(ifaceSpec.Name, out var iface) || iface.Kind != "interface") continue;
            var ifaceArgs = EffectiveArgs(ifaceSpec, iface.Arity);
            if (ifaceArgs == null) continue;

            foreach (var slot in iface.Methods.OfType<JsonObject>())
            {
                if (Bool(slot["static"]) || Str(slot["name"]) is not string name
                    || slot["params"] is not JsonArray slotParamNodes) continue;
                var methodArity = (slot["typeParams"] as JsonArray)?.Count ?? 0;
                var slotParams = slotParamNodes.OfType<JsonObject>()
                    .Select(p => TypeJson.Read(p["type"]))
                    .Select(t => t == null ? null : SubstOwnerTvs(t, ifaceArgs)).ToArray();
                var slotRet0 = TypeJson.Read(slot["ret"]);
                var slotRet = slotRet0 == null ? null : SubstOwnerTvs(slotRet0, ifaceArgs);
                if (slotParams.Any(p => p == null) || slotRet == null) continue;

                var candidates = methods.OfType<JsonObject>().Where(m =>
                    !Bool(m["static"]) && Str(m["name"]) == name
                    && ((m["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                    && ParamsEqual(m, slotParams, ClassOwnArgs(cls))
                    && Overrides(m, iface.Name, name)).ToList();
                if (candidates.Count != 1) continue;
                var implementation = candidates[0];
                var implementationRet0 = TypeJson.Read(implementation["ret"]);
                if (implementationRet0 == null) continue;
                var implementationRet = SubstOwnerTvs(implementationRet0, ClassOwnArgs(cls));
                if (implementationRet == slotRet) continue;

                var key = name + "(" + string.Join(",", slotParams.Select(TypeKey)) + ")->" + TypeKey(slotRet);
                if (!bridges.TryGetValue(key, out var bridge))
                {
                    bridge = BuildBridge(cls, implementation, slotParams, slotRet,
                        $"dotkt$covar${SafeName(name)}${bridgeOrdinal++}");
                    bridges[key] = bridge;
                    methods.Add(bridge);
                }
                ((JsonArray)bridge["clrInterfaceImpls"]).Add(ImplDescriptor(ifaceSpec, name, slotParams, slotRet));
            }
        }
    }

    static JsonObject BuildBridge(Def cls, JsonObject implementation, TypeNode[] slotParams, TypeNode slotRet,
        string bridgeName)
    {
        var sourceParams = implementation["params"] as JsonArray ?? new JsonArray();
        var bridgeParams = new JsonArray();
        var callArgs = new JsonArray();
        var callSig = new JsonArray();
        for (var i = 0; i < slotParams.Length; i++)
        {
            var sourceParam = sourceParams[i] as JsonObject;
            var name = Str(sourceParam?["name"]) ?? "p" + i;
            bridgeParams.Add(new JsonObject { ["name"] = name, ["type"] = TypeJson.Write(slotParams[i]) });
            callArgs.Add(new JsonObject { ["k"] = "local", ["name"] = name });
            callSig.Add(sourceParam?["type"]?.DeepClone());
        }

        var implementationRet = TypeJson.Read(implementation["ret"]);
        var ownerArgs = ClassOwnArgs(cls);
        var owner = new TypeNode.Fqn(cls.Name, ownerArgs.Length == 0 ? null : ownerArgs);
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(owner),
            // The bridge itself owns the interface slot. A virtual call here can redispatch straight back into this
            // bridge (same source member, different CLR return signature) and recurse forever.
            ["virtual"] = false,
            // This call is synthesized by bir2cir with its exact CLR declaration owner.  Do not let the later
            // inherited-owner pass reinterpret it as an ordinary Kotlin receiver call and bind it back to the
            // interface slot that this bridge implements.
            ["clrOwnerResolved"] = true,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = Str(implementation["name"]),
            ["sig"] = callSig,
            ["dynRet"] = TypeJson.Write(implementationRet),
            ["ret"] = TypeJson.Write(implementationRet),
            ["args"] = callArgs,
        };
        if (implementation["typeParams"] is JsonArray methodTps)
        {
            var typeArgs = new JsonArray();
            for (var i = 0; i < methodTps.Count; i++)
                typeArgs.Add(TypeJson.Write(new TypeNode.Tv("method", i)));
            call["typeArgs"] = typeArgs;
        }

        var bridge = new JsonObject
        {
            ["name"] = bridgeName,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "private",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(slotRet),
            ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = call }),
            ["attrs"] = new JsonArray(),
            ["clrInterfaceImpls"] = new JsonArray(),
        };
        if (implementation["typeParams"] is JsonArray tps) bridge["typeParams"] = tps.DeepClone();
        return bridge;
    }

    static JsonObject ImplDescriptor(TypeNode.Fqn ifaceSpec, string member, TypeNode[] slotParams, TypeNode slotRet)
    {
        var ps = new JsonArray();
        foreach (var p in slotParams) ps.Add(TypeJson.Write(p));
        return new JsonObject
        {
            ["owner"] = TypeJson.Write(ifaceSpec),
            ["member"] = member,
            ["params"] = ps,
            ["ret"] = TypeJson.Write(slotRet),
        };
    }

    static IEnumerable<TypeNode.Fqn> ReachableInterfaces(Def cls, IReadOnlyDictionary<string, Def> defs)
    {
        var queue = new Queue<TypeNode.Fqn>(cls.Interfaces);
        var seen = new HashSet<TypeNode.Fqn>();
        while (queue.Count > 0)
        {
            var spec = queue.Dequeue();
            if (!seen.Add(spec)) continue;
            yield return spec;
            if (!defs.TryGetValue(spec.Name, out var def)) continue;
            var args = EffectiveArgs(spec, def.Arity);
            if (args == null) continue;
            foreach (var parent in def.Interfaces)
                queue.Enqueue((TypeNode.Fqn)SubstOwnerTvs(parent, args));
        }
    }

    static bool ParamsEqual(JsonObject method, TypeNode[] slotParams, TypeNode[] ownerArgs)
    {
        if (method["params"] is not JsonArray ps || ps.Count != slotParams.Length) return false;
        for (var i = 0; i < ps.Count; i++)
        {
            var p = TypeJson.Read((ps[i] as JsonObject)?["type"]);
            if (p == null || SubstOwnerTvs(p, ownerArgs) != slotParams[i]) return false;
        }
        return true;
    }

    static bool Overrides(JsonObject method, string owner, string member) =>
        method["overrides"] is JsonArray overrides && overrides.OfType<JsonObject>().Any(o =>
            TypeJson.Read(o["owner"]) is TypeNode.Fqn f && f.Name == owner
            && (Str(o["member"]) == member || "get_" + Str(o["member"]) == member || "set_" + Str(o["member"]) == member));

    static TypeNode[] ClassOwnArgs(Def def) =>
        Enumerable.Range(0, def.Arity).Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray();

    static TypeNode[] EffectiveArgs(TypeNode.Fqn spec, int arity)
    {
        if (arity == 0) return Array.Empty<TypeNode>();
        return spec.Args is { } args && args.Length == arity ? args : null;
    }

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstOwnerTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstOwnerTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args)),
        _ => type,
    };

    static string TypeKey(TypeNode type) => TypeJson.Write(type).ToJsonString();
    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static bool Bool(JsonNode node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Str(JsonNode node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

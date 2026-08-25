using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize a CLR interface slot when Kotlin satisfies it with a NON-VIRTUAL method inherited from a base class.
//
// Kotlin fake-override resolution accepts `class D : B(), I` when B has the matching public concrete function. CLR
// implicit interface implementation cannot bind I.M to an inherited non-virtual method, so D is unloadable unless it
// owns a virtual slot. kotc must remain a Kotlin-IR projection; bir2cir uses the explicit local hierarchy/signatures to
// synthesize a forwarding method in D. ilemit then emits an ordinary CIR method and wires its declared override; it
// performs no fake-override inference.
//
// Exact formal signature, generic arity and return equality are mandatory. Ambiguity is skipped, never guessed. This is
// hierarchy-driven and contains no library/type/member special cases.
static class InheritedClassInterfaceBridge
{
    sealed class Def
    {
        public string Name;
        public string Kind;
        public int Arity;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonObject Node;
        public JsonArray Methods;
    }

    readonly record struct MethodMatch(Def Owner, TypeNode.Fqn ConstructedOwner, JsonObject Method);

    public static void ApplyAll(IEnumerable<JsonNode> roots)
    {
        var rootList = roots.ToList();
        var defs = Collect(rootList);
        var classes = defs.Values.Where(d => d.Kind == "class").ToList();

        // Normalize every declaration-owned interface implementation before inspecting inherited members. Doing both
        // operations in one traversal makes the result depend on dictionary/type order: a derived class may observe a
        // still-non-virtual base method and synthesize a forwarding slot which later recursively dispatches to itself
        // after the base declaration is normalized.
        foreach (var def in classes)
            NormalizeOwnedSlots(def, defs);
        foreach (var def in classes)
            AddBridges(def, defs);
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
                Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Node = type,
                Methods = type["methods"] as JsonArray ?? new JsonArray(),
            };
            CollectFrom(type, result);
        }
    }

    static void AddBridges(Def cls, Dictionary<string, Def> defs)
    {
        if (cls.Node["methods"] is not JsonArray classMethods)
        {
            classMethods = new JsonArray();
            cls.Node["methods"] = classMethods;
            cls.Methods = classMethods;
        }

        foreach (var ifaceSpec in ReachableInterfaces(cls, defs))
        {
            if (!defs.TryGetValue(ifaceSpec.Name, out var iface) || iface.Kind != "interface") continue;
            var ifaceArgs = EffectiveArgs(ifaceSpec, iface.Arity);
            if (ifaceArgs == null) continue;

            foreach (var im in iface.Methods.OfType<JsonObject>().ToList())
            {
                if (Bool(im["static"]) || HasConcreteBody(im)) continue;
                if (Str(im["name"]) is not string name || im["params"] is not JsonArray ips) continue;
                var methodArity = (im["typeParams"] as JsonArray)?.Count ?? 0;
                var slotParams = ips.OfType<JsonObject>().Select(p => TypeJson.Read(p["type"]))
                    .Select(t => t == null ? null : SubstOwnerTvs(t, ifaceArgs)).ToArray();
                var slotRet0 = TypeJson.Read(im["ret"]);
                var slotRet = slotRet0 == null ? null : SubstOwnerTvs(slotRet0, ifaceArgs);
                if (slotParams.Any(p => p == null) || slotRet == null) continue;
                var own = ExactMethods(classMethods, name, methodArity, slotParams, slotRet, ClassOwnArgs(cls))
                    .Where(m => !Bool(m["static"]) && (Str(m["vis"]) is null or "public"))
                    .ToList();
                if (own.Count > 1) continue; // overload ambiguity: never guess which declaration owns the slot
                if (own.Count == 1) continue;

                if (Bool(cls.Node["abstract"])) continue;

                var inherited = FindNonVirtualBaseMethod(cls, defs, name, methodArity, slotParams, slotRet);
                if (inherited == null) continue;
                classMethods.Add(BuildBridge(iface, ifaceSpec, ifaceArgs, im, slotParams, slotRet, inherited.Value));
            }
        }
    }

    static void NormalizeOwnedSlots(Def cls, Dictionary<string, Def> defs)
    {
        if (cls.Node["methods"] is not JsonArray classMethods) return;

        foreach (var ifaceSpec in ReachableInterfaces(cls, defs))
        {
            if (!defs.TryGetValue(ifaceSpec.Name, out var iface) || iface.Kind != "interface") continue;
            var ifaceArgs = EffectiveArgs(ifaceSpec, iface.Arity);
            if (ifaceArgs == null) continue;

            foreach (var im in iface.Methods.OfType<JsonObject>())
            {
                if (Bool(im["static"]) || HasConcreteBody(im)) continue;
                if (Str(im["name"]) is not string name || im["params"] is not JsonArray ips) continue;
                var methodArity = (im["typeParams"] as JsonArray)?.Count ?? 0;
                var slotParams = ips.OfType<JsonObject>().Select(p => TypeJson.Read(p["type"]))
                    .Select(t => t == null ? null : SubstOwnerTvs(t, ifaceArgs)).ToArray();
                var slotRet0 = TypeJson.Read(im["ret"]);
                var slotRet = slotRet0 == null ? null : SubstOwnerTvs(slotRet0, ifaceArgs);
                if (slotParams.Any(p => p == null) || slotRet == null) continue;

                var own = ExactMethods(classMethods, name, methodArity, slotParams, slotRet, ClassOwnArgs(cls))
                    .Where(m => !Bool(m["static"]) && (Str(m["vis"]) is null or "public"))
                    .ToList();
                if (own.Count != 1) continue;

                // Kotlin does not require `open` when a declaration merely satisfies an interface. CLR interface
                // MethodImpl targets, however, must be virtual. This is ABI normalization, not Kotlin openness.
                own[0]["virtual"] = true;
            }
        }
    }

    static IEnumerable<TypeNode.Fqn> ReachableInterfaces(Def cls, Dictionary<string, Def> defs)
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

    static MethodMatch? FindNonVirtualBaseMethod(Def cls, Dictionary<string, Def> defs, string name, int methodArity,
        TypeNode[] slotParams, TypeNode slotRet)
    {
        var current = cls.Base;
        var currentOwnerArgs = ClassOwnArgs(cls);
        if (current != null) current = (TypeNode.Fqn)SubstOwnerTvs(current, currentOwnerArgs);
        var seen = new HashSet<TypeNode.Fqn>();
        while (current != null && seen.Add(current) && defs.TryGetValue(current.Name, out var def))
        {
            var args = EffectiveArgs(current, def.Arity);
            if (args == null) return null;
            var matches = ExactMethods(def.Methods, name, methodArity, slotParams, slotRet, args)
                .Where(m => !Bool(m["static"]) && !Bool(m["abstract"]) && HasConcreteBody(m)
                    && (Str(m["vis"]) is null or "public"))
                .ToList();
            if (matches.Count > 1) return null;
            if (matches.Count == 1)
            {
                var method = matches[0];
                // A virtual inherited member already participates in CLR slot dispatch. The missing case is exactly the
                // Kotlin concrete/non-virtual method; don't introduce an unnecessary shadow slot.
                if (Bool(method["virtual"]) || Bool(method["override"])) return null;
                return new MethodMatch(def, current, method);
            }
            current = def.Base == null ? null : (TypeNode.Fqn)SubstOwnerTvs(def.Base, args);
        }
        return null;
    }

    static JsonObject BuildBridge(Def iface, TypeNode.Fqn ifaceSpec, TypeNode[] ifaceArgs, JsonObject im,
        TypeNode[] slotParams, TypeNode slotRet, MethodMatch target)
    {
        var name = Str(im["name"]);
        var ps = im["params"] as JsonArray;
        var bridgeParams = new JsonArray();
        var args = new JsonArray();
        for (var i = 0; i < slotParams.Length; i++)
        {
            var pn = ps[i] is JsonObject po && Str(po["name"]) is string n ? n : "p" + i;
            var bridgeParam = new JsonObject { ["name"] = pn, ["type"] = TypeJson.Write(slotParams[i]) };
            // NullableGenericErasure recorded the interface's pre-erasure `Slot<T?>` before this late bridge existed.
            // Carry that semantic slot onto the synthesized public bridge, substituting the interface owner's type args
            // into the derived class's scope. RoundtripMetadata can then stamp the ordinary parameter attribute later.
            CopyNullableGenericFact(ps[i] as JsonObject, bridgeParam, "nullableGeneric", ifaceArgs);
            bridgeParams.Add(bridgeParam);
            args.Add(new JsonObject { ["k"] = "local", ["name"] = pn });
        }

        var rawTargetSig = new JsonArray();
        foreach (var p in (target.Method["params"] as JsonArray).OfType<JsonObject>())
            rawTargetSig.Add(p["type"]?.DeepClone());
        var typeArgs = new JsonArray();
        var methodTpCount = (im["typeParams"] as JsonArray)?.Count ?? 0;
        for (var i = 0; i < methodTpCount; i++) typeArgs.Add(TypeJson.Write(new TypeNode.Tv("method", i)));

        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(target.ConstructedOwner),
            ["virtual"] = false,
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = name,
            ["sig"] = rawTargetSig,
            // `slotRet` has already been closed through the interface specification into the derived class's frame.
            // Preserve that call-site fact just like kotc does for an ordinary call: the constructed-member return
            // sweeps must not interpret its remaining type variables as formals of `target.ConstructedOwner`.
            ["sty"] = TypeJson.Write(slotRet),
            ["dynRet"] = TypeJson.Write(slotRet),
            ["ret"] = TypeJson.Write(slotRet),
            ["args"] = args,
        };
        if (typeArgs.Count > 0) call["typeArgs"] = typeArgs;

        var body = new JsonArray();
        if (slotRet is TypeNode.Fqn { Name: "kotlin.Unit" })
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = call });
        else
            body.Add(new JsonObject { ["k"] = "return", ["value"] = call });

        var bridge = new JsonObject
        {
            ["name"] = name,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["vis"] = "public",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(slotRet),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
            ["overrides"] = new JsonArray
            {
                new JsonObject
                {
                    ["owner"] = TypeJson.Fqn(iface.Name), ["member"] = name,
                    ["kind"] = "method", ["arity"] = slotParams.Length,
                }
            },
        };
        CopyNullableGenericFact(im, bridge, "nullableGenericRet", ifaceArgs);
        if (im["typeParams"] is JsonArray tps) bridge["typeParams"] = tps.DeepClone();
        return bridge;
    }

    static void CopyNullableGenericFact(JsonObject source, JsonObject target, string key, TypeNode[] ownerArgs)
    {
        if (Str(source?[key]) is not string encoded) return;
        try
        {
            target[key] = TypeNode.ToJson(SubstOwnerTvs(TypeNode.Parse(encoded), ownerArgs));
        }
        catch
        {
            // A malformed transient fact is not an excuse to corrupt or suppress the otherwise-valid CLR bridge.
            // RoundtripMetadata follows the same fail-soft contract for absent carrier facts.
        }
    }

    static bool DeclaresExact(JsonArray methods, string name, int arity, TypeNode[] ps, TypeNode ret, TypeNode[] ownerArgs) =>
        ExactMethods(methods, name, arity, ps, ret, ownerArgs).Any();

    static IEnumerable<JsonObject> ExactMethods(JsonArray methods, string name, int arity, TypeNode[] ps, TypeNode ret,
        TypeNode[] ownerArgs)
    {
        foreach (var m in methods.OfType<JsonObject>())
        {
            if (Str(m["name"]) != name || ((m["typeParams"] as JsonArray)?.Count ?? 0) != arity) continue;
            if (m["params"] is not JsonArray mps || mps.Count != ps.Length) continue;
            var exact = true;
            for (var i = 0; i < ps.Length; i++)
            {
                var mt = mps[i] is JsonObject po ? TypeJson.Read(po["type"]) : null;
                if (mt == null || SubstOwnerTvs(mt, ownerArgs) != ps[i]) { exact = false; break; }
            }
            var mr = TypeJson.Read(m["ret"]);
            if (exact && mr != null && SubstOwnerTvs(mr, ownerArgs) == ret) yield return m;
        }
    }

    static bool HasConcreteBody(JsonObject method) => method["body"] is JsonArray body && body.Count > 0;

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

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}

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
    internal readonly record struct BridgedSlot(JsonObject Implementation, string Descriptor);

    sealed class Def
    {
        public string Name;
        public string Kind;
        public int Arity;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonObject Node;
        public JsonArray Methods;
    }

    public static IReadOnlySet<BridgedSlot> ApplyAll(IEnumerable<JsonNode> roots,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var defs = Collect(roots);
        var bridgedSlots = new HashSet<BridgedSlot>();
        // A concrete member on a derived interface is a DIM body, but it still needs an exact MethodImpl when its
        // covariant return differs from the base-interface slot. Treat interfaces and classes uniformly here: the
        // frontend override edge selects the declaration, and this pass only materializes its CLR representation.
        foreach (var cls in defs.Values.Where(d => d.Kind is "class" or "interface"))
            ApplyClass(cls, defs, refs, isValue, bridgedSlots);
        return bridgedSlots;
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

    static void ApplyClass(Def cls, IReadOnlyDictionary<string, Def> defs,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, ISet<BridgedSlot> bridgedSlots)
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
                // A private exact MethodImpl body synthesized on a derived interface is not another declaration slot
                // for implementing classes. Exclude it explicitly; consuming it here would bridge the bridge and make
                // the result depend on whether that interface happened to be visited before the class.
                if (Bool(slot["static"]) || KotlinPropertyAccessors.IsPhysicalSlotBridge(slot)
                    || Str(slot["name"]) is not string name
                    || slot["params"] is not JsonArray slotParamNodes) continue;
                var methodArity = (slot["typeParams"] as JsonArray)?.Count ?? 0;
                var slotParams = slotParamNodes.OfType<JsonObject>()
                    .Select(p => TypeJson.Read(p["type"]))
                    .Select(t => t == null ? null : SubstOwnerTvs(t, ifaceArgs)).ToArray();
                var slotRet0 = TypeJson.Read(slot["ret"]);
                var slotRet = slotRet0 == null ? null : SubstOwnerTvs(slotRet0, ifaceArgs);
                if (slotParams.Any(p => p == null) || slotRet == null) continue;
                KotlinPropertyAccessors.TryIdentity(slot, out var propertyName, out var accessorKind);

                var candidates = methods.OfType<JsonObject>().Where(m =>
                    !Bool(m["static"]) && !Bool(m["abstract"])
                    && !KotlinPropertyAccessors.IsPhysicalSlotBridge(m)
                    && SameIdentity(m, name, propertyName, accessorKind)
                    && ((m["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                    && KotlinOverrideSlotBridge.SameMethodTypeParameterShape(
                        slot["typeParams"] as JsonArray, m["typeParams"] as JsonArray,
                        ifaceArgs, ClassOwnArgs(cls))
                    && ParamsEqual(m, slotParams, ClassOwnArgs(cls))
                    && Overrides(m, iface.Name, propertyName ?? name,
                        accessorKind switch { "get" => "getter", "set" => "setter", _ => "method" })).ToList();
                if (candidates.Count != 1) continue;
                var implementation = candidates[0];
                var implementationRet0 = TypeJson.Read(implementation["ret"]);
                if (implementationRet0 == null) continue;
                var implementationRet = SubstOwnerTvs(implementationRet0, ClassOwnArgs(cls));
                if (implementationRet == slotRet) continue;
                // A return slot that diverges because the base declaration was OBJECT-ERASED (#86 D3) is
                // KotlinOverrideSlotBridge's, not this pass's. Both bridging it states the slot's signature twice on
                // one class, and the emitter binds whichever it matches first; and only that pass's bridge forwards
                // virtually, which is what a further-derived override needs. This pass keeps the Kotlin covariance it
                // was written for.
                if (KotlinOverrideSlotBridge.IsErasureDivergence(slotRet, implementationRet)) continue;

                // Method generic arity is part of the CLI slot identity. An arity-0 and arity-1 accessor can otherwise
                // share one bridge even though no MethodImpl body can implement both declarations.
                var key = name + "`" + methodArity + "<"
                          + KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                              slot["typeParams"] as JsonArray, ifaceArgs)
                          + ">(" + string.Join(",", slotParams.Select(type =>
                              ReferencedPhysicalTypeKey(type, refs, isValue))) + ")->"
                          + ReferencedPhysicalTypeKey(slotRet, refs, isValue);
                if (!bridges.TryGetValue(key, out var bridge))
                {
                    bridge = BuildBridge(cls, implementation, slotParams, slotRet,
                        $"dotkt$covar${SafeName(name)}${bridgeOrdinal++}");
                    bridges[key] = bridge;
                    methods.Add(bridge);
                    if (propertyName != null)
                    {
                        var sourceAssociation = Str(implementation[KotlinPropertyAccessors.AssociationKey]);
                        // An interface bridge is a private/final MethodImpl body for the authored public DIM, not a
                        // second property declaration. Preserve its exact source relation for round-trip metadata
                        // without attempting to attach several inherited CLR slots to one additional Property row.
                        if (cls.Kind == "interface")
                            KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(
                                bridge, propertyName, accessorKind, sourceAssociation);
                        else
                            KotlinPropertyAccessors.AssociateBridgeProperty(cls.Node, bridge, propertyName, accessorKind,
                                sourceAssociation, slotParams, slotRet);
                    }
                }
                ((JsonArray)bridge["clrInterfaceImpls"]).Add(
                    ImplDescriptor(ifaceSpec, name, methodArity, slotParams, slotRet,
                        KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(
                            slot["typeParams"] as JsonArray, ifaceArgs)));
            }
        }

        ApplyReferencedInterfaces(cls, defs, refs, isValue, methods, bridges,
            bridgedSlots, ref bridgeOrdinal);
    }

    // A referenced interface contributes no MethodDef nodes to the staged BIR. The frontend override edge still
    // names its exact declaring interface and source member, while ReferenceMetadataIndex owns the referenced
    // declaration's physical MethodDef identity and signature. Join those two authoritative facts here; neither
    // ilemit nor a different semantic pass should infer covariance from names or physical layout.
    static void ApplyReferencedInterfaces(Def cls, IReadOnlyDictionary<string, Def> defs,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue, JsonArray methods,
        Dictionary<string, JsonObject> bridges, ISet<BridgedSlot> bridgedSlots,
        ref int bridgeOrdinal)
    {
        if (refs == null) return;
        var ownArgs = ClassOwnArgs(cls);
        foreach (var implementation in methods.OfType<JsonObject>().ToList())
        {
            // A referenced suspend MethodDef exposes the physical Task ABI but not its logical Kotlin result. Until
            // that fact is carried explicitly (#511), leave suspend declarations to suspend lowering; never infer the
            // missing semantic result from Task<T> and accidentally classify an ordinary override as covariance.
            if (Bool(implementation["static"]) || Bool(implementation["abstract"])
                || IsSuspend(implementation)
                || KotlinPropertyAccessors.IsPhysicalSlotBridge(implementation)
                || Str(implementation["name"]) is not string implementationName
                || implementation["params"] is not JsonArray implementationParamNodes
                || implementation["overrides"] is not JsonArray overrides
                || TypeJson.Read(implementation["ret"]) is not TypeNode implementationRet0)
                continue;
            var implementationParams = implementationParamNodes.OfType<JsonObject>()
                .Select(parameter => TypeJson.Read(parameter["type"])).ToArray();
            if (implementationParams.Length != implementationParamNodes.Count
                || implementationParams.Any(type => type == null)) continue;
            var methodArity = (implementation["typeParams"] as JsonArray)?.Count ?? 0;

            foreach (var edge in overrides.OfType<JsonObject>())
            {
                if (TypeJson.Read(edge["owner"]) is not TypeNode.Fqn semanticOwner
                    || defs.ContainsKey(semanticOwner.Name)
                    || Str(edge["member"]) is not string sourceMember
                    || !refs.TryReferenceTypeShape(semanticOwner, out var ownerArity, out var ownerKind,
                        out _, out _) || ownerKind != "interface")
                    continue;
                var ownerArgs = semanticOwner.Args ?? Array.Empty<TypeNode>();
                if (ownerArity != ownerArgs.Length) continue;
                var accessorKind = Str(edge["kind"]) switch
                {
                    "getter" => "get",
                    "setter" => "set",
                    _ => null,
                };
                if (accessorKind == "set") continue; // a setter's Unit return cannot be covariantly narrowed
                if (!refs.TrySelectedOverrideDeclaration(semanticOwner.Name, sourceMember, accessorKind,
                        methodArity, implementationParams, ownerArgs,
                        implementation["typeParams"] as JsonArray, ownArgs,
                        selectedSuspend: false, out var declaration))
                    continue;

                var slotParams = declaration.Parameters
                    .Select(type => SupertypeGraph.SubstOwnerTvs(
                        NullableGenericErasure.EraseNullableTv(type, isValue), ownerArgs))
                    .ToArray();
                var slotRet = SupertypeGraph.SubstOwnerTvs(
                    NullableGenericErasure.EraseNullableTv(declaration.Return, isValue), ownerArgs);
                if (slotParams.Any(type => type == null) || slotRet == null
                    || !ParamsPhysicallyEqual(implementation, slotParams, ownArgs, refs, isValue))
                    continue;
                var implementationRet = SubstOwnerTvs(implementationRet0, ownArgs);
                if (implementationRet == slotRet
                    || BirTypeLowering.SamePhysicalSlotType(slotRet, implementationRet,
                        refs.Aliases, isValue, refs.PhysicalTypeNames, returnPosition: true)
                    || KotlinOverrideSlotBridge.IsErasureDivergence(slotRet, implementationRet))
                    continue;

                var physicalOwner = refs.ExactReflectedOwner(semanticOwner.Name, ownerArity);
                if (physicalOwner == null) continue;
                var descriptorOwner = new TypeNode.Fqn(physicalOwner,
                    ownerArgs.Length == 0 ? null : ownerArgs);
                // Two referenced interfaces can redeclare the same physical slot with equivalent Kotlin surface
                // spellings (`T` substituted through an oblivious edge versus the concrete type directly). One CLR
                // body can implement both MethodImpl declarations, so key the body by the canonical physical
                // signature while retaining each declaration's own descriptor below.
                var key = declaration.PhysicalMember + "`" + methodArity + "<"
                          + KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                              declaration.TypeParams, ownerArgs)
                          + ">(" + string.Join(",", slotParams.Select(type =>
                              ReferencedPhysicalTypeKey(type, refs, isValue))) + ")->"
                          + ReferencedPhysicalTypeKey(slotRet, refs, isValue);
                if (!bridges.TryGetValue(key, out var bridge))
                {
                    bridge = BuildBridge(cls, implementation, slotParams, slotRet,
                        $"dotkt$covar${SafeName(implementationName)}${bridgeOrdinal++}");
                    bridges[key] = bridge;
                    methods.Add(bridge);
                    if (accessorKind != null)
                    {
                        var sourceAssociation = Str(implementation[KotlinPropertyAccessors.AssociationKey]);
                        if (cls.Kind == "interface")
                            KotlinPropertyAccessors.MarkExactInterfaceBridgeProperty(
                                bridge, sourceMember, accessorKind, sourceAssociation);
                        else
                            KotlinPropertyAccessors.AssociateBridgeProperty(cls.Node, bridge, sourceMember,
                                accessorKind, sourceAssociation, slotParams, slotRet);
                    }
                }
                var descriptor = ImplDescriptor(descriptorOwner, declaration.PhysicalMember, methodArity,
                    slotParams, slotRet,
                    KotlinOverrideSlotBridge.SubstituteOwnerTypeParameterConstraints(
                        declaration.TypeParams, ownerArgs));
                var encoded = descriptor.ToJsonString();
                if (!((JsonArray)bridge["clrInterfaceImpls"])
                    .Any(existing => existing?.ToJsonString() == encoded))
                    ((JsonArray)bridge["clrInterfaceImpls"]).Add(descriptor);
                bridgedSlots.Add(BridgedSlotKey(implementation, descriptorOwner,
                    declaration.PhysicalMember, methodArity, slotParams, slotRet, refs, isValue));
            }
        }
    }

    internal static BridgedSlot BridgedSlotKey(JsonObject implementation, TypeNode.Fqn descriptorOwner,
        string descriptorMember, int methodArity, IEnumerable<TypeNode> slotParams, TypeNode slotRet,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var ownerName = refs.ExactReflectedOwner(descriptorOwner.Name, descriptorOwner.Args?.Length ?? 0);
        var ownerArgs = descriptorOwner.Args == null
            ? ""
            : "<" + string.Join(",", descriptorOwner.Args.Select(type =>
                ReferencedPhysicalTypeKey(type, refs, isValue))) + ">";
        var descriptor = ownerName + ownerArgs + "::" + descriptorMember + "`" + methodArity
                         + "(" + string.Join(",", slotParams.Select(type =>
                             ReferencedPhysicalTypeKey(type, refs, isValue))) + ")->"
                         + ReferencedPhysicalTypeKey(slotRet, refs, isValue);
        return new BridgedSlot(implementation, descriptor);
    }

    static string ReferencedPhysicalTypeKey(TypeNode type, ReferenceMetadataIndex refs, ValueTypeOracle isValue) =>
        TypeKey(BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValue,
            refs.PhysicalTypeNames, typeArg: false));

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
            [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
            ["clrInterfaceImpls"] = new JsonArray(),
        };
        if (cls.Kind == "interface")
            bridge[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey] = true;
        if (implementation["typeParams"] is JsonArray tps) bridge["typeParams"] = tps.DeepClone();
        return bridge;
    }

    static JsonObject ImplDescriptor(TypeNode.Fqn ifaceSpec, string member, int methodArity,
        TypeNode[] slotParams, TypeNode slotRet, JsonArray typeParams)
    {
        var ps = new JsonArray();
        foreach (var p in slotParams) ps.Add(TypeJson.Write(p));
        var descriptor = new JsonObject
        {
            ["owner"] = TypeJson.Write(ifaceSpec),
            ["member"] = member,
            ["arity"] = methodArity,
            ["params"] = ps,
            ["ret"] = TypeJson.Write(slotRet),
        };
        if (typeParams != null) descriptor["typeParams"] = typeParams.DeepClone();
        return descriptor;
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

    static bool ParamsPhysicallyEqual(JsonObject method, TypeNode[] slotParams, TypeNode[] ownerArgs,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        if (method["params"] is not JsonArray ps || ps.Count != slotParams.Length) return false;
        for (var i = 0; i < ps.Count; i++)
        {
            var parameter = TypeJson.Read((ps[i] as JsonObject)?["type"]);
            if (parameter == null) return false;
            var implementation = SubstOwnerTvs(parameter, ownerArgs);
            if (implementation != slotParams[i]
                && !BirTypeLowering.SamePhysicalSlotType(slotParams[i], implementation,
                    refs.Aliases, isValue, refs.PhysicalTypeNames, returnPosition: false))
                return false;
        }
        return true;
    }

    static bool SameIdentity(JsonObject method, string physicalName, string propertyName, string accessorKind) =>
        propertyName != null
            ? KotlinPropertyAccessors.TryIdentity(method, out var candidateName, out var candidateKind)
                && candidateName == propertyName && candidateKind == accessorKind
            : Str(method["name"]) == physicalName && !KotlinPropertyAccessors.TryIdentity(method, out _, out _);

    static bool Overrides(JsonObject method, string owner, string member, string memberKind) =>
        method["overrides"] is JsonArray overrides && overrides.OfType<JsonObject>().Any(o =>
            TypeJson.Read(o["owner"]) is TypeNode.Fqn f && f.Name == owner
            && Str(o["member"]) == member && Str(o["kind"]) == memberKind);

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
    static bool IsSuspend(JsonObject method) =>
        method["mods"] is JsonObject mods && Bool(mods["suspend"]);
    static string Str(JsonNode node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

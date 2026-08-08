using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// CLR generics are reified and invariant, so a Kotlin star projection cannot be represented by substituting Any
// (`G<*>` -> `G<object>`): a `G<Concrete>` value is not a `G<object>`. Give each used local generic a
// deterministic non-generic existential view instead. Every closed `G<X>` implements that view, preserving identity
// and allowing a `G<X>` value to flow through a `G<*>` slot without a fictitious variance conversion. The same view
// is used for Kotlin's erased generic `is`/`as` checks (`x is G<T>`): JVM semantics test only the raw classifier.
//
// BIR faithfully carries `{t:"star"}`. This pass synthesizes the CLR-facing interface, attaches it to the generic
// declaration, and rewrites only explicit star positions and erased runtime classifier tests/casts. Reference and
// runtime builds both run it, so downstream compilations recognize the view from trusted DotKt metadata. BIR from an
// older toolchain is unsupported: `G<Any>` is always the concrete Kotlin type and is never guessed back into `G<*>`.
static class FBoundStarProjectionErasure
{
    const string CarrierMark = "$dotkt$star";

    sealed class Owner
    {
        public string Name;
        public string ErasedName;
        public JsonObject Def;
        public JsonObject Root;
        public int Arity;
        public bool Needed;
    }

    public static IReadOnlyDictionary<string, string> ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.OfType<JsonObject>().ToList();
        var owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        var defs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in rootList) Collect(root, root, owners, defs);
        AllocateCarrierNames(owners, defs.Keys);
        foreach (var root in rootList) MarkNeeded(root, owners);
        MarkNeededClosure(owners);

        foreach (var owner in owners.Values.Where(o => o.Needed)) Synthesize(owner, owners, defs, refs);
        foreach (var root in rootList) RecordDeclarationSurfaces(root);
        ForeignStarProjectionBinding.ApplyAll(rootList, refs);
        foreach (var root in rootList) Rewrite(root, owners, defs, refs);
        return owners.Values.Where(o => o.Needed)
            .ToDictionary(o => o.Name, o => o.ErasedName, StringComparer.Ordinal);
    }

    // The physical name is intentionally not an ABI oracle: trusted [KotlinType] metadata carries the relation.
    // Allocate against every TypeDef in the emission unit so a user declaration (including a backtick identifier)
    // can never collide with the compiler carrier. The sorted walk makes the chosen suffix deterministic.
    static void AllocateCarrierNames(IReadOnlyDictionary<string, Owner> owners, IEnumerable<string> declaredNames)
    {
        var used = declaredNames.ToHashSet(StringComparer.Ordinal);
        foreach (var owner in owners.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            var stem = owner.Name + CarrierMark;
            var candidate = stem;
            for (var ordinal = 1; !used.Add(candidate); ordinal++) candidate = stem + "$" + ordinal;
            owner.ErasedName = candidate;
        }
    }

    // If G<T>'s existential surface exposes H<T>, substituting object would manufacture the invalid invariant
    // conversion H<T> -> H<object>. H's existential view is part of the same closure and must exist first-class.
    static void MarkNeededClosure(IReadOnlyDictionary<string, Owner> owners)
    {
        while (true)
        {
            var before = owners.Values.Count(o => o.Needed);
            MarkNeededAncestors(owners);
            foreach (var owner in owners.Values.Where(o => o.Needed).ToList())
                if (owner.Def["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>())
                    {
                        if (TypeJson.Read(method["ret"]) is TypeNode ret)
                            MarkDependentResult(ret, owners);
                        if (method["params"] is JsonArray ps)
                            foreach (var p in ps.OfType<JsonObject>())
                                if (TypeJson.Read(p["type"]) is TypeNode pt)
                                    MarkDependentResult(pt, owners);
                    }
            if (owners.Values.Count(o => o.Needed) == before) return;
        }
    }

    static void MarkDependentResult(TypeNode type, IReadOnlyDictionary<string, Owner> owners)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } f:
                if (args.Any(ContainsOwnerTv) && owners.TryGetValue(f.Name, out var nested))
                    nested.Needed = true;
                foreach (var arg in args) MarkDependentResult(arg, owners);
                break;
            case TypeNode.Nullable n: MarkDependentResult(n.Of, owners); break;
            case TypeNode.Oblivious o: MarkDependentResult(o.Of, owners); break;
            case TypeNode.Array a: MarkDependentResult(a.Elem, owners); break;
            case TypeNode.ByRef b: MarkDependentResult(b.Of, owners); break;
            case TypeNode.Fn fn:
                MarkDependentResult(fn.Ret, owners);
                foreach (var p in fn.Params) MarkDependentResult(p, owners);
                if (fn.Recv != null) MarkDependentResult(fn.Recv, owners);
                break;
        }
    }

    static void MarkNeededAncestors(IReadOnlyDictionary<string, Owner> owners)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var owner in owners.Values.Where(o => o.Needed).ToList())
            {
                void Mark(JsonNode slot)
                {
                    if (TypeJson.Read(slot) is TypeNode.Fqn f
                        && owners.TryGetValue(f.Name, out var ancestor) && !ancestor.Needed)
                    {
                        ancestor.Needed = true;
                        changed = true;
                    }
                }
                Mark(owner.Def["base"]);
                if (owner.Def["interfaces"] is JsonArray interfaces)
                    foreach (var i in interfaces) Mark(i);
            }
        }
    }

    static void Collect(JsonObject root, JsonObject container, Dictionary<string, Owner> owners,
        Dictionary<string, JsonObject> defs)
    {
        if (container["types"] is not JsonArray types) return;
        foreach (var def in types.OfType<JsonObject>().ToList())
        {
            var name = Str(def["name"]);
            if (name != null) defs.TryAdd(name, def);
            if (name != null && IsSourceGeneric(def))
                owners.TryAdd(name, new Owner
                {
                    Name = name, Def = def, Root = root,
                    Arity = (def["typeParams"] as JsonArray)?.Count ?? 0,
                    // A downstream module may introduce G<*> even when this producer never does. Every source-level
                    // generic declaration that participates in the exported Kotlin ABI therefore owns its existential
                    // view unconditionally; private declarations remain demand-driven within this compilation.
                    Needed = IsAbiVisible(def),
                });
            Collect(root, def, owners, defs);
        }
    }

    static bool IsSourceGeneric(JsonObject def)
    {
        // Lifted/local compiler artifacts are not part of the Kotlin ABI and cannot be named by a
        // downstream star-projected use.  Attaching an existential interface to them also turns
        // their implementation-detail type variables into public CLR MethodImpl signatures.
        if (Bool(def["generated"]) || HasClrTypeAlias(def)
            || BirTypeLowering.ErasesGenericApplicationToNonGenericClassifier(Str(def["name"]))) return false;
        return def["typeParams"] is JsonArray { Count: > 0 };
    }

    static bool HasClrTypeAlias(JsonObject def)
    {
        if (def["attrs"] is not JsonArray attrs) return false;
        foreach (var a in attrs.OfType<JsonObject>())
            if (TypeJson.OwnerName(a["attr"]) == "kotlin.clr.ClrTypeAlias") return true;
        return false;
    }

    static bool IsAbiVisible(JsonObject def) => Str(def["vis"]) is null or "public" or "protected" or "internal";

    // The physical existential is one non-generic interface for every projection mask. Preserve each declaration's
    // exact Kotlin type (`Pair<*, String>`, not merely Pair<*, *>) before the slot becomes that interface/object.
    // RoundtripMetadata emits these facts as [KotlinType], and dll2klib restores them without inspecting the physical
    // carrier name. Calls and locals are intentionally excluded: this is exported declaration ABI only.
    static void RecordDeclarationSurfaces(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["k"] == null && obj["name"] is JsonValue && obj["params"] is JsonArray parameters)
                {
                    foreach (var parameter in parameters.OfType<JsonObject>())
                        RecordProjectionSlot(parameter, "type", "kotlinType");
                    RecordProjectionSlot(obj, "ret", "retKotlinType");
                }
                if (obj["k"] == null && obj["type"] != null)
                    RecordProjectionSlot(obj, "type", "kotlinType");
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null).ToList())
                    RecordDeclarationSurfaces(child);
                break;
            case JsonArray array:
                foreach (var child in array.Where(v => v != null).ToList()) RecordDeclarationSurfaces(child);
                break;
        }
    }

    static void RecordProjectionSlot(JsonObject declaration, string slot, string fact)
    {
        if (declaration[fact] != null || TypeJson.Read(declaration[slot]) is not TypeNode type
            || !ContainsExplicitStar(type))
            return;
        declaration[fact] = TypeNode.ToJson(type);
    }

    static void MarkNeeded(JsonNode node, IReadOnlyDictionary<string, Owner> owners)
    {
        switch (node)
        {
            case JsonObject obj:
                var runtimeClassifier = Str(obj["k"]) is "isInst" or "cast";
                foreach (var kv in obj)
                {
                    if (kv.Value == null || kv.Key == "name") continue;
                    if (TypeJson.Read(kv.Value) is TypeNode type)
                    {
                        MarkNeededType(type, owners, runtimeClassifier && kv.Key == "type");
                    }
                    else MarkNeeded(kv.Value, owners);
                }
                break;
            case JsonArray arr:
                foreach (var value in arr)
                    if (value != null)
                    {
                        if (TypeJson.Read(value) is TypeNode type) MarkNeededType(type, owners, false);
                        else MarkNeeded(value, owners);
                    }
                break;
        }
    }

    static void MarkNeededType(TypeNode type, IReadOnlyDictionary<string, Owner> owners, bool runtimeClassifier)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } f:
                if (owners.TryGetValue(f.Name, out var owner)
                    && (runtimeClassifier || args.Any(a => a is TypeNode.Star)))
                    owner.Needed = true;
                foreach (var a in args) MarkNeededType(a, owners, false);
                break;
            case TypeNode.Nullable n: MarkNeededType(n.Of, owners, false); break;
            case TypeNode.Oblivious o: MarkNeededType(o.Of, owners, false); break;
            case TypeNode.Array a: MarkNeededType(a.Elem, owners, false); break;
            case TypeNode.ByRef b: MarkNeededType(b.Of, owners, false); break;
            case TypeNode.Fn fn:
                MarkNeededType(fn.Ret, owners, false);
                foreach (var p in fn.Params) MarkNeededType(p, owners, false);
                if (fn.Recv != null) MarkNeededType(fn.Recv, owners, false);
                break;
        }
    }

    static void Synthesize(Owner owner, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        var rootTypes = owner.Root["types"] as JsonArray;
        if (rootTypes == null || rootTypes.OfType<JsonObject>().Any(t => Str(t["name"]) == owner.ErasedName)) return;

        var inherited = new JsonArray();
        // An interface can inherit the existential view of a generic class base, but never
        // the concrete class itself. Non-generic interface contracts are handled below.
        AddErasedAncestor(owner.Def["base"], inherited, owners, refs, allowConcrete: false);
        if (owner.Def["interfaces"] is JsonArray interfaces)
            foreach (var i in interfaces) AddErasedAncestor(i, inherited, owners, refs, allowConcrete: true);

        var methods = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (owner.Def["methods"] is JsonArray declared)
        {
            var originals = declared.OfType<JsonObject>().ToList();
            foreach (var method in originals)
            {
                if (Bool(method["static"])) continue;
                // `<R : T>` has no sound CLR signature on a non-generic existential interface. Concrete Kotlin casts
                // retain G<X> and call this member there; a true G<*> receiver cannot supply a value for T.
                if (HasOwnerDependentMethodConstraint(method)) continue;
                // A non-public method cannot implicitly fill a public CLR interface slot. Give it the same
                // deterministic forwarding bridge as an owner-T-dependent signature. The bridge is declared on the
                // original owner, so it can invoke a private implementation without changing source visibility.
                var dependent = ContainsOwnerTvInSignature(method) || !IsPublic(method);
                var slot = InterfaceSlot(method, dependent ? StarMethodName(owner, method) : null, owners, refs);
                var key = MethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                methods.Add(slot);
                if (dependent) declared.Add(BridgeMethod(owner, method, owners, refs));
            }

            // A class existential cannot inherit a concrete non-generic base, yet star-typed
            // Kotlin code can call accessible members inherited from that base. Materialize
            // those contracts on the interface and forward from G<T> to the exact base
            // declaration. A generic base is represented by its own existential interface
            // and therefore terminates this walk.
            foreach (var (declaringName, method) in InheritedConcreteBaseMethods(owner, owners, defs))
            {
                if (HasOwnerDependentMethodConstraint(method)) continue;
                var bridgeName = BaseStarMethodName(declaringName, defs[declaringName], method);
                var slot = InterfaceSlot(method, bridgeName, owners, refs);
                var key = MethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                methods.Add(slot);
                declared.Add(BridgeMethod(owner, method, owners, refs, bridgeName, new TypeNode.Fqn(declaringName)));
            }
        }

        var erased = new JsonObject
        {
            ["name"] = owner.ErasedName,
            ["kind"] = "interface",
            ["generated"] = true,
            // The CLR existential view is an implementation detail, not the Kotlin source type. Preserve the exact
            // pre-lowering Kotlin projection as an opaque fact; RoundtripMetadata turns it into [KotlinType], and a
            // downstream dll2klib restores G<*> for the frontend instead of exposing this synthetic interface or
            // degrading the whole signature to Any?. The ordinary FIR -> IR path erases the captured star before BIR.
            ["kotlinType"] = TypeJson.Write(new TypeNode.Fqn(owner.Name,
                Enumerable.Range(0, owner.Arity).Select(_ => (TypeNode)new TypeNode.Star()).ToArray())).ToJsonString(),
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

    static void AddErasedAncestor(JsonNode slot, JsonArray target, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, bool allowConcrete)
    {
        if (TypeJson.Read(slot) is not TypeNode.Fqn f) return;
        TypeNode.Fqn inherited;
        if (owners.TryGetValue(f.Name, out var ancestor))
            inherited = new TypeNode.Fqn(ancestor.ErasedName);
        else if (refs.TryExistentialPhysicalOwner(f.Name, out var referenced))
            // A local Derived<T> can inherit a generic Base<T> from a referenced DotKt assembly. The semantic base
            // application contains this owner's T, but Base's trusted non-generic existential is precisely the contract
            // every closed Base<X> implements, so it is the one legal ancestor of Derived's non-generic view.
            inherited = new TypeNode.Fqn(referenced);
        else if (allowConcrete && !ContainsOwnerTv(f))
            // A non-generic (or owner-T-independent constructed) interface is implemented by
            // every closed G<T> already, so it is also a sound contract of G's existential
            // view. Keeping it is essential for inherited members such as Job.parent.
            inherited = f;
        else
            return;
        if (!target.Any(i => TypeJson.Read(i) == inherited))
            target.Add(TypeJson.Write(inherited));
    }

    static JsonObject InterfaceSlot(JsonObject method, string replacementName,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
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
            ["params"] = EraseParams(method["params"] as JsonArray, MethodDisplay(method), owners, refs),
            ["ret"] = TypeJson.Write(EraseOwnerTv(
                TypeJson.Read(method["ret"]) ?? new TypeNode.Fqn("kotlin.Unit"), owners, refs)),
            ["body"] = new JsonArray(),
            ["attrs"] = new JsonArray(),
        };
        if (method["typeParams"] is JsonArray tps && tps.Count > 0)
            slot["typeParams"] = EraseOwnerTypeParamConstraints(tps, owners, refs);
        // `suspend` is still a Kotlin declaration fact at this point.  The existential slot must
        // participate in the same later SuspendColdLowering as the generic declaration; dropping
        // mods here would cold-lower the call while leaving only an unlowered interface member.
        if (method["mods"] != null) slot["mods"] = method["mods"].DeepClone();
        CopyResultFacts(method, slot, owners, refs);
        return slot;
    }

    static JsonObject BridgeMethod(Owner owner, JsonObject method,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        string bridgeName = null, TypeNode.Fqn callOwner = null)
    {
        var originalParams = method["params"] as JsonArray ?? new JsonArray();
        var methodDisplay = $"{owner.Name}.{MethodDisplay(method)}";
        var bridgeParams = EraseParams(originalParams, methodDisplay, owners, refs);
        var args = new JsonArray();
        var sig = new JsonArray();
        for (var i = 0; i < originalParams.Count; i++)
        {
            if (originalParams[i] is not JsonObject p) continue;
            var name = Str(p["name"]) ?? "p" + i;
            var originalType = RequiredParamType(p, i, methodDisplay);
            JsonNode value = new JsonObject { ["k"] = "local", ["name"] = name };
            if (ContainsOwnerTv(originalType))
                value = new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(originalType),
                    ["e"] = value,
                    // This is not a Kotlin erased `as G<T>` check. The interface is implemented by this exact
                    // G<T> instance, so its forwarding bridge must recover the closed CLR signature.
                    ["_exactBridgeCast"] = true,
                };
            args.Add(value);
            sig.Add(TypeJson.Write(originalType));
        }

        var originalRet = TypeJson.Read(method["ret"]) ?? new TypeNode.Fqn("kotlin.Unit");
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(callOwner
                ?? new TypeNode.Fqn(owner.Name, Enumerable.Range(0, owner.Arity)
                    .Select(i => (TypeNode)new TypeNode.Tv("type", i)).ToArray())),
            ["virtual"] = Bool(method["virtual"]) || Bool(method["abstract"]),
            ["recv"] = new JsonObject { ["k"] = "this" },
            ["method"] = method["name"]?.DeepClone(),
            ["sig"] = sig,
            ["ret"] = TypeJson.Write(originalRet),
            ["args"] = args,
        };
        if (method["typeParams"] is JsonArray methodTypeParams && methodTypeParams.Count > 0)
        {
            var typeArgs = new JsonArray();
            for (var i = 0; i < methodTypeParams.Count; i++)
                typeArgs.Add(TypeJson.Write(new TypeNode.Tv("method", i)));
            call["typeArgs"] = typeArgs;
        }
        if (IsSuspend(method)) call["suspendCall"] = true;
        var body = new JsonArray();
        if (originalRet is TypeNode.Fqn { Name: "kotlin.Unit" })
            body.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = call });
        else
            body.Add(new JsonObject { ["k"] = "return", ["value"] = call });

        var bridge = new JsonObject
        {
            ["name"] = bridgeName ?? StarMethodName(owner, method),
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "public",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(EraseOwnerTv(originalRet, owners, refs)),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
        };
        if (method["typeParams"] is JsonArray tps && tps.Count > 0) bridge["typeParams"] = tps.DeepClone();
        // The forwarding bridge is a real declaration consumed by later lowering passes. Preserve
        // Kotlin modifiers rather than manufacturing a non-suspend body that calls a suspend slot.
        if (method["mods"] != null) bridge["mods"] = method["mods"].DeepClone();
        CopyResultFacts(method, bridge, owners, refs);
        return bridge;
    }

    // This pass runs after declaration-side erasure has recorded semantic return facts but before suspend lowering.
    // A synthesized existential slot/bridge is a declaration in its own right, so it must carry the same facts in
    // its owner-erased form. In particular, copying only `mods.suspend` admits the declaration to SuspendColdLowering
    // while dropping the mandatory `suspendRet`, which makes valid producer BIR internally inconsistent.
    static void CopyResultFacts(JsonObject source, JsonObject target,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        if (TypeJson.Read(source["suspendRet"]) is TypeNode suspendRet)
            target["suspendRet"] = TypeJson.Write(EraseOwnerTv(suspendRet, owners, refs));

        foreach (var key in new[]
                 {
                     "nullableGenericRet", "nullableGenericSuspendRet",
                     NullableGenericErasure.RetSuspendFnPre, "retKotlinType",
                 })
        {
            if (Str(source[key]) is not string encoded) continue;
            try
            {
                target[key] = TypeNode.ToJson(EraseOwnerTv(TypeNode.Parse(encoded), owners, refs));
            }
            catch
            {
                // The compiler-generated ABI never depends on a malformed optional round-trip fact. The structured
                // declaration slot above remains authoritative, matching RoundtripMetadata's fail-soft carrier rule.
            }
        }
    }

    static JsonArray EraseParams(JsonArray parameters, string methodDisplay,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        var result = new JsonArray();
        if (parameters == null) return result;
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is not JsonObject p) continue;
            var copy = p.DeepClone() as JsonObject;
            var pt = RequiredParamType(p, i, methodDisplay);
            copy["type"] = TypeJson.Write(EraseOwnerTv(pt, owners, refs));
            result.Add(copy);
        }
        return result;
    }

    static string MethodDisplay(JsonObject method) => Str(method["name"]) ?? "<unnamed>";

    static TypeNode RequiredParamType(JsonObject parameter, int index, string methodDisplay) =>
        TypeJson.Read(parameter["type"]) ?? throw new NotSupportedException(
            $"bir2cir: star-projection erasure: parameter "
            + $"`{Str(parameter["name"]) ?? "p" + index}` of `{methodDisplay}` carries no type — "
            + "an earlier lowering dropped it.");

    static JsonArray EraseOwnerTypeParamConstraints(JsonArray typeParams,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        var result = new JsonArray();
        foreach (var typeParamNode in typeParams)
        {
            // Unconstrained BIR method parameters use the compact string spelling ("R"); constrained declarations
            // use an object. Both spellings contribute to generic arity and must survive unchanged.
            if (typeParamNode is not JsonObject tp)
            {
                result.Add(typeParamNode?.DeepClone());
                continue;
            }
            var copy = tp.DeepClone() as JsonObject;
            if (tp["constraints"] is JsonArray constraints)
            {
                var erased = new JsonArray();
                foreach (var constraintNode in constraints)
                {
                    if (TypeJson.Read(constraintNode) is not TypeNode constraint) continue;
                    var erasedConstraint = EraseOwnerTv(constraint, owners, refs);
                    // `R : T` becomes the existential top bound. It imposes no useful CLR constraint, and emitting an
                    // explicit System.Object constraint is both redundant and less portable across metadata readers.
                    if (!IsObjectish(erasedConstraint)) erased.Add(TypeJson.Write(erasedConstraint));
                }
                copy["constraints"] = erased;
            }
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

    static string BaseStarMethodName(string declaringName, JsonObject declaringDef, JsonObject method)
    {
        var methods = declaringDef["methods"] as JsonArray;
        var ordinal = methods == null ? 0 : methods.TakeWhile(m => !ReferenceEquals(m, method)).Count();
        var ownerToken = new string(declaringName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return "$dotkt_star$base$" + ownerToken + "$" + Str(method["name"]) + "$" + ordinal;
    }

    static IEnumerable<(string DeclaringName, JsonObject Method)> InheritedConcreteBaseMethods(Owner owner,
        IReadOnlyDictionary<string, Owner> owners, IReadOnlyDictionary<string, JsonObject> defs)
    {
        var next = TypeJson.Read(owner.Def["base"]) as TypeNode.Fqn;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next != null && seen.Add(next.Name))
        {
            // A generic base contributes its own existential interface, including its
            // concrete-base bridges. Do not duplicate that surface in every subclass.
            if (owners.ContainsKey(next.Name) || !defs.TryGetValue(next.Name, out var def)) yield break;
            if (def["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (!Bool(method["static"]) && !IsPrivate(method))
                        yield return (next.Name, method);
            next = TypeJson.Read(def["base"]) as TypeNode.Fqn;
        }
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

    static bool HasOwnerDependentMethodConstraint(JsonObject method)
    {
        if (method["typeParams"] is not JsonArray mtps) return false;
        foreach (var tp in mtps.OfType<JsonObject>())
            if (tp["constraints"] is JsonArray constraints
                && constraints.Any(c => TypeJson.Read(c) is TypeNode constraint && ContainsOwnerTv(constraint)))
                return true;
        return false;
    }

    static void Rewrite(JsonNode node, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                BindInheritedStarMember(obj, owners, defs, refs);
                // Kotlin `is G<X>` is a raw-classifier check, and a true `as G<*>` also has no closed CLR target.
                // A concrete `as G<X>`, however, remains the compiler's documented eager CLR cast semantics and its
                // concrete result type is required by immediately-following member calls. Erasing that cast changes
                // a valid `as G<Unit>; g.consume(Unit)` into an impossible existential call.
                var runtimeKind = Str(obj["k"]);
                if (!Bool(obj["_exactBridgeCast"]) && runtimeKind is "isInst" or "cast"
                    && TypeJson.Read(obj["type"]) is TypeNode.Fqn { Args: { } runtimeArgs } runtimeF
                    && owners.TryGetValue(runtimeF.Name, out var runtimeOwner) && runtimeOwner.Needed
                    && (runtimeKind == "isInst" || runtimeArgs.Any(ContainsStarOrTypeVariable)))
                    obj["type"] = TypeJson.Write(new TypeNode.Fqn(runtimeOwner.ErasedName));
                obj.Remove("_exactBridgeCast");
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name") continue;
                    if (TypeJson.Read(value) is TypeNode type)
                        obj[key] = TypeJson.Write(RewriteType(type, owners, refs));
                    else
                        Rewrite(value, owners, defs, refs);
                }
                if (Str(obj["k"]) == "callInstance"
                    && TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn erasedOwner
                    && (owners.Values.Any(o => o.ErasedName == erasedOwner.Name)
                        || refs.IsExistentialPhysicalOwner(erasedOwner.Name)))
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
                        Rewrite(value, owners, defs, refs);
                }
                break;
        }
    }

    // A star smart-cast keeps the receiver's most-derived Kotlin type (`ComparableRange<*>.isEmpty`) even when the
    // member is declared by an ancestor (`ClosedRange<T>.isEmpty`).  Once the receiver becomes an erased interface,
    // CIR must name that exact declaring interface; ilemit is intentionally not allowed to search/infer it.
    static void BindInheritedStarMember(JsonObject call, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (Str(call["k"]) != "callInstance"
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn { Args: { } args } f
            || Str(call["method"]) is not string method) return;

        owners.TryGetValue(f.Name, out var start);
        var starOwner = args.Any(a => a is TypeNode.Star);
        var erasedSmartCast = call["recv"] is JsonObject recv && Str(recv["k"]) == "cast"
            && TypeJson.Read(recv["type"]) is TypeNode.Fqn { Args: { } castArgs } castF
            && castF.Name == f.Name
            && castArgs.Any(ContainsStarOrTypeVariable);
        if (!starOwner && !erasedSmartCast) return;

        var pc = (call["sig"] as JsonArray)?.Count
            ?? (call["argTypes"] as JsonArray)?.Count
            ?? (call["args"] as JsonArray)?.Count ?? 0;
        var ga = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var authoredSignature = ((call["sig"] ?? call["argTypes"]) as JsonArray)?
            .Select(TypeJson.Read).ToArray();
        if (authoredSignature?.Any(t => t == null) == true) authoredSignature = null;
        if (start != null && start.Needed
            && FindDeclaringOwner(start, method, pc, ga, authoredSignature, owners) is { } found)
        {
            var (declaring, declaration) = found;
            call["ownerType"] = TypeJson.Write(new TypeNode.Fqn(declaring.ErasedName));
            call["virtual"] = true; // erased owner is an interface; CIR must carry callvirt explicitly
            if (ContainsOwnerTvInSignature(declaration) || !IsPublic(declaration))
                call["method"] = StarMethodName(declaring, declaration);
            call["sig"] = ErasedPhysicalSignature(declaration, owners, refs);
            return;
        }

        if (start != null && start.Needed
            && FindInheritedConcreteBaseMember(start, method, pc, ga, owners, defs) is { } baseFound)
        {
            var (bridgeOwner, declaringName, declaration) = baseFound;
            call["ownerType"] = TypeJson.Write(new TypeNode.Fqn(bridgeOwner.ErasedName));
            call["method"] = BaseStarMethodName(declaringName, defs[declaringName], declaration);
            call["sig"] = ErasedPhysicalSignature(declaration, owners, refs);
            call["virtual"] = true;
            return;
        }

        // Cross-module equivalent of the local declaration walk above.  The emitted reference is the
        // authority for the physical existential slot; no reference-assembly or namespace special case
        // participates in the decision.
        if (refs.TryStarProjectionMember(f, method, ga, authoredSignature, pc,
                out var erasedOwner, out var erasedMethod, out var erasedSignature))
        {
            call["ownerType"] = TypeJson.Write(new TypeNode.Fqn(erasedOwner));
            call["method"] = erasedMethod;
            call["sig"] = new JsonArray(erasedSignature.Select(TypeJson.Write).ToArray());
            call["virtual"] = true;
            return;
        }

        // A nullary member may be declared by a non-generic interface inherited by the
        // existential view. Give the general inherited-owner pass an exact empty signature;
        // it will select the unique nearest declaration after all synthetic types exist.
        if (pc == 0 && call["sig"] == null) call["sig"] = new JsonArray();
    }

    static JsonArray ErasedPhysicalSignature(JsonObject declaration,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) =>
        new((declaration["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => TypeJson.Write(EraseOwnerTv(TypeJson.Read(p["type"]), owners, refs)))
            .ToArray() ?? Array.Empty<JsonNode>());

    static (Owner Owner, JsonObject Method)? FindDeclaringOwner(Owner start, string method, int pc, int ga,
        IReadOnlyList<TypeNode> authoredSignature, IReadOnlyDictionary<string, Owner> owners)
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
                        // Synthesize() exposes every non-static source member through a public forwarding
                        // bridge when needed, including private members used by lifted nested classes.
                        if (Str(m["name"]) == method && !Bool(m["static"])
                            && ((m["params"] as JsonArray)?.Count ?? 0) == pc
                            && ((m["typeParams"] as JsonArray)?.Count ?? 0) == ga
                            && SignatureMatches(m, authoredSignature))
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

    static bool SignatureMatches(JsonObject declaration, IReadOnlyList<TypeNode> authoredSignature)
    {
        if (authoredSignature == null) return true;
        var parameters = (declaration["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => TypeJson.Read(p["type"])).ToArray() ?? Array.Empty<TypeNode>();
        return parameters.Length == authoredSignature.Count
            && parameters.Select((p, i) => p == authoredSignature[i]).All(equal => equal);
    }

    static (Owner BridgeOwner, string DeclaringName, JsonObject Method)? FindInheritedConcreteBaseMember(
        Owner start, string method, int pc, int ga, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs)
    {
        var frontier = new List<Owner> { start };
        var seenOwners = new HashSet<string>(StringComparer.Ordinal);
        while (frontier.Count > 0)
        {
            var matches = new List<(Owner, string, JsonObject)>();
            foreach (var owner in frontier)
            {
                if (!seenOwners.Add(owner.Name)) continue;
                foreach (var (declaringName, candidate) in InheritedConcreteBaseMethods(owner, owners, defs))
                    if (Str(candidate["name"]) == method && !Bool(candidate["static"]) && !IsPrivate(candidate)
                        && ((candidate["params"] as JsonArray)?.Count ?? 0) == pc
                        && ((candidate["typeParams"] as JsonArray)?.Count ?? 0) == ga)
                        matches.Add((owner, declaringName, candidate));
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return null;

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
            case TypeNode.Fqn { Args: { } nestedArgs } nestedForeign
                when nestedArgs.Any(ContainsStar)
                    && ForeignStarProjectionBinding.IsForeignStarType(nestedForeign, refs):
                // A star anywhere below a foreign invariant construction makes the whole construction
                // non-reifiable on the CLR.  In particular Outer<Inner<*>> is not Outer<object> (nor
                // Outer<Inner<object>>); keep the original runtime value in one opaque object slot and let
                // ForeignStarProjectionBinding route member access through the reflection ABI.
                if (refs.IsByRefLikeFqn(nestedForeign.Name))
                    throw new NotSupportedException(
                        $"bir2cir: foreign byref-like generic star projection `{nestedForeign.Name}<*>` has no boxable CLR existential representation");
                return new TypeNode.Fqn("kotlin.Any");
            case TypeNode.Fqn { Args: { } args } f when args.Any(a => a is TypeNode.Star):
            {
                var erased = owners.TryGetValue(f.Name, out var local) ? local.ErasedName : null;
                if (local != null && local.Needed) return new TypeNode.Fqn(erased);
                if (refs.TryExistentialPhysicalOwner(f.Name, out var referenced))
                    return new TypeNode.Fqn(referenced);
                // A foreign CLR generic cannot be retrofitted to implement DotKt's nominal existential. Its value
                // slot is object; classifier checks/casts/member dispatch were already authored explicitly by
                // ForeignStarProjectionBinding above. Never manufacture the invariant fiction G<object>.
                if (!refs.HasDotKtOwner(f.Name) && refs.ResolveNetType(f.Name, args.Length) != null)
                {
                    if (refs.IsByRefLikeFqn(f.Name))
                        throw new NotSupportedException(
                            $"bir2cir: foreign byref-like generic star projection `{f.Name}<*>` has no boxable CLR existential representation");
                    return new TypeNode.Fqn("kotlin.Any");
                }
                return new TypeNode.Fqn(f.Name, args.Select(a => RewriteType(a, owners, refs)).ToArray());
            }
            case TypeNode.Star:
                // A residual star belongs to an unsupported shape (multi-parameter mask / external CLR generic).
                // Keep CIR well-formed with the explicit erasure; supported local/reference one-parameter owners
                // have already become their existential view above.
                return new TypeNode.Fqn("kotlin.Any");
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

    static bool ContainsStarOrTypeVariable(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsStarOrTypeVariable),
        TypeNode.Nullable n => ContainsStarOrTypeVariable(n.Of),
        TypeNode.Oblivious o => ContainsStarOrTypeVariable(o.Of),
        TypeNode.Array a => ContainsStarOrTypeVariable(a.Elem),
        TypeNode.ByRef b => ContainsStarOrTypeVariable(b.Of),
        TypeNode.Fn fn => ContainsStarOrTypeVariable(fn.Ret) || fn.Params.Any(ContainsStarOrTypeVariable)
            || (fn.Recv != null && ContainsStarOrTypeVariable(fn.Recv)),
        _ => false,
    };

    static bool ContainsStar(TypeNode t) => t switch
    {
        TypeNode.Star => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsStar),
        TypeNode.Nullable n => ContainsStar(n.Of),
        TypeNode.Oblivious o => ContainsStar(o.Of),
        TypeNode.Array a => ContainsStar(a.Elem),
        TypeNode.ByRef b => ContainsStar(b.Of),
        TypeNode.Fn fn => ContainsStar(fn.Ret) || fn.Params.Any(ContainsStar)
            || (fn.Recv != null && ContainsStar(fn.Recv)),
        _ => false,
    };

    static bool ContainsExplicitStar(TypeNode t) => t switch
    {
        TypeNode.Star => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExplicitStar),
        TypeNode.Nullable n => ContainsExplicitStar(n.Of),
        TypeNode.Oblivious o => ContainsExplicitStar(o.Of),
        TypeNode.Array a => ContainsExplicitStar(a.Elem),
        TypeNode.ByRef b => ContainsExplicitStar(b.Of),
        TypeNode.Fn fn => ContainsExplicitStar(fn.Ret) || fn.Params.Any(ContainsExplicitStar)
            || (fn.Recv != null && ContainsExplicitStar(fn.Recv)),
        _ => false,
    };

    static TypeNode EraseOwnerTv(TypeNode t, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs) => t switch
    {
        TypeNode.Tv { Scope: "type" } => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fqn { Args: { } args } f when args.Any(ContainsOwnerTv)
            && owners.TryGetValue(f.Name, out var nested) && nested.Needed
            => new TypeNode.Fqn(nested.ErasedName),
        TypeNode.Fqn { Args: { } args } f when args.Any(ContainsOwnerTv)
            && refs.TryExistentialPhysicalOwner(f.Name, out var referenced)
            => new TypeNode.Fqn(referenced),
        // A foreign/reified construction containing an owner slot is not covariantly substitutable in general:
        // List<T> is not List<object>, T[] is not object[] for value T, and Func<T,R> has variance of its own.
        // The existential slot therefore carries the whole value as object; the forwarding bridge casts that object
        // back to the exact closed declaration type before calling the real member. DotKt-authored generic results
        // were handled by the two nominal existential cases above and retain their identity-preserving view.
        TypeNode.Fqn { Args: { } args } when args.Any(ContainsOwnerTv)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Array a when ContainsOwnerTv(a.Elem)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.ByRef b when ContainsOwnerTv(b.Of)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fn fn when ContainsOwnerTv(fn)
            => new TypeNode.Fqn("kotlin.Any"),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(
            f.Name, args.Select(a => EraseOwnerTv(a, owners, refs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(EraseOwnerTv(n.Of, owners, refs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(EraseOwnerTv(o.Of, owners, refs)),
        TypeNode.Array a => new TypeNode.Array(EraseOwnerTv(a.Elem, owners, refs)),
        TypeNode.ByRef b => new TypeNode.ByRef(EraseOwnerTv(b.Of, owners, refs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, EraseOwnerTv(fn.Ret, owners, refs),
            fn.Params.Select(p => EraseOwnerTv(p, owners, refs)).ToArray(),
            fn.Recv == null ? null : EraseOwnerTv(fn.Recv, owners, refs)),
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
    static bool IsSuspend(JsonObject method) => method["mods"] is JsonObject mods && Bool(mods["suspend"]);
    // Older/common BIR omits `vis` for Kotlin's default public visibility; explicit non-public declarations carry a
    // value (`internal`, `private`, ...).  Treat omission exactly as the emitter does, rather than dropping public slots.
    static bool IsPublic(JsonObject method) => Str(method["vis"]) is null or "public";
    static bool IsPrivate(JsonObject method) => Str(method["vis"]) == "private";
}

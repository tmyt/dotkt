using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    const string CarrierMark = "$star";
    internal const int InnerFactoryBottomTypeArgument = -1;
    internal const string ErasedInnerConstraintKey = "erasedInnerConstraints";
    const string NormalizedInnerFactoryReturnKey = "_normalizedInnerFactoryReturn";
    const string ProjectedArrayReadKey = "_projectedArrayRead";
    const string ExistentialArrayElementProjectionKey = "_existentialArrayElementProjection";
    internal const string SourceMemberKey = "existentialSourceMember";
    internal const string InnerConstructorFactoryKey = "existentialInnerConstructorFactory";
    const string ExistentialResultProjectionKey = "_existentialResultProjection";
    static Dictionary<string, List<JsonObject>> _localMethods = new(StringComparer.Ordinal);

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
        var localClrAliases = CollectLocalClrAliases(defs);
        _localMethods = IndexLocalMethods(rootList);
        AllocateCarrierNames(owners, defs.Keys);
        foreach (var root in rootList) MarkNeeded(root, owners, defs, refs);
        MarkNeededClosure(owners);

        foreach (var owner in owners.Values.Where(o => o.Needed)) Synthesize(owner, owners, defs, refs);
        foreach (var root in rootList) RecordDeclarationSurfaces(root);
        foreach (var root in rootList) EraseProjectedAliasConstraints(root, refs);
        ForeignStarProjectionBinding.ApplyAll(rootList,
            owners.Values.Where(owner => owner.Needed).ToDictionary(
                owner => owner.Name, owner => owner.ErasedName, StringComparer.Ordinal), refs, localClrAliases);
        foreach (var root in rootList) Rewrite(root, owners, defs, refs, localClrAliases: localClrAliases);
        var normalizedReturns = new NormalizedReturnBindings();
        // Method-local normalization runs after the first post-order binding walk. Revisit consumers once so a
        // projected array read that flowed through a compiler-generated nullable temporary binds its member on the
        // existential carrier rather than retaining the frontend's exact constructed owner.
        foreach (var root in rootList)
            RewriteNormalizedInnerFactoryCalls(root, normalizedReturns, owners, defs, refs);
        while (CollectNormalizedInnerFactoryReturns(rootList, owners, refs, normalizedReturns))
            foreach (var root in rootList)
                RewriteNormalizedInnerFactoryCalls(root, normalizedReturns, owners, defs, refs);
        // Exact bridge casts carry a pass-local guard only while this pass can still mistake them for Kotlin's erased
        // runtime classifier operation. A post-order member binding can create one after its object was visited, so a
        // final cleanup is the single guarantee that this implementation marker never becomes CIR.
        foreach (var root in rootList) RemoveExactBridgeCastMarkers(root);
        // Binding above consumes the complete Kotlin constraint graph. Only after every local call and generated
        // seam has been selected may the physical inner TypeDefs drop constraints that cannot name a star outer.
        var weakenedOwnerSlots = WeakenOwnerDependentInnerConstraints(
            owners.Values.Where(owner => owner.Needed));
        SynchronizeGeneratedOwnerConstraintPrefixes(defs, weakenedOwnerSlots);
        return owners.Values.Where(o => o.Needed)
            .ToDictionary(o => o.Name, o => o.ErasedName, StringComparer.Ordinal);
    }

    sealed class NormalizedReturnBindings
    {
        public readonly Dictionary<string, TypeNode> ByDeclaration = new(StringComparer.Ordinal);
        public readonly Dictionary<string, TypeNode> ByPhysicalMethod = new(StringComparer.Ordinal);
    }

    static string PhysicalMethodKey(string owner, string method) => owner + "\0" + method;

    static bool CollectNormalizedInnerFactoryReturns(
        IEnumerable<JsonObject> roots, IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        NormalizedReturnBindings result)
    {
        var found = false;
        void CollectMethods(JsonArray methods, string ownerName)
        {
            foreach (var method in methods.OfType<JsonObject>())
            {
                if (!Bool(method[NormalizedInnerFactoryReturnKey])) continue;
                method.Remove(NormalizedInnerFactoryReturnKey);
                if (method["body"] is not JsonArray || method["params"] is not JsonArray
                    || Str(method["name"]) is not string methodName
                    || TypeJson.Read(method["ret"]) is not TypeNode ret
                    || !ContainsPhysicalExistential(ret, owners, refs)) continue;
                if (Str(method[DeclarationIdentityBinding.Key]) is string id)
                {
                    if (result.ByDeclaration.TryGetValue(id, out var prior) && prior != ret)
                        throw new InvalidOperationException(
                            $"normalized declaration '{id}' has conflicting physical return carriers");
                    result.ByDeclaration[id] = ret;
                }
                else
                {
                    var key = PhysicalMethodKey(ownerName, methodName);
                    if (result.ByPhysicalMethod.TryGetValue(key, out var prior) && prior != ret)
                        throw new InvalidOperationException(
                            $"normalized method '{ownerName}.{methodName}' has conflicting physical return carriers");
                    result.ByPhysicalMethod[key] = ret;
                }
                found = true;
            }
        }
        void CollectOwner(JsonObject owner, string ownerName)
        {
            if (owner["methods"] is JsonArray methods) CollectMethods(methods, ownerName);
            if (owner["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                    if (Str(type["name"]) is string typeName) CollectOwner(type, typeName);
        }
        foreach (var root in roots)
        {
            if (Str(root["fileClass"]) is string fileClass)
                if (root["methods"] is JsonArray methods) CollectMethods(methods, fileClass);
            if (root["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>())
                    if (Str(type["name"]) is string typeName) CollectOwner(type, typeName);
        }
        return found;
    }

    static void RewriteNormalizedInnerFactoryCalls(JsonNode node,
        NormalizedReturnBindings normalizedReturns,
        IReadOnlyDictionary<string, Owner> owners, IReadOnlyDictionary<string, JsonObject> defs,
        ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                TypeNode result = null;
                if (Str(obj["k"]) is "callStatic" or "callInstance")
                {
                    if (Str(obj[DeclarationIdentityBinding.Key]) is string id)
                        normalizedReturns.ByDeclaration.TryGetValue(id, out result);
                    if (result == null && TypeJson.Read(obj["calleeOwner"]) is TypeNode.Fqn calleeOwner
                        && Str(obj["method"]) is string method)
                        normalizedReturns.ByPhysicalMethod.TryGetValue(
                            PhysicalMethodKey(calleeOwner.Name, method), out result);
                }
                if (result != null)
                {
                    var semanticResult = ExpressionType(obj);
                    obj["sty"] = TypeJson.Write(result);
                    if (obj["ret"] != null) obj["ret"] = TypeJson.Write(result);
                    if (obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(result);
                    if (result is TypeNode.Array { Elem: var physicalElement }
                        && IsExistentialPhysicalCarrier(physicalElement, owners, refs)
                        && semanticResult is TypeNode.Array semanticArray)
                        obj[ExistentialArrayElementProjectionKey] = TypeJson.Write(
                            semanticArray.Elem is TypeNode.Projection projection
                                ? projection.Of : semanticArray.Elem);
                    else if (IsExistentialPhysicalCarrier(result, owners, refs)
                             && semanticResult != null)
                        obj[ExistentialResultProjectionKey] = TypeJson.Write(semanticResult);
                }
                foreach (var value in obj.Select(pair => pair.Value).ToList())
                    if (value != null) RewriteNormalizedInnerFactoryCalls(
                        value, normalizedReturns, owners, defs, refs);
                BindProjectedArrayRead(obj, owners, refs);
                BindProjectedArrayGenericCall(obj, owners, refs);
                BindInheritedStarMember(obj, owners, defs, refs);
                if (obj["body"] is JsonArray && obj["params"] is JsonArray)
                    NormalizeInnerFactoryLocals(obj, owners, defs, refs);
                break;
            case JsonArray array:
                foreach (var value in array)
                    if (value != null) RewriteNormalizedInnerFactoryCalls(
                        value, normalizedReturns, owners, defs, refs);
                break;
        }
    }

    // Some structural passes must run after the main existential rewrite (notably the final override-slot bridge,
    // which waits for suspend lowering). Those passes can copy a frontend-resolved `G<*>` slot into a fresh physical
    // declaration. Re-apply only the type projection to those late declarations; do not repeat member binding or
    // synthesize carriers. The carrier allocation returned by ApplyAll is the sole local authority, while referenced
    // carriers remain metadata-driven through ReferenceMetadataIndex.
    public static void RewriteLateTypes(IEnumerable<JsonNode> roots,
        IReadOnlyDictionary<string, string> localExistentialOwners, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> knownClrAliases = null)
    {
        var rootList = roots.ToList();
        var owners = localExistentialOwners.ToDictionary(
            pair => pair.Key,
            pair => new Owner { Name = pair.Key, ErasedName = pair.Value, Needed = true },
            StringComparer.Ordinal);
        var defs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in rootList.OfType<JsonObject>())
            CollectDefinitions(root, defs);
        var localClrAliases = new Dictionary<string, string>(
            knownClrAliases ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var alias in CollectLocalClrAliases(defs)) localClrAliases.TryAdd(alias.Key, alias.Value);
        foreach (var root in rootList) RewriteTypesOnly(root, owners, refs, localClrAliases);
    }

    static void CollectDefinitions(JsonObject container, Dictionary<string, JsonObject> defs)
    {
        if (container["types"] is not JsonArray types) return;
        foreach (var definition in types.OfType<JsonObject>())
        {
            if (Str(definition["name"]) is string name) defs.TryAdd(name, definition);
            CollectDefinitions(definition, defs);
        }
    }

    static IReadOnlyDictionary<string, string> CollectLocalClrAliases(
        IReadOnlyDictionary<string, JsonObject> defs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, definition) in defs)
        {
            if (definition["attrs"] is not JsonArray attributes) continue;
            foreach (var attribute in attributes.OfType<JsonObject>())
            {
                if (TypeJson.OwnerName(attribute["attr"]) != "kotlin.clr.ClrTypeAlias") continue;
                var target = attribute["args"] is JsonArray { Count: 1 } arguments
                    && arguments[0] is JsonObject constant
                    && Str(constant["k"]) == "const"
                    ? Str(constant["value"])
                    : null;
                if (string.IsNullOrEmpty(target))
                    throw new InvalidOperationException($"bir2cir: malformed @ClrTypeAlias on '{name}'");
                result[name] = target;
            }
        }
        return result;
    }

    static void RewriteTypesOnly(JsonNode node, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, IReadOnlyDictionary<string, string> localClrAliases,
        bool boundDeclaration = false)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name" || key == InnerConstructorFactoryKey
                        || key == ExistentialResultProjectionKey
                        || key == ExistentialArrayElementProjectionKey) continue;
                    var childBoundDeclaration = boundDeclaration
                        || IsBoundDeclarationType(obj, key, refs, localClrAliases);
                    if (TypeJson.Read(value) is TypeNode type)
                        obj[key] = TypeJson.Write(RewriteType(
                            type, owners, refs, childBoundDeclaration, localClrAliases));
                    else
                        RewriteTypesOnly(value, owners, refs, localClrAliases, childBoundDeclaration);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    if (value == null) continue;
                    if (TypeJson.Read(value) is TypeNode type)
                        arr[i] = TypeJson.Write(RewriteType(
                            type, owners, refs, boundDeclaration, localClrAliases));
                    else
                        RewriteTypesOnly(value, owners, refs, localClrAliases, boundDeclaration);
                }
                break;
        }
    }

    // A declaration selected in another owner is an exact linkage fact, not a Kotlin value slot this pass owns.
    // In particular an @ClrTypeAlias constructor's memberSignature may contain a source projection
    // (LinkedHashMap(Map<out K, V>)); its physical descriptor is IDictionary<K, V>, never object.  Keep the
    // projection's bound while still lowering its aliases.  A Kotlin-local constructor is deliberately excluded:
    // its declaration is rewritten by this pass too, so its memberSignature must follow the same existential ABI.
    static bool IsBoundDeclarationType(JsonObject owner, string key, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
    {
        var kind = Str(owner["k"]);
        if (key == "resolvedMemberParams" || key == ClrMemberResolution.ResolvedMemberReturnKey)
            return true;
        if (key == "argTypes" && ClrBoundNode.IsAny(kind)) return true;
        return key == "memberSignature" && kind == "new"
            && TypeJson.Read(owner["type"]) is TypeNode.Fqn constructed
            && (refs.TryResolveClrOwner(constructed.Name, out _, out _)
                || localClrAliases?.ContainsKey(constructed.Name) == true);
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
        // A newly-bound carrier result can initialize the next local in a chain. Discover and normalize to a fixed
        // point so each lexical hop retains the same projected star mask before its consumer is rebound.
        while (true)
        {
            var before = owners.Values.Count(o => o.Needed);
            MarkNeededAncestors(owners);
            foreach (var owner in owners.Values.Where(o => o.Needed).ToList())
            {
                // A star-typed outer receiver cannot be converted to one arbitrary invariant G<object> merely so an
                // inner constructor can consume its hidden outer argument.  The existential outer instead exposes a
                // factory whose result is the inner classifier's own existential view.  Keep that result carrier in
                // the same reachability closure as ordinary owner-dependent member results.
                foreach (var inner in owners.Values.Where(candidate =>
                             Str(candidate.Def["semanticOwner"]) == owner.Name && IsInner(candidate.Def)))
                    inner.Needed = true;
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
    // exact Kotlin type (`Pair<*, String>` or `Array<out G<T>>`) before the slot becomes that interface/object.
    // RoundtripMetadata emits these facts as [KotlinType], and dll2klib restores them without inspecting the physical
    // carrier name. Calls and locals are intentionally excluded: this is exported declaration ABI only.
    static void RecordDeclarationSurfaces(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["kind"]) != null)
                    RecordProjectedTypeParameterBounds(obj);
                if (obj["k"] == null && obj["name"] is JsonValue && obj["params"] is JsonArray parameters)
                {
                    RecordProjectedMethodTypeParameterBounds(obj);
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

    // A use-site projection in a bound is Kotlin declaration truth but is not a legal CLR
    // GenericParamConstraint when the physical generic is invariant. Preserve the complete sibling list in the
    // existing round-trip carriers before RewriteType chooses the CLR approximation below. The payload is opaque to
    // every intervening physical pass, so dll2klib restores exactly the authored `in`/`out` nodes rather than
    // reconstructing them from an erased constraint row.
    static void RecordProjectedTypeParameterBounds(JsonObject declaration)
    {
        var bounds = ProjectedTypeParameterBounds(declaration["typeParams"] as JsonArray);
        if (bounds.Count > 0)
            KotlinSupertypesRecord.Merge(declaration, new JsonObject { ["bounds"] = bounds });
    }

    static void RecordProjectedMethodTypeParameterBounds(JsonObject declaration)
    {
        var bounds = ProjectedTypeParameterBounds(declaration["typeParams"] as JsonArray);
        if (bounds.Count == 0) return;
        var payload = Str(declaration[NullableGenericErasure.MethodTypeParameterBoundsPre]) is string encoded
            ? JsonNode.Parse(encoded) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var existing = payload["bounds"] as JsonObject;
        if (existing == null)
        {
            existing = new JsonObject();
            payload["bounds"] = existing;
        }
        foreach (var bound in bounds)
            if (!existing.ContainsKey(bound.Key)) existing[bound.Key] = bound.Value?.DeepClone();
        declaration[NullableGenericErasure.MethodTypeParameterBoundsPre] = payload.ToJsonString();
    }

    static JsonObject ProjectedTypeParameterBounds(JsonArray parameters)
    {
        var bounds = new JsonObject();
        if (parameters == null) return bounds;
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index] is not JsonObject parameter
                || parameter["constraints"] is not JsonArray constraints
                || !constraints.Any(constraint => TypeJson.Read(constraint) is TypeNode type
                    && ContainsUseSiteProjection(type))) continue;
            bounds[index.ToString()] = constraints.DeepClone();
        }
        return bounds;
    }

    // A CLR GenericParamConstraint cannot encode a use-site projection on an invariant physical alias. The exact
    // Kotlin bound was recorded above, so weaken only that constraint row to Object here. Direct declaration/value
    // slots are handled later by the ordinary existential rewrite, while a selected constructor/member descriptor
    // remains on its exact reifiable face (for example OrderedDictionary(IDictionary<K,V>)).
    static void EraseProjectedAliasConstraints(JsonNode node, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["typeParams"] is JsonArray parameters)
                    foreach (var parameter in parameters.OfType<JsonObject>())
                        if (parameter["constraints"] is JsonArray constraints)
                            for (var index = 0; index < constraints.Count; index++)
                                if (StripSourceNullability(TypeJson.Read(constraints[index])) is TypeNode.Fqn bound
                                    && ContainsUseSiteProjection(bound)
                                    && refs.TryResolveClrOwner(bound.Name, out _, out _))
                                    constraints[index] = TypeJson.Write(new TypeNode.Fqn("kotlin.Any"));
                foreach (var child in obj.Select(kv => kv.Value).Where(value => value != null).ToList())
                    EraseProjectedAliasConstraints(child, refs);
                break;
            case JsonArray array:
                foreach (var child in array.Where(value => value != null).ToList())
                    EraseProjectedAliasConstraints(child, refs);
                break;
        }
    }

    static bool ContainsUseSiteProjection(TypeNode type) => type switch
    {
        TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsUseSiteProjection),
        TypeNode.Nullable nullable => ContainsUseSiteProjection(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsUseSiteProjection(oblivious.Of),
        TypeNode.Array array => ContainsUseSiteProjection(array.Elem),
        TypeNode.ByRef byRef => ContainsUseSiteProjection(byRef.Of),
        TypeNode.Ptr pointer => ContainsUseSiteProjection(pointer.Of),
        TypeNode.Mod modifier => ContainsUseSiteProjection(modifier.M)
            || ContainsUseSiteProjection(modifier.Of),
        TypeNode.Fn function => ContainsUseSiteProjection(function.Ret)
            || function.Params.Any(ContainsUseSiteProjection)
            || function.Recv != null && ContainsUseSiteProjection(function.Recv)
            || function.Ctx?.Any(ContainsUseSiteProjection) == true,
        _ => false,
    };

    static void RecordProjectionSlot(JsonObject declaration, string slot, string fact)
    {
        if (declaration[fact] != null || TypeJson.Read(declaration[slot]) is not TypeNode type
            || !ContainsExistentialProjection(type))
            return;
        declaration[fact] = TypeNode.ToJson(type);
    }

    static void MarkNeeded(JsonNode node, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                if (TryVarianceProjectedArrayOwner(obj, defs, refs, out var arrayOwner)
                    && owners.TryGetValue(arrayOwner.Name, out var projectedOwner))
                    projectedOwner.Needed = true;
                if (TryVarianceProjectedArraySpreadOwner(obj, defs, refs, out var spreadOwner)
                    && owners.TryGetValue(spreadOwner.Name, out var projectedSpreadOwner))
                    projectedSpreadOwner.Needed = true;
                if (TryVarianceProjectedArrayInitializerOwner(obj, defs, refs, out var initializerOwner)
                    && owners.TryGetValue(initializerOwner.Name, out var projectedInitializerOwner))
                    projectedInitializerOwner.Needed = true;
                var runtimeClassifier = Str(obj["k"]) is "isInst" or "cast";
                foreach (var kv in obj)
                {
                    if (kv.Value == null || kv.Key == "name") continue;
                    if (TypeJson.Read(kv.Value) is TypeNode type)
                    {
                        MarkNeededType(type, owners, runtimeClassifier && kv.Key == "type");
                    }
                    else MarkNeeded(kv.Value, owners, defs, refs);
                }
                break;
            case JsonArray arr:
                foreach (var value in arr)
                    if (value != null)
                    {
                        if (TypeJson.Read(value) is TypeNode type) MarkNeededType(type, owners, false);
                        else MarkNeeded(value, owners, defs, refs);
                    }
                break;
        }
    }

    // Kotlin can infer `Array<G<Join>>` from values `G<A>`, `G<B>`, ... when G's source variance makes each
    // conversion legal. CLR arrays are covariant only in their immediate reference element; that does not turn the
    // invariant physical constructions G<A>/G<B> into G<Join>. The already type-checked BIR is the authority for the
    // semantic join. Preserve it physically by allocating the nominal existential view implemented by every G<X>.
    //
    // This is deliberately structural: neither arrayOf nor a library/function name is recognized. Every non-null
    // element must project through the declaration graph to the array element's generic owner, and at least one such
    // construction must differ from the inferred join. An ordinary homogeneous generic array remains reified.
    static bool TryVarianceProjectedArrayOwner(JsonObject obj,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        out TypeNode.Fqn targetOwner)
    {
        targetOwner = null;
        if (Str(obj["k"]) != "newArray"
            || StripSourceNullability(TypeJson.Read(obj["elem"]))
                is not TypeNode.Fqn { Args: { Length: > 0 } } target
            || obj["elems"] is not JsonArray elements || elements.Count == 0)
            return false;

        var targetKey = string.Join("\0", target.Args.Select(SupertypeGraph.TypeKey));
        var sawTypedElement = false;
        var sawClrIncompatibleConstruction = false;
        foreach (var element in elements)
        {
            if (element is JsonObject { } expression && Str(expression["k"]) == "const"
                && expression["value"] == null)
                continue;
            if (StripSourceNullability(ExpressionType(element))
                is not TypeNode.Fqn source)
                return false;
            var projected = SameDeclarationOwner(source.Name, target.Name)
                ? source.Args ?? Array.Empty<TypeNode>()
                : ProjectConstructedArguments(source, target.Name, defs, refs)?.ToArray();
            if (projected == null || projected.Length != target.Args.Length)
                return false;
            sawTypedElement = true;
            var projectedKey = string.Join("\0", projected.Select(SupertypeGraph.TypeKey));
            if (projectedKey != targetKey
                && !CanUseClrVariance(target, projected, defs, refs))
                sawClrIncompatibleConstruction = true;
        }
        if (!sawTypedElement || !sawClrIncompatibleConstruction) return false;
        targetOwner = target;
        return true;
    }

    static bool RequiresExistentialConversion(TypeNode sourceType, TypeNode.Fqn target,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (StripSourceNullability(sourceType) is not TypeNode.Fqn source) return false;
        var projected = SameDeclarationOwner(source.Name, target.Name)
            ? source.Args ?? Array.Empty<TypeNode>()
            : ProjectConstructedArguments(source, target.Name, defs, refs)?.ToArray();
        if (projected == null || projected.Length != target.Args.Length) return false;
        return string.Join("\0", projected.Select(SupertypeGraph.TypeKey))
                   != string.Join("\0", target.Args.Select(SupertypeGraph.TypeKey))
               && !CanUseClrVariance(target, projected, defs, refs);
    }

    static bool TryVarianceProjectedArraySpreadOwner(JsonObject obj,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        out TypeNode.Fqn targetOwner)
    {
        targetOwner = null;
        if (Str(obj["k"]) != "spreadConcat"
            || StripSourceNullability(TypeJson.Read(obj["elem"]))
                is not TypeNode.Fqn { Args: { Length: > 0 } } target
            || obj["parts"] is not JsonArray parts)
            return false;

        bool ContainsIncompatibleElement(JsonNode node)
        {
            if (node is JsonObject expression)
            {
                if (ExpressionType(expression) is TypeNode.Array sourceArray
                    && RequiresExistentialConversion(sourceArray.Elem, target, defs, refs))
                    return true;
                if (Str(expression["k"]) == "newArray" && expression["elems"] is JsonArray elements
                    && elements.Any(element => RequiresExistentialConversion(
                        ExpressionType(element), target, defs, refs)))
                    return true;
                return expression.Select(pair => pair.Value)
                    .Any(value => value != null && ContainsIncompatibleElement(value));
            }
            return node is JsonArray array
                && array.Any(value => value != null && ContainsIncompatibleElement(value));
        }

        if (!ContainsIncompatibleElement(parts)) return false;
        targetOwner = target;
        return true;
    }

    static Dictionary<string, List<JsonObject>> IndexLocalMethods(IEnumerable<JsonObject> roots)
    {
        var result = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void AddMethods(JsonObject owner, string ownerName)
        {
            if (owner["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    if (Str(method["name"]) is string name)
                    {
                        var key = ownerName + "\0" + name;
                        if (!result.TryGetValue(key, out var candidates))
                            result[key] = candidates = new List<JsonObject>();
                        candidates.Add(method);
                    }
            if (owner["types"] is JsonArray nested)
                foreach (var type in nested.OfType<JsonObject>())
                    if (Str(type["name"]) is string typeName) AddMethods(type, typeName);
        }
        foreach (var root in roots)
            if (Str(root["fileClass"]) is string fileClass) AddMethods(root, fileClass);
        return result;
    }

    static JsonObject LocalDelegateTarget(JsonObject arrayInitializer)
    {
        if (arrayInitializer["init"] is not JsonObject { } initializer
            || Str(initializer["method"]) is not string method
            || (Str(initializer["k"]) switch
                {
                    "newDelegate" => TypeJson.Read(initializer["calleeOwner"]),
                    "newClosure" => TypeJson.Read(initializer["closureType"]),
                    _ => null,
                }) is not TypeNode.Fqn owner
            || !_localMethods.TryGetValue(owner.Name + "\0" + method, out var candidates)
            || candidates.Count != 1)
            return null;
        return candidates[0];
    }

    static bool TryVarianceProjectedArrayInitializerOwner(JsonObject obj,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        out TypeNode.Fqn targetOwner)
    {
        targetOwner = null;
        if (Str(obj["k"]) != "newArrayInit"
            || StripSourceNullability(TypeJson.Read(obj["elem"]))
                is not TypeNode.Fqn { Args: { Length: > 0 } } target
            || LocalDelegateTarget(obj) is not JsonObject targetMethod
            || targetMethod["body"] is not JsonArray body)
            return false;

        var sawIncompatible = false;
        void Inspect(JsonNode expression)
        {
            if (expression is not JsonObject value || sawIncompatible) return;
            switch (Str(value["k"]))
            {
                case "valueBlock": Inspect(value["result"]); return;
                case "cond": Inspect(value["then"]); Inspect(value["else"]); return;
                case "cast": Inspect(value["e"]); return;
                default:
                    sawIncompatible = RequiresExistentialConversion(
                        ExpressionType(value), target, defs, refs);
                    return;
            }
        }
        void InspectReturns(JsonNode node)
        {
            if (node is JsonObject expression)
            {
                if (Str(expression["k"]) == "return") Inspect(expression["value"]);
                else foreach (var value in expression.Select(pair => pair.Value))
                    if (value != null) InspectReturns(value);
            }
            else if (node is JsonArray array)
                foreach (var value in array) if (value != null) InspectReturns(value);
        }
        InspectReturns(body);
        if (!sawIncompatible) return false;
        targetOwner = target;
        return true;
    }

    static void RewriteVarianceProjectedArrayInitializer(
        JsonObject array, TypeNode.Fqn semanticElement, TypeNode.Fqn physicalElement,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        array["elem"] = TypeJson.Write(physicalElement);
        array[ExistentialArrayElementProjectionKey] = TypeJson.Write(semanticElement);
        if (array["init"] is not JsonObject initializer
            || TypeJson.Read(initializer["funcType"]) is not TypeNode.Fn function)
            return;
        var physicalFunction = new TypeNode.Fn(function.Suspend, physicalElement, function.Params,
            function.Recv, function.Clr, function.Ctx);
        initializer["funcType"] = TypeJson.Write(physicalFunction);
        array["funcType"] = TypeJson.Write(physicalFunction);
        if (LocalDelegateTarget(array) is not JsonObject targetMethod) return;
        if (TypeJson.Read(targetMethod["ret"]) is TypeNode originalReturn)
            targetMethod["retKotlinType"] ??= TypeNode.ToJson(originalReturn);
        targetMethod["ret"] = TypeJson.Write(physicalElement);
        targetMethod[NormalizedInnerFactoryReturnKey] = true;
        if (targetMethod["body"] is JsonArray body)
            foreach (var statement in body.OfType<JsonObject>())
                if (Str(statement["k"]) == "return")
                {
                    var original = statement["value"];
                    var rewritten = RetypeCovariantResult(original, semanticElement, physicalElement, defs, refs);
                    if (!ReferenceEquals(original, rewritten)) statement["value"] = rewritten;
                }
    }

    static JsonNode RetypeCovariantResult(JsonNode expression, TypeNode.Fqn semanticTarget,
        TypeNode.Fqn physicalTarget, IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (expression is not JsonObject value) return expression;
        switch (Str(value["k"]))
        {
            case "valueBlock":
                value["type"] = TypeJson.Write(physicalTarget);
                ReplaceRetypedChild(value, "result", semanticTarget, physicalTarget, defs, refs);
                return value;
            case "cond":
                value["type"] = TypeJson.Write(physicalTarget);
                ReplaceRetypedChild(value, "then", semanticTarget, physicalTarget, defs, refs);
                ReplaceRetypedChild(value, "else", semanticTarget, physicalTarget, defs, refs);
                return value;
            case "cast":
                if (StripSourceNullability(ExpressionType(value["e"])) is TypeNode.Fqn source
                    && ProjectConstructedArguments(source, semanticTarget.Name, defs, refs) is { } projected
                    && !CanUseClrVariance(semanticTarget, projected, defs, refs))
                    value["type"] = TypeJson.Write(physicalTarget);
                return value;
        }

        // The CLR verifier merges unrelated concrete branch values as object even when both reach the same
        // compiler-generated existential interface through G<X>. State that already-existing interface conversion
        // explicitly so a conditional initializer has the carrier type expected by its delegate signature.
        if (StripSourceNullability(ExpressionType(value)) is TypeNode.Fqn sourceType
            && sourceType.Name != physicalTarget.Name
            && ProjectConstructedArguments(sourceType, semanticTarget.Name, defs, refs) != null)
            return new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(physicalTarget),
                ["e"] = value.DeepClone(),
                ["_exactBridgeCast"] = true,
            };
        return value;
    }

    static void ReplaceRetypedChild(JsonObject parent, string key, TypeNode.Fqn semanticTarget,
        TypeNode.Fqn physicalTarget, IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        var original = parent[key];
        var rewritten = RetypeCovariantResult(original, semanticTarget, physicalTarget, defs, refs);
        if (!ReferenceEquals(original, rewritten)) parent[key] = rewritten;
    }

    static void RewriteVarianceProjectedArraySpread(JsonObject spread, TypeNode.Fqn semanticElement,
        TypeNode physicalElement)
    {
        void ReplacePhysicalTypes(JsonNode node)
        {
            if (node is JsonObject expression)
            {
                foreach (var key in expression.Select(pair => pair.Key).ToList())
                {
                    var value = expression[key];
                    if (value == null || key == ExistentialArrayElementProjectionKey) continue;
                    if (TypeJson.Read(value) is TypeNode type)
                    {
                        var replacement = ReplaceEquivalentType(type, semanticElement, physicalElement);
                        if (replacement != type) expression[key] = TypeJson.Write(replacement);
                    }
                    else ReplacePhysicalTypes(value);
                }
            }
            else if (node is JsonArray array)
                for (var index = 0; index < array.Count; index++)
                {
                    var value = array[index];
                    if (value == null) continue;
                    if (TypeJson.Read(value) is TypeNode type)
                    {
                        var replacement = ReplaceEquivalentType(type, semanticElement, physicalElement);
                        if (replacement != type) array[index] = TypeJson.Write(replacement);
                    }
                    else ReplacePhysicalTypes(value);
                }
        }

        ReplacePhysicalTypes(spread);
        spread["elem"] = TypeJson.Write(physicalElement);
        spread[ExistentialArrayElementProjectionKey] = TypeJson.Write(semanticElement);
    }

    static bool CanUseClrVariance(TypeNode.Fqn target, IReadOnlyList<TypeNode> sourceArguments,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (target.Args == null || sourceArguments.Count != target.Args.Length) return false;
        for (var index = 0; index < target.Args.Length; index++)
        {
            if (SupertypeGraph.TypeKey(sourceArguments[index]) == SupertypeGraph.TypeKey(target.Args[index]))
                continue;
            if (!TryClrVariance(target.Name, target.Args.Length, index, defs, refs, out var variance)
                || variance is not ("in" or "out")
                || !(variance == "out"
                    ? IsClrAssignable(sourceArguments[index], target.Args[index], defs, refs)
                    : IsClrAssignable(target.Args[index], sourceArguments[index], defs, refs)))
                return false;
        }
        return true;
    }

    static bool TryClrVariance(string ownerName, int ownerArity, int index,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs, out string variance)
    {
        variance = null;
        if (defs.TryGetValue(ownerName, out var local) && local["typeParams"] is JsonArray localParameters
            && index >= 0 && index < localParameters.Count)
        {
            var declared = localParameters[index] is JsonObject parameter
                ? Str(parameter["variance"])
                : null;
            if (Str(local["kind"]) != "interface"
                || declared is not ("in" or "out")
                || LocalVarianceConflicts(local, index, declared, defs, refs))
                return false;
            variance = declared;
            return variance != null;
        }
        var reflected = refs.ResolveNetType(ownerName, ownerArity);
        var parameters = reflected?.IsGenericTypeDefinition == true ? reflected.GetGenericArguments() : null;
        if (parameters == null || index < 0 || index >= parameters.Length) return false;
        var attributes = parameters[index].GenericParameterAttributes
            & System.Reflection.GenericParameterAttributes.VarianceMask;
        variance = attributes switch
        {
            System.Reflection.GenericParameterAttributes.Covariant => "out",
            System.Reflection.GenericParameterAttributes.Contravariant => "in",
            _ => null,
        };
        return variance != null;
    }

    static bool LocalVarianceConflicts(JsonObject definition, int position, string variance,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        HashSet<string> visiting = null)
    {
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        var declared = variance == "out" ? 1 : -1;
        if (definition["methods"] is not JsonArray methods) return false;
        foreach (var method in methods.OfType<JsonObject>())
        {
            if (method["params"] is JsonArray parameters)
                foreach (var parameter in parameters.OfType<JsonObject>())
                    if (TypeJson.Read(parameter["type"]) is TypeNode parameterType
                        && VarianceConflicts(parameterType, position, -1, declared, defs, refs,
                            visiting))
                        return true;
            if (TypeJson.Read(method["ret"]) is TypeNode result
                && VarianceConflicts(result, position, 1, declared, defs, refs,
                    visiting))
                return true;
        }
        return false;
    }

    static bool VarianceConflicts(TypeNode type, int position, int context, int declared,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs, HashSet<string> visiting)
    {
        switch (type)
        {
            case TypeNode.Tv tv:
                return tv.Scope == "type" && tv.I == position
                    && (context == 0 || context != declared);
            case TypeNode.Fqn { Args: { } args } application:
                for (var index = 0; index < args.Length; index++)
                {
                    var nestedVariance = EffectiveClrVariance(
                        application.Name, args.Length, index, defs, refs, visiting);
                    var nestedContext = context == 0 || nestedVariance == 0
                        ? 0 : context * nestedVariance;
                    if (VarianceConflicts(args[index], position, nestedContext, declared,
                            defs, refs, visiting)) return true;
                }
                return false;
            case TypeNode.Projection projection:
                return VarianceConflicts(projection.Of, position, context, declared, defs, refs, visiting);
            case TypeNode.Nullable nullable:
                return VarianceConflicts(nullable.Of, position, context, declared, defs, refs, visiting);
            case TypeNode.Oblivious oblivious:
                return VarianceConflicts(oblivious.Of, position, context, declared, defs, refs, visiting);
            case TypeNode.Array array:
                return VarianceConflicts(array.Elem, position, 0, declared, defs, refs, visiting);
            case TypeNode.ByRef byRef:
                return VarianceConflicts(byRef.Of, position, 0, declared, defs, refs, visiting);
            case TypeNode.Ptr pointer:
                return VarianceConflicts(pointer.Of, position, 0, declared, defs, refs, visiting);
            case TypeNode.Mod modifier:
                return VarianceConflicts(modifier.Of, position, context, declared, defs, refs, visiting);
            case TypeNode.Fn function:
                if (VarianceConflicts(function.Ret, position, context, declared, defs, refs, visiting))
                    return true;
                return function.DelegateParams.Any(parameter =>
                    VarianceConflicts(parameter, position, -context, declared, defs, refs, visiting));
            default:
                return false;
        }
    }

    static int EffectiveClrVariance(string ownerName, int ownerArity, int index,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs, HashSet<string> visiting)
    {
        if (defs.TryGetValue(ownerName, out var local))
        {
            if (Str(local["kind"]) != "interface" || local["typeParams"] is not JsonArray parameters
                || index < 0 || index >= parameters.Count || parameters[index] is not JsonObject parameter)
                return 0;
            var variance = Str(parameter["variance"]);
            if (variance is not ("in" or "out")) return 0;
            var key = ownerName + "\0" + index;
            if (!visiting.Add(key)) return variance == "out" ? 1 : -1;
            var conflict = LocalVarianceConflicts(local, index, variance, defs, refs, visiting);
            visiting.Remove(key);
            return conflict ? 0 : variance == "out" ? 1 : -1;
        }
        var reflected = refs.ResolveNetType(ownerName, ownerArity);
        var reflectedParameters = reflected?.IsGenericTypeDefinition == true ? reflected.GetGenericArguments() : null;
        if (reflectedParameters == null || index < 0 || index >= reflectedParameters.Length) return 0;
        return (reflectedParameters[index].GenericParameterAttributes
                & System.Reflection.GenericParameterAttributes.VarianceMask) switch
        {
            System.Reflection.GenericParameterAttributes.Covariant => 1,
            System.Reflection.GenericParameterAttributes.Contravariant => -1,
            _ => 0,
        };
    }

    static bool IsClrAssignable(TypeNode source, TypeNode target,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        source = StripSourceNullability(source);
        target = StripSourceNullability(target);
        if (SupertypeGraph.TypeKey(source) == SupertypeGraph.TypeKey(target)) return true;
        if (!IsDefinitelyClrReferenceType(source, defs, refs)
            || !IsDefinitelyClrReferenceType(target, defs, refs)) return false;
        if (target is TypeNode.Fqn { Args: null, Name: "kotlin.Any" or "System.Object" or "object" })
            return true;
        if (source is TypeNode.Array sourceArray && target is TypeNode.Array targetArray)
            return sourceArray.Rank == targetArray.Rank && sourceArray.SzArray == targetArray.SzArray
                && IsClrAssignable(sourceArray.Elem, targetArray.Elem, defs, refs);
        if (source is not TypeNode.Fqn sourceFqn || target is not TypeNode.Fqn targetFqn) return false;

        var reflectedSource = ResolveClosedReferenceType(sourceFqn, refs);
        var reflectedTarget = ResolveClosedReferenceType(targetFqn, refs);
        if (reflectedSource != null && reflectedTarget != null)
            return reflectedTarget.IsAssignableFrom(reflectedSource);

        // A semantic edge out of an alias does not prove a direct CLR conversion to a locally emitted type.
        // `kotlin.String : CharSequence` is the canonical case: System.String does not implement the synthetic
        // dotkt$CharSequence interface and needs an adapter. Reflection above remains authoritative when both
        // endpoints are external; a local target cannot be inferred from the Kotlin hierarchy of an aliased source.
        if (reflectedSource != null && reflectedTarget == null
            && refs.TryResolveClrOwner(sourceFqn.Name, out _, out _))
            return false;

        var projected = ProjectConstructedArguments(sourceFqn, targetFqn.Name, defs, refs);
        if (projected == null) return false;
        if (targetFqn.Args == null) return true;
        return projected.Count == targetFqn.Args.Length
            && CanUseClrVariance(targetFqn, projected, defs, refs);
    }

    static Type ResolveClosedReferenceType(TypeNode.Fqn type, ReferenceMetadataIndex refs)
    {
        var arity = type.Args?.Length ?? 0;
        var name = refs.Aliases.TryGetValue(type.Name, out var alias) ? alias : type.Name;
        var open = refs.ResolveNetType(name, arity);
        if (open == null) return null;
        if (arity == 0) return open;
        if (!open.IsGenericTypeDefinition) return null;
        var arguments = type.Args.Select(argument =>
            StripSourceNullability(argument) is TypeNode.Fqn f ? ResolveClosedReferenceType(f, refs) : null).ToArray();
        if (arguments.Any(argument => argument == null)) return null;
        try { return open.MakeGenericType(arguments); }
        catch { return null; }
    }

    static bool IsDefinitelyClrReferenceType(TypeNode type,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Nullable nullable => nullable.Of is TypeNode.Fqn valueFqn
            ? !refs.IsValueType(valueFqn) && IsDefinitelyClrReferenceType(nullable.Of, defs, refs)
            : IsDefinitelyClrReferenceType(nullable.Of, defs, refs),
        TypeNode.Oblivious oblivious => IsDefinitelyClrReferenceType(oblivious.Of, defs, refs),
        TypeNode.Array or TypeNode.Fn => true,
        TypeNode.Fqn f when defs.TryGetValue(f.Name, out var local) =>
            Str(local["kind"]) is not ("struct" or "enum"),
        TypeNode.Fqn f => refs.ResolveNetType(
                refs.Aliases.TryGetValue(f.Name, out var alias) ? alias : f.Name,
                f.Args?.Length ?? 0) is Type reflected
            && !reflected.IsValueType,
        _ => false,
    };

    static bool HasKotlinVariantParameter(TypeNode.Fqn application, JsonArray parameters)
    {
        if (application.Args == null || parameters == null
            || application.Args.Length != parameters.Count) return false;
        return parameters.OfType<JsonObject>().Any(parameter =>
            Str(parameter["variance"]) is "in" or "out");
    }

    static bool ProjectionCanChangeConstruction(TypeNode.Fqn application,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        if (owners.TryGetValue(application.Name, out var local))
            return HasKotlinVariantParameter(application, local.Def["typeParams"] as JsonArray);
        return HasKotlinVariantParameter(application, refs.OwnerTypeParamDeclarations(application.Name));
    }

    static void MarkNeededType(TypeNode type, IReadOnlyDictionary<string, Owner> owners, bool runtimeClassifier)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } f:
                if (owners.TryGetValue(f.Name, out var owner)
                    && (runtimeClassifier || args.Any(IsExistentialArgument)))
                    owner.Needed = true;
                foreach (var a in args) MarkNeededType(a, owners, false);
                break;
            case TypeNode.Projection p:
                if (StripProjectionShell(p.Of) is TypeNode.Fqn projected
                    && owners.TryGetValue(projected.Name, out var projectedOwner))
                    projectedOwner.Needed |= HasKotlinVariantParameter(
                        projected, projectedOwner.Def["typeParams"] as JsonArray);
                MarkNeededType(p.Of, owners, false);
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
                var slot = InterfaceSlot(method, dependent ? StarMethodName(owner, method) : null,
                    owner.Name, owners, refs);
                var key = MethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                methods.Add(slot);
                if (dependent)
                    declared.Add(BridgeMethod(owner, method, owners, refs,
                        slotTypeParams: slot["typeParams"] as JsonArray));
                else if (Str(owner.Def["kind"]) == "interface")
                    // A concrete/default declaration on a derived CLR interface does not implicitly implement the
                    // same-shaped abstract slot on the synthesized existential base interface. Give every source
                    // interface method a distinct private forwarding body and an exact MethodImpl; abstract source
                    // methods are valid virtual call targets and are supplied by the eventual implementing class.
                    // The slot keeps its public source name, while the body name is deliberately unspeakable.
                    declared.Add(BridgeMethod(owner, method, owners, refs,
                        StarMethodName(owner, method),
                        slotMemberName: Str(slot[DeclarationIdentityBinding.ExplicitNameKey]) ?? Str(slot["name"]),
                        slotTypeParams: slot["typeParams"] as JsonArray));
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
                var slot = InterfaceSlot(method, bridgeName, owner.Name, owners, refs);
                var key = MethodKey(slot);
                if (key == null || !seen.Add(key)) continue;
                methods.Add(slot);
                declared.Add(BridgeMethod(owner, method, owners, refs, bridgeName,
                    new TypeNode.Fqn(declaringName), slotTypeParams: slot["typeParams"] as JsonArray));
            }

            foreach (var inner in owners.Values.Where(candidate => candidate.Needed
                         && Str(candidate.Def["semanticOwner"]) == owner.Name && IsInner(candidate.Def)))
            {
                if (inner.Def["ctors"] is not JsonArray constructors) continue;
                var ordinal = 0;
                foreach (var constructor in constructors.OfType<JsonObject>())
                {
                    var factory = InnerConstructorFactory(owner, inner, constructor, ordinal++, owners, refs);
                    var key = MethodKey(factory.Slot);
                    if (key == null || !seen.Add(key))
                        throw new InvalidOperationException(
                            $"duplicate existential inner-constructor factory for '{inner.Name}'");
                    methods.Add(factory.Slot);
                    declared.Add(factory.Bridge);
                }
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

    static JsonObject InterfaceSlot(JsonObject method, string replacementName, string semanticCarrierOwner,
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
        // An owner-independent existential slot is the same physical contract as the source MethodDef. Preserve an
        // explicit source allocation while it is still a stated BIR fact so the forwarding MethodImpl descriptor is
        // authored with that name before module-wide declaration allocation runs. A dependent slot has its own
        // compiler-generated name and must remain in that separate naming domain.
        if (replacementName == null && method[DeclarationIdentityBinding.ExplicitNameKey] != null)
            slot[DeclarationIdentityBinding.ExplicitNameKey] =
                method[DeclarationIdentityBinding.ExplicitNameKey].DeepClone();
        if (ExistentialSlotIdentity(method, semanticCarrierOwner) is string physicalIdentity)
        {
            slot[DeclarationIdentityBinding.Key] = physicalIdentity;
            slot["declarationSourceName"] = method["declarationSourceName"]?.DeepClone()
                ?? method["name"]?.DeepClone();
        }
        // A dependent existential slot may need a distinct physical name, but later override/bridge passes still need
        // the source declaration identity. Property accessors retain their explicit #397 identity; ordinary methods
        // carry this pass-local source edge. Neither consumer recovers meaning from the replacement spelling.
        if (KotlinPropertyAccessors.TryIdentity(method, out var propertyName, out var propertyAccessor))
        {
            var association = Str(method[KotlinPropertyAccessors.AssociationKey])
                ?? throw new InvalidOperationException(
                    $"property accessor '{MethodDisplay(method)}' has no source association");
            slot[KotlinPropertyAccessors.SourceNameKey] = propertyName;
            slot[KotlinPropertyAccessors.KindKey] = propertyAccessor;
            slot[KotlinPropertyAccessors.AssociationKey] = association;
            // An existential interface has no semantic PropertyDef of its own, so MethodSemantics cannot preserve the
            // slot's Kotlin accessor identity. Carry that already-known association on the exact generated MethodDef;
            // a consuming bir2cir can then select the physical row without reconstructing its unspeakable name.
            slot[KotlinPropertyAccessors.MetadataCarrierKey] = new JsonObject
            {
                ["name"] = propertyName,
                ["kind"] = propertyAccessor,
                ["association"] = association,
            };
        }
        else if (replacementName != null)
        {
            slot[SourceMemberKey] = method["name"]?.DeepClone();
            RoundtripMetadata.AddSourceMethodIdentity(slot,
                Str(method["name"])
                ?? throw new InvalidOperationException("existential interface slot has no source method name"));
        }
        // `suspend` is still a Kotlin declaration fact at this point.  The existential slot must
        // participate in the same later SuspendColdLowering as the generic declaration; dropping
        // mods here would cold-lower the call while leaving only an unlowered interface member.
        if (method["mods"] != null) slot["mods"] = method["mods"].DeepClone();
        CopyResultFacts(method, slot, owners, refs);
        return slot;
    }

    static JsonObject BridgeMethod(Owner owner, JsonObject method,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        string bridgeName = null, TypeNode.Fqn callOwner = null, string slotMemberName = null,
        JsonArray slotTypeParams = null)
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
        // The existential bridge is only a representation of this already-selected source member. Preserve #395's
        // exact declaration key on the forwarding edge so later suspend/collision lowering retargets the same
        // MethodDef; the bridge itself remains compiler-generated and does not become a second Kotlin declaration.
        if (Str(method[DeclarationIdentityBinding.Key]) is string declarationId)
            call[DeclarationIdentityBinding.Key] = declarationId;
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
            // This is the explicit MethodImpl body for the public member on the existential interface, not another
            // Kotlin-visible declaration on G<T>. Keep it private and generated; calls through G<*> target the public
            // interface slot, while the body forwards to G<T>'s source declaration.
            ["vis"] = "private",
            ["params"] = bridgeParams,
            ["ret"] = TypeJson.Write(EraseOwnerTv(originalRet, owners, refs)),
            ["body"] = body,
            ["attrs"] = new JsonArray(),
            ["generated"] = true,
            [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
        };
        if (slotTypeParams is { Count: > 0 }) bridge["typeParams"] = slotTypeParams.DeepClone();
        else if (method["typeParams"] is JsonArray tps && tps.Count > 0) bridge["typeParams"] = tps.DeepClone();
        // This forwarding declaration is created precisely to implement the corresponding method on the owner's
        // existential interface. State that exact relation now, while both identities are explicit. Leaving ilemit to
        // match the generated physical name against interface hierarchy members made the emitter re-infer the purpose
        // of this bridge and was also unable to distinguish inherited same-signature slots.
        var descriptor = new JsonObject
        {
            ["owner"] = TypeJson.Write(new TypeNode.Fqn(owner.ErasedName)),
            ["member"] = slotMemberName ?? bridge["name"]?.DeepClone(),
            ["arity"] = (method["typeParams"] as JsonArray)?.Count ?? 0,
            ["params"] = new JsonArray(bridgeParams.OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray()),
            ["ret"] = bridge["ret"]?.DeepClone(),
        };
        if (slotTypeParams is { Count: > 0 } descriptorTypeParams)
            descriptor["typeParams"] = descriptorTypeParams.DeepClone();
        bridge["clrInterfaceImpls"] = new JsonArray(descriptor);
        if (Str(owner.Def["kind"]) == "interface")
            bridge[KotlinPropertyAccessors.ClrInterfaceSlotBridgeKey] = true;
        // The forwarding bridge is a real declaration consumed by later lowering passes. Preserve
        // Kotlin modifiers rather than manufacturing a non-suspend body that calls a suspend slot.
        if (method["mods"] != null) bridge["mods"] = method["mods"].DeepClone();
        CopyResultFacts(method, bridge, owners, refs);
        return bridge;
    }

    static (JsonObject Slot, JsonObject Bridge) InnerConstructorFactory(
        Owner outer, Owner inner, JsonObject constructor, int ordinal,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        var capturedCount = InnerCapturedCount(inner.Def);
        if (capturedCount <= 0 || capturedCount > inner.Arity)
            throw new InvalidOperationException(
                $"inner type '{inner.Name}' has an invalid captured generic frame");
        var constructorParams = constructor["params"] as JsonArray ?? new JsonArray();
        if (constructorParams.Count == 0
            || TypeJson.Read((constructorParams[0] as JsonObject)?["type"]) is not TypeNode.Fqn hiddenOuter
            || hiddenOuter.Name != outer.Name)
            throw new InvalidOperationException(
                $"inner constructor '{inner.Name}/{constructorParams.Count}' has no exact '{outer.Name}' outer slot");

        var name = InnerConstructorFactoryName(inner, ordinal);
        var physicalParams = new JsonArray();
        var exactParams = new List<TypeNode>();
        foreach (var (parameter, index) in constructorParams.OfType<JsonObject>().Skip(1).Select((p, i) => (p, i)))
        {
            var source = RequiredParamType(parameter, index + 1, inner.Name + ".<init>");
            var copy = parameter.DeepClone() as JsonObject;
            physicalParams.Add(copy);
            exactParams.Add(source);
        }

        var directDependent = DirectOwnerDependentInnerParameters(inner, capturedCount);
        var witnesses = directDependent.ToDictionary(
            index => index, _ => (TypeNode)new TypeNode.Fqn("kotlin.Nothing"));
        var methodPositions = Enumerable.Range(capturedCount, inner.Arity - capturedCount)
            .Where(index => !directDependent.Contains(index)).ToArray();
        var methodIndex = methodPositions.Select((source, index) => (source, index))
            .ToDictionary(pair => pair.source, pair => pair.index);
        TypeNode Project(TypeNode type) => ProjectInnerFactoryType(
            type, capturedCount, witnesses, methodIndex);

        // The public existential factory cannot mention the captured outer frame. A directly dependent own parameter
        // is therefore fixed to Kotlin bottom and omitted from the MethodDef frame. A transitively dependent parameter
        // remains a real method argument so its exact source value and identity survive the construction seam.
        for (var i = 0; i < exactParams.Count; i++) exactParams[i] = Project(exactParams[i]);
        for (var i = 0; i < physicalParams.Count; i++)
        {
            if (physicalParams[i] is not JsonObject parameter) continue;
            var exact = exactParams[i];
            var physical = EraseOwnerTv(exact, owners, refs);
            parameter["type"] = TypeJson.Write(physical);
            if (!physical.Equals(exact)) parameter["kotlinType"] ??= TypeNode.ToJson(exact);
        }

        var ownTypeParams = InnerFactoryTypeParams(
            inner, capturedCount, directDependent, witnesses, methodIndex, owners, refs);
        var result = new TypeNode.Fqn(inner.ErasedName);
        var carrierParams = new JsonArray(constructorParams.OfType<JsonObject>().Skip(1)
            .Select(parameter => TypeJson.Write(RequiredParamType(parameter, 0, inner.Name + ".<init>"))).ToArray());
        var slot = new JsonObject
        {
            ["name"] = name,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = true,
            ["objectOverride"] = false,
            ["vis"] = "public",
            ["params"] = physicalParams.DeepClone(),
            ["ret"] = TypeJson.Write(result),
            ["body"] = new JsonArray(),
            ["attrs"] = new JsonArray(),
            ["generated"] = true,
            [InnerConstructorFactoryKey] = new JsonObject
            {
                ["inner"] = inner.Name,
                ["params"] = carrierParams,
                // Position map, not type tokens: -1 is a direct owner-dependent Kotlin-bottom argument, while N
                // selects factory MethodDef argument N. Transitively dependent source arguments remain ordinary
                // method slots; only constraints that name the hidden outer or a bottom-fixed slot are omitted.
                ["typeArgs"] = new JsonArray(Enumerable.Range(capturedCount, inner.Arity - capturedCount)
                    .Select(index => (JsonNode)JsonValue.Create(
                        directDependent.Contains(index)
                            ? InnerFactoryBottomTypeArgument
                            : methodIndex[index])).ToArray()),
            },
        };
        if (ownTypeParams.Count > 0) slot["typeParams"] = ownTypeParams.DeepClone();

        var args = new JsonArray { new JsonObject { ["k"] = "this" } };
        foreach (var (parameter, index) in physicalParams.OfType<JsonObject>().Select((p, i) => (p, i)))
        {
            var parameterName = Str(parameter["name"]) ?? "p" + index;
            JsonNode value = new JsonObject { ["k"] = "local", ["name"] = parameterName };
            var physical = TypeJson.Read(parameter["type"]);
            var exact = exactParams[index];
            if (!exact.Equals(physical))
                value = new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(exact),
                    ["e"] = value,
                    ["_exactBridgeCast"] = true,
                };
            args.Add(value);
        }

        var constructedArgs = Enumerable.Range(0, inner.Arity)
            .Select(index => Project(new TypeNode.Tv("type", index))).ToArray();
        var exactSignature = new JsonArray(constructorParams.OfType<JsonObject>()
            .Select(parameter => TypeJson.Write(Project(
                RequiredParamType(parameter, 0, inner.Name + ".<init>")))).ToArray());
        var construction = new JsonObject
        {
            ["k"] = "new",
            ["type"] = TypeJson.Write(new TypeNode.Fqn(inner.Name, constructedArgs)),
            ["argTypes"] = exactSignature.DeepClone(),
            ["memberSignature"] = exactSignature,
            ["args"] = args,
        };
        var implementation = new JsonObject
        {
            ["owner"] = TypeJson.Write(new TypeNode.Fqn(outer.ErasedName)),
            ["member"] = name,
            ["arity"] = ownTypeParams.Count,
            ["params"] = new JsonArray(physicalParams.OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray()),
            ["ret"] = TypeJson.Write(result),
        };
        if (ownTypeParams.Count > 0) implementation["typeParams"] = ownTypeParams.DeepClone();
        var bridge = new JsonObject
        {
            ["name"] = name,
            ["static"] = false,
            ["override"] = false,
            ["virtual"] = true,
            ["abstract"] = false,
            ["objectOverride"] = false,
            ["vis"] = "private",
            ["params"] = physicalParams,
            ["ret"] = TypeJson.Write(result),
            ["body"] = new JsonArray(new JsonObject { ["k"] = "return", ["value"] = construction }),
            ["attrs"] = new JsonArray(),
            ["generated"] = true,
            [KotlinPropertyAccessors.PhysicalSlotBridgeKey] = true,
            ["clrInterfaceImpls"] = new JsonArray(implementation),
        };
        if (ownTypeParams.Count > 0) bridge["typeParams"] = ownTypeParams;
        return (slot, bridge);
    }

    static JsonArray InnerFactoryTypeParams(Owner inner, int capturedCount,
        IReadOnlySet<int> directDependent,
        IReadOnlyDictionary<int, TypeNode> witnesses,
        IReadOnlyDictionary<int, int> methodIndex,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        var result = new JsonArray();
        if (inner.Def["typeParams"] is not JsonArray typeParams) return result;
        foreach (var (parameter, sourceIndex) in typeParams.Select((parameter, index) => (parameter, index))
                     .Where(pair => pair.index >= capturedCount && !directDependent.Contains(pair.index)))
        {
            if (parameter is not JsonObject declaration)
            {
                result.Add(parameter?.DeepClone());
                continue;
            }
            var copy = declaration.DeepClone() as JsonObject;
            if (copy["constraints"] is JsonArray constraints)
            {
                var rewritten = new JsonArray();
                foreach (var constraint in constraints)
                    if (TypeJson.Read(constraint) is TypeNode type)
                    {
                        // The MethodDef can name its retained own parameters, including self-bounds. It cannot name
                        // the captured outer frame or an own parameter fixed to bottom and omitted from this frame.
                        // Apply the same positional rule as the real inner TypeDef below.
                        if (!InnerConstraintIsRepresentable(
                                sourceIndex, type, capturedCount, directDependent)) continue;
                        var physical = EraseOwnerTv(
                            ProjectInnerFactoryType(type, capturedCount, witnesses, methodIndex), owners, refs);
                        if (!IsObjectish(physical)) rewritten.Add(TypeJson.Write(physical));
                    }
                copy["constraints"] = rewritten;
            }
            result.Add(copy);
        }
        return result;
    }

    static IReadOnlyDictionary<string, HashSet<int>> WeakenOwnerDependentInnerConstraints(IEnumerable<Owner> owners)
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var inner in owners.Where(owner => IsInner(owner.Def)))
        {
            var capturedCount = InnerCapturedCount(inner.Def);
            var dependent = OwnerDependentInnerParameters(inner, capturedCount);
            var directDependent = DirectOwnerDependentInnerParameters(inner, capturedCount);
            if (dependent.Count == 0 || inner.Def["typeParams"] is not JsonArray parameters) continue;

            var recorded = new JsonObject();
            foreach (var index in dependent.OrderBy(index => index))
            {
                if (parameters[index] is not JsonObject parameter
                    || parameter["constraints"] is not JsonArray constraints || constraints.Count == 0) continue;
                var retained = new JsonArray();
                var removed = new JsonArray();
                foreach (var constraint in constraints)
                {
                    var destination = TypeJson.Read(constraint) is TypeNode type
                        && !InnerConstraintIsRepresentable(index, type, capturedCount, directDependent)
                        ? removed
                        : retained;
                    destination.Add(constraint?.DeepClone());
                }
                if (retained.Count == constraints.Count) continue;
                recorded[index.ToString()] = constraints.DeepClone();
                parameter[ErasedInnerConstraintKey] = removed;
                parameter["constraints"] = retained;
                if (!result.TryGetValue(inner.Name, out var changed))
                    result[inner.Name] = changed = new HashSet<int>();
                changed.Add(index);
            }
            if (recorded.Count > 0)
                KotlinSupertypesRecord.Merge(inner.Def, new JsonObject { ["bounds"] = recorded });
        }
        return result;
    }

    // TypeOwnershipLowering prepares a compiler-generated nested type by copying its semantic owner's complete
    // generic frame into the synthetic prefix. That happens while every constraint is still Kotlin-complete. If the
    // existential inner ABI subsequently removes an owner constraint that cannot be expressed physically, leaving
    // the old row on a closure/SAM prefix creates an impossible construction: the owner's now-unconstrained `E` is
    // supplied to the nested copy that still requires `E : T`, and the CLR loader correctly rejects it.
    //
    // Synchronize only compiler-generated owner-prefix slots, from the already-selected physical owner declaration.
    // The removed facts travel too: constrained receiver binding inside the generated body must cast a call through
    // that erased bound just as it does in the owner body. The marker remains bir2cir-internal and is dropped before
    // CIR. User declarations and a generated type's own parameters are untouched.
    static void SynchronizeGeneratedOwnerConstraintPrefixes(
        IReadOnlyDictionary<string, JsonObject> defs,
        IReadOnlyDictionary<string, HashSet<int>> weakenedOwnerSlots)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var completed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        HashSet<int> Synchronize(string name, JsonObject generated)
        {
            if (completed.TryGetValue(name, out var prior)) return prior;
            if (!visiting.Add(name))
                throw new InvalidOperationException($"cyclic semantic owner chain at generated type '{name}'");
            try
            {
                var changed = weakenedOwnerSlots.TryGetValue(name, out var directlyWeakened)
                    ? new HashSet<int>(directlyWeakened)
                    : new HashSet<int>();
                if (Str(generated["semanticOwner"]) is not string ownerName
                    || !defs.TryGetValue(ownerName, out var owner)) return completed[name] = changed;
                var ownerChanges = Bool(owner["generated"])
                    ? Synchronize(ownerName, owner)
                    : weakenedOwnerSlots.TryGetValue(ownerName, out var weakened)
                        ? weakened
                        : new HashSet<int>();
                if (ownerChanges.Count == 0) return completed[name] = changed;
                if (generated["outerTypeParamCount"] is not JsonValue countValue
                    || !countValue.TryGetValue<int>(out var capturedCount) || capturedCount <= 0
                    || generated["typeParams"] is not JsonArray generatedParams
                    || owner["typeParams"] is not JsonArray ownerParams
                    || capturedCount > generatedParams.Count || capturedCount != ownerParams.Count)
                    throw new InvalidOperationException(
                        $"generated type '{name}' cannot synchronize its weakened semantic owner '{ownerName}' frame");

                foreach (var index in ownerChanges.OrderBy(index => index))
                {
                    if (index < 0 || index >= capturedCount || ownerParams[index] is not JsonObject source)
                        throw new InvalidOperationException(
                            $"generated type '{name}' has no declaration for weakened owner slot {index}");
                    var target = generatedParams[index] as JsonObject;
                    if (target == null)
                    {
                        if (Str(generatedParams[index]) is not string parameterName)
                            throw new InvalidOperationException(
                                $"generated type '{name}' has a malformed owner slot {index}");
                        target = new JsonObject { ["name"] = parameterName };
                        generatedParams[index] = target;
                    }
                    if (source["constraints"] is JsonArray constraints)
                        target["constraints"] = constraints.DeepClone();
                    else
                        target.Remove("constraints");
                    if (source[ErasedInnerConstraintKey] is JsonArray removed)
                        target[ErasedInnerConstraintKey] = removed.DeepClone();
                    else
                        target.Remove(ErasedInnerConstraintKey);
                    changed.Add(index);
                }
                return completed[name] = changed;
            }
            finally
            {
                visiting.Remove(name);
            }
        }

        foreach (var (name, definition) in defs)
            if (Bool(definition["generated"])) Synchronize(name, definition);
    }

    static HashSet<int> OwnerDependentInnerParameters(Owner inner, int capturedCount)
    {
        var result = new HashSet<int>();
        if (inner.Def["typeParams"] is not JsonArray parameters) return result;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (parameter, index) in parameters.Select((parameter, index) => (parameter, index)))
            {
                if (index < capturedCount || result.Contains(index)
                    || parameter is not JsonObject declaration
                    || declaration["constraints"] is not JsonArray constraints) continue;
                if (constraints.Any(constraint => TypeJson.Read(constraint) is TypeNode type
                        && ContainsInnerDependency(type, capturedCount, result)))
                    changed |= result.Add(index);
            }
        }
        return result;
    }

    static HashSet<int> DirectOwnerDependentInnerParameters(Owner inner, int capturedCount)
    {
        var result = new HashSet<int>();
        if (inner.Def["typeParams"] is not JsonArray parameters) return result;
        foreach (var (parameter, index) in parameters.Select((parameter, index) => (parameter, index)))
        {
            if (index < capturedCount || parameter is not JsonObject declaration
                || declaration["constraints"] is not JsonArray constraints) continue;
            if (constraints.Any(constraint => TypeJson.Read(constraint) is TypeNode type
                    && ContainsCapturedInnerParameter(type, capturedCount)))
                result.Add(index);
        }
        return result;
    }

    static bool InnerConstraintIsRepresentable(
        int parameterIndex, TypeNode constraint, int capturedCount, IReadOnlySet<int> directDependent) =>
        // A direct parameter is instantiated with the physical bottom witness (object), so no source-authored bound
        // on that parameter can remain a valid CLR GenericParamConstraint. A retained parameter may keep every bound
        // expressible solely in its MethodDef/TypeDef frame, including F : Comparable<F>; only references to the
        // unavailable captured frame or a bottom-fixed slot must be omitted.
        !directDependent.Contains(parameterIndex)
        && !ContainsInnerDependency(constraint, capturedCount, directDependent);

    static bool ContainsCapturedInnerParameter(TypeNode type, int capturedCount) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv => tv.I < capturedCount,
        TypeNode.Projection projection => ContainsCapturedInnerParameter(projection.Of, capturedCount),
        TypeNode.Fqn { Args: { } args } => args.Any(argument =>
            ContainsCapturedInnerParameter(argument, capturedCount)),
        TypeNode.Nullable nullable => ContainsCapturedInnerParameter(nullable.Of, capturedCount),
        TypeNode.Oblivious oblivious => ContainsCapturedInnerParameter(oblivious.Of, capturedCount),
        TypeNode.Array array => ContainsCapturedInnerParameter(array.Elem, capturedCount),
        TypeNode.ByRef byRef => ContainsCapturedInnerParameter(byRef.Of, capturedCount),
        TypeNode.Ptr pointer => ContainsCapturedInnerParameter(pointer.Of, capturedCount),
        TypeNode.Fn function => ContainsCapturedInnerParameter(function.Ret, capturedCount)
            || function.Params.Any(parameter => ContainsCapturedInnerParameter(parameter, capturedCount))
            || function.Recv != null && ContainsCapturedInnerParameter(function.Recv, capturedCount)
            || function.Ctx?.Any(context => ContainsCapturedInnerParameter(context, capturedCount)) == true,
        _ => false,
    };

    static bool ContainsInnerDependency(TypeNode type, int capturedCount, IReadOnlySet<int> dependent) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv => tv.I < capturedCount || dependent.Contains(tv.I),
        TypeNode.Projection projection => ContainsInnerDependency(projection.Of, capturedCount, dependent),
        TypeNode.Fqn { Args: { } args } => args.Any(argument =>
            ContainsInnerDependency(argument, capturedCount, dependent)),
        TypeNode.Nullable nullable => ContainsInnerDependency(nullable.Of, capturedCount, dependent),
        TypeNode.Oblivious oblivious => ContainsInnerDependency(oblivious.Of, capturedCount, dependent),
        TypeNode.Array array => ContainsInnerDependency(array.Elem, capturedCount, dependent),
        TypeNode.ByRef byRef => ContainsInnerDependency(byRef.Of, capturedCount, dependent),
        TypeNode.Ptr pointer => ContainsInnerDependency(pointer.Of, capturedCount, dependent),
        TypeNode.Fn function => ContainsInnerDependency(function.Ret, capturedCount, dependent)
            || function.Params.Any(parameter => ContainsInnerDependency(parameter, capturedCount, dependent))
            || function.Recv != null && ContainsInnerDependency(function.Recv, capturedCount, dependent)
            || function.Ctx?.Any(context => ContainsInnerDependency(context, capturedCount, dependent)) == true,
        _ => false,
    };

    static TypeNode ProjectInnerFactoryType(TypeNode type, int capturedCount,
        IReadOnlyDictionary<int, TypeNode> witnesses, IReadOnlyDictionary<int, int> methodIndex) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I < capturedCount => tv,
        TypeNode.Tv { Scope: "type" } tv when witnesses.TryGetValue(tv.I, out var witness) =>
            ProjectInnerFactoryType(witness, capturedCount, witnesses, methodIndex),
        TypeNode.Tv { Scope: "type" } tv when methodIndex.TryGetValue(tv.I, out var index)
            => new TypeNode.Tv("method", index),
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            ProjectInnerFactoryType(projection.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => ProjectInnerFactoryType(arg, capturedCount, witnesses, methodIndex)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(
            ProjectInnerFactoryType(n.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(
            ProjectInnerFactoryType(o.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Array a => new TypeNode.Array(
            ProjectInnerFactoryType(a.Elem, capturedCount, witnesses, methodIndex)),
        TypeNode.ByRef b => new TypeNode.ByRef(
            ProjectInnerFactoryType(b.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Ptr p => new TypeNode.Ptr(
            ProjectInnerFactoryType(p.Of, capturedCount, witnesses, methodIndex)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            ProjectInnerFactoryType(fn.Ret, capturedCount, witnesses, methodIndex),
            fn.Params.Select(parameter => ProjectInnerFactoryType(
                parameter, capturedCount, witnesses, methodIndex)).ToArray(),
            fn.Recv == null ? null : ProjectInnerFactoryType(fn.Recv, capturedCount, witnesses, methodIndex), fn.Clr,
            fn.Ctx?.Select(context => ProjectInnerFactoryType(
                context, capturedCount, witnesses, methodIndex)).ToArray()),
        _ => type,
    };

    static string InnerConstructorFactoryName(Owner inner, int ordinal)
    {
        // The full UTF-8 classifier is encoded injectively. Replacing punctuation with '_' made distinct legal
        // Kotlin names such as `A-B` and A_B compete for the same MethodDef name on one existential outer.
        var token = Convert.ToHexString(Encoding.UTF8.GetBytes(inner.Name));
        return "$star$new$" + token + "$" + ordinal;
    }

    static int InnerCapturedCount(JsonObject inner) =>
        inner["outerTypeParamCount"] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;

    static bool IsInner(JsonObject type) =>
        type["mods"] is JsonObject mods && Bool(mods["inner"]);

    static string ExistentialSlotIdentity(JsonObject declaration, string semanticCarrierOwner) =>
        Str(declaration[DeclarationIdentityBinding.Key]) is string declarationId
            ? DeclarationIdentityBinding.PhysicalOnlyId(
                declarationId, "existential-slot:" + semanticCarrierOwner)
            : null;

    // This pass runs after declaration-side erasure has recorded semantic return facts but before suspend lowering.
    // A synthesized existential slot/bridge is a declaration in its own right, so it must carry the same facts in
    // its owner-erased form. In particular, copying only `mods.suspend` admits the declaration to SuspendColdLowering
    // while dropping the mandatory `suspendRet`, which makes valid producer BIR internally inconsistent.
    static void CopyResultFacts(JsonObject source, JsonObject target,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        if (TypeJson.Read(source["suspendRet"]) is TypeNode suspendRet)
            target["suspendRet"] = TypeJson.Write(EraseOwnerTv(suspendRet, owners, refs));

        if (Str(source["suspendResult"]) is string logicalSuspendResult)
            target["suspendResult"] = TypeNode.ToJson(
                EraseOwnerTv(TypeNode.Parse(logicalSuspendResult), owners, refs));

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
            var erased = EraseOwnerTv(pt, owners, refs);
            if (TypeNode.ToJson(erased) != TypeNode.ToJson(pt))
                copy["kotlinType"] ??= TypeNode.ToJson(pt);
            copy["type"] = TypeJson.Write(erased);
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
        return "$star$" + Str(method["name"]) + "$" + ordinal;
    }

    static string BaseStarMethodName(string declaringName, JsonObject declaringDef, JsonObject method)
    {
        var methods = declaringDef["methods"] as JsonArray;
        var ordinal = methods == null ? 0 : methods.TakeWhile(m => !ReferenceEquals(m, method)).Count();
        var ownerToken = new string(declaringName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return "$star$base$" + ownerToken + "$" + Str(method["name"]) + "$" + ordinal;
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
            .Select(p => TypeJson.Read(p["type"])?.ToString() ?? "?") ?? Enumerable.Empty<string>())
            + "|" + KotlinOverrideSlotBridge.MethodTypeParameterShapeKey(
                method["typeParams"] as JsonArray, Array.Empty<TypeNode>());
    }

    static bool ContainsOwnerTvInSignature(JsonObject method)
    {
        // NullableGenericErasure runs before this pass. Its physical object slot no longer contains the owner Tv, but
        // the preserved Kotlin slot still does and therefore still requires a distinct existential bridge. Ignoring
        // that fact makes `G<T>.f(T?)` and `G<T>.f(Any?)` collapse again on G<*>, after #395 separated the source slots.
        if (EncodedContainsOwnerTv(method["nullableGenericRet"])
            || EncodedContainsOwnerTv(method["nullableGenericSuspendRet"])) return true;
        if (TypeJson.Read(method["ret"]) is TypeNode ret && ContainsOwnerTv(ret)) return true;
        if (method["params"] is JsonArray ps)
            foreach (var p in ps.OfType<JsonObject>())
                if (EncodedContainsOwnerTv(p["nullableGeneric"])
                    || TypeJson.Read(p["type"]) is TypeNode pt && ContainsOwnerTv(pt)) return true;
        if (method["typeParams"] is JsonArray mtps)
            foreach (var tp in mtps.OfType<JsonObject>())
                if (tp["constraints"] is JsonArray cs)
                    foreach (var c in cs)
                        if (TypeJson.Read(c) is TypeNode ct && ContainsOwnerTv(ct)) return true;
        return false;
    }

    static bool EncodedContainsOwnerTv(JsonNode node)
    {
        if (Str(node) is not string encoded) return false;
        try { return ContainsOwnerTv(TypeNode.Parse(encoded)); }
        catch { return false; }
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
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        IReadOnlySet<int> existentialTypeParameters = null,
        IReadOnlySet<int> existentialMethodParameters = null,
        JsonArray typeParameterDeclarations = null,
        JsonArray methodParameterDeclarations = null,
        TypeNode.Fqn currentThisType = null,
        bool boundDeclaration = false,
        IReadOnlyDictionary<string, string> localClrAliases = null)
    {
        switch (node)
        {
            case JsonObject obj:
                var projectedArrayRead = Bool(obj[ProjectedArrayReadKey]);
                var projectedConstructorArgumentTargets = ProjectedConstructorArgumentTargets(
                    obj, owners, refs, localClrAliases);
                var childTypeParameters = existentialTypeParameters;
                var childMethodParameters = existentialMethodParameters;
                var childTypeParameterDeclarations = typeParameterDeclarations;
                var childMethodParameterDeclarations = methodParameterDeclarations;
                var childThisType = currentThisType;
                if (Str(obj["kind"]) != null)
                {
                    childTypeParameterDeclarations = obj["typeParams"] is JsonArray declaredTypeParameters
                        ? declaredTypeParameters.DeepClone().AsArray() : null;
                    childTypeParameters = ExistentialTypeParameters(childTypeParameterDeclarations, "type");
                    childMethodParameters = null;
                    childMethodParameterDeclarations = null;
                    if (Str(obj["name"]) is string typeName)
                    {
                        var arity = (obj["typeParams"] as JsonArray)?.Count ?? 0;
                        childThisType = new TypeNode.Fqn(typeName, Enumerable.Range(0, arity)
                            .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
                    }
                }
                else if (obj["body"] is JsonArray && obj["params"] is JsonArray)
                {
                    childMethodParameterDeclarations = obj["typeParams"] is JsonArray declaredMethodParameters
                        ? declaredMethodParameters.DeepClone().AsArray() : null;
                    childMethodParameters = ExistentialTypeParameters(childMethodParameterDeclarations, "method");
                }

                BindStarInnerConstruction(obj, owners, defs, refs,
                    existentialTypeParameters, existentialMethodParameters);
                LowerProjectedConstruction(obj, owners, defs, refs);
                BindStarFieldThroughCanonicalGetter(obj, owners, defs, refs);
                BindProjectedConstraintMember(obj, typeParameterDeclarations, methodParameterDeclarations,
                    defs, refs);
                BindInheritedStarMember(obj, owners, defs, refs);
                if (!projectedArrayRead)
                    BindProjectedArrayRead(obj, owners, refs);
                BindProjectedArrayGenericCall(obj, owners, refs);
                if (TryVarianceProjectedArrayOwner(obj, defs, refs, out var projectedArrayOwner)
                    && TryExistentialCarrier(projectedArrayOwner.Name, owners, refs, out var arrayCarrier))
                {
                    obj["elem"] = TypeJson.Write(new TypeNode.Fqn(arrayCarrier));
                    obj[ExistentialArrayElementProjectionKey] = TypeJson.Write(projectedArrayOwner);
                }
                if (TryVarianceProjectedArraySpreadOwner(obj, defs, refs, out var projectedSpreadOwner)
                    && TryExistentialCarrier(projectedSpreadOwner.Name, owners, refs, out var spreadCarrier))
                    RewriteVarianceProjectedArraySpread(
                        obj, projectedSpreadOwner, new TypeNode.Fqn(spreadCarrier));
                if (TryVarianceProjectedArrayInitializerOwner(obj, defs, refs, out var initializerArrayOwner)
                    && TryExistentialCarrier(initializerArrayOwner.Name, owners, refs,
                        out var initializerArrayCarrier))
                    RewriteVarianceProjectedArrayInitializer(
                        obj, initializerArrayOwner, new TypeNode.Fqn(initializerArrayCarrier), defs, refs);
                // A cast operand can itself lower onto an existential carrier. Decide whether the parent has an
                // exact constructed source only after that physical representation is known; its stale frontend
                // `sty` is not proof that `(x as Derived<T>) as Base<T>` still carries Derived<T> at this point.
                var runtimeKind = Str(obj["k"]);
                var runtimeOperand = runtimeKind == "cast" ? obj["e"] : null;
                var rewroteRuntimeOperand = runtimeOperand != null;
                if (runtimeOperand != null)
                    Rewrite(runtimeOperand, owners, defs, refs,
                        childTypeParameters, childMethodParameters,
                        childTypeParameterDeclarations, childMethodParameterDeclarations, childThisType,
                        boundDeclaration, localClrAliases);
                // Kotlin `is G<X>` is a raw-classifier check, and a true `as G<*>` also has no closed CLR target.
                // An `as G<T>` from an otherwise-unrelated source likewise cannot test T on the CLR and uses the
                // existential carrier. Do not confuse that erased runtime check with a statically proven constructed
                // upcast, though: `Derived<T> as Base<T>` already has an exact CLR edge. Erasing that target leaves a
                // Base$star value at later Base<T> call/return slots and makes otherwise ordinary generic code
                // unverifiable.
                if (!Bool(obj["_exactBridgeCast"]) && runtimeKind is "isInst" or "cast"
                    && TypeJson.Read(obj["type"]) is TypeNode.Fqn { Args: { } runtimeArgs } runtimeF
                    && TryExistentialCarrier(runtimeF.Name, owners, refs, out var runtimeCarrier)
                    && (runtimeKind == "isInst"
                        || (runtimeArgs.Any(ContainsStarOrTypeVariable)
                            && !IsProvenConstructedUpcast(
                                obj, runtimeF, defs, refs, currentThisType))))
                    obj["type"] = TypeJson.Write(new TypeNode.Fqn(runtimeCarrier));
                obj.Remove("_exactBridgeCast");
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value == null || key == "name" || key == InnerConstructorFactoryKey
                        || key == ExistentialResultProjectionKey
                        || key == ExistentialArrayElementProjectionKey
                        || rewroteRuntimeOperand && key == "e") continue;
                    var childBoundDeclaration = boundDeclaration
                        || IsBoundDeclarationType(obj, key, refs, localClrAliases);
                    if (TypeJson.Read(value) is TypeNode type)
                        obj[key] = TypeJson.Write(RewriteType(
                            type, owners, refs, childBoundDeclaration, localClrAliases));
                    else
                        Rewrite(value, owners, defs, refs,
                            childTypeParameters, childMethodParameters,
                            childTypeParameterDeclarations, childMethodParameterDeclarations, childThisType,
                            childBoundDeclaration, localClrAliases);
                }
                ApplyProjectedConstructorArgumentCasts(obj, projectedConstructorArgumentTargets);
                // A star-projected inner construction is replaced while visiting the receiver below this call.  Its
                // result is the inner existential carrier, so bind the immediately-following member only after that
                // receiver seam is visible.  The initial pass above is still required for ordinary star receivers.
                BindInheritedStarMember(obj, owners, defs, refs);
                if (Str(obj["k"]) == "callInstance"
                    && TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn erasedOwner
                    && (owners.Values.Any(o => o.ErasedName == erasedOwner.Name)
                        || refs.IsExistentialPhysicalOwner(erasedOwner.Name)))
                    obj["virtual"] = true;
                if (obj["body"] is JsonArray && obj["params"] is JsonArray)
                    NormalizeInnerFactoryLocals(obj, owners, defs, refs);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    if (value == null) continue;
                    if (TypeJson.Read(value) is TypeNode type)
                        arr[i] = TypeJson.Write(RewriteType(
                            type, owners, refs, boundDeclaration, localClrAliases));
                    else
                        Rewrite(value, owners, defs, refs,
                            existentialTypeParameters, existentialMethodParameters,
                            typeParameterDeclarations, methodParameterDeclarations, currentThisType,
                            boundDeclaration, localClrAliases);
                }
                break;
        }
    }

    // An opaque projected-alias value can still be passed to a constructor whose exact selected declaration names a
    // reifiable closed face.  The value slot is object, but `newobj` consumes that declaration face; state the
    // runtime-checked conversion explicitly from the frontend's memberSignature rather than asking ilemit or the CLR
    // verifier to accept object implicitly.  Capture the targets before the ordinary sweep erases argTypes.
    static TypeNode[] ProjectedConstructorArgumentTargets(JsonObject construction,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
    {
        var kind = Str(construction["k"]);
        if (kind is not ("new" or "newClr")
            || TypeJson.Read(construction["type"]) is not TypeNode.Fqn owner
            || (kind == "new" && !(refs.TryResolveClrOwner(owner.Name, out _, out _)
                || localClrAliases?.ContainsKey(owner.Name) == true))
            || construction["argTypes"] is not JsonArray arguments
            || construction["argTypes"] is not JsonArray declaration
            || arguments.Count != declaration.Count)
            return null;

        var targets = new TypeNode[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var source = TypeJson.Read(arguments[index]);
            var selected = TypeJson.Read(declaration[index]);
            if (source == null || selected == null || !ContainsExistentialProjection(source)) continue;
            var physicalSource = RewriteType(source, owners, refs, localClrAliases: localClrAliases);
            if (physicalSource is not TypeNode.Fqn { Name: "kotlin.Any", Args: null }) continue;
            var physicalTarget = RewriteType(
                selected, owners, refs, boundDeclaration: true, localClrAliases: localClrAliases);
            if (physicalTarget is TypeNode.Fqn { Name: "kotlin.Any", Args: null }) continue;
            targets[index] = physicalTarget;
        }
        return targets.Any(target => target != null) ? targets : null;
    }

    static void ApplyProjectedConstructorArgumentCasts(JsonObject construction, TypeNode[] targets)
    {
        if (targets == null || construction["args"] is not JsonArray arguments
            || arguments.Count != targets.Length) return;
        for (var index = 0; index < targets.Length; index++)
            if (targets[index] is TypeNode target && arguments[index] is JsonNode argument)
                arguments[index] = new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(target),
                    ["e"] = argument.DeepClone(),
                };
    }

    // A captured construction such as Wrapper(source: Source<*>) denotes Wrapper<Capture>, not Wrapper<object>.
    // The CLR cannot name that capture in a TypeSpec. Keep overload selection compile-time exact via the authored
    // constructor descriptor, then let the runtime close only the hidden owner slots from the generic witnesses on
    // the argument objects. The result immediately returns to the ordinary existential carrier; ilemit sees one
    // explicit helper call and performs no generic inference.
    static void LowerProjectedConstruction(JsonObject construction,
        IReadOnlyDictionary<string, Owner> owners, IReadOnlyDictionary<string, JsonObject> defs,
        ReferenceMetadataIndex refs)
    {
        if (Str(construction["k"]) != "new"
            || TypeJson.Read(construction["type"]) is not TypeNode.Fqn { Args: { Length: > 0 } } owner
            || !owner.Args.Any(ContainsExistentialProjection)
            || construction["memberSignature"] is not JsonArray declarationSignature
            || construction["args"] is not JsonArray arguments
            || declarationSignature.Count != arguments.Count) return;
        var signature = declarationSignature.Select(TypeJson.Read).ToArray();
        if (signature.Any(type => type == null)) return;
        if (signature.Any(ContainsManagedReference))
            throw new NotSupportedException(
                $"bir2cir: projected constructor `{owner.Name}` has a ref/out or pointer parameter; "
                + "the object[] reflection ABI cannot preserve managed-reference aliasing");

        var localNames = defs.Keys.ToHashSet(StringComparer.Ordinal);
        var parameterKeys = new JsonArray(signature.Select(type => (JsonNode)new JsonObject
        {
            ["k"] = "const",
            ["type"] = TypeJson.Write(new TypeNode.Fqn("kotlin.String")),
            ["value"] = ProjectedConstructorTypeKey(
                RewriteType(type, owners, refs), refs, localNames),
        }).ToArray());
        var fallbacks = new JsonArray(owner.Args.Select(argument => (JsonNode)new JsonObject
        {
            ["k"] = "classRef",
            ["type"] = TypeJson.Write(ProjectedConstructionFallback(argument, refs, localNames)),
        }).ToArray());
        var runtimeArguments = new JsonArray(arguments.Select(argument => argument?.DeepClone()).ToArray());
        var any = new TypeNode.Fqn("kotlin.Any");
        var anyN = new TypeNode.Nullable(any);
        var systemType = new TypeNode.Fqn("System.Type");
        JsonNode invocation = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Write(new TypeNode.Fqn(
                "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt")),
            ["method"] = "starProjectionConstruct",
            ["sig"] = new JsonArray(
                TypeJson.Write(systemType),
                TypeJson.Write(new TypeNode.Array(new TypeNode.Fqn("kotlin.String"))),
                TypeJson.Write(new TypeNode.Array(systemType)),
                TypeJson.Write(new TypeNode.Array(anyN))),
            ["ret"] = TypeJson.Write(anyN),
            ["args"] = new JsonArray(
                new JsonObject
                {
                    ["k"] = "classRef",
                    ["type"] = TypeJson.Write(new TypeNode.Fqn(owner.Name)),
                },
                new JsonObject
                {
                    ["k"] = "newArray", ["elem"] = TypeJson.Write(new TypeNode.Fqn("kotlin.String")),
                    ["elems"] = parameterKeys,
                },
                new JsonObject
                {
                    ["k"] = "newArray", ["elem"] = TypeJson.Write(systemType), ["elems"] = fallbacks,
                },
                new JsonObject
                {
                    ["k"] = "newArray", ["elem"] = TypeJson.Write(anyN), ["elems"] = runtimeArguments,
                }),
        };
        var projectedResult = RewriteType(owner, owners, refs);
        if (!IsObjectish(projectedResult)) invocation = new JsonObject
        {
            ["k"] = "cast", ["type"] = TypeJson.Write(projectedResult), ["e"] = invocation,
        };
        foreach (var key in construction.Select(pair => pair.Key).ToList()) construction.Remove(key);
        foreach (var pair in invocation.AsObject().ToList())
        {
            invocation.AsObject().Remove(pair.Key);
            construction[pair.Key] = pair.Value;
        }
        ForeignStarProjectionBinding.RequireRuntimeFallback();
    }

    static bool ContainsManagedReference(TypeNode type) => type switch
    {
        TypeNode.ByRef or TypeNode.Ptr => true,
        TypeNode.Mod modifier => ContainsManagedReference(modifier.M)
            || ContainsManagedReference(modifier.Of),
        TypeNode.Projection projection => ContainsManagedReference(projection.Of),
        TypeNode.Nullable nullable => ContainsManagedReference(nullable.Of),
        TypeNode.Oblivious oblivious => ContainsManagedReference(oblivious.Of),
        TypeNode.Array array => ContainsManagedReference(array.Elem),
        TypeNode.Fqn { Args: { } arguments } => arguments.Any(ContainsManagedReference),
        TypeNode.Fn function => ContainsManagedReference(function.Ret)
            || function.Params.Any(ContainsManagedReference)
            || function.Recv != null && ContainsManagedReference(function.Recv)
            || function.Ctx?.Any(ContainsManagedReference) == true,
        _ => false,
    };

    static TypeNode ProjectedConstructionFallback(TypeNode argument, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localNames)
    {
        argument = argument switch
        {
            TypeNode.Projection { Variance: "out" } projection => projection.Of,
            TypeNode.Projection => new TypeNode.Fqn("kotlin.Any"),
            TypeNode.Star => new TypeNode.Fqn("kotlin.Any"),
            _ => argument,
        };
        while (argument is TypeNode.Nullable nullable) argument = nullable.Of;
        while (argument is TypeNode.Oblivious oblivious) argument = oblivious.Of;
        if (ContainsExistentialProjection(argument)) argument = new TypeNode.Fqn("kotlin.Any");
        var physical = BirTypeLowering.LowerPhysicalType(argument, refs.Aliases, refs.IsValueType,
            refs.PhysicalTypeNames, typeArg: true, localTypeNames: localNames);
        return physical is TypeNode.Fn function
            ? BirTypeLowering.DelegateFqnOf(function)
                ?? throw new InvalidOperationException("projected constructor fallback has no CLR delegate family")
            : physical;
    }

    static string ProjectedConstructorTypeKey(TypeNode type, ReferenceMetadataIndex refs,
        IReadOnlySet<string> localNames)
    {
        while (type is TypeNode.Projection projection) type = projection.Of;
        var physical = BirTypeLowering.LowerPhysicalType(type, refs.Aliases, refs.IsValueType,
            refs.PhysicalTypeNames, typeArg: false, localTypeNames: localNames);
        if (physical is TypeNode.Fn function)
            physical = BirTypeLowering.DelegateFqnOf(function)
                ?? throw new InvalidOperationException("projected constructor parameter has no CLR delegate family");
        return physical switch
        {
            TypeNode.Tv variable => (variable.Scope == "method" ? "m" : "t") + variable.I,
            TypeNode.ByRef byRef => "r[" + ProjectedConstructorTypeKey(byRef.Of, refs, localNames) + "]",
            TypeNode.Array array => "a" + (array.SzArray ? "s" : "m") + array.Rank + "["
                + ProjectedConstructorTypeKey(array.Elem, refs, localNames) + "]",
            TypeNode.Fqn { Args: { } arguments } fqn => "g{" + NormalizeProjectedConstructorTypeName(fqn.Name)
                + "}<" + string.Join(",", arguments.Select(argument =>
                    ProjectedConstructorTypeKey(argument, refs, localNames))) + ">",
            TypeNode.Fqn fqn => "n{" + NormalizeProjectedConstructorTypeName(fqn.Name) + "}",
            // LowerPhysicalType retains nullable VALUE types as the semantic nullable shell. Reflection observes the
            // corresponding System.Nullable<T> generic construction, so the structural descriptor must spell that
            // physical shell rather than collide with the non-nullable T overload.
            TypeNode.Nullable nullable => "g{System.Nullable}<"
                + ProjectedConstructorTypeKey(nullable.Of, refs, localNames) + ">",
            TypeNode.Oblivious oblivious => ProjectedConstructorTypeKey(oblivious.Of, refs, localNames),
            TypeNode.Mod modifier => ProjectedConstructorTypeKey(modifier.Of, refs, localNames),
            _ => throw new NotSupportedException(
                $"bir2cir: projected constructor parameter type `{SupertypeGraph.TypeKey(physical)}` "
                + "has no runtime structural key"),
        };
    }

    static string NormalizeProjectedConstructorTypeName(string name)
    {
        var result = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current == '`')
            {
                while (index + 1 < name.Length && char.IsDigit(name[index + 1])) index++;
                continue;
            }
            result.Append(current == '+' ? '.' : current);
        }
        return result.ToString();
    }

    // A call through `M` is emitted against the declaration selected from M's bound. kotc deliberately keeps the
    // receiver as the type variable and the selected owner open; close that owner from the exact projected bound
    // before existential member binding. This is declaration-graph evidence, not reconstruction from a name or CLR
    // layout, and supports indirect bounds as well as a member inherited from a projected bound's ancestor.
    static void BindProjectedConstraintMember(JsonObject call, JsonArray typeParameters, JsonArray methodParameters,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        if (Str(call["k"]) is not ("callInstance" or "newBoundDelegate")
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn selectedOwner
            || selectedOwner.Args is { Length: > 0 }
            || ExpressionType(call["recv"]) is not TypeNode.Tv receiver) return;

        var declarations = receiver.Scope == "type" ? typeParameters
            : receiver.Scope == "method" ? methodParameters : null;
        if (declarations == null || receiver.I < 0 || receiver.I >= declarations.Count) return;

        var matches = new Dictionary<string, TypeNode.Fqn>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(TypeNode type)
        {
            if (type is TypeNode.Tv tv)
            {
                var parameters = tv.Scope == "type" ? typeParameters
                    : tv.Scope == "method" ? methodParameters : null;
                var key = tv.Scope + ":" + tv.I;
                if (parameters == null || tv.I < 0 || tv.I >= parameters.Count || !seen.Add(key)
                    || parameters[tv.I] is not JsonObject parameter
                    || parameter["constraints"] is not JsonArray constraints) return;
                foreach (var constraint in constraints)
                    if (TypeJson.Read(constraint) is TypeNode constraintType) Visit(constraintType);
                return;
            }
            if (type is not TypeNode.Fqn bound) return;
            IReadOnlyList<TypeNode> arguments = SameDeclarationOwner(bound.Name, selectedOwner.Name)
                ? bound.Args
                : ProjectConstructedArguments(bound, selectedOwner.Name, defs, refs);
            if (arguments == null || !arguments.Any(ContainsExistentialProjection)) return;
            var view = new TypeNode.Fqn(selectedOwner.Name, arguments.ToArray());
            matches.TryAdd(SupertypeGraph.TypeKey(view), view);
        }

        Visit(receiver);
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"projected generic constraint gives ambiguous owner views for `{selectedOwner.Name}`");
        if (matches.Count == 1) call["ownerType"] = TypeJson.Write(matches.Values.Single());
    }

    // `Array<out G<T>>` is physically an array of G's existential carrier. A read is nevertheless known by Kotlin's
    // projection contract to have semantic upper bound G<T>. Keep the value in its carrier representation and attach
    // that semantic projection for the immediately-following member binder; casting the carrier to G<T> would be
    // false for a value that entered through covariance (Producer<Int> is not Producer<Comparable<*>> on the CLR).
    static void BindProjectedArrayRead(JsonObject read,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        if (Str(read["k"]) != "arrayGet" || Bool(read[ProjectedArrayReadKey])) return;
        TypeNode exact;
        TypeNode physical;
        if (ProjectedArrayElement(read["array"]) is TypeNode flowedProjection
            && ExpressionType(read["array"]) is TypeNode.Array flowedArray)
        {
            exact = flowedProjection;
            physical = flowedArray.Elem;
        }
        else if (ExpressionType(read["array"]) is TypeNode.Array
                 { Elem: TypeNode.Projection projection })
        {
            exact = RewriteType(projection.Of, owners, refs);
            physical = RewriteType(projection, owners, refs);
        }
        else return;
        if (exact.Equals(physical) || !IsExistentialPhysicalCarrier(physical, owners, refs)) return;

        var inner = read.DeepClone().AsObject();
        inner[ProjectedArrayReadKey] = true;
        foreach (var key in read.Select(pair => pair.Key).ToList()) read.Remove(key);
        read["k"] = "cast";
        read["type"] = TypeJson.Write(physical);
        read["e"] = inner;
        read[ExistentialResultProjectionKey] = TypeJson.Write(exact);
        read["_exactBridgeCast"] = true;
    }

    // A generic array helper must be instantiated with the array's physical element, not the semantic G<T> hidden
    // behind `out`. Re-close both result-independent helpers and result-dependent shapes over the carrier; the latter
    // retain their Kotlin result as a pass-local projection fact so subsequent reads and member calls bind to the
    // existential slot without inventing an invalid CLR conversion back to G<T>.
    static void BindProjectedArrayGenericCall(JsonObject call,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        if (Str(call["k"]) is not ("callStatic" or "callInstance") || call["sig"] is not JsonArray signature
            || call["args"] is not JsonArray arguments || call["typeArgs"] is not JsonArray typeArguments
            || signature.Count != arguments.Count)
            return;

        TypeNode semanticResult = null;
        TypeNode physicalResult = null;
        for (var parameterIndex = 0; parameterIndex < signature.Count; parameterIndex++)
        {
            if (TypeJson.Read(signature[parameterIndex]) is not TypeNode.Array signatureArray)
                continue;

            var variable = signatureArray.Elem switch
            {
                TypeNode.Projection { Of: TypeNode.Tv { Scope: "method" } projectedVariable } =>
                    projectedVariable,
                TypeNode.Tv { Scope: "method" } directVariable => directVariable,
                _ => null,
            };
            if (variable == null || variable.I < 0 || variable.I >= typeArguments.Count) continue;

            var exact = TypeJson.Read(typeArguments[variable.I]);
            TypeNode projectedExact;
            TypeNode physical;
            if (ProjectedArrayElement(arguments[parameterIndex]) is TypeNode flowedProjection
                && ExpressionType(arguments[parameterIndex]) is TypeNode.Array flowedArray)
            {
                projectedExact = StripSourceNullability(flowedProjection);
                physical = flowedArray.Elem;
            }
            else if (ExpressionType(arguments[parameterIndex]) is TypeNode.Array
                     { Elem: TypeNode.Projection argumentProjection })
            {
                projectedExact = StripSourceNullability(argumentProjection.Of);
                physical = RewriteType(argumentProjection, owners, refs);
            }
            else continue;
            if (exact == null || projectedExact == null
                || SupertypeGraph.TypeKey(exact) != SupertypeGraph.TypeKey(projectedExact))
                continue;
            if (!IsExistentialPhysicalCarrier(physical, owners, refs)) continue;

            typeArguments[variable.I] = TypeJson.Write(physical);
            semanticResult ??= TypeJson.Read(call["ret"]);
            var rewrittenResult = ReplaceEquivalentType(semanticResult, exact, physical);
            if (rewrittenResult != semanticResult) physicalResult = rewrittenResult;
        }
        if (semanticResult != null && physicalResult != null)
        {
            if (physicalResult is TypeNode.Array { Elem: var physicalElement }
                && semanticResult is TypeNode.Array semanticArray
                && IsExistentialPhysicalCarrier(physicalElement, owners, refs))
            {
                call["ret"] = TypeJson.Write(physicalResult);
                if (call["dynRet"] != null) call["dynRet"] = TypeJson.Write(physicalResult);
                if (call["sty"] != null) call["sty"] = TypeJson.Write(physicalResult);
                call[ExistentialArrayElementProjectionKey] = TypeJson.Write(
                    semanticArray.Elem is TypeNode.Projection projection
                        ? projection.Of : semanticArray.Elem);
            }
            else AlignExistentialResult(call, physicalResult, semanticResult);
        }
    }

    static TypeNode ReplaceEquivalentType(TypeNode type, TypeNode expected, TypeNode replacement)
    {
        if (type == null) return null;
        if (SupertypeGraph.TypeKey(type) == SupertypeGraph.TypeKey(expected)) return replacement;
        return type switch
        {
            TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
                args.Select(argument => ReplaceEquivalentType(argument, expected, replacement)).ToArray()),
            TypeNode.Nullable nullable => new TypeNode.Nullable(
                ReplaceEquivalentType(nullable.Of, expected, replacement)),
            TypeNode.Oblivious oblivious => new TypeNode.Oblivious(
                ReplaceEquivalentType(oblivious.Of, expected, replacement)),
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
                ReplaceEquivalentType(projection.Of, expected, replacement)),
            TypeNode.Array array => new TypeNode.Array(
                ReplaceEquivalentType(array.Elem, expected, replacement), array.Rank, array.SzArray),
            TypeNode.ByRef byRef => new TypeNode.ByRef(
                ReplaceEquivalentType(byRef.Of, expected, replacement)),
            TypeNode.Ptr pointer => new TypeNode.Ptr(
                ReplaceEquivalentType(pointer.Of, expected, replacement)),
            TypeNode.Mod modifier => new TypeNode.Mod(modifier.Req,
                ReplaceEquivalentType(modifier.M, expected, replacement),
                ReplaceEquivalentType(modifier.Of, expected, replacement)),
            TypeNode.Fn function => new TypeNode.Fn(function.Suspend,
                ReplaceEquivalentType(function.Ret, expected, replacement),
                function.Params.Select(parameter => ReplaceEquivalentType(parameter, expected, replacement)).ToArray(),
                function.Recv == null ? null : ReplaceEquivalentType(function.Recv, expected, replacement),
                function.Clr,
                function.Ctx?.Select(context => ReplaceEquivalentType(context, expected, replacement)).ToArray()),
            _ => type,
        };
    }

    static bool ContainsEquivalentType(TypeNode type, TypeNode expected)
    {
        if (type == null) return false;
        if (SupertypeGraph.TypeKey(type) == SupertypeGraph.TypeKey(expected)) return true;
        return type switch
        {
            TypeNode.Fqn { Args: { } args } => args.Any(argument => ContainsEquivalentType(argument, expected)),
            TypeNode.Nullable nullable => ContainsEquivalentType(nullable.Of, expected),
            TypeNode.Oblivious oblivious => ContainsEquivalentType(oblivious.Of, expected),
            TypeNode.Projection projection => ContainsEquivalentType(projection.Of, expected),
            TypeNode.Array array => ContainsEquivalentType(array.Elem, expected),
            TypeNode.ByRef byRef => ContainsEquivalentType(byRef.Of, expected),
            TypeNode.Ptr pointer => ContainsEquivalentType(pointer.Of, expected),
            TypeNode.Fn function => ContainsEquivalentType(function.Ret, expected)
                || function.Params.Any(parameter => ContainsEquivalentType(parameter, expected))
                || function.Recv != null && ContainsEquivalentType(function.Recv, expected)
                || function.Ctx?.Any(context => ContainsEquivalentType(context, expected)) == true,
            _ => false,
        };
    }

    // A default-expression carrier or compiler-generated equality body authored inside a generic class may contain a
    // direct read of that class's backing field. Once its dispatch receiver is G<*>, the physical receiver is G$star:
    // an interface which deliberately owns no fields. Reuse the already-projected property-getter slot only when the
    // local source getter proves that this is an exact representation change, or kotc explicitly identifies a
    // generated data-class copy/equality or value-class equality field read. Those frontend facts also cover a final
    // generated getter that is virtual only because it fills an interface slot and a referenced declaration whose
    // body is intentionally absent. Custom or genuinely overridable accessors are never guessed into the field access.
    static void BindStarFieldThroughCanonicalGetter(JsonObject field,
        IReadOnlyDictionary<string, Owner> owners, IReadOnlyDictionary<string, JsonObject> defs,
        ReferenceMetadataIndex refs)
    {
        var dataClassCopyDefault = Bool(field["dataClassCopyDefault"]);
        field.Remove("dataClassCopyDefault");
        var generatedEqualsFieldRead = Bool(field["generatedEqualsFieldRead"]);
        field.Remove("generatedEqualsFieldRead");
        if (Str(field["k"]) != "field"
            || Str(field["name"]) is not string fieldName
            || TypeJson.Read(field["ownerType"]) is not TypeNode.Fqn { Args: { } } fieldOwner)
            return;

        (Owner Owner, JsonObject Method)? canonicalGetter = null;
        if (owners.TryGetValue(fieldOwner.Name, out var start) && start.Needed)
            canonicalGetter = FindDeclaringOwner(start, fieldName, "get", 0, 0,
                Array.Empty<TypeNode>(), null, owners);
        var localCanonical = canonicalGetter is { } found
            && found.Owner.Name == fieldOwner.Name
            && IsCanonicalFieldGetter(found.Method, fieldOwner.Name, fieldName);
        var generatedEqualsCanonical = generatedEqualsFieldRead && canonicalGetter is { } equalsGetter
            && equalsGetter.Owner.Name == fieldOwner.Name
            && IsCanonicalFieldGetter(equalsGetter.Method, fieldOwner.Name, fieldName, allowVirtual: true);
        // A referenced data class has no declaration body in this compilation. kotc nevertheless knows the exact
        // generated-copy contract from the restored data-class declaration and marks only that reconstructed property
        // default. The referenced MethodDef/property carriers then remain the authority for its physical getter slot.
        if (!localCanonical && !dataClassCopyDefault && !generatedEqualsCanonical) return;

        var resultType = field["memberType"] ?? field["sty"] ?? canonicalGetter?.Method["ret"];
        if (field["recv"] == null || resultType == null) return;
        var call = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(fieldOwner),
            ["method"] = fieldName,
            ["recv"] = field["recv"].DeepClone(),
            ["args"] = new JsonArray(),
            ["sig"] = new JsonArray(),
            ["ret"] = resultType.DeepClone(),
            ["virtual"] = false,
        };
        if (field["sty"] != null) call["sty"] = field["sty"].DeepClone();
        if (field["pos"] != null) call["pos"] = field["pos"].DeepClone();
        // The frontend fact identifies a compiler-generated equality peer read. Its local is the successful
        // raw-classifier cast of the peer, whose physical result is this owner's already-selected existential carrier;
        // stamp only the tentative getter receiver and leave the field untouched if slot binding still fails.
        if (call["recv"] is JsonObject { } receiver
            && generatedEqualsFieldRead && Str(receiver["k"]) == "local"
            && owners.TryGetValue(fieldOwner.Name, out var equalityOwner) && equalityOwner.Needed)
            receiver["sty"] = TypeJson.Write(new TypeNode.Fqn(equalityOwner.ErasedName));
        KotlinPropertyAccessors.PreserveCallIdentity(call, fieldName, "get");
        BindInheritedStarMember(call, owners, defs, refs, alignResult: false);

        if (TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn boundOwner
            || !owners.Values.Any(owner => owner.ErasedName == boundOwner.Name)
                && !refs.IsExistentialPhysicalOwner(boundOwner.Name))
            return;
        if (canonicalGetter is { } selectedGetter
            && TypeJson.Read(selectedGetter.Method["ret"]) is TypeNode getterResult)
        {
            var physicalResult = TypeJson.Write(EraseOwnerTv(getterResult, owners, refs));
            call["ret"] = physicalResult;
            if (field["sty"] != null) call["sty"] = physicalResult.DeepClone();
        }
        foreach (var key in field.Select(pair => pair.Key).ToList()) field.Remove(key);
        foreach (var pair in call) field[pair.Key] = pair.Value?.DeepClone();
    }

    static bool IsCanonicalFieldGetter(JsonObject getter, string owner, string fieldName, bool allowVirtual = false)
    {
        if ((!allowVirtual && Bool(getter["virtual"])) || getter["body"] is not JsonArray { Count: 1 } body
            || body[0] is not JsonObject statement || Str(statement["k"]) != "return"
            || CanonicalGetterField(statement["value"]) is not JsonObject value
            || Str(value["name"]) != fieldName
            || TypeJson.Read(value["ownerType"]) is not TypeNode.Fqn fieldOwner
            || fieldOwner.Name != owner
            || value["recv"] is not JsonObject receiver || Str(receiver["k"]) != "this")
            return false;
        return true;
    }

    static JsonObject CanonicalGetterField(JsonNode value)
    {
        if (value is JsonObject direct && Str(direct["k"]) == "field") return direct;
        // A non-null Kotlin return wraps the same field in `{ var __nn = field; __nn != null ? __nn : throw }`.
        // Recognize that exact validation wrapper; arbitrary value blocks remain ineligible.
        if (value is not JsonObject block || Str(block["k"]) != "valueBlock"
            || block["stmts"] is not JsonArray { Count: 1 } statements
            || statements[0] is not JsonObject variable || Str(variable["k"]) != "var"
            || Str(variable["name"]) is not string name
            || variable["init"] is not JsonObject field || Str(field["k"]) != "field"
            || block["result"] is not JsonObject conditional || Str(conditional["k"]) != "cond"
            || conditional["cond"] is not JsonObject not || Str(not["k"]) != "unaryOp"
            || Str(not["op"]) != "!" || not["e"] is not JsonObject equals
            || Str(equals["k"]) != "objEq"
            || equals["lhs"] is not JsonObject lhs || Str(lhs["k"]) != "local" || Str(lhs["name"]) != name
            || equals["rhs"] is not JsonObject rhs || Str(rhs["k"]) != "const" || rhs["value"] != null
            || conditional["then"] is not JsonObject then || Str(then["k"]) != "local"
            || Str(then["name"]) != name
            || conditional["else"] is not JsonObject otherwise || Str(otherwise["k"]) != "throwExpr")
            return null;
        return field;
    }

    static void BindStarInnerConstruction(JsonObject construction,
        IReadOnlyDictionary<string, Owner> owners, IReadOnlyDictionary<string, JsonObject> defs,
        ReferenceMetadataIndex refs,
        IReadOnlySet<int> existentialTypeParameters,
        IReadOnlySet<int> existentialMethodParameters)
    {
        if (Str(construction["k"]) != "new"
            || TypeJson.Read(construction["type"]) is not TypeNode.Fqn innerType
            || construction["args"] is not JsonArray arguments || arguments.Count == 0
            || (construction["memberSignature"] as JsonArray
                ?? construction["argTypes"] as JsonArray) is not JsonArray signature || signature.Count == 0
            || TypeJson.Read(signature[0]) is not TypeNode.Fqn selectedOuter
            || ExpressionType(arguments[0]) is not TypeNode suppliedOuter
            || !ContainsExistential(suppliedOuter,
                existentialTypeParameters, existentialMethodParameters)) return;

        // memberSignature remains declaration-relative for an inner classifier's own type parameters (for example
        // GenericEntry<E>'s value slot is type#1), while argTypes is the frontend-selected constructor descriptor
        // closed at this use (String).  The hidden outer slot was normalized by kotc #555 and is exact in both.
        var isLocalInner = defs.TryGetValue(innerType.Name, out var innerDef) && IsInner(innerDef);
        var isReferencedInner = refs.TryInnerSemanticOwner(innerType.Name, out var referencedOuter);
        // A regular constructor may simply take a star-projected value as its first source parameter. Only a local
        // inner declaration or trusted referenced inner-owner fact proves that this slot is the hidden outer.
        if (!isLocalInner && !isReferencedInner) return;
        var selectedDescriptor = construction["argTypes"] as JsonArray ?? signature;
        var authoredSignature = selectedDescriptor.Select(TypeJson.Read).ToArray();
        if (authoredSignature.Any(type => type == null))
            throw new InvalidOperationException(
                $"bir2cir: star-projected inner construction '{innerType.Name}' has a malformed selected constructor descriptor");
        string physicalOwner = null;
        string physicalMethod = null;
        TypeNode[] physicalParameters = null;
        TypeNode physicalResult = null;
        TypeNode[] physicalTypeArguments = null;

        if (isLocalInner)
        {
            var declaredOuter = Str(innerDef["semanticOwner"]);
            if (declaredOuter == null
                || !LocalDeclarationReaches(selectedOuter.Name, declaredOuter, defs)
                || !owners.TryGetValue(declaredOuter, out var outer)
                || !owners.TryGetValue(innerType.Name, out var inner)
                || !outer.Needed || !inner.Needed)
                throw new InvalidOperationException(
                    $"bir2cir: local inner construction '{innerType.Name}' has no exact existential owner '{selectedOuter.Name}<*>'");
            var constructors = (innerDef["ctors"] as JsonArray)?.OfType<JsonObject>().ToList()
                ?? new List<JsonObject>();
            var matches = constructors.Select((ctor, ordinal) => (ctor, ordinal))
                .Where(candidate => ConstructorDescribesUse(
                    candidate.ctor, innerType.Args ?? Array.Empty<TypeNode>(), authoredSignature))
                .ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"bir2cir: star-projected inner construction '{inner.Name}' resolves to {matches.Count} exact local constructors");
            var generatedFactory = InnerConstructorFactory(outer, inner, matches[0].ctor,
                matches[0].ordinal, owners, refs);
            var factory = generatedFactory.Slot;
            physicalOwner = outer.ErasedName;
            physicalMethod = Str(factory["name"]);
            physicalParameters = (factory["params"] as JsonArray)?.OfType<JsonObject>()
                .Select(parameter => TypeJson.Read(parameter["type"])).ToArray() ?? Array.Empty<TypeNode>();
            physicalResult = TypeJson.Read(factory["ret"]);
            physicalTypeArguments = InnerFactoryCallTypeArguments(inner, innerType.Args ?? Array.Empty<TypeNode>());
        }
        else
        {
            if (!refs.TryExistentialInnerConstructorFactory(
                     referencedOuter, innerType, authoredSignature.Skip(1).ToArray(),
                     out physicalOwner, out physicalMethod, out physicalParameters, out physicalResult,
                     out physicalTypeArguments))
                throw new InvalidOperationException(
                    $"bir2cir: referenced inner construction '{innerType.Name}' through star-projected outer " +
                    $"'{referencedOuter}<*>' has no exact existential constructor factory");
        }

        var replacement = new JsonObject
        {
            ["k"] = "callInstance",
            ["ownerType"] = TypeJson.Write(new TypeNode.Fqn(physicalOwner)),
            ["virtual"] = true,
            ["recv"] = arguments[0]?.DeepClone(),
            ["method"] = physicalMethod,
            ["sig"] = new JsonArray(physicalParameters.Select(TypeJson.Write).ToArray()),
            ["ret"] = TypeJson.Write(physicalResult),
            ["args"] = new JsonArray(arguments.Skip(1).Select(argument => argument?.DeepClone()).ToArray()),
        };
        if (physicalTypeArguments is { Length: > 0 })
            replacement["typeArgs"] = new JsonArray(physicalTypeArguments.Select(TypeJson.Write).ToArray());
        if (construction["pos"] != null) replacement["pos"] = construction["pos"]?.DeepClone();
        construction.Clear();
        foreach (var pair in replacement) construction[pair.Key] = pair.Value?.DeepClone();
    }

    static bool LocalDeclarationReaches(string from, string target,
        IReadOnlyDictionary<string, JsonObject> defs)
    {
        var pending = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(from);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!seen.Add(name)) continue;
            if (name == target) return true;
            if (!defs.TryGetValue(name, out var definition)) continue;
            void Add(JsonNode node)
            {
                if (TypeJson.Read(node) is TypeNode.Fqn parent) pending.Enqueue(parent.Name);
            }
            Add(definition["base"]);
            if (definition["interfaces"] is JsonArray interfaces)
                foreach (var parent in interfaces) Add(parent);
        }
        return false;
    }

    static TypeNode[] InnerFactoryCallTypeArguments(Owner inner, IReadOnlyList<TypeNode> innerArguments)
    {
        var capturedCount = InnerCapturedCount(inner.Def);
        if (innerArguments.Count != inner.Arity)
            throw new InvalidOperationException(
                $"inner construction '{inner.Name}' has {innerArguments.Count} type arguments; expected {inner.Arity}");
        var directDependent = DirectOwnerDependentInnerParameters(inner, capturedCount);
        foreach (var index in directDependent)
            if (innerArguments[index] is not TypeNode.Fqn { Args: null, Name: "kotlin.Nothing" })
                throw new InvalidOperationException(
                    $"inner construction '{inner.Name}' through a star-projected outer must use kotlin.Nothing " +
                    $"for owner-dependent type argument {index - capturedCount}");
        return Enumerable.Range(capturedCount, inner.Arity - capturedCount)
            .Where(index => !directDependent.Contains(index)).Select(index => innerArguments[index]).ToArray();
    }

    static IReadOnlySet<int> ExistentialTypeParameters(JsonArray declarations, string scope)
    {
        if (declarations == null || declarations.Count == 0) return null;
        var result = new HashSet<int>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (declaration, index) in declarations.Select((parameter, i) => (parameter, i)))
            {
                if (declaration is not JsonObject parameter) continue;
                if (result.Contains(index) || parameter["constraints"] is not JsonArray constraints) continue;
                if (constraints.Any(constraint => TypeJson.Read(constraint) is TypeNode type
                        && ContainsExistential(type,
                            scope == "type" ? result : null,
                            scope == "method" ? result : null)))
                    changed |= result.Add(index);
            }
        }
        return result.Count == 0 ? null : result;
    }

    static bool ContainsExistential(TypeNode type,
        IReadOnlySet<int> typeParameters, IReadOnlySet<int> methodParameters) => type switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Tv { Scope: "type" } tv => typeParameters?.Contains(tv.I) == true,
        TypeNode.Tv { Scope: "method" } tv => methodParameters?.Contains(tv.I) == true,
        TypeNode.Fqn { Args: { } args } => args.Any(argument =>
            ContainsExistential(argument, typeParameters, methodParameters)),
        TypeNode.Nullable nullable => ContainsExistential(nullable.Of, typeParameters, methodParameters),
        TypeNode.Oblivious oblivious => ContainsExistential(oblivious.Of, typeParameters, methodParameters),
        TypeNode.Array array => ContainsExistential(array.Elem, typeParameters, methodParameters),
        TypeNode.ByRef byRef => ContainsExistential(byRef.Of, typeParameters, methodParameters),
        TypeNode.Ptr pointer => ContainsExistential(pointer.Of, typeParameters, methodParameters),
        TypeNode.Mod modifier => ContainsExistential(modifier.M, typeParameters, methodParameters)
            || ContainsExistential(modifier.Of, typeParameters, methodParameters),
        TypeNode.Fn function => ContainsExistential(function.Ret, typeParameters, methodParameters)
            || function.Params.Any(parameter => ContainsExistential(parameter, typeParameters, methodParameters))
            || function.Recv != null && ContainsExistential(function.Recv, typeParameters, methodParameters)
            || function.Ctx?.Any(context => ContainsExistential(context, typeParameters, methodParameters)) == true,
        _ => false,
    };

    static void NormalizeInnerFactoryLocals(JsonObject declaration,
        IReadOnlyDictionary<string, Owner> owners, IReadOnlyDictionary<string, JsonObject> defs,
        ReferenceMetadataIndex refs)
    {
        bool LogicalTypeMatchesPhysical(TypeNode logical, TypeNode physical)
        {
            if (logical is TypeNode.Array logicalArray && physical is TypeNode.Array physicalArray)
                return LogicalTypeMatchesPhysical(
                    logicalArray.Elem is TypeNode.Projection projection ? projection.Of : logicalArray.Elem,
                    physicalArray.Elem);
            var logicalCore = StripProjectionShell(logical) as TypeNode.Fqn;
            var physicalCore = StripProjectionShell(physical) as TypeNode.Fqn;
            return logicalCore != null && physicalCore != null
                && (owners.TryGetValue(logicalCore.Name, out var local)
                        && local.ErasedName == physicalCore.Name
                    || refs.TryExistentialPhysicalOwner(logicalCore.Name, out var referenced)
                        && referenced == physicalCore.Name);
        }

        bool TryCarrier(TypeNode type, out TypeNode.Fqn carrier)
        {
            var core = StripProjectionShell(type) as TypeNode.Fqn;
            if (core != null && (owners.Values.Any(owner => owner.ErasedName == core.Name)
                    || refs.IsExistentialPhysicalOwner(core.Name)))
            {
                carrier = core;
                return true;
            }
            carrier = null;
            return false;
        }

        bool TryPhysicalExistential(TypeNode type, out TypeNode physical)
        {
            if (TryCarrier(type, out var carrier))
            {
                physical = carrier;
                return true;
            }
            if (type is TypeNode.Array array && TryCarrier(array.Elem, out var elementCarrier))
            {
                physical = new TypeNode.Array(elementCarrier, array.Rank, array.SzArray);
                return true;
            }
            physical = null;
            return false;
        }

        var locals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        var projectedLocals = new Dictionary<string, TypeNode.Fqn>(StringComparer.Ordinal);
        var projectedArrayLocals = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        void Collect(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (Str(obj["k"]) == "var" && Str(obj["name"]) is string name
                        && ExpressionType(obj["init"]) is TypeNode resultType
                        && TryPhysicalExistential(resultType, out var result)
                        && TypeJson.Read(obj["type"]) is TypeNode declared
                        && LogicalTypeMatchesPhysical(declared, result))
                    {
                        locals[name] = result;
                        if (ProjectedExistentialType(obj["init"]) is { } projected)
                            projectedLocals[name] = projected;
                        if (ProjectedArrayElement(obj["init"]) is { } arrayProjection)
                            projectedArrayLocals[name] = arrayProjection;
                    }
                    foreach (var value in obj.Select(pair => pair.Value))
                        if (value != null) Collect(value);
                    break;
                case JsonArray array:
                    foreach (var value in array)
                        if (value != null) Collect(value);
                    break;
            }
        }
        void Normalize(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (Str(obj["name"]) is string name && locals.TryGetValue(name, out var carrier))
                    {
                        if (Str(obj["k"]) == "var") obj["type"] = TypeJson.Write(carrier);
                        else if (Str(obj["k"]) == "local")
                        {
                            obj["sty"] = TypeJson.Write(carrier);
                            if (projectedLocals.TryGetValue(name, out var projected))
                                obj[ExistentialResultProjectionKey] = TypeJson.Write(projected);
                            if (projectedArrayLocals.TryGetValue(name, out var arrayProjection))
                                obj[ExistentialArrayElementProjectionKey] = TypeJson.Write(arrayProjection);
                        }
                    }
                    foreach (var value in obj.Select(pair => pair.Value).ToList())
                        if (value != null) Normalize(value);
                    BindInheritedStarMember(obj, owners, defs, refs);
                    break;
                case JsonArray array:
                    foreach (var value in array)
                        if (value != null) Normalize(value);
                    break;
            }
        }
        while (true)
        {
            var priorLocals = locals.Count;
            var priorProjections = projectedLocals.Count;
            Collect(declaration["body"]);
            if (locals.Count == 0) return;
            Normalize(declaration["body"]);
            if (locals.Count == priorLocals && projectedLocals.Count == priorProjections) break;
        }

        TypeNode ResultCarrier(JsonNode expression) => expression switch
        {
            JsonObject obj when ExpressionType(obj) is TypeNode result
                && TryPhysicalExistential(result, out var physical) => physical,
            JsonObject obj when Str(obj["k"]) == "local" && Str(obj["name"]) is string name
                && locals.TryGetValue(name, out var carrier) => carrier,
            JsonObject obj when Str(obj["k"]) == "valueBlock" => ResultCarrier(obj["result"]),
            JsonObject obj when Str(obj["k"]) == "cond" =>
                ResultCarrier(obj["then"]) is TypeNode thenCarrier
                    && (IsBottomExpression(obj["else"]) || ResultCarrier(obj["else"]) == thenCarrier)
                        ? thenCarrier
                        : ResultCarrier(obj["else"]) is TypeNode elseCarrier
                            && IsBottomExpression(obj["then"])
                                ? elseCarrier
                                : null,
            _ => null,
        };

        static bool IsBottomExpression(JsonNode expression) => expression is JsonObject bottom
            && Str(bottom["k"]) is "throwExpr";

        var returned = new List<TypeNode>();
        var hasNonCarrierReturn = false;
        void CollectReturns(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (Str(obj["k"]) == "localFun") return;
                    if (Str(obj["k"]) == "return")
                    {
                        if (ResultCarrier(obj["value"]) is TypeNode carrier) returned.Add(carrier);
                        else hasNonCarrierReturn = true;
                        return;
                    }
                    foreach (var value in obj.Select(pair => pair.Value))
                        if (value != null) CollectReturns(value);
                    break;
                case JsonArray array:
                    foreach (var value in array)
                        if (value != null) CollectReturns(value);
                    break;
            }
        }
        CollectReturns(declaration["body"]);
        var distinctReturns = returned.Distinct().ToArray();
        if (!hasNonCarrierReturn && distinctReturns.Length == 1
            && TypeJson.Read(declaration["ret"]) is TypeNode originalReturn
            && originalReturn != distinctReturns[0]
            && LogicalTypeMatchesPhysical(originalReturn, distinctReturns[0]))
        {
            declaration["retKotlinType"] ??= TypeNode.ToJson(originalReturn);
            declaration["ret"] = TypeJson.Write(distinctReturns[0]);
            declaration[NormalizedInnerFactoryReturnKey] = true;
        }
    }

    static bool ConstructorDescribesUse(JsonObject constructor, IReadOnlyList<TypeNode> innerArguments,
        IReadOnlyList<TypeNode> authoredSignature)
    {
        var parameters = (constructor["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(parameter => TypeJson.Read(parameter["type"])).ToArray() ?? Array.Empty<TypeNode>();
        if (parameters.Length != authoredSignature.Count) return false;
        // Slot zero is the hidden outer value. A Kotlin-selected inherited inner constructor legitimately supplies a
        // derived receiver there; the declaration reachability check above proves that relation. Constructor overload
        // identity is determined by the remaining source parameters and remains exact.
        return parameters.Select((parameter, index) => (parameter, index)).Skip(1).Select(pair =>
                CloseInnerConstructorType(pair.parameter, innerArguments).Equals(authoredSignature[pair.index]))
            .All(equal => equal);
    }

    internal static TypeNode CloseInnerConstructorType(TypeNode type, IReadOnlyList<TypeNode> innerArguments) =>
        type switch
        {
            TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < innerArguments.Count
                => innerArguments[tv.I],
            TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
                CloseInnerConstructorType(projection.Of, innerArguments)),
            TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
                args.Select(argument => CloseInnerConstructorType(argument, innerArguments)).ToArray()),
            TypeNode.Nullable n => new TypeNode.Nullable(CloseInnerConstructorType(n.Of, innerArguments)),
            TypeNode.Oblivious o => new TypeNode.Oblivious(CloseInnerConstructorType(o.Of, innerArguments)),
            TypeNode.Array a => new TypeNode.Array(CloseInnerConstructorType(a.Elem, innerArguments)),
            TypeNode.ByRef b => new TypeNode.ByRef(CloseInnerConstructorType(b.Of, innerArguments)),
            TypeNode.Ptr p => new TypeNode.Ptr(CloseInnerConstructorType(p.Of, innerArguments)),
            TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
                CloseInnerConstructorType(fn.Ret, innerArguments),
                fn.Params.Select(parameter => CloseInnerConstructorType(parameter, innerArguments)).ToArray(),
                fn.Recv == null ? null : CloseInnerConstructorType(fn.Recv, innerArguments), fn.Clr,
                fn.Ctx?.Select(context => CloseInnerConstructorType(context, innerArguments)).ToArray()),
            _ => type,
        };

    static TypeNode ExpressionType(JsonNode expression)
    {
        if (expression is not JsonObject obj) return null;
        if (Str(obj["k"]) is "newArray" or "newArrayInit" or "newArraySized" or "spreadConcat"
            && TypeJson.Read(obj["elem"]) is TypeNode element)
            return new TypeNode.Array(element);
        return TypeJson.Read(obj["sty"]) ?? TypeJson.Read(obj["ret"]) ?? TypeJson.Read(obj["type"]);
    }

    // The frontend can retain an explicit cast for a source-level generic upcast even though the source construction
    // already reaches that exact target construction. The relation is authoritative only when the current
    // declaration graph proves every target argument in the source frame; names or erased physical layouts are not
    // evidence. Explicit stars deliberately stay on the existential path.
    static bool TryExistentialCarrier(string semanticOwner, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, out string physicalOwner)
    {
        if (owners.TryGetValue(semanticOwner, out var local) && local.Needed)
        {
            physicalOwner = local.ErasedName;
            return true;
        }
        return refs.TryExistentialPhysicalOwner(semanticOwner, out physicalOwner);
    }

    static TypeNode StripSourceNullability(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => StripSourceNullability(nullable.Of),
        TypeNode.Oblivious oblivious => StripSourceNullability(oblivious.Of),
        _ => type,
    };

    static bool IsProvenConstructedUpcast(JsonObject cast, TypeNode.Fqn target,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs,
        TypeNode.Fqn currentThisType)
    {
        var operand = cast["e"] as JsonObject;
        var sourceType = Str(operand?["k"]) switch
        {
            "this" => currentThisType,
            "cast" or "isInst" => TypeJson.Read(operand?["type"]),
            "callInstance" or "callStatic" =>
                TypeJson.Read(operand?["dynRet"]) ?? TypeJson.Read(operand?["sty"]),
            _ => TypeJson.Read(operand?["sty"]) ?? TypeJson.Read(operand?["type"]),
        };
        if (target.Args is not { } targetArguments || targetArguments.Any(ContainsExplicitExistential)
            || StripSourceNullability(sourceType)
                is not TypeNode.Fqn { Args: { } sourceArguments } source)
            return false;

        if (SameDeclarationOwner(source.Name, target.Name))
            return sourceArguments.Length == targetArguments.Length
                && sourceArguments.Select(SupertypeGraph.TypeKey)
                    .SequenceEqual(targetArguments.Select(SupertypeGraph.TypeKey));

        var projected = ProjectConstructedArguments(source, target.Name, defs, refs);
        return projected != null && projected.Count == targetArguments.Length
            && projected.Select(SupertypeGraph.TypeKey)
                .SequenceEqual(targetArguments.Select(SupertypeGraph.TypeKey));
    }

    // Project through the declarations that actually own the relation. `defs` includes lifted local/object types
    // even though they intentionally do not own a public existential ABI; referenced edges come only from trusted
    // metadata. A unique nearest construction is required, matching local slot binding's ambiguity rule.
    static IReadOnlyList<TypeNode> ProjectConstructedArguments(TypeNode.Fqn source, string targetName,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs)
    {
        var queue = new Queue<(TypeNode.Fqn Type, int Depth)>();
        queue.Enqueue((source, 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        int? matchDepth = null;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (matchDepth is int nearest && depth > nearest) break;
            var currentArguments = current.Args ?? Array.Empty<TypeNode>();
            var key = current.Name + "|" + new JsonArray(
                currentArguments.Select(TypeJson.Write).ToArray()).ToJsonString();
            if (!seen.Add(key)) continue;
            if (SameDeclarationOwner(current.Name, targetName))
            {
                matchDepth = depth;
                matches.TryAdd(key, currentArguments);
                continue;
            }

            IEnumerable<TypeNode.Fqn> Edges()
            {
                if (defs.TryGetValue(current.Name, out var definition))
                {
                    if (definition["base"] is JsonNode baseNode
                        && TypeJson.Read(baseNode) is TypeNode.Fqn baseType)
                        yield return baseType;
                    if (definition["interfaces"] is JsonArray interfaces)
                        foreach (var edge in interfaces)
                            if (TypeJson.Read(edge) is TypeNode.Fqn interfaceType)
                                yield return interfaceType;
                    yield break;
                }
                foreach (var (spec, _) in refs.ReferencedSupertypes(current)) yield return spec;
            }

            foreach (var edge in Edges())
            {
                if (SubstituteDeclarationTypeArguments(edge, currentArguments, Array.Empty<TypeNode>())
                    is TypeNode.Fqn projected)
                    queue.Enqueue((projected, depth + 1));
            }
        }

        return matches.Count == 1 ? matches.Values.Single() : null;
    }

    // Current-format referenced BIR can carry the exact CLR TypeDef spelling while reflection-derived inheritance
    // edges use the semantic Kotlin spelling. They identify the same declaration after arity/nesting normalization.
    // Keep two distinct exact CLR spellings distinct: flattened semantic names cannot disambiguate their ownership.
    static bool SameDeclarationOwner(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        var leftExact = left.Contains('`') || left.Contains('+');
        var rightExact = right.Contains('`') || right.Contains('+');
        if (leftExact && rightExact) return false;
        return string.Equals(
            ReferenceMetadataIndex.BareOwnerFqn(left).Replace('+', '.'),
            ReferenceMetadataIndex.BareOwnerFqn(right).Replace('+', '.'),
            StringComparison.Ordinal);
    }

    // A star smart-cast keeps the receiver's most-derived Kotlin type (`ComparableRange<*>.isEmpty`) even when the
    // member is declared by an ancestor (`ClosedRange<T>.isEmpty`).  Once the receiver becomes an erased interface,
    // CIR must name that exact declaring interface; ilemit is intentionally not allowed to search/infer it.
    static void BindInheritedStarMember(JsonObject call, IReadOnlyDictionary<string, Owner> owners,
        IReadOnlyDictionary<string, JsonObject> defs, ReferenceMetadataIndex refs, bool alignResult = true)
    {
        var useKind = Str(call["k"]);
        if (useKind is not ("callInstance" or "newBoundDelegate")
            || TypeJson.Read(call["ownerType"]) is not TypeNode.Fqn { Args: { } args } f
            || Str(call["method"]) is not string authoredMethod) return;

        void BindOwner(string physicalOwner)
        {
            var ownerType = TypeJson.Write(new TypeNode.Fqn(physicalOwner));
            call["ownerType"] = ownerType;
            // A bound delegate carries both the receiver seam and the declaring MethodDef owner. Once the selected
            // Kotlin member is projected onto an existential slot, both facts must name that same physical carrier.
            if (useKind == "newBoundDelegate") call["calleeOwner"] = ownerType.DeepClone();
        }

        // `ret` is the selected declaration's return vocabulary; `dynRet`/`sty` are already instantiated caller facts.
        // Close only the former from the declaration we actually selected. Rewriting the caller facts by positional
        // owner indices corrupts an unrelated caller-owned `type#i` when this call appears in a generic function.
        void CloseDeclarationResult(TypeNode declarationResult)
        {
            var currentResult = TypeJson.Read(call["ret"]);
            // An instantiated frontend result can be a bound approximation (`T` on `G<*>` -> `Comparable<*>`) or a
            // caller-owned generic. Only the unchanged declaration token proves this slot still needs closing.
            if (declarationResult == null || currentResult == null || !currentResult.Equals(declarationResult)) return;
            var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
                ?? Array.Empty<TypeNode>();
            call["ret"] = TypeJson.Write(CloseDeclarationType(declarationResult, args, methodArgs));
        }

        // Binding selects a real MethodDef on a non-generic existential owner. Its result may therefore be an
        // existential carrier even when the selected method itself owns generic parameters,
        // though the Kotlin expression retains a caller-instantiated constructed type. State both facts explicitly:
        // the inner call has the MethodDef's exact physical result; the outer projection is either a checked concrete
        // cast or a carrier identity that preserves the captured star for subsequent binding. ilemit must never pretend
        // the selected slot itself returns the constructed type.
        owners.TryGetValue(f.Name, out var start);
        var projectedReceiver = ProjectedExistentialType(call["recv"]);
        var ownerArguments = projectedReceiver is { Args: { } projectedArgs }
            && (projectedReceiver.Name == f.Name
                || refs.TryExactPhysicalTypeName(projectedReceiver.Name, projectedArgs.Length, out var exactReceiver)
                    && exactReceiver == f.Name)
            ? projectedArgs
            : args;

        IReadOnlyList<TypeNode> DeclarationOwnerArguments(string declaringName)
        {
            if (declaringName == f.Name) return ownerArguments;
            return start == null ? null : ProjectOwnerArguments(start, ownerArguments, declaringName, owners);
        }

        void AlignResult(TypeNode physicalResult, TypeNode declarationResult,
            IReadOnlyList<TypeNode> declarationOwnerArguments)
        {
            // newBoundDelegate describes the target MethodDef in these fields, but the expression itself produces a
            // delegate instance. Only an invocation places the target's return value on the evaluation stack.
            if (!alignResult || useKind != "callInstance") return;
            var methodArgs = (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray()
                ?? Array.Empty<TypeNode>();
            physicalResult = SubstituteMethodTypeArguments(physicalResult, methodArgs);
            var projectedResult = declarationOwnerArguments == null
                ? null
                : SubstituteDeclarationTypeArguments(declarationResult, declarationOwnerArguments, methodArgs);
            if (projectedResult != null && ContainsExplicitExistential(projectedResult)
                && IsExistentialPhysicalCarrier(physicalResult, owners, refs))
            {
                AlignExistentialResult(call, physicalResult, projectedResult);
                return;
            }
            AlignCallResult(call, physicalResult, protectExactCast: true);
        }

        var propertyCall = KotlinPropertyAccessors.TryCallIdentity(call,
            out var sourcePropertyName, out var accessorKind);
        var sourceMember = propertyCall ? sourcePropertyName : authoredMethod;
        var declarationId = Str(call[DeclarationIdentityBinding.Key]);

        var starOwner = args.Any(IsExistentialArgument);
        var erasedSmartCast = call["recv"] is JsonObject recv && Str(recv["k"]) == "cast"
            && TypeJson.Read(recv["type"]) is TypeNode.Fqn { Args: { } castArgs } castF
            && castF.Name == f.Name
            && castArgs.Any(ContainsStarOrTypeVariable);
        var existentialReceiver = projectedReceiver != null
            || ExpressionType(call["recv"]) is TypeNode.Fqn receiverType
                && (owners.Values.Any(owner => owner.ErasedName == receiverType.Name)
                    || refs.IsExistentialPhysicalOwner(receiverType.Name));
        if (!starOwner && !erasedSmartCast && !existentialReceiver) return;

        var pc = (call["sig"] as JsonArray)?.Count
            ?? (call["argTypes"] as JsonArray)?.Count
            ?? (call["args"] as JsonArray)?.Count ?? 0;
        var ga = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var authoredSignature = ((call["sig"] ?? call["argTypes"]) as JsonArray)?
            .Select(TypeJson.Read).ToArray();
        if (authoredSignature?.Any(t => t == null) == true) authoredSignature = null;
        if (start != null && start.Needed
            && FindDeclaringOwner(start, sourceMember, accessorKind, pc, ga, authoredSignature,
                declarationId, owners) is { } found)
        {
            var (declaring, declaration) = found;
            CloseDeclarationResult(TypeJson.Read(declaration["ret"]));
            BindOwner(declaring.ErasedName);
            call["virtual"] = true; // erased owner is an interface; CIR must carry callvirt explicitly
            if (ContainsOwnerTvInSignature(declaration) || !IsPublic(declaration))
                call["method"] = StarMethodName(declaring, declaration);
            call["sig"] = ErasedPhysicalSignature(declaration, owners, refs);
            MarkPhysicalPropertyCall(call, propertyCall, sourcePropertyName, accessorKind,
                ExistentialSlotIdentity(declaration, declaring.Name));
            var selectedDeclarationResult = TypeJson.Read(declaration["ret"])
                ?? new TypeNode.Fqn("kotlin.Unit");
            AlignResult(EraseOwnerTv(selectedDeclarationResult, owners, refs), selectedDeclarationResult,
                DeclarationOwnerArguments(declaring.Name));
            return;
        }

        if (start != null && start.Needed
            && FindInheritedConcreteBaseMember(start, sourceMember, accessorKind, pc, ga,
                declarationId, owners, defs) is { } baseFound)
        {
            var (bridgeOwner, declaringName, declaration) = baseFound;
            CloseDeclarationResult(TypeJson.Read(declaration["ret"]));
            BindOwner(bridgeOwner.ErasedName);
            call["method"] = BaseStarMethodName(declaringName, defs[declaringName], declaration);
            call["sig"] = ErasedPhysicalSignature(declaration, owners, refs);
            call["virtual"] = true;
            MarkPhysicalPropertyCall(call, propertyCall, sourcePropertyName, accessorKind,
                ExistentialSlotIdentity(declaration, bridgeOwner.Name));
            var selectedDeclarationResult = TypeJson.Read(declaration["ret"])
                ?? new TypeNode.Fqn("kotlin.Unit");
            AlignResult(EraseOwnerTv(selectedDeclarationResult, owners, refs), selectedDeclarationResult,
                DeclarationOwnerArguments(declaringName));
            return;
        }

        // Cross-module equivalent of the local declaration walk above.  The emitted reference is the
        // authority for the physical existential slot; no reference-assembly or namespace special case
        // participates in the decision.
        if (refs.TryStarProjectionMember(f, sourceMember, accessorKind, ga, authoredSignature, pc,
                declarationId,
                out var erasedOwner, out var erasedMethod, out var erasedSignature, out var declarationResult,
                out var physicalResult))
        {
            CloseDeclarationResult(declarationResult);
            BindOwner(erasedOwner);
            call["method"] = erasedMethod;
            call["sig"] = new JsonArray(erasedSignature.Select(TypeJson.Write).ToArray());
            call["virtual"] = true;
            MarkPhysicalPropertyCall(call, propertyCall, sourcePropertyName, accessorKind);
            AlignResult(physicalResult, declarationResult, ownerArguments);
            return;
        }

        // A nullary member may be declared by a non-generic interface inherited by the
        // existential view. Give the general inherited-owner pass an exact empty signature;
        // it will select the unique nearest declaration after all synthetic types exist.
        if (pc == 0 && call["sig"] == null) call["sig"] = new JsonArray();
    }

    static TypeNode CloseDeclarationType(TypeNode type, IReadOnlyList<TypeNode> ownerArgs,
        IReadOnlyList<TypeNode> methodArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArgs.Count
            => ownerArgs[tv.I] is TypeNode.Star ? new TypeNode.Fqn("kotlin.Any") : ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArgs.Count
            => methodArgs[tv.I] is TypeNode.Star ? new TypeNode.Fqn("kotlin.Any") : methodArgs[tv.I],
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            CloseDeclarationType(projection.Of, ownerArgs, methodArgs)),
        TypeNode.Fqn { Args: { } args } f => new TypeNode.Fqn(f.Name,
            args.Select(arg => CloseDeclarationType(arg, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(CloseDeclarationType(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(CloseDeclarationType(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(CloseDeclarationType(a.Elem, ownerArgs, methodArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(CloseDeclarationType(b.Of, ownerArgs, methodArgs)),
        TypeNode.Ptr p => new TypeNode.Ptr(CloseDeclarationType(p.Of, ownerArgs, methodArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            CloseDeclarationType(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(parameter => CloseDeclarationType(parameter, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : CloseDeclarationType(fn.Recv, ownerArgs, methodArgs), fn.Clr,
            fn.Ctx?.Select(context => CloseDeclarationType(context, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

    static IReadOnlyList<TypeNode> ProjectOwnerArguments(Owner start, IReadOnlyList<TypeNode> startArguments,
        string targetName, IReadOnlyDictionary<string, Owner> owners)
    {
        var queue = new Queue<(Owner Owner, TypeNode[] Arguments, int Depth)>();
        queue.Enqueue((start, startArguments.ToArray(), 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new Dictionary<string, TypeNode[]>(StringComparer.Ordinal);
        int? matchDepth = null;

        while (queue.Count > 0)
        {
            var (current, currentArguments, depth) = queue.Dequeue();
            if (matchDepth is int nearest && depth > nearest) break;
            var key = current.Name + "|" + new JsonArray(
                currentArguments.Select(TypeJson.Write).ToArray()).ToJsonString();
            if (!seen.Add(key)) continue;
            if (current.Name == targetName)
            {
                matchDepth = depth;
                matches.TryAdd(key, currentArguments);
                continue;
            }

            IEnumerable<JsonNode> Edges()
            {
                if (current.Def["base"] != null) yield return current.Def["base"];
                if (current.Def["interfaces"] is JsonArray interfaces)
                    foreach (var edge in interfaces)
                        if (edge != null) yield return edge;
            }

            foreach (var edge in Edges())
            {
                if (TypeJson.Read(edge) is not TypeNode.Fqn application
                    || !owners.TryGetValue(application.Name, out var ancestor)) continue;
                var projected = SubstituteDeclarationTypeArguments(
                    application, currentArguments, Array.Empty<TypeNode>()) as TypeNode.Fqn;
                var arguments = projected?.Args ?? Array.Empty<TypeNode>();
                var expected = (ancestor.Def["typeParams"] as JsonArray)?.Count ?? 0;
                if (arguments.Length != expected) continue;
                queue.Enqueue((ancestor, arguments, depth + 1));
            }
        }

        return matches.Count == 1 ? matches.Values.Single() : null;
    }

    internal static TypeNode SubstituteMethodTypeArguments(TypeNode type, IReadOnlyList<TypeNode> args) => type switch
    {
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < args.Count => args[tv.I],
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            SubstituteMethodTypeArguments(projection.Of, args)),
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
            nested.Select(argument => SubstituteMethodTypeArguments(argument, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteMethodTypeArguments(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteMethodTypeArguments(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstituteMethodTypeArguments(a.Elem, args), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteMethodTypeArguments(b.Of, args)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteMethodTypeArguments(p.Of, args)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, SubstituteMethodTypeArguments(m.M, args),
            SubstituteMethodTypeArguments(m.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            SubstituteMethodTypeArguments(fn.Ret, args),
            fn.Params.Select(parameter => SubstituteMethodTypeArguments(parameter, args)).ToArray(),
            fn.Recv == null ? null : SubstituteMethodTypeArguments(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(context => SubstituteMethodTypeArguments(context, args)).ToArray()),
        _ => type,
    };

    static TypeNode SubstituteDeclarationTypeArguments(TypeNode type,
        IReadOnlyList<TypeNode> ownerArguments, IReadOnlyList<TypeNode> methodArguments) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < ownerArguments.Count
            => ownerArguments[tv.I],
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodArguments.Count
            => methodArguments[tv.I],
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance,
            SubstituteDeclarationTypeArguments(projection.Of, ownerArguments, methodArguments)),
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name,
            nested.Select(argument => SubstituteDeclarationTypeArguments(
                argument, ownerArguments, methodArguments)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteDeclarationTypeArguments(
            n.Of, ownerArguments, methodArguments)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteDeclarationTypeArguments(
            o.Of, ownerArguments, methodArguments)),
        TypeNode.Array a => new TypeNode.Array(SubstituteDeclarationTypeArguments(
            a.Elem, ownerArguments, methodArguments), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteDeclarationTypeArguments(
            b.Of, ownerArguments, methodArguments)),
        TypeNode.Ptr p => new TypeNode.Ptr(SubstituteDeclarationTypeArguments(
            p.Of, ownerArguments, methodArguments)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req,
            SubstituteDeclarationTypeArguments(m.M, ownerArguments, methodArguments),
            SubstituteDeclarationTypeArguments(m.Of, ownerArguments, methodArguments)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend,
            SubstituteDeclarationTypeArguments(fn.Ret, ownerArguments, methodArguments),
            fn.Params.Select(parameter => SubstituteDeclarationTypeArguments(
                parameter, ownerArguments, methodArguments)).ToArray(),
            fn.Recv == null ? null : SubstituteDeclarationTypeArguments(
                fn.Recv, ownerArguments, methodArguments), fn.Clr,
            fn.Ctx?.Select(context => SubstituteDeclarationTypeArguments(
                context, ownerArguments, methodArguments)).ToArray()),
        _ => type,
    };

    static TypeNode.Fqn ProjectedExistentialType(JsonNode expression)
    {
        if (expression is not JsonObject obj) return null;
        if (StripProjectionShell(TypeJson.Read(obj[ExistentialResultProjectionKey])) is TypeNode.Fqn direct)
            return direct;
        if (Str(obj["k"]) == "valueBlock") return ProjectedExistentialType(obj["result"]);
        if (Str(obj["k"]) == "cond")
        {
            var thenProjection = ProjectedExistentialType(obj["then"]);
            var elseProjection = ProjectedExistentialType(obj["else"]);
            if (thenProjection != null
                && (obj["else"] is JsonObject elseBottom && Str(elseBottom["k"]) == "throwExpr"
                    || thenProjection == elseProjection))
                return thenProjection;
            if (elseProjection != null
                && obj["then"] is JsonObject thenBottom && Str(thenBottom["k"]) == "throwExpr")
                return elseProjection;
        }
        return null;
    }

    static TypeNode ProjectedArrayElement(JsonNode expression) => expression is JsonObject obj
        ? TypeJson.Read(obj[ExistentialArrayElementProjectionKey])
        : null;

    static bool IsExistentialPhysicalCarrier(TypeNode type,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        return type is TypeNode.Fqn { Args: null } f
            && (owners.Values.Any(owner => owner.ErasedName == f.Name)
                || refs.IsExistentialPhysicalOwner(f.Name));
    }

    static bool ContainsPhysicalExistential(TypeNode type,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) => type switch
    {
        TypeNode.Fqn f => IsExistentialPhysicalCarrier(f, owners, refs)
            || f.Args?.Any(argument => ContainsPhysicalExistential(argument, owners, refs)) == true,
        TypeNode.Nullable nullable => ContainsPhysicalExistential(nullable.Of, owners, refs),
        TypeNode.Oblivious oblivious => ContainsPhysicalExistential(oblivious.Of, owners, refs),
        TypeNode.Projection projection => ContainsPhysicalExistential(projection.Of, owners, refs),
        TypeNode.Array array => ContainsPhysicalExistential(array.Elem, owners, refs),
        TypeNode.ByRef byRef => ContainsPhysicalExistential(byRef.Of, owners, refs),
        TypeNode.Ptr pointer => ContainsPhysicalExistential(pointer.Of, owners, refs),
        TypeNode.Mod modifier => ContainsPhysicalExistential(modifier.M, owners, refs)
            || ContainsPhysicalExistential(modifier.Of, owners, refs),
        TypeNode.Fn function => ContainsPhysicalExistential(function.Ret, owners, refs)
            || function.Params.Any(parameter => ContainsPhysicalExistential(parameter, owners, refs))
            || function.Recv != null && ContainsPhysicalExistential(function.Recv, owners, refs)
            || function.Ctx?.Any(context => ContainsPhysicalExistential(context, owners, refs)) == true,
        _ => false,
    };

    static void AlignExistentialResult(JsonObject call, TypeNode physicalResult,
        TypeNode projectedSemanticResult)
    {
        var inner = call.DeepClone().AsObject();
        inner["ret"] = TypeJson.Write(physicalResult);
        if (inner["dynRet"] != null) inner["dynRet"] = TypeJson.Write(physicalResult);
        if (inner["sty"] != null) inner["sty"] = TypeJson.Write(physicalResult);

        foreach (var key in call.Select(pair => pair.Key).ToList()) call.Remove(key);
        // This is a physical identity projection. The selected existential slot already returns the carrier, but the
        // explicit cast gives later local-flow normalization one exact result token while the pass-local semantic
        // projection preserves which nested owner arguments were stars across immediate and local member binding.
        call["k"] = "cast";
        call["type"] = TypeJson.Write(physicalResult);
        call["e"] = inner;
        call[ExistentialResultProjectionKey] = TypeJson.Write(projectedSemanticResult);
        call["_exactBridgeCast"] = true;
    }

    internal static bool IsVoidResult(TypeNode type) =>
        type is TypeNode.Fqn { Args: null, Name: "kotlin.Unit" or "void" or "System.Void" };

    internal static void AlignCallResult(JsonObject call, TypeNode physicalResult, bool protectExactCast)
    {
        var semanticResult = NodeType.Stamp(call);
        if (physicalResult == null || semanticResult == null || physicalResult.Equals(semanticResult)
            || IsVoidResult(physicalResult) && IsVoidResult(semanticResult)) return;

        var hadSty = call["sty"] != null;
        var inner = call.DeepClone().AsObject();
        inner["ret"] = TypeJson.Write(physicalResult);
        if (inner["dynRet"] != null) inner["dynRet"] = TypeJson.Write(physicalResult);
        if (inner["sty"] != null) inner["sty"] = TypeJson.Write(physicalResult);

        foreach (var key in call.Select(pair => pair.Key).ToList()) call.Remove(key);
        call["k"] = "cast";
        call["type"] = TypeJson.Write(semanticResult);
        call["e"] = inner;
        if (hadSty) call["sty"] = TypeJson.Write(semanticResult);
        // This cast is the exact semantic projection of a successfully raw-classifier-tested value, not another
        // Kotlin erased `as G<T>` operation for this pass to collapse back to the existential carrier.
        if (protectExactCast) call["_exactBridgeCast"] = true;
    }

    static void RemoveExactBridgeCastMarkers(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("_exactBridgeCast");
                obj.Remove(ProjectedArrayReadKey);
                obj.Remove(ExistentialResultProjectionKey);
                obj.Remove(ExistentialArrayElementProjectionKey);
                foreach (var value in obj.Select(pair => pair.Value).ToList())
                    if (value != null) RemoveExactBridgeCastMarkers(value);
                break;
            case JsonArray array:
                foreach (var value in array)
                    if (value != null) RemoveExactBridgeCastMarkers(value);
                break;
        }
    }

    static void MarkPhysicalPropertyCall(JsonObject call, bool propertyCall,
        string sourcePropertyName, string accessorKind, string physicalIdentity = null)
    {
        // This call now targets the synthesized existential slot/bridge, not the frontend-selected source MethodDef.
        // Consume that source identity exactly where the dedicated representation is chosen; otherwise #395's final
        // local binder would retarget the carrier call back to the generic source declaration.
        if (physicalIdentity != null) call[DeclarationIdentityBinding.Key] = physicalIdentity;
        else call.Remove(DeclarationIdentityBinding.Key);
        if (!propertyCall) return;
        KotlinPropertyAccessors.PreserveCallIdentity(call, sourcePropertyName, accessorKind);
        call.Remove("prop");
    }

    static JsonArray ErasedPhysicalSignature(JsonObject declaration,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs) =>
        new((declaration["params"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => TypeJson.Write(EraseOwnerTv(TypeJson.Read(p["type"]), owners, refs)))
            .ToArray() ?? Array.Empty<JsonNode>());

    static (Owner Owner, JsonObject Method)? FindDeclaringOwner(Owner start, string sourceMember,
        string accessorKind, int pc, int ga,
        IReadOnlyList<TypeNode> authoredSignature, string declarationId,
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
                        // Synthesize() exposes every non-static source member through a public forwarding
                        // bridge when needed, including private members used by lifted nested classes.
                        if ((declarationId != null
                                ? Str(m[DeclarationIdentityBinding.Key]) == declarationId
                                : MatchesSourceMember(m, sourceMember, accessorKind))
                            && !Bool(m["static"])
                            && ((m["params"] as JsonArray)?.Count ?? 0) == pc
                            && ((m["typeParams"] as JsonArray)?.Count ?? 0) == ga
                            && (declarationId != null || SignatureMatches(m, authoredSignature)))
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

    static bool MatchesSourceMember(JsonObject declaration, string sourceMember, string accessorKind)
    {
        if (KotlinPropertyAccessors.TryIdentity(declaration, out var propertyName, out var propertyAccessor))
            return accessorKind != null && propertyName == sourceMember && propertyAccessor == accessorKind;
        return accessorKind == null
            && (Str(declaration[SourceMemberKey]) ?? Str(declaration["name"])) == sourceMember;
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
        Owner start, string sourceMember, string accessorKind, int pc, int ga,
        string declarationId,
        IReadOnlyDictionary<string, Owner> owners,
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
                    if ((declarationId != null
                            ? Str(candidate[DeclarationIdentityBinding.Key]) == declarationId
                            : MatchesSourceMember(candidate, sourceMember, accessorKind))
                        && !Bool(candidate["static"]) && !IsPrivate(candidate)
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

    static TypeNode RewriteType(TypeNode type, IReadOnlyDictionary<string, Owner> owners,
        ReferenceMetadataIndex refs, bool boundDeclaration = false,
        IReadOnlyDictionary<string, string> localClrAliases = null)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } nestedArgs } nestedForeign
                when !boundDeclaration && nestedArgs.Any(ContainsExistentialProjection)
                    && IsOpaqueForeignProjection(nestedForeign, refs, localClrAliases):
                // A star anywhere below a foreign invariant construction makes the whole construction
                // non-reifiable on the CLR.  In particular Outer<Inner<*>> is not Outer<object> (nor
                // Outer<Inner<object>>); keep the original runtime value in one opaque object slot and let
                // ForeignStarProjectionBinding route member access through the reflection ABI.
                if (IsByRefLikeForeignProjection(nestedForeign, refs, localClrAliases))
                    throw new NotSupportedException(
                        $"bir2cir: foreign byref-like generic star projection `{nestedForeign.Name}<*>` has no boxable CLR existential representation");
                return new TypeNode.Fqn("kotlin.Any");
            case TypeNode.Fqn { Args: { } args } f when !boundDeclaration && args.Any(IsExistentialArgument):
            {
                var erased = owners.TryGetValue(f.Name, out var local) ? local.ErasedName : null;
                if (local != null && local.Needed) return new TypeNode.Fqn(erased);
                if (refs.TryExistentialPhysicalOwner(f.Name, out var referenced))
                    return new TypeNode.Fqn(referenced);
                // A foreign CLR generic cannot be retrofitted to implement DotKt's nominal existential. Its value
                // slot is object; classifier checks/casts/member dispatch were already authored explicitly by
                // ForeignStarProjectionBinding above. Never manufacture the invariant fiction G<object>.
                if (!refs.HasDotKtOwner(f.Name) && IsOpaqueForeignProjection(f, refs, localClrAliases))
                {
                    if (IsByRefLikeForeignProjection(f, refs, localClrAliases))
                        throw new NotSupportedException(
                            $"bir2cir: foreign byref-like generic star projection `{f.Name}<*>` has no boxable CLR existential representation");
                    return new TypeNode.Fqn("kotlin.Any");
                }
                return new TypeNode.Fqn(f.Name, args.Select(a => RewriteType(
                    a, owners, refs, boundDeclaration, localClrAliases)).ToArray());
            }
            case TypeNode.Star:
                // A residual star belongs to an unsupported shape (multi-parameter mask / external CLR generic).
                // Keep CIR well-formed with the explicit erasure; supported local/reference one-parameter owners
                // have already become their existential view above.
                return new TypeNode.Fqn("kotlin.Any");
            case TypeNode.Projection p:
            {
                // A use-site projection is existential at the CLR boundary. A compiler-authored local/referenced
                // generic has a nominal non-generic carrier implemented by every closed construction, so retain the
                // classifier without inventing G<object>. Foreign generics have no such nominal view and use the
                // same opaque object representation as a star projection.
                var projected = StripProjectionShell(p.Of);
                if (projected is TypeNode.Fqn f && ProjectionCanChangeConstruction(f, owners, refs))
                {
                    if (owners.TryGetValue(f.Name, out var local) && local.Needed)
                        return ReplaceProjectionCore(p.Of, f, new TypeNode.Fqn(local.ErasedName));
                    if (refs.TryExistentialPhysicalOwner(f.Name, out var referenced))
                        return ReplaceProjectionCore(p.Of, f, new TypeNode.Fqn(referenced));
                }
                return RewriteType(p.Of, owners, refs, boundDeclaration, localClrAliases);
            }
            case TypeNode.Fqn { Args: { } args } f:
                return new TypeNode.Fqn(f.Name, args.Select(a => RewriteType(
                    a, owners, refs, boundDeclaration, localClrAliases)).ToArray());
            case TypeNode.Nullable n: return new TypeNode.Nullable(RewriteType(
                n.Of, owners, refs, boundDeclaration, localClrAliases));
            case TypeNode.Oblivious o: return new TypeNode.Oblivious(RewriteType(
                o.Of, owners, refs, boundDeclaration, localClrAliases));
            case TypeNode.Array a: return new TypeNode.Array(RewriteType(
                a.Elem, owners, refs, boundDeclaration, localClrAliases));
            case TypeNode.ByRef b: return new TypeNode.ByRef(RewriteType(
                b.Of, owners, refs, boundDeclaration, localClrAliases));
            case TypeNode.Fn fn: return new TypeNode.Fn(fn.Suspend, RewriteType(
                    fn.Ret, owners, refs, boundDeclaration, localClrAliases),
                fn.Params.Select(p => RewriteType(
                    p, owners, refs, boundDeclaration, localClrAliases)).ToArray(),
                fn.Recv == null ? null : RewriteType(
                    fn.Recv, owners, refs, boundDeclaration, localClrAliases));
            default: return type;
        }
    }

    static bool IsOpaqueForeignProjection(TypeNode.Fqn type, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
    {
        if (localClrAliases != null && localClrAliases.TryGetValue(type.Name, out var physical))
            return !BirTypeLowering.GenericAliasHeadDependsOnLoweredArguments(physical)
                || BirTypeLowering.ProjectedAliasHasReifiedGenericHead(type.Name, physical, type.Args);
        return ForeignStarProjectionBinding.IsForeignStarType(type, refs);
    }

    static bool IsByRefLikeForeignProjection(TypeNode.Fqn type, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, string> localClrAliases)
    {
        if (refs.IsByRefLikeFqn(type)) return true;
        return localClrAliases != null && localClrAliases.TryGetValue(type.Name, out var physical)
            && refs.IsByRefLikeFqn(new TypeNode.Fqn(physical, type.Args));
    }

    static bool ContainsOwnerTv(TypeNode t) => t switch
    {
        TypeNode.Tv { Scope: "type" } => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsOwnerTv),
        TypeNode.Nullable n => ContainsOwnerTv(n.Of),
        TypeNode.Oblivious o => ContainsOwnerTv(o.Of),
        TypeNode.Projection p => ContainsOwnerTv(p.Of),
        TypeNode.Array a => ContainsOwnerTv(a.Elem),
        TypeNode.ByRef b => ContainsOwnerTv(b.Of),
        TypeNode.Fn fn => ContainsOwnerTv(fn.Ret) || fn.Params.Any(ContainsOwnerTv)
            || (fn.Recv != null && ContainsOwnerTv(fn.Recv)),
        _ => false,
    };

    static bool ContainsStarOrTypeVariable(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Projection or TypeNode.Tv => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsStarOrTypeVariable),
        TypeNode.Nullable n => ContainsStarOrTypeVariable(n.Of),
        TypeNode.Oblivious o => ContainsStarOrTypeVariable(o.Of),
        TypeNode.Array a => ContainsStarOrTypeVariable(a.Elem),
        TypeNode.ByRef b => ContainsStarOrTypeVariable(b.Of),
        TypeNode.Ptr p => ContainsStarOrTypeVariable(p.Of),
        TypeNode.Mod m => ContainsStarOrTypeVariable(m.M) || ContainsStarOrTypeVariable(m.Of),
        TypeNode.Fn fn => ContainsStarOrTypeVariable(fn.Ret) || fn.Params.Any(ContainsStarOrTypeVariable)
            || (fn.Recv != null && ContainsStarOrTypeVariable(fn.Recv))
            || (fn.Ctx?.Any(ContainsStarOrTypeVariable) ?? false),
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

    static bool ContainsExistentialProjection(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExistentialProjection),
        TypeNode.Nullable n => ContainsExistentialProjection(n.Of),
        TypeNode.Oblivious o => ContainsExistentialProjection(o.Of),
        TypeNode.Array a => ContainsExistentialProjection(a.Elem),
        TypeNode.ByRef b => ContainsExistentialProjection(b.Of),
        TypeNode.Ptr p => ContainsExistentialProjection(p.Of),
        TypeNode.Mod m => ContainsExistentialProjection(m.M) || ContainsExistentialProjection(m.Of),
        TypeNode.Fn fn => ContainsExistentialProjection(fn.Ret) || fn.Params.Any(ContainsExistentialProjection)
            || (fn.Recv != null && ContainsExistentialProjection(fn.Recv))
            || (fn.Ctx?.Any(ContainsExistentialProjection) ?? false),
        _ => false,
    };

    static bool IsExistentialArgument(TypeNode type) => type is TypeNode.Star or TypeNode.Projection;

    static TypeNode StripProjectionShell(TypeNode type) => type switch
    {
        TypeNode.Nullable n => StripProjectionShell(n.Of),
        TypeNode.Oblivious o => StripProjectionShell(o.Of),
        _ => type,
    };

    static TypeNode ReplaceProjectionCore(TypeNode shell, TypeNode.Fqn core, TypeNode replacement) => shell switch
    {
        TypeNode.Nullable n => new TypeNode.Nullable(ReplaceProjectionCore(n.Of, core, replacement)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(ReplaceProjectionCore(o.Of, core, replacement)),
        _ => replacement,
    };

    static bool ContainsExplicitExistential(TypeNode t) => t switch
    {
        TypeNode.Star or TypeNode.Projection => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsExplicitExistential),
        TypeNode.Nullable n => ContainsExplicitExistential(n.Of),
        TypeNode.Oblivious o => ContainsExplicitExistential(o.Of),
        TypeNode.Array a => ContainsExplicitExistential(a.Elem),
        TypeNode.ByRef b => ContainsExplicitExistential(b.Of),
        TypeNode.Ptr p => ContainsExplicitExistential(p.Of),
        TypeNode.Mod m => ContainsExplicitExistential(m.M) || ContainsExplicitExistential(m.Of),
        TypeNode.Fn fn => ContainsExplicitExistential(fn.Ret) || fn.Params.Any(ContainsExplicitExistential)
            || (fn.Recv != null && ContainsExplicitExistential(fn.Recv))
            || (fn.Ctx?.Any(ContainsExplicitExistential) ?? false),
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
    // Current BIR omits `vis` for Kotlin's default public visibility; explicit non-public declarations carry a value.
    static bool IsPublic(JsonObject method) => Str(method["vis"]) is null or "public";
    static bool IsPrivate(JsonObject method) => Str(method["vis"]) == "private";
}

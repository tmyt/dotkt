using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Bind an inherited instance member to its CONSTRUCTED declaring owner.
//
// BIR deliberately records the Kotlin receiver owner.  For example, a call through
// `Derived<T>` to `Base<T>.m()` remains ownerType=Derived<T>; that is the faithful
// Kotlin-IR projection and must not be polluted with a CLR MemberRef decision in kotc.
// A CLR MemberRef, however, cannot name the open `Base<>.m` when the call is made on
// `Base<T>`: doing so produces "containing type is not fully instantiated" at JIT time.
//
// Discovering the base declaration while emitting and reconstructing its generic
// instantiation makes emission semantic and loses
// the substitution on generic-method calls.  This pass performs the general hierarchy
// substitution in bir2cir and rewrites ownerType to the exact constructed declaration
// (`Base<T>`).  ilemit subsequently links that owner one-to-one.
//
// Resolution is intentionally conservative:
//   * a kotc `overrides` fact wins when it names one unambiguous reachable declaration;
//   * otherwise an exact name/generic-arity/parameter-signature match is required at the
//     nearest hierarchy depth;
//   * ambiguity or incomplete type information leaves the node untouched (never guesses
//     an overload).
// No library/member FQNs are special-cased.
static class InheritedMemberOwnerBinding
{
    sealed class TypeDef
    {
        public string Name;
        public string Kind;
        public int TypeParamCount;
        public JsonArray TypeParams;
        public TypeNode.Fqn Base;
        public TypeNode.Fqn[] Interfaces = Array.Empty<TypeNode.Fqn>();
        public JsonArray Methods;
        public JsonArray InheritedDefaultMethods;
    }

    readonly record struct Reachable(TypeNode.Fqn Type, int Depth);
    readonly record struct LocalDeclaration(string Owner, JsonObject Method);
    readonly record struct NativeSlot(TypeNode.Fqn Owner, MemberBinding Declaration);

    public static void ApplyAll(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var types = CollectTypes(rootList);
        var localDeclarations = CollectLocalDeclarations(types);
        foreach (var root in rootList) Walk(root, types, localDeclarations, refs, null);
    }

    // Frame materialization must see use-site owner arguments, not the variables of an inherited
    // declaration. Native inherited slots must also bind before Kotlin frame materialization: their formal
    // variables and constraints belong to the existing CLR declaration, not to a new erased fake-override slot.
    public static void ProjectOwners(IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var rootList = roots.ToList();
        var types = CollectTypes(rootList);
        var nativeSlots = CollectNativeSlots(types, refs);
        var sourceDeclarations = CollectLocalDeclarations(types, sourceOnly: true);
        foreach (var root in rootList) Walk(root, types, sourceDeclarations, refs, null, projectOnly: true, nativeSlots);
        var inherited = nativeSlots.Keys.ToHashSet();
        foreach (var root in rootList) InheritedDefaultFakeOverrideElision.ApplyNativeSlots(root, inherited);
    }

    static Dictionary<JsonObject, NativeSlot> CollectNativeSlots(Dictionary<string, TypeDef> types,
        ReferenceMetadataIndex refs)
    {
        var result = new Dictionary<JsonObject, NativeSlot>();
        foreach (var type in types.Values.Where(type => type.Kind == "interface"))
        {
            var owner = new TypeNode.Fqn(type.Name, type.TypeParamCount == 0 ? null
                : Enumerable.Range(0, type.TypeParamCount).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
            foreach (var method in type.Methods?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (!Bool(method["fakeOverride"])
                    || method["params"] is not JsonArray parameters || method["overrides"] is not JsonArray overrides)
                    continue;
                var signature = parameters.OfType<JsonObject>().Select(parameter => TypeJson.Read(parameter["type"])).ToArray();
                var resultType = TypeJson.Read(method["ret"]);
                if (signature.Length != parameters.Count || signature.Any(type => type == null) || resultType == null) continue;
                KotlinPropertyAccessors.TryIdentity(method, out var propertyName, out var accessorKind);
                var name = Str(method["name"]);
                var owners = overrides.OfType<JsonObject>().Where(ancestor =>
                        Str(ancestor["member"]) == (propertyName ?? name)
                        && Str(ancestor["kind"]) == (accessorKind switch { "get" => "getter", "set" => "setter", _ => "method" }))
                    .Select(ancestor => TypeJson.OwnerName(ancestor["owner"])).ToHashSet(StringComparer.Ordinal);
                var arity = (method["typeParams"] as JsonArray)?.Count ?? 0;
                var hierarchy = ReachableTypes(owner, types, refs, sourceFrames: true).ToList();
                var candidates = hierarchy
                    .Where(ancestor => owners.Contains(ancestor.Type.Name) && !types.ContainsKey(ancestor.Type.Name))
                    .Select(ancestor => (ancestor, declaration: refs.NativeInterfaceSourceDeclaration(
                        ancestor.Type, name, arity, signature, resultType, propertyName, accessorKind)))
                    .Where(candidate => candidate.declaration != null).ToList();
                if (candidates.Count == 0) continue;
                var nearestDepth = candidates.Min(candidate => candidate.ancestor.Depth);
                // The native closure is ancestry, not a request to bypass a closer real Kotlin override.
                // A derived fake of that real declaration owns the Kotlin ABI rather than the native slot ABI.
                if (hierarchy.Any(ancestor => ancestor.Depth > 0 && ancestor.Depth <= nearestDepth
                    && owners.Contains(ancestor.Type.Name) && types.TryGetValue(ancestor.Type.Name, out var local)
                    && EffectiveArgs(ancestor.Type, local.TypeParamCount) is { } localArguments
                    && local.Methods?.OfType<JsonObject>().Any(declaration => !Bool(declaration["fakeOverride"])
                        && MatchesSourceMethod(declaration, name, arity, signature, localArguments,
                            propertyName, accessorKind)) == true)) continue;
                var nearest = candidates.Where(candidate => candidate.ancestor.Depth == nearestDepth)
                    .Select(candidate => new NativeSlot(candidate.ancestor.Type, candidate.declaration)).Distinct().ToList();
                if (nearest.Count == 1) result.Add(method, nearest[0]);
            }
        }
        return result;
    }

    static bool MatchesSourceMethod(JsonObject method, string name, int arity, TypeNode[] signature,
        TypeNode[] ownerArguments, string propertyName, string accessorKind)
    {
        if (((method["typeParams"] as JsonArray)?.Count ?? 0) != arity
            || method["params"] is not JsonArray parameters || parameters.Count != signature.Length) return false;
        if (propertyName != null)
        {
            if (!KotlinPropertyAccessors.TryIdentity(method, out var candidateProperty, out var candidateAccessor)
                || candidateProperty != propertyName || candidateAccessor != accessorKind) return false;
        }
        else if (Str(method["name"]) != name || KotlinPropertyAccessors.TryIdentity(method, out _, out _)) return false;
        if (parameters.Any(parameter => parameter is not JsonObject)) return false;
        return parameters.Cast<JsonObject>().Select((parameter, index) =>
            TypeJson.Read(parameter["type"]) is { } type
                && ReferenceMetadataIndex.SourceDeclarationDescribesCall(
                    SubstOwnerTvs(type, ownerArguments), signature[index])).All(match => match);
    }

    // A fake inherited call can carry a constructed Kotlin signature but no
    // primary declaration ID. Its override closure still selects the actual
    // declaration. Consume that fact before erasure destroys the source
    // signature correspondence; never relax physical overload matching later.
    static void BindSourceDeclarationCall(JsonObject call, TypeNode.Fqn owner,
        Dictionary<string, TypeDef> types, IReadOnlyDictionary<string, LocalDeclaration> declarations,
        ReferenceMetadataIndex refs)
    {
        if (Str(call["k"]) is not ("callInstance" or "newBoundDelegate")
            || Bool(call["super"]) || Str(call["method"]) is not string name
            || ReadTypes(call["sig"] as JsonArray) is not { } signature) return;
        KotlinPropertyAccessors.TryCallIdentity(call, out var propertyName, out var accessorKind);
        var arity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        // A fake override's descriptor is expressed in the accessed owner's
        // open frame. It may also arrive already closed by that owner's
        // construction. Both describe the exact selected declaration; neither
        // permits matching by argument assignability after erasure.
        var accessedArguments = types.TryGetValue(owner.Name, out var accessedType)
            ? EffectiveArgs(owner, accessedType.TypeParamCount) : null;
        var closedSignature = accessedArguments == null ? signature
            : signature.Select(type => SubstOwnerTvs(type, accessedArguments)).ToArray();
        bool Matches(JsonObject method, TypeNode.Fqn construction) =>
            !Bool(method["fakeOverride"]) && !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
            && (MatchesSourceMethod(method, name, arity, signature,
                    construction.Args ?? Array.Empty<TypeNode>(), propertyName, accessorKind)
                || MatchesSourceMethod(method, name, arity, closedSignature,
                    construction.Args ?? Array.Empty<TypeNode>(), propertyName, accessorKind));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (Str(call[DeclarationIdentityBinding.Key]) is string selectedId) ids.Add(selectedId);
        else if (call["overrides"] is JsonArray overrides)
            foreach (var fact in overrides.OfType<JsonObject>())
                if (Str(fact["member"]) == (propertyName ?? name)
                    && Str(fact["kind"]) == (accessorKind switch { "get" => "getter", "set" => "setter", _ => "method" })
                    && Str(fact[DeclarationIdentityBinding.Key]) is string id) ids.Add(id);
        // A real override takes precedence over its ancestry, but an unrelated
        // overload must not shadow the declaration selected by Kotlin merely
        // because closing the owner makes their signatures coincide.
        if (types.TryGetValue(owner.Name, out var accessed)
            && accessed.Methods?.OfType<JsonObject>().Any(method => Matches(method, owner)
                && (Str(method[DeclarationIdentityBinding.Key]) is string ownId && ids.Contains(ownId)
                    || ids.Any(id => OverridesDeclaration(method, id)))) == true) return;
        var candidates = new List<(string Id, TypeNode.Fqn Owner, JsonObject Method, int Depth)>();
        foreach (var reachable in ReachableTypes(owner, types, refs, sourceFrames: true))
            foreach (var id in ids)
                if (declarations.TryGetValue(id, out var declaration)
                    && declaration.Owner == reachable.Type.Name && Matches(declaration.Method, reachable.Type))
                    candidates.Add((id, reachable.Type, declaration.Method, reachable.Depth));
        if (candidates.Count == 0) return;
        // Override closure is not an ambiguity: a declaration explicitly
        // refining another candidate wins even if both interfaces were listed
        // directly. Unrelated declarations remain distinct candidates.
        candidates = candidates.Where(candidate => !candidates.Any(other =>
            other.Id != candidate.Id && OverridesDeclaration(other.Method, candidate.Id))).ToList();
        if (candidates.Count == 0) return;
        var depth = candidates.Min(candidate => candidate.Depth);
        var nearest = candidates.Where(candidate => candidate.Depth == depth)
            .GroupBy(candidate => (candidate.Id, SupertypeGraph.TypeKey(candidate.Owner)))
            .Select(group => group.First()).ToList();
        if (nearest.Count != 1) return;
        var selected = nearest[0];
        call["ownerType"] = TypeJson.Write(selected.Owner);
        if (Str(call["k"]) == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(selected.Owner);
        call[DeclarationIdentityBinding.Key] = selected.Id;
        var parameters = (JsonArray)selected.Method["params"];
        call["sig"] = new JsonArray(parameters.OfType<JsonObject>()
            .Select(parameter => parameter["type"].DeepClone()).ToArray());
        if (call[FunctionSignatureIdentity.CallKey] != null)
            call[FunctionSignatureIdentity.CallKey] = call["sig"].DeepClone();
        if (call["memberSignature"] != null) call["memberSignature"] = call["sig"].DeepClone();
        if (call["memberReturnType"] != null) call["memberReturnType"] = selected.Method["ret"]?.DeepClone();
        if (call["memberOwnerTypeParams"] != null)
            call["memberOwnerTypeParams"] = types[selected.Owner.Name].TypeParams.DeepClone();
        if (call["memberMethodTypeParams"] != null)
            call["memberMethodTypeParams"] = selected.Method["typeParams"]?.DeepClone() ?? new JsonArray();
        if (IsInterface(selected.Owner, types, refs) || Bool(selected.Method["virtual"])) call["virtual"] = true;
    }

    static bool OverridesDeclaration(JsonObject method, string id) =>
        method["overrides"] is JsonArray overrides && overrides.OfType<JsonObject>()
            .Any(fact => Str(fact[DeclarationIdentityBinding.Key]) == id);

    static void BindNativeSlotCall(JsonObject call, TypeNode.Fqn owner, Dictionary<string, TypeDef> types,
        IReadOnlyDictionary<JsonObject, NativeSlot> nativeSlots, ReferenceMetadataIndex refs)
    {
        if (nativeSlots == null || Str(call["k"]) is not ("callInstance" or "newBoundDelegate")
            || Bool(call["super"]) || !types.TryGetValue(owner.Name, out var type)
            || ReadTypes(call["sig"] as JsonArray) is not { } signature) return;
        KotlinPropertyAccessors.TryCallIdentity(call, out var propertyName, out var accessorKind);
        var arity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        if (ExactMethod(type, Str(call["method"]), arity, signature, owner.Args,
                propertyName, accessorKind, fakeOverride: false) != null) return;
        if (EffectiveArgs(owner, type.TypeParamCount) is not { } arguments) return;
        // A frontend descriptor selects a declaration in the accessed owner's OPEN frame. Closing it first
        // can collapse distinct overloads (T versus Int when the owner is instantiated at Int).
        var selectionOwner = new TypeNode.Fqn(owner.Name, type.TypeParamCount == 0 ? null
            : Enumerable.Range(0, type.TypeParamCount).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        var candidates = new List<(TypeNode.Fqn Owner, MemberBinding Declaration, int Depth)>();
        foreach (var reachable in ReachableTypes(selectionOwner, types, refs, sourceFrames: true))
        {
            if (!types.TryGetValue(reachable.Type.Name, out var inheritedType)
                || EffectiveArgs(reachable.Type, inheritedType.TypeParamCount) is not { } inheritedArguments) continue;
            foreach (var method in inheritedType.Methods?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (!nativeSlots.TryGetValue(method, out var selected)
                    || !MatchesSourceMethod(method, Str(call["method"]), arity, signature, inheritedArguments,
                        propertyName, accessorKind)) continue;
                candidates.Add(((TypeNode.Fqn)SubstOwnerTvs(selected.Owner, inheritedArguments),
                    selected.Declaration, reachable.Depth));
            }
        }
        if (candidates.Count == 0) return;
        var depth = candidates.Min(candidate => candidate.Depth);
        var nearest = candidates.Where(candidate => candidate.Depth == depth)
            .Select(candidate => new NativeSlot(candidate.Owner, candidate.Declaration)).Distinct().ToList();
        if (nearest.Count != 1) return;
        var selectedSlot = nearest[0];
        var nativeOwner = (TypeNode.Fqn)SubstOwnerTvs(selectedSlot.Owner, arguments);
        call["ownerType"] = TypeJson.Write(nativeOwner);
        if (Str(call["k"]) == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(nativeOwner);
        var nativeParameters = new JsonArray(selectedSlot.Declaration.ParamTypeNodes.Select(TypeJson.Write).ToArray());
        call["sig"] = nativeParameters;
        if (call["memberSignature"] != null) call["memberSignature"] = nativeParameters.DeepClone();
        if (call["memberReturnType"] != null)
            call["memberReturnType"] = TypeJson.Write(selectedSlot.Declaration.ReturnTypeNode);
        if (call["memberOwnerTypeParams"] != null)
            call["memberOwnerTypeParams"] = refs.OwnerTypeParamDeclarations(nativeOwner.Name)?.DeepClone() ?? new JsonArray();
        if (call["memberMethodTypeParams"] != null)
            call["memberMethodTypeParams"] = selectedSlot.Declaration.MethodTypeParams?.DeepClone() ?? new JsonArray();
        call["virtual"] = true;
    }

    static Dictionary<string, LocalDeclaration> CollectLocalDeclarations(Dictionary<string, TypeDef> types,
        bool sourceOnly = false)
    {
        var candidates = new Dictionary<string, List<LocalDeclaration>>(StringComparer.Ordinal);
        foreach (var type in types.Values)
        foreach (var method in type.Methods?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (sourceOnly && (Bool(method["fakeOverride"]) || KotlinPropertyAccessors.IsPhysicalSlotBridge(method))) continue;
            if (Str(method[DeclarationIdentityBinding.Key]) is not string declarationId) continue;
            if (!candidates.TryGetValue(declarationId, out var declarations))
                candidates[declarationId] = declarations = new List<LocalDeclaration>();
            declarations.Add(new LocalDeclaration(type.Name, method));
        }
        return candidates.Where(candidate => candidate.Value.Count == 1)
            .ToDictionary(candidate => candidate.Key, candidate => candidate.Value[0], StringComparer.Ordinal);
    }

    public static void SelfTestSourceDeclarations()
    {
        var root = JsonNode.Parse("""
        {"types":[
          {"name":"SourceSlot","kind":"interface","typeParams":["A","B"],"methods":[
            {"name":"pick","declarationId":"slot","virtual":true,"params":[{"name":"value","type":{"t":"tv","scope":"type","i":1}}],"ret":{"t":"tv","scope":"type","i":1}},
            {"name":"pick","declarationId":"sibling","virtual":true,"params":[{"name":"value","type":{"t":"fqn","name":"kotlin.String"}}],"ret":{"t":"fqn","name":"kotlin.String"}}]},
          {"name":"SourceMiddle","kind":"class","typeParams":["X","Y"],"interfaces":[{"t":"fqn","name":"SourceSlot","args":[{"t":"tv","scope":"type","i":1},{"t":"tv","scope":"type","i":0}]}],"methods":[]},
          {"name":"SourceLeaf","kind":"class","typeParams":["P","Q"],"base":{"t":"fqn","name":"SourceMiddle","args":[{"t":"tv","scope":"type","i":1},{"t":"tv","scope":"type","i":0}]},"methods":[]}]}
        """)!.AsObject();
        var types = CollectTypes(new[] { root });
        var declarations = CollectLocalDeclarations(types, sourceOnly: true);
        var owner = new TypeNode.Fqn("SourceLeaf", new TypeNode[] {
            new TypeNode.Fqn("kotlin.Int"), new TypeNode.Fqn("kotlin.String"),
        });
        var call = JsonNode.Parse("""
        {"k":"newBoundDelegate","method":"pick","sig":[{"t":"fqn","name":"kotlin.String"}],
          "memberSignature":[],"memberReturnType":{"t":"fqn","name":"kotlin.String"},
          "memberOwnerTypeParams":[],"memberMethodTypeParams":[],
          "functionCallSignature":[{"t":"fqn","name":"kotlin.String"}],
          "overrides":[{"member":"pick","kind":"method","declarationId":"slot"}]}
        """)!.AsObject();
        call["ownerType"] = TypeJson.Write(owner);
        var source = call.DeepClone();
        BindSourceDeclarationCall(call, owner, types, declarations, null);
        var expectedOwner = new TypeNode.Fqn("SourceSlot", owner.Args);
        if (!JsonNode.DeepEquals(call["ownerType"], TypeJson.Write(expectedOwner))
            || !JsonNode.DeepEquals(call["calleeOwner"], call["ownerType"])
            || Str(call[DeclarationIdentityBinding.Key]) != "slot"
            || TypeJson.Read(call["sig"][0]) != new TypeNode.Tv("type", 1)
            || !JsonNode.DeepEquals(call["memberSignature"], call["sig"])
            || !JsonNode.DeepEquals(call[FunctionSignatureIdentity.CallKey], call["sig"])
            || TypeJson.Read(call["memberReturnType"]) != new TypeNode.Tv("type", 1)
            || call["memberOwnerTypeParams"] is not JsonArray { Count: 2 })
            throw new InvalidOperationException("Source inherited selection lost exact identity, construction or declaration frame");
        var openCall = (JsonObject)source.DeepClone();
        openCall["sig"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("type", 1)));
        BindSourceDeclarationCall(openCall, owner, types, declarations, null);
        if (!JsonNode.DeepEquals(openCall["ownerType"], TypeJson.Write(expectedOwner))
            || Str(openCall[DeclarationIdentityBinding.Key]) != "slot")
            throw new InvalidOperationException("Source inherited selection lost the accessed-owner descriptor frame");
        var own = (JsonObject)types["SourceSlot"].Methods[1].DeepClone();
        own[DeclarationIdentityBinding.Key] = "own";
        types[owner.Name].Methods.Add(own);
        var ownCall = (JsonObject)source.DeepClone();
        BindSourceDeclarationCall(ownCall, owner, types, declarations, null);
        if (Str(ownCall[DeclarationIdentityBinding.Key]) != "slot"
            || !JsonNode.DeepEquals(ownCall["ownerType"], TypeJson.Write(expectedOwner)))
            throw new InvalidOperationException("Source inherited selection confused an unrelated own overload with the selected override");
        types[owner.Name].Methods.Clear();
        own = (JsonObject)types["SourceSlot"].Methods[0].DeepClone();
        own[DeclarationIdentityBinding.Key] = "own";
        own["overrides"] = new JsonArray(new JsonObject { [DeclarationIdentityBinding.Key] = "slot" });
        types[owner.Name].Methods.Add(own);
        ownCall = (JsonObject)source.DeepClone();
        BindSourceDeclarationCall(ownCall, owner, types, declarations, null);
        if (!JsonNode.DeepEquals(ownCall, source))
            throw new InvalidOperationException("Source inherited selection displaced a real accessed-owner override");
        types[owner.Name].Methods.Clear();
        var missing = (JsonObject)source.DeepClone();
        missing.Remove("overrides");
        var unchanged = missing.DeepClone();
        BindSourceDeclarationCall(missing, owner, types, declarations, null);
        if (!JsonNode.DeepEquals(missing, unchanged))
            throw new InvalidOperationException("Source inherited selection guessed a declaration without frontend facts");
        ((JsonArray)root["types"]).Add(JsonNode.Parse("""
        {"name":"SourceRefined","kind":"interface","typeParams":["T"],
          "interfaces":[{"t":"fqn","name":"SourceSlot","args":[{"t":"fqn","name":"kotlin.Int"},{"t":"tv","scope":"type","i":0}]}],
          "methods":[{"name":"pick","declarationId":"refined","virtual":true,
            "overrides":[{"declarationId":"slot"}],
            "params":[{"name":"value","type":{"t":"tv","scope":"type","i":0}}],
            "ret":{"t":"tv","scope":"type","i":0}}]}
        """));
        ((JsonArray)root["types"]).Add(JsonNode.Parse("""
        {"name":"SourceDiamond","kind":"class","typeParams":["A","B"],
          "interfaces":[{"t":"fqn","name":"SourceSlot","args":[{"t":"fqn","name":"kotlin.Int"},{"t":"tv","scope":"type","i":1}]},
            {"t":"fqn","name":"SourceRefined","args":[{"t":"tv","scope":"type","i":1}]}],"methods":[]}
        """));
        types = CollectTypes(new[] { root });
        declarations = CollectLocalDeclarations(types, sourceOnly: true);
        var diamondOwner = new TypeNode.Fqn("SourceDiamond", owner.Args);
        var diamond = (JsonObject)source.DeepClone();
        diamond["ownerType"] = TypeJson.Write(diamondOwner);
        ((JsonArray)diamond["overrides"]).Add(new JsonObject {
            ["member"] = "pick", ["kind"] = "method", [DeclarationIdentityBinding.Key] = "refined",
        });
        BindSourceDeclarationCall(diamond, diamondOwner, types, declarations, null);
        if (Str(diamond[DeclarationIdentityBinding.Key]) != "refined"
            || !JsonNode.DeepEquals(diamond["ownerType"], TypeJson.Write(new TypeNode.Fqn("SourceRefined", new[] { owner.Args[1] })))
            || TypeJson.Read(diamond["sig"][0]) != new TypeNode.Tv("type", 0))
            throw new InvalidOperationException("Source inherited selection lost the explicit refinement in a redundant diamond");
        types["SourceRefined"].Methods[0].AsObject().Remove("overrides");
        var ambiguous = (JsonObject)source.DeepClone();
        ambiguous["ownerType"] = TypeJson.Write(diamondOwner);
        ambiguous["overrides"] = diamond["overrides"].DeepClone();
        unchanged = ambiguous.DeepClone();
        BindSourceDeclarationCall(ambiguous, diamondOwner, types, declarations, null);
        if (!JsonNode.DeepEquals(ambiguous, unchanged))
            throw new InvalidOperationException("Source inherited selection guessed between unrelated exact declarations");
        var projection = new TypeNode.Projection("out", new TypeNode.Tv("type", 1));
        if (SubstOwnerTvs(projection, owner.Args) != new TypeNode.Projection("out", owner.Args[1]))
            throw new InvalidOperationException("Source owner substitution lost a projected generic frame");
        Console.WriteLine("[source inherited declaration] self-test OK (exact ID, reordered hierarchy, colliding signatures, real override precedence, descriptors, projections, refinement)");
    }

    static Dictionary<string, TypeDef> CollectTypes(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
        foreach (var root in roots) CollectFrom(root, result);
        return result;
    }

    static void CollectFrom(JsonNode node, Dictionary<string, TypeDef> result)
    {
        if (node is not JsonObject obj) return;
        if (obj["types"] is not JsonArray arr) return;
        foreach (var item in arr)
        {
            if (item is not JsonObject type || Str(type["name"]) is not string name) continue;
            result[name] = new TypeDef
            {
                Name = name,
                Kind = Str(type["kind"]),
                TypeParamCount = TypeParameterFrame.Count(type),
                TypeParams = TypeParameterFrame.CloneDeclarations(type),
                Base = TypeJson.Read(type["base"]) as TypeNode.Fqn,
                Interfaces = (type["interfaces"] as JsonArray)?.Select(TypeJson.Read)
                    .OfType<TypeNode.Fqn>().ToArray() ?? Array.Empty<TypeNode.Fqn>(),
                Methods = type["methods"] as JsonArray,
                InheritedDefaultMethods = type[KotlinPropertyAccessors.InheritedDefaultMethodsKey] as JsonArray,
            };
            CollectFrom(type, result);
        }
    }

    static void Walk(JsonNode node, Dictionary<string, TypeDef> types,
        IReadOnlyDictionary<string, LocalDeclaration> localDeclarations, ReferenceMetadataIndex refs,
        TypeNode.Fqn enclosingOwner, bool projectOnly = false,
        IReadOnlyDictionary<JsonObject, NativeSlot> nativeSlots = null)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["name"]) is string typeName
                    && obj["methods"] is JsonArray methods
                    && types.TryGetValue(typeName, out var typeDef)
                    && ReferenceEquals(typeDef.Methods, methods))
                {
                    var ownerArgs = Enumerable.Range(0, typeDef.TypeParamCount)
                        .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
                    enclosingOwner = new TypeNode.Fqn(typeName,
                        ownerArgs.Length == 0 ? null : ownerArgs);
                }
                var ownerBefore = DeclaringOwner(obj)?.DeepClone();
                Bind(obj, types, localDeclarations, refs, enclosingOwner, projectOnly, nativeSlots);
                // The early projection still carries own-first Kotlin inner arguments. Declaration-relative
                // result slots close only after inner applications have their physical argument order.
                if (!projectOnly && !JsonNode.DeepEquals(ownerBefore, DeclaringOwner(obj)))
                    ConstructedMemberReturnSubstitution.ApplyCall(obj);
                foreach (var kv in obj)
                    if (kv.Value != null) Walk(kv.Value, types, localDeclarations, refs, enclosingOwner, projectOnly, nativeSlots);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item != null) Walk(item, types, localDeclarations, refs, enclosingOwner, projectOnly, nativeSlots);
                break;
        }
    }

    static JsonNode DeclaringOwner(JsonObject call) => Str(call["k"]) switch
    {
        "clrInstance" or "clrPropGet" or "clrPropSet" or "clrEventAdd" or "clrEventRemove" => call["type"],
        "newBoundClrDelegate" => call["clrType"],
        "callInstance" or "newBoundDelegate" => call["ownerType"],
        _ => null,
    };

    static void Bind(JsonObject call, Dictionary<string, TypeDef> types,
        IReadOnlyDictionary<string, LocalDeclaration> localDeclarations, ReferenceMetadataIndex refs,
        TypeNode.Fqn enclosingOwner, bool projectOnly, IReadOnlyDictionary<JsonObject, NativeSlot> nativeSlots)
    {
        var kind = Str(call["k"]);
        if (kind is not ("callInstance" or "newBoundDelegate" or "newBoundClrDelegate"
            or "clrInstance" or "clrPropGet" or "clrPropSet" or "clrEventAdd" or "clrEventRemove")) return;
        // Some earlier bir2cir passes synthesize a call whose CLR declaration owner and dispatch have already been
        // selected. Rebinding such a call from its receiver hierarchy would undo that decision (in particular, an
        // exact covariant-interface bridge would call its own interface slot and recurse).
        if (Bool(call["clrOwnerResolved"])) return;
        var ownerSlot = kind switch
        {
            "clrInstance" or "clrPropGet" or "clrPropSet" or "clrEventAdd" or "clrEventRemove" => "type",
            "newBoundClrDelegate" => "clrType",
            _ => "ownerType",
        };
        if (TypeJson.Read(call[ownerSlot]) is not TypeNode.Fqn owner) return;
        // FIR can state the member's OPEN declaration owner while the receiver already carries the constructed
        // use-site type.  For example, `TargetList<String> : List<T>` may surface `Count` as owned by `List<type#0>`
        // even in a non-generic caller.  That type variable belongs to the declaration hierarchy, not the caller's
        // lexical frame.  Project the named declaration through the receiver's exact static hierarchy here, while
        // both `sty` and the local declarations are still available.  A receiver may reach the same generic owner
        // through more than one construction; only a unique constructed spec is authoritative, so ambiguity stays
        // unresolved instead of being guessed from arguments or expression values. This applies to `super` too:
        // its immediate Kotlin superclass is preserved, but its owner type variables must be constructed before the
        // class-only lookup below can identify the exact CLR MethodDef. Interface-qualified `super<I>` retains that
        // projected interface owner and is returned unchanged by the dedicated guard below.
        var projectionRoot = TypeJson.Read(call["recv"]?["sty"]) as TypeNode.Fqn;
        // A bare `this` without expression `sty` uses the enclosing class declaration frame as its receiver,
        // both for ordinary inherited calls and for super calls. A downstream `Derived : Base<String>` may have a BIR
        // member fact naming the open declaration Base<T> (or even Base without arguments): the current hierarchy, not the
        // argument expression, is the authoritative construction.
        if (projectionRoot == null && (Bool(call["super"]) || Str(call["recv"]?["k"]) == "this"))
            projectionRoot = enclosingOwner;
        if (projectionRoot != null
            && (projectionRoot.Name != owner.Name || owner.Args == null && projectionRoot.Args != null))
        {
            var projectedOwners = ConstructedOwners(projectionRoot, owner.Name, types, refs, sourceFrames: projectOnly);
            if (projectedOwners.Count == 1)
            {
                owner = projectedOwners[0];
                call[ownerSlot] = TypeJson.Write(owner);
                if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(owner);
            }
        }
        if (projectOnly)
        {
            BindSourceDeclarationCall(call, owner, types, localDeclarations, refs);
            owner = TypeJson.Read(call[ownerSlot]) as TypeNode.Fqn ?? owner;
            BindNativeSlotCall(call, owner, types, nativeSlots, refs);
            return;
        }
        // The CLR-shaped nodes have already crossed MemberCallSubstitution. Their declaration descriptor and member
        // kind are resolved later by ClrMemberResolution; this pass owns only the constructed declaring owner.
        if (kind is "newBoundClrDelegate" or "clrInstance" or "clrPropGet" or "clrPropSet"
            or "clrEventAdd" or "clrEventRemove") return;
        if (!types.ContainsKey(owner.Name)
            && !refs.TryReferenceTypeShape(owner, out _, out _, out _, out _)) return;
        if (Str(call["method"]) is not string method) return;
        var hasPropertyIdentity = KotlinPropertyAccessors.TryCallIdentity(
            call, out var propertyName, out var propertyAccessor);
        if (!hasPropertyIdentity)
        {
            propertyName = null;
            propertyAccessor = null;
        }

        var methodArity = (call["typeArgs"] as JsonArray)?.Count ?? 0;
        var sig = ReadTypes(call["sig"] as JsonArray);
        var paramCount = (call["args"] as JsonArray)?.Count ?? -1;

        // A callable-reference forwarding closure can retain the receiver's derived Kotlin owner and use-site
        // signature even though its frontend-selected declaration identity names a local or referenced base MethodDef.
        // Project that exact owner through the receiver hierarchy before any structural declaration lookup. For a
        // local declaration, also copy its already-lowered physical parameter ABI. The identity selects the
        // declaration; this walk contributes only its constructed owner arguments and refuses a disagreeing diamond.
        if (Str(call[DeclarationIdentityBinding.Key]) is string declarationId)
        {
            var localSelection = localDeclarations.TryGetValue(declarationId, out var localDeclaration);
            var selectedPhysicalOwner = localSelection ? localDeclaration.Owner : null;
            var hasSelectedOwner = localSelection || refs.TryDeclarationIdentity(
                declarationId, out _, out selectedPhysicalOwner, out _, out _);
            if (hasSelectedOwner)
            {
                var selectedOwners = ConstructedOwners(owner, selectedPhysicalOwner, types, refs);
                if (selectedOwners.Count == 1)
                {
                    owner = selectedOwners[0];
                    call[ownerSlot] = TypeJson.Write(owner);
                    if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(owner);
                    if (localSelection && localDeclaration.Method["params"] is JsonArray selectedParameters)
                    {
                        var selectedSignature = new JsonArray(selectedParameters.OfType<JsonObject>()
                            .Select(parameter => parameter["type"]?.DeepClone()
                                ?? throw new InvalidOperationException(
                                    $"Local declaration '{declarationId}' has an untyped parameter"))
                            .ToArray());
                        call["sig"] = selectedSignature;
                        sig = ReadTypes(selectedSignature);
                    }
                }
            }
        }

        // A call can already name its exact declaration owner yet still lack the CLR dispatch bit. This is especially
        // visible cross-module when a Kotlin-final accessor implements an existential interface slot and is therefore
        // virtual in metadata. Consume that declaration fact here. With no BIR signature, require a unique
        // name/method-arity/parameter-count declaration; never guess among overloads.
        if (!Bool(call["super"]) && paramCount >= 0
            && (DeclaresVirtual(types.GetValueOrDefault(owner.Name), method, methodArity, sig, paramCount,
                    propertyName, propertyAccessor)
                || (propertyAccessor != null
                    ? refs.DeclaresVirtualInstancePropertyAccessor(owner.Name, propertyName, propertyAccessor,
                        methodArity, sig, paramCount, owner.Args ?? Array.Empty<TypeNode>())
                    : refs.DeclaresVirtualInstanceMember(owner.Name, method, methodArity, sig, paramCount))))
            call["virtual"] = true;

        if (sig == null) return; // inherited owner binding requires the declaration signature

        // The static receiver owner can itself declare the exact slot while also carrying the Kotlin override closure.
        // That closure records semantic ancestry; it does not ask CIR to call the base/interface declaration. Retargeting
        // a covariant concrete method (`Derived.m(): Derived`) to its interface slot (`Base.m(): Base`) changes the
        // physical return type and makes an immediately-narrow consumer unverifiable. Prefer the real declaration on
        // the current owner. A local fake override is not emitted as a slot, so it deliberately falls through to the
        // hierarchy search.
        var ownDeclarationSig = ExactDeclarationSignature(
            types.GetValueOrDefault(owner.Name), method, methodArity, sig, owner.Args,
            propertyName, propertyAccessor);
        if (ownDeclarationSig != null)
        {
            call["sig"] = ownDeclarationSig;
            return;
        }
        // A Kotlin `super` call names its immediate super CLASS as the receiver owner, but that class need not
        // redeclare the selected member. The CLR call operand must name the nearest CLASS MethodDef that actually
        // implements it. In particular, an interface implemented by the immediate class can expose the same abstract
        // slot; letting ilemit walk interfaces before the base class then turns a valid `super.p` into `call` on an
        // abstract accessor. Resolve the class chain here, while Kotlin property identity and the complete local type
        // graph are available. Never consider interfaces for this class-super lookup: they describe slot obligations,
        // not the non-virtual implementation selected by `super`.
        if (Bool(call["super"]) && !IsInterface(owner, types, refs))
        {
            // Kotlin class-super dispatch is non-virtual regardless of whether the selected MethodDef also declares
            // a CLR virtual slot. Earlier property/member routing may have copied that declaration flag onto the call;
            // this is the pass that owns the exact class MethodDef decision, so normalize the dispatch fact here.
            call["virtual"] = false;
            foreach (var baseOwner in BaseClassChain(owner, types, refs))
            {
                var localSig = ExactDeclarationSignature(
                    types.GetValueOrDefault(baseOwner.Name), method, methodArity, sig, baseOwner.Args,
                    propertyName, propertyAccessor);
                var referenced = propertyAccessor != null
                    ? refs.DeclaresExactInstancePropertyAccessor(baseOwner.Name, propertyName, propertyAccessor,
                        methodArity, sig, baseOwner.Args ?? Array.Empty<TypeNode>())
                    : refs.DeclaresExactInstanceMember(baseOwner.Name, method, methodArity, sig,
                        baseOwner.Args ?? Array.Empty<TypeNode>());
                if (localSig == null && !referenced) continue;
                call["ownerType"] = TypeJson.Write(baseOwner);
                if (localSig != null) call["sig"] = localSig;
                return;
            }
            // A well-formed frontend call always resolves above. Leave an incomplete external graph untouched so the
            // later exact foreign-member resolver can diagnose it from its compile references; do not fall through to
            // the ordinary interface-inclusive lookup and silently change class-super semantics.
            return;
        }
        // `super<I>.m()` names an interface default implementation directly. Its owner is already the selected
        // interface declaration; inherited class-member binding must not reinterpret it through another branch.
        if (Bool(call["super"])) return;
        // A local interface can expose an inherited external default implementation as a fake override. That fake
        // method is not emitted and therefore cannot be a bound-delegate target, but its inheritedImplementation
        // carrier is the frontend's exact declaration fact. Retarget the callable reference to that declaration now;
        // leaving it on the local owner makes the later exact local lookup (correctly) find no MethodDef.
        if (kind == "newBoundDelegate"
            && ExactInheritedDefault(types.GetValueOrDefault(owner.Name), method, methodArity, sig, owner.Args)
                is JsonObject inherited
            && inherited["implementation"] is JsonObject implementation
            && TypeJson.Read(implementation["owner"]) is TypeNode.Fqn implementationOwner
            && Str(implementation["member"]) is string implementationMember)
        {
            // kotc's implementation fact identifies the declaration, not its use-site instantiation. Project that
            // identity through the already-constructed receiver hierarchy so `Local<T> : External<T>` becomes
            // `External<T>`, rather than throwing away T by copying the carrier's open declaration owner.
            var projectedOwners = ConstructedOwners(owner, implementationOwner.Name, types, refs);
            if (projectedOwners.Count != 1) return;
            var projectedOwner = projectedOwners[0];
            call["ownerType"] = TypeJson.Write(projectedOwner);
            call["calleeOwner"] = TypeJson.Write(projectedOwner);
            call["method"] = implementationMember;
            call["virtual"] = true;
            return;
        }
        if (propertyAccessor != null
                ? refs.DeclaresExactInstancePropertyAccessor(owner.Name, propertyName, propertyAccessor,
                    methodArity, sig, owner.Args ?? Array.Empty<TypeNode>())
                : refs.DeclaresExactInstanceMember(owner.Name, method, methodArity, sig,
                    owner.Args ?? Array.Empty<TypeNode>()))
            return;

        var hierarchy = ReachableTypes(owner, types, refs).ToList();

        // Prefer the frontend's semantic override/declaration fact, but still verify that
        // the reachable constructed declaration exactly matches this call signature.
        var overrideOwners = new HashSet<string>(StringComparer.Ordinal);
        if (call["overrides"] is JsonArray ovs)
            foreach (var ov in ovs.OfType<JsonObject>())
                if ((propertyAccessor == null
                        ? Str(ov["kind"]) is null or "method"
                        : Str(ov["kind"]) == (propertyAccessor == "get" ? "getter" : "setter"))
                    && Str(ov["member"]) == (propertyName ?? method)
                    && Str(ov["owner"]?["name"]) is string declared)
                    overrideOwners.Add(declared);

        var candidates = hierarchy
            .Where(r => !r.Type.Equals(owner))
            .Where(r => overrideOwners.Count == 0 || overrideOwners.Contains(r.Type.Name))
            .Where(r => DeclaresExact(types.GetValueOrDefault(r.Type.Name), method, methodArity, sig, r.Type.Args,
                    propertyName, propertyAccessor)
                || (propertyAccessor != null
                    ? refs.DeclaresExactInstancePropertyAccessor(r.Type.Name, propertyName, propertyAccessor,
                        methodArity, sig, r.Type.Args ?? Array.Empty<TypeNode>())
                    : refs.DeclaresExactInstanceMember(r.Type.Name, method, methodArity, sig,
                        r.Type.Args ?? Array.Empty<TypeNode>())))
            .ToList();

        if (overrideOwners.Count > 0)
        {
            // `overrides` is a closure, not a single direct-parent pointer: Element.get may carry both
            // Element.get and CoroutineContext.get.  Select the unique nearest declaration in that
            // semantic closure; only same-depth collisions are genuinely ambiguous.
            if (candidates.Count == 0) return;
            var overrideDepth = candidates.Min(c => c.Depth);
            var direct = candidates.Where(c => c.Depth == overrideDepth)
                .Select(c => c.Type).Distinct().ToList();
            if (direct.Count != 1) return;
            call["ownerType"] = TypeJson.Write(direct[0]);
            if (ExactDeclarationSignature(types.GetValueOrDefault(direct[0].Name), method, methodArity,
                    sig, direct[0].Args, propertyName, propertyAccessor) is { } directDeclarationSig)
                call["sig"] = directDeclarationSig;
            if (IsInterface(direct[0], types, refs)) call["virtual"] = true;
            if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(direct[0]);
            return;
        }

        if (candidates.Count == 0) return;
        var nearestDepth = candidates.Min(c => c.Depth);
        var nearest = candidates.Where(c => c.Depth == nearestDepth)
            .Select(c => c.Type).Distinct().ToList();
        if (nearest.Count != 1) return;
        call["ownerType"] = TypeJson.Write(nearest[0]);
        if (ExactDeclarationSignature(types.GetValueOrDefault(nearest[0].Name), method, methodArity,
                sig, nearest[0].Args, propertyName, propertyAccessor) is { } nearestDeclarationSig)
            call["sig"] = nearestDeclarationSig;
        if (IsInterface(nearest[0], types, refs)) call["virtual"] = true;
        if (kind == "newBoundDelegate") call["calleeOwner"] = TypeJson.Write(nearest[0]);
    }

    // Reference hierarchy edges can use the index's semantic spelling while the selected declaration names
    // an exact CLR TypeDef. Match only through the recorded arity-aware identity map, never by stripping names
    // or borrowing argument types. Keep the selected owner spelling and the hierarchy's constructed arguments.
    static List<TypeNode.Fqn> ConstructedOwners(TypeNode.Fqn start, string selectedOwner,
        Dictionary<string, TypeDef> types, ReferenceMetadataIndex refs, bool sourceFrames = false) =>
        ReachableTypes(start, types, refs, sourceFrames)
            .Where(candidate => candidate.Type.Name == selectedOwner
                || !types.ContainsKey(candidate.Type.Name) && !types.ContainsKey(selectedOwner)
                    && refs.TryExactPhysicalTypeName(candidate.Type.Name, candidate.Type.Args?.Length ?? 0,
                        out var physicalOwner) && physicalOwner == selectedOwner)
            .Select(candidate => new TypeNode.Fqn(selectedOwner, candidate.Type.Args))
            .GroupBy(SupertypeGraph.TypeKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

    static IEnumerable<Reachable> ReachableTypes(TypeNode.Fqn start, Dictionary<string, TypeDef> types,
        ReferenceMetadataIndex refs, bool sourceFrames = false)
    {
        var queue = new Queue<Reachable>();
        var seen = new HashSet<TypeNode.Fqn>();
        queue.Enqueue(new Reachable(start, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Type)) continue;
            yield return current;
            TypeNode.Fqn baseType;
            TypeNode.Fqn[] interfaces;
            int typeParamCount;
            if (types.TryGetValue(current.Type.Name, out var def))
            {
                typeParamCount = def.TypeParamCount;
                baseType = def.Base;
                interfaces = def.Interfaces;
            }
            else if (sourceFrames && refs.TryReferenceSourceTypeShape(current.Type, out typeParamCount, out baseType,
                         out interfaces)) { }
            else if (!sourceFrames && refs.TryReferenceTypeShape(current.Type, out typeParamCount, out _, out baseType,
                         out interfaces)) { }
            else continue;
            var args = EffectiveArgs(current.Type, typeParamCount);
            if (args == null) continue;
            if (baseType is not null)
                queue.Enqueue(new Reachable((TypeNode.Fqn)SubstOwnerTvs(baseType, args), current.Depth + 1));
            foreach (var iface in interfaces)
                queue.Enqueue(new Reachable((TypeNode.Fqn)SubstOwnerTvs(iface, args), current.Depth + 1));
        }
    }

    // The constructed base-CLASS chain only, including start. Each edge is expressed in its parent's type-parameter
    // frame, so close it through the current construction before continuing (`Middle<T> : Base<List<T>>`).
    static IEnumerable<TypeNode.Fqn> BaseClassChain(TypeNode.Fqn start,
        Dictionary<string, TypeDef> types, ReferenceMetadataIndex refs)
    {
        var current = start;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(SupertypeGraph.TypeKey(current)))
        {
            yield return current;
            TypeNode.Fqn baseType;
            int typeParamCount;
            if (types.TryGetValue(current.Name, out var def))
            {
                typeParamCount = def.TypeParamCount;
                baseType = def.Base;
            }
            else if (refs.TryReferenceTypeShape(current, out typeParamCount, out _, out baseType, out _)) { }
            else yield break;
            if (baseType == null) yield break;
            var args = EffectiveArgs(current, typeParamCount);
            if (args == null) yield break;
            current = (TypeNode.Fqn)SubstOwnerTvs(baseType, args);
        }
    }

    static bool IsInterface(TypeNode.Fqn owner, Dictionary<string, TypeDef> types, ReferenceMetadataIndex refs)
    {
        if (types.GetValueOrDefault(owner.Name)?.Kind == "interface") return true;
        return refs.TryReferenceTypeShape(owner, out _, out var kind, out _, out _) && kind == "interface";
    }

    static TypeNode[] EffectiveArgs(TypeNode.Fqn type, int count)
    {
        if (count == 0) return Array.Empty<TypeNode>();
        return type.Args is { } args && args.Length == count ? args : null;
    }

    static bool DeclaresExact(TypeDef def, string name, int methodArity, TypeNode[] callSig, TypeNode[] ownerArgs,
        string propertyName, string propertyAccessor)
        => ExactDeclarationSignature(def, name, methodArity, callSig, ownerArgs,
            propertyName, propertyAccessor) != null;

    static JsonArray ExactDeclarationSignature(TypeDef def, string name, int methodArity,
        TypeNode[] callSig, TypeNode[] ownerArgs, string propertyName, string propertyAccessor)
    {
        var match = ExactMethod(def, name, methodArity, callSig, ownerArgs,
            propertyName, propertyAccessor, fakeOverride: false);
        return match?["params"] is JsonArray ps
            ? new JsonArray(ps.OfType<JsonObject>()
                .Select(p => TypeJson.Write(TypeJson.Read(p["type"]))).ToArray())
            : null;
    }

    static JsonObject ExactMethod(TypeDef def, string name, int methodArity,
        TypeNode[] callSig, TypeNode[] ownerArgs, string propertyName, string propertyAccessor,
        bool fakeOverride)
    {
        if (def?.Methods == null) return null;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return null;

        var matches = new List<JsonObject>();
        foreach (var method in def.Methods.OfType<JsonObject>())
        {
            if (KotlinPropertyAccessors.IsPhysicalSlotBridge(method)) continue;
            if (propertyAccessor != null)
            {
                if (!KotlinPropertyAccessors.TryIdentity(method, out var candidateProperty, out var candidateAccessor)
                    || candidateProperty != propertyName || candidateAccessor != propertyAccessor) continue;
            }
            else if (Str(method["name"]) != name
                || KotlinPropertyAccessors.TryIdentity(method, out _, out _)) continue;
            if (Bool(method["fakeOverride"]) != fakeOverride) continue;
            if (((method["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) continue;
            if (method["params"] is not JsonArray ps || ps.Count != callSig.Length) continue;
            var exact = true;
            for (var i = 0; i < ps.Count; i++)
            {
                var declared = ps[i] is JsonObject p ? TypeJson.Read(p["type"]) : null;
                // kotc preserves the formal descriptor where FIR exposes it, but some inherited call sites carry the
                // same declaration after owner substitution (`Base<T>.m(T)` reached through `Derived : Base<Leaf>`
                // arrives as `m(Leaf)`). Both are exact descriptions of one declaration; accept only raw identity or
                // the hierarchy-derived owner substitution, never expression assignability.
                var substituted = declared == null ? null : SubstOwnerTvs(declared, ownerArgs);
                if (declared == null || (declared != callSig[i]
                    && substituted != callSig[i]
                    // An override of `Base<T>.m(List<T?>)` may itself be declared as `m(List<String?>)`, while the
                    // CLR slot is rewritten to the base declaration's uniform `IReadOnlyList<object>`. The call still
                    // carries the Kotlin declaration descriptor (`IReadOnlyList<string>`). This is one declaration
                    // precisely when the emitted vector is its NESTED object-erasure image; a bare `object` is not
                    // accepted here because that would turn ordinary argument assignability into member selection.
                    && !IsNestedObjectErasureOf(declared, callSig[i])
                    && !IsNestedObjectErasureOf(substituted, callSig[i])))
                {
                    exact = false;
                    break;
                }
            }
            if (exact) matches.Add(method);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    static JsonObject ExactInheritedDefault(TypeDef def, string name, int methodArity,
        TypeNode[] callSig, TypeNode[] ownerArgs)
    {
        if (def?.InheritedDefaultMethods == null) return null;
        ownerArgs ??= def.TypeParamCount == 0 ? Array.Empty<TypeNode>() : null;
        if (ownerArgs == null || ownerArgs.Length != def.TypeParamCount) return null;
        var matches = def.InheritedDefaultMethods.OfType<JsonObject>().Where(fact =>
        {
            if (Str(fact["member"]) != name || fact["params"] is not JsonArray ps
                || ps.Count != callSig.Length || fact["implementation"] is not JsonObject implementation
                || ((implementation["typeParams"] as JsonArray)?.Count ?? 0) != methodArity) return false;
            return ps.Select(TypeJson.Read).Select((declared, i) =>
            {
                var substituted = declared == null ? null : SubstOwnerTvs(declared, ownerArgs);
                return declared != null && (declared == callSig[i] || substituted == callSig[i]
                    || IsNestedObjectErasureOf(declared, callSig[i])
                    || IsNestedObjectErasureOf(substituted, callSig[i]));
            }).All(match => match);
        }).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static bool IsNestedObjectErasureOf(TypeNode candidate, TypeNode source)
    {
        if (candidate == null || source == null || candidate.Equals(source)) return candidate != null;
        return (candidate, source) switch
        {
            (TypeNode.Fqn { Args: { } ca } cf, TypeNode.Fqn { Args: { } sa } sf)
                when cf.Name == sf.Name && ca.Length == sa.Length
                => ca.Zip(sa, IsObjectErasureOf).All(x => x),
            (TypeNode.Array c, TypeNode.Array s) => IsObjectErasureOf(c.Elem, s.Elem),
            (TypeNode.Nullable c, TypeNode.Nullable s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.Oblivious c, TypeNode.Oblivious s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.ByRef c, TypeNode.ByRef s) => IsObjectErasureOf(c.Of, s.Of),
            (TypeNode.Fn c, TypeNode.Fn s)
                when c.Params.Length == s.Params.Length && c.Suspend == s.Suspend
                     && (c.Recv == null) == (s.Recv == null)
                => IsObjectErasureOf(c.Ret, s.Ret)
                   && c.Params.Zip(s.Params, IsObjectErasureOf).All(x => x)
                   && (c.Recv == null || IsObjectErasureOf(c.Recv, s.Recv)),
            _ => false,
        };
    }

    static bool IsObjectErasureOf(TypeNode candidate, TypeNode source)
    {
        if (candidate.Equals(source)) return true;
        if (candidate is TypeNode.Fqn { Name: "object", Args: null }) return true;
        return IsNestedObjectErasureOf(candidate, source);
    }

    static bool DeclaresVirtual(TypeDef def, string name, int methodArity, TypeNode[] callSig, int paramCount,
        string propertyName, string propertyAccessor)
    {
        if (def?.Methods == null) return false;
        var matches = def.Methods.OfType<JsonObject>()
            .Where(method => !KotlinPropertyAccessors.IsPhysicalSlotBridge(method)
                && (propertyAccessor != null
                    ? KotlinPropertyAccessors.TryIdentity(method, out var candidateProperty, out var candidateAccessor)
                        && candidateProperty == propertyName && candidateAccessor == propertyAccessor
                    : Str(method["name"]) == name && !KotlinPropertyAccessors.TryIdentity(method, out _, out _))
                && ((method["typeParams"] as JsonArray)?.Count ?? 0) == methodArity
                && method["params"] is JsonArray ps && ps.Count == paramCount)
            .Where(method => callSig == null || callSig.Length == paramCount
                && method["params"] is JsonArray ps
                && ps.Select((p, i) => p is JsonObject parameter
                    && TypeJson.Read(parameter["type"]) == callSig[i]).All(x => x))
            .ToList();
        return matches.Count == 1 && Bool(matches[0]["virtual"]);
    }

    static TypeNode[] ReadTypes(JsonArray array)
    {
        if (array == null) return null;
        var result = new TypeNode[array.Count];
        for (var i = 0; i < array.Count; i++)
            if ((result[i] = TypeJson.Read(array[i])) == null) return null;
        return result;
    }

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Projection p => new TypeNode.Projection(p.Variance, SubstOwnerTvs(p.Of, args)),
        TypeNode.Nullable n => SubstOwnerTvs(n.Of, args) switch {
            TypeNode.Nullable nullable => nullable,
            var inner => new TypeNode.Nullable(inner),
        },
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstOwnerTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args), fn.Clr,
            fn.Ctx?.Select(context => SubstOwnerTvs(context, args)).ToArray()),
        _ => type,
    };

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
}

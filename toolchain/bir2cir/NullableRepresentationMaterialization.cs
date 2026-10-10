using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materializes signature frames and independently owned nonvirtual implementation frames. Virtual body-only
// specialization is separate: it must not grow a published virtual slot. Runs after the Kotlin declaration snapshot.
static class NullableRepresentationMaterialization
{
    public static void Apply(IEnumerable<JsonNode> inputs, ValueTypeOracle isValue, ReferenceMetadataIndex references = null,
        Func<TypeNode.Fqn, bool, NullableRepresentationFrame, TypeNode> argumentHead = null,
        GenericRepresentationPolicy policy = null)
    {
        var roots = inputs.ToArray();
        var importedMethods = new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal);
        var importedTypes = references == null ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
            : new Dictionary<string, NullableRepresentationFrame>(references.NullableTypeFrames, StringComparer.Ordinal);
        void FindReferencedCalls(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Text(obj["k"]) != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                    && references != null)
                {
                    if (references.NullableMethodFrame(id) is { } frame) importedMethods[id] = frame;
                    if (references.TryDeclarationIdentity(id, out _, out var owner, out _, out _)
                        && !importedTypes.ContainsKey(owner))
                        // No companions means an identity declaration frame, not an absent owner correspondence.
                        importedTypes[owner] = new NullableRepresentationFrame(references.OwnerArity(owner), Array.Empty<int>());
                }
                foreach (var (key, value) in obj)
                    if (key != "attrs") FindReferencedCalls(value);
            }
            else if (node is JsonArray array)
                foreach (var item in array) FindReferencedCalls(item);
        }
        foreach (var root in roots) FindReferencedCalls(root);
        var applicationFrames = new InnerApplicationFrames(roots, references);
        var demands = NullableRepresentationDemand.Collect(roots, importedTypes, importedMethods, policy, applicationFrames);
        var localBindings = NullableRepresentationDemand.BindLocalFunctions(roots);
        // Frames refer to immutable source arities. Snapshot before any declaration's parameters are expanded.
        var ownerFrames = demands.ToDictionary(owner => owner.Declaration, owner => owner.Frame);
        var methodFrames = demands.SelectMany(owner => owner.Methods)
            .ToDictionary(method => method.Declaration, method => method.Frame);
        // Check before editing any declaration. A caller that needs body specialization cannot use this entry
        // until that specialization has given its implementation an explicit frame.
        foreach (var owner in demands)
        {
            var ownerName = Text(owner.Declaration["name"]) ?? Text(owner.Declaration["fileClass"]);
            RequireCovered(owner.Body.Type, ownerFrames[owner.Declaration], ownerName + " (type)");
            foreach (var method in owner.Methods)
            {
                var methodName = ownerName + "." + Text(method.Declaration["name"]);
                RequireCovered(method.Body.Type, ownerFrames[owner.Declaration], methodName + " (type)");
                if (method.Declaration["body"] is not JsonArray)
                    RequireCovered(method.Body.Method, method.Frame, methodName + " (method)");
            }
        }
        var types = new Dictionary<string, NullableRepresentationFrame>(importedTypes, StringComparer.Ordinal);
        foreach (var owner in demands.Where(d => d.IsTypeDeclaration))
            types[Text(owner.Declaration["name"])] = ownerFrames[owner.Declaration];
        var methods = new Dictionary<string, NullableRepresentationFrame>(importedMethods, StringComparer.Ordinal);
        foreach (var method in demands.SelectMany(d => d.Methods))
            if (Text(method.Declaration[DeclarationIdentityBinding.Key]) is string id) methods[id] = method.Frame;
        foreach (var root in roots.OfType<JsonObject>()) NullableGenericErasure.PreserveSourceFacts(root, isValue);
        var empty = new NullableRepresentationFrame(0, Array.Empty<int>());
        var applicationTypes = applicationFrames.Project(types);
        var dispatchTypes = applicationFrames.Project(types, retainPhysicalOrder: true);
        NullableRepresentationTypes Mapping(NullableRepresentationFrame owner, NullableRepresentationFrame method,
            string ownerName = null) => new(owner, method, applicationTypes, isValue, argumentHead, policy,
                ownerName == null ? owner : dispatchTypes.GetValueOrDefault(ownerName) ?? owner,
                ownerName == null ? owner : applicationTypes.GetValueOrDefault(ownerName) ?? owner,
                nativeOwnerSlots: policy?.UsesNativeOwnerSlots(ownerName) == true);
        var declarations = demands.SelectMany(owner => owner.Methods.Select(method => (owner, method)))
            .Where(pair => Text(pair.method.Declaration[DeclarationIdentityBinding.Key]) != null)
            .ToDictionary(pair => Text(pair.method.Declaration[DeclarationIdentityBinding.Key]),
                pair => Mapping(pair.owner.Frame, pair.method.Frame,
                    Text(pair.owner.Declaration["name"]) ?? Text(pair.owner.Declaration["fileClass"])), StringComparer.Ordinal);
        var localDeclarations = demands.SelectMany(owner => owner.Methods.Where(method => method.IsLocal)
                .Select(method => (owner, method)))
            .ToDictionary(pair => pair.method.Declaration,
                pair => Mapping(pair.owner.Frame, pair.method.Frame, Text(pair.owner.Declaration["name"])));
        foreach (var (id, frame) in importedMethods)
            if (!declarations.ContainsKey(id) && references.TryDeclarationIdentity(id, out _, out var owner, out _, out _))
                declarations[id] = Mapping(types.GetValueOrDefault(owner) ?? empty, frame, owner);
        // Constructor delegation descriptors belong to the selected base/this declaration, not to the
        // subclass's lexical frame. Snapshot this relation before rewriting any owner's base type.
        var constructorMappings = new Dictionary<JsonObject, NullableRepresentationTypes>();
        foreach (var owner in demands)
            if (owner.Declaration["ctors"] is JsonArray constructors)
                foreach (var constructor in constructors.OfType<JsonObject>())
                {
                    var targetFrame = constructor["thisArgs"] is JsonArray ? owner.Frame
                        : TypeJson.Read(owner.Declaration["base"]) is TypeNode.Fqn baseType
                            ? types.GetValueOrDefault(baseType.Name) : null;
                    constructorMappings[constructor] = targetFrame == null ? null
                        : Mapping(targetFrame, empty, constructor["thisArgs"] is JsonArray
                            ? Text(owner.Declaration["name"])
                            : TypeJson.OwnerName(owner.Declaration["base"]));
                }
        NullableRepresentationTypes DeclarationMapping(JsonObject use)
        {
            if (use["sharedCellTypeParams"] is JsonArray
                && TypeJson.Read(use["sharedCellType"]) is TypeNode.Fqn cell)
                return Mapping(types[cell.Name], empty);
            if (localBindings.TryGetValue(use, out var localDeclaration)) return localDeclarations[localDeclaration];
            if (constructorMappings.TryGetValue(use, out var constructorMapping)) return constructorMapping;
            if (Text(use[DeclarationIdentityBinding.Key]) is string id && declarations.TryGetValue(id, out var selected))
            {
                // A fake override preserves the implementing declaration's ID, but its explicit member
                // descriptors are written by kotc in the accessed owner's frame. The receiver/declaration
                // relation is already stated in ownerType; the ID must not substitute the base owner's arity.
                var descriptorOwner = Text(use["k"]) == "new" ? use["type"] : use["ownerType"] ?? use["owner"];
                if (use["memberOwnerTypeParams"] is JsonArray
                    && TypeJson.Read(descriptorOwner) is TypeNode.Fqn descriptorType
                    && types.TryGetValue(descriptorType.Name, out var descriptorFrame))
                    return Mapping(descriptorFrame, selected.MethodFrame, descriptorType.Name);
                return selected;
            }
            // A Kotlin declaration may have no generic parameters of its own while its signature contains
            // constructed Kotlin types that require companions. Its identity establishes source vocabulary;
            // absence of a method frame does not make the signature a foreign CLR descriptor.
            if (Text(use[DeclarationIdentityBinding.Key]) is string referencedId
                && references != null
                && references.TryDeclarationIdentity(referencedId, out _, out var referencedOwner, out _, out _))
                return Mapping(types.GetValueOrDefault(referencedOwner), references.NullableMethodFrame(referencedId), referencedOwner);
            var ownerType = Text(use["k"]) == "new" ? use["type"] : use["ownerType"] ?? use["owner"];
            if (Text(use["k"]) == "new" && TypeJson.Read(ownerType) is TypeNode.Fqn constructorOwner
                && references != null
                && references.KotlinConstructorOwnerFrames.TryGetValue(constructorOwner.Name, out var constructorFrame))
                return Mapping(constructorFrame, empty, constructorOwner.Name);
            return TypeJson.Read(ownerType) is TypeNode.Fqn owner && types.TryGetValue(owner.Name, out var frame)
                // Knowing the owner does not establish a zero-arity method frame. Unbound method variables in
                // its declaration signature must not be interpreted as variables of a fictitious empty method.
                ? Mapping(frame, null, owner.Name) : null;
        }
        foreach (var owner in demands)
        {
            try
            {
                var frame = ownerFrames[owner.Declaration];
                var ownerName = Text(owner.Declaration["name"]) ?? Text(owner.Declaration["fileClass"]);
                var mapping = Mapping(frame, empty, ownerName);
                PreserveCompanionSourceBounds(owner.Declaration, frame, owner: true);
                PreserveEdges(owner.Declaration, mapping);
                if (owner.Declaration["fields"] is JsonArray fields)
                    foreach (var field in fields.OfType<JsonObject>())
                        if (!mapping.IsNativeField(field)) PreserveSlot(field, "type", "nullableGeneric", mapping);
                if (owner.Declaration["ctors"] is JsonArray constructors)
                    foreach (var constructor in constructors.OfType<JsonObject>())
                        PreserveParameters(constructor, mapping);
                // A generic extension property's type uses its accessor method's variables. The property row
                // carries the explicit association, so it must not be interpreted in a zero-arity facade frame.
                if (owner.Declaration["properties"] is JsonArray properties)
                    foreach (var property in properties.OfType<JsonObject>())
                    {
                        var association = Text(property["propertyAssociation"]);
                        var accessor = owner.Methods.FirstOrDefault(method => association != null
                            && Text(method.Declaration["propertyAssociation"]) == association
                            && Text(method.Declaration["propertyAccessor"]) == "get")
                            ?? owner.Methods.FirstOrDefault(method => association != null
                                && Text(method.Declaration["propertyAssociation"]) == association);
                        var propertyMapping = accessor == null ? mapping
                            : Mapping(frame, methodFrames[accessor.Declaration], ownerName);
                        PreserveSlot(property, "type", "nullableGeneric", propertyMapping);
                        Rewrite(property, propertyMapping, methods, DeclarationMapping);
                    }
                foreach (var key in owner.Declaration.Select(p => p.Key).ToArray())
                    if (key is not ("types" or "methods" or "properties" or "attrs" or "overrides" or "refTypes")
                        && !NullableRepresentationDemand.InheritedMemberKeys.Contains(key))
                    {
                        if (TypeJson.Read(owner.Declaration[key]) is TypeNode type)
                            owner.Declaration[key] = TypeJson.Write(mapping.Slot(type));
                        else Rewrite(owner.Declaration[key], mapping, methods, DeclarationMapping);
                    }
                foreach (var method in owner.Methods)
                {
                    var methodFrame = methodFrames[method.Declaration];
                    var methodMapping = Mapping(frame, methodFrame, ownerName);
                    // Inherited entries describe another implementation; its defining declaration owns metadata.
                    if (method.ImplementationKey == null)
                        PreserveCompanionSourceBounds(method.Declaration, methodFrame, owner: false);
                    PreserveParameters(method.Declaration, methodMapping);
                    PreserveSlot(method.Declaration, "ret", "nullableGenericRet", methodMapping);
                    if (method.ImplementationKey != null)
                    {
                        PreserveCompanionSourceBounds(method.Implementation, methodFrame, owner: false);
                        var inheritedMapping = Mapping(frame, methodFrame, ownerName);
                        foreach (var key in new[] { "params", "ret" })
                        {
                            if (TypeJson.Read(method.Declaration[key]) is TypeNode type)
                                method.Declaration[key] = TypeJson.Write(inheritedMapping.Slot(type));
                            else Rewrite(method.Declaration[key], inheritedMapping, methods, DeclarationMapping);
                        }
                        var implementationMapping = DeclarationMapping(method.Implementation);
                        RewriteImplementation(method.Implementation,
                            implementationMapping == null ? null : Mapping(
                                implementationMapping.OwnerFrame, methodFrame), inheritedMapping);
                        AppendParameters(method.Implementation, methodFrame, policy: policy);
                        method.Implementation["arity"] = methodFrame.PhysicalArity;
                        continue;
                    }
                    var splitBody = method.Body.Method.Except(methodFrame.NullableIndices).Any()
                        || method.Body.For("method", NullableRepresentationFrame.Role.Storage).Except(methodFrame.StorageIndices).Any()
                        || method.Body.For("method", NullableRepresentationFrame.Role.NullableStorage).Except(methodFrame.NullableStorageIndices).Any();
                    var source = splitBody ? (JsonObject)method.Declaration.DeepClone() : null;
                    if (splitBody) method.Declaration["body"] = new JsonArray();
                    Rewrite(method.Declaration, methodMapping, methods, DeclarationMapping);
                    AppendParameters(method.Declaration, methodFrame, policy: policy);
                    if (methodFrame.RequiresMetadata)
                        method.Declaration[NullableRepresentationTypes.MethodFrameKey] = methodFrame.ToJson().ToJsonString();
                    if (splitBody)
                        NullableBodyDispatch.Build(owner.Declaration, source, method.Declaration, frame, methodFrame,
                            method.Body, (helper, helperFrame) => {
                                Rewrite(helper, Mapping(frame, helperFrame, ownerName), methods, DeclarationMapping);
                                AppendParameters(helper, helperFrame, policy: policy);
                                helper[NullableRepresentationTypes.MethodFrameKey] = helperFrame.ToJson().ToJsonString();
                            });
                }
                AppendParameters(owner.Declaration, frame, "type", policy);
                if (owner.CapturedOwner != null)
                {
                    owner.Declaration["outerTypeParamCount"] = ownerFrames[owner.CapturedOwner.Declaration].PhysicalArity;
                    owner.Declaration.Remove("outerTypeParamOffset");
                }
                if (frame.RequiresMetadata)
                    KotlinSupertypesRecord.Merge(owner.Declaration,
                        new JsonObject { [NullableRepresentationFrame.MetadataKey] = frame.ToJson() });
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Nullable owner {Text(owner.Declaration["name"]) ?? Text(owner.Declaration["fileClass"])}: {error.Message}", error);
            }
        }
    }

    static void PreserveParameters(JsonObject declaration, NullableRepresentationTypes mapping)
    {
        if (declaration["params"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                PreserveSlot(parameter, "type", "nullableGeneric", mapping);
    }

    static void PreserveSlot(JsonObject declaration, string typeKey, string factKey,
        NullableRepresentationTypes mapping)
    {
        if (declaration[factKey] == null && TypeJson.Read(declaration[typeKey]) is TypeNode source
            && !source.Equals(mapping.Slot(source)))
            declaration[factKey] = TypeNode.ToJson(source);
    }

    static void RequireCovered(IEnumerable<int> indices, NullableRepresentationFrame frame, string declaration)
    {
        var missing = indices.Except(frame.NullableIndices).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Nullable frame materialization requires body specialization first: {declaration}, slots {string.Join(",", missing)}");
    }

    static void PreserveCompanionSourceBounds(JsonObject declaration, NullableRepresentationFrame frame, bool owner)
    {
        if (!frame.RequiresMetadata) return;
        var parameters = (JsonArray)declaration["typeParams"];
        var bounds = new JsonObject();
        for (var index = 0; index < frame.SourceArity; index++)
            bounds[index.ToString()] = parameters[index] is JsonObject parameter
                && parameter["constraints"] is JsonArray source ? source.DeepClone() : new JsonArray();
        if (owner)
            KotlinSupertypesRecord.Merge(declaration, new JsonObject { ["bounds"] = bounds });
        else
        {
            var key = NullableGenericErasure.MethodTypeParameterBoundsPre;
            var original = Text(declaration[key]) is string encoded ? JsonNode.Parse(encoded).AsObject()
                : new JsonObject { ["bounds"] = new JsonObject() };
            var retained = original["bounds"].AsObject();
            foreach (var bound in bounds)
                if (!retained.ContainsKey(bound.Key)) retained[bound.Key] = bound.Value.DeepClone();
            declaration[key] = original.ToJsonString();
        }
    }

    static void AppendParameters(JsonObject declaration, NullableRepresentationFrame frame) =>
        AppendParameters(declaration, frame, "method");

    static void AppendParameters(JsonObject declaration, NullableRepresentationFrame frame, string scope = "method",
        GenericRepresentationPolicy policy = null)
    {
        if (frame.PhysicalArity == 0) return;
        var source = (JsonArray)declaration["typeParams"];
        var expanded = ExpandParameters(source, frame, scope);
        // A Kotlin generic bound describes an erased classifier, not the original closed CLR construction.
        // Its storage representation implements the same existential interface even when the source arguments
        // have been unchecked-cast. Native generic bounds are not projected by this rule.
        foreach (var sourceIndex in frame.StorageIndices)
        {
            var seen = new HashSet<int>();
            IEnumerable<TypeNode> Bounds(int index)
            {
                if (!seen.Add(index) || source[index] is not JsonObject parameter
                    || parameter["constraints"] is not JsonArray constraints) yield break;
                foreach (var bound in constraints.Select(TypeJson.Read))
                    if (bound is TypeNode.Tv variable && variable.Scope == scope)
                    {
                        var slot = frame.PhysicalSlot(variable.I);
                        foreach (var inherited in Bounds(slot.SourceIndex)) yield return inherited;
                    }
                    else if (bound != null) yield return bound;
            }
            var projected = Bounds(sourceIndex).OfType<TypeNode.Fqn>()
                .Where(bound => bound.Args is { Length: > 0 } && policy?.OwnsValueSlots(bound.Name) == true)
                .Select(bound => (TypeNode)new TypeNode.Fqn(bound.Name,
                    bound.Args.Select(_ => (TypeNode)new TypeNode.Star()).ToArray())).Distinct().ToArray();
            if (projected.Length == 0) continue;
            var variable = frame.Variable(new TypeNode.Tv(scope, sourceIndex), NullableRepresentationFrame.Role.Storage);
            bool Mentions(JsonNode node) => TypeJson.Read(node) is TypeNode type ? Contains(type)
                : node is JsonObject obj ? obj.Any(pair => Mentions(pair.Value))
                : node is JsonArray array && array.Any(Mentions);
            bool Contains(TypeNode type) => type.Equals(variable) || type switch {
                TypeNode.Fqn { Args: { } args } => args.Any(Contains),
                TypeNode.Array array => Contains(array.Elem),
                TypeNode.Nullable nullable => Contains(nullable.Of),
                TypeNode.Oblivious oblivious => Contains(oblivious.Of),
                TypeNode.ByRef reference => Contains(reference.Of),
                TypeNode.Ptr pointer => Contains(pointer.Of),
                TypeNode.Mod modifier => Contains(modifier.M) || Contains(modifier.Of),
                TypeNode.Fn function => Contains(function.Ret) || function.DelegateParams.Any(Contains)
                    || function.Ctx?.Any(Contains) == true,
                _ => false,
            };
            var publishedSlot = new[] { "params", "ret", "fields", "properties" }
                .Any(key => Mentions(declaration[key]));
            expanded[variable.I] = new JsonObject {
                ["name"] = expanded[variable.I].DeepClone(),
                [publishedSlot ? "constraints" : FBoundStarProjectionErasure.ErasedInnerConstraintKey] =
                    new JsonArray(projected.Select(TypeJson.Write).ToArray()),
            };
        }
        declaration["typeParams"] = expanded;
    }

    static JsonArray ExpandParameters(JsonArray source, NullableRepresentationFrame frame, string scope = "method")
    {
        if (source.Count != frame.SourceArity)
            throw new InvalidOperationException("Generic parameter declarations do not match their source frame");
        return new JsonArray(Enumerable.Range(0, frame.PhysicalArity).Select(index => {
            var slot = frame.PhysicalSlot(index);
            // Source bounds apply to the ordinary/root parameter. A companion's CLR representation does not
            // necessarily implement the source bound, so do not copy those constraints onto it.
            if (slot.Representation != NullableRepresentationFrame.Role.Ordinary)
                return (JsonNode)JsonValue.Create("$" + slot.Representation.ToString().ToLowerInvariant() + slot.SourceIndex);
            var parameter = source[slot.SourceIndex]?.DeepClone();
            if (!frame.NullableIndices.Contains(slot.SourceIndex)) return parameter;
            var declaration = parameter as JsonObject ?? new JsonObject { ["name"] = parameter };
            var constraints = declaration["constraints"] as JsonArray;
            if (constraints == null) declaration["constraints"] = constraints = new JsonArray();
            // Nullable generic arguments use the same reference type or boxed object, not Nullable<V>.
            // The CLR frame must state this physical widening relation for calls with Kotlin T <: T?.
            constraints.Add(TypeJson.Write(frame.NullableVariable(new TypeNode.Tv(scope, slot.SourceIndex))));
            return declaration;
        }).ToArray());
    }

    static void PreserveEdges(JsonObject owner, NullableRepresentationTypes mapping)
    {
        var facts = new JsonObject();
        // Class bounds are Kotlin declaration facts too. Expanding a referenced owner's
        // frame must not expose its physical companion arguments to a later Kotlin import.
        if (owner["typeParams"] is JsonArray parameters)
        {
            var bounds = new JsonObject();
            for (var i = 0; i < parameters.Count; i++)
                if (parameters[i] is JsonObject parameter
                    && parameter["constraints"] is JsonArray constraints
                    && constraints.Any(bound => TypeJson.Read(bound) is TypeNode type
                        && mapping.Slot(type) != type))
                    bounds[i.ToString()] = constraints.DeepClone();
            if (bounds.Count != 0) facts["bounds"] = bounds;
        }
        if (TypeJson.Read(owner["base"]) is TypeNode baseType && mapping.Slot(baseType) != baseType)
            facts["base"] = owner["base"].DeepClone();
        if (owner["interfaces"] is JsonArray interfaces)
        {
            var changed = new JsonArray(interfaces.Where(edge => TypeJson.Read(edge) is TypeNode type
                    && mapping.Slot(type) != type).Select(edge => edge.DeepClone()).ToArray());
            if (changed.Count != 0) facts["interfaces"] = changed;
        }
        KotlinSupertypesRecord.Merge(owner, facts);
    }

    static void Rewrite(JsonNode node, NullableRepresentationTypes mapping,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods,
        Func<JsonObject, NullableRepresentationTypes> declarationMapping,
        NullableGenericErasure.Pos position = NullableGenericErasure.Pos.Slot)
    {
        try { RewriteCore(node, mapping, methods, declarationMapping, position); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException && node is JsonObject method
            && method["k"] == null && method["params"] is JsonArray && Text(method["name"]) != null)
        {
            throw new InvalidOperationException($"Nullable frame rewrite of {Text(method["name"])}: {error.Message}", error);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException && node is JsonObject expression && Text(expression["k"]) != null)
        {
            throw new InvalidOperationException($"{Text(expression["k"])} {Text(expression["method"])}: {error.Message}", error);
        }
    }

    static void RewriteCore(JsonNode node, NullableRepresentationTypes mapping,
        IReadOnlyDictionary<string, NullableRepresentationFrame> methods,
        Func<JsonObject, NullableRepresentationTypes> declarationMapping,
        NullableGenericErasure.Pos position)
    {
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                if (TypeJson.Read(array[i]) is TypeNode type) array[i] = TypeJson.Write(mapping.Rewrite(type, position));
                else Rewrite(array[i], mapping, methods, declarationMapping, position);
        }
        else if (node is JsonObject obj)
        {
            var kind = Text(obj["k"]);
            var selectedMapping = kind == null && obj["delegationSig"] == null
                && obj["sharedCellTypeParams"] == null ? null : declarationMapping(obj);
            JsonArray closedArguments = null;
            JsonArray closedDispatchArguments = null;
            JsonArray closedDispatchOwnerArguments = null;
            NullableRepresentationFrame capturedFrame = null;
            if (kind is "newClosure" or "newSam" && obj["synthClass"] == null
                && obj["typeArgs"] is JsonArray syntheticArguments
                && TypeJson.OwnerName(obj["closureType"] ?? obj["samType"]) is string syntheticOwner)
            {
                var application = mapping.Slot(new TypeNode.Fqn(syntheticOwner,
                    syntheticArguments.Select(TypeJson.Read).ToArray()));
                if (application is TypeNode.Fqn { Args: { } physicalArguments })
                    closedArguments = new JsonArray(physicalArguments.Select(TypeJson.Write).ToArray());
            }
            if (kind == "callInline" && selectedMapping?.DispatchOwnerFrame is { } dispatchFrame
                && obj["recvs"] is JsonObject receivers && receivers["dispatchTypeArgs"] is JsonArray dispatchArguments)
            {
                var arguments = dispatchArguments.Select(TypeJson.Read).ToArray();
                closedDispatchArguments = new JsonArray(mapping.CloseMethod(dispatchFrame, arguments).Select(TypeJson.Write).ToArray());
                closedDispatchOwnerArguments = new JsonArray(mapping.CloseMethod(selectedMapping.ApplicationOwnerFrame,
                    arguments).Select(TypeJson.Write).ToArray());
            }
            if (kind == "localFun")
            {
                var local = (JsonObject)obj["decl"];
                var localFrame = selectedMapping.MethodFrame;
                if (local["_syntheticTypeArgs"] is JsonArray origins)
                {
                    var source = origins.Select(TypeJson.Read).ToArray();
                    if (source.Length != localFrame.SourceArity)
                        throw new InvalidOperationException("Local function capture origins do not match its source frame");
                    local["_syntheticTypeArgs"] = new JsonArray(Enumerable.Range(0, localFrame.PhysicalArity).Select(index => {
                        var slot = localFrame.PhysicalSlot(index);
                        var origin = source[slot.SourceIndex];
                        return TypeJson.Write(origin is TypeNode.Tv { Scope: "type" }
                            ? mapping.ArgumentForRole(origin, slot.Representation)
                            : slot.Representation == NullableRepresentationFrame.Role.Ordinary ? origin
                                : localFrame.Variable(new TypeNode.Tv("method", slot.SourceIndex), slot.Representation));
                    }).ToArray());
                }
                // Its independent signature/body are visited once through the discovered local MethodDemand.
                return;
            }
            if (kind is "callLocal" or "localFunRef" && selectedMapping.MethodFrame.RequiresMetadata)
            {
                var arguments = (JsonArray)obj["typeArgs"];
                closedArguments = new JsonArray(mapping.CloseMethod(selectedMapping.MethodFrame,
                    arguments.Select(TypeJson.Read).ToArray()).Select(TypeJson.Write).ToArray());
            }
            // A closure's invoke return implements the delegate's return generic argument. It is not an
            // ordinary scalar Kotlin return (where open T? erases to object). Preserve the source fact before
            // the recursive walk maps the payload, then install the same representation as funcType.Ret.
            var closureReturn = kind == "newClosure" && obj["synthClass"] is JsonObject closure
                && !ClosureSynthesis.HasPreboundFrame(closure) && TypeJson.Read(closure["ret"]) is TypeNode returnType
                    ? mapping.Argument(returnType) : null;
            var synthetic = kind == "newSuspendLambda" ? obj : obj["synthClass"] as JsonObject;
            if (kind is "newSam" or "newClosure" or "newSuspendLambda" && synthetic != null
                && (kind == "newSuspendLambda" ? Text(obj["typeFrame"]) != "dense"
                    : !ClosureSynthesis.HasPreboundFrame(synthetic))
                && obj["typeArgs"] is JsonArray captureArguments && synthetic["typeParams"] is JsonArray captureParameters)
            {
                // Raw payload types still belong to the lexical frame. Materializing T? inside that payload
                // creates a use of N(T), so the generated class must capture N(T) alongside the ordinary T.
                // The later closure binder consumes this explicit argument/parameter correspondence.
                var captures = captureArguments.Select(TypeJson.Read).ToArray();
                if (captures.Length != captureParameters.Count)
                    throw new InvalidOperationException("Synthetic capture arguments do not match their declarations");
                var captureFrame = CaptureFrame(captures, mapping);
                if (captureFrame.RequiresMetadata)
                {
                    capturedFrame = captureFrame;
                    if (kind is "newSam" or "newClosure")
                        PreserveCompanionSourceBounds(synthetic, captureFrame, owner: true);
                    closedArguments = new JsonArray(mapping.CloseMethod(captureFrame, captures).Select(TypeJson.Write).ToArray());
                    foreach (var slot in captureFrame.Companions)
                    {
                        var variable = (TypeNode.Tv)captures[slot.SourceIndex];
                        var name = "$" + slot.Representation.ToString().ToLowerInvariant() + "Capture" + variable.Scope + variable.I;
                        captureParameters.Add(name);
                        if (kind == "newSuspendLambda" && obj["typeParamDecls"] is JsonArray declarations)
                            declarations.Add(name);
                    }
                    if (kind is "newSam" or "newClosure")
                    {
                        // The payload's untouched override edges remain source vocabulary. Record the same
                        // capture-to-companion correspondence that constructs the synthetic CLR owner, so later
                        // slot resolution can translate those edges without reconstructing it from field names.
                        KotlinSupertypesRecord.Merge(synthetic, new JsonObject {
                            [NullableRepresentationFrame.MetadataKey] = captureFrame.ToJson(),
                        });
                    }
                }
            }
            if (kind != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                && methods.TryGetValue(id, out var frame) && frame.RequiresMetadata)
            {
                var arguments = obj["typeArgs"] as JsonArray
                    ?? throw new InvalidOperationException("Nullable-frame call has no source type arguments");
                closedArguments = new JsonArray(mapping.CloseMethod(frame, arguments.Select(TypeJson.Read).ToArray())
                    .Select(TypeJson.Write).ToArray());
                // Inline substitution consumes the physical payload frame captured after this pass. Its arity
                // must include companions too, or the splice leaves their variables in the callee's frame.
                if (kind == "callInline") obj["ga"] = frame.PhysicalArity;
            }
            // Determine ownership before mutating sty or ret; otherwise JSON property order changes the frame.
            var declarationKeys = obj.Select(p => p.Key)
                .Where(key => NullableRepresentationTypes.IsDeclarationFrameKey(key, kind, obj)).ToHashSet();
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (key is "attrs" or "overrides" or "_syntheticTypeArgs" || key == "typeArgs" && closedArguments != null) continue;
                if (key == "recvs" && closedDispatchArguments != null && obj[key] is JsonObject rewrittenReceivers)
                {
                    // This is a separate application of the selected owner's frame, not just a list of
                    // independent caller types. InlineSplice consumes its complete physical correspondence.
                    rewrittenReceivers.Remove("dispatchTypeArgs");
                    Rewrite(rewrittenReceivers, mapping, methods, declarationMapping, position);
                    rewrittenReceivers["dispatchTypeArgs"] = closedDispatchArguments;
                    rewrittenReceivers["dispatchOwnerTypeArgs"] = closedDispatchOwnerArguments;
                    continue;
                }
                // The lexical ID already selects the declaration. LocalFunctionLowering supplies its final
                // descriptor after dense captures have been split into owner and method parameters.
                if (kind is "callLocal" or "localFunRef" && key == "sig") continue;
                if (key == "inheritedImplementation" && obj[key] is JsonObject implementation)
                {
                    RewriteImplementation(implementation, declarationMapping(implementation), mapping);
                    continue;
                }
                if (declarationKeys.Contains(key))
                {
                    if (key == "memberType" && mapping.IsNativeField(obj)
                        && TypeJson.Read(obj[key]) is TypeNode nativeFieldType)
                        obj[key] = TypeJson.Write((selectedMapping ?? mapping).Argument(nativeFieldType));
                    else RewriteDescriptor(obj, key, selectedMapping, mapping);
                    continue;
                }
                if (mapping.IsStorageElement(kind, key) && TypeJson.Read(obj[key]) is TypeNode storageElement)
                {
                    obj[key] = TypeJson.Write(mapping.StorageArgument(storageElement));
                    continue;
                }
                var childPosition = key switch {
                    "type" when kind == null && mapping.IsNativeField(obj) => NullableGenericErasure.Pos.Argument,
                    "ret" when obj[PreparedClosureDefaultFrames.ReturnArgumentKey]?.GetValue<bool>() == true
                        => NullableGenericErasure.Pos.Argument,
                    "typeArgs" => NullableGenericErasure.Pos.Argument,
                    "elem" when NullableGenericErasure.IsArgumentElementKind(kind) => NullableGenericErasure.Pos.Argument,
                    "resolvedMemberParams" => NullableGenericErasure.Pos.Bound,
                    ClrMemberResolution.ResolvedMemberReturnKey => NullableGenericErasure.Pos.Bound,
                    "argTypes" when ClrBoundNode.IsAny(kind) => NullableGenericErasure.Pos.Bound,
                    _ => position,
                };
                if (TypeJson.Read(obj[key]) is TypeNode type) obj[key] = TypeJson.Write(mapping.Rewrite(type, childPosition));
                else Rewrite(obj[key], mapping, methods, declarationMapping, childPosition);
            }
            if (closedArguments != null) obj["typeArgs"] = closedArguments;
            if (capturedFrame != null)
            {
                // The payload still uses lexical variables until closure/suspend synthesis binds its
                // explicit capture arguments. Add already-materialized bounds after the lexical rewrite.
                var declarations = kind == "newSuspendLambda"
                    ? obj["typeParamDecls"] as JsonArray : synthetic["typeParams"] as JsonArray;
                if (declarations != null)
                    foreach (var sourceIndex in capturedFrame.NullableIndices)
                    {
                        var ordinary = capturedFrame.Variable(new TypeNode.Tv("type", sourceIndex),
                            NullableRepresentationFrame.Role.Ordinary).I;
                        var companion = capturedFrame.NullableVariable(new TypeNode.Tv("type", sourceIndex)).I;
                        var declaration = declarations[ordinary] as JsonObject;
                        if (declaration == null)
                            declarations[ordinary] = declaration = new JsonObject {
                                ["name"] = declarations[ordinary]?.DeepClone(),
                            };
                        if (declaration["constraints"] is not JsonArray constraints)
                            declaration["constraints"] = constraints = new JsonArray();
                        constraints.Add(closedArguments[companion].DeepClone());
                    }
            }
            if (closureReturn != null) obj["synthClass"]["ret"] = TypeJson.Write(closureReturn);
        }
    }

    static NullableRepresentationFrame CaptureFrame(TypeNode[] captures, NullableRepresentationTypes mapping)
    {
        var companions = captures.OfType<TypeNode.Tv>().Distinct().SelectMany(variable =>
            (variable.Scope == "type" ? mapping.OwnerFrame : mapping.MethodFrame)?.Companions
                .Where(slot => slot.SourceIndex == variable.I)
                .Select(slot => new NullableRepresentationFrame.Slot(Array.IndexOf(captures, variable), slot.Representation))
                ?? Enumerable.Empty<NullableRepresentationFrame.Slot>()).ToArray();
        IEnumerable<int> Indices(NullableRepresentationFrame.Role role) => companions.Where(slot => slot.Representation == role)
            .Select(slot => slot.SourceIndex).OrderBy(index => index);
        return new NullableRepresentationFrame(captures.Length, Indices(NullableRepresentationFrame.Role.Nullable),
            storageIndices: Indices(NullableRepresentationFrame.Role.Storage),
            nullableStorageIndices: Indices(NullableRepresentationFrame.Role.NullableStorage));
    }

    static void RewriteImplementation(JsonObject implementation, NullableRepresentationTypes declaration,
        NullableRepresentationTypes lexical)
    {
        // The selected member's descriptors use its declaration frame, but its constructed owner is
        // an application in the inheriting type's frame (Base<Pair<K,V>>, not Base<Base.T>).
        foreach (var key in implementation.Select(pair => pair.Key).ToArray())
            if (key == "owner" && TypeJson.Read(implementation[key]) is TypeNode owner)
                implementation[key] = TypeJson.Write(lexical.Slot(owner));
            else RewriteDescriptor(implementation, key, declaration, lexical);
    }

    static void RewriteDescriptor(JsonObject node, string key, NullableRepresentationTypes declaration,
        NullableRepresentationTypes lexical)
    {
        var mapper = declaration ?? lexical;
        var position = declaration == null ? NullableGenericErasure.Pos.Bound : NullableGenericErasure.Pos.Slot;
        JsonNode Map(JsonNode value)
        {
            if (TypeJson.Read(value) is TypeNode type) return TypeJson.Write(mapper.Rewrite(type, position));
            if (value is JsonArray array) return new JsonArray(array.Select(Map).ToArray());
            if (value is JsonObject obj) return new JsonObject(obj.Select(pair =>
                new KeyValuePair<string, JsonNode>(pair.Key, Map(pair.Value))));
            return value?.DeepClone();
        }
        node[key] = Map(node[key]);
        var frame = key is "memberOwnerTypeParams" or "sharedCellTypeParams" ? declaration?.OwnerFrame
            : key == "memberMethodTypeParams" ? declaration?.MethodFrame : null;
        if (frame != null && node[key] is JsonArray parameters)
            node[key] = ExpandParameters(parameters, frame,
                key == "memberMethodTypeParams" ? "method" : "type");
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;

    public static void SelfTest()
    {
        var nativeOwnerRoot = JsonNode.Parse("""
        {"fileClass":"NativeOwnerSlots","types":[
          {"kind":"class","name":"ValueStore","typeParams":["T"],"fields":[
            {"name":"value","type":{"t":"tv","scope":"type","i":0}}]},
          {"kind":"class","name":"NativeAlias","typeParams":["T"],"methods":[
            {"name":"store","params":[{"name":"value","type":{"t":"tv","scope":"type","i":0}}],
             "ret":{"t":"fqn","name":"ValueStore","args":[{"t":"tv","scope":"type","i":0}]},
             "body":[{"k":"var","name":"nativeValue","type":{"t":"tv","scope":"type","i":0}}]},
            {"name":"ownMethod","typeParams":["U"],
             "params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
             "ret":{"t":"fqn","name":"ValueStore","args":[{"t":"tv","scope":"method","i":0}]},"body":[]}]}]}
        """)!.AsObject();
        var nativePolicy = new GenericRepresentationPolicy(new Dictionary<string, string> {
            ["NativeAlias"] = "NativeGeneric",
        }, roots: new[] { nativeOwnerRoot });
        Apply(new[] { nativeOwnerRoot }, _ => false, policy: nativePolicy);
        var nativeOwner = nativeOwnerRoot["types"][1];
        var nativeStore = nativeOwner["methods"][0];
        if (((JsonArray)nativeOwner["typeParams"]).Count != 2
            || TypeJson.Read(nativeStore["params"][0]["type"]) != new TypeNode.Tv("type", 0)
            || TypeJson.Read(nativeStore["body"][0]["type"]) != new TypeNode.Tv("type", 0)
            || !((JsonArray)nativeStore["ret"]["args"]).Select(TypeJson.Read).SequenceEqual(new TypeNode[] {
                new TypeNode.Tv("type", 0), new TypeNode.Tv("type", 1),
            })
            || TypeJson.Read(nativeOwner["methods"][1]["params"][0]["type"]) != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Kotlin body demand changed a native owner's scalar slot or lost an independent method value frame");
        var roleFrame = new NullableRepresentationFrame(1, new[] { 0 }, storageIndices: new[] { 0 },
            nullableStorageIndices: new[] { 0 });
        var boundedSource = JsonNode.Parse("""[{"name":"T","constraints":[{"t":"fqn","name":"Bound"}]}]""")!.AsArray();
        var physicalParameters = ExpandParameters(boundedSource, roleFrame);
        if (physicalParameters.Count != 4
            || !JsonNode.DeepEquals(physicalParameters[0]?["constraints"]?[0], boundedSource[0]?["constraints"]?[0])
            || TypeJson.Read(physicalParameters[0]?["constraints"]?[1]) != new TypeNode.Tv("method", 1)
            || physicalParameters.Skip(1).Any(parameter => parameter is not JsonValue))
            throw new InvalidOperationException("Representation companion inherited unsupported source constraints");
        var boundPolicy = new GenericRepresentationPolicy(new Dictionary<string, string> {
            ["NativeBound"] = "NativeBound",
        }, roots: new[] { JsonNode.Parse("""
          {"types":[{"kind":"interface","name":"ValueBound","typeParams":["T"]}]}
          """) });
        var boundMethod = JsonNode.Parse("""
          {"typeParams":[{"name":"R","constraints":[
              {"t":"fqn","name":"ValueBound","args":[{"t":"fqn","name":"kotlin.Int"}]},
              {"t":"fqn","name":"NativeBound","args":[{"t":"fqn","name":"kotlin.Int"}]}]}],
           "params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
           "ret":{"t":"fqn","name":"kotlin.Unit"}}
          """).AsObject();
        var boundFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        var privateBoundMethod = (JsonObject)boundMethod.DeepClone();
        privateBoundMethod["params"] = new JsonArray();
        AppendParameters(boundMethod, boundFrame, policy: boundPolicy);
        AppendParameters(privateBoundMethod, boundFrame, policy: boundPolicy);
        var carrierBound = new TypeNode.Fqn("ValueBound", new TypeNode[] { new TypeNode.Star() });
        if (TypeJson.Read(boundMethod["typeParams"][0]["constraints"][0]) != carrierBound
            || boundMethod["typeParams"][0]["constraints"].AsArray().Count != 1
            || boundMethod["typeParams"][1]["constraints"].AsArray().Count != 2
            || privateBoundMethod["typeParams"][0]["constraints"] != null
            || TypeJson.Read(privateBoundMethod["typeParams"][0][FBoundStarProjectionErasure.ErasedInnerConstraintKey][0]) != carrierBound)
            throw new InvalidOperationException("Storage bound lost its physical role, narrowed a native bound, or constrained a boxed private body");
        var reordered = new NullableRepresentationFrame(1, new[] { 0 }, new[] { 1, 0 });
        var ownerParameters = ExpandParameters(new JsonArray("T"), reordered, "type");
        if (TypeJson.Read(ownerParameters[1]?["constraints"]?[0]) != new TypeNode.Tv("type", 0))
            throw new InvalidOperationException("Nullable companion constraint lost its physical owner frame");
        var unboundedMethod = JsonNode.Parse("""{"typeParams":["T"]}""").AsObject();
        PreserveCompanionSourceBounds(unboundedMethod, roleFrame, owner: false);
        AppendParameters(unboundedMethod, roleFrame);
        var unboundedSource = JsonNode.Parse(Text(unboundedMethod[NullableGenericErasure.MethodTypeParameterBoundsPre]));
        if (unboundedSource["bounds"]["0"] is not JsonArray { Count: 0 })
            throw new InvalidOperationException("Physical nullable companion became a Kotlin source bound");
        var storageOnlyOwner = new JsonObject { ["typeParams"] = boundedSource.DeepClone() };
        var storageOnlyFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), storageIndices: new[] { 0 });
        PreserveCompanionSourceBounds(storageOnlyOwner, storageOnlyFrame, owner: true);
        AppendParameters(storageOnlyOwner, storageOnlyFrame, "type");
        var storageOnlyFacts = JsonNode.Parse(Text(storageOnlyOwner[KotlinSupertypesRecord.PreKey]));
        if (!JsonNode.DeepEquals(storageOnlyFacts["bounds"]["0"], boundedSource[0]["constraints"])
            || storageOnlyFacts["bounds"]["1"] != null)
            throw new InvalidOperationException("Storage-only frame failed to preserve source bounds independently of physical companions");
        var captureMapping = new NullableRepresentationTypes(roleFrame, roleFrame,
            new Dictionary<string, NullableRepresentationFrame>(), _ => false);
        var captureArguments = new TypeNode[] { new TypeNode.Tv("method", 0), new TypeNode.Tv("type", 0) };
        var captureFrame = CaptureFrame(captureArguments, captureMapping);
        var closedCaptures = captureMapping.CloseMethod(captureFrame, captureArguments);
        if (captureFrame.PhysicalArity != 8 || !closedCaptures.SequenceEqual(new TypeNode[] {
            new TypeNode.Tv("method", 0), new TypeNode.Tv("type", 0),
            new TypeNode.Tv("method", 1), new TypeNode.Tv("type", 1),
            new TypeNode.Tv("method", 2), new TypeNode.Tv("type", 2),
            new TypeNode.Tv("method", 3), new TypeNode.Tv("type", 3),
        })) throw new InvalidOperationException("Synthetic capture lost scoped representation roles or their physical order");
        var dispatchFrame = new NullableRepresentationFrame(1, Array.Empty<int>(), new[] { 1, 0 }, storageIndices: new[] { 0 });
        var specializedSam = new JsonObject {
            ["name"] = "SpecializedSam", ["typeParams"] = new JsonArray("P", "T"),
        };
        KotlinSupertypesRecord.Merge(specializedSam, new JsonObject {
            [NullableRepresentationFrame.MetadataKey] = dispatchFrame.ToJson(),
            ["interfaces"] = new JsonArray(TypeJson.Write(new TypeNode.Fqn("SourceContract",
                new TypeNode[] { new TypeNode.Tv("type", 0) }))),
        });
        var specializedConstruction = new JsonObject {
            ["k"] = "newSam", ["synthClass"] = specializedSam,
            ["typeArgs"] = new JsonArray(TypeJson.Fqn("object"), TypeJson.Fqn("kotlin.String")),
        };
        InlineSplice.PruneConcreteSynthClasses(specializedConstruction);
        var specializedFacts = JsonNode.Parse(Text(specializedSam[KotlinSupertypesRecord.PreKey]));
        if (specializedSam["typeParams"] != null || specializedConstruction["typeArgs"] != null
            || specializedFacts[NullableRepresentationFrame.MetadataKey] != null
            || TypeJson.Read(specializedFacts["interfaces"][0]) != new TypeNode.Fqn("SourceContract",
                new TypeNode[] { new TypeNode.Fqn("kotlin.String") }))
            throw new InvalidOperationException("Closed synthetic specialization retained a frame or substituted a storage role into source facts");
        var dispatchSource = JsonNode.Parse("""
        {"name":"run","declarationId":"storage-dispatch","typeParams":["T"],"params":[],
         "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[],"virtual":true}
        """)!.AsObject();
        var dispatchEntry = (JsonObject)dispatchSource.DeepClone();
        AppendParameters(dispatchEntry, dispatchFrame);
        var dispatchOwner = new JsonObject { ["name"] = "RoleDispatcher", ["methods"] = new JsonArray(dispatchEntry) };
        var dispatchDemand = new NullableRepresentationDemand.Variables();
        dispatchDemand.Method.Add(0);
        NullableBodyDispatch.Build(dispatchOwner, dispatchSource, dispatchEntry,
            new NullableRepresentationFrame(0, Array.Empty<int>()), dispatchFrame, dispatchDemand, AppendParameters);
        var roleDispatch = dispatchEntry["body"][0]["value"];
        var dispatchThen = ((JsonArray)roleDispatch["then"]["typeArgs"]).Select(TypeJson.Read).ToArray();
        var dispatchElse = ((JsonArray)roleDispatch["else"]["typeArgs"]).Select(TypeJson.Read).ToArray();
        if (!dispatchThen.SequenceEqual(new TypeNode[] {
                new TypeNode.Tv("method", 1), new TypeNode.Fqn("object"), new TypeNode.Tv("method", 0),
            }) || !dispatchElse.SequenceEqual(new TypeNode[] {
                new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 0),
            }) || TypeJson.Read(roleDispatch["cond"]["recv"]["type"]) != new TypeNode.Tv("method", 1)
            || ((JsonArray)dispatchEntry["typeParams"]).Count != 2)
            throw new InvalidOperationException("Nullable body dispatch lost an existing storage role or changed the entry frame");
        var descriptorTypes = new Dictionary<string, NullableRepresentationFrame> {
            ["ImportedStore"] = new NullableRepresentationFrame(1, new[] { 0 }),
        };
        var inheritedDescriptor = JsonNode.Parse("""
        {"fileClass":"InheritedDescriptor","types":[
          {"kind":"class","name":"DescriptorBox","typeParams":["T"]},
          {"kind":"class","name":"DescriptorBase","typeParams":["T"],"methods":[
            {"name":"accept","declarationId":"descriptor-base-accept","params":[{"name":"value","type":
              {"t":"fqn","name":"DescriptorBox","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}}],
             "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[]}]},
          {"kind":"class","name":"DescriptorDerived","typeParams":["U","V"],
           "base":{"t":"fqn","name":"DescriptorBase","args":[{"t":"tv","scope":"type","i":1}]},
           "methods":[{"name":"forward","params":[{"name":"value","type":
             {"t":"fqn","name":"DescriptorBox","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":1}}]}}],
             "ret":{"t":"fqn","name":"kotlin.Unit"},"body":[
              {"k":"callInstance","method":"accept","declarationId":"descriptor-base-accept",
               "ownerType":{"t":"fqn","name":"DescriptorDerived","args":[
                 {"t":"tv","scope":"type","i":0},{"t":"tv","scope":"type","i":1}]},
               "memberOwnerTypeParams":["U","V"],"memberSignature":[
                 {"t":"fqn","name":"DescriptorBox","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":1}}]}],
               "recv":{"k":"this"},"args":[{"k":"local","name":"value"}],"ret":{"t":"fqn","name":"kotlin.Unit"}}]}]}]}
        """)!.AsObject();
        Apply(new[] { inheritedDescriptor }, _ => false);
        var rewrittenDescriptor = inheritedDescriptor["types"][2]["methods"][0]["body"][0];
        if (rewrittenDescriptor["memberOwnerTypeParams"] is not JsonArray { Count: 3 }
            || TypeJson.Read(rewrittenDescriptor["memberSignature"][0]) is not TypeNode.Fqn { Args: { } descriptorArguments }
            || descriptorArguments[0] != new TypeNode.Tv("type", 2))
            throw new InvalidOperationException("Inherited declaration identity replaced the explicit accessed-owner frame");
        var nongenericSource = new NullableRepresentationTypes(null, null, descriptorTypes, _ => false);
        var sourceSignature = JsonNode.Parse("""
        {"sig":[{"t":"fqn","name":"ImportedStore","args":[{"t":"fqn","name":"kotlin.String"}]}]}
        """)!.AsObject();
        var foreignSignature = (JsonObject)sourceSignature.DeepClone();
        RewriteDescriptor(sourceSignature, "sig", nongenericSource, nongenericSource);
        RewriteDescriptor(foreignSignature, "sig", null, nongenericSource);
        if (TypeJson.Read(sourceSignature["sig"][0]) is not TypeNode.Fqn { Args.Length: 2 }
            || TypeJson.Read(foreignSignature["sig"][0]) is not TypeNode.Fqn { Args.Length: 1 })
            throw new InvalidOperationException("Nongeneric Kotlin and foreign CLR signature frames were conflated");
        var carrierRoot = (JsonObject)JsonNode.Parse("""
        {"fileClass":"CarrierProbe","methods":[{"name":"use","params":[{"name":"value","type":
          {"t":"fqn","name":"CarrierStore","args":[{"t":"fqn","name":"kotlin.Int"}]}}],
          "ret":{"t":"fqn","name":"CarrierStore","args":[{"t":"fqn","name":"kotlin.Int"}]},"body":[]}],
         "types":[{"kind":"class","name":"CarrierBox","typeParams":["T"]},
          {"kind":"class","name":"CarrierStore","typeParams":["T"],"fields":[{"name":"value","type":
           {"t":"fqn","name":"CarrierBox","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}}]}]}
        """);
        var sourceCarrierType = carrierRoot["methods"][0]["ret"].ToJsonString();
        Apply(new[] { carrierRoot }, type => type.Name == "kotlin.Int");
        NullableGenericErasure.Apply(carrierRoot, type => type.Name == "kotlin.Int");
        if (Text(carrierRoot["methods"][0]["nullableGenericRet"]) != sourceCarrierType
            || Text(carrierRoot["methods"][0]["params"][0]["nullableGeneric"]) != sourceCarrierType)
            throw new InvalidOperationException("A physical companion argument replaced a Kotlin source slot carrier");
        var boundRoot = JsonNode.Parse("""
        {"fileClass":"BoundProbe","types":[{"kind":"class","name":"Bound","typeParams":["A","B"]}],
         "methods":[{"name":"keep","declarationId":"bound-probe","typeParams":["T",
          {"name":"U","constraints":[{"t":"fqn","name":"Bound","args":[
           {"t":"nullable","of":{"t":"tv","scope":"method","i":0}},
           {"t":"nullable","of":{"t":"fqn","name":"kotlin.Int"}}]}]}],
          "params":[{"name":"x","type":{"t":"tv","scope":"method","i":1}}],
          "ret":{"t":"tv","scope":"method","i":1},"body":[]}]}
        """)!.AsObject();
        var sourceBounds = boundRoot["methods"][0]["typeParams"][1]["constraints"].DeepClone();
        Apply(new[] { boundRoot }, type => type.Name == "kotlin.Int");
        if (((JsonArray)boundRoot["methods"][0]["typeParams"]).Count != 3)
            throw new InvalidOperationException("Method-bound probe did not materialize its nullable companion");
        NullableGenericErasure.Apply(boundRoot, type => type.Name == "kotlin.Int");
        var recordedBounds = JsonNode.Parse(Text(boundRoot["methods"][0][NullableGenericErasure.MethodTypeParameterBoundsPre]));
        if (!JsonNode.DeepEquals(recordedBounds["bounds"]["1"], sourceBounds))
            throw new InvalidOperationException("A physical companion index replaced a Kotlin method-bound carrier");
        var classBoundRoot = JsonNode.Parse("""
        {"types":[{"kind":"class","name":"Cell","typeParams":["T"]},
         {"kind":"class","name":"Node","typeParams":[{"name":"N","constraints":[
          {"t":"fqn","name":"Node","args":[{"t":"tv","scope":"type","i":0}]}]}],
          "fields":[{"name":"previous","type":{"t":"fqn","name":"Cell","args":[
           {"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}}]}]}
        """)!.AsObject();
        var sourceClassBounds = classBoundRoot["types"][1]["typeParams"][0]["constraints"].DeepClone();
        Apply(new[] { classBoundRoot }, _ => false);
        NullableGenericErasure.Apply(classBoundRoot, _ => false);
        var classOwner = classBoundRoot["types"][1];
        var classBounds = JsonNode.Parse(Text(classOwner[KotlinSupertypesRecord.PreKey]));
        if (((JsonArray)classOwner["typeParams"]).Count != 2
            || !JsonNode.DeepEquals(classBounds["bounds"]["0"], sourceClassBounds))
            throw new InvalidOperationException("Physical companion arguments replaced a Kotlin class-bound carrier");
        var descriptorOwners = JsonNode.Parse("""
        {"fileClass":"DescriptorOwners","properties":[
          {"name":"extensionValue","propertyAssociation":"p","type":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]}}],
         "methods":[{"name":"extensionValue","propertyAssociation":"p","propertyAccessor":"get","typeParams":["T"],
           "params":[],"ret":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]},"body":[]},
          {"name":"awaitUse","params":[],"ret":{"t":"fqn","name":"kotlin.Int"},"body":[
            {"k":"callInstance","method":"await","ownerType":{"t":"fqn","name":"ForeignTask","args":[{"t":"fqn","name":"kotlin.Int"}]},
             "clrAwaitBridge":true,"awaitResult":{"t":"tv","scope":"type","i":0},"args":[]}]}],
         "types":[{"kind":"class","name":"Base","typeParams":["T"],"fields":[
           {"name":"value","type":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}}]},
          {"kind":"class","name":"Child","base":{"t":"fqn","name":"Base","args":[{"t":"fqn","name":"kotlin.String"}]},
           "ctors":[{"params":[],"baseArgs":[],"delegationSig":[{"t":"tv","scope":"type","i":0}],"body":[]}]}]}
        """);
        ((JsonArray)descriptorOwners["methods"][1]["body"]).Add(JsonNode.Parse("""
        {"k":"callInline","owner":{"t":"fqn","name":"Base"},"typeArgs":[],"ga":0,
         "recvs":{"dispatchTypeArgs":[{"t":"fqn","name":"kotlin.String"}],
          "dispatch":{"k":"local","name":"receiver","sty":{"t":"fqn","name":"Base",
           "args":[{"t":"fqn","name":"kotlin.String"}]}}}}
        """));
        Apply(new[] { descriptorOwners }, _ => false);
        if (TypeJson.Read(descriptorOwners["properties"][0]["type"]["args"][0]) != new TypeNode.Tv("method", 1)
            || TypeJson.Read(descriptorOwners["types"][1]["ctors"][0]["delegationSig"][0]) != new TypeNode.Tv("type", 0)
            || TypeJson.Read(descriptorOwners["methods"][1]["body"][0]["awaitResult"]) != new TypeNode.Tv("type", 0))
            throw new InvalidOperationException("Property, constructor, or await descriptors lost their declaration frames");
        var dispatch = descriptorOwners["methods"][1]["body"][1]["recvs"];
        if (dispatch["dispatchTypeArgs"] is not JsonArray { Count: 2 } dispatchArguments
            || TypeJson.Read(dispatchArguments[1]) != new TypeNode.Nullable(new TypeNode.Fqn("kotlin.String"))
            || !JsonNode.DeepEquals(dispatch["dispatch"]["sty"]["args"], dispatchArguments))
            throw new InvalidOperationException("Inline dispatch application lost its owner's nullable companion");
        var root = JsonNode.Parse("""
        {"fileClass":"FrameTest","methods":[
          {"name":"pass","declarationId":"pass","typeParams":["T"],
           "params":[{"name":"x","type":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]}}],
           "ret":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}]},
           "body":[{"k":"return","value":{"k":"local","name":"x"}}]},
          {"name":"caller","declarationId":"caller","params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},
           "body":[{"k":"callStatic","declarationId":"pass","typeArgs":[{"t":"fqn","name":"kotlin.String"}]}]}],
         "types":[{"kind":"class","name":"Box","typeParams":["T"],"fields":[{"name":"value","type":{"t":"tv","scope":"type","i":0}}]},
          {"kind":"class","name":"Store","typeParams":["T"],"fields":[{"name":"box","type":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}}]},
          {"kind":"class","name":"Derived","base":{"t":"fqn","name":"Store","args":[{"t":"fqn","name":"kotlin.String"}]}}]}
        """);
        var constructorOwner = JsonNode.Parse("""
        {"kind":"class","name":"ConstructorOwner","typeParams":["T"],"ctors":[
          {"params":[{"name":"value","type":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}}],"body":[]}]}
        """);
        ((JsonArray)root["types"]).Add(constructorOwner);
        var construction = JsonNode.Parse("""
        {"k":"new","type":{"t":"fqn","name":"ConstructorOwner","args":[{"t":"fqn","name":"kotlin.String"}]},
         "argTypes":[{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"fqn","name":"kotlin.String"}}]}],
         "memberSignature":[{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}],"args":[]}
        """);
        ((JsonArray)root["methods"][1]["body"]).Add(construction);
        var genericCall = (JsonObject)root["methods"][1]["body"][0];
        genericCall["memberMethodTypeParams"] = new JsonArray("T");
        genericCall["ownerType"] = TypeJson.Fqn("FrameTest");
        genericCall["memberSignature"] = new JsonArray(root["methods"][0]["params"][0]["type"].DeepClone());
        genericCall["memberReturnType"] = root["methods"][0]["ret"].DeepClone();
        genericCall["argTypes"] = genericCall["memberSignature"].DeepClone();
        genericCall["ret"] = genericCall["memberReturnType"].DeepClone();
        var nested = JsonNode.Parse("""
        {"kind":"class","name":"NestedStore","semanticOwner":"Store","outerTypeParamOffset":1,
         "outerTypeParamCount":1,"typeParams":["Own","T"],"fields":[
         {"name":"own","type":{"t":"tv","scope":"type","i":0}},
         {"name":"captured","type":{"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":1}}]}}]}
        """);
        ((JsonArray)root["types"]).Add(nested);
        var defaultMember = root["methods"][0].DeepClone();
        defaultMember[DeclarationIdentityBinding.Key] = "defaultPass";
        ((JsonArray)root["types"]).Add(new JsonObject {
            ["name"] = "DefaultSource", ["kind"] = "interface", ["methods"] = new JsonArray(defaultMember),
        });
        var inherited = new JsonObject {
            ["member"] = "pass", ["params"] = new JsonArray(defaultMember["params"][0]["type"].DeepClone()),
            ["ret"] = defaultMember["ret"].DeepClone(),
            ["implementation"] = new JsonObject {
                ["owner"] = TypeJson.Fqn("DefaultSource"), ["member"] = "pass", ["kind"] = "method",
                ["arity"] = 1, ["typeParams"] = new JsonArray("T"),
            },
        };
        ((JsonArray)root["types"]).Add(new JsonObject {
            ["name"] = "DefaultUser", ["kind"] = "class",
            [KotlinPropertyAccessors.InheritedDefaultMethodsKey] = new JsonArray(inherited),
        });
        var refCell = new JsonObject {
            ["name"] = "Cell", ["typeParams"] = new JsonArray("S"),
            ["elem"] = TypeJson.Write(new TypeNode.Fqn("Box", new TypeNode[] {
                new TypeNode.Nullable(new TypeNode.Tv("type", 0)) })),
        };
        root["refTypes"] = new JsonArray(refCell);
        var rawSam = new JsonObject {
            ["k"] = "newSam", ["typeArgs"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 0))),
            ["synthClass"] = new JsonObject {
                ["name"] = "CapturedSam", ["typeParams"] = new JsonArray("T"),
                ["interfaces"] = new JsonArray(TypeJson.Write(new TypeNode.Fqn("Comparator", new TypeNode[] {
                    new TypeNode.Nullable(new TypeNode.Tv("method", 0)) }))),
            },
        };
        ((JsonArray)root["methods"][0]["body"]).Add(rawSam);
        var inlineCall = new JsonObject {
            ["k"] = "callInline", ["declarationId"] = "pass", ["ga"] = 1,
            ["typeArgs"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 0))),
        };
        ((JsonArray)root["methods"][0]["body"]).Add(inlineCall);
        var closedCalls = new List<JsonObject>();
        foreach (var stampFirst in new[] { true, false })
        {
            var callerResult = TypeJson.Write(new TypeNode.Fqn("Box", new TypeNode[] {
                new TypeNode.Nullable(new TypeNode.Tv("method", 1)) }));
            var closedCall = new JsonObject();
            if (stampFirst) closedCall["sty"] = callerResult.DeepClone();
            closedCall["k"] = "callStatic";
            closedCall["ownerType"] = TypeJson.Fqn("FrameTest");
            closedCall["declarationId"] = "pass";
            closedCall["typeArgs"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 1)));
            closedCall["ret"] = callerResult.DeepClone();
            if (!stampFirst) closedCall["sty"] = callerResult.DeepClone();
            ((JsonArray)root["methods"]).Add(new JsonObject {
                ["name"] = "closedCaller" + stampFirst, ["static"] = true,
                ["typeParams"] = new JsonArray("A", "B"), ["params"] = new JsonArray(),
                ["ret"] = callerResult.DeepClone(), ["body"] = new JsonArray(closedCall),
            });
            closedCalls.Add(closedCall);
        }
        var boundedCell = JsonNode.Parse("""
        {"name":"BoundedCell","typeParams":["T",{"name":"U","constraints":[
          {"t":"fqn","name":"Box","args":[{"t":"nullable","of":{"t":"tv","scope":"type","i":0}}]}]}],
         "elem":{"t":"tv","scope":"type","i":1}}
        """);
        ((JsonArray)root["refTypes"]).Add(boundedCell);
        var cellCaptures = new List<JsonObject>();
        foreach (var descriptorFirst in new[] { false, true })
        {
            var capture = new JsonObject { ["name"] = "captured", ["type"] = TypeJson.Write(new TypeNode.Tv("method", 0)) };
            if (descriptorFirst) capture["sharedCellTypeParams"] = boundedCell["typeParams"].DeepClone();
            capture["sharedCellType"] = TypeJson.Write(new TypeNode.Fqn("BoundedCell", new TypeNode[] {
                new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 0) }));
            if (!descriptorFirst) capture["sharedCellTypeParams"] = boundedCell["typeParams"].DeepClone();
            ((JsonArray)root["methods"]).Add(new JsonObject {
                ["name"] = "captureCell" + descriptorFirst, ["typeParams"] = new JsonArray("A", "B"),
                ["params"] = new JsonArray(), ["ret"] = TypeJson.Fqn("kotlin.Unit"),
                ["body"] = new JsonArray(new JsonObject {
                    ["k"] = "inlineLambda", ["params"] = new JsonArray(),
                    ["captures"] = new JsonArray(capture), ["body"] = new JsonArray(),
                }),
            });
            cellCaptures.Add(capture);
        }
        Apply(new[] { root }, _ => false);
        foreach (var capture in cellCaptures)
            if (!JsonNode.DeepEquals(capture["sharedCellTypeParams"], boundedCell["typeParams"])
                || ((JsonArray)capture["sharedCellTypeParams"]).Count != 3
                || !JsonNode.DeepEquals(capture["sharedCellType"]["args"], new JsonArray(
                    TypeJson.Write(new TypeNode.Tv("method", 1)), TypeJson.Write(new TypeNode.Tv("method", 0)),
                    TypeJson.Write(new TypeNode.Tv("method", 2))))
                || TypeJson.Read(capture["sharedCellTypeParams"][1]["constraints"][0]) !=
                    new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("type", 2) }))
                throw new InvalidOperationException("Shared capture descriptor diverges from its cell's expanded declaration frame");
        if ((int)inlineCall["ga"] != 2 || ((JsonArray)inlineCall["typeArgs"]).Count != 2
            || TypeJson.Read(inlineCall["typeArgs"][1]) != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Inline substitution arity excludes the materialized companion frame");
        if (((JsonArray)rawSam["typeArgs"]).Count != 2
            || ((JsonArray)rawSam["synthClass"]["typeParams"]).Count != 2
            || TypeJson.Read(rawSam["typeArgs"][1]) != new TypeNode.Tv("method", 1)
            || TypeJson.Read(rawSam["synthClass"]["interfaces"][0]) !=
                new TypeNode.Fqn("Comparator", new TypeNode[] { new TypeNode.Tv("method", 1) }))
            throw new InvalidOperationException("Synthetic payload companion is absent from its capture correspondence");
        var samFrame = KotlinSupertypesRecord.ReadNullableFrame(rawSam["synthClass"].AsObject());
        if (samFrame == null || samFrame.SourceArity != 1
            || samFrame.NullableVariable(new TypeNode.Tv("type", 0)) != new TypeNode.Tv("type", 1))
            throw new InvalidOperationException("Synthetic owner lost its declaration-owned nullable frame");
        if ((int)inherited["implementation"]["arity"] != 2
            || ((JsonArray)inherited["implementation"]["typeParams"]).Count != 2
            || TypeJson.Read(inherited["ret"]) != TypeJson.Read(defaultMember["ret"])
            || ((JsonArray)refCell["typeParams"]).Count != 2
            || TypeJson.Read(refCell["elem"]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("type", 1) }))
            throw new InvalidOperationException("Inherited methods or reference cells lost their declaration-owned frames");
        if ((int)nested["outerTypeParamCount"] != 2 || nested["outerTypeParamOffset"] != null
            || TypeJson.Read(nested["fields"][0]["type"]) != new TypeNode.Tv("type", 2)
            || TypeJson.Read(nested["fields"][1]["type"]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("type", 1) })
            || ((JsonArray)nested["typeParams"]).Count != 3)
            throw new InvalidOperationException("Nested frame lost enclosing companions or child source positions");
        TypeOwnershipLowering.PrepareOwnershipFacts(new[] { root });
        foreach (var closedCall in closedCalls)
            if (TypeJson.Read(closedCall["ret"]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("method", 2) })
                || TypeJson.Read(closedCall["ret"]) != TypeJson.Read(closedCall["sty"]))
                throw new InvalidOperationException("Caller result ownership depends on callee frame or property order");
        var method = root["methods"][0];
        var store = root["types"][1];
        if (((JsonArray)method["typeParams"]).Count != 2
            || TypeJson.Read(method["ret"]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("method", 1) })
            || ((JsonArray)root["methods"][1]["body"][0]["typeArgs"]).Count != 2
            || ((JsonArray)root["types"][0]["typeParams"]).Count != 1
            || ((JsonArray)store["typeParams"]).Count != 2
            || ((JsonArray)root["types"][2]["base"]["args"]).Count != 2
            || ((JsonArray)construction["type"]["args"]).Count != 2
            || TypeJson.Read(construction["memberSignature"][0]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("type", 1) })
            || ((JsonArray)genericCall["memberMethodTypeParams"]).Count != 2
            || TypeJson.Read(genericCall["memberSignature"][0]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("method", 1) })
            || TypeJson.Read(genericCall["memberReturnType"]) != TypeJson.Read(method["ret"])
            || TypeJson.Read(genericCall["argTypes"][0]) != TypeJson.Read(method["ret"])
            || TypeJson.Read(genericCall["ret"]) != TypeJson.Read(method["ret"])
            || Text(method[NullableRepresentationTypes.MethodFrameKey]) == null
            || Text(store[KotlinSupertypesRecord.PreKey]) == null
            || Text(method["nullableGenericRet"]) == null)
            throw new InvalidOperationException("Nullable frame materialization self-test failed");
        var bodyOnly = JsonNode.Parse("""
        {"kind":"class","name":"FixedEntry","methods":[{"name":"entry","static":false,"virtual":true,"typeParams":["T"],
         "params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},
         "body":[{"k":"newArraySized","elem":{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}}]}]}
        """);
        Apply(new[] { bodyOnly }, _ => false);
        if (((JsonArray)bodyOnly["methods"]).Count != 2
            || ((JsonArray)bodyOnly["methods"][0]["typeParams"]).Count != 1
            || ((JsonArray)bodyOnly["methods"][1]["typeParams"]).Count != 2
            || Text(bodyOnly["methods"][0]["body"][0]["value"]["k"]) != "cond")
            throw new InvalidOperationException("Body-only specialization changed the fixed entry frame");
        Console.WriteLine("[nullable frame materialization] self-test OK (declarations, calls, exact values, metadata)");
    }
}

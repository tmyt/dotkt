using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Select the physical CLR identity of Kotlin 2.4 companion extensions.
//
// kotc carries only Kotlin facts: receiver classifier, source member name, and whether a declaration/use is a
// function, property getter/setter, or field. This pass owns the physical CLR graph. For cross-module uses it accepts
// only the exact mapping indexed from producer metadata; it never reconstructs an ABI from generated names.
static class CompanionExtensionBinding
{
    const string KotlinDefault = "kotlin.clr.KotlinDefault";

    internal sealed record Binding(string PhysicalName, string PhysicalOwner, string ValueType = null);

    sealed class ExtensionGroup
    {
        public readonly List<JsonObject> Functions = [];
        public readonly List<JsonObject> Accessors = [];
        public readonly List<JsonObject> Fields = [];
    }

    sealed class PropertyParts
    {
        public string SourceName;
        public JsonObject Getter;
        public JsonObject Setter;
        public JsonObject Field;
    }

    public sealed class LocalIndex
    {
        internal LocalIndex(IReadOnlyDictionary<string, Binding> bindings) => Bindings = bindings;
        internal IReadOnlyDictionary<string, Binding> Bindings { get; }
    }

    public static LocalIndex Apply(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var bindings = CollectBindings(roots, refs);
        foreach (var root in roots)
        {
            RewriteUses(root, bindings, refs);
            RewriteDefaultCarriers(root, bindings, refs);
        }
        return new LocalIndex(bindings);
    }

    // Raw default-argument BIR is opaque while declarations are first bound, then materialized into a consumer root
    // later by InlineSplice or DefaultArgSplice. Bind only that newly materialized graph against the already-collected
    // module declaration index and trusted reference index; never rediscover declarations or re-walk sibling roots.
    public static void BindMaterializedUses(
        JsonNode root,
        LocalIndex local,
        ReferenceMetadataIndex refs) =>
        RewriteUses(root, local.Bindings, refs);

    static Dictionary<string, Binding> CollectBindings(
        IReadOnlyList<JsonNode> roots,
        ReferenceMetadataIndex refs)
    {
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        var localTypes = roots.OfType<JsonObject>()
            .SelectMany(root => (root["types"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(type => Str(type["name"]) is not null)
            .ToDictionary(
                type => Str(type["name"]),
                type => type,
                StringComparer.Ordinal);

        foreach (var root in roots.OfType<JsonObject>())
        {
            var owner = Str(root["fileClass"])
                ?? throw new InvalidOperationException("BIR file has no fileClass while binding companion extensions");
            var extensionGroups = new Dictionary<string, ExtensionGroup>(StringComparer.Ordinal);
            if (root["methods"] is JsonArray methods)
            {
                foreach (var method in methods.OfType<JsonObject>().ToArray())
                {
                    if (ShouldEmitCSharp14Function(method))
                    {
                        var receiver = Str(method["companionReceiver"])!;
                        if (!extensionGroups.TryGetValue(receiver, out var group))
                            extensionGroups[receiver] = group = new ExtensionGroup();
                        group.Functions.Add(method);
                        methods.Remove(method);
                    }
                    else if (ShouldEmitCSharp14PropertyAccessor(method))
                    {
                        var receiver = Str(method["companionReceiver"])!;
                        if (!extensionGroups.TryGetValue(receiver, out var group))
                            extensionGroups[receiver] = group = new ExtensionGroup();
                        group.Accessors.Add(method);
                        methods.Remove(method);
                    }
                    else BindDeclaration(owner, method, bindings);
                }
            }
            if (root["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>().ToArray())
                {
                    if (ShouldEmitCSharp14PropertyField(field))
                    {
                        var receiver = Str(field["companionReceiver"])!;
                        if (!extensionGroups.TryGetValue(receiver, out var group))
                            extensionGroups[receiver] = group = new ExtensionGroup();
                        group.Fields.Add(field);
                    }
                    else
                    {
                        BindDeclaration(owner, field, bindings);
                    }
                }
            foreach (var (receiver, declarations) in extensionGroups)
                MaterializeCSharp14Members(root, owner, receiver, declarations, bindings, localTypes, refs);
        }
        return bindings;
    }

    static void BindDeclaration(string owner, JsonObject declaration, Dictionary<string, Binding> bindings)
    {
        if (Str(declaration["companionReceiver"]) is not string receiver) return;
        var sourceName = Str(declaration["companionSourceName"])
            ?? throw new InvalidOperationException("companion extension declaration has no source name");
        var kind = Str(declaration["companionMemberKind"]);
        ValidateKind(kind);

        var physicalRoot = PhysicalRoot(receiver, sourceName);
        var physicalName = kind is "get" or "set"
            ? KotlinPropertyAccessors.PhysicalName(physicalRoot, kind)
            : physicalRoot;
        var key = Key(owner, receiver, kind, sourceName);
        if (bindings.TryGetValue(key, out var prior))
        {
            if (prior.PhysicalName != physicalName)
                throw new InvalidOperationException(
                    $"inconsistent companion-extension physical identity for '{owner}.{sourceName}'");
        }
        else bindings.Add(key, new Binding(physicalName, owner));

        declaration["name"] = physicalName;
    }

    static bool ShouldEmitCSharp14Function(JsonObject declaration)
    {
        if (Str(declaration["companionReceiver"]) is not string receiver ||
            Str(declaration["companionMemberKind"]) != "function")
            return false;
        // Increment 2 moves ordinary functions only. A suspend declaration is expanded into its public Task bridge
        // and cold core later in the pipeline; keeping it on the existing facade until that graph is authored avoids
        // manufacturing a grouping declaration from a pre-expansion signature.
        if ((declaration["mods"] as JsonObject)?["suspend"] is JsonValue suspend &&
            suspend.TryGetValue<bool>(out var isSuspend) && isSuspend)
            return false;
        return true;
    }

    static bool ShouldEmitCSharp14PropertyAccessor(JsonObject declaration)
    {
        if (Str(declaration["companionReceiver"]) is not string receiver ||
            Str(declaration["companionMemberKind"]) is not ("get" or "set"))
            return false;
        // Context parameters are physical index parameters on the CLR Property row. Multiple Kotlin context
        // overloads may share one source property name, while CIR's property-to-accessor edge is still name-only.
        // Keep those declarations on the legacy path until that edge carries a full method identity.
        if ((declaration["params"] as JsonArray)?.OfType<JsonObject>().Any(parameter =>
                (parameter["mods"] as JsonObject)?["context"]?.GetValue<bool>() == true) == true)
            return false;
        return true;
    }

    static bool ShouldEmitCSharp14PropertyField(JsonObject declaration) =>
        Str(declaration["companionReceiver"]) is not null &&
        Str(declaration["companionMemberKind"]) == "field";

    static void MaterializeCSharp14Members(
        JsonObject root,
        string semanticOwner,
        string receiver,
        ExtensionGroup declarations,
        Dictionary<string, Binding> bindings,
        IReadOnlyDictionary<string, JsonObject> localTypes,
        ReferenceMetadataIndex refs)
    {
        // Accessors moved into the C# 14 extension container take their CLR Property rows with them. Match the
        // original BIR row through kotc's exact file-local association, never through its source or physical name.
        // The signature Property row authored below is the sole MethodSemantics owner after the move.
        if (declarations.Accessors.Count != 0)
        {
            var movedAssociations = declarations.Accessors.Select(accessor =>
                Str(accessor[KotlinPropertyAccessors.AssociationKey])
                    ?? throw new InvalidOperationException("companion-extension accessor has no property association"))
                .ToHashSet(StringComparer.Ordinal);
            var rootProperties = root["properties"] as JsonArray
                ?? throw new InvalidOperationException("BIR file has no property declarations");
            foreach (var property in rootProperties.OfType<JsonObject>().ToArray())
                if (Str(property[KotlinPropertyAccessors.AssociationKey]) is string association &&
                    movedAssociations.Contains(association))
                    rootProperties.Remove(property);
        }
        var receiverClassifier = ReceiverClassifier(JsonNode.Parse(receiver))
            ?? throw new InvalidOperationException("companion extension receiver is not a classifier type: " + receiver);
        // Preserve the semantic classifier through BirTypeLowering when it has a CLR alias. Exact metadata spelling is
        // needed only for unaliased referenced types (notably nested generic TypeDefs); applying it to an alias first
        // would turn `kotlin.collections.List` into `kotlin.collections.List`1` and bypass the IReadOnlyList binding.
        var physicalReceiverClassifier = receiverClassifier;
        if (!localTypes.ContainsKey(receiverClassifier) && !refs.Aliases.ContainsKey(receiverClassifier) &&
            refs.TryExactPhysicalTypeName(receiverClassifier, refs.OwnerArity(receiverClassifier), out var exactReceiver))
            physicalReceiverClassifier = exactReceiver ?? throw new InvalidOperationException(
                $"ambiguous CLR metadata identity for companion-extension receiver '{receiverClassifier}'");
        var graphKey = semanticOwner + "\u001f" + receiverClassifier;
        var containerName = semanticOwner + "$extensions$" + StableId(graphKey).ToLowerInvariant();
        var groupSimpleName = "<G>$" + StableId("group\u001f" + graphKey);
        var markerSimpleName = "<M>$" + StableId("marker\u001f" + graphKey);
        var groupName = containerName + "." + groupSimpleName;
        var markerName = groupName + "." + markerSimpleName;
        var receiverTypeParams = ReceiverTypeParameters(receiverClassifier, localTypes, refs);
        var physicalReceiverTypeParams = PhysicalReceiverTypeParameters(receiverTypeParams);
        var blockArity = receiverTypeParams.Count;
        var implementations = new JsonArray();
        var signatureDeclarations = new JsonArray();
        var signatureProperties = new JsonArray();

        foreach (var declaration in declarations.Functions)
        {
            var sourceName = Str(declaration["companionSourceName"])
                ?? throw new InvalidOperationException("companion extension declaration has no source name");
            var key = Key(semanticOwner, receiver, "function", sourceName);
            var coreName = blockArity == 0 ? sourceName : CoreName("function", sourceName);
            var binding = new Binding(coreName, containerName);
            if (bindings.TryGetValue(key, out var prior) && prior != binding)
                throw new InvalidOperationException(
                    $"inconsistent companion-extension physical identity for '{semanticOwner}.{sourceName}'");
            bindings[key] = binding;

            var signature = (JsonObject)declaration.DeepClone();
            signature["name"] = sourceName;
            SetPhysicalOnlyIdentity(signature, declaration, "signature");
            signature["static"] = true;
            signature["override"] = false;
            signature["virtual"] = false;
            signature["abstract"] = false;
            signature["specialName"] = false;
            signature["bodyTerminates"] = true;
            signature["mods"] = new JsonObject();
            signature["attrs"] = new JsonArray(ExtensionMarker(markerSimpleName));
            signature["body"] = ThrowStubBody();
            signature.Remove("generated");
            signature.Remove("inlineBir");
            RemoveCompanionFacts(signature);
            signatureDeclarations.Add(signature);

            declaration["name"] = coreName;
            RemoveCompanionFacts(declaration);
            implementations.Add(declaration);
            if (blockArity != 0)
                implementations.Add(WrapperImplementation(
                    declaration, sourceName, coreName, containerName, receiverTypeParams));
        }

        var properties = new Dictionary<string, PropertyParts>(StringComparer.Ordinal);
        foreach (var accessor in declarations.Accessors)
        {
            var sourceName = Str(accessor["companionSourceName"])
                ?? throw new InvalidOperationException("companion extension accessor has no source name");
            if (!properties.TryGetValue(sourceName, out var parts))
                properties[sourceName] = parts = new PropertyParts { SourceName = sourceName };
            var kind = Str(accessor["companionMemberKind"]);
            if (kind == "get")
            {
                if (parts.Getter != null)
                    throw new InvalidOperationException($"duplicate companion-extension getter '{semanticOwner}.{sourceName}'");
                parts.Getter = accessor;
            }
            else
            {
                if (parts.Setter != null)
                    throw new InvalidOperationException($"duplicate companion-extension setter '{semanticOwner}.{sourceName}'");
                parts.Setter = accessor;
            }
        }
        foreach (var field in declarations.Fields)
        {
            var sourceName = Str(field["companionSourceName"])
                ?? throw new InvalidOperationException("companion extension field has no source name");
            if (properties.ContainsKey(sourceName))
                throw new InvalidOperationException($"duplicate companion-extension property '{semanticOwner}.{sourceName}'");
            properties[sourceName] = new PropertyParts { SourceName = sourceName, Field = field };
        }

        foreach (var parts in properties.Values)
        {
            if (parts.Field != null)
                MaterializeFieldBackedProperty(parts, semanticOwner, receiver);
            if (parts.Getter == null)
                throw new InvalidOperationException(
                    $"companion-extension property '{semanticOwner}.{parts.SourceName}' has no getter");

            var propertyTypeJson = parts.Getter["ret"]!.ToJsonString();
            // The C# 14 signature PropertyDef, its implementation binding, and every generated wrapper must name the
            // same final accessor MethodDef. The common allocator applies explicitClrName later, so state that known
            // final name here as well instead of freezing the pre-allocation prop_get/prop_set descriptor.
            var getterPhysicalName = Str(parts.Getter[DeclarationIdentityBinding.ExplicitNameKey])
                ?? KotlinPropertyAccessors.PhysicalName(parts.SourceName, "get");
            var setterPhysicalName = Str(parts.Setter?[DeclarationIdentityBinding.ExplicitNameKey])
                ?? KotlinPropertyAccessors.PhysicalName(parts.SourceName, "set");
            var getterCoreName = blockArity == 0 ? getterPhysicalName : CoreName("get", parts.SourceName);
            var setterCoreName = blockArity == 0 ? setterPhysicalName : CoreName("set", parts.SourceName);
            AddPropertyBinding(bindings, semanticOwner, receiver, "get", parts.SourceName, containerName, propertyTypeJson,
                getterCoreName);
            if (parts.Setter != null)
                AddPropertyBinding(bindings, semanticOwner, receiver, "set", parts.SourceName, containerName, propertyTypeJson,
                    setterCoreName);

            PrepareImplementationAccessor(parts.Getter, getterCoreName, implementations);
            if (blockArity != 0)
                implementations.Add(WrapperImplementation(
                    parts.Getter, getterPhysicalName, getterCoreName, containerName, receiverTypeParams));
            if (parts.Setter != null)
            {
                PrepareImplementationAccessor(parts.Setter, setterCoreName, implementations);
                if (blockArity != 0)
                    implementations.Add(WrapperImplementation(
                        parts.Setter, setterPhysicalName, setterCoreName, containerName, receiverTypeParams));
            }

            var getterSignature = SignatureAccessor(parts.Getter, getterPhysicalName, markerSimpleName);
            signatureDeclarations.Add(getterSignature);
            JsonObject setterSignature = null;
            if (parts.Setter != null)
            {
                setterSignature = SignatureAccessor(parts.Setter, setterPhysicalName, markerSimpleName);
                signatureDeclarations.Add(setterSignature);
            }
            var signatureProperty = new JsonObject
            {
                ["name"] = parts.SourceName,
                ["type"] = parts.Getter["ret"]!.DeepClone(),
                ["get"] = getterPhysicalName,
                ["getSig"] = AccessorSignature(getterSignature),
                ["getMethodArity"] = (getterSignature["typeParams"] as JsonArray)?.Count ?? 0,
                ["set"] = setterSignature == null ? null : setterPhysicalName,
                ["attrs"] = new JsonArray(ExtensionMarker(markerSimpleName)),
            };
            if (setterSignature != null)
            {
                signatureProperty["setSig"] = AccessorSignature(setterSignature);
                signatureProperty["setMethodArity"] =
                    (setterSignature["typeParams"] as JsonArray)?.Count ?? 0;
            }
            signatureProperties.Add(signatureProperty);
        }

        var container = new JsonObject
        {
            ["name"] = containerName,
            ["kind"] = "class",
            ["vis"] = "public",
            ["abstract"] = true,
            ["final"] = true,
            ["beforeFieldInit"] = true,
            ["generated"] = true,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["methods"] = implementations,
            ["properties"] = new JsonArray(),
            ["ctors"] = new JsonArray(),
            ["attrs"] = new JsonArray(ExtensionAttribute()),
        };
        // Roslyn recognizes the grouping TypeDef only with the C# 14 sealed, non-abstract shape.
        // PersistedAssemblyBuilder adds an otherwise inert default constructor to that shape; consumers ignore
        // constructors while validating every attributed extension declaration.
        var group = new JsonObject
        {
            ["name"] = groupName,
            ["kind"] = "class",
            ["nestedIn"] = containerName,
            ["vis"] = "public",
            ["final"] = true,
            ["specialName"] = true,
            ["generated"] = true,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["methods"] = signatureDeclarations,
            ["properties"] = signatureProperties,
            ["ctors"] = new JsonArray(),
            ["attrs"] = new JsonArray(ExtensionAttribute()),
        };
        if (blockArity != 0) group["typeParams"] = physicalReceiverTypeParams.DeepClone();
        var marker = new JsonObject
        {
            ["name"] = markerName,
            ["kind"] = "class",
            ["nestedIn"] = groupName,
            ["vis"] = "public",
            ["abstract"] = true,
            ["final"] = true,
            ["specialName"] = true,
            ["generated"] = true,
            ["interfaces"] = new JsonArray(),
            ["fields"] = new JsonArray(),
            ["methods"] = new JsonArray(new JsonObject
            {
                ["name"] = "<Extension>$",
                ["static"] = true,
                ["override"] = false,
                ["virtual"] = false,
                ["abstract"] = false,
                ["specialName"] = true,
                ["generated"] = true,
                ["vis"] = "public",
                ["params"] = new JsonArray(new JsonObject
                {
                    ["name"] = "",
                    ["type"] = blockArity == 0
                        ? JsonNode.Parse(receiver)
                        : TypeJson.Write(new TypeNode.Fqn(physicalReceiverClassifier,
                            Enumerable.Range(0, blockArity)
                                .Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray())),
                }),
                ["ret"] = TypeJson.Fqn("void"),
                ["body"] = new JsonArray(),
                ["attrs"] = new JsonArray(),
            }),
            ["ctors"] = new JsonArray(),
            ["attrs"] = new JsonArray(),
        };
        if (blockArity != 0) marker["capturedTypeParams"] = physicalReceiverTypeParams.DeepClone();
        var types = root["types"] as JsonArray ?? new JsonArray();
        types.Add(container);
        types.Add(group);
        types.Add(marker);
        root["types"] = types;
    }

    static void AddPropertyBinding(
        Dictionary<string, Binding> bindings,
        string semanticOwner,
        string receiver,
        string kind,
        string sourceName,
        string containerName,
        string valueType,
        string physicalName)
    {
        var key = Key(semanticOwner, receiver, kind, sourceName);
        var binding = new Binding(physicalName, containerName, valueType);
        if (bindings.TryGetValue(key, out var prior) && prior != binding)
            throw new InvalidOperationException(
                $"inconsistent companion-extension physical identity for '{semanticOwner}.{sourceName}'");
        bindings[key] = binding;
    }

    static JsonArray ReceiverTypeParameters(
        string receiver,
        IReadOnlyDictionary<string, JsonObject> localTypes,
        ReferenceMetadataIndex refs)
    {
        JsonArray source = null;
        if (localTypes.TryGetValue(receiver, out var local))
        {
            var captured = local["capturedTypeParams"] as JsonArray;
            var declared = local["typeParams"] as JsonArray;
            source = new JsonArray();
            if (captured != null)
                foreach (var parameter in captured) source.Add(parameter?.DeepClone());
            if (declared != null)
                foreach (var parameter in declared) source.Add(parameter?.DeepClone());
        }
        else source = refs.OwnerTypeParamDeclarations(receiver);

        var expected = localTypes.TryGetValue(receiver, out var definition)
            ? ((definition["capturedTypeParams"] as JsonArray)?.Count ?? 0) +
                ((definition["typeParams"] as JsonArray)?.Count ?? 0)
            : refs.OwnerArity(receiver);
        if (expected == 0) return new JsonArray();
        if (source == null || source.Count != expected)
            throw new InvalidOperationException(
                $"generic companion-extension receiver '{receiver}' has no exact type-parameter declarations");
        return source;
    }

    static JsonArray WrapperBlockTypeParameters(JsonArray receiverTypeParams, JsonArray ownTypeParams)
    {
        var usedNames = new HashSet<string>(
            (ownTypeParams ?? new JsonArray()).Select(parameter => parameter is JsonObject obj
                ? Str(obj["name"]) : Str(parameter)).Where(name => name is not null),
            StringComparer.Ordinal);
        var result = new JsonArray();
        for (var index = 0; index < receiverTypeParams.Count; index++)
        {
            var source = receiverTypeParams[index];
            var descriptor = source is JsonObject obj
                ? (JsonObject)obj.DeepClone()
                : new JsonObject { ["name"] = Str(source) ?? "T" + index };
            var name = "dotkt$receiver$" + index;
            while (!usedNames.Add(name)) name += "$";
            descriptor["name"] = name;
            if (descriptor["constraints"] is JsonArray constraints)
                descriptor["constraints"] = new JsonArray(constraints.Select(constraint =>
                    constraint == null ? null : TypeJson.Write(RemapWrapperType(TypeJson.Read(constraint), 0))).ToArray());
            descriptor.Remove("variance");
            result.Add(descriptor);
        }
        return result;
    }

    // CLR generic-parameter names share one namespace across a flattened nested declaration. Imported nested types
    // can legally repeat an outer name on an inner slot (`Outer<T>.Inner<T>`), but ilemit's builder map is name-keyed.
    // Give the signature group and its nested receiver marker one collision-free physical spelling while retaining
    // the exact positional constraints. The wrapper performs the separate type-scope -> method-scope remap below.
    static JsonArray PhysicalReceiverTypeParameters(JsonArray receiverTypeParams)
    {
        var result = new JsonArray();
        for (var index = 0; index < receiverTypeParams.Count; index++)
        {
            var source = receiverTypeParams[index];
            var descriptor = source is JsonObject obj
                ? (JsonObject)obj.DeepClone()
                : new JsonObject { ["name"] = Str(source) ?? "T" + index };
            descriptor["name"] = "dotkt$receiver$" + index;
            descriptor.Remove("variance");
            result.Add(descriptor);
        }
        return result;
    }

    static JsonObject WrapperImplementation(
        JsonObject core,
        string wrapperName,
        string coreName,
        string containerName,
        JsonArray blockTypeParams)
    {
        var blockArity = blockTypeParams.Count;
        var wrapper = (JsonObject)core.DeepClone();
        wrapper["name"] = wrapperName;
        SetPhysicalOnlyIdentity(wrapper, core, "wrapper");
        KotlinPropertyAccessors.RemoveIdentity(wrapper);
        wrapper["generated"] = true;
        wrapper["mods"] = new JsonObject();
        wrapper.Remove("inlineBir");
        wrapper.Remove("suspendBridge");

        var ownTypeParams = core["typeParams"] as JsonArray;
        blockTypeParams = WrapperBlockTypeParameters(blockTypeParams, ownTypeParams);
        var combined = new JsonArray();
        foreach (var parameter in blockTypeParams) combined.Add(parameter?.DeepClone());
        if (ownTypeParams != null)
            foreach (var parameter in ownTypeParams)
            {
                var shifted = parameter is JsonObject obj
                    ? (JsonObject)obj.DeepClone()
                    : new JsonObject { ["name"] = Str(parameter) };
                if (shifted["constraints"] is JsonArray constraints)
                    shifted["constraints"] = new JsonArray(constraints.Select(constraint =>
                        constraint == null ? null : TypeJson.Write(
                            RemapWrapperType(TypeJson.Read(constraint), blockArity))).ToArray());
                combined.Add(shifted);
            }
        wrapper["typeParams"] = combined;

        var parameters = wrapper["params"] as JsonArray ?? new JsonArray();
        foreach (var parameter in parameters.OfType<JsonObject>())
            if (parameter["type"] is JsonNode type)
                parameter["type"] = TypeJson.Write(RemapWrapperType(TypeJson.Read(type), blockArity));
        wrapper["ret"] = TypeJson.Write(RemapWrapperType(TypeJson.Read(wrapper["ret"]!), blockArity));

        var args = new JsonArray(parameters.OfType<JsonObject>().Select(parameter => (JsonNode)new JsonObject
        {
            ["k"] = "local",
            ["name"] = parameter["name"]?.DeepClone(),
        }).ToArray());
        var call = new JsonObject
        {
            ["k"] = "callStatic",
            ["owner"] = TypeJson.Fqn(containerName),
            ["ownerType"] = TypeJson.Fqn(containerName),
            ["method"] = coreName,
            ["sig"] = new JsonArray(parameters.OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray()),
            ["args"] = args,
            ["ret"] = wrapper["ret"]!.DeepClone(),
        };
        if (Str(core[DeclarationIdentityBinding.Key]) is string coreIdentity)
        {
            call[DeclarationIdentityBinding.Key] = coreIdentity;
            wrapper["extensionCoreDeclarationId"] = coreIdentity;
        }
        else
            RoundtripMetadata.StampExtensionCore(wrapper, coreName);
        var ownArity = ownTypeParams?.Count ?? 0;
        if (ownArity != 0)
            call["typeArgs"] = new JsonArray(Enumerable.Range(0, ownArity)
                .Select(index => (JsonNode)TypeJson.Write(
                    new TypeNode.Tv("method", blockArity + index))).ToArray());
        var returnsUnit = TypeJson.Read(wrapper["ret"]!) is TypeNode.Fqn { Name: "kotlin.Unit" or "void" };
        wrapper["body"] = returnsUnit
            ? new JsonArray(
                new JsonObject { ["k"] = "exprStmt", ["expr"] = call },
                new JsonObject { ["k"] = "return" })
            : new JsonArray(new JsonObject { ["k"] = "return", ["value"] = call });
        return wrapper;
    }

    static TypeNode RemapWrapperType(TypeNode type, int methodShift) => type switch
    {
        TypeNode.Tv tv when tv.Scope == "type" => new TypeNode.Tv("method", tv.I),
        TypeNode.Tv tv when tv.Scope == "method" => new TypeNode.Tv("method", tv.I + methodShift),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(arg => RemapWrapperType(arg, methodShift)).ToArray()),
        TypeNode.Nullable nullable => new TypeNode.Nullable(RemapWrapperType(nullable.Of, methodShift)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(RemapWrapperType(oblivious.Of, methodShift)),
        TypeNode.Array array => new TypeNode.Array(RemapWrapperType(array.Elem, methodShift)),
        TypeNode.ByRef byRef => new TypeNode.ByRef(RemapWrapperType(byRef.Of, methodShift)),
        TypeNode.Fn function => new TypeNode.Fn(
            function.Suspend,
            RemapWrapperType(function.Ret, methodShift),
            function.Params.Select(parameter => RemapWrapperType(parameter, methodShift)).ToArray(),
            function.Recv == null ? null : RemapWrapperType(function.Recv, methodShift),
            function.Clr,
            function.Ctx?.Select(parameter => RemapWrapperType(parameter, methodShift)).ToArray()),
        _ => type,
    };

    static string CoreName(string kind, string sourceName)
    {
        var root = "dotkt$core$" + sourceName;
        return kind is "get" or "set" ? KotlinPropertyAccessors.PhysicalName(root, kind) : root;
    }

    static void MaterializeFieldBackedProperty(
        PropertyParts parts,
        string storageOwner,
        string receiver)
    {
        var field = parts.Field;
        var sourceName = parts.SourceName;
        var backingName = PhysicalRoot(receiver, sourceName) + "$storage";
        var propertyType = field["type"]!.DeepClone();
        var sourceVisibility = Str(field["vis"]) ?? "public";
        var mutable = field["companionPropertyMutable"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                $"companion-extension storage '{storageOwner}.{sourceName}' has no val/var fact");
        var setterVisibility = mutable
            ? Str(field["companionSetterVisibility"])
                ?? throw new InvalidOperationException(
                    $"companion-extension var '{storageOwner}.{sourceName}' has no setter visibility")
            : null;
        var storageReadOnly = field["companionStorageReadOnly"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                $"companion-extension storage '{storageOwner}.{sourceName}' has no mutability fact");
        field["name"] = backingName;
        field["vis"] = "private";
        field["static"] = true;
        var isConst = field["const"]?.GetValue<bool>() == true;
        if (storageReadOnly && !isConst) field["initOnly"] = true;
        field.Remove("companionPropertyMutable");
        field.Remove("companionSetterVisibility");
        field.Remove("companionStorageReadOnly");
        RemoveCompanionFacts(field);

        var read = isConst
            ? field["init"]?.DeepClone()
                ?? throw new InvalidOperationException($"const companion-extension property '{sourceName}' has no value")
            : field["lateinit"]?.GetValue<bool>() == true
            ? new JsonObject
            {
                ["sty"] = propertyType.DeepClone(),
                ["k"] = "lateinitGet",
                ["ownerType"] = TypeJson.Fqn(storageOwner),
                ["static"] = true,
                ["name"] = backingName,
                ["lateinitSourceName"] = sourceName,
            }
            : new JsonObject
            {
                ["sty"] = propertyType.DeepClone(),
                ["k"] = "staticField",
                ["ownerType"] = TypeJson.Fqn(storageOwner),
                ["name"] = backingName,
            };
        parts.Getter = Accessor(
            sourceName,
            sourceName,
            "get",
            sourceVisibility,
            new JsonArray(new JsonObject { ["k"] = "return", ["value"] = read }),
            new JsonArray(),
            propertyType.DeepClone());
        if (Str(field["companionGetterExplicitClrName"]) is string getterExplicitName)
            parts.Getter[DeclarationIdentityBinding.ExplicitNameKey] = getterExplicitName;
        if (Str(field["companionGetterDeclarationId"]) is string getterDeclarationId)
        {
            parts.Getter[DeclarationIdentityBinding.Key] = getterDeclarationId;
            parts.Getter["declarationSourceName"] = sourceName;
        }
        if (isConst || field["lateinit"]?.GetValue<bool>() == true)
            RoundtripMetadata.StampPropertyStorage(parts.Getter, storageOwner, backingName);
        if (mutable)
            parts.Setter = Accessor(
                sourceName,
                sourceName,
                "set",
                setterVisibility,
                new JsonArray(new JsonObject
                {
                    ["k"] = "exprStmt",
                    ["expr"] = new JsonObject
                    {
                        ["k"] = "staticFieldSet",
                        ["ownerType"] = TypeJson.Fqn(storageOwner),
                        ["name"] = backingName,
                        ["value"] = new JsonObject { ["k"] = "local", ["name"] = "value" },
                    },
                }),
                new JsonArray(new JsonObject { ["name"] = "value", ["type"] = propertyType.DeepClone() }),
                TypeJson.Fqn("kotlin.Unit"));
        if (parts.Setter != null &&
            Str(field["companionSetterExplicitClrName"]) is string setterExplicitName)
            parts.Setter[DeclarationIdentityBinding.ExplicitNameKey] = setterExplicitName;
        if (parts.Setter != null &&
            Str(field["companionSetterDeclarationId"]) is string setterDeclarationId)
        {
            parts.Setter[DeclarationIdentityBinding.Key] = setterDeclarationId;
            parts.Setter["declarationSourceName"] = sourceName;
        }
        field.Remove("companionGetterExplicitClrName");
        field.Remove("companionSetterExplicitClrName");
        field.Remove("companionGetterDeclarationId");
        field.Remove("companionSetterDeclarationId");
    }

    static JsonObject Accessor(
        string name,
        string propertyName,
        string propertyAccessor,
        string visibility,
        JsonArray body,
        JsonArray parameters,
        JsonNode returnType) => new()
        {
            ["name"] = name,
            [KotlinPropertyAccessors.SourceNameKey] = propertyName,
            [KotlinPropertyAccessors.KindKey] = propertyAccessor,
            ["static"] = true,
            ["override"] = false,
            ["virtual"] = false,
            ["abstract"] = false,
            ["specialName"] = false,
            ["vis"] = visibility,
            ["params"] = parameters,
            ["ret"] = returnType,
            ["body"] = body,
            ["attrs"] = new JsonArray(),
        };

    static void PrepareImplementationAccessor(JsonObject accessor, string physicalName, JsonArray implementations)
    {
        accessor["name"] = physicalName;
        accessor["static"] = true;
        accessor["override"] = false;
        accessor["virtual"] = false;
        accessor["abstract"] = false;
        accessor["specialName"] = false;
        RemoveCompanionFacts(accessor);
        implementations.Add(accessor);
    }

    static JsonObject SignatureAccessor(JsonObject accessor, string physicalName, string markerSimpleName)
    {
        var signature = (JsonObject)accessor.DeepClone();
        signature["name"] = physicalName;
        SetPhysicalOnlyIdentity(signature, accessor, "signature");
        KotlinPropertyAccessors.RemoveIdentity(signature);
        signature["static"] = true;
        signature["override"] = false;
        signature["virtual"] = false;
        signature["abstract"] = false;
        signature["specialName"] = true;
        signature["bodyTerminates"] = true;
        signature["mods"] = new JsonObject();
        signature["attrs"] = new JsonArray(ExtensionMarker(markerSimpleName));
        signature["body"] = ThrowStubBody();
        signature.Remove("generated");
        signature.Remove("inlineBir");
        RemoveCompanionFacts(signature);
        return signature;
    }

    static JsonArray AccessorSignature(JsonObject accessor) => new(
        ((accessor["params"] as JsonArray) ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(parameter => parameter["type"]?.DeepClone()
                ?? throw new InvalidOperationException("companion-extension accessor parameter has no type"))
            .ToArray());

    static void RemoveCompanionFacts(JsonObject declaration)
    {
        declaration.Remove("companionReceiver");
        declaration.Remove("companionSourceName");
        declaration.Remove("companionMemberKind");
    }

    static void SetPhysicalOnlyIdentity(JsonObject target, JsonObject declaration, string role)
    {
        if (Str(declaration[DeclarationIdentityBinding.Key]) is string id)
            target[DeclarationIdentityBinding.Key] = DeclarationIdentityBinding.PhysicalOnlyId(id, role);
    }

    static JsonArray ThrowStubBody() => new(new JsonObject
    {
        ["k"] = "throw",
        ["value"] = new JsonObject
        {
            ["k"] = "newClr",
            ["type"] = TypeJson.Fqn("System.NotSupportedException"),
            ["argTypes"] = new JsonArray(),
            ["args"] = new JsonArray(),
        },
    });

    static JsonObject ExtensionAttribute() => Attribute(
        "System.Runtime.CompilerServices.ExtensionAttribute");

    static JsonObject ExtensionMarker(string markerName) => Attribute(
        "System.Runtime.CompilerServices.ExtensionMarkerAttribute",
        new JsonObject { ["value"] = markerName, ["type"] = TypeJson.Fqn("System.String") });

    static JsonObject Attribute(string type, params JsonObject[] arguments)
    {
        var args = new JsonArray();
        var argTypes = new JsonArray();
        foreach (var argument in arguments)
        {
            args.Add(argument);
            argTypes.Add(argument["type"]!.DeepClone());
        }
        return new JsonObject
        {
            ["attr"] = TypeJson.Fqn(type),
            ["argTypes"] = argTypes,
            ["args"] = args,
            ["attrExternal"] = true,
            ["attrAssembly"] = "System.Runtime",
        };
    }

    static string StableId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    static void ValidateKind(string kind)
    {
        if (kind is not ("get" or "set" or "function" or "field"))
            throw new InvalidOperationException("invalid companion extension member kind: " + kind);
    }

    static string PhysicalRoot(string receiverJson, string sourceName)
    {
        var receiver = ReceiverClassifier(JsonNode.Parse(receiverJson))
            ?? throw new InvalidOperationException(
                "companion extension receiver is not a classifier type: " + receiverJson);
        var encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(receiver)).ToLowerInvariant();
        return "dotkt$companion$" + encoded + "$" + sourceName;
    }

    static string Key(string owner, string receiver, string kind, string sourceName) =>
        NormalizeOwner(owner) + "\u001f" + CanonicalReceiver(receiver) + "\u001f" + kind + "\u001f" + sourceName;

    static string NormalizeOwner(string owner) => owner.Replace('+', '.');

    static string CanonicalReceiver(string receiver)
    {
        var classifier = ReceiverClassifier(JsonNode.Parse(receiver))
            ?? throw new InvalidOperationException(
                "companion extension receiver is not a classifier type: " + receiver);
        // Kotlin accepts only a bare classifier here. A projected generic classifier may be rehydrated by the
        // consumer frontend as C<Any>, but those arguments are not part of the companion association.
        return TypeJson.Fqn(classifier).ToJsonString();
    }

    // A classifier imported from an ordinary C# assembly is commonly a platform type (`oblivious(fqn)`). The
    // companion association is the bare classifier and is independent of NRT flexibility/nullability.
    static string ReceiverClassifier(JsonNode node)
    {
        TypeNode type = TypeJson.Read(node);
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        return type is TypeNode.Fqn fqn ? fqn.Name : null;
    }

    static void RewriteUses(
        JsonNode node,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                RewriteUse(obj, bindings, refs);
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                    RewriteUses(child, bindings, refs);
                break;
            case JsonArray array:
                foreach (var child in array.Where(value => value != null).ToList())
                    RewriteUses(child, bindings, refs);
                break;
        }
    }

    // kotc serializes a default expression into the KotlinDefault string before bir2cir selects the CLR owner. Bind
    // that producer-authored BIR now, exactly like the ordinary body beside it, so a later module never needs an alias
    // from the retired semantic file facade to the C# 14 implementation container.
    static void RewriteDefaultCarriers(
        JsonNode node,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            if (TypeJson.OwnerName(obj["attr"]) == KotlinDefault &&
                obj["args"] is JsonArray args && args.Count >= 2 &&
                args[1] is JsonObject carrierArg &&
                (carrierArg["value"] as JsonValue)?.TryGetValue<string>(out var carrierJson) == true &&
                !string.IsNullOrEmpty(carrierJson))
            {
                var payload = JsonNode.Parse(carrierJson)
                    ?? throw new InvalidOperationException("KotlinDefault carrier decoded to null");
                RewriteUses(payload, bindings, refs);
                carrierArg["value"] = payload.ToJsonString();
            }

            foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                RewriteDefaultCarriers(child, bindings, refs);
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(value => value != null).ToList())
                RewriteDefaultCarriers(child, bindings, refs);
    }

    static void RewriteUse(
        JsonObject node,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs)
    {
        if (Str(node["companionReceiver"]) is not string receiver) return;
        // Declarations retain these semantic facts until RoundtripMetadata stamps the producer carrier.
        // Only use sites have a receiver tag without declaration identity.
        if (node["companionSourceName"] != null || node["companionMemberKind"] != null) return;
        var nodeKind = Str(node["k"]);

        // A referenced DotKt declaration already has the exact MethodDef selected by FIR. The companion tag only
        // supplies semantic association; it must never send an identified use back through the erased source-name
        // index. Local identities are absent from refs and continue through the local companion-container pass below.
        if (Str(node[DeclarationIdentityBinding.Key]) is string declarationId &&
            refs.TryDeclarationIdentity(
                declarationId, out var exactName, out var exactOwner, out var intrinsic, out _) &&
            intrinsic == null)
        {
            switch (nodeKind)
            {
                case "callInline":
                    node["owner"] = TypeJson.Fqn(exactOwner);
                    node["callee"] = TypeJson.Fqn(exactOwner + "." + exactName);
                    break;
                case "callStatic":
                    node["method"] = exactName;
                    RewriteStaticOwner(node, exactOwner);
                    node.Remove("prop");
                    break;
                case "newDelegate":
                    node["method"] = exactName;
                    node["calleeOwner"] = TypeJson.Fqn(exactOwner);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"identified companion extension appears on unsupported BIR node '{nodeKind ?? "<missing>"}'");
            }
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind == "callInline")
        {
            var owner = TypeJson.OwnerName(node["owner"]);
            var callee = TypeJson.OwnerName(node["callee"]);
            if (owner == null || callee == null)
                throw Missing(owner, receiver, "function", callee, nodeKind);
            var dot = callee.LastIndexOf('.');
            var sourceName = dot < 0 ? callee : callee[(dot + 1)..];
            var physical = Resolve(owner, receiver, "function", sourceName, bindings, refs, nodeKind);
            node["owner"] = TypeJson.Fqn(physical.PhysicalOwner);
            node["callee"] = TypeJson.Fqn(physical.PhysicalOwner + "." + physical.PhysicalName);
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind == "callStatic")
        {
            var owner = TypeJson.OwnerName(node["calleeOwner"])
                ?? TypeJson.OwnerName(node["owner"])
                ?? TypeJson.OwnerName(node["ownerType"]);
            var sourceName = Str(node["method"]);
            var kind = Str(node["prop"]) switch
            {
                "get" => "get",
                "set" => "set",
                _ => "function",
            };
            var physical = Resolve(owner, receiver, kind, sourceName, bindings, refs, nodeKind);
            if (kind is "get" or "set")
                KotlinPropertyAccessors.PreserveCallIdentity(node, sourceName, kind);
            node["method"] = physical.PhysicalName;
            RewriteStaticOwner(node, physical.PhysicalOwner);
            node.Remove("prop");
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind == "newDelegate")
        {
            var owner = TypeJson.OwnerName(node["calleeOwner"]);
            var sourceName = Str(node["method"]);
            var physical = Resolve(owner, receiver, "function", sourceName, bindings, refs, nodeKind);
            node["method"] = physical.PhysicalName;
            node["calleeOwner"] = TypeJson.Fqn(physical.PhysicalOwner);
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind is "staticField" or "staticFieldSet" or "lateinitGet")
        {
            var owner = TypeJson.OwnerName(node["ownerType"]);
            var sourceName = Str(node["name"]);
            // Standard C# 14 static extension properties are executable through ordinary implementation accessors;
            // their fields, when any, are private storage details. Turn every Kotlin field-shaped use into that same
            // accessor call so same-module code, property references, and cross-module consumers share one path.
            var kind = nodeKind == "staticFieldSet" ? "set" : "get";
            if (!TryResolve(owner, receiver, kind, sourceName, bindings, refs, out var physical))
                throw Missing(owner, receiver, kind, sourceName, nodeKind);

            var value = nodeKind == "staticFieldSet" ? node["value"]?.DeepClone() : null;
            var style = node["sty"]?.DeepClone();
            node.Clear();
            if (style != null) node["sty"] = style;
            node["k"] = "callStatic";
            node["ownerType"] = TypeJson.Fqn(physical.PhysicalOwner);
            node["method"] = physical.PhysicalName;
            KotlinPropertyAccessors.PreserveCallIdentity(node, sourceName, kind);
            node["sig"] = kind == "set" && physical.ValueType != null
                ? new JsonArray(JsonNode.Parse(physical.ValueType))
                : new JsonArray();
            node["args"] = value == null ? new JsonArray() : new JsonArray(value);
            return;
        }

        throw new InvalidOperationException(
            $"companion extension tag appears on unsupported BIR node '{nodeKind ?? "<missing>"}'");
    }

    static Binding Resolve(
        string owner,
        string receiver,
        string kind,
        string sourceName,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs,
        string nodeKind)
    {
        if (TryResolve(owner, receiver, kind, sourceName, bindings, refs, out var binding)) return binding;
        throw Missing(owner, receiver, kind, sourceName, nodeKind);
    }

    static bool TryResolve(
        string owner,
        string receiver,
        string kind,
        string sourceName,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs,
        out Binding binding)
    {
        binding = null;
        if (owner == null || sourceName == null) return false;
        if (bindings.TryGetValue(Key(owner, receiver, kind, sourceName), out binding)) return true;
        if (!refs.TryCompanionExtensionMember(owner, receiver, kind, sourceName, out var physical)) return false;
        binding = new Binding(physical, owner);
        return true;
    }

    static void RewriteStaticOwner(JsonObject node, string physicalOwner)
    {
        if (node["calleeOwner"] is not null) node["calleeOwner"] = TypeJson.Fqn(physicalOwner);
        else if (node["owner"] is not null) node["owner"] = TypeJson.Fqn(physicalOwner);
        else if (node["ownerType"] is not null) node["ownerType"] = TypeJson.Fqn(physicalOwner);
        else node["calleeOwner"] = TypeJson.Fqn(physicalOwner);
    }

    static InvalidOperationException Missing(
        string owner, string receiver, string kind, string sourceName, string nodeKind) =>
        new($"no trusted companion-extension binding for '{owner ?? "<missing>"}.{sourceName ?? "<missing>"}' " +
            $"(receiver {receiver}, kind {kind}, BIR node {nodeKind})");

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}

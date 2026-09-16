using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materializes signature frames and independently owned static implementation frames. Instance body-only
// specialization is separate: it must not grow a published virtual slot. Runs after the Kotlin declaration snapshot.
static class NullableRepresentationMaterialization
{
    public static void Apply(IEnumerable<JsonNode> inputs, ValueTypeOracle isValue, ReferenceMetadataIndex references = null)
    {
        var roots = inputs.ToArray();
        var importedMethods = new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal);
        void FindReferencedCalls(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (Text(obj["k"]) != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                    && references?.NullableMethodFrame(id) is { } frame) importedMethods[id] = frame;
                foreach (var (key, value) in obj)
                    if (key != "attrs") FindReferencedCalls(value);
            }
            else if (node is JsonArray array)
                foreach (var item in array) FindReferencedCalls(item);
        }
        foreach (var root in roots) FindReferencedCalls(root);
        var demands = NullableRepresentationDemand.Collect(roots, references?.NullableTypeFrames, importedMethods);
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
        var types = references == null ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
            : new Dictionary<string, NullableRepresentationFrame>(references.NullableTypeFrames, StringComparer.Ordinal);
        foreach (var owner in demands.Where(d => d.IsTypeDeclaration))
            types[Text(owner.Declaration["name"])] = ownerFrames[owner.Declaration];
        var methods = new Dictionary<string, NullableRepresentationFrame>(importedMethods, StringComparer.Ordinal);
        foreach (var method in demands.SelectMany(d => d.Methods))
            if (Text(method.Declaration[DeclarationIdentityBinding.Key]) is string id) methods[id] = method.Frame;
        foreach (var root in roots.OfType<JsonObject>()) NullableGenericErasure.PreserveSourceFacts(root, isValue);
        var empty = new NullableRepresentationFrame(0, Array.Empty<int>());
        var declarations = demands.SelectMany(owner => owner.Methods.Select(method => (owner, method)))
            .Where(pair => Text(pair.method.Declaration[DeclarationIdentityBinding.Key]) != null)
            .ToDictionary(pair => Text(pair.method.Declaration[DeclarationIdentityBinding.Key]),
                pair => new NullableRepresentationTypes(pair.owner.Frame, pair.method.Frame, types, isValue), StringComparer.Ordinal);
        foreach (var (id, frame) in importedMethods)
            if (!declarations.ContainsKey(id) && references.TryDeclarationIdentity(id, out _, out var owner, out _, out _))
                declarations[id] = new NullableRepresentationTypes(types.GetValueOrDefault(owner) ?? empty, frame, types, isValue);
        NullableRepresentationTypes DeclarationMapping(JsonObject use)
        {
            if (Text(use[DeclarationIdentityBinding.Key]) is string id && declarations.TryGetValue(id, out var selected))
                return selected;
            var ownerType = Text(use["k"]) == "new" ? use["type"] : use["ownerType"] ?? use["owner"];
            return TypeJson.Read(ownerType) is TypeNode.Fqn owner && types.TryGetValue(owner.Name, out var frame)
                // Knowing the owner does not establish a zero-arity method frame. Unbound method variables in
                // its declaration signature must not be interpreted as variables of a fictitious empty method.
                ? new NullableRepresentationTypes(frame, null, types, isValue) : null;
        }
        foreach (var owner in demands)
        {
            try
            {
                var frame = ownerFrames[owner.Declaration];
                var mapping = new NullableRepresentationTypes(frame, empty, types, isValue);
                PreserveEdges(owner.Declaration, mapping);
                foreach (var key in owner.Declaration.Select(p => p.Key).ToArray())
                    if (key is not ("types" or "methods" or "attrs" or "overrides" or "refTypes")
                        && !NullableRepresentationDemand.InheritedMemberKeys.Contains(key))
                    {
                        if (TypeJson.Read(owner.Declaration[key]) is TypeNode type)
                            owner.Declaration[key] = TypeJson.Write(mapping.Slot(type));
                        else Rewrite(owner.Declaration[key], mapping, methods, DeclarationMapping);
                    }
                foreach (var method in owner.Methods)
                {
                    var methodFrame = methodFrames[method.Declaration];
                    if (method.ImplementationKey != null)
                    {
                        var inheritedMapping = new NullableRepresentationTypes(frame, methodFrame, types, isValue);
                        foreach (var key in new[] { "params", "ret" })
                        {
                            if (TypeJson.Read(method.Declaration[key]) is TypeNode type)
                                method.Declaration[key] = TypeJson.Write(inheritedMapping.Slot(type));
                            else Rewrite(method.Declaration[key], inheritedMapping, methods, DeclarationMapping);
                        }
                        var implementationMapping = DeclarationMapping(method.Implementation);
                        RewriteDescriptor(method.Declaration, method.ImplementationKey,
                            implementationMapping == null ? null : new NullableRepresentationTypes(
                                implementationMapping.OwnerFrame, methodFrame, types, isValue), inheritedMapping);
                        AppendParameters(method.Implementation, methodFrame);
                        method.Implementation["arity"] = methodFrame.PhysicalArity;
                        continue;
                    }
                    var splitBody = method.Body.Method.Except(methodFrame.NullableIndices).Any();
                    var source = splitBody ? (JsonObject)method.Declaration.DeepClone() : null;
                    if (splitBody) method.Declaration["body"] = new JsonArray();
                    var methodMapping = new NullableRepresentationTypes(frame, methodFrame, types, isValue);
                    Rewrite(method.Declaration, methodMapping, methods, DeclarationMapping);
                    AppendParameters(method.Declaration, methodFrame);
                    if (methodFrame.NullableIndices.Count != 0)
                        method.Declaration[NullableRepresentationTypes.MethodFrameKey] = methodFrame.ToJson().ToJsonString();
                    if (splitBody)
                        NullableBodyDispatch.Build(owner.Declaration, source, method.Declaration, frame, methodFrame,
                            method.Body.Method, (helper, helperFrame) => {
                                Rewrite(helper, new NullableRepresentationTypes(frame, helperFrame, types, isValue), methods, DeclarationMapping);
                                AppendParameters(helper, helperFrame);
                                helper[NullableRepresentationTypes.MethodFrameKey] = helperFrame.ToJson().ToJsonString();
                            });
                }
                AppendParameters(owner.Declaration, frame);
                if (owner.CapturedOwner != null)
                {
                    owner.Declaration["outerTypeParamCount"] = ownerFrames[owner.CapturedOwner.Declaration].PhysicalArity;
                    owner.Declaration.Remove("outerTypeParamOffset");
                }
                if (frame.NullableIndices.Count != 0 || frame.PhysicalOrder.Where((slot, index) => slot != index).Any())
                    KotlinSupertypesRecord.Merge(owner.Declaration,
                        new JsonObject { [NullableRepresentationFrame.MetadataKey] = frame.ToJson() });
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Nullable owner {Text(owner.Declaration["name"]) ?? Text(owner.Declaration["fileClass"])}: {error.Message}", error);
            }
        }
    }

    static void RequireCovered(IEnumerable<int> indices, NullableRepresentationFrame frame, string declaration)
    {
        var missing = indices.Except(frame.NullableIndices).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Nullable frame materialization requires body specialization first: {declaration}, slots {string.Join(",", missing)}");
    }

    static void AppendParameters(JsonObject declaration, NullableRepresentationFrame frame)
    {
        if (frame.PhysicalArity == 0) return;
        var parameters = (JsonArray)declaration["typeParams"];
        foreach (var index in frame.NullableIndices) parameters.Add("$nullable" + index);
        declaration["typeParams"] = new JsonArray(frame.PhysicalOrder.Select(index => parameters[index]?.DeepClone()).ToArray());
    }

    static void PreserveEdges(JsonObject owner, NullableRepresentationTypes mapping)
    {
        var facts = new JsonObject();
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
            var selectedMapping = kind == null ? null : declarationMapping(obj);
            JsonArray closedArguments = null;
            // A closure's invoke return implements the delegate's return generic argument. It is not an
            // ordinary scalar Kotlin return (where open T? erases to object). Preserve the source fact before
            // the recursive walk maps the payload, then install the same representation as funcType.Ret.
            var closureReturn = kind == "newClosure" && obj["synthClass"] is JsonObject closure
                && !ClosureSynthesis.HasPreboundFrame(closure) && TypeJson.Read(closure["ret"]) is TypeNode returnType
                    ? mapping.Argument(returnType) : null;
            if (kind is "newSam" or "newClosure" && obj["synthClass"] is JsonObject synthetic
                && !ClosureSynthesis.HasPreboundFrame(synthetic)
                && obj["typeArgs"] is JsonArray captureArguments && synthetic["typeParams"] is JsonArray captureParameters)
            {
                // Raw payload types still belong to the lexical frame. Materializing T? inside that payload
                // creates a use of N(T), so the generated class must capture N(T) alongside the ordinary T.
                // The later closure binder consumes this explicit argument/parameter correspondence.
                var captures = captureArguments.Select(TypeJson.Read).ToArray();
                if (captures.Length != captureParameters.Count)
                    throw new InvalidOperationException("Synthetic capture arguments do not match their declarations");
                var nullableCaptures = captures.OfType<TypeNode.Tv>().Where(variable =>
                    (variable.Scope == "type" ? mapping.OwnerFrame : mapping.MethodFrame)?.NullableIndices.Contains(variable.I) == true)
                    .Distinct().ToArray();
                if (nullableCaptures.Length != 0)
                {
                    closedArguments = new JsonArray(captures.Select(mapping.Argument)
                        .Concat(nullableCaptures.Select(mapping.NullableArgument)).Select(TypeJson.Write).ToArray());
                    foreach (var variable in nullableCaptures)
                        captureParameters.Add("$nullableCapture" + variable.Scope + variable.I);
                }
            }
            if (kind != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                && methods.TryGetValue(id, out var frame) && frame.NullableIndices.Count != 0)
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
            var declarationKeys = kind == null ? new HashSet<string>() : obj.Select(p => p.Key)
                .Where(key => NullableRepresentationTypes.IsDeclarationFrameKey(key, kind, obj)).ToHashSet();
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (key is "attrs" or "overrides" || key == "typeArgs" && closedArguments != null) continue;
                if (key == "inheritedImplementation" && obj[key] is JsonObject implementation)
                {
                    RewriteDescriptor(obj, key, declarationMapping(implementation), mapping);
                    continue;
                }
                if (declarationKeys.Contains(key))
                {
                    RewriteDescriptor(obj, key, selectedMapping, mapping);
                    continue;
                }
                var childPosition = key switch {
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
            if (closureReturn != null) obj["synthClass"]["ret"] = TypeJson.Write(closureReturn);
        }
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
        var frame = key == "memberOwnerTypeParams" ? declaration?.OwnerFrame
            : key == "memberMethodTypeParams" ? declaration?.MethodFrame : null;
        if (frame != null && node[key] is JsonArray parameters)
        {
            foreach (var index in frame.NullableIndices) parameters.Add("$nullable" + index);
            node[key] = new JsonArray(frame.PhysicalOrder.Select(index => parameters[index]?.DeepClone()).ToArray());
        }
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var text) == true ? text : null;

    public static void SelfTest()
    {
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
        Apply(new[] { root }, _ => false);
        if ((int)inlineCall["ga"] != 2 || ((JsonArray)inlineCall["typeArgs"]).Count != 2
            || TypeJson.Read(inlineCall["typeArgs"][1]) != new TypeNode.Tv("method", 1))
            throw new InvalidOperationException("Inline substitution arity excludes the materialized companion frame");
        if (((JsonArray)rawSam["typeArgs"]).Count != 2
            || ((JsonArray)rawSam["synthClass"]["typeParams"]).Count != 2
            || TypeJson.Read(rawSam["typeArgs"][1]) != new TypeNode.Tv("method", 1)
            || TypeJson.Read(rawSam["synthClass"]["interfaces"][0]) !=
                new TypeNode.Fqn("Comparator", new TypeNode[] { new TypeNode.Tv("method", 1) }))
            throw new InvalidOperationException("Synthetic payload companion is absent from its capture correspondence");
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

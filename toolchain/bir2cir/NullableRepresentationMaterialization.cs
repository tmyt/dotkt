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
        // Check before editing any declaration. A caller that needs body specialization cannot use this entry
        // until that specialization has given its implementation an explicit frame.
        foreach (var owner in demands)
        {
            var ownerName = Text(owner.Declaration["name"]) ?? Text(owner.Declaration["fileClass"]);
            RequireCovered(owner.Body.Type, owner.Frame, ownerName + " (type)");
            foreach (var method in owner.Methods)
            {
                var methodName = ownerName + "." + Text(method.Declaration["name"]);
                RequireCovered(method.Body.Type, owner.Frame, methodName + " (type)");
                RequireCovered(method.Body.Method, method.Frame, methodName + " (method)");
            }
        }
        var types = references == null ? new Dictionary<string, NullableRepresentationFrame>(StringComparer.Ordinal)
            : new Dictionary<string, NullableRepresentationFrame>(references.NullableTypeFrames, StringComparer.Ordinal);
        foreach (var owner in demands.Where(d => Text(d.Declaration["kind"]) != null))
            types[Text(owner.Declaration["name"])] = owner.Frame;
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
            var ownerType = Text(use["k"]) == "new" ? use["type"] : use["ownerType"];
            return TypeJson.Read(ownerType) is TypeNode.Fqn owner && types.TryGetValue(owner.Name, out var frame)
                ? new NullableRepresentationTypes(frame, empty, types, isValue) : null;
        }
        foreach (var owner in demands)
        {
            var frame = owner.Frame;
            var mapping = new NullableRepresentationTypes(frame, empty, types, isValue);
            PreserveEdges(owner.Declaration, mapping);
            foreach (var key in owner.Declaration.Select(p => p.Key).ToArray())
                if (key is not ("types" or "methods" or "attrs" or "overrides"))
                {
                    if (TypeJson.Read(owner.Declaration[key]) is TypeNode type)
                        owner.Declaration[key] = TypeJson.Write(mapping.Slot(type));
                    else Rewrite(owner.Declaration[key], mapping, methods, DeclarationMapping);
                }
            foreach (var method in owner.Methods)
            {
                var methodFrame = method.Frame;
                var methodMapping = new NullableRepresentationTypes(frame, methodFrame, types, isValue);
                Rewrite(method.Declaration, methodMapping, methods, DeclarationMapping);
                AppendParameters(method.Declaration, methodFrame);
                if (methodFrame.NullableIndices.Count != 0)
                    method.Declaration[NullableRepresentationTypes.MethodFrameKey] = methodFrame.ToJson().ToJsonString();
            }
            AppendParameters(owner.Declaration, frame);
            if (frame.NullableIndices.Count != 0)
                KotlinSupertypesRecord.Merge(owner.Declaration,
                    new JsonObject { [NullableRepresentationFrame.MetadataKey] = frame.ToJson() });
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
        if (frame.NullableIndices.Count == 0) return;
        var parameters = (JsonArray)declaration["typeParams"];
        foreach (var index in frame.NullableIndices) parameters.Add("$nullable" + index);
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
            if (kind != null && Text(obj[DeclarationIdentityBinding.Key]) is string id
                && methods.TryGetValue(id, out var frame) && frame.NullableIndices.Count != 0)
            {
                var arguments = obj["typeArgs"] as JsonArray
                    ?? throw new InvalidOperationException("Nullable-frame call has no source type arguments");
                closedArguments = new JsonArray(mapping.CloseMethod(frame, arguments.Select(TypeJson.Read).ToArray())
                    .Select(TypeJson.Write).ToArray());
            }
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (key is "attrs" or "overrides" || key == "typeArgs" && closedArguments != null) continue;
                if (kind != null && NullableRepresentationTypes.IsDeclarationFrameKey(key, kind))
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
            foreach (var index in frame.NullableIndices) parameters.Add("$nullable" + index);
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
        genericCall["memberSignature"] = new JsonArray(root["methods"][0]["params"][0]["type"].DeepClone());
        genericCall["memberReturnType"] = root["methods"][0]["ret"].DeepClone();
        genericCall["argTypes"] = genericCall["memberSignature"].DeepClone();
        genericCall["ret"] = genericCall["memberReturnType"].DeepClone();
        Apply(new[] { root }, _ => false);
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
        {"fileClass":"FixedEntry","methods":[{"name":"entry","typeParams":["T"],
         "params":[],"ret":{"t":"fqn","name":"kotlin.Unit"},
         "body":[{"k":"newArraySized","elem":{"t":"nullable","of":{"t":"tv","scope":"method","i":0}}}]}]}
        """);
        var before = bodyOnly.ToJsonString();
        var deferred = false;
        try { Apply(new[] { bodyOnly }, _ => false); }
        catch (InvalidOperationException) { deferred = true; }
        if (!deferred || bodyOnly.ToJsonString() != before)
            throw new InvalidOperationException("Body-only specialization must precede signature-frame materialization");
        Console.WriteLine("[nullable frame materialization] self-test OK (declarations, calls, exact values, metadata)");
    }
}

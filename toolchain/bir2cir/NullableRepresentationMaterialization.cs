using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materializes a signature-demanded frame. Body-only specialization is a separate operation: it must not
// silently grow a published virtual slot. Not yet scheduled by Program's normal lowering pipeline.
static class NullableRepresentationMaterialization
{
    public static void Apply(IEnumerable<JsonNode> inputs, ValueTypeOracle isValue)
    {
        var roots = inputs.ToArray();
        var demands = NullableRepresentationDemand.Collect(roots);
        // Check before editing any declaration. A caller that needs body specialization cannot use this entry
        // until that specialization has given its implementation an explicit frame.
        foreach (var owner in demands)
        {
            RequireCovered(owner.Body.Type, owner.Frame);
            foreach (var method in owner.Methods)
            {
                RequireCovered(method.Body.Type, owner.Frame);
                RequireCovered(method.Body.Method, method.Frame);
            }
        }
        var types = demands.Where(d => Text(d.Declaration["kind"]) != null)
            .ToDictionary(d => Text(d.Declaration["name"]), d => d.Frame, StringComparer.Ordinal);
        var methods = demands.SelectMany(d => d.Methods)
            .Where(m => Text(m.Declaration[DeclarationIdentityBinding.Key]) != null)
            .ToDictionary(m => Text(m.Declaration[DeclarationIdentityBinding.Key]), m => m.Frame, StringComparer.Ordinal);
        foreach (var root in roots.OfType<JsonObject>()) NullableGenericErasure.PreserveSourceFacts(root, isValue);
        var empty = new NullableRepresentationFrame(0, Array.Empty<int>());
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
                    else Rewrite(owner.Declaration[key], mapping, methods);
                }
            foreach (var method in owner.Methods)
            {
                var methodFrame = method.Frame;
                var methodMapping = new NullableRepresentationTypes(frame, methodFrame, types, isValue);
                Rewrite(method.Declaration, methodMapping, methods);
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

    static void RequireCovered(IEnumerable<int> indices, NullableRepresentationFrame frame)
    {
        if (indices.Except(frame.NullableIndices).Any())
            throw new InvalidOperationException("Nullable frame materialization requires body specialization first");
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
        NullableGenericErasure.Pos position = NullableGenericErasure.Pos.Slot)
    {
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                if (TypeJson.Read(array[i]) is TypeNode type) array[i] = TypeJson.Write(mapping.Rewrite(type, position));
                else Rewrite(array[i], mapping, methods, position);
        }
        else if (node is JsonObject obj)
        {
            var kind = Text(obj["k"]);
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
                var childPosition = key switch {
                    "typeArgs" => NullableGenericErasure.Pos.Argument,
                    "elem" when NullableGenericErasure.IsArgumentElementKind(kind) => NullableGenericErasure.Pos.Argument,
                    "resolvedMemberParams" => NullableGenericErasure.Pos.Bound,
                    "argTypes" when ClrBoundNode.IsAny(kind) => NullableGenericErasure.Pos.Bound,
                    _ => position,
                };
                if (TypeJson.Read(obj[key]) is TypeNode type) obj[key] = TypeJson.Write(mapping.Rewrite(type, childPosition));
                else Rewrite(obj[key], mapping, methods, childPosition);
            }
            if (closedArguments != null) obj["typeArgs"] = closedArguments;
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
        Apply(new[] { root }, _ => false);
        var method = root["methods"][0];
        var store = root["types"][1];
        if (((JsonArray)method["typeParams"]).Count != 2
            || TypeJson.Read(method["ret"]) != new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Tv("method", 1) })
            || ((JsonArray)root["methods"][1]["body"][0]["typeArgs"]).Count != 2
            || ((JsonArray)root["types"][0]["typeParams"]).Count != 1
            || ((JsonArray)store["typeParams"]).Count != 2
            || ((JsonArray)root["types"][2]["base"]["args"]).Count != 2
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

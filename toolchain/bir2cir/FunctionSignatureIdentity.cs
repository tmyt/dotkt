using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Function value storage is uniform; declaration identity still distinguishes
// the pre-carrier signature. This fact describes a CLR signature, never a value.
static class FunctionSignatureIdentity
{
    internal const string Key = "functionSignatureType";
    internal const string CallKey = "functionCallSignature";
    internal const string PhysicalKey = "functionPhysicalSignatureMarker";
    internal const string OutputName = "000-dotkt-parameter-signatures.cir.json";
    static readonly HashSet<string> Markers = new(StringComparer.Ordinal);

    public static void Capture(JsonNode root)
    {
        void Visit(JsonNode node)
        {
            if (node is not JsonObject owner) return;
            var ownerName = (owner["name"] ?? owner["fileClass"])?.GetValue<string>() ?? "";
            foreach (var key in new[] { "methods", "ctors" })
            foreach (var declaration in (owner[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            foreach (var parameter in (declaration["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (parameter[Key] == null && (TypeJson.Read(parameter["type"]) is TypeNode.Tv
                    || FunctionValueRepresentation.ContainsOrdinaryFunction(parameter["type"])))
                    parameter[Key] = parameter["type"].DeepClone();
                if (parameter[PhysicalKey] == null && TypeJson.Read(parameter[Key]) is TypeNode.Tv variable)
                {
                    var marker = "dotkt$ParameterSignature$" + Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(ownerName + "|" + TypeNode.ToJson(variable))));
                    parameter[PhysicalKey] = marker;
                    Markers.Add(marker);
                }
            }
            foreach (var type in owner["types"] as JsonArray ?? new JsonArray()) Visit(type);
        }
        Visit(root);
    }

    internal static JsonNode SignatureType(JsonObject parameter) => parameter[Key] is JsonNode discriminator
        ? TypeJson.Write(SignatureType(parameter, TypeJson.Read(parameter["type"])))
        : parameter["type"]?.DeepClone()
            ?? throw new InvalidOperationException("A physical declaration parameter has no type");

    // A variable remains a source selector; its physical discriminator is a
    // context-free nominal declaration. CLR interface dispatch can resolve custom
    // modifier tokens outside the method's generic frame, so !n/!!n is not itself
    // a usable modifier. The marker does not describe storage or a runtime cast.
    internal static TypeNode SignatureType(JsonObject parameter, TypeNode physical,
        Func<TypeNode, TypeNode> close = null)
    {
        if (parameter[Key] == null) return physical;
        var discriminator = parameter[PhysicalKey] is JsonValue marker
            ? new TypeNode.Fqn(marker.GetValue<string>()) : TypeJson.Read(parameter[Key]);
        if (parameter[PhysicalKey] == null && close != null) discriminator = close(discriminator);
        return new TypeNode.Mod(false, discriminator, physical);
    }

    internal static JsonObject SynthDefsFile(IEnumerable<JsonNode> roots)
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (TypeJson.Read(obj) is TypeNode.Mod { M: TypeNode.Fqn { Args: null } marker }
                    && Markers.Contains(marker.Name)) used.Add(marker.Name);
                foreach (var child in obj.Select(pair => pair.Value)) Visit(child);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        foreach (var root in roots) Visit(root);
        return new JsonObject {
            ["fileClass"] = "", ["hasMain"] = false,
            ["methods"] = new JsonArray(), ["fields"] = new JsonArray(),
            ["types"] = new JsonArray(used.Select(name => (JsonNode)new JsonObject {
                ["name"] = name, ["kind"] = "class", ["generated"] = true,
                ["abstract"] = true, ["vis"] = "public", ["base"] = null,
                ["typeParams"] = new JsonArray(), ["interfaces"] = new JsonArray(),
                ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(), ["methods"] = new JsonArray(),
            }).ToArray()),
        };
    }

    internal static void SelfTest()
    {
        var physical = new TypeNode.Fqn("object");
        foreach (var scope in new[] { "type", "method" })
        foreach (var index in new[] { 0, 2 })
        {
            var source = new TypeNode.Tv(scope, index);
            var parameter = new JsonObject { ["type"] = TypeJson.Write(source) };
            var root = new JsonObject { ["fileClass"] = "SignatureProbe", ["methods"] = new JsonArray(new JsonObject {
                ["params"] = new JsonArray(parameter),
            }) };
            Capture(root);
            parameter["type"] = TypeJson.Write(physical);
            var signature = TypeJson.Read(SignatureType(parameter));
            if (TypeJson.Read(parameter[Key]) != source || signature is not TypeNode.Mod
                { Req: false, M: TypeNode.Fqn { Args: null }, Of: TypeNode.Fqn { Name: "object" } })
                throw new InvalidOperationException("Generic source selector lost its nominal physical discriminator");
            var selectedMarker = parameter[PhysicalKey].GetValue<string>();
            Capture(root);
            if (parameter[PhysicalKey].GetValue<string>() != selectedMarker)
                throw new InvalidOperationException("Repeated capture changed a physical discriminator");
            Complete(root);
            Complete(root);
            var definitions = SynthDefsFile(new[] { root });
            if (parameter[Key] != null || parameter[PhysicalKey] != null
                || TypeJson.Read(parameter["type"]) != signature
                || definitions["types"].AsArray().Count != 1
                || definitions["types"][0]["name"].GetValue<string>() != selectedMarker)
                throw new InvalidOperationException("Completing generic signature identity lost its explicit marker declaration");
        }
        var marker = new TypeNode.Fqn("NominalSignatureMarker");
        if (SignatureType(new JsonObject { [Key] = TypeJson.Write(marker) }, physical)
            != new TypeNode.Mod(false, marker, physical))
            throw new InvalidOperationException("A nominal declaration discriminator was dropped");
        Console.WriteLine("[function signature identity] self-test OK (source variable selectors, nominal physical modifiers)");
    }

    internal static JsonArray Signature(JsonArray parameters) =>
        new(parameters.OfType<JsonObject>().Select(SignatureType).ToArray());

    public static void Complete(JsonNode root)
    {
        void RemoveCallFacts(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove(CallKey);
                obj.Remove(ConstructorSignatureIdentity.CallKey);
                obj.Remove(ConstructorSignatureIdentity.SourceCallKey);
                obj.Remove(ConstructorSignatureIdentity.ParameterKey);
                obj.Remove(ConstructorSignatureIdentity.PhysicalKey);
                foreach (var child in obj.Select(pair => pair.Value).ToList()) RemoveCallFacts(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) RemoveCallFacts(child);
        }
        RemoveCallFacts(root);
        foreach (var parameters in DeclarationParameters(root))
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (parameter[Key] != null)
                {
                    parameter["type"] = SignatureType(parameter);
                    parameter.Remove(Key);
                    parameter.Remove(PhysicalKey);
                }
    }

    static IEnumerable<JsonArray> DeclarationParameters(JsonNode node)
    {
        if (node is not JsonObject owner) yield break;
        foreach (var key in new[] { "methods", "ctors" })
            if (owner[key] is JsonArray declarations)
                foreach (var declaration in declarations.OfType<JsonObject>())
                    if (declaration["params"] is JsonArray parameters) yield return parameters;
        if (owner["types"] is JsonArray types)
            foreach (var type in types)
                foreach (var parameters in DeclarationParameters(type)) yield return parameters;
    }
}

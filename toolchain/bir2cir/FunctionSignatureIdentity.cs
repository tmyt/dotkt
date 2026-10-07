using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Function value storage is uniform; declaration identity still distinguishes
// the pre-carrier signature. This fact describes a CLR signature, never a value.
static class FunctionSignatureIdentity
{
    internal const string Key = "functionSignatureType";
    internal const string CallKey = "functionCallSignature";

    public static void Capture(JsonNode root)
    {
        foreach (var parameters in DeclarationParameters(root))
            foreach (var parameter in parameters.OfType<JsonObject>())
                if (parameter[Key] == null && (TypeJson.Read(parameter["type"]) is TypeNode.Tv
                    || FunctionValueRepresentation.ContainsOrdinaryFunction(parameter["type"])))
                    parameter[Key] = parameter["type"].DeepClone();
    }

    internal static JsonNode SignatureType(JsonObject parameter) => parameter[Key] is JsonNode discriminator
        ? TypeJson.Write(new TypeNode.Mod(false, TypeJson.Read(discriminator), TypeJson.Read(parameter["type"])))
        : parameter["type"]?.DeepClone()
            ?? throw new InvalidOperationException("A physical declaration parameter has no type");

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

using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Declaration identity precedes existential value projection. Keep it opaque until
// binding. Physical discriminators are nominal identities, not value types that
// could themselves collapse under star projection, aliasing, or nullable erasure.
static class ConstructorSignatureIdentity
{
    internal const string ParameterKey = "_constructorDeclarationType";
    internal const string BeforeValueErasureKey = "constructorTypeBeforeValueErasure";
    internal const string CallKey = "_constructorDeclarationSignature";
    internal const string SourceCallKey = "_constructorSourceSignature";
    internal const string PhysicalKey = "_constructorPhysicalDiscriminator";

    public static void Capture(JsonNode root)
    {
        Walk(root, obj =>
        {
            if (obj["ctors"] is JsonArray constructors)
                foreach (var ctor in constructors.OfType<JsonObject>())
                    if (ctor["params"] is JsonArray parameters)
                        foreach (var parameter in parameters.OfType<JsonObject>())
                        {
                            parameter[ParameterKey] = (parameter[FunctionSignatureIdentity.Key]
                                ?? parameter[BeforeValueErasureKey]
                                ?? parameter["type"]).ToJsonString();
                            parameter.Remove(BeforeValueErasureKey);
                        }
        });
        CaptureCalls(root);
    }

    public static void CaptureCalls(JsonNode root) => CaptureCalls(root, CallKey);

    public static void CaptureSourceCalls(JsonNode root) => CaptureCalls(root, SourceCallKey);

    static void CaptureCalls(JsonNode root, string key)
    {
        Walk(root, obj =>
        {
            var signature = obj["k"]?.GetValue<string>() is "new" or "newClr"
                ? obj["memberSignature"] : obj["delegationSig"];
            if (signature is JsonArray) obj[key] ??= signature.ToJsonString();
        });
    }

    public static JsonNode DeclarationType(JsonNode parameter) => parameter?[ParameterKey] is JsonValue encoded
        ? JsonNode.Parse(encoded.GetValue<string>()) : null;

    public static JsonArray DeclarationSignature(JsonObject call) => call[CallKey] is JsonValue encoded
        ? JsonNode.Parse(encoded.GetValue<string>()).AsArray() : null;

    public static JsonArray SourceSignature(JsonObject call) => call[SourceCallKey] is JsonValue encoded
        ? JsonNode.Parse(encoded.GetValue<string>()).AsArray() : null;

    public static void Materialize(JsonNode root)
    {
        if (root is not JsonObject file) return;
        var markers = new Dictionary<string, JsonObject>();
        Walk(root, obj =>
        {
            if (obj["ctors"] is not JsonArray constructors) return;
            // Constructor overloads have a nominal, whole-signature identity below.
            // A bare generic-variable modifier is redundant here and is not a
            // C#-consumable modifier type. Keep the captured declaration frame,
            // but do not also encode method-style variable discriminators.
            foreach (var ctor in constructors.OfType<JsonObject>())
                if (ctor["params"] is JsonArray ctorParameters)
                    foreach (var parameter in ctorParameters.OfType<JsonObject>())
                        if (TypeJson.Read(parameter[FunctionSignatureIdentity.Key]) is TypeNode.Tv)
                            parameter.Remove(FunctionSignatureIdentity.Key);
            // Arity survives every value-type projection. Reserve identity for the
            // whole overload family before later passes expose its final collisions.
            var groups = constructors.OfType<JsonObject>()
                .Where(ctor => ctor["params"] is JsonArray { Count: > 0 } parameters
                    && parameters.OfType<JsonObject>().All(parameter => DeclarationType(parameter) != null))
                .GroupBy(ctor => ((JsonArray)ctor["params"]).Count);
            foreach (var group in groups.Where(group => group.Count() > 1))
                foreach (var ctor in group)
                {
                    var parameters = (JsonArray)ctor["params"];
                    var signature = new JsonArray(parameters.OfType<JsonObject>()
                        .Select(DeclarationType).ToArray()).ToJsonString();
                    var identity = obj["name"].GetValue<string>() + "|" + signature;
                    var marker = "dotkt$ConstructorSignature$" + System.Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    markers.TryAdd(marker, new JsonObject
                    {
                        ["name"] = marker, ["kind"] = "class", ["generated"] = true,
                        ["abstract"] = true, ["vis"] = "public", ["base"] = null,
                        ["typeParams"] = new JsonArray(), ["interfaces"] = new JsonArray(),
                        ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(), ["methods"] = new JsonArray(),
                    });
                    // One slot identifies the entire declaration; other argument slots
                    // retain their own physical representation and modifiers.
                    var first = (JsonObject)parameters[0];
                    first[FunctionSignatureIdentity.Key] = TypeJson.Write(new TypeNode.Fqn(marker));
                    first[PhysicalKey] = true;
                }
        });
        if (markers.Count == 0) return;
        if (file["types"] is not JsonArray types) file["types"] = types = new JsonArray();
        foreach (var marker in markers.Values) types.Add(marker);
    }

    static void Walk(JsonNode node, System.Action<JsonObject> visit)
    {
        if (node is JsonObject obj)
        {
            visit(obj);
            foreach (var child in obj.Select(pair => pair.Value).ToList()) Walk(child, visit);
        }
        else if (node is JsonArray array)
            foreach (var child in array) Walk(child, visit);
    }
}

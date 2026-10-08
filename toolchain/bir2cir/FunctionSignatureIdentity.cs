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
    const string ConstraintKey = "_functionSourceConstraintIdentity";
    internal const string OutputName = "000-dotkt-parameter-signatures.cir.json";
    static readonly HashSet<string> Markers = new(StringComparer.Ordinal);

    public static void CaptureSourceConstraints(JsonNode root)
    {
        void Visit(JsonNode node)
        {
            if (node is not JsonObject owner) return;
            foreach (var key in new[] { "methods", "ctors" })
            foreach (var declaration in (owner[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            foreach (var parameter in (declaration["params"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (parameter[ConstraintKey] == null && TypeJson.Read(parameter["type"]) is TypeNode.Tv variable)
                    parameter[ConstraintKey] = ConstraintIdentity(owner, declaration, variable);
            foreach (var type in owner["types"] as JsonArray ?? new JsonArray()) Visit(type);
        }
        Visit(root);
    }

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
                        SHA256.HashData(Encoding.UTF8.GetBytes(ownerName + "|" + TypeNode.ToJson(variable)
                            + (parameter[ConstraintKey]?.GetValue<string>() ?? ""))));
                    parameter[PhysicalKey] = marker;
                    Markers.Add(marker);
                }
            }
            foreach (var type in owner["types"] as JsonArray ?? new JsonArray()) Visit(type);
        }
        Visit(root);
    }

    // A method variable's index is local to its declaration: two !!1 parameters
    // in the same facade need not have the same Kotlin bounds. Retain the source
    // bound graph before representation lowering erases it. Follow only variables
    // mentioned by those bounds; unrelated method parameters do not describe this
    // parameter. Names and bound declaration order are not semantic selectors.
    static string ConstraintIdentity(JsonObject owner, JsonObject declaration, TypeNode.Tv variable)
    {
        var selected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var hasBounds = false;
        void CaptureVariable(TypeNode.Tv current)
        {
            var key = current.Scope + ":" + current.I;
            if (selected.ContainsKey(key)) return;
            var frame = (current.Scope == "type" ? owner : declaration)["typeParams"] as JsonArray;
            var parameter = frame != null && current.I < frame.Count
                ? frame[current.I] as JsonObject : null;
            var recorded = current.Scope == "type"
                ? KotlinSupertypesRecord.ReadSourceBounds(owner)
                : declaration[NullableGenericErasure.MethodTypeParameterBoundsPre] is JsonValue encoded
                    ? JsonNode.Parse(encoded.GetValue<string>())?["bounds"] as JsonObject : null;
            // Earlier representation passes may have moved a complete source
            // constraint list into its authoritative hand-off. An existing list
            // owns the truth, including an explicitly empty one; otherwise this
            // still-source frame has not had that parameter's bounds moved.
            var constraints = recorded?[current.I.ToString()] as JsonArray
                ?? parameter?["constraints"] as JsonArray;
            hasBounds |= constraints is { Count: > 0 };
            var bounds = (constraints ?? new JsonArray())
                .Select(bound => TypeJson.Write(TypeJson.Read(bound)))
                .OrderBy(bound => bound.ToJsonString(), StringComparer.Ordinal).ToArray();
            selected.Add(key, new JsonArray(bounds).ToJsonString());
            foreach (var bound in bounds) VisitType(bound);
        }
        void VisitType(JsonNode node)
        {
            if (node is JsonObject type)
            {
                if (TypeJson.Read(type) is TypeNode.Tv mentioned) CaptureVariable(mentioned);
                foreach (var child in type.Select(pair => pair.Value)) VisitType(child);
            }
            else if (node is JsonArray array)
                foreach (var child in array) VisitType(child);
        }
        CaptureVariable(variable);
        return hasBounds
            ? "|bounds:" + string.Join(";", selected.Select(pair => pair.Key + "=" + pair.Value)) : "";
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
        SelfTestConstraintIdentity();
        SelfTestComparableParity();
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
            var copied = root.DeepClone().AsObject();
            copied["fileClass"] = "SignatureProbePhysicalWrapper";
            Capture(copied);
            if (copied["methods"][0]["params"][0][PhysicalKey].GetValue<string>() != selectedMarker)
                throw new InvalidOperationException("A physical declaration copy acquired a different source discriminator");
            Complete(root);
            Complete(copied);
            Complete(root);
            var definitions = SynthDefsFile(new[] { root, copied });
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

    static void SelfTestConstraintIdentity()
    {
        JsonObject Root(string ownerBound, string sink, bool reverse = false)
        {
            JsonObject Parameter(string name, params TypeNode[] bounds) => new() {
                ["name"] = name, ["constraints"] = new JsonArray(bounds.Select(TypeJson.Write).ToArray()),
            };
            var targetBounds = new TypeNode[] {
                new TypeNode.Fqn(sink, new[] { new TypeNode.Tv("method", 0) }), new TypeNode.Fqn("AdditionalBound"),
            };
            return new JsonObject {
                ["fileClass"] = "ConstraintSignatureProbe",
                ["typeParams"] = new JsonArray(Parameter("A", new TypeNode.Fqn(ownerBound))),
                ["methods"] = new JsonArray(new JsonObject {
                    ["typeParams"] = new JsonArray(
                        Parameter("E", new TypeNode.Tv("type", 0)),
                        Parameter("C", reverse ? targetBounds.Reverse().ToArray() : targetBounds),
                        Parameter("Unrelated", new TypeNode.Fqn("UnrelatedBound"))),
                    ["params"] = new JsonArray(new JsonObject {
                        ["type"] = TypeJson.Write(new TypeNode.Tv("method", 1)),
                    }),
                }),
            };
        }
        string Marker(JsonObject root)
        {
            CaptureSourceConstraints(root);
            Capture(root);
            return root["methods"][0]["params"][0][PhysicalKey].GetValue<string>();
        }
        var original = Root("FirstBound", "Sink");
        var expected = Marker(original);
        if (Marker(Root("FirstBound", "OtherSink")) == expected
            || Marker(Root("SecondBound", "Sink")) == expected
            || Marker(Root("FirstBound", "Sink", reverse: true)) != expected)
            throw new InvalidOperationException("Parameter signature lost its reachable source constraint graph");
        var renamed = Root("FirstBound", "Sink");
        renamed["methods"][0]["typeParams"][0]["name"] = "Renamed";
        renamed["methods"][0]["typeParams"][2]["constraints"] = new JsonArray(TypeJson.Write(new TypeNode.Fqn("OtherUnused")));
        if (Marker(renamed) != expected)
            throw new InvalidOperationException("Names or unrelated constraints changed a parameter signature");
        var projected = Root("FirstBound", "Sink");
        CaptureSourceConstraints(projected);
        projected["methods"][0]["typeParams"][1]["constraints"] = new JsonArray(TypeJson.Write(new TypeNode.Fqn("PhysicalBound")));
        if (Marker(projected) != expected)
            throw new InvalidOperationException("Physical lowering replaced a captured source constraint identity");
        var recursive = Root("FirstBound", "Sink");
        recursive["typeParams"][0]["constraints"] = new JsonArray(TypeJson.Write(
            new TypeNode.Fqn("RecursiveBound", new[] { new TypeNode.Tv("type", 0) })));
        var recursiveMarker = Marker(recursive);
        if (recursiveMarker != Marker(recursive.DeepClone().AsObject()))
            throw new InvalidOperationException("Recursive source bounds changed a copied signature identity");
        Complete(original);
        if (original["methods"][0]["params"][0].AsObject().ContainsKey(ConstraintKey))
            throw new InvalidOperationException("Source constraint routing facts leaked into CIR");
    }

    static void SelfTestComparableParity()
    {
        var direct = JsonNode.Parse("""
        {"fileClass":"ComparableParity","methods":[{"name":"pick","typeParams":[{"name":"T","constraints":[
          {"t":"fqn","name":"kotlin.Comparable","args":[{"t":"tv","scope":"method","i":0}]}]}],
          "params":[{"name":"value","type":{"t":"tv","scope":"method","i":0}}],
          "ret":{"t":"tv","scope":"method","i":0},"body":[]}]}
        """).AsObject();
        var transitive = direct.DeepClone().AsObject();
        transitive["methods"][0]["typeParams"].AsArray().Add(new JsonObject {
            ["name"] = "U", ["constraints"] = new JsonArray(TypeJson.Write(new TypeNode.Tv("method", 0))),
        });
        transitive["methods"][0]["params"][0]["type"] = TypeJson.Write(new TypeNode.Tv("method", 1));
        var owner = JsonNode.Parse("""
        {"fileClass":"ComparableOwnerParity","methods":[],"types":[{"kind":"class","name":"ComparableOwner",
          "typeParams":[{"name":"T","constraints":[{"t":"fqn","name":"kotlin.Comparable","args":[
          {"t":"tv","scope":"type","i":0}]}]}],"methods":[{"name":"pick","params":[{"name":"value",
          "type":{"t":"tv","scope":"type","i":0}}],"ret":{"t":"tv","scope":"type","i":0},"body":[]}]}]}
        """).AsObject();
        string Marker(JsonObject source, bool reference)
        {
            var root = source.DeepClone().AsObject();
            ComparableRepresentationLowering.Apply(new[] { root }, reference);
            CaptureSourceConstraints(root);
            Capture(root);
            var declarationOwner = root["types"] is JsonArray { Count: > 0 } types ? types[0] : root;
            return declarationOwner["methods"][0]["params"][0][PhysicalKey].GetValue<string>();
        }
        foreach (var root in new[] { direct, transitive, owner })
            if (Marker(root, reference: true) != Marker(root, reference: false))
                throw new InvalidOperationException("Comparable source bounds gave reference/runtime signature mismatch");
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
            {
                parameter.Remove(ConstraintKey);
                if (parameter[Key] != null)
                {
                    parameter["type"] = SignatureType(parameter);
                    parameter.Remove(Key);
                    parameter.Remove(PhysicalKey);
                }
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

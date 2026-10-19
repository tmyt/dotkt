using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static partial class FBoundStarProjectionErasure
{
    // A managed reference cannot pass through the object-valued existential ABI. Retain exactly the owner
    // parameters required by its pointee (and their constraints) in a separate, invariant interface contract.
    // Other owner arguments remain erased: an unchecked view of an unrelated argument must not change this call.
    sealed record ReferenceContract(TypeNode.Fqn Owner, JsonObject Slot, string Invoke);

    static JsonArray ReferenceCallShape(JsonObject call) => call["shapeTypes"] is JsonArray shape
        && shape.Any(type => SignatureValueTypes.Of(TypeJson.Read(type)) is TypeNode.ByRef) ? shape : null;

    static ReferenceContract CreateReferenceContract(Owner owner, JsonObject method, JsonObject slot,
        IReadOnlyDictionary<string, Owner> owners, ReferenceMetadataIndex refs)
    {
        var originals = method["params"] as JsonArray ?? new JsonArray();
        var dependencies = new HashSet<int>();
        foreach (var parameter in originals.OfType<JsonObject>())
            if (SignatureValueTypes.Of(TypeJson.Read(parameter["type"])) is TypeNode.ByRef reference)
                VisitVariables(TypeJson.Write(reference.Of), dependencies);
        if (dependencies.Count == 0) return null;

        var ownerParameters = owner.Def["typeParams"] as JsonArray ?? new JsonArray();
        int previous;
        do
        {
            previous = dependencies.Count;
            foreach (var index in dependencies.ToArray())
                if (index < ownerParameters.Count && ownerParameters[index] is JsonObject parameter)
                    VisitVariables(parameter["constraints"], dependencies);
        } while (previous != dependencies.Count);
        var indices = dependencies.OrderBy(index => index).ToArray();
        if (indices.Any(index => index < 0 || index >= ownerParameters.Count))
            throw new InvalidOperationException("Managed-reference contract has no exact owner parameter frame");
        var mapping = indices.Select((index, position) => (index, position))
            .ToDictionary(pair => pair.index, pair => pair.position);
        var parameters = new JsonArray(indices.Select(index => {
            var parameter = ownerParameters[index].DeepClone();
            ReindexVariables(parameter, mapping);
            if (parameter is JsonObject declaration) declaration.Remove("variance");
            return parameter;
        }).ToArray());
        RestoreReferenceParameters(originals, (JsonArray)slot["params"]);
        foreach (var parameter in ((JsonArray)slot["params"]).OfType<JsonObject>())
        {
            ReindexVariables(parameter["type"], mapping);
            ReindexVariables(parameter[FunctionSignatureIdentity.Key], mapping);
        }
        ReindexVariables(slot["typeParams"], mapping);
        var name = owner.ErasedName + "$ref$" + Convert.ToHexString(
            System.Text.Encoding.UTF8.GetBytes(Str(slot["name"])));
        var rootTypes = (JsonArray)owner.Root["types"];
        var allocated = name;
        for (var suffix = 1; !_usedCarrierNames.Add(allocated); suffix++) allocated = name + "$" + suffix;
        var definition = new JsonObject
        {
            ["name"] = allocated, ["kind"] = "interface", ["generated"] = true,
            ["typeParams"] = parameters,
            ["managedReferenceContract"] = new JsonObject {
                ["owner"] = owner.Name,
                ["declaration"] = method[DeclarationIdentityBinding.Key]?.DeepClone(),
                ["invoke"] = "$ref$invoke",
                ["slots"] = new JsonArray(indices.Select(index => (JsonNode)JsonValue.Create(index)).ToArray()),
            },
            ["interfaces"] = new JsonArray(), ["fields"] = new JsonArray(), ["ctors"] = new JsonArray(),
            ["methods"] = new JsonArray(slot), ["properties"] = new JsonArray(), ["attrs"] = new JsonArray(),
        };
        definition["methods"].AsArray().Add(ReferenceInvoker(allocated, indices.Length, slot));
        if (PublishedApiTypeVisibilityLowering.ResolveVisibility(owner.Def) is { } visibility)
            definition["vis"] = visibility;
        rootTypes.Add(definition);
        var implemented = new TypeNode.Fqn(allocated,
            indices.Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        if (owner.Def["interfaces"] is not JsonArray interfaces)
            owner.Def["interfaces"] = interfaces = new JsonArray();
        interfaces.Add(TypeJson.Write(implemented));
        return new ReferenceContract(implemented, slot, "$ref$invoke");
    }

    static JsonObject ReferenceInvoker(string ownerName, int arity, JsonObject slot)
    {
        var owner = new TypeNode.Fqn(ownerName,
            Enumerable.Range(0, arity).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray());
        var parameters = new JsonArray(new JsonObject {
            ["name"] = "$receiver", ["type"] = TypeJson.Fqn("System.Object"),
        });
        var arguments = new JsonArray();
        foreach (var parameter in ((JsonArray)slot["params"]).OfType<JsonObject>())
        {
            var name = "p" + arguments.Count;
            parameters.Add(new JsonObject { ["name"] = name, ["type"] = parameter["type"].DeepClone() });
            arguments.Add(new JsonObject { ["k"] = "local", ["name"] = name });
        }
        var call = new JsonObject {
            ["k"] = "callInstance", ["ownerType"] = TypeJson.Write(owner), ["virtual"] = true,
            ["recv"] = new JsonObject {
                ["k"] = "cast", ["type"] = TypeJson.Write(owner), ["_exactBridgeCast"] = true,
                ["e"] = new JsonObject { ["k"] = "local", ["name"] = "$receiver" },
            },
            ["method"] = slot["name"].DeepClone(),
            ["sig"] = FunctionSignatureIdentity.Signature((JsonArray)slot["params"]),
            ["ret"] = slot["ret"].DeepClone(), ["args"] = arguments,
            [ExactBridgeOwnerCallKey] = true, ["clrOwnerResolved"] = true,
        };
        if (slot["typeParams"] is JsonArray { Count: > 0 } typeParameters)
            call["typeArgs"] = new JsonArray(Enumerable.Range(0, typeParameters.Count)
                .Select(index => TypeJson.Write(new TypeNode.Tv("method", index))).ToArray());
        return new JsonObject {
            ["name"] = "$ref$invoke", ["static"] = true, ["vis"] = "public", ["generated"] = true,
            ["override"] = false, ["virtual"] = false, ["abstract"] = false, ["objectOverride"] = false,
            ["params"] = parameters, ["ret"] = slot["ret"].DeepClone(),
            ["typeParams"] = slot["typeParams"]?.DeepClone() ?? new JsonArray(),
            ["body"] = new JsonArray(IsVoidResult(TypeJson.Read(slot["ret"]))
                ? new JsonObject { ["k"] = "exprStmt", ["expr"] = call }
                : new JsonObject { ["k"] = "return", ["value"] = call }),
        };
    }

    static void RestoreReferenceParameters(JsonArray original, JsonArray projected)
    {
        for (var index = 0; index < original.Count; index++)
            if (original[index] is JsonObject parameter
                && SignatureValueTypes.Of(TypeJson.Read(parameter["type"])) is TypeNode.ByRef)
            {
                projected[index]["type"] = parameter["type"].DeepClone();
                if (parameter[FunctionSignatureIdentity.Key] != null)
                    projected[index][FunctionSignatureIdentity.Key] = parameter[FunctionSignatureIdentity.Key].DeepClone();
            }
    }

    static void VisitVariables(JsonNode node, HashSet<int> result)
    {
        if (node is JsonObject obj)
        {
            if (TypeJson.Read(obj) is TypeNode.Tv { Scope: "type" } variable) result.Add(variable.I);
            else foreach (var value in obj.Select(pair => pair.Value)) VisitVariables(value, result);
        }
        else if (node is JsonArray array)
            foreach (var value in array) VisitVariables(value, result);
    }

    static void ReindexVariables(JsonNode node, IReadOnlyDictionary<int, int> mapping)
    {
        if (node is JsonObject obj)
        {
            if (TypeJson.Read(obj) is TypeNode.Tv { Scope: "type" } variable)
                obj["i"] = mapping.TryGetValue(variable.I, out var position) ? position
                    : throw new InvalidOperationException("Managed-reference contract dropped a required owner parameter");
            else foreach (var value in obj.Select(pair => pair.Value).ToList()) ReindexVariables(value, mapping);
        }
        else if (node is JsonArray array)
            foreach (var value in array) ReindexVariables(value, mapping);
    }

    static void BindReferenceContract(JsonObject call, ReferenceContract contract,
        IReadOnlyList<TypeNode> arguments, bool propertyCall, string sourcePropertyName, string accessorKind,
        TypeNode.Fqn constructedOwner = null)
    {
        if (arguments == null) throw new InvalidOperationException("Managed-reference call has no selected owner frame");
        var owner = constructedOwner ?? (TypeNode.Fqn)SubstituteDeclarationTypeArguments(
            contract.Owner, arguments, Array.Empty<TypeNode>());
        if (Str(call["k"]) == "callInstance")
        {
            // Check the receiver only after Kotlin has evaluated every argument. The static invoker receives the
            // real managed references directly; no pointer is boxed, reflected, copied or kept beyond this call.
            var values = new JsonArray(call["recv"].DeepClone());
            foreach (var argument in (JsonArray)call["args"]) values.Add(argument.DeepClone());
            var signature = new JsonArray(TypeJson.Fqn("System.Object"));
            foreach (var parameter in (JsonArray)contract.Slot["params"])
                signature.Add(parameter["type"].DeepClone());
            call["k"] = "callStatic";
            call["owner"] = TypeJson.Write(owner);
            call["calleeOwner"] = TypeJson.Write(owner);
            call["method"] = contract.Invoke;
            call["args"] = values;
            call["sig"] = signature;
            call["calleeParams"] = signature.DeepClone();
            foreach (var key in new[] { "recv", "ownerType", "virtual", "super", "memberRef",
                         "memberOwnerTypeParams", "memberVisibility", "shapeTypes", DeclarationIdentityBinding.Key }) call.Remove(key);
            AlignCallResult(call, SubstituteMethodTypeArguments(TypeJson.Read(contract.Slot["ret"]),
                (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray() ?? Array.Empty<TypeNode>()),
                protectExactCast: true);
            return;
        }
        call["ownerType"] = TypeJson.Write(owner);
        call["recv"] = new JsonObject {
            ["k"] = "cast", ["type"] = TypeJson.Write(owner), ["e"] = call["recv"].DeepClone(),
            ["_exactBridgeCast"] = true,
        };
        call["method"] = contract.Slot["name"].DeepClone();
        call["sig"] = FunctionSignatureIdentity.Signature((JsonArray)contract.Slot["params"]);
        call["calleeParams"] = call["sig"].DeepClone();
        call["virtual"] = true;
        call[ExactBridgeOwnerCallKey] = true;
        call.Remove("memberOwnerTypeParams");
        call.Remove("memberVisibility");
        MarkPhysicalPropertyCall(call, propertyCall, sourcePropertyName, accessorKind,
            Str(contract.Slot[DeclarationIdentityBinding.Key]));
        if (Str(call["k"]) == "newBoundDelegate")
        {
            call["calleeOwner"] = TypeJson.Write(owner);
            call["ret"] = contract.Slot["ret"].DeepClone();
        }
        else
            AlignCallResult(call, SubstituteMethodTypeArguments(TypeJson.Read(contract.Slot["ret"]),
                (call["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray() ?? Array.Empty<TypeNode>()),
                protectExactCast: true);
    }
}

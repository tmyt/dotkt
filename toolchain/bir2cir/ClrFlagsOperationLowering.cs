using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A dll2klib-synthesized CLR [Flags] member is a metadata-only Kotlin declaration. kotc carries the exact selected
// declaration's ClrFlagsOperation role; this pass consumes that source meaning and resolves the target enum's concrete
// CLR width. No fabricated MethodDef call survives. enumBits states that an explicitly converted integral expression
// inhabits the named enum slot, leaving ilemit only the one-to-one conv/bit/comparison emission.
static class ClrFlagsOperationLowering
{
    static int _binding;

    public static void Apply(JsonNode root, ReferenceMetadataIndex refs)
    {
        _binding = 0;
        Walk(root, refs);
        AssertConsumed(root);
    }

    static void Walk(JsonNode node, ReferenceMetadataIndex refs)
    {
        if (node is JsonObject obj)
        {
            foreach (var child in obj.ToList())
                if (child.Value != null) Walk(child.Value, refs);
            if (obj["clrFlagsOperation"] is JsonValue) Lower(obj, refs);
        }
        else if (node is JsonArray array)
            foreach (var child in array.ToList())
                if (child != null) Walk(child, refs);
    }

    static void Lower(JsonObject call, ReferenceMetadataIndex refs)
    {
        var role = Str(call["clrFlagsOperation"]);
        if (Str(call["k"]) != "callInstance" || role is null)
            throw new InvalidOperationException("bir2cir: ClrFlagsOperation must annotate an instance call");
        if (role is not ("or" or "and" or "xor" or "inv" or "contains"))
            throw new InvalidOperationException($"bir2cir: unknown ClrFlagsOperation role '{role}'");
        var owner = TypeJson.Read(call["ownerType"])
            ?? throw new InvalidOperationException("bir2cir: ClrFlagsOperation call carries no owner type");
        var representation = refs.ResolveFlagsEnum(owner)
            ?? throw new InvalidOperationException(
                $"bir2cir: ClrFlagsOperation owner '{TypeJson.OwnerName(call["ownerType"])}' is not an exact referenced CLR [Flags] enum");
        var args = call["args"] as JsonArray ?? new JsonArray();
        var argTypes = call["argTypes"] as JsonArray ?? new JsonArray();
        var unary = role == "inv";
        if (args.Count != (unary ? 0 : 1) || argTypes.Count != args.Count)
            throw new InvalidOperationException(
                $"bir2cir: ClrFlagsOperation '{role}' has an invalid argument vector");
        if (!unary && TypeJson.Read(argTypes[0]) != owner)
            throw new InvalidOperationException(
                $"bir2cir: ClrFlagsOperation '{role}' argument must have the receiver's exact enum type");
        var expectedReturn = role == "contains" ? new TypeNode.Fqn("kotlin.Boolean") : owner;
        if (TypeJson.Read(call["ret"]) != expectedReturn)
            throw new InvalidOperationException(
                $"bir2cir: ClrFlagsOperation '{role}' carries an invalid return type");
        var receiver = call["recv"]?.DeepClone()
            ?? throw new InvalidOperationException("bir2cir: ClrFlagsOperation call carries no receiver");
        var receiverRef = Bind("recv", "receiver", owner, receiver, out var receiverBinding);
        var bindings = new JsonArray { receiverBinding };
        JsonObject argumentRef = null;
        if (!unary)
        {
            argumentRef = Bind("arg", "argument", owner, args[0]?.DeepClone(), out var argumentBinding);
            bindings.Add(argumentBinding);
        }

        JsonNode result = role switch
        {
            "or" => EnumBits(representation, Binary("|", receiverRef, argumentRef)),
            "and" => EnumBits(representation, Binary("&", receiverRef, argumentRef)),
            "xor" => EnumBits(representation, Binary("^", receiverRef, argumentRef)),
            "inv" => EnumBits(representation, new JsonObject
            {
                ["k"] = "unaryOp", ["op"] = "~", ["e"] = receiverRef.DeepClone(),
            }),
            "contains" => Binary("==",
                EnumBits(representation, Binary("&", receiverRef, argumentRef)),
                argumentRef.DeepClone()),
            _ => throw new InvalidOperationException(),
        };
        var resultType = role == "contains"
            ? TypeJson.Fqn("kotlin.Boolean")
            : TypeJson.Write(representation.EnumType);
        call.Clear();
        call["k"] = "callEval";
        call["type"] = resultType;
        call["bindings"] = bindings;
        call["expr"] = result;
    }

    static JsonObject Bind(string phase, string role, TypeNode type, JsonNode expression, out JsonObject binding)
    {
        var id = $"cir$flags{_binding++}";
        binding = new JsonObject
        {
            ["id"] = id, ["phase"] = phase, ["kind"] = "value", ["stable"] = false,
            ["type"] = TypeJson.Write(type), ["role"] = $"[Flags] {role}", ["expr"] = expression,
        };
        return new JsonObject { ["k"] = "bindRef", ["id"] = id, ["sty"] = TypeJson.Write(type) };
    }

    static JsonObject Binary(string op, JsonNode lhs, JsonNode rhs) => new()
    {
        ["k"] = "binOp", ["op"] = op, ["lhs"] = lhs.DeepClone(), ["rhs"] = rhs.DeepClone(),
    };

    static JsonObject EnumBits(FlagsEnumRepresentation representation, JsonNode expression) => new()
    {
        ["k"] = "enumBits",
        ["type"] = TypeJson.Write(representation.EnumType),
        ["underlying"] = TypeJson.Write(representation.Underlying),
        ["e"] = new JsonObject
        {
            ["k"] = "conv", ["to"] = TypeJson.Write(representation.Underlying), ["e"] = expression,
        },
    };

    static void AssertConsumed(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("clrFlagsOperation"))
                throw new InvalidOperationException("bir2cir: ClrFlagsOperation carrier survived lowering");
            foreach (var child in obj)
                if (child.Value != null) AssertConsumed(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child != null) AssertConsumed(child);
    }

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}

sealed record FlagsEnumRepresentation(TypeNode EnumType, TypeNode Underlying);

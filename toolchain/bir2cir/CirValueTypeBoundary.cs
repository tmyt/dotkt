using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Custom modifiers identify CLR declaration signatures, not evaluation-stack values.
// Keep them on declarations and exact linkage; remove them from ordinary physical type uses.
static class CirValueTypeBoundary
{
    enum Role { Value, Owner, Method, Parameter, Field, Property, Linkage, Signature }

    public static void Apply(JsonNode root) => Visit(root, Role.Owner);

    static JsonNode Visit(JsonNode node, Role role)
    {
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var child = Visit(array[i], role);
                if (!ReferenceEquals(child, array[i])) array[i] = child;
            }
        }
        else if (node is JsonObject obj)
        {
            if (obj.ContainsKey("t"))
                return TypeJson.Write(role == Role.Signature
                    ? SignatureType(TypeJson.Read(obj)) : ValueType(TypeJson.Read(obj)));
            // The declaration identity is the authoritative shape of all memberRef carriers,
            // including per-module role tables and interface-slot sets.
            if (obj.ContainsKey("declaringType")) role = Role.Signature;
            var kind = (obj["k"] as JsonValue)?.GetValue<string>();
            foreach (var (key, value) in obj.ToList())
            {
                var childRole = role == Role.Signature ? Role.Signature : Role.Value;
                if (role == Role.Owner)
                    childRole = key switch {
                        "types" => Role.Owner, "methods" or "ctors" => Role.Method,
                        "fields" => Role.Field, "properties" => Role.Property, _ => childRole,
                    };
                else if (role == Role.Method)
                    childRole = key switch {
                        "params" => Role.Parameter, "ret" => Role.Signature,
                        "clrInterfaceImpls" or "clrBaseImpls" => Role.Linkage, _ => childRole,
                    };
                else if (role is Role.Parameter or Role.Field && key == "type"
                    || role == Role.Property && key is "type" or "getRet" or "getSig" or "setSig"
                    || role == Role.Linkage && key is "params" or "ret")
                    childRole = Role.Signature;
                if (kind != null && key is "calleeParams" or "calleeRet"
                    || kind is "callStatic" or "callInstance" or "constrainedCall" or "newDelegate" && key == "sig"
                    || kind is "clrEventAdd" or "clrEventRemove" && key == "sig"
                        && (obj["localAccessor"] as JsonValue)?.GetValue<bool>() == true)
                    childRole = Role.Signature;
                var child = Visit(value, childRole);
                if (!ReferenceEquals(child, value)) obj[key] = child;
            }
        }
        return node;
    }

    static TypeNode ValueType(TypeNode type) => type switch {
        TypeNode.Mod modifier => ValueType(modifier.Of),
        TypeNode.Fqn { Args: { } args } fqn => new TypeNode.Fqn(fqn.Name, args.Select(ValueType).ToArray()),
        TypeNode.Array array => new TypeNode.Array(ValueType(array.Elem), array.Rank, array.SzArray),
        TypeNode.ByRef byRef => new TypeNode.ByRef(ValueType(byRef.Of)),
        TypeNode.Ptr pointer => new TypeNode.Ptr(ValueType(pointer.Of)),
        TypeNode.Nullable nullable => new TypeNode.Nullable(ValueType(nullable.Of)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(ValueType(oblivious.Of)),
        TypeNode.Projection projection => new TypeNode.Projection(projection.Variance, ValueType(projection.Of)),
        TypeNode.Fn function => new TypeNode.Fn(function.Suspend, ValueType(function.Ret),
            function.Params.Select(ValueType).ToArray(), function.Recv == null ? null : ValueType(function.Recv),
            function.Clr, function.Ctx?.Select(ValueType).ToArray()),
        _ => type,
    };

    // A modifier names an actual CLR type, including its closed generic frame.
    // Normalize only that identity; value/return spellings (especially `void`)
    // remain unchanged and local descriptors still equal their declarations.
    static TypeNode SignatureType(TypeNode type) => type switch {
        TypeNode.Fqn { Args: { } args } named => named with { Args = args.Select(SignatureType).ToArray() },
        TypeNode.Array array => array with { Elem = SignatureType(array.Elem) },
        TypeNode.Nullable nullable => nullable with { Of = SignatureType(nullable.Of) },
        TypeNode.Oblivious oblivious => oblivious with { Of = SignatureType(oblivious.Of) },
        TypeNode.ByRef byRef => byRef with { Of = SignatureType(byRef.Of) },
        TypeNode.Ptr pointer => pointer with { Of = SignatureType(pointer.Of) },
        TypeNode.Mod modifier => modifier with {
            M = BirTypeLowering.CanonicalPhysicalSlotType(modifier.M), Of = SignatureType(modifier.Of) },
        TypeNode.Fn function => function with {
            Ret = SignatureType(function.Ret), Params = function.Params.Select(SignatureType).ToArray(),
            Recv = function.Recv == null ? null : SignatureType(function.Recv),
            Ctx = function.Ctx?.Select(SignatureType).ToArray() },
        _ => type,
    };

    public static void SelfTest()
    {
        var modified = TypeJson.Write(new TypeNode.Mod(false, new TypeNode.Fqn("Marker"), new TypeNode.Fqn("System.Object")));
        var root = JsonNode.Parse("""
            {"types":[{"fields":[{}],"properties":[{}],"methods":[{"params":[{}],
              "clrInterfaceImpls":[{}],"clrBaseImpls":[{}],"body":[{"k":"var"},{"k":"callStatic","memberRef":{"declaringType":{},"kind":"method"}}]}]}]}
            """);
        var owner = root["types"][0];
        var method = owner["methods"][0];
        var property = owner["properties"][0];
        owner["fields"][0]["type"] = modified.DeepClone();
        foreach (var key in new[] { "type", "getRet", "getSig", "setSig" })
            property[key] = key.EndsWith("Sig") ? new JsonArray(modified.DeepClone()) : modified.DeepClone();
        method["ret"] = modified.DeepClone();
        method["params"][0]["type"] = modified.DeepClone();
        method["clrInterfaceImpls"][0]["ret"] = modified.DeepClone();
        method["clrBaseImpls"][0]["ret"] = modified.DeepClone();
        method["clrBaseImpls"][0]["params"] = new JsonArray(modified.DeepClone());
        method["body"][0]["type"] = modified.DeepClone();
        var call = method["body"][1];
        call["ret"] = modified.DeepClone();
        call["sig"] = new JsonArray(modified.DeepClone());
        call["memberRef"]["returnType"] = modified.DeepClone();
        Apply(root);
        foreach (var signature in new[] { owner["fields"][0]["type"], property["type"], property["getRet"],
            property["getSig"][0], property["setSig"][0], method["ret"], method["params"][0]["type"],
            method["clrInterfaceImpls"][0]["ret"], method["clrBaseImpls"][0]["ret"],
            method["clrBaseImpls"][0]["params"][0], call["sig"][0], call["memberRef"]["returnType"] })
            if (!JsonNode.DeepEquals(modified, signature)) throw new InvalidOperationException("CIR signature modifier lost");
        foreach (var value in new[] { method["body"][0]["type"], call["ret"] })
            if (TypeJson.Read(value) is not TypeNode.Fqn { Name: "System.Object" })
                throw new InvalidOperationException("CIR value retains a signature modifier");
        var closedModifier = new TypeNode.Mod(false,
            new TypeNode.Fqn("System.Func`2", new TypeNode[] {
                new TypeNode.Fqn("object"), new TypeNode.Tv("method", 1) }), new TypeNode.Fqn("object"));
        method["clrInterfaceImpls"][0]["params"] = new JsonArray(TypeJson.Write(closedModifier));
        call["memberRef"]["parameterTypes"] = new JsonArray(TypeJson.Write(closedModifier));
        Apply(root);
        var exact = closedModifier with { M = BirTypeLowering.CanonicalPhysicalSlotType(closedModifier.M) };
        foreach (var signature in new[] { method["clrInterfaceImpls"][0]["params"][0],
            call["memberRef"]["parameterTypes"][0] })
            if (TypeJson.Read(signature) != exact)
                throw new InvalidOperationException("Exact CLR linkage lost a modifier/frame or retained a primitive alias");
        call["memberRef"]["returnType"] = TypeJson.Write(new TypeNode.Fqn("void"));
        Apply(root);
        if (TypeJson.Read(call["memberRef"]["returnType"]) != new TypeNode.Fqn("void"))
            throw new InvalidOperationException("Exact CLR linkage changed its canonical void contract");
        Console.WriteLine("[CIR value types] self-test OK (declaration/linkage modifiers retained; values unmodified)");
    }
}

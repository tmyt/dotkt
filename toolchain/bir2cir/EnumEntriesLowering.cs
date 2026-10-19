using System.Text.Json.Nodes;
using System.Threading;
using DotKt.Bir;

// Author ordinary, declaration-bound stdlib calls before representation demand analysis.
// Keep the cold enumValues operation semantic until inline specialization has selected its enum type.
static class EnumEntriesLowering
{
    const string Owner = "kotlin.enums.EnumEntriesCacheKt";
    static int counter;

    public static void Apply(JsonNode node, ReferenceMetadataIndex refs, Action<JsonObject> bind)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child != null) Apply(child, refs, bind);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.ToArray()) if (child.Value != null) Apply(child.Value, refs, bind);
        if (obj["k"]?.GetValue<string>() != "callStatic" || obj["args"] is not JsonArray { Count: 0 }) return;

        JsonNode enumType = null;
        var method = obj["method"]?.GetValue<string>();
        if (method == "entries" && obj["prop"]?.GetValue<string>() == "get"
            && TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn owner
            && TypeJson.Read(obj["ret"]) is TypeNode.Fqn { Name: "kotlin.enums.EnumEntries", Args.Length: 1 } result
            && result.Args[0] is TypeNode.Fqn entry && entry == owner)
            enumType = TypeJson.Write(entry);
        else if (method is "enumEntries" or "enumEntriesIntrinsic" && obj["owner"] == null
            && !obj.ContainsKey("ownerType") && !obj.ContainsKey("prop")
            && IsIntrinsic(obj, refs)
            && obj["typeArgs"] is JsonArray { Count: 1 } typeArguments)
            enumType = typeArguments[0]?.DeepClone();
        if (enumType == null) return;

        var entriesType = TypeJson.Write(new TypeNode.Fqn("kotlin.enums.EnumEntries", new[] { TypeJson.Read(enumType) }));
        var token = new JsonObject { ["k"] = "classRef", ["type"] = enumType.DeepClone() };
        var read = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(Owner), ["method"] = "cachedEnumEntries",
            ["sig"] = new JsonArray(TypeJson.Fqn("kotlin.Any")), ["args"] = new JsonArray(token.DeepClone()),
            ["ret"] = TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))),
        };
        bind(read);
        var create = new JsonObject {
            ["k"] = "callStatic", ["owner"] = TypeJson.Fqn(Owner), ["method"] = "getOrCreateEnumEntries",
            ["sig"] = new JsonArray(TypeJson.Fqn("kotlin.Any"), TypeJson.Write(new TypeNode.Array(new TypeNode.Tv("method", 0)))),
            ["typeArgs"] = new JsonArray(enumType.DeepClone()), ["ret"] = entriesType.DeepClone(),
            ["args"] = new JsonArray(token.DeepClone(), new JsonObject { ["k"] = "enumValues", ["type"] = enumType.DeepClone() }),
        };
        bind(create);
        var name = "__enumEntries$" + Interlocked.Increment(ref counter);
        var local = new JsonObject { ["k"] = "local", ["name"] = name };
        var position = obj["pos"]?.DeepClone();
        obj.Clear();
        obj["k"] = "valueBlock";
        obj["type"] = entriesType.DeepClone();
        obj["stmts"] = new JsonArray(new JsonObject {
            ["k"] = "var", ["name"] = name,
            ["type"] = TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))), ["init"] = read,
        });
        obj["result"] = new JsonObject {
            ["k"] = "cond",
            ["cond"] = new JsonObject { ["k"] = "objEq", ["lhs"] = local.DeepClone(), ["rhs"] = new JsonObject {
                ["k"] = "const", ["type"] = TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Nothing"))), ["value"] = null,
            } },
            ["then"] = create,
            ["else"] = new JsonObject { ["k"] = "cast", ["type"] = entriesType.DeepClone(), ["e"] = local.DeepClone() },
        };
        if (position != null) obj["pos"] = position;
    }

    static bool IsIntrinsic(JsonObject call, ReferenceMetadataIndex refs)
    {
        static bool IsEntriesOwner(string owner) => owner is "kotlin.enums.EnumEntriesKt" or "kotlin.enums.EnumEntriesClrKt";
        // Local stdlib calls retain their frontend owner. Referenced calls retain the selected declaration identity.
        // A same-named user function, local or imported, is not an enum intrinsic.
        if (IsEntriesOwner(TypeJson.OwnerName(call["calleeOwner"]))) return true;
        return refs.TryDeclarationIdentity(call[DeclarationIdentityBinding.Key]?.GetValue<string>(),
            out _, out var owner, out _, out _) && IsEntriesOwner(owner);
    }

    internal static void SelfTest()
    {
        var refs = ReferenceMetadataIndex.Build(Array.Empty<string>());
        var parameter = new TypeNode.Tv("method", 0);
        JsonObject Call(string owner) => new() {
            ["k"] = "callStatic", ["owner"] = null, ["method"] = "enumEntries", ["sig"] = new JsonArray(),
            ["typeArgs"] = new JsonArray(TypeJson.Write(parameter)), ["args"] = new JsonArray(),
            ["calleeOwner"] = TypeJson.Fqn(owner),
        };
        var bound = new List<JsonObject>();
        var intrinsic = Call("kotlin.enums.EnumEntriesKt");
        Apply(intrinsic, refs, bound.Add);
        if (intrinsic["k"]?.GetValue<string>() != "valueBlock" || bound.Count != 2
            || bound[0]["method"]?.GetValue<string>() != "cachedEnumEntries"
            || bound[1]["method"]?.GetValue<string>() != "getOrCreateEnumEntries"
            || TypeJson.Read(bound[1]["typeArgs"][0]) != parameter
            || TypeJson.Read(bound[1]["args"][1]["type"]) != parameter
            || intrinsic["result"]?["then"]?["args"]?[1]?["k"]?.GetValue<string>() != "enumValues")
            throw new InvalidOperationException("Enum entries lost its lazy cache or source-generic helper binding");
        var shadow = Call("user.EnumEntriesKt");
        Apply(shadow, refs, _ => throw new InvalidOperationException("A user enumEntries function became an intrinsic"));
        if (shadow["k"]?.GetValue<string>() != "callStatic")
            throw new InvalidOperationException("A user enumEntries function was rewritten");
        var overload = Call("kotlin.enums.EnumEntriesKt");
        ((JsonArray)overload["args"]).Add(new JsonObject { ["k"] = "local", ["name"] = "provider" });
        Apply(overload, refs, _ => throw new InvalidOperationException("The enumEntries provider overload was rewritten"));
        Console.WriteLine("[enum entries] self-test OK (lazy cache, source frame, intrinsic identity, provider overload)");
    }
}

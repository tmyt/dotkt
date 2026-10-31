using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Resolve the compiler's array content operations before source types are erased and before helper demand
// analysis. In particular, Array<UInt> and IntArray can share CLR array identity without sharing content policy.
static class DataClassArrayMemberLowering
{
    const string ArrayContentOwner = "kotlin.collections._ArraysKt";

    public static void Apply(JsonNode node, Action<JsonObject> bind)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Apply(child, bind);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var child in obj.ToArray()) Apply(child.Value, bind);
        if (obj["k"]?.GetValue<string>() != "callStatic"
            || TypeJson.OwnerName(obj["owner"]) != "kotlin.internal.ir") return;
        var operation = obj["method"]?.GetValue<string>() switch {
            "dataClassArrayMemberToString" => "contentToString",
            "dataClassArrayMemberHashCode" => "contentHashCode",
            _ => null,
        };
        if (operation == null) return;
        if (obj["args"] is not JsonArray { Count: 1 } args)
            throw new InvalidOperationException("Array content intrinsic requires one operand");
        var source = NodeType.Of(args[0]);
        if (source is TypeNode.Nullable nullable) source = nullable.Of;
        TypeNode signature;
        JsonArray typeArguments = null;
        if (source is TypeNode.Array { Rank: 1, SzArray: true } vector)
        {
            signature = new TypeNode.Array(new TypeNode.Projection("out", new TypeNode.Tv("method", 0)));
            // A consuming projection guarantees no readable X: ordinary content operations infer Any? for
            // Array<in X>, just as for Array<*>. An out projection retains its readable upper bound.
            var element = vector.Elem switch {
                TypeNode.Star or TypeNode.Projection { Variance: "in" } => new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                TypeNode.Projection projection => projection.Of,
                _ => vector.Elem,
            };
            typeArguments = new JsonArray(TypeJson.Write(element));
        }
        else if (source is TypeNode.Fqn primitive && primitive.Name is
            "kotlin.ByteArray" or "kotlin.ShortArray" or "kotlin.IntArray" or "kotlin.LongArray" or
            "kotlin.FloatArray" or "kotlin.DoubleArray" or "kotlin.BooleanArray" or "kotlin.CharArray")
            signature = source;
        else throw new InvalidOperationException("Array content intrinsic has no source array type");

        obj["owner"] = TypeJson.Fqn(ArrayContentOwner);
        obj["method"] = operation;
        obj["sig"] = new JsonArray(TypeJson.Write(new TypeNode.Nullable(signature)));
        if (typeArguments != null) obj["typeArgs"] = typeArguments;
        var result = TypeJson.Fqn(operation == "contentToString" ? "kotlin.String" : "kotlin.Int");
        obj["ret"] = result.DeepClone();
        obj["sty"] = result;
        bind(obj);
    }
}

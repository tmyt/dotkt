using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Resolve the compiler's array content operations before source types are erased and before helper demand
// analysis. In particular, Array<UInt> and IntArray can share CLR array identity without sharing content policy.
static class DataClassArrayMemberLowering
{
    const string ArrayContentOwner = "kotlin.collections._ArraysKt";

    public static BirGraphValue Apply(BirGraphValue node, Func<BirCallStatic, BirCallStatic> bind)
    {
        if (node is IBirExecutablePayload) return node;
        node.VisitChildSlots(child => child.Replace(Apply(child.Value, bind)));
        if (node is not BirCallStatic call
            || call.Owner.OrDefault() is not TypeNode.Fqn { Name: "kotlin.internal.ir" }) return node;
        var operation = call.Method.OrDefault() switch {
            "dataClassArrayMemberToString" => "contentToString",
            "dataClassArrayMemberHashCode" => "contentHashCode",
            _ => null,
        };
        if (operation == null) return node;
        if (!call.Args.HasValue || call.Args.Value.Length != 1)
            throw new InvalidOperationException("Array content intrinsic requires one operand");
        var source = BirNodeType.Of((BirGraphNode)call.Args.Value[0]);
        if (source is TypeNode.Nullable nullable) source = nullable.Of;
        TypeNode signature;
        TypeNode[] typeArguments = null;
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
            typeArguments = [element];
        }
        else if (source is TypeNode.Fqn primitive && primitive.Name is
            "kotlin.ByteArray" or "kotlin.ShortArray" or "kotlin.IntArray" or "kotlin.LongArray" or
            "kotlin.FloatArray" or "kotlin.DoubleArray" or "kotlin.BooleanArray" or "kotlin.CharArray")
            signature = source;
        else throw new InvalidOperationException("Array content intrinsic has no source array type");

        var result = new TypeNode.Fqn(operation == "contentToString" ? "kotlin.String" : "kotlin.Int");
        return bind(call with {
            Owner = new TypeNode.Fqn(ArrayContentOwner), Method = operation,
            Sig = new TypeNode[] { new TypeNode.Nullable(signature) },
            TypeArgs = typeArguments != null ? typeArguments : call.TypeArgs,
            Ret = result, Sty = result,
        });
    }
}

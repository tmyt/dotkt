using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize CLR collection-view conversions at Kotlin call sites.
//
// BirTypeLowering deliberately maps a head-position `List<T>` slot to IReadOnlyList<T>, while a mutable/spilled
// value can carry IList<T>. Those sibling interfaces are not related in the CLR type lattice even when the concrete
// object implements both. CIR must therefore contain the cast explicitly; ilemit must not discover this semantic seam
// from Reflection.Emit's transient stack types.
//
// This pass runs after type lowering, substitutes method type arguments into the declaration signature, and wraps only
// the exact sanctioned sibling-interface shapes. It is structural CLR lowering: no source/library declaration name is
// special-cased.
static class CollectionViewCallCoercion
{
    const string IList = "System.Collections.Generic.IList";
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IReadOnlyList = "System.Collections.Generic.IReadOnlyList";
    const string IReadOnlyCollection = "System.Collections.Generic.IReadOnlyCollection";

    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var child in obj.ToList())
                    if (child.Value != null) Walk(child.Value);
                CoerceCall(obj);
                break;
            case JsonArray array:
                foreach (var child in array)
                    if (child != null) Walk(child);
                break;
        }
    }

    static void CoerceCall(JsonObject call)
    {
        if (Str(call["k"]) is not ("callStatic" or "callInstance")
            || call["args"] is not JsonArray args
            || call["sig"] is not JsonArray sig
            || args.Count != sig.Count)
            return;

        var typeArgs = ReadTypes(call["typeArgs"] as JsonArray) ?? Array.Empty<TypeNode>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is not JsonObject arg) continue;
            var want0 = TypeJson.Read(sig[i]);
            var got = ExprType(arg);
            if (want0 == null || got == null) continue;
            var want = SubstituteMethodTvs(want0, typeArgs);
            if (!IsCollectionViewSeam(got, want)) continue;
            args[i] = new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(want),
                ["e"] = arg.DeepClone(),
            };
        }
    }

    static TypeNode ExprType(JsonObject expr)
    {
        // Every executable expression kind that can carry a declaration/result type uses one of these CIR slots.
        // Prefer the explicit result; `type` is the construction/cast/constant target.
        return TypeJson.Read(expr["ret"]) ?? TypeJson.Read(expr["type"]);
    }

    static bool IsCollectionViewSeam(TypeNode got, TypeNode want)
    {
        if (got is not TypeNode.Fqn { Args.Length: 1 } g
            || want is not TypeNode.Fqn { Args.Length: 1 } w
            || g.Args[0] != w.Args[0])
            return false;
        return (g.Name, w.Name) switch
        {
            (IList, IReadOnlyList) => true,
            (IList, IReadOnlyCollection) => true,
            (ICollection, IReadOnlyCollection) => true,
            (IReadOnlyList, IList) => true,
            (IReadOnlyList, ICollection) => true,
            (IReadOnlyCollection, ICollection) => true,
            _ => false,
        };
    }

    static TypeNode SubstituteMethodTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null =>
            new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstituteMethodTvs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteMethodTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteMethodTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstituteMethodTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteMethodTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstituteMethodTvs(fn.Ret, args),
            fn.Params.Select(p => SubstituteMethodTvs(p, args)).ToArray(),
            fn.Recv == null ? null : SubstituteMethodTvs(fn.Recv, args)),
        _ => type,
    };

    static TypeNode[] ReadTypes(JsonArray array)
    {
        if (array == null) return null;
        var result = new TypeNode[array.Count];
        for (var i = 0; i < array.Count; i++)
            if ((result[i] = TypeJson.Read(array[i])) == null) return null;
        return result;
    }

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

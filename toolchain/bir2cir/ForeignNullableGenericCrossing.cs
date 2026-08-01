using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE ONE SHAPE CARRIER-ARGUMENT ERASURE CANNOT MEET (#86).
//
// Kotlin's `X?` for a possibly-value `X` is `System.Object` in every reified ARGUMENT, so a Kotlin `List<Int?>` is an
// `IReadOnlyList<object>` and there is no Kotlin type whose physical form is `List<Nullable<int32>>`. A .NET API may
// nevertheless DECLARE one — `List<int?>`, `Dictionary<string, int?>`, `int?[]`, `Func<int?, string>` — and a
// resolved foreign declaration is authoritative: NullableGenericErasure does not restate what a CLR member declares.
//
// The two therefore do not meet, and neither side can be bent to the other:
//   * `List<object>` and `List<Nullable<int32>>` are unrelated INVARIANT reified generics; no `castclass` joins them
//     (one throws) and no covariance relates them, because a reified value-type argument has none.
//   * Adapting silently — copying into a fresh `List<int?>` at the call, or wrapping — would give the argument
//     different identity and different mutation semantics than the Kotlin source says it has. An adapter that
//     changes what `xs.add(1)` does to the caller's list is a wrong program, not a bridge.
//
// So the crossing is REFUSED, naming the member and the slot. That is the same discipline as the other refusals in
// this family: a program with no valid CIL lowering owes its author an actionable message rather than a silently
// different meaning. The refusal is narrow by construction — it needs a .NET member whose signature genuinely nests
// a `Nullable<V>` inside a reified argument, which the BCL surface almost never does — and a DIRECT `Nullable<V>`
// parameter or return is untouched, because a Kotlin scalar `Int?` IS a `System.Nullable<int32>` and crosses exactly.
//
// Runs on the LOWERED tree, where `memberSig`/`ret` are the final CLR signature: earlier the same node may still be
// mid-resolution and a Kotlin-vocabulary `Nullable(Tv)` would be read as a foreign declaration it is not.
static class ForeignNullableGenericCrossing
{
    public static void Check(JsonNode root, string file)
    {
        Walk(root, file);
    }

    static void Walk(JsonNode node, string file)
    {
        switch (node)
        {
            case JsonObject obj:
                if (IsClrBoundKind(Str(obj["k"]))) CheckCall(obj, file);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, file);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, file);
                break;
        }
    }

    // The call kinds bound to a .NET member. Their `memberSig` is that member's declared parameter vector and their
    // `ret`/`type` is its result; both are the foreign declaration, not a Kotlin view of one.
    static bool IsClrBoundKind(string k) =>
        k is "clrStatic" or "clrInstance" or "clrGenericStatic" or "clrGenericInstance" or "newClr"
          or "clrPropGet" or "clrPropSet";

    static void CheckCall(JsonObject call, string file)
    {
        // A call names its member in `method`; a property/field access names it in `name`; a `newClr` names none.
        var member = Str(call["method"]) ?? Str(call["name"]) ?? ".ctor";
        var owner = TypeJson.Read(call["type"]) is TypeNode.Fqn f ? f.Name : "<unknown>";
        if (call["memberSig"] is JsonArray sig)
            for (var i = 0; i < sig.Count; i++)
                if (TypeJson.Read(sig[i]) is TypeNode p && NestedValueNullable(p))
                    throw Refuse(file, owner, member, "parameter " + i, p);
        if (TypeJson.Read(call["ret"]) is TypeNode ret && NestedValueNullable(ret))
            throw Refuse(file, owner, member, "return", ret);
    }

    static InvalidOperationException Refuse(string file, string owner, string member, string slot, TypeNode t)
        => new(
            $"bir2cir: {file}: the .NET member '{owner}.{member}' declares '{Render(t)}' at its {slot}, which no "
            + "Kotlin type inhabits. A nullable value type inside a generic argument, an array element or a delegate "
            + "component is System.Object in Kotlin (#86), so a Kotlin 'List<Int?>' is an IReadOnlyList<object> and "
            + "is not a List<Nullable<Int32>> — the two are unrelated invariant reified generics and no conversion "
            + "relates them. Call an overload whose argument is not a nullable value type, or build the .NET "
            + "collection explicitly and pass it through a slot declared with that .NET type.");

    // A `Nullable<V>` sitting inside a REIFIED ARGUMENT — a generic type argument, an array element, a delegate
    // component. The head is deliberately excluded: a DIRECT `Nullable<V>` parameter or return is exactly what a
    // Kotlin scalar `Int?` is, and it crosses without any adaptation at all.
    static bool NestedValueNullable(TypeNode t) => t switch
    {
        TypeNode.Fqn { Args: { } args } => args.Any(InArgument),
        TypeNode.Array a => InArgument(a.Elem),
        TypeNode.ByRef b => NestedValueNullable(b.Of),
        TypeNode.Nullable n => NestedValueNullable(n.Of),
        TypeNode.Oblivious o => NestedValueNullable(o.Of),
        TypeNode.Fn fn => InArgument(fn.Ret) || fn.Params.Any(InArgument)
                          || (fn.Recv != null && InArgument(fn.Recv)),
        _ => false,
    };

    // One reified argument: either it IS the `Nullable<V>` Kotlin cannot put there, or it contains one deeper down.
    // By this point the tree is lowered, so a `Nullable` node is always a real `System.Nullable<V>` over a value
    // type — BirTypeLowering strips every reference `?` before it gets here. An NRT-OBLIVIOUS wrapper is a pure
    // annotation and is looked through, so a `[MaybeNull] List<int?>` is the same crossing as a plain one.
    static bool InArgument(TypeNode t) => t switch
    {
        TypeNode.Nullable => true,
        TypeNode.Oblivious o => InArgument(o.Of),
        _ => NestedValueNullable(t),
    };

    static string Render(TypeNode t) => t switch
    {
        TypeNode.Nullable n => "System.Nullable<" + Render(n.Of) + ">",
        TypeNode.Oblivious o => Render(o.Of),
        TypeNode.Array a => Render(a.Elem) + "[]",
        TypeNode.ByRef b => "ref " + Render(b.Of),
        TypeNode.Fqn { Args: { } args } fa => fa.Name + "<" + string.Join(", ", args.Select(Render)) + ">",
        TypeNode.Fqn f => f.Name,
        TypeNode.Fn fn => "(" + string.Join(", ", fn.Params.Select(Render)) + ") -> " + Render(fn.Ret),
        _ => t.ToString(),
    };

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A generic Kotlin iterator read yields an erased value. The actual consumer, not the iterator adapter,
// owns its extraction into a native T. Select only the standard declaration/override relation; unrelated
// next/previous methods, super dispatch, and already-bound CLR bridges remain exact.
sealed partial class KotlinIteratorValueBinding
{
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    internal TypeNode.Tv Element(JsonObject call)
    {
        var element = CandidateElement(Text(call["k"]) == "callInstance", call["super"]?.GetValue<bool>() == true,
            call["clrOwnerResolved"]?.GetValue<bool>() == true, (call["args"] as JsonArray)?.Count,
            (call["typeArgs"] as JsonArray)?.Count ?? 0, TypeJson.Read(call["sty"] ?? call["dynRet"]));
        if (element == null) return null;
        return Operation(call) == null ? null : element;
    }

    static TypeNode.Tv CandidateElement(bool instanceCall, bool super, bool resolved, int? arguments,
        int typeArguments, TypeNode source) => instanceCall && !super && !resolved && arguments == 0 && typeArguments == 0
            ? source as TypeNode.Tv : null;

    static string Operation(JsonObject call)
    {
        return SelectOperation(Text(call[DeclarationIdentityBinding.Key]), TypeJson.OwnerName(call["ownerType"]),
            Text(call["method"]), (call["overrides"] as JsonArray)?.OfType<JsonObject>()
                .Select(edge => new ProtocolOverride(Text(edge[DeclarationIdentityBinding.Key]),
                    TypeJson.OwnerName(edge["owner"]), Text(edge["member"]), edge["arity"]?.GetValue<int>())));
    }

    readonly record struct ProtocolOverride(string Identity, string Owner, string Member, int? Arity);

    static string SelectOperation(string identity, string owner, string member, IEnumerable<ProtocolOverride> overrides)
    {
        static bool Protocol(string owner, string member) => member switch {
            "next" => owner is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator"
                or "kotlin.collections.ListIterator" or "kotlin.collections.MutableListIterator",
            "previous" => owner is "kotlin.collections.ListIterator" or "kotlin.collections.MutableListIterator",
            _ => false,
        };
        if (identity != null && Protocol(owner, member)) return member;
        return overrides?.Where(edge => edge.Identity != null && edge.Arity == 0 && Protocol(edge.Owner, edge.Member))
            .Select(edge => edge.Member).Distinct().SingleOrDefault();
    }

    internal void Bind(JsonObject read)
    {
        var previous = Operation(read) == "previous";
        var capability = TypeJson.Fqn("DotKt.Runtime.CompilerServices."
            + (previous ? "KotlinListIteratorValueSlot" : "KotlinIteratorValueSlot"));
        var name = CallEvalLowering.FreshBindingId();
        JsonObject Local() => new() { ["k"] = "local", ["name"] = name };
        var receiver = read["recv"]
            ?? throw new InvalidOperationException("Iterator value read has no receiver");
        // Move, do not clone, the receiver: nested reads already belong to the same value-flow plan.
        read.Remove("recv");
        var direct = read.DeepClone().AsObject();
        direct["recv"] = Local();
        direct["sty"] = TypeJson.Write(ObjectType);
        read.Clear();
        read["k"] = "valueBlock";
        read["stmts"] = new JsonArray(new JsonObject {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Write(ObjectType), ["init"] = receiver,
        });
        read["result"] = new JsonObject {
            ["k"] = "cond", ["type"] = TypeJson.Write(ObjectType),
            ["cond"] = new JsonObject { ["k"] = "isInst", ["type"] = capability.DeepClone(), ["e"] = Local() },
            ["then"] = new JsonObject {
                ["k"] = "callInstance", ["ownerType"] = capability, ["virtual"] = true,
                ["method"] = previous ? "dotktPreviousValue" : "dotktNextValue",
                ["recv"] = Local(), ["sig"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = TypeJson.Write(new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"))),
            },
            // Keep the selected native/nominal protocol when there is no erased adapter capability.
            // Generic Kotlin iteration must not become uncached reflection or lose its exact interface view.
            ["else"] = direct,
        };
        read["type"] = TypeJson.Write(ObjectType);
        read["sty"] = TypeJson.Write(ObjectType);
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}

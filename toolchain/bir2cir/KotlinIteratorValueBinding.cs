using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A generic Kotlin iterator read yields an erased value. The actual consumer, not the iterator adapter,
// owns its extraction into a native T. Select only the standard declaration/override relation; unrelated
// next/previous methods, super dispatch, and already-bound CLR bridges remain exact.
sealed class KotlinIteratorValueBinding
{
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    internal TypeNode.Tv Element(JsonObject call)
    {
        if (Text(call["k"]) != "callInstance" || call["super"]?.GetValue<bool>() == true
            || call["clrOwnerResolved"]?.GetValue<bool>() == true
            || call["args"] is not JsonArray { Count: 0 }
            || call["typeArgs"] is JsonArray { Count: > 0 }
            || TypeJson.Read(call["sty"] ?? call["dynRet"]) is not TypeNode.Tv element)
            return null;
        return Operation(call) == null ? null : element;
    }

    static string Operation(JsonObject call)
    {
        static bool Protocol(string owner, string member) => member switch {
            "next" => owner is "kotlin.collections.Iterator" or "kotlin.collections.MutableIterator"
                or "kotlin.collections.ListIterator" or "kotlin.collections.MutableListIterator",
            "previous" => owner is "kotlin.collections.ListIterator" or "kotlin.collections.MutableListIterator",
            _ => false,
        };
        if (Text(call[DeclarationIdentityBinding.Key]) != null
            && Protocol(TypeJson.OwnerName(call["ownerType"]), Text(call["method"])))
            return Text(call["method"]);
        return (call["overrides"] as JsonArray)?.OfType<JsonObject>()
            .Where(edge => Text(edge[DeclarationIdentityBinding.Key]) != null
                && edge["arity"]?.GetValue<int>() == 0
                && Protocol(TypeJson.OwnerName(edge["owner"]), Text(edge["member"])))
            .Select(edge => Text(edge["member"])).Distinct().SingleOrDefault();
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

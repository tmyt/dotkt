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
    const string Runtime = "DotKt.Runtime.CompilerServices.StarProjectionRuntimeKt";
    readonly ReferenceMetadataIndex references;
    readonly Dictionary<string, JsonObject> localHelpers = new(StringComparer.Ordinal);
    static readonly TypeNode ObjectType = new TypeNode.Fqn("object");

    internal KotlinIteratorValueBinding(IEnumerable<JsonObject> roots, ReferenceMetadataIndex refs)
    {
        references = refs;
        foreach (var root in roots.Where(root => Text(root["fileClass"]) == Runtime))
            foreach (var method in (root["methods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (Text(method["name"]) is "listIteratorNextErased" or "listIteratorPreviousErased")
                    localHelpers.Add(Text(method["name"]), method);
    }

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
        var name = Operation(read) == "previous" ? "listIteratorPreviousErased" : "listIteratorNextErased";
        string identity;
        JsonArray signature;
        if (localHelpers.TryGetValue(name, out var local))
        {
            identity = Text(local[DeclarationIdentityBinding.Key])
                ?? throw new InvalidOperationException("Iterator value helper has no declaration identity");
            signature = new JsonArray(((JsonArray)local["params"]).OfType<JsonObject>()
                .Select(parameter => parameter["type"]?.DeepClone()).ToArray());
        }
        else
        {
            var helper = references.AuthoredKotlinHelper(Runtime, name, 0,
                new TypeNode[] { new TypeNode.Fqn("kotlin.Any") });
            identity = helper.DeclarationId;
            signature = new JsonArray(helper.ParamTypeNodes.Select(TypeJson.Write).ToArray());
        }
        var receiver = read["recv"]?.DeepClone()
            ?? throw new InvalidOperationException("Iterator value read has no receiver");
        read.Clear();
        read["k"] = "callStatic";
        read["owner"] = TypeJson.Fqn(Runtime);
        read["method"] = name;
        read[DeclarationIdentityBinding.Key] = identity;
        read["sig"] = signature;
        read["typeArgs"] = new JsonArray();
        read["args"] = new JsonArray(receiver);
        read["ret"] = TypeJson.Write(ObjectType);
        read["sty"] = TypeJson.Write(ObjectType);
    }

    static string Text(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}

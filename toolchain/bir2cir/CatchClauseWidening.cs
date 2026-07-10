using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class CatchClauseWidening
{
    static readonly string[] IndexOobNet = { "System.ArgumentOutOfRangeException", "System.IndexOutOfRangeException" };

    public static void Apply(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["catches"] is JsonArray catches) WidenCatches(catches);
            foreach (var kv in obj) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray arr)
            foreach (var it in arr) if (it != null) Apply(it);
    }

    static void WidenCatches(JsonArray catches)
    {
        for (var i = catches.Count - 1; i >= 0; i--)
        {
            if (catches[i] is not JsonObject c) continue;
            if (TypeJson.Read(c["excType"]) is not TypeNode.Fqn et
                || ReferenceMetadataIndex.BareOwnerFqn(et.Name) != "kotlin.IndexOutOfBoundsException") continue;
            catches.RemoveAt(i);
            for (var j = IndexOobNet.Length - 1; j >= 0; j--)   // insert in reverse -> keeps [ArgumentOOR, IndexOOR] order
            {
                var clone = (JsonObject)c.DeepClone();
                clone["excType"] = TypeJson.Fqn(IndexOobNet[j]);
                catches.Insert(i, clone);
            }
        }
    }
}


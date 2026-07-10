using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET EVENT `+=`/`-=` binding (the idiomatic ClrEvent<T> redesign). A .NET event is surfaced by facadegen/kotc as a
// read-only `kotlin.clr.ClrEvent<T>` property (a compile-time fiction — a .NET event is NOT a first-class value), and
// `w.Changed += handler` resolves through NORMAL Kotlin operator resolution to `w.Changed.plusAssign(handler)`. kotc
// emits that as the PLAIN operator call `callInstance(ownerType = kotlin.clr.ClrEvent, method = plusAssign/minusAssign,
// recv = <clrEventGet w Changed>, args = [handler])` — no `add_`/`remove_` naming, no CLR binding. The event READ
// `w.Changed` is a DEDICATED kotc-dialect node `clrEventGet` (the ClrEvent<T> handle — a CLR-only-vocab synthetic
// kotc lowers itself; NOT `clrPropGet`, which after A2/#61 is exclusively a real bir2cir-produced .NET property).
// This pass BINDS the pair: it reads the owner .NET type + event name straight off the clrEventGet member-access node
// and emits the EXISTING clrEventAdd/clrEventRemove node (ilemit's EmitClrEvent, unchanged) — so the emitted
// add/remove accessor IL is identical to the old `add_<E>`/`remove_<E>` model. The ClrEvent<T> value + the clrEventGet
// are consumed here, never emitted (a .NET event isn't materializable). This is the Kotlin<->CLR event relation, bir2cir's to own.
static class ClrEventOperatorBinding
{
    public static JsonNode Apply(JsonNode root) => Walk(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonNode Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var kv in obj) copy[kv.Key] = kv.Value == null ? null : Walk(kv.Value);   // children first (bottom-up)
            return Transform(copy) ?? copy;
        }
        if (node is JsonArray arr)
        {
            var copy = new JsonArray();
            foreach (var item in arr) copy.Add(item == null ? null : Walk(item));
            return copy;
        }
        return node.DeepClone();
    }

    // A `callInstance` on kotlin.clr.ClrEvent whose method is plusAssign/minusAssign -> the add/remove accessor node.
    static JsonNode Transform(JsonObject node)
    {
        if (Str(node["k"]) != "callInstance") return null;
        if (TypeJson.OwnerName(node["ownerType"]) != "kotlin.clr.ClrEvent") return null;
        var method = Str(node["method"]);
        if (method != "plusAssign" && method != "minusAssign") return null;
        // The receiver is the event member-access `w.Changed`, emitted by kotc as a `clrEventGet` carrying the .NET
        // owner type (`type`), the event name (`name`), and the actual owner value (`recv`). Anything else is not an event op.
        if (node["recv"] is not JsonObject eventGet || Str(eventGet["k"]) != "clrEventGet") return null;
        if (node["args"] is not JsonArray args || args.Count != 1) return null;
        var isStatic = (eventGet["static"] as JsonValue)?.GetValue<bool>() ?? false;
        return new JsonObject
        {
            ["k"] = method == "plusAssign" ? "clrEventAdd" : "clrEventRemove",
            ["type"] = eventGet["type"]?.DeepClone(),
            ["event"] = eventGet["name"]?.DeepClone(),
            ["static"] = isStatic,
            ["recv"] = isStatic ? null : eventGet["recv"]?.DeepClone(),
            ["handler"] = args[0]?.DeepClone(),
        };
    }
}


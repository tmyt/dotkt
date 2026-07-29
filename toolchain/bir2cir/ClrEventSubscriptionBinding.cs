using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET EVENT `subscribe` binding. A .NET event is surfaced by dll2klib/kotc as a
// read-only `kotlin.clr.ClrEvent<T>` property (a compile-time fiction — a .NET event is NOT a first-class value), and
// `w.Changed.subscribe(handler)` resolves through NORMAL Kotlin resolution. kotc emits it as a PLAIN call
// `callInstance(ownerType = kotlin.clr.ClrEvent, method = subscribe,
// recv = <clrEventGet w Changed>, args = [handler])` — no `add_`/`remove_` naming, no CLR binding. The event READ
// `w.Changed` is a DEDICATED kotc-dialect node `clrEventGet` (the ClrEvent<T> handle — a CLR-only-vocab synthetic
// kotc lowers itself; NOT `clrPropGet`, which after A2/#61 is exclusively a real bir2cir-produced .NET property).
// This pass BINDS the pair: it reads the owner .NET type + event name straight off the clrEventGet member-access node
// and emits the EXISTING clrEventAdd/clrEventRemove nodes (ilemit's EmitClrEvent, unchanged). `subscribe` additionally
// creates the real stdlib EventSubscription<T> with a synthesized remove callback, after spilling receiver + handler
// once. The emitted add/remove accessor IL is identical to the old direct-accessor model. The ClrEvent<T> value + clrEventGet
// are consumed here, never emitted (a .NET event isn't materializable). This is the Kotlin<->CLR event relation, bir2cir's to own.
static class ClrEventSubscriptionBinding
{
    sealed class Binder
    {
        readonly string _scope;
        readonly ReferenceMetadataIndex _refs;
        int _next;

        public Binder(JsonNode root, ReferenceMetadataIndex refs)
        {
            _refs = refs;
            var fileClass = root is JsonObject f ? Str(f["fileClass"]) : null;
            _scope = string.Concat((fileClass ?? "File").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        }

        public JsonNode Apply(JsonNode root) => Walk(root);

        JsonNode Walk(JsonNode node)
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

        JsonNode Transform(JsonObject node)
        {
            if (Str(node["k"]) != "callInstance") return null;
            if (TypeJson.OwnerName(node["ownerType"]) != "kotlin.clr.ClrEvent") return null;
            if (Str(node["method"]) != "subscribe") return null;
            // The receiver is the event member-access `w.Changed`, emitted by kotc as a `clrEventGet` carrying the .NET
            // owner type (`type`), the event name (`name`), and the actual owner value (`recv`). Anything else is not an event op.
            if (node["recv"] is not JsonObject eventGet || Str(eventGet["k"]) != "clrEventGet") return null;
            if (node["args"] is not JsonArray args || args.Count != 1) return null;
            return Subscribe(node, eventGet, args[0]);
        }

        JsonNode Subscribe(JsonObject call, JsonObject eventGet, JsonNode handler)
        {
            var handlerType = HandlerType(call, handler)
                ?? throw new InvalidOperationException("bir2cir: ClrEvent.subscribe is missing its instantiated handler type");
            var ownerType = eventGet["type"]?.DeepClone()
                ?? throw new InvalidOperationException("bir2cir: ClrEvent.subscribe is missing its event owner type");
            var isStatic = (eventGet["static"] as JsonValue)?.GetValue<bool>() ?? false;
            // A CLR static class is surfaced as a Kotlin object, so kotc's neutral Kotlin projection carries an
            // INSTANCE-looking receiver. Bind the actual CLR event declaration here; this is the lowering layer that
            // owns the ABI decision. If metadata cannot establish one unique declaration, preserve the projected bit
            // and let ClrMemberResolution emit its existing precise missing/ambiguous-member diagnostic.
            var ownerName = TypeJson.OwnerName(ownerType);
            var eventName = Str(eventGet["name"]);
            if (_refs.TryClrEventIsStatic(ownerName, eventName, out var declaredStatic))
                isStatic = declaredStatic;
            var id = _next++;
            var handlerLocal = $"__clrEventSubscriptionHandler{id}";
            var receiverLocal = $"__clrEventSubscriptionReceiver{id}";
            var closureName = $"dotkt${_scope}$EventRemove{id}";

            var free = FreeTypeVariables(ownerType, handlerType);
            var closureOwnerType = RemapForClosure(ownerType, free);
            var closureHandlerType = RemapForClosure(handlerType, free);
            var unit = Fqn("kotlin.Unit");
            var publicRemoveFnType = Fn(handlerType, unit);
            var closureFqn = Fqn(closureName);

            var fields = new JsonArray();
            var captures = new JsonArray();
            JsonNode removeReceiver = null;
            if (!isStatic)
            {
                fields.Add(new JsonObject { ["name"] = "__receiver", ["type"] = closureOwnerType.DeepClone() });
                captures.Add(Local(receiverLocal, ownerType));
                removeReceiver = new JsonObject
                {
                    ["k"] = "field",
                    ["ownerType"] = closureFqn.DeepClone(),
                    ["recv"] = new JsonObject { ["k"] = "this" },
                    ["name"] = "__receiver",
                };
            }

            var remove = new JsonObject
            {
                ["k"] = "clrEventRemove",
                ["type"] = closureOwnerType.DeepClone(),
                ["event"] = eventGet["name"]?.DeepClone(),
                ["static"] = isStatic,
                ["recv"] = removeReceiver,
                ["handler"] = Local("handler", closureHandlerType),
            };
            var synthClass = new JsonObject
            {
                ["name"] = closureName,
                ["fields"] = fields,
                ["params"] = new JsonArray
                {
                    new JsonObject { ["name"] = "handler", ["type"] = closureHandlerType.DeepClone() },
                },
                ["ret"] = unit.DeepClone(),
                ["body"] = new JsonArray
                {
                    new JsonObject { ["k"] = "exprStmt", ["expr"] = remove },
                },
            };
            if (free.Count > 0)
                synthClass["typeParams"] = new JsonArray(free.Select((_, i) => (JsonNode)JsonValue.Create($"T{i}")).ToArray());

            var removeClosure = new JsonObject
            {
                ["k"] = "newClosure",
                ["closureType"] = closureFqn,
                ["captures"] = captures,
                ["method"] = "invoke",
                ["funcType"] = publicRemoveFnType.DeepClone(),
                ["synthClass"] = synthClass,
            };
            if (free.Count > 0)
                removeClosure["typeArgs"] = new JsonArray(free.Select(x => x.Original.DeepClone()).ToArray());

            var stmts = new JsonArray();
            if (!isStatic)
                stmts.Add(new JsonObject
                {
                    ["k"] = "var", ["name"] = receiverLocal, ["type"] = ownerType.DeepClone(),
                    ["init"] = eventGet["recv"]?.DeepClone(),
                });
            stmts.Add(new JsonObject
            {
                ["k"] = "var", ["name"] = handlerLocal, ["type"] = handlerType.DeepClone(),
                ["init"] = handler?.DeepClone(),
            });
            stmts.Add(new JsonObject
            {
                ["k"] = "exprStmt",
                ["expr"] = new JsonObject
                {
                    ["k"] = "clrEventAdd",
                    ["type"] = ownerType.DeepClone(),
                    ["event"] = eventGet["name"]?.DeepClone(),
                    ["static"] = isStatic,
                    ["recv"] = isStatic ? null : Local(receiverLocal, ownerType),
                    ["handler"] = Local(handlerLocal, handlerType),
                },
            });

            var subscriptionType = new JsonObject
            {
                ["t"] = "fqn",
                ["name"] = "kotlin.clr.EventSubscription",
                ["args"] = new JsonArray { handlerType.DeepClone() },
            };
            return new JsonObject
            {
                ["k"] = "valueBlock",
                ["stmts"] = stmts,
                ["result"] = new JsonObject
                {
                    ["k"] = "new",
                    ["type"] = subscriptionType,
                    ["argTypes"] = new JsonArray { handlerType.DeepClone(), publicRemoveFnType.DeepClone() },
                    ["args"] = new JsonArray { Local(handlerLocal, handlerType), removeClosure },
                },
            };
        }
    }

    public static JsonNode Apply(JsonNode root, ReferenceMetadataIndex refs) => new Binder(root, refs).Apply(root);

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static JsonObject Fqn(string name) => new() { ["t"] = "fqn", ["name"] = name };

    static JsonObject Fn(JsonNode param, JsonNode ret) => new()
    {
        ["t"] = "fn", ["suspend"] = false, ["ret"] = ret.DeepClone(),
        ["params"] = new JsonArray { param.DeepClone() },
    };

    static JsonObject Local(string name, JsonNode type) => new()
    {
        ["sty"] = type.DeepClone(), ["k"] = "local", ["name"] = name,
    };

    static JsonNode HandlerType(JsonObject call, JsonNode handler)
    {
        if (call["ownerType"] is JsonObject owner && owner["args"] is JsonArray oa && oa.Count == 1)
            return oa[0]?.DeepClone();
        if (call["sig"] is JsonArray sig && sig.Count == 1) return sig[0]?.DeepClone();
        if (call["argTypes"] is JsonArray ats && ats.Count == 1) return ats[0]?.DeepClone();
        if (handler is JsonObject h && h["sty"] != null) return h["sty"]?.DeepClone();
        return null;
    }

    sealed record FreeTv(string Scope, int Index, JsonNode Original);

    static List<FreeTv> FreeTypeVariables(params JsonNode[] types)
    {
        var result = new List<FreeTv>();
        var seen = new HashSet<(string Scope, int Index)>();
        void Walk(JsonNode n)
        {
            if (n is JsonObject o)
            {
                if (Str(o["t"]) == "tv")
                {
                    var scope = Str(o["scope"]) ?? "method";
                    var index = (o["i"] as JsonValue)?.GetValue<int>() ?? 0;
                    if (seen.Add((scope, index))) result.Add(new FreeTv(scope, index, o.DeepClone()));
                    return;
                }
                foreach (var kv in o) if (kv.Value != null) Walk(kv.Value);
            }
            else if (n is JsonArray a) foreach (var child in a) if (child != null) Walk(child);
        }
        foreach (var type in types) if (type != null) Walk(type);
        return result;
    }

    static JsonNode RemapForClosure(JsonNode node, List<FreeTv> free)
    {
        if (node is JsonObject o)
        {
            if (Str(o["t"]) == "tv")
            {
                var scope = Str(o["scope"]) ?? "method";
                var index = (o["i"] as JsonValue)?.GetValue<int>() ?? 0;
                var mapped = free.FindIndex(x => x.Scope == scope && x.Index == index);
                if (mapped >= 0) return new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = mapped };
            }
            var copy = new JsonObject();
            foreach (var kv in o) copy[kv.Key] = kv.Value == null ? null : RemapForClosure(kv.Value, free);
            return copy;
        }
        if (node is JsonArray a)
        {
            var copy = new JsonArray();
            foreach (var child in a) copy.Add(child == null ? null : RemapForClosure(child, free));
            return copy;
        }
        return node.DeepClone();
    }
}

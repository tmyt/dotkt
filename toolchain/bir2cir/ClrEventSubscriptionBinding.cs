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
        readonly IReadOnlyDictionary<(string Owner, string Event), JsonNode> _forwardedOwners;
        readonly IReadOnlySet<string> _localTypes;
        int _next;

        public Binder(
            JsonNode root,
            ReferenceMetadataIndex refs,
            IReadOnlyDictionary<(string Owner, string Event), JsonNode> forwardedOwners,
            IReadOnlySet<string> localTypes)
        {
            _refs = refs;
            _forwardedOwners = forwardedOwners;
            _localTypes = localTypes;
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
            var ownerType = EventOwner(eventGet)
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
            // The synthesized remove method receives the same spilled source delegate as the add operation.  Keep
            // that delegate's Invoke identity before its transient expression type is gone.
            ClrMemberResolution.ResolveDelegateInvoke(remove, handlerType, _refs, _localTypes);
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
            var add = new JsonObject
            {
                ["k"] = "clrEventAdd",
                ["type"] = ownerType.DeepClone(),
                ["event"] = eventGet["name"]?.DeepClone(),
                ["static"] = isStatic,
                ["recv"] = isStatic ? null : Local(receiverLocal, ownerType),
                ["handler"] = Local(handlerLocal, handlerType),
            };
            // The subscription spill turns the handler into a plain local and its transient `sty`
            // is consumed before final member stamping.  Preserve the already-known source delegate
            // declaration now, so ilemit can re-wrap it without looking up Invoke by name.
            ClrMemberResolution.ResolveDelegateInvoke(add, handlerType, _refs, _localTypes);
            stmts.Add(new JsonObject { ["k"] = "exprStmt", ["expr"] = add });

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

        // A handle on a Kotlin delegating class names that Kotlin receiver in `type`; the CLR event itself belongs to
        // the delegated interface. Resolve that relation from the sibling forwarder directive, preserving constructed
        // generic owner arguments. Direct .NET event handles keep their original owner unchanged.
        JsonNode EventOwner(JsonObject eventGet)
        {
            var ownerType = eventGet["type"]?.DeepClone();
            var owner = TypeJson.OwnerName(ownerType);
            var name = Str(eventGet["name"]);
            if (owner != null && name != null && _forwardedOwners.TryGetValue((owner, name), out var forwarded))
            {
                var template = TypeJson.Read(forwarded);
                var actualArgs = (TypeJson.Read(ownerType) as TypeNode.Fqn)?.Args;
                return template == null ? forwarded.DeepClone() : TypeJson.Write(SubstituteTypeVariables(template, actualArgs));
            }
            return ownerType;
        }

    }

    // Class delegation and its use site can live in different Kotlin source files. Build this relation once for the
    // whole compilation before the per-file lowering loop; scanning only the current root would leave a sibling-file
    // `DelegatingSource.Changed.subscribe(...)` incorrectly bound against the local wrapper rather than the delegated
    // CLR interface that owns the event declaration.
    public static IReadOnlyDictionary<(string Owner, string Event), JsonNode> CollectForwardedOwners(
        IEnumerable<JsonNode> roots)
    {
        var definitions = roots.OfType<JsonObject>()
            .SelectMany(root => root["types"] is JsonArray types
                ? types.OfType<JsonObject>() : Enumerable.Empty<JsonObject>())
            .Where(type => Str(type["name"]) != null)
            .GroupBy(type => Str(type["name"]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var direct = new Dictionary<(string Owner, string Event), JsonNode>();
        var eventNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in definitions.Values)
        {
            var owner = Str(type["name"]);
            if (type["clrEventForwarders"] is not JsonArray forwarders) continue;
            foreach (var forwarder in forwarders.OfType<JsonObject>())
            {
                var name = Str(forwarder["name"]);
                if (name == null || forwarder["ownerType"] == null) continue;
                direct[(owner, name)] = forwarder["ownerType"].DeepClone();
                eventNames.Add(name);
            }
        }

        // A subclass inherits the delegating event property/accessors. Carry its base instantiation through so
        // `Derived<X> : Wrapper<String,X>` maps the inherited event to `Source<X>`, not to the local Derived owner.
        var result = new Dictionary<(string Owner, string Event), JsonNode>(direct);
        JsonNode Resolve(string owner, string eventName, HashSet<string> visiting)
        {
            if (result.TryGetValue((owner, eventName), out var found)) return found;
            if (!visiting.Add(owner) || !definitions.TryGetValue(owner, out var definition)
                || TypeJson.Read(definition["base"]) is not TypeNode.Fqn baseType
                || !definitions.ContainsKey(baseType.Name)) return null;
            var inherited = Resolve(baseType.Name, eventName, visiting);
            visiting.Remove(owner);
            if (inherited == null || TypeJson.Read(inherited) is not TypeNode template) return null;
            var closed = TypeJson.Write(SubstituteTypeVariables(template, baseType.Args));
            result[(owner, eventName)] = closed;
            return closed;
        }
        foreach (var owner in definitions.Keys)
            foreach (var eventName in eventNames)
                Resolve(owner, eventName, new HashSet<string>(StringComparer.Ordinal));
        return result;
    }

    // Close a declaration-space physical owner with the constructed local owner/base arguments. If the current use is
    // itself inside the same generic frame and no arguments are available, the original `!i` remains valid.
    static TypeNode SubstituteTypeVariables(TypeNode type, TypeNode[] ownerArgs) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when ownerArgs != null && tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Fqn { Args: { Length: > 0 } args } f =>
            new TypeNode.Fqn(f.Name, args.Select(a => SubstituteTypeVariables(a, ownerArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstituteTypeVariables(n.Of, ownerArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstituteTypeVariables(o.Of, ownerArgs)),
        TypeNode.Array a => new TypeNode.Array(SubstituteTypeVariables(a.Elem, ownerArgs)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstituteTypeVariables(b.Of, ownerArgs)),
        TypeNode.Fn fn => new TypeNode.Fn(
            fn.Suspend,
            SubstituteTypeVariables(fn.Ret, ownerArgs),
            fn.Params.Select(p => SubstituteTypeVariables(p, ownerArgs)).ToArray(),
            fn.Recv == null ? null : SubstituteTypeVariables(fn.Recv, ownerArgs),
            fn.Clr,
            fn.Ctx?.Select(c => SubstituteTypeVariables(c, ownerArgs)).ToArray()),
        _ => type,
    };

    public static JsonNode Apply(
        JsonNode root,
        ReferenceMetadataIndex refs,
        IReadOnlyDictionary<(string Owner, string Event), JsonNode> forwardedOwners,
        IReadOnlySet<string> localTypes) =>
        new Binder(root, refs, forwardedOwners, localTypes).Apply(root);

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

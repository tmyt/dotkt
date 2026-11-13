using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Materialize each event-member read as a real stdlib ClrEvent<H>. The source is evaluated once and
// captured by ordinary add/remove callback closures. H is the complete handler shape; subscribe and close
// are ordinary stdlib calls, so stored, returned, captured and cross-module handles use the same path.
// Only this layer resolves the Kotlin event declaration into its concrete CLR accessors.
static partial class ClrEventSubscriptionBinding
{
    sealed class Binder
    {
        readonly string _scope;
        readonly ReferenceMetadataIndex _refs;
        readonly IReadOnlyDictionary<(string Owner, string Event), TypeNode> _forwardedOwners;
        readonly Dictionary<JsonObject, (JsonArray TypeParams, JsonArray MethodParams)> _scopes = new();
        readonly JsonArray _empty = new();
        int _next;

        public List<JsonNode> MaterializedRoots { get; } = new();

        public Binder(
            JsonNode root,
            ReferenceMetadataIndex refs,
            IReadOnlyDictionary<(string Owner, string Event), TypeNode> forwardedOwners)
        {
            _refs = refs;
            _forwardedOwners = forwardedOwners;
            var fileClass = root is JsonObject f ? Str(f["fileClass"]) : null;
            _scope = string.Concat((fileClass ?? "File").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            IndexScopes(root);
        }

        void IndexScopes(JsonNode root)
        {
            void IndexCallable(JsonObject callable, JsonArray typeParams) =>
                _scopes[callable] = (typeParams, callable["typeParams"] as JsonArray ?? _empty);

            void IndexType(JsonObject type)
            {
                var typeParams = TypeParameterFrame.CloneDeclarations(type);
                _scopes[type] = (typeParams, _empty);
                if (type["ctors"] is JsonArray ctors)
                    foreach (var ctor in ctors.OfType<JsonObject>()) IndexCallable(ctor, typeParams);
                if (type["methods"] is JsonArray methods)
                    foreach (var method in methods.OfType<JsonObject>()) IndexCallable(method, typeParams);
                if (type["types"] is JsonArray nested)
                    foreach (var child in nested.OfType<JsonObject>()) IndexType(child);
            }

            if (root is not JsonObject file) return;
            _scopes[file] = (_empty, _empty);
            if (file["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>()) IndexCallable(method, _empty);
            if (file["types"] is JsonArray types)
                foreach (var type in types.OfType<JsonObject>()) IndexType(type);
        }

        public JsonNode Apply(JsonNode root) => Walk(root, _empty, _empty);

        JsonNode Walk(JsonNode node, JsonArray typeParams, JsonArray methodParams)
        {
            if (node is JsonObject obj)
            {
                if (_scopes.TryGetValue(obj, out var scope))
                    (typeParams, methodParams) = scope;
                var denseSuspend = Str(obj["k"]) == "newSuspendLambda" && Str(obj["typeFrame"]) == "dense";
                var suspendDeclarations = obj["typeParams"] as JsonArray ?? _empty;
                var copy = new JsonObject();
                foreach (var kv in obj)
                {
                    if (kv.Value == null) { copy[kv.Key] = null; continue; }
                    if (denseSuspend
                        && kv.Key is "body" or "params" or "captures" or "suspendRet" or "typeParams")
                        // Both source TV scopes index a dense carrier's single owned declaration frame.
                        copy[kv.Key] = Walk(kv.Value, suspendDeclarations, suspendDeclarations);
                    else if (kv.Key == "synthClass" && ClosureSynthesis.HasPreboundFrame(kv.Value))
                        copy[kv.Key] = Walk(kv.Value, TypeParameterFrame.CloneDeclarations((JsonObject)kv.Value), _empty);
                    else copy[kv.Key] = Walk(kv.Value, typeParams, methodParams);
                }
                return Transform(copy, typeParams, methodParams) ?? copy;
            }
            if (node is JsonArray arr)
            {
                var copy = new JsonArray();
                foreach (var item in arr)
                    copy.Add(item == null ? null : Walk(item, typeParams, methodParams));
                return copy;
            }
            return node.DeepClone();
        }

        JsonNode Transform(JsonObject node, JsonArray typeParams, JsonArray methodParams)
        {
            if (Str(node["k"]) != "clrEventGet") return null;
            var materialized = Materialize(node, typeParams, methodParams);
            MaterializedRoots.Add(materialized);
            return materialized;
        }

        JsonNode Materialize(JsonObject eventGet, JsonArray typeParams, JsonArray methodParams)
        {
            var eventType = TypeJson.Read(eventGet["sty"] ?? eventGet["ret"]) as TypeNode.Fqn;
            if (eventType?.Name != "kotlin.clr.ClrEvent" || eventType.Args == null)
                throw new InvalidOperationException("bir2cir: an event read is missing its complete handler type");
            var handlerArguments = _refs.NullableTypeFrames.TryGetValue(eventType.Name, out var frame)
                ? frame.OrdinaryArguments(eventType.Args) : eventType.Args;
            if (handlerArguments.Length != 1)
                throw new InvalidOperationException("bir2cir: an event read has no unique handler argument");
            var handlerType = TypeJson.Write(handlerArguments[0]);
            var ownerType = EventOwner(eventGet)
                ?? throw new InvalidOperationException("bir2cir: an event read is missing its event owner type");
            var isStatic = (eventGet["static"] as JsonValue)?.GetValue<bool>() ?? false;
            if (_refs.TryClrEventIsStatic(TypeJson.OwnerName(ownerType), Str(eventGet["name"]), out var declaredStatic))
                isStatic = declaredStatic;

            var id = _next++;
            var receiverLocal = $"__clrEventReceiver{id}";
            var free = FreeTypeVariables(ownerType, handlerType);
            ExpandConstraintVariables(free, typeParams, methodParams);
            // Author ingredients in the enclosing lexical frame. ClosureSynthesis owns the one rebind into its
            // dense class frame; pre-remapping here would rebind type-scoped permutations a second time.
            var closureOwnerType = ownerType.DeepClone();
            var closureHandlerType = handlerType.DeepClone();
            var unit = Fqn("kotlin.Unit");
            var callbackType = Fn(handlerType, unit);

            JsonObject Callback(bool add)
            {
                var kind = add ? "Add" : "Remove";
                var closureName = $"dotkt${_scope}$Event{kind}{id}";
                var closureFqn = Fqn(closureName);
                var fields = new JsonArray();
                var captures = new JsonArray();
                JsonNode receiver = null;
                if (!isStatic)
                {
                    fields.Add(new JsonObject { ["name"] = "__receiver", ["type"] = closureOwnerType.DeepClone() });
                    captures.Add(Local(receiverLocal, ownerType));
                    receiver = new JsonObject
                    {
                        ["k"] = "field", ["ownerType"] = closureFqn.DeepClone(),
                        ["recv"] = new JsonObject { ["k"] = "this" }, ["name"] = "__receiver",
                    };
                }
                var operation = new JsonObject
                {
                    ["k"] = add ? "clrEventAdd" : "clrEventRemove",
                    ["type"] = closureOwnerType.DeepClone(), ["event"] = eventGet["name"]?.DeepClone(),
                    ["static"] = isStatic,
                    ["handler"] = Local("handler", closureHandlerType), ["handlerExact"] = true,
                };
                if (!isStatic) operation["recv"] = receiver;
                if (eventGet["memberVisibility"] is JsonNode visibility)
                    operation["memberVisibility"] = visibility.DeepClone();
                var synthClass = new JsonObject
                {
                    ["name"] = closureName, ["fields"] = fields,
                    ["params"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "handler", ["type"] = closureHandlerType.DeepClone() },
                    },
                    ["ret"] = unit.DeepClone(),
                    ["body"] = new JsonArray { new JsonObject { ["k"] = "exprStmt", ["expr"] = operation } },
                };
                if (free.Count > 0)
                    synthClass["typeParams"] = new JsonArray(free.Select((item, i) =>
                        ClosureTypeParameter(item, i, typeParams, methodParams)).ToArray());
                var callback = new JsonObject
                {
                    ["k"] = "newClosure", ["closureType"] = closureFqn, ["captures"] = captures,
                    ["method"] = "invoke", ["funcType"] = callbackType.DeepClone(), ["synthClass"] = synthClass,
                };
                if (free.Count > 0)
                    callback["typeArgs"] = new JsonArray(free.Select(item => item.Original.DeepClone()).ToArray());
                return callback;
            }

            var handleType = TypeJson.Write(eventType);
            // The event-read result already passed through declaration-frame materialization. Reuse that complete
            // physical application, including storage companions, rather than closing its source frame twice.
            var stmts = new JsonArray();
            if (!isStatic)
                stmts.Add(new JsonObject
                {
                    ["k"] = "var", ["name"] = receiverLocal, ["type"] = ownerType.DeepClone(),
                    ["init"] = eventGet["recv"]?.DeepClone(),
                });
            return new JsonObject
            {
                ["k"] = "valueBlock", ["stmts"] = stmts, ["sty"] = handleType.DeepClone(),
                ["result"] = new JsonObject
                {
                    ["k"] = "new", ["type"] = handleType,
                    ["argTypes"] = new JsonArray { callbackType.DeepClone(), callbackType.DeepClone() },
                    ["args"] = new JsonArray { Callback(true), Callback(false) },
                },
            };
        }

        static JsonNode SourceTypeParameter(FreeTv item, JsonArray typeParams, JsonArray methodParams)
        {
            var frame = item.Scope == "type" ? typeParams : item.Scope == "method" ? methodParams : null;
            return frame != null && item.Index >= 0 && item.Index < frame.Count ? frame[item.Index] : null;
        }

        static void ExpandConstraintVariables(
            List<FreeTv> free,
            JsonArray typeParams,
            JsonArray methodParams)
        {
            for (var i = 0; i < free.Count; i++)
            {
                if (SourceTypeParameter(free[i], typeParams, methodParams) is not JsonObject descriptor
                    || descriptor["constraints"] is not JsonArray constraints) continue;
                foreach (var dependency in FreeTypeVariables(constraints))
                    if (!free.Any(item => item.Scope == dependency.Scope && item.Index == dependency.Index))
                        free.Add(dependency);
            }
        }

        static JsonNode ClosureTypeParameter(
            FreeTv item,
            int index,
            JsonArray typeParams,
            JsonArray methodParams)
        {
            var name = $"T{index}";
            var declaration = TypeParameterDeclaration.Read(SourceTypeParameter(item, typeParams, methodParams));
            return (declaration with { Name = name }).ToDom();
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
                var actualArgs = (TypeJson.Read(ownerType) as TypeNode.Fqn)?.Args;
                return TypeJson.Write(SubstituteTypeVariables(forwarded, actualArgs));
            }
            return ownerType;
        }

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
        IReadOnlyDictionary<(string Owner, string Event), TypeNode> forwardedOwners,
        out IReadOnlyList<JsonNode> materializedRoots)
    {
        var binder = new Binder(root, refs, forwardedOwners);
        var result = binder.Apply(root);
        materializedRoots = binder.MaterializedRoots;
        return result;
    }

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

}

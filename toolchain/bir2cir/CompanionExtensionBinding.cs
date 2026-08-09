using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Select the physical CLR identity of Kotlin 2.4 companion extensions.
//
// kotc carries only Kotlin facts: receiver classifier, source member name, and whether a declaration/use is a
// function, property getter/setter, or field. This pass owns the physical CLR name. For cross-module uses it accepts
// only the exact mapping recorded by the trusted producer carrier; it never reconstructs an old ABI from names.
static class CompanionExtensionBinding
{
    internal sealed record Binding(string PhysicalName);

    public sealed class LocalIndex
    {
        internal LocalIndex(IReadOnlyDictionary<string, Binding> bindings) => Bindings = bindings;
        internal IReadOnlyDictionary<string, Binding> Bindings { get; }
    }

    public static LocalIndex Apply(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs)
    {
        var bindings = CollectBindings(roots);
        foreach (var root in roots) RewriteUses(root, bindings, refs);
        return new LocalIndex(bindings);
    }

    // Raw default-argument BIR is opaque while declarations are first bound, then materialized into a consumer root
    // later by InlineSplice or DefaultArgSplice. Bind only that newly materialized graph against the already-collected
    // module declaration index and trusted reference index; never rediscover declarations or re-walk sibling roots.
    public static void BindMaterializedUses(
        JsonNode root,
        LocalIndex local,
        ReferenceMetadataIndex refs) =>
        RewriteUses(root, local.Bindings, refs);

    static Dictionary<string, Binding> CollectBindings(IReadOnlyList<JsonNode> roots)
    {
        var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);

        foreach (var root in roots.OfType<JsonObject>())
        {
            var owner = Str(root["fileClass"])
                ?? throw new InvalidOperationException("BIR file has no fileClass while binding companion extensions");
            if (root["methods"] is JsonArray methods)
                foreach (var method in methods.OfType<JsonObject>())
                    BindDeclaration(owner, method, bindings);
            if (root["fields"] is JsonArray fields)
                foreach (var field in fields.OfType<JsonObject>())
                    BindDeclaration(owner, field, bindings);
        }
        return bindings;
    }

    static void BindDeclaration(string owner, JsonObject declaration, Dictionary<string, Binding> bindings)
    {
        if (Str(declaration["companionReceiver"]) is not string receiver) return;
        var sourceName = Str(declaration["companionSourceName"])
            ?? throw new InvalidOperationException("companion extension declaration has no source name");
        var kind = Str(declaration["companionMemberKind"]);
        ValidateKind(kind);

        var physicalName = Prefix(kind) + PhysicalRoot(receiver, sourceName);
        var key = Key(owner, receiver, kind, sourceName);
        if (bindings.TryGetValue(key, out var prior))
        {
            if (prior.PhysicalName != physicalName)
                throw new InvalidOperationException(
                    $"inconsistent companion-extension physical identity for '{owner}.{sourceName}'");
        }
        else bindings.Add(key, new Binding(physicalName));

        declaration["name"] = physicalName;
    }

    static string Prefix(string kind) => kind switch {
        "get" => "get_",
        "set" => "set_",
        "function" or "field" => "",
        _ => throw new InvalidOperationException("invalid companion extension member kind: " + kind),
    };

    static void ValidateKind(string kind)
    {
        _ = Prefix(kind);
    }

    static string PhysicalRoot(string receiverJson, string sourceName)
    {
        var receiver = TypeJson.OwnerName(JsonNode.Parse(receiverJson))
            ?? throw new InvalidOperationException(
                "companion extension receiver is not a classifier type: " + receiverJson);
        var encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(receiver)).ToLowerInvariant();
        return "dotkt$companion$" + encoded + "$" + sourceName;
    }

    static string Key(string owner, string receiver, string kind, string sourceName) =>
        NormalizeOwner(owner) + "\u001f" + CanonicalReceiver(receiver) + "\u001f" + kind + "\u001f" + sourceName;

    static string NormalizeOwner(string owner) => owner.Replace('+', '.');

    static string CanonicalReceiver(string receiver)
    {
        var classifier = TypeJson.OwnerName(JsonNode.Parse(receiver))
            ?? throw new InvalidOperationException(
                "companion extension receiver is not a classifier type: " + receiver);
        // Kotlin accepts only a bare classifier here. A projected generic classifier may be rehydrated by the
        // consumer frontend as C<Any>, but those arguments are not part of the companion association.
        return TypeJson.Fqn(classifier).ToJsonString();
    }

    static void RewriteUses(
        JsonNode node,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs)
    {
        switch (node)
        {
            case JsonObject obj:
                RewriteUse(obj, bindings, refs);
                foreach (var child in obj.Select(pair => pair.Value).Where(value => value != null).ToList())
                    RewriteUses(child, bindings, refs);
                break;
            case JsonArray array:
                foreach (var child in array.Where(value => value != null).ToList())
                    RewriteUses(child, bindings, refs);
                break;
        }
    }

    static void RewriteUse(
        JsonObject node,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs)
    {
        if (Str(node["companionReceiver"]) is not string receiver) return;
        // Declarations retain these semantic facts until RoundtripMetadata stamps the producer carrier.
        // Only use sites have a receiver tag without declaration identity.
        if (node["companionSourceName"] != null || node["companionMemberKind"] != null) return;
        var nodeKind = Str(node["k"]);

        if (nodeKind == "callInline")
        {
            var owner = TypeJson.OwnerName(node["owner"]);
            var callee = TypeJson.OwnerName(node["callee"]);
            if (owner == null || callee == null)
                throw Missing(owner, receiver, "function", callee, nodeKind);
            var dot = callee.LastIndexOf('.');
            var sourceName = dot < 0 ? callee : callee[(dot + 1)..];
            var physical = Resolve(owner, receiver, "function", sourceName, bindings, refs, nodeKind);
            node["callee"] = TypeJson.Fqn(dot < 0 ? physical : callee[..(dot + 1)] + physical);
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind == "callStatic")
        {
            var owner = TypeJson.OwnerName(node["calleeOwner"])
                ?? TypeJson.OwnerName(node["owner"])
                ?? TypeJson.OwnerName(node["ownerType"]);
            var sourceName = Str(node["method"]);
            var kind = Str(node["prop"]) switch {
                "get" => "get",
                "set" => "set",
                _ => "function",
            };
            var physical = Resolve(owner, receiver, kind, sourceName, bindings, refs, nodeKind);
            node["method"] = physical;
            node.Remove("prop");
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind == "newDelegate")
        {
            var owner = TypeJson.OwnerName(node["calleeOwner"]);
            var sourceName = Str(node["method"]);
            node["method"] = Resolve(owner, receiver, "function", sourceName, bindings, refs, nodeKind);
            node.Remove("companionReceiver");
            return;
        }

        if (nodeKind is "staticField" or "staticFieldSet" or "lateinitGet")
        {
            var owner = TypeJson.OwnerName(node["ownerType"]);
            var sourceName = Str(node["name"]);
            if (nodeKind == "lateinitGet" && sourceName is not null)
                node["lateinitSourceName"] = sourceName;
            node["name"] = Resolve(owner, receiver, "field", sourceName, bindings, refs, nodeKind);
            node.Remove("companionReceiver");
            return;
        }

        throw new InvalidOperationException(
            $"companion extension tag appears on unsupported BIR node '{nodeKind ?? "<missing>"}'");
    }

    static string Resolve(
        string owner,
        string receiver,
        string kind,
        string sourceName,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs,
        string nodeKind)
    {
        if (owner == null || sourceName == null) throw Missing(owner, receiver, kind, sourceName, nodeKind);
        if (bindings.TryGetValue(Key(owner, receiver, kind, sourceName), out var local))
            return local.PhysicalName;
        if (refs.TryCompanionExtensionMember(owner, receiver, kind, sourceName, out var physical))
            return physical;
        throw Missing(owner, receiver, kind, sourceName, nodeKind);
    }

    static InvalidOperationException Missing(
        string owner, string receiver, string kind, string sourceName, string nodeKind) =>
        new($"no trusted companion-extension binding for '{owner ?? "<missing>"}.{sourceName ?? "<missing>"}' " +
            $"(receiver {receiver}, kind {kind}, BIR node {nodeKind})");

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}

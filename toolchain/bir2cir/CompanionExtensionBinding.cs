using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Placement decisions are allocated once over the owning typed declarations.
// The remaining DOM use binders consume these same scalar identities.
static partial class CompanionExtensionBinding
{
    internal sealed record Binding(string PhysicalName, string PhysicalOwner, string ValueType = null);
    public sealed class LocalIndex
    {
        internal LocalIndex(IReadOnlyDictionary<string, Binding> bindings) => Bindings = bindings;
        internal IReadOnlyDictionary<string, Binding> Bindings { get; }
    }

    public static void BindMaterializedUses(JsonNode root, LocalIndex local, ReferenceMetadataIndex refs)
        => RewriteUses(root, local.Bindings, refs);

    static TypeNode RemapWrapperType(TypeNode type, int methodShift) => type switch
    {
        TypeNode.Tv tv when tv.Scope == "type" => new TypeNode.Tv("method", tv.I),
        TypeNode.Tv tv when tv.Scope == "method" => new TypeNode.Tv("method", tv.I + methodShift),
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args?.Select(arg => RemapWrapperType(arg, methodShift)).ToArray()),
        TypeNode.Nullable nullable => new TypeNode.Nullable(RemapWrapperType(nullable.Of, methodShift)),
        TypeNode.Oblivious oblivious => new TypeNode.Oblivious(RemapWrapperType(oblivious.Of, methodShift)),
        TypeNode.Array array => new TypeNode.Array(RemapWrapperType(array.Elem, methodShift)),
        TypeNode.ByRef byRef => new TypeNode.ByRef(RemapWrapperType(byRef.Of, methodShift)),
        TypeNode.Fn function => new TypeNode.Fn(
            function.Suspend,
            RemapWrapperType(function.Ret, methodShift),
            function.Params.Select(parameter => RemapWrapperType(parameter, methodShift)).ToArray(),
            function.Recv == null ? null : RemapWrapperType(function.Recv, methodShift),
            function.Clr,
            function.Ctx?.Select(parameter => RemapWrapperType(parameter, methodShift)).ToArray()),
        _ => type,
    };

    static string CoreName(string kind, string sourceName)
    {
        var root = "dotkt$core$" + sourceName;
        return kind is "get" or "set" ? KotlinPropertyAccessors.PhysicalName(root, kind) : root;
    }

    static string StableId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    static void ValidateKind(string kind)
    {
        if (kind is not ("get" or "set" or "function" or "field"))
            throw new InvalidOperationException("invalid companion extension member kind: " + kind);
    }

    static string PhysicalRoot(string receiverJson, string sourceName)
    {
        var receiver = ReceiverClassifier(TypeNode.Parse(receiverJson))
            ?? throw new InvalidOperationException(
                "companion extension receiver is not a classifier type: " + receiverJson);
        var encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(receiver)).ToLowerInvariant();
        return "dotkt$companion$" + encoded + "$" + sourceName;
    }

    static string Key(string owner, string receiver, string kind, string sourceName) =>
        NormalizeOwner(owner) + "\u001f" + CanonicalReceiver(receiver) + "\u001f" + kind + "\u001f" + sourceName;

    static string NormalizeOwner(string owner) => owner.Replace('+', '.');

    internal static string CanonicalReceiver(string receiver)
    {
        var classifier = ReceiverClassifier(TypeNode.Parse(receiver))
            ?? throw new InvalidOperationException(
                "companion extension receiver is not a classifier type: " + receiver);
        // Kotlin accepts only a bare classifier here. A projected generic classifier may be rehydrated by the
        // consumer frontend as C<Any>, but those arguments are not part of the companion association.
        return TypeNode.ToJson(new TypeNode.Fqn(classifier));
    }

    // A classifier imported from an ordinary C# assembly is commonly a platform type (`oblivious(fqn)`). The
    // companion association is the bare classifier and is independent of NRT flexibility/nullability.
    static string ReceiverClassifier(JsonNode node) => ReceiverClassifier(TypeJson.Read(node));

    internal static string ReceiverClassifier(TypeNode type)
    {
        while (type is TypeNode.Oblivious oblivious) type = oblivious.Of;
        while (type is TypeNode.Nullable nullable) type = nullable.Of;
        return type is TypeNode.Fqn fqn ? fqn.Name : null;
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

    readonly record struct UseFacts(string NodeKind, string DeclarationId, string Receiver, string Owner, string Name, string Accessor);
    sealed record UsePlan(Binding Target, string SourceName, string Kind, bool Identified);

    static UsePlan PlanUse(UseFacts facts, IReadOnlyDictionary<string, Binding> bindings, ReferenceMetadataIndex refs)
    {
        if (facts.DeclarationId != null && refs.TryDeclarationIdentity(facts.DeclarationId,
                out var exactName, out var exactOwner, out var intrinsic, out _) && intrinsic == null)
        {
            if (facts.NodeKind is not ("callInline" or "callStatic" or "newDelegate"))
                throw new InvalidOperationException($"identified companion extension appears on unsupported BIR node '{facts.NodeKind ?? "<missing>"}'");
            return new(new Binding(exactName, exactOwner), facts.Name, null, true);
        }
        var kind = facts.NodeKind switch {
            "callInline" or "newDelegate" => "function",
            "callStatic" => facts.Accessor is "get" or "set" ? facts.Accessor : "function",
            "staticField" or "lateinitGet" => "get", "staticFieldSet" => "set",
            _ => throw new InvalidOperationException($"companion extension tag appears on unsupported BIR node '{facts.NodeKind ?? "<missing>"}'"),
        };
        var name = facts.Name;
        if (facts.NodeKind == "callInline")
        {
            if (facts.Owner == null || name == null) throw Missing(facts.Owner, facts.Receiver, kind, name, facts.NodeKind);
            var dot = name.LastIndexOf('.');
            name = dot < 0 ? name : name[(dot + 1)..];
        }
        return new(Resolve(facts.Owner, facts.Receiver, kind, name, bindings, refs, facts.NodeKind), name, kind, false);
    }

    static void RewriteUse(JsonObject node, IReadOnlyDictionary<string, Binding> bindings, ReferenceMetadataIndex refs)
    {
        if (Str(node["companionReceiver"]) is not string receiver
            || node["companionSourceName"] != null || node["companionMemberKind"] != null) return;
        var nodeKind = Str(node["k"]);
        var owner = nodeKind switch {
            "callInline" => TypeJson.OwnerName(node["owner"]),
            "callStatic" => TypeJson.OwnerName(node["calleeOwner"]) ?? TypeJson.OwnerName(node["owner"]) ?? TypeJson.OwnerName(node["ownerType"]),
            "newDelegate" => TypeJson.OwnerName(node["calleeOwner"]),
            _ => TypeJson.OwnerName(node["ownerType"]),
        };
        var name = nodeKind == "callInline" ? TypeJson.OwnerName(node["callee"])
            : Str(node[nodeKind is "staticField" or "staticFieldSet" or "lateinitGet" ? "name" : "method"]);
        var plan = PlanUse(new(nodeKind, Str(node[DeclarationIdentityBinding.Key]), receiver, owner, name, Str(node["prop"])), bindings, refs);
        var target = plan.Target;
        switch (nodeKind)
        {
            case "callInline":
                node["owner"] = TypeJson.Fqn(target.PhysicalOwner);
                node["callee"] = TypeJson.Fqn(target.PhysicalOwner + "." + target.PhysicalName);
                break;
            case "callStatic":
                if (!plan.Identified && plan.Kind is "get" or "set") KotlinPropertyAccessors.PreserveCallIdentity(node, plan.SourceName, plan.Kind);
                node["method"] = target.PhysicalName;
                RewriteStaticOwner(node, target.PhysicalOwner);
                node.Remove("prop");
                break;
            case "newDelegate":
                node["method"] = target.PhysicalName;
                node["calleeOwner"] = TypeJson.Fqn(target.PhysicalOwner);
                break;
            default:
                var value = nodeKind == "staticFieldSet" ? node["value"]?.DeepClone() : null;
                var style = node["sty"]?.DeepClone();
                node.Clear();
                if (style != null) node["sty"] = style;
                node["k"] = "callStatic";
                node["ownerType"] = TypeJson.Fqn(target.PhysicalOwner);
                node["method"] = target.PhysicalName;
                KotlinPropertyAccessors.PreserveCallIdentity(node, plan.SourceName, plan.Kind);
                node["sig"] = plan.Kind == "set" && target.ValueType != null ? new JsonArray(JsonNode.Parse(target.ValueType)) : new JsonArray();
                node["args"] = value == null ? new JsonArray() : new JsonArray(value);
                break;
        }
        node.Remove("companionReceiver");
    }

    static Binding Resolve(
        string owner,
        string receiver,
        string kind,
        string sourceName,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs,
        string nodeKind)
    {
        if (TryResolve(owner, receiver, kind, sourceName, bindings, refs, out var binding)) return binding;
        throw Missing(owner, receiver, kind, sourceName, nodeKind);
    }

    static bool TryResolve(
        string owner,
        string receiver,
        string kind,
        string sourceName,
        IReadOnlyDictionary<string, Binding> bindings,
        ReferenceMetadataIndex refs,
        out Binding binding)
    {
        binding = null;
        if (owner == null || sourceName == null) return false;
        if (bindings.TryGetValue(Key(owner, receiver, kind, sourceName), out binding)) return true;
        if (!refs.TryCompanionExtensionMember(owner, receiver, kind, sourceName, out var physical)) return false;
        binding = new Binding(physical, owner);
        return true;
    }

    static void RewriteStaticOwner(JsonObject node, string physicalOwner)
    {
        if (node["calleeOwner"] is not null) node["calleeOwner"] = TypeJson.Fqn(physicalOwner);
        else if (node["owner"] is not null) node["owner"] = TypeJson.Fqn(physicalOwner);
        else if (node["ownerType"] is not null) node["ownerType"] = TypeJson.Fqn(physicalOwner);
        else node["calleeOwner"] = TypeJson.Fqn(physicalOwner);
    }

    static InvalidOperationException Missing(
        string owner, string receiver, string kind, string sourceName, string nodeKind) =>
        new($"no trusted companion-extension binding for '{owner ?? "<missing>"}.{sourceName ?? "<missing>"}' " +
            $"(receiver {receiver}, kind {kind}, BIR node {nodeKind})");

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Put a Kotlin static member call onto the CLR owner axis.
//
// kotc reports a static member call in Kotlin vocabulary: `callStatic` naming the DECLARING TYPE in `ownerType`, with
// no `owner` at all. That is the whole frontend fact — `class C { companion { fun f() } }` resolves `C.f()` to a member
// of `C` that takes no receiver. Which CLR type physically hosts it is this layer's decision, and for a type declared
// in THIS compilation the answer is "that same type": the emitted TypeDef is the owner, so the declaring identity moves
// verbatim onto `owner`, the axis ilemit dispatches from.
//
// The discriminator is structural and local: this compilation declares a type of that name, and that declaration
// carries a static member of that name. Nothing else qualifies — a reference-KLIB or .NET owner is bound by
// NetInteropBinding / MemberCallSubstitution off the reference universe, and both of those key on the ownerless axis.
// Moving the token (rather than copying it) keeps ONE owner axis on the node, so the later generic-owner closure has a
// single key to rewrite and cannot leave the two spellings disagreeing.
//
// Runs per file, right after NetInteropBinding and before MemberCallSubstitution: the external binders have had first
// refusal, and the ownerless recognizers that follow must not see a call whose declaring type is already resolved.
static class LocalStaticOwnerBinding
{
    internal sealed class LocalStatics
    {
        public string PhysicalOwner;
        public readonly HashSet<string> Methods = new(System.StringComparer.Ordinal);
        public readonly Dictionary<string, string> PropertyAccessors = new(System.StringComparer.Ordinal);
    }

    /// The static members every type declared in this compilation carries, keyed by the declared type name.
    public static Dictionary<string, LocalStatics> Collect(IEnumerable<JsonNode> roots)
    {
        var index = new Dictionary<string, LocalStatics>(System.StringComparer.Ordinal);
        foreach (var root in roots) CollectInto(root, index);
        return index;
    }

    static void CollectInto(JsonNode node, Dictionary<string, LocalStatics> index)
    {
        if (node is not JsonObject obj || obj["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is string physicalName && type["methods"] is JsonArray methods)
            {
                // GenericStaticOwnerBinding has already moved a generic owner's companion-block declarations to its
                // non-generic carrier. Keep indexing them by the semantic owner kotc put on the call, while recording
                // the carrier as the exact CLR dispatch owner. Otherwise an ownerless recognizer can capture a local
                // `G.sqrt(Double)` as the unrelated top-level kotlin.math.sqrt before the late carrier rewrite runs.
                var name = type["staticCarrier"] is JsonObject carrier && Str(carrier["owner"]) is string semantic
                    ? semantic
                    : physicalName;
                LocalStatics statics = null;
                foreach (var method in methods.OfType<JsonObject>())
                    if (Bool(method["static"]) && Str(method["name"]) is string mn)
                    {
                        if (statics == null && !index.TryGetValue(name, out statics))
                            index[name] = statics = new LocalStatics { PhysicalOwner = physicalName };
                        else if (statics.PhysicalOwner != physicalName)
                            throw new System.InvalidOperationException(
                                $"multiple local static owners claim semantic type '{name}'");
                        statics.Methods.Add(mn);
                        if (KotlinPropertyAccessors.TryIdentity(method, out var propertyName, out var accessorKind))
                            statics.PropertyAccessors[propertyName + "\u001f" + accessorKind] = mn;
                    }
            }
            CollectInto(type, index);
        }
    }

    public static void Apply(JsonNode root, Dictionary<string, LocalStatics> index)
    {
        if (index.Count == 0) return;
        Walk(root, index);
    }

    static void Walk(JsonNode node, Dictionary<string, LocalStatics> index)
    {
        switch (node)
        {
            case JsonObject obj:
                Bind(obj, index);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, index);
                break;
            case JsonArray arr:
                foreach (var item in arr) if (item != null) Walk(item, index);
                break;
        }
    }

    static void Bind(JsonObject node, Dictionary<string, LocalStatics> index)
    {
        if (Str(node["k"]) != "callStatic" || node.ContainsKey("owner")) return;
        if (TypeJson.Read(node["ownerType"]) is not TypeNode.Fqn owner) return;
        if (Str(node["method"]) is not string method) return;
        if (!index.TryGetValue(owner.Name, out var statics)) return;
        // A static PROPERTY access carries the property IDENTITY plus a `prop` marker rather than a baked accessor
        // slot name, exactly as the ownerless axis does. Resolve it through the declared accessor index so a local
        // owner never has to travel through the ownerless recognizers — the accessor it names must itself be a
        // declared static of that type, so the marker cannot manufacture a member.
        var propertyAccess = Str(node["prop"]);
        var accessor = propertyAccess is "get" or "set"
            && statics.PropertyAccessors.TryGetValue(method + "\u001f" + propertyAccess, out var physicalAccessor)
                ? physicalAccessor : null;
        if (accessor != null)
        {
            KotlinPropertyAccessors.PreserveCallIdentity(node, method, propertyAccess);
            node.Remove("prop");
            node["method"] = accessor;
        }
        else if (propertyAccess is "get" or "set") return;
        else if (!statics.Methods.Contains(method)) return;
        // The declared parameter list doubles as the overload key, matching how every other resolved static callable
        // reaches ilemit.
        if (node["argTypes"] is JsonArray argTypes) node["sig"] ??= argTypes.DeepClone();
        node["owner"] = TypeJson.Fqn(statics.PhysicalOwner);
        node.Remove("ownerType");
    }

    static bool Bool(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}

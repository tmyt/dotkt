using System.Text.Json.Nodes;
using DotKt.Bir;

// Shared bridge between the BIR/CIR JSON tree (System.Text.Json.Nodes) and the structured DotKt.Bir.TypeNode model.
// Every satellite lowering pass that inspects/rewrites a type slot uses these — it reads the Type node NATIVELY
// (pattern-matching Fqn/Tv/Fn/Nullable/Array/ByRef), never re-serializing to a legacy string token to feed a scanner.
static class TypeJson
{
    // True iff a JSON value is a structured Type node (`{t:…}` with a `t` discriminator) — distinguishing a type slot
    // from a legacy type STRING (the m3-pending sig/accessOwner fields) or a k-tagged sub-node.
    public static bool IsType(JsonNode n) =>
        n is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s != null;

    // Read the structured Type out of a JSON slot (null when the slot is absent or is a legacy string, not an object).
    public static TypeNode Read(JsonNode n) => IsType(n) ? TypeNode.Parse(n.ToJsonString()) : null;

    // Write a Type back into a JSON node for storage in a slot.
    public static JsonNode Write(TypeNode t) => TypeNode.Write(t);

    // A bare-FQN convenience node (the common `Fqn(name)` slot value).
    public static JsonNode Fqn(string name) => TypeNode.Write(new TypeNode.Fqn(name));

    // The bare Kotlin/CLR FQN NAME an owner/type slot names (a member-call owner, a `new` owner, a cast target). Reads
    // the structured Fqn's Name directly (the pure identity), or accepts a legacy string slot verbatim; null otherwise.
    public static string OwnerName(JsonNode n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (Read(n) is TypeNode.Fqn f) return f.Name;
        return null;
    }
}

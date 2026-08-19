using System.Text.Json.Nodes;
using DotKt.Bir;

// Shared bridge between the BIR/CIR JSON tree (System.Text.Json.Nodes) and the structured DotKt.Bir.TypeNode model.
// Every satellite lowering pass that inspects/rewrites a type slot uses these — it reads the Type node NATIVELY
// (pattern-matching Fqn/Tv/Fn/Nullable/Array/ByRef).
static class TypeJson
{
    // True iff a JSON value is a structured Type node (`{t:…}` with a `t` discriminator).
    public static bool IsType(JsonNode n) =>
        n is JsonObject o && o["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) && s != null;

    // Read the structured Type out of a JSON slot (null when the slot is absent or not a Type node).
    public static TypeNode Read(JsonNode n) => IsType(n) ? TypeNode.Parse(n.ToJsonString()) : null;

    // Write a Type back into a JSON node for storage in a slot.
    public static JsonNode Write(TypeNode t) => TypeNode.Write(t);

    // A bare-FQN convenience node (the common `Fqn(name)` slot value).
    public static JsonNode Fqn(string name) => TypeNode.Write(new TypeNode.Fqn(name));

    // The bare Kotlin/CLR FQN NAME an owner/type slot names (a member-call owner, a `new` owner, a cast target).
    public static string OwnerName(JsonNode n) => Read(n) is TypeNode.Fqn f ? f.Name : null;
}

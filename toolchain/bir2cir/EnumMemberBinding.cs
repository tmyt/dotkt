using System.Text.Json.Nodes;

// GENERIC-ENUM member binding (C2 boxed-primitive dual-representation, `fun <T : Enum<T>> …`).
//
// A Kotlin `enum class` is emitted as a real CLR `System.Enum`-backed enum, and kotc lowers a member access on a
// CONCRETE enum receiver directly (`Color.RED.name` -> `objMethod ToString`, `.ordinal` -> `enumOrdinal`). But on a
// GENERIC receiver `e: T` (`T : Enum<T>`) kotc cannot see the enum-ness, so it emits a plain
// `callInstance ownerType=kotlin.Enum method=get_name` on the stdlib base class `kotlin.Enum` — which the app never
// emits (it lives in the stdlib) -> `TypeLoadException: kotlin.Enum\`1`. `kotlin.Enum` is NOT @ClrTypeAlias'd (so
// MemberCallSubstitution never touches it), yet on the CLR every enum value IS a `System.Enum`. Rebind the member call
// to the `System.Enum` operation: `name`/`toString` -> `System.Enum.ToString()` (returns the declared constant name),
// mirroring the concrete-receiver lowering. The receiver is a value-type `gp:T`; `objMethod` boxes it first.
//
// Non-ref builds only (the ref/rt stdlib keeps `kotlin.Enum`'s own pure-Kotlin member bodies untouched). Scoped to the
// `kotlin.Enum` owner so no other call is affected.
static class EnumMemberBinding
{
    public static void Apply(JsonNode root) => Walk(root);

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                Rebind(obj);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it);
                break;
        }
    }

    static void Rebind(JsonObject obj)
    {
        if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || k != "callInstance") return;
        if ((obj["ownerType"] as JsonValue)?.TryGetValue<string>(out var owner) != true) return;
        var bare = owner.StartsWith("@") ? owner[1..] : owner;
        var br = bare.IndexOf('[');
        if (br >= 0) bare = bare[..br];
        if (bare != "kotlin.Enum") return;
        if ((obj["method"] as JsonValue)?.TryGetValue<string>(out var method) != true) return;
        if (obj["recv"] is not JsonNode recv) return;
        // `name` (get_name) / `toString` -> System.Enum.ToString() = the declared constant name. objMethod boxes the
        // value-type `gp:T` receiver, then calls the object-slot virtual (System.Enum overrides ToString to the name).
        if (method is "get_name" or "toString" or "get_toString")
        {
            var newNode = new JsonObject
            {
                ["k"] = "objMethod",
                ["method"] = "ToString",
                ["recv"] = recv.DeepClone(),
            };
            foreach (var key in new[] { "ownerType", "virtual", "method", "args", "sig", "dynRet", "retType", "overrides", "recv" })
                obj.Remove(key);
            foreach (var kv in newNode) obj[kv.Key] = kv.Value.DeepClone();
        }
    }
}

using System.Collections.Generic;
using System.Text.Json.Nodes;

// ENUM inherited-member binding (C2 boxed-primitive dual-representation).
//
// A BASIC Kotlin `enum class` (constants only — no ctor params / user methods / per-entry bodies) is emitted as a real
// CLR value-type `enum` (`kind:"enum"`), which does NOT declare its own `ToString`/`GetHashCode`/`Equals` — it INHERITS
// them from `System.Enum`/`System.ValueType`/`System.Object`. Two receiver shapes reach a `System.Enum` member here and
// both would otherwise dead-end at a method the concrete enum type does not declare:
//
//  (1) CONCRETE receiver (`E.A.toString()`, `#90`): kotc emits `callInstance ownerType=E method=toString anySlot:true`
//      (the static receiver type is the concrete enum). ObjectSlotRename renames `toString`->`ToString` but keeps
//      owner `E` — so ilemit's `FindMethod("E","ToString")` fails ("method E.ToString not found"). Rebind to an
//      `objMethod` (box the value-type receiver, then the `System.Object` virtual slot) — `System.Enum` overrides
//      `ToString`/`Equals`/`GetHashCode`, so virtual dispatch yields the correct result. (kotc already lowers `.name`
//      -> `objMethod ToString` and `.ordinal` -> `enumOrdinal`, and `compareTo`/`==` to ordinal arithmetic, so those
//      concrete accesses never hit this gap.)
//
//  (2) GENERIC receiver (`e: T`, `T : Enum<T>`): kotc cannot see the enum-ness, so it emits a plain
//      `callInstance ownerType=kotlin.Enum method=get_name` on the stdlib base class `kotlin.Enum` — which the app
//      never emits (it lives in the stdlib) -> `TypeLoadException: kotlin.Enum\`1`. `kotlin.Enum` is NOT
//      @ClrTypeAlias'd, yet on the CLR every enum value IS a `System.Enum`. Rebind `name`/`toString` ->
//      `System.Enum.ToString()` (the declared constant name), mirroring the concrete-receiver lowering.
//
// Non-ref builds only (the ref/rt stdlib keeps `kotlin.Enum`'s own pure-Kotlin member bodies untouched). Scoped to the
// `kotlin.Enum` owner and to locally-declared value-type enum owners, so no other call is affected.
static class EnumMemberBinding
{
    // BCL Object-slot spellings (ObjectSlotRename runs first, so `anySlot` calls already carry these), and the arity of
    // each as an `objMethod` — ToString/GetHashCode take no arg, Equals takes one (moved from args[0] to `arg`).
    static readonly HashSet<string> ObjectSlots = new(System.StringComparer.Ordinal) { "ToString", "GetHashCode", "Equals" };

    // Collect the local BASIC (value-type) enum type names — a `kind:"enum"` decl — across ALL input files. The set
    // MUST span the whole compilation, not one .bir.json: kotc emits one file per source, but a call site
    // (`E.A.toString()`) may live in a DIFFERENT file from the `enum class E` declaration, while ilemit's emitted-type
    // table is assembly-wide (a same-assembly cross-file `E.ToString` still dead-ends). A RICH enum is `kind:"class"` +
    // `enumRich:true` (a singleton-field reference class that DOES synthesize its own toString) — deliberately excluded.
    public static HashSet<string> CollectBasicEnums(IEnumerable<JsonNode> roots)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var root in roots)
            if (root is JsonObject ro && ro["types"] is JsonArray ts)
                foreach (var t in ts)
                    if (t is JsonObject to && (to["kind"] as JsonValue)?.TryGetValue<string>(out var kind) == true
                        && kind == "enum" && (to["name"] as JsonValue)?.TryGetValue<string>(out var n) == true)
                        set.Add(n);
        return set;
    }

    public static void Apply(JsonNode root, HashSet<string> basicEnums) => Walk(root, basicEnums);

    static void Walk(JsonNode node, HashSet<string> basicEnums)
    {
        switch (node)
        {
            case JsonObject obj:
                Rebind(obj, basicEnums);
                foreach (var kv in obj) if (kv.Value != null) Walk(kv.Value, basicEnums);
                break;
            case JsonArray arr:
                foreach (var it in arr) if (it != null) Walk(it, basicEnums);
                break;
        }
    }

    static void Rebind(JsonObject obj, HashSet<string> basicEnums)
    {
        if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) != true || k != "callInstance") return;
        if ((obj["method"] as JsonValue)?.TryGetValue<string>(out var method) != true) return;
        if (obj["recv"] is not JsonNode recv) return;
        var owner = TypeJson.OwnerName(obj["ownerType"]);

        // (2) GENERIC receiver: `kotlin.Enum.name`/`.toString` -> objMethod ToString (= the declared constant name).
        if (owner == "kotlin.Enum" && method is "get_name" or "toString" or "get_toString")
        {
            ToObjMethod(obj, recv, "ToString", arg: null);
            return;
        }
        // `T : Enum<T>`.ordinal has no System.Enum property slot. Preserve Kotlin's declaration-index semantics
        // explicitly: enumOrdinal over the receiver's lexical T asks ilemit only to emit the CIR-authored generic
        // enum reflection sequence. This must happen before generic constrained-member binding; otherwise the bare
        // Kotlin owner is mistaken for a real `System.Enum.get_ordinal` MemberRef.
        if (owner == "kotlin.Enum" && method is "get_ordinal" or "ordinal")
        {
            var ordinal = new JsonObject
            {
                ["k"] = "enumOrdinal",
                ["e"] = recv.DeepClone(),
            };
            if (recv is JsonObject receiver
                && (receiver["sty"]?.DeepClone() ?? receiver["ret"]?.DeepClone()) is JsonNode receiverType)
                ordinal["type"] = receiverType;
            obj.Clear();
            foreach (var kv in ordinal) obj[kv.Key] = kv.Value?.DeepClone();
            return;
        }

        // (1) CONCRETE receiver: a `System.Enum`-inherited Object slot on a local value-type enum owner that declares
        // none of them. Box + `System.Object` virtual slot; System.Enum's override supplies name/equality/hash.
        if (owner != null && basicEnums.Contains(owner) && ObjectSlots.Contains(method))
        {
            // Object-slot arity gate (matches EmitObjMethod): Equals takes exactly one arg (moved args[0] -> `arg`),
            // ToString/GetHashCode take none. A mismatch is unreachable (a `kind:"enum"` declares no members), but a
            // stray shape should fall through to the original `callInstance` (a clear "method not found") rather than
            // be miscast into an `objMethod` EmitObjMethod can't service.
            var args = obj["args"] as JsonArray;
            var argc = args?.Count ?? 0;
            if (method == "Equals" ? argc == 1 : argc == 0)
                ToObjMethod(obj, recv, method, method == "Equals" ? args[0] : null);
        }
    }

    // Rewrite `obj` in place into an `objMethod` node (ilemit boxes a value-type receiver, then callvirt the Object slot).
    static void ToObjMethod(JsonObject obj, JsonNode recv, string method, JsonNode arg)
    {
        var newNode = new JsonObject
        {
            ["k"] = "objMethod",
            ["method"] = method,
            ["recv"] = recv.DeepClone(),
        };
        if (arg != null) newNode["arg"] = arg.DeepClone();
        foreach (var key in new[] { "ownerType", "virtual", "method", "args", "arg", "sig", "dynRet", "ret", "overrides", "recv" })
            obj.Remove(key);
        foreach (var kv in newNode) obj[kv.Key] = kv.Value.DeepClone();
    }
}

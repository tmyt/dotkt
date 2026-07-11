using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A `for (x in seq)` over a Kotlin `Sequence` (@ClrTypeAlias IEnumerable) is kotc-lowered to `forEachInline`, which
// ilemit emits as a TYPED `IEnumerable<elem>::GetEnumerator` dispatch. But the object `sequence { .. }` returns
// (`dotkt_obj*` — the lifted anon Sequence) has NO type params of its OWN yet declares its `IEnumerable<T>` interface
// referencing the ENCLOSING `sequence<T>` method's type param (`tv scope=method`), so it is erased to
// `IEnumerable<object>` at runtime. The typed `IEnumerable<string>::GetEnumerator` slot the app then dispatches is
// therefore absent -> System.EntryPointNotFoundException (cases/il-seqforin).
//
// The variance-immune fix (the same non-generic escape hatch StarProjectionCountLowering / StarProjectionLowering use
// for reification/variance mismatches): dispatch the enumeration through the NON-generic `System.Collections.IEnumerable`
// / `IEnumerator` — which EVERY `IEnumerable<T>` implements regardless of the erased element — and cast each
// `get_Current` (object) to the loop element type. This keeps `elem` for the yielded-value cast (object -> string /
// unbox to Int) while aligning the GetEnumerator dispatch with the erased runtime shape.
//
// Rewrites the forEachInline in place into a `block`:
//     var $seqIt$N : System.Collections.IEnumerator = (<src>).GetEnumerator()          // non-generic
//     while ($seqIt$N.MoveNext()) { var <x> : elem = (elem) $seqIt$N.get_Current(); <body...> }
// The `while` registers a loop label (break/continue keep working; the labeled-loop `label` is preserved). SCOPED to a
// `src` whose static Kotlin type is `kotlin.sequences.Sequence` (only the erased-anon-object case) — a `for` over a real
// List/Set keeps its faster typed IEnumerable<elem> path. Non-ref builds only; runs BEFORE BirTypeLowering (the Sequence
// FQN is still in the source vocabulary, and the emitted System.Collections.* / elem tokens flow through lowering).
static class SequenceForEachLowering
{
    static int _tmp;

    public static void Apply(JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (Str(o["k"]) == "forEachInline" && o["src"] is JsonObject src && IsSequenceTyped(src))
                Rewrite(o, src);
            foreach (var kv in o) if (kv.Value != null) Apply(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var it in a) if (it != null) Apply(it);
    }

    static void Rewrite(JsonObject fe, JsonObject src)
    {
        var id = System.Threading.Interlocked.Increment(ref _tmp);
        var itName = "$seqIt$" + id;
        var elem = fe["elem"];
        var loopVar = Str(fe["var"]);
        var body = fe["body"] as JsonArray ?? new JsonArray();
        var label = fe["label"];

        // var $seqIt$N = ((System.Collections.IEnumerable)src).GetEnumerator()  (non-generic — variance-immune)
        var enumVar = new JsonObject
        {
            ["k"] = "var", ["name"] = itName, ["type"] = TypeJson.Fqn("System.Collections.IEnumerator"),
            ["init"] = new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IEnumerable"),
                ["method"] = "GetEnumerator", ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = TypeJson.Fqn("System.Collections.IEnumerator"),
                ["recv"] = new JsonObject { ["k"] = "cast", ["type"] = TypeJson.Fqn("System.Collections.IEnumerable"), ["e"] = src.DeepClone() },
            },
        };

        // var <x> : elem = (elem) $seqIt$N.get_Current();  (object -> elem cast; ilemit unbox.any for a value elem)
        var bindVar = new JsonObject
        {
            ["k"] = "var", ["name"] = loopVar, ["type"] = elem?.DeepClone(),
            ["init"] = new JsonObject
            {
                ["k"] = "cast", ["type"] = elem?.DeepClone(),
                ["e"] = new JsonObject
                {
                    ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IEnumerator"),
                    ["method"] = "get_Current", ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                    ["ret"] = TypeJson.Fqn("System.Object"),
                    ["recv"] = new JsonObject { ["k"] = "local", ["name"] = itName },
                },
            },
        };

        var whileBody = new JsonArray { bindVar };
        foreach (var b in body) whileBody.Add(b?.DeepClone());

        var whileStmt = new JsonObject
        {
            ["k"] = "while",
            ["cond"] = new JsonObject
            {
                ["k"] = "clrInstance", ["type"] = TypeJson.Fqn("System.Collections.IEnumerator"),
                ["method"] = "MoveNext", ["argTypes"] = new JsonArray(), ["args"] = new JsonArray(),
                ["ret"] = TypeJson.Fqn("System.Boolean"),
                ["recv"] = new JsonObject { ["k"] = "local", ["name"] = itName },
            },
            ["body"] = whileBody,
        };
        if (label != null) whileStmt["label"] = label.DeepClone();

        // In-place: the forEachInline sits in a statement body array; recast it as a `block` of [enumVar, while].
        foreach (var key in fe.Select(kv => kv.Key).ToList()) fe.Remove(key);
        fe["k"] = "block";
        fe["body"] = new JsonArray { enumVar, whileStmt };
    }

    // The non-generic BCL interfaces StarProjectionLowering rewrites a star-projected/erased collection `cast` onto
    // (#74b) — a `for`-loop source landing here already wearing one of these needs the SAME non-generic dispatch a
    // Sequence does (its underlying runtime value, e.g. a `List<int>`, has no typed `IEnumerable<object>` slot).
    static readonly HashSet<string> NonGenericIfaces = new(System.StringComparer.Ordinal)
    {
        "System.Collections.ICollection", "System.Collections.IList",
        "System.Collections.IEnumerable", "System.Collections.IDictionary",
    };

    // True iff the src expression's static type is `kotlin.sequences.Sequence` (the erased-anon-object Sequence
    // case), OR (#74b) it is a `cast` to a star-projected/erased collection — either ALREADY rewritten to a
    // non-generic BCL interface by StarProjectionLowering (Phase 1, runs BEFORE this pass — #74b(i)) or
    // (defensively, in case ordering ever changes) still the raw star-projected `kotlin.collections.*` alias.
    static bool IsSequenceTyped(JsonObject src)
    {
        foreach (var key in new[] { "ret", "dynRet", "type" })
            if (src[key] is JsonNode n && Unwrap(TypeJson.Read(n)) is TypeNode.Fqn f && f.Name == "kotlin.sequences.Sequence")
                return true;
        if (Str(src["k"]) == "cast" && TypeJson.Read(src["type"]) is TypeNode castType)
        {
            if (castType is TypeNode.Fqn { Args: null } cf && NonGenericIfaces.Contains(cf.Name)) return true;
            if (FaithfulHints.IsStarProjectedColl(castType)) return true;
        }
        return false;
    }

    static TypeNode Unwrap(TypeNode t) => t is TypeNode.Nullable nu ? Unwrap(nu.Of) : t;

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}

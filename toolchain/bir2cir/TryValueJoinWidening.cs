using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A VALUE-POSITION `try` JOIN THAT ONE BRANCH LEAVES NULL (#86 §3, relocated from kotc).
//
// `try { … } catch { null }` in value position becomes a temp local the branches assign and the block reads back, and
// the temp's declared type is the join's type. When the frontend resolved that join to a bare non-null VALUE type
// while a branch still yields a literal `null`, the temp cannot be that type: `null` into a `Nullable<V>` slot
// materializes as `HasValue=false`, but into a bare `int32` slot it is a reference stored over a value — the
// raw-Nullable/InvalidProgram miscompile class. The join is therefore widened to `Nullable<V>`.
//
// The shape is real and not hypothetical: a substituted generic or a spliced stdlib inline body (`takeIf`,
// `firstOrNull`) resolves the join to `Int` with the `?` living on the function's own return, so the branch types and
// the join type genuinely disagree in the IR the frontend hands over.
//
// THIS IS A CLR-REPRESENTATION DECISION AND SO IT LIVES HERE, not in kotc, which decides no physical slot. It also
// stops asking a hardcoded primitive/unsigned list and asks the struct-ness oracle instead: the reason a bare slot
// cannot hold the null is that the slot is a VALUE type, which is as true of a `value class` or a BCL struct as of
// `Int`, and the oracle is the same one every other erasure decision in this layer consults.
//
// AS NARROW AS THE SHAPE IT GUARDS. Only a branch's LAST statement counts, and only a bare `null` constant there — a
// `null` wrapped in a cast or one block deeper is not the form ilemit materializes as an empty `Nullable<V>`, so it
// must not arm the widening, and a branch that throws or returns yields nothing at all. Runs before type lowering,
// while the join type is still the Kotlin `kotlin.Int`.
static class TryValueJoinWidening
{
    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        Walk(root, isValue);
    }

    static void Walk(JsonNode node, Func<string, bool> isValue)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var e in a) Walk(e, isValue);
                return;
            case JsonObject o:
                if (Str(o["k"]) == "valueBlock") Widen(o, isValue);
                foreach (var kv in o) Walk(kv.Value, isValue);
                return;
        }
    }

    static void Widen(JsonObject block, Func<string, bool> isValue)
    {
        if (block["stmts"] is not JsonArray stmts) return;
        for (var i = 0; i + 1 < stmts.Count; i++)
        {
            if (stmts[i] is not JsonObject decl || Str(decl["k"]) != "var") continue;
            if (stmts[i + 1] is not JsonObject tryNode || Str(tryNode["k"]) != "try") continue;
            if (Str(decl["name"]) is not string tv) continue;
            if (TypeJson.Read(decl["type"]) is not TypeNode.Fqn { Args: null } t || !isValue(t.Name)) continue;
            if (!YieldsNull(tryNode["body"], tv) && !Catches(tryNode).Any(c => YieldsNull(c, tv))) continue;
            decl["type"] = TypeJson.Write(new TypeNode.Nullable(t));
        }
    }

    static System.Collections.Generic.IEnumerable<JsonNode> Catches(JsonObject tryNode) =>
        (tryNode["catches"] as JsonArray)?.OfType<JsonObject>().Select(c => c["body"])
        ?? Enumerable.Empty<JsonNode>();

    // True iff this branch's RESULT — its last statement — is a bare `null` constant. Two forms, because the value's
    // own type decides whether it is worth storing: a bare `null` literal is typed `Nothing?`, which the emitter
    // leaves as a plain expression statement and so the temp keeps its DEFAULT — which is precisely why the default
    // has to be `HasValue=false` and not `0`. A null that arrived carrying a real type is stored instead. Only the
    // last statement, and only a `const` directly: a null under a cast or one block deeper is a different shape that
    // ilemit does not materialize as an empty `Nullable<V>`, so it must not arm the widening.
    static bool YieldsNull(JsonNode body, string tv)
    {
        if (body is not JsonArray a || a.Count == 0 || a[^1] is not JsonObject last) return false;
        return Str(last["k"]) switch
        {
            "exprStmt" => IsNullConst(last["expr"]),
            "setLocal" => Str(last["name"]) == tv && IsNullConst(last["value"]),
            _ => false,
        };
    }

    // A JSON null reaches the node model as an ABSENT child, so the key must be present and the child must be null.
    static bool IsNullConst(JsonNode n)
        => n is JsonObject o && Str(o["k"]) == "const" && o.ContainsKey("value") && o["value"] is null;

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

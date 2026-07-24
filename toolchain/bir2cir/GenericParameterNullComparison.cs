using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A cross-module inline payload can carry Kotlin identity `T === null` as raw `{k:binOp,op:"=="}` after its local
// declaration has been spliced into the caller. ECMA `ceq` cannot compare an unboxed generic-parameter stack value
// with null, even for a class-bounded T. Lower that one null-specific shape to objEq, whose physical representation
// boxes T before the null test. Non-null `T === T` remains raw ceq (objEq would incorrectly call Equals).
static class GenericParameterNullComparison
{
    public static void Apply(JsonNode root) => VisitDeclarations(root);

    static void VisitDeclarations(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        if (obj["body"] is JsonArray body && obj["params"] is JsonArray)
        {
            var vars = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            void Record(JsonObject declaration)
            {
                if (Str(declaration["name"]) is not string name
                    || TypeJson.Read(declaration["type"]) is not TypeNode type) return;
                if (vars.TryGetValue(name, out var prior) && prior != type)
                {
                    ambiguous.Add(name);
                    vars.Remove(name);
                }
                else if (!ambiguous.Contains(name)) vars[name] = type;
            }
            foreach (var p in (obj["params"] as JsonArray).OfType<JsonObject>()) Record(p);
            CollectVars(body, Record);
            Rewrite(body, vars);
        }

        if (obj["types"] is JsonArray types)
            foreach (var type in types.OfType<JsonObject>()) VisitDeclarations(type);
        if (obj["methods"] is JsonArray methods)
            foreach (var method in methods.OfType<JsonObject>()) VisitDeclarations(method);
        if (obj["ctors"] is JsonArray ctors)
            foreach (var ctor in ctors.OfType<JsonObject>()) VisitDeclarations(ctor);
    }

    static void CollectVars(JsonNode node, Action<JsonObject> record)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) == "var") record(o);
                foreach (var kv in o)
                    if (kv.Value != null) CollectVars(kv.Value, record);
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item != null) CollectVars(item, record);
                break;
        }
    }

    static void Rewrite(JsonNode node, IReadOnlyDictionary<string, TypeNode> vars)
    {
        switch (node)
        {
            case JsonObject o:
                if (Str(o["k"]) == "binOp" && Str(o["op"]) == "=="
                    && ((IsNull(o["lhs"]) && IsGenericLocal(o["rhs"], vars))
                        || (IsNull(o["rhs"]) && IsGenericLocal(o["lhs"], vars))))
                {
                    var lhs = o["lhs"]?.DeepClone();
                    var rhs = o["rhs"]?.DeepClone();
                    o.Clear();
                    o["k"] = "objEq";
                    o["lhs"] = lhs;
                    o["rhs"] = rhs;
                }
                foreach (var kv in o)
                    if (kv.Value != null) Rewrite(kv.Value, vars);
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item != null) Rewrite(item, vars);
                break;
        }
    }

    static bool IsGenericLocal(JsonNode node, IReadOnlyDictionary<string, TypeNode> vars) =>
        node is JsonObject o && Str(o["k"]) == "local" && Str(o["name"]) is string name
        // The expression surface may already have been erased to an existential/object
        // by an earlier Kotlin-to-CLR binding pass. The emitted local slot, however, is
        // governed by its declaration type, so retain either positive generic fact.
        && (IsGeneric(TypeJson.Read(o["sty"])) || IsGeneric(vars.GetValueOrDefault(name)));

    // Kotlin keeps a nullable use of a type parameter as `T?` in BIR even when the
    // declaration's bound later proves to be a reference type. Both shapes still
    // occupy the CLR generic-parameter stack category at this point.
    static bool IsGeneric(TypeNode type) =>
        type is TypeNode.Tv
        || type is TypeNode.Nullable { Of: TypeNode.Tv }
        || type is TypeNode.Oblivious { Of: TypeNode.Tv };

    static bool IsNull(JsonNode node) =>
        node is JsonObject o && Str(o["k"]) == "const" && o["value"] == null;

    static string Str(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

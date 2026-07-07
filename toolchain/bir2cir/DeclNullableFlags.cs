using System;
using System.Text.Json.Nodes;
using DotKt.Bir;

// DECL-position NRT-byte collection (#37/#48 nullability fold). Runs on the SEMANTIC BIR (kotlin.* type tokens, the
// `{t:nullable}` reference wrappers still present) — AFTER the object-erasure passes but BEFORE BirTypeLowering strips
// the reference `?` wrappers to bare types. For every declaration slot whose Type node carries a nullable REFERENCE
// position, it emits the flattened `NullableAttribute` byte array that ilemit stamps in place of the retired scalar
// `"nullable"` / `"retNullable"` decl flags:
//   * a method  -> `retNullableFlags` (its `ret` node)         [ilemit: Program.cs return-param path]
//   * a param   -> `nullableFlags`    (its `type` node)        [ilemit: DefineParamNames]
//   * a field / property -> `nullableFlags` (its `type` node)  [forward-compat; harmless if unconsumed]
//
// A VALUE `T?` (`Nullable<Int>`) contributes NO byte (it is the structural Nullable<T>, kept by BirTypeLowering); a
// non-null reference emits nothing (the type's [NullableContext(1)] default covers it) — only a nullable reference
// position yields an override array. NEVER overwrites a flags key already set (SuspendColdLowering's synthesized
// Task-bridge sets its own `retNullableFlags` up-front and must win).
static class DeclNullableFlags
{
    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        if (root is JsonObject o) ApplyRec(o, isValue);
    }

    static void ApplyRec(JsonObject o, Func<string, bool> isValue)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo) ApplyToMethod(mo, isValue);
        ApplyToDecls(o["fields"], isValue);
        ApplyToDecls(o["properties"], isValue);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to, isValue);
    }

    static void ApplyToMethod(JsonObject mo, Func<string, bool> isValue)
    {
        if (!mo.ContainsKey("retNullableFlags")
            && TypeJson.Read(mo["ret"]) is TypeNode ret
            && NullableFlags.Compute(ret, isValue) is JsonArray rf)
            mo["retNullableFlags"] = rf;
        ApplyToDecls(mo["params"], isValue);
    }

    // Stamp `nullableFlags` on each declaration in a params/fields/properties array whose Type node carries a nullable
    // reference position (and that lacks the key already).
    static void ApplyToDecls(JsonNode arr, Func<string, bool> isValue)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject po
                && !po.ContainsKey("nullableFlags")
                && TypeJson.Read(po["type"]) is TypeNode t
                && NullableFlags.Compute(t, isValue) is JsonArray f)
                po["nullableFlags"] = f;
    }
}

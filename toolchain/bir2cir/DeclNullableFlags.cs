using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// DECL-position NRT-byte collection (#37/#48 nullability fold). Runs on the SEMANTIC BIR (kotlin.* type tokens, the
// `{t:nullable}` reference wrappers still present) — AFTER the object-erasure passes but BEFORE BirTypeLowering strips
// the reference `?` wrappers to bare types. For every declaration slot whose Type node carries a nullable REFERENCE
// position, it emits the flattened `NullableAttribute` byte array in place of the retired scalar `"nullable"` /
// `"retNullable"` decl flags:
//   * a method            -> `retNullableFlags` (its `ret` node)
//   * a method param      -> `nullableFlags`    (its `type` node)
//   * a CONSTRUCTOR param -> `nullableFlags`    (its `type` node)
//   * a field / property  -> `nullableFlags`    (its `type` node)
//
// The CONSUMER is RoundtripMetadata (Stamp), which turns each flags key into a real `[Nullable]` entry in the decl's
// `attrs`/`retAttrs` array; ilemit then stamps those entries through its generic BuildCab path and never reads the
// flags keys itself. Producer and consumer traversals must therefore agree on the decl kinds they visit — a slot this
// pass skips silently loses its `[Nullable]` at stamp time (the #251 ctor bug). RoundtripMetadata.StampType visits
// methods / fields / properties / ctors, so ApplyRec visits the same four; StampType additionally skips a
// `kind:"enum"` type outright (a real CLR enum has no ctors and no nullable slot, so nothing is stamped there).
//
// A VALUE `T?` (`Nullable<Int>`) contributes NO byte (it is the structural Nullable<T>, kept by BirTypeLowering); a
// non-null reference emits nothing (the type's [NullableContext(1)] default covers it) — only a nullable reference
// position yields an override array. NEVER overwrites a flags key already set (SuspendColdLowering's synthesized
// Task-bridge sets its own `retNullableFlags` up-front and must win).
static class DeclNullableFlags
{
    public static void Apply(JsonNode root, ValueTypeOracle isValue)
    {
        if (root is JsonObject o) ApplyRec(o, isValue);
    }

    static void ApplyRec(JsonObject o, ValueTypeOracle isValue)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo) ApplyToMethod(mo, isValue);
        // A ctor decl has params but no `ret` (BirEmitterDeclarations.ctor), so its params are stamped directly
        // rather than through ApplyToMethod.
        if (o["ctors"] is JsonArray ctors)
            foreach (var c in ctors)
                if (c is JsonObject co) ApplyToDecls(co["params"], isValue);
        ApplyToDecls(o["fields"], isValue);
        ApplyToDecls(o["properties"], isValue);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to, isValue);
    }

    static void ApplyToMethod(JsonObject mo, ValueTypeOracle isValue)
    {
        PreserveNullableUnitSurface(mo, "ret", "retKotlinType", "nullableGenericRet");
        if (!mo.ContainsKey("retNullableFlags")
            && TypeJson.Read(mo["ret"]) is TypeNode ret
            && NullableFlags.Compute(ret, isValue) is JsonArray rf)
            mo["retNullableFlags"] = rf;
        ApplyToDecls(mo["params"], isValue);
    }

    // Stamp `nullableFlags` on each declaration in a params/fields/properties array whose Type node carries a nullable
    // reference position (and that lacks the key already).
    static void ApplyToDecls(JsonNode arr, ValueTypeOracle isValue)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject po)
            {
                PreserveNullableUnitSurface(po, "type", "kotlinType", "nullableGeneric");
                if (!po.ContainsKey("nullableFlags")
                && TypeJson.Read(po["type"]) is TypeNode t
                && NullableFlags.Compute(t, isValue) is JsonArray f)
                    po["nullableFlags"] = f;
            }
    }

    // Unit has no NRT byte in the Kotlin projection (including nested generic positions). Its nullable
    // source contract therefore uses the existing exact KotlinType carrier, not a guessed CLR signature.
    // Earlier representation passes may already own a more original source surface; never replace it.
    static void PreserveNullableUnitSurface(JsonObject slot, string key, string carrier, string genericCarrier)
    {
        if (slot[carrier] != null || slot[genericCarrier] != null) return;
        if (TypeJson.Read(slot[key]) is TypeNode type && ContainsNullableUnit(type))
            slot[carrier] = TypeNode.ToJson(type);
    }

    static bool ContainsNullableUnit(TypeNode type) => type switch
    {
        TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Unit", Args: null } } => true,
        TypeNode.Nullable n => ContainsNullableUnit(n.Of),
        TypeNode.Oblivious o => ContainsNullableUnit(o.Of),
        TypeNode.Projection p => ContainsNullableUnit(p.Of),
        TypeNode.Fqn f => f.Args?.Any(ContainsNullableUnit) == true,
        TypeNode.Array a => ContainsNullableUnit(a.Elem),
        TypeNode.ByRef b => ContainsNullableUnit(b.Of),
        TypeNode.Fn f => ContainsNullableUnit(f.Ret) || f.Params.Any(ContainsNullableUnit)
            || (f.Recv != null && ContainsNullableUnit(f.Recv)) || f.Ctx?.Any(ContainsNullableUnit) == true,
        _ => false,
    };
}

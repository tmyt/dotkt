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
// The physical-head query determines which generic arguments still occupy signature positions, without
// discarding the semantic annotation wrappers on surviving arguments. It uses the current build's representation.
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
    public static void Apply(JsonNode root, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
    {
        if (root is JsonObject o) ApplyRec(o, isValue, annotationArguments);
    }

    static void ApplyRec(JsonObject o, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
    {
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo) ApplyToMethod(mo, isValue, annotationArguments);
        // A ctor decl has params but no `ret` (BirEmitterDeclarations.ctor), so its params are stamped directly
        // rather than through ApplyToMethod.
        if (o["ctors"] is JsonArray ctors)
            foreach (var c in ctors)
                if (c is JsonObject co) ApplyToDecls(co["params"], isValue, annotationArguments);
        ApplyToDecls(o["fields"], isValue, annotationArguments);
        ApplyToDecls(o["properties"], isValue, annotationArguments);
        if (o["types"] is JsonArray types)
            foreach (var t in types) if (t is JsonObject to) ApplyRec(to, isValue, annotationArguments);
    }

    static void ApplyToMethod(JsonObject mo, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
    {
        PreserveExactSurface(mo, "ret", "retKotlinType", "nullableGenericRet");
        if (!mo.ContainsKey("retNullableFlags")
            && TypeJson.Read(mo["ret"]) is TypeNode ret
            && NullableFlags.Compute(ret, isValue, annotationArguments: annotationArguments) is JsonArray rf)
            mo["retNullableFlags"] = rf;
        ApplyToDecls(mo["params"], isValue, annotationArguments);
    }

    // Stamp `nullableFlags` on each declaration in a params/fields/properties array whose Type node carries a nullable
    // reference position (and that lacks the key already).
    static void ApplyToDecls(JsonNode arr, ValueTypeOracle isValue, Func<TypeNode.Fqn, TypeNode[]> annotationArguments)
    {
        if (arr is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject po)
            {
                PreserveExactSurface(po, "type", "kotlinType", "nullableGeneric");
                if (!po.ContainsKey("nullableFlags")
                && TypeJson.Read(po["type"]) is TypeNode t
                && NullableFlags.Compute(t, isValue, annotationArguments: annotationArguments) is JsonArray f)
                    po["nullableFlags"] = f;
            }
    }

    // Unit has no NRT byte, and function types carry only a head byte in the Kotlin NRT convention.
    // Preserve the exact source subtree for these positions before physical lowering removes its annotations.
    // Earlier representation passes may already own a more original source surface; never replace it.
    static void PreserveExactSurface(JsonObject slot, string key, string carrier, string genericCarrier)
    {
        if (slot[carrier] != null || slot[genericCarrier] != null) return;
        if (TypeJson.Read(slot[key]) is TypeNode type && RequiresExactSurface(type))
            slot[carrier] = TypeNode.ToJson(type);
    }

    static bool RequiresExactSurface(TypeNode type) => type switch
    {
        TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Unit", Args: null } } => true,
        TypeNode.Nullable n => RequiresExactSurface(n.Of),
        TypeNode.Oblivious o => RequiresExactSurface(o.Of),
        TypeNode.Projection p => RequiresExactSurface(p.Of),
        TypeNode.Fqn f => f.Args?.Any(RequiresExactSurface) == true,
        TypeNode.Array a => RequiresExactSurface(a.Elem),
        TypeNode.ByRef b => RequiresExactSurface(b.Of),
        TypeNode.Fn => true,
        _ => false,
    };
}

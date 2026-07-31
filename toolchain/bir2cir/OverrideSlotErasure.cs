using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using DotKt.Bir;

// ERASURE PROPAGATES FROM THE OVERRIDDEN SLOT, NOT FROM SYNTAX (#86 D3).
//
// `interface Sink<T> { fun accept(x: T?): String }` has its parameter object-erased like every other `Nullable(Tv)`
// slot, so the CLR slot an implementor must fill is `accept(object)` — at EVERY instantiation, because the erasure
// is a property of the DECLARATION and not of the type argument. `class TextSink : Sink<String>` writes
// `override fun accept(x: String?)`, which holds a CONCRETE type: there is no `Nullable(Tv)` anywhere in it, so no
// syntactic sweep can reach it, and left alone it emits `accept(string)` — a NEW OVERLOAD. The interface method
// stays unimplemented, and the type fails to load.
//
// So the override's physical slot is derived from what it overrides. This pass reads the pre-erasure declaration of
// each overridden member out of the same-compilation index and forces the overriding slot to `object` wherever the
// base slot is one. It must run BEFORE NullableGenericErasure: the recorder there keys on `Nullable(Tv)` and cannot
// see a concrete `String?`, so the Kotlin surface of the narrowed slot is recorded HERE, from the override's own
// declared type, on the same two channels every other erased slot uses — the `[KotlinNullableGeneric]` carrier and
// the slot's NRT byte.
static class OverrideSlotErasure
{
    static readonly TypeNode Obj = new TypeNode.Fqn("object");

    public static void Apply(JsonNode root, NullableTvErasureCallRealign.DeclIndex idx, Func<string, bool> isValue)
    {
        if (root is not JsonObject o) return;
        ApplyRec(o, idx, isValue);
    }

    static void ApplyRec(JsonObject o, NullableTvErasureCallRealign.DeclIndex idx, Func<string, bool> isValue)
    {
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    if (to["methods"] is JsonArray ms)
                        foreach (var m in ms)
                            if (m is JsonObject mo) ApplyToMethod(mo, idx, isValue);
                    ApplyRec(to, idx, isValue);
                }
    }

    static void ApplyToMethod(JsonObject mo, NullableTvErasureCallRealign.DeclIndex idx, Func<string, bool> isValue)
    {
        if (mo["overrides"] is not JsonArray overrides) return;
        var declParams = mo["params"] as JsonArray;
        foreach (var ov in overrides)
        {
            if (ov is not JsonObject oo) continue;
            if (TypeJson.OwnerName(oo["owner"]) is not string owner) continue;
            if ((oo["member"] as JsonValue)?.TryGetValue<string>(out var member) != true || member == null) continue;
            // An accessor's override entry names the PROPERTY; the emitted method is its `get_`/`set_`.
            var kind = (oo["kind"] as JsonValue)?.TryGetValue<string>(out var k) == true ? k : "method";
            var name = kind switch { "getter" => "get_" + member, "setter" => "set_" + member, _ => member };
            var arity = declParams?.Count ?? 0;
            if (!idx.ByOwner.TryGetValue(owner, out var sigs)) continue;      // a REFERENCED base: its slots are already physical
            if (!sigs.TryGetValue(name + "|" + arity, out var baseSig) || baseSig == null) continue;
            if (baseSig.Ret is TypeNode.Nullable { Of: TypeNode.Tv })
                EraseSlot(mo, "ret", "nullableGenericRet", "retNullableFlags", isValue);
            if (declParams == null || baseSig.Params.Length != declParams.Count) continue;
            for (var i = 0; i < declParams.Count; i++)
                if (baseSig.Params[i] is TypeNode.Nullable { Of: TypeNode.Tv } && declParams[i] is JsonObject po)
                    EraseSlot(po, "type", "nullableGeneric", "nullableFlags", isValue);
        }
    }

    // Force one slot to `object`, carrying its own pre-erasure Kotlin type across on the round-trip channels. A slot
    // already `object` (the base itself, or a second override entry naming the same member) is left alone so the
    // carrier is never overwritten with the erased form.
    static void EraseSlot(JsonObject decl, string typeKey, string factKey, string flagsKey, Func<string, bool> isValue)
    {
        if (TypeJson.Read(decl[typeKey]) is not TypeNode t) return;
        if (t is TypeNode.Fqn { Name: "object", Args: null }) return;
        decl[typeKey] = TypeJson.Write(Obj);
        decl[factKey] ??= TypeNode.ToJson(t);
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(t, isValue) is JsonArray f) decl[flagsKey] = f;
    }
}

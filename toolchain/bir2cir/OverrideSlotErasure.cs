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
            // A base declared in a REFERENCED assembly is not indexed here, so its overrides are not propagated —
            // the same cross-module reader gap that keeps the other referenced-declaration derivations out.
            if (!idx.ByOwner.TryGetValue(owner, out var sigs)) continue;
            if (!sigs.TryGetValue(name + "|" + arity, out var baseSig) || baseSig == null) continue;
            if (baseSig.Ret != null) EraseSlot(mo, "ret", "nullableGenericRet", "retNullableFlags", baseSig.Ret, isValue);
            if (declParams == null || baseSig.Params == null || baseSig.Params.Length != declParams.Count) continue;
            for (var i = 0; i < declParams.Count; i++)
                if (declParams[i] is JsonObject po)
                    EraseSlot(po, "type", "nullableGeneric", "nullableFlags", baseSig.Params[i], isValue);
        }
    }

    // Apply the BASE slot's erasure PATTERN to the overriding slot, carrying the override's own pre-erasure Kotlin
    // type across on the round-trip channels. The pattern, not a fixed `object`: the base's `Box<T?>` erases to
    // `Box<object>`, so an override's `Box<Int?>` must become `Box<object>` and NOT a bare `object` — only the
    // positions the base actually erased move, and every other position keeps the override's own concrete type
    // (`Pair<T, T?>` against `Pair<Int, Int?>` gives `Pair<int32, object>`). A slot the base did not erase at all is
    // left exactly as written.
    static void EraseSlot(JsonObject decl, string typeKey, string factKey, string flagsKey, TypeNode baseSlot,
        Func<string, bool> isValue)
    {
        if (TypeJson.Read(decl[typeKey]) is not TypeNode t) return;
        var erased = ApplyErasurePattern(baseSlot, NullableGenericErasure.EraseNullableTv(baseSlot), t);
        if (erased.Equals(t)) return;
        decl[typeKey] = TypeJson.Write(erased);
        decl[factKey] ??= TypeNode.ToJson(t);
        if (!decl.ContainsKey(flagsKey) && NullableFlags.Compute(t, isValue) is JsonArray f) decl[flagsKey] = f;
    }

    // Rewrite `derived` to `object` at exactly the positions where erasing `baseSlot` produced one, walking the two
    // base shapes in parallel. Where the shapes diverge — the override narrowed something the base did not name in a
    // matching position — the derived subtree is returned untouched rather than guessed at.
    static TypeNode ApplyErasurePattern(TypeNode baseSlot, TypeNode baseErased, TypeNode derived)
    {
        if (baseErased is TypeNode.Fqn { Name: "object", Args: null } && !baseSlot.Equals(baseErased)) return Obj;
        switch (baseSlot, baseErased, derived)
        {
            case (TypeNode.Fqn { Args: { } ba } , TypeNode.Fqn { Args: { } ea }, TypeNode.Fqn { Args: { } da } df)
                when ba.Length == ea.Length && ea.Length == da.Length:
            {
                var na = new TypeNode[da.Length];
                for (var i = 0; i < da.Length; i++) na[i] = ApplyErasurePattern(ba[i], ea[i], da[i]);
                return new TypeNode.Fqn(df.Name, na);
            }
            case (TypeNode.Array b, TypeNode.Array e, TypeNode.Array d):
                return new TypeNode.Array(ApplyErasurePattern(b.Elem, e.Elem, d.Elem));
            case (TypeNode.Nullable b, TypeNode.Nullable e, TypeNode.Nullable d):
                return new TypeNode.Nullable(ApplyErasurePattern(b.Of, e.Of, d.Of));
            case (TypeNode.ByRef b, TypeNode.ByRef e, TypeNode.ByRef d):
                return new TypeNode.ByRef(ApplyErasurePattern(b.Of, e.Of, d.Of));
            case (TypeNode.Fn b, TypeNode.Fn e, TypeNode.Fn d)
                when b.Params.Length == d.Params.Length && e.Params.Length == d.Params.Length
                     && b.Suspend == d.Suspend && (b.Recv == null) == (d.Recv == null):
            {
                var ps = new TypeNode[d.Params.Length];
                for (var i = 0; i < ps.Length; i++) ps[i] = ApplyErasurePattern(b.Params[i], e.Params[i], d.Params[i]);
                return new TypeNode.Fn(d.Suspend, ApplyErasurePattern(b.Ret, e.Ret, d.Ret), ps,
                    d.Recv == null ? null : ApplyErasurePattern(b.Recv, e.Recv, d.Recv));
            }
            default:
                return derived;
        }
    }
}

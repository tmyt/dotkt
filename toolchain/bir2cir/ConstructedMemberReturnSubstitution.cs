using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A BIR member call keeps the callee declaration's return type vocabulary. For `AtomicRef<Any>.value`, for example,
// that is `tv{scope:type,0}` plus the constructed owner `AtomicRef<Any>`. Once bir2cir has resolved the exact declaring
// owner, substitute the owner's actual arguments into the result. Leaving the callee-relative tv for ilemit makes it
// bind to the caller class's unrelated `!0` (or object fallback), producing invalid casts and signatures.
static class ConstructedMemberReturnSubstitution
{
    public static void ApplyAll(System.Collections.Generic.IEnumerable<JsonNode> roots)
    {
        foreach (var root in roots) Walk(root);
    }

    /// <summary>Close receiver-relative result slots in one newly synthesized executable graph.</summary>
    public static T ApplyMaterialized<T>(T root) where T : JsonNode
    {
        Walk(root);
        return root;
    }

    // InheritedMemberOwnerBinding already visits every call whose declaring owner it may rewrite. Discharge the
    // constructed-result capability on that same work item instead of scheduling a later whole-module repair walk.
    internal static void ApplyCall(JsonObject obj)
    {
        if (Str(obj["k"]) != "callInstance"
            || TypeJson.Read(obj["ownerType"]) is not TypeNode.Fqn { Args: { } args }) return;
        var changed = RewriteSlot(obj, "ret", args) | RewriteSlot(obj, "dynRet", args);
        if (changed) NodeType.DropStampIfStale(obj);
    }

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                ApplyCall(obj);
                foreach (var value in obj.Select(kv => kv.Value).ToList()) if (value != null) Walk(value);
                break;
            case JsonArray arr:
                foreach (var value in arr) if (value != null) Walk(value);
                break;
        }
    }

    // TRUE when the slot was actually rewritten — the caller owns the §2.7 `sty` consequence of that.
    static bool RewriteSlot(JsonObject obj, string key, TypeNode[] args)
    {
        if (TypeJson.Read(obj[key]) is not TypeNode type || !ContainsOwnerTv(type)) return false;
        // kotc's `sty` is the frontend-resolved CALL-SITE result. When the result slot is exactly that shape, every
        // tv in it already belongs to the caller's frame; substituting it through the callee owner would apply the
        // construction twice (`Iterator<Entry<K,V>>.next()` -> `Entry<Entry<K,V>,V>`). A callee-relative result is
        // distinguishable at the slot boundary without inventing another tv scope: it differs from the exact stamp
        // (`AtomicRef<Any>.value`: ret=!0, sty=String), so only that form is substituted. A bir2cir producer that
        // authors an already-closed result must carry the same exact stamp; equality then keeps that call stable
        // across source admission and later construction-boundary normalization.
        if (TypeJson.Read(obj["sty"]) is TypeNode stamp && type.Equals(stamp)) return false;
        obj[key] = TypeJson.Write(Subst(type, args));
        return true;
    }

    static TypeNode Subst(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn { Args: { } nested } f => new TypeNode.Fqn(f.Name, nested.Select(t => Subst(t, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Subst(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Subst(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(Subst(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(Subst(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Subst(fn.Ret, args),
            fn.Params.Select(p => Subst(p, args)).ToArray(), fn.Recv == null ? null : Subst(fn.Recv, args)),
        _ => type,
    };

    static bool ContainsOwnerTv(TypeNode type) => type switch
    {
        TypeNode.Tv { Scope: "type" } => true,
        TypeNode.Fqn { Args: { } args } => args.Any(ContainsOwnerTv),
        TypeNode.Nullable n => ContainsOwnerTv(n.Of),
        TypeNode.Oblivious o => ContainsOwnerTv(o.Of),
        TypeNode.Array a => ContainsOwnerTv(a.Elem),
        TypeNode.ByRef b => ContainsOwnerTv(b.Of),
        TypeNode.Fn fn => ContainsOwnerTv(fn.Ret) || fn.Params.Any(ContainsOwnerTv)
            || (fn.Recv != null && ContainsOwnerTv(fn.Recv)),
        _ => false,
    };

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

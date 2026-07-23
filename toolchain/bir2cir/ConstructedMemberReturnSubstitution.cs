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

    static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (Str(obj["k"]) == "callInstance"
                    && TypeJson.Read(obj["ownerType"]) is TypeNode.Fqn { Args: { } args })
                {
                    RewriteSlot(obj, "ret", args);
                    RewriteSlot(obj, "dynRet", args);
                }
                foreach (var value in obj.Select(kv => kv.Value).ToList()) if (value != null) Walk(value);
                break;
            case JsonArray arr:
                foreach (var value in arr) if (value != null) Walk(value);
                break;
        }
    }

    static void RewriteSlot(JsonObject obj, string key, TypeNode[] args)
    {
        if (TypeJson.Read(obj[key]) is TypeNode type && ContainsOwnerTv(type))
            obj[key] = TypeJson.Write(Subst(type, args));
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

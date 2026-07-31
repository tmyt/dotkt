using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

static class NullableFuncReturnErasure
{
    // A nullable func-RETURN is erased to `object` ONLY when a plain reference type cannot carry its null faithfully:
    // a CONCRETE VALUE-type inner (`Int?` — `Nullable<int>` can't ride a delegate's `out TResult` covariance while
    // keeping null). The open-generic inner (`(…) -> T?`) is NOT this pass's: `Nullable(Tv)` is object-erased at every
    // position, function-type return included, by NullableGenericErasure.EraseNullableTv (#86), which runs first — so
    // this pass never sees one. A
    // REFERENCE inner (`String?`, `List<T>?`) already carries null as a plain reference, so it stays the
    // (nullable-stripped, by ReferenceNullableStrip) reference type — erasing it to `object` would make the lifted
    // lambda return `object` where the concrete delegate slot (`Func<string>` from a .NET signature) demands a
    // covariantly-assignable return, and `object -> string` is NOT assignable → ilverify DelegateCtor rejects the
    // ctor (#189, the nullable/NRT-erasure delegate-return axis, distinct from #170's String->CharSequence). Codex-
    // confirmed ECMA-335 rule: the ldftn target's return must be assignable-TO the delegate `Invoke` return.
    static Func<string, bool> _isValue = _ => false;

    // A CONSTRUCTED struct is a struct: `ArraySegment<String>?` in a function-type return needs the same erasure its
    // argument-less siblings do, and the oracle answers false for a constructed reference generic.
    static bool ShouldEraseNullableInner(TypeNode of) => of switch
    {
        TypeNode.Fqn f => _isValue(f.Name),
        _ => false,
    };

    public static void Apply(JsonNode root, Func<string, bool> isValue)
    {
        _isValue = isValue ?? (_ => false);
        if (root is not JsonObject o) return;
        var erasedDelegateMethods = new HashSet<string>(StringComparer.Ordinal);
        var erasedClosureInvokes = new HashSet<string>(StringComparer.Ordinal);   // closure TYPE names
        // Structural sweep first (records delegate targets + repairs var dataflow off the PRE-rewrite tokens),
        // then the token rewrite.
        StructuralSweep(o, erasedDelegateMethods, erasedClosureInvokes);
        RewriteAllStrings(o);
        if (o["methods"] is JsonArray methods)
            foreach (var m in methods)
                if (m is JsonObject mo && (mo["name"] as JsonValue)?.GetValue<string>() is string mn
                    && erasedDelegateMethods.Contains(mn))
                    EraseMethodRet(mo);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to && (to["name"] as JsonValue)?.GetValue<string>() is string tn
                    && erasedClosureInvokes.Contains(tn) && to["methods"] is JsonArray tms)
                    foreach (var tm in tms)
                        if (tm is JsonObject tmo && (tmo["name"] as JsonValue)?.GetValue<string>() == "invoke")
                            EraseMethodRet(tmo);
    }

    static readonly TypeNode ObjFqn = new TypeNode.Fqn("object");

    static void EraseMethodRet(JsonObject mo)
    {
        if (TypeJson.Read(mo["ret"]) is not TypeNode ret) return;
        if (ret is TypeNode.Fqn { Args: null, Name: "object" or "void" }) return;
        mo["ret"] = TypeJson.Write(ObjFqn);
        RetypeReturnValues(mo["body"], ret);
    }

    static void RetypeReturnValues(JsonNode node, TypeNode oldRet)
    {
        switch (node)
        {
            case JsonObject obj:
                if ((obj["k"] as JsonValue)?.TryGetValue<string>(out var k) == true && k == "return"
                    && obj["value"] is JsonObject v)
                {
                    if (TypeJson.Read(v["type"]) is TypeNode vt && vt == oldRet) v["type"] = TypeJson.Write(ObjFqn);
                    if (TypeJson.Read(v["ret"]) is TypeNode vr && vr == oldRet) v["ret"] = TypeJson.Write(ObjFqn);
                }
                foreach (var kv in obj) RetypeReturnValues(kv.Value, oldRet);
                break;
            case JsonArray arr:
                foreach (var it in arr) RetypeReturnValues(it, oldRet);
                break;
        }
    }

    // Walks the tree recording (a) newDelegate/newClosure whose funcType RETURN is nullable-marked and
    // (b) `var` nodes needing dataflow repair. Carries the per-walk set of var names retyped to object so a
    // downstream `var y: gp:R = local(x_object)` re-narrowing gets a cast wrap.
    static void StructuralSweep(JsonNode node, HashSet<string> delegateMethods, HashSet<string> closureTypes)
        => Sweep(node, delegateMethods, closureTypes, new HashSet<string>(StringComparer.Ordinal));

    static void Sweep(JsonNode node, HashSet<string> delegateMethods, HashSet<string> closureTypes, HashSet<string> objectVars)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var k = (obj["k"] as JsonValue)?.TryGetValue<string>(out var ks) == true ? ks : null;
                if (k == "newDelegate" && HasErasedRet(obj) && (obj["method"] as JsonValue)?.GetValue<string>() is string dm)
                    delegateMethods.Add(dm);
                // `closureType` is a STRUCTURED TypeNode (`{t:fqn,name:…}`) since the #37 type flip — read the fqn
                // NAME, not a bare string (the old `as JsonValue` silently missed EVERY closure, so a capturing
                // closure whose funcType erased its `(…)->R?` return to `Func<object>` kept an `invoke` returning the
                // value-type `!T` → `newobj Func<object>(ldftn !T ::invoke)` read the value as an object ref → NRE,
                // the genseq2 `generateSequence(1){…}` `{ seed }` closure).
                else if (k == "newClosure" && HasErasedRet(obj) && TypeJson.Read(obj["closureType"]) is TypeNode.Fqn { Name: { } ct })
                    closureTypes.Add(ct);
                else if (k == "var" && TypeJson.Read(obj["type"]) is TypeNode vt && obj["init"] is JsonObject init)
                {
                    var ik = (init["k"] as JsonValue)?.TryGetValue<string>(out var iks) == true ? iks : null;
                    var vn = (obj["name"] as JsonValue)?.GetValue<string>();
                    var isObj = vt is TypeNode.Fqn { Args: null, Name: "object" };
                    if (ik == "delegateInvoke" && HasErasedRet(init))
                    {
                        if (vt is TypeNode.Tv)
                        {
                            obj["type"] = TypeJson.Write(ObjFqn);
                            if (vn != null) objectVars.Add(vn);
                        }
                        else if (!isObj)
                            obj["init"] = new JsonObject { ["k"] = "cast", ["type"] = obj["type"].DeepClone(), ["e"] = init.DeepClone() };
                    }
                    else if (ik == "local" && !isObj && vt is not TypeNode.Nullable
                        && (init["name"] as JsonValue)?.GetValue<string>() is string src && objectVars.Contains(src))
                        // Post-null-check narrowing of an object-retyped local back into its typed slot:
                        // unbox.any/castclass via the universal `cast`.
                        obj["init"] = new JsonObject { ["k"] = "cast", ["type"] = obj["type"].DeepClone(), ["e"] = init.DeepClone() };
                }
                foreach (var kv in obj) Sweep(kv.Value, delegateMethods, closureTypes, objectVars);
                break;
            }
            case JsonArray arr:
                foreach (var it in arr) Sweep(it, delegateMethods, closureTypes, objectVars);
                break;
        }
    }

    static bool HasErasedRet(JsonObject node)
        => TypeJson.Read(node["funcType"]) is TypeNode.Fn { Suspend: false, Ret: TypeNode.Nullable n }
           && ShouldEraseNullableInner(n.Of);

    // Type-slot sweep: a NON-suspend function type whose RETURN is a Nullable (`(…) -> R?`) over a VALUE or open-generic
    // inner (ShouldEraseNullableInner) has its return erased to `object` — the only CLR delegate return that carries a
    // real null for a value-type/unconstrained R. A reference inner keeps its (later-stripped) reference type so the
    // lifted lambda's return stays covariantly-assignable to the concrete delegate slot (#189). Recurses nested funcs/args.
    static void RewriteAllStrings(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) obj[key] = TypeJson.Write(RewriteFnRet(tn));
                    else RewriteAllStrings(child);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (TypeJson.Read(child) is TypeNode tn) arr[i] = TypeJson.Write(RewriteFnRet(tn));
                    else RewriteAllStrings(child);
                }
                break;
        }
    }

    internal static TypeNode RewriteFnRet(TypeNode t) => t switch
    {
        TypeNode.Fn { Suspend: false } fn => new TypeNode.Fn(false,
            fn.Ret is TypeNode.Nullable rn && ShouldEraseNullableInner(rn.Of)
                ? new TypeNode.Fqn("object") : RewriteFnRet(fn.Ret),
            fn.Params.Select(RewriteFnRet).ToArray(), fn.Recv == null ? null : RewriteFnRet(fn.Recv)),
        TypeNode.Fn fn => new TypeNode.Fn(true, fn.Ret, fn.Params.Select(RewriteFnRet).ToArray(),
            fn.Recv == null ? null : RewriteFnRet(fn.Recv)),
        TypeNode.Nullable n => new TypeNode.Nullable(RewriteFnRet(n.Of)),
        TypeNode.Fqn { Args: null } f => f,
        TypeNode.Fqn f => new TypeNode.Fqn(f.Name, f.Args.Select(RewriteFnRet).ToArray()),
        TypeNode.Array a => new TypeNode.Array(RewriteFnRet(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(RewriteFnRet(b.Of)),
        _ => t,
    };
}


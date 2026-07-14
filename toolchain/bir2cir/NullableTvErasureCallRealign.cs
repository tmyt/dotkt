using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// NULLABLE-Tv ERASURE call-site realignment (#4; the value-type-array-nullability / generic-boundary
// read family — #113/#117/#120/#142, READ side).
//
// A generic class `Box<T>` with a member typed `…Ref<T?>…` (a constructed generic whose arg is the
// nullable type-VARIABLE `T?`) has that `Nullable(Tv)` erased to `object` on the DECLARATION side by
// NullableGenericReturnErasure.EraseNullableTv — `object` is the only uniform CLR storage that carries a
// real null for BOTH a reference and a value instantiation of the unconstrained T. So Box's emitted
// field/getter/`elem` all return `Ref<object>` (…[]). That erasure is correct (#142) and mandatory.
//
// But a CALL site is emitted by kotc with T ALREADY substituted to the concrete argument — e.g.
// `Box<Int>.get_a()` carries `Array<Ref<Nullable(kotlin.Int)>>`, NOT a bare `Nullable(Tv)`. The blanket
// EraseNullableTv sweep cannot see it (there is no `Tv` left), so it lowers to `Ref<Nullable<int32>>`,
// contradicting the member's ACTUAL erased return `Ref<object>`. `Ref<object>` and `Ref<Nullable<int32>>`
// are UNRELATED invariant reified generics (generic variance is interfaces/delegates only) — no castclass
// reconciles them (a castclass throws), so the read must be typed `Ref<object>` THROUGHOUT. Left alone this
// is an ilverify StackUnexpected (found `Ref`1<object>` expected `Ref`1<Nullable`1<int32>>`) at the element
// read / slot store.
//
// This pass re-derives each call's return by SUBSTITUTING the owner's type-args into the member's
// EraseNullableTv-applied declaration — the callsite return then equals what the emitted method actually
// returns, BY CONSTRUCTION. A rewrite fires ONLY when the derived type is the object-ERASURE of the
// stamped type (IsObjectErasureOf) — i.e. it differs solely by `object` appearing where the callsite has a
// `Nullable(value)`/concrete arg. This is precisely the erasure boundary and nothing else: a directly-written
// `Ref<Int?>` (whose `Ref` declaration has NO `Nullable(Tv)`, so the derived type equals the stamped one)
// is untouched, and a genuine widen/narrow (not an object-erasure) never matches the gate. The corrected
// receiver type then flows through a per-method forward type-env so a chained `…[i].v` re-stamps `get_v`'s
// owner (`Ref<object>`) and return (`object`) too. A `var` whose declared type is the erasure counterpart of
// its init is retyped when the difference sits inside a constructed-generic arg (irreconcilable), or its init
// is wrapped in a `cast`->declared when the whole value erased to a TOP-LEVEL `object` (ilemit's unbox.any
// reconciles a boxed value / genuine null). Runs in BIR-space (kotlin.* names) right after the DEF-side
// EraseNullableTv, before BirTypeLowering. Body-only, so naturally inert in the ref build.
//
// SCOPE (v1, GitHub #4): the READ positions — a member read into a `var`, a chained `…[i].v` receiver, and a
// value-typed consumer (`val x: Int? = …`). A LOCAL generic owner only (the user's own generic class); a
// REFERENCED (stdlib) owner is out of scope — the ref.dll surface already names `object` (not a bare `Tv`), so
// a reflected member return cannot be re-derived here without risking a wrong rewrite, and #4 is the app-side
// user class. STORE-side and join positions of the SAME erasure family are NOT yet handled (a documented
// follow-up): reassigning an erased read into a direct-written slot, `return`ing / passing / `setField`ing an
// erased value into a `Ref<T?>`-typed target, and an `if/else` value-join whose branches are erased. The
// verify-il gate (cases/il-genarrlam) is the arbiter for the covered set.
static class NullableTvErasureCallRealign
{
    // owner FQN -> ("member|argCount" -> declared return TypeNode), captured across ALL roots BEFORE the
    // per-file DEF-side EraseNullableTv mutates the declarations in place. ALL members are stored (not only
    // erasure-affected ones): re-deriving `get_v` on a rewritten `Ref<object>` receiver needs the plain
    // `tv{type,0}` declaration too.
    public sealed class DeclIndex
    {
        public readonly Dictionary<string, Dictionary<string, TypeNode>> ByOwner = new(StringComparer.Ordinal);
    }

    public static DeclIndex CollectDeclaredMemberRets(IEnumerable<JsonNode> roots)
    {
        var idx = new DeclIndex();
        foreach (var r in roots) CollectFrom(r, idx);
        return idx;
    }

    static void CollectFrom(JsonNode node, DeclIndex idx)
    {
        if (node is not JsonObject o) return;
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    // A generic owner only (a non-generic type's members never carry a `Nullable(Tv)`).
                    if (Str(to["name"]) is string nm && to["typeParams"] is JsonArray tps && tps.Count > 0
                        && !idx.ByOwner.ContainsKey(nm))
                    {
                        var rets = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
                        if (to["methods"] is JsonArray ms)
                            foreach (var m in ms)
                                if (m is JsonObject mo && Str(mo["name"]) is string mn && TypeJson.Read(mo["ret"]) is TypeNode rt)
                                {
                                    var key = mn + "|" + ((mo["params"] as JsonArray)?.Count ?? 0);
                                    // AMBIGUOUS overload guard: two same-name/same-arity members whose returns DISAGREE
                                    // (`g(Int): Ref<T?>` vs `g(String): Ref<T>`) would otherwise collapse first-wins and
                                    // could derive the WRONG return for a call — manufacturing the very mismatch this pass
                                    // fixes. A conflicting key is poisoned to `null` (LookupDeclRet then skips it).
                                    if (rets.TryGetValue(key, out var prior))
                                    {
                                        if (prior != null && !prior.Equals(rt)) rets[key] = null;
                                    }
                                    else rets[key] = rt;
                                }
                        idx.ByOwner[nm] = rets;
                    }
                    CollectFrom(to, idx);   // nested types
                }
    }

    public static void Apply(JsonNode root, DeclIndex idx)
    {
        if (root is not JsonObject o) return;
        ProcessMethods(o["methods"], idx);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t != null) Apply(t, idx);
    }

    static void ProcessMethods(JsonNode methods, DeclIndex idx)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
            if (m is JsonObject mo)
            {
                var env = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
                if (mo["params"] is JsonArray ps)
                    foreach (var p in ps)
                        if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                            env[pn] = pt;
                if (mo["body"] is JsonNode body) Eval(body, env, idx);
            }
    }

    // Forward type-flow evaluation of a body node: rewrites erasure-boundary reads in place and returns the
    // node's static type (null for statements / unknown). A `var` registers its (possibly-retyped) type in
    // `env` before its siblings are visited, so a later read of that local re-derives against the corrected type.
    static TypeNode Eval(JsonNode node, Dictionary<string, TypeNode> env, DeclIndex idx)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var it in a) if (it != null) Eval(it, env, idx);
                return null;
            case JsonObject o:
                break;
            default:
                return null;
        }
        var obj = (JsonObject)node;
        switch (Str(obj["k"]))
        {
            case "var":
                EvalVar(obj, env, idx);
                return null;
            case "local":
                return Str(obj["name"]) is string ln ? env.GetValueOrDefault(ln) : null;
            case "const":
                return TypeJson.Read(obj["type"]);
            case "new":
                EvalChildrenOf(obj, "args", env, idx);
                return TypeJson.Read(obj["type"]);
            case "cast":
                if (obj["e"] != null) Eval(obj["e"], env, idx);
                return TypeJson.Read(obj["type"]);
            case "callStatic":
                EvalChildrenOf(obj, "args", env, idx);
                return TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
            case "arrayGet":
            {
                var arrType = obj["array"] != null ? Eval(obj["array"], env, idx) : null;
                if (obj["index"] != null) Eval(obj["index"], env, idx);
                if (arrType is TypeNode.Array arr)
                {
                    // Re-stamp the ldelem `elem` token ONLY when the flowed array element is the object-erasure
                    // of the stamped one (same discipline as every other rewrite here) — caps the blast radius to
                    // the erasure family even if a flat-env local type is stale.
                    if (TypeJson.Read(obj["elem"]) is TypeNode cur && !cur.Equals(arr.Elem) && IsObjectErasureOf(arr.Elem, cur))
                        obj["elem"] = TypeJson.Write(arr.Elem);
                    return arr.Elem;
                }
                return TypeJson.Read(obj["elem"]);
            }
            case "callInstance":
                return EvalCallInstance(obj, env, idx);
            default:
                // Unknown statement/expression: recurse every child, then report a `type`/`ret` if it has one.
                foreach (var kv in obj) if (kv.Value != null) Eval(kv.Value, env, idx);
                return TypeJson.Read(obj["type"]) ?? TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
        }
    }

    static void EvalChildrenOf(JsonObject obj, string arrayKey, Dictionary<string, TypeNode> env, DeclIndex idx)
    {
        if (obj[arrayKey] is JsonArray args)
            foreach (var arg in args) if (arg != null) Eval(arg, env, idx);
    }

    static void EvalVar(JsonObject obj, Dictionary<string, TypeNode> env, DeclIndex idx)
    {
        var initType = obj["init"] != null ? Eval(obj["init"], env, idx) : null;
        var name = Str(obj["name"]);
        var declType = TypeJson.Read(obj["type"]);
        if (name == null) return;
        if (declType != null && initType != null && !initType.Equals(declType) && IsObjectErasureOf(initType, declType))
        {
            if (initType is TypeNode.Fqn { Name: "object", Args: null })
            {
                // The whole value erased to a TOP-LEVEL `object` (e.g. `val x: Int? = r.v`). Keep the
                // declared slot and wrap the init in a `cast`->declared so ilemit's unbox.any reconciles the
                // boxed value / genuine null back to Nullable<V> (or castclass for a reference declared type).
                obj["init"] = new JsonObject
                {
                    ["k"] = "cast",
                    ["type"] = TypeJson.Write(declType),
                    ["e"] = obj["init"].DeepClone(),
                };
                env[name] = declType;
            }
            else
            {
                // The erasure sits INSIDE a constructed-generic arg / array elem (e.g. `val r: Ref<Int?> =
                // b.a[0]` -> `Ref<object>`). Ref<object> and Ref<Nullable<int32>> are irreconcilable invariant
                // reified generics — retype the slot to the erased form and keep propagating.
                obj["type"] = TypeJson.Write(initType);
                env[name] = initType;
            }
            return;
        }
        env[name] = declType ?? initType;
    }

    static TypeNode EvalCallInstance(JsonObject obj, Dictionary<string, TypeNode> env, DeclIndex idx)
    {
        var recvType = obj["recv"] != null ? Eval(obj["recv"], env, idx) : null;
        if (obj["args"] is JsonArray args)
            foreach (var arg in args) if (arg != null) Eval(arg, env, idx);

        var stampedRet = TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
        if (Str(obj["method"]) is not string method) return stampedRet;

        var nodeOwner = TypeJson.Read(obj["ownerType"]);
        // The corrected owner: prefer the receiver's flowed static type (it may be an erased `Ref<object>`),
        // else the stamped ownerType.
        var owner = recvType as TypeNode.Fqn ?? nodeOwner as TypeNode.Fqn;
        if (owner == null) return stampedRet;

        var argCount = (obj["args"] as JsonArray)?.Count ?? 0;
        var declRet = LookupDeclRet(owner.Name, method, argCount, idx);
        if (declRet == null) return stampedRet;

        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        var derived = Subst(NullableGenericReturnErasure.EraseNullableTv(declRet), owner.Args, methodArgs);
        if (derived == null) return stampedRet;

        // Re-stamp the owner when the flowed receiver type is the object-erasure of the stamped owner (a chained
        // read through an already-corrected `Ref<object>` receiver): ilemit must resolve `get_v` on `Ref<object>`.
        if (nodeOwner != null && !owner.Equals(nodeOwner) && IsObjectErasureOf(owner, nodeOwner))
            obj["ownerType"] = TypeJson.Write(owner);

        // Rewrite the return ONLY when `derived` is the object-erasure of the stamped return — the exact erasure
        // boundary, never a genuine widen/narrow. Keeps a direct-write `Ref<Int?>` (derived == stamped) untouched.
        if (stampedRet != null && !derived.Equals(stampedRet) && IsObjectErasureOf(derived, stampedRet))
        {
            if (obj["ret"] != null) obj["ret"] = TypeJson.Write(derived);
            if (obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(derived);
            return derived;
        }
        return stampedRet;
    }

    // The declared return of a LOCAL generic owner's member, keyed by EXACT name+arity (DefaultArgSplice has already
    // run, so an app-build call carries its real arity). A poisoned `null` value = an ambiguous same-name/same-arity
    // overload set (CollectFrom) — skip the rewrite rather than risk deriving the wrong member's return. Referenced
    // (stdlib) owners are intentionally OUT of scope for #4: the ref.dll surface names `object` (not a bare `Tv`) so a
    // reflected member return cannot be re-derived here safely — see the header note.
    static TypeNode LookupDeclRet(string ownerFqn, string method, int argCount, DeclIndex idx)
    {
        if (idx.ByOwner.TryGetValue(ownerFqn, out var rets) && rets.TryGetValue(method + "|" + argCount, out var local))
            return local;   // may be null (poisoned/ambiguous) -> caller skips
        return null;
    }

    // Substitute class-scope `tv{type,i}` with `typeArgs[i]` and method-scope `tv{method,i}` with `methodArgs[i]`,
    // recursively. Returns null when a needed binding is unavailable (caller skips the rewrite).
    static TypeNode Subst(TypeNode t, TypeNode[] typeArgs, TypeNode[] methodArgs)
    {
        switch (t)
        {
            case TypeNode.Tv { Scope: "type" } tv:
                return typeArgs != null && tv.I >= 0 && tv.I < typeArgs.Length ? typeArgs[tv.I] : null;
            case TypeNode.Tv { Scope: "method" } tv:
                return methodArgs != null && tv.I >= 0 && tv.I < methodArgs.Length ? methodArgs[tv.I] : null;
            case TypeNode.Fqn { Args: { } a } f:
            {
                var na = new TypeNode[a.Length];
                for (var i = 0; i < a.Length; i++)
                    if (Subst(a[i], typeArgs, methodArgs) is TypeNode s) na[i] = s; else return null;
                return new TypeNode.Fqn(f.Name, na);
            }
            case TypeNode.Fqn f:
                return f;
            case TypeNode.Nullable n:
                return Subst(n.Of, typeArgs, methodArgs) is TypeNode i0 ? new TypeNode.Nullable(i0) : null;
            case TypeNode.Oblivious o:
                return Subst(o.Of, typeArgs, methodArgs) is TypeNode i1 ? new TypeNode.Oblivious(i1) : null;
            case TypeNode.Array ar:
                return Subst(ar.Elem, typeArgs, methodArgs) is TypeNode i2 ? new TypeNode.Array(i2) : null;
            case TypeNode.ByRef br:
                return Subst(br.Of, typeArgs, methodArgs) is TypeNode i3 ? new TypeNode.ByRef(i3) : null;
            case TypeNode.Fn fn:
            {
                if (Subst(fn.Ret, typeArgs, methodArgs) is not TypeNode ret) return null;
                var ps = new TypeNode[fn.Params.Length];
                for (var i = 0; i < ps.Length; i++)
                    if (Subst(fn.Params[i], typeArgs, methodArgs) is TypeNode s) ps[i] = s; else return null;
                TypeNode recv = null;
                if (fn.Recv != null)
                {
                    if (Subst(fn.Recv, typeArgs, methodArgs) is not TypeNode r) return null;
                    recv = r;
                }
                return new TypeNode.Fn(fn.Suspend, ret, ps, recv);
            }
            default:
                return t;
        }
    }

    // Whether `candidate` is `expected` with one or more sub-positions collapsed to the erased `object` — i.e.
    // `candidate` == `expected` except that where `expected` has a non-`object` type, `candidate` may have
    // `object`. True for `object` vs anything (a leaf erasure), and structurally through Fqn args / array elem /
    // nullable / byref / fn. This is the exact "object-erasure of" relation that gates every rewrite here.
    static bool IsObjectErasureOf(TypeNode candidate, TypeNode expected)
    {
        if (candidate.Equals(expected)) return true;
        if (candidate is TypeNode.Fqn { Name: "object", Args: null }) return true;
        return (candidate, expected) switch
        {
            (TypeNode.Fqn { Args: { } ca } cf, TypeNode.Fqn { Args: { } ea } ef)
                when cf.Name == ef.Name && ca.Length == ea.Length
                => ca.Zip(ea, IsObjectErasureOf).All(x => x),
            (TypeNode.Array c, TypeNode.Array e) => IsObjectErasureOf(c.Elem, e.Elem),
            (TypeNode.Nullable c, TypeNode.Nullable e) => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.Oblivious c, TypeNode.Oblivious e) => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.ByRef c, TypeNode.ByRef e) => IsObjectErasureOf(c.Of, e.Of),
            (TypeNode.Fn c, TypeNode.Fn e)
                when c.Params.Length == e.Params.Length && c.Suspend == e.Suspend && (c.Recv == null) == (e.Recv == null)
                => IsObjectErasureOf(c.Ret, e.Ret) && c.Params.Zip(e.Params, IsObjectErasureOf).All(x => x)
                   && (c.Recv == null || IsObjectErasureOf(c.Recv, e.Recv)),
            _ => false,
        };
    }

    static string Str(JsonNode n) => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
}

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
// NullableGenericErasure.EraseNullableTv — `object` is the only uniform CLR storage that carries a
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
// This pass re-derives each local generic call's return by SUBSTITUTING its class/method type-args into the
// EraseNullableTv-applied declaration — the callsite return then equals what the emitted method actually
// returns, BY CONSTRUCTION. This includes ownerless top-level generic functions such as #28's
// `fun <T> boxes(x: T): List<T?>`. A rewrite fires ONLY when the derived type is the object-ERASURE of the
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
// SCOPE (#4/#28/#86): every USE position of an object-erased slot, read and write alike.
//   READ  — a local generic call into a `var`, a chained `…[i].v` receiver, collection member dispatch
//           (`List<T?>.size` / iterator), an array element read, a field read, a value-typed consumer
//           (`val x: Int? = …`).
//   WRITE — the positions where a value flows INTO a fixed slot, which is the other half of the same formula:
//           `setLocal`, `setField`, `arraySet`, `return`, an `if/else` value-join, and every call/ctor ARGUMENT
//           (whose target is the callee's `Subst(Erase(declared param))`, sig included). See the Writes half.
// Referenced declarations are not indexed (their ref.dll surface has already erased the original `Nullable(Tv)`),
// but a referenced generic member's owner/return is realigned from the corrected receiver args.
static partial class NullableTvErasureCallRealign
{
    // The pre-erasure declaration of one member: its return AND its parameter vector. A use is typed
    // `Subst(Erase(<the matching component>), typeArgs)` — never `Erase(Subst(...))`.
    public sealed class DeclSig
    {
        public TypeNode Ret;
        public TypeNode[] Params;
    }

    // Local owner/top-level declarations, captured across ALL roots BEFORE the per-file DEF-side EraseNullableTv
    // mutates declarations in place. ALL members are stored (not only erasure-affected ones): re-deriving `get_v`
    // on a rewritten `Ref<object>` receiver needs the plain `tv{type,0}` declaration too. Ambiguous
    // same-name/same-arity entries are poisoned to null.
    public sealed class DeclIndex
    {
        public readonly Dictionary<string, Dictionary<string, DeclSig>> ByOwner = new(StringComparer.Ordinal);
        public readonly Dictionary<string, DeclSig> TopLevel = new(StringComparer.Ordinal);
        // owner -> ctor arity -> parameter vector. A `new`'s args/argTypes are typed against it, the same way a
        // call's are typed against a method's — a ctor param is a declaration slot like any other (`Cell<T>(x: T?)`).
        public readonly Dictionary<string, Dictionary<int, TypeNode[]>> Ctors = new(StringComparer.Ordinal);
        // owner -> field/property name -> declared type. The target of a `setField` and the result of a `field` read.
        public readonly Dictionary<string, Dictionary<string, TypeNode>> Slots = new(StringComparer.Ordinal);
    }

    public static DeclIndex CollectDeclaredMemberRets(IEnumerable<JsonNode> roots)
    {
        var idx = new DeclIndex();
        foreach (var r in roots) CollectFrom(r, idx, topLevel: true);
        return idx;
    }

    static void CollectFrom(JsonNode node, DeclIndex idx, bool topLevel)
    {
        if (node is not JsonObject o) return;
        // TOP-LEVEL functions are keyed by bare name + arity across every root. EVERY one is indexed, generic or not:
        // the entry is only usable when it is UNAMBIGUOUS, so a non-generic same-name/same-arity sibling is not
        // noise — it is what poisons the key and stops a generic declaration from being applied to a call that meant
        // the other one. (`Int.coerceIn(min, max)` beside `<T : Comparable<T>> T.coerceIn(min: T?, max: T?)` is
        // exactly that pair: index only the generic half and every `coerceIn` call gets the erased parameter vector.)
        if (topLevel && o["methods"] is JsonArray topMethods)
            foreach (var m in topMethods)
                if (m is JsonObject mo && Str(mo["name"]) is string mn && ReadSig(mo) is DeclSig sig)
                    AddUnambiguous(idx.TopLevel, mn + "|" + sig.Params.Length, sig);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    if (Str(to["name"]) is string nm && !idx.ByOwner.ContainsKey(nm))
                    {
                        var sigs = new Dictionary<string, DeclSig>(StringComparer.Ordinal);
                        if (to["methods"] is JsonArray ms)
                            foreach (var m in ms)
                                if (m is JsonObject mo && Str(mo["name"]) is string mn2 && ReadSig(mo) is DeclSig sig)
                                    // AMBIGUOUS overload guard: two same-name/same-arity members whose declarations
                                    // DISAGREE (`g(Int): Ref<T?>` vs `g(String): Ref<T>`) would otherwise collapse
                                    // first-wins and could derive the WRONG type for a use — manufacturing the very
                                    // mismatch this pass fixes. A conflicting key is poisoned to `null` (the lookups
                                    // then skip it).
                                    AddUnambiguous(sigs, mn2 + "|" + sig.Params.Length, sig);
                        idx.ByOwner[nm] = sigs;
                        var ctors = new Dictionary<int, TypeNode[]>();
                        if (to["ctors"] is JsonArray cs)
                            foreach (var c in cs)
                                if (c is JsonObject co && ReadParams(co["params"]) is TypeNode[] cp)
                                {
                                    if (ctors.TryGetValue(cp.Length, out var prior))
                                    {
                                        if (prior != null && !SameVector(prior, cp)) ctors[cp.Length] = null;
                                    }
                                    else ctors[cp.Length] = cp;
                                }
                        idx.Ctors[nm] = ctors;
                        var slots = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
                        CollectSlots(to["fields"], slots);
                        CollectSlots(to["properties"], slots);
                        idx.Slots[nm] = slots;
                    }
                    CollectFrom(to, idx, topLevel: false);   // nested types
                }
    }

    static void CollectSlots(JsonNode decls, Dictionary<string, TypeNode> slots)
    {
        if (decls is not JsonArray a) return;
        foreach (var d in a)
            if (d is JsonObject o && Str(o["name"]) is string n && TypeJson.Read(o["type"]) is TypeNode t)
            {
                if (slots.TryGetValue(n, out var prior))
                {
                    if (prior != null && !prior.Equals(t)) slots[n] = null;
                }
                else slots[n] = t;
            }
    }

    static DeclSig ReadSig(JsonObject mo)
    {
        if (TypeJson.Read(mo["ret"]) is not TypeNode rt) return null;
        return ReadParams(mo["params"]) is TypeNode[] ps ? new DeclSig { Ret = rt, Params = ps } : null;
    }

    // The declared parameter vector, or null when any slot is untyped — a partially-read vector would silently
    // realign the wrong positions.
    static TypeNode[] ReadParams(JsonNode ps)
    {
        if (ps is not JsonArray a) return Array.Empty<TypeNode>();
        var result = new TypeNode[a.Count];
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] is not JsonObject po || TypeJson.Read(po["type"]) is not TypeNode t) return null;
            result[i] = t;
        }
        return result;
    }

    // Two same-name/same-arity members whose declarations DISAGREE poison the whole entry. Name+arity is all a call
    // site gives us, so a surviving entry would be a GUESS at which overload was meant — and deriving the wrong
    // member's types manufactures exactly the mismatch this pass exists to remove. A poisoned key falls back to the
    // call's own descriptor, which is at least what the member will be resolved by. (Keeping the components apart —
    // a conflicting return poisoning only the return — was tried and is unsound for the same reason: the surviving
    // component still names one arbitrary overload of the set.)
    static void AddUnambiguous(Dictionary<string, DeclSig> entries, string key, DeclSig sig)
    {
        if (!entries.TryGetValue(key, out var prior)) { entries[key] = sig; return; }
        if (prior == null) return;                                          // already fully poisoned
        if (!prior.Ret.Equals(sig.Ret) || !SameVector(prior.Params, sig.Params)) entries[key] = null;
    }

    static bool SameVector(TypeNode[] a, TypeNode[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    // The struct-ness oracle, needed by the WRITE axis to tell an `object` seam that genuinely needs a conversion
    // (a value / type-variable slot) from one that is plain reference assignment.
    static Func<string, bool> _isValue = _ => false;

    public static void Apply(JsonNode root, DeclIndex idx, Func<string, bool> isValue)
    {
        _isValue = isValue ?? (_ => false);
        ApplyRec(root, idx);
    }

    static void ApplyRec(JsonNode root, DeclIndex idx)
    {
        if (root is not JsonObject o) return;
        ProcessMethods(o["methods"], idx);
        if (o["types"] is JsonArray types)
            foreach (var t in types)
                if (t is JsonObject to)
                {
                    ProcessCtors(to, idx);
                    ApplyRec(to, idx);
                }
    }

    // A CONSTRUCTOR body is a body, and its base/this DELEGATION arguments are call arguments — into the delegated
    // constructor's own (erased) parameter vector. `class Derived(y: Int?) : Base<Int>(y)` hands a `Nullable<int32>`
    // to a `Base<T>..ctor(object)` and is invalid IL without the box, exactly as `Base<Int>(y)` written as an
    // expression would be. Skipping `ctors` left that whole surface out of the use axis.
    static void ProcessCtors(JsonObject to, DeclIndex idx)
    {
        if (to["ctors"] is not JsonArray ctors) return;
        var ownerArgs = OwnTypeArgs(to);
        var baseType = TypeJson.Read(to["base"]) as TypeNode.Fqn;
        var baseParams = baseType != null && idx.Ctors.TryGetValue(baseType.Name, out var byArity) ? byArity : null;
        var ownName = Str(to["name"]);
        foreach (var c in ctors)
        {
            if (c is not JsonObject co) continue;
            var ctx = new Ctx { Idx = idx };
            if (co["params"] is JsonArray ps)
                foreach (var p in ps)
                    if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                        ctx.Env[pn] = pt;
            DelegationArgs(co, "baseArgs", baseParams, baseType?.Args, ctx);
            DelegationArgs(co, "thisArgs",
                ownName != null && idx.Ctors.TryGetValue(ownName, out var own) ? own : null, ownerArgs, ctx);
            if (co["body"] is JsonNode body) Eval(body, ctx);
        }
    }

    static void DelegationArgs(JsonObject co, string key, Dictionary<int, TypeNode[]> byArity,
        TypeNode[] ownerArgs, Ctx ctx)
    {
        if (co[key] is not JsonArray args) return;
        var argTypes = new TypeNode[args.Count];
        for (var i = 0; i < args.Count; i++)
            if (args[i] != null) argTypes[i] = Eval(args[i], ctx);
        TypeNode[] declParams = null;
        byArity?.TryGetValue(args.Count, out declParams);
        RealignDelegation(args, declParams, ownerArgs, argTypes);
    }

    // A generic owner's own type parameters, as the arguments a `this(...)` delegation substitutes with (a ctor
    // delegating within `Holder<T>` stays at `Holder<T>`).
    static TypeNode[] OwnTypeArgs(JsonObject to)
    {
        if (to["typeParams"] is not JsonArray tps || tps.Count == 0) return null;
        var args = new TypeNode[tps.Count];
        for (var i = 0; i < tps.Count; i++) args[i] = new TypeNode.Tv("type", i);
        return args;
    }

    // The per-method type environment: local/param slot types, plus the method's own (already-erased) return type,
    // which is the target of every `return` in the body.
    sealed class Ctx
    {
        public readonly Dictionary<string, TypeNode> Env = new(StringComparer.Ordinal);
        public DeclIndex Idx;
        public TypeNode Ret;
    }

    static void ProcessMethods(JsonNode methods, DeclIndex idx)
    {
        if (methods is not JsonArray arr) return;
        foreach (var m in arr)
            if (m is JsonObject mo)
            {
                var ctx = new Ctx { Idx = idx, Ret = TypeJson.Read(mo["ret"]) };
                if (mo["params"] is JsonArray ps)
                    foreach (var p in ps)
                        if (p is JsonObject po && Str(po["name"]) is string pn && TypeJson.Read(po["type"]) is TypeNode pt)
                            ctx.Env[pn] = pt;
                if (mo["body"] is JsonNode body) Eval(body, ctx);
            }
    }

    // Forward type-flow evaluation of a body node: rewrites erasure-boundary uses in place and returns the
    // node's static type (null for statements / unknown). A `var` registers its (possibly-retyped) type in
    // `ctx.Env` before its siblings are visited, so a later read of that local re-derives against the corrected type.
    static TypeNode Eval(JsonNode node, Ctx ctx)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var it in a) if (it != null) Eval(it, ctx);
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
                EvalVar(obj, ctx);
                return null;
            case "local":
                return Str(obj["name"]) is string ln ? ctx.Env.GetValueOrDefault(ln) : null;
            case "const":
                return TypeJson.Read(obj["type"]);
            case "new":
                return EvalNew(obj, ctx);
            case "cast":
                if (obj["e"] != null) Eval(obj["e"], ctx);
                return TypeJson.Read(obj["type"]);
            case "callStatic":
                return EvalCallStatic(obj, ctx);
            case "arrayGet":
            {
                var arrType = obj["array"] != null ? Eval(obj["array"], ctx) : null;
                if (obj["index"] != null) Eval(obj["index"], ctx);
                if (arrType is TypeNode.Array arr)
                {
                    // Re-stamp the ldelem `elem` token ONLY when the flowed array element is the object-erasure
                    // of the stamped one (same discipline as every other rewrite here) — caps the blast radius to
                    // the erasure family even if a flat-env local type is stale.
                    if (TypeJson.Read(obj["elem"]) is TypeNode cur && !cur.Equals(arr.Elem) && IsObjectErasureOf(arr.Elem, cur))
                    {
                        obj["elem"] = TypeJson.Write(arr.Elem);
                        RestampSty(obj, arr.Elem);   // `elem` IS this node's result slot
                    }
                    return arr.Elem;
                }
                return TypeJson.Read(obj["elem"]);
            }
            case "callInstance":
                return EvalCallInstance(obj, ctx);
            case "delegateInvoke":
                // The value a `(…) -> R` invocation produces is the function type's RETURN, which the erasure has
                // already rewritten in the `funcType` slot. Without this the node reports no type at all and every
                // consumer of a lambda result falls outside the realignment.
                if (obj["recv"] != null) Eval(obj["recv"], ctx);
                EvalChildrenOf(obj, "args", ctx);
                return TypeJson.Read(obj["funcType"]) is TypeNode.Fn dfn ? dfn.Ret : null;
            case "field":
                return EvalField(obj, ctx);
            case "setLocal":
                EvalSetLocal(obj, ctx);
                return null;
            case "setField":
                EvalSetField(obj, ctx);
                return null;
            case "arraySet":
                EvalArraySet(obj, ctx);
                return null;
            case "return":
                EvalReturn(obj, ctx);
                return null;
            case "cond":
                return EvalCond(obj, ctx);
            case "newArray":
            case "newArrayInit":
            case "newArraySized":
                // An array CONSTRUCTION — including the pack a `vararg xs: T?` call site builds. Its `elem` is the
                // array's element type, so it is both this node's result type and the target its own elements fill.
                return EvalNewArray(obj, ctx);
            case "forEachInline":
                // A loop VARIABLE is a slot, and this node states its type in `elem`. Binding it is what lets a value
                // consumer inside the loop body be reconciled at all. (`forArray` carries no `elem` — its loop var is
                // the array's element and needs no separate statement — so it stays on the default walk.)
                return EvalForEach(obj, ctx);
            case "nullableHasValue":
            case "nullableValue":
                // Both read a structural `Nullable<V>` — `HasValue` and `Value`. An erased slot hands them an
                // `object` instead, which has neither, so the operand narrows first: `unbox.any Nullable<V>` turns a
                // boxed value into a present one and a genuine null into an empty one, which is exactly the pair of
                // states the erasure represents. `x?.f()` on a `T?` param at `T = Int` is this shape.
                return EvalNullableUnwrap(obj, ctx);
            case "valueBlock":
                // The value a statement-then-expression block produces is its `result`'s. Reporting nothing here
                // blinds every use whose operand is one — `t!!.x` puts its null-check temp inside exactly this shape.
                // A valueBlock may carry EITHER of two statement lists and its consumers run `stmts` then `body`, so
                // both are walked; missing one silently drops a whole subtree out of the realignment.
                if (obj["stmts"] != null) Eval(obj["stmts"], ctx);
                if (obj["body"] != null) Eval(obj["body"], ctx);
                return obj["result"] != null ? Eval(obj["result"], ctx) : null;
            default:
                // Unknown statement/expression: recurse every child, then report a `type`/`ret` if it has one.
                foreach (var kv in obj) if (kv.Value != null) Eval(kv.Value, ctx);
                return TypeJson.Read(obj["type"]) ?? TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
        }
    }

    // A node's RESULT TYPE just changed, so the frontend `sty` stamp on it changes with it — the spec §2.7
    // invariant, which is a contract on every pass that retypes a result and not on the deriver that reads one.
    // `sty` is a claim about the value the node produces, never a historical note about the node it used to be,
    // and every downstream deriver reads it FIRST (bir-common/NodeType.cs PRECEDENCE).
    //
    // Here the stale stamp is not merely imprecise. `List<object>` (what the erased declaration actually returns)
    // and `List<Nullable<int32>>` (what kotc stamped at the substituted call site) are UNRELATED invariant reified
    // generics — the very reason this pass exists — so a slot declared from the pre-erasure stamp is INVALID IL
    // rather than a diagnosable drop. Two compositions reach it: the erased call sitting LEFT of a suspending
    // operand, where stage 0 declares the plan's spill local from the stamp, and the erased call BEING the
    // suspension, where the awaited state-machine field is declared from it.
    static void RestampSty(JsonObject obj, TypeNode derived)
    {
        if (obj["sty"] != null) obj["sty"] = TypeJson.Write(derived);
    }

    static void EvalChildrenOf(JsonObject obj, string arrayKey, Ctx ctx)
    {
        if (obj[arrayKey] is JsonArray args)
            foreach (var arg in args) if (arg != null) Eval(arg, ctx);
    }

    static void EvalVar(JsonObject obj, Ctx ctx)
    {
        var env = ctx.Env;
        var initType = obj["init"] != null ? Eval(obj["init"], ctx) : null;
        var name = Str(obj["name"]);
        var declType = TypeJson.Read(obj["type"]);
        if (name == null) return;
        // A #120 reify-back local is a deliberate, chain-consistent `!T[]`: its allocation, its element tokens and its
        // trailing `as Array<T>` were collapsed together. Re-deriving its slot from `arrayOfNulls`' declared
        // `Array<T?>` would widen it to `object[]` over a `newarr !T` — the value-type miscompile that collapse exists
        // to prevent (`arrayOf(1,2,3).plus(4)` printing random ints).
        if (NullableGenericErasure.ReifiedArrayVars.Contains(obj))
        {
            env[name] = declType;
            return;
        }
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

    static TypeNode EvalCallInstance(JsonObject obj, Ctx ctx)
    {
        var idx = ctx.Idx;
        var recvType = obj["recv"] != null ? Eval(obj["recv"], ctx) : null;
        var stampedRet = TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
        if (Str(obj["method"]) is not string method) { RealignArgs(obj, null, null, null, ctx); return stampedRet; }

        var nodeOwner = TypeJson.Read(obj["ownerType"]);
        // The corrected owner: prefer the receiver's flowed static type (it may be an erased `Ref<object>`), else the
        // stamped ownerType. A receiver that erased to a BARE `object` names no member at all, so it is not an owner
        // — it is a receiver needing narrowing (below), and the stamped ownerType stays authoritative.
        var erasedRecv = recvType is TypeNode.Fqn { Name: "object", Args: null };
        var owner = (erasedRecv ? null : recvType as TypeNode.Fqn) ?? nodeOwner as TypeNode.Fqn;
        if (owner == null) { RealignArgs(obj, null, null, null, ctx); return stampedRet; }

        // A value returned through an object-erased generic boundary carries the erased instantiation in the
        // receiver flow. Keep every subsequent member dispatch on that same instantiation. For a generic member
        // return (Iterator<T>.next(): T, List<T>.iterator(): Iterator<T>, etc.), substitute the corrected receiver
        // args into the stamped return too; the exact object-erasure gate prevents ordinary widening/narrowing.
        if (nodeOwner is TypeNode.Fqn stampedOwner && !owner.Equals(stampedOwner)
            && IsObjectErasureOf(owner, stampedOwner))
        {
            obj["ownerType"] = TypeJson.Write(owner);
            if (stampedRet != null
                && DeriveKnownReceiverReturn(stampedRet, stampedOwner, owner, method) is TypeNode recvDerived
                && !recvDerived.Equals(stampedRet) && IsObjectErasureOf(recvDerived, stampedRet))
            {
                if (obj["ret"] != null) obj["ret"] = TypeJson.Write(recvDerived);
                if (obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(recvDerived);
                RestampSty(obj, recvDerived);
                stampedRet = recvDerived;
            }
        }

        // THE RECEIVER IS A USE POSITION TOO. A member call whose receiver flowed out of the erasure as a bare
        // `object` — `t!!.tag()` on a `t: T?`, whose null-check temp is the erased slot — must narrow to the
        // member's own owner before dispatch: `callvirt Tagged::tag` on an `object` is not verifiable IL, and at a
        // value instantiation the narrowing is the `unbox.any` that produces a callable receiver at all. Fires only
        // for a bare-`object` receiver against a non-object owner, which is ill-typed however it arose.
        if (erasedRecv && obj["recv"] is JsonObject recvNode
            && owner is not TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any", Args: null }
            && !(Str(recvNode["k"]) == "cast" && TypeJson.Read(recvNode["type"]) is TypeNode rc && rc.Equals(owner)))
        {
            obj["recv"] = new JsonObject
            {
                ["k"] = "cast",
                ["type"] = TypeJson.Write(owner),
                ["e"] = recvNode.DeepClone(),
            };
        }

        var decl = LookupDecl(owner.Name, method, (obj["args"] as JsonArray)?.Count ?? 0, idx);
        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        // THE ARGUMENT AXIS: each parameter slot is `Subst(Erase(declared param))` exactly as the return is; with no
        // local declaration the call's own descriptor stands in (see RealignArgs).
        RealignArgs(obj, decl?.Params, owner.Args, methodArgs, ctx);
        if (decl == null) return stampedRet;   // no declaration, or an ambiguous same-name/same-arity overload set

        var derived = Subst(NullableGenericErasure.EraseNullableTv(decl.Ret), owner.Args, methodArgs);
        if (derived == null) return stampedRet;

        // Rewrite the return ONLY when `derived` is the object-erasure of the stamped return — the exact erasure
        // boundary, never a genuine widen/narrow. Keeps a direct-write `Ref<Int?>` (derived == stamped) untouched.
        if (stampedRet != null && !derived.Equals(stampedRet) && IsObjectErasureOf(derived, stampedRet))
        {
            if (obj["ret"] != null) obj["ret"] = TypeJson.Write(derived);
            if (obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(derived);
            RestampSty(obj, derived);
            return derived;
        }
        return stampedRet;
    }

    static TypeNode EvalCallStatic(JsonObject obj, Ctx ctx)
    {
        var stampedRet = TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);
        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        // A same-module top-level call has no owner at this stage. Re-derive a generic function's declaration from
        // its pre-erasure form, just as EvalCallInstance does for a generic class member.
        NullableTvErasureCallRealign.DeclSig decl = null;
        if (obj["owner"] == null && Str(obj["method"]) is string method)
            ctx.Idx.TopLevel.TryGetValue(method + "|" + ((obj["args"] as JsonArray)?.Count ?? 0), out decl);
        RealignArgs(obj, decl?.Params, null, methodArgs, ctx);
        if (decl == null) return stampedRet;   // no declaration, or an ambiguous same-name/same-arity overload set
        var derived = Subst(NullableGenericErasure.EraseNullableTv(decl.Ret), null, methodArgs);
        if (stampedRet != null && derived != null && !derived.Equals(stampedRet) && IsObjectErasureOf(derived, stampedRet))
        {
            if (obj["ret"] != null) obj["ret"] = TypeJson.Write(derived);
            if (obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(derived);
            RestampSty(obj, derived);
            return derived;
        }
        return stampedRet;
    }

    // A CONSTRUCTION is a call whose declaration is the owner's constructor: its args are typed
    // `Subst(Erase(ctor param), the constructed type's own args)`. `Cell<Int>(null)` is the ctor twin of
    // `pickOr<Int>(null, 7)` and fails identically without this.
    static TypeNode EvalNew(JsonObject obj, Ctx ctx)
    {
        var type = TypeJson.Read(obj["type"]);
        TypeNode[] declParams = null;
        if (type is TypeNode.Fqn owner && ctx.Idx.Ctors.TryGetValue(owner.Name, out var byArity))
            byArity.TryGetValue((obj["args"] as JsonArray)?.Count ?? 0, out declParams);
        RealignArgs(obj, declParams, (type as TypeNode.Fqn)?.Args, null, ctx);
        // The construction may have been RETYPED by the caller's argument realignment before this ran.
        return TypeJson.Read(obj["type"]);
    }

    static readonly HashSet<string> CollectionOwners = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Iterable", "kotlin.collections.MutableIterable",
        "kotlin.collections.Collection", "kotlin.collections.MutableCollection",
        "kotlin.collections.List", "kotlin.collections.MutableList",
        "kotlin.collections.Set", "kotlin.collections.MutableSet",
    };

    static readonly HashSet<string> IteratorOwners = new(StringComparer.Ordinal)
    {
        "kotlin.collections.Iterator", "kotlin.collections.MutableIterator",
        "kotlin.collections.ListIterator", "kotlin.collections.MutableListIterator",
    };

    // Referenced declarations cannot generally be re-derived here: the ref metadata's structured return intentionally
    // drops unresolved generic parameters. Avoid guessing that every occurrence equal to an owner arg came from that
    // arg (a member could independently return String on Owner<String>). Propagate only for Kotlin collection members
    // whose declared owner-arg relationship is fixed and checked structurally below. Local generic owners use their
    // actual pre-erasure declarations through LookupDeclRet instead.
    static TypeNode DeriveKnownReceiverReturn(TypeNode stampedRet, TypeNode.Fqn stampedOwner,
        TypeNode.Fqn correctedOwner, string method)
    {
        if (stampedOwner.Args is not { Length: 1 } fromArgs
            || correctedOwner.Args is not { Length: 1 } toArgs) return stampedRet;
        var from = fromArgs[0];
        var to = toArgs[0];

        // Iterable<E>.iterator(): Iterator<E> (and the mutable/list iterator variants).
        if ((method == "iterator" || method == "listIterator") && CollectionOwners.Contains(stampedOwner.Name)
            && stampedRet is TypeNode.Fqn { Args: { Length: 1 } retArgs } ret
            && IteratorOwners.Contains(ret.Name) && retArgs[0].Equals(from))
            return new TypeNode.Fqn(ret.Name, new[] { to });

        // Iterator<E>.next()/ListIterator<E>.previous(): E.
        if ((method == "next" || method == "previous") && IteratorOwners.Contains(stampedOwner.Name)
            && stampedRet.Equals(from))
            return to;

        // List<E>.get(index): E.
        if (method == "get" && stampedOwner.Name is "kotlin.collections.List" or "kotlin.collections.MutableList"
            && stampedRet.Equals(from))
            return to;

        return stampedRet;
    }

    // The declaration of a LOCAL owner's member, keyed by EXACT name+arity (DefaultArgSplice has already run, so an
    // app-build call carries its real arity). Either component may be a poisoned `null` — an ambiguous
    // same-name/same-arity overload set (AddUnambiguous) — and each caller skips only the component it cannot trust.
    // Referenced (stdlib) owners are intentionally OUT of scope here: the ref.dll surface names `object` (not a bare
    // `Tv`) so a reflected member cannot be re-derived safely — see the header note.
    static DeclSig LookupDecl(string ownerFqn, string method, int argCount, DeclIndex idx)
        => idx.ByOwner.TryGetValue(ownerFqn, out var sigs) && sigs.TryGetValue(method + "|" + argCount, out var local)
            ? local : null;

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

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
// REFERENCED declarations are read the same way (#86 D1): the producing assembly states each erased slot's pre-erasure
// Kotlin type on its `[KotlinNullableGeneric]` carrier, and its physical signature — which IS `Erase(declared)`, with
// generic parameters retained — states the rest. So a cross-module call's return, arguments and constructor slots are
// DERIVED from the real declaration exactly as a local one's are, rather than guessed from the call site. The lookup
// refuses a same-shape overload set outright (ReferenceMetadataIndex.TryNullableGenericSlot): name, static-ness, arity
// and generic arity are all a call gives us, and picking one sibling of an overload set would manufacture the very
// mismatch this pass removes.
static partial class NullableTvErasureCallRealign
{
    // The pre-erasure declaration of one member: its return AND its parameter vector. A use is typed
    // `Subst(Erase(<the matching component>), typeArgs)` — never `Erase(Subst(...))`.
    public sealed class DeclSig
    {
        public TypeNode Ret;
        public TypeNode[] Params;
        // Parameter positions whose declaration the REFERENCED reader deliberately would not state (#86 D1). Null for
        // a same-compilation declaration, which has nothing to refuse. A refused position is NOT the same as an
        // absent one: an absent declaration falls back to the call's own descriptor, a refused one must not, because
        // the descriptor is that same erasure written in the call's substituted vocabulary.
        public bool[] ParamsRefused;
    }

    // Local owner/top-level declarations, captured across ALL roots BEFORE the per-file DEF-side EraseNullableTv
    // mutates declarations in place. ALL members are stored (not only erasure-affected ones): re-deriving `get_v`
    // on a rewritten `Ref<object>` receiver needs the plain `tv{type,0}` declaration too. Ambiguous
    // same-name/same-arity entries are poisoned to null.
    public sealed class DeclIndex
    {
        public readonly Dictionary<string, Dictionary<string, DeclSig>> ByOwner = new(StringComparer.Ordinal);
        public readonly Dictionary<string, DeclSig> TopLevel = new(StringComparer.Ordinal);
        // The file classes THIS compilation declares. A `callStatic` naming one of them is a same-module top-level
        // call — its declaration is in `TopLevel`, keyed by bare name+arity — and a `callStatic` naming any other
        // owner is a call into a referenced assembly. kotc names the owner in both cases, and MemberCallSubstitution
        // fills it in for the referenced calls that arrived without one, so the owner alone cannot tell them apart.
        public readonly HashSet<string> FileClasses = new(StringComparer.Ordinal);
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
        if (topLevel && Str(o["fileClass"]) is string fc && fc.Length > 0) idx.FileClasses.Add(fc);
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
    // The REFERENCED declarations (#86 D1). A slot whose declaration is not in this compilation is read off the
    // producing assembly instead — its `[KotlinNullableGeneric]` carrier where the erasure recorded one, its physical
    // signature otherwise — and typed by the identical formula. Null when the build has no references.
    static ReferenceMetadataIndex _refs;

    public static void Apply(JsonNode root, DeclIndex idx, Func<string, bool> isValue, ReferenceMetadataIndex refs)
    {
        _isValue = isValue ?? (_ => false);
        _refs = refs;
        ApplyRec(root, idx);
    }

    // The CROSS-MODULE half (#86 D1) — the same formula and the same code, run once more at a later point in the
    // pipeline. It exists because of WHEN a referenced callee acquires an owner: kotc emits a referenced top-level call
    // as `callStatic owner=null`, and only MemberCallSubstitution attributes it to the file class the reference index
    // is keyed by, so those calls have no resolvable declaration on the first run. Every rewrite here is gated on a
    // difference plus the object-erasure relation, so re-deriving a slot the first run already corrected is a no-op.
    public static void ApplyReferenced(JsonNode root, DeclIndex idx, Func<string, bool> isValue, ReferenceMetadataIndex refs)
        => Apply(root, idx, isValue, refs);

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
            case string ck when IsClrBoundKind(ck):
                return EvalClrCall(obj, ctx);
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
                // consumer inside the loop body be reconciled at all.
                return EvalForEach(obj, ctx);
            case "forArray":
                // `for (x in arr)` states BOTH the `ldelem` token and the loop variable's slot in one `elem`, so it is
                // the read axis at an array element exactly as `arrayGet` is — and it must be re-derived from the array
                // that actually flows. An `Array<Int?>` param is `object[]` (#86 D2) while the stamp taken before the
                // erasure still says `Nullable<int32>`; leaving it emits `ldelem Nullable<int32>` over an `object[]`.
                return EvalForArray(obj, ctx);
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
        if (declType != null && initType != null && !initType.Equals(declType) && IsObjectErasureOf(initType, declType))
        {
            // A DIFFERENCE THE CLR CAN CONVERT keeps the declared slot and wraps the init: the whole value erased to a
            // TOP-LEVEL `object` (`val x: Int? = r.v`), reconciled by `unbox.any` for a value declared type and
            // `castclass` for a reference one. Anything else is retyped below.
            if (obj["init"] is JsonObject initNode
                && CastForTarget(initNode, initType, declType) is JsonNode wrappedInit)
            {
                obj["init"] = wrappedInit;
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
        var stampedRet = StampedResult(obj);
        if (Str(obj["method"]) is not string method) { RealignArgs(obj, null, null, null, null, ctx); return stampedRet; }

        var nodeOwner = TypeJson.Read(obj["ownerType"]);
        // The corrected owner: prefer the receiver's flowed static type (it may be an erased `Ref<object>`), else the
        // stamped ownerType. A receiver that erased to a BARE `object` names no member at all, so it is not an owner
        // — it is a receiver needing narrowing (below), and the stamped ownerType stays authoritative.
        var erasedRecv = recvType is TypeNode.Fqn { Name: "object", Args: null };
        var owner = (erasedRecv ? null : recvType as TypeNode.Fqn) ?? nodeOwner as TypeNode.Fqn;
        if (owner == null) { RealignArgs(obj, null, null, null, null, ctx); return stampedRet; }

        // A value returned through an object-erased generic boundary carries the erased instantiation in the
        // receiver flow. Keep every subsequent member dispatch on that same instantiation; the member's own return is
        // then re-derived from its declaration against those corrected owner args, below.
        if (nodeOwner is TypeNode.Fqn stampedOwner && !owner.Equals(stampedOwner)
            && IsObjectErasureOf(owner, stampedOwner))
            obj["ownerType"] = TypeJson.Write(owner);

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

        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        var decl = LookupDecl(owner.Name, method, (obj["args"] as JsonArray)?.Count ?? 0,
            methodArgs?.Length ?? 0, isStatic: false, idx);
        // Before anything is derived FROM the type arguments, give them their physical form (#86 D2).
        CanonicalizeArrayTypeArgs(obj, decl, methodArgs);
        // THE ARGUMENT AXIS: each parameter slot is `Subst(Erase(declared param))` exactly as the return is; with no
        // declaration the call's own descriptor stands in (see RealignArgs).
        RealignArgs(obj, decl?.Params, decl?.ParamsRefused, owner.Args, methodArgs, ctx);
        if (decl?.Ret == null) return stampedRet;   // no declaration, or an ambiguous same-name/same-arity overload set

        var erasedRet = NullableGenericErasure.EraseNullableTv(decl.Ret, _isValue);
        var derived = Subst(erasedRet, owner.Args, methodArgs);
        return ApplyDerivedRet(obj, derived, stampedRet, !erasedRet.Equals(decl.Ret));
    }

    static TypeNode EvalCallStatic(JsonObject obj, Ctx ctx)
    {
        var stampedRet = StampedResult(obj);
        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        var argCount = (obj["args"] as JsonArray)?.Count ?? 0;
        // A top-level fun's declaration lives in one of two indexes, and the OWNER decides which: this compilation's own
        // file class means the same-module `TopLevel` index (keyed by bare name+arity), anything else means a call into
        // a referenced assembly. Both re-derive the generic function's declaration from its pre-erasure form, just as
        // EvalCallInstance does for a generic class member.
        NullableTvErasureCallRealign.DeclSig decl = null;
        TypeNode[] ownerArgs = null;
        if (Str(obj["method"]) is string method)
        {
            var owner = StaticOwner(obj) as TypeNode.Fqn;
            if (owner != null && !ctx.Idx.FileClasses.Contains(owner.Name))
            {
                decl = LookupDecl(owner.Name, method, argCount, methodArgs?.Length ?? 0, isStatic: true, ctx.Idx);
                ownerArgs = owner.Args;
            }
            else
                ctx.Idx.TopLevel.TryGetValue(method + "|" + argCount, out decl);
        }
        // Before anything is derived FROM the type arguments, give them their physical form (#86 D2).
        CanonicalizeArrayTypeArgs(obj, decl, methodArgs);
        RealignArgs(obj, decl?.Params, decl?.ParamsRefused, ownerArgs, methodArgs, ctx);
        if (decl?.Ret == null) return stampedRet;   // no declaration, or an ambiguous same-name/same-arity overload set
        var erasedRet = NullableGenericErasure.EraseNullableTv(decl.Ret, _isValue);
        var derived = Subst(erasedRet, ownerArgs, methodArgs);
        return ApplyDerivedRet(obj, derived, stampedRet, !erasedRet.Equals(decl.Ret));
    }

    // What the CALL SITE says its result is: the explicit `ret`/`dynRet` it carries.
    //
    // NOT the frontend `sty` stamp, and that is a MEASURED limit rather than an oversight. kotc writes an explicit
    // `ret` for some generic calls and, for the rest, only `sty` — so a cross-module generic factory
    // (`holderOf<String>(3)`, which says `Vault<String?>` in `sty` and nothing else) is outside this axis, and its
    // erased `Vault<object>` return still meets the consumer's restored `Vault<string>` slot as a formal-only
    // ilverify finding. Deriving from `sty` and WRITING the `ret` does close that one, and was tried: it then reaches
    // the same call's function-type ARGUMENT, whose delegate the consumer cannot yet build at the erased shape (the
    // parameter half of the func-slot erasure), turning one formal finding into two `DelegateCtor` ones. So the
    // `sty`-only call shape lands with the func-slot parameter erasure, not before it.
    static TypeNode StampedResult(JsonObject obj)
        => TypeJson.Read(obj["dynRet"]) ?? TypeJson.Read(obj["ret"]);

    // Take the derived result type, ONLY when it is the object-erasure of what the call site stamped — the exact
    // erasure boundary, never a genuine widen/narrow. A direct-write `Ref<Int?>` (derived == stamped) is untouched.
    //
    // A call may state its result in `ret`/`dynRet` or only in the frontend `sty` stamp — kotc emits an explicit `ret`
    // for some generic calls and leaves others to be resolved from the member. Both are the same claim about the same
    // value, so both are read, and a rewrite STATES the corrected type in `ret` even where none was written before:
    // that is what stops ilemit re-inferring the member's return from a call whose surrounding slots have moved, and
    // it is the same shape UncheckedGenericCastReturnErasure.ApplyReferenced uses at this boundary. `sty` moves with
    // it, per spec §2.7 — a stamp is a claim about the value produced, never a note about the node it used to be.
    // A call with NO result stamp at all is still in the axis when the DECLARATION itself was erased — `firstTwo(xs):
    // Array<T?>` states `object[]` whatever `T` is, and a caller binding it to an `Array<String?>` slot (`string[]`)
    // has nothing to reconcile against unless the call says what it produces. Only that case writes a `ret` where
    // none stood: `erasureApplied` is false wherever `Erase` left the declared return alone, so an ordinary generic
    // call keeps stating nothing and ilemit keeps inferring it from the member exactly as before.
    static TypeNode ApplyDerivedRet(JsonObject obj, TypeNode derived, TypeNode stampedRet, bool erasureApplied)
    {
        if (derived == null) return stampedRet;
        if (stampedRet == null)
        {
            if (!erasureApplied) return null;
            obj["ret"] = TypeJson.Write(derived);
            RestampSty(obj, derived);
            return derived;
        }
        if (derived.Equals(stampedRet) || !IsObjectErasureOf(derived, stampedRet)) return stampedRet;
        obj["ret"] = TypeJson.Write(derived);
        if (obj["dynRet"] != null) obj["dynRet"] = TypeJson.Write(derived);
        RestampSty(obj, derived);
        return derived;
    }

    // The owner of a static call. kotc names a cross-module callee's file class in `ownerType`; MemberCallSubstitution
    // later restates it as `owner`. A same-module top-level call has neither, and falls to the TopLevel index.
    static TypeNode StaticOwner(JsonObject obj)
        => TypeJson.Read(obj["ownerType"]) ?? TypeJson.Read(obj["owner"]) ?? TypeJson.Read(obj["calleeOwner"]);

    // A CONSTRUCTION is a call whose declaration is the owner's constructor: its args are typed
    // `Subst(Erase(ctor param), the constructed type's own args)`. `Cell<Int>(null)` is the ctor twin of
    // `pickOr<Int>(null, 7)` and fails identically without this.
    static TypeNode EvalNew(JsonObject obj, Ctx ctx)
    {
        var type = TypeJson.Read(obj["type"]);
        var argCount = (obj["args"] as JsonArray)?.Count ?? 0;
        TypeNode[] declParams = null;
        bool[] declRefused = null;
        if (type is TypeNode.Fqn owner)
        {
            if (ctx.Idx.Ctors.TryGetValue(owner.Name, out var byArity)) byArity.TryGetValue(argCount, out declParams);
            // A REFERENCED owner's constructor is a declaration like any other (#86 D1): `Slot<T>(value: T)` erases to
            // `.ctor(!0)`, so a `Slot<object>` retyped by the caller's argument realignment must BOX its argument.
            else if (_refs != null
                     && _refs.TryNullableGenericCtorSlot(owner.Name, argCount, out var refParams, out var refRefused))
            {
                declParams = refParams;
                declRefused = refRefused;
            }
        }
        RealignArgs(obj, declParams, declRefused, (type as TypeNode.Fqn)?.Args, null, ctx);
        // The construction may have been RETYPED by the caller's argument realignment before this ran.
        return TypeJson.Read(obj["type"]);
    }

    // The declaration of a member, keyed by EXACT name+arity (DefaultArgSplice has already run, so an app-build call
    // carries its real arity). A LOCAL owner's declaration is the pre-erasure one this compilation collected; either
    // component may be a poisoned `null` — an ambiguous same-name/same-arity overload set (AddUnambiguous) — and each
    // caller skips only the component it cannot trust.
    //
    // A REFERENCED owner's declaration comes from the producing assembly (#86 D1): its `[KotlinNullableGeneric]`
    // carrier at each slot the erasure rewrote, its physical signature (which IS `Erase(declared)`) elsewhere. That is
    // the real declaration, so the general `Subst(Erase(decl))` rule covers a referenced generic member — including
    // `Iterable<E>.iterator()`, `Iterator<E>.next()` and `List<E>.get(i)` on a receiver corrected to its erased
    // instantiation, which a hardcoded member table used to approximate.
    static DeclSig LookupDecl(string ownerFqn, string method, int argCount, int methodArity, bool isStatic, DeclIndex idx)
    {
        if (idx.ByOwner.TryGetValue(ownerFqn, out var sigs))
            return sigs.TryGetValue(method + "|" + argCount, out var local) ? local : null;
        return LookupReferencedDecl(ownerFqn, method, argCount, methodArity, isStatic);
    }

    static DeclSig LookupReferencedDecl(string ownerFqn, string method, int argCount, int methodArity, bool isStatic)
        => _refs != null
           && _refs.TryNullableGenericSlot(ownerFqn, method, isStatic, argCount, methodArity,
               out var ret, out var ps, out var refused)
            ? new DeclSig { Ret = ret, Params = ps, ParamsRefused = refused }
            : null;

    // D2'S TYPE-ARGUMENT HALF, at the RESULT. A callee that hands back a bare `Array<T>` produces a physical `!!T[]`,
    // and the caller's slot for it is `Array<Int?>` = `object[]` when `T` binds to a nullable value type — so the only
    // instantiation whose result the caller can hold is `T = object`. `arrayOf<Int?>(1, null)` is the shape: its own
    // vararg pack is built here and states nothing, but what it RETURNS has to be the array the slot names.
    //
    // This is NOT the rejected call-site type-argument collapse: the argument is not weakened because the callee is
    // generic, it is weakened because ONE physical slot — an array element — has a single representation and the
    // instantiation has to name it. A type argument that never reaches an array is untouched, so `listOf<Int?>` keeps
    // its `List<Nullable<int32>>`. The PARAMETER side is settled by what actually flows (UnifyMethodArgs), which needs
    // no guess about whether the array already exists.
    //
    // Only a BARE type variable DIRECTLY under an `Array` constrains its argument. `Array<T?>` is `object[]` whatever
    // `T` is, so it says nothing about `T`; and `Array<List<T>>` is `List<!!T>[]`, whose element genuinely stays
    // `List<Nullable<int32>>` — forcing `object` there would name an array the caller does not hold.
    static void CanonicalizeArrayTypeArgs(JsonObject obj, DeclSig decl, TypeNode[] methodArgs)
    {
        if (decl == null || methodArgs == null || obj["typeArgs"] is not JsonArray typeArgs) return;
        for (var i = 0; i < methodArgs.Length && i < typeArgs.Count; i++)
        {
            if (!NullableGenericErasure.IsNullableMaybeValue(methodArgs[i], _isValue)) continue;
            if (!MentionsArrayOf(decl.Ret, i)) continue;
            methodArgs[i] = new TypeNode.Fqn("object");
            typeArgs[i] = TypeJson.Fqn("object");
        }
    }

    static bool MentionsArrayOf(TypeNode t, int i) => t switch
    {
        null => false,
        TypeNode.Array a => IsDirectElement(a.Elem, i) || MentionsArrayOf(a.Elem, i),
        TypeNode.Fqn { Args: { } args } => args.Any(x => MentionsArrayOf(x, i)),
        TypeNode.Nullable n => MentionsArrayOf(n.Of, i),
        TypeNode.Oblivious o => MentionsArrayOf(o.Of, i),
        TypeNode.ByRef b => MentionsArrayOf(b.Of, i),
        TypeNode.Fn fn => MentionsArrayOf(fn.Ret, i) || fn.Params.Any(p => MentionsArrayOf(p, i))
                          || (fn.Recv != null && MentionsArrayOf(fn.Recv, i)),
        _ => false,
    };

    // The element of an `Array<…>`, unwrapping further array nesting: `Array<Array<T>>` constrains `T` exactly as
    // `Array<T>` does, because `!!T[][]` is an `object[][]` only at `T = object`.
    static bool IsDirectElement(TypeNode elem, int i) => elem switch
    {
        TypeNode.Tv { Scope: "method" } tv => tv.I == i,
        TypeNode.Array a => IsDirectElement(a.Elem, i),
        _ => false,
    };

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

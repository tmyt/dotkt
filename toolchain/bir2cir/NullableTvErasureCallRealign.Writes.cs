using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// THE WRITE AXIS of the nullable-Tv erasure realignment (#86) — the other half of the formula its sibling file
// applies to reads. A read asks "what type does this node PRODUCE"; a write asks "what type does this fixed slot
// ACCEPT", and the answer has the same shape: `Subst(Erase(declaredKotlinType(slot)), typeArgs)`.
//
// The positions, and what fixes each:
//   setLocal / var         the local's (possibly already realigned) declared type
//   setField               `Subst(Erase(field decl), owner args)`
//   arraySet               the flowed array's element type — and the `stelem` token is restamped with it
//   return                 the enclosing method's own (already-erased) return type
//   cond                   the join slot's `type`, against BOTH branch values
//   call / ctor arguments  `Subst(Erase(declared param), owner args, method args)` — the `sig`/`argTypes`
//                          descriptor is realigned WITH the value, because a stale descriptor makes ilemit
//                          resolve a member that does not exist (EntryPointNotFound), not merely mistype a stack slot
//
// THE ONLY CASTABLE SEAM IS `object`. `box` carries a value or a `Nullable<V>` into `object` (an empty
// `Nullable<V>` boxes to a genuine null), and `unbox.any`/`castclass` carries it back. A difference sitting INSIDE a
// constructed generic — `Ref<object>` against `Ref<Nullable<int32>>` — is NOT castable at all: those are unrelated
// invariant reified generics and a `castclass` between them throws. Those are made to agree by DERIVING the use
// type (the read axis), never by converting the value, so this half leaves them alone.
static partial class NullableTvErasureCallRealign
{
    // Realign every argument position of a call/construction against the slot it fills.
    //
    // With the callee's DECLARATION in hand the target is `Subst(Erase(p), ownerArgs, methodArgs)` — never
    // `Erase(Subst(...))`, which is the distinction the whole family turns on: substituting first destroys the `Tv`
    // that tells `Erase` this position was erased at all. The signature DESCRIPTOR is corrected to the same type,
    // because it is what the emitter resolves the member by: leave it substituted and the emitter looks for a member
    // that does not exist.
    //
    // With no declaration — a callee whose owner names no indexed declaration, or an ambiguous overload set the
    // reference index refuses to guess at — the descriptor is the only statement of the slot there is, and it is
    // authoritative precisely because it is what the member will be resolved by. That fallback is restricted to the
    // `object` seam: a value flowed out of the erasure as a bare `object` and the descriptor names a slot the CLR
    // cannot hand an `object` to. Anything wider would be guessing against a descriptor that may itself be the
    // substituted (pre-erasure) view.
    // Evaluates the arguments as it goes, because one rewrite has to happen BEFORE an argument is evaluated: see the
    // construction case below. Reports the flowed argument types (null when the node has no `args`).
    static TypeNode[] RealignArgs(JsonObject call, TypeNode[] declParams, TypeNode[] ownerArgs, TypeNode[] methodArgs,
        Ctx ctx)
    {
        if (call["args"] is not JsonArray args) return null;
        // A callStatic/callInstance carries `sig`; a `new` carries `argTypes`; a call NetInteropBinding has already
        // bound to a .NET member carries the same vector as `memberSig` — the name changes, the fact does not, and
        // reading only two of the three left every .NET-interop argument outside the axis (an `object` from an erased
        // stdlib return handed to a `Nullable<bool>` parameter, with nothing to narrow it). Any of them may be absent
        // (resolution then falls back to arity), which is not an error.
        var descriptor = call["sig"] as JsonArray ?? call["argTypes"] as JsonArray ?? call["memberSig"] as JsonArray;
        if (descriptor != null && descriptor.Count != args.Count) descriptor = null;
        var haveDecl = declParams != null && declParams.Length == args.Count;
        var argTypes = new TypeNode[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            // A declared slot may be individually unknown (a referenced parameter whose declaration the producing
            // assembly could not state structurally); that position falls back to the descriptor like an undeclared call.
            var target = haveDecl && declParams[i] != null
                ? Subst(NullableGenericErasure.EraseNullableTv(declParams[i]), ownerArgs, methodArgs)
                : null;
            // A CONSTRUCTION whose instantiation is the erasure counterpart of the slot is RETYPED rather than
            // converted, and before it is evaluated so its own constructor arguments are reconciled against the
            // corrected instantiation. `Box<Nullable<int32>>` and `Box<object>` are unrelated invariant reified
            // generics that no cast reconciles — but the construction is ours to type, so we build the one the slot
            // names instead of building the wrong one and failing to convert it.
            if (target is TypeNode.Fqn tf && args[i] is JsonObject na && Str(na["k"]) == "new"
                && TypeJson.Read(na["type"]) is TypeNode.Fqn sf
                && sf.Name == tf.Name && !sf.Equals(tf) && IsObjectErasureOf(tf, sf))
                na["type"] = TypeJson.Write(tf);
            // The same rule for an ARRAY construction, which is how a `vararg` argument list is packed: build the
            // element type the slot names. `EvalNewArray` then reconciles the elements against it, so the `newarr`
            // and every `stelem` filling it agree by construction.
            if (target is TypeNode.Array ta && args[i] is JsonObject ar
                && Str(ar["k"]) is "newArray" or "newArrayInit" or "newArraySized"
                && TypeJson.Read(ar["elem"]) is TypeNode se
                && !se.Equals(ta.Elem) && IsObjectErasureOf(ta.Elem, se))
                ar["elem"] = TypeJson.Write(ta.Elem);
            argTypes[i] = args[i] != null ? Eval(args[i], ctx) : null;
            if (target != null)
            {
                if (descriptor != null && TypeJson.Read(descriptor[i]) is TypeNode stamped
                    && !stamped.Equals(target) && IsObjectErasureOf(target, stamped))
                    descriptor[i] = TypeJson.Write(target);
            }
            else if (descriptor != null && TypeJson.Read(descriptor[i]) is TypeNode slot
                     && IsBareObject(argTypes[i]) && NeedsObjectSeam(slot))
                target = slot;
            if (target != null && args[i] is JsonObject arg && CastForTarget(arg, argTypes[i], target) is JsonNode wrapped)
                args[i] = wrapped;
        }
        return argTypes;
    }

    // A call NetInteropBinding has already BOUND to a .NET member. Only the WRITE axis applies to it: the callee is
    // .NET, so its declared parameter types (`memberSig`) ARE the declaration and there is nothing Kotlin to
    // re-derive — but an argument that flowed out of the erasure as a bare `object` still has to be narrowed into the
    // slot that member declares, or the emitter pushes an `object` where a `Nullable<bool>` is required and the whole
    // method fails verification. Its own result is the .NET member's and is never re-derived.
    //
    // This is the arm the erasure family reaches once a call crosses into .NET: `assertTrue(map.merge(…))` hands an
    // erased stdlib return straight to `ClassicAssert.IsTrue(bool?)`.
    static TypeNode EvalClrCall(JsonObject obj, Ctx ctx)
    {
        if (obj["recv"] != null) Eval(obj["recv"], ctx);
        RealignArgs(obj, null, null, null, ctx);
        // Whatever else the node carries (an index expression, a value) still needs walking; the descriptor keys and
        // the owner `type` are not operands.
        foreach (var kv in obj)
            if (kv.Value != null
                && kv.Key is not ("recv" or "args" or "k" or "sty" or "type" or "ret" or "memberSig" or "argTypes" or "sig"))
                Eval(kv.Value, ctx);
        return Str(obj["k"]) == "newClr" ? TypeJson.Read(obj["type"]) : TypeJson.Read(obj["ret"]);
    }

    // An array construction, and the ELEMENTS it is built from, are one operation. The `elem` may already have been
    // corrected by the caller's argument realignment (a `vararg xs: T?` pack fills an `Array<T?>` slot, erased to
    // `object[]`), and each element must then be reconciled against THAT element type — a pack built as
    // `Nullable<int32>[]` and handed to an `object[]` slot is not convertible after the fact, while a pack built as
    // `object[]` with boxed elements is exactly right. Splitting the two is what makes a `stelem` disagree with the
    // `newarr` that produced the array.
    static TypeNode EvalNewArray(JsonObject obj, Ctx ctx)
    {
        var elem = TypeJson.Read(obj["elem"]);
        if (obj["size"] != null) Eval(obj["size"], ctx);
        if (obj["elems"] is JsonArray elems)
            for (var i = 0; i < elems.Count; i++)
            {
                if (elems[i] is not JsonObject e) continue;
                var t = Eval(e, ctx);
                if (elem != null && CastForTarget(e, t, elem) is JsonNode wrapped) elems[i] = wrapped;
            }
        foreach (var kv in obj)
            if (kv.Value != null && kv.Key is not ("elem" or "elems" or "size" or "k" or "sty"))
                Eval(kv.Value, ctx);
        return elem == null ? null : new TypeNode.Array(elem);
    }

    // An inline iteration binds a loop VARIABLE, whose type is the node's `elem`. Registering it makes the body's
    // reads of that variable flow with a type instead of none, which is what lets a value consumer inside the loop be
    // reconciled at all.
    //
    // The `elem` itself is NOT re-derived from the source here. Erasing a loop element and re-narrowing it at its
    // value consumers is one atomic decision, and the narrowing target is the element's PRE-erasure type, which no
    // longer exists at this point in the pipeline — so both halves stay together in the declaration-axis pass that
    // still has it (NullableGenericErasure's forEach handling). Restamping here without the matching narrow is
    // precisely the `stelem`-over-a-typed-slot miscompile that pairing exists to prevent.
    static TypeNode EvalForEach(JsonObject obj, Ctx ctx)
    {
        if (obj["src"] != null) Eval(obj["src"], ctx);
        if (Str(obj["var"]) is string v && TypeJson.Read(obj["elem"]) is TypeNode elem) ctx.Env[v] = elem;
        if (obj["body"] != null) Eval(obj["body"], ctx);
        foreach (var kv in obj)
            if (kv.Value != null && kv.Key is not ("src" or "body" or "elem" or "k" or "var" or "label"))
                Eval(kv.Value, ctx);
        return null;
    }

    // `nullableHasValue`/`nullableValue` unwrap a structural `Nullable<V>`; the node's own `elem` names the `V`.
    // Narrow an erased `object` operand to that `Nullable<V>` first. The result is `bool` for the test and the bare
    // `V` for the read.
    static TypeNode EvalNullableUnwrap(JsonObject obj, Ctx ctx)
    {
        var srcType = obj["e"] != null ? Eval(obj["e"], ctx) : null;
        var elem = TypeJson.Read(obj["elem"]);
        if (elem != null && obj["e"] is JsonObject e
            && CastForTarget(e, srcType, new TypeNode.Nullable(elem)) is JsonNode wrapped)
            obj["e"] = wrapped;
        return Str(obj["k"]) == "nullableValue" ? elem : new TypeNode.Fqn("kotlin.Boolean");
    }

    // A base/this DELEGATION argument list. Unlike a call it carries no signature descriptor — the delegated
    // constructor is selected by arity — so there is nothing to correct but the values themselves.
    static void RealignDelegation(JsonArray args, TypeNode[] declParams, TypeNode[] ownerArgs, TypeNode[] argTypes)
    {
        if (declParams == null || declParams.Length != args.Count) return;
        for (var i = 0; i < args.Count; i++)
            if (declParams[i] != null
                && Subst(NullableGenericErasure.EraseNullableTv(declParams[i]), ownerArgs, null) is TypeNode target
                && args[i] is JsonObject arg && CastForTarget(arg, argTypes[i], target) is JsonNode wrapped)
                args[i] = wrapped;
    }

    static void EvalSetLocal(JsonObject obj, Ctx ctx)
    {
        var valueType = obj["value"] != null ? Eval(obj["value"], ctx) : null;
        if (Str(obj["name"]) is not string name || ctx.Env.GetValueOrDefault(name) is not TypeNode target) return;
        if (obj["value"] is JsonObject v && CastForTarget(v, valueType, target) is JsonNode wrapped)
            obj["value"] = wrapped;
    }

    static void EvalSetField(JsonObject obj, Ctx ctx)
    {
        if (obj["recv"] != null) Eval(obj["recv"], ctx);
        var valueType = obj["value"] != null ? Eval(obj["value"], ctx) : null;
        if (SlotType(obj, ctx) is not TypeNode target) return;
        if (obj["value"] is JsonObject v && CastForTarget(v, valueType, target) is JsonNode wrapped)
            obj["value"] = wrapped;
    }

    // A `field` read produces `Subst(Erase(field decl), owner args)` — the same derivation the store side uses, so a
    // read of an object-erased field flows as `object` and its consumer re-narrows once.
    static TypeNode EvalField(JsonObject obj, Ctx ctx)
    {
        if (obj["recv"] != null) Eval(obj["recv"], ctx);
        if (SlotType(obj, ctx) is not TypeNode derived) return TypeJson.Read(obj["type"]);
        if (TypeJson.Read(obj["type"]) is TypeNode stamped && !stamped.Equals(derived) && IsObjectErasureOf(derived, stamped))
        {
            obj["type"] = TypeJson.Write(derived);
            RestampSty(obj, derived);
        }
        else if (TypeJson.Read(obj["sty"]) is TypeNode sty && !sty.Equals(derived) && IsObjectErasureOf(derived, sty))
            RestampSty(obj, derived);
        return derived;
    }

    // The declared type of the field/property a `field`/`setField` node names, substituted with the owner's args.
    static TypeNode SlotType(JsonObject obj, Ctx ctx)
    {
        if (TypeJson.Read(obj["ownerType"]) is not TypeNode.Fqn owner || Str(obj["name"]) is not string name) return null;
        if (!ctx.Idx.Slots.TryGetValue(owner.Name, out var slots)) return null;
        if (!slots.TryGetValue(name, out var declared) || declared == null) return null;
        return Subst(NullableGenericErasure.EraseNullableTv(declared), owner.Args, null);
    }

    static void EvalArraySet(JsonObject obj, Ctx ctx)
    {
        var arrType = obj["array"] != null ? Eval(obj["array"], ctx) : null;
        if (obj["index"] != null) Eval(obj["index"], ctx);
        var valueType = obj["value"] != null ? Eval(obj["value"], ctx) : null;
        if (arrType is not TypeNode.Array arr) return;
        // The `stelem` token is the array's element type, exactly as `arrayGet`'s `ldelem` token is.
        if (TypeJson.Read(obj["elem"]) is TypeNode cur && !cur.Equals(arr.Elem) && IsObjectErasureOf(arr.Elem, cur))
            obj["elem"] = TypeJson.Write(arr.Elem);
        if (obj["value"] is JsonObject v && CastForTarget(v, valueType, arr.Elem) is JsonNode wrapped)
            obj["value"] = wrapped;
    }

    static void EvalReturn(JsonObject obj, Ctx ctx)
    {
        var valueType = obj["value"] != null ? Eval(obj["value"], ctx) : null;
        if (ctx.Ret is not TypeNode target) return;
        if (obj["value"] is JsonObject v && CastForTarget(v, valueType, target) is JsonNode wrapped)
            obj["value"] = wrapped;
    }

    // An `if/else` in value position is a JOIN: both branches must inhabit the declared join slot. A branch that came
    // through the erasure boundary carries `object` where the slot is a value/type-variable, and the branch that did
    // not carries the concrete type — the two only meet at the slot.
    static TypeNode EvalCond(JsonObject obj, Ctx ctx)
    {
        if (obj["cond"] != null) Eval(obj["cond"], ctx);
        var thenType = obj["then"] != null ? Eval(obj["then"], ctx) : null;
        var elseType = obj["else"] != null ? Eval(obj["else"], ctx) : null;
        // Recurse whatever else the node carries (a `cond` with extra operand keys must not lose them).
        foreach (var kv in obj)
            if (kv.Value != null && kv.Key is not ("cond" or "then" or "else" or "k" or "type" or "sty"))
                Eval(kv.Value, ctx);
        var joinType = TypeJson.Read(obj["type"]);
        if (joinType == null) return thenType ?? elseType;
        if (obj["then"] is JsonObject t && CastForTarget(t, thenType, joinType) is JsonNode wt) obj["then"] = wt;
        if (obj["else"] is JsonObject e && CastForTarget(e, elseType, joinType) is JsonNode we) obj["else"] = we;
        return joinType;
    }

    // The conversion a value needs to inhabit `target`, or null when it needs none / none is expressible.
    //
    // Exactly one side must be a bare `object` — the erased form — and which side it is decides the rule, because
    // the CLR's own assignment rule is not symmetric:
    //   * `object` -> X is a NARROWING and always needs the conversion, whatever X is: `unbox.any` for a value or a
    //     type variable, `castclass` for a reference. This is the position an erased slot is READ at.
    //   * X -> `object` is a WIDENING and needs one only when X is not already a reference: `box` for a value, a
    //     structural `Nullable<V>` (whose empty case boxes to a genuine null), or a type variable.
    // A difference nested inside a constructed generic is NOT castable in either direction — `Ref<object>` and
    // `Ref<Nullable<int32>>` are unrelated invariant reified generics — and is left to the read-side derivation.
    static JsonNode CastForTarget(JsonNode value, TypeNode src, TypeNode target)
    {
        if (value is not JsonObject vo || src == null || target == null || src.Equals(target)) return null;
        var srcObj = IsBareObject(src);
        var tgtObj = IsBareObject(target);
        if (srcObj == tgtObj) return null;                                   // neither side is the erased form
        if (srcObj ? IsVoidish(target) : !NeedsObjectSeam(src)) return null;
        // A node that never yields a value (a `throw` in value position, which is TERMINATED where it stands) has
        // nothing to convert, and wrapping it would state a stack value the emitter must then produce.
        if (Str(vo["k"]) is "throwExpr" or "throw") return null;
        // Idempotence: never re-wrap a cast that already states the target.
        if (Str(vo["k"]) == "cast" && TypeJson.Read(vo["type"]) is TypeNode ct && ct.Equals(target)) return null;
        return new JsonObject
        {
            ["k"] = "cast",
            ["type"] = TypeJson.Write(target),
            ["e"] = vo.DeepClone(),
        };
    }

    static bool IsBareObject(TypeNode t) => t is TypeNode.Fqn { Name: "object", Args: null };

    // `Unit`/`void` is the absence of a value, not a type to convert into.
    static bool IsVoidish(TypeNode t) => t is TypeNode.Fqn { Name: "void" or "kotlin.Unit" or "kotlin.Nothing", Args: null };

    // Whether widening this type into `object` needs a real IL conversion. A `Tv` does (it may be instantiated with a
    // struct, and `box` on a type variable is the verifier-clean form for both instantiations); a structural
    // `Nullable<V>` does; a struct does. A reference is already an `object` and needs nothing.
    static bool NeedsObjectSeam(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => NeedsObjectSeam(n.Of),
        TypeNode.Fqn { Args: null } f => _isValue(f.Name),
        _ => false,
    };
}

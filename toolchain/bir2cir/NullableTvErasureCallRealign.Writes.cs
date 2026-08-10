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
//   call / ctor arguments  `Subst(Erase(declared param), owner args, method args)` for the VALUE conversion; the
//                          `sig`/`argTypes` descriptor remains the OPEN declaration identity used for exact linking
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
    // that tells `Erase` this position was erased at all. The signature DESCRIPTOR remains the callee's OPEN
    // declaration identity. Only the value target is substituted; replacing `Array<!!0>` with `Array<object>` would
    // make exact linking search for a declaration that does not exist.
    //
    // With no declaration — a callee whose owner names no indexed declaration, or an ambiguous overload set the
    // reference index refuses to guess at — the descriptor is the only statement of the slot there is, and it is
    // authoritative precisely because it is what the member will be resolved by. That fallback is restricted to the
    // `object` seam: a value flowed out of the erasure as a bare `object` and the descriptor names a slot the CLR
    // cannot hand an `object` to. Anything wider would be guessing against a descriptor that may itself be the
    // substituted (pre-erasure) view.
    // Evaluates the arguments as it goes, because one rewrite has to happen BEFORE an argument is evaluated: see the
    // construction case below. Reports the flowed argument types (null when the node has no `args`).
    static TypeNode[] RealignArgs(JsonObject call, TypeNode[] declParams, bool[] declRefused, TypeNode[] ownerArgs,
        TypeNode[] methodArgs, Ctx ctx, bool exactPropertyTarget = false)
    {
        if (call["args"] is not JsonArray args) return null;
        // A callStatic/callInstance carries `sig`; a `new` carries `argTypes`; a call NetInteropBinding has already
        // bound to a .NET member carries the same vector as `memberSig` — the name changes, the fact does not, and
        // reading only two of the three left every .NET-interop argument outside the axis (an `object` from an erased
        // stdlib return handed to a `Nullable<bool>` parameter, with nothing to narrow it). Any of them may be absent
        // (resolution then falls back to arity), which is not an error.
        //
        // WHETHER the descriptor is a resolved CLR signature or Kotlin vocabulary is remembered, because the fallback
        // below trusts the two to different depths — and that is decided by the call's KIND, never by which key holds
        // the vector. A `clr*` node is .NET-bound by construction; its key only records how far resolution has got. A
        // GENERIC .NET call carries `memberSig` from the moment NetInteropBinding reshapes it, while a NON-GENERIC one
        // carries `argTypes` until ClrMemberResolution stamps `memberSig` — which happens long AFTER this pass runs.
        // Reading .NET-boundness off `memberSig`'s presence therefore left every non-generic .NET call on the Kotlin
        // fallback, where the widening screen below drops every reference slot: an erased `object` reached a `string`
        // parameter with no `castclass` at all, and the emitter pushed it into a slot the CLR does not accept.
        var clrBound = IsClrBoundKind(Str(call["k"]));
        var descriptor = call["sig"] as JsonArray ?? call["argTypes"] as JsonArray ?? call["memberSig"] as JsonArray;
        if (descriptor != null && descriptor.Count != args.Count) descriptor = null;
        var haveDecl = declParams != null && declParams.Length == args.Count;
        var haveRefusals = declRefused != null && declRefused.Length == args.Count;
        var argTypes = new TypeNode[args.Count];
        // SETTLE THE METHOD TYPE ARGUMENTS FIRST (#86 D2). What flows into one parameter can force the callee's
        // instantiation — an `object[]` inhabits `!!i[]` at `T = object` and nowhere else — and that instantiation is
        // what every OTHER position is then derived against. Doing it inside the main loop made the result depend on
        // ARGUMENT ORDER: in `fun <T> g(x: T, xs: Array<T>)` called as `g(v, arrayOfNulls<Int>(2))`, `x` was targeted,
        // converted and descriptor-rewritten at `T = Nullable<int32>` before `xs` moved `T` to `object`, and nothing
        // revisited it — so the emitted `g<object>` carried a first argument reconciled against an instantiation that
        // no longer existed. One pass to settle, then one pass that derives everything from a fixed answer.
        //
        // An ARRAY construction still naming its OWN element is skipped: it is built here, so it states nothing the
        // instantiation must match — the main loop types it against the parameter instead, which is the ordering its
        // `new`/pack retyping depends on. An array construction whose element is already the bare `object` is the
        // opposite case and is NOT skipped: that is an `arrayOf<Int?>(…)` the factory has already canonicalized, a real
        // `Array<X?>` value, and it settles the instantiation exactly as a variable holding one would. An OBJECT
        // construction is skipped outright — see [BuildsItsOwnElement] for why that conservative skip is correct here.
        // Everything else is evaluated here and again below; every rewrite on that path is guarded by
        // `IsObjectErasureOf` or an idempotence check, so the second visit finds its own work already done.
        if (haveDecl && methodArgs != null)
            for (var i = 0; i < args.Count; i++)
                if (declParams[i] != null && !(haveRefusals && declRefused[i])
                    && args[i] is JsonObject pre && !BuildsItsOwnElement(pre)
                    && Eval(args[i], ctx) is TypeNode flowed)
                    UnifyMethodArgs(declParams[i], flowed, methodArgs, call);
        for (var i = 0; i < args.Count; i++)
        {
            // A REFUSED slot is not an unknown one. The reader saw this parameter's carrier and decided it must not
            // be stated, and the call's descriptor is that same erasure written in the call's substituted vocabulary.
            // Falling back to it applies precisely the derivation the refusal exists to prevent, so this position gets
            // no target from either source and the argument is left exactly as the frontend typed it. Nothing produces
            // a refusal today — the `Array<X?>` slot that did is served now that #86 D2 made the erasure uniform there
            // — so this is the channel standing ready for the next one rather than a live path.
            var refused = haveRefusals && declRefused[i];
            // A declared slot may instead be individually unknown (a referenced parameter whose declaration the
            // producing assembly could not state structurally); THAT position falls back to the descriptor like an
            // undeclared call.
            var target = !refused && haveDecl && declParams[i] != null
                ? Subst(NullableGenericErasure.EraseNullableTv(declParams[i], _isValue), ownerArgs, methodArgs)
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
            //
            // ONLY when that element is the bare `object` seam — `vararg xs: T?`, whose pack is an `object[]` the
            // elements box into. An element erased INSIDE a constructed generic (`vararg slots: Slot<T?>`, packed as
            // `Slot<object>[]`) is not reconcilable: a `Slot<String?>` the caller holds is a `Slot<string>`, unrelated
            // to `Slot<object>`, so retyping the pack would emit a `stelem Slot<object>` over it and turn a formal
            // stack-type difference into an ArrayTypeMismatchException. The pack keeps the element it was built with
            // there; only the DESCRIPTOR follows the callee, because that is what resolves the member.
            if (target is TypeNode.Array ta && IsBareObject(ta.Elem) && args[i] is JsonObject ar
                && Str(ar["k"]) is "newArray" or "newArrayInit" or "newArraySized"
                && TypeJson.Read(ar["elem"]) is TypeNode se
                && !se.Equals(ta.Elem) && IsObjectErasureOf(ta.Elem, se))
                ar["elem"] = TypeJson.Write(ta.Elem);
            // And the same rule for a DELEGATE construction. `fun <T> invokeNullable(block: (T?) -> String)` declares
            // a physical `Func<object, string>` whatever `T` is, while kotc states the construction's `funcType` from
            // the SUBSTITUTED Kotlin type — `(String?) -> String`, a `Func<string, string>`. Those are two different
            // delegate types, and the one the call accepts is the callee's. The construction is ours to type, so it
            // is BUILT at the slot's shape rather than built wrong and cast (no cast joins two delegate
            // instantiations); DelegateTargetSlotAlignment then makes the lifted target's own slots follow.
            // Only a construction whose TARGET the compiler synthesized: a lifted `newDelegate` and a `newClosure`'s
            // synthetic `invoke` both follow the retyped `funcType` (DelegateTargetSlotAlignment). A delegate over a
            // DECLARED member — `expr::member`, or a `::fn` whose `newDelegate` carries the overload `sig` kotc
            // writes only for that form — points at a signature that is not ours to move, so retyping its delegate
            // would state a shape no target can fill and turn a formal mismatch into an invalid program.
            if (target is TypeNode.Fn && args[i] is JsonObject dl
                && Str(dl["k"]) is "newDelegate" or "newClosure" && dl["sig"] is not JsonArray
                && TypeJson.Read(dl["funcType"]) is TypeNode.Fn dft
                && !dft.Equals(target) && IsObjectErasureOf(target, dft))
                dl["funcType"] = TypeJson.Write(target);
            argTypes[i] = args[i] != null ? Eval(args[i], ctx) : null;
            // THE DESCRIPTOR IS OPEN; THE TARGET MUST BE CLOSED. A descriptor states the callee's DECLARED parameter
            // vector, so a generic callee's slot is still `!!0` there — and `.NET`-bound calls (`memberSig`) keep it
            // that way deliberately, because that open form is what the emitter matches the member by. Converting a
            // value INTO it needs the closed type: substituting the call's own owner/type arguments turns
            // `Enumerable.Repeat<Int?>`'s `!!0` into `Nullable<int32>`, where using the open node emitted a cast to
            // whatever `!!0` lowered to in the CALLER (its own type parameter, or `object`) and pushed an `object`
            // where a `Nullable<int32>` was required. The descriptor itself is left untouched. (Same closed/open
            // split as the #64 await-plan template and its substitution.)
            //
            // The arm is reached only when the value flowed out as a bare `object`, so this is always the NARROWING
            // direction — and narrowing out of `object` needs a conversion whatever the target is, `unbox.any` for a
            // value and `castclass` for a reference. Which one, and whether one is needed at all, is CastForTarget's
            // asymmetric rule. Screening it with the WIDENING test (`NeedsObjectSeam`) drops every reference target,
            // which for the .NET arm left an `object` where a `string` was required — measured at BOTH arities, and
            // the reason that arm is unscreened whatever key its descriptor arrived under. The KOTLIN arms — a
            // `callStatic`/`callInstance`'s `sig` and a Kotlin `new`'s `argTypes` — KEEP the screen: their descriptor
            // is the call's own substituted view rather than a resolved CLR signature, so an unrestricted reference
            // cast there would be typed by something that may not be the callee's slot at all. Widening those needs
            // its own evidence, and there is none yet.
            if (target == null && !refused && descriptor != null && TypeJson.Read(descriptor[i]) is TypeNode slot
                     && IsBareObject(argTypes[i])
                     && Subst(slot, ownerArgs, methodArgs) is TypeNode closed
                     && (clrBound || NeedsObjectSeam(closed)))
                target = closed;
            if (target != null && args[i] is JsonObject arg
                && CastForTarget(arg, argTypes[i], target, exactPropertyTarget) is JsonNode wrapped)
                args[i] = wrapped;
        }
        return argTypes;
    }

    // Settle method type arguments against the type that FLOWED into a parameter (#86 D2). Walks the DECLARED
    // parameter beside the flowed type; wherever the declaration says `!!i` INSIDE an array or a constructed generic
    // and what arrives there says a bare `object`, `!!i` must BE `object`. That is forced by what the value IS, not
    // chosen: `object[]` inhabits `!!i[]` at that instantiation and no other (array compatibility needs
    // reference-compatible elements, ECMA-335 I.8.7.1), and `X<object>` inhabits `X<!!i>` at that one and no other
    // (a reified generic is invariant; a covariant one only ever widens TOWARDS `object`). Reports whether anything
    // moved, and writes the corrected arguments back onto the node so the emitter resolves the same instantiation.
    //
    // NOT at the top-level scalar position. A bare `!!i` parameter handed an erased `object` needs no
    // re-instantiation — the object seam narrows the value into it, keeping the caller's own type argument — so
    // unifying there would weaken `f<String>(erasedValue)` into `f<object>` for nothing. The walk pairs constructed
    // types by ARGUMENT POSITION rather than by name, because the two sides are legitimately different types (a
    // `List<object>` arrives at a `Collection<!!0>` slot) and position is what the CLR's assignability rule aligns.
    //
    // And ONLY for this family: either the binding is the nullable-possibly-value type D2 moves, or the position is
    // an ARRAY ELEMENT, where `object[]` genuinely inhabits one instantiation. Without that
    // gate the pairing-by-position reaches ordinary generic calls it has no business re-instantiating — a
    // `Comparator<object>` arriving at a contravariant `Comparator<in T>` would rewrite `T = String` to `object` and
    // drop it through its own `T : CharSequence` bound, and a `Derived<U> : Base<String>` arriving at a `Base<T>`
    // would be zipped against the DERIVED arity rather than the base view it is actually seen through.
    static bool UnifyMethodArgs(TypeNode declared, TypeNode flowed, TypeNode[] methodArgs, JsonObject call)
    {
        var moved = false;
        Walk(declared, flowed, nested: false);
        if (moved && call["typeArgs"] is JsonArray typeArgs)
            for (var i = 0; i < methodArgs.Length && i < typeArgs.Count; i++)
                typeArgs[i] = TypeJson.Write(methodArgs[i]);
        return moved;

        void Walk(TypeNode d, TypeNode f, bool nested, bool underArray = false)
        {
            switch (d, f)
            {
                case (TypeNode.Tv { Scope: "method" } tv, _) when tv.I >= 0 && tv.I < methodArgs.Length:
                    if (nested && IsBareObject(f) && !IsBareObject(methodArgs[tv.I])
                        && (underArray || NullableGenericErasure.IsNullableMaybeValue(methodArgs[tv.I], _isValue)))
                    {
                        methodArgs[tv.I] = new TypeNode.Fqn("object");
                        moved = true;
                    }
                    break;
                case (TypeNode.Array da, TypeNode.Array fa): Walk(da.Elem, fa.Elem, nested: true, underArray: true); break;
                case (TypeNode.Nullable dn, TypeNode.Nullable fn): Walk(dn.Of, fn.Of, nested, underArray); break;
                case (TypeNode.Oblivious dobl, TypeNode.Oblivious fo): Walk(dobl.Of, fo.Of, nested, underArray); break;
                case (TypeNode.ByRef db, TypeNode.ByRef fb): Walk(db.Of, fb.Of, nested, underArray); break;
                // SAME HEAD, or nothing. Two constructed types pair position-by-position only when they are the same
                // definition; across different heads the declared type's arguments are not the flowed type's, they are
                // whatever its supertype declaration fixed them to. `class Fixed<U> : Base<Int?>` flowing as
                // `Fixed<object>` into a `Base<T>` parameter would zip `T` against `object` and instantiate the callee
                // at `object`, though the argument is a `Base<Nullable<int32>>` and never was a `Base<object>` — an
                // argument the emitted member does not accept. Projecting onto the declared head instead of skipping
                // means walking the supertype chain, which is a subtyping question this pass does not need to answer:
                // it exists to notice an `object[]`, and an array pairs by its own shape, not by a head.
                case (TypeNode.Fqn { Args: { } dargs } df, TypeNode.Fqn { Args: { } fargs } ff)
                    when dargs.Length == fargs.Length && df.Name == ff.Name:
                    for (var i = 0; i < dargs.Length; i++) Walk(dargs[i], fargs[i], nested: true);
                    break;
            }
        }
    }

    // Being a .NET-bound CALL is what makes the node's argument descriptor a .NET declaration rather than the caller's
    // own Kotlin view — read both by the walk that routes these nodes to EvalClrCall and by the argument realignment
    // that decides how far to trust their descriptor. The property/field accessors are deliberately NOT here: they
    // carry no argument vector, and this axis is about arguments (ClrBoundNode states the split once).
    static bool IsClrBoundKind(string k) => ClrBoundNode.IsCall(k);

    // A call NetInteropBinding has already BOUND to a .NET member. Only the WRITE axis applies to it: the callee is
    // .NET, so its declared parameter types (`memberSig`) ARE the declaration and there is nothing Kotlin to
    // re-derive — but an argument that flowed out of the erasure as a bare `object` still has to be narrowed into the
    // slot that member declares, or the emitter pushes an `object` where a `Nullable<bool>` is required and the whole
    // method fails verification. Its own result is the .NET member's and is never re-derived.
    //
    // This is the arm the erasure family reaches once a call crosses into .NET: `assertTrue(map.merge(…))` hands an
    // erased stdlib return straight to `ClassicAssert.IsTrue(bool?)`.
    //
    // The call's own owner and type arguments come along, because `memberSig` is the callee's OPEN declaration —
    // `Enumerable.Repeat<T>`'s first parameter is `!!0` there and stays `!!0` for the emitter to match the member by.
    // RealignArgs closes it over these before converting anything into it.
    static TypeNode EvalClrCall(JsonObject obj, Ctx ctx)
    {
        if (obj["recv"] != null) Eval(obj["recv"], ctx);
        var ownerArgs = (TypeJson.Read(obj["type"]) as TypeNode.Fqn)?.Args;
        var methodArgs = (obj["typeArgs"] as JsonArray)?.Select(TypeJson.Read).ToArray();
        // No declaration and nothing to refuse: the callee is .NET, so `memberSig` IS its declaration.
        RealignArgs(obj, null, null, ownerArgs, methodArgs, ctx);
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

    // A DELEGATE INVOCATION. Its callee is the function type in `funcType`, which the declaration axis has already
    // erased, so its components ARE the physical slots: each argument is reconciled against the one it fills and the
    // result is the erased return. A `T.() -> R` states its receiver as the delegate's first parameter, so the
    // vector is chosen by whichever of the two spellings matches the argument count — never by guessing.
    //
    // Nothing is substituted into those components: a `funcType` is already closed at the invocation site (there is
    // no separate owner/method instantiation for a delegate), and `Subst` with no bindings leaves a concrete slot
    // alone while declining an open one, which is the right refusal.
    static TypeNode EvalDelegateInvoke(JsonObject obj, Ctx ctx)
    {
        var recvType = obj["recv"] != null ? Eval(obj["recv"], ctx) : null;
        var fn = TypeJson.Read(obj["funcType"]) as TypeNode.Fn;
        // The invoked delegate is whatever actually FLOWS into the receiver. A local holding an object-erased
        // `Func<object, string>` is dispatched through that type's `Invoke`, not through the `Func<string, string>`
        // the frontend stamped from the pre-erasure Kotlin type — the two are unrelated constructed delegates and
        // the emitted `callvirt` would name a method the value does not have.
        if (recvType is TypeNode.Fn rfn && fn != null && !rfn.Equals(fn) && IsObjectErasureOf(rfn, fn))
        {
            obj["funcType"] = TypeJson.Write(rfn);
            fn = rfn;
        }
        TypeNode[] declParams = null;
        if (fn is { Suspend: false } && obj["args"] is JsonArray args)
            declParams = fn.DelegateParams.Length == args.Count ? fn.DelegateParams
                : fn.Params.Length == args.Count ? fn.Params
                : null;
        if (declParams == null) { EvalChildrenOf(obj, "args", ctx); return fn?.Ret; }
        var flowed = RealignArgs(obj, declParams, null, null, null, ctx);
        // A DELEGATE PARAMETER KEEPS ITS DECLARED `Nullable<V>` (see NullableGenericErasure's header), so an
        // invocation is also the one call shape whose argument may need the ordinary VALUE-nullable wrap rather than
        // the object seam: `f(3)` on a `(Int?) -> R` pushes an `int32` where a `Nullable<int32>` is required, which
        // is not a stack-type imprecision but an invalid program. A direct call gets this from kotc, which knows the
        // callee's declaration; a delegate invocation has only the `funcType`, and it is read here.
        if (flowed != null && obj["args"] is JsonArray args2)
            for (var i = 0; i < args2.Count && i < declParams.Length; i++)
                if (declParams[i] is TypeNode.Nullable { Of: TypeNode.Fqn ev } && _isValue(ev.Name)
                    && flowed[i] is TypeNode.Fqn av && av.Name == ev.Name
                    && args2[i] is JsonObject a2)
                    args2[i] = new JsonObject { ["k"] = "nullableWrap", ["elem"] = TypeJson.Write(ev), ["e"] = a2.DeepClone() };
        return fn?.Ret;
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

    // `for (x in arr)` over an object-erased array. The node's `elem` is simultaneously the `ldelem` token and the loop
    // variable's declared slot, so BOTH follow the array that flows: re-stamp it to the flowed element (the same
    // object-erasure gate every other rewrite here uses) and register the loop var at that type, so a value consumer
    // inside the body — `x!!`, `x?.f()`, an argument — narrows out of `object` once, where it is used.
    static TypeNode EvalForArray(JsonObject obj, Ctx ctx)
    {
        var arrType = obj["array"] != null ? Eval(obj["array"], ctx) : null;
        var elem = TypeJson.Read(obj["elem"]);
        if (arrType is TypeNode.Array arr && elem != null && !elem.Equals(arr.Elem) && IsObjectErasureOf(arr.Elem, elem))
        {
            obj["elem"] = TypeJson.Write(arr.Elem);
            elem = arr.Elem;
        }
        if (Str(obj["var"]) is string v && elem != null) ctx.Env[v] = elem;
        if (obj["body"] != null) Eval(obj["body"], ctx);
        foreach (var kv in obj)
            if (kv.Value != null && kv.Key is not ("array" or "body" or "elem" or "k" or "var" or "label"))
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
                && Subst(NullableGenericErasure.EraseNullableTv(declParams[i], _isValue), ownerArgs, null) is TypeNode target
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
        return Subst(NullableGenericErasure.EraseNullableTv(declared, _isValue), owner.Args, null);
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
    static JsonNode CastForTarget(JsonNode value, TypeNode src, TypeNode target,
        bool exactPropertyTarget = false)
    {
        if (value is not JsonObject vo || src == null || target == null || src.Equals(target)) return null;
        // A compiler-generated mutable-property-reference adapter can expose `value: kotlin.Any` even though its
        // explicitly resolved generic accessor closes to `String` (or another narrower CLR slot). The exact source
        // property/role/MethodSemantics/signature lookup above makes that target authoritative, so this is an ordinary
        // runtime narrowing conversion, not an inference from `get_`/`set_` or from a physical object signature.
        var srcObj = IsBareObject(src) || exactPropertyTarget && IsSemanticObject(src);
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

    static bool IsSemanticObject(TypeNode t) =>
        t is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object", Args: null };

    // The arguments the type-argument settle takes NO evidence from — each because the MAIN loop still has to retype
    // it, and would be reconciling a construction against a stale instantiation had this pass evaluated it first.
    // Two node families match:
    //   * an ARRAY construction (`newArray`/`newArrayInit`/`newArraySized`) that does not already state the bare
    //     `object` element. It is built at this call site, so its element is its OWN choice rather than a fact about
    //     the callee: `f<Int?>(1, null)` packs a `Nullable<int32>[]` that agrees with whatever `!!0[]` is instantiated
    //     at. Once the element IS the bare `object` that freedom is gone — the array factory canonicalized it
    //     (#86 D2) and it is a real `Array<X?>` value like any other — so that one form is deliberately not matched.
    //     (An array node with no `elem` slot at all reads as "not the bare object" and matches, like the rest.)
    //   * a `new` — the Kotlin OBJECT-construction kind, and only that kind. A `.NET` `newClr`, a `newList`,
    //     `newClosure`, `newDelegate` and the rest are separate kinds and never match here. A `new` carries no `elem`
    //     slot, so the element test cannot narrow it and every `new` matches; that is the RIGHT answer rather than
    //     merely the safe one, because the main loop retypes a `new`'s own instantiation to the slot's BEFORE
    //     evaluating it, which is the ordering its constructor arguments depend on. Nothing in this pass reads an
    //     instantiation off a constructed object, so skipping one here loses no evidence.
    // Both cases are provisional in the way the whole pre-pass is: the general type-argument rule replaces this
    // predicate outright rather than growing another arm on it.
    static bool BuildsItsOwnElement(JsonObject arg)
        => Str(arg["k"]) is "new" or "newArray" or "newArrayInit" or "newArraySized"
           && !IsBareObject(TypeJson.Read(arg["elem"]));

    // `Unit`/`void` is the absence of a value, not a type to convert into.
    static bool IsVoidish(TypeNode t) => t is TypeNode.Fqn { Name: "void" or "kotlin.Unit" or "kotlin.Nothing", Args: null };

    // Whether widening this type into `object` needs a real IL conversion. A `Tv` does (it may be instantiated with a
    // struct, and `box` on a type variable is the verifier-clean form for both instantiations); a structural
    // `Nullable<V>` does; a struct does — INCLUDING a constructed one, since `KeyValuePair<K,V>` needs the same `box`
    // its argument-less siblings do and the oracle strips generic arity to say so. A reference is already an `object`
    // and needs nothing.
    static bool NeedsObjectSeam(TypeNode t) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => NeedsObjectSeam(n.Of),
        TypeNode.Fqn f => _isValue(f.Name),
        _ => false,
    };
}

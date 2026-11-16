using System;
using System.Collections.Generic;
using System.Linq;
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
//   call / ctor arguments  `Subst(Erase(declared param), owner args, method args)` for the VALUE conversion. A call's
//                          `sig` remains the OPEN declaration identity; a construction's `argTypes` is its USE-SITE
//                          physical target vector and is rewritten to the same derived type.
//
// THE ONLY CASTABLE SEAM IS `object`. `box` carries a value or a `Nullable<V>` into `object` (an empty
// `Nullable<V>` boxes to a genuine null), and `unbox.any`/`castclass` carries it back. A difference sitting INSIDE a
// constructed generic — `Ref<object>` against `Ref<Nullable<int32>>` — is NOT castable at all: those are unrelated
// invariant reified generics and a `castclass` between them throws. Those are made to agree by DERIVING the use
// type (the read axis), never by converting the value, so this half leaves them alone.
static partial class NullableTvErasureCallRealign
{


    // Declaration-to-use type argument selection is independent of graph rendering.
    static bool UnifyMethodArgs(TypeNode declared, TypeNode flowed, TypeNode[] methodArgs)
    {
        var moved = false;
        Walk(declared, flowed, nested: false);
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
                case (TypeNode.Projection dp, TypeNode.Projection fp): Walk(dp.Of, fp.Of, nested, underArray); break;
                case (TypeNode.Projection dp, _): Walk(dp.Of, f, nested, underArray); break;
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
                    when dargs.Length == fargs.Length && SameClassifier(df, ff):
                    for (var i = 0; i < dargs.Length; i++) Walk(dargs[i], fargs[i], nested: true);
                    break;
            }
        }
    }

    // Being a .NET-bound CALL is what makes the node's argument descriptor a .NET declaration rather than the caller's
    // own Kotlin view — read both by the walk that routes these nodes to SourceUseClrCall and by argument realignment
    // that decides how far to trust their descriptor. The property/field accessors are deliberately NOT here: they
    // carry no argument vector, and this axis is about arguments (ClrBoundNode states the split once).
    static bool IsClrBoundKind(string k) => ClrBoundNode.IsCall(k);


    static TypeNode.Fqn ClrOwner(TypeNode type)
    {
        var t = type;
        while (true)
            switch (t)
            {
                case TypeNode.Fqn f: return f;
                case TypeNode.Nullable n: t = n.Of; break;
                case TypeNode.Oblivious o: t = o.Of; break;
                case TypeNode.ByRef b: t = b.Of; break;
                default: return null;
            }
    }


    // Template-copy and consumed-owning materializers share one conversion policy.
    static SlotCoercion SelectFixedSlotCoercion(string kind, TypeNode existingCast,
        TypeNode src, TypeNode target, bool exactPropertyTarget, ValueTypeOracle isValue, ReferenceMetadataIndex refs)
    {
        if (src == null || target == null) return default;
        // Oblivious is an NRT/platform annotation, not a CLR container. BirTypeLowering erases the OUTER wrapper to
        // its inner physical slot, so compare that physical spelling here as well: V! -> Nullable<V> still needs a
        // constructor, and Nullable<V> -> V! still needs extraction. A platform local that must retain the reflected
        // initializer representation is retyped by body flow before it reaches this fixed-slot rule.
        src = PhysicalTopLevel(src);
        target = PhysicalTopLevel(target);
        if (src.Equals(target)) return default;
        if ((IsBareObject(src) && IsSemanticObject(target))
            || (IsSemanticObject(src) && (IsBareObject(target) || IsSemanticObject(target)))) return default;
        if (kind is "throwExpr" or "throw") return default;
        if (target is TypeNode.Nullable { Of: TypeNode.Fqn targetElem }
            && SameNominalSlot(src, targetElem, refs) && isValue(targetElem))
            return new(SlotCoercionKind.NullableWrap, targetElem);
        if (src is TypeNode.Nullable { Of: TypeNode.Fqn sourceElem }
            && SameNominalSlot(target, sourceElem, refs) && isValue(sourceElem))
            return new(SlotCoercionKind.NullableValue, sourceElem);
        // The nullable-generic declaration `Array<T?>` is physically `object[]`. At a concrete REFERENCE
        // instantiation, compiler-produced values retain their reified runtime array (`String[]`, `Slot[]`) and CLR
        // covariance lets that value inhabit the open object[] declaration slot. Reading it into a concrete Kotlin
        // use requires the inverse checked projection. SourceDerivedCall handles the boundary when its semantic
        // result stamp is still present; this fixed-slot twin handles a value first flowed through an object[] local,
        // Ref field, return, or other consumer before the concrete target is encountered.
        if (CanNarrowReferenceArrayProjection(src, target, isValue))
            return new(SlotCoercionKind.Cast, target);
        return SelectObjectSlotCoercion(kind, existingCast, src, target, exactPropertyTarget, isValue);
    }

    enum SlotCoercionKind { None, NullableWrap, NullableValue, Cast }
    readonly record struct SlotCoercion(SlotCoercionKind Kind, TypeNode Type);


    static TypeNode PhysicalTopLevel(TypeNode type) => type is TypeNode.Oblivious o ? o.Of : type;

    // Exact imported TypeDef names and declaration carrier names can denote the same constructed value slot.
    // Use reference-backed classifier identity, not spelling or assignability, before adding/removing Nullable<V>.
    static bool SameNominalSlot(TypeNode left, TypeNode right, ReferenceMetadataIndex refs)
    {
        if (left.Equals(right)) return true;
        if (left is not TypeNode.Fqn l || right is not TypeNode.Fqn r || !SameClassifier(l, r, refs)) return false;
        var la = l.Args ?? Array.Empty<TypeNode>();
        var ra = r.Args ?? Array.Empty<TypeNode>();
        return la.Length == ra.Length && la.Zip(ra, (a, b) => SameNominalSlot(a, b, refs)).All(equal => equal);
    }


    static SlotCoercion SelectObjectSlotCoercion(string kind, TypeNode existingCast,
        TypeNode src, TypeNode target, bool exactPropertyTarget, ValueTypeOracle isValue)
    {
        if (src == null || target == null || src.Equals(target)) return default;
        // A managed-reference argument names storage. Preserve its lvalue for
        // the native-local allocator; an object-to-pointer value cast cannot
        // supply the selected declaration's exact managed-reference location.
        if (SignatureValueTypes.Of(target) is TypeNode.ByRef) return default;
        // A compiler-generated mutable-property-reference adapter can expose `value: kotlin.Any` even though its
        // explicitly resolved generic accessor closes to `String` (or another narrower CLR slot). The exact source
        // property/role/MethodSemantics/signature lookup above makes that target authoritative, so this is an ordinary
        // runtime narrowing conversion, not an inference from `get_`/`set_` or from a physical object signature.
        var srcObj = IsBareObject(src) || exactPropertyTarget && IsSemanticObject(src);
        var tgtObj = IsBareObject(target);
        if (srcObj == tgtObj) return default;                                // neither side is the erased form
        if (srcObj ? IsVoidish(target) : !NeedsObjectSeam(src, isValue)) return default;
        // A node that never yields a value (a `throw` in value position, which is TERMINATED where it stands) has
        // nothing to convert, and wrapping it would state a stack value the emitter must then produce.
        if (kind is "throwExpr" or "throw") return default;
        // Idempotence: never re-wrap a cast that already states the target.
        if (kind == "cast" && existingCast is TypeNode ct && ct.Equals(target)) return default;
        return new(SlotCoercionKind.Cast, target);
    }

    static bool IsBareObject(TypeNode t) => t is TypeNode.Fqn { Name: "object", Args: null };

    static bool IsSemanticObject(TypeNode t) =>
        t is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object", Args: null };


    static bool BuildsItsOwnElement(string kind, TypeNode element)
        => kind is "new" or "newArray" or "newArrayInit" or "newArraySized" && !IsBareObject(element);

    // `Unit`/`void` is the absence of a value, not a type to convert into.
    static bool IsVoidish(TypeNode t) => t is TypeNode.Fqn { Name: "void" or "kotlin.Unit" or "kotlin.Nothing", Args: null };

    // Whether widening this type into `object` needs a real IL conversion. A `Tv` does (it may be instantiated with a
    // struct, and `box` on a type variable is the verifier-clean form for both instantiations); a structural
    // `Nullable<V>` does; a struct does — INCLUDING a constructed one, since `KeyValuePair<K,V>` needs the same `box`
    // and the oracle classifies its complete constructed FQN. A reference is already an `object`
    // and needs nothing.
    static bool NeedsObjectSeam(TypeNode t) => NeedsObjectSeam(t, _isValue);

    static bool NeedsObjectSeam(TypeNode t, ValueTypeOracle isValue) => t switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable n => NeedsObjectSeam(n.Of, isValue),
        TypeNode.Oblivious o => NeedsObjectSeam(o.Of, isValue),
        TypeNode.Fqn f => isValue(f),
        _ => false,
    };
}

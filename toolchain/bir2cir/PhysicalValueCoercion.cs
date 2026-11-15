using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Shared physical TypeNode conversion, native-storage eligibility and frame-closing policy.
// Final value-flow rewriting is owned by TypedPhysicalValueCoercion.
static partial class PhysicalValueCoercion
{
    internal static bool NeedsConversion(TypeNode source, TypeNode target, ValueTypeOracle isValue,
        Func<TypeNode.Fqn, bool> isExistentialOwner, Func<TypeNode.Fqn, bool> isNativeReference,
        Func<TypeNode.Fqn, TypeNode.Fqn, bool> hasDeclaredCarrier)
    {
        source = SignatureValueTypes.Of(source);
        target = SignatureValueTypes.Of(target);
        ValidateNativePointerConversion(source, target);
        return source is TypeNode.Tv && target is TypeNode.Tv && !source.Equals(target)
        || target is TypeNode.Tv && source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
        || source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && !IsVoid(target) && NeedsBox(target, isValue)
        || source is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && IsReferenceSlot(target, isValue)
            && target is not TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
        || source is TypeNode.Fqn carrier && target is TypeNode.Fqn native
            && isExistentialOwner(carrier)
            && (isNativeReference(native) || hasDeclaredCarrier(native, carrier))
        || CollectionViewFaces.IsViewSeam(source, target)
        || source is TypeNode.Fqn { Name: "System.Array" } && target is TypeNode.Array
        // A concrete value or generic stack slot is not a reference, even when its boxed value implements the
        // target interface. State the boxing edge before a conditional merge, store, argument or return.
        || !IsVoid(source) && !IsVoid(target)
            && NeedsBox(source, isValue) && IsReferenceSlot(target, isValue);
    }

    internal static void ValidateNativePointerConversion(TypeNode source, TypeNode target)
    {
        if (source == null || target == null || IsVoid(target)) return;
        if ((source is TypeNode.FnPtr || target is TypeNode.FnPtr)
            && BirTypeLowering.CanonicalPhysicalSlotType(source) != BirTypeLowering.CanonicalPhysicalSlotType(target))
            throw new InvalidOperationException("bir2cir: a native function-pointer value requires its exact signature; boxing and checked casts are not supported"
                + $" (source={TypeNode.ToJson(source)}, target={TypeNode.ToJson(target)})");
    }

    internal static bool NeedsBox(TypeNode type, ValueTypeOracle isValue) => type switch
    {
        TypeNode.Tv => true,
        TypeNode.Nullable nullable => NeedsBox(nullable.Of, isValue),
        TypeNode.Oblivious oblivious => NeedsBox(oblivious.Of, isValue),
        TypeNode.Fqn named => isValue(named),
        _ => false,
    };

    internal static bool IsReferenceSlot(TypeNode type, ValueTypeOracle isValue) => type switch
    {
        TypeNode.Array => true,
        TypeNode.Nullable nullable => IsReferenceSlot(nullable.Of, isValue),
        TypeNode.Oblivious oblivious => IsReferenceSlot(oblivious.Of, isValue),
        TypeNode.Fqn named => !IsVoid(named) && !isValue(named),
        _ => false,
    };
    // The early scalar-dispatch boundary can allocate storage only for an
    // actual incoming parameter and a bare generic native referent. Full ABI
    // allocation retains its existing eligibility for all addressed locals.
    internal static bool NativeLocalEligible(bool genericParametersOnly, bool parameter, TypeNode referent)
        => !genericParametersOnly || parameter && SignatureValueTypes.Of(referent) is TypeNode.Tv;

    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };

    internal static int NumericRank(TypeNode type) => type is TypeNode.Fqn { Args: null } named ? named.Name switch
    {
        "System.Double" => 5,
        "System.Single" => 4,
        "System.Int64" or "System.UInt64" => 3,
        "System.Int32" or "System.UInt32" => 2,
        "System.Int16" or "System.UInt16" or "System.Char" => 1,
        "System.Byte" or "System.SByte" => 0,
        _ => -1,
    } : -1;

    internal static TypeNode Close(TypeNode type, TypeNode[] ownerArgs, TypeNode[] methodArgs) => type switch
    {
        null => null,
        TypeNode.Tv { Scope: "type" } tv when ownerArgs != null && tv.I >= 0 && tv.I < ownerArgs.Length => ownerArgs[tv.I],
        TypeNode.Tv { Scope: "method" } tv when methodArgs != null && tv.I >= 0 && tv.I < methodArgs.Length => methodArgs[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => Close(a, ownerArgs, methodArgs)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(Close(n.Of, ownerArgs, methodArgs)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(Close(o.Of, ownerArgs, methodArgs)),
        TypeNode.Array a => new TypeNode.Array(Close(a.Elem, ownerArgs, methodArgs), a.Rank, a.SzArray),
        TypeNode.ByRef b => new TypeNode.ByRef(Close(b.Of, ownerArgs, methodArgs)),
        TypeNode.Ptr p => new TypeNode.Ptr(Close(p.Of, ownerArgs, methodArgs)),
        TypeNode.Mod m => new TypeNode.Mod(m.Req, Close(m.M, ownerArgs, methodArgs), Close(m.Of, ownerArgs, methodArgs)),
        TypeNode.FnPtr pointer => pointer with {
            Ret = Close(pointer.Ret, ownerArgs, methodArgs),
            Params = pointer.Params.Select(parameter => Close(parameter, ownerArgs, methodArgs)).ToArray(),
        },
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, Close(fn.Ret, ownerArgs, methodArgs),
            fn.Params.Select(p => Close(p, ownerArgs, methodArgs)).ToArray(),
            fn.Recv == null ? null : Close(fn.Recv, ownerArgs, methodArgs), fn.Clr,
            fn.Ctx?.Select(p => Close(p, ownerArgs, methodArgs)).ToArray()),
        _ => type,
    };

}

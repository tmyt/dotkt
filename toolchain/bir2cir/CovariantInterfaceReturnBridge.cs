using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// Kotlin covariance needs a private exact-return CLR MethodImpl body while
// retaining the authored narrow declaration and its virtual forwarding slot.
// The owning source producer is in Source.cs; the remaining DOM slot key is
// consumed by the later suspend/override capability after the single frontier.
static partial class CovariantInterfaceReturnBridge
{
    internal readonly record struct BridgedSlot(JsonObject Implementation, string Descriptor);

    internal static BridgedSlot BridgedSlotKey(JsonObject implementation, TypeNode.Fqn descriptorOwner,
        string descriptorMember, int methodArity, IEnumerable<TypeNode> slotParams, TypeNode slotRet,
        ReferenceMetadataIndex refs, ValueTypeOracle isValue)
        => new(implementation, DescriptorKey(descriptorOwner, descriptorMember, methodArity, slotParams, slotRet, refs, isValue));

    internal static string DescriptorKey(TypeNode.Fqn descriptorOwner, string descriptorMember, int methodArity,
        IEnumerable<TypeNode> slotParams, TypeNode slotRet, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        var ownerName = refs.ExactReflectedOwner(descriptorOwner.Name, descriptorOwner.Args?.Length ?? 0);
        var ownerArgs = descriptorOwner.Args == null ? "" : "<" + string.Join(",", descriptorOwner.Args.Select(type =>
            ReferencedPhysicalTypeKey(type, refs, isValue))) + ">";
        return ownerName + ownerArgs + "::" + descriptorMember + "`" + methodArity + "("
            + string.Join(",", slotParams.Select(type => ReferencedPhysicalTypeKey(type, refs, isValue))) + ")->"
            + ReferencedPhysicalTypeKey(slotRet, refs, isValue);
    }

    static string ReferencedPhysicalTypeKey(TypeNode type, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
        => TypeKey(BirTypeLowering.LowerPhysicalType(type, refs.Aliases, isValue, refs.PhysicalTypeNames,
            typeArg: false, nullableFrames: refs.NullableTypeFrames));

    static bool ParametersEqual(IReadOnlyList<TypeNode> implementation, IReadOnlyList<TypeNode> slots, TypeNode[] ownerArgs)
    {
        if (implementation.Count != slots.Count) return false;
        for (var index = 0; index < implementation.Count; index++)
            if (implementation[index] == null || SubstOwnerTvs(implementation[index], ownerArgs) != slots[index]) return false;
        return true;
    }

    static bool ParametersPhysicallyEqual(IReadOnlyList<TypeNode> parameters, IReadOnlyList<TypeNode> slots,
        TypeNode[] ownerArgs, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
    {
        if (parameters.Count != slots.Count) return false;
        for (var index = 0; index < parameters.Count; index++)
        {
            if (parameters[index] == null) return false;
            var implementation = SubstOwnerTvs(parameters[index], ownerArgs);
            if (implementation != slots[index] && !BirTypeLowering.SamePhysicalSlotType(slots[index], implementation,
                    refs.Aliases, isValue, refs.PhysicalTypeNames, returnPosition: false, nullableFrames: refs.NullableTypeFrames)) return false;
        }
        return true;
    }

    static TypeNode[] EffectiveArgs(TypeNode.Fqn spec, int arity) => arity == 0 ? Array.Empty<TypeNode>()
        : spec.Args is { } args && args.Length == arity ? args : null;

    static TypeNode SubstOwnerTvs(TypeNode type, TypeNode[] args) => type switch
    {
        TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < args.Length => args[tv.I],
        TypeNode.Fqn f when f.Args is not null => new TypeNode.Fqn(f.Name, f.Args.Select(a => SubstOwnerTvs(a, args)).ToArray()),
        TypeNode.Nullable n => new TypeNode.Nullable(SubstOwnerTvs(n.Of, args)),
        TypeNode.Oblivious o => new TypeNode.Oblivious(SubstOwnerTvs(o.Of, args)),
        TypeNode.Array a => new TypeNode.Array(SubstOwnerTvs(a.Elem, args)),
        TypeNode.ByRef b => new TypeNode.ByRef(SubstOwnerTvs(b.Of, args)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, SubstOwnerTvs(fn.Ret, args),
            fn.Params.Select(p => SubstOwnerTvs(p, args)).ToArray(), fn.Recv == null ? null : SubstOwnerTvs(fn.Recv, args)),
        _ => type,
    };

    static string TypeKey(TypeNode type) => TypeJson.Write(type).ToJsonString();
    static string SafeName(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    static bool IsUnit(TypeNode type) => type is TypeNode.Fqn { Name: "kotlin.Unit" or "Unit" or "void" or "System.Void", Args: null };
}

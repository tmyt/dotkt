using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclarationRename
{
    readonly record struct SlotOverride(TypeNode.Fqn Owner, string Member, string Kind, int Arity);
    sealed record CallableSlot(TypeNode[] Signature, TypeNode[] BindingSignature, int MethodArity,
        bool Declaration, bool SelectedCall, string DeclarationId);

    internal static string ResolveSlot(BirFunctionDeclaration declaration, ReferenceMetadataIndex refs,
        NullableRepresentationFrame ownerFrame = null)
        => ResolveSlot(Overrides(declaration.Overrides.OrEmpty()), refs, Callable(declaration, ownerFrame));

    internal static string ResolveSlot(BirCallInstance call, ReferenceMetadataIndex refs)
        => ResolveSlot(Overrides(call.Overrides.OrEmpty()), refs, Callable(call));

    static SlotOverride[] Overrides(BirOverride[] markers) => markers
        .Where(marker => marker.Owner.OrDefault() is TypeNode.Fqn && marker.Member.HasValue)
        .Select(marker => new SlotOverride((TypeNode.Fqn)marker.Owner.Value, marker.Member.Value,
            marker.Kind.OrDefault(), marker.Arity.OrDefault())).ToArray();

    static CallableSlot Callable(BirFunctionDeclaration declaration, NullableRepresentationFrame ownerFrame)
    {
        var parameters = declaration.Params.OrDefault();
        var signature = parameters?.Select(parameter => parameter.Type.OrDefault()).ToArray();
        if (signature?.Any(type => type == null) == true) signature = null;
        var binding = signature?.Select((type, index) => NullableRepresentationTypes.BindingScalarFromSource(
            type, parameters[index].NullableGeneric.OrDefault(), ownerFrame,
            declaration.NullableRepresentationFrame.OrDefault())).ToArray();
        return new CallableSlot(signature, binding, declaration.TypeParams.OrEmpty().Length,
            parameters != null, false, declaration.DeclarationId.OrDefault());
    }

    static CallableSlot Callable(BirCallInstance call)
    {
        var signature = call.Sig.OrDefault();
        if (signature?.Any(type => type == null) == true) signature = null;
        return new CallableSlot(signature, null, call.TypeArgs.OrEmpty().Length,
            false, true, call.DeclarationId.OrDefault());
    }

    // Both graph adapters supply named facts only. The selected-slot policy is
    // shared by source declarations and actual late declarations/call nodes.
    static string ResolveSlot(SlotOverride[] overrides, ReferenceMetadataIndex refs, CallableSlot callable)
    {
        foreach (var marker in overrides)
        {
            var owner = marker.Owner;
            var member = marker.Member;
            // Property spelling belongs to its exact MethodSemantics association.
            if (marker.Kind is "getter" or "setter")
            {
                if (callable.Declaration && callable.Signature != null
                    && TryExactPropertySlot(callable.Signature, callable.MethodArity, refs,
                        owner, member, marker.Kind, out _, out _, out var accessor, out _)) return accessor;
                continue;
            }
            if (callable.Signature == null || callable.Signature.Length != marker.Arity) continue;
            // Check the authored vector's arity before selecting a pristine call
            // descriptor, as the existing declaration/call resolver does.
            var signature = callable.SelectedCall && refs.DeclarationOverrideParameters(callable.DeclarationId)
                is { } selected ? selected : callable.Signature;
            if (callable.Declaration) signature = callable.BindingSignature;
            if (refs.TryProjectedIndexerSlot(owner, member, callable.MethodArity, signature, out var indexer, out _))
                return indexer;
            // Only already-selected call descriptors try the open intrinsic
            // first. A declaration closes its ancestor into its own frame.
            if (callable.SelectedCall
                && refs.TryExactMemberIntrinsic(owner.Name, member, callable.MethodArity, signature, out var open))
                return open;
            if (refs.TryExactMemberIntrinsic(owner.Name, member, callable.MethodArity, signature,
                    owner.Args ?? Array.Empty<TypeNode>(), out var closed)) return closed;
        }
        // Projected native methods already carry their CLR identity. Properties
        // were handled above, not reconstructed from get_/set_ names.
        foreach (var marker in overrides)
        {
            if (refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(marker.Owner.Name),
                    marker.Owner.Args?.Length ?? 0) is not { } native) continue;
            if (marker.Kind is "getter" or "setter") continue;
            if (NetInteropBinding.DeclaresPublicMethodNamed(native, marker.Member)) return marker.Member;
        }
        return null;
    }
}

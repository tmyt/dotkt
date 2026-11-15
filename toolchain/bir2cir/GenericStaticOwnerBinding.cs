using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// A Kotlin companion-block static on G<T> is one logical declaration, while CLR static storage and .cctors on G<T>
// exist once per constructed type. Put the declarations on one non-generic compiler carrier and bind every use to it.
// The trusted carrier is also consumed by dll2klib, which merges those declarations back into semantic G rather than
// exposing the physical implementation class. No representative type argument or generic-constraint guess exists.
static partial class GenericStaticOwnerBinding
{
    internal const string Marker = "$dotkt_statics";
    const string CarrierInitMarker = "dotkt$initialized";
    const string OwnerInitTrigger = "dotkt$staticInit";

    static string SelectCarrier(TypeNode type, string kind, IReadOnlyDictionary<string, string> local,
        IReadOnlySet<string> localGenericOwners, ReferenceMetadataIndex refs)
    {
        // A constructed owner already states an exact CLR frame; only the
        // receiverless Kotlin static surface selects a non-generic carrier.
        if (type is not TypeNode.Fqn owner || owner.Args is { Length: > 0 }) return null;
        var bare = owner.Name;
        if (!local.TryGetValue(bare, out var carrier) && !refs.TryGenericStaticCarrier(bare, out carrier))
        {
            if (!local.Values.Contains(bare, StringComparer.Ordinal) && !refs.IsGenericStaticCarrier(bare))
            {
                // Native event owners are closed later by NetInteropBinding.
                if (kind is "clrEventGet" or "clrEventRaise" && !localGenericOwners.Contains(bare) && !refs.HasDotKtOwner(bare)) return null;
                if (owner.Args == null && (localGenericOwners.Contains(bare) || refs.OwnerArity(bare) > 0))
                    throw new InvalidOperationException($"static access to generic owner '{bare}' has no constructed type or explicit non-generic carrier");
                return null;
            }
            carrier = bare;
        }
        return carrier;
    }

}

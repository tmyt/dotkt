using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Kotlin Map operations and native IDictionary slots have different contracts.
// The physical canonical carrier supplies native faces through selected Kotlin
// slots; its reference twin and runtime definition share one identity.
static partial class KotlinMapStorageSynthesis
{
    const string CarrierName = "DotKt.Runtime.CompilerServices.dotkt$KotlinMapStorage";
    const string EnumeratorName = "dotkt$MapEnumeratorOverKotlinIterator";
    const string Dictionary = "System.Collections.Generic.IDictionary";
    const string Collection = "System.Collections.Generic.ICollection";
    const string Enumerable = "System.Collections.Generic.IEnumerable";
    const string RawEnumerable = "System.Collections.IEnumerable";
    const string Helpers = "kotlin.collections.ClrAuthoredMapAdaptersKt";
    static readonly TypeNode K = new TypeNode.Tv("type", 0);
    static readonly TypeNode V = new TypeNode.Tv("type", 1);
    static readonly TypeNode Any = new TypeNode.Fqn("System.Object");
    static readonly TypeNode Bool = new TypeNode.Fqn("System.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("System.Int32");
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static TypeNode.Fqn F(string name, params TypeNode[] args) => new(name, args.Length == 0 ? null : args);
    static TypeNode Pair => F("System.Collections.Generic.KeyValuePair", K, V);

    static IEnumerable<TypeNode.Fqn> StorageFaces(TypeNode.Fqn[] reached)
    {
        if (!reached.Any(f => f.Name == KotlinCollectionSlotSynthesis.MapSlots
                || f.Name == KotlinCollectionSlotSynthesis.MutableMapSlots)) yield break;
        foreach (var dictionary in reached.Where(f => ReferenceMetadataIndex.BareOwnerFqn(f.Name) == Dictionary
                     && f.Args is { Length: 2 }))
            if (!reached.Any(f => ReferenceMetadataIndex.BareOwnerFqn(f.Name) == CarrierName
                    && f.Args is { Length: 2 }
                    && f.Args.Zip(dictionary.Args).All(pair =>
                        ClrMemberResolution.SameInterfaceSlotType(pair.First, pair.Second))))
                yield return F(CarrierName, dictionary.Args);
    }

    sealed record HelperPlan(string DeclarationId, TypeNode[] TypeArguments, TypeNode[] Signature);

    static HelperPlan PlanHelper(ReferenceMetadataIndex refs, string owner, string name,
        TypeNode[] typeArgs, TypeNode[] sourceSignature, Func<TypeNode, TypeNode> projectReferencedValueType)
    {
        var helper = refs.AuthoredKotlinHelper(owner, name, typeArgs.Length, sourceSignature);
        TypeNode NoWitness(TypeNode _) => throw new InvalidOperationException("Native map storage has no nullable witness");
        var closed = helper.NullableFrame?.Close(typeArgs, t => t, NoWitness, t => t, NoWitness) ?? typeArgs;
        TypeNode Physical(TypeNode type) => BirTypeLowering.CanonicalPhysicalSlotType(BirTypeLowering.LowerPhysicalType(
            projectReferencedValueType(type), refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames,
            typeArg: false, nullableFrames: refs.NullableTypeFrames));
        return new(helper.DeclarationId, closed, helper.ParamTypeNodes.Select(Physical).ToArray());
    }
}

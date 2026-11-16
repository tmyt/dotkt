using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;

// Read-only Kotlin collections inhabit invariant CLR storage without acquiring
// Kotlin mutability or losing identity. Default interface methods keep reads on
// the original read-only face and reject mutation.
static partial class ReadOnlyCollectionStorageSynthesis
{
    internal const string CarrierName = "DotKt.Runtime.CompilerServices.dotkt$ReadOnlyCollectionStorage";
    const string ListCarrierName = "DotKt.Runtime.CompilerServices.dotkt$ReadOnlyListStorage";
    static readonly TypeNode Element = new TypeNode.Tv("type", 0);
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static readonly TypeNode Bool = new TypeNode.Fqn("System.Boolean");
    static readonly TypeNode Int = new TypeNode.Fqn("System.Int32");
    static TypeNode.Fqn Face(string name, TypeNode element) => new(name, new[] { element });

    static IEnumerable<TypeNode.Fqn> StorageFaces(TypeNode.Fqn[] reached,
        IReadOnlyDictionary<string, SupertypeGraph.Def> definitions, ReferenceMetadataIndex refs)
    {
        var mapStorage = refs.KotlinMapStorageDefinitions();
        var storageRoots = reached.Where(type =>
            ReferenceMetadataIndex.BareOwnerFqn(type.Name) is "System.Collections.Generic.IDictionary"
                or "System.Collections.Generic.IReadOnlyDictionary"
            || mapStorage.Any(name => type.Name == name
                || type.Name + "`" + (type.Args?.Length ?? 0) == name)).ToArray();
        bool StorageOwns(TypeNode.Fqn face) => storageRoots.Any(root =>
            SupertypeGraph.Reaches(root, face, definitions, refs));
        var independentRoots = reached.Where(type =>
            (IsFace(type, CollectionViewFaces.IReadOnlyList) || IsFace(type, CollectionViewFaces.IList)
                || IsFace(type, "System.Collections.Generic.IReadOnlySet")
                || IsFace(type, "System.Collections.Generic.ISet")) && !StorageOwns(type)).ToArray();
        bool StorageOnly(TypeNode.Fqn face) => StorageOwns(face)
            && !independentRoots.Any(root => SupertypeGraph.Reaches(root, face, definitions, refs));
        // Dictionary entry storage is not an authored Kotlin Collection. Giving
        // it this carrier would invent a second Count closure on a map subclass.
        var lists = reached.Where(type => IsFace(type, CollectionViewFaces.IReadOnlyList)
            && !StorageOnly(type)
            && !reached.Any(other => IsFace(other, CollectionViewFaces.IList)
                && other.Args[0] == type.Args[0])).ToArray();
        foreach (var list in lists) yield return Face(ListCarrierName, list.Args[0]);
        foreach (var readOnly in reached.Where(type => IsFace(type, CollectionViewFaces.IReadOnlyCollection)))
        {
            if (StorageOnly(readOnly)) continue;
            var element = readOnly.Args[0];
            if (lists.Any(list => list.Args[0] == element)) continue;
            if (reached.Any(type => IsFace(type, CollectionViewFaces.ICollection)
                    && type.Args[0] == element)) continue;
            yield return Face(CarrierName, element);
        }
    }

    static bool IsFace(TypeNode.Fqn type, string name) => type.Args is { Length: 1 }
        && (type.Name == name || type.Name == name + "`1");

    sealed record HelperPlan(string DeclarationId, TypeNode[] TypeArguments, TypeNode[] Signature);

    static HelperPlan PlanStorageHelper(ReferenceMetadataIndex refs, string name, string sourceCollection,
        Func<TypeNode, TypeNode> projectReferencedValueType)
    {
        const string owner = "kotlin.collections.ClrCollectionDefaultsKt";
        var formal = new TypeNode.Tv("method", 0);
        var helper = refs.AuthoredKotlinHelper(owner, name, 1,
            new TypeNode[] { Face(sourceCollection, formal), formal });
        // This carrier owns CLR storage elements. The enumerated value and query
        // argument already share that representation, with no nullable witness.
        TypeNode NoNullableWitness(TypeNode _) => throw new InvalidOperationException(
            "Physical collection storage carrier has no nullable representation witness");
        var arguments = helper.NullableFrame?.Close(new TypeNode[] { Element },
            type => type, NoNullableWitness, type => type, NoNullableWitness) ?? new TypeNode[] { Element };
        TypeNode Physical(TypeNode type) => BirTypeLowering.LowerPhysicalType(
            projectReferencedValueType(type), refs.Aliases, refs.IsValueType, refs.PhysicalTypeNames,
            typeArg: false, nullableFrames: refs.NullableTypeFrames);
        return new(helper.DeclarationId, arguments, helper.ParamTypeNodes.Select(Physical).ToArray());
    }
}

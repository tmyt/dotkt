using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionBclSlotSynthesis
{
    internal static void SourcePreparationSelfTest()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Mutable-contract snapshot: " + message); }
        const string mutable = "kotlin.collections.MutableCollection";
        var captured = new TypeNode.Tv("type", 0); var own = new TypeNode.Tv("type", 1);
        var continuation = new TypeNode.Fqn("kotlin.coroutines.Continuation", new TypeNode[] { captured });
        var direct = new TypeNode.Fqn(mutable, new TypeNode[] { own });
        var baseType = new BirClassDefinition { Name = "Snapshot.CollectionBase", Kind = "class", Abstract = true,
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "Captured" } },
            TypeParams = new[] { new BirTypeParameter { Name = "Element" } },
            Interfaces = new TypeNode[] { direct }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var child = new BirClassDefinition { Name = "Snapshot.CollectionChild", Kind = "class", Abstract = true,
            TypeParams = new[] { new BirTypeParameter { Name = "Element" } },
            Base = new TypeNode.Fqn("Snapshot.CollectionBase", new TypeNode[] { new TypeNode.Fqn("kotlin.Any"), continuation }),
            Methods = Array.Empty<BirFunctionDeclaration>(), Interfaces = Array.Empty<TypeNode>() };
        // A later file owns the base. Source closure must still substitute its
        // full captured+own frame rather than only the current own parameters.
        var first = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { child } };
        var second = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { baseType } };
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        RecordContracts(new[] { first, second }, refs);
        var baseContract = baseType.DotktKotlinMutableCollectionContract.Value;
        var childContract = child.DotktKotlinMutableCollectionContract.Value;
        Check(baseContract.Types.Count == 1 && baseContract.Types[0] is TypeNode.Fqn { Args: [TypeNode.Tv { Scope: "type", I: 1 }] }
            && childContract.Types.Count == 1 && childContract.Types[0] is TypeNode.Fqn { Args: [TypeNode.Fqn {
                Name: "kotlin.coroutines.Continuation", Args: [TypeNode.Tv { Scope: "type", I: 0 }] }] },
            "constructed source closure lost captured/own positions or later-file base selection");
        var stored = (TypeNode.Fqn)baseContract.Types[0];
        Check(!ReferenceEquals(stored.Args, direct.Args), "immutable contract aliases an input argument vector");
        direct.Args[0] = new TypeNode.Fqn("System.String");
        Check(stored.Args[0] is TypeNode.Tv { I: 1 }, "input mutation changed captured contract");
        direct.Args[0] = own;
        var frozen = childContract.FrozenText;
        ContinuationErasure.Apply(first, refs); ContinuationErasure.Apply(second, refs);
        Check(((TypeNode.Fqn)((TypeNode.Fqn)child.Base.Value).Args[1]).Args[0] is TypeNode.Fqn { Name: "kotlin.Any" }
            && ReferenceEquals(child.DotktKotlinMutableCollectionContract.Value, childContract) && childContract.FrozenText == frozen
            && ((TypeNode.Fqn)((TypeNode.Fqn)childContract.Types[0]).Args[0]).Args[0] is TypeNode.Tv { I: 0 },
            "Continuation changed an immutable operational source contract");
        var projections = 0;
        var projectedChild = first.ToDom((actual, _) => { if (ReferenceEquals(actual, child)) projections++; });
        var projectedBase = second.ToDom();
        Check(projections == 1 && projectedChild["types"][0][ContractKey].GetValue<string>() == frozen,
            "the one owning projection changed contract text");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal) { [mutable] = ICollection };
        var localNames = new HashSet<string>(StringComparer.Ordinal) { "Snapshot.CollectionBase", "Snapshot.CollectionChild" };
        // Continue the actual late owners with their explicit alias facts. No
        // test-local type projector or duplicate native-slot renderer is used.
        var lowered = new[] { BirTypeLowering.Lower(projectedChild, false, aliases, refs.IsValueType,
                physicalTypeNames: refs.PhysicalTypeNames, localTypeNames: localNames),
            BirTypeLowering.Lower(projectedBase, false, aliases, refs.IsValueType,
                physicalTypeNames: refs.PhysicalTypeNames, localTypeNames: localNames) };
        ApplyAll(lowered, refs, type => BirTypeLowering.LowerPhysicalType(type, aliases, refs.IsValueType,
            refs.PhysicalTypeNames, typeArg: true, localTypeNames: localNames));
        var physicalChild = lowered[0]["types"][0].AsObject();
        var physicalBase = lowered[1]["types"][0].AsObject();
        var methods = physicalBase["methods"].AsArray().OfType<JsonObject>().ToArray();
        Check(!physicalBase.ContainsKey(ContractKey) && !physicalChild.ContainsKey(ContractKey)
            && methods.Select(method => method["name"].GetValue<string>()).Order().SequenceEqual(new[] { "Contains", "CopyTo", "get_IsReadOnly" }.Order())
            && physicalChild["methods"].AsArray().Count == 0,
            "retained consumer did not consume frozen contracts or duplicated inherited native slots");
        var contains = methods.Single(method => method["name"].GetValue<string>() == "Contains");
        Check(TypeJson.Read(contains["params"][0]["type"]) is TypeNode.Tv { Scope: "type", I: 1 },
            "native consumer lost the exact physical captured-prefix position");
        Console.WriteLine("[mutable-contract preparation] self-test OK (source closure, immutable continuation fact, owning frontier and retained slot consumer)");
    }
}

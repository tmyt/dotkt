using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ReadOnlyCollectionStorageSynthesis
{
    internal static IReadOnlyList<string> ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType,
        Action<BirFileDeclaration, IReadOnlyList<IBirTypeDeclaration>> publishTypes)
    {
        ArgumentNullException.ThrowIfNull(publishTypes);
        ArgumentNullException.ThrowIfNull(projectReferencedValueType);
        var definitions = SupertypeGraph.CollectSource(roots);
        var declarations = new Dictionary<string, IBirTypeDeclaration>(StringComparer.Ordinal);
        foreach (var declaration in roots.SelectMany(root => root.Types.OrEmpty()))
            if (declaration.Name.OrDefault() is string name) declarations[name] = declaration;
        var used = false;
        var listUsed = false;
        foreach (var definition in definitions.Values)
        {
            if (definition.Kind != "class") continue;
            var reached = SupertypeGraph.Reachable(definition, definitions, refs).Select(edge => edge.spec).ToArray();
            foreach (var face in StorageFaces(reached, definitions, refs))
            {
                var actual = declarations[definition.Name];
                if (!actual.Interfaces.OrEmpty().Any(type => type == face))
                    actual.Interfaces = actual.Interfaces.OrEmpty().Append((TypeNode)BirGraphValue.CopyType(face)).ToArray();
                used = true;
                listUsed |= face.Name == ListCarrierName;
            }
        }
        if (!used) return Array.Empty<string>();
        var host = roots.First();
        IBirTypeDeclaration[] appended = listUsed
            ? new IBirTypeDeclaration[] { CarrierSource(refs, projectReferencedValueType), ListCarrierSource(refs, projectReferencedValueType) }
            : new IBirTypeDeclaration[] { CarrierSource(refs, projectReferencedValueType) };
        host.Types = host.Types.OrEmpty().Concat(appended).ToArray();
        publishTypes(host, appended);
        return listUsed ? new[] { CarrierName, ListCarrierName } : new[] { CarrierName };
    }

    static TypeNode SourceType(TypeNode type) => BirGraphValue.CopyType(type);
    static BirLocal SourceLocal(string name) => new() { Name = name };
    static BirReturn SourceReturn(IBirExpression value) => new() { Value = OptionalField<IBirExpression>.Of(value) };
    static BirParameter SourceParameter(string name, TypeNode type) => new() { Name = name, Type = SourceType(type) };
    static BirFunctionDeclaration SourceMethod(string slot, TypeNode result, BirParameter[] parameters,
        IBirStatement[] body, string face = CollectionViewFaces.ICollection) => new() {
        Name = "dotkt$storage$" + slot, Static = false, Virtual = true,
        Abstract = false, Override = false, Vis = "private", Generated = true, ClrInterfaceSlotBridge = true,
        Params = parameters, Ret = SourceType(result), Body = body,
        ClrInterfaceImpls = new[] { new BirMethodImpl { Owner = Face(face, SourceType(Element)), Member = slot, Arity = 0,
            Params = parameters.Select(parameter => SourceType(parameter.Type.Value)).ToArray(), Ret = SourceType(result) } },
    };
    static IBirStatement[] UnsupportedSource() => new IBirStatement[] { new BirThrow { Value = new BirNew {
        Type = new TypeNode.Fqn("System.NotSupportedException"), ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(),
    } } };
    static BirCast ReadOnlySelfSource(string face = CollectionViewFaces.IReadOnlyCollection) => new() {
        Type = Face(face, SourceType(Element)), E = new BirThis(),
    };
    static BirCallStatic StorageHelperSource(ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType,
        string name, string sourceCollection,
        TypeNode result, IBirExpression receiver)
    {
        var plan = PlanStorageHelper(refs, name, sourceCollection, projectReferencedValueType);
        return new() { Owner = new TypeNode.Fqn("kotlin.collections.ClrCollectionDefaultsKt"), Method = name,
            DeclarationId = plan.DeclarationId, TypeArgs = plan.TypeArguments.Select(SourceType).ToArray(),
            Sig = plan.Signature.Select(SourceType).ToArray(), Ret = SourceType(result),
            Args = new IBirExpression[] { receiver, SourceLocal("element") } };
    }

    static BirInterfaceDefinition CarrierSource(ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType)
    {
        var copy = CollectionBclSlotSynthesis.CopyToSource(Element);
        var parameters = copy.Params.OrEmpty();
        var body = copy.Body.OrEmpty();
        copy.Params = default; copy.Body = default;
        var methods = new[] {
            SourceMethod("get_Count", Int, Array.Empty<BirParameter>(), new IBirStatement[] { SourceReturn(new BirCallInstance {
                OwnerType = Face(CollectionViewFaces.IReadOnlyCollection, SourceType(Element)), Virtual = true,
                Recv = ReadOnlySelfSource(), Method = "get_Count", Sig = Array.Empty<TypeNode>(), Ret = SourceType(Int), Args = Array.Empty<IBirExpression>(),
            }) }),
            SourceMethod("get_IsReadOnly", Bool, Array.Empty<BirParameter>(), new IBirStatement[] { SourceReturn(new BirConstant {
                Type = SourceType(Bool), Value = new BirLiteral.BooleanLiteral(true),
            }) }),
            SourceMethod("Contains", Bool, new[] { SourceParameter("element", Element) }, new IBirStatement[] {
                SourceReturn(StorageHelperSource(refs, projectReferencedValueType, "clrCollStorageContains", "kotlin.collections.Collection", Bool, ReadOnlySelfSource())) }),
            SourceMethod("CopyTo", Void, parameters, body),
            SourceMethod("Add", Void, new[] { SourceParameter("element", Element) }, UnsupportedSource()),
            SourceMethod("Remove", Bool, new[] { SourceParameter("element", Element) }, UnsupportedSource()),
            SourceMethod("Clear", Void, Array.Empty<BirParameter>(), UnsupportedSource()),
        };
        return new() { Name = CarrierName, Kind = "interface", Vis = "internal", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Base = OptionalField<TypeNode>.Null,
            Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Interfaces = new TypeNode[] { Face(CollectionViewFaces.ICollection, SourceType(Element)),
                Face(CollectionViewFaces.IReadOnlyCollection, SourceType(Element)) }, Methods = methods };
    }

    static BirInterfaceDefinition ListCarrierSource(ReferenceMetadataIndex refs, Func<TypeNode, TypeNode> projectReferencedValueType)
    {
        const string face = CollectionViewFaces.IList;
        const string readOnly = CollectionViewFaces.IReadOnlyList;
        var methods = new[] {
            SourceMethod("get_Item", Element, new[] { SourceParameter("index", Int) }, new IBirStatement[] { SourceReturn(new BirCallInstance {
                OwnerType = Face(readOnly, SourceType(Element)), Virtual = true, Recv = ReadOnlySelfSource(readOnly), Method = "get_Item",
                Sig = new[] { SourceType(Int) }, Ret = SourceType(Element), Args = new IBirExpression[] { SourceLocal("index") },
            }) }, face),
            SourceMethod("IndexOf", Int, new[] { SourceParameter("element", Element) }, new IBirStatement[] {
                SourceReturn(StorageHelperSource(refs, projectReferencedValueType, "clrListStorageIndexOf", "kotlin.collections.List", Int, ReadOnlySelfSource(readOnly))) }, face),
            SourceMethod("set_Item", Void, new[] { SourceParameter("index", Int), SourceParameter("value", Element) }, UnsupportedSource(), face),
            SourceMethod("Insert", Void, new[] { SourceParameter("index", Int), SourceParameter("element", Element) }, UnsupportedSource(), face),
            SourceMethod("RemoveAt", Void, new[] { SourceParameter("index", Int) }, UnsupportedSource(), face),
        };
        return new() { Name = ListCarrierName, Kind = "interface", Vis = "internal", Generated = true,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Base = OptionalField<TypeNode>.Null,
            Fields = Array.Empty<BirFieldDeclaration>(), Ctors = Array.Empty<BirConstructor>(),
            Interfaces = new TypeNode[] { Face(CarrierName, SourceType(Element)), Face(face, SourceType(Element)), Face(readOnly, SourceType(Element)) },
            Methods = methods };
    }
}

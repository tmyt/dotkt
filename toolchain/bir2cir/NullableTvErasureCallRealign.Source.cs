using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    // Capture the current post-override declarations. The index owns types and
    // scalar keys, never a declaration reference from the continuing graph.
    public static DeclIndex CollectDeclaredMemberRets(IReadOnlyList<BirFileDeclaration> roots,
        ReferenceMetadataIndex refs = null)
    {
        var index = new DeclIndex { SourceApplicationOrder = new InnerApplicationOrder(roots, refs) };
        foreach (var file in roots)
        {
            var facade = file.FileClass.OrDefault();
            if (facade is { Length: > 0 })
            {
                index.FileClasses.Add(facade);
                if (!index.Slots.TryGetValue(facade, out var slots))
                    index.Slots[facade] = slots = new(StringComparer.Ordinal);
                CollectSlots(file.Fields.OrEmpty(), file.Properties.OrEmpty(), slots);
            }
            // Include every named method, including nongeneric overloads. A
            // same-name/arity conflict poisons the shared fallback key.
            foreach (var method in file.Methods.OrEmpty())
            {
                if (ReadMethodSnapshot(method, includeProperty: facade is { Length: > 0 }) is not { } snapshot) continue;
                Dictionary<string, List<DeclSig>> properties = null;
                if (facade is { Length: > 0 } && snapshot.PropertyName != null
                    && !index.TopLevelPropertiesByOwner.TryGetValue(facade, out properties))
                    index.TopLevelPropertiesByOwner[facade] = properties = new(StringComparer.Ordinal);
                AddMethodSnapshot(index, facade, snapshot, index.TopLevel, properties, topLevel: true);
            }
            foreach (var type in file.Types.OrEmpty())
            {
                var name = type.Name.OrDefault();
                if (name == null || index.ByOwner.ContainsKey(name)) continue;
                var signatures = new Dictionary<string, DeclSig>(StringComparer.Ordinal);
                var properties = new Dictionary<string, List<DeclSig>>(StringComparer.Ordinal);
                foreach (var method in type.Methods.OrEmpty())
                    if (ReadMethodSnapshot(method) is { } snapshot)
                        AddMethodSnapshot(index, name, snapshot, signatures, properties);
                index.ByOwner[name] = signatures;
                index.PropertiesByOwner[name] = properties;
                var constructors = new Dictionary<int, TypeNode[]>();
                foreach (var constructor in type.Ctors.OrEmpty())
                    if (ReadParams(constructor.Params.OrEmpty(), preferCarrier: false) is { } parameters)
                        AddConstructorSnapshot(constructors, parameters);
                index.Ctors[name] = constructors;
                var slots = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
                CollectSlots(type.Fields.OrEmpty(), type.Properties.OrEmpty(), slots);
                index.Slots[name] = slots;
            }
        }
        return index;
    }

    static MethodSnapshot ReadMethodSnapshot(BirFunctionDeclaration method, bool includeProperty = true)
    {
        if (!method.Name.HasValue || ReadSig(method) is not { } signature) return null;
        string propertyName = null, accessorKind = null;
        if (includeProperty) KotlinPropertyAccessors.TryIdentity(method, out propertyName, out accessorKind);
        return new MethodSnapshot(method.Name.Value, method.DeclarationId.OrDefault(), propertyName, accessorKind,
            method.TypeParams.OrEmpty().Length, method.Static.OrDefault(), signature);
    }

    static DeclSig ReadSig(BirFunctionDeclaration method)
    {
        var signature = ReadSignature(method.Ret.OrDefault(), () => method.NullableGenericRet.OrDefault(),
            () => method.RetKotlinType.OrDefault(), ownershipKnown: false,
            preferCarrier => ReadParams(method.Params.OrEmpty(), preferCarrier));
        if (signature == null) return null;
        signature.Ret = BirGraphValue.CopyType(signature.Ret);
        // ReadParams already owns its parameter-type snapshots.
        return signature;
    }

    static TypeNode[] ReadParams(BirParameter[] parameters, bool preferCarrier)
        => ReadParameterVector(parameters.Length, index => {
            var parameter = parameters[index];
            var type = ReadDeclaredSlot(parameter.Type.OrDefault(), () => parameter.NullableGeneric.OrDefault(), preferCarrier);
            return type == null ? null : BirGraphValue.CopyType(type);
        });

    static void CollectSlots(IEnumerable<BirFieldDeclaration> fields, IEnumerable<BirPropertyDeclaration> properties,
        Dictionary<string, TypeNode> slots)
    {
        foreach (var field in fields)
            if (field.Name.HasValue && field.Type.HasValue)
                AddSlotSnapshot(slots, field.Name.Value, BirGraphValue.CopyType(field.Type.Value));
        // Field storage precedes property slots and shares the same ambiguity
        // policy; a conflict remains poisoned rather than becoming last-wins.
        foreach (var property in properties)
            if (property.Name.HasValue && property.Type.HasValue)
                AddSlotSnapshot(slots, property.Name.Value, BirGraphValue.CopyType(property.Type.Value));
    }
}

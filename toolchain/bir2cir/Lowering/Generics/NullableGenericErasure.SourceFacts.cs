using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableGenericErasure
{
    internal static void PreserveSourceFacts(BirFileDeclaration root, ValueTypeOracle isValue)
    {
        foreach (var owner in NullableRepresentationViews.Owners(new[] { root }))
        {
            foreach (var method in owner.Methods.OrEmpty())
            {
                if (!method.MethodTypeParameterBoundsPre.HasValue && !method.NullableRepresentationFrame.HasValue && method.TypeParams.HasValue)
                {
                    var bounds = method.TypeParams.Value.Select((parameter, index) => (parameter, index))
                        .Where(pair => pair.parameter.Constraints.HasValue && pair.parameter.Constraints.Value.Length != 0)
                        .ToDictionary(pair => pair.index, pair => pair.parameter.Constraints.Value);
                    if (bounds.Count != 0) method.MethodTypeParameterBoundsPre = new SourceBoundsSnapshot(bounds);
                }
                if (!method.NullableGenericRet.HasValue && method.Ret.OrDefault() is { } result && HasRestorableNullableTv(result, isValue))
                {
                    method.NullableGenericRet = result;
                    if (result is TypeNode.Nullable { Of: TypeNode.Tv } && NullableFlags.ComputeValues(result, isValue) is { } flags)
                        method.RetNullableFlags = flags;
                }
                if (method.SuspendRet.OrDefault() is TypeNode.Nullable { Of: TypeNode.Tv } suspend)
                    method.NullableGenericSuspendRet = suspend;
                foreach (var parameter in method.Params.OrEmpty()) Record(parameter, isValue);
            }
            foreach (var constructor in owner.Constructors)
                foreach (var parameter in constructor.Params.OrEmpty()) Record(parameter, isValue);
            foreach (var field in owner.Fields) Record(field, isValue);
            foreach (var property in owner.Properties) Record(property, isValue);
            if (owner.Node is IBirTypeDeclaration) RecordEdges(owner, isValue);
        }
        void SuspendShapes(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.RewriteRepresentationInPlace((ownership, role, type) => {
                if (type is TypeNode.Fn { Suspend: true } function && HasNullableTv(function))
                {
                    if (role == BirRepresentationRole.ValueType && !node.SuspendFunctionTypeFact.IsPresent)
                        node.SuspendFunctionTypeFact = function;
                    if (role == BirRepresentationRole.Return && !node.SuspendFunctionReturnFact.IsPresent)
                        node.SuspendFunctionReturnFact = function;
                }
                return type;
            }, (_, _, child) => SuspendShapes(child));
        }
        SuspendShapes(root);
    }

    static void Record(BirParameter parameter, ValueTypeOracle isValue)
    {
        if (parameter.NullableGeneric.HasValue || parameter.Type.OrDefault() is not { } type || !HasRestorableNullableTv(type, isValue)) return;
        parameter.NullableGeneric = type;
        if (type is TypeNode.Nullable { Of: TypeNode.Tv } && NullableFlags.ComputeValues(type, isValue) is { } flags) parameter.NullableFlags = flags;
    }
    static void Record(BirFieldDeclaration field, ValueTypeOracle isValue)
    {
        if (field.NullableGeneric.HasValue || field.Type.OrDefault() is not { } type || !HasRestorableNullableTv(type, isValue)) return;
        field.NullableGeneric = type;
        if (type is TypeNode.Nullable { Of: TypeNode.Tv } && NullableFlags.ComputeValues(type, isValue) is { } flags) field.NullableFlags = flags;
    }
    static void Record(BirPropertyDeclaration property, ValueTypeOracle isValue)
    {
        if (property.NullableGeneric.HasValue || property.Type.OrDefault() is not { } type || !HasRestorableNullableTv(type, isValue)) return;
        property.NullableGeneric = type;
        if (type is TypeNode.Nullable { Of: TypeNode.Tv } && NullableFlags.ComputeValues(type, isValue) is { } flags) property.NullableFlags = flags;
    }
    static void RecordEdges(NullableRepresentationViews.Owner owner, ValueTypeOracle isValue)
    {
        var baseType = owner.Base is { } source && Erase(source, Pos.Slot, isValue) != source ? source : null;
        var interfaces = owner.Interfaces.Where(type => Erase(type, Pos.Slot, isValue) != type).ToArray();
        var bounds = new Dictionary<int, TypeNode[]>();
        var frame = owner.Facts?.NullableFrame;
        var parameters = owner.Parameters.OrEmpty();
        for (var index = 0; index < parameters.Length; index++)
            if (parameters[index].Constraints.HasValue && parameters[index].Constraints.Value.Length != 0
                && parameters[index].Constraints.Value.Any(type => Erase(type, Pos.Slot, isValue) != type)
                && (frame == null ? index : frame.SourceIndex(index)) is int sourceIndex)
                bounds[sourceIndex] = parameters[index].Constraints.Value;
        if (baseType != null || interfaces.Length != 0 || bounds.Count != 0)
            owner.Facts = (owner.Facts ?? SourceTypeFacts.Empty).Merge(baseType, interfaces, bounds);
    }
}

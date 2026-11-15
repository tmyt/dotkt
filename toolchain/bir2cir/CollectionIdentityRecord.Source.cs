using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CollectionIdentityRecord
{
    internal static void RecordMemberSlots(BirFileDeclaration file)
    {
        void Parameters(IEnumerable<BirParameter> parameters)
        {
            foreach (var parameter in parameters)
                if (parameter.Type.HasValue && NestsCollapsingReadonly(parameter.Type.Value))
                    parameter.CollIdentity = BirGraphValue.CopyType(parameter.Type.Value);
        }
        void Members(IEnumerable<BirFunctionDeclaration> methods, IEnumerable<BirConstructor> constructors,
            IEnumerable<BirFieldDeclaration> fields, IEnumerable<BirPropertyDeclaration> properties)
        {
            foreach (var method in methods)
            {
                if (method.Ret.HasValue && NestsCollapsingReadonly(method.Ret.Value))
                    method.CollIdentityRet = BirGraphValue.CopyType(method.Ret.Value);
                Parameters(method.Params.OrEmpty());
            }
            foreach (var constructor in constructors) Parameters(constructor.Params.OrEmpty());
            foreach (var property in properties)
                if (property.Type.HasValue && NestsCollapsingReadonly(property.Type.Value))
                    property.CollIdentity = BirGraphValue.CopyType(property.Type.Value);
            foreach (var field in fields)
                if (field.Type.HasValue && NestsCollapsingReadonly(field.Type.Value))
                    field.CollIdentity = BirGraphValue.CopyType(field.Type.Value);
        }
        Members(file.Methods.OrEmpty(), Array.Empty<BirConstructor>(), file.Fields.OrEmpty(), file.Properties.OrEmpty());
        foreach (var owner in file.Types.OrEmpty())
            Members(owner.Methods.OrEmpty(), owner.Ctors.OrEmpty(), owner.Fields.OrEmpty(), owner.Properties.OrEmpty());
    }

    internal static void RecordTypeEdges(BirFileDeclaration file)
    {
        foreach (var declaration in file.Types.OrEmpty())
        {
            var baseType = declaration.Base.OrDefault();
            if (baseType == null || !NestsCollapsingReadonly(baseType)) baseType = null;
            var interfaces = declaration.Interfaces.OrEmpty().Where(type => NestsCollapsingReadonly(type)
                || type is TypeNode.Fqn { Name: "kotlin.collections.MutableCollection"
                    or "kotlin.collections.MutableList" or "kotlin.collections.MutableSet" })
                .Select(BirGraphValue.CopyType).ToArray();
            // This record owns only declared rows, not a captured-parameter
            // prefix or the independently frozen nullable companion frame.
            var bounds = new Dictionary<int, TypeNode[]>();
            var parameters = declaration.TypeParams.OrEmpty();
            for (var index = 0; index < parameters.Length; index++)
                if (parameters[index].Constraints.HasValue && parameters[index].Constraints.Value.Any(NestsCollapsingReadonly))
                    bounds[index] = parameters[index].Constraints.Value.Select(BirGraphValue.CopyType).ToArray();
            var node = (BirGraphValue)declaration;
            var before = NullableRepresentationViews.TypeFacts(node);
            var after = (before ?? SourceTypeFacts.Empty).Merge(BirGraphValue.CopyType(baseType), interfaces, bounds);
            if (!ReferenceEquals(after, before ?? SourceTypeFacts.Empty))
                NullableRepresentationViews.SetTypeFacts(node, after);
        }
    }
}

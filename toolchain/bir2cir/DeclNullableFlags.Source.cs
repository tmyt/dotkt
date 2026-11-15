using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class DeclNullableFlags
{
    internal static void Apply(BirFileDeclaration file, ValueTypeOracle isValue,
        Func<TypeNode.Fqn, TypeNode[]> annotationArguments, Func<TypeNode.Fqn, bool> hasAlias)
    {
        void Parameter(BirParameter parameter)
        {
            parameter.KotlinType = Surface(parameter.Type, parameter.KotlinType, parameter.NullableGeneric);
            if (!parameter.NullableFlags.IsPresent && Flags(parameter.Type) is { } flags) parameter.NullableFlags = flags;
        }
        void Method(BirFunctionDeclaration method)
        {
            method.RetKotlinType = Surface(method.Ret, method.RetKotlinType, method.NullableGenericRet);
            if (!method.RetNullableFlags.IsPresent && Flags(method.Ret) is { } flags) method.RetNullableFlags = flags;
            foreach (var parameter in method.Params.OrEmpty()) Parameter(parameter);
        }
        int[] Flags(OptionalField<TypeNode> type) => type.HasValue
            ? NullableFlags.ComputeValues(type.Value, isValue, annotationArguments: annotationArguments) : null;
        OptionalField<TypeNode> Surface(OptionalField<TypeNode> type, OptionalField<TypeNode> previous,
            OptionalField<TypeNode> generic)
            => !previous.HasValue && !generic.HasValue && type.HasValue && RequiresExactSurface(type.Value, hasAlias)
                ? BirGraphValue.CopyType(type.Value) : previous;
        void Members(IEnumerable<BirFunctionDeclaration> methods, IEnumerable<BirConstructor> constructors,
            IEnumerable<BirFieldDeclaration> fields, IEnumerable<BirPropertyDeclaration> properties)
        {
            foreach (var method in methods) Method(method);
            foreach (var constructor in constructors)
                foreach (var parameter in constructor.Params.OrEmpty()) Parameter(parameter);
            foreach (var field in fields)
            {
                field.KotlinType = Surface(field.Type, field.KotlinType, field.NullableGeneric);
                if (!field.NullableFlags.IsPresent && Flags(field.Type) is { } flags) field.NullableFlags = flags;
            }
            foreach (var property in properties)
            {
                property.KotlinType = Surface(property.Type, property.KotlinType, property.NullableGeneric);
                if (!property.NullableFlags.IsPresent && Flags(property.Type) is { } flags) property.NullableFlags = flags;
            }
        }
        // Deliberately declaration vectors, not a walk of executable/capture or
        // descriptor graphs. Current generated classes are already in Types.
        Members(file.Methods.OrEmpty(), Array.Empty<BirConstructor>(), file.Fields.OrEmpty(), file.Properties.OrEmpty());
        foreach (var owner in file.Types.OrEmpty())
            Members(owner.Methods.OrEmpty(), owner.Ctors.OrEmpty(), owner.Fields.OrEmpty(), owner.Properties.OrEmpty());
    }
}

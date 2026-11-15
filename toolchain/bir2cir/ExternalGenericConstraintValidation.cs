using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;

// Validate the CLR-only half of referenced generic type and method declarations.
//
// dll2klib can expose an ordinary nominal row (`T : IFoo`) as a KLIB upper bound and Kotlin's frontend then owns
// subtype checking. ECMA-335 also has constraints Kotlin metadata cannot state as nominal bounds: class/struct/new()
// flags, plus the implicit System.ValueType/System.Enum rows emitted with value/enum constraints. Publishing those
// roots as Kotlin bounds makes every legal Kotlin value uninhabitable; dropping the flags lets invalid TypeSpecs reach
// the loader. ReferenceMetadataIndex already owns their exact CLR declarations, so bir2cir checks constructed types
// before physical lowering and checks method arguments after exact member resolution has selected the MethodDef.
static partial class ExternalGenericConstraintValidation
{
    sealed record ParameterFacts(bool Reference, bool NonNullableValue, bool Enum, bool PublicDefaultConstructor);
    sealed record ConstraintFacts(IReadOnlyList<string> Specials, IReadOnlyList<TypeNode> Bounds);
    internal sealed record LocalTypeFacts(string Kind, bool Abstract, bool PublicDefaultConstructor);

    // These vectors describe another declaration in that declaration's own generic frame. They are selection/linkage
    // facts, not use-site TypeSpecs, so resolving their !N/!!N against the caller's lexical frame is always wrong.
    // Keep this boundary aligned with TypeOwnershipLowering's lexical-frame rewrites.
    static readonly HashSet<string> ForeignDeclarationTypeKeys = new(StringComparer.Ordinal)
    {
        "sig", "resolvedMemberParams", "shapeTypes", "paramSig", "delegationSig",
        "memberOwnerTypeParams", "memberMethodTypeParams", "memberReturnType", "memberSignature", "memberType",
        ClrMemberResolution.ResolvedMethodTypeParamsKey,
    };

    static bool UseSiteTypeField(string field, bool construction) =>
        !ForeignDeclarationTypeKeys.Contains(field) && (field != "argTypes" || construction);

    public sealed partial class Prepared
    {
        readonly ReferenceMetadataIndex _refs;
        readonly ValueTypeOracle _isValueFqn;
        readonly IReadOnlySet<string> _localEnums;
        readonly IReadOnlyDictionary<string, LocalTypeFacts> _localTypes;
        // ReferenceMetadataIndex stores declarations as immutable serialized metadata. Parse each owner once for the
        // whole module instead of once for every occurrence of a constructed type.
        readonly Dictionary<string, ConstraintFacts[]> _declarationCache = new(StringComparer.Ordinal);

        internal Prepared(ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn,
            IReadOnlySet<string> localEnums, IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
        {
            _refs = refs;
            _isValueFqn = isValueFqn;
            _localEnums = localEnums;
            _localTypes = localTypes;
        }

    }

    static void ValidateType(TypeNode type, ParameterFacts[] typeParameters, ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes, IDictionary<string, ConstraintFacts[]> declarationCache)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } application:
                foreach (var argument in args)
                    ValidateType(argument, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                        declarationCache);
                if (!declarationCache.TryGetValue(application.Name, out var declarations))
                {
                    declarations = ReadConstraintDeclarations(refs.OwnerTypeParamDeclarations(application.Name));
                    declarationCache[application.Name] = declarations;
                }
                if (declarations is null ||
                    declarations.Length != args.Length)
                    return;
                for (var i = 0; i < args.Length; i++)
                {
                    if (declarations[i] is not { } declaration) continue;
                    ValidateArgument(application.Name, i, args[i], declaration, typeParameters, methodParameters,
                        refs, isValueFqn, localEnums, localTypes);
                }
                return;
            case TypeNode.Fqn:
            case TypeNode.Tv:
            case TypeNode.Star:
                return;
            case TypeNode.Nullable nullable:
                ValidateType(nullable.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                return;
            case TypeNode.Oblivious oblivious:
                ValidateType(oblivious.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                return;
            case TypeNode.Array array:
                ValidateType(array.Elem, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                return;
            case TypeNode.ByRef byRef:
                ValidateType(byRef.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                return;
            case TypeNode.Ptr pointer:
                ValidateType(pointer.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                return;
            case TypeNode.Mod modifier:
                ValidateType(modifier.M, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                ValidateType(modifier.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                return;
            case TypeNode.Fn function:
                ValidateType(function.Ret, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
                foreach (var parameter in function.DelegateParams)
                    ValidateType(parameter, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                        declarationCache);
                return;
        }
    }

    static void ValidateArgument(string owner, int index, TypeNode argument, ConstraintFacts declaration,
        ParameterFacts[] typeParameters, ParameterFacts[] methodParameters, ReferenceMetadataIndex refs,
        ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        var actual = Facts(argument, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
        var specials = declaration.Specials;
        var requiresValue = specials.Contains("struct");
        var requiresEnum = false;
        foreach (var constraint in declaration.Bounds)
            if (constraint is TypeNode.Fqn bound)
            {
                requiresValue |= bound.Name == "System.ValueType";
                requiresEnum |= bound.Name == "System.Enum";
            }
        if (requiresValue && !actual.NonNullableValue)
            Fail(owner, index, argument, "a non-nullable CLR value type");
        if (requiresEnum && !actual.Enum)
            Fail(owner, index, argument, "a CLR enum type");
        if (specials.Contains("class") && !actual.Reference)
            Fail(owner, index, argument, "a CLR reference type");
        if (specials.Contains("new") && !actual.PublicDefaultConstructor)
            Fail(owner, index, argument, "a public parameterless constructor");
    }

    static ParameterFacts Facts(TypeNode type, ParameterFacts[] typeParameters, ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        switch (type)
        {
            case TypeNode.Tv { Scope: "type" } tv when tv.I >= 0 && tv.I < typeParameters.Length:
                return typeParameters[tv.I];
            case TypeNode.Tv { Scope: "method" } tv when tv.I >= 0 && tv.I < methodParameters.Length:
                return methodParameters[tv.I];
            case TypeNode.Nullable nullable:
            {
                var inner = Facts(nullable.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums,
                    localTypes);
                return inner.NonNullableValue
                    ? new ParameterFacts(false, false, false, false)
                    : inner with { NonNullableValue = false, Enum = false };
            }
            case TypeNode.Oblivious oblivious:
                return Facts(oblivious.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
            case TypeNode.Array:
            case TypeNode.Fn:
                return new ParameterFacts(true, false, false, false);
            case TypeNode.Fqn fqn:
            {
                var name = fqn.Name;
                string kind;
                if (localTypes.TryGetValue(name, out var localFact))
                    kind = localFact.Kind;
                else if (refs.TryResolveClrOwner(name, out var physicalName, out var aliasKind))
                {
                    name = physicalName;
                    kind = aliasKind;
                }
                else
                    kind = LocalOrReferencedKind(fqn, refs, localTypes);
                if (isValueFqn(fqn))
                    return new ParameterFacts(false, true, localEnums.Contains(name) || kind == "enum", true);
                var isInterface = kind == "interface";
                var isAbstract = localTypes.TryGetValue(name, out var local) && local.Abstract;
                var referencedOwner = new TypeNode.Fqn(name, fqn.Args);
                var hasDefault = name == "System.Object" ||
                    localTypes.GetValueOrDefault(name)?.PublicDefaultConstructor == true ||
                    refs.HasPublicParameterlessConstructor(referencedOwner);
                return new ParameterFacts(true, false, kind == "enum",
                    !isInterface && !isAbstract && hasDefault);
            }
            default:
                return new ParameterFacts(false, false, false, false);
        }
    }

    static ConstraintFacts[] ReadConstraintDeclarations(IReadOnlyList<BirTypeParameter> parameters) => parameters?.Select(parameter =>
        parameter == null ? null : new ConstraintFacts(parameter.SpecialConstraints.OrEmpty().Where(value => value != null).ToArray(),
            parameter.Constraints.OrEmpty())).ToArray();

    static ParameterFacts[] ReadParameterFacts(IReadOnlyList<ConstraintFacts> parameters, ReferenceMetadataIndex refs,
        ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        if (parameters == null) return Array.Empty<ParameterFacts>();
        var result = new ParameterFacts[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is not { } parameter)
            {
                result[i] = new ParameterFacts(false, false, false, false);
                continue;
            }
            var specials = parameter.Specials;
            var reference = specials.Contains("class");
            var value = specials.Contains("struct");
            var isEnum = false;
            foreach (var constraint in parameter.Bounds)
                if (constraint is TypeNode.Fqn bound)
                {
                    value |= bound.Name == "System.ValueType";
                    isEnum |= bound.Name is "System.Enum" or "kotlin.Enum";
                    var kind = LocalOrReferencedKind(bound, refs, localTypes);
                    reference |= kind == "class" && !isValueFqn(bound);
                }
            result[i] = new ParameterFacts(reference, value, isEnum, specials.Contains("new") || value);
        }
        return result;
    }

    static bool IsTypeKind(string kind) => kind is "class" or "interface" or "enum" or "struct" or "value";

    static string LocalOrReferencedKind(TypeNode.Fqn type, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        if (localTypes.TryGetValue(type.Name, out var local)) return local.Kind;
        return refs.TryReferenceTypeShape(type, out _, out var kind, out _, out _) ? kind : null;
    }

    static void Fail(string owner, int index, TypeNode actual, string requirement) =>
        throw new InvalidOperationException(
            $"bir2cir: CLR generic constraint violation: type argument {index} of '{owner}' is " +
            $"'{Display(actual)}', but the referenced declaration requires {requirement}");

    static string Display(TypeNode type) => type switch
    {
        TypeNode.Fqn { Args: { } args } f => $"{f.Name}<{string.Join(", ", args.Select(Display))}>",
        TypeNode.Fqn f => f.Name,
        TypeNode.Tv tv => tv.Scope == "method" ? $"!!{tv.I}" : $"!{tv.I}",
        TypeNode.Nullable nullable => Display(nullable.Of) + "?",
        TypeNode.Oblivious oblivious => Display(oblivious.Of) + "!",
        TypeNode.Array array => Display(array.Elem) + "[]",
        TypeNode.Fn => "function type",
        _ => type.GetType().Name,
    };

    static string Str(JsonNode node) => (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;
}

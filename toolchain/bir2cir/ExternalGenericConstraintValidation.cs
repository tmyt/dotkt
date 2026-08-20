using System.Text.Json.Nodes;
using DotKt.Bir;

// Validate the CLR-only half of a referenced generic TYPE declaration before physical type lowering.
//
// dll2klib can expose an ordinary nominal row (`T : IFoo`) as a KLIB upper bound and Kotlin's frontend then owns
// subtype checking. ECMA-335 also has constraints Kotlin metadata cannot state as nominal bounds: class/struct/new()
// flags, plus the implicit System.ValueType/System.Enum rows emitted with value/enum constraints. Publishing those
// roots as Kotlin bounds makes every legal Kotlin value uninhabitable; dropping the flags lets invalid TypeSpecs reach
// the loader. ReferenceMetadataIndex already owns their exact CLR declarations, so bir2cir checks this physical part
// while the constructed BIR type and its declaration-scoped type variables are still explicit.
static class ExternalGenericConstraintValidation
{
    sealed record ParameterFacts(bool Reference, bool NonNullableValue, bool Enum, bool PublicDefaultConstructor);
    sealed record LocalTypeFacts(string Kind, bool Abstract, bool PublicDefaultConstructor);

    public static void Apply(JsonNode root, IEnumerable<JsonNode> allRoots, ReferenceMetadataIndex refs,
        Func<string, bool> isValueFqn, IReadOnlySet<string> localEnums)
    {
        var localTypes = CollectLocalTypes(allRoots, isValueFqn);
        Walk(root, Array.Empty<ParameterFacts>(), Array.Empty<ParameterFacts>(), refs, isValueFqn, localEnums,
            localTypes);
    }

    static Dictionary<string, LocalTypeFacts> CollectLocalTypes(IEnumerable<JsonNode> roots,
        Func<string, bool> isValueFqn)
    {
        var result = new Dictionary<string, LocalTypeFacts>(StringComparer.Ordinal);
        foreach (var root in roots) Collect(root);
        return result;

        void Collect(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (Str(o["name"]) is string name && Str(o["kind"]) is string kind && o["types"] is JsonArray)
                    Add(name, kind, o);
                // Ordinary type declarations need not themselves own a nested `types` array.
                else if (Str(o["name"]) is string ordinaryName && Str(o["kind"]) is string ordinaryKind &&
                         o.ContainsKey("ctors"))
                    Add(ordinaryName, ordinaryKind, o);
                foreach (var value in o.Select(pair => pair.Value))
                    if (value is not null) Collect(value);
            }
            else if (node is JsonArray array)
                foreach (var value in array)
                    if (value is not null) Collect(value);
        }

        void Add(string name, string kind, JsonObject declaration)
        {
            var isValue = isValueFqn(name) || kind is "struct" or "enum" or "value";
            var isAbstract = Bool(declaration["abstract"]);
            var hasPublicDefault = isValue || !isAbstract && declaration["ctors"] is JsonArray ctors &&
                ctors.OfType<JsonObject>().Any(ctor => Str(ctor["vis"]) == "public" &&
                    (ctor["params"] as JsonArray)?.Count == 0);
            result[name] = new LocalTypeFacts(kind, isAbstract, hasPublicDefault);
        }
    }

    static void Walk(JsonNode node, ParameterFacts[] typeParameters, ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, Func<string, bool> isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        if (node is JsonArray array)
        {
            foreach (var value in array)
                if (value is not null)
                    Walk(value, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
            return;
        }
        if (node is not JsonObject o) return;

        if (o["t"] is JsonValue)
        {
            if (TypeJson.Read(o) is TypeNode type)
                ValidateType(type, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
            return;
        }

        var nextTypeParameters = typeParameters;
        var nextMethodParameters = methodParameters;
        if (Str(o["kind"]) is string && o.ContainsKey("ctors"))
            nextTypeParameters = ReadParameterFacts(o["typeParams"], refs, isValueFqn, localEnums, localTypes);
        else if (o.ContainsKey("params") && (o.ContainsKey("ret") || o.ContainsKey("body")))
            nextMethodParameters = ReadParameterFacts(o["typeParams"], refs, isValueFqn, localEnums, localTypes);

        foreach (var value in o.Select(pair => pair.Value))
            if (value is not null)
                Walk(value, nextTypeParameters, nextMethodParameters, refs, isValueFqn, localEnums, localTypes);
    }

    static void ValidateType(TypeNode type, ParameterFacts[] typeParameters, ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, Func<string, bool> isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } application:
                foreach (var argument in args)
                    ValidateType(argument, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                if (refs.OwnerTypeParamDeclarations(application.Name) is not JsonArray declarations ||
                    declarations.Count != args.Length)
                    return;
                for (var i = 0; i < args.Length; i++)
                {
                    if (declarations[i] is not JsonObject declaration) continue;
                    var actual = Facts(args[i], typeParameters, methodParameters, refs, isValueFqn, localEnums,
                        localTypes);
                    var specials = (declaration["specialConstraints"] as JsonArray)?
                        .Select(Str).Where(value => value is not null).ToHashSet(StringComparer.Ordinal)
                        ?? new HashSet<string>(StringComparer.Ordinal);
                    var requiresValue = specials.Contains("struct");
                    var requiresEnum = false;
                    if (declaration["constraints"] is JsonArray constraints)
                        foreach (var constraint in constraints)
                            if (TypeJson.Read(constraint) is TypeNode.Fqn bound)
                            {
                                requiresValue |= bound.Name == "System.ValueType";
                                requiresEnum |= bound.Name == "System.Enum";
                            }
                    if (requiresValue && !actual.NonNullableValue)
                        Fail(application.Name, i, args[i], "a non-nullable CLR value type");
                    if (requiresEnum && !actual.Enum)
                        Fail(application.Name, i, args[i], "a CLR enum type");
                    if (specials.Contains("class") && !actual.Reference)
                        Fail(application.Name, i, args[i], "a CLR reference type");
                    if (specials.Contains("new") && !actual.PublicDefaultConstructor)
                        Fail(application.Name, i, args[i], "a public parameterless constructor");
                }
                return;
            case TypeNode.Fqn:
            case TypeNode.Tv:
            case TypeNode.Star:
                return;
            case TypeNode.Nullable nullable:
                ValidateType(nullable.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
            case TypeNode.Oblivious oblivious:
                ValidateType(oblivious.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
            case TypeNode.Array array:
                ValidateType(array.Elem, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
            case TypeNode.ByRef byRef:
                ValidateType(byRef.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
            case TypeNode.Ptr pointer:
                ValidateType(pointer.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
            case TypeNode.Mod modifier:
                ValidateType(modifier.M, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                ValidateType(modifier.Of, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
            case TypeNode.Fn function:
                ValidateType(function.Ret, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                foreach (var parameter in function.DelegateParams)
                    ValidateType(parameter, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
                return;
        }
    }

    static ParameterFacts Facts(TypeNode type, ParameterFacts[] typeParameters, ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, Func<string, bool> isValueFqn, IReadOnlySet<string> localEnums,
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
                if (isValueFqn(fqn.Name))
                    return new ParameterFacts(false, true, localEnums.Contains(fqn.Name) || IsEnum(fqn.Name, refs,
                        localTypes), true);
                var kind = LocalOrReferencedKind(fqn.Name, refs, localTypes);
                var isInterface = kind == "interface";
                var isAbstract = localTypes.TryGetValue(fqn.Name, out var local) && local.Abstract;
                var hasDefault = fqn.Name is "kotlin.Any" or "System.Object" ||
                    localTypes.GetValueOrDefault(fqn.Name)?.PublicDefaultConstructor == true ||
                    refs.HasPublicParameterlessConstructor(fqn.Name);
                return new ParameterFacts(true, false, kind == "enum",
                    !isInterface && !isAbstract && hasDefault);
            }
            default:
                return new ParameterFacts(false, false, false, false);
        }
    }

    static ParameterFacts[] ReadParameterFacts(JsonNode node, ReferenceMetadataIndex refs,
        Func<string, bool> isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        if (node is not JsonArray parameters) return Array.Empty<ParameterFacts>();
        var result = new ParameterFacts[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is not JsonObject parameter)
            {
                result[i] = new ParameterFacts(false, false, false, false);
                continue;
            }
            var specials = (parameter["specialConstraints"] as JsonArray)?
                .Select(Str).Where(value => value is not null).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
            var reference = specials.Contains("class");
            var value = specials.Contains("struct");
            var isEnum = false;
            if (parameter["constraints"] is JsonArray constraints)
                foreach (var constraint in constraints)
                    if (TypeJson.Read(constraint) is TypeNode.Fqn bound)
                    {
                        value |= bound.Name == "System.ValueType";
                        isEnum |= bound.Name is "System.Enum" or "kotlin.Enum";
                        var kind = LocalOrReferencedKind(bound.Name, refs, localTypes);
                        reference |= kind == "class" && !isValueFqn(bound.Name);
                    }
            result[i] = new ParameterFacts(reference, value, isEnum, specials.Contains("new") || value);
        }
        return result;
    }

    static string LocalOrReferencedKind(string name, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        if (localTypes.TryGetValue(name, out var local)) return local.Kind;
        return refs.TryReferenceTypeShape(name, out _, out var kind, out _, out _) ? kind : null;
    }

    static bool IsEnum(string name, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes) =>
        LocalOrReferencedKind(name, refs, localTypes) == "enum";

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
    static bool Bool(JsonNode node) => (node as JsonValue)?.TryGetValue<bool>(out var value) == true && value;
}

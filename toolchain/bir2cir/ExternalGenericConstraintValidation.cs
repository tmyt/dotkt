using System.Text.Json.Nodes;
using DotKt.Bir;

// Validate the CLR-only half of referenced generic type and method declarations.
//
// dll2klib can expose an ordinary nominal row (`T : IFoo`) as a KLIB upper bound and Kotlin's frontend then owns
// subtype checking. ECMA-335 also has constraints Kotlin metadata cannot state as nominal bounds: class/struct/new()
// flags, plus the implicit System.ValueType/System.Enum rows emitted with value/enum constraints. Publishing those
// roots as Kotlin bounds makes every legal Kotlin value uninhabitable; dropping the flags lets invalid TypeSpecs reach
// the loader. ReferenceMetadataIndex already owns their exact CLR declarations, so bir2cir checks constructed types
// before physical lowering and checks method arguments after exact member resolution has selected the MethodDef.
static class ExternalGenericConstraintValidation
{
    sealed record ParameterFacts(bool Reference, bool NonNullableValue, bool Enum, bool PublicDefaultConstructor);
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

    public sealed class Prepared
    {
        readonly ReferenceMetadataIndex _refs;
        readonly ValueTypeOracle _isValueFqn;
        readonly IReadOnlySet<string> _localEnums;
        readonly IReadOnlyDictionary<string, LocalTypeFacts> _localTypes;
        // ReferenceMetadataIndex stores declarations as immutable serialized metadata. Parse each owner once for the
        // whole module instead of once for every occurrence of a constructed type.
        readonly Dictionary<string, JsonArray> _declarationCache = new(StringComparer.Ordinal);

        internal Prepared(ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn,
            IReadOnlySet<string> localEnums, IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
        {
            _refs = refs;
            _isValueFqn = isValueFqn;
            _localEnums = localEnums;
            _localTypes = localTypes;
        }

        public void Apply(JsonNode root) =>
            Walk(root, null, Array.Empty<ParameterFacts>(), Array.Empty<ParameterFacts>(), _refs, _isValueFqn,
                _localEnums, _localTypes, _declarationCache);

        public void ApplyResolvedMembers(JsonNode root) =>
            WalkResolvedMembers(root, null, Array.Empty<ParameterFacts>(), Array.Empty<ParameterFacts>(), _refs,
                _isValueFqn, _localEnums, _localTypes, _declarationCache);
    }

    public static Prepared Prepare(IReadOnlyList<JsonNode> roots, ReferenceMetadataIndex refs,
        ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums) =>
        new(refs, isValueFqn, localEnums, CollectLocalTypes(roots));

    static Dictionary<string, LocalTypeFacts> CollectLocalTypes(IEnumerable<JsonNode> roots)
    {
        var result = new Dictionary<string, LocalTypeFacts>(StringComparer.Ordinal);
        foreach (var root in roots) Collect(root);
        return result;

        void Collect(JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (Str(o["name"]) is string name && Str(o["kind"]) is string kind && IsTypeKind(kind))
                    Add(name, kind, o);
                foreach (var value in o.Select(pair => pair.Value))
                    if (value is not null) Collect(value);
            }
            else if (node is JsonArray array)
                foreach (var value in array)
                    if (value is not null) Collect(value);
        }

        void Add(string name, string kind, JsonObject declaration)
        {
            var isValue = kind is "struct" or "enum" or "value";
            var isAbstract = Bool(declaration["abstract"]);
            var hasPublicDefault = isValue || !isAbstract && declaration["ctors"] is JsonArray ctors &&
                ctors.OfType<JsonObject>().Any(ctor => Str(ctor["vis"]) is null or "public" &&
                    (ctor["params"] as JsonArray)?.Count == 0);
            result[name] = new LocalTypeFacts(kind, isAbstract, hasPublicDefault);
        }
    }

    static void Walk(JsonNode node, string incomingKey, ParameterFacts[] typeParameters,
        ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes, IDictionary<string, JsonArray> declarationCache)
    {
        if (node is JsonArray array)
        {
            foreach (var value in array)
                if (value is not null)
                    Walk(value, incomingKey, typeParameters, methodParameters, refs, isValueFqn, localEnums,
                        localTypes, declarationCache);
            return;
        }
        if (node is not JsonObject o) return;

        // A scalar memberRef is already a resolved foreign declaration identity. Its open declaring/parameter/return
        // types belong to that declaration, exactly like the descriptor vectors skipped below.
        if (o.ContainsKey("assembly") && o.ContainsKey("declaringType") && o.ContainsKey("genericArity") &&
            o.ContainsKey("returnType") && MemberRefNode.Kinds.IsKnown(Str(o["kind"])))
            return;

        if (o["t"] is JsonValue)
        {
            if (TypeJson.Read(o) is TypeNode type)
                ValidateType(type, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                    declarationCache);
            return;
        }

        var nextTypeParameters = typeParameters;
        var nextMethodParameters = methodParameters;
        if (Str(o["kind"]) is string kind && IsTypeKind(kind))
            nextTypeParameters = ReadParameterFacts(TypeParameterFrame.CloneDeclarations(o), refs, isValueFqn,
                localEnums, localTypes);
        if (incomingKey == "methods")
            nextMethodParameters = ReadParameterFacts(o["typeParams"], refs, isValueFqn, localEnums, localTypes);
        else if (incomingKey == "ctors")
            nextMethodParameters = Array.Empty<ParameterFacts>();

        var nodeKind = Str(o["k"]);
        foreach (var pair in o.ToList())
        {
            if (pair.Value is null || ForeignDeclarationTypeKeys.Contains(pair.Key) ||
                pair.Key == "argTypes" && nodeKind != "new")
                continue;
            Walk(pair.Value, pair.Key, nextTypeParameters, nextMethodParameters, refs, isValueFqn, localEnums,
                localTypes, declarationCache);
        }
    }

    static void ValidateType(TypeNode type, ParameterFacts[] typeParameters, ParameterFacts[] methodParameters,
        ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes, IDictionary<string, JsonArray> declarationCache)
    {
        switch (type)
        {
            case TypeNode.Fqn { Args: { } args } application:
                foreach (var argument in args)
                    ValidateType(argument, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes,
                        declarationCache);
                if (!declarationCache.TryGetValue(application.Name, out var declarations))
                {
                    declarations = refs.OwnerTypeParamDeclarations(application.Name);
                    declarationCache[application.Name] = declarations;
                }
                if (declarations is null ||
                    declarations.Count != args.Length)
                    return;
                for (var i = 0; i < args.Length; i++)
                {
                    if (declarations[i] is not JsonObject declaration) continue;
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

    static void WalkResolvedMembers(JsonNode node, string incomingKey, ParameterFacts[] typeParameters,
        ParameterFacts[] methodParameters, ReferenceMetadataIndex refs, ValueTypeOracle isValueFqn,
        IReadOnlySet<string> localEnums, IReadOnlyDictionary<string, LocalTypeFacts> localTypes,
        IDictionary<string, JsonArray> declarationCache)
    {
        if (node is JsonArray array)
        {
            foreach (var value in array)
                if (value is not null)
                    WalkResolvedMembers(value, incomingKey, typeParameters, methodParameters, refs, isValueFqn,
                        localEnums, localTypes, declarationCache);
            return;
        }
        if (node is not JsonObject o || o["t"] is JsonValue) return;
        if (o.ContainsKey("assembly") && o.ContainsKey("declaringType") && o.ContainsKey("genericArity") &&
            o.ContainsKey("returnType") && MemberRefNode.Kinds.IsKnown(Str(o["kind"])))
            return;

        var nextTypeParameters = typeParameters;
        var nextMethodParameters = methodParameters;
        if (Str(o["kind"]) is string kind && IsTypeKind(kind))
            nextTypeParameters = ReadParameterFacts(TypeParameterFrame.CloneDeclarations(o), refs, isValueFqn,
                localEnums, localTypes);
        if (incomingKey == "methods")
            nextMethodParameters = ReadParameterFacts(o["typeParams"], refs, isValueFqn, localEnums, localTypes);
        else if (incomingKey == "ctors")
            nextMethodParameters = Array.Empty<ParameterFacts>();

        if (o[ClrMemberResolution.ResolvedMethodTypeParamsKey] is JsonArray declarations)
        {
            if (o["typeArgs"] is not JsonArray arguments || arguments.Count != declarations.Count)
                throw new InvalidDataException(
                    "bir2cir: resolved generic member constraint carrier does not match its type arguments");
            var owner = ResolvedMemberName(o);
            for (var i = 0; i < arguments.Count; i++)
            {
                if (TypeJson.Read(arguments[i]) is not TypeNode actual || declarations[i] is not JsonObject declaration)
                    throw new InvalidDataException(
                        $"bir2cir: resolved generic member '{owner}' has a malformed type argument/constraint at {i}");
                ValidateType(actual, nextTypeParameters, nextMethodParameters, refs, isValueFqn, localEnums,
                    localTypes, declarationCache);
                ValidateArgument(owner, i, actual, declaration, nextTypeParameters, nextMethodParameters, refs,
                    isValueFqn, localEnums, localTypes);
            }
            o.Remove(ClrMemberResolution.ResolvedMethodTypeParamsKey);
        }

        var nodeKind = Str(o["k"]);
        foreach (var pair in o.ToList())
        {
            if (pair.Value is null || ForeignDeclarationTypeKeys.Contains(pair.Key) ||
                pair.Key == "argTypes" && nodeKind != "new")
                continue;
            WalkResolvedMembers(pair.Value, pair.Key, nextTypeParameters, nextMethodParameters, refs, isValueFqn,
                localEnums, localTypes, declarationCache);
        }
    }

    static void ValidateArgument(string owner, int index, TypeNode argument, JsonObject declaration,
        ParameterFacts[] typeParameters, ParameterFacts[] methodParameters, ReferenceMetadataIndex refs,
        ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
        IReadOnlyDictionary<string, LocalTypeFacts> localTypes)
    {
        var actual = Facts(argument, typeParameters, methodParameters, refs, isValueFqn, localEnums, localTypes);
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
            Fail(owner, index, argument, "a non-nullable CLR value type");
        if (requiresEnum && !actual.Enum)
            Fail(owner, index, argument, "a CLR enum type");
        if (specials.Contains("class") && !actual.Reference)
            Fail(owner, index, argument, "a CLR reference type");
        if (specials.Contains("new") && !actual.PublicDefaultConstructor)
            Fail(owner, index, argument, "a public parameterless constructor");
    }

    static string ResolvedMemberName(JsonObject node)
    {
        var memberRef = node["memberRef"] as JsonObject;
        var owner = TypeJson.Read(memberRef?["declaringType"]);
        var name = Str(memberRef?["name"]) ?? Str(node["method"]) ?? "<unknown>";
        return owner is null ? name : Display(owner) + "." + name;
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
                    kind = LocalOrReferencedKind(name, refs, localTypes);
                if (isValueFqn(fqn))
                    return new ParameterFacts(false, true, localEnums.Contains(name) || IsEnum(name, refs,
                        localTypes), true);
                var isInterface = kind == "interface";
                var isAbstract = localTypes.TryGetValue(name, out var local) && local.Abstract;
                var hasDefault = name == "System.Object" ||
                    localTypes.GetValueOrDefault(name)?.PublicDefaultConstructor == true ||
                    refs.HasPublicParameterlessConstructor(name);
                return new ParameterFacts(true, false, kind == "enum",
                    !isInterface && !isAbstract && hasDefault);
            }
            default:
                return new ParameterFacts(false, false, false, false);
        }
    }

    static ParameterFacts[] ReadParameterFacts(JsonNode node, ReferenceMetadataIndex refs,
        ValueTypeOracle isValueFqn, IReadOnlySet<string> localEnums,
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
                        reference |= kind == "class" && !isValueFqn(bound);
                    }
            result[i] = new ParameterFacts(reference, value, isEnum, specials.Contains("new") || value);
        }
        return result;
    }

    static bool IsTypeKind(string kind) => kind is "class" or "interface" or "enum" or "struct" or "value";

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

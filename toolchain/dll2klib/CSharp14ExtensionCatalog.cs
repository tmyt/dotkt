using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// Validated view of the released C# 14 static extension-member graph. Names are deliberately not discovery keys:
// ExtensionMarkerAttribute supplies the marker edge, ExtensionAttribute supplies the two containers, and only then
// are the special-name/shape requirements checked. This keeps ordinary user types named <G>$... or <M>$... inert.
internal sealed class CSharp14ExtensionCatalog
{
    internal sealed record Function(
        MethodDefinitionHandle Declaration,
        MethodDefinitionHandle Implementation,
        MethodDefinitionHandle KotlinImplementation,
        MethodDefinitionHandle ReceiverMarker,
        int BlockArity);

    internal sealed record Property(
        PropertyDefinitionHandle Declaration,
        MethodDefinitionHandle GetterImplementation,
        MethodDefinitionHandle SetterImplementation,
        MethodDefinitionHandle KotlinGetterImplementation,
        MethodDefinitionHandle KotlinSetterImplementation,
        MethodDefinitionHandle ReceiverMarker,
        int BlockArity);

    internal sealed record Container(
        IReadOnlyList<Function> Functions,
        IReadOnlyList<Property> Properties);

    private const string ExtensionAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
    private const string ExtensionMarkerAttribute = "System.Runtime.CompilerServices.ExtensionMarkerAttribute";
    private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
    private const string KotlinExtensionCoreAttribute =
        "DotKt.Runtime.CompilerServices.KotlinExtensionCoreAttribute";

    private readonly Dictionary<TypeDefinitionHandle, Container> _containers;
    private readonly HashSet<TypeDefinitionHandle> _infrastructure;

    private CSharp14ExtensionCatalog(
        Dictionary<TypeDefinitionHandle, Container> containers,
        HashSet<TypeDefinitionHandle> infrastructure)
    {
        _containers = containers;
        _infrastructure = infrastructure;
    }

    internal bool TryGetContainer(TypeDefinitionHandle handle, out Container container) =>
        _containers.TryGetValue(handle, out container!);

    internal bool IsInfrastructure(TypeDefinitionHandle handle) => _infrastructure.Contains(handle);

    internal static CSharp14ExtensionCatalog Discover(
        PEReader pe,
        MetadataReader md,
        MetadataAttributes attrs)
    {
        var propertyOwners = new Dictionary<PropertyDefinitionHandle, TypeDefinitionHandle>();
        foreach (var typeHandle in md.TypeDefinitions)
            foreach (var propertyHandle in md.GetTypeDefinition(typeHandle).GetProperties())
                propertyOwners.Add(propertyHandle, typeHandle);

        var markers = new Dictionary<EntityHandle, string>();
        foreach (var (parent, value) in attrs.StringAttributes(ExtensionMarkerAttribute))
        {
            if (parent.Kind is not (HandleKind.MethodDefinition or HandleKind.PropertyDefinition))
                throw new InvalidDataException(
                    $"[{ExtensionMarkerAttribute}] has unsupported target {parent.Kind}");
            if (!markers.TryAdd(parent, value))
                throw new InvalidDataException(
                    $"duplicate [{ExtensionMarkerAttribute}] on metadata token 0x{MetadataTokens.GetToken(parent):X8}");
        }

        var groupHandles = markers.Keys.Select(parent => parent.Kind switch
        {
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)parent).GetDeclaringType(),
            HandleKind.PropertyDefinition => propertyOwners[(PropertyDefinitionHandle)parent],
            _ => default,
        }).Distinct().ToArray();

        var containers = new Dictionary<TypeDefinitionHandle, Container>();
        var infrastructure = new HashSet<TypeDefinitionHandle>();
        foreach (var groupHandle in groupHandles)
        {
            var group = md.GetTypeDefinition(groupHandle);
            var containerHandle = group.GetDeclaringType();
            if (containerHandle.IsNil ||
                (group.Attributes & TypeAttributes.SpecialName) == 0 ||
                !attrs.Has(groupHandle, ExtensionAttribute, requireTrust: false))
                throw Malformed(md, groupHandle, "grouping type must be a nested specialname [Extension] type");
            var container = md.GetTypeDefinition(containerHandle);
            if (!container.GetDeclaringType().IsNil ||
                (container.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) !=
                    (TypeAttributes.Abstract | TypeAttributes.Sealed) ||
                !attrs.Has(containerHandle, ExtensionAttribute, requireTrust: false))
                throw Malformed(md, groupHandle, "grouping type must belong to a top-level static [Extension] container");

            var groupParents = markers.Where(pair => pair.Key.Kind switch
            {
                HandleKind.MethodDefinition =>
                    md.GetMethodDefinition((MethodDefinitionHandle)pair.Key).GetDeclaringType() == groupHandle,
                HandleKind.PropertyDefinition => propertyOwners[(PropertyDefinitionHandle)pair.Key] == groupHandle,
                _ => false,
            }).ToArray();
            var markerNames = groupParents.Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (markerNames.Length != 1 || string.IsNullOrEmpty(markerNames[0]))
                throw Malformed(md, groupHandle, "group declarations must name exactly one receiver marker");
            var markerName = markerNames[0];
            var markerMatches = group.GetNestedTypes()
                .Where(handle => md.GetString(md.GetTypeDefinition(handle).Name) == markerName)
                .ToArray();
            if (markerMatches.Length != 1)
                throw Malformed(md, groupHandle, $"receiver marker '{markerName}' does not resolve exactly once");
            var markerHandle = markerMatches[0];
            var marker = md.GetTypeDefinition(markerHandle);
            if ((marker.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.SpecialName)) !=
                    (TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.SpecialName))
                throw Malformed(md, markerHandle, "receiver marker has invalid TypeDef flags");

            var markerMethods = marker.GetMethods()
                .Where(handle => md.GetString(md.GetMethodDefinition(handle).Name) == "<Extension>$")
                .ToArray();
            if (markerMethods.Length != 1)
                throw Malformed(md, markerHandle, "receiver marker must declare exactly one <Extension>$ method");
            var markerMethodHandle = markerMethods[0];
            var markerMethod = md.GetMethodDefinition(markerMethodHandle);
            var markerSignature = markerMethod.DecodeSignature(
                RawSignatureTypeProvider.Instance,
                new GenericContext(markerHandle, markerMethodHandle,
                    ImmutableDictionary<GenericParameterHandle, int>.Empty));
            if ((markerMethod.Attributes & (MethodAttributes.Static | MethodAttributes.SpecialName)) !=
                    (MethodAttributes.Static | MethodAttributes.SpecialName) ||
                !attrs.Has(markerMethodHandle, CompilerGeneratedAttribute, requireTrust: false) ||
                markerSignature.GenericParameterCount != 0 ||
                markerSignature.ParameterTypes.Length != 1 ||
                markerSignature.ReturnType != $"primitive:{(int)PrimitiveTypeCode.Void}" ||
                !MarkerBodyIsSignatureOnly(pe, markerMethod))
                throw Malformed(md, markerHandle, "receiver marker method has an invalid signature or body");

            var blockArity = group.GetGenericParameters().Count;
            if (marker.GetGenericParameters().Count != blockArity ||
                !GenericParametersMatch(md, groupHandle, default, markerHandle, default, blockArity, 0, 0))
                throw Malformed(md, markerHandle, "receiver marker generic constraints differ from its grouping type");

            var propertyAccessorMethods = new HashSet<MethodDefinitionHandle>();
            var properties = new List<Property>();
            foreach (var propertyHandle in group.GetProperties())
            {
                var property = md.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();
                var handles = new[] { accessors.Getter, accessors.Setter }.Where(h => !h.IsNil).ToArray();
                if (!markers.TryGetValue(propertyHandle, out var propertyMarker))
                {
                    if (handles.Any(handle => markers.ContainsKey(handle)))
                        throw Malformed(md, groupHandle, "marked property accessor has no marked Property row");
                    continue;
                }
                if (propertyMarker != markerName || handles.Length == 0 ||
                    handles.Any(handle => !markers.TryGetValue(handle, out var value) || value != markerName))
                    throw Malformed(md, groupHandle, "property and accessor marker edges are inconsistent");
                foreach (var handle in handles) propertyAccessorMethods.Add(handle);

                if (accessors.Getter.IsNil)
                    throw Malformed(md, groupHandle, "static extension property has no getter");
                // Instance extension members share the same C# 14 grouping graph, but their executable method keeps
                // the ordinary [Extension] + leading-receiver ABI already handled by ReadCSharpExtensions.
                if ((md.GetMethodDefinition(accessors.Getter).Attributes & MethodAttributes.Static) == 0)
                    continue;
                var getterImplementation = PairImplementation(
                    pe, md, containerHandle, groupHandle, accessors.Getter, blockArity);
                var setterImplementation = accessors.Setter.IsNil
                    ? default
                    : PairImplementation(pe, md, containerHandle, groupHandle, accessors.Setter, blockArity);
                properties.Add(new Property(
                    propertyHandle,
                    getterImplementation,
                    setterImplementation,
                    KotlinImplementation(md, attrs, containerHandle, groupHandle, accessors.Getter,
                        getterImplementation),
                    setterImplementation.IsNil ? default : KotlinImplementation(
                        md, attrs, containerHandle, groupHandle, accessors.Setter, setterImplementation),
                    markerMethodHandle,
                    blockArity));
            }

            var functions = new List<Function>();
            foreach (var declarationHandle in group.GetMethods())
            {
                var declarationName = md.GetString(md.GetMethodDefinition(declarationHandle).Name);
                // PersistedAssemblyBuilder inserts a default instance constructor on DotKt's otherwise metadata-only
                // sealed grouping TypeDef. Constructors are not extension declarations and Roslyn ignores this one;
                // keep validating every non-constructor callable exactly like a native C# 14 graph.
                if (declarationName is ".ctor" or ".cctor") continue;
                if (!markers.TryGetValue(declarationHandle, out var declarationMarker))
                    throw Malformed(md, groupHandle, "grouping type contains an unmarked callable declaration");
                if (declarationMarker != markerName)
                    throw Malformed(md, groupHandle, "callable declaration names the wrong receiver marker");
                if (propertyAccessorMethods.Contains(declarationHandle)) continue;
                if ((md.GetMethodDefinition(declarationHandle).Attributes & MethodAttributes.Static) == 0)
                    continue;
                var implementation = PairImplementation(
                    pe, md, containerHandle, groupHandle, declarationHandle, blockArity);
                functions.Add(new Function(
                    declarationHandle,
                    implementation,
                    KotlinImplementation(md, attrs, containerHandle, groupHandle, declarationHandle, implementation),
                    markerMethodHandle,
                    blockArity));
            }

            if (!containers.TryGetValue(containerHandle, out var prior))
                prior = new Container([], []);
            containers[containerHandle] = new Container(
                prior.Functions.Concat(functions).ToArray(),
                prior.Properties.Concat(properties).ToArray());
            infrastructure.Add(groupHandle);
            infrastructure.Add(markerHandle);
        }
        return new CSharp14ExtensionCatalog(containers, infrastructure);
    }

    private static MethodDefinitionHandle KotlinImplementation(
        MetadataReader md,
        MetadataAttributes attrs,
        TypeDefinitionHandle containerHandle,
        TypeDefinitionHandle groupHandle,
        MethodDefinitionHandle declarationHandle,
        MethodDefinitionHandle wrapperHandle)
    {
        using var document = attrs.CarrierDocument(wrapperHandle, KotlinExtensionCoreAttribute);
        if (document is null) return wrapperHandle;
        var root = document.RootElement;
        var entries = root.ValueKind == System.Text.Json.JsonValueKind.Object
            ? root.EnumerateObject().ToArray() : [];
        if (entries.Length != 1 ||
            !root.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != System.Text.Json.JsonValueKind.String ||
            string.IsNullOrEmpty(nameElement.GetString()))
            throw Malformed(md, groupHandle, "malformed trusted Kotlin extension-core edge");
        var name = nameElement.GetString()!;
        var matches = md.GetTypeDefinition(containerHandle).GetMethods()
            .Where(handle => md.GetString(md.GetMethodDefinition(handle).Name) == name)
            .Where(handle => CoreSignatureMatches(md, groupHandle, declarationHandle, containerHandle, handle))
            .ToArray();
        if (matches.Length != 1)
            throw Malformed(md, groupHandle,
                $"Kotlin extension core '{name}' resolves to {matches.Length} methods");
        return matches[0];
    }

    // A Kotlin core has only the declaration's own method parameters; the receiver block belongs exclusively to the
    // C# wrapper. Kotlin source cannot mention those receiver arguments in the callable signature, so any group-scope
    // type variable here is malformed rather than something to erase or infer.
    private static bool CoreSignatureMatches(
        MetadataReader md,
        TypeDefinitionHandle groupHandle,
        MethodDefinitionHandle declarationHandle,
        TypeDefinitionHandle containerHandle,
        MethodDefinitionHandle coreHandle)
    {
        var declaration = md.GetMethodDefinition(declarationHandle);
        var core = md.GetMethodDefinition(coreHandle);
        if ((core.Attributes & MethodAttributes.Static) == 0 ||
            (core.Attributes & MethodAttributes.SpecialName) != 0 ||
            (core.Attributes & MethodAttributes.MemberAccessMask) !=
                (declaration.Attributes & MethodAttributes.MemberAccessMask) ||
            core.GetGenericParameters().Count != declaration.GetGenericParameters().Count)
            return false;
        var declarationSignature = declaration.DecodeSignature(
            RawSignatureTypeProvider.Instance,
            new GenericContext(groupHandle, declarationHandle,
                ImmutableDictionary<GenericParameterHandle, int>.Empty));
        var coreSignature = core.DecodeSignature(
            RawSignatureTypeProvider.Instance,
            new GenericContext(containerHandle, coreHandle,
                ImmutableDictionary<GenericParameterHandle, int>.Empty));
        if (declarationSignature.ReturnType != coreSignature.ReturnType ||
            !declarationSignature.ParameterTypes.SequenceEqual(coreSignature.ParameterTypes, StringComparer.Ordinal))
            return false;
        return GenericParametersMatch(
            md, groupHandle, declarationHandle, containerHandle, coreHandle,
            declarationSignature.GenericParameterCount, 0, 0);
    }

    private static MethodDefinitionHandle PairImplementation(
        PEReader pe,
        MetadataReader md,
        TypeDefinitionHandle containerHandle,
        TypeDefinitionHandle groupHandle,
        MethodDefinitionHandle declarationHandle,
        int blockArity)
    {
        var declaration = md.GetMethodDefinition(declarationHandle);
        if (!DeclarationBodyIsSignatureOnly(pe, declaration))
            throw Malformed(md, groupHandle, $"declaration '{md.GetString(declaration.Name)}' is callable");
        var name = md.GetString(declaration.Name);
        var matches = md.GetTypeDefinition(containerHandle).GetMethods()
            .Where(handle => md.GetString(md.GetMethodDefinition(handle).Name) == name)
            .Where(handle => MethodSignaturesMatch(md, groupHandle, declarationHandle, containerHandle, handle, blockArity))
            .ToArray();
        if (matches.Length != 1)
            throw Malformed(md, groupHandle,
                $"declaration '{name}' resolves to {matches.Length} implementation methods");
        return matches[0];
    }

    private static bool MethodSignaturesMatch(
        MetadataReader md,
        TypeDefinitionHandle groupHandle,
        MethodDefinitionHandle declarationHandle,
        TypeDefinitionHandle containerHandle,
        MethodDefinitionHandle implementationHandle,
        int blockArity)
    {
        var declaration = md.GetMethodDefinition(declarationHandle);
        var implementation = md.GetMethodDefinition(implementationHandle);
        if ((declaration.Attributes & MethodAttributes.MemberAccessMask) !=
                (implementation.Attributes & MethodAttributes.MemberAccessMask) ||
            (implementation.Attributes & MethodAttributes.Static) == 0 ||
            (implementation.Attributes & MethodAttributes.SpecialName) != 0)
            return false;
        var declarationSignature = declaration.DecodeSignature(
            RawSignatureTypeProvider.Instance,
            new GenericContext(groupHandle, declarationHandle,
                ImmutableDictionary<GenericParameterHandle, int>.Empty));
        var implementationSignature = implementation.DecodeSignature(
            RawSignatureTypeProvider.Instance,
            new GenericContext(containerHandle, implementationHandle,
                ImmutableDictionary<GenericParameterHandle, int>.Empty));
        if (implementationSignature.GenericParameterCount !=
                blockArity + declarationSignature.GenericParameterCount ||
            (implementationSignature.Header.Attributes & ~SignatureAttributes.Generic) !=
                (declarationSignature.Header.Attributes & ~SignatureAttributes.Generic) ||
            implementationSignature.RequiredParameterCount != declarationSignature.RequiredParameterCount ||
            declarationSignature.ReturnType != NormalizeImplementationType(
                implementationSignature.ReturnType, blockArity) ||
            !declarationSignature.ParameterTypes.SequenceEqual(
                implementationSignature.ParameterTypes.Select(type => NormalizeImplementationType(type, blockArity)),
                StringComparer.Ordinal))
            return false;
        return GenericParametersMatch(
                md, groupHandle, default, containerHandle, implementationHandle,
                blockArity, 0, blockArity) &&
            GenericParametersMatch(
                md, groupHandle, declarationHandle, containerHandle, implementationHandle,
                declarationSignature.GenericParameterCount, blockArity, blockArity);
    }

    // Compare a range of generic parameters after mapping Roslyn's implementation-method parameter space
    // [block parameters, member parameters] back to the grouping graph's [type parameters, method parameters].
    private static bool GenericParametersMatch(
        MetadataReader md,
        TypeDefinitionHandle leftType,
        MethodDefinitionHandle leftMethod,
        TypeDefinitionHandle rightType,
        MethodDefinitionHandle rightMethod,
        int count,
        int rightOffset,
        int normalizationBlockArity)
    {
        var leftOwner = leftMethod.IsNil
            ? md.GetTypeDefinition(leftType).GetGenericParameters().ToArray()
            : md.GetMethodDefinition(leftMethod).GetGenericParameters().ToArray();
        var rightOwner = rightMethod.IsNil
            ? md.GetTypeDefinition(rightType).GetGenericParameters().ToArray()
            : md.GetMethodDefinition(rightMethod).GetGenericParameters().ToArray();
        if (leftOwner.Length != count || rightOwner.Length < rightOffset + count) return false;
        for (var index = 0; index < count; index++)
        {
            var left = md.GetGenericParameter(leftOwner[index]);
            var right = md.GetGenericParameter(rightOwner[rightOffset + index]);
            if (left.Attributes != right.Attributes) return false;
            var leftContext = new GenericContext(leftType, leftMethod,
                ImmutableDictionary<GenericParameterHandle, int>.Empty);
            var rightContext = new GenericContext(rightType, rightMethod,
                ImmutableDictionary<GenericParameterHandle, int>.Empty);
            var leftConstraints = left.GetConstraints()
                .Select(handle => RawEntity(md, md.GetGenericParameterConstraint(handle).Type, leftContext))
                .OrderBy(value => value, StringComparer.Ordinal);
            var rightConstraints = right.GetConstraints()
                .Select(handle => NormalizeImplementationType(
                    RawEntity(md, md.GetGenericParameterConstraint(handle).Type, rightContext), normalizationBlockArity))
                .OrderBy(value => value, StringComparer.Ordinal);
            if (!leftConstraints.SequenceEqual(rightConstraints, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static string RawEntity(MetadataReader md, EntityHandle handle, GenericContext context) => handle.Kind switch
    {
        HandleKind.TypeDefinition => RawSignatureTypeProvider.Instance.GetTypeFromDefinition(
            md, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => RawSignatureTypeProvider.Instance.GetTypeFromReference(
            md, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => RawSignatureTypeProvider.Instance.GetTypeFromSpecification(
            md, context, (TypeSpecificationHandle)handle, 0),
        _ => throw new InvalidDataException($"generic constraint has unsupported handle kind {handle.Kind}"),
    };

    private static string NormalizeImplementationType(string type, int blockArity) =>
        Regex.Replace(type, @"!!([0-9]+)", match =>
        {
            var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return index < blockArity ? $"!{index}" : $"!!{index - blockArity}";
        });

    private static bool MarkerBodyIsSignatureOnly(PEReader pe, MethodDefinition method)
    {
        // Reference assemblies intentionally strip both marker and declaration bodies. Implementation assemblies
        // carry a trivial marker ret and a non-returning declaration stub.
        if (method.RelativeVirtualAddress == 0) return true;
        var body = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        return body.SequenceEqual(new byte[] { 0x2A }) || IsKnownThrowStub(body);
    }

    private static bool DeclarationBodyIsSignatureOnly(PEReader pe, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0) return true;
        var body = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        // Roslyn 14 on net10 emits ldnull/throw; earlier released compiler builds emitted
        // newobj NotSupportedException/throw. Accept those exact generated forms, not arbitrary code that happens
        // to end in throw after observable work.
        return IsKnownThrowStub(body);
    }

    private static bool IsKnownThrowStub(byte[] body) =>
        body.SequenceEqual(new byte[] { 0x14, 0x7A }) || // ldnull; throw
        body.Length == 6 && body[0] == 0x73 && body[^1] == 0x7A; // newobj <ctor>; throw

    private static InvalidDataException Malformed(
        MetadataReader md,
        TypeDefinitionHandle type,
        string detail)
    {
        var definition = md.GetTypeDefinition(type);
        var name = md.GetString(definition.Name);
        return new InvalidDataException($"malformed C# 14 static extension graph at '{name}': {detail}");
    }
}

#nullable enable
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;

namespace DotKt.Toolchain;

// A lossless-enough identity decoder for comparing two parameter TYPE signatures inside one CLR owner. The ordinary
// SignatureDecoder deliberately projects distinct CLR types into shared Kotlin vocabulary (two delegate classes with
// the same Invoke shape, for example), so its KType output cannot prove that an NRT bridge still targets the same
// physical member. This decoder retains CLR names, class/value kind, generic positions, arrays, pointers, byrefs,
// function pointers, and custom modifiers. A false negative merely omits a bridge; a false positive could retarget a
// call, so the comparison is intentionally strict.
internal sealed class RawSignatureTypeProvider<TContext> : ISignatureTypeProvider<string, TContext>
{
    public static RawSignatureTypeProvider<TContext> Instance { get; } = new();

    // Used only within a known declaring type. Unlike a MethodDef token or a signature blob,
    // this key survives ref/implementation table renumbering. No Kotlin nullability is involved.
    public static string ConstructorKey(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var signature = reader.GetMethodDefinition(handle).DecodeSignature(Instance, default(TContext)!);
        return FormattableString.Invariant($"{signature.Header.RawValue}:{signature.RequiredParameterCount}:")
            + string.Concat(signature.ParameterTypes.Select(slot => FormattableString.Invariant($"{slot.Length}:{slot}")));
    }
    private readonly Func<MetadataReader, EntityHandle, string>? _typeIdentity;
    public RawSignatureTypeProvider(Func<MetadataReader, EntityHandle, string>? typeIdentity = null) =>
        _typeIdentity = typeIdentity;

    internal static string ScopedReferenceIdentity(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var scope = reference.ResolutionScope;
        string identity;
        if (scope.Kind == HandleKind.TypeReference)
            identity = ScopedReferenceIdentity(reader, (TypeReferenceHandle)scope);
        else if (scope.Kind == HandleKind.AssemblyReference)
        {
            var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
            var name = new AssemblyName(reader.GetString(assembly.Name))
            {
                Version = assembly.Version,
                CultureName = reader.GetString(assembly.Culture),
                ContentType = (assembly.Flags & AssemblyFlags.WindowsRuntime) != 0
                    ? AssemblyContentType.WindowsRuntime : AssemblyContentType.Default,
                Flags = (assembly.Flags & AssemblyFlags.Retargetable) != 0
                    ? AssemblyNameFlags.Retargetable : AssemblyNameFlags.None,
            };
            var key = reader.GetBlobBytes(assembly.PublicKeyOrToken);
            if ((assembly.Flags & AssemblyFlags.PublicKey) != 0) name.SetPublicKey(key);
            else name.SetPublicKeyToken(key);
            identity = "assembly:" + name.FullName;
        }
        else
            identity = "module:" + reader.GetGuid(reader.GetModuleDefinition().Mvid) + ":" +
                (scope.Kind == HandleKind.ModuleReference
                    ? reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)scope).Name)
                    : scope.Kind.ToString());
        return JsonSerializer.Serialize(new[] { identity, reader.GetString(reference.Namespace), reader.GetString(reference.Name) });
    }

    public string GetArrayType(string elementType, ArrayShape shape) =>
        $"array[{shape.Rank};{string.Join(",", shape.Sizes)};{string.Join(",", shape.LowerBounds)}]<{elementType}>";
    public string GetByReferenceType(string elementType) => $"byref<{elementType}>";
    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        $"fnptr[{signature.Header.RawValue};{signature.GenericParameterCount};{signature.RequiredParameterCount}]" +
        $"({string.Join(",", signature.ParameterTypes)})->{signature.ReturnType}";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetGenericMethodParameter(TContext genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(TContext genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        $"{(isRequired ? "modreq" : "modopt")}<{modifier}>({unmodifiedType})";
    public string GetPinnedType(string elementType) => $"pinned<{elementType}>";
    public string GetPointerType(string elementType) => $"ptr<{elementType}>";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => $"primitive:{(int)typeCode}";
    public string GetSZArrayType(string elementType) => $"szarray<{elementType}>";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        _typeIdentity is null ? $"{rawTypeKind}:def:{DefinitionName(reader, handle)}"
            : $"{rawTypeKind}:type:{_typeIdentity(reader, handle)}";
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        _typeIdentity is null ? $"{rawTypeKind}:ref:{ReferenceName(reader, handle)}"
            : $"{rawTypeKind}:type:{_typeIdentity(reader, handle)}";
    public string GetTypeFromSpecification(
        MetadataReader reader,
        TContext genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        _typeIdentity is null
            ? $"{rawTypeKind}:spec:{reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext)}"
            : reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    private static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var simple = reader.GetString(definition.Name);
        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(reader, parent) + "+" + simple;
        var ns = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
    }

    private static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var simple = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return ReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + simple;
        var ns = reader.GetString(reference.Namespace);
        var name = string.IsNullOrEmpty(ns) ? simple : ns + "." + simple;
        var scope = reference.ResolutionScope.Kind switch
        {
            HandleKind.AssemblyReference =>
                "asm:" + reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name),
            HandleKind.ModuleReference =>
                "module:" + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)reference.ResolutionScope).Name),
            HandleKind.ModuleDefinition => "module:self",
            _ => reference.ResolutionScope.Kind.ToString(),
        };
        return scope + ":" + name;
    }
}

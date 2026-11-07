// Read declaration facts from ECMA metadata. Reflection's unmodified Type view
// does not retain nested custom modifiers or the exact function-pointer header.
#nullable enable
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotKt.Bir;

public sealed class NativeSignatureReader
{
    public sealed record Signature(TypeNode Return, TypeNode[] Parameters);
    readonly Dictionary<(string Path, Guid Mvid, int Token), Signature> _methods = new();
    readonly Dictionary<(string Path, Guid Mvid, int Token), TypeNode> _fields = new();

    public static bool ContainsPointer(Type type) => type.IsFunctionPointer
        || type.HasElementType && ContainsPointer(type.GetElementType()!)
        || type.IsConstructedGenericType && type.GetGenericArguments().Any(ContainsPointer);

    public Signature? Method(MethodBase method)
    {
        if (!(method is MethodInfo info && ContainsPointer(info.ReturnType))
            && !method.GetParameters().Any(parameter => ContainsPointer(parameter.ParameterType))) return null;
        var key = (method.Module.FullyQualifiedName, method.Module.ModuleVersionId, method.MetadataToken);
        if (_methods.TryGetValue(key, out var cached)) return cached;
        using var stream = File.OpenRead(key.FullyQualifiedName);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var signature = reader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(key.MetadataToken))
            .DecodeSignature(new Provider(), reader);
        var result = new Signature(signature.ReturnType, signature.ParameterTypes.ToArray());
        _methods.Add(key, result);
        return result;
    }

    public TypeNode? Field(FieldInfo field)
    {
        if (!ContainsPointer(field.FieldType)) return null;
        var key = (field.Module.FullyQualifiedName, field.Module.ModuleVersionId, field.MetadataToken);
        if (_fields.TryGetValue(key, out var cached)) return cached;
        using var stream = File.OpenRead(key.FullyQualifiedName);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var type = reader.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.Handle(key.MetadataToken))
            .DecodeSignature(new Provider(), reader);
        _fields.Add(key, type);
        return type;
    }

    sealed class Provider : ISignatureTypeProvider<TypeNode, MetadataReader>
    {
        public TypeNode GetArrayType(TypeNode element, ArrayShape shape)
        {
            if (shape.Sizes.Length != 0 || shape.LowerBounds.Any(bound => bound != 0))
                throw new InvalidDataException("Bounded native array signatures are not supported by the target persisted encoder");
            return TypeNode.Array.General(element, shape.Rank);
        }
        public TypeNode GetSZArrayType(TypeNode element) => new TypeNode.Array(element);
        public TypeNode GetByReferenceType(TypeNode element) => new TypeNode.ByRef(element);
        public TypeNode GetPointerType(TypeNode element) => new TypeNode.Ptr(element);
        public TypeNode GetModifiedType(TypeNode modifier, TypeNode type, bool required) => new TypeNode.Mod(required, modifier, type);
        public TypeNode GetPinnedType(TypeNode element) => element;
        public TypeNode GetFunctionPointerType(MethodSignature<TypeNode> signature) => new TypeNode.FnPtr(
            signature.Header.RawValue, signature.GenericParameterCount, signature.RequiredParameterCount,
            signature.ReturnType, signature.ParameterTypes.ToArray());
        public TypeNode GetGenericMethodParameter(MetadataReader reader, int index) => new TypeNode.Tv("method", index);
        public TypeNode GetGenericTypeParameter(MetadataReader reader, int index) => new TypeNode.Tv("type", index);
        public TypeNode GetGenericInstantiation(TypeNode generic, ImmutableArray<TypeNode> arguments) => generic is TypeNode.Fqn named
            ? new TypeNode.Fqn(named.Name, arguments.ToArray())
            : throw new InvalidDataException("Native generic signature has no named definition");
        public TypeNode GetPrimitiveType(PrimitiveTypeCode code) => new TypeNode.Fqn(code switch {
            PrimitiveTypeCode.Void => "System.Void", PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char => "System.Char", PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Byte => "System.Byte", PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16", PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32", PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double", PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object", PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr", PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => throw new InvalidDataException($"Unknown native signature primitive {code}"),
        });
        public TypeNode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind)
        {
            var type = reader.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            return new TypeNode.Fqn(parent.IsNil ? Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name))
                : ((TypeNode.Fqn)GetTypeFromDefinition(reader, parent, kind)).Name + "+" + reader.GetString(type.Name));
        }
        public TypeNode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind)
        {
            var type = reader.GetTypeReference(handle);
            return new TypeNode.Fqn(type.ResolutionScope.Kind == HandleKind.TypeReference
                ? ((TypeNode.Fqn)GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, kind)).Name + "+" + reader.GetString(type.Name)
                : Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name)));
        }
        public TypeNode GetTypeFromSpecification(MetadataReader reader, MetadataReader context, TypeSpecificationHandle handle, byte kind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, reader);
        static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }
}

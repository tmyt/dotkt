using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
    throw new ArgumentException("usage: PointerInspector <consumer.dll>");

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var provider = new TypeTextProvider();
var fields = new Dictionary<string, string>(StringComparer.Ordinal);
var methods = new Dictionary<string, (string Return, string[] Parameters)>(StringComparer.Ordinal);

foreach (var handle in metadata.MemberReferences)
{
    var member = metadata.GetMemberReference(handle);
    if (member.Parent.Kind != HandleKind.TypeReference) continue;
    var owner = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
    if (metadata.GetString(owner.Namespace) != "Probe" || metadata.GetString(owner.Name) != "PointerProbe")
        continue;

    var name = metadata.GetString(member.Name);
    if (member.GetKind() == MemberReferenceKind.Field)
        fields[name] = member.DecodeFieldSignature(provider, genericContext: null);
    else
    {
        var signature = member.DecodeMethodSignature(provider, genericContext: null);
        methods[name] = (signature.ReturnType, signature.ParameterTypes.ToArray());
    }
}

Require(fields.GetValueOrDefault("Value") == "System.Int32*", "Value MemberRef is not int32*");
RequireMethod("Null", "System.Int32*");
RequireMethod("Echo", "System.Int32*", "System.Int32*");
RequireMethod("IsNull", "System.Boolean", "System.Int32*");
RequireMethod("NullVoid", "System.Void*");
RequireMethod("IsNullVoid", "System.Boolean", "System.Void*");
RequireMethod("NullNested", "System.Int32**");
RequireMethod("IsNullNested", "System.Boolean", "System.Int32**");
RequireMethod("NullNullable", "System.Nullable`1<System.Int32>*");
RequireMethod("EchoNullable", "System.Nullable`1<System.Int32>*", "System.Nullable`1<System.Int32>*");
RequireMethod("IsNullNullable", "System.Boolean", "System.Nullable`1<System.Int32>*");
RequireMethod("NullStruct", "Probe.PointerValue*");
RequireMethod("IsNullStruct", "System.Boolean", "Probe.PointerValue*");
Console.WriteLine("emitted unmanaged-pointer MemberRef signatures: OK");

void RequireMethod(string name, string returnType, params string[] parameterTypes)
{
    Require(methods.TryGetValue(name, out var actual), $"missing PointerProbe.{name} MemberRef");
    Require(actual.Return == returnType,
        $"PointerProbe.{name} returns {actual.Return}, expected {returnType}");
    Require(actual.Parameters.SequenceEqual(parameterTypes),
        $"PointerProbe.{name} parameters are ({string.Join(", ", actual.Parameters)}), " +
        $"expected ({string.Join(", ", parameterTypes)})");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

sealed class TypeTextProvider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        $"method ({string.Join(",", signature.ParameterTypes)}) -> {signature.ReturnType}*";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        $"{(isRequired ? "modreq" : "modopt")}({modifier}) {unmodifiedType}";
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => typeCode.ToString(),
    };
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        return Name(type.Namespace, type.Name, reader);
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        return Name(type.Namespace, type.Name, reader);
    }
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    static string Name(StringHandle ns, StringHandle name, MetadataReader reader)
    {
        var namespaceName = reader.GetString(ns);
        var simpleName = reader.GetString(name);
        return string.IsNullOrEmpty(namespaceName) ? simpleName : namespaceName + "." + simpleName;
    }
}

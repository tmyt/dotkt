using System.Collections.Immutable;
using System.Reflection.Metadata;
using DotKt.Bir;

internal sealed class MetadataAttributes
{
    internal const string DotKtNs = "DotKt.Runtime.CompilerServices.";
    internal const string Nullable = "System.Runtime.CompilerServices.NullableAttribute";
    internal const string NullableContext = "System.Runtime.CompilerServices.NullableContextAttribute";
    internal const string MaybeNull = "System.Diagnostics.CodeAnalysis.MaybeNullAttribute";
    internal const string NotNull = "System.Diagnostics.CodeAnalysis.NotNullAttribute";

    private readonly MetadataReader _md;
    private readonly bool _dotKtAssembly;
    private readonly bool _standardLibrary;
    private readonly HashSet<string> _trustedCarriers = new(StringComparer.Ordinal);

    public MetadataAttributes(MetadataReader md)
    {
        _md = md;
        _dotKtAssembly = HasAssemblyMarker();
        _standardLibrary = HasAssemblyMetadata("DotKt.LibraryKind", "stdlib");
        if (_dotKtAssembly)
        {
            foreach (var handle in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(handle);
                var name = FullName(def.Namespace, def.Name);
                if (name.StartsWith(DotKtNs, StringComparison.Ordinal) &&
                    Has(handle, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", requireTrust: false))
                    _trustedCarriers.Add(name);
            }
        }
    }

    public bool IsDotKtAssembly => _dotKtAssembly &&
        _trustedCarriers.Contains(DotKtNs + "KotlinFileClassAttribute");

    public bool IsStandardLibrary => _standardLibrary;

    public bool Has(EntityHandle owner, string name, bool requireTrust = true) =>
        All(owner, requireTrust).Any(a => a.Name == name);

    public Attr? Find(EntityHandle owner, string name, bool requireTrust = true) =>
        All(owner, requireTrust).FirstOrDefault(a => a.Name == name);

    public byte? Byte(EntityHandle owner, string name, bool requireTrust = true) =>
        Find(owner, name, requireTrust)?.ByteValue;

    public int? Int32(EntityHandle owner, string name) =>
        Find(owner, name)?.Int32Value;

    // A carrier whose payload is a JSON OBJECT rather than a single TypeNode — `[KotlinSupertypes]`, whose body is
    // `{base?, interfaces?, bounds?}` of pre-erasure nodes. Same envelope and same opaque encoding as every other
    // carrier; only the shape inside differs, which is why it is decoded here rather than through `CarrierType`.
    public System.Text.Json.JsonDocument? CarrierDocument(EntityHandle owner, string name)
    {
        var attr = Find(owner, name);
        if (attr?.StringValue is not { } version || attr.BytesValue is not { } bytes) return null;
        try
        {
            var body = BirCarrier.DecodeBody(version, bytes);
            return System.Text.Json.JsonDocument.Parse(body.ToJsonString(), DotKt.Bir.BirJson.DocOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"malformed [{name}] carrier: {ex.Message}", ex);
        }
    }

    public TypeNode? CarrierType(EntityHandle owner, string name)
    {
        var attr = Find(owner, name);
        if (attr?.StringValue is not { } version || attr.BytesValue is not { } bytes) return null;
        try
        {
            var body = BirCarrier.DecodeBody(version, bytes);
            using var doc = System.Text.Json.JsonDocument.Parse(body.ToJsonString(), DotKt.Bir.BirJson.DocOptions);
            return TypeNode.Read(doc.RootElement);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"malformed [{name}] carrier: {ex.Message}", ex);
        }
    }

    public byte[]? Nullability(EntityHandle owner)
    {
        var attr = Find(owner, Nullable, requireTrust: false);
        return attr?.BytesValue ?? (attr?.ByteValue is byte b ? [b] : null);
    }

    private IEnumerable<Attr> All(EntityHandle owner, bool requireTrust)
    {
        if (owner.IsNil) yield break;
        foreach (var handle in _md.GetCustomAttributes(owner))
        {
            var ca = _md.GetCustomAttribute(handle);
            var name = AttributeTypeName(ca.Constructor);
            if (name is null) continue;
            if (requireTrust && name.StartsWith(DotKtNs, StringComparison.Ordinal) &&
                (!IsDotKtAssembly || !_trustedCarriers.Contains(name)))
                continue;
            Attr? decoded = null;
            try { decoded = Decode(name, ca.Value); }
            catch { /* unrelated or malformed attributes never hide usable metadata siblings */ }
            if (decoded is not null) yield return decoded;
        }
    }

    private Attr Decode(string name, BlobHandle valueHandle)
    {
        var reader = _md.GetBlobReader(valueHandle);
        if (reader.ReadUInt16() != 1) throw new BadImageFormatException("custom attribute prolog");

        if (name == Nullable)
        {
            // The scalar form is exactly prolog + byte + named-count. The array
            // form is prolog + int32 length + bytes + named-count.
            if (reader.RemainingBytes == 3)
            {
                var b = reader.ReadByte();
                return new(name, ByteValue: b);
            }
            var count = reader.ReadInt32();
            if (count < 0 || count > reader.RemainingBytes - 2) throw new BadImageFormatException("Nullable byte array");
            return new(name, BytesValue: reader.ReadBytes(count));
        }
        if (name == NullableContext)
            return new(name, ByteValue: reader.ReadByte());
        if (name == DotKtNs + "KotlinFunctionAttribute" ||
            name == DotKtNs + "KotlinContextFunctionTypeAttribute")
            return new(name, Int32Value: reader.ReadInt32());
        if (name == "System.Reflection.AssemblyMetadataAttribute")
            return new(name, StringValue: reader.ReadSerializedString(), StringValue2: reader.ReadSerializedString());
        if (name.StartsWith(DotKtNs, StringComparison.Ordinal) &&
            name is not (DotKtNs + "KotlinFileClassAttribute"
                or DotKtNs + "KotlinReadOnlyAttribute"
                or DotKtNs + "KotlinFunInterfaceAttribute"
                or DotKtNs + "KotlinSealedAttribute"
                or DotKtNs + "KotlinValueAttribute"
                or DotKtNs + "KotlinObjectAttribute"
                or DotKtNs + "KotlinExtensionFunctionTypeAttribute"
                or DotKtNs + "KotlinContextParameterAttribute"
                or DotKtNs + "KotlinNothingAttribute"))
        {
            var version = reader.ReadSerializedString();
            var count = reader.ReadInt32();
            if (version is null || count < 0 || count > reader.RemainingBytes - 2)
                throw new BadImageFormatException("carrier");
            return new(name, StringValue: version, BytesValue: reader.ReadBytes(count));
        }
        return new(name);
    }

    private bool HasAssemblyMarker()
        => HasAssemblyMetadata("DotKt.Compiler", "metadata-v1");

    private bool HasAssemblyMetadata(string key, string value)
    {
        if (!_md.IsAssembly) return false;
        return All(_md.GetAssemblyDefinition().GetCustomAttributes(), "System.Reflection.AssemblyMetadataAttribute")
            .Any(a => a.StringValue == key && a.StringValue2 == value);
    }

    private IEnumerable<Attr> All(CustomAttributeHandleCollection handles, string expectedName)
    {
        foreach (var handle in handles)
        {
            var ca = _md.GetCustomAttribute(handle);
            if (AttributeTypeName(ca.Constructor) != expectedName) continue;
            Attr? decoded = null;
            try { decoded = Decode(expectedName, ca.Value); } catch { }
            if (decoded is not null) yield return decoded;
        }
    }

    private string? AttributeTypeName(EntityHandle constructor) => constructor.Kind switch
    {
        HandleKind.MemberReference => ParentTypeName(_md.GetMemberReference((MemberReferenceHandle)constructor).Parent),
        HandleKind.MethodDefinition => DefinitionName(_md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
        _ => null,
    };

    private string? ParentTypeName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => ReferenceName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => DefinitionName((TypeDefinitionHandle)parent),
        _ => null,
    };

    private string ReferenceName(TypeReferenceHandle handle)
    {
        var type = _md.GetTypeReference(handle);
        return FullName(type.Namespace, type.Name);
    }

    private string DefinitionName(TypeDefinitionHandle handle)
    {
        var type = _md.GetTypeDefinition(handle);
        return FullName(type.Namespace, type.Name);
    }

    private string FullName(StringHandle ns, StringHandle name)
    {
        var n = _md.GetString(name);
        var p = ns.IsNil ? "" : _md.GetString(ns);
        return string.IsNullOrEmpty(p) ? n : p + "." + n;
    }

    internal sealed record Attr(
        string Name,
        byte? ByteValue = null,
        int? Int32Value = null,
        string? StringValue = null,
        string? StringValue2 = null,
        byte[]? BytesValue = null);
}

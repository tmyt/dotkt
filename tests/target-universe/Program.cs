using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: MetadataProbe <raw.dll>");
    return 2;
}

var raw = Read(args[0]);

RequirePrimitiveSignature(raw);
RequireEnumShape(raw, "TargetEnum");
RequireGenericBaseShape(raw, "TargetList`1");
RequireNoLocalType(raw, "NullableAttribute");
RequireNoLocalType(raw, "NullableContextAttribute");
RequireScope(raw, "System.Object", "System.Runtime");
RequireScope(raw, "System.Collections.Generic.List`1", "System.Collections");
RequireScope(raw, "System.Func`2", "System.Runtime");
RequireScope(raw, "System.IDisposable", "System.Runtime");
RequireScope(raw, "System.Reflection.AssemblyMetadataAttribute", "System.Runtime");
RequireScope(raw, "System.Runtime.Versioning.TargetFrameworkAttribute", "System.Runtime");
RequireScope(raw, "System.Runtime.CompilerServices.NullableAttribute", "System.Runtime");
RequireScope(raw, "System.Runtime.CompilerServices.NullableContextAttribute", "System.Runtime");
RequireAppliedAttribute(raw, "System.Runtime.Versioning.TargetFrameworkAttribute");
RequireAppliedAttribute(raw, "System.Runtime.CompilerServices.NullableAttribute");
RequireAppliedAttribute(raw, "System.Runtime.CompilerServices.NullableContextAttribute");

RejectScope(raw, "System.Private.CoreLib");

Console.WriteLine("PASS  target-universe emission: raw scopes are target contracts; target framework and nullable " +
                  "attributes are target-BCL references (primitive, generic, delegate, inheritance/interface, external signature)");
return 0;

static Snapshot Read(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
    var md = pe.GetMetadataReader();
    var scopes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var handle in md.TypeReferences)
    {
        var type = md.GetTypeReference(handle);
        var name = md.GetString(type.Namespace);
        if (name.Length > 0) name += ".";
        name += md.GetString(type.Name);
        var scope = ScopeName(md, type.ResolutionScope);
        if (!scopes.TryGetValue(name, out var set)) scopes[name] = set = new(StringComparer.Ordinal);
        set.Add(scope);
    }
    var methods = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var handle in md.MethodDefinitions)
    {
        var method = md.GetMethodDefinition(handle);
        methods[md.GetString(method.Name)] = md.GetBlobBytes(method.Signature);
    }
    var types = new Dictionary<string, TypeShape>(StringComparer.Ordinal);
    foreach (var handle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(handle);
        var name = md.GetString(type.Name);
        types[name] = new TypeShape(
            type.GetMethods().Select(h => md.GetString(md.GetMethodDefinition(h).Name)).ToArray(),
            type.BaseType.Kind,
            type.BaseType.Kind == HandleKind.TypeSpecification
                ? md.GetBlobBytes(md.GetTypeSpecification((TypeSpecificationHandle)type.BaseType).Signature)
                : Array.Empty<byte>());
    }
    var appliedAttributes = new HashSet<string>(StringComparer.Ordinal);
    foreach (var handle in md.CustomAttributes)
    {
        var attr = md.GetCustomAttribute(handle);
        var owner = attr.Constructor.Kind switch
        {
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent,
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType(),
            _ => default,
        };
        var name = TypeName(md, owner);
        if (name != null) appliedAttributes.Add(name);
    }
    return new Snapshot(scopes, methods, types, appliedAttributes);
}

static string? TypeName(MetadataReader md, EntityHandle handle)
{
    StringHandle ns;
    StringHandle name;
    switch (handle.Kind)
    {
        case HandleKind.TypeReference:
            var reference = md.GetTypeReference((TypeReferenceHandle)handle);
            ns = reference.Namespace;
            name = reference.Name;
            break;
        case HandleKind.TypeDefinition:
            var definition = md.GetTypeDefinition((TypeDefinitionHandle)handle);
            ns = definition.Namespace;
            name = definition.Name;
            break;
        default:
            return null;
    }
    var prefix = md.GetString(ns);
    return prefix.Length == 0 ? md.GetString(name) : prefix + "." + md.GetString(name);
}

static string ScopeName(MetadataReader md, EntityHandle scope) => scope.Kind switch
{
    HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
    HandleKind.TypeReference => ScopeName(md, md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope),
    HandleKind.ModuleDefinition => "<module>",
    HandleKind.ModuleReference => md.GetString(md.GetModuleReference((ModuleReferenceHandle)scope).Name),
    _ => "<" + scope.Kind + ">",
};

static void RequireScope(Snapshot snapshot, string type, string expected)
{
    if (!snapshot.TypeScopes.TryGetValue(type, out var actual)
        || actual.Count != 1 || !actual.Contains(expected))
        throw new InvalidOperationException(
            $"TypeRef {type} is not scoped exclusively to {expected}; actual: " +
            (actual == null ? "<missing>" : string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))));
}

static void RejectScope(Snapshot snapshot, string rejected)
{
    var offenders = snapshot.TypeScopes
        .Where(pair => pair.Value.Contains(rejected))
        .Select(pair => pair.Key)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
    if (offenders.Length != 0)
        throw new InvalidOperationException(
            $"unexpected {rejected} TypeRef scope: {string.Join(", ", offenders)}");
}

static void RequirePrimitiveSignature(Snapshot snapshot)
{
    // primitive(Int)->Int is a non-generic static signature: calling convention, param-count, return I4, param I4.
    // Pin the primitive signature category independently of TypeRef rows (ECMA primitives are encoded inline).
    if (!snapshot.MethodSignatures.TryGetValue("primitive", out var signature)
        || signature.Count(b => b == (byte)SignatureTypeCode.Int32) < 2)
        throw new InvalidOperationException("primitive(Int): Int does not carry two ELEMENT_TYPE_I4 signature slots");
}

static void RequireEnumShape(Snapshot snapshot, string name)
{
    if (!snapshot.Types.TryGetValue(name, out var type))
        throw new InvalidOperationException($"enum {name} is missing");
    if (type.Methods.Length != 0)
        throw new InvalidOperationException($"enum {name} carries illegal methods: {string.Join(", ", type.Methods)}");
}

static void RequireGenericBaseShape(Snapshot snapshot, string name)
{
    if (!snapshot.Types.TryGetValue(name, out var type))
        throw new InvalidOperationException($"generic derived type {name} is missing");
    // GENERICINST CLASS <TypeRef> 1 VAR 0. The compressed TypeDefOrRef token is intentionally opaque here.
    if (type.BaseKind != HandleKind.TypeSpecification || type.BaseSignature.Length < 6
        || type.BaseSignature[0] != (byte)SignatureTypeCode.GenericTypeInstance
        || type.BaseSignature[^2] != (byte)SignatureTypeCode.GenericTypeParameter
        || type.BaseSignature[^1] != 0)
        throw new InvalidOperationException(
            $"generic derived type {name} does not retain its target TypeSpec<T> base: " +
            Convert.ToHexString(type.BaseSignature));
}

static void RequireNoLocalType(Snapshot snapshot, string name)
{
    if (snapshot.Types.ContainsKey(name))
        throw new InvalidOperationException($"output illegally defines target-BCL type {name}");
}

static void RequireAppliedAttribute(Snapshot snapshot, string name)
{
    if (!snapshot.AppliedAttributes.Contains(name))
        throw new InvalidOperationException($"output does not apply target attribute {name}");
}

sealed record Snapshot(
    Dictionary<string, HashSet<string>> TypeScopes,
    Dictionary<string, byte[]> MethodSignatures,
    Dictionary<string, TypeShape> Types,
    HashSet<string> AppliedAttributes);

sealed record TypeShape(string[] Methods, HandleKind BaseKind, byte[] BaseSignature);

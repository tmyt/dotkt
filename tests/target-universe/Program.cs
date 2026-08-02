using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: MetadataProbe <raw.dll> <retargeted.dll>");
    return 2;
}

var raw = Read(args[0]);
var repaired = Read(args[1]);

RequirePrimitiveSignature(raw);
RequireScope(raw, "System.Object", "System.Private.CoreLib");
RequireScope(raw, "System.Collections.Generic.List`1", "System.Private.CoreLib");
RequireScope(raw, "System.Func`2", "System.Private.CoreLib");
RequireScope(raw, "System.IDisposable", "System.Private.CoreLib");
RequireScope(raw, "System.Reflection.AssemblyMetadataAttribute", "System.Private.CoreLib");

RequireScope(repaired, "System.Object", "System.Runtime");
RequireScope(repaired, "System.Collections.Generic.List`1", "System.Collections");
RequireScope(repaired, "System.Func`2", "System.Runtime");
RequireScope(repaired, "System.IDisposable", "System.Runtime");
RequireScope(repaired, "System.Reflection.AssemblyMetadataAttribute", "System.Runtime");

if (File.ReadAllBytes(args[0]).AsSpan().SequenceEqual(File.ReadAllBytes(args[1])))
    throw new InvalidOperationException("retarget made no change to the calibrated host-scoped raw output");

Console.WriteLine("PASS  target-universe baseline: raw host scopes -> target contract scopes " +
                  "(primitive, generic, delegate, attribute, inheritance/interface, external signature)");
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
    return new Snapshot(scopes, methods);
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
    if (!snapshot.TypeScopes.TryGetValue(type, out var actual) || !actual.Contains(expected))
        throw new InvalidOperationException(
            $"TypeRef {type} is not scoped to {expected}; actual: " +
            (actual == null ? "<missing>" : string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))));
}

static void RequirePrimitiveSignature(Snapshot snapshot)
{
    // primitive(Int)->Int is a non-generic static signature: calling convention, param-count, return I4, param I4.
    // Pin the primitive signature category independently of TypeRef rows (ECMA primitives are encoded inline).
    if (!snapshot.MethodSignatures.TryGetValue("primitive", out var signature)
        || signature.Count(b => b == (byte)SignatureTypeCode.Int32) < 2)
        throw new InvalidOperationException("primitive(Int): Int does not carry two ELEMENT_TYPE_I4 signature slots");
}

sealed record Snapshot(
    Dictionary<string, HashSet<string>> TypeScopes,
    Dictionary<string, byte[]> MethodSignatures);

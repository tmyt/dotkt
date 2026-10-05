using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

var tool = Assembly.LoadFrom(args[0]);
var identity = tool.GetType("RawSignatureTypeProvider", true)!
    .GetMethod("ScopedReferenceIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;
string Key(int major, byte tokenByte, string ns, string name, string parent = null,
    AssemblyFlags flags = 0)
{
    var md = new MetadataBuilder();
    md.AddModule(0, md.GetOrAddString("Probe"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
    var assembly = md.AddAssemblyReference(md.GetOrAddString("External"), new Version(major, 0, 0, 0),
        default, md.GetOrAddBlob(ImmutableArray.CreateRange(Enumerable.Repeat(tokenByte, 8))), flags, default);
    EntityHandle scope = assembly;
    if (parent != null) scope = md.AddTypeReference(scope, md.GetOrAddString("Outer"), md.GetOrAddString(parent));
    var handle = md.AddTypeReference(scope, md.GetOrAddString(ns), md.GetOrAddString(name));
    var blob = new BlobBuilder();
    new MetadataRootBuilder(md).Serialize(blob, 0, 0);
    using var provider = MetadataReaderProvider.FromMetadataImage(blob.ToImmutableArray());
    return (string)identity.Invoke(null, [provider.GetMetadataReader(), handle])!;
}
var baseline = Key(1, 1, "N", "T");
if (baseline != Key(1, 1, "N", "T")) throw new Exception("Reader identity leaked into external scope");
foreach (var other in new[] { Key(2, 1, "N", "T"), Key(1, 2, "N", "T"), Key(1, 1, "Other", "T"),
    Key(1, 1, "N", "Other"), Key(1, 1, "N", "T", "A"), Key(1, 1, "N", "T", "B"),
    Key(1, 1, "N", "T", flags: AssemblyFlags.Retargetable),
    Key(1, 1, "N", "T", flags: AssemblyFlags.WindowsRuntime) })
    if (baseline == other) throw new Exception("Distinct scoped TypeRefs compare equal");
if (Key(1, 1, "N", "T", "A") == Key(1, 1, "N", "T", "B")) throw new Exception("Nested scopes collapsed");
if (Key(1, 1, "A/B", "C") == Key(1, 1, "A", "B/C")) throw new Exception("Metadata name boundaries collapsed");
Console.WriteLine("Scoped TypeRef identity: PASS");

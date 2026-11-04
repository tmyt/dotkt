using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using var pe = new PEReader(File.OpenRead(args[0]));
var entries = pe.ReadDebugDirectory();
if (args.Length == 1)
{
    Require(!entries.Any(e => e.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.EmbeddedPortablePdb),
        "An image built without symbols advertises a PDB.");
    return;
}

using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(args[1]));
var pdb = provider.GetMetadataReader();
var id = new BlobContentId(pdb.DebugMetadataHeader!.Id);
var codeView = entries.Single(e => e.Type == DebugDirectoryEntryType.CodeView);
var cv = pe.ReadCodeViewDebugDirectoryData(codeView);
Require(cv.Guid == id.Guid && codeView.Stamp == id.Stamp && cv.Age == 1
    && cv.Path == Path.GetFileName(args[1]), "DLL and Portable PDB identities differ.");

var metadata = pe.GetMetadataReader();
Require(pdb.MethodDebugInformation.Count == metadata.MethodDefinitions.Count, "PDB MethodDef rows are misaligned.");
string Source(DocumentHandle document) => pdb.GetString(pdb.GetDocument(document).Name);
var methods = new Dictionary<string, SequencePoint[]>();
foreach (var handle in metadata.MethodDefinitions)
{
    var info = pdb.GetMethodDebugInformation(handle);
    var points = info.GetSequencePoints().ToArray();
    if (points.Length == 0) continue;
    var method = metadata.GetMethodDefinition(handle);
    var body = pe.GetMethodBody(method.RelativeVirtualAddress);
    int codeSize = body.GetILReader().Length;
    Require(info.LocalSignature == body.LocalSignature, "PDB local signature does not match the method body.");
    int previous = -1;
    foreach (var point in points)
    {
        Require(point.Offset > previous && point.Offset < codeSize, "Invalid sequence point IL offset.");
        Require(Source(point.Document).EndsWith(".kt", StringComparison.Ordinal), "A source document is not Kotlin.");
        previous = point.Offset;
    }
    methods[metadata.GetString(method.Name)] = points;
}
var main = methods["main"].Where(p => !p.IsHidden).ToArray();
Require(main.Select(p => p.StartLine).SequenceEqual([2, 3, 4])
    && main.All(p => Source(p.Document) == args[2] && p.StartColumn == 5 && p.EndColumn > p.StartColumn),
    "main does not map to the three actual Kotlin source lines.");
if (args.Length > 3)
{
    var documents = methods["documents"].Where(p => !p.IsHidden).Select(p => Source(p.Document)).ToArray();
    int foreign = Array.IndexOf(documents, args[3]);
    Require(foreign > 0 && documents.Take(foreign).Contains(args[2]) && documents.Skip(foreign + 1).Contains(args[2]),
        "Inline source document transitions were lost or reordered.");
    var loop = methods["controlFlow"];
    int hidden = Array.FindIndex(loop, p => p.IsHidden);
    Require(loop.Where(p => !p.IsHidden).Select(p => p.StartLine).Distinct().SequenceEqual([14, 15, 16, 18])
        && hidden > 0 && hidden + 1 < loop.Length
        && loop[hidden - 1].StartLine == 16 && loop[hidden + 1].StartLine == 18,
        "The generated loop back edge is not hidden.");
}

static void Require(bool result, string message)
{
    if (!result) throw new InvalidDataException(message);
}

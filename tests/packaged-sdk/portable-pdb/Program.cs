using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;

if (args[0] == "--shift-body-map")
{
    var input = JsonNode.Parse(File.ReadAllText(args[1]))!;
    var declarations = input["types"]![0]!["methods"]!;
    var point = declarations[1]!["body"]![0]!.DeepClone();
    declarations[0]!["body"] = declarations[1]!["body"]!.DeepClone();
    ((JsonArray)declarations[0]!["body"]!).RemoveAt(0);
    declarations[0]!["abstract"] = false;
    declarations[1]!["body"] = declarations[2]!["body"]!.DeepClone();
    ((JsonArray)declarations[1]!["body"]!).Insert(0, point);
    declarations[2]!["body"] = new JsonArray();
    declarations[2]!["abstract"] = true;
    File.WriteAllText(args[2], input.ToJsonString());
    return;
}
if (args[0] == "--different-pdb")
{
    (BlobContentId Id, StandaloneSignatureHandle Locals) ReadIdentity(string path)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path));
        var reader = provider.GetMetadataReader();
        var method = reader.MethodDebugInformation.Select(reader.GetMethodDebugInformation).Single(info =>
            !info.Document.IsNil && reader.GetString(reader.GetDocument(info.Document).Name) == "BodyMap.kt");
        return (new BlobContentId(reader.DebugMetadataHeader!.Id), method.LocalSignature);
    }
    var first = ReadIdentity(args[1]);
    var second = ReadIdentity(args[2]);
    Require(first.Locals != second.Locals, "The witness did not change the source method's local signature.");
    Require(first.Id != second.Id, "Different PDB content retained the same identity.");
    return;
}

bool? deterministic = args[^1] switch
{
    "--deterministic" => true,
    "--non-deterministic" => false,
    _ => null,
};
if (deterministic.HasValue) args = args[..^1];
using var pe = new PEReader(File.OpenRead(args[0]));
var metadata = pe.GetMetadataReader();
if (args.Length == 3 && args[1] == "--different-mvid")
{
    using var previous = new PEReader(File.OpenRead(args[2]));
    var previousMetadata = previous.GetMetadataReader();
    Require(metadata.GetGuid(metadata.GetModuleDefinition().Mvid) !=
        previousMetadata.GetGuid(previousMetadata.GetModuleDefinition().Mvid),
        "Different image content retained the same MVID.");
    return;
}
var inlineMethod = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
    .Single(method => metadata.GetString(method.Name) == "withSeed");
var inline = inlineMethod.GetCustomAttributes().Select(metadata.GetCustomAttribute).Single(attribute =>
    attribute.Constructor.Kind == HandleKind.MethodDefinition
    && metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType() is var parent
    && metadata.GetString(metadata.GetTypeDefinition(parent).Name) == "KotlinInlineAttribute");
var carrier = metadata.GetBlobReader(inline.Value);
Require(carrier.ReadUInt16() == 1 && carrier.ReadSerializedString() == DotKt.Bir.BirCarrier.MessagePackV1, "Unexpected inline carrier.");
var payload = MessagePack.MessagePackSerializer.ConvertToJson(carrier.ReadBytes(carrier.ReadInt32()));
Require(payload.Contains("\"k\":\"sourcePoint\"") && !payload.Contains("\"span\":"),
    "The executable DLL carrier must not contain the producer's source spans.");

var supplied = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition)
    .Single(method => metadata.GetString(method.Name) == "supplied");
var parameterHandle = supplied.GetParameters().Single(handle => metadata.GetParameter(handle).SequenceNumber == 1);
var parameter = metadata.GetParameter(parameterHandle);
var defaultValue = parameter.GetCustomAttributes().Select(metadata.GetCustomAttribute).Single(attribute =>
    attribute.Constructor.Kind == HandleKind.MethodDefinition
    && metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType() is var parent
    && metadata.GetString(metadata.GetTypeDefinition(parent).Name) == "KotlinDefaultAttribute");
var defaultCarrier = metadata.GetBlobReader(defaultValue.Value);
var defaultConstructor = metadata.GetMethodDefinition((MethodDefinitionHandle)defaultValue.Constructor);
var defaultSignature = metadata.GetBlobReader(defaultConstructor.Signature);
Require(defaultSignature.ReadSignatureHeader().IsInstance && defaultSignature.ReadCompressedInteger() == 3
    && defaultSignature.ReadSignatureTypeCode() == SignatureTypeCode.Void
    && defaultSignature.ReadSignatureTypeCode() == SignatureTypeCode.Int32
    && defaultSignature.ReadSignatureTypeCode() == SignatureTypeCode.String
    && defaultSignature.ReadSignatureTypeCode() == SignatureTypeCode.SZArray
    && defaultSignature.ReadSignatureTypeCode() == SignatureTypeCode.Byte
    && defaultSignature.RemainingBytes == 0, "Unexpected physical default carrier constructor.");
Require(defaultCarrier.ReadUInt16() == 1 && defaultCarrier.ReadInt32() == 0, "Unexpected default carrier.");
var defaultCodec = defaultCarrier.ReadSerializedString()!;
Require(defaultCodec == DotKt.Bir.BirCarrier.MessagePackV1, "Unexpected default codec.");
var defaultBytes = defaultCarrier.ReadBytes(defaultCarrier.ReadInt32());
var defaultFacts = new MetadataAttributes(metadata).Find(parameterHandle,
    "DotKt.Runtime.CompilerServices.KotlinDefaultAttribute");
Require(defaultFacts is { Int32Value: 0 } && defaultFacts.StringValue == defaultCodec
    && defaultFacts.BytesValue!.SequenceEqual(defaultBytes),
    "The projection metadata reader did not preserve the indexed default carrier.");
var defaultPayload = MessagePack.MessagePackSerializer.ConvertToJson(defaultFacts.BytesValue!);
Require(defaultPayload.Contains("\"k\":\"sourcePoint\"") && !defaultPayload.Contains("\"span\":"),
    "The default expression carrier must not contain the producer's source spans.");

var entries = pe.ReadDebugDirectory();
if (deterministic.HasValue)
    Require(entries.Any(e => e.Type == DebugDirectoryEntryType.Reproducible) == deterministic.Value,
        "The reproducible-image marker does not reflect Deterministic.");
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
    var name = metadata.GetString(method.Name);
    if (name is ".ctor" or ".cctor")
        name = metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name) + "::" + name;
    methods[name] = points;
}
var main = methods["main"].Where(p => !p.IsHidden).ToArray();
Require(main.Select(p => p.StartLine).SequenceEqual([2, 3, 4, 5])
    && main.All(p => Source(p.Document) == args[2] && p.StartColumn == 5 && p.EndColumn > 5),
    "main does not map to its actual Kotlin source.");

var documents = methods["documents"].Where(p => !p.IsHidden).Select(p => (Source(p.Document), p.StartLine)).ToArray();
int callee = Array.IndexOf(documents, (args[3], 2));
int callSite = Array.IndexOf(documents, (args[4], 3));
int lambda = Array.IndexOf(documents, (args[4], 4));
Require(callSite > 0 && callee > callSite && lambda > callee && documents[^1] == (args[4], 6),
    "The invocation, inlined method, lambda and following source must have distinct ordered points.");
var loop = methods["controlFlow"];
int hidden = Array.FindIndex(loop, p => p.IsHidden);
Require(loop.Where(p => !p.IsHidden).Select(p => p.StartLine).Distinct().SequenceEqual([10, 11, 12, 14])
    && hidden > 0 && hidden + 1 < loop.Length
    && loop[hidden - 1].StartLine == 12 && loop[hidden + 1].StartLine == 14,
    "The generated loop back edge is not hidden.");
Require(HasLine("branch", 18) && HasLine("branch", 20), "Value branch spans were lost.");
Require(HasLine("SourceInitializer::.ctor", 23) && HasLine("SourceCasesKt::.cctor", 26),
    "Property initializers do not map to the source expressions.");
var initializer = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
    .Single(type => metadata.GetString(type.Name) == "SourceInitializer");
var getter = initializer.GetProperties().Select(metadata.GetPropertyDefinition)
    .Single(property => metadata.GetString(property.Name) == "value").GetAccessors().Getter;
Require(pdb.GetMethodDebugInformation(getter).GetSequencePoints().All(point => point.IsHidden),
    "A generated getter claims the property initializer's source line.");

var lines = File.ReadAllLines(args[4]);
foreach (var expression in new[] { "readNumber()", "makePair()", "nullableString()" })
{
    int line = Array.FindIndex(lines, text => text.StartsWith("    ") && text.Contains(expression));
    Require(line >= 0 && methods["loweredEvaluations"].Any(p => !p.IsHidden && Source(p.Document) == args[4]
        && p.StartLine == line + 1 && p.StartColumn == lines[line].IndexOf(expression, StringComparison.Ordinal) + 1),
        $"Evaluation moved into a temporary lost its source position: {expression}.");
}

bool HasLine(string method, int line) => methods.TryGetValue(method, out var points)
    && points.Any(p => !p.IsHidden && Source(p.Document) == args[4] && p.StartLine == line);

static void Require(bool result, string message)
{
    if (!result) throw new InvalidDataException(message);
}

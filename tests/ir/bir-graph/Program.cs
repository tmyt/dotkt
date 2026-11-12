using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static byte[] Write(BirGraphValue graph)
{
    using var output = new MemoryStream();
    using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = BirJson.MaxDepth }))
        graph.WriteJson(writer);
    return output.ToArray();
}

static Utf8JsonReader Reader(string json)
{
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = BirJson.MaxDepth });
    if (!reader.Read()) throw new Exception("Empty sample");
    return reader;
}

static void Same(string json, BirGraphValue graph)
{
    if (!JsonNode.DeepEquals(JsonNode.Parse(json, documentOptions: BirJson.DocOptions),
        JsonNode.Parse(Write(graph), documentOptions: BirJson.DocOptions)))
        throw new Exception("Owning BIR graph changed a logical value");
}

var samples = XDocument.Load(args[0]).Descendants("system-out")
    .SelectMany(output => output.Value.Split('\n')).ToArray();
var core = 0;
foreach (var line in samples)
{
    const string prefix = "IR_BIR_CORE_SAMPLE ";
    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
    var json = line[prefix.Length..].TrimEnd('\r');
    var reader = Reader(json);
    var graph = BirGraphJson.ReadNode(ref reader);
    if (reader.Read()) throw new Exception("Trailing executable sample content");
    Same(json, graph);
    var copy = graph.Rewrite(value => value);
    Same(json, copy);
    core++;
}
if (core != 5) throw new Exception($"Expected five actual Kotlin owning-node samples, got {core}");

var scalars = 0;
foreach (var line in samples)
{
    const string prefix = "IR_BIR_LITERAL_SAMPLE ";
    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
    var json = line[prefix.Length..].TrimEnd('\r');
    var reader = Reader(json);
    var graph = BirGraphJson.ReadNode(ref reader);
    Same(json, graph);
    if (graph is not BirConstant) throw new Exception("Literal sample is not a source constant");
    scalars++;
}
if (scalars != 23) throw new Exception($"Expected 23 actual Kotlin scalar samples, got {scalars}");

var negativeZeroReader = Reader("-0");
if (BirLiteral.ReadJson(ref negativeZeroReader) is not BirLiteral.RealLiteral zero
    || BitConverter.DoubleToInt64Bits(zero.Value) != long.MinValue)
    throw new Exception("A raw JSON negative zero lost its sign before physical scalar allocation");

// Values are owned and rewrites are structural, including nested local function
// declarations, nullable inline argument slots, and declaration-frame types.
var frame = new TypeNode.Tv("method", 1);
var original = new BirCallInline {
    Args = new IBirExpression?[] { null, new BirLocal { Name = "before", Sty = frame } },
    RetType = frame,
};
var rewritten = (BirCallInline)original.Rewrite(value => value is BirLocal local
    ? local with { Name = "after" } : value);
if (original.Args.Value[0] is not null || rewritten.Args.Value[0] is not null
    || ((BirLocal)original.Args.Value[1]!).Name.Value != "before"
    || ((BirLocal)rewritten.Args.Value[1]!).Name.Value != "after"
    || rewritten.RetType.Value != frame || ReferenceEquals(original.Args.Value, rewritten.Args.Value))
    throw new Exception("A typed rewrite lost a null slot/frame or mutated the input graph");

var absent = BirGraphJson.ReadFile(Encoding.UTF8.GetBytes("{\"fileClass\":\"Probe\"}"));
var present = BirGraphJson.ReadFile(Encoding.UTF8.GetBytes("{\"fileClass\":\"Probe\",\"fields\":null,\"methods\":[]}"));
if (absent.Fields.IsPresent || !present.Fields.IsNull || !present.Methods.HasValue || present.Methods.Value.Length != 0)
    throw new Exception("Missing, null, and empty fields were conflated");
Same("{\"fileClass\":\"Probe\"}", absent);
Same("{\"fileClass\":\"Probe\",\"fields\":null,\"methods\":[]}", present);

foreach (var json in new[] { "{\"fileClass\":\"A\",\"fileClass\":\"B\"}",
    "{\"fileClass\":\"A\"}{}" })
{
    try { BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(json)); }
    catch (Exception error) when (error is FormatException or JsonException) { continue; }
    throw new Exception("A malformed current document was accepted");
}
try
{
    var reader = Reader("{\"k\":\"const\"}");
    BirGraphJson.ReadBirStatement(ref reader);
    throw new Exception("An expression was accepted in a statement slot");
}
catch (FormatException) { }

var opaque = new BirAttribute {
    Args = new IBirAttributeValue[] { new BirOpaqueBytes { Bytes = new byte[] { 0, 255, 0xd8, 0, 17 } } },
};
var seenBytes = 0;
opaque.Rewrite(value => { if (value is BirOpaqueBytes) seenBytes++; return value; });
if (seenBytes != 1) throw new Exception("Opaque metadata was traversed as executable children");
using (var stream = new MemoryStream(Write(opaque)))
{
    var reader = new Utf8JsonReader(stream.ToArray());
    reader.Read();
    var copy = BirGraphJson.ReadBirAttribute(ref reader);
    if (!((BirOpaqueBytes)copy.Args.Value[0]).Bytes.Value.SequenceEqual(((BirOpaqueBytes)opaque.Args.Value[0]).Bytes.Value))
        throw new Exception("Opaque bytes changed at the graph boundary");
}

var defaults = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
    Mods = new BirModifiers { Suspend = true },
    Params = new[] { new BirParameter { Name = "p", DefaultBir = new BirDefaultBody {
        Index = 2, Body = new BirConstant { Type = frame, Value = new Float32Literal(unchecked((int)0xffc01234)) },
    } } },
} } };
var frozen = DefaultBirCarrier.FreezeSourceDefaults(defaults);
var parameter = frozen.Methods.Value[0].Params.Value[0];
if (!defaults.Methods.Value[0].Params.Value[0].DefaultBir.HasValue || parameter.DefaultBir.IsPresent
    || SuspendShapeAnalyzer.Analyze(frozen).FunctionCount != 1)
    throw new Exception("Freezing source defaults changed original ownership or the declaration inventory");
var carrier = parameter.Attrs.Value[0].ToDom();
if (!JsonNode.DeepEquals(DefaultBirCarrier.Decode(carrier),
    ((BirGraphValue)defaults.Methods.Value[0].Params.Value[0].DefaultBir.Value.Body.Value).ToDom()))
    throw new Exception("The typed source default changed its frame or float payload while freezing");

var authored = 0;
foreach (var file in Directory.EnumerateFiles("tests/ir/lowering", "*.bir*.json").Order(StringComparer.Ordinal))
{
    if (Path.GetFileName(file).StartsWith("reject-", StringComparison.Ordinal)) continue;
    var json = File.ReadAllText(file);
    Same(json, BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(json)));
    authored++;
}
if (authored == 0) throw new Exception("No accepted lowering inputs were checked");

var corpus = 0;
if (args.Length > 1)
{
    foreach (var file in File.ReadAllLines(args[1]).Distinct(StringComparer.Ordinal))
    {
        var json = File.ReadAllText(file);
        Same(json, BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(json)));
        corpus++;
    }
    if (corpus == 0) throw new Exception("No actual compiler BIR was selected");
}
Console.WriteLine($"Owning BIR graph: {core} Kotlin node / {scalars} scalar samples, {authored} authored / {corpus} actual inputs; presence, structural rewrite, default freeze OK");

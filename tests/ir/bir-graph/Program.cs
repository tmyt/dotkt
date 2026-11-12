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

static void Same(string json, BirGraphValue graph, string? context = null)
{
    var source = JsonNode.Parse(json, documentOptions: BirJson.DocOptions)!;
    if (!LogicalBirValue.Equal(source,
        JsonNode.Parse(Write(graph), documentOptions: BirJson.DocOptions)))
        throw new Exception($"Owning BIR graph changed a logical value: {context ?? "inline sample"}");
    // Exercise the actual production seam too, not only the debug JSON writer.
    if (graph is BirFileDeclaration file)
    {
        source.AsObject().Remove("typeAliases");
        graph = file with { TypeAliases = default };
    }
    var dom = graph.ToDom();
    if (!LogicalBirValue.Equal(source, dom))
        throw new Exception($"The temporary owning-pass frontier changed a logical value: {context ?? "inline sample"}");
    BirGraphValue? restored = graph switch {
        BirGraphNode => BirGraphDom.ReadNode(dom),
        BirFileDeclaration => BirGraphDom.ReadBirFileDeclaration(dom),
        _ => null,
    };
    if (restored is not null && !LogicalBirValue.Equal(source,
        JsonNode.Parse(Write(restored), documentOptions: BirJson.DocOptions)))
        throw new Exception($"The direct owning-pass entry changed a logical value: {context ?? "inline sample"}");
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
var binarySamples = 0;
foreach (var line in samples)
{
    const string prefix = "IR_BIR_BINARY_SAMPLE ";
    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
    var pair = line[prefix.Length..].TrimEnd('\r').Split(' ');
    if (pair.Length != 2) throw new Exception("Malformed Kotlin binary sample report");
    var json = Encoding.UTF8.GetString(Convert.FromBase64String(pair[0]));
    var graph = BirFileCodec.Decode(Convert.FromBase64String(pair[1]));
    Same(json, graph, "actual Kotlin MessagePack file");
    Same(json, BirFileCodec.Decode(BirFileCodec.Encode(graph)), "C# MessagePack rewrite");
    binarySamples++;
}
if (binarySamples != 29) throw new Exception($"Expected 29 actual Kotlin binary files, got {binarySamples}");
Console.WriteLine($"Kotlin -> C# BIR MessagePack: {binarySamples} files preserve literals, aliases, UTF16, source IDs and generic frames");
var nullReader = Reader("{\"k\":\"const\",\"value\":null}");
if (BirGraphJson.ReadNode(ref nullReader) is not BirConstant { Value.HasValue: true, Value.Value: BirLiteral.NullLiteral })
    throw new Exception("A semantic null literal was decoded as an untyped null field");

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
var independent = (BirCallInline)original.Rewrite(value => value);
independent.Args.Value[1] = new BirLocal { Name = "independent" };
if (((BirLocal)original.Args.Value[1]!).Name.Value != "before")
    throw new Exception("Independent executable mutation leaked through a shared child array");
var childSlots = new List<BirGraphSlot>();
independent.VisitChildSlots(childSlots.Add);
childSlots.Single().Replace(new BirLocal { Name = "slot-replacement", Sty = frame });
if (((BirLocal)childSlots.Single().Value).Name.Value != "slot-replacement"
    || ((BirLocal)independent.Args.Value[1]!).Name.Value != "slot-replacement"
    || original.Args.Value[0] != null || independent.Args.Value[0] != null
    || ((BirLocal)original.Args.Value[1]!).Name.Value != "before" || independent.RetType.Value != frame)
    throw new Exception("Typed live child replacement lost its role, ownership, null slot or exact frame");

var absent = BirGraphJson.ReadFile(Encoding.UTF8.GetBytes("{\"fileClass\":\"Probe\"}"));
var present = BirGraphJson.ReadFile(Encoding.UTF8.GetBytes("{\"fileClass\":\"Probe\",\"fields\":null,\"methods\":[]}"));
if (absent.Fields.IsPresent || !present.Fields.IsNull || !present.Methods.HasValue || present.Methods.Value.Length != 0)
    throw new Exception("Missing, null, and empty fields were conflated");
Same("{\"fileClass\":\"Probe\"}", absent);
Same("{\"fileClass\":\"Probe\",\"fields\":null,\"methods\":[]}", present);

// Frozen TypeNode facts retain their structure, not a particular JSON string
// escape spelling. Ordinary user string literals still compare byte-for-byte.
const string frozenTypeJson = """{"k":"callStatic","companionReceiver":"{\"t\":\"fqn\",\"name\":\"probe.箱`1\"}"}""";
var frozenTypeReader = Reader(frozenTypeJson);
Same(frozenTypeJson, BirGraphJson.ReadNode(ref frozenTypeReader));
if (LogicalBirValue.Equal(JsonNode.Parse(frozenTypeJson), JsonNode.Parse(frozenTypeJson.Replace("箱", "箱違い")))
    || LogicalBirValue.Equal(JsonNode.Parse("""{"k":"const","value":"a"}"""), JsonNode.Parse("""{"k":"const","value":"b"}""")))
    throw new Exception("Logical comparison ignored a frozen classifier or an ordinary string value");

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
    Args = new IBirAttributeValue[] {
        new BirOpaqueBytes { Bytes = new byte[] { 0, 255, 0xd8, 0, 17 } },
        new BirAttributeLiteral(),
        new BirAttributeLiteral { Value = new BirLiteral.NullLiteral() },
        new BirAttributeLiteral { Type = new TypeNode.Fqn("System.UInt64"),
            Value = new BirLiteral.UnsignedIntegerLiteral(ulong.MaxValue) },
        new BirAttributeLiteral { Type = new TypeNode.Fqn("System.Single"),
            Value = new Float32Literal(unchecked((int)0xffc01234)) },
        new BirAttributeLiteral { Type = new TypeNode.Fqn("System.String"),
            Value = new BirLiteral.TextLiteral("\ud800x\udfff") },
        new BirConstant { Value = new BirLiteral.BooleanLiteral(true) },
    },
};
var seenBytes = 0;
opaque.Rewrite(value => { if (value is BirOpaqueBytes) seenBytes++; return value; });
if (seenBytes != 1) throw new Exception("Opaque metadata was traversed as executable children");
using (var stream = new MemoryStream(Write(opaque)))
{
    var reader = new Utf8JsonReader(stream.ToArray());
    reader.Read();
    var copy = BirGraphJson.ReadBirAttribute(ref reader);
    Same(Encoding.UTF8.GetString(Write(opaque)), copy, "metadata argument domains");
    Same(Encoding.UTF8.GetString(Write(opaque)), BirGraphDom.ReadBirAttribute(opaque.ToDom()),
        "direct metadata frontier");
    var file = new BirFileDeclaration { Attrs = new[] { opaque } };
    Same(Encoding.UTF8.GetString(Write(file)), BirFileCodec.Decode(BirFileCodec.Encode(file)),
        "metadata MessagePack");
    if (copy.Args.Value[1] is not BirAttributeLiteral { Value.IsPresent: false }
        || copy.Args.Value[2] is not BirAttributeLiteral { Value.HasValue: true, Value.Value: BirLiteral.NullLiteral })
        throw new Exception("Missing metadata value was conflated with a null literal");
}

// A lambda's construction operands execute in the enclosing frame, but its body,
// returns and labels do not. Reuse the same witness for every deferred body form.
BirConstant FrameNumber(int value) => new() {
    Type = new TypeNode.Fqn("System.Int32"), Value = new BirLiteral.IntegerLiteral(value),
};
IBirStatement[] DeferredBody() => new IBirStatement[] {
    new BirLabel { Id = 0 },
    new BirTry { Body = new IBirStatement[] { new BirReturn { Value = FrameNumber(7) } },
        Finally = new IBirStatement[] { new BirReturn { Value = FrameNumber(8) } } },
};
IBirExpression CallerReturn() => new BirReturnExpression { Value = FrameNumber(6) };
JsonObject FindConstruction(JsonNode node, string kind)
{
    if (node is JsonObject obj)
    {
        if (obj["k"]?.GetValue<string>() == kind) return obj;
        foreach (var pair in obj)
            if (pair.Value != null && FindConstruction(pair.Value, kind) is { } found) return found;
    }
    else if (node is JsonArray values)
        foreach (var value in values)
            if (value != null && FindConstruction(value, kind) is { } found) return found;
    return null!;
}
foreach (var expression in new BirGraphNode[] {
    new BirNewSuspendLambda {
        Params = Array.Empty<BirLambdaParameter>(), Captures = Array.Empty<BirCaptureDescriptor>(),
        CapValues = new IBirExpression?[] { CallerReturn() }, Body = DeferredBody(), TypeFrame = "dense",
        _suspendDeclarationTypeArgs = new TypeNode[] { frame }, Sty = new TypeNode.Fn(true, frame, Array.Empty<TypeNode>()),
    },
    new BirInlineLambda { Params = Array.Empty<BirLambdaParameter>(), Body = DeferredBody(), Result = FrameNumber(0),
        Captures = new[] { new BirInlineCapture { Name = "value", Value = OptionalField<IBirExpression>.Of(CallerReturn()) } } },
    new BirNewClosure { Captures = new[] { CallerReturn() }, SynthClass = new BirClosureDefinition {
        Params = Array.Empty<BirLambdaParameter>(), Ret = new TypeNode.Fqn("System.Int32"), Body = DeferredBody(),
    } },
})
{
    var before = expression.ToDom();
    var body = expression is BirNewClosure ? before["synthClass"]!["body"] : before["body"];
    var source = new JsonArray(new BirLabel { Id = 0 }.ToDom(), new BirTry {
        Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)expression) } },
        Finally = new IBirStatement[] { new BirReturn { Value = FrameNumber(9) } },
    }.ToDom());
    var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
        Body = new IBirStatement[] { new BirExpressionStatement { Expr = OptionalField<IBirExpression>.Of((IBirExpression)expression) } },
    } } };
    Same(Encoding.UTF8.GetString(Write(file)), BirFileCodec.Decode(BirFileCodec.Encode(file)), "deferred frame codec");
    EscapingFinallyLowering.Apply(new[] { source }, TypeNode.Write(new TypeNode.Fqn("System.Int32")), null!, false);
    var after = FindConstruction(source, expression.Tag);
    var actualBody = expression is BirNewClosure ? after["synthClass"]!["body"] : after["body"];
    if (!JsonNode.DeepEquals(body, actualBody))
        throw new Exception($"Enclosing cleanup rewrote a deferred {expression.Tag} frame");
    var caller = expression switch {
        BirNewSuspendLambda => after["capValues"]![0],
        BirInlineLambda => after["captures"]![0]!["value"],
        _ => after["captures"]![0],
    };
    if (caller?["k"]?.GetValue<string>() != "valueBlock")
        throw new Exception($"Caller construction operand escaped cleanup routing: {expression.Tag}");
    if (expression is BirNewSuspendLambda && (after["typeFrame"]?.GetValue<string>() != "dense"
        || !JsonNode.DeepEquals(before["_suspendDeclarationTypeArgs"], after["_suspendDeclarationTypeArgs"])
        || !JsonNode.DeepEquals(before["sty"], after["sty"])))
        throw new Exception("Pre-suspend cleanup lost the declaration-to-caller generic frame");
}
var closureLoop = new BirWhile { Label = "inner", Cond = new BirConstant { Value = new BirLiteral.BooleanLiteral(false) },
    Body = new IBirStatement[] { new BirBreak { Label = "inner" } } };
var closureFrame = new BirClosureDefinition { Params = Array.Empty<BirLambdaParameter>(), Ret = new TypeNode.Fqn("System.Int32"),
    Body = new IBirStatement[] { closureLoop, new BirReturn { Value = FrameNumber(1) } } };
TypedProtectedRegions.Apply(closureFrame, null!);
if (!closureLoop.LoopId.HasValue || closureLoop.Label.IsPresent
    || closureLoop.Body.Value[0] is not BirBreak { LoopId.HasValue: true, Label.IsPresent: false })
    throw new Exception("A closure definition did not receive its own physical transfer frame");
Console.WriteLine("Typed execution frames: three deferred body forms retain private returns/labels and caller capture evaluation; generic frames/codecs and closure admission OK");

var signatureSnapshot = SourceTypeVector.ReadFrozenText("[{\"t\":\"tv\",\"scope\":\"method\",\"i\":1}]");
var phaseFacts = new BirFileDeclaration {
    Methods = new[] { new BirFunctionDeclaration { Body = new IBirStatement[] {
        new BirExpressionStatement { Expr = new BirNativeStatic {
            ResolvedMemberParams = new TypeNode[] { frame }, ResolvedMemberReturn = frame,
            _resolvedMethodTypeParams = new[] { new BirTypeParameter { Name = "T", SpecialConstraints = new[] { "class" } } },
        } },
        new BirExpressionStatement { Expr = new BirPropertyGet { _foreignDeclarationSignature = signatureSnapshot } },
        new BirExpressionStatement { Expr = new BirCallStatic { NullableWitnessCount = 2 } },
        new BirExpressionStatement { Expr = new BirNewClr { MemberSignature = new TypeNode[] { frame } } },
        new BirExpressionStatement { Expr = new BirValueBlock { Sty = frame } },
    } } },
    Types = new IBirTypeDeclaration[] { new BirClassDefinition {
        Kind = "class", ClrEvents = new[] { new BirClrEventBacking {
            K = "clrEventDecl", Name = "Changed", DelegateType = new TypeNode.Fqn("System.Action"),
        } },
    } },
};
var phaseJson = Encoding.UTF8.GetString(Write(phaseFacts));
Same(phaseJson, BirGraphJson.ReadFile(Write(phaseFacts)), "current signature/event/witness facts");
Same(phaseJson, BirFileCodec.Decode(BirFileCodec.Encode(phaseFacts)), "current phase metadata binary");
if (signatureSnapshot.Types[0] != frame)
    throw new Exception("Frozen foreign signature is not a typed declaration-frame vector");

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

var enumMethods = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
    new BirEnumDefinition { Kind = "enum", Methods = defaults.Methods.Value },
} };
if (SuspendShapeAnalyzer.Analyze(enumMethods).FunctionCount != 1)
    throw new Exception("The intermediate enum declaration lost its suspend-method inventory");

// A lowered const field still holds a BIR literal until final CIR declaration
// projection chooses its physical Constant-table value. Do not admit the final
// attribute vocabulary prematurely just because the physical field has the same key.
var constantJson = "{\"fileClass\":\"Constant\",\"fields\":[{\"name\":\"value\",\"type\":{\"t\":\"fqn\",\"name\":\"System.Int32\"},\"constant\":42}]}";
var constantRoot = BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(constantJson));
Same(constantJson, constantRoot);
if (constantRoot.Fields.Value[0].Constant.Value is not BirLiteral.IntegerLiteral { Value: 42 }
    || CirDeclarationProjection.ReadCirField(constantRoot.Fields.Value[0].ToDom()).Constant.Value is not AttributeArgument.Int32Value { Value: 42 })
    throw new Exception("An intermediate field constant was not projected from its source literal");
foreach (var (type, literal, expected) in new (string, BirLiteral, AttributeArgument)[] {
    ("System.Int64", new BirLiteral.IntegerLiteral(long.MinValue), new AttributeArgument.Int64Value(long.MinValue)),
    ("System.Single", new Float32Literal(unchecked((int)0xffc01234)), new AttributeArgument.Float32Value(unchecked((int)0xffc01234))),
    ("System.Double", new Float64Literal(long.MinValue), new AttributeArgument.Float64Value(long.MinValue)),
    ("System.String", new BirLiteral.NullLiteral(), new AttributeArgument.NullValue()),
})
{
    var field = new BirFieldDeclaration { Name = "value", Type = new TypeNode.Fqn(type), Constant = literal };
    var physical = CirDeclarationProjection.ReadCirField(field.ToDom());
    if (!expected.Equals(physical.Constant.Value))
        throw new Exception("A typed intermediate field changed literal width, bits, or null during final projection");
}

var authored = 0;
foreach (var file in Directory.EnumerateFiles("tests/ir/lowering", "*.bir*.json").Order(StringComparer.Ordinal))
{
    if (Path.GetFileName(file).StartsWith("reject-", StringComparison.Ordinal)) continue;
    var json = File.ReadAllText(file);
    Same(json, BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(json)), file);
    authored++;
}
if (authored == 0) throw new Exception("No accepted lowering inputs were checked");

var corpus = 0;
if (args.Length > 1)
{
    foreach (var file in File.ReadAllLines(args[1]).Distinct(StringComparer.Ordinal))
    {
        var json = File.ReadAllText(file);
        Same(json, BirGraphJson.ReadFile(Encoding.UTF8.GetBytes(json)), file);
        corpus++;
    }
    if (corpus == 0) throw new Exception("No actual compiler BIR was selected");
}
Console.WriteLine($"Owning BIR graph: {core} Kotlin node / {scalars} scalar samples, {authored} authored / {corpus} actual inputs; presence, structural rewrite, default freeze OK");

var cirGraphs = 0;
if (args.Length > 2)
{
    foreach (var file in File.ReadAllLines(args[2]).Distinct(StringComparer.Ordinal))
    {
        var originalCir = DeclarationJson.ReadFile(file);
        var copiedCir = CirBodyRewrite.RewriteCirFileDeclaration(originalCir, node => node);
        if (DeclarationJson.Write(originalCir) != DeclarationJson.Write(copiedCir))
            throw new Exception("Typed CIR executable rewriting changed an actual graph: " + file);
        cirGraphs++;
    }
    if (cirGraphs == 0) throw new Exception("No actual compiler CIR was selected for typed rewriting");
}
Console.WriteLine($"Typed CIR executable rewrite: {cirGraphs} actual graphs preserved declarations, frames, opaque metadata and ordered children");

static class LogicalBirValue
{
    // The maintained model, not a heuristic that parses arbitrary strings,
    // identifies the few frozen type facts whose current wire form is text.
    static readonly HashSet<string> FrozenTypeFields = JsonNode.Parse(File.ReadAllText("docs/bir-cir.schema.json"))!["$defs"]!
        .AsObject().SelectMany(definition => new[] {
            definition.Value?["properties"] as JsonObject,
            definition.Value?["x-dotkt-model"]?["loweringProperties"] as JsonObject,
        }).Where(properties => properties != null).SelectMany(properties => properties!)
        .Where(property => property.Value is JsonObject field && field["x-dotkt-kotlin-json-type"]?.GetValue<bool>() == true)
        .Select(property => property.Key).ToHashSet(StringComparer.Ordinal);

    internal static bool Equal(JsonNode? before, JsonNode? after, string? field = null)
    {
        if (field != null && FrozenTypeFields.Contains(field) && before is JsonValue a && after is JsonValue b
            && a.TryGetValue<string>(out var left) && b.TryGetValue<string>(out var right))
            return JsonNode.DeepEquals(JsonNode.Parse(left, documentOptions: BirJson.DocOptions),
                JsonNode.Parse(right, documentOptions: BirJson.DocOptions));
        if (before is JsonObject first && after is JsonObject second)
            return first.Count == second.Count && first.All(pair => second.TryGetPropertyValue(pair.Key, out var value)
                && Equal(pair.Value, value, pair.Key));
        if (before is JsonArray firstArray && after is JsonArray secondArray)
            return firstArray.Count == secondArray.Count && firstArray.Zip(secondArray).All(pair => Equal(pair.First, pair.Second));
        return JsonNode.DeepEquals(before, after);
    }
}

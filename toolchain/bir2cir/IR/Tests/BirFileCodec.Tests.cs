using System.Buffers;
using System.Text;
using System.Text.Json;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using MessagePack;

static partial class BirFileCodec
{
    internal static void SelfTest()
    {
        byte[] Json(BirFileDeclaration file) => BirCarrier.EncodeBody(BirCarrier.JsonV1, file, (writer, value) => value.WriteJson(writer));
        BirFileDeclaration Roundtrip(BirFileDeclaration file)
        {
            var bytes = Encode(file);
            var result = Decode(bytes);
            if (!Json(file).SequenceEqual(Json(result)) || !bytes.SequenceEqual(Encode(result)))
                throw new InvalidOperationException("BIR MessagePack changed a value, presence or deterministic ordering");
            return result;
        }
        var absent = Roundtrip(new BirFileDeclaration { FileClass = "Codec" });
        var explicitSlots = Roundtrip(new BirFileDeclaration { FileClass = "Codec", Fields = DotKt.Cir.OptionalField<BirFieldDeclaration[]>.Null,
            Methods = Array.Empty<BirFunctionDeclaration>() });
        if (absent.Fields.IsPresent || !explicitSlots.Fields.IsNull || explicitSlots.Methods.Value.Length != 0)
            throw new InvalidOperationException("BIR binary file conflated absent, null and empty fields");

        var frame = new TypeNode.Tv("method", 3);
        var opaque = new byte[] { 0, 255, 0x81, 0xa1, 0x6b, 0xc0 };
        var call = new BirCallInline { DeclarationId = "exact-id", TypeArgs = new[] { frame }, RetType = frame,
            Args = new IBirExpression[] { null, new BirLocal { Name = "input", Sty = frame }, new BirLocal { Name = "input", Sty = frame } } };
        var literals = new BirLiteral[] {
            new BirLiteral.NullLiteral(), new BirLiteral.BooleanLiteral(false), new BirLiteral.IntegerLiteral(long.MinValue),
            new BirLiteral.IntegerLiteral(long.MaxValue), new BirLiteral.UnsignedIntegerLiteral(ulong.MaxValue),
            new BirLiteral.RealLiteral(-0.0), new BirLiteral.RealLiteral(1.25),
            new BirLiteral.TextLiteral("zero\0日本\ud800|\udfff"),
            new Float32Literal(unchecked((int)0xffa01234)), new Float32Literal(int.MinValue),
            new Float64Literal(unchecked((long)0x7ff0000000001234)), new Float64Literal(long.MinValue),
        };
        var input = new BirFileDeclaration { FileClass = "Codec", Methods = new[] { new BirFunctionDeclaration {
            Name = "generic", DeclarationId = "method-id", TypeParams = new[] { new BirTypeParameter {
                Name = "T", Constraints = new[] { new TypeNode.Fqn("Box", new[] { frame }) }, Reified = true,
            } }, Params = Array.Empty<BirParameter>(), Ret = frame,
            Body = literals.Select(value => (IBirStatement)new BirExpressionStatement {
                Expr = new BirConstant { Type = new TypeNode.Fqn("kotlin.Any"), Value = value },
            }).Append(new BirExpressionStatement { Expr = call }).ToArray(),
            Attrs = new[] { new BirAttribute { Attr = new TypeNode.Fqn("Opaque"), Args = new IBirAttributeValue[] { new BirOpaqueBytes { Bytes = opaque } } } },
        } } };
        // This current phase declaration crosses the first presence-mask word.
        input.Methods.Value[0].RetSuspendFnType = new TypeNode.Fn(true, frame, Array.Empty<TypeNode>());
        var decoded = Roundtrip(input);
        if (!decoded.StructurallyEquals(BirGraphJson.ReadFile(Json(input))))
            throw new InvalidOperationException("Wide declaration fields diverged between JSON and MessagePack");
        var decodedCall = (BirCallInline)((BirExpressionStatement)decoded.Methods.Value[0].Body.Value[^1]).Expr.Value;
        if (decodedCall.Args.Value[0] != null || decodedCall.RetType.Value != frame
            || decodedCall.DeclarationId.Value != "exact-id" || ReferenceEquals(decodedCall.Args.Value, call.Args.Value)
            || ReferenceEquals(decodedCall.Args.Value[1], decodedCall.Args.Value[2]))
            throw new InvalidOperationException("Binary BIR changed a null argument/frame/ID or shared executable identity");
        decodedCall.Args.Value[1] = new BirLocal { Name = "changed" };
        if (((BirLocal)call.Args.Value[1]).Name.Value != "input" || ((BirLocal)decodedCall.Args.Value[2]).Name.Value != "input")
            throw new InvalidOperationException("Binary executable mutation escaped its owning occurrence");

        // Every current executable kind has a concrete codec, without a dynamic IR bag.
        foreach (var type in typeof(BirGraphNode).Assembly.GetTypes().Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(BirGraphNode))))
        {
            var node = (BirGraphNode)Activator.CreateInstance(type, nonPublic: true);
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            BirGraphMessagePack.WriteNode(ref writer, node, 0);
            writer.Flush();
            var reader = new MessagePackReader(buffer.WrittenMemory);
            var restored = BirGraphMessagePack.ReadNode(ref reader);
            if (!reader.End || restored.GetType() != type)
                throw new InvalidOperationException("Missing BIR binary kind: " + node.Tag);
        }

        var valid = Encode(input);
        foreach (var malformed in new[] { valid[..^1], valid.Concat(new byte[] { 0 }).ToArray(), "DOTKT\0CIR\0MSGPACK\0"u8.ToArray() })
        {
            var failed = false;
            try { Decode(malformed); }
            catch (Exception error) when (error is FormatException or MessagePackSerializationException or EndOfStreamException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Malformed current BIR framing was accepted");
        }
        foreach (var fields in new[] {
            new[] { ("k", "const"), ("unknownField", "value") },
            new[] { ("k", "const"), ("k", "const") },
            new[] { ("k", "unknownKind") },
        })
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteMapHeader(fields.Length);
            foreach (var (key, value) in fields) { writer.Write(key); writer.Write(value); }
            writer.Flush();
            var reader = new MessagePackReader(buffer.WrittenMemory);
            var failed = false;
            try { BirGraphMessagePack.ReadNode(ref reader); }
            catch (FormatException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Malformed current BIR fields/kind were accepted");
        }
        var duplicateWideJson = new Utf8JsonReader("{\"retSuspendFnType\":null,\"retSuspendFnType\":null}"u8);
        duplicateWideJson.Read();
        var rejectedWide = false;
        try { BirGraphJson.ReadBirFunctionDeclaration(ref duplicateWideJson); }
        catch (FormatException) { rejectedWide = true; }
        if (!rejectedWide) throw new InvalidOperationException("Duplicate current wide JSON declaration field was accepted");
        var wideBuffer = new ArrayBufferWriter<byte>();
        var wideWriter = new MessagePackWriter(wideBuffer);
        wideWriter.WriteMapHeader(2);
        for (var index = 0; index < 2; index++) { wideWriter.Write("retSuspendFnType"); wideWriter.WriteNil(); }
        wideWriter.Flush();
        var duplicateWideBinary = new MessagePackReader(wideBuffer.WrittenMemory);
        rejectedWide = false;
        try { BirGraphMessagePack.ReadBirFunctionDeclaration(ref duplicateWideBinary); }
        catch (FormatException) { rejectedWide = true; }
        if (!rejectedWide) throw new InvalidOperationException("Duplicate current wide binary declaration field was accepted");
        Console.WriteLine("[BIR MessagePack] self-test OK (all kinds, numeric bits/UTF16, presence, exact frames/IDs, opaque bytes, independent executable mutation)");
    }
}

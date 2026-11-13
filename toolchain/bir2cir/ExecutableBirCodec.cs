using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using MessagePack;

// Executable carriers share the file codecs' owning phase vocabulary and the
// declared codec dispatch. The remaining DOM owners use direct neighbours,
// never an IR-text/binary/IR-text bridge. Those neighbours retire with the owners.
static class ExecutableBirCodec
{
    internal const string Codec = BirCarrier.MessagePackV1;

    internal static byte[] EncodeExpression(string codec, IBirExpression body) => BirCarrier.EncodeBody(
        codec, body, (writer, value) => ((BirGraphValue)value).WriteJson(writer),
        (ref MessagePackWriter writer, IBirExpression value) => BirGraphMessagePack.WriteBirExpression(ref writer, value, 0));

    internal static IBirExpression ReadExpression(string codec, byte[] content) => BirCarrier.DecodeBody(
        codec, content, BirGraphJson.ReadBirExpression, BirGraphMessagePack.ReadBirExpression);

    internal static byte[] EncodeInline(string codec, BirInlinePayload body) => BirCarrier.EncodeBody(
        codec, body, (writer, value) => value.WriteJson(writer),
        (ref MessagePackWriter writer, BirInlinePayload value) => BirGraphMessagePack.WriteBirInlinePayload(ref writer, value, 0));

    internal static BirInlinePayload ReadInline(string codec, byte[] content) => BirCarrier.DecodeBody(
        codec, content, BirGraphJson.ReadBirInlinePayload, BirGraphMessagePack.ReadBirInlinePayload);

    internal static byte[] EncodeExpressionDom(string codec, JsonNode body)
        => EncodeExpression(codec, BirGraphDom.ReadBirExpression(body));

    internal static JsonObject ReadExpressionDom(string codec, byte[] content)
        => ((BirGraphValue)ReadExpression(codec, content)).ToDom();

    internal static byte[] EncodeInlineDom(string codec, JsonObject body)
        => EncodeInline(codec, BirGraphDom.ReadBirInlinePayload(body));

    internal static JsonObject ReadInlineDom(string codec, byte[] content)
        => ReadInline(codec, content).ToDom();

    // Conflict checks are not graph hand-offs. Compare the canonical logical
    // encoding only when duplicate declarations actually need to be compared.
    internal static bool SameInline(BirInlinePayload left, BirInlinePayload right) => ReferenceEquals(left, right)
        || EncodeInline(Codec, left).AsSpan().SequenceEqual(EncodeInline(Codec, right));

    internal static void SelfTest()
    {
        var text = new TypeNode.Fqn("kotlin.String");
        var frame = new TypeNode.Tv("method", 1);
        var suspendShape = new TypeNode.Fn(true, new TypeNode.Nullable(frame), new TypeNode[] { frame });
        var sourceDefault = new BirValueBlock {
            Type = text,
            Stmts = new IBirStatement[] { new BirSourcePoint { Span = new BirSourceSpan { F = "producer.kt", L = 3 } } },
            Result = new BirConstant { Type = text, Value = new BirLiteral.TextLiteral("a\ud800b\udfff") },
        };
        var source = new BirInlinePayload {
            V = 1, Fqn = "Sample.Owner.read", Owner = "Sample.Owner", FileClass = OptionalField<string>.Null,
            Recv = "none", Static = true, DeclarationId = "exact-declaration",
            TypeParams = new[] { new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "T", Reified = true } },
            Params = new[] { new BirParameter { Name = "value", Type = frame },
                new BirParameter { Name = "text", Type = text, Attrs = new[] { DefaultBirCarrier.Create(1, sourceDefault) } },
                new BirParameter { Name = "action", Type = suspendShape, SuspendFnTypePre = suspendShape } },
            Ret = frame, Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "value", Sty = frame } } },
            Lifted = new[] { new BirFunctionDeclaration { Name = "lifted", Static = true, Generated = true,
                NullableRepresentationFrame = new NullableRepresentationFrame(2, new[] { 1 },
                    new[] { 3, 0, 2, 1, 4 }, new[] { 0 }, new[] { 1 }),
                Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Double"),
                Body = new IBirStatement[] { new BirReturn { Value = new BirConstant {
                    Type = new TypeNode.Fqn("kotlin.Double"), Value = new Float64Literal(unchecked((long)0xfff800000000002aUL)) } } } },
                new BirFunctionDeclaration { Name = "readAction", Static = true, Generated = true,
                    TypeParams = new[] { new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "T" } },
                    Params = new[] { new BirParameter { Name = "action", Type = suspendShape, SuspendFnTypePre = suspendShape } },
                    Ret = suspendShape, RetSuspendFnTypePre = suspendShape,
                    Body = new IBirStatement[] { new BirReturn { Value = new BirLocal { Name = "action", Sty = suspendShape } } } } },
        };
        foreach (var codec in new[] { BirCarrier.JsonV1, BirCarrier.MessagePackV1 })
        {
            var restored = ReadInline(codec, EncodeInline(codec, source));
            if (!JsonNode.DeepEquals(restored.ToDom(), source.ToDom()) || !SameInline(restored, source)
                || restored.Ret.Value != frame || !restored.FileClass.IsNull)
                throw new InvalidOperationException("Executable carrier lost frames, values or presence");
            if (!restored.Lifted.Value[0].NullableRepresentationFrame.Value.PhysicalOrder
                .SequenceEqual(new[] { 3, 0, 2, 1, 4 }))
                throw new InvalidOperationException("Executable carrier lost its lifted method frame");
            if (!suspendShape.Equals(restored.Params.Value[2].SuspendFnTypePre.Value)
                || !suspendShape.Equals(restored.Lifted.Value[1].RetSuspendFnTypePre.Value)
                || !suspendShape.Equals(restored.Lifted.Value[1].Params.Value[0].SuspendFnTypePre.Value))
                throw new InvalidOperationException("Executable carrier lost a pre-erasure suspend signature");
            var first = restored.ToDom();
            var second = restored.ToDom();
            first["body"][0]["value"]["name"] = "changed";
            if (second["body"][0]["value"]["name"].GetValue<string>() != "value")
                throw new InvalidOperationException("Executable carrier materializations share mutable nodes");
            var defaultBody = (BirGraphValue)ReadExpression(codec, EncodeExpression(codec, sourceDefault));
            if (!JsonNode.DeepEquals(defaultBody.ToDom(), sourceDefault.ToDom()))
                throw new InvalidOperationException("Default codec changed UTF16 or source facts");
        }
        var preparedCall = new BirCallInline {
            Recvs = new BirInlineReceivers { DispatchOwnerTypeArgs = new TypeNode[] { frame } },
            PreparedDefaults = new IBirExpression[] { null, new BirLocal { Name = "earlier", Sty = frame } },
        };
        var captured = new BirNewClosure {
            ClosureType = new TypeNode.Fqn("Captured"), Method = "invoke", Captures = Array.Empty<IBirExpression>(),
            FuncType = new TypeNode.Fn(false, text, Array.Empty<TypeNode>()),
            SynthClass = new BirClosureDefinition {
                Name = "Captured", OuterTypeParamCount = 2,
                TypeParams = new[] { new BirTypeParameter { Name = "O" }, new BirTypeParameter { Name = "T" } },
                Fields = Array.Empty<BirCaptureDescriptor>(), Params = Array.Empty<BirLambdaParameter>(), Ret = text,
                Body = new IBirStatement[] { new BirReturn { Value = new BirConstant {
                    Type = text, Value = new BirLiteral.TextLiteral("captured") } } },
            },
        };
        foreach (var codec in new[] { BirCarrier.JsonV1, BirCarrier.MessagePackV1 })
        {
            var prepared = (BirCallInline)ReadExpression(codec,
                EncodeExpressionDom(codec, preparedCall.ToDom()));
            if (prepared.Recvs.Value.DispatchOwnerTypeArgs.Value[0] != frame
                || prepared.PreparedDefaults.Value.Length != 2 || prepared.PreparedDefaults.Value[0] != null
                || prepared.PreparedDefaults.Value[1] is not BirLocal { Name.Value: "earlier" })
                throw new InvalidOperationException("Executable carrier lost prepared default or dispatch owner facts");
            var capture = (BirNewClosure)ReadExpression(codec, EncodeExpressionDom(codec, captured.ToDom()));
            if (capture.SynthClass.Value.OuterTypeParamCount.Value != 2
                || capture.SynthClass.Value.TypeParams.Value.Length != 2)
                throw new InvalidOperationException("Executable carrier lost a closure's enclosing generic frame");
        }
        var outgoing = source.ToDom();
        MaterializedBirPayload.HideSourceSpans(outgoing);
        var hidden = ReadInlineDom(Codec, EncodeInlineDom(Codec, outgoing));
        var defaultAttribute = hidden["params"][1]["attrs"][0].AsObject();
        if (DefaultBirCarrier.Decode(defaultAttribute)["stmts"][0]["span"] != null
            || sourceDefault.Stmts.Value[0] is not BirSourcePoint { Span.HasValue: true }
            || defaultAttribute["args"][1]["value"].GetValue<string>() != Codec)
            throw new InvalidOperationException("Executable carrier rewrote source ownership or lost nested codec identity");
        foreach (var literal in new BirLiteral[] { new Float32Literal(unchecked((int)0x80000000)),
                     new Float64Literal(unchecked((long)0x7ff8000000000042UL)),
                     new BirLiteral.IntegerLiteral(long.MinValue), new BirLiteral.UnsignedIntegerLiteral(ulong.MaxValue) })
        {
            var expression = new BirConstant { Type = new TypeNode.Fqn("kotlin.Any"), Value = literal };
            var restored = (BirConstant)ReadExpression(Codec, EncodeExpression(Codec, expression));
            if (restored.Value.Value != literal)
                throw new InvalidOperationException("Executable binary carrier changed scalar bits");
        }
        Console.WriteLine("[executable BIR codecs] self-test OK (inline/default JSON+MessagePack, frames, UTF16/bits, opaque nested defaults, independent materializations)");
    }
}

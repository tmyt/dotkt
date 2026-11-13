using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class OpaqueCarrierTypeBinding
{
    internal static void SelfTest()
    {
        var source = new TypeNode.Fqn("Carrier.Outer.Inner", new TypeNode[] {
            new TypeNode.Tv("method", 1), new TypeNode.Tv("method", 0),
        }) { Classifier = new TypeNode.SourceClassifier("Carrier", new[] { "Outer" }, "Inner") };
        var physical = source with { Name = "Carrier.Outer`1+Inner`2" };
        const string closureName = "Carrier.Outer.Closure";
        const string samName = "Carrier.Outer.Sam";
        var localNames = new Dictionary<string, string>(StringComparer.Ordinal) {
            [source.Name] = physical.Name,
            [closureName] = "Carrier.Outer`1+Closure",
            [samName] = "Carrier.Outer`1+Sam",
        };
        var defaultSource = new BirValueBlock {
            Type = source,
            Stmts = new IBirStatement[] { new BirSourcePoint { Span = new BirSourceSpan { F = "producer.kt", L = 12 } } },
            Result = new BirClassReference { Type = source },
        };
        var parameter = new BirParameter {
            Name = "input", Type = source, KotlinType = source,
            Attrs = new[] { DefaultBirCarrier.Create(0, defaultSource) },
        };
        var closure = new BirNewClosure {
            ClosureType = new TypeNode.Fqn(closureName), Method = "invoke", Captures = Array.Empty<IBirExpression>(),
            FuncType = new TypeNode.Fn(false, source, Array.Empty<TypeNode>()),
            SynthClass = new BirClosureDefinition {
                Name = closureName, Fields = new[] { new BirCaptureDescriptor { Name = "captured", Type = source } },
                Params = Array.Empty<BirLambdaParameter>(), Ret = source,
                Body = new IBirStatement[] { new BirReturn { Value = new BirClassReference { Type = new TypeNode.Fqn(closureName) } } },
            },
        };
        var sam = new BirNewSam {
            SamType = new TypeNode.Fqn(samName), Captures = Array.Empty<IBirExpression>(),
            SynthClass = new BirClassDefinition { Name = samName, Kind = "class",
                Fields = new[] { new BirFieldDeclaration { Name = "captured", Type = source } },
                Methods = Array.Empty<BirFunctionDeclaration>(),
            },
        };
        var inline = new BirInlinePayload {
            V = 1, Fqn = "Carrier.File.inline", Owner = "Carrier.File", Static = true,
            FileClass = OptionalField<string>.Null, Recv = "none",
            TypeParams = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { source } } },
            Params = new[] { parameter }, Ret = source, RetSuspendFnTypePre = source,
            Body = new IBirStatement[] {
                new BirVariable { Name = "inner", Type = source },
                new BirExpressionStatement { Expr = closure }, new BirExpressionStatement { Expr = sam },
            },
            Lifted = Array.Empty<BirFunctionDeclaration>(),
        };
        var untouched = inline.DeepClone();
        foreach (var codec in new[] { BirCarrier.JsonV1, BirCarrier.MessagePackV1 })
        {
            var occurrence = ExecutableBirCodec.ReadInline(codec, ExecutableBirCodec.EncodeInline(codec, inline));
            var parameters = occurrence.Params.Value;
            var actualParameter = parameters[0];
            var beforeParameter = actualParameter.DeepClone();
            BindExecutablePayload(occurrence, localNames);
            Require(ReferenceEquals(parameters, occurrence.Params.Value)
                && ReferenceEquals(actualParameter, occurrence.Params.Value[0])
                && actualParameter.StructurallyEquals(beforeParameter),
                "root inline parameter vector or attached source/default facts changed");
            var actualClosure = (BirNewClosure)((BirExpressionStatement)occurrence.Body.Value[1]).Expr.Value;
            var actualSam = (BirNewSam)((BirExpressionStatement)occurrence.Body.Value[2]).Expr.Value;
            Require(TypeNodeDataEquality.Equal(occurrence.Ret.Value, physical)
                && TypeNodeDataEquality.Equal(((BirVariable)occurrence.Body.Value[0]).Type.Value, physical)
                && TypeNodeDataEquality.Equal(occurrence.TypeParams.Value[0].Constraints.Value[0], physical)
                && TypeNodeDataEquality.Equal(actualClosure.SynthClass.Value.Fields.Value[0].Type.Value, physical)
                && TypeNodeDataEquality.Equal(actualSam.SynthClass.Value.Fields.Value[0].Type.Value, physical)
                && actualClosure.ClosureType.Value is TypeNode.Fqn { Name: closureName }
                && actualSam.SamType.Value is TypeNode.Fqn { Name: samName }
                && actualClosure.SynthClass.Value.Name.Value == closureName
                && actualSam.SynthClass.Value.Name.Value == samName
                && TypeNodeDataEquality.Equal(occurrence.RetSuspendFnTypePre.Value, source)
                && occurrence.FileClass.IsNull,
                "typed carrier lost physical nested binding, carried synthetic identity, frozen facts or presence");
            Require(ExecutableBirCodec.ReadInline(codec, ExecutableBirCodec.EncodeInline(codec, occurrence))
                .StructurallyEquals(occurrence), "bound executable payload did not retain its declared codec content");

            // Test the actual scalar DOM-attribute bridge in both supported
            // current codecs, without projecting a fabricated graph header.
            var attribute = DefaultBirCarrier.Create(3, defaultSource).ToDom();
            attribute["args"][1]["value"] = codec;
            attribute["args"][2]["bytes"] = Convert.ToBase64String(ExecutableBirCodec.EncodeExpression(codec, defaultSource));
            var arguments = attribute["args"];
            DefaultBirCarrier.RewriteTyped(attribute, expression => {
                BindExecutablePayload((BirGraphValue)expression, localNames);
                return expression;
            });
            MaterializedBirPayload.HideDefaultSourceSpans(attribute);
            var restoredDefault = (BirValueBlock)DefaultBirCarrier.DecodeTyped(attribute);
            Require(ReferenceEquals(arguments, attribute["args"])
                && attribute["args"][0]["value"].GetValue<int>() == 3
                && attribute["args"][1]["value"].GetValue<string>() == codec
                && TypeNodeDataEquality.Equal(restoredDefault.Type.Value, physical)
                && restoredDefault.Stmts.Value[0] is BirSourcePoint { Span.IsPresent: false }
                && defaultSource.Stmts.Value[0] is BirSourcePoint { Span.HasValue: true },
                "default bridge changed its index/codec, source ownership or outgoing source-span rule");
        }
        Require(inline.StructurallyEquals(untouched), "a materialized carrier mutated its producer-owned source");

        // Exercise the actual outer declaration traversal, not just a typed
        // helper: inline/default bytes change while source metadata stays on its
        // existing JSON leaf path and ordinary bodies remain unmodified.
        var root = new BirFileDeclaration { FileClass = "Carrier.File", Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Carrier.Outer", Kind = "class", TypeParams = new[] { new BirTypeParameter { Name = "O" } } },
            new BirClassDefinition { Name = source.Name, Kind = "class", NestedIn = "Carrier.Outer",
                TypeParams = new[] { new BirTypeParameter { Name = "I" }, new BirTypeParameter { Name = "N" } } },
        }, Methods = new[] { new BirFunctionDeclaration { Name = "ordinary", Ret = source } } }.ToDom();
        root["inlineBir"] = Convert.ToBase64String(ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, inline));
        root["suspendResult"] = TypeNode.ToJson(source);
        var defaultAttribute = DefaultBirCarrier.Create(0, defaultSource).ToDom();
        root["attrs"] = new JsonArray(defaultAttribute);
        ApplyAll(new JsonNode[] { root }, ReferenceMetadataIndex.Build(Array.Empty<string>()));
        var outgoing = ExecutableBirCodec.ReadInline(ExecutableBirCodec.Codec, Convert.FromBase64String(root["inlineBir"].GetValue<string>()));
        Require(TypeNodeDataEquality.Equal(outgoing.Ret.Value, physical)
            && outgoing.Params.Value[0].StructurallyEquals(parameter)
            && TypeNodeDataEquality.Equal(TypeNode.Parse(root["suspendResult"].GetValue<string>()), physical)
            && TypeNodeDataEquality.Equal(((BirValueBlock)DefaultBirCarrier.DecodeTyped(defaultAttribute)).Type.Value, physical)
            && TypeNodeDataEquality.Equal(TypeJson.Read(root["methods"][0]["ret"]), source),
            "outer carrier owner did not use typed payload binding or touched the live source declaration");
        Console.WriteLine("[typed opaque executable binding] self-test OK (inline/default codecs, parameter vectors, carried identities, spans and scalar boundary)");
    }

    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Typed opaque carrier binding: " + message);
    }
}

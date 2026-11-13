using System;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// kotc states the default expression and its Kotlin argument position. The CLR layer freezes that source fact
// before ordinary walkers can lower it, and owns the physical compiler-metadata attribute just as for inline BIR.
static class DefaultBirCarrier
{
    internal const string AttributeName = "DotKt.Runtime.CompilerServices.KotlinDefaultAttribute";

    internal static BirFileDeclaration FreezeSourceDefaults(BirFileDeclaration root)
    {
        return (BirFileDeclaration)root.Rewrite(node =>
        {
            if (node is not BirParameter { DefaultBir.HasValue: true } parameter) return node;
            var source = parameter.DefaultBir.Value;
            return parameter with {
                DefaultBir = default,
                Attrs = parameter.Attrs.OrEmpty().Append(Create(source.Index.Value, (BirGraphValue)source.Body.Value)).ToArray(),
            };
        });
    }

    internal static BirAttribute Create(int index, BirGraphValue body) => new()
    {
        Attr = new TypeNode.Fqn(AttributeName),
        ArgTypes = new TypeNode[] { new TypeNode.Fqn("System.Int32"), new TypeNode.Fqn("System.String"),
            new TypeNode.Array(new TypeNode.Fqn("System.Byte")) },
        Args = new IBirAttributeValue[] {
            new BirConstant { Type = new TypeNode.Fqn("System.Int32"), Value = new BirLiteral.IntegerLiteral(index) },
            new BirConstant { Type = new TypeNode.Fqn("System.String"), Value = new BirLiteral.TextLiteral(ExecutableBirCodec.Codec) },
            new BirOpaqueBytes { Bytes = ExecutableBirCodec.EncodeExpression(ExecutableBirCodec.Codec, (IBirExpression)body) },
        },
    };

    internal static bool IsCarrier(JsonObject node) => TypeJson.OwnerName(node["attr"]) == AttributeName;

    internal static IBirExpression Decode(BirAttribute attribute)
    {
        var arguments = attribute.Args.Value;
        var codec = ((BirLiteral.TextLiteral)((BirConstant)arguments[1]).Value.Value).Value;
        return ExecutableBirCodec.ReadExpression(codec, ((BirOpaqueBytes)arguments[2]).Bytes.Value);
    }

    internal static IBirExpression OfParameter(BirParameter parameter) => parameter.Attrs.OrEmpty()
        .Where(attribute => attribute.Attr.OrDefault() is TypeNode.Fqn { Name: AttributeName })
        .Select(Decode).SingleOrDefault();

    internal static JsonNode Decode(JsonObject attribute) => ExecutableBirCodec.ReadExpressionDom(
        attribute["args"][1]["value"].GetValue<string>(),
        Convert.FromBase64String(attribute["args"][2]["bytes"].GetValue<string>()));

    internal static void Rewrite(JsonObject attribute, Action<JsonNode> rewrite)
    {
        var payload = Decode(attribute);
        rewrite(payload);
        var args = attribute["args"];
        args[2]["bytes"] = Convert.ToBase64String(ExecutableBirCodec.EncodeExpressionDom(args[1]["value"].GetValue<string>(), payload));
    }

    internal static void Rewrite(BirAttribute attribute, Func<IBirExpression, IBirExpression> rewrite)
    {
        var arguments = attribute.Args.Value;
        var codec = ((BirLiteral.TextLiteral)((BirConstant)arguments[1]).Value.Value).Value;
        var payload = rewrite(Decode(attribute));
        ((BirOpaqueBytes)arguments[2]).Bytes = ExecutableBirCodec.EncodeExpression(codec, payload);
    }

    internal static JsonNode OfParameter(JsonObject parameter) => parameter["attrs"] is JsonArray attrs
        ? attrs.OfType<JsonObject>().Where(IsCarrier).Select(Decode).SingleOrDefault() : null;

    internal static void SelfTest()
    {
        var body = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirSourcePoint { Span = new BirSourceSpan { F = "default.kt", L = 7, C = 1, El = 7, Ec = 2 } } },
            Result = new BirConstant { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(42) },
        };
        var source = new BirParameter { Name = "value", Type = new TypeNode.Fqn("kotlin.Int"),
            DefaultBir = new BirDefaultBody { Index = 2, Body = body } };
        var input = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Params = new[] { source } } } };
        var frozenParameter = FreezeSourceDefaults(input).Methods.Value[0].Params.Value[0];
        if (OfParameter(frozenParameter) is not BirValueBlock {
                Result.Value: BirConstant { Value.Value: BirLiteral.IntegerLiteral { Value: 42 } },
                Stmts.Value: [BirSourcePoint { Span.Value.L.Value: 7 }] })
            throw new InvalidOperationException("Typed default carrier decoding lost its expression or source facts");
        var frozen = frozenParameter.ToDom();
        if (frozen["defaultBir"] != null || !JsonNode.DeepEquals(OfParameter(frozen), body.ToDom()) || !source.DefaultBir.HasValue)
            throw new InvalidOperationException("Default BIR source fact did not round-trip through the shared codec");
        var attribute = (JsonObject)frozen["attrs"][0];
        if (TypeJson.OwnerName(attribute["argTypes"][0]) != "System.Int32"
            || TypeJson.OwnerName(attribute["argTypes"][1]) != "System.String")
            throw new InvalidOperationException("Default carrier must name its exact physical constructor slots");
        MaterializedBirPayload.HideSourceSpans(attribute);
        if (Decode(attribute)["stmts"][0]["span"] != null || body.Stmts.Value[0] is not BirSourcePoint { Span.HasValue: true }
            || attribute["args"][0]["value"].GetValue<int>() != 2)
            throw new InvalidOperationException("Default carrier rewriting changed source ownership or argument position");
    }
}

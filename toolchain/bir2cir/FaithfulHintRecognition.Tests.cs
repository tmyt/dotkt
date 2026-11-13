using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class FaithfulHintRecognition
{
    internal static void TypedSelfTest()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var variable = new TypeNode.Tv("method", 0);
        var text = new TypeNode.Fqn("kotlin.String");
        var any = new TypeNode.Fqn("kotlin.Any");
        var anyN = new TypeNode.Nullable(any);
        var list = new TypeNode.Fqn("kotlin.collections.List", new TypeNode[] { variable });
        var map = new TypeNode.Fqn("kotlin.collections.Map", new TypeNode[] { text, variable });
        var star = new TypeNode.Fqn(map.Name, new TypeNode[] { anyN, anyN });
        BirLocal Local(string name) => new() { Name = name };
        BirExpressionStatement Statement(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var source = new BirFunctionDeclaration {
            Name = "recognition", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Params = new[] {
                new BirParameter { Name = "input", Type = list }, new BirParameter { Name = "map", Type = map },
                new BirParameter { Name = "nullable", Type = new TypeNode.Nullable(text) }, new BirParameter { Name = "opaque", Type = any },
                new BirParameter { Name = "bytes", Type = new TypeNode.Fqn("kotlin.UByteArray") },
            },
            Body = new IBirStatement[] {
                new BirVariable { Name = "value", Type = list, Init = Local("input") },
                Statement(new BirConcat { Parts = new IBirExpression[] {
                    new BirCast { Type = any, E = Local("value") }, Local("nullable"),
                } }),
                Statement(new BirObjectMethod { Method = "ToString", Recv = Local("map") }),
                Statement(new BirCallStatic { Method = "println", Args = new IBirExpression[] {
                    new BirCast { Type = star, E = Local("opaque") }, Local("value"),
                } }),
                Statement(new BirCallInstance { OwnerType = new TypeNode.Fqn("kotlin.Double"), Method = "compareTo",
                    Recv = new BirConstant { Type = new TypeNode.Fqn("kotlin.Double"), Value = new BirLiteral.RealLiteral(-0.0) },
                    Args = new IBirExpression[] { new BirConstant { Type = new TypeNode.Fqn("kotlin.Double"), Value = new BirLiteral.RealLiteral(0.0) } } }),
                Statement(new BirCallStatic { Method = "toByteArray", Args = new IBirExpression[] { Local("bytes") } }),
            },
        };
        var expected = source.ToDom();
        var actual = (BirFunctionDeclaration)source.DeepClone();
        BirCallStatic Bind(BirCallStatic call) => call with { DeclarationId = "helper:" + call.Method.Value };
        FaithfulHints.WithHelperBinding(Bind, () => Apply(expected, localTopLevelFns: names));
        FaithfulHints.WithHelperBinding(Bind, () => actual = (BirFunctionDeclaration)Apply(actual, names));
        if (!JsonNode.DeepEquals(expected, actual.ToDom()))
            throw new InvalidOperationException("Owning recognition changed lexical source types, rendering, or selected helper binding");
        var concat = (BirConcat)((BirExpressionStatement)actual.Body.Value[1]).Expr.Value;
        if (concat.Parts.Value[0] is not BirCallStatic { TypeArgs.Value: [TypeNode.Tv { Scope: "method", I: 0 }] }
            || concat.Parts.Value[1] is not BirCallStatic { Method.Value: "toString" })
            throw new InvalidOperationException("Owning rendering lost the caller frame or nullable stringifier");
        var operand = new BirCallStatic { Method = "sideEffect", Args = Array.Empty<IBirExpression>() };
        var mapRender = FaithfulHints.CollToString((IBirExpression)operand, FaithfulHints.CollKind.Map, map.Args);
        if (!ReferenceEquals(mapRender.Args.Value[0], operand) || mapRender.Args.Value.Length != 2
            || mapRender.Args.Value[1] is not BirCallStatic { Method.Value: "clrMapViewPreference" })
            throw new InvalidOperationException("Owning map rendering duplicated its operand or omitted the selected source view");
        foreach (var owner in new[] {
            map,
            new TypeNode.Fqn("kotlin.collections.MutableMap", new TypeNode[] { new TypeNode.Projection("in", text), variable }),
            new TypeNode.Fqn(map.Name, new TypeNode[] { new TypeNode.Array(new TypeNode.Star()), variable }),
            new TypeNode.Fqn(map.Name, new TypeNode[] { new TypeNode.Star(), new TypeNode.Star() }),
        })
            foreach (var phase in new[] { MemberCallSubstitution.CollectionOperationPhase.Source, MemberCallSubstitution.CollectionOperationPhase.Physical })
            {
                var dom = MemberCallSubstitution.StaticMapPreference(owner, null, phase);
                var typed = (BirGraphValue)MemberCallSubstitution.StaticMapPreferenceValue(owner, null, phase);
                if (!JsonNode.DeepEquals(dom, typed.ToDom()))
                    throw new InvalidOperationException("Typed Map view preference disagrees with the shared source/physical plan");
            }
        var boxedNullable = new BirCast { Type = any, E = Local("later") };
        var resultRegion = new BirValueBlock {
            Stmts = new IBirStatement[] { new BirVariable { Name = "later", Type = new TypeNode.Nullable(text) } },
            Result = new BirConcat { Parts = new IBirExpression[] { boxedNullable } },
        };
        var renderedRegion = (BirValueBlock)Apply(resultRegion, names);
        if (renderedRegion.Result.Value is not BirConcat { Parts.HasValue: true } resultConcat
            || resultConcat.Parts.Value.Single() is not BirCallStatic { Method.Value: "toString", Args.HasValue: true } renderer
            || !ReferenceEquals(renderer.Args.Value.Single(), boxedNullable))
            throw new InvalidOperationException("Result rendering lost its local's nullable type or stripped the required Any cast");
        BirCallStatic Reinterpret() => new() { Method = "toByteArray", Args = new IBirExpression[] {
            new BirLocal { Name = "bytes", Sty = new TypeNode.Fqn("kotlin.UByteArray") },
        } };
        var shadowed = Reinterpret();
        if (!ReferenceEquals(Apply(shadowed, new HashSet<string> { "toByteArray" }), shadowed))
            throw new InvalidOperationException("A user function was reinterpreted as the stdlib byte-array view");
        var native = Reinterpret();
        native.OwnerType = new TypeNode.Fqn("probe.Native");
        if (!ReferenceEquals(Apply(native, names), native))
            throw new InvalidOperationException("A CLR static method was reinterpreted as a Kotlin extension");
        var payload = new BirInlinePayload { Body = new IBirStatement[] { Statement(Reinterpret()) } };
        if (!ReferenceEquals(Apply(payload, names), payload) || ((BirExpressionStatement)payload.Body.Value[0]).Expr.Value is not BirCallStatic)
            throw new InvalidOperationException("Source recognition entered an opaque executable carrier");
        Console.WriteLine("[typed faithful recognition] self-test OK (lexical rendering parity, helper binding, Map views, byte-array guards, opaque payloads)");
    }
}

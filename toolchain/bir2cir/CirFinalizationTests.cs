using DotKt.Bir;
using DotKt.Cir;

static class CirFinalizationTests
{
    internal static void Run()
    {
        var delegates = CanonicalDelegateSynthesis.SynthDefsFile();
        var expectedTypes = 2 * (BirTypeLowering.CanonicalDelegateMaxArity - BirTypeLowering.CanonicalDelegateMinArity + 1);
        if (delegates.Types.Length != expectedTypes || delegates.Methods.Length != 0 || delegates.Fields.Length != 0)
            throw new InvalidOperationException("Typed canonical delegate synthesis changed the physical family");
        for (var arity = BirTypeLowering.CanonicalDelegateMinArity; arity <= BirTypeLowering.CanonicalDelegateMaxArity; arity++)
            foreach (var returnsValue in new[] { false, true })
            {
                var genericArity = arity + (returnsValue ? 1 : 0);
                var family = returnsValue ? "KFunc" : "KAction";
                var type = delegates.Types.Single(type => type.Name == $"DotKt.Runtime.CompilerServices.{family}`{genericArity}");
                if (type.Kind != CirTypeKind.Delegate || type.Vis.Value != "public" || type.Generated.Value != true
                    || type.Params.Value.Length != arity || type.TypeParams.Value.Length != genericArity)
                    throw new InvalidOperationException("A synthesized delegate lost physical declaration facts");
                for (var index = 0; index < arity; index++)
                    if (type.Params.Value[index].Type != new TypeNode.Tv("type", index)
                        || type.TypeParams.Value[index].Variance.Value != "in")
                        throw new InvalidOperationException("A delegate parameter lost its declaration frame or variance");
                if (returnsValue ? type.Ret.Value != new TypeNode.Tv("type", genericArity - 1)
                    || type.TypeParams.Value[^1].Variance.Value != "out"
                    : type.Ret.Value != new TypeNode.Fqn("System.Void"))
                    throw new InvalidOperationException("A delegate return lost its frame or void representation");
            }

        var facade = new TypeNode.Fqn("kotlin.Int");
        var frame = new TypeNode.Tv("method", 1);
        var bytes = new byte[] { 0, 255, 0xd8, 0, 17 };
        var attribute = new AttributeApplication(new TypeNode.Fqn("Physical.Metadata"),
            [new TypeNode.Array(new TypeNode.Fqn("System.Byte"))], [new AttributeArgument.BytesValue(bytes)]);
        CirConstant Literal() => new() { Type = facade, Value = new AttributeArgument.Int32Value(42) };
        var signature = new CirParameter { Name = "input", Type = facade,
            Default = new AttributeArgument.Int32Value(7), Attrs = new[] { attribute } };
        var constructor = new CirConstructor { Params = [signature],
            BaseArgs = new ICirExpression[] { Literal() }, ThisArgs = OptionalField<ICirExpression[]>.Null,
            PreStmts = new ICirStatement[] { new CirExpressionStatement { Expr = Literal() } },
            Body = [new CirThrow { Value = Literal() }] };
        var method = new CirMethod { Name = "Read", Static = true, Override = false, Virtual = false, Params = [signature], Ret = frame,
            Body = new ICirStatement[] { new CirReturn { Value = Literal() } }, InlineBir = bytes,
            Attrs = new[] { attribute }, TypeParams = new[] { new CirTypeParameter { Name = "T", Constraints = new[] { facade } }, new CirTypeParameter { Name = "N" } } };
        var field = new CirField { Name = "slot", Type = facade, Init = Literal(), Constant = new AttributeArgument.Int32Value(19) };
        var file = new CirFileDeclaration { FileClass = "PhysicalTail", HasMain = false, Fields = [field], Methods = [method],
            Types = [new CirTypeDeclaration { Name = "Nested", Kind = CirTypeKind.Class,
                Fields = new[] { field }, Ctors = new[] { constructor }, Methods = new[] { method }, Attrs = new[] { attribute } }] };
        var lowered = RefBodySquash.LowerLiteralPlaceholders(file);
        if (lowered.Fields[0].Init.Value is not CirConstant { Type: TypeNode.Fqn { Name: "System.Object" }, Value: AttributeArgument.NullValue }
            || file.Fields[0].Init.Value is not CirConstant { Type: TypeNode.Fqn { Name: "kotlin.Int" }, Value: AttributeArgument.Int32Value { Value: 42 } })
            throw new InvalidOperationException("Typed reference placeholders changed their source graph or failed to lower a field initializer");
        var remaining = 0;
        CirBodyRewrite.RewriteCirFileDeclaration(lowered, node => {
            if (node is CirConstant { Type: TypeNode.Fqn { Name: "kotlin.Int" } }) remaining++;
            return node;
        });
        if (remaining != 0 || lowered.Types[0].Ctors.Value[0].ThisArgs.IsNull != true
            || lowered.Methods[0].Ret != frame || lowered.Methods[0].Params[0].Type != facade
            || lowered.Methods[0].Params[0].Default.Value is not AttributeArgument.Int32Value { Value: 7 }
            || lowered.Fields[0].Constant.Value is not AttributeArgument.Int32Value { Value: 19 }
            || !lowered.Methods[0].InlineBir.Value.SequenceEqual(bytes)
            || !((AttributeArgument.BytesValue)lowered.Methods[0].Attrs.Value[0].Args[0]).Bytes.SequenceEqual(bytes)
            || lowered.Methods[0].TypeParams.Value[0].Constraints.Value[0] != facade)
            throw new InvalidOperationException("CIR executable rewriting missed a body edge or changed signatures/opaque metadata");
        Console.WriteLine("[typed CIR finalization] self-test OK (canonical delegates, constructor/field/method bodies, frozen metadata, declaration frames)");
    }
}

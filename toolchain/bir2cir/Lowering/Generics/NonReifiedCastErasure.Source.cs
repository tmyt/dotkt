using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class NonReifiedCastErasure
{
    internal static void RewriteOperation(BirGraphValue node)
    {
        // Bare unchecked type-parameter casts keep their documented null
        // admission; a later concrete/native consumer owns its own narrowing.
        static TypeNode Target(TypeNode source) => source is TypeNode.Nullable
            ? BirGraphValue.CopyType(source) : new TypeNode.Nullable(BirGraphValue.CopyType(source));
        switch (node)
        {
            case BirCast { ErasedTypeOperand.HasValue: true } cast:
                cast.Type = Target(cast.ErasedTypeOperand.Value);
                cast.ErasedTypeOperand = default;
                break;
            case BirInstanceOrNull { ErasedTypeOperand.HasValue: true } safe:
                safe.Type = Target(safe.ErasedTypeOperand.Value);
                safe.ErasedTypeOperand = default;
                break;
        }
    }

    internal static void SourceSelfTest()
    {
        var variable = new TypeNode.Tv("method", 0);
        var any = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
        var integer = new TypeNode.Fqn("kotlin.Int");
        var text = new TypeNode.Fqn("kotlin.String");
        var bound = new TypeNode.Fqn("Bound", new TypeNode[] { new TypeNode.Star() });
        var effect = new BirCallStatic { Method = "effect", Args = Array.Empty<IBirExpression>(), Ret = any };
        var cast = new BirCast { Type = integer, ReifiedTypeOperand = false, ErasedTypeOperand = any, E = effect };
        var safe = new BirInstanceOrNull { Type = variable, ReifiedTypeOperand = false, ErasedTypeOperand = bound,
            E = new BirLocal { Name = "input", Sty = any } };
        var concrete = new BirCast { Type = text, ReifiedTypeOperand = false, E = new BirLocal { Name = "input", Sty = any } };
        var reified = new BirCast { Type = variable, ReifiedTypeOperand = true, E = new BirLocal { Name = "input", Sty = any } };
        var physical = new BirCast { Type = variable, _exactBridgeCast = true, E = new BirLocal { Name = "input", Sty = any } };
        var body = new IBirStatement[] { new BirReturn { Value = cast }, new BirExpressionStatement { Expr = safe },
            new BirExpressionStatement { Expr = concrete }, new BirExpressionStatement { Expr = reified },
            new BirExpressionStatement { Expr = physical } };
        var root = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration { Name = "probe",
            Params = Array.Empty<BirParameter>(), TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Ret = any, Body = body } } };
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            node.VisitChildren(Walk);
            RewriteOperation(node);
        }
        Walk(root);
        if (cast.Type.Value != any || !ReferenceEquals(cast.E.Value, effect) || cast.ErasedTypeOperand.IsPresent
            || safe.Type.Value is not TypeNode.Nullable { Of: TypeNode.Fqn { Name: "Bound", Args: [TypeNode.Star] } }
            || safe.ErasedTypeOperand.IsPresent || concrete.Type.Value != text || reified.Type.Value != variable
            || physical.Type.Value != variable || !ReferenceEquals(root.Methods.Value[0].Body.Value, body))
            throw new InvalidOperationException("Non-reified cast erasure did not consume the source bound without changing other checks");
        var once = cast.Type.Value;
        Walk(root);
        var effects = 0;
        root.Visit(node => { if (ReferenceEquals(node, effect)) effects++; });
        if (!ReferenceEquals(cast.Type.Value, once) || effects != 1)
            throw new InvalidOperationException("Non-reified cast erasure revisited or duplicated its operand");
        Console.WriteLine("[non-reified source casts] self-test OK (inline substitution, raw bounds, null admission, checked/native controls, once evaluation)");
    }
}

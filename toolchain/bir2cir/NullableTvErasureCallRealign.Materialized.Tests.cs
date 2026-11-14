using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    static void SourceUseMaterializedSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var text = new TypeNode.Fqn("kotlin.String");
        var erased = new TypeNode.Fqn("object");
        var callback = new BirNewClosure {
            ClosureType = new TypeNode.Fqn("Materialized.Callback"), Method = "invoke",
            Captures = Array.Empty<IBirExpression>(),
            FuncType = new TypeNode.Fn(false, text, new TypeNode[] { erased }),
            SynthClass = new BirClosureDefinition {
                Name = "Materialized.Callback", Fields = Array.Empty<BirCaptureDescriptor>(),
                Params = new[] { new BirLambdaParameter { Name = "value", Type = nullable } }, Ret = text,
                Body = new IBirStatement[] { new BirReturn { Value = new BirConstant {
                    Type = text, Value = new BirLiteral.TextLiteral("done") } } },
            },
        };
        var receiver = new BirVariable { Name = "receiver", Type = integer,
            Init = new BirLocal { Name = "source", Sty = nullable } };
        var block = new BirValueBlock { Stmts = new IBirStatement[] { receiver }, Result = callback };
        var statement = new BirExpressionStatement { Expr = block };
        // This sibling is deliberately not an input: late normalization must
        // not broaden into an enclosing declaration or recapture its locals.
        var untouched = new BirVariable { Name = "untouched", Type = integer,
            Init = new BirLocal { Name = "source", Sty = nullable } };
        var method = new BirFunctionDeclaration { Name = "caller", Body = new IBirStatement[] { untouched, statement } };
        var file = new BirFileDeclaration { FileClass = "Materialized", Methods = new[] { method },
            Types = Array.Empty<IBirTypeDeclaration>() };
        var index = CollectDeclaredMemberRets(new[] { file });
        var context = new ClosureSynthesis.CaptureContext("caller", "/source/Materialized.kt", 7);
        var roots = new[] { new ClosureSynthesis.MaterializedOccurrence(new BirGraphSlot(
            () => (BirGraphValue)statement.Expr.Value,
            value => statement.Expr = OptionalField<IBirExpression>.Of((IBirExpression)value)), context) };
        ApplyMaterialized(roots, index, type => type.Name == "kotlin.Int", null);
        if (!ReferenceEquals(statement.Expr.Value, block) || receiver.Init.Value is not BirNullableValue
            || untouched.Init.Value is not BirLocal || callback.SynthClass.IsPresent != true)
            throw new InvalidOperationException("Exact materialized source flow lost its owning root, fixed receiver or work-item boundary");
        var result = ClosureSynthesis.ApplyMaterialized(file, roots, null);
        if (!DelegateTargetSlotAlignment.ApplyMaterialized(roots, result.AppendedTypes, type => type.Name == "kotlin.Int"))
            throw new InvalidOperationException("Materialized delegate target was not aligned to its consuming slot");
        foreach (var owner in result.AppendedTypes)
            ApplyAfterDelegateSlotAlignment(owner, index, type => type.Name == "kotlin.Int", null);
        var invoke = result.AppendedTypes.Single().Methods.Value.Single();
        if (invoke.Params.Value.Single().Type.Value != erased || invoke.Ret.Value != text
            || invoke.Body.Value[0] is not BirVariable { Init.Value: BirCast { Type.Value: var restored } }
            || restored != nullable || callback.SynthClass.IsPresent || untouched.Init.Value is not BirLocal)
            throw new InvalidOperationException("Materialized slot alignment lost parameter restoration, covariant return or declaration boundary");
    }
}

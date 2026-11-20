using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static class NonReifiedCastErasure
{
    // An erased upper bound checks membership; it does not convert the Kotlin
    // value into the interface view used for CLR CharSequence member dispatch.
    internal static BirGraphValue ProjectOperation(BirGraphValue node, ReferenceMetadataIndex refs)
    {
        var (bound, input, safe) = node switch {
            BirCast cast => (cast.ErasedTypeOperand.OrDefault(), cast.E.OrDefault(), false),
            BirInstanceOrNull test => (test.ErasedTypeOperand.OrDefault(), test.E.OrDefault(), true),
            _ => (null, null, false),
        };
        if (input == null || !StringCharSequenceBridge.IsGenericCharSequenceBound(bound))
        {
            RewriteOperation(node);
            return node;
        }
        var name = "$csBound$" + System.Threading.Interlocked.Increment(ref nextLocal);
        var obj = new TypeNode.Fqn("object");
        BirLocal Read() => new() { Name = name, Sty = obj };
        BirConstant Null() => new() { Type = obj, Value = new BirLiteral.NullLiteral() };
        BirConditional Admit(IBirExpression condition, IBirExpression otherwise) => new() {
            Type = obj, Cond = OptionalField<IBirExpression>.Of(condition), Then = Read(),
            Else = OptionalField<IBirExpression>.Of(otherwise),
        };
        IBirExpression result = Null();
        if (!safe)
        {
            var exception = refs.ResolveRefType("System.InvalidCastException");
            result = new BirThrowExpression { Value = new BirNewClr {
                Type = new TypeNode.Fqn("System.InvalidCastException"), Args = Array.Empty<IBirExpression>(),
                MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs,
                    exception.GetConstructor(System.Type.EmptyTypes), "ctor", exception, null)),
            } };
            result = Admit(new BirBinaryOperation { Op = "===", Lhs = Read(), Rhs = Null() }, result);
        }
        foreach (var classifier in new[] { SharedSyntheticSynthesis.CharSeq, "System.Text.StringBuilder", "System.String" })
            result = Admit(new BirIsInstance { Type = new TypeNode.Fqn(classifier), E = Read() }, result);
        return new BirValueBlock {
            Type = obj, Stmts = new IBirStatement[] { new BirVariable { Name = name, Type = obj,
                Init = OptionalField<IBirExpression>.Of(input) } }, Result = OptionalField<IBirExpression>.Of(result),
        };
    }
    static int nextLocal;

    internal static void RewriteOperation(BirGraphValue node)
    {
        // Bare unchecked type-parameter casts keep their documented null
        // admission; a later concrete/native consumer owns its own narrowing.
        static TypeNode Target(TypeNode source) => source is TypeNode.Nullable or TypeNode.Array
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

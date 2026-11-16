using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class LateinitGetLowering
{
    internal static void SourceSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Owning lateinit failure: " + message); }
        static OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        var text = new TypeNode.Fqn("kotlin.String");
        var receiver = new BirLocal { Name = "owner" };
        var address = new BirLocal { Name = "address" };
        var value = new BirByRefLoad { Ptr = E(address), Elem = text };
        var missing = new BirLateinitGet { Name = "physicalField", LateinitSourceName = "sourceProperty",
            Recv = E(receiver), Value = E(value), Ret = text, Sty = text };
        var explicitNull = new BirLateinitGet { Name = "fallbackName", Exception = OptionalField<IBirExpression>.Null,
            LateinitSourceName = OptionalField<string>.Null };
        var nested = new BirLateinitGet { Name = "nested" };
        var custom = new BirNew { Type = new TypeNode.Fqn("kotlin.IllegalStateException"),
            Args = new IBirExpression[] { nested } };
        var existing = new BirLateinitGet { Name = "kept", Exception = E(custom), LateinitSourceName = "keptSource" };
        var body = new IBirStatement[] {
            new BirExpressionStatement { Expr = E(missing) }, new BirExpressionStatement { Expr = E(explicitNull) },
            new BirExpressionStatement { Expr = E(existing) },
        };
        var method = new BirFunctionDeclaration { Name = "reads", Body = body, InlineBir = new byte[] { 0x80 } };
        var methods = new[] { method };
        var file = new BirFileDeclaration { Methods = methods };
        var opaque = method.InlineBir.Value;
        ApplyAll(new[] { file });
        void Check(BirLateinitGet read, string name)
        {
            Need(read.Exception.Value is BirNew failure
                && failure.Type.Value == new TypeNode.Fqn("kotlin.UninitializedPropertyAccessException")
                && failure.ArgTypes.Value.SequenceEqual(new TypeNode[] { text })
                && failure.Args.Value is [BirConstant message] && message.Type.Value == text
                && message.Value.Value is BirLiteral.TextLiteral literal
                && literal.Value == $"lateinit property {name} has not been initialized"
                && !read.LateinitSourceName.IsPresent, "constructor signature/message/source-name consumption changed");
        }
        Check(missing, "sourceProperty"); Check(explicitNull, "fallbackName"); Check(nested, "nested");
        Need(ReferenceEquals(existing.Exception.Value, custom) && existing.LateinitSourceName.Value == "keptSource"
            && ReferenceEquals(custom.Args.Value[0], nested), "existing exception or nested owning operand changed");
        Need(ReferenceEquals(missing.Recv.Value, receiver) && ReferenceEquals(missing.Value.Value, value)
            && ReferenceEquals(value.Ptr.Value, address) && missing.Ret.Value == text && missing.Sty.Value == text
            && ReferenceEquals(file.Methods.Value, methods) && ReferenceEquals(method.Body.Value, body)
            && ReferenceEquals(method.InlineBir.Value, opaque), "declarations, storage reads or opaque payload were cloned/replaced");
        var failure = missing.Exception.Value;
        var once = file.DeepClone();
        ApplyAll(new[] { file });
        Need(ReferenceEquals(missing.Exception.Value, failure) && file.StructurallyEquals(once), "completed failure construction is not idempotent");
        Console.WriteLine("[owning lateinit failure] self-test OK (postorder, missing/null, explicit exception, exact constructor, storage MOVE and idempotence)");
    }
}

using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinHashStorageLowering
{
    internal static void RequestComparer(BirNewClr construction) => construction.KotlinHashStorageSignature = true;

    // Capture every source operand before allocation can invoke user hashing or
    // equality. The erased capture slot delays native storage checks until Add,
    // exactly as the source argument list delays them until after evaluation.
    internal static BirValueBlock SetLiteral(TypeNode element, IEnumerable<IBirExpression> elements)
    {
        var storage = new TypeNode.Fqn("System.Collections.Generic.HashSet", new[] { BirGraphValue.CopyType(element) });
        var name = CallEvalLowering.FreshBindingId();
        BirLocal Local() => new() { Name = name };
        var statements = new List<IBirStatement>();
        var operands = new List<IBirExpression>();
        foreach (var value in elements)
        {
            var captured = CallEvalLowering.FreshBindingId();
            statements.Add(new BirVariable { Name = captured,
                Type = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any")),
                Init = OptionalField<IBirExpression>.Of((IBirExpression)((BirGraphValue)value).DeepClone()) });
            operands.Add(new BirCast { Type = BirGraphValue.CopyType(element), E = new BirLocal { Name = captured } });
        }
        var allocation = new BirNewClr { Type = BirGraphValue.CopyType(storage),
            ArgTypes = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>() };
        RequestComparer(allocation);
        statements.Add(new BirVariable { Name = name, Type = BirGraphValue.CopyType(storage), Init = allocation });
        foreach (var value in operands)
            statements.Add(new BirExpressionStatement { Expr = new BirNativeInstance {
                Type = BirGraphValue.CopyType(storage), Method = "Add", Recv = Local(),
                ArgTypes = new[] { BirGraphValue.CopyType(element) }, KotlinHashStorageSignature = true,
                Ret = new TypeNode.Fqn("System.Boolean"),
                Args = new[] { (IBirExpression)((BirGraphValue)value).DeepClone() },
            } });
        return new BirValueBlock { Type = storage, Stmts = statements.ToArray(), Result = Local() };
    }
}

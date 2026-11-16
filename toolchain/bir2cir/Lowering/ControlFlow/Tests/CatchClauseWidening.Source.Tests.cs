using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class CatchClauseWidening
{
    internal static void SelfTestSource()
    {
        var inner = new BirTry { Catches = new[] { new BirCatch {
            ExcType = new TypeNode.Fqn("kotlin.IndexOutOfBoundsException"), Var = "inner",
            Body = Array.Empty<IBirStatement>(),
        } } };
        var widened = new BirCatch { ExcType = new TypeNode.Fqn("kotlin.IndexOutOfBoundsException"), Var = "outer",
            Body = new IBirStatement[] { inner } };
        var untouched = new BirCatch { ExcType = new TypeNode.Fqn("probe.Other"), Var = "other" };
        var absent = new BirTry();
        var explicitNull = new BirTry { Catches = OptionalField<BirCatch[]>.Null };
        var empty = new BirTry { Catches = Array.Empty<BirCatch>() };
        var rootTry = new BirTry { Catches = new[] { untouched, widened } };
        var file = new BirFileDeclaration { Methods = new[] { new BirFunctionDeclaration {
            Body = new IBirStatement[] { rootTry, absent, explicitNull, empty },
        } } };
        Apply(file);
        var clauses = rootTry.Catches.Value;
        if (clauses.Length != 3 || !ReferenceEquals(clauses[0], untouched)
            || clauses[1].ExcType.Value != new TypeNode.Fqn(IndexOobNet[0])
            || clauses[2].ExcType.Value != new TypeNode.Fqn(IndexOobNet[1])
            || clauses[1].Var.Value != "outer" || clauses[2].Var.Value != "outer"
            || ReferenceEquals(clauses[1].Body.Value, clauses[2].Body.Value)
            || ReferenceEquals(clauses[1].Body.Value[0], inner)
            || clauses.Skip(1).Any(clause => clause.Body.Value[0] is not BirTry { Catches.Value.Length: 2 })
            || absent.Catches.IsPresent || !explicitNull.Catches.IsNull || empty.Catches.Value.Length != 0
            || widened.ExcType.Value != new TypeNode.Fqn("kotlin.IndexOutOfBoundsException")
            || inner.Catches.Value.Length != 1)
            throw new InvalidOperationException("Owning catch widening changed clause order, deep ownership or presence");
        Console.WriteLine("[owning catch widening] self-test OK (ordered alternatives, independent nested bodies, unchanged siblings, presence)");
    }
}

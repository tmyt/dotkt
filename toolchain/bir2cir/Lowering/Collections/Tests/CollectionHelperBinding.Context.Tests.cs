using System;
using DotKt.Bir;
using DotKt.Bir.Lowering;

static partial class CollectionHelperBinding
{
    static void SourceContextSelfTest()
    {
        TypeNode variable = new TypeNode.Tv("method", 0);
        TypeNode element = new TypeNode.Tv("method", 1);
        var constraint = new TypeNode.Fqn("kotlin.collections.MutableCollection", new[] { element });
        var concrete = new TypeNode.Fqn("kotlin.collections.MutableList", new TypeNode[] { new TypeNode.Fqn("kotlin.Int") });
        var method = new BirFunctionDeclaration {
            Params = new[] { new BirParameter { Name = "receiver", Type = new TypeNode.Nullable(variable) } },
            TypeParams = new[] { new BirTypeParameter { Name = "C", Constraints = new TypeNode[] { constraint } },
                new BirTypeParameter { Name = "E" } },
            Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = new BirLocal { Name = "later" } },
                new BirVariable { Name = "later", Type = new TypeNode.Oblivious(new TypeNode.Nullable(concrete)) },
                new BirExpressionStatement { Expr = new BirInlineLambda {
                    Params = new[] { new BirLambdaParameter { Name = "inner", Type = element } },
                    Body = new IBirStatement[] { new BirVariable { Name = "lambdaLocal", Type = element } },
                } },
            },
        };
        var context = new CollectionSourceContext().Extend(method);
        if (context.Variables["receiver"] != variable || context.Variables["later"] != concrete
            || context.Variables.ContainsKey("lambdaLocal") || context.Constraints["method:0"][0] != constraint)
            throw new InvalidOperationException("Source collection context changed declaration-wide capture or nested binding ownership");
        var nested = context.Extend(new BirFunctionDeclaration {
            Params = new[] { new BirParameter { Name = "receiver", Type = concrete } }, Body = Array.Empty<IBirStatement>(),
        });
        if (nested.Variables["receiver"] != concrete || context.Variables["receiver"] != variable)
            throw new InvalidOperationException("Source collection context leaked a nested parameter shadow");

        var unstampedCall = new BirCallStatic { Ret = concrete };
        var stampedCall = unstampedCall with { Sty = new TypeNode.Nullable(concrete) };
        var field = new BirFieldGet { Ret = concrete };
        if (context.Receiver(unstampedCall) != null || context.Receiver(stampedCall) != concrete
            || context.Receiver(field) != concrete || context.Receiver(new BirLocal { Name = "later" }) != concrete)
            throw new InvalidOperationException("Source collection receiver recognition widened beyond its authored source facts");
        Console.WriteLine("[source collection context] self-test OK (declaration capture clock, constraints, nested scope, exact receiver facts)");
    }
}

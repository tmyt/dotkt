using System;
using DotKt.Bir;

static class FunctionRewriteTests
{
    internal static void Check(string pass, Func<TypeNode, TypeNode> rewrite)
    {
        var unit = new TypeNode.Fqn("kotlin.Unit");
        var variable = new TypeNode.Tv("method", 0);
        var function = new TypeNode.Fn(false, unit, new TypeNode[] { variable }, unit,
            "System.Func", new TypeNode[] { variable });
        var action = new TypeNode.Fn(false, new TypeNode.Fqn("void"), new TypeNode[] { unit }, Clr: "System.Action");
        foreach (var type in new TypeNode[] { function, action, function with { Clr = null },
                     new TypeNode.Fqn("Probe.Container", new TypeNode[] { function }) })
            if (rewrite(type) != type)
                throw new InvalidOperationException(pass + " discarded an unchanged function contract");
    }
}

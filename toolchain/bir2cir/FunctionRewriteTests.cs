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
        if (rewrite(function) != function)
            throw new InvalidOperationException(pass + " discarded an unchanged function contract");
        var action = new TypeNode.Fn(false, new TypeNode.Fqn("void"), new TypeNode[] { unit }, Clr: "System.Action");
        if (rewrite(action) != action || rewrite(function with { Clr = null }) != function with { Clr = null })
            throw new InvalidOperationException(pass + " changed a stated delegate family");
        var nested = new TypeNode.Fqn("Container", new TypeNode[] { function });
        if (rewrite(nested) != nested)
            throw new InvalidOperationException(pass + " discarded a nested function contract");
    }
}

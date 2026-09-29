using System;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    internal static void FunctionSlotComparisonSelfTest()
    {
        var owner = new TypeNode.Tv("type", 0);
        var method = new TypeNode.Tv("method", 0);
        var result = new TypeNode.Fqn("System.String");
        var fn = new TypeNode.Fn(false, result, new TypeNode[] { method }, owner, "System.Func");
        var nominal = new TypeNode.Fqn("System.Func`3", new TypeNode[] { owner, method, result });
        Check(fn, nominal, true);
        Check(new TypeNode.Array(fn), new TypeNode.Array(nominal), true);
        Check(fn, new TypeNode.Fqn("System.Func`3", new TypeNode[] { method, owner, result }), false);
        Check(fn, new TypeNode.Fqn("System.Func`3", new TypeNode[] { owner, new TypeNode.Tv("method", 1), result }), false);
        Check(fn, new TypeNode.Fqn("Other.Callback`3", new TypeNode[] { owner, method, result }), false);
        Check(fn, new TypeNode.Fqn("System.Func`2", new TypeNode[] { owner, result }), false);
        Check(new TypeNode.Fn(false, new TypeNode.Fqn("void"), Array.Empty<TypeNode>(), Clr: "System.Action"),
            new TypeNode.Fqn("System.Action"), true);
        Check(new TypeNode.Fn(false, new TypeNode.Fqn("void"), new TypeNode[] { owner }, Clr: "System.Action"),
            new TypeNode.Fqn("System.Action`1", new TypeNode[] { owner }), true);
        Check(fn with { Clr = null }, nominal, false);
        Console.WriteLine("[function MethodImpl slots] self-test OK (exact family, arguments, receiver and generic frames)");

        static void Check(TypeNode left, TypeNode right, bool expected)
        {
            if (SameInterfaceSlotType(left, right) != expected
                || SameInterfaceSlotType(right, left) != expected
                || (MethodImplComparisonType(left) == MethodImplComparisonType(right)) != expected)
                throw new InvalidOperationException("Function MethodImpl comparison lost exact physical identity");
        }
    }
}

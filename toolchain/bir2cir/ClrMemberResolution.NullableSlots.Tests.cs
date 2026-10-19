using System;
using DotKt.Bir;

static partial class ClrMemberResolution
{
    internal static void NullableSlotComparisonSelfTest()
    {
        var scalar = new TypeNode.Nullable(new TypeNode.Fqn("int"));
        var native = new TypeNode.Fqn("System.Nullable`1", new TypeNode[] { new TypeNode.Fqn("System.Int32") });
        Check(scalar, native, true);
        Check(scalar, new TypeNode.Fqn("System.Nullable", native.Args), true);
        Check(new TypeNode.Array(scalar), new TypeNode.Array(native), true);
        Check(new TypeNode.ByRef(scalar), new TypeNode.ByRef(native), true);
        Check(new TypeNode.Fqn("Other.Container", new TypeNode[] { scalar }),
            new TypeNode.Fqn("Other.Container`1", new TypeNode[] { native }), true);
        Check(scalar, new TypeNode.Fqn("System.Int32"), false);
        Check(scalar, new TypeNode.Fqn("System.Object"), false);
        Check(scalar, new TypeNode.Nullable(new TypeNode.Fqn("System.Int64")), false);
        Check(scalar, new TypeNode.Fqn("Other.Nullable`1", native.Args), false);
        Check(new TypeNode.Array(scalar), native, false);
        Check(new TypeNode.ByRef(scalar), native, false);
        Check(new TypeNode.Nullable(new TypeNode.Tv("type", 0)),
            new TypeNode.Fqn("System.Nullable`1", new TypeNode[] { new TypeNode.Tv("type", 0) }), true);
        Check(new TypeNode.Nullable(new TypeNode.Tv("type", 0)),
            new TypeNode.Fqn("System.Nullable`1", new TypeNode[] { new TypeNode.Tv("method", 0) }), false);
        Console.WriteLine("[nullable MethodImpl slots] self-test OK (exact physical nullable identity and generic frames)");

        static void Check(TypeNode left, TypeNode right, bool expected)
        {
            if (SameInterfaceSlotType(left, right) != expected
                || SameInterfaceSlotType(right, left) != expected
                || (MethodImplComparisonType(left) == MethodImplComparisonType(right)) != expected)
                throw new InvalidOperationException("Nullable MethodImpl comparison lost exact physical identity");
        }
    }
}
